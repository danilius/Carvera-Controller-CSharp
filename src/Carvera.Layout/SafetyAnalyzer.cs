using System.Text.Json.Nodes;
using Carvera.Core.Commands;

namespace Carvera.Layout;

/// <summary>
/// Finds safety controls (Feed Hold, Stop, Reset) that a layout does not show. A control counts as shown
/// when a button, toggle or jog-style element runs one of its commands and neither it nor any ancestor is
/// hidden with "visible": false or a zero width/height. Conditional visibility (an expression) counts as shown.
/// Keyboard shortcuts do not count: they are not visible.
/// </summary>
public static class SafetyAnalyzer
{
    public static IReadOnlyList<string> FindMissing(LayoutDocument document)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (node, ancestors) in document.Root.Walk())
        {
            var command = node.GetString("command");
            if (command is null || node.Type is not ("button" or "toggle")) continue;
            if (IsHidden(node) || ancestors.Any(IsHidden)) continue;
            present.Add(command);
        }
        return StandardCommands.SafetyControls
            .Where(control => !control.CommandIds.Any(present.Contains))
            .Select(control => control.Name)
            .ToArray();
    }

    public static bool IsHidden(LayoutNode node)
    {
        var visible = node.Get("visible");
        if (visible is JsonValue v && ((v.TryGetValue<bool>(out var b) && !b) || (v.TryGetValue<string>(out var s) && s.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))))
            return true;
        if (node.Get("style") is JsonObject style && style["opacity"] is JsonValue o && o.TryGetValue<double>(out var opacity) && opacity <= 0.01)
            return true;
        return IsZero(node.Width) || IsZero(node.Height) || node.GetNumber("maxWidth") is 0 || node.GetNumber("maxHeight") is 0;
    }

    private static bool IsZero(SizeSpec size) => size.Kind is SizeKind.Pixels or SizeKind.Percent && size.Value <= 0;
}
