using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>The problems found in the layout, worst first. Double-click selects the element concerned.</summary>
public sealed class DiagnosticsPanel : DockPanel
{
    private readonly EditorContext _ctx;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };

    public DiagnosticsPanel(EditorContext ctx)
    {
        _ctx = ctx;
        Children.Add(_list);
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: string path }) Jump(path);
        };
        ctx.DocumentChanged += _ => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Items.Clear();
        var doc = _ctx.Doc;
        if (doc.ParseError is { } error) _list.Items.Add(Item(DiagnosticSeverity.Error, "JSON", error, null));
        foreach (var d in doc.Diagnostics.OrderByDescending(d => d.Severity).ThenBy(d => d.Path, StringComparer.Ordinal))
            _list.Items.Add(Item(d.Severity, d.Path, d.Message, d.Path));
        if (_list.ItemCount == 0) _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "No problems found.", Foreground = Brushes.Gray }, IsEnabled = false });
    }

    private static ListBoxItem Item(DiagnosticSeverity severity, string path, string message, string? target)
    {
        var (glyph, color) = severity switch { DiagnosticSeverity.Error => ("✕", "#DC2626"), DiagnosticSeverity.Warning => ("⚠", "#D97706"), _ => ("ⓘ", "#2563EB") };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = glyph, Foreground = Brush.Parse(color), Width = 14 });
        row.Children.Add(new TextBlock { Text = path, Foreground = Brush.Parse("#64748B"), FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 12 });
        row.Children.Add(new TextBlock { Text = message });
        return new ListBoxItem { Content = row, Tag = target, Padding = new Thickness(8, 2) };
    }

    private void Jump(string path)
    {
        var selection = SelectionFor(path);
        _ctx.Select(selection);
        _ctx.RequestCodeAt?.Invoke(path);
    }

    /// <summary>The tree item a diagnostic path belongs to.</summary>
    public static string SelectionFor(string path)
    {
        if (JsonPath.ElementPrefix(path) is { } element) return element;
        var segments = JsonPath.Parse(path);
        if (segments.Count == 0) return "root";
        return segments[0] switch
        {
            "styles" when segments.Count > 1 => "styles." + segments[1],
            "shortcuts" when segments.Count > 1 && segments[1] is int i => $"shortcuts[{i}]",
            "shortcuts" => "root",
            "theme" => "theme",
            "window" => "window",
            "name" or "description" => "meta",
            _ => "root",
        };
    }
}
