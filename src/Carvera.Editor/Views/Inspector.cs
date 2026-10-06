using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>The properties of whatever is selected: an element, a named style, the theme, a shortcut, the window or the layout's own details.</summary>
public sealed class Inspector : DockPanel
{
    private static readonly string[] SizeNames = ["width", "height", "minWidth", "maxWidth", "minHeight", "maxHeight", "margin", "padding", "align", "valign"];
    private static readonly string[] VisibilityNames = ["visible", "enabled", "tooltip"];
    private static readonly string[] AppearanceNames = ["class", "style", "visuals", "conditions"];
    private static readonly string[] PlacementNames = ["row", "column", "rowSpan", "columnSpan", "x", "y", "title"];
    private static readonly string[] Structural = ["type", "children", "child", "region"];

    private readonly EditorContext _ctx;
    private readonly StackPanel _content = new() { Spacing = 8, Margin = new Thickness(12, 8, 12, 24) };
    private readonly ScrollViewer _scroll;
    private int _builtRevision = -1;
    private int _builtGeneration = -1;
    private string? _builtSelection;
    private bool _rebuildPending;

    public Inspector(EditorContext ctx)
    {
        _ctx = ctx;
        _scroll = new ScrollViewer { Content = _content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Children.Add(_scroll);
        ctx.SelectionChanged += (_, _) => Schedule(force: true);
        ctx.DocumentChanged += _ => Schedule(force: false);
        // When a text box gives up focus its edit has been applied: bring the form's markers up to date.
        AddHandler(LostFocusEvent, (_, _) => { if (_builtRevision != _ctx.Doc.Revision) Schedule(force: false); }, RoutingStrategies.Bubble, handledEventsToo: true);
        Rebuild();
    }

    private bool FocusIsInside() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused && focused.FindAncestorOfType<Inspector>() == this;

    private void Schedule(bool force)
    {
        // A field's own edit changes the document without making the form stale. Rebuilding the form under the cursor (or while the user
        // tabs from one field to the next) would take the focus away, so it waits until the focus leaves the form.
        var stale = force || _ctx.FormGeneration != _builtGeneration;
        if (!stale && FocusIsInside()) return;
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.UIThread.Post(() => { _rebuildPending = false; Rebuild(); }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var offset = _builtSelection == _ctx.Selection ? _scroll.Offset : default;
        _builtRevision = _ctx.Doc.Revision;
        _builtSelection = _ctx.Selection;
        _ctx.BumpForms();
        _builtGeneration = _ctx.FormGeneration;
        _content.Children.Clear();
        var path = _ctx.Selection;
        if (path is null || _ctx.Doc.Data is null)
        {
            _content.Children.Add(Note(_ctx.Doc.Data is null ? "The JSON has an error. Fix it in the Code tab; the form comes back when it parses." : "Select something in the tree or the preview."));
            return;
        }
        if (JsonPath.IsElementPath(path)) BuildElement(path);
        else if (path.StartsWith("styles.", StringComparison.Ordinal)) BuildStyle(path["styles.".Length..]);
        else if (path == "theme") BuildTheme();
        else if (path.StartsWith("shortcuts[", StringComparison.Ordinal)) BuildShortcut(path);
        else if (path == "window") BuildWindow();
        else if (path == "meta") BuildMeta();
        else _content.Children.Add(Note("Nothing to edit here."));
        Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ pieces

    private static Control Note(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6) };

    private static Control Title(string title, string? subtitle = null)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold });
        if (subtitle is not null) stack.Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush.Parse("#64748B") });
        return stack;
    }

    private Control Section(string title, Control body, bool open = true)
    {
        var expanded = !_ctx.Settings.CollapsedSections.Contains(title) && open || _ctx.Settings.CollapsedSections.Contains("+" + title);
        var collapsible = new Collapsible(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, body, expanded, isOpen =>
        {
            var list = _ctx.Settings.CollapsedSections;
            list.Remove(title);
            list.Remove("+" + title);
            if (isOpen && !open) list.Add("+" + title);
            if (!isOpen && open) list.Add(title);
        });
        return new Border { Child = collapsible, BorderBrush = Brush.Parse("#E2E8F0"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private Control Diagnostics(string path)
    {
        var own = _ctx.Doc.Diagnostics.Where(d => d.Path == path || d.Path == path + ".type" || d.Path == path + ".region" || d.Path == path + ".children").ToList();
        var stack = new StackPanel { Spacing = 3 };
        foreach (var d in own)
        {
            var error = d.Severity == DiagnosticSeverity.Error;
            stack.Children.Add(new Border
            {
                Background = Brush.Parse(error ? "#FEE2E2" : "#FEF3C7"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4),
                Child = new TextBlock { Text = d.Message, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush.Parse(error ? "#991B1B" : "#92400E") },
            });
        }
        return stack;
    }

    private IEnumerable<PropSpec> Specs(ComponentSpec spec, params string[] names) =>
        names.Select(n => spec.Find(n)).Where(p => p is not null).Select(p => p!);

    // ------------------------------------------------------------------ element

    private void BuildElement(string path)
    {
        var element = _ctx.Doc.Element(path);
        if (element is null) { _content.Children.Add(Note("This element no longer exists.")); return; }
        var isReference = ElementOps.IsRegionReference(element);
        var type = ElementOps.TypeOf(element);
        var known = ComponentCatalog.TryGet(type, out var spec);
        var parent = ElementOps.ParentOf(_ctx.Doc.Data!, path);
        var parentType = parent is null ? null : ElementOps.TypeOf(parent.Value.Parent).ToLowerInvariant();

        _content.Children.Add(Title(isReference ? "Region " + element["region"] : type, isReference ? "A use of a region. Change the region to change every use; the settings here apply to this use only." : spec?.Description));
        _content.Children.Add(Diagnostics(path));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(SmallButton("Show in the Controller", () => _ctx.Link?.Reveal(path), _ctx.Link is { Connected: true }));
        actions.Children.Add(SmallButton("Show in the code", () => _ctx.RequestCodeAt?.Invoke(path)));
        if (isReference) actions.Children.Add(SmallButton("Go to region", () => _ctx.Select("regions." + element["region"])));
        _content.Children.Add(actions);

        if (!isReference && type.Equals("tabs", StringComparison.OrdinalIgnoreCase) && ElementOps.Children(path, element) is { Count: > 0 } pages)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (pagePath, page) in pages)
            {
                var target = pagePath;
                var title = page["title"]?.ToString() ?? ElementOps.TypeOf(page);
                var button = SmallButton(title, () => _ctx.Select(target));
                button.Margin = new Thickness(0, 0, 4, 4);
                row.Children.Add(button);
            }
            _content.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Show page in the preview (and the Controller):", FontSize = 12, Foreground = Brush.Parse("#64748B") }, row } });
        }

        // Identity
        var identity = new StackPanel { Spacing = 2 };
        if (!isReference)
        {
            var typeBox = new ComboBox { ItemsSource = ComponentCatalog.All.Select(c => c.Type).ToList(), SelectedItem = known ? spec!.Type : null, MinWidth = 160 };
            typeBox.SelectionChanged += (_, _) =>
            {
                if (typeBox.SelectedItem is string chosen && !chosen.Equals(type, StringComparison.Ordinal)) _ctx.SetProperty(path, "type", JsonValue.Create(chosen), "Change type");
            };
            identity.Children.Add(PropertyEditors.FieldRow("type", "What kind of element this is. Properties the new type does not have are flagged.", typeBox, true));
        }
        else
        {
            var regions = new ComboBox { ItemsSource = _ctx.Doc.RegionNames.ToList(), SelectedItem = element["region"]?.ToString(), MinWidth = 160 };
            regions.SelectionChanged += (_, _) => { if (regions.SelectedItem is string name) _ctx.SetProperty(path, "region", JsonValue.Create(name), "Change region"); };
            identity.Children.Add(PropertyEditors.FieldRow("region", "Which region this element uses.", regions, true));
        }
        var common = ComponentCatalog.Common;
        var idSpec = common.First(p => p.Name == "id");
        identity.Children.Add(new PropertyGrid(_ctx, path, [idSpec], type));
        _content.Children.Add(identity);

        if (!isReference && known)
        {
            var contentSpecs = spec!.Properties.ToList();
            if (contentSpecs.Count > 0) _content.Children.Add(Section("Content", new PropertyGrid(_ctx, path, contentSpecs, type)));
            _content.Children.Add(Section("Size and spacing", new PropertyGrid(_ctx, path, Specs(spec, SizeNames), type)));
            var placement = PlacementFor(spec, type, parentType);
            if (placement.Count > 0) _content.Children.Add(Section("Placement in its container", new PropertyGrid(_ctx, path, placement, type)));
            _content.Children.Add(Section("Visibility and help", new PropertyGrid(_ctx, path, Specs(spec, VisibilityNames), type)));
            _content.Children.Add(Section("Appearance", new PropertyGrid(_ctx, path, Specs(spec, AppearanceNames), type), open: element["style"] is not null || element["visuals"] is not null || element["conditions"] is not null || element["class"] is not null));
            AddOther(element, path, spec);
        }
        else if (isReference)
        {
            var referenceSpec = new ComponentSpec("region", "", ChildRule.None, [], []);
            _content.Children.Add(Section("Size and spacing", new PropertyGrid(_ctx, path, Specs(referenceSpec, SizeNames), null)));
            var placement = PlacementFor(referenceSpec, "region", parentType);
            if (placement.Count > 0) _content.Children.Add(Section("Placement in its container", new PropertyGrid(_ctx, path, placement, null)));
            _content.Children.Add(Section("Visibility", new PropertyGrid(_ctx, path, Specs(referenceSpec, "visible", "enabled"), null)));
        }
    }

    private List<PropSpec> PlacementFor(ComponentSpec spec, string type, string? parentType)
    {
        var names = new List<string>();
        if (parentType == "grid") names.AddRange(["row", "column", "rowSpan", "columnSpan"]);
        if (parentType == "canvas") names.AddRange(["x", "y"]);
        if (parentType == "tabs" || type.Equals("panel", StringComparison.OrdinalIgnoreCase)) names.Add("title");
        return ComponentCatalog.Common.Where(p => names.Contains(p.Name)).ToList();
    }

    private void AddOther(JsonObject element, string path, ComponentSpec spec)
    {
        var others = element.Where(p => !Structural.Contains(p.Key) && !LayoutLoaderKey(p.Key) && spec.Find(p.Key) is null).ToList();
        if (others.Count == 0) return;
        var body = new StackPanel { Spacing = 2, Children = { Note("The Controller ignores these for this kind of element.") } };
        foreach (var (name, value) in others)
        {
            var key = name;
            var editor = PropertyEditors.TextEditor(new PropContext { Ctx = _ctx, Spec = new PropSpec(name, PropKind.Any, ""), ObjectPath = path, Current = value, Commit = v => _ctx.SetProperty(path, key, v) },
                text =>
                {
                    if (string.IsNullOrWhiteSpace(text)) return (null, null);
                    try { return (JsonNode.Parse(text, documentOptions: LayoutLoader.DocumentOptions), null); }
                    catch (System.Text.Json.JsonException) { return (JsonValue.Create(text), null); }
                });
            body.Children.Add(PropertyEditors.FieldRow(name, "Not used by this kind of element", editor, true, () => _ctx.SetProperty(path, key, null, $"Remove {key}")));
        }
        _content.Children.Add(Section("Other properties", body, open: true));
    }

    private static bool LayoutLoaderKey(string key) => key.StartsWith("//", StringComparison.Ordinal) || key.StartsWith('_') || key == "$comment";

    private static Button SmallButton(string text, Action click, bool enabled = true)
    {
        var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(7, 2), IsEnabled = enabled };
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------------ styles

    private void BuildStyle(string name)
    {
        _content.Children.Add(Title("Style " + name, "A named look. Elements use it by listing the name in 'class'; several can be combined."));
        var uses = ElementOps.AllElements(_ctx.Doc.Data!).Where(e => ClassNames(e.Element).Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        var usedBy = new StackPanel { Spacing = 2 };
        usedBy.Children.Add(new TextBlock { Text = uses.Count == 0 ? "Not used by any element yet." : $"Used by {uses.Count} element{(uses.Count == 1 ? "" : "s")}:", FontSize = 12, Foreground = Brush.Parse("#64748B") });
        foreach (var (usePath, use) in uses.Take(12))
        {
            var link = new Button { Content = ElementOps.TypeOf(use) + (use["id"] is { } id ? " #" + id : "") + (use["text"] is JsonValue t && t.TryGetValue<string>(out var text) ? " “" + text + "”" : ""), Padding = new Thickness(6, 1), FontSize = 12, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
            var target = usePath;
            link.Click += (_, _) => _ctx.Select(target);
            usedBy.Children.Add(link);
        }
        _content.Children.Add(usedBy);
        _content.Children.Add(new PropertyGrid(_ctx, "styles." + name, ComponentCatalog.VisualProperties, null));
    }

    private static IEnumerable<string> ClassNames(JsonObject element) => element["class"] switch
    {
        JsonArray a => a.Select(n => n?.ToString() ?? ""),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
        _ => [],
    };

    // ------------------------------------------------------------------ theme

    private void BuildTheme()
    {
        _content.Children.Add(Title("Theme", "Colours and fonts every element can use as @name. Only the values you change are written to the layout; the rest keep the built-in defaults."));
        var theme = _ctx.Doc.Data!["theme"] as JsonObject;
        var body = new StackPanel { Spacing = 2 };

        void Row(string token, string defaultValue)
        {
            var current = theme?[token];
            var isColor = defaultValue.StartsWith('#');
            var spec = new PropSpec(token, isColor ? PropKind.Color : PropKind.String, "Default: " + defaultValue);
            var editor = PropertyEditors.Create(new PropContext
            {
                Ctx = _ctx, Spec = spec, ObjectPath = "theme", Current = current ?? (JsonNode?)JsonValue.Create(defaultValue), OwnerType = null,
                Commit = v => _ctx.SetProperty("theme", token, v is JsonValue jv && jv.TryGetValue<string>(out var s) && s == defaultValue ? null : v),
            });
            body.Children.Add(PropertyEditors.FieldRow(token, "Default: " + defaultValue, editor, current is not null, () => _ctx.SetProperty("theme", token, null, $"Reset {token}")));
        }

        foreach (var (token, value) in EditorCatalog.ThemeDefaults.Where(p => !p.Key.StartsWith("operation", StringComparison.OrdinalIgnoreCase))) Row(token, value);
        foreach (var (token, value) in EditorCatalog.ThemeDefaults.Where(p => p.Key.StartsWith("operation", StringComparison.OrdinalIgnoreCase))) Row(token, value);
        foreach (var (token, value) in theme ?? [])
            if (!EditorCatalog.ThemeDefaults.ContainsKey(token)) Row(token, value?.ToString() ?? "");
        _content.Children.Add(body);

        var add = SmallButton("Add your own colour…", async () =>
        {
            if (_ctx.Actions.Owner is not { } owner) return;
            var name = await Dialogs.PromptAsync(owner, "New theme colour", "Name (used as @name):", "brand", "Add");
            if (!string.IsNullOrWhiteSpace(name)) _ctx.SetProperty("theme", name.Trim(), JsonValue.Create("#888888"), "Add colour");
        });
        _content.Children.Add(add);
    }

    // ------------------------------------------------------------------ shortcuts

    private void BuildShortcut(string path)
    {
        if (_ctx.Doc.Element(path) is not { } shortcut) { _content.Children.Add(Note("This shortcut no longer exists.")); return; }
        _content.Children.Add(Title("Shortcut " + shortcut["key"], "A key that runs a command. With a release command it can jog while held."));
        _content.Children.Add(Diagnostics(path));
        var key = new PropSpec("key", PropKind.String, "Key combination, e.g. F5, Ctrl+Shift+J, Left.");
        var keyEditor = PropertyEditors.TextEditor(new PropContext { Ctx = _ctx, Spec = key, ObjectPath = path, Current = shortcut["key"], Commit = v => _ctx.SetProperty(path, "key", v) },
            text => text.Trim().Length == 0 ? (null, "A shortcut needs a key.") : TryKey(text.Trim()) ? (JsonValue.Create(text.Trim()), null) : (null, "That is not a valid key combination."));
        var record = SmallButton("Press keys…", async () =>
        {
            if (_ctx.Actions.Owner is not { } owner) return;
            var gesture = await CaptureKeyAsync(owner);
            if (gesture is not null) _ctx.SetProperty(path, "key", JsonValue.Create(gesture), "Set key");
        });
        var keyRow = new DockPanel();
        DockPanel.SetDock(record, Dock.Right);
        keyRow.Children.Add(record);
        keyRow.Children.Add(keyEditor);
        _content.Children.Add(PropertyEditors.FieldRow("key", key.Description, keyRow, true));

        var specs = new List<PropSpec>
        {
            new("command", PropKind.Command, "Command to run."),
            new("args", PropKind.Args, "Arguments for the command."),
            new("release", PropKind.Command, "Command to run when the key is let go."),
            new("releaseArgs", PropKind.Args, "Arguments for the release command."),
            new("repeat", PropKind.Bool, "Run again while the key is held (default true)."),
        };
        _content.Children.Add(new PropertyGrid(_ctx, path, specs, null));
    }

    private static bool TryKey(string text)
    {
        try { _ = KeyGesture.Parse(text); return true; }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return false; }
    }

    private static async Task<string?> CaptureKeyAsync(Window owner)
    {
        var label = new TextBlock { Text = "Press the key combination…", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(40, 30) };
        var window = new Window { Title = "Press keys", Content = label, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        string? result = null;
        window.KeyDown += (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { window.Close(); return; }
            var parts = new List<string>();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            parts.Add(e.Key.ToString());
            result = string.Join("+", parts);
            e.Handled = true;
            window.Close();
        };
        await window.ShowDialog(owner);
        return result;
    }

    // ------------------------------------------------------------------ window and details

    private void BuildWindow()
    {
        _content.Children.Add(Title("Window", "How the Controller's window starts out with this layout."));
        PropSpec[] specs =
        [
            new("width", PropKind.Number, "Starting width in pixels."), new("height", PropKind.Number, "Starting height in pixels."),
            new("minWidth", PropKind.Number, "The window cannot be made narrower than this."), new("minHeight", PropKind.Number, "The window cannot be made shorter than this."),
            new("title", PropKind.String, "Window title (default: Carvera Controller — layout name)."),
        ];
        _content.Children.Add(new PropertyGrid(_ctx, "window", specs, null));
    }

    private void BuildMeta()
    {
        _content.Children.Add(Title("Layout details"));
        PropSpec[] specs =
        [
            new("name", PropKind.String, "The name shown in the layout list."),
            new("description", PropKind.String, "A sentence about this layout."),
        ];
        _content.Children.Add(new PropertyGrid(_ctx, "", specs, null));
        var file = _ctx.Doc.FilePath;
        _content.Children.Add(Note(file is null ? "Not saved to a file yet." : "File: " + file));
    }
}
