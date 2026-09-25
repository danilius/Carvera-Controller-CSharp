namespace Carvera.App.Layout;

/// <summary>
/// Built-in look of each element type per state (light theme). Layout files layer their own classes,
/// style, visuals and conditions on top of these.
/// </summary>
public static class VisualDefaults
{
    /// <summary>Later entries for the same state replace earlier ones.</summary>
    private static Dictionary<string, VisualBlock> Map(params (string State, string Css)[] entries)
    {
        var map = new Dictionary<string, VisualBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var (state, css) in entries) map[state] = VisualBlock.Css(css);
        return map;
    }

    private static readonly (string, string)[] ButtonLike =
    [
        ("normal", "background: @surface; foreground: @text; borderColor: @border; borderWidth: 1; cornerRadius: 6; padding: 6 12; textAlign: center"),
        ("hover", "background: @hover; borderColor: @accent"),
        ("pressed", "background: @pressed; borderColor: @accent"),
        ("disabled", "opacity: 0.45"),
        ("active", "background: @accentSoft; borderColor: @accent"),
        ("selected", "background: @accent; foreground: @accentText; borderColor: @accent"),
        ("on", "background: @accent; foreground: @accentText; borderColor: @accent"),
    ];

    private static readonly Dictionary<string, Dictionary<string, VisualBlock>> ByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = Map(ButtonLike),
        ["toggle"] = Map(ButtonLike),
        ["choiceOption"] = Map(ButtonLike),
        ["jogButton"] = Map([.. ButtonLike, ("normal", "background: @surface; foreground: @text; borderColor: @border; borderWidth: 1; cornerRadius: 8; padding: 4; textAlign: center; fontWeight: semibold")]),
        ["panel"] = Map(
            ("normal", "background: @surface; borderColor: @border; borderWidth: 1; cornerRadius: 10; padding: 10")),
        ["axisReadout"] = Map(
            ("normal", "background: @surface; borderColor: @border; borderWidth: 1; cornerRadius: 8; padding: 6 12"),
            ("hover", "borderColor: @borderStrong"),
            ("disabled", "opacity: 0.6")),
        ["value"] = Map(("normal", "padding: 2 4")),
        ["machineStatus"] = Map(
            ("normal", "background: @surfaceAlt; foreground: @text; borderColor: @border; borderWidth: 1; cornerRadius: 6; padding: 6 14; fontWeight: semibold; textAlign: center"),
            ("idle", "background: #DBEAFE; foreground: #1D4ED8; borderColor: #93C5FD"),
            ("run", "background: #DCFCE7; foreground: #15803D; borderColor: #86EFAC"),
            ("hold", "background: #FEF3C7; foreground: #B45309; borderColor: #FCD34D"),
            ("pause", "background: #FEF3C7; foreground: #B45309; borderColor: #FCD34D"),
            ("alarm", "background: #FEE2E2; foreground: #B91C1C; borderColor: #FCA5A5"),
            ("home", "background: #FEF9C3; foreground: #A16207; borderColor: #FDE047"),
            ("tool", "background: #DCFCE7; foreground: #15803D; borderColor: #86EFAC"),
            ("wait", "background: #FEF9C3; foreground: #A16207; borderColor: #FDE047"),
            ("sleep", "background: #F1F5F9; foreground: #64748B"),
            ("disable", "background: #E2E8F0; foreground: #475569"),
            ("disconnected", "background: #F1F5F9; foreground: #64748B; borderColor: @border")),
        ["indicator"] = Map(("normal", "padding: 2 4"), ("disabled", "opacity: 0.5")),
        ["console"] = Map(("normal", "background: @surface; borderColor: @border; borderWidth: 1; cornerRadius: 8; padding: 4")),
        ["gcodeList"] = Map(("normal", "background: @surface; borderColor: @border; borderWidth: 1; cornerRadius: 8; padding: 2")),
        ["toolpath"] = Map(("normal", "background: @surface; borderColor: @border; borderWidth: 1; cornerRadius: 8")),
        ["progress"] = Map(("normal", "padding: 0"), ("disabled", "opacity: 0.5")),
        ["text"] = Map(("disabled", "opacity: 0.5")),
        ["override"] = Map(("disabled", "opacity: 0.5")),
        ["jogPad"] = Map(("disabled", "opacity: 0.5")),
        ["slider"] = Map(("disabled", "opacity: 0.5")),
    };

    public static IReadOnlyDictionary<string, VisualBlock> For(string type) =>
        ByType.TryGetValue(type, out var map) ? map : EmptyMap;

    private static readonly Dictionary<string, VisualBlock> EmptyMap = new();
}
