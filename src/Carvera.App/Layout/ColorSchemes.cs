namespace Carvera.App.Layout;

/// <summary>
/// Named colour schemes for the operation colours of the G-code views (the 3D path, the G-code list's colour bars,
/// the operation list). "Layout" leaves the colours to the layout's theme (<c>operation1</c> to <c>operation10</c>).
/// </summary>
public static class ColorSchemes
{
    public const string Layout = "Layout";

    private static readonly Dictionary<string, string[]> Palettes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Okabe-Ito: tells apart for the common kinds of colour blindness.
        ["Colour-blind safe"] = ["#0072B2", "#D55E00", "#009E73", "#E69F00", "#CC79A7", "#56B4E9", "#F0E442", "#000000", "#882255", "#44AA99"],
        // Strong, dark colours for a bright shop or a projector.
        ["High contrast"] = ["#0000CC", "#CC0000", "#007700", "#000000", "#8800AA", "#0088AA", "#AA5500", "#555555", "#CC0088", "#004400"],
        // One hue in ten steps, light to dark: operations show their order rather than their identity.
        ["Blue ramp"] = ["#93C5FD", "#7DB3F8", "#679FF2", "#528BEA", "#3D78E0", "#2C66D2", "#2055BF", "#1A46A8", "#153A90", "#102E78"],
        ["Earth tones"] = ["#8C510A", "#BF812D", "#80CDC1", "#35978F", "#01665E", "#762A83", "#9970AB", "#5AAE61", "#1B7837", "#DFC27D"],
    };

    /// <summary>The names offered in the settings, with "Layout" first.</summary>
    public static IReadOnlyList<string> Names { get; } = [Layout, .. Palettes.Keys];

    /// <summary>The theme tokens (operation1 to operation10) a scheme sets; empty for "Layout" and for names that do not exist.</summary>
    public static IReadOnlyDictionary<string, string> Tokens(string? scheme)
    {
        if (scheme is null || !Palettes.TryGetValue(scheme, out var colors)) return new Dictionary<string, string>();
        return colors.Select((color, i) => ($"operation{i + 1}", color)).ToDictionary(p => p.Item1, p => p.color);
    }
}
