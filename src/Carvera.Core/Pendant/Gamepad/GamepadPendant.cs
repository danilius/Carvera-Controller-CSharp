using Carvera.Core.Commands;
using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Pendant.Gamepad;

public abstract record GamepadEvent;
public sealed record GamepadConnected(string Name) : GamepadEvent;
public sealed record GamepadDisconnected : GamepadEvent;
/// <summary>An axis moved. <see cref="Value"/> is the raw SDL value, -32768 to 32767.</summary>
public sealed record GamepadAxis(int Axis, int Value) : GamepadEvent;
public sealed record GamepadButton(int Button, bool Down) : GamepadEvent;
/// <summary>The D-pad: <see cref="Dx"/> -1 left, +1 right; <see cref="Dy"/> +1 up, -1 down.</summary>
public sealed record GamepadHat(int Dx, int Dy) : GamepadEvent;

/// <summary>Where gamepad input comes from; the desktop app supplies an SDL implementation, tests supply a fake.</summary>
public interface IGamepadSource : IDisposable
{
    event Action<GamepadEvent>? Event;
    void Start();
}

public sealed class GamepadOptions
{
    public required Func<GamepadBindings> Bindings { get; init; }
    public Func<PendantPolicy> Policy { get; init; } = () => new PendantPolicy();
    public Func<IReadOnlyList<PendantMacro>> Macros { get; init; } = () => [];
    /// <summary>Stick dead zone as a fraction of full travel.</summary>
    public Func<double> Deadzone { get; init; } = () => 0.15;
    /// <summary>Fastest continuous jog in mm/min, reached with the largest step size.</summary>
    public Func<double> MaxJogSpeed { get; init; } = () => 3000;
    public Func<(bool X, bool Y, bool Z, bool A)> Invert { get; init; } = () => (false, false, false, false);
}

/// <summary>
/// A gamepad as a pendant, ported from the Python controller's gamepad manager and <c>GamepadPendant</c>. Sticks and the
/// D-pad jog (one step per push in step mode, a continuous jog while held in continuous mode, which needs community
/// firmware), buttons and triggers run actions such as stop, feed override or macros. The pendant obeys the same jogging
/// rules as every other one; stop, reset and pause are never gated.
/// </summary>
public sealed class GamepadPendant : IDisposable
{
    public const string PendantDisplayName = "Gamepad";
    private const double ZMaxSpeed = 800;
    private static readonly double[] StepSizes = [0.01, 0.1, 1.0, 10.0];

    private readonly CarveraController _controller;
    private readonly CommandRegistry _commands;
    private readonly IGamepadSource _source;
    private readonly GamepadOptions _options;
    private readonly PendantGate _gate;
    private readonly object _lock = new();

    private bool _connected;
    private bool _continuousMode;
    private readonly Dictionary<int, double> _axisValues = [];
    private readonly Dictionary<string, int> _heldActions = [];
    private readonly Dictionary<string, bool> _heldTriggers = [];
    private readonly Dictionary<string, bool> _heldHat = [];
    private readonly Dictionary<string, int> _lastDirection = [];
    private string? _activeContinuousAction;
    private bool _closed;

    public GamepadPendant(CarveraController controller, CommandRegistry commands, IGamepadSource source, GamepadOptions options)
    {
        _controller = controller;
        _commands = commands;
        _source = source;
        _options = options;
        _gate = new PendantGate(controller.State, options.Policy);
        _source.Event += OnEvent;
        PublishJogMode();
    }

    public bool IsConnected { get { lock (_lock) return _connected; } }
    public bool ContinuousMode { get { lock (_lock) return _continuousMode; } }

    public void Start() => _source.Start();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            _source.Event -= OnEvent;
            ReleaseAll();
            if (_controller.ContinuousJogActive) Fire(_controller.StopContinuousJogAsync());
            _activeContinuousAction = null;
            SetLink(false);
        }
        _source.Dispose();
    }

    private void Fire(Task task) =>
        _ = task.ContinueWith(t => _controller.Console.Error($"Gamepad: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);

    private void SetLink(bool connected, string? name = null)
    {
        _connected = connected;
        PendantStatus.Publish(_controller.State, "gamepad", connected, name ?? PendantDisplayName);
    }

    private void PublishJogMode() => _controller.State.Set(StatePaths.JogMode, _continuousMode ? "continuous" : "step");

    // ------------------------------------------------------------------ input

    private void OnEvent(GamepadEvent e)
    {
        lock (_lock)
        {
            if (_closed) return;
            try
            {
                switch (e)
                {
                    case GamepadConnected c:
                        SetLink(true, c.Name);
                        _controller.Console.Info($"Gamepad connected: {c.Name}.");
                        break;
                    case GamepadDisconnected:
                        ReleaseAll();
                        SetLink(false);
                        _controller.Console.Warning("Gamepad disconnected.");
                        break;
                    case GamepadAxis a: OnAxis(a.Axis, a.Value); break;
                    case GamepadHat h: OnHat(h.Dx, h.Dy); break;
                    case GamepadButton { Down: true } b:
                        if (_options.Bindings().ButtonAction(b.Button) is { } action) RunAction(action);
                        break;
                }
            }
            catch (Exception ex)
            {
                _controller.Console.Error($"Gamepad: {ex.Message}");
            }
        }
    }

    private double Deadzone => Math.Clamp(_options.Deadzone(), 0.01, 0.99);

    private double ApplyDeadzone(double value)
    {
        if (Math.Abs(value) < Deadzone) return 0;
        return Math.Sign(value) * (Math.Abs(value) - Deadzone) / (1 - Deadzone);
    }

    private void OnAxis(int axis, int raw)
    {
        var bindings = _options.Bindings();
        var normalised = Math.Clamp(raw / 32767.0, -1.0, 1.0);

        // A trigger axis: each direction is bound and released separately.
        var plus = bindings.TriggerAction(axis, "+");
        var minus = bindings.TriggerAction(axis, "-");
        if (plus is not null || minus is not null)
        {
            if (plus is not null) HandleTrigger(plus, normalised);
            if (minus is not null) HandleTrigger(minus, -normalised);
            return;
        }

        var action = bindings.AxisAction(axis);
        if (action is null) return;
        var filtered = ApplyDeadzone(normalised);
        var wasActive = _axisValues.GetValueOrDefault(axis) != 0;
        var isActive = filtered != 0;
        if (isActive && !wasActive) _heldActions[action] = filtered > 0 ? 1 : -1;
        else if (!isActive && wasActive)
        {
            _heldActions.Remove(action);
            StopJog(action);
        }
        _axisValues[axis] = filtered;
        if (isActive) Jog(action, filtered);
    }

    private void HandleTrigger(string action, double value)
    {
        var active = value > Deadzone;
        var wasHeld = _heldTriggers.GetValueOrDefault(action);
        if (active && !wasHeld)
        {
            _heldTriggers[action] = true;
            RunAction(action);
        }
        else if (!active && wasHeld) _heldTriggers[action] = false;
    }

    private void OnHat(int dx, int dy)
    {
        var bindings = _options.Bindings();
        var pressed = new Dictionary<string, bool> { ["up"] = dy > 0, ["down"] = dy < 0, ["left"] = dx < 0, ["right"] = dx > 0 };
        foreach (var direction in _heldHat.Keys.ToList())
        {
            if (!_heldHat[direction] || pressed[direction]) continue;
            _heldHat[direction] = false;
            var released = bindings.HatAction(direction == "left" ? -1 : direction == "right" ? 1 : 0, direction == "up" ? 1 : direction == "down" ? -1 : 0);
            if (released is null) continue;
            _heldActions.Remove(released);
            if (released.StartsWith("jog_", StringComparison.Ordinal)) StopJog(released);
        }
        foreach (var (direction, isPressed) in pressed)
        {
            if (!isPressed || _heldHat.GetValueOrDefault(direction)) continue;
            _heldHat[direction] = true;
            var action = bindings.HatAction(direction == "left" ? -1 : direction == "right" ? 1 : 0, direction == "up" ? 1 : direction == "down" ? -1 : 0);
            if (action is null) continue;
            if (action.StartsWith("jog_", StringComparison.Ordinal))
            {
                var sign = direction is "up" or "right" ? 1 : -1;
                _heldActions[action] = sign; // held before the jog runs, as the stick path does
                Jog(action, sign);
            }
            else RunAction(action);
        }
    }

    /// <summary>Lets go of everything: used when the pad is disconnected or its input can no longer be trusted.</summary>
    private void ReleaseAll()
    {
        var held = _heldActions.Keys.ToList();
        _axisValues.Clear();
        _heldActions.Clear();
        _heldTriggers.Clear();
        _heldHat.Clear();
        foreach (var action in held) StopJog(action);
        // A jog started from a stick that is no longer there must not run on.
        if (_activeContinuousAction is not null && _controller.ContinuousJogActive)
        {
            Fire(_controller.StopContinuousJogAsync());
            _activeContinuousAction = null;
        }
    }

    // ------------------------------------------------------------------ jogging

    private double StepSize => _controller.State.Get(StatePaths.JogStep, 0.1);

    private static string? AxisLetter(string action) => action switch { "jog_x" => "X", "jog_y" => "Y", "jog_z" => "Z", "jog_a" => "A", _ => null };

    private double Invert(string axis, double value)
    {
        var (x, y, z, a) = _options.Invert();
        return axis switch { "X" when x => -value, "Y" when y => -value, "Z" when z => -value, "A" when a => -value, _ => value };
    }

    private void Jog(string action, double value)
    {
        if (!_gate.JoggingEnabled(false) || AxisLetter(action) is not { } axis || value == 0) return;
        value = Invert(axis, value);
        var direction = value > 0 ? 1 : -1;
        if (!_continuousMode) StepJog(action, axis, direction);
        else ContinuousJog(action, axis, direction);
    }

    private void StepJog(string action, string axis, int direction)
    {
        // One push, one step: repeated events with the stick held the same way do nothing.
        if (_heldActions.ContainsKey(action) && _lastDirection.GetValueOrDefault(action) == direction) return;
        _lastDirection[action] = direction;
        Fire(_commands.ExecuteAsync("jog", CommandArgs.Empty.With("axis", axis).With("direction", direction)));
    }

    private static readonly Dictionary<double, double> SpeedFraction = new() { [0.01] = 0.02, [0.1] = 0.10, [1.0] = 0.30, [10.0] = 1.0 };

    private double ContinuousFeed(string axis)
    {
        var step = StepSize;
        var fraction = SpeedFraction.TryGetValue(step, out var f) ? f : 1.0;
        var cap = _options.MaxJogSpeed();
        if (axis == "Z") cap = Math.Min(cap, ZMaxSpeed);
        return fraction * cap;
    }

    private void ContinuousJog(string action, string axis, int direction)
    {
        var previous = _lastDirection.GetValueOrDefault(action);
        _lastDirection[action] = direction;
        if (previous != 0 && previous != direction && _controller.ContinuousJogActive && action == _activeContinuousAction)
            Fire(_controller.StopContinuousJogAsync());
        if (_controller.ContinuousJogActive) return;
        Fire(_controller.StartContinuousJogAsync($"{axis}{direction}", ContinuousFeed(axis)));
        _activeContinuousAction = action;
    }

    private void StopJog(string action)
    {
        _lastDirection.Remove(action);
        if (action != _activeContinuousAction) return;
        _activeContinuousAction = null;
        if (_controller.ContinuousJogActive) Fire(_controller.StopContinuousJogAsync());
    }

    // ------------------------------------------------------------------ actions

    private void RunAction(string action)
    {
        var state = _controller.State;
        if (action.StartsWith("macro_", StringComparison.Ordinal))
        {
            if (int.TryParse(action.AsSpan(6), out var id)) RunMacro(id);
            return;
        }
        switch (action)
        {
            case "reset": Fire(_commands.ExecuteAsync("reset")); break;
            case "stop": Fire(_commands.ExecuteAsync("stop")); break;
            case "start_pause": Fire(_commands.ExecuteAsync("pauseResume")); break;
            case "mode_toggle": ToggleMode(); break;
            case "feed_plus": Override("feedOverride", StatePaths.FeedOverride, +10); break;
            case "feed_minus": Override("feedOverride", StatePaths.FeedOverride, -10); break;
            case "spindle_plus": Override("spindleOverride", StatePaths.SpindleOverride, +10); break;
            case "spindle_minus": Override("spindleOverride", StatePaths.SpindleOverride, -10); break;
            case "m_home": Fire(_commands.ExecuteAsync("gotoMachineHome")); break;
            case "safe_z": Fire(_commands.ExecuteAsync("gotoSafeZ")); break;
            case "w_home": Fire(_commands.ExecuteAsync("gotoWorkHome")); break;
            case "spindle_on_off":
                if (!state.Get(StatePaths.LaserMode, false))
                    Fire(_commands.ExecuteAsync("setSpindle", CommandArgs.Empty.With("on", !(state.Get(StatePaths.SpindleCurrent, 0.0) > 0))));
                break;
            case "probe_z": _controller.Console.Info("Gamepad: Probe Z is not available in this app yet."); break;
            case "step_size_up": ChangeStep(+1); break;
            case "step_size_down": ChangeStep(-1); break;
        }
    }

    private void Override(string command, string path, int delta)
    {
        var value = Math.Clamp(_controller.State.Get(path, 100.0) + delta, 10, 300);
        Fire(_commands.ExecuteAsync(command, CommandArgs.Empty.With("value", value)));
    }

    private void ChangeStep(int direction)
    {
        // Steps larger than the biggest the gamepad offers are skipped so the speed table stays meaningful.
        var current = _controller.State.Get(StatePaths.JogStep, 0.1);
        var index = Array.FindIndex(StepSizes, s => Math.Abs(s - current) < 1e-9);
        if (index < 0) index = 1;
        var next = Math.Clamp(index + direction, 0, StepSizes.Length - 1);
        if (next != index) _controller.State.Set(StatePaths.JogStep, StepSizes[next]);
    }

    private void ToggleMode()
    {
        if (!_continuousMode && !_controller.State.Get(StatePaths.CommunityFirmware, false))
        {
            _controller.Console.Warning("Gamepad: continuous jogging needs community firmware.");
            return;
        }
        if (_continuousMode && _controller.ContinuousJogActive) Fire(_controller.StopContinuousJogAsync());
        _continuousMode = !_continuousMode;
        PublishJogMode();
    }

    private void RunMacro(int id)
    {
        var macro = _options.Macros().FirstOrDefault(m => m.Id == id);
        if (macro is null || macro.Gcode.Trim().Length == 0)
        {
            _controller.Console.Warning($"Gamepad: no macro {id} is set up.");
            return;
        }
        if (!_gate.MachineAllowsJogging(false))
        {
            _controller.Console.Warning($"Gamepad: macro {id} not run; the machine is busy.");
            return;
        }
        foreach (var raw in macro.Gcode.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) Fire(_controller.SendLineAsync(line));
        }
    }
}
