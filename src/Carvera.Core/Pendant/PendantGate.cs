using Carvera.Core.State;

namespace Carvera.Core.Pendant;

/// <summary>
/// The rules for when a pendant may move the machine, shared by every pendant driver (the Python controller's
/// <c>_machine_allows_jogging</c> and <c>is_jogging_enabled</c>). Stop, reset and feed hold are never gated.
/// </summary>
public sealed class PendantGate(StateStore state, Func<PendantPolicy> policy)
{
    public PendantPolicy Policy => policy();

    public bool Playing => state.Get(StatePaths.JobPlaying, false);
    public string MachineState => state.Get(StatePaths.MachineState, "N/A") ?? "N/A";
    public bool SpindleOrLaserOn => state.Get(StatePaths.SpindleCurrent, 0.0) > 0 || state.Get(StatePaths.LaserState, false);

    /// <summary>The machine's state allows jogging. <paramref name="continuing"/> is true for a jog that is already under way.</summary>
    public bool MachineAllowsJogging(bool continuing)
    {
        var p = Policy;
        var s = MachineState;
        return (!Playing || s == "Pause")
            && (s is "Idle" or "Pause" || (s == "Run" && (p.AllowJoggingWhileRunning || (continuing && !Playing))))
            && (!SpindleOrLaserOn || p.AllowJoggingWhileSpindleOn);
    }

    /// <summary>The user has pendant jogging switched on and the machine allows it.</summary>
    public bool JoggingEnabled(bool continuing) => Policy.JoggingEnabled && MachineAllowsJogging(continuing);
}
