using System.Text.Json.Nodes;
using Carvera.Layout;

namespace Carvera.App.Layout;

/// <summary>
/// A set of optional visual properties. Blocks are layered (theme defaults, classes, style, per-state
/// visuals, conditions); later non-null values win.
/// </summary>
public sealed record VisualBlock
{
    public static readonly VisualBlock Empty = new();

    public string? Background { get; init; }
    public string? Foreground { get; init; }
    public string? BorderColor { get; init; }
    public Edges? BorderWidth { get; init; }
    public double? CornerRadius { get; init; }
    public double? FontSize { get; init; }
    public string? FontWeight { get; init; }
    public string? FontFamily { get; init; }
    public string? FontStyle { get; init; }
    public string? TextAlign { get; init; }
    public double? Opacity { get; init; }
    public string? Image { get; init; }
    public double? ImageWidth { get; init; }
    public double? ImageHeight { get; init; }
    public string? ImagePlacement { get; init; }
    public string? Text { get; init; }
    public Edges? Padding { get; init; }

    public VisualBlock Merge(VisualBlock? over) => over is null ? this : new VisualBlock
    {
        Background = over.Background ?? Background,
        Foreground = over.Foreground ?? Foreground,
        BorderColor = over.BorderColor ?? BorderColor,
        BorderWidth = over.BorderWidth ?? BorderWidth,
        CornerRadius = over.CornerRadius ?? CornerRadius,
        FontSize = over.FontSize ?? FontSize,
        FontWeight = over.FontWeight ?? FontWeight,
        FontFamily = over.FontFamily ?? FontFamily,
        FontStyle = over.FontStyle ?? FontStyle,
        TextAlign = over.TextAlign ?? TextAlign,
        Opacity = over.Opacity ?? Opacity,
        Image = over.Image ?? Image,
        ImageWidth = over.ImageWidth ?? ImageWidth,
        ImageHeight = over.ImageHeight ?? ImageHeight,
        ImagePlacement = over.ImagePlacement ?? ImagePlacement,
        Text = over.Text ?? Text,
        Padding = over.Padding ?? Padding,
    };

    public static VisualBlock Parse(JsonObject? json)
    {
        if (json is null) return Empty;
        string? S(string name) => json[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        double? N(string name) => json[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        Edges? E(string name) => json[name] is { } node && Edges.TryParse(node, out var e, out _) ? e : null;
        return new VisualBlock
        {
            Background = S("background"),
            Foreground = S("foreground"),
            BorderColor = S("borderColor"),
            BorderWidth = E("borderWidth"),
            CornerRadius = N("cornerRadius"),
            FontSize = N("fontSize"),
            FontWeight = S("fontWeight"),
            FontFamily = S("fontFamily"),
            FontStyle = S("fontStyle"),
            TextAlign = S("textAlign"),
            Opacity = N("opacity"),
            Image = S("image"),
            ImageWidth = N("imageWidth"),
            ImageHeight = N("imageHeight"),
            ImagePlacement = S("imagePlacement"),
            Text = S("text"),
            Padding = E("padding"),
        };
    }

    /// <summary>Parses a compact "key: value; key: value" description used for built-in defaults.</summary>
    public static VisualBlock Css(string css)
    {
        var json = new JsonObject();
        foreach (var part in css.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            var key = part[..colon].Trim();
            var value = part[(colon + 1)..].Trim();
            json[key] = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && key != "padding" && key != "borderWidth"
                ? JsonValue.Create(d)
                : JsonValue.Create(value);
        }
        return Parse(json);
    }
}
