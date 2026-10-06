using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Carvera.App.Services;
using Carvera.Editor.Model;
using Carvera.Editor.Preview;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>The editor's window: menus and toolbar on top; the structure tree and control list on the left; the Visual and Code tabs on the right.</summary>
public sealed class EditorWindow : Window
{
    private readonly EditorContext _ctx;
    private readonly EditorSettings _settings;
    private readonly ControllerLink _link;
    private readonly PreviewServices _sim = new();
    private readonly PreviewSurface _preview;
    private readonly CodePanel _code;
    private readonly TreePanel _tree;
    private readonly PalettePanel _palette;
    private readonly Inspector _inspector;
    private readonly DiagnosticsPanel _diagnostics;
    private readonly TabControl _tabs = new();
    private readonly TabItem _visualTab;
    private readonly TabItem _codeTab;
    private readonly DragService _drag;
    private readonly TextBlock _saveState = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _controllerState = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _problems = new() { Padding = new Thickness(8, 1), FontSize = 12, Background = Brushes.Transparent };
    private readonly Border _diagnosticsBox;
    private readonly Border _conflictBar;
    private readonly ToggleButton _liveToggle = new() { Content = "Live", FontWeight = FontWeight.SemiBold, Padding = new Thickness(12, 4) };
    private readonly ToggleButton _pickToggle = new() { Content = "Pick in Controller", Padding = new Thickness(10, 4) };
    private readonly Button _switchToController = new() { Padding = new Thickness(8, 2), FontSize = 12, IsVisible = false };
    private readonly Button _showInController = new() { Content = "Show this in the Controller", Padding = new Thickness(8, 2), FontSize = 12, IsVisible = false };
    private readonly DispatcherTimer _messageTimer;
    private bool _followControllerAtStart = true;

    public EditorWindow(string[] args, EditorSettings? settings = null, ControllerLink? link = null, bool connect = true)
    {
        _settings = settings ?? EditorSettings.Load();
        _link = link ?? new ControllerLink();
        Title = "Carvera Layout Editor";
        Width = _settings.WindowWidth ?? 1500;
        Height = _settings.WindowHeight ?? 950;
        MinWidth = 1000;
        MinHeight = 640;

        var document = ChooseInitialDocument(args);
        _ctx = new EditorContext(document, _settings, _link);
        _ctx.Actions.Owner = this;

        var dragLayer = new Canvas { IsHitTestVisible = false };
        _drag = new DragService(this, dragLayer);
        _tree = new TreePanel(_ctx);
        _palette = new PalettePanel(_ctx);
        _preview = new PreviewSurface(_ctx, _sim);
        _inspector = new Inspector(_ctx);
        _code = new CodePanel(_ctx);
        _diagnostics = new DiagnosticsPanel(_ctx);
        _tree.UseDragService(_drag);
        _palette.UseDragService(_drag);
        _preview.UseDragService(_drag);

        _visualTab = new TabItem { Header = "Visual", Content = BuildVisual(), FontSize = 14, Padding = new Thickness(18, 6) };
        _codeTab = new TabItem { Header = "Code", Content = _code, FontSize = 14, Padding = new Thickness(18, 6) };
        _tabs.Items.Add(_visualTab);
        _tabs.Items.Add(_codeTab);

        _diagnosticsBox = new Border
        {
            Height = 150, IsVisible = false, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.White, Child = _diagnostics,
        };
        _conflictBar = BuildConflictBar();

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(_conflictBar, Dock.Top);
        root.Children.Add(_conflictBar);
        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        DockPanel.SetDock(_diagnosticsBox, Dock.Bottom);
        root.Children.Add(_diagnosticsBox);
        root.Children.Add(BuildMain());
        Content = new Panel { Children = { root, dragLayer } };

        _messageTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) => { _messageTimer!.Stop(); _message.Text = ""; });
        _ctx.Notice += (text, warning) =>
        {
            _message.Text = text;
            _message.Foreground = warning ? Brush.Parse("#B45309") : Brush.Parse("#334155");
            _messageTimer.Stop();
            _messageTimer.Start();
        };
        _ctx.RequestCodeAt = path =>
        {
            _tabs.SelectedItem = _codeTab;
            Dispatcher.UIThread.Post(() => _code.GoTo(path), DispatcherPriority.Loaded);
        };
        _ctx.SaveStateChanged += UpdateStatus;
        _ctx.DocumentChanged += _ => { UpdateStatus(); UpdateTitle(); };
        _ctx.DiskConflict += () => _conflictBar.IsVisible = true;
        _ctx.SelectionChanged += (_, _) => UpdateStatus();

        _link.StatusChanged += () => Dispatcher.UIThread.Post(OnControllerStatus);
        _link.Picked += (path, layout) => Dispatcher.UIThread.Post(() => OnPicked(path));
        if (connect) _link.Start();

        KeyDown += OnKeyDown;
        Closing += OnClosing;
        Opened += (_, _) => { UpdateStatus(); UpdateTitle(); OnControllerStatus(); };
        UpdateTitle();
        UpdateStatus();
    }

    public EditorContext Context => _ctx;
    public PreviewSurface Preview => _preview;
    public TabControl Tabs => _tabs;
    internal CodePanel Code => _code;
    internal DragService Drag => _drag;
    internal Inspector InspectorPanel => _inspector;
    internal TreePanel TreeView => _tree;

    // ------------------------------------------------------------------ opening

    private EditorDocument ChooseInitialDocument(string[] args)
    {
        var explicitLayout = ArgValue(args, "--layout");
        var explicitFile = args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (explicitFile is not null)
        {
            _followControllerAtStart = false;
            return new EditorDocument(File.ReadAllText(explicitFile), Path.GetFileNameWithoutExtension(explicitFile), Path.GetFullPath(explicitFile));
        }
        var library = EditorSettings.Library();
        var wanted = explicitLayout ?? _settings.LastLayout ?? "desktop2";
        if (explicitLayout is not null) _followControllerAtStart = false;
        var entry = library.FirstOrDefault(e => e.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? library.FirstOrDefault();
        if (entry is null) return new EditorDocument(BlankTemplate("layout"), "layout");
        var text = LiveFile.TryRead(entry.Path) ?? BlankTemplate(entry.Name);
        return entry.IsUser
            ? new EditorDocument(text, entry.Name, entry.Path)
            : new EditorDocument(text, entry.Name, Path.Combine(EditorSettings.UserLayoutsDirectory, Path.GetFileName(entry.Path)), Path.GetDirectoryName(entry.Path));
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string BlankTemplate(string name) => $$"""
        {
          "name": "{{name}}",
          "description": "",
          "root": {
            "type": "stack",
            "children": [
              {
                "type": "stack", "orientation": "horizontal", "height": "auto", "spacing": 8, "padding": 8,
                "children": [
                  { "type": "machineStatus", "width": 120 },
                  { "type": "button", "text": "Feed hold", "command": "pauseResume" },
                  { "type": "button", "text": "Stop", "command": "stop" },
                  { "type": "button", "text": "Reset", "command": "reset" }
                ]
              },
              { "type": "text", "text": "Drag controls from the list on the left into this layout.", "align": "center", "valign": "center" }
            ]
          }
        }
        """.Replace("\r\n", "\n") + "\n";

    private async Task NewLayoutAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var name = await Dialogs.PromptAsync(this, "New layout", "Name for the new layout (it becomes a file in your layouts folder):", "my-layout", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var path = Path.Combine(EditorSettings.UserLayoutsDirectory, name + ".json");
        if (File.Exists(path) && !await Dialogs.ConfirmAsync(this, "Replace layout?", $"There is already a layout named '{name}'. Replace it?", "Replace")) return;
        var document = new EditorDocument(BlankTemplate(name), name, path) { SavedText = "" };
        _ctx.Open(document);
        _followControllerAtStart = false;
    }

    private async Task OpenLibraryAsync(LayoutEntry entry)
    {
        if (!await ConfirmDiscardAsync()) return;
        _followControllerAtStart = false;
        _ctx.OpenLibrary(entry);
    }

    private async Task OpenFileAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a layout file", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Layouts") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0 || StorageProviderExtensions.TryGetLocalPath(files[0]) is not { } path) return;
        _followControllerAtStart = false;
        _ctx.OpenFile(path);
    }

    private async Task SaveAsAsync()
    {
        var suggested = _ctx.Doc.FilePath is { } p ? Path.GetFileName(p) : _ctx.Doc.Name + ".json";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save layout as", SuggestedFileName = suggested, DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Layouts") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(EditorSettings.UserLayoutsDirectory),
        });
        if (file is null || StorageProviderExtensions.TryGetLocalPath(file) is not { } path) return;
        _ctx.SaveAs(path);
    }

    /// <summary>Asks what to do with edits that are not in a file (the layout is invalid, or has no file).</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        _ctx.SaveNow();
        if (!_ctx.Doc.IsDirty) return true;
        return await Dialogs.ConfirmAsync(this, "Unsaved edits", "The layout has edits that could not be saved (it has errors, or has no file yet). Discard them?", "Discard");
    }

    // ------------------------------------------------------------------ layout of the window

    private Control BuildMain()
    {
        var left = new Grid { RowDefinitions = new RowDefinitions("*,5,*") };
        var treeBox = Panel("Structure", _tree);
        var paletteBox = Panel("Controls", _palette);
        var split = new GridSplitter { Background = Brush.Parse("#E2E8F0"), ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 1);
        Grid.SetRow(paletteBox, 2);
        left.Children.Add(treeBox);
        left.Children.Add(split);
        left.Children.Add(paletteBox);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("300,5,*") };
        var vertical = new GridSplitter { Background = Brush.Parse("#E2E8F0"), ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(vertical, 1);
        Grid.SetColumn(_tabs, 2);
        main.Children.Add(left);
        main.Children.Add(vertical);
        main.Children.Add(_tabs);
        return main;
    }

    private Control BuildVisual()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,340") };
        var split = new GridSplitter { Background = Brush.Parse("#E2E8F0"), ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        var inspector = new Border { Child = _inspector, Background = Brushes.White };
        Grid.SetColumn(inspector, 2);
        grid.Children.Add(_preview);
        grid.Children.Add(split);
        grid.Children.Add(inspector);
        return grid;
    }

    private static Control Panel(string title, Control body)
    {
        var header = new Border
        {
            Background = Brush.Parse("#F1F5F9"), Padding = new Thickness(10, 5), BorderBrush = Brush.Parse("#E2E8F0"), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#475569") },
        };
        DockPanel.SetDock(header, Dock.Top);
        return new DockPanel { Background = Brushes.White, Children = { header, body } };
    }

    private Control BuildToolbar()
    {
        Button Tool(string text, string tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        _liveToggle.IsChecked = _settings.Live;
        _liveToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.Live = _liveToggle.IsChecked == true;
            if (_settings.Live) _ctx.SaveNow();
            UpdateStatus();
        };
        ToolTip.SetTip(_liveToggle, "On: every valid edit is written to the layout file at once, and the Controller shows it within a moment. Off: nothing is written until you save (Ctrl+S).");
        _pickToggle.IsCheckedChanged += (_, _) => _link.SetPicking(_pickToggle.IsChecked == true);
        ToolTip.SetTip(_pickToggle, "Then click an element in the Controller's window to select it here. Esc in the Controller stops.");
        _switchToController.Click += (_, _) =>
        {
            if (_link.Layout is { } name && EditorSettings.Library().FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } entry) _ = OpenLibraryAsync(entry);
        };

        _showInController.Click += (_, _) =>
        {
            var doc = _ctx.Doc;
            _link.Load(doc.FilePath is { } file && File.Exists(file) ? file : doc.Name);
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 6),
            Children =
            {
                Tool("↶ Undo", "Undo (Ctrl+Z)", _ctx.Undo), Tool("↷ Redo", "Redo (Ctrl+Y)", _ctx.Redo),
                new Border { Width = 1, Background = Brush.Parse("#CBD5E1"), Margin = new Thickness(4, 2) },
                _liveToggle, _saveState,
            },
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _controllerState, _switchToController, _showInController, _pickToggle } };
        var dock = new DockPanel { Children = { } };
        DockPanel.SetDock(right, Dock.Right);
        right.Margin = new Thickness(10, 6);
        dock.Children.Add(right);
        dock.Children.Add(row);
        return new Border { Child = dock, Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private Control BuildStatusBar()
    {
        _problems.Click += (_, _) => _diagnosticsBox.IsVisible = !_diagnosticsBox.IsVisible;
        var dock = new DockPanel { Margin = new Thickness(10, 3) };
        DockPanel.SetDock(_problems, Dock.Right);
        dock.Children.Add(_problems);
        dock.Children.Add(_message);
        return new Border { Child = dock, Background = Brush.Parse("#F1F5F9"), BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private Border BuildConflictBar()
    {
        var reload = new Button { Content = "Load the file's version" };
        var keep = new Button { Content = "Keep my edits" };
        var bar = new Border
        {
            Background = Brush.Parse("#FEF3C7"), Padding = new Thickness(12, 6), IsVisible = false, BorderBrush = Brush.Parse("#F59E0B"), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { LastChildFill = false },
        };
        var dock = (DockPanel)bar.Child;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { reload, keep } };
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(new TextBlock { Text = "The layout file was changed outside the editor while you have unsaved edits.", VerticalAlignment = VerticalAlignment.Center });
        reload.Click += (_, _) => { _ctx.ReloadFromDisk(); bar.IsVisible = false; };
        keep.Click += (_, _) => bar.IsVisible = false;
        return bar;
    }

    // ------------------------------------------------------------------ menu

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action click, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => click();
            return item;
        }
        var a = _ctx.Actions;

        var open = new MenuItem { Header = "Open layout" };
        open.SubmenuOpened += (_, _) =>
        {
            open.Items.Clear();
            foreach (var entry in EditorSettings.Library())
                open.Items.Add(Item(entry.Name + (entry.IsUser ? "  (yours)" : ""), () => _ = OpenLibraryAsync(entry)));
            if (open.Items.Count == 0) open.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        };
        open.Items.Add(new MenuItem { Header = "…" });

        var file = new MenuItem { Header = "_File" };
        file.Items.Add(Item("New layout…", () => _ = NewLayoutAsync()));
        file.Items.Add(open);
        file.Items.Add(Item("Open file…", () => _ = OpenFileAsync(), "Ctrl+O"));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Save", () => { if (!_ctx.SaveNow(force: true)) _ = SaveIfNoFileAsync(); }, "Ctrl+S"));
        file.Items.Add(Item("Save as…", () => _ = SaveAsAsync(), "Ctrl+Shift+S"));
        var revert = Item("Discard my copy of this built-in layout", () => _ = RevertAsync());
        file.Items.Add(revert);
        file.SubmenuOpened += (_, _) => revert.IsEnabled = _ctx.HasCustomisedCopy;
        file.Items.Add(new Separator());
        file.Items.Add(Item("Exit", Close));

        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(Item("Undo", _ctx.Undo, "Ctrl+Z"));
        edit.Items.Add(Item("Redo", _ctx.Redo, "Ctrl+Y"));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Cut", () => _ = a.CutAsync(), "Ctrl+X"));
        edit.Items.Add(Item("Copy", () => _ = a.CopyAsync(), "Ctrl+C"));
        edit.Items.Add(Item("Paste", () => _ = a.PasteAsync(), "Ctrl+V"));
        edit.Items.Add(Item("Duplicate", () => a.Duplicate(), "Ctrl+D"));
        edit.Items.Add(Item("Delete", () => a.Delete(), "Delete"));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Move up", () => a.MoveBy(-1), "Alt+Up"));
        edit.Items.Add(Item("Move down", () => a.MoveBy(1), "Alt+Down"));
        edit.Items.Add(Item("Select the container", SelectParent, "Escape"));
        var wrap = new MenuItem { Header = "Wrap in" };
        foreach (var type in new[] { "stack", "panel", "scroll", "grid", "split", "tabs" }) wrap.Items.Add(Item(type, () => a.WrapIn(type)));
        edit.Items.Add(wrap);
        edit.Items.Add(Item("Unwrap", () => a.Unwrap()));
        edit.Items.Add(Item("Make a region…", () => _ = a.ExtractRegionAsync()));

        var insert = new MenuItem { Header = "_Insert" };
        foreach (var (group, types) in NewElements.Groups)
        {
            var sub = new MenuItem { Header = group };
            foreach (var type in types) sub.Items.Add(Item(type, () => a.Add(type)));
            insert.Items.Add(sub);
        }
        insert.Items.Add(new Separator());
        insert.Items.Add(Item("New region…", () => _ = a.AddRegionAsync()));

        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("Visual tab", () => _tabs.SelectedItem = _visualTab, "Ctrl+1"));
        view.Items.Add(Item("Code tab", () => _tabs.SelectedItem = _codeTab, "Ctrl+2"));
        view.Items.Add(Item("Problems", () => _diagnosticsBox.IsVisible = !_diagnosticsBox.IsVisible, "Ctrl+Shift+M"));

        var controller = new MenuItem { Header = "_Controller" };
        var follow = new MenuItem { Header = "Show the selection in the Controller", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.FollowInController };
        follow.Click += (_, _) => _settings.FollowInController = follow.IsChecked;
        controller.Items.Add(follow);
        controller.Items.Add(Item("Show the selected element now", () => { if (_ctx.Selection is { } s && JsonPath.IsElementPath(s)) _link.Reveal(s); }, "F5"));
        controller.Items.Add(Item("Pick an element in the Controller", () => _pickToggle.IsChecked = !(_pickToggle.IsChecked ?? false)));

        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(Item("How this works", () => _ = ShowHelpAsync()));

        return new Menu { Items = { file, edit, insert, view, controller, help } };
    }

    private async Task SaveIfNoFileAsync()
    {
        if (_ctx.Doc.FilePath is null) await SaveAsAsync();
        else if (!_ctx.Doc.IsValid) _ctx.Notify("Not saved: the layout has errors. The Controller keeps the last good version.", warning: true);
    }

    private async Task RevertAsync()
    {
        if (await Dialogs.ConfirmAsync(this, "Discard your copy?", "Your customised copy of this built-in layout will be deleted and the Controller goes back to the built-in one.", "Discard my copy"))
            _ctx.RevertToBuiltIn();
    }

    private async Task ShowHelpAsync()
    {
        await Dialogs.AskAsync(this, "How this works",
            "Every change you make is checked the way the Controller checks a layout and, when it is valid, written to the layout file (Live). The Controller notices the file and shows the new layout within a moment; it keeps its tab, scroll position and splitters. A change with errors is held back, and the Controller keeps the last good layout.\n\n" +
            "• Click an element in the preview to select it; drag it to move it; drag the handles to resize; Alt+click selects the container.\n" +
            "• Drag controls from the list into the preview or the tree, or double-click to add them to the selection.\n" +
            "• The Code tab is the same layout as text, with completion (Ctrl+Space) and error marks.\n" +
            "• 'Pick in Controller' lets you click an element in the Controller's window to select it here.\n" +
            "• Built-in layouts are edited as a copy in your layouts folder, which the Controller then prefers.\n" +
            "• The preview never operates the machine: nothing in it can be clicked.", "OK");
    }

    // ------------------------------------------------------------------ keys

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var typing = FocusManager?.GetFocusedElement() is TextBox or AvaloniaEdit.Editing.TextArea;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var a = _ctx.Actions;
        bool Handle(Action action) { e.Handled = true; action(); return true; }

        if (ctrl && e.Key == Key.S) { if (shift) _ = SaveAsAsync(); else if (!_ctx.SaveNow(force: true)) _ = SaveIfNoFileAsync(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { Handle(() => _ = OpenFileAsync()); return; }
        if (ctrl && e.Key == Key.D1) { Handle(() => _tabs.SelectedItem = _visualTab); return; }
        if (ctrl && e.Key == Key.D2) { Handle(() => _tabs.SelectedItem = _codeTab); return; }
        if (ctrl && shift && e.Key == Key.M) { Handle(() => _diagnosticsBox.IsVisible = !_diagnosticsBox.IsVisible); return; }
        if (e.Key == Key.F5) { Handle(() => { if (_ctx.Selection is { } s && JsonPath.IsElementPath(s)) _link.Reveal(s); }); return; }
        if (typing) return;
        if (ctrl && e.Key == Key.Z) { Handle(_ctx.Undo); return; }
        if (ctrl && e.Key == Key.Y) { Handle(_ctx.Redo); return; }
        if (!_tabs.SelectedItem?.Equals(_visualTab) ?? false) return;
        if (ctrl && e.Key == Key.C) Handle(() => _ = a.CopyAsync());
        else if (ctrl && e.Key == Key.X) Handle(() => _ = a.CutAsync());
        else if (ctrl && e.Key == Key.V) Handle(() => _ = a.PasteAsync());
        else if (ctrl && e.Key == Key.D) Handle(() => a.Duplicate());
        else if (e.Key == Key.Delete) Handle(() => a.Delete());
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Up) Handle(() => a.MoveBy(-1));
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Down) Handle(() => a.MoveBy(1));
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) Handle(SelectParent);
    }

    private void SelectParent()
    {
        if (_ctx.Selection is { } s && _ctx.Doc.Data is not null && ElementOps.ParentOf(_ctx.Doc.Data, s) is { } p) _ctx.Select(p.ParentPath);
    }

    // ------------------------------------------------------------------ status

    private void UpdateTitle()
    {
        var doc = _ctx.Doc;
        Title = $"{doc.Name}{(doc.IsDirty ? " •" : "")} — Carvera Layout Editor";
    }

    private void UpdateStatus()
    {
        var doc = _ctx.Doc;
        string text;
        IBrush brush;
        if (doc.FilePath is null) { text = "Not saved to a file yet — use File ▸ Save as"; brush = Brush.Parse("#B45309"); }
        else if (!doc.IsValid) { text = "Has errors — the Controller keeps its last good layout"; brush = PropertyEditors.ErrorBrush; }
        else if (_ctx.LastSaveError is { } error) { text = "Could not save: " + error; brush = PropertyEditors.ErrorBrush; }
        else if (!_settings.Live && doc.IsDirty) { text = "Unsaved changes (Live is off) — Ctrl+S"; brush = Brush.Parse("#B45309"); }
        else if (doc.IsDirty) { text = "Saving…"; brush = Brushes.Gray; }
        else if (_ctx.BuiltInSource is not null) { text = $"Editing a copy of the built-in {doc.Name}; your first change saves it to your layouts folder"; brush = Brush.Parse("#475569"); }
        else { text = _ctx.LastSaved is { } t ? $"Saved {t:HH:mm:ss} — {Path.GetFileName(doc.FilePath)}" : $"{Path.GetFileName(doc.FilePath)}"; brush = Brush.Parse("#15803D"); }
        _saveState.Text = text;
        _saveState.Foreground = brush;

        var errors = doc.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) + (doc.ParseError is null ? 0 : 1);
        var warnings = doc.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        _problems.Content = errors + warnings == 0 ? "✓ No problems" : $"{(errors > 0 ? "✕ " + errors + " error" + (errors == 1 ? "" : "s") + "  " : "")}{(warnings > 0 ? "⚠ " + warnings + " warning" + (warnings == 1 ? "" : "s") : "")}";
        _problems.Foreground = errors > 0 ? PropertyEditors.ErrorBrush : warnings > 0 ? Brush.Parse("#B45309") : Brush.Parse("#15803D");
    }

    private void OnControllerStatus()
    {
        if (!_link.Connected)
        {
            _controllerState.Text = "○ Controller not running";
            _controllerState.Foreground = Brushes.Gray;
            _switchToController.IsVisible = false;
            _showInController.IsVisible = false;
            _pickToggle.IsEnabled = false;
            _pickToggle.IsChecked = false;
            return;
        }
        _pickToggle.IsEnabled = true;
        if (_pickToggle.IsChecked != _link.Picking) _pickToggle.IsChecked = _link.Picking;
        var showing = _link.Layout ?? "?";
        var same = showing.Equals(_ctx.Doc.Name, StringComparison.OrdinalIgnoreCase) || (_ctx.Doc.FilePath is { } f && Path.GetFileNameWithoutExtension(f).Equals(showing, StringComparison.OrdinalIgnoreCase));
        _controllerState.Text = same ? $"● Controller connected — showing this layout" : $"● Controller shows “{showing}”";
        _controllerState.Foreground = same ? Brush.Parse("#15803D") : Brush.Parse("#B45309");
        _switchToController.IsVisible = !same;
        _showInController.IsVisible = !same;
        _switchToController.Content = $"Edit “{showing}”";

        // Started without saying which layout: take the one the Controller has open.
        if (_followControllerAtStart && !same && _link.Layout is { } name && !_ctx.Doc.IsDirty)
        {
            _followControllerAtStart = false;
            if (EditorSettings.Library().FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } entry) _ctx.OpenLibrary(entry);
        }
    }

    private void OnPicked(string path)
    {
        _followControllerAtStart = false;
        _tabs.SelectedItem = _visualTab;
        var target = _ctx.Doc.Data is not null && JsonPath.Resolve(_ctx.Doc.Data, path) is not null ? path : JsonPath.ElementPrefix(path) ?? "root";
        _ctx.Select(target, this);
        _ctx.Notify("Picked in the Controller: " + target);
        Activate();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed) return;
        _ctx.SaveNow();
        if (_ctx.Doc.IsDirty)
        {
            e.Cancel = true;
            if (!await Dialogs.ConfirmAsync(this, "Unsaved edits", "The layout has edits that are not in a file (it has errors, or has no file yet). Close and lose them?", "Close anyway")) return;
        }
        _closingConfirmed = true;
        _settings.WindowWidth = Bounds.Width;
        _settings.WindowHeight = Bounds.Height;
        _settings.LastLayout = _ctx.Doc.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : _ctx.Doc.Name;
        _settings.Save();
        _link.SetPicking(false);
        _link.Dispose();
        _ctx.Dispose();
        _sim.Dispose();
        Close();
    }

    private bool _closingConfirmed;
}
