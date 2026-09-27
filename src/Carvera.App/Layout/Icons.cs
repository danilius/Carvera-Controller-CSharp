using Avalonia.Media;

namespace Carvera.App.Layout;

/// <summary>Built-in vector icons, referenced from layouts as "builtin:&lt;name&gt;" (24×24 design grid).</summary>
public static class Icons
{
    public static readonly IReadOnlyDictionary<string, string> PathData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["arrow-up"] = "M12 3 L20 13 H15 V21 H9 V13 H4 Z",
        ["arrow-down"] = "M12 21 L4 11 H9 V3 H15 V11 H20 Z",
        ["arrow-left"] = "M3 12 L13 4 V9 H21 V15 H13 V20 Z",
        ["arrow-right"] = "M21 12 L11 20 V15 H3 V9 H11 V4 Z",
        ["arrow-up-left"] = "M5 5 H15 L11.5 8.5 L19 16 L16 19 L8.5 11.5 L5 15 Z",
        ["arrow-up-right"] = "M19 5 V15 L15.5 11.5 L8 19 L5 16 L12.5 8.5 L9 5 Z",
        ["arrow-down-left"] = "M5 19 V9 L8.5 12.5 L16 5 L19 8 L11.5 15.5 L15 19 Z",
        ["arrow-down-right"] = "M19 19 H9 L12.5 15.5 L5 8 L8 5 L15.5 12.5 L19 9 Z",
        ["play"] = "M7 4 L20 12 L7 20 Z",
        ["pause"] = "M6 4 H10 V20 H6 Z M14 4 H18 V20 H14 Z",
        ["stop"] = "M5 5 H19 V19 H5 Z",
        ["reset"] = "M12 3 A9 9 0 1 0 21 12 H18 A6 6 0 1 1 12 6 V9.5 L17 4.75 L12 0 Z",
        ["home"] = "M12 3 L21 11 H18 V20 H14 V14 H10 V20 H6 V11 H3 Z",
        ["unlock"] = "M7 11 V7 A5 5 0 0 1 16.9 6 L14.9 6.5 A3 3 0 0 0 9 7 V11 H18 V21 H6 V11 Z",
        ["light"] = "M12 2 A7 7 0 0 0 8 14.7 V17 H16 V14.7 A7 7 0 0 0 12 2 Z M9 18 H15 V20 H9 Z M10 21 H14 V22.5 H10 Z",
        ["air"] = "M3 8 H14 A3 3 0 1 0 11 5 H9 A5 5 0 1 1 14 10 H3 Z M3 14 H17 A3.5 3.5 0 1 1 13.5 17.5 H15.5 A1.5 1.5 0 1 0 17 16 H3 Z",
        ["spindle"] = "F0 M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z M12 9 A3 3 0 1 1 12 15 A3 3 0 1 1 12 9 Z",
        ["plus"] = "M10 4 H14 V10 H20 V14 H14 V20 H10 V14 H4 V10 H10 Z",
        ["minus"] = "M4 10 H20 V14 H4 Z",
        ["folder"] = "M3 5 H10 L12 7 H21 V19 H3 Z",
        ["target"] = "F0 M11 2 H13 V5.1 A7 7 0 0 1 18.9 11 H22 V13 H18.9 A7 7 0 0 1 13 18.9 V22 H11 V18.9 A7 7 0 0 1 5.1 13 H2 V11 H5.1 A7 7 0 0 1 11 5.1 Z M12 7 A5 5 0 1 0 12 17 A5 5 0 1 0 12 7 Z",
        ["warning"] = "F0 M12 2 L23 21 H1 Z M11 9 V15 H13 V9 Z M11 17 V19 H13 V17 Z",
        ["plug"] = "M8 2 V7 H6 V12 A6 6 0 0 0 11 17.9 V22 H13 V17.9 A6 6 0 0 0 18 12 V7 H16 V2 H14 V7 H10 V2 Z",
        ["circle"] = "M12 4 A8 8 0 1 0 12 20 A8 8 0 1 0 12 4 Z",
        ["check"] = "M4 12 L9 17 L20 6 L18.5 4.5 L9 14 L5.5 10.5 Z",
        ["close"] = "M5 3.6 L12 10.6 L19 3.6 L20.4 5 L13.4 12 L20.4 19 L19 20.4 L12 13.4 L5 20.4 L3.6 19 L10.6 12 L3.6 5 Z",
        ["tool"] = "M14.7 3.3 A5 5 0 0 0 9.5 9.9 L3 16.4 L7.6 21 L14.1 14.5 A5 5 0 0 0 20.7 9.3 L17.6 12.4 L14.5 11.5 L13.6 8.4 Z",
        ["probe"] = "M10 2 H14 V12 L17 15 H13 V22 H11 V15 H7 L10 12 Z",
        ["skip-back"] = "M5 4 H8 V20 H5 Z M20 4 V20 L9 12 Z",
        ["skip-forward"] = "M16 4 H19 V20 H16 Z M4 4 V20 L15 12 Z",
        ["step-back"] = "M17 5 V19 L7 12 Z",
        ["step-forward"] = "M7 5 V19 L17 12 Z",
        ["save"] = "F0 M4 3 H17 L21 7 V21 H4 Z M7 5 V9 H15 V5 Z M8 13 H17 V19 H8 Z",
        ["zero"] = "F0 M12 3 A7 9 0 1 0 12 21 A7 9 0 1 0 12 3 Z M12 6 A4 6 0 1 1 12 18 A4 6 0 1 1 12 6 Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Geometry? Get(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var g)) return g;
            if (!PathData.TryGetValue(name, out var data)) return null;
            return Cache[name] = Geometry.Parse(data);
        }
    }
}
