using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Layout;
using Carvera.App.Shell;
using Carvera.Editor.Model;
using Carvera.Editor.Views;
using Carvera.Layout;

namespace Carvera.Editor.Preview;

/// <summary>
/// The visual editing surface: the layout built with the same code the Controller uses, shown in a frame like the Controller's window,
/// with nothing in it operable. Clicking selects, dragging moves, the handles resize, and palette items can be dropped onto it.
/// </summary>
public sealed class PreviewSurface : Grid, IDropTarget
{
    private const string CustomName = "Custom size…";

    private static readonly (string Name, double W, double H)[] Sizes =
    [
        ("Fill the pane", 0, 0), ("1920 × 1080 (Full HD)", 1920, 1080), ("2560 × 1440", 2560, 1440), ("1600 × 900", 1600, 900), ("1400 × 900", 1400, 900), ("1280 × 800", 1280, 800),
        ("1024 × 768", 1024, 768), ("1024 × 600 (touch)", 1024, 600), ("800 × 480 (small)", 800, 480),
    ];

    private static readonly string[] ZoomChoices = ["Fit", "25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%"];

    private ComboBox _sizeBox = null!, _zoomBox = null!;
    private Button _zoomIn = null!, _zoomOut = null!;
    private (double W, double H)? _custom;

    private readonly EditorContext _ctx;
    private readonly PreviewServices _sim;
    private readonly Border _stage = new() { Background = Brush.Parse("#CBD5E1"), Padding = new Thickness(0) };
    private readonly Grid _frame = new();
    private readonly Border _layoutHost = new() { IsHitTestVisible = false };
    private readonly PreviewOverlay _overlay = new() { Focusable = true, Cursor = new Cursor(StandardCursorType.Arrow) };
    private readonly StackPanel _breadcrumb = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _banner = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#991B1B") };
    private readonly Border _bannerBox;
    private readonly Border _hintBox = new()
    {
        Background = Brush.Parse("#E0F2FE"), BorderBrush = Brush.Parse("#7DD3FC"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 6),
        IsVisible = false, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14), IsHitTestVisible = false,
    };
    private readonly TextBlock _hint = new() { Foreground = Brush.Parse("#075985"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { Text = "Nothing to show", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
    private readonly DispatcherTimer _rebuildTimer;
    private LayoutSession? _session;
    private LayoutDocument? _built;
    private bool _forgetViewState;
    private string? _pressedPath;
    private Point _pressPoint;
    private Point _grab;
    private PreviewOverlay.Handle _resizing;
    private Rect _resizeStart;
    private Point _resizeOrigin;
    private double? _newWidth, _newHeight;
    private ActionsDropCache? _lastDrop;
    private DragService? _drag;

    private sealed record ActionsDropCache(EditorActions.DropLocation Where);

    public PreviewSurface(EditorContext ctx, PreviewServices simulation)
    {
        _ctx = ctx;
        _sim = simulation;
        RowDefinitions = new RowDefinitions("Auto,*");

        _bannerBox = new Border
        {
            Background = Brush.Parse("#FEE2E2"), BorderBrush = Brush.Parse("#FCA5A5"), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 6), IsVisible = false, Child = _banner, VerticalAlignment = VerticalAlignment.Top,
        };

        Children.Add(BuildToolbar());
        _hintBox.Child = _hint;
        var stageGrid = new Grid { Children = { _stage, _bannerBox, _hintBox } };
        SetRow(stageGrid, 1);
        Children.Add(stageGrid);

        // The frame copies the Controller's window: the layout, then a slim column that holds its settings button.
        _frame.ColumnDefinitions = new ColumnDefinitions("*,44");
        var strip = new Border { Width = 44, Background = Brush.Parse("#E2E8F0"), Child = new TextBlock { Text = "⚙", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Foreground = Brushes.Gray } };
        SetColumn(strip, 1);
        var layoutCell = new Grid { Children = { _layoutHost, _empty, _overlay } };
        _frame.Children.Add(layoutCell);
        _frame.Children.Add(strip);
        _stage.PointerWheelChanged += OnStageWheel;
        ApplyView();

        _rebuildTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => { _rebuildTimer!.Stop(); Rebuild(); });
        ctx.DocumentChanged += origin =>
        {
            // Another layout was opened: the tabs and scroll positions of the last one mean nothing here.
            if (origin == ChangeOrigin.Load) _forgetViewState = true;
            _rebuildTimer.Start();
        };
        ctx.SelectionChanged += OnSelectionChanged;
        _frame.SizeChanged += (_, _) => Dispatcher.UIThread.Post(RefreshOverlay, DispatcherPriority.Loaded);
        _overlay.PointerPressed += OnPressed;
        _overlay.PointerMoved += OnMoved;
        _overlay.PointerReleased += OnReleased;
        _overlay.PointerExited += (_, _) => { _overlay.Hover = null; _overlay.Invalidate(); };
        Rebuild();
    }

    internal EditorActions.DropLocation? PendingDrop => _lastDrop?.Where;
    public LayoutSession? Session => _session;
    public PreviewOverlay Overlay => _overlay;
    public bool ShowStructure { get; private set; }

    /// <summary>Lets the surface start drags of existing elements; called by the main window once the drag service exists.</summary>
    public void UseDragService(DragService drag)
    {
        _drag = drag;
        drag.Register(this);
        drag.Attach(_overlay, StartDrag);
    }

    // ------------------------------------------------------------------ toolbar

    private Control BuildToolbar()
    {
        _custom = ParseCustom(_ctx.Settings.PreviewSize);
        _sizeBox = new ComboBox { MinWidth = 170 };
        FillSizeList();
        _sizeBox.SelectionChanged += async (_, _) =>
        {
            if (_syncingToolbar || _sizeBox.SelectedItem is not string name) return;
            if (name == CustomName)
            {
                if (_ctx.Actions.Owner is not { } owner) return;
                var text = await Dialogs.PromptAsync(owner, "Custom preview size", "Width × height in pixels, e.g. 1920x1080:", _custom is { } c ? $"{c.W}x{c.H}" : "1920x1080", "Use");
                if (text is null || ParseCustom("custom:" + text.Trim()) is not { } parsed) { FillSizeList(); return; }
                _custom = parsed;
                _ctx.Settings.PreviewSize = $"custom:{parsed.W}x{parsed.H}";
            }
            else if (name.StartsWith("Custom:", StringComparison.Ordinal)) _ctx.Settings.PreviewSize = $"custom:{_custom!.Value.W}x{_custom.Value.H}";
            else _ctx.Settings.PreviewSize = name;
            FillSizeList();
            ApplyView();
        };
        ToolTip.SetTip(_sizeBox, "The screen size the layout is drawn for. Use a size that matches the Controller's screen to see how the layout will really fit.");

        _zoomBox = new ComboBox { ItemsSource = ZoomChoices, SelectedItem = ZoomChoices.Contains(_ctx.Settings.PreviewZoom) ? _ctx.Settings.PreviewZoom : "Fit", MinWidth = 80 };
        _zoomBox.SelectionChanged += (_, _) =>
        {
            if (_syncingToolbar || _zoomBox.SelectedItem is not string z) return;
            _ctx.Settings.PreviewZoom = z;
            ApplyView();
        };
        ToolTip.SetTip(_zoomBox, "Zoom the preview (Ctrl + mouse wheel also zooms). Fit shrinks or grows it to fill the pane.");
        _zoomOut = new Button { Content = "−", Padding = new Thickness(8, 2), MinHeight = 30 };
        _zoomIn = new Button { Content = "+", Padding = new Thickness(8, 2), MinHeight = 30 };
        _zoomOut.Click += (_, _) => StepZoom(-1);
        _zoomIn.Click += (_, _) => StepZoom(1);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _zoomOut, _zoomBox, _zoomIn } };

        var structure = new ToggleButton { Content = "Show structure", IsChecked = _ctx.Settings.ShowStructure };
        ShowStructure = _ctx.Settings.ShowStructure;
        structure.IsCheckedChanged += (_, _) =>
        {
            ShowStructure = structure.IsChecked == true;
            _ctx.Settings.ShowStructure = ShowStructure;
            RefreshOverlay();
        };
        ToolTip.SetTip(structure, "Outline every container, including empty space and containers that draw nothing themselves.");

        var simulate = new Button { Content = "Simulate ▾" };
        simulate.Flyout = BuildSimulationFlyout();
        ToolTip.SetTip(simulate, "Preview the layout in another machine state (running, alarm, disconnected...) or with a G-code file loaded. Nothing is sent anywhere.");

        var bar = new DockPanel { Margin = new Thickness(8, 6), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _sizeBox, zoomRow, structure, simulate } };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _breadcrumb, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Child = bar, BorderBrush = Brush.Parse("#E2E8F0"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.White };
    }

    private Flyout BuildSimulationFlyout()
    {
        var situation = new ComboBox { ItemsSource = PreviewServices.Situations.Select(s => s.Name).ToList(), SelectedIndex = 1, MinWidth = 220 };
        var program = new CheckBox { Content = "Show a sample G-code file" };
        var extra = new TextBox
        {
            AcceptsReturn = true, Height = 90, PlaceholderText = "job.percent = 80\nmachine.state = Run\nanything.you.bind = true",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 12,
        };
        var problems = new TextBlock { Foreground = Brush.Parse("#B91C1C"), FontSize = 12, TextWrapping = TextWrapping.Wrap };

        void Apply()
        {
            var values = PreviewServices.ParseOverrides(extra.Text ?? "", out var bad);
            problems.Text = string.Join("\n", bad);
            _ = ApplyAsync(PreviewServices.Situations[Math.Max(0, situation.SelectedIndex)], values, program.IsChecked == true);
        }
        situation.SelectionChanged += (_, _) => Apply();
        program.IsCheckedChanged += (_, _) => Apply();
        extra.LostFocus += (_, _) => Apply();

        return new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new StackPanel
            {
                Spacing = 8, Width = 300,
                Children =
                {
                    new TextBlock { Text = "Pretend the machine is:", FontWeight = FontWeight.SemiBold },
                    situation, program,
                    new TextBlock { Text = "Extra state values (path = value):", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                    extra, problems,
                    new TextBlock { Text = "Only the preview changes; the layout file is not touched.", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
    }

    private async Task ApplyAsync(SimulatedSituation situation, Dictionary<string, object?> values, bool sample)
    {
        await _sim.ApplyAsync(situation, values);
        Dispatcher.UIThread.Post(() => _sim.SetSampleProgram(sample));
    }

    private bool _syncingToolbar;
    private double _currentScale = 1;

    private static (double W, double H)? ParseCustom(string saved)
    {
        if (!saved.StartsWith("custom:", StringComparison.Ordinal)) return null;
        var parts = saved[7..].ToLowerInvariant().Replace("×", "x").Split('x', StringSplitOptions.TrimEntries);
        var style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return parts.Length == 2 && double.TryParse(parts[0], style, culture, out var w) && double.TryParse(parts[1], style, culture, out var h)
            && w >= 200 && h >= 150 && w <= 10000 && h <= 10000 ? (w, h) : null;
    }

    private string CustomLabel => _custom is { } c ? $"Custom: {c.W} × {c.H}" : CustomName;

    private void FillSizeList()
    {
        _syncingToolbar = true;
        var items = Sizes.Select(x => x.Name).ToList();
        if (_custom is not null) items.Add(CustomLabel);
        items.Add(CustomName);
        _sizeBox.ItemsSource = items;
        var saved = _ctx.Settings.PreviewSize;
        _sizeBox.SelectedItem = _custom is not null && saved.StartsWith("custom:", StringComparison.Ordinal) ? CustomLabel : Sizes.Any(x => x.Name == saved) ? saved : Sizes[0].Name;
        _syncingToolbar = false;
    }

    private (double W, double H) CurrentSize()
    {
        var saved = _ctx.Settings.PreviewSize;
        if (ParseCustom(saved) is { } custom) return custom;
        var found = Sizes.FirstOrDefault(x => x.Name == saved);
        return found.Name is null ? (0, 0) : (found.W, found.H);
    }

    private double? ZoomFactor() =>
        _ctx.Settings.PreviewZoom.EndsWith('%') && double.TryParse(_ctx.Settings.PreviewZoom.TrimEnd('%'), out var p) ? p / 100 : null;

    private void StepZoom(int direction)
    {
        var levels = ZoomChoices.Skip(1).Select(z => double.Parse(z.TrimEnd('%')) / 100).ToList();
        var current = ZoomFactor() ?? Math.Max(0.1, _currentScale);
        var next = direction > 0 ? levels.FirstOrDefault(l => l > current + 0.001, levels[^1]) : levels.LastOrDefault(l => l < current - 0.001, levels[0]);
        var label = $"{Math.Round(next * 100)}%";
        _ctx.Settings.PreviewZoom = label;
        _syncingToolbar = true;
        _zoomBox.SelectedItem = label;
        _syncingToolbar = false;
        ApplyView();
    }

    /// <summary>Lays the frame out at the chosen screen size and zoom: Fit scales it to the pane; a percentage scales it and scrolls.</summary>
    private void ApplyView()
    {
        var (w, h) = CurrentSize();
        // The frame still sits in the container of the previous view; a control can have only one parent.
        if (_frame.Parent is Viewbox oldBox) oldBox.Child = null;
        else if (_frame.Parent is Decorator oldDecorator) oldDecorator.Child = null;
        _stage.Child = null;
        _zoomBox.IsEnabled = _zoomIn.IsEnabled = _zoomOut.IsEnabled = w > 0;
        if (w == 0)
        {
            _frame.Width = double.NaN;
            _frame.Height = double.NaN;
            _currentScale = 1;
            _stage.Child = _frame;
        }
        else
        {
            _frame.Width = w;
            _frame.Height = h;
            if (ZoomFactor() is { } zoom)
            {
                _currentScale = zoom;
                var scaled = new LayoutTransformControl { LayoutTransform = new ScaleTransform(zoom, zoom), Child = _frame, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
                _stage.Child = new ScrollViewer { Content = scaled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }
            else
            {
                var box = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Margin = new Thickness(16), Child = _frame, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                box.SizeChanged += (_, _) => _currentScale = box.Bounds.Width > 0 ? Math.Min(box.Bounds.Width / w, box.Bounds.Height / h) : 1;
                _stage.Child = box;
            }
        }
        Dispatcher.UIThread.Post(RefreshOverlay, DispatcherPriority.Loaded);
    }

    private void OnStageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || CurrentSize().W == 0) return;
        StepZoom(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ building

    private void Rebuild()
    {
        var doc = _ctx.Doc;
        var good = doc.GoodDocument;

        if (doc.IsValid) _bannerBox.IsVisible = false;
        else
        {
            var first = doc.ParseError ?? doc.Result.Errors.FirstOrDefault()?.ToString() ?? "unknown error";
            _banner.Text = "Not applied — the Controller keeps its last layout. " + first;
            _bannerBox.IsVisible = true;
        }

        if (good is null)
        {
            _empty.IsVisible = true;
            return;
        }
        _empty.IsVisible = false;
        // Same layout as before (only broken text changed since): nothing to rebuild.
        if (ReferenceEquals(good, _built) && _session is not null)
        {
            RefreshOverlay();
            return;
        }

        var viewState = _session is not null && !_forgetViewState ? ViewState.Capture(_session) : null;
        _forgetViewState = false;
        _session?.Dispose();
        _layoutHost.Child = null;
        _session = new LayoutSession(good, _sim.Services);
        _built = good;
        var theme = _session.Context.Theme;
        _layoutHost.Child = _session.Root;
        _layoutHost.Background = theme.TokenBrush("background");
        _layoutHost.SetValue(TextElement.FontFamilyProperty, theme.FontFamily);
        _layoutHost.SetValue(TextElement.FontSizeProperty, theme.FontSize);
        _layoutHost.SetValue(TextElement.ForegroundProperty, theme.TokenBrush("text"));
        _frame.Background = theme.TokenBrush("background");

        _layoutHost.UpdateLayout();
        viewState?.Restore(_session);
        if (_ctx.Selection is { } selection && JsonPath.IsElementPath(selection)) LayoutInspector.Reveal(_session, selection);
        Dispatcher.UIThread.Post(() => { _sim.Services.Binder.Flush(); RefreshOverlay(); }, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ overlay

    /// <summary>Finds the host for a selection path: the element itself, or (for a region) its first use.</summary>
    private ComponentHost? HostFor(string? path)
    {
        if (_session is null || path is null) return null;
        var exact = _session.Hosts.FirstOrDefault(h => h.Node.Path == path);
        if (exact is not null) return exact;
        if (path.StartsWith("regions.", StringComparison.Ordinal) && ElementOps.IsTopLevel(path))
        {
            var name = path["regions.".Length..];
            return _session.Hosts.FirstOrDefault(h => string.Equals(h.Node.Region, name, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private Rect? BoundsOf(ComponentHost? host)
    {
        if (host is null || TopLevel.GetTopLevel(host) is null || !host.IsEffectivelyVisible) return null;
        return host.TranslatePoint(new Point(0, 0), _overlay) is { } origin ? new Rect(origin, host.Bounds.Size) : null;
    }

    public void RefreshOverlay()
    {
        var selected = _ctx.Selection;
        var host = HostFor(selected);
        _overlay.Selection = BoundsOf(host);
        _overlay.ShowHandles = host is not null && selected is not null && JsonPath.IsElementPath(selected) && selected != "root" && !ElementOps.IsTopLevel(selected);
        _overlay.Structure.Clear();
        if (ShowStructure && _session is not null)
            foreach (var (h, bounds) in LayoutInspector.Visible(_session, _overlay))
                if (ComponentCatalog.TryGet(h.Node.Type, out var spec) && spec.IsContainer) _overlay.Structure.Add(bounds);
        var unusedRegion = host is null && selected is not null && selected.StartsWith("regions.", StringComparison.Ordinal) && ElementOps.IsTopLevel(selected);
        _hintBox.IsVisible = unusedRegion;
        if (unusedRegion) _hint.Text = "This region is not used in the layout yet, so it cannot be shown here. Drag it in from Controls ▸ Regions in this layout.";
        _overlay.Label = _overlay.Selection is { } r && selected is not null && _resizing == PreviewOverlay.Handle.None ? SizeText(r.Size) : _overlay.Label;
        if (_resizing == PreviewOverlay.Handle.None) _overlay.Label = _overlay.Selection is { } r2 ? SizeText(r2.Size) : null;
        _overlay.Invalidate();
        BuildBreadcrumb();
    }

    private static string SizeText(Size s) => $"{Math.Round(s.Width)} × {Math.Round(s.Height)}";

    private void OnSelectionChanged(string? path, object? source)
    {
        if (_session is not null && path is not null && JsonPath.IsElementPath(path) && !ReferenceEquals(source, this))
            LayoutInspector.Reveal(_session, path);
        Dispatcher.UIThread.Post(RefreshOverlay, DispatcherPriority.Loaded);
    }

    private void BuildBreadcrumb()
    {
        _breadcrumb.Children.Clear();
        var selected = _ctx.Selection;
        if (selected is null || !JsonPath.IsElementPath(selected)) return;
        var chain = new List<string>();
        for (var path = selected; path is not null; path = ParentElement(path)) chain.Insert(0, path);
        for (var i = 0; i < chain.Count; i++)
        {
            var path = chain[i];
            var element = _ctx.Doc.Element(path);
            if (element is null) continue;
            var label = ElementOps.IsRegionReference(element) ? "↪ " + element["region"] : ElementOps.TypeOf(element);
            if (element["id"] is JsonValue id && id.TryGetValue<string>(out var text)) label += " #" + text;
            var button = new Button
            {
                Content = label, Padding = new Thickness(6, 2), FontSize = 12, MinHeight = 0, Background = Brushes.Transparent,
                FontWeight = i == chain.Count - 1 ? FontWeight.SemiBold : FontWeight.Normal,
            };
            button.Click += (_, _) => _ctx.Select(path, this);
            if (i > 0) _breadcrumb.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
            _breadcrumb.Children.Add(button);
        }
    }

    private string? ParentElement(string path) => ElementOps.ParentOf(_ctx.Doc.Data ?? new JsonObject(), path)?.ParentPath;

    // ------------------------------------------------------------------ pointer: select, resize

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _overlay.Focus();
        if (_session is null) return;
        var point = e.GetPosition(_overlay);
        var props = e.GetCurrentPoint(_overlay).Properties;

        if (props.IsLeftButtonPressed && _overlay.HandleAt(point) is var handle and not PreviewOverlay.Handle.None && _overlay.Selection is { } selection)
        {
            _resizing = handle;
            _resizeStart = selection;
            _resizeOrigin = point;
            _newWidth = _newHeight = null;
            e.Pointer.Capture(_overlay);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && TabHeaderAt(point) is { } page)
        {
            // The tab strip is not operable in the preview, so a click on a header shows that page and selects it.
            _pressedPath = null;
            _ctx.Select(page.Path, this);
            LayoutInspector.Reveal(_session, page.Path);
            Dispatcher.UIThread.Post(RefreshOverlay, DispatcherPriority.Loaded);
            e.Handled = true;
            return;
        }
        var host = LayoutInspector.HitTest(_session, _overlay, point);
        if (host is not null && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) host = OuterHost(host, point);
        _pressedPath = host?.Node.Path;
        _pressPoint = point;
        if (host is not null && BoundsOf(host) is { } bounds) _grab = point - bounds.TopLeft;
        if (_pressedPath is not null) _ctx.Select(_pressedPath, this);

        if (props.IsRightButtonPressed && _pressedPath is not null)
        {
            _overlay.ContextMenu = ElementMenu.Build(_ctx, _pressedPath);
            _overlay.ContextMenu.Open(_overlay);
            e.Handled = true;
        }
    }

    /// <summary>The page whose tab header is under a point, if the point is on a tab strip.</summary>
    private LayoutNode? TabHeaderAt(Point point)
    {
        foreach (var (host, _) in LayoutInspector.Visible(_session!, _overlay))
        {
            if (!host.Node.Type.Equals("tabs", StringComparison.OrdinalIgnoreCase)) continue;
            if (host.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is not { } tabs) continue;
            var index = 0;
            foreach (var item in tabs.Items.OfType<TabItem>())
            {
                if (item.TranslatePoint(new Point(0, 0), _overlay) is { } origin && new Rect(origin, item.Bounds.Size).Contains(point))
                    return index < host.Node.Children.Count ? host.Node.Children[index] : null;
                index++;
            }
        }
        return null;
    }

    /// <summary>Alt+click: the next enclosing element under the point.</summary>
    private ComponentHost? OuterHost(ComponentHost inner, Point point)
    {
        var current = HostFor(_ctx.Selection);
        var candidates = LayoutInspector.Visible(_session!, _overlay).Where(v => v.Bounds.Contains(point)).OrderBy(v => v.Bounds.Width * v.Bounds.Height).ToList();
        if (current is null) return inner;
        var index = candidates.FindIndex(c => ReferenceEquals(c.Host, current));
        return index >= 0 && index + 1 < candidates.Count ? candidates[index + 1].Host : inner;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_session is null) return;
        var point = e.GetPosition(_overlay);
        if (_resizing != PreviewOverlay.Handle.None)
        {
            var dx = point.X - _resizeOrigin.X;
            var dy = point.Y - _resizeOrigin.Y;
            var w = _resizeStart.Width;
            var h = _resizeStart.Height;
            if (_resizing is PreviewOverlay.Handle.Right or PreviewOverlay.Handle.Corner) w = Math.Max(8, Math.Round(w + dx));
            if (_resizing is PreviewOverlay.Handle.Bottom or PreviewOverlay.Handle.Corner) h = Math.Max(8, Math.Round(h + dy));
            _newWidth = _resizing == PreviewOverlay.Handle.Bottom ? null : w;
            _newHeight = _resizing == PreviewOverlay.Handle.Right ? null : h;
            _overlay.Ghost = new Rect(_resizeStart.X, _resizeStart.Y, w, h);
            _overlay.Label = $"{w} × {h}";
            _overlay.Invalidate();
            return;
        }

        if (e.GetCurrentPoint(_overlay).Properties.IsLeftButtonPressed) return;
        _overlay.Cursor = _overlay.HandleAt(point) switch
        {
            PreviewOverlay.Handle.Right => new Cursor(StandardCursorType.SizeWestEast),
            PreviewOverlay.Handle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
            PreviewOverlay.Handle.Corner => new Cursor(StandardCursorType.BottomRightCorner),
            _ => new Cursor(StandardCursorType.Arrow),
        };
        var host = LayoutInspector.HitTest(_session, _overlay, point);
        var bounds = BoundsOf(host);
        if (bounds != _overlay.Hover)
        {
            _overlay.Hover = bounds;
            _overlay.Invalidate();
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizing == PreviewOverlay.Handle.None) return;
        e.Pointer.Capture(null);
        _resizing = PreviewOverlay.Handle.None;
        _overlay.Ghost = null;
        var path = _ctx.Selection;
        var (w, h) = (_newWidth, _newHeight);
        _newWidth = _newHeight = null;
        if (path is null || !JsonPath.IsElementPath(path) || w is null && h is null)
        {
            RefreshOverlay();
            return;
        }
        _ctx.Edit("Resize", data =>
        {
            if (ElementOps.Element(data, path) is not { } element) return null;
            if (w is { } width) element["width"] = (int)width;
            if (h is { } height) element["height"] = (int)height;
            return "";
        });
        RefreshOverlay();
    }

    // ------------------------------------------------------------------ drag and drop

    private DragPayload? StartDrag()
    {
        if (_resizing != PreviewOverlay.Handle.None || _pressedPath is not { } path) return null;
        if (path == "root" || ElementOps.IsTopLevel(path) || _ctx.Doc.Element(path) is null) return null;
        return new DragPayload(ElementOps.TypeOf(_ctx.Doc.Element(path)!), path, null);
    }

    Control IDropTarget.Surface => _overlay;

    bool IDropTarget.Hover(Point windowPoint, DragPayload payload)
    {
        var where = Locate(windowPoint, payload, out var indicator, out var line);
        _lastDrop = where is null ? null : new ActionsDropCache(where);
        _overlay.Drop = indicator;
        _overlay.DropIsLine = line;
        _overlay.Ghost = payload.SourcePath is not null && where is { X: not null } ? GhostFor(payload, where) : null;
        _overlay.Label = where is null ? null : _overlay.Ghost is null ? "Drop here" : SizeText(_overlay.Ghost.Value.Size);
        _overlay.Invalidate();
        return where is not null;
    }

    void IDropTarget.Drop(Point windowPoint, DragPayload payload)
    {
        var where = _lastDrop?.Where;
        ((IDropTarget)this).Leave();
        if (where is not null) _ctx.Actions.ApplyDrop(payload, where);
    }

    void IDropTarget.Leave()
    {
        _overlay.Drop = null;
        _overlay.Ghost = null;
        _lastDrop = null;
        RefreshOverlay();
    }

    private Rect? GhostFor(DragPayload payload, EditorActions.DropLocation where)
    {
        var host = HostFor(payload.SourcePath);
        var size = BoundsOf(host)?.Size ?? new Size(120, 40);
        if (_overlay.Drop is not { } cell || where.X is null || where.Y is null) return null;
        // For a canvas the indicator is the container; the ghost sits at the requested offset inside it.
        var container = HostFor(where.ParentPath) ?? _session?.Hosts.FirstOrDefault(h => ReferenceEquals(h.Node.Path, where.ParentPath));
        var origin = BoundsOf(container)?.TopLeft ?? cell.TopLeft;
        return new Rect(origin.X + where.X.Value, origin.Y + where.Y.Value, size.Width, size.Height);
    }

    /// <summary>The path to edit for a node: a node that came from a region is changed in the region's definition.</summary>
    private static string EditPath(LayoutNode node) => node.Region is { } region ? "regions." + region : node.Path;

    private EditorActions.DropLocation? Locate(Point windowPoint, DragPayload payload, out Rect? indicator, out bool line)
    {
        indicator = null;
        line = false;
        if (_session is null || TopLevel.GetTopLevel(_overlay) is not { } top || top.TranslatePoint(windowPoint, _overlay) is not { } point) return null;

        var visible = LayoutInspector.Visible(_session, _overlay).ToList();
        var lookup = visible.ToDictionary(v => v.Host, v => v.Bounds);

        // The innermost container under the pointer that can take the element.
        (ComponentHost Host, Rect Bounds)? best = null;
        foreach (var (host, bounds) in visible)
        {
            if (!bounds.Contains(point)) continue;
            if (!ComponentCatalog.TryGet(host.Node.Type, out var spec) || !spec.IsContainer) continue;
            var editPath = EditPath(host.Node);
            if (payload.SourcePath is { } source && JsonPath.IsWithin(editPath, source)) continue;
            if (spec.Children == ChildRule.Single && host.Node.Children.Any(c => c.Path != payload.SourcePath)) continue;
            if (best is null || bounds.Width * bounds.Height <= best.Value.Bounds.Width * best.Value.Bounds.Height) best = (host, bounds);
        }
        if (best is not { } target) return null;

        var node = target.Host.Node;
        var parentPath = EditPath(node);
        var type = node.Type.ToLowerInvariant();
        var box = target.Bounds;

        // Children with their bounds, in file order.
        var kids = node.Children.Select((child, i) => (Index: i, Child: child, Host: _session.Hosts.FirstOrDefault(h => ReferenceEquals(h.Node, child))))
            .Where(k => k.Host is not null && lookup.ContainsKey(k.Host)).Select(k => (k.Index, Bounds: lookup[k.Host!])).ToList();

        switch (type)
        {
            case "canvas":
            {
                var x = point.X - box.X - (payload.SourcePath is not null ? _grab.X : 0);
                var y = point.Y - box.Y - (payload.SourcePath is not null ? _grab.Y : 0);
                indicator = box.Deflate(1);
                return new EditorActions.DropLocation(parentPath, -1, X: Math.Max(0, x), Y: Math.Max(0, y));
            }
            case "grid":
            {
                var grid = target.Host.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => ReferenceEquals(g.FindAncestorOfType<ComponentHost>(), target.Host));
                if (grid is null) break;
                var origin = grid.TranslatePoint(new Point(0, 0), _overlay) ?? box.TopLeft;
                var (column, cx, cw) = Track(grid.ColumnDefinitions.Select(c => c.ActualWidth).ToList(), grid.ColumnSpacing, point.X - origin.X, grid.Bounds.Width);
                var (row, ry, rh) = Track(grid.RowDefinitions.Select(r => r.ActualHeight).ToList(), grid.RowSpacing, point.Y - origin.Y, grid.Bounds.Height);
                indicator = new Rect(origin.X + cx, origin.Y + ry, cw, rh).Deflate(2);
                return new EditorActions.DropLocation(parentPath, -1, Row: row, Column: column);
            }
            case "tabs" or "scroll":
                indicator = box.Deflate(2);
                return new EditorActions.DropLocation(parentPath, -1);
        }

        // Rows and columns: find the gap the pointer is nearest.
        var horizontal = node.GetString("orientation")?.ToLowerInvariant() switch
        {
            "horizontal" => true,
            "vertical" => false,
            _ => type == "split",
        };
        if (kids.Count == 0)
        {
            indicator = box.Deflate(3);
            return new EditorActions.DropLocation(parentPath, -1);
        }
        var index = node.Children.Count;
        foreach (var (i, bounds) in kids)
        {
            var center = horizontal ? bounds.Center.X : bounds.Center.Y;
            if ((horizontal ? point.X : point.Y) < center) { index = i; break; }
        }
        double edge;
        if (index == node.Children.Count)
        {
            var last = kids[^1].Bounds;
            edge = horizontal ? last.Right : last.Bottom;
        }
        else
        {
            var next = kids.First(k => k.Index == index).Bounds;
            var previous = kids.LastOrDefault(k => k.Index < index);
            var nextEdge = horizontal ? next.Left : next.Top;
            edge = previous.Bounds.IsEmpty() ? nextEdge : ((horizontal ? previous.Bounds.Right : previous.Bounds.Bottom) + nextEdge) / 2;
        }
        line = true;
        indicator = horizontal ? new Rect(edge - 2, box.Top + 2, 4, Math.Max(4, box.Height - 4)) : new Rect(box.Left + 2, edge - 2, Math.Max(4, box.Width - 4), 4);
        return new EditorActions.DropLocation(parentPath, index);
    }

    /// <summary>Which track of a grid (row or column) holds an offset, with the track's start and size.</summary>
    private static (int Index, double Start, double Size) Track(List<double> sizes, double spacing, double offset, double total)
    {
        if (sizes.Count == 0) return (0, 0, total);
        var start = 0.0;
        for (var i = 0; i < sizes.Count; i++)
        {
            var end = start + sizes[i];
            if (offset < end + spacing / 2 || i == sizes.Count - 1) return (i, start, sizes[i]);
            start = end + spacing;
        }
        return (sizes.Count - 1, start, sizes[^1]);
    }
}

internal static class RectExtensions
{
    public static bool IsEmpty(this Rect r) => r.Width <= 0 && r.Height <= 0 && r.X == 0 && r.Y == 0;
}
