using System.Threading.Channels;

namespace Carvera.Core.Transfer;

/// <summary>
/// Raw, exclusive access to the machine's byte stream, used for file transfers. While one is open the
/// controller stops polling and stops parsing incoming bytes as text lines.
/// </summary>
public interface IByteLink
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Reads one byte, or returns -1 when nothing arrives within <paramref name="timeout"/>.</summary>
    ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Discards incoming bytes until the line has been quiet for <paramref name="quiet"/>.</summary>
    ValueTask DrainAsync(TimeSpan quiet, CancellationToken cancellationToken);
}

/// <summary>The controller's implementation of <see cref="IByteLink"/>: the read loop feeds it while it is installed.</summary>
internal sealed class ExclusiveLink(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write) : IByteLink
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _index;

    public void Push(ReadOnlySpan<byte> data) => _incoming.Writer.TryWrite(data.ToArray());

    /// <summary>The connection ended: pending reads return -1 once the queue is empty.</summary>
    public void Complete() => _incoming.Writer.TryComplete();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => write(data, cancellationToken);

    public async ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        while (_index >= _current.Length)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                if (!await _incoming.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)) return -1;
                if (!_incoming.Reader.TryRead(out var chunk)) continue;
                _current = chunk;
                _index = 0;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return -1;
            }
        }
        return _current[_index++];
    }

    public async ValueTask DrainAsync(TimeSpan quiet, CancellationToken cancellationToken)
    {
        _index = _current.Length;
        while (await ReadByteAsync(quiet, cancellationToken).ConfigureAwait(false) >= 0) { }
    }
}
