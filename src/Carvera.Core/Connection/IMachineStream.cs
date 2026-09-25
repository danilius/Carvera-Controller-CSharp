namespace Carvera.Core.Connection;

public enum ConnectionKind { WiFi, Usb, Simulator }

public sealed record ConnectionOptions(ConnectionKind Kind, string Address)
{
    public const int DefaultTcpPort = 2222;
    public const int DefaultBaudRate = 115200;
}

/// <summary>A byte stream to a Carvera machine (TCP over Wi-Fi, USB serial or the built-in simulator).</summary>
public interface IMachineStream : IAsyncDisposable
{
    string Description { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    /// <summary>Reads available bytes; returns 0 when the stream has closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

public static class MachineStreamFactory
{
    public static IMachineStream Create(ConnectionOptions options) => options.Kind switch
    {
        ConnectionKind.WiFi => new TcpMachineStream(options.Address),
        ConnectionKind.Usb => new SerialMachineStream(options.Address),
        ConnectionKind.Simulator => new Simulation.SimulatedMachine(),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };
}
