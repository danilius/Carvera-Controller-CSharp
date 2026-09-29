using System.Globalization;
using Carvera.Core.State;

namespace Carvera.Core.Probing;

/// <summary>
/// The ring-gauge drift check as a small state machine. It publishes its progress under <c>probe.drift.*</c>, so a
/// layout shows it with ordinary labels and buttons, and the commands <c>ringGaugeProbe</c>, <c>ringGaugeBack</c>,
/// <c>ringGaugeReset</c>, <c>ringGaugeApplyTip</c> and <c>ringGaugePersist</c> drive it.
/// </summary>
public sealed class RingGaugeSession
{
    private readonly StateStore _state;
    private readonly IProbeStore _store;
    private readonly Func<string, Task> _send;
    private readonly List<(double X, double Y)> _points = [];
    private DriftResult? _result;
    private bool _running, _applyTip = true, _persist;
    private string _message = RingGaugeDrift.Introduction;
    private int _step;

    /// <summary>How often the machine state is checked while the probe runs, and how many checks pass before "did not start".</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int StartPolls { get; init; } = 20;

    public RingGaugeSession(StateStore state, IProbeStore store, Func<string, Task> send)
    {
        _state = state;
        _store = store;
        _send = send;
        Reset();
    }

    public int Step => _step;
    public bool Done => _step >= RingGaugeDrift.Steps.Length;
    public bool Running => _running;
    public IReadOnlyList<(double X, double Y)> Points => _points;

    public void Reset()
    {
        _points.Clear();
        _result = null;
        _step = 0;
        _applyTip = true;
        _persist = _store.Drift.Enabled;
        _running = false;
        _message = RingGaugeDrift.Introduction;
        Publish();
    }

    public void Back()
    {
        if (_step <= 0 || _running) return;
        if (_points.Count >= _step) _points.RemoveRange(_step - 1, _points.Count - (_step - 1));
        _step--;
        _result = null;
        _message = $"Returned to {RingGaugeDrift.Steps[_step].Title}.";
        Publish();
    }

    public void SetApplyTip(bool value) { _applyTip = value; Publish(); }

    public void SetPersist(bool value)
    {
        _persist = value;
        if (value && Done && _result is not null) Store(_result);
        else _store.Drift = _store.Drift with { Enabled = value };
        Publish();
    }

    /// <summary>Measures the ring once (or restarts when all three measurements are in). <paramref name="confirm"/> is asked before the machine moves.</summary>
    public async Task ProbeAsync(Func<string, Task<bool>> confirm)
    {
        if (_running) return;
        if (Done) { Reset(); return; }
        var build = RingGaugeDrift.BuildProbe(_store.ProbeSettings("probeTip"), _applyTip);
        if (!build.Ok)
        {
            _message = build.Problem + ". Fill in the probe tip settings in the Probing tab first.";
            Publish();
            return;
        }
        if (!await confirm($"Probe the ring gauge now? The machine will move.\n\n{build.Gcode}").ConfigureAwait(false)) return;

        _running = true;
        _message = "Probing: " + build.Gcode;
        Publish();
        try
        {
            await _send(build.Gcode + "\n").ConfigureAwait(false);
            if (!await WaitForProbeAsync().ConfigureAwait(false)) return;
            Capture((_state.Get(StatePaths.AxisWork("x"), 0.0), _state.Get(StatePaths.AxisWork("y"), 0.0)));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _message = "The probe command could not be sent: " + ex.Message;
        }
        finally
        {
            _running = false;
            Publish();
        }
    }

    /// <summary>Waits for the machine to leave Idle and come back. Sets the message and returns false when it does not.</summary>
    private async Task<bool> WaitForProbeAsync()
    {
        var busy = false;
        for (var polls = 1; ; polls++)
        {
            await Task.Delay(PollInterval).ConfigureAwait(false);
            if (!_state.Get(StatePaths.Connected, false))
            {
                _message = "No connected machine was detected. Check the connection and restart this step.";
                return false;
            }
            if (_state.Get<string>(StatePaths.MachineState) != "Idle") busy = true;
            else if (busy) return true;
            else if (polls > StartPolls)
            {
                _message = "Probe did not appear to start. Check the connection and restart this step.";
                return false;
            }
        }
    }

    /// <summary>Records one measurement (the work X and Y after the probe finished).</summary>
    public void Capture((double X, double Y) point)
    {
        if (_points.Count > _step) _points[_step] = point; else _points.Add(point);
        _step = Math.Min(_points.Count, RingGaugeDrift.Steps.Length);
        _message = "Captured " + RingGaugeDrift.Format(point.X, point.Y) + ".";
        if (_points.Count >= RingGaugeDrift.Steps.Length) Conclude();
        Publish();
    }

    private void Conclude()
    {
        var result = RingGaugeDrift.Analyse(_points)!;
        _result = result;
        var stored = Store(result);
        var tip = _applyTip ? "Probe tip diameter was included in the probing command." : "Probe tip diameter was not included in the probing command.";
        _message = string.Create(CultureInfo.InvariantCulture,
            $"Worst center shift: {result.MaxShift:0.0000} mm ({RingGaugeDrift.Steps[result.First].Title} to {RingGaugeDrift.Steps[result.Second].Title})\n" +
            $"Estimated eccentricity lower bound: {result.LowerBound:0.0000} mm\n" +
            $"Center-position correction: {RingGaugeDrift.Format(result.CorrectionX, result.CorrectionY)}\n{tip}\n{result.Quality}{stored}");
    }

    private string Store(DriftResult result)
    {
        if (!_persist) return "";
        _store.Drift = new DriftCorrection(true, result.CorrectionX, result.CorrectionY);
        return "\nCorrection stored and enabled for future XY-zeroing probe jobs. Align the USB cable to the marked position before probing.";
    }

    /// <summary>Copies the session and the stored correction into the state store.</summary>
    public void Publish()
    {
        using var _ = _state.BeginBatch();
        var drift = _store.Drift;
        _state.Set(StatePaths.DriftEnabled, drift.Enabled);
        _state.Set(StatePaths.DriftX, drift.X);
        _state.Set(StatePaths.DriftY, drift.Y);
        _state.Set(StatePaths.DriftStep, _step);
        _state.Set(StatePaths.DriftDone, Done);
        _state.Set(StatePaths.DriftRunning, _running);
        _state.Set(StatePaths.DriftApplyTip, _applyTip);
        _state.Set(StatePaths.DriftPersist, _persist);
        if (Done)
        {
            _state.Set(StatePaths.DriftTitle, "Results");
            _state.Set(StatePaths.DriftText, "Review the measured center shift and correction estimate.");
        }
        else
        {
            _state.Set(StatePaths.DriftTitle, $"Step {_step + 1} of {RingGaugeDrift.Steps.Length}: {RingGaugeDrift.Steps[_step].Title}");
            _state.Set(StatePaths.DriftText, RingGaugeDrift.Steps[_step].Body);
        }
        _state.Set(StatePaths.DriftPrimary, _running ? "Probing..." : Done ? "Restart" : "Probe ring");
        _state.Set(StatePaths.DriftPoints, RingGaugeDrift.FormatPoints(_points));
        _state.Set(StatePaths.DriftResult, _message);
        _state.Set(StatePaths.DriftStored, RingGaugeDrift.StoredText(drift));
    }
}
