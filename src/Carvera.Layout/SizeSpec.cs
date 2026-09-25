using System.Globalization;
using System.Text.Json.Nodes;

namespace Carvera.Layout;

public enum SizeKind
{
    /// <summary>No size given: fill the available space (shared equally with other filling siblings).</summary>
    Fill,
    /// <summary>A fixed size in device-independent pixels.</summary>
    Pixels,
    /// <summary>A percentage of the parent's content box.</summary>
    Percent,
    /// <summary>A weighted share of the remaining space, e.g. "2*".</summary>
    Star,
    /// <summary>Sized to the content.</summary>
    Auto,
}

/// <summary>
/// A width or height as written in a layout file. Follows web conventions: a missing size fills the
/// available space, a number is pixels, "40%" is relative to the parent and "auto" fits the content.
/// "2*" takes a double share of the space left after fixed, percentage and auto-sized siblings.
/// </summary>
public readonly record struct SizeSpec(SizeKind Kind, double Value)
{
    public static readonly SizeSpec Fill = new(SizeKind.Fill, 1);
    public static readonly SizeSpec Auto = new(SizeKind.Auto, 0);

    public static SizeSpec Pixels(double value) => new(SizeKind.Pixels, value);
    public static SizeSpec Percent(double value) => new(SizeKind.Percent, value);
    public static SizeSpec Star(double weight) => new(SizeKind.Star, weight);

    public bool IsFlexible => Kind is SizeKind.Fill or SizeKind.Star;
    public double Weight => Kind == SizeKind.Fill ? 1 : Kind == SizeKind.Star ? Value : 0;

    public static bool TryParse(JsonNode? node, out SizeSpec size, out string? error)
    {
        error = null;
        size = Fill;
        if (node is null) return true;
        if (node is JsonValue v && v.TryGetValue<double>(out var number))
        {
            if (number < 0) { error = "Sizes cannot be negative"; return false; }
            size = Pixels(number);
            return true;
        }
        if (node is JsonValue s && s.TryGetValue<string>(out var text)) return TryParse(text, out size, out error);
        error = "Expected a number or a string such as \"120\", \"30%\", \"2*\", \"auto\" or \"fill\"";
        return false;
    }

    public static bool TryParse(string text, out SizeSpec size, out string? error)
    {
        error = null;
        size = Fill;
        var t = text.Trim().ToLowerInvariant();
        switch (t)
        {
            case "" or "fill" or "*": return true;
            case "auto": size = Auto; return true;
        }
        if (t.EndsWith('%') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct >= 0)
        {
            size = Percent(pct);
            return true;
        }
        if (t.EndsWith('*') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) && weight > 0)
        {
            size = Star(weight);
            return true;
        }
        if (t.EndsWith("px")) t = t[..^2];
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px >= 0)
        {
            size = Pixels(px);
            return true;
        }
        error = $"'{text}' is not a valid size (use a number, \"30%\", \"2*\", \"auto\" or \"fill\")";
        return false;
    }

    /// <summary>Resolves fixed kinds against a parent length; returns null for flexible and auto sizes.</summary>
    public double? Resolve(double parentLength) => Kind switch
    {
        SizeKind.Pixels => Value,
        SizeKind.Percent => double.IsFinite(parentLength) ? parentLength * Value / 100.0 : null,
        _ => null,
    };

    public override string ToString() => Kind switch
    {
        SizeKind.Fill => "fill",
        SizeKind.Auto => "auto",
        SizeKind.Pixels => Value.ToString(CultureInfo.InvariantCulture),
        SizeKind.Percent => $"{Value.ToString(CultureInfo.InvariantCulture)}%",
        _ => $"{Value.ToString(CultureInfo.InvariantCulture)}*",
    };
}

/// <summary>Edge sizes written CSS-style: 8, "8 4" (vertical horizontal), "8 4 2" or "8 4 2 6" (top right bottom left).</summary>
public readonly record struct Edges(double Left, double Top, double Right, double Bottom)
{
    public static readonly Edges Zero = new(0, 0, 0, 0);

    public static bool TryParse(JsonNode? node, out Edges edges, out string? error)
    {
        edges = Zero;
        error = null;
        if (node is null) return true;
        double[] values;
        if (node is JsonValue v && v.TryGetValue<double>(out var single)) values = [single];
        else if (node is JsonValue s && s.TryGetValue<string>(out var text))
        {
            var parts = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            values = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i].Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    error = $"'{text}' is not a valid edge list";
                    return false;
                }
        }
        else if (node is JsonArray array)
        {
            values = new double[array.Count];
            for (var i = 0; i < array.Count; i++)
                if (array[i] is not JsonValue item || !item.TryGetValue(out values[i]))
                {
                    error = "Edge arrays must contain numbers";
                    return false;
                }
        }
        else
        {
            error = "Expected a number, a string such as \"8 4\" or an array of numbers";
            return false;
        }
        switch (values.Length)
        {
            case 1: edges = new(values[0], values[0], values[0], values[0]); return true;
            case 2: edges = new(values[1], values[0], values[1], values[0]); return true;
            case 3: edges = new(values[1], values[0], values[1], values[2]); return true;
            case 4: edges = new(values[3], values[0], values[1], values[2]); return true;
            default: error = "Give 1 to 4 values"; return false;
        }
    }
}
