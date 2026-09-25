using System.IO.Ports;
using System.Text;

namespace Carvera.Core.Connection;

/// <summary>USB connection through the machine's serial port (115200 8N1).</summary>
public sealed class SerialMachineStream(string portName, int baudRate = ConnectionOptions.DefaultBaudRate) : IMachineStream
{
    private SerialPort? _port;

    public string Description => $"USB {portName}";

    public static string[] AvailablePorts()
    {
        try { return SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception) { return []; }
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 1000,
        };
        _port.Open();
        // Same wake-up sequence as the Python controller: toggle DTR, flush, then send an empty line.
        _port.DtrEnable = false;
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        _port.DiscardInBuffer();
        _port.DtrEnable = true;
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        var wake = Encoding.ASCII.GetBytes("\n;\n");
        await _port.BaseStream.WriteAsync(wake, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _port?.BaseStream.WriteAsync(data, cancellationToken) ?? throw new InvalidOperationException("Not connected.");

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_port is null) return 0;
        try
        {
            return await _port.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (!_port.IsOpen)
        {
            return 0;
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _port?.Close(); } catch (IOException) { }
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
