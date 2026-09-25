using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Carvera.App.Components;
using Carvera.Layout;

namespace Carvera.App.Layout;

/// <summary>Turns a <see cref="LayoutDocument"/> into Avalonia controls.</summary>
public sealed class LayoutBuilder(BuildContext ctx)
{
    public BuildContext Context => ctx;

    /// <summary>All hosts created, in document order (used for tests and keyboard shortcuts).</summary>
    public List<ComponentHost> Hosts { get; } = [];

    public Control Build() => Build(ctx.Document.Root);

    public Control Build(LayoutNode node)
    {
        var host = new ComponentHost(node, ctx);
        Hosts.Add(host);
        try
        {
            host.Child = node.Type.ToLowerInvariant() switch
            {
                "stack" => Stack(node),
                "panel" => PanelContainer(node, host),
                "grid" => GridContainer(node),
                "split" => Split(node),
                "tabs" => Tabs(node),
                "scroll" => Scroll(node),
                "canvas" => Canvas(node),
                _ => ComponentFactory.Create(node, host, ctx),
            };
        }
        catch (Exception ex)
        {
            ctx.Console.Error($"{node.Path}: could not create '{node.Type}': {ex.Message}");
            host.Child = new TextBlock { Text = $"⚠ {node.Type}: {ex.Message}", Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        }
        return host;
    }

    /// <summary>Wraps a child so its width/height/align apply inside a cell of a non-flex container.</summary>
    private Control Slot(LayoutNode child) => new FlexPanel { Children = { Build(child) } };

    private static Orientation OrientationOf(LayoutNode node, Orientation fallback) =>
        node.GetString("orientation")?.ToLowerInvariant() switch
        {
            "horizontal" => Orientation.Horizontal,
            "vertical" => Orientation.Vertical,
            _ => fallback,
        };

    private static Justify JustifyOf(LayoutNode node) => node.GetString("justify")?.ToLowerInvariant() switch
    {
        "center" => Justify.Center,
        "end" => Justify.End,
        "space-between" => Justify.SpaceBetween,
        "space-around" => Justify.SpaceAround,
        "space-evenly" => Justify.SpaceEvenly,
        _ => Justify.Start,
    };

    private FlexPanel Stack(LayoutNode node, Orientation fallback = Orientation.Vertical)
    {
        var panel = new FlexPanel
        {
            Orientation = OrientationOf(node, fallback),
            Spacing = node.GetNumber("spacing") ?? 0,
            Justify = JustifyOf(node),
        };
        foreach (var child in node.Children) panel.Children.Add(Build(child));
        return panel;
    }

    private Control PanelContainer(LayoutNode node, ComponentHost host)
    {
        var body = Stack(node);
        if (node.GetNumber("spacing") is null) body.Spacing = 8;
        var title = ctx.TemplateProp(node, "title");
        if (title is null) return body;

        var header = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = ctx.Theme.TokenBrush("textMuted"), Margin = new Thickness(0, 0, 0, 8) };
        void Update() => header.Text = title.Render(ctx.Resolve);
        ctx.Watch(title.Paths, Update);
        Update();

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(body);
        if (node.GetBool("collapsible") == true)
        {
            header.Cursor = new Cursor(StandardCursorType.Hand);
            void SetCollapsed(bool collapsed)
            {
                body.IsVisible = !collapsed;
                header.Text = (collapsed ? "▸ " : "▾ ") + title.Render(ctx.Resolve);
                host.SetState("collapsed", collapsed);
            }
            header.PointerPressed += (_, e) => { SetCollapsed(body.IsVisible); e.Handled = true; };
            SetCollapsed(node.GetBool("collapsed") == true);
        }
        return dock;
    }

    private static GridLength ToGridLength(SizeSpec size) => size.Kind switch
    {
        SizeKind.Pixels => new GridLength(size.Value, GridUnitType.Pixel),
        SizeKind.Auto => GridLength.Auto,
        SizeKind.Star => new GridLength(size.Value, GridUnitType.Star),
        // Percentages in grid definitions act as proportional shares ("30%" and "70%" split 3:7).
        SizeKind.Percent => new GridLength(size.Value, GridUnitType.Star),
        _ => new GridLength(1, GridUnitType.Star),
    };

    private static IEnumerable<SizeSpec> Sizes(LayoutNode node, string name) =>
        node.Get(name) is System.Text.Json.Nodes.JsonArray array
            ? array.Select(n => SizeSpec.TryParse(n, out var s, out _) ? s : SizeSpec.Fill)
            : [];

    private Control GridContainer(LayoutNode node)
    {
        var grid = new Grid
        {
            RowSpacing = node.GetNumber("rowSpacing") ?? node.GetNumber("spacing") ?? 0,
            ColumnSpacing = node.GetNumber("columnSpacing") ?? node.GetNumber("spacing") ?? 0,
        };
        foreach (var size in Sizes(node, "columns")) grid.ColumnDefinitions.Add(new ColumnDefinition(ToGridLength(size)));
        foreach (var size in Sizes(node, "rows")) grid.RowDefinitions.Add(new RowDefinition(ToGridLength(size)));
        foreach (var child in node.Children)
        {
            var slot = Slot(child);
            Grid.SetRow(slot, (int)(child.GetNumber("row") ?? 0));
            Grid.SetColumn(slot, (int)(child.GetNumber("column") ?? 0));
            Grid.SetRowSpan(slot, Math.Max(1, (int)(child.GetNumber("rowSpan") ?? 1)));
            Grid.SetColumnSpan(slot, Math.Max(1, (int)(child.GetNumber("columnSpan") ?? 1)));
            grid.Children.Add(slot);
        }
        return grid;
    }

    private Control Split(LayoutNode node)
    {
        var horizontal = OrientationOf(node, Orientation.Horizontal) == Orientation.Horizontal;
        var sizes = Sizes(node, "sizes").ToList();
        var splitter = node.GetNumber("splitterSize") ?? 6;
        var grid = new Grid();
        for (var i = 0; i < node.Children.Count; i++)
        {
            if (i > 0)
            {
                var gs = new GridSplitter
                {
                    Background = Brushes.Transparent,
                    ResizeDirection = horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                };
                AddTrack(grid, horizontal, new GridLength(splitter), gs);
            }
            var length = i < sizes.Count ? ToGridLength(sizes[i]) : new GridLength(1, GridUnitType.Star);
            AddTrack(grid, horizontal, length, Slot(node.Children[i]));
        }
        return grid;
    }

    private static void AddTrack(Grid grid, bool horizontal, GridLength length, Control content)
    {
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(length));
            Grid.SetColumn(content, grid.ColumnDefinitions.Count - 1);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(length));
            Grid.SetRow(content, grid.RowDefinitions.Count - 1);
        }
        grid.Children.Add(content);
    }

    private Control Tabs(LayoutNode node)
    {
        var tabs = new TabControl
        {
            TabStripPlacement = node.GetString("placement")?.ToLowerInvariant() switch
            {
                "bottom" => Dock.Bottom,
                "left" => Dock.Left,
                "right" => Dock.Right,
                _ => Dock.Top,
            },
            Padding = new Thickness(0, 6, 0, 0),
        };
        foreach (var child in node.Children)
        {
            var title = ctx.TemplateProp(child, "title");
            var item = new TabItem { Content = Slot(child), FontSize = ctx.Theme.FontSize + 1 };
            void Update() => item.Header = title?.Render(ctx.Resolve) ?? child.Type;
            if (title is not null) ctx.Watch(title.Paths, Update);
            Update();
            tabs.Items.Add(item);
        }
        if (node.GetNumber("selected") is { } selected && selected >= 0 && selected < node.Children.Count) tabs.SelectedIndex = (int)selected;
        return tabs;
    }

    private Control Scroll(LayoutNode node)
    {
        var mode = node.GetString("orientation")?.ToLowerInvariant() ?? "vertical";
        var viewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = mode is "horizontal" or "both" ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = mode is "vertical" or "both" ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        if (node.Children.Count > 0) viewer.Content = Slot(node.Children[0]);
        return viewer;
    }

    private Control Canvas(LayoutNode node)
    {
        var panel = new AbsolutePanel();
        foreach (var child in node.Children) panel.Children.Add(Build(child));
        return panel;
    }
}
