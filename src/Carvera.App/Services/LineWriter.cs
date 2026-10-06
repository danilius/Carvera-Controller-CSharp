using System.Threading.Channels;

namespace Carvera.App.Services;

/// <summary>
/// Sends lines to a stream from a background task, so the caller (usually the UI thread) never waits for the reader at the other end:
/// a named pipe write can block until the other side reads.
/// </summary>
public sealed class LineWriter : IAsyncDisposable
{
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _pump;

    public LineWriter(TextWriter writer)
    {
        _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* the connection went away */ }
        });
    }

    public void Send(string line) => _queue.Writer.TryWrite(line);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _pump.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { }
    }
}
