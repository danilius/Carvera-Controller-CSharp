using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Carvera.Core.Commands;
using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Pendant;

/// <summary>A named G-code macro the pendant can run. Ids are 1-10, as in the Python controller's settings.</summary>
public sealed record PendantMacro(int Id, string Name, string Gcode);

/// <summary>What the user allows a pendant to do, mirroring the Python controller's jogging preferences.</summary>
public sealed record PendantPolicy(bool JoggingEnabled = true, bool AllowJoggingWhileRunning = false, bool AllowJoggingWhileSpindleOn = false);

public sealed class CydPendantOptions
{
    public required Func<string> Host { get; init; }
    public required Func<int> Port { get; init; }
    public Func<IReadOnlyList<PendantMacro>> Macros { get; init; } = () => [];
    public Func<PendantPolicy> Policy { get; init; } = () => new PendantPolicy();
    public CydClientTimings? Timings { get; init; }
}

public sealed record CydClientTimings(TimeSpan HeartbeatInterval, TimeSpan HeartbeatTimeout, TimeSpan ReconnectInterval);

/// <summary>
/// The CYD touchscreen pendant. Ported from cyd.py and speaking the same newline-delimited JSON protocol,
/// so existing pendant firmware works unchanged. The pendant sends jog, tool, probing (M461/M462/M466),
/// macro, override, position and manual-milling requests; the controller pushes <c>pos</c>,
/// <c>machine_state</c> and <c>macro_list</c> updates. Every request is checked against the machine state,
/// and motion additionally needs a live heartbeat.
/// </summary>
public sealed class CydPendant : IDisposable
{
    public const string PendantDisplayName = "CYD";

    private const double JogSessionSeconds = 1.2;
    private static readonly Regex ProbeGcode = new(@"^M46[126](?:\s+[XYZES][-+]?\d+(?:\.\d+)*)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly int[] ChangeableTools = [0, 999990, 1, 2, 3, 4, 5, 6];

    private readonly CarveraController _controller;
    private readonly CommandRegistry _commands;
    private readonly CydPendantOptions _options;
    private readonly CydClient _client;
    private readonly StateStore _state;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _ticker;

    private bool _closed, _connected;
    private bool _manualActive, _manualRpmDirty;
    private double _manualFeed = 100, _manualRpm = 12000, _manualRpmSentAt = -1;
    private bool? _manualSpindleCommanded, _airCommanded;
    private bool _airStateAtCommand;
    private double _airCommandedAt;
    private string _manualLastReason = "";
    private (string Reason, double At) _lastManualEditRejection = ("", -1);
    private double _jogSessionUntil, _cancelUntil;
    private long _lastFullStopSeq;
    private (string Command, double Feed, double Deadline)? _pendingJog;
    private (string Command, double Feed)? _activeJog;
    private double? _lastStepAt;
    private string? _lastMachineSnapshot;
    private string? _lastMacroSnapshot;
    private (double Mx, double My, double Mz, double Wx, double Wy, double Wz)? _lastPosition;

    public CydPendant(CarveraController controller, CommandRegistry commands, CydPendantOptions options)
    {
        _controller = controller;
        _commands = commands;
        _options = options;
        _state = controller.State;
        var t = options.Timings;
        _client = new CydClient(options.Host, options.Port, t?.HeartbeatInterval, t?.HeartbeatTimeout, t?.ReconnectInterval);
        _client.Connected += _ => Locked(HandleConnect);
        _client.Disconnected += _ => Locked(HandleDisconnect);
        _client.MessageReceived += (_, message) => Locked(() => HandleIncoming(message));
    }

    public bool IsConnected { get { lock (_gate) return _connected; } }

    /// <summary>True when the pendant's heartbeat is current, which motion requires.</summary>
    public bool LinkReady => _client.MotionReady;

    public void Start()
    {
        _client.Start();
        _ticker = Task.Run(() => TickAsync(_cancel.Token));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _connected = false;
            StopContinuousJog(force: true);
            ClearJogSession();
        }
        _cancel.Cancel();
        try { _ticker?.Wait(500); } catch (AggregateException) { }
        _client.Stop();
        SetLinkState(false);
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Locked(Action action)
    {
        lock (_gate)
        {
            try { action(); }
            catch (Exception ex) { _controller.Console.Error($"CYD pendant: {ex.Message}"); }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        // Jog servicing runs at 20 Hz, position/state/macro polling at 5 Hz, as in the Python controller.
        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Locked(ServiceJog);
                if (++tick % 4 == 0) Locked(PollPositions);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Fire(Task task)
    {
        _ = task.ContinueWith(t => _controller.Console.Error($"CYD pendant: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void SendLine(string line) => Fire(_controller.SendLineAsync(line));

    private void SetLinkState(bool connected)
    {
        PendantStatus.Publish(_state, "cyd", connected, PendantDisplayName);
    }

    // ------------------------------------------------------------------ state helpers

    private string MachineState => _state.Get(StatePaths.MachineState, "N/A") ?? "N/A";
    private int AtcState => _state.Get(StatePaths.AtcState, 0);
    private bool Playing => _state.Get(StatePaths.JobPlaying, false);
    private bool CommunityFirmware => _state.Get(StatePaths.CommunityFirmware, false);
    private PendantPolicy Policy => _options.Policy();

    private PendantGate Gate => _pendantGate ??= new PendantGate(_state, _options.Policy);
    private PendantGate? _pendantGate;

    private bool JoggingEnabled(bool continuing) => Gate.JoggingEnabled(continuing);
    private bool MachineActionsGate() => Gate.MachineAllowsJogging(false);

    private bool ManualMillingAllowed() =>
        _state.Get<string>(StatePaths.MachineModelName) == "C1" && Policy.JoggingEnabled && !Playing && MachineState is "Idle" or "Run";

    private double CurrentFeed => _state.Get(StatePaths.FeedCurrent, double.PositiveInfinity);

    private bool CydJogAllowed()
    {
        if (_closed || !_connected || !_client.MotionReady || !_controller.IsConnected) return false;
        if (Playing && MachineState != "Pause") return false;
        if (_manualActive) return ManualAllowed();
        if (JoggingEnabled(false)) return true;
        return JogSessionActive && JoggingEnabled(true);
    }

    private bool MachineActionAllowed() =>
        !_closed && _connected && _client.MotionReady && _controller.IsConnected && _pendingJog is null && _activeJog is null
        && !_controller.ContinuousJogActive && Now >= _cancelUntil && MachineActionsGate();

    private bool JogSessionActive => Now < _jogSessionUntil;
    private void RefreshJogSession(double hold = JogSessionSeconds) => _jogSessionUntil = Now + hold;
    private void ClearJogSession() => _jogSessionUntil = 0;

    private string ManualBlockReason()
    {
        if (_closed || !_connected) return "pendant disconnected";
        if (!_client.MotionReady) return "heartbeat not ready";
        if (!_controller.IsConnected) return "machine disconnected";
        if (!CommunityFirmware) return "community firmware required";
        if (Playing) return "program active";
        if (MachineState is not ("Idle" or "Run")) return "machine state: " + MachineState;
        if (AtcState != 0) return "ATC/probe active: " + AtcState;
        if (_state.Get(StatePaths.LaserMode, false) || _state.Get(StatePaths.ToolCurrent, -1) == 8888) return "laser mode";
        if (!ManualMillingAllowed()) return "desktop controls gated";
        return "";
    }

    private bool ManualAllowed() => ManualBlockReason().Length == 0;

    private bool ManualMotionBusy() =>
        _controller.ContinuousJogActive || _pendingJog is not null || Now < _cancelUntil || Math.Abs(CurrentFeed) > 0.001;

    // ------------------------------------------------------------------ jog control

    private void StopContinuousJog(bool force = false)
    {
        _pendingJog = null;
        _activeJog = null;
        if (_controller.ContinuousJogActive) Fire(_controller.StopContinuousJogAsync());
        else if (force && _controller.IsConnected)
        {
            // A step jog cannot be cancelled by ^Y alone: firmware keeps an unconsumed stop request for
            // 500 ms, so do not start another jog until it has lapsed.
            Fire(_controller.SendRealtimeAsync(MachineCommands.StopContinuousJog, "Ctrl-Y (stop jog)"));
            _cancelUntil = Now + 0.6;
        }
    }

    private void ServiceJog()
    {
        if (_closed || !_connected) return;
        if (_manualActive && !ManualAllowed())
        {
            _manualLastReason = ManualBlockReason();
            _controller.Console.Warning($"CYD manual session disabled: {_manualLastReason}");
            _manualActive = false;
            _manualRpmDirty = false;
            StopContinuousJog(force: true);
            ClearJogSession();
        }
        ServiceManualRpm();
        if ((_activeJog is not null || _pendingJog is not null) && !CydJogAllowed())
        {
            StopContinuousJog();
            ClearJogSession();
            return;
        }
        if (_activeJog is not null && !_controller.ContinuousJogActive)
        {
            _activeJog = null;
            if (_pendingJog is null) ClearJogSession();
        }
        if (_pendingJog is not { } pending) return;
        var now = Now;
        if (now > pending.Deadline)
        {
            _pendingJog = null;
            SendJogResult(false, "jog_start_timeout", pending.Command);
            return;
        }
        if (_controller.ContinuousJogActive) return;
        if (_cancelUntil > 0)
        {
            var stopped = MachineState == "Idle" || (_manualActive && ManualAllowed() && Math.Abs(CurrentFeed) < 0.001);
            if (now < _cancelUntil || !stopped) return;
            _cancelUntil = 0;
        }
        _pendingJog = null;
        Fire(_controller.StartContinuousJogAsync(pending.Command, pending.Feed));
        if (_controller.ContinuousJogActive)
        {
            _activeJog = (pending.Command, pending.Feed);
            _lastStepAt = null;
            RefreshJogSession();
            SendJogResult(true, command: pending.Command);
        }
        else SendJogResult(false, "jog_start_rejected", pending.Command);
    }

    // ------------------------------------------------------------------ connection

    private void HandleConnect()
    {
        if (_closed) return;
        _connected = true;
        _manualActive = false;
        _manualRpmDirty = false;
        _manualSpindleCommanded = null;
        _airCommanded = null;
        _lastFullStopSeq = 0;
        SetLinkState(true);
        _controller.Console.Info("CYD pendant connected.");
        _lastMachineSnapshot = null;
        _lastMacroSnapshot = null;
        SendMachineState(force: true);
        SendMacroList(force: true);
        SendPosition(force: true);
    }

    private void HandleDisconnect()
    {
        if (_closed) return;
        _connected = false;
        _manualActive = false;
        _manualRpmDirty = false;
        _manualSpindleCommanded = null;
        _airCommanded = null;
        StopContinuousJog(force: true);
        ClearJogSession();
        SetLinkState(false);
        _controller.Console.Warning("CYD pendant disconnected.");
    }

    // ------------------------------------------------------------------ outgoing state

    private static double Round3(double v) => double.IsFinite(v) ? Math.Round(v, 3) : 0;

    private double Axis(string path) => Round3(_state.Get(path, 0.0));

    private void SendPosition(bool force)
    {
        var p = (Axis(StatePaths.AxisMachine("x")), Axis(StatePaths.AxisMachine("y")), Axis(StatePaths.AxisMachine("z")),
            Axis(StatePaths.AxisWork("x")), Axis(StatePaths.AxisWork("y")), Axis(StatePaths.AxisWork("z")));
        if (!force && _lastPosition == p) return;
        _lastPosition = p;
        _client.Send(new Dictionary<string, object?>
        {
            ["type"] = "pos", ["units"] = "mm",
            ["x"] = p.Item1, ["y"] = p.Item2, ["z"] = p.Item3,
            ["mx"] = p.Item1, ["my"] = p.Item2, ["mz"] = p.Item3,
            ["wx"] = p.Item4, ["wy"] = p.Item5, ["wz"] = p.Item6,
        });
    }

    private void PollPositions()
    {
        if (!_connected) return;
        if (_activeJog is not null && _controller.ContinuousJogActive) RefreshJogSession();
        SendMachineState(force: false);
        SendMacroList(force: false);
        SendPosition(force: false);
    }

    public static string DeriveActivity(string state, int atcState, bool playing)
    {
        var s = state.ToLowerInvariant();
        switch (s)
        {
            case "idle": return "idle";
            case "sleep": return "sleeping";
            case "alarm": return "alarm";
            case "pause": return "paused";
            case "hold": return "holding";
            case "wait": return "waiting";
            case "tool": return "changing_tool";
        }
        if (atcState == 5) return "probing";
        if (atcState is 1 or 2 or 3) return "changing_tool";
        if (atcState == 6) return "leveling";
        if (playing && s == "run") return "running_gcode";
        if (s == "run") return "running";
        return s.Length > 0 ? s : "unknown";
    }

    public static string ToolLabel(int tool) => ToolInfo.Label(tool);

    private bool SpindleOn()
    {
        var actual = _state.Get(StatePaths.SpindleCurrent, 0.0) > 0;
        if (_manualSpindleCommanded == actual) _manualSpindleCommanded = null;
        return _manualSpindleCommanded ?? actual;
    }

    private bool AirOn()
    {
        // Position reports do not carry the air switch, so trust the issued command until the diagnose
        // poll shows a change (or two polls have passed).
        var reported = _state.Get("switch.air", false);
        if (_airCommanded is { } commanded)
        {
            if (reported != _airStateAtCommand || Now - _airCommandedAt > 2.5) _airCommanded = null;
            else return commanded;
        }
        return reported;
    }

    private Dictionary<string, object?> MachineSnapshot()
    {
        var state = MachineState;
        var atc = AtcState;
        var playing = Playing;
        var tool = _state.Get(StatePaths.ToolCurrent, -1);
        var target = _state.Get(StatePaths.ToolTarget, -1);
        var block = ManualBlockReason();
        return new Dictionary<string, object?>
        {
            ["state"] = state,
            ["activity"] = DeriveActivity(state, atc, playing),
            ["jog_allowed"] = CydJogAllowed(),
            ["pendant_link_ready"] = _client.MotionReady,
            ["manual_supported"] = true,
            ["manual_active"] = _manualActive,
            ["manual_allowed"] = block.Length == 0,
            ["manual_block_reason"] = block,
            ["manual_last_stop_reason"] = _manualLastReason,
            ["manual_feed"] = (long)Math.Round(_manualFeed, MidpointRounding.ToEven),
            ["manual_rpm"] = (long)Math.Round(_manualRpm, MidpointRounding.ToEven),
            ["spindle_on"] = SpindleOn(),
            ["spindle_actual"] = (long)Math.Round(_state.Get(StatePaths.SpindleCurrent, 0.0), MidpointRounding.ToEven),
            ["atc_state"] = atc,
            ["playing"] = playing,
            ["program_running"] = playing && state == "Run",
            ["program_paused"] = playing && state is "Pause" or "Hold",
            ["feed_override"] = (long)Math.Round(_state.Get(StatePaths.FeedOverride, 100.0), MidpointRounding.ToEven),
            ["spindle_override"] = (long)Math.Round(_state.Get(StatePaths.SpindleOverride, 100.0), MidpointRounding.ToEven),
            ["air_on"] = AirOn(),
            ["playedpercent"] = Math.Round(_state.Get(StatePaths.JobPercent, 0.0), 1, MidpointRounding.ToEven),
            ["tool"] = tool,
            ["tool_label"] = ToolLabel(tool),
            ["target_tool"] = target,
            ["target_tool_label"] = ToolLabel(target),
        };
    }

    private static long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void SendMachineState(bool force, string responseTo = "")
    {
        var snapshot = MachineSnapshot();
        var key = JsonSerializer.Serialize(snapshot);
        if (!force && key == _lastMachineSnapshot) return;
        var payload = new Dictionary<string, object?> { ["type"] = "machine_state" };
        foreach (var (k, v) in snapshot) payload[k] = v;
        payload["ts"] = UnixNow;
        if (responseTo.Length > 0) payload["response_to"] = responseTo;
        _client.Send(payload);
        _lastMachineSnapshot = key;
    }

    private List<PendantMacro> NamedMacros() =>
        _options.Macros()
            .Where(m => m.Id is >= 1 and <= 10)
            .Select(m => m with { Name = m.Name.Trim(), Gcode = m.Gcode.Trim() })
            .Where(m => m.Name.Length > 0 && m.Gcode.Length > 0 && !m.Name.Equals($"macro {m.Id}", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private void SendMacroList(bool force)
    {
        var macros = NamedMacros();
        var key = string.Join("|", macros.Select(m => $"{m.Id}:{Truncate(m.Name)}"));
        if (!force && key == _lastMacroSnapshot) return;
        _client.Send(new Dictionary<string, object?>
        {
            ["type"] = "macro_list",
            ["macros"] = macros.Select(m => new Dictionary<string, object?> { ["id"] = m.Id, ["name"] = Truncate(m.Name) }).ToList(),
            ["ts"] = UnixNow,
        });
        _lastMacroSnapshot = key;
    }

    private static string Truncate(string name) => name.Length <= 24 ? name : name[..24];

    // ------------------------------------------------------------------ results

    private void Result(string type, bool ok, string reason, params (string Key, object? Value)[] extra)
    {
        var payload = new Dictionary<string, object?> { ["type"] = type, ["ok"] = ok, ["reason"] = reason };
        foreach (var (key, value) in extra) payload[key] = value;
        _client.Send(payload);
    }

    private void SendJogResult(bool ok, string reason = "", string command = "") => Result("jog_result", ok, reason, ("command", command));
    private void SendGcodeResult(bool ok, string reason = "", string command = "") => Result("gcode_result", ok, reason, ("command", command));
    private void SendToolResult(bool ok, string reason = "", string action = "", int tool = -1) => Result("tool_result", ok, reason, ("action", action), ("tool", tool));
    private void SendMacroResult(bool ok, string reason = "", int id = -1) => Result("macro_result", ok, reason, ("id", id), ("ts", UnixNow));
    private void SendRuntimeResult(bool ok, string reason = "", string action = "") => Result("runtime_result", ok, reason, ("action", action), ("ts", UnixNow));
    private void SendPositionResult(bool ok, string reason = "", string action = "", string message = "") =>
        Result("position_result", ok, reason, ("action", action), ("message", message), ("ts", UnixNow));

    // ------------------------------------------------------------------ incoming messages

    private static string Str(JsonElement msg, string key) =>
        msg.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => v.GetRawText(),
        } : "";

    /// <summary>Python's float(): numbers, numeric strings and booleans.</summary>
    private static bool TryNumber(JsonElement msg, string key, out double value)
    {
        value = 0;
        if (!msg.TryGetProperty(key, out var v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number: value = v.GetDouble(); return true;
            case JsonValueKind.True: value = 1; return true;
            case JsonValueKind.False: value = 0; return true;
            case JsonValueKind.String:
                return double.TryParse(v.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default: return false;
        }
    }

    /// <summary>Python's bool(): false, null, 0 and empty values are false.</summary>
    private static bool Truthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.String => v.GetString()?.Length > 0,
        JsonValueKind.Array or JsonValueKind.Object => v.GetRawText().Length > 2,
        _ => false,
    };

    private static bool TryInteger(JsonElement msg, string key, out long value)
    {
        value = 0;
        if (!TryNumber(msg, key, out var d) || !double.IsFinite(d)) return false;
        value = (long)Math.Truncate(d);
        return true;
    }

    private void HandleIncoming(JsonElement msg)
    {
        if (_closed || !_connected) return;
        var type = Str(msg, "type");
        switch (type)
        {
            case "machine_state_query" or "get_machine_state" or "state_query": SendMachineState(force: true, responseTo: type); break;
            case "macro_query" or "get_macros": SendMacroList(force: true); break;
            case "jog": HandleJog(msg); break;
            case "jog_cont": HandleContinuousJog(msg); break;
            case "tool": HandleTool(msg); break;
            case "gcode": HandleGcode(msg); break;
            case "macro": HandleMacro(msg); break;
            case "runtime": HandleRuntime(msg); break;
            case "manual": HandleManual(msg); break;
            case "position": HandlePosition(msg); break;
            case "ping":
                _client.Send(new Dictionary<string, object?>
                {
                    ["type"] = "pong",
                    ["ts"] = msg.TryGetProperty("ts", out var ts) ? JsonSerializer.Deserialize<object?>(ts.GetRawText()) : null,
                });
                break;
        }
    }

    // ---- step jog

    private void HandleJog(JsonElement msg)
    {
        if (_controller.ContinuousJogActive || _pendingJog is not null || Now < _cancelUntil)
        {
            SendJogResult(false, "jog_transition_pending");
            return;
        }
        if (!CydJogAllowed())
        {
            SendJogResult(false, "jogging_disabled");
            return;
        }
        var axis = Str(msg, "axis").ToUpperInvariant();
        if (axis is not ("X" or "Y" or "Z"))
        {
            SendJogResult(false, "unsupported_axis");
            return;
        }
        if (!TryNumber(msg, "delta", out var delta))
        {
            SendJogResult(false, "invalid_delta");
            return;
        }
        const double maxDelta = 2.000, tolerance = 0.0005;
        if (!double.IsFinite(delta) || Math.Abs(delta) <= 0 || Math.Abs(delta) > maxDelta + tolerance)
        {
            SendJogResult(false, $"outside_first_test_limit:{delta.ToString("0.000000", CultureInfo.InvariantCulture)}");
            return;
        }
        var command = $"{axis}{(delta < 0 ? "-" : "")}{Math.Abs(delta).ToString("0.000", CultureInfo.InvariantCulture)}";
        RefreshJogSession();
        var feed = _manualActive ? Math.Min(_manualFeed, axis == "Z" ? 800.0 : 3000.0) : _state.Get(StatePaths.JogFeed, 0.0);
        SendLine(MachineCommands.Jog(new Dictionary<char, double> { [axis[0]] = delta }, feed > 0 ? feed : null));
        _lastStepAt = Now;
        SendJogResult(true, command: command);
    }

    // ---- continuous jog

    private void HandleContinuousJog(JsonElement msg)
    {
        var action = Str(msg, "action").ToLowerInvariant();
        var seq = TryInteger(msg, "seq", out var s) ? s : 0;

        if (action is "stop" or "full_stop")
        {
            StopContinuousJog(force: action == "full_stop");
            if (action == "full_stop")
            {
                ClearJogSession();
                _lastFullStopSeq = Math.Max(_lastFullStopSeq, seq);
            }
            else RefreshJogSession(0.3); // keep authorisation across direction reversals while firmware acknowledges the stop
            SendJogResult(true, command: action);
            return;
        }
        if (action != "start")
        {
            SendJogResult(false, "unsupported_continuous_action");
            return;
        }
        if (!CydJogAllowed())
        {
            SendJogResult(false, "jogging_disabled");
            return;
        }
        if (!CommunityFirmware)
        {
            SendJogResult(false, "continuous_jog_unsupported");
            return;
        }
        var axis = Str(msg, "axis").ToUpperInvariant();
        if (axis is not ("X" or "Y" or "Z"))
        {
            SendJogResult(false, "unsupported_axis");
            return;
        }
        if (!TryInteger(msg, "dir", out var dir) || !TryNumber(msg, "feed", out var feed))
        {
            SendJogResult(false, "invalid_continuous_jog");
            return;
        }
        if (dir is not (-1 or 1))
        {
            SendJogResult(false, "invalid_direction");
            return;
        }
        if (!double.IsFinite(feed))
        {
            SendJogResult(false, "invalid_continuous_jog");
            return;
        }
        if (seq != 0 && seq <= _lastFullStopSeq) return; // a stale start that lost the race with a full stop

        if (_manualActive) feed = _manualFeed;
        feed = Math.Clamp(feed, 1.0, 3000.0);
        if (axis == "Z") feed = Math.Min(feed, 800.0);
        var command = $"{axis}{(dir < 0 ? "-" : "")}1";

        if (_activeJog == (command, feed) && _controller.ContinuousJogActive) return;
        if (_controller.ContinuousJogActive) StopContinuousJog();
        else if (_lastStepAt is not null)
        {
            StopContinuousJog(force: true);
            _lastStepAt = null;
        }
        RefreshJogSession();
        _pendingJog = (command, feed, Now + 1.2);
        ServiceJog();
    }

    // ---- tools and probing

    private bool ToolChangeAllowed() => DeriveActivity(MachineState, AtcState, _state.Get(StatePaths.JobLines, -1) > 0) == "idle" && MachineActionAllowed();

    private void HandleTool(JsonElement msg)
    {
        var action = Str(msg, "action").ToLowerInvariant();
        if (!ToolChangeAllowed())
        {
            var activity = DeriveActivity(MachineState, AtcState, _state.Get(StatePaths.JobLines, -1) > 0);
            _controller.Console.Warning($"CYD: rejected tool action '{action}' ({activity}).");
            SendToolResult(false, $"not_idle:{activity}", action);
            return;
        }
        switch (action)
        {
            case "drop": SendLine(MachineCommands.DropTool); SendToolResult(true, action: action); return;
            case "clamp": SendLine(MachineCommands.ClampTool); SendToolResult(true, action: action); return;
            case "unclamp": SendLine(MachineCommands.UnclampTool); SendToolResult(true, action: action); return;
            case "change":
                var tool = TryInteger(msg, "tool", out var t) ? (int)Math.Clamp(t, int.MinValue, int.MaxValue) : -999999;
                if (!ChangeableTools.Contains(tool))
                {
                    SendToolResult(false, "unsupported_tool", action, tool);
                    return;
                }
                SendLine(MachineCommands.ChangeTool(tool));
                SendToolResult(true, action: action, tool: tool);
                return;
            default: SendToolResult(false, "unsupported_action", action); return;
        }
    }

    private void HandleGcode(JsonElement msg)
    {
        var line = Str(msg, "line").Trim();
        if (line.Length == 0)
        {
            SendGcodeResult(false, "empty_line");
            return;
        }
        // Allowlist: pendant probing commands only.
        if (line.Contains('\n') || line.Contains('\r') || !ProbeGcode.IsMatch(line))
        {
            _controller.Console.Warning($"CYD: rejected G-code not on the allowlist: {line}");
            SendGcodeResult(false, "not_allowed");
            return;
        }
        if (!MachineActionAllowed())
        {
            _controller.Console.Warning("CYD: rejected probing command because machine controls are gated.");
            SendGcodeResult(false, "machine_controls_gated");
            return;
        }
        SendLine(line);
        SendGcodeResult(true, command: line);
    }

    // ---- macros

    private void HandleMacro(JsonElement msg)
    {
        if (!MachineActionAllowed())
        {
            SendMacroResult(false, "machine_controls_gated");
            return;
        }
        if (Str(msg, "action").ToLowerInvariant() != "run")
        {
            SendMacroResult(false, "unsupported_action");
            return;
        }
        if (!TryInteger(msg, "id", out var id))
        {
            SendMacroResult(false, "invalid_id");
            return;
        }
        var macro = NamedMacros().FirstOrDefault(m => m.Id == id);
        if (macro is null)
        {
            SendMacroResult(false, "not_named", (int)id);
            return;
        }
        foreach (var raw in macro.Gcode.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) SendLine(line);
        }
        _controller.Console.Info($"CYD: ran macro {macro.Id}: {macro.Name}");
        SendMacroResult(true, id: macro.Id);
    }

    // ---- runtime (overrides, air, pause/resume, stop)

    private void HandleRuntime(JsonElement msg)
    {
        var action = Str(msg, "action").ToLowerInvariant();
        switch (action)
        {
            case "override":
                var target = Str(msg, "target").ToLowerInvariant();
                var id = target == "feed" ? "feedOverride" : target == "spindle" ? "spindleOverride" : null;
                if (id is null)
                {
                    SendRuntimeResult(false, "bad_override_target", action);
                    return;
                }
                var delta = TryNumber(msg, "delta", out var d) ? d : 0;
                var reset = msg.TryGetProperty("reset", out var r) && r.ValueKind == JsonValueKind.True;
                var path = target == "feed" ? StatePaths.FeedOverride : StatePaths.SpindleOverride;
                double value;
                if (reset) value = 100;
                else if (delta > 0) value = Math.Min(_state.Get(path, 100.0) + 10, 300);
                else if (delta < 0) value = Math.Max(_state.Get(path, 100.0) - 10, 10);
                else value = _state.Get(path, 100.0);
                Fire(_commands.ExecuteAsync(id, CommandArgs.Empty.With("value", value)));
                SendRuntimeResult(true, action: action);
                SendMachineState(force: true);
                return;

            case "air":
                var next = msg.TryGetProperty("on", out var on) ? Truthy(on) : !_state.Get("switch.air", false);
                SendLine(MachineState.Equals("Run", StringComparison.OrdinalIgnoreCase) ? (next ? "buffer M7" : "buffer M9") : MachineCommands.Air(next));
                SendRuntimeResult(true, action: action);
                SendMachineState(force: true);
                return;

            case "pause_resume":
                Fire(_commands.ExecuteAsync("pauseResume"));
                SendRuntimeResult(true, action: action);
                SendMachineState(force: true);
                return;

            case "stop" or "probe_cancel":
                StopContinuousJog();
                ClearJogSession();
                Fire(_commands.ExecuteAsync("stop"));
                SendRuntimeResult(true, action: action);
                SendMachineState(force: true);
                return;

            default:
                SendRuntimeResult(false, "unsupported_action", action);
                return;
        }
    }

    // ---- position

    private void HandlePosition(JsonElement msg)
    {
        var action = Str(msg, "action").ToLowerInvariant();
        if (!MachineActionAllowed())
        {
            SendPositionResult(false, $"machine_not_idle:{MachineState.ToLowerInvariant()}", action);
            return;
        }
        switch (action)
        {
            case "goto":
                switch (Str(msg, "target").ToLowerInvariant())
                {
                    case "work_origin":
                        SendLine(MachineCommands.GotoWorkOrigin);
                        SendPositionResult(true, action: action, message: "Going to work origin");
                        return;
                    case "path_origin":
                        if (ProbeCommands.GotoPathOrigin(_state) is not { } gotoPath)
                        {
                            SendPositionResult(false, "path_origin_unavailable", action);
                            return;
                        }
                        SendLine(gotoPath);
                        SendPositionResult(true, action: action, message: "Going to path origin");
                        return;
                    default:
                        SendPositionResult(false, "unsupported_target", action);
                        return;
                }
            case "set_origin":
                switch (Str(msg, "axes").ToLowerInvariant())
                {
                    case "xy":
                        SendLine(MachineCommands.SetWorkPosition(0, 0));
                        SendPositionResult(true, action: action, message: "Origin set: X/Y");
                        SendMachineState(force: true);
                        return;
                    case "xyz":
                        SendLine(MachineCommands.SetWorkPosition(0, 0, 0));
                        SendPositionResult(true, action: action, message: "Origin set: X/Y/Z");
                        SendMachineState(force: true);
                        return;
                    default:
                        SendPositionResult(false, "unsupported_axes", action);
                        return;
                }
            default:
                SendPositionResult(false, "unsupported_action", action);
                return;
        }
    }

    // ---- manual milling mode (C1 only)

    private void ManualResult(bool ok, string action, string reason = "", long requestId = 0)
    {
        // Knob detents can arrive faster than the pendant can draw snapshots: successful edits are
        // acknowledged by the regular state poll, failures are reported (rate-limited).
        if (action is "rpm" or "feed")
        {
            if (ok) return;
            var now = Now;
            if (reason == _lastManualEditRejection.Reason && now - _lastManualEditRejection.At < 0.2) return;
            _lastManualEditRejection = (reason, now);
        }
        _client.Send(new Dictionary<string, object?> { ["type"] = "manual_result", ["ok"] = ok, ["action"] = action, ["reason"] = reason, ["request_id"] = requestId });
        SendMachineState(force: true);
    }

    private void HandleManual(JsonElement msg)
    {
        var action = Str(msg, "action");
        var requestId = TryInteger(msg, "request_id", out var rid) ? rid : 0;
        if (action == "exit")
        {
            if (_manualActive)
            {
                StopContinuousJog(force: true);
                ClearJogSession();
            }
            _manualActive = false;
            _manualRpmDirty = false;
            ManualResult(true, action);
            return;
        }
        if (!ManualAllowed())
        {
            ManualResult(false, action, "manual_controls_gated");
            return;
        }
        if (action == "enter")
        {
            if (_controller.ContinuousJogActive || _pendingJog is not null)
            {
                ManualResult(false, action, "stop_jog_first");
                return;
            }
            if (!_manualActive)
            {
                var rpm = _state.Get(StatePaths.SpindleTarget, 0.0);
                if (double.IsFinite(rpm) && rpm > 0) _manualRpm = Math.Clamp(Math.Round(rpm), 1000, 15000);
                _manualSpindleCommanded = null;
            }
            _manualActive = true;
            _manualLastReason = "";
            ManualResult(true, action, requestId: requestId);
            return;
        }
        if (!_manualActive)
        {
            ManualResult(false, action, _manualLastReason.Length > 0 ? _manualLastReason : "manual_mode_required");
            return;
        }
        if (action is "feed" or "rpm")
        {
            var delta = TryNumber(msg, "delta", out var d) ? d : double.NaN;
            if (!double.IsFinite(delta) || Math.Abs(delta) > (action == "rpm" ? 10000 : 1000))
            {
                ManualResult(false, action, "invalid_increment");
                return;
            }
            if (action == "feed")
            {
                if (_controller.ContinuousJogActive || _pendingJog is not null)
                {
                    StopContinuousJog(force: true);
                    ClearJogSession();
                }
                var limit = Str(msg, "axis") == "Z" ? 800.0 : 3000.0;
                _manualFeed = Math.Clamp(Math.Min(_manualFeed, limit) + delta, 1.0, limit);
            }
            else
            {
                if (ManualMotionBusy())
                {
                    ManualResult(false, action, "release_mpg_first");
                    return;
                }
                var nextRpm = Math.Clamp(Math.Round(_manualRpm + delta), 1000, 15000);
                if (nextRpm != _manualRpm)
                {
                    _manualRpm = nextRpm;
                    if (SpindleOn())
                    {
                        _manualRpmDirty = true;
                        ServiceManualRpm();
                    }
                }
            }
            ManualResult(true, action);
            return;
        }
        if (action == "spindle")
        {
            if (!msg.TryGetProperty("on", out var on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                ManualResult(false, action, "invalid_spindle_state");
                return;
            }
            var wantOn = on.ValueKind == JsonValueKind.True;
            if (wantOn && ManualMotionBusy())
            {
                ManualResult(false, action, "release_mpg_first");
                return;
            }
            if (!wantOn)
            {
                StopContinuousJog(force: true);
                ClearJogSession();
            }
            _manualRpmDirty = false;
            SendLine(MachineCommands.Spindle(wantOn, _manualRpm));
            _manualSpindleCommanded = wantOn;
        }
        else if (action == "air")
        {
            if (!msg.TryGetProperty("on", out var on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                ManualResult(false, action, "invalid_air_state");
                return;
            }
            var wantOn = on.ValueKind == JsonValueKind.True;
            SendLine(MachineCommands.Air(wantOn));
            _airCommanded = wantOn;
            _airStateAtCommand = _state.Get("switch.air", false);
            _airCommandedAt = Now;
            SendLine("diagnose");
        }
        else
        {
            ManualResult(false, action, "unsupported_action");
            return;
        }
        ManualResult(true, action);
    }

    private void ServiceManualRpm()
    {
        if (!_manualRpmDirty) return;
        if (!_manualActive || !ManualAllowed() || ManualMotionBusy() || !SpindleOn())
        {
            _manualRpmDirty = false;
            return;
        }
        var now = Now;
        if (_manualRpmSentAt >= 0 && now - _manualRpmSentAt < 0.1) return;
        _manualRpmDirty = false;
        _manualRpmSentAt = now;
        SendLine(MachineCommands.Spindle(true, _manualRpm));
    }
}
