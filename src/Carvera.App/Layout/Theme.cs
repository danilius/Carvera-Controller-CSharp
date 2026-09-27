using Avalonia.Media;

namespace Carvera.App.Layout;

/// <summary>
/// Named colours and fonts. Layout files override tokens in their "theme" section and refer to them
/// as "@name" in colour properties.
/// </summary>
public sealed class Theme
{
    public static readonly IReadOnlyDictionary<string, string> LightDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["background"] = "#F1F4F8",
        ["surface"] = "#FFFFFF",
        ["surfaceAlt"] = "#F7F9FC",
        ["border"] = "#D8DEE7",
        ["borderStrong"] = "#B9C3D0",
        ["text"] = "#1E293B",
        ["textMuted"] = "#64748B",
        ["accent"] = "#2563EB",
        ["accentText"] = "#FFFFFF",
        ["accentSoft"] = "#DBE7FE",
        ["hover"] = "#EEF3FC",
        ["pressed"] = "#DCE6F8",
        ["success"] = "#16A34A",
        ["successSoft"] = "#DCFCE7",
        ["warning"] = "#D97706",
        ["warningSoft"] = "#FEF3C7",
        ["danger"] = "#DC2626",
        ["dangerSoft"] = "#FEE2E2",
        ["axisX"] = "#DC2626",
        ["axisY"] = "#16A34A",
        ["axisZ"] = "#2563EB",
        ["axisA"] = "#B45309",
        ["lampOff"] = "#CBD5E1",
        ["lampOn"] = "#22C55E",
        // Operation colours in G-code views, used in turn (operation1 for the first operation...).
        ["operation1"] = "#2563EB",
        ["operation2"] = "#DC2626",
        ["operation3"] = "#16A34A",
        ["operation4"] = "#D97706",
        ["operation5"] = "#7C3AED",
        ["operation6"] = "#0891B2",
        ["operation7"] = "#DB2777",
        ["operation8"] = "#65A30D",
        ["operation9"] = "#EA580C",
        ["operation10"] = "#4F46E5",
        ["fontFamily"] = "Segoe UI, Inter, Noto Sans, sans-serif",
        ["monoFontFamily"] = "Cascadia Mono, Consolas, DejaVu Sans Mono, monospace",
        ["fontSize"] = "13",
        ["radius"] = "6",
    };

    private readonly Dictionary<string, string> _tokens;

    public Theme(IReadOnlyDictionary<string, string>? overrides = null)
    {
        _tokens = new Dictionary<string, string>(LightDefaults, StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
            foreach (var (key, value) in overrides) _tokens[key] = value;
    }

    public string this[string token] => _tokens.TryGetValue(token, out var v) ? v : "";

    public double Number(string token, double fallback) =>
        double.TryParse(this[token], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public FontFamily FontFamily => new(this["fontFamily"]);
    public FontFamily MonoFontFamily => new(this["monoFontFamily"]);
    public double FontSize => Number("fontSize", 13);
    public double Radius => Number("radius", 6);

    /// <summary>Resolves "#RRGGBB", colour names and "@token" references; null when invalid.</summary>
    public Color? ResolveColor(string? text, int depth = 0)
    {
        if (string.IsNullOrWhiteSpace(text) || depth > 8) return null;
        text = text.Trim();
        if (text.StartsWith('@')) return ResolveColor(this[text[1..]], depth + 1);
        return Color.TryParse(text, out var color) ? color : null;
    }

    public IBrush? Brush(string? text) => ResolveColor(text) is { } c ? new SolidColorBrush(c) : null;
    public IBrush TokenBrush(string token) => Brush("@" + token) ?? Brushes.Magenta;

    public const int OperationColorCount = 10;

    /// <summary>Colour of the operation with this 0-based index (cycling through operation1..10).</summary>
    public Color OperationColor(int index) =>
        ResolveColor($"@operation{(index < 0 ? 0 : index % OperationColorCount) + 1}") ?? Colors.SteelBlue;
}
