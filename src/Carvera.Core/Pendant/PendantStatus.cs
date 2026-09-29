using Carvera.Core.State;

namespace Carvera.Core.Pendant;

/// <summary>
/// Publishes pendant link state. Each device has its own paths (<c>pendant.cyd.connected</c>, <c>pendant.gamepad.name</c>...)
/// and the combined <c>pendant.connected</c> / <c>pendant.name</c> say whether any pendant is connected and which.
/// </summary>
public static class PendantStatus
{
    public static readonly IReadOnlyList<string> Devices = ["cyd", "gamepad"];

    public static string ConnectedPath(string device) => $"pendant.{device}.connected";
    public static string NamePath(string device) => $"pendant.{device}.name";

    public static void Publish(StateStore state, string device, bool connected, string? name)
    {
        using var _ = state.BeginBatch();
        state.Set(ConnectedPath(device), connected);
        state.Set(NamePath(device), connected ? name : null);
        var names = Devices.Where(d => state.Get(ConnectedPath(d), false)).Select(d => state.Get<string>(NamePath(d)) ?? d).ToList();
        state.Set(StatePaths.PendantConnected, names.Count > 0);
        state.Set(StatePaths.PendantName, names.Count > 0 ? string.Join(", ", names) : null);
    }
}
