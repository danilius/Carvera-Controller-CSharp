using System.Net.Sockets;

namespace Carvera.Core.Connection;

/// <summary>Wi-Fi connection: plain TCP to port 2222 (or host:port).</summary>
public sealed class TcpMachineStream(string address) : IMachineStream
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public string Description => $"Wi-Fi {address}";

    public static (string Host, int Port) ParseAddress(string address)
    {
        var trimmed = address.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && int.TryParse(trimmed[(colon + 1)..], out var port)) return (trimmed[..colon], port);
        return (trimmed, ConnectionOptions.DefaultTcpPort);
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        var (host, port) = ParseAddress(address);
        _client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await _client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response from {host}:{port}.");
        }
        _stream = _client.GetStream();
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _stream?.WriteAsync(data, cancellationToken) ?? throw new InvalidOperationException("Not connected.");

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _stream?.ReadAsync(buffer, cancellationToken) ?? ValueTask.FromResult(0);

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }
}
