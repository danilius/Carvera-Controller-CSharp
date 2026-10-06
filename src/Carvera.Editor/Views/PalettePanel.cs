using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>The catalog of controls: drag one into the preview or the tree, or double-click to add it to the selection.</summary>
public sealed class PalettePanel : DockPanel
{
    private readonly EditorContext _ctx;
    private readonly TextBox _search = new() { PlaceholderText = "Search controls…", Margin = new Thickness(6, 6, 6, 4), MinHeight = 28 };
    private readonly StackPanel _list = new() { Spacing = 0 };
    private DragService? _drag;

    public PalettePanel(EditorContext ctx)
    {
        _ctx = ctx;
        SetDock(_search, Dock.Top);
        Children.Add(_search);
        Children.Add(new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        _search.TextChanged += (_, _) => Build();
        ctx.DocumentChanged += _ => BuildRegionsOnly();
        Build();
    }

    public void UseDragService(DragService drag)
    {
        _drag = drag;
        Build();
    }

    private StackPanel? _regionSection;

    private void Build()
    {
        _list.Children.Clear();
        var filter = _search.Text?.Trim() ?? "";
        var groups = NewElements.Groups.Select(g => (g.Group, g.Types)).ToList();
        if (NewElements.Ungrouped.ToArray() is { Length: > 0 } rest) groups.Add(("Other", rest));

        foreach (var (group, types) in groups)
        {
            var shown = types.Where(t => ComponentCatalog.TryGet(t, out var spec) && (filter.Length == 0 || t.Contains(filter, StringComparison.OrdinalIgnoreCase) || spec.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
            if (shown.Count == 0) continue;
            _list.Children.Add(Header(group));
            foreach (var type in shown) _list.Children.Add(Item(type));
        }

        _regionSection = new StackPanel();
        _list.Children.Add(_regionSection);
        BuildRegionsOnly();
    }

    private void BuildRegionsOnly()
    {
        if (_regionSection is null) return;
        _regionSection.Children.Clear();
        var filter = _search.Text?.Trim() ?? "";
        var names = _ctx.Doc.RegionNames.Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0) return;
        _regionSection.Children.Add(Header("Regions in this layout"));
        foreach (var name in names)
        {
            var row = Row("↪ " + name, "A use of the region; edit the region once to change every use.", () => new JsonObject { ["region"] = name }, "region " + name, () => _ctx.Actions.AddRegionUse(name));
            _regionSection.Children.Add(row);
        }
    }

    private static Control Header(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#64748B"), Margin = new Thickness(10, 10, 6, 3),
    };

    private Control Item(string type)
    {
        ComponentCatalog.TryGet(type, out var spec);
        var sentence = spec is null ? "" : spec.Description.Split(". ")[0].TrimEnd('.');
        return Row(type, sentence, () => NewElements.Create(type), type, () => _ctx.Actions.Add(type));
    }

    private Control Row(string title, string description, Func<JsonObject> create, string label, Action add)
    {
        var text = new StackPanel { Spacing = 0, Children = { new TextBlock { Text = title, FontWeight = FontWeight.Medium } } };
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, FontSize = 11, Foreground = Brush.Parse("#64748B"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 });
        var row = new Border
        {
            Child = text, Padding = new Thickness(10, 3), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(row, description.Length > 0 ? $"{title}: {description}. Drag into the preview or the tree, or double-click to add." : title);
        row.PointerEntered += (_, _) => row.Background = Brush.Parse("#EEF3FC");
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.DoubleTapped += (_, _) => add();
        _drag?.Attach(row, () => new DragPayload(label, null, create()));
        return row;
    }
}
