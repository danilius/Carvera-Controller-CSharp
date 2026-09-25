using System.Text;
using Carvera.Core.Connection;
using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core;

/// <summary>
/// Owns the connection to one machine: opens the stream, polls status, parses replies into
/// <see cref="State"/> and sends commands. Events and state changes may arrive on background threads.
/// </summary>
public sealed class CarveraController : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Func<ConnectionOptions, IMachineStream> _streamFactory;
    private IMachineStream? _stream;
    private CancellationTokenSource? _session;
    private Task? _readLoop, _pollLoop;

    public CarveraController(StateStore? state = null, ConsoleLog? console = null, Func<ConnectionOptions, IMachineStream>? streamFactory = null)
    {
        State = state ?? new StateStore();
        Console = console ?? new ConsoleLog();
        _streamFactory = streamFactory ?? MachineStreamFactory.Create;
        MarkDisconnected();
        State.Set(StatePaths.JogStep, 1.0);
        State.Set(StatePaths.JogFeed, 3000.0);
    }

    public StateStore State { get; }
    public ConsoleLog Console { get; }
    public ConnectionOptions? Options { get; private set; }
    public bool IsConnected => _stream is not null;
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan DiagnoseInterval { get; set; } = TimeSpan.FromMilliseconds(1000);
    /// <summary>Poll the diagnose report so switch/sensor states (light, air, limit switches...) stay current.</summary>
    public bool DiagnosePolling { get; set; } = true;
    /// <summary>Log every status and diagnose poll in the console (very noisy).</summary>
    public bool LogPolling { get; set; }

    public event Action<string>? LineReceived;

    public async Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        Options = options;
        State.Set(StatePaths.ConnectionState, "Connecting");
        State.Set(StatePaths.ConnectionAddress, options.Address);
        State.Set(StatePaths.ConnectionKind, options.Kind.ToString().ToLowerInvariant());
        var stream = _streamFactory(options);
        try
        {
            await stream.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            MarkDisconnected();
            Console.Error($"Connection to {options.Address} failed: {ex.Message}");
            throw;
        }

        _stream = stream;
        _session = new CancellationTokenSource();
        using (State.BeginBatch())
        {
            State.Set(StatePaths.ConnectionState, "Connected");
            State.Set(StatePaths.Connected, true);
            State.Set(StatePaths.MachineState, "Wait");
        }
        Console.Info($"Connected to {stream.Description}.");
        _readLoop = Task.Run(() => ReadLoopAsync(stream, _session.Token));
        _pollLoop = Task.Run(() => PollLoopAsync(_session.Token));
        await SendLineAsync(MachineCommands.Version, log: false).ConfigureAwait(false);
        await SendLineAsync(MachineCommands.Model, log: false).ConfigureAwait(false);
        await SendLineAsync(MachineCommands.GetWcs, log: false).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null) return;
        _session?.Cancel();
        await stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
            if (_pollLoop is not null) await _pollLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _session?.Dispose();
        _session = null;
        MarkDisconnected();
        Console.Info("Disconnected.");
    }

    private void MarkDisconnected()
    {
        using var _ = State.BeginBatch();
        State.Set(StatePaths.ConnectionState, "Disconnected");
        State.Set(StatePaths.Connected, false);
        State.Set(StatePaths.MachineState, "N/A");
    }

    /// <summary>Sends a text command, appending a newline if needed.</summary>
    public Task SendLineAsync(string line, bool log = true)
    {
        var text = MachineCommands.Line(line);
        if (log) Console.Add(ConsoleEntryKind.Sent, text.TrimEnd('\n'));
        return WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Sends a single-byte real-time command (feed hold, cycle start, soft reset...).</summary>
    public Task SendRealtimeAsync(byte command, string? description = null)
    {
        if (description is not null) Console.Add(ConsoleEntryKind.Sent, description);
        return WriteAsync(new[] { command });
    }

    private async Task WriteAsync(byte[] data)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected to a machine.");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        var lastDiagnose = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StatusInterval, token).ConfigureAwait(false);
                await WriteAsync(Encoding.ASCII.GetBytes(MachineCommands.StatusQuery)).ConfigureAwait(false);
                if (DiagnosePolling && DateTime.UtcNow - lastDiagnose >= DiagnoseInterval)
                {
                    lastDiagnose = DateTime.UtcNow;
                    await WriteAsync(Encoding.ASCII.GetBytes(MachineCommands.DiagnoseQuery)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                if (!token.IsCancellationRequested) Console.Error($"Status poll failed: {ex.Message}");
                return;
            }
        }
    }

    private async Task ReadLoopAsync(IMachineStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        var line = new StringBuilder();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    var b = buffer[i];
                    if (b is (byte)'\n' or 0x04 or 0x18)
                    {
                        HandleLine(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else line.Append((char)b);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (token.IsCancellationRequested) return;
            Console.Error($"Connection lost: {ex.Message}");
        }
        if (!token.IsCancellationRequested)
        {
            Console.Warning("The machine closed the connection.");
            _ = Task.Run(DisconnectAsync);
        }
    }

    internal void HandleLine(string line)
    {
        if (line.Length == 0) return;
        LineReceived?.Invoke(line);
        switch (ResponseParser.Classify(line))
        {
            case ResponseKind.Status:
                if (!ResponseParser.TryParseStatus(line, State)) Console.Warning($"Unrecognised status report: {line}");
                if (LogPolling) Console.Add(ConsoleEntryKind.Received, line);
                return;
            case ResponseKind.Diagnose:
                ResponseParser.TryParseDiagnose(line, State);
                if (LogPolling) Console.Add(ConsoleEntryKind.Received, line);
                return;
            case ResponseKind.Wcs:
                ResponseParser.TryParseWcs(line, State);
                Console.Add(ConsoleEntryKind.Received, line);
                return;
            case ResponseKind.JogStopped:
                return;
            case ResponseKind.Error:
                Console.Add(ConsoleEntryKind.Error, line);
                return;
            default:
                ResponseParser.TryParseInfo(line, State);
                Console.Add(ConsoleEntryKind.Received, line);
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
