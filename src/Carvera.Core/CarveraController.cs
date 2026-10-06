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

    private int _disconnectCount;

    /// <summary>How many times a live connection has been closed on request. Lets background reconnecting notice that the user chose to disconnect.</summary>
    public int DisconnectCount => Volatile.Read(ref _disconnectCount);
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the machine may go without a status report before <see cref="StatePaths.Stalled"/> is set.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private long _lastStatusTicks = Environment.TickCount64;
    private bool _stalled;
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
            State.Set(StatePaths.AwaitingStatus, true);
            State.Set(StatePaths.Stalled, false);
            State.Set(StatePaths.SilentSeconds, 0);
        }
        _stalled = false;
        Volatile.Write(ref _lastStatusTicks, Environment.TickCount64);
        Console.Info($"Connected to {stream.Description}.");
        _readLoop = Task.Run(() => ReadLoopAsync(stream, _session.Token));
        _pollLoop = Task.Run(() => PollLoopAsync(stream, _session.Token));
        await SendLineAsync(MachineCommands.Version, log: false).ConfigureAwait(false);
        await SendLineAsync(MachineCommands.Model, log: false).ConfigureAwait(false);
        await SendLineAsync(MachineCommands.GetWcs, log: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Raised after the machine dropped the connection without <see cref="DisconnectAsync"/> being called, once the
    /// controller has finished tearing the connection down. Carries the options it was using so a caller can reconnect.
    /// Not raised for a deliberate disconnect. May be raised on any thread.
    /// </summary>
    public event Action<ConnectionOptions?>? ConnectionLost;

    public async Task DisconnectAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null) return;
        Interlocked.Increment(ref _disconnectCount);
        await TearDownAsync(stream).ConfigureAwait(false);
        Console.Info("Disconnected.");
    }

    /// <summary>Handles a dead connection found by the reader or the poller. Acts once, and only for the current stream.</summary>
    private void OnConnectionLost(IMachineStream stream)
    {
        if (Interlocked.CompareExchange(ref _stream, null, stream) != stream) return; // a disconnect is already under way
        var options = Options;
        _ = Task.Run(async () =>
        {
            await TearDownAsync(stream).ConfigureAwait(false);
            ConnectionLost?.Invoke(options);
        });
    }

    private async Task TearDownAsync(IMachineStream stream)
    {
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
    }

    private void MarkDisconnected()
    {
        ClearContinuousJog();
        using var _ = State.BeginBatch();
        State.Set(StatePaths.ConnectionState, "Disconnected");
        State.Set(StatePaths.Connected, false);
        State.Set(StatePaths.MachineState, "N/A");
        State.Set(StatePaths.AwaitingStatus, false);
        State.Set(StatePaths.Stalled, false);
        State.Set(StatePaths.SilentSeconds, 0);
    }

    /// <summary>Sends a text command, appending a newline if needed.</summary>
    public Task SendLineAsync(string line, bool log = true)
    {
        var text = MachineCommands.Line(line);
        if (log) Console.Add(ConsoleEntryKind.Sent, text.TrimEnd('\n'));
        return WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    // ------------------------------------------------------------------ continuous jog

    // 0 = idle, 1 = jogging, 2 = stopping (Ctrl+Y sent, waiting for the firmware's ^Y).
    private int _continuousJog;

    /// <summary>True from <c>$J -c</c> until the firmware confirms the stop with ^Y.</summary>
    public bool ContinuousJogActive => Volatile.Read(ref _continuousJog) != 0;

    /// <summary>
    /// Starts a continuous jog ("X-1" style direction). Does nothing while one is running or stopping.
    /// Status polls send the "?1" keepalive until <see cref="StopContinuousJogAsync"/>.
    /// </summary>
    public Task StartContinuousJogAsync(string direction, double? feed)
    {
        if (Interlocked.CompareExchange(ref _continuousJog, 1, 0) != 0) return Task.CompletedTask;
        var line = feed is { } f && f > 0 ? $"$J -c {direction} F{f.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" : $"$J -c {direction}";
        return SendLineAsync(line);
    }

    /// <summary>Sends Ctrl+Y. A new continuous jog may only start after the firmware answers ^Y.</summary>
    public Task StopContinuousJogAsync()
    {
        if (Interlocked.CompareExchange(ref _continuousJog, 2, 1) != 1) return Task.CompletedTask;
        return SendRealtimeAsync(MachineCommands.StopContinuousJog, "Ctrl-Y (stop jog)");
    }

    /// <summary>Forgets a continuous jog without telling the machine (its ^Y arrived, or the link dropped).</summary>
    public void ClearContinuousJog() => Volatile.Write(ref _continuousJog, 0);

    /// <summary>Sends a single-byte real-time command (feed hold, cycle start, soft reset...).</summary>
    public Task SendRealtimeAsync(byte command, string? description = null)
    {
        if (description is not null) Console.Add(ConsoleEntryKind.Sent, description);
        return WriteAsync(new[] { command });
    }

    // ------------------------------------------------------------------ captured commands (listings and file operations)

    private sealed class CaptureSession
    {
        public readonly List<string> Lines = [];
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private volatile CaptureSession? _capture;
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    /// <summary>The reply to a captured command: its lines, whether it ended normally (EOT), or timed out.</summary>
    public sealed record CaptureResult(IReadOnlyList<string> Lines, bool Succeeded, bool TimedOut);

    /// <summary>
    /// Sends a command that ends its reply with EOT (or CAN on failure), such as <c>ls -e</c>, and collects the reply lines.
    /// Status polling pauses while it waits; one such command runs at a time.
    /// </summary>
    public async Task<CaptureResult> CaptureAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var session = new CaptureSession();
        try
        {
            _capture = session;
            await SendLineAsync(command, log: false).ConfigureAwait(false);
            bool? outcome = null;
            try { outcome = await session.Done.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            string[] lines;
            lock (session.Lines) lines = [.. session.Lines];
            return new CaptureResult(lines, outcome == true, outcome is null);
        }
        finally
        {
            _capture = null;
            _captureGate.Release();
        }
    }

    // ------------------------------------------------------------------ exclusive access (file transfers)

    private volatile Transfer.ExclusiveLink? _exclusive;

    /// <summary>True while <see cref="RunExclusiveAsync{T}"/> holds the stream.</summary>
    public bool IsExclusive => _exclusive is not null;

    /// <summary>
    /// Runs <paramref name="action"/> with raw access to the machine's byte stream. Polling stops and incoming bytes
    /// go to the <see cref="Transfer.IByteLink"/> until it returns. Only one transfer can run at a time.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Transfer.IByteLink, Task<T>> action)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected to a machine.");
        var link = new Transfer.ExclusiveLink((data, token) => stream.WriteAsync(data, token));
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.CompareExchange(ref _exclusive, link, null) is not null)
                throw new InvalidOperationException("A file transfer is already running.");
        }
        finally
        {
            _writeLock.Release();
        }
        try
        {
            // Let a status reply that was already on its way arrive before the transfer starts.
            await link.DrainAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None).ConfigureAwait(false);
            return await action(link).ConfigureAwait(false);
        }
        finally
        {
            _exclusive = null;
            link.Complete();
        }
    }

    /// <param name="poll">Status polls are silently skipped during a file transfer; anything else is refused, since it would corrupt the transfer.</param>
    private async Task WriteAsync(byte[] data, bool poll = false)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected to a machine.");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_exclusive is not null)
            {
                if (poll) return;
                throw new InvalidOperationException("A file transfer is running; wait for it to finish or cancel it first.");
            }
            await stream.WriteAsync(data, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PollLoopAsync(IMachineStream stream, CancellationToken token)
    {
        var lastDiagnose = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StatusInterval, token).ConfigureAwait(false);
                if (_exclusive is not null || _capture is not null)
                {
                    // No polling while a file transfer or a listing runs, so silence is expected: restart the clock.
                    Volatile.Write(ref _lastStatusTicks, Environment.TickCount64);
                    continue;
                }
                CheckForStall();
                // While jogging continuously the status query doubles as the keepalive ("?1", one write).
                var query = Volatile.Read(ref _continuousJog) == 1 ? MachineCommands.StatusQuery + MachineCommands.JogKeepAlive : MachineCommands.StatusQuery;
                await WriteAsync(Encoding.ASCII.GetBytes(query), poll: true).ConfigureAwait(false);
                if (DiagnosePolling && DateTime.UtcNow - lastDiagnose >= DiagnoseInterval)
                {
                    lastDiagnose = DateTime.UtcNow;
                    await WriteAsync(Encoding.ASCII.GetBytes(MachineCommands.DiagnoseQuery), poll: true).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.Error($"Status poll failed: {ex.Message}");
                    OnConnectionLost(stream);
                }
                return;
            }
        }
    }

    /// <summary>Publishes <see cref="StatePaths.Stalled"/> when no status report has arrived for <see cref="StallTimeout"/>.</summary>
    private void CheckForStall()
    {
        var silent = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastStatusTicks));
        if (silent < StallTimeout) return;
        using var _ = State.BeginBatch();
        State.Set(StatePaths.SilentSeconds, (int)silent.TotalSeconds);
        if (_stalled) return;
        _stalled = true;
        State.Set(StatePaths.Stalled, true);
        Console.Warning($"The machine has not reported its status for {(int)silent.TotalSeconds} s. It may be busy, or stuck: try Reset, or restart the machine.");
    }

    private void NoteStatusReceived()
    {
        Volatile.Write(ref _lastStatusTicks, Environment.TickCount64);
        if (State.Get(StatePaths.AwaitingStatus, false)) State.Set(StatePaths.AwaitingStatus, false);
        if (!_stalled) return;
        _stalled = false;
        using var _ = State.BeginBatch();
        State.Set(StatePaths.Stalled, false);
        State.Set(StatePaths.SilentSeconds, 0);
        Console.Info("The machine is reporting its status again.");
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
                if (_exclusive is { } exclusive)
                {
                    // A file transfer owns the stream: hand the raw bytes over instead of parsing lines.
                    exclusive.Push(buffer.AsSpan(0, count));
                    line.Clear();
                    continue;
                }
                for (var i = 0; i < count; i++)
                {
                    var b = buffer[i];
                    if (b is (byte)'\n' or 0x04 or 0x18 or 0x16)
                    {
                        HandleLine(line.ToString().TrimEnd('\r'));
                        line.Clear();
                        // Commands sent with "-e" end their reply with EOT, or with CAN when they failed.
                        if (b == 0x04) _capture?.Done.TrySetResult(true);
                        else if (b == 0x16) _capture?.Done.TrySetResult(false);
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
            _exclusive?.Complete(); // a running file transfer finds out and fails
            OnConnectionLost(stream);
        }
    }

    internal void HandleLine(string line)
    {
        if (line.Length == 0) return;
        LineReceived?.Invoke(line);
        var kind = ResponseParser.Classify(line);
        if (_capture is { } capture && kind is not (ResponseKind.Status or ResponseKind.Diagnose or ResponseKind.JogStopped))
        {
            lock (capture.Lines) capture.Lines.Add(line); // the reply to a captured command; not for the console
            return;
        }
        switch (kind)
        {
            case ResponseKind.Status:
                if (ResponseParser.TryParseStatus(line, State)) NoteStatusReceived();
                else Console.Warning($"Unrecognised status report: {line}");
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
                ClearContinuousJog();
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
