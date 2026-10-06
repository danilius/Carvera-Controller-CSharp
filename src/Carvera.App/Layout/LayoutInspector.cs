using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Carvera.App.Shell;
using Carvera.Layout;

namespace Carvera.App.Layout;

/// <summary>
/// Finds layout elements on screen and brings them into view. The Controller uses it for the layout editor's
/// "show me this element" and "pick from the Controller" requests; the editor's preview uses it for selection.
/// </summary>
public static class LayoutInspector
{
    /// <summary>The hosts that are currently on screen, with their bounds in the coordinates of <paramref name="relativeTo"/>.</summary>
    public static IEnumerable<(ComponentHost Host, Rect Bounds)> Visible(LayoutSession session, Visual relativeTo)
    {
        foreach (var host in session.Hosts)
        {
            if (TopLevel.GetTopLevel(host) is null || !host.IsEffectivelyVisible) continue;
            if (host.Bounds.Width <= 0 || host.Bounds.Height <= 0) continue;
            var origin = host.TranslatePoint(new Point(0, 0), relativeTo);
            if (origin is null) continue;
            yield return (host, new Rect(origin.Value, host.Bounds.Size));
        }
    }

    /// <summary>The innermost element under a point: the smallest one containing it, the later-built one on a tie.</summary>
    public static ComponentHost? HitTest(LayoutSession session, Visual relativeTo, Point point)
    {
        ComponentHost? best = null;
        var bestArea = double.MaxValue;
        foreach (var (host, bounds) in Visible(session, relativeTo))
        {
            if (!bounds.Contains(point)) continue;
            var area = bounds.Width * bounds.Height;
            if (area <= bestArea) { best = host; bestArea = area; }
        }
        return best;
    }

    /// <summary>The element built for the node at a layout path such as root.children[2].</summary>
    public static ComponentHost? FindByPath(LayoutSession session, string path) =>
        session.Hosts.FirstOrDefault(h => h.Node.Path == path);

    /// <summary>
    /// Makes the element visible: switches the tabs on the way to it, expands collapsed panels and scrolls it into view.
    /// Returns the element, or null when the path names nothing in this layout.
    /// </summary>
    public static ComponentHost? Reveal(LayoutSession session, string path)
    {
        LayoutNode? target = null;
        IReadOnlyList<LayoutNode> ancestors = [];
        foreach (var (node, chain) in session.Document.Root.Walk())
            if (node.Path == path) { target = node; ancestors = chain; break; }
        if (target is null) return null;

        var trail = ancestors.Append(target).ToList();
        for (var i = 0; i < trail.Count - 1; i++)
        {
            var container = trail[i];
            var host = session.Hosts.FirstOrDefault(h => ReferenceEquals(h.Node, container));
            if (host is null) continue;
            var index = container.Children.ToList().FindIndex(c => ReferenceEquals(c, trail[i + 1]));
            if (container.Type.Equals("tabs", StringComparison.OrdinalIgnoreCase) && index >= 0
                && host.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is { } tabs)
            {
                if (tabs.SelectedIndex != index) tabs.SelectedIndex = index;
                tabs.UpdateLayout(); // realises the page so tabs nested inside it can be found next
            }
            else if (host.CollapseToggle is { } toggle && host.HasState("collapsed"))
            {
                toggle(false);
                host.UpdateLayout();
            }
        }

        var found = session.Hosts.FirstOrDefault(h => ReferenceEquals(h.Node, target));
        found?.BringIntoView();
        return found;
    }
}

/// <summary>
/// What the user has done to a layout's controls that the layout file does not say: the tab selected, how far a
/// scroller is scrolled, how a splitter was dragged, whether a panel is collapsed. Kept across a live reload so an
/// edit in the layout editor does not throw the Controller back to the first tab.
/// </summary>
public sealed class ViewState
{
    private readonly Dictionary<string, (string Signature, object Value)> _items = [];

    public static ViewState Capture(LayoutSession session)
    {
        var state = new ViewState();
        foreach (var (key, host) in Keyed(session))
        {
            var signature = Signature(host.Node);
            switch (host.Node.Type.ToLowerInvariant())
            {
                case "tabs" when Own<TabControl>(host) is { } tabs:
                    state._items[key] = (signature, tabs.SelectedIndex);
                    break;
                case "scroll" when Own<ScrollViewer>(host) is { } viewer:
                    state._items[key] = (signature, viewer.Offset);
                    break;
                case "split" when Own<Grid>(host) is { } grid:
                    state._items[key] = (signature, (grid.ColumnDefinitions.Select(c => c.Width).ToArray(), grid.RowDefinitions.Select(r => r.Height).ToArray()));
                    break;
                case "panel" when host.CollapseToggle is not null:
                    state._items[key] = (signature, host.HasState("collapsed"));
                    break;
            }
        }
        return state;
    }

    public void Restore(LayoutSession session)
    {
        foreach (var (key, host) in Keyed(session))
        {
            if (!_items.TryGetValue(key, out var item) || item.Signature != Signature(host.Node)) continue;
            switch (item.Value)
            {
                case int index when Own<TabControl>(host) is { } tabs && index >= 0 && index < tabs.ItemCount:
                    tabs.SelectedIndex = index;
                    break;
                case Vector offset when Own<ScrollViewer>(host) is { } viewer:
                    viewer.Offset = offset;
                    break;
                case ValueTuple<GridLength[], GridLength[]> lengths when Own<Grid>(host) is { } grid
                    && lengths.Item1.Length == grid.ColumnDefinitions.Count && lengths.Item2.Length == grid.RowDefinitions.Count:
                    for (var i = 0; i < lengths.Item1.Length; i++) grid.ColumnDefinitions[i].Width = lengths.Item1[i];
                    for (var i = 0; i < lengths.Item2.Length; i++) grid.RowDefinitions[i].Height = lengths.Item2[i];
                    break;
                case bool collapsed when host.CollapseToggle is { } toggle:
                    toggle(collapsed);
                    break;
            }
        }
    }

    /// <summary>Only the properties that decide the initial state count: editing the tab container's 'selected' discards the remembered tab.</summary>
    private static string Signature(LayoutNode node) => node.Type.ToLowerInvariant() switch
    {
        "tabs" => node.Get("selected")?.ToJsonString() + "|" + node.Children.Count,
        "split" => node.Get("sizes")?.ToJsonString() + "|" + node.Get("orientation") + "|" + node.Children.Count,
        "panel" => node.Get("collapsed")?.ToJsonString() + "|" + node.Get("collapsible")?.ToJsonString(),
        _ => "",
    };

    /// <summary>Identifies elements across two builds of the layout: by id when there is one, else by path (numbered when a region is used more than once).</summary>
    private static IEnumerable<(string Key, ComponentHost Host)> Keyed(LayoutSession session)
    {
        var seen = new Dictionary<string, int>();
        foreach (var host in session.Hosts)
        {
            var baseKey = host.Node.Id is { Length: > 0 } id ? "#" + id : host.Node.Path;
            seen[baseKey] = seen.GetValueOrDefault(baseKey) + 1;
            yield return ($"{baseKey}@{seen[baseKey]}", host);
        }
    }

    /// <summary>The first control of a type that belongs to this element rather than to an element nested in it.</summary>
    private static T? Own<T>(ComponentHost host) where T : Control =>
        host.GetVisualDescendants().OfType<T>().FirstOrDefault(c => ReferenceEquals(c.FindAncestorOfType<ComponentHost>(), host));
}
