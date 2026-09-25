using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Carvera.Core;

namespace Carvera.App.Layout;

/// <summary>Loads images referenced by layouts (SVG, PNG, JPG, BMP, GIF, or builtin:&lt;name&gt;) with caching.</summary>
public sealed class ImageLoader(string baseDirectory, ConsoleLog? console = null)
{
    private readonly Dictionary<string, IImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public string BaseDirectory { get; } = baseDirectory;

    public string ResolvePath(string source) =>
        System.IO.Path.IsPathRooted(source) ? source : System.IO.Path.GetFullPath(System.IO.Path.Combine(BaseDirectory, source));

    /// <summary>Creates a control that shows the image, tinted with <paramref name="tint"/> for built-in icons.</summary>
    public Control? CreateControl(string? source, double? width, double? height, IBrush? tint, Stretch stretch = Stretch.Uniform)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        Control? control;
        if (source.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
        {
            var geometry = Icons.Get(source["builtin:".Length..]);
            if (geometry is null)
            {
                Report(source, $"Unknown built-in image '{source}'.");
                return null;
            }
            control = new Avalonia.Controls.Shapes.Path { Data = geometry, Fill = tint ?? Brushes.Black, Stretch = Stretch.Uniform, Width = width ?? 18, Height = height ?? 18 };
        }
        else
        {
            var image = Load(source);
            if (image is null) return null;
            control = new Image { Source = image, Stretch = stretch };
            if (width is { } w) control.Width = w;
            if (height is { } h) control.Height = h;
        }
        control.IsHitTestVisible = false;
        return control;
    }

    public IImage? Load(string source)
    {
        var path = ResolvePath(source);
        if (_cache.TryGetValue(path, out var cached)) return cached;
        IImage? image = null;
        try
        {
            if (!File.Exists(path)) Report(path, $"Image not found: {path}");
            else if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                image = new SvgImage { Source = SvgSource.LoadFromStream(stream) };
            }
            else image = new Bitmap(path);
        }
        catch (Exception ex)
        {
            Report(path, $"Cannot load image {path}: {ex.Message}");
        }
        return _cache[path] = image;
    }

    private void Report(string key, string message)
    {
        if (_reported.Add(key)) console?.Warning(message);
    }

    public static Size? IntrinsicSize(IImage image) => image.Size is { Width: > 0, Height: > 0 } s ? s : null;
}
