using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>The structure of the layout as a tree: the element tree, the regions, styles, theme, shortcuts and window settings.</summary>
public sealed class TreePanel : DockPanel, IDropTarget
{
    private static readonly IBrush ErrorBrush = Brush.Parse("#DC2626");
    private static readonly IBrush WarningBrush = Brush.Parse("#D97706");
    private static readonly IBrush MutedBrush = Brush.Parse("#64748B");
    private static readonly IBrush DropBrush = Brush.Parse("#16A34A");

    private readonly EditorContext _ctx;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter…", MinHeight = 28 };
    private readonly Dictionary<string, TreeViewItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal) { "group:regions", "group:styles", "group:shortcuts" };
    private readonly HashSet<string> _expandedOnce = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _refreshTimer;
    private DragService? _drag;
    private string? _signature;
    private bool _syncing;
    private Border? _dropRow;
    private ActionsDrop? _pendingDrop;

    private sealed record ActionsDrop(EditorActions.DropLocation Where);

    public TreePanel(EditorContext ctx)
    {
        _ctx = ctx;
        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        SetDock(_filter, Dock.Top);
        _filter.Margin = new Thickness(6, 0, 6, 4);
        Children.Add(_filter);
        Children.Add(_tree);

        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => { _refreshTimer!.Stop(); Rebuild(); });
        _filter.TextChanged += (_, _) => { _signature = null; _refreshTimer.Start(); };
        _tree.SelectionChanged += (_, _) =>
        {
            if (_syncing || _tree.SelectedItem is not TreeViewItem { Tag: string tag } || tag.StartsWith("group:", StringComparison.Ordinal)) return;
            _ctx.Select(tag, this);
        };
        ctx.DocumentChanged += _ => _refreshTimer.Start();
        ctx.SelectionChanged += (path, source) => { if (!ReferenceEquals(source, this)) SyncSelection(); };
        Rebuild();
    }

    public void UseDragService(DragService drag)
    {
        _drag = drag;
        drag.Register(this);
        foreach (var (path, row) in _rows) AttachDrag(path, row);
    }

    // ------------------------------------------------------------------ toolbar

    private Control BuildToolbar()
    {
        var a = _ctx.Actions;
        Button Tool(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, Padding = new Thickness(8, 3), MinWidth = 30, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        var add = new Button { Content = "＋ Add ▾", Padding = new Thickness(8, 3) };
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var (group, types) in NewElements.Groups)
            {
                var sub = new MenuItem { Header = group };
                foreach (var type in types)
                {
                    var item = new MenuItem { Header = type };
                    ToolTip.SetTip(item, ComponentCatalog.TryGet(type, out var spec) ? spec.Description : type);
                    item.Click += (_, _) => a.Add(type);
                    sub.Items.Add(item);
                }
                menu.Items.Add(sub);
            }
            if (_ctx.Doc.RegionNames.Any())
            {
                var regions = new MenuItem { Header = "Region" };
                foreach (var name in _ctx.Doc.RegionNames)
                {
                    var item = new MenuItem { Header = name };
                    item.Click += (_, _) => a.AddRegionUse(name);
                    regions.Items.Add(item);
                }
                menu.Items.Add(regions);
            }
            menu.Open(add);
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(6, 6, 6, 4),
            Children =
            {
                add,
                Tool("⧉", "Duplicate (Ctrl+D)", () => a.Duplicate()),
                Tool("✕", "Delete (Del)", () => a.Delete()),
                Tool("↑", "Move up (Alt+↑)", () => a.MoveBy(-1)),
                Tool("↓", "Move down (Alt+↓)", () => a.MoveBy(1)),
            },
        };
        return row;
    }

    // ------------------------------------------------------------------ building

    private void Rebuild()
    {
        var data = _ctx.Doc.Data;
        var filter = _filter.Text?.Trim() ?? "";
        var signature = data is null ? "" : Signature(data) + "|" + filter + "|" + string.Join(";", _ctx.Doc.Diagnostics.Select(d => d.Path + d.Severity));
        if (signature == _signature) { SyncSelection(); return; }
        _signature = signature;
        RememberExpansion();
        _items.Clear();
        _rows.Clear();
        _syncing = true;
        _tree.Items.Clear();
        if (data is not null)
        {
            if (data["root"] is JsonObject root && AddElement(null, "root", root, filter) is { } rootItem) _tree.Items.Add(rootItem);

            var regions = new List<TreeViewItem>();
            if (data["regions"] is JsonObject regionObject)
                foreach (var (name, value) in regionObject)
                    if (value is JsonObject region && AddElement(null, "regions." + name, region, filter) is { } item) regions.Add(item);
            AddGroup("group:regions", "Regions", regions, "Reusable pieces of the layout, referenced by name.");

            var styles = new List<TreeViewItem>();
            if (data["styles"] is JsonObject styleObject)
                foreach (var (name, _) in styleObject)
                    if (Matches(name, filter)) styles.Add(Leaf("styles." + name, "style", name, null));
            AddGroup("group:styles", "Styles", styles, "Named looks that elements use through 'class'.");

            if (filter.Length == 0 || "theme colours colors".Contains(filter, StringComparison.OrdinalIgnoreCase))
                _tree.Items.Add(Leaf("theme", "theme", "Theme", "colours and fonts"));

            var shortcuts = new List<TreeViewItem>();
            if (data["shortcuts"] is JsonArray shortcutArray)
                for (var i = 0; i < shortcutArray.Count; i++)
                    if (shortcutArray[i] is JsonObject s && Matches(s["key"]?.ToString() + " " + s["command"], filter))
                        shortcuts.Add(Leaf($"shortcuts[{i}]", "key", s["key"]?.ToString() ?? "?", s["command"]?.ToString()));
            AddGroup("group:shortcuts", "Shortcuts", shortcuts, "Keys that run commands.");

            if (filter.Length == 0 || "window".Contains(filter, StringComparison.OrdinalIgnoreCase))
                _tree.Items.Add(Leaf("window", "window", "Window", "size and title"));
            if (filter.Length == 0 || "layout name description".Contains(filter, StringComparison.OrdinalIgnoreCase))
                _tree.Items.Add(Leaf("meta", "meta", "Layout details", "name and description"));
        }
        _syncing = false;
        SyncSelection();
    }

    /// <summary>Everything the tree displays, and nothing else: editing a width or a colour leaves the tree alone.</summary>
    private static string Signature(JsonObject data)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (path, element) in ElementOps.AllElements(data))
        {
            sb.Append(path).Append('|').Append(ElementOps.TypeOf(element)).Append('|').Append(element["region"]).Append('|').Append(Summary(element, path)).Append(';');
        }
        sb.Append('#');
        if (data["styles"] is JsonObject styles) foreach (var (name, _) in styles) sb.Append(name).Append(',');
        sb.Append('#');
        if (data["shortcuts"] is JsonArray shortcuts) foreach (var s in shortcuts) sb.Append(s?["key"]).Append(' ').Append(s?["command"]).Append(',');
        sb.Append('#').Append(data["theme"] is not null).Append(data["window"] is not null);
        return sb.ToString();
    }

    private void AddGroup(string tag, string title, List<TreeViewItem> children, string tip)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"({children.Count})", Foreground = MutedBrush } },
        };
        ToolTip.SetTip(header, tip);
        var group = new TreeViewItem { Header = header, Tag = tag, IsExpanded = !_collapsed.Contains(tag) };
        foreach (var child in children) group.Items.Add(child);
        var menu = new ContextMenu();
        if (tag == "group:regions")
        {
            var item = new MenuItem { Header = "New region…" };
            item.Click += (_, _) => _ = _ctx.Actions.AddRegionAsync();
            menu.Items.Add(item);
        }
        else if (tag == "group:styles")
        {
            var item = new MenuItem { Header = "New style…" };
            item.Click += async (_, _) => await AddStyleAsync();
            menu.Items.Add(item);
        }
        else if (tag == "group:shortcuts")
        {
            var item = new MenuItem { Header = "New shortcut" };
            item.Click += (_, _) => _ctx.Edit("Add shortcut", d => PartOps.AddShortcut(d));
            menu.Items.Add(item);
        }
        group.ContextMenu = menu;
        _tree.Items.Add(group);
        _items[tag] = group;
    }

    private async Task AddStyleAsync()
    {
        if (_ctx.Actions.Owner is not { } owner) return;
        var name = await Dialogs.PromptAsync(owner, "New style", "Name of the style (elements use it as \"class\"):", "style", "Create");
        if (!string.IsNullOrWhiteSpace(name)) _ctx.Edit("Add style", d => PartOps.AddStyle(d, name.Trim()));
    }

    private static bool Matches(string? text, string filter) => filter.Length == 0 || (text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

    private TreeViewItem Leaf(string path, string kind, string title, string? detail)
    {
        var item = new TreeViewItem { Tag = path, Header = Row(path, Glyph(kind), title, detail, null) };
        _items[path] = item;
        AttachMenu(item, path);
        return item;
    }

    private TreeViewItem? AddElement(TreeViewItem? parent, string path, JsonObject element, string filter)
    {
        var isReference = ElementOps.IsRegionReference(element);
        var type = ElementOps.TypeOf(element);
        var kids = new List<TreeViewItem>();
        if (!isReference)
            foreach (var (childPath, child) in ElementOps.Children(path, element))
                if (AddElement(null, childPath, child, filter) is { } childItem) kids.Add(childItem);

        var detail = Summary(element, path);
        var own = Matches(type + " " + detail + " " + element["id"], filter);
        if (filter.Length > 0 && !own && kids.Count == 0) return null;

        var isContainer = !isReference && ElementOps.RuleFor(element) != Carvera.Layout.ChildRule.None;
        var title = isReference ? "↪ " + element["region"] : type;
        var glyph = isReference ? "↪" : isContainer ? "▦" : "▫";
        var region = path.StartsWith("regions.", StringComparison.Ordinal) && ElementOps.IsTopLevel(path) ? "region " + path["regions.".Length..] : null;
        var item = new TreeViewItem { Tag = path, Header = Row(path, glyph, region ?? title, region is null ? detail : type, isReference ? null : null) };
        foreach (var kid in kids) item.Items.Add(kid);
        item.IsExpanded = filter.Length > 0 || !_collapsed.Contains(path) && (_expandedOnce.Contains(path) || IsDefaultExpanded(path));
        _items[path] = item;
        AttachMenu(item, path);
        if (isReference)
            item.DoubleTapped += (_, e) => { _ctx.Select("regions." + element["region"], this); e.Handled = true; };
        return item;
    }

    /// <summary>The root and the first two levels start open; deeper levels start closed until the user opens them.</summary>
    private static bool IsDefaultExpanded(string path) => JsonPath.Parse(path).Count <= 4 && path != "";

    private static string? Summary(JsonObject element, string path)
    {
        foreach (var key in new[] { "id", "text", "title", "label", "command", "bind", "source", "kind" })
            if (element[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return key == "id" ? "#" + s : s.Length > 28 ? s[..28] + "…" : s;
        return null;
    }

    private static string Glyph(string kind) => kind switch { "style" => "◐", "theme" => "◑", "key" => "⌨", "window" => "▭", "meta" => "ⓘ", _ => "▫" };

    private Border Row(string path, string glyph, string title, string? detail, string? extra)
    {
        var worst = _ctx.Doc.WorstFor(path);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 1) };
        panel.Children.Add(new TextBlock { Text = glyph, Width = 14, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center });
        if (detail is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = detail, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis });
        if (worst is not null)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = worst == DiagnosticSeverity.Error ? ErrorBrush : WarningBrush, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(dot, string.Join("\n", _ctx.Doc.DiagnosticsFor(path).Take(6).Select(d => d.Message)));
            panel.Children.Add(dot);
        }
        var row = new Border { Child = panel, Background = Brushes.Transparent, BorderThickness = new Thickness(0, 2), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(3) };
        _rows[path] = row;
        if (_drag is not null) AttachDrag(path, row);
        return row;
    }

    private void AttachDrag(string path, Border row)
    {
        _drag!.Attach(row, () =>
        {
            if (!JsonPath.IsElementPath(path) || path == "root" || ElementOps.IsTopLevel(path) || _ctx.Doc.Element(path) is not { } element) return null;
            return new DragPayload(ElementOps.TypeOf(element), path, null);
        });
    }

    private void AttachMenu(TreeViewItem item, string path)
    {
        item.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(item).Properties.IsRightButtonPressed) return;
            _ctx.Select(path, this);
            SyncSelection();
            item.ContextMenu = JsonPath.IsElementPath(path) ? ElementMenu.Build(_ctx, path) : PartMenu(path);
            item.ContextMenu.Open(item);
            e.Handled = true;
        };
    }

    private ContextMenu PartMenu(string path)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action click)
        {
            var i = new MenuItem { Header = header };
            i.Click += (_, _) => click();
            return i;
        }
        if (path.StartsWith("styles.", StringComparison.Ordinal))
        {
            var name = path["styles.".Length..];
            menu.Items.Add(Item("Duplicate", () => _ctx.Edit("Duplicate style", d => PartOps.DuplicateStyle(d, name))));
            menu.Items.Add(Item("Rename…", async () =>
            {
                if (_ctx.Actions.Owner is not { } owner) return;
                var renamed = await Dialogs.PromptAsync(owner, "Rename style", "New name (elements using it are updated):", name, "Rename");
                if (!string.IsNullOrWhiteSpace(renamed)) _ctx.Edit("Rename style", d => PartOps.RenameStyle(d, name, renamed));
            }));
            menu.Items.Add(Item("Delete", () => _ctx.Edit("Delete style", d => PartOps.RemoveStyle(d, name))));
        }
        else if (path.StartsWith("shortcuts[", StringComparison.Ordinal) && JsonPath.Parse(path)[1] is int index)
        {
            menu.Items.Add(Item("Move up", () => _ctx.Edit("Move shortcut", d => PartOps.MoveShortcut(d, index, -1))));
            menu.Items.Add(Item("Move down", () => _ctx.Edit("Move shortcut", d => PartOps.MoveShortcut(d, index, 1))));
            menu.Items.Add(Item("Delete", () => _ctx.Edit("Delete shortcut", d => PartOps.RemoveShortcut(d, index))));
        }
        menu.Items.Add(Item("Show in the code", () => _ctx.RequestCodeAt?.Invoke(path)));
        return menu;
    }

    // ------------------------------------------------------------------ selection

    private void RememberExpansion()
    {
        foreach (var (path, item) in _items)
        {
            if (item.IsExpanded) { _collapsed.Remove(path); _expandedOnce.Add(path); }
            else if (item.Items.Count > 0) { _collapsed.Add(path); _expandedOnce.Remove(path); }
        }
    }

    private void SyncSelection()
    {
        var selected = _ctx.Selection;
        _syncing = true;
        try
        {
            if (selected is null || !_items.TryGetValue(selected, out var item))
            {
                // A selection inside a folded part shows the part it belongs to.
                var fallback = selected is null ? null : JsonPath.ElementPrefix(selected);
                if (fallback is null || !_items.TryGetValue(fallback, out item)) { _tree.SelectedItem = null; return; }
            }
            // Unfold the way to it.
            for (var parent = item.Parent as TreeViewItem; parent is not null; parent = parent.Parent as TreeViewItem)
            {
                parent.IsExpanded = true;
                if (parent.Tag is string tag) { _collapsed.Remove(tag); _expandedOnce.Add(tag); }
            }
            _tree.SelectedItem = item;
            item.BringIntoView();
        }
        finally { _syncing = false; }
    }

    // ------------------------------------------------------------------ dropping

    Control IDropTarget.Surface => _tree;

    bool IDropTarget.Hover(Point windowPoint, DragPayload payload)
    {
        ClearDropMark();
        if (RowAt(windowPoint) is not { } hit) return false;
        var (path, row, zone) = hit;
        var where = Locate(path, zone, payload);
        if (where is null) return false;
        _dropRow = row;
        row.BorderBrush = DropBrush;
        row.Background = zone == 0 ? Brush.Parse("#2216A34A") : Brushes.Transparent;
        row.BorderThickness = zone < 0 ? new Thickness(0, 2, 0, 0) : zone > 0 ? new Thickness(0, 0, 0, 2) : new Thickness(1);
        _pendingDrop = new ActionsDrop(where);
        return true;
    }

    void IDropTarget.Drop(Point windowPoint, DragPayload payload)
    {
        var where = _pendingDrop?.Where;
        ((IDropTarget)this).Leave();
        if (where is not null) _ctx.Actions.ApplyDrop(payload, where);
    }

    void IDropTarget.Leave()
    {
        ClearDropMark();
        _pendingDrop = null;
    }

    private void ClearDropMark()
    {
        if (_dropRow is null) return;
        _dropRow.BorderBrush = Brushes.Transparent;
        _dropRow.Background = Brushes.Transparent;
        _dropRow.BorderThickness = new Thickness(0, 2);
        _dropRow = null;
    }

    /// <summary>The element row under a window point, and whether the point is in its upper part (-1), middle (0) or lower part (1).</summary>
    private (string Path, Border Row, int Zone)? RowAt(Point windowPoint)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        foreach (var (path, row) in _rows)
        {
            if (!JsonPath.IsElementPath(path) || !row.IsEffectivelyVisible || row.TranslatePoint(new Point(0, 0), top) is not { } origin) continue;
            // The row's own bounds are narrow; use the full width of the tree.
            var rect = new Rect(0, origin.Y, top.Bounds.Width, row.Bounds.Height);
            if (!rect.Contains(windowPoint)) continue;
            var fraction = (windowPoint.Y - origin.Y) / Math.Max(1, row.Bounds.Height);
            return (path, row, fraction < 0.28 ? -1 : fraction > 0.72 ? 1 : 0);
        }
        return null;
    }

    private EditorActions.DropLocation? Locate(string path, int zone, DragPayload payload)
    {
        var data = _ctx.Doc.Data;
        if (data is null || ElementOps.Element(data, path) is not { } element) return null;
        if (payload.SourcePath is { } source && JsonPath.IsWithin(path, source)) return null;

        var top = ElementOps.IsTopLevel(path);
        if (zone == 0 || top)
        {
            // Into the element: only containers take children (a region reference is changed at the region).
            if (ElementOps.IsRegionReference(element)) return null;
            if (!ElementOps.CanContain(element)) return zone == 0 && !top ? Sibling(data, path, 1) : null;
            if (ElementOps.RuleFor(element) == ChildRule.Single && ElementOps.Children(path, element).Any(c => c.Path != payload.SourcePath)) return null;
            return new EditorActions.DropLocation(path, -1);
        }
        return Sibling(data, path, zone);
    }

    private static EditorActions.DropLocation? Sibling(JsonObject data, string path, int side)
    {
        if (ElementOps.ParentOf(data, path) is not { } p) return null;
        return new EditorActions.DropLocation(p.ParentPath, side < 0 ? p.Index : p.Index + 1);
    }
}
