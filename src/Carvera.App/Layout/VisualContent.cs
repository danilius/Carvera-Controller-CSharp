using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Carvera.App.Layout;

/// <summary>
/// Shows the image and text of a <see cref="ComponentHost"/>'s current visual. Each state can change the
/// image, its size and placement, and the text, which is how components get per-state custom graphics.
/// </summary>
public sealed class VisualContent : Panel
{
    private readonly ComponentHost _host;
    private readonly ImageLoader _images;
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Func<Control?>? _fallbackImage;
    private string? _imageKey;
    private string? _layoutKey;
    private Control? _image;

    public VisualContent(ComponentHost host, ImageLoader images, Func<Control?>? fallbackImage = null, bool wrap = false)
    {
        _host = host;
        _images = images;
        _fallbackImage = fallbackImage;
        _text.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _text.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        host.VisualChanged += _ => Update();
        Update();
    }

    public TextBlock TextBlock => _text;

    private void Update()
    {
        var v = _host.Effective;
        var text = _host.EffectiveText;
        _text.Text = text;
        _text.IsVisible = !string.IsNullOrEmpty(text);
        var align = v.TextAlign?.ToLowerInvariant() switch { "left" => TextAlignment.Left, "right" => TextAlignment.Right, "center" => TextAlignment.Center, _ => (TextAlignment?)null };
        _text.TextAlignment = align ?? TextAlignment.Left;

        var tint = _host.GetValue(TextElement.ForegroundProperty);
        var placement = (v.ImagePlacement ?? (string.IsNullOrEmpty(text) ? "only" : "left")).ToLowerInvariant();
        var fallbackKey = v.Image is null && _fallbackImage is not null ? string.Join(",", _host.ActiveStates()) : "";
        var key = $"{v.Image}|{v.ImageWidth}|{v.ImageHeight}|{placement}|{(tint as ISolidColorBrush)?.Color}|{fallbackKey}";
        if (key != _imageKey)
        {
            _imageKey = key;
            _image = v.Image is not null
                ? _images.CreateControl(v.Image, v.ImageWidth, v.ImageHeight, tint, placement == "fill" ? Stretch.Uniform : Stretch.Uniform)
                : _fallbackImage?.Invoke();
        }
        if (placement == "only" && _image is not null) _text.IsVisible = false;
        var layoutKey = $"{key}|{_text.IsVisible}|{align}";
        if (layoutKey == _layoutKey) return;
        _layoutKey = layoutKey;
        Rebuild(placement, align);
    }

    private void Rebuild(string placement, TextAlignment? align)
    {
        Children.Clear();
        var horizontal = align switch { TextAlignment.Left => HorizontalAlignment.Left, TextAlignment.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        if (_image is null)
        {
            _text.HorizontalAlignment = align is null ? HorizontalAlignment.Left : horizontal;
            Children.Add(_text);
            return;
        }
        Detach(_image);
        Detach(_text);
        if (placement == "fill")
        {
            _image.HorizontalAlignment = HorizontalAlignment.Stretch;
            _image.VerticalAlignment = VerticalAlignment.Stretch;
            _text.HorizontalAlignment = horizontal;
            Children.Add(_image);
            Children.Add(_text);
            return;
        }
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        if (placement == "only" || !_text.IsVisible)
        {
            Children.Add(_image);
            return;
        }
        var stack = new StackPanel
        {
            Spacing = 6,
            Orientation = placement is "top" or "bottom" ? Orientation.Vertical : Orientation.Horizontal,
            HorizontalAlignment = align is null ? HorizontalAlignment.Center : horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        if (placement is "right" or "bottom") { stack.Children.Add(_text); stack.Children.Add(_image); }
        else { stack.Children.Add(_image); stack.Children.Add(_text); }
        Children.Add(stack);
    }

    private static void Detach(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
    }
}
