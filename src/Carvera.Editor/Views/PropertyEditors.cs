using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.Core.Expressions;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>What an editor needs to show and change one property.</summary>
public sealed class PropContext
{
    public required EditorContext Ctx { get; init; }
    public required PropSpec Spec { get; init; }
    public required string ObjectPath { get; init; }
    public required JsonNode? Current { get; init; }
    /// <summary>Stores the new value; null removes the property.</summary>
    public required Action<JsonNode?> Commit { get; init; }

    private int? _generation;

    /// <summary>Remembers which generation of forms this property belongs to. Called when the editor is created.</summary>
    public void Arm() => _generation ??= Ctx.FormGeneration;

    /// <summary>Whether the form this editor is part of still describes the document.</summary>
    public bool IsCurrent => _generation is null || _generation == Ctx.FormGeneration;

    /// <summary>Stores a value, unless the form is out of date (a pause in typing may end after the selection moved or the document changed).</summary>
    public void Set(JsonNode? value)
    {
        if (IsCurrent) Commit(value);
    }
    /// <summary>The type of the element that owns the property, when there is one (its states and children rules matter to some editors).</summary>
    public string? OwnerType { get; init; }
    public EditorDocument Doc => Ctx.Doc;
}

/// <summary>Editors for each kind of layout property. Each returns a control that reads <see cref="PropContext.Current"/> and calls Commit as the user edits.</summary>
public static class PropertyEditors
{
    public static readonly IBrush ErrorBrush = Brush.Parse("#DC2626");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    public static Control Create(PropContext c)
    {
        c.Arm();
        return Build(c);
    }

    private static Control Build(PropContext c) => c.Spec.Kind switch
    {
        PropKind.Bool => Bool(c),
        PropKind.Enum => Enum(c),
        PropKind.Number => TextEditor(c, ParseNumber),
        PropKind.String => TextEditor(c, text => (text.Length == 0 ? null : JsonValue.Create(text), null)),
        PropKind.Size => SizeEditor(c),
        PropKind.Edges => TextEditor(c, ParseEdges, "8 | 8 4 | top right bottom left"),
        PropKind.Color => ColorEditor(c),
        PropKind.Image => ImageEditor(c),
        PropKind.Expression => WithStatePicker(c, TextEditor(c, ParseExpression, "e.g. machine.state == 'Idle'"), template: false),
        PropKind.Template => WithStatePicker(c, TextEditor(c, ParseTemplate, "Text with {axis.x.work:0.000}"), template: true),
        PropKind.Command => CommandEditor(c),
        PropKind.Args => ArgsEditor(c),
        PropKind.StringList when c.Spec.Name == "class" => ClassEditor(c),
        PropKind.StringList when c.Spec.Values is { Length: > 0 } => ChoiceList(c),
        PropKind.StringList => TextEditor(c, ParseStringList, "comma separated"),
        PropKind.NumberList => TextEditor(c, ParseNumberList, "comma separated numbers"),
        PropKind.SizeList => TextEditor(c, ParseSizeList, "e.g. 200, *, 2*, auto"),
        PropKind.Style => VisualBlockField(c),
        PropKind.Visuals => new VisualsEditor(c),
        PropKind.Conditions => new ConditionsEditor(c),
        PropKind.Options => new OptionsEditor(c),
        _ => JsonEditor(c),
    };

    // ------------------------------------------------------------------ text

    /// <summary>
    /// A text box that applies its value while the user types (after a short pause) and when it loses focus. The parser returns the JSON
    /// value (null = unset) or an error message, which is shown on the box and stops the value being applied.
    /// </summary>
    public static TextBox TextEditor(PropContext c, Func<string, (JsonNode? Value, string? Error)> parse, string? hint = null, bool multiline = false)
    {
        var box = new TextBox { Text = Display(c.Current), PlaceholderText = hint ?? Placeholder(c.Spec), MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
        if (multiline) { box.AcceptsReturn = true; box.MinHeight = 70; box.FontFamily = Mono; box.FontSize = 12; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        var dirty = false;
        var applying = false;
        var baseline = box.Text ?? "";

        void Apply()
        {
            timer.Stop();
            if (!dirty) return;
            dirty = false;
            var (value, error) = parse(box.Text ?? "");
            baseline = box.Text ?? "";
            if (error is not null)
            {
                box.BorderBrush = ErrorBrush;
                ToolTip.SetTip(box, error);
                return;
            }
            box.ClearValue(Border.BorderBrushProperty);
            ToolTip.SetTip(box, c.Spec.Description);
            applying = true;
            try { c.Set(value); }
            finally { applying = false; }
        }
        timer.Tick += (_, _) => Apply();
        box.TextChanged += (_, _) =>
        {
            if (applying) return;
            // The box announces the text it was created with; only a change made by the user counts.
            if ((box.Text ?? "") == baseline) return;
            dirty = true;
            timer.Stop();
            timer.Start();
        };
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !multiline) { Apply(); e.Handled = true; }
        };
        ToolTip.SetTip(box, c.Spec.Description);
        return box;
    }

    private static string Placeholder(PropSpec spec) => spec.Kind switch
    {
        PropKind.Number => "number",
        PropKind.Size => "fill",
        _ => "",
    };

    public static string Display(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        JsonArray a => string.Join(", ", a.Select(Display)),
        _ => node.ToJsonString(),
    };

    private static (JsonNode?, string?) ParseNumber(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return (null, null);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (null, "Enter a number.");
        return (d == Math.Floor(d) && Math.Abs(d) < 1e9 ? JsonValue.Create((long)d) : JsonValue.Create(d), null);
    }

    private static (JsonNode?, string?) ParseEdges(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return (null, null);
        var node = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d == Math.Floor(d) ? (long)d : d) : JsonValue.Create(text);
        return Edges.TryParse(node, out _, out var error) ? (node, null) : (null, error ?? "Not a valid spacing.");
    }

    private static (JsonNode?, string?) ParseExpression(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return (null, null);
        if (text is "true" or "false") return (JsonValue.Create(text == "true"), null);
        return Expression.TryParse(text, out _, out var error) ? (JsonValue.Create(text), null) : (null, error);
    }

    private static (JsonNode?, string?) ParseTemplate(string text) =>
        text.Length == 0 ? (null, null) : TextTemplate.TryParse(text, out _, out var error) ? (JsonValue.Create(text), null) : (null, error);

    private static (JsonNode?, string?) ParseStringList(string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (parts.Length == 0 ? null : new JsonArray(parts.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()), null);
    }

    private static (JsonNode?, string?) ParseNumberList(string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numbers = new List<JsonNode>();
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (null, $"'{part}' is not a number.");
            numbers.Add(d == Math.Floor(d) && Math.Abs(d) < 1e9 ? JsonValue.Create((long)d)! : JsonValue.Create(d)!);
        }
        return (numbers.Count == 0 ? null : new JsonArray(numbers.ToArray()), null);
    }

    private static (JsonNode?, string?) ParseSizeList(string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = new List<JsonNode>();
        foreach (var part in parts)
        {
            if (!SizeSpec.TryParse(part, out _, out var error)) return (null, error ?? $"'{part}' is not a size.");
            items.Add(double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create((long)d)! : JsonValue.Create(part)!);
        }
        return (items.Count == 0 ? null : new JsonArray(items.ToArray()), null);
    }

    private static Control JsonEditor(PropContext c)
    {
        var current = c.Current is null ? "" : JsonFormatter.Format(c.Current).TrimEnd();
        var box = TextEditor(c, text =>
        {
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            try { return (JsonNode.Parse(text, documentOptions: LayoutLoader.DocumentOptions), null); }
            catch (System.Text.Json.JsonException ex) { return (null, ex.Message); }
        }, "JSON", multiline: true);
        box.Text = current;
        return box;
    }

    // ------------------------------------------------------------------ simple choices

    private static Control Bool(PropContext c)
    {
        var box = new ComboBox { ItemsSource = new[] { "(default)", "true", "false" }, MinWidth = 110 };
        box.SelectedIndex = c.Current is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? 1 : 2) : 0;
        box.SelectionChanged += (_, _) => c.Set(box.SelectedIndex switch { 1 => JsonValue.Create(true), 2 => JsonValue.Create(false), _ => null });
        ToolTip.SetTip(box, c.Spec.Description);
        return box;
    }

    private static Control Enum(PropContext c)
    {
        var values = c.Spec.Values ?? [];
        var current = Display(c.Current);
        var items = new List<string> { "(default)" };
        items.AddRange(values);
        if (current.Length > 0 && !values.Contains(current, StringComparer.OrdinalIgnoreCase)) items.Add(current);
        var box = new ComboBox { ItemsSource = items, MinWidth = 140 };
        box.SelectedIndex = current.Length == 0 ? 0 : items.FindIndex(i => i.Equals(current, StringComparison.OrdinalIgnoreCase));
        box.SelectionChanged += (_, _) => c.Set(box.SelectedIndex <= 0 ? null : JsonValue.Create(items[box.SelectedIndex]));
        ToolTip.SetTip(box, c.Spec.Description);
        return box;
    }

    private static Control ChoiceList(PropContext c)
    {
        var chosen = (c.Current as JsonArray)?.Select(n => n?.ToString() ?? "").ToList()
                     ?? (c.Current is JsonValue v && v.TryGetValue<string>(out var s) ? s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).ToList() : []);
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var value in c.Spec.Values!)
        {
            var box = new ToggleButton { Content = value, IsChecked = chosen.Contains(value, StringComparer.OrdinalIgnoreCase), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 4), MinHeight = 24 };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true && !chosen.Contains(value)) chosen.Add(value);
                else if (box.IsChecked != true) chosen.RemoveAll(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
                // Keep the order the catalog lists them in.
                var ordered = c.Spec.Values!.Where(v2 => chosen.Contains(v2, StringComparer.OrdinalIgnoreCase)).ToList();
                c.Set(ordered.Count == 0 ? null : new JsonArray(ordered.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()));
            };
            panel.Children.Add(box);
        }
        ToolTip.SetTip(panel, c.Spec.Description);
        return panel;
    }

    private static Control ClassEditor(PropContext c)
    {
        var selected = (c.Current as JsonArray)?.Select(n => n?.ToString() ?? "").ToList()
                       ?? (c.Current is JsonValue v && v.TryGetValue<string>(out var s) ? s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).ToList() : []);
        var styles = c.Doc.StyleNames.ToList();
        foreach (var name in selected.Where(n => !styles.Contains(n))) styles.Add(name);
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        if (styles.Count == 0) panel.Children.Add(new TextBlock { Text = "No styles yet — add one in the tree under Styles.", Foreground = Brushes.Gray, FontSize = 12 });
        foreach (var name in styles)
        {
            var known = c.Doc.StyleNames.Contains(name);
            var box = new ToggleButton { Content = name, IsChecked = selected.Contains(name), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 4), MinHeight = 24, Foreground = known ? null : ErrorBrush };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true && !selected.Contains(name)) selected.Add(name);
                else if (box.IsChecked != true) selected.Remove(name);
                c.Set(selected.Count == 0 ? null : selected.Count == 1 ? JsonValue.Create(selected[0]) : new JsonArray(selected.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()));
            };
            panel.Children.Add(box);
        }
        ToolTip.SetTip(panel, c.Spec.Description);
        return panel;
    }

    // ------------------------------------------------------------------ size

    private static Control SizeEditor(PropContext c)
    {
        var box = TextEditor(c, text =>
        {
            text = text.Trim();
            if (text.Length == 0) return (null, null);
            if (!SizeSpec.TryParse(text, out _, out var error)) return (null, error);
            return (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d == Math.Floor(d) ? (long)d : d) : JsonValue.Create(text), null);
        }, "fill");
        var presets = new Button { Content = "▾", Padding = new Thickness(6, 2), MinHeight = 26 };
        presets.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var (label, value) in new (string, string?)[] { ("Fill the space (default)", null), ("Fit the content (auto)", "auto"), ("Half (50%)", "50%"), ("One share (1*)", "1*"), ("Two shares (2*)", "2*"), ("120 px", "120") })
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => { box.Text = value ?? ""; c.Set(value is null ? null : double.TryParse(value, out var d) ? JsonValue.Create((long)d) : JsonValue.Create(value)); };
                menu.Items.Add(item);
            }
            menu.Open(presets);
        };
        var dock = new DockPanel();
        DockPanel.SetDock(presets, Dock.Right);
        dock.Children.Add(presets);
        dock.Children.Add(box);
        return dock;
    }

    // ------------------------------------------------------------------ state paths in expressions and templates

    private static Control WithStatePicker(PropContext c, TextBox box, bool template)
    {
        var button = new Button { Content = "⋯", Padding = new Thickness(6, 2), MinHeight = 26 };
        ToolTip.SetTip(button, "Insert a machine state value");
        button.Click += (_, _) => Pickers.Show(button, "Machine state values", EditorCatalog.StatePathNames.Select(p => new PickItem(p)).ToList(), path =>
        {
            var insert = template ? "{" + path + "}" : path;
            var text = box.Text ?? "";
            var start = Math.Min(box.SelectionStart, box.SelectionEnd);
            var end = Math.Max(box.SelectionStart, box.SelectionEnd);
            box.Text = text[..Math.Min(start, text.Length)] + insert + text[Math.Min(end, text.Length)..];
            box.CaretIndex = start + insert.Length;
            box.Focus();
        });
        var dock = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        dock.Children.Add(button);
        dock.Children.Add(box);
        return dock;
    }

    // ------------------------------------------------------------------ colour

    private static Control ColorEditor(PropContext c)
    {
        var box = TextEditor(c, text =>
        {
            text = text.Trim();
            if (text.Length == 0) return (null, null);
            return text.StartsWith('@') || Color.TryParse(text, out _) ? (JsonValue.Create(text), null) : (null, "Use #RRGGBB, a colour name, or @token.");
        }, "#RRGGBB or @token");
        var swatch = new Button { Width = 30, MinHeight = 26, Padding = new Thickness(0), BorderBrush = Brush.Parse("#94A3B8"), BorderThickness = new Thickness(1) };
        void Paint()
        {
            var text = box.Text?.Trim() ?? "";
            var theme = new Theme(c.Doc.Data?["theme"] is JsonObject t ? t.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "") : null);
            swatch.Background = theme.ResolveColor(text) is { } color ? new SolidColorBrush(color) : new DrawingBrush();
            swatch.Content = swatch.Background is SolidColorBrush ? null : new TextBlock { Text = "∅", HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Gray };
        }
        box.TextChanged += (_, _) => Paint();
        Paint();

        swatch.Click += (_, _) =>
        {
            var view = new ColorView { IsAlphaVisible = false, IsColorSpectrumVisible = true, IsComponentSliderVisible = true, IsColorPaletteVisible = false, Width = 260 };
            var theme = new Theme(c.Doc.Data?["theme"] is JsonObject t ? t.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "") : null);
            if (theme.ResolveColor(box.Text?.Trim()) is { } start) view.Color = start;
            var tokens = new WrapPanel { Width = 260 };
            foreach (var token in EditorCatalog.ColorTokens.Where(k => !k.StartsWith("operation")))
            {
                var chip = new Button { Width = 22, Height = 22, Margin = new Thickness(0, 0, 3, 3), Padding = new Thickness(0), Background = new SolidColorBrush(theme.ResolveColor("@" + token) ?? Colors.Transparent) };
                ToolTip.SetTip(chip, "@" + token);
                chip.Click += (_, _) => { box.Text = "@" + token; c.Set(JsonValue.Create("@" + token)); };
                tokens.Children.Add(chip);
            }
            var clear = new Button { Content = "No colour" };
            clear.Click += (_, _) => { box.Text = ""; c.Set(null); };
            var suppress = true;
            view.ColorChanged += (_, e) =>
            {
                if (suppress) return;
                var hex = e.NewColor.A == 255 ? $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}" : e.NewColor.ToString();
                box.Text = hex;
                c.Set(JsonValue.Create(hex));
            };
            var flyout = new Flyout
            {
                Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Theme colours", FontWeight = FontWeight.SemiBold }, tokens, new TextBlock { Text = "Custom colour", FontWeight = FontWeight.SemiBold }, view, clear } },
            };
            flyout.Opened += (_, _) => suppress = false;
            flyout.ShowAt(swatch);
        };
        var dock = new DockPanel();
        DockPanel.SetDock(swatch, Dock.Right);
        dock.Children.Add(swatch);
        dock.Children.Add(box);
        return dock;
    }

    // ------------------------------------------------------------------ image

    private static Control ImageEditor(PropContext c)
    {
        var box = TextEditor(c, text => (text.Trim().Length == 0 ? null : JsonValue.Create(text.Trim()), null), "path or builtin:name");
        var thumb = new Border { Width = 30, Height = 26, Background = Brush.Parse("#F1F5F9"), CornerRadius = new CornerRadius(3), ClipToBounds = true };
        var loader = new ImageLoader(c.Doc.BaseDirectory);
        void Paint()
        {
            var source = box.Text?.Trim();
            thumb.Child = string.IsNullOrEmpty(source) ? null : loader.CreateControl(source, 22, 22, Brush.Parse("#334155"));
        }
        box.TextChanged += (_, _) => Paint();
        Paint();

        var builtin = new Button { Content = "Built-in", Padding = new Thickness(6, 2), MinHeight = 26 };
        builtin.Click += (_, _) => Pickers.Show(builtin, "Built-in pictures", EditorCatalog.BuiltinImages.Select(n => new PickItem("builtin:" + n, null, null, () =>
        {
            var icon = loader.CreateControl("builtin:" + n, 18, 18, Brush.Parse("#334155"));
            return icon;
        })).ToList(), value => { box.Text = value; c.Set(JsonValue.Create(value)); }, box.Text?.Trim(), 300);
        var browse = new Button { Content = "…", Padding = new Thickness(6, 2), MinHeight = 26 };
        ToolTip.SetTip(browse, "Choose a picture file (copied next to the layout when it is somewhere else)");
        browse.Click += async (_, _) =>
        {
            if (c.Ctx.Actions.Owner?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose a picture", AllowMultiple = false,
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Pictures") { Patterns = ["*.svg", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] }],
            });
            if (files.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]) is not { } file) return;
            var relative = ImportPicture(c.Doc, file);
            box.Text = relative;
            c.Set(JsonValue.Create(relative));
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { thumb, builtin, browse } };
        var dock = new DockPanel();
        DockPanel.SetDock(row, Dock.Right);
        dock.Children.Add(row);
        dock.Children.Add(box);
        return dock;
    }

    /// <summary>Returns the path to store for a picture file: relative to the layout's folder, copying the file in beside the layout when it lies elsewhere.</summary>
    public static string ImportPicture(EditorDocument doc, string file)
    {
        var layoutDir = doc.FilePath is { } p ? Path.GetDirectoryName(Path.GetFullPath(p))! : doc.BaseDirectory;
        foreach (var root in new[] { layoutDir, doc.BaseDirectory, EditorSettings.ShippedLayoutsDirectory })
        {
            var relative = Path.GetRelativePath(root, file);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)) return relative.Replace('\\', '/');
        }
        var assets = Path.Combine(layoutDir, "assets");
        Directory.CreateDirectory(assets);
        var target = Path.Combine(assets, Path.GetFileName(file));
        if (!File.Exists(target)) File.Copy(file, target);
        return "assets/" + Path.GetFileName(file);
    }

    // ------------------------------------------------------------------ command and arguments

    private static Control CommandEditor(PropContext c)
    {
        var box = TextEditor(c, text =>
        {
            text = text.Trim();
            if (text.Length == 0) return (null, null);
            return EditorCatalog.Command(text) is null ? (null, $"There is no command '{text}'. Use the list to see what is available.") : (JsonValue.Create(text), null);
        }, "command name");
        var pick = new Button { Content = "Choose…", Padding = new Thickness(8, 2), MinHeight = 26 };
        pick.Click += (_, _) => Pickers.Show(pick, "Commands", EditorCatalog.Commands.Select(cmd => new PickItem(cmd.Id, cmd.Title + (cmd.Description.Length > 0 ? " — " + cmd.Description : ""), cmd.Category)).ToList(),
            value => { box.Text = value; c.Set(JsonValue.Create(value)); }, box.Text?.Trim(), 430);
        var dock = new DockPanel();
        DockPanel.SetDock(pick, Dock.Right);
        dock.Children.Add(pick);
        dock.Children.Add(box);
        var command = EditorCatalog.Command(Display(c.Current));
        if (command is null) return dock;
        var info = new TextBlock { Text = command.Title + (command.RequiresConnection ? "  ·  needs a connection" : ""), FontSize = 11, Foreground = Brush.Parse("#64748B"), TextWrapping = TextWrapping.Wrap };
        return new StackPanel { Spacing = 2, Children = { dock, info } };
    }

    private static Control ArgsEditor(PropContext c)
    {
        // The command whose parameters to offer: the sibling 'command' (or 'release' for shortcuts' releaseArgs).
        var ownerPath = JsonPath.Parent(c.ObjectPath + "." + c.Spec.Name) ?? "";
        var owner = JsonPath.ResolveObject(c.Doc.Data, ownerPath);
        var commandId = c.Spec.Name == "releaseArgs" ? owner?["release"]?.ToString() : owner?["command"]?.ToString();
        var command = EditorCatalog.Command(commandId);
        var args = c.Current as JsonObject ?? new JsonObject();
        var panel = new StackPanel { Spacing = 3 };
        var argsPath = ownerPath + "." + c.Spec.Name;

        void Set(string name, JsonNode? value) => c.Ctx.SetProperty(argsPath, name, value, $"Set {name}");

        var known = command?.Parameters ?? [];
        foreach (var parameter in known)
        {
            var spec = new PropSpec(parameter.Name, parameter.Kind switch { "bool" or "boolean" => PropKind.Bool, "number" or "int" or "double" => PropKind.Number, _ => PropKind.String }, parameter.Description);
            var editor = Create(new PropContext { Ctx = c.Ctx, Spec = spec, ObjectPath = argsPath, Current = args[parameter.Name], Commit = v => Set(parameter.Name, v), OwnerType = c.OwnerType });
            panel.Children.Add(FieldRow(parameter.Name + (parameter.Required ? " *" : ""), parameter.Description, editor, args.ContainsKey(parameter.Name)));
        }
        foreach (var (name, value) in args.Where(p => known.All(k => !k.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase))))
        {
            var spec = new PropSpec(name, PropKind.String, "Custom argument");
            var editor = TextEditor(new PropContext { Ctx = c.Ctx, Spec = spec, ObjectPath = argsPath, Current = value, Commit = v => Set(name, v) },
                text => (text.Length == 0 ? null : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : text is "true" or "false" ? JsonValue.Create(text == "true") : JsonValue.Create(text), null));
            panel.Children.Add(FieldRow(name, "Custom argument", editor, true));
        }
        if (known.Count == 0 && args.Count == 0)
            panel.Children.Add(new TextBlock { Text = command is null ? "Choose a command first." : "This command takes no arguments.", FontSize = 11, Foreground = Brushes.Gray });
        var add = new Button { Content = "Add argument…", Padding = new Thickness(8, 2), FontSize = 12 };
        add.Click += async (_, _) =>
        {
            if (c.Ctx.Actions.Owner is not { } window) return;
            var name = await Dialogs.PromptAsync(window, "Add argument", "Name of the argument:", "", "Add");
            if (!string.IsNullOrWhiteSpace(name)) Set(name.Trim(), JsonValue.Create(""));
        };
        panel.Children.Add(add);
        return panel;
    }

    public static Control FieldRow(string label, string tip, Control editor, bool isSet, Action? clear = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("104,*,Auto"), Margin = new Thickness(0, 1) };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = isSet ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(text, tip);
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
        if (clear is not null)
        {
            var x = new Button { Content = "✕", Padding = new Thickness(5, 1), FontSize = 10, Background = Brushes.Transparent, IsVisible = isSet, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(x, "Remove this setting");
            x.Click += (_, _) => clear();
            Grid.SetColumn(x, 2);
            grid.Children.Add(x);
        }
        return grid;
    }

    // ------------------------------------------------------------------ style, visuals, conditions

    private static Control VisualBlockField(PropContext c)
    {
        var path = c.ObjectPath + "." + c.Spec.Name;
        return new PropertyGrid(c.Ctx, path, ComponentCatalog.VisualProperties, c.OwnerType, compact: true);
    }
}

/// <summary>The per-state visuals of an element: one folded section for each state that has settings, and a way to add a state.</summary>
public sealed class VisualsEditor : StackPanel
{
    public VisualsEditor(PropContext c)
    {
        Spacing = 4;
        var path = c.ObjectPath + "." + c.Spec.Name;
        var current = c.Current as JsonObject ?? new JsonObject();
        foreach (var (state, _) in current)
        {
            var grid = new PropertyGrid(c.Ctx, path + "." + state, ComponentCatalog.VisualProperties, c.OwnerType, compact: true);
            var remove = new Button { Content = "Remove state", FontSize = 11, Padding = new Thickness(6, 1), Margin = new Thickness(0, 4, 0, 0) };
            var name = state;
            remove.Click += (_, _) => c.Ctx.Edit("Remove state", data =>
            {
                if (JsonPath.ResolveObject(data, path) is { } visuals) { visuals.Remove(name); if (visuals.Count == 0) JsonPath.ResolveObject(data, c.ObjectPath)?.Remove(c.Spec.Name); }
                return "";
            });
            grid.Children.Add(remove);
            Children.Add(new Collapsible(new TextBlock { Text = state, FontWeight = FontWeight.SemiBold }, grid, expanded: true));
        }

        var states = (c.OwnerType is not null && ComponentCatalog.TryGet(c.OwnerType, out var spec) ? spec.States : ComponentCatalog.BaseStates)
            .Where(s => !current.ContainsKey(s)).ToList();
        var add = new Button { Content = "＋ Add a state…", Padding = new Thickness(8, 2), FontSize = 12 };
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var state in states)
            {
                var item = new MenuItem { Header = state };
                item.Click += (_, _) => c.Ctx.Edit("Add state", data =>
                {
                    if (JsonPath.ResolveObject(data, c.ObjectPath) is not { } owner) return null;
                    var visuals = owner[c.Spec.Name] as JsonObject ?? (JsonObject)(owner[c.Spec.Name] = new JsonObject())!;
                    visuals[state] = new JsonObject { ["opacity"] = 1 };
                    return "";
                });
                menu.Items.Add(item);
            }
            menu.Open(add);
        };
        Children.Add(add);
    }
}

/// <summary>The conditional looks of an element: when an expression is true, its visual settings apply.</summary>
public sealed class ConditionsEditor : StackPanel
{
    public ConditionsEditor(PropContext c)
    {
        Spacing = 4;
        var path = c.ObjectPath + "." + c.Spec.Name;
        var array = c.Current as JsonArray ?? [];
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item) continue;
            var index = i;
            var itemPath = JsonPath.Index(path, i);
            var whenSpec = new PropSpec("when", PropKind.Expression, "Applies while this is true, e.g. machine.state == 'Run'.");
            var whenEditor = PropertyEditors.Create(new PropContext { Ctx = c.Ctx, Spec = whenSpec, ObjectPath = itemPath, Current = item["when"], Commit = v => c.Ctx.SetProperty(itemPath, "when", v), OwnerType = c.OwnerType });
            var visualPath = item["visual"] is JsonObject ? itemPath + ".visual" : itemPath;
            var body = new PropertyGrid(c.Ctx, visualPath, ComponentCatalog.VisualProperties, c.OwnerType, compact: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            Button Small(string text, Action click)
            {
                var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(6, 1) };
                b.Click += (_, _) => click();
                return b;
            }
            buttons.Children.Add(Small("Move up", () => c.Ctx.Edit("Move condition", data => Swap(data, path, index, -1))));
            buttons.Children.Add(Small("Move down", () => c.Ctx.Edit("Move condition", data => Swap(data, path, index, 1))));
            buttons.Children.Add(Small("Remove", () => c.Ctx.Edit("Remove condition", data =>
            {
                if (JsonPath.Resolve(data, path) is JsonArray list) { list.RemoveAt(index); if (list.Count == 0) JsonPath.ResolveObject(data, c.ObjectPath)?.Remove(c.Spec.Name); }
                return "";
            })));
            var stack = new StackPanel { Spacing = 3, Children = { PropertyEditors.FieldRow("when", whenSpec.Description, whenEditor, true), body, buttons } };
            Children.Add(new Collapsible(new TextBlock { Text = "when " + Truncate(item["when"]?.ToString() ?? "…", 34), FontWeight = FontWeight.SemiBold }, stack, expanded: true));
        }
        var add = new Button { Content = "＋ Add a condition", Padding = new Thickness(8, 2), FontSize = 12 };
        add.Click += (_, _) => c.Ctx.Edit("Add condition", data =>
        {
            if (JsonPath.ResolveObject(data, c.ObjectPath) is not { } owner) return null;
            var list = owner[c.Spec.Name] as JsonArray ?? (JsonArray)(owner[c.Spec.Name] = new JsonArray())!;
            list.Add(new JsonObject { ["when"] = "machine.state == 'Run'" });
            return "";
        });
        Children.Add(add);
    }

    private static string? Swap(JsonObject data, string path, int index, int delta)
    {
        if (JsonPath.Resolve(data, path) is not JsonArray list || index + delta < 0 || index + delta >= list.Count) return null;
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(index + delta, item);
        return "";
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}

/// <summary>The options of a choice: value, caption and picture for each.</summary>
public sealed class OptionsEditor : StackPanel
{
    public OptionsEditor(PropContext c)
    {
        Spacing = 4;
        c.Arm();
        var path = c.ObjectPath + "." + c.Spec.Name;
        var array = c.Current as JsonArray ?? [];
        for (var i = 0; i < array.Count; i++)
        {
            var index = i;
            var option = array[i];
            var obj = option as JsonObject;
            var value = obj is not null ? obj["value"] : option;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), Margin = new Thickness(0, 1) };

            TextBox Cell(string watermark, string? text, Action<string> changed, int column)
            {
                var box = new TextBox { Text = text ?? "", PlaceholderText = watermark, MinHeight = 26, Margin = new Thickness(0, 0, 3, 0) };
                Grid.SetColumn(box, column);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                timer.Tick += (_, _) => { timer.Stop(); changed(box.Text ?? ""); };
                box.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
                box.LostFocus += (_, _) => { if (timer.IsEnabled) { timer.Stop(); changed(box.Text ?? ""); } };
                return box;
            }

            void Write(string? newValue, string? newText, string? newImage)
            {
                if (!c.IsCurrent) return;
                c.Ctx.Edit("Edit option", data =>
                {
                    if (JsonPath.Resolve(data, path) is not JsonArray list || index >= list.Count) return null;
                    var oldObj = list[index] as JsonObject;
                    JsonNode? val = newValue is null ? null : double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d == Math.Floor(d) ? (long)d : d) : JsonValue.Create(newValue);
                    if (string.IsNullOrEmpty(newText) && string.IsNullOrEmpty(newImage) && (oldObj is null || oldObj.All(p => p.Key is "value" or "text" or "image")))
                        list[index] = val;
                    else
                    {
                        var replacement = oldObj is null ? new JsonObject() : (JsonObject)oldObj.DeepClone();
                        replacement["value"] = val;
                        if (string.IsNullOrEmpty(newText)) replacement.Remove("text"); else replacement["text"] = newText;
                        if (string.IsNullOrEmpty(newImage)) replacement.Remove("image"); else replacement["image"] = newImage;
                        list[index] = replacement;
                    }
                    return "";
                }, coalesce: path + index);
            }

            var valueText = value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : value?.ToJsonString();
            var textText = obj?["text"]?.ToString();
            var imageText = obj?["image"]?.ToString();
            row.Children.Add(Cell("value", valueText, v => Write(v, textText, imageText), 0));
            row.Children.Add(Cell("caption", textText, t => Write(valueText, t, imageText), 1));
            var remove = new Button { Content = "✕", Padding = new Thickness(5, 1), FontSize = 10, Background = Brushes.Transparent };
            remove.Click += (_, _) => c.Ctx.Edit("Remove option", data =>
            {
                if (JsonPath.Resolve(data, path) is JsonArray list) list.RemoveAt(index);
                return "";
            });
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            Children.Add(row);
        }
        var add = new Button { Content = "＋ Add an option", Padding = new Thickness(8, 2), FontSize = 12 };
        add.Click += (_, _) => c.Ctx.Edit("Add option", data =>
        {
            if (JsonPath.ResolveObject(data, c.ObjectPath) is not { } owner) return null;
            var list = owner[c.Spec.Name] as JsonArray ?? (JsonArray)(owner[c.Spec.Name] = new JsonArray())!;
            list.Add(JsonValue.Create("option " + (list.Count + 1)));
            return "";
        });
        Children.Add(add);
    }
}
