using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Carvera.Editor.Views;

/// <summary>A titled section that folds away: a compact replacement for the theme's expander.</summary>
public sealed class Collapsible : StackPanel
{
    public Collapsible(Control header, Control body, bool expanded, Action<bool>? changed = null)
    {
        var arrow = new TextBlock { Text = expanded ? "▾" : "▸", Width = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#64748B") };
        var row = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(2, 6), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { arrow, header } },
        };
        body.IsVisible = expanded;
        body.Margin = new Thickness(16, 0, 0, 8);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            body.IsVisible = !body.IsVisible;
            arrow.Text = body.IsVisible ? "▾" : "▸";
            changed?.Invoke(body.IsVisible);
        };
        Children.Add(row);
        Children.Add(body);
    }

    public bool IsExpanded => Children.Count > 1 && Children[1].IsVisible;
}
