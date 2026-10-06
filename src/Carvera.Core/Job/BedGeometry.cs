using System.Globalization;
using Carvera.Core.State;

namespace Carvera.Core.Job;

/// <summary>
/// The machine bed as the firmware reports it (<c>coordinate.*</c> in config.txt): the work area, and where the anchors sit.
/// Machine coordinates run from the anchor 1 corner; the bed picture starts <see cref="AnchorWidth"/> further out, at the outer edge of the anchor.
/// The defaults are the Python controller's, for the Carvera.
/// </summary>
public sealed record BedGeometry(
    double SizeX, double SizeY, double Anchor1X, double Anchor1Y, double AnchorWidth, double AnchorLength,
    double Anchor2OffsetX, double Anchor2OffsetY, double RotationOffsetX, double RotationOffsetY)
{
    public static readonly BedGeometry Default = new(340, 240, -360.158, -234.568, 15, 100, 90, 45, -8, 0);

    /// <summary>Reads the geometry from config.txt values (keys like <c>coordinate.anchor1_x</c>), keeping the defaults for keys that are missing.</summary>
    public static BedGeometry From(IReadOnlyDictionary<string, string> config)
    {
        double V(string key, double fallback) =>
            config.TryGetValue(key, out var text) && double.TryParse(text.Split('#')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        var d = Default;
        return new BedGeometry(
            V("coordinate.worksize_x", d.SizeX), V("coordinate.worksize_y", d.SizeY),
            V("coordinate.anchor1_x", d.Anchor1X), V("coordinate.anchor1_y", d.Anchor1Y),
            V("coordinate.anchor_width", d.AnchorWidth), V("coordinate.anchor_length", d.AnchorLength),
            V("coordinate.anchor2_offset_x", d.Anchor2OffsetX), V("coordinate.anchor2_offset_y", d.Anchor2OffsetY),
            V("coordinate.rotation_offset_x", d.RotationOffsetX), V("coordinate.rotation_offset_y", d.RotationOffsetY));
    }

    /// <summary>The value a coordinate name such as <c>anchor1_x</c> has, for <see cref="Commands.WorkCommands.OriginPosition"/>.</summary>
    public double? Coordinate(string name) => name switch
    {
        "anchor1_x" => Anchor1X, "anchor1_y" => Anchor1Y,
        "anchor2_offset_x" => Anchor2OffsetX, "anchor2_offset_y" => Anchor2OffsetY,
        "rotation_offset_x" => RotationOffsetX, "rotation_offset_y" => RotationOffsetY,
        "anchor_width" => AnchorWidth, "anchor_length" => AnchorLength,
        "worksize_x" => SizeX, "worksize_y" => SizeY,
        _ => null,
    };

    /// <summary>Where a machine position (mm) lies on the bed picture, in mm from its lower-left corner.</summary>
    public (double X, double Y) ToBed(double machineX, double machineY) => (machineX - Anchor1X + AnchorWidth, machineY - Anchor1Y + AnchorWidth);

    /// <summary>The bed's lower-left corner in machine coordinates.</summary>
    public (double X, double Y) BedCorner => (Anchor1X - AnchorWidth, Anchor1Y - AnchorWidth);

    public void Publish(StateStore state)
    {
        using var _ = state.BeginBatch();
        state.Set(StatePaths.BedSizeX, SizeX);
        state.Set(StatePaths.BedSizeY, SizeY);
        state.Set(StatePaths.BedAnchor1X, Anchor1X);
        state.Set(StatePaths.BedAnchor1Y, Anchor1Y);
        state.Set(StatePaths.BedAnchorWidth, AnchorWidth);
        state.Set(StatePaths.BedAnchorLength, AnchorLength);
        state.Set(StatePaths.BedAnchor2X, Anchor2OffsetX);
        state.Set(StatePaths.BedAnchor2Y, Anchor2OffsetY);
    }

    /// <summary>Reads the geometry back from the state store (what <see cref="Publish"/> wrote), so components need not know where it came from.</summary>
    public static BedGeometry FromState(StateStore state)
    {
        var d = Default;
        return new BedGeometry(
            state.Get(StatePaths.BedSizeX, d.SizeX), state.Get(StatePaths.BedSizeY, d.SizeY),
            state.Get(StatePaths.BedAnchor1X, d.Anchor1X), state.Get(StatePaths.BedAnchor1Y, d.Anchor1Y),
            state.Get(StatePaths.BedAnchorWidth, d.AnchorWidth), state.Get(StatePaths.BedAnchorLength, d.AnchorLength),
            state.Get(StatePaths.BedAnchor2X, d.Anchor2OffsetX), state.Get(StatePaths.BedAnchor2Y, d.Anchor2OffsetY),
            d.RotationOffsetX, d.RotationOffsetY);
    }
}
