using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Layout;
using Carvera.App.Services;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Gcode;
using Carvera.Core.Transfer;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Shell;

/// <summary>
/// The only fixed UI: a message area for layout errors and safety warnings above the layout itself.
/// Everything else comes from the layout file.
/// </summary>
public sealed class MainWindow : Window, IAppHost
{
    private readonly AppServices _services;
    private readonly StackPanel _banners = new() { Spacing = 0 };
    private readonly Border _layoutHost = new();
    private LayoutSession? _session;
    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherTimer? _reloadTimer;
    private string? _currentLayoutPath;
    private string? _currentLayoutName;
    private string? _loadedText;
    private bool _assetsChanged;
    private EditorLink? _editorLink;

    public MainWindow(string[] args)
    {
        var settings = Settings.Load();
        var controller = new CarveraController();
        _services = new AppServices(controller, this, LayoutLibrary.Default(), settings) { MainWindow = this };
        controller.State.Set(StatePaths.ConnectionKind, settings.ConnectionKind);
        controller.State.Set(StatePaths.ConnectionAddress, settings.ConnectionAddress);

        Title = "Carvera Controller";
        Width = settings.WindowWidth ?? 1400;
        Height = settings.WindowHeight ?? 900;
        MinWidth = 640;
        MinHeight = 480;
        RestoreWindowPlacement(settings);

        var layoutArea = new DockPanel();
        // The settings button lives in its own slim column so it never sits over a layout control.
        _settingsButton = CreateSettingsButton();
        DockPanel.SetDock(_settingsStrip, Dock.Right);
        _settingsStrip.Child = _settingsButton;
        layoutArea.Children.Add(_settingsStrip);
        DockPanel.SetDock(_banners, Dock.Top);
        layoutArea.Children.Add(_banners);
        layoutArea.Children.Add(_layoutHost);
        _settingsHost.IsVisible = false;
        Content = new Panel { Children = { layoutArea, _settingsHost, CreateEditorOverlay() } };

        var requested = ArgValue(args, "--layout") ?? settings.Layout;
        LoadLayout(requested, initial: true);
        if (ArgValue(args, "--connect") is { } connect)
            _ = _services.Commands.ExecuteAsync("connect", CommandArgs.Empty.With("kind", connect.Equals("simulator", StringComparison.OrdinalIgnoreCase) ? "simulator" : settings.ConnectionKind).With("address", connect));

        // "--check-gpu [report file]": draw a large test toolpath on the GPU for a few seconds, write what happened, and exit.
        // Nothing is connected, and no settings are saved.
        var gpuCheck = args.Any(a => a.Equals("--check-gpu", StringComparison.OrdinalIgnoreCase));
        if (!gpuCheck) _services.Pendants.Apply();
        controller.ConnectionLost += lost =>
        {
            if (lost is not null) Dispatcher.UIThread.Post(() => StartReconnect(lost));
        };

        StartEditorLink(args);
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Deactivated += (_, _) => ReleaseHeldKeys();
        Opened += (_, _) =>
        {
            if (gpuCheck) _ = RunGpuCheckAsync(ArgValue(args, "--check-gpu") is { } file && !file.StartsWith("--") ? file : Path.Combine(Path.GetTempPath(), "carvera-gpu-check.txt"));
            else StartAutoConnect(ArgValue(args, "--connect") is not null);
        };
        Closing += (_, _) =>
        {
            if (gpuCheck) return;
            CloseSettings();
            SaveWindowPlacement(settings);
            settings.Save();
        };
        Closed += async (_, _) =>
        {
            _autoConnect?.Cancel();
            foreach (var watcher in _watchers) watcher.Dispose();
            _editorLink?.Dispose();
            _session?.Dispose();
            await controller.DisposeAsync();
            _services.Dispose();
        };
    }

    public AppServices Services => _services;
    public LayoutSession? Session => _session;

    // ------------------------------------------------------------------ GPU self-check

    private async Task RunGpuCheckAsync(string reportFile)
    {
        var report = new List<string> { $"Carvera Controller GPU check, {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        try
        {
            _services.Settings.ViewerRenderer = "GPU"; // in memory only; this mode never saves settings
            var lines = new List<string> { "G21 G90" };
            for (var i = 0; i < 120_000; i++)
                lines.Add($"G1 X{(i % 300) * 0.4:0.###} Y{(i / 300) * 0.05:0.###} Z{-Math.Abs(Math.Sin(i * 0.01)) * 3:0.###} F800");
            // A sparse part with rapids and long feed moves beside the dense block, so line width and dashes can be judged in the capture.
            for (var k = 0; k < 9; k++)
            {
                lines.Add($"G0 X{k * 14} Y45 Z5");
                lines.Add($"G1 X{k * 14 + 9} Y{55 + 12 * (k % 3)} Z-2 F600");
                lines.Add($"G1 X{k * 14 + 3} Y95 Z-2");
            }
            _services.SetProgram(GcodeProgram.Parse(lines, null));
            var view = _session?.Root.GetVisualDescendants().OfType<Components.Viewer.ToolpathView>().FirstOrDefault();
            if (view is null)
            {
                report.Add("This layout has no 3D view (toolpath).");
            }
            else
            {
                Topmost = true; // so a capture of the screen shows this window and not another one
                Activate();
                await Task.Delay(TimeSpan.FromSeconds(4));
                var picture = Path.ChangeExtension(reportFile, ".png");
                if (ScreenCapture.Save(this, picture)) report.Add($"Screen capture: {picture}");
                // Scrub to the middle: the far half should fade, which shows the colour logic in the shader at work.
                _services.SetPreviewSegment(60_000);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                var scrubbed = Path.ChangeExtension(reportFile, null) + "-scrubbed.png";
                if (ScreenCapture.Save(this, scrubbed)) report.Add($"Screen capture after scrubbing: {scrubbed}");
                report.Add(view.RendererReport());
                report.Add(view.GpuActive ? "RESULT: the GPU renderer works." : "RESULT: the GPU renderer is not in use (see above).");
            }
        }
        catch (Exception ex)
        {
            report.Add($"The check failed: {ex}");
        }
        try { File.WriteAllLines(reportFile, report); } catch (IOException) { }
        foreach (var line in report) System.Console.WriteLine(line);
        Close();
    }

    // ------------------------------------------------------------------ window placement

    private PixelPoint? _normalPosition;
    private Size? _normalSize;
    private WindowState _lastVisibleState = WindowState.Normal;

    /// <summary>Puts the window back where it was, unless that spot is no longer on any screen.</summary>
    private void RestoreWindowPlacement(Settings settings)
    {
        _normalSize = new Size(Width, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.WindowX is { } x && settings.WindowY is { } y && IsOnAScreen(new PixelPoint(x, y)))
        {
            _normalPosition = new PixelPoint(x, y);
            Position = _normalPosition.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
            _lastVisibleState = WindowState.Maximized;
        }

        // Only a window in its normal state tells us where it belongs; a maximised or minimised one does not.
        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal) _normalPosition = e.Point;
        };
        Resized += (_, e) =>
        {
            if (WindowState == WindowState.Normal) _normalSize = e.ClientSize;
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized) _lastVisibleState = WindowState;
        };
    }

    /// <summary>True when a good part of the title bar would be visible at the position, so the window can be grabbed.</summary>
    private bool IsOnAScreen(PixelPoint position)
    {
        var grab = new PixelRect(position.X, position.Y, 200, 40);
        return Screens?.All.Any(s => s.WorkingArea.Intersects(grab)) ?? false;
    }

    private void SaveWindowPlacement(Settings settings)
    {
        if (WindowState == WindowState.Normal)
        {
            _normalPosition = Position;
            _normalSize = new Size(ClientSize.Width, ClientSize.Height);
        }
        settings.WindowMaximized = _lastVisibleState == WindowState.Maximized || WindowState == WindowState.Maximized;
        if (_normalSize is { Width: > 0, Height: > 0 } size)
        {
            settings.WindowWidth = size.Width;
            settings.WindowHeight = size.Height;
        }
        if (_normalPosition is { } p)
        {
            settings.WindowX = p.X;
            settings.WindowY = p.Y;
        }
    }

    // ------------------------------------------------------------------ auto-connect

    private CancellationTokenSource? _autoConnect;

    private AutoConnectPolicy CurrentAutoConnectPolicy() =>
        new(_services.Settings.AutoConnectRetries, TimeSpan.FromSeconds(_services.Settings.AutoConnectIntervalSeconds));

    /// <summary>Runs a connector in the background; a newer one replaces (cancels) any earlier one.</summary>
    private void RunConnector(AutoConnector connector)
    {
        _autoConnect?.Cancel();
        _autoConnect = new CancellationTokenSource();
        var token = _autoConnect.Token;
        _ = Task.Run(() => connector.RunAsync(token), token);
    }

    /// <summary>Connects to the last-used machine at start-up, retrying as configured in the settings.</summary>
    private void StartAutoConnect(bool connectRequestedOnCommandLine)
    {
        var settings = _services.Settings;
        if (!settings.AutoConnect || connectRequestedOnCommandLine) return;
        var state = _services.State;
        var disconnects = _services.Controller.DisconnectCount;
        RunConnector(new AutoConnector(
            CurrentAutoConnectPolicy,
            // A simulator is never picked up automatically: it is for trying things out, not for the machine.
            () => !string.Equals(state.Get<string>(StatePaths.ConnectionKind), "simulator", StringComparison.OrdinalIgnoreCase)
                  && !string.IsNullOrWhiteSpace(state.Get<string>(StatePaths.ConnectionAddress)),
            () => state.Get<string>(StatePaths.ConnectionState) != "Disconnected" || _services.Controller.DisconnectCount != disconnects,
            _ => _services.Commands.ExecuteAsync("connect"),
            _services.Console.Info));
    }

    /// <summary>The machine dropped the connection: get it back, trying as often as the auto-connect settings allow.</summary>
    private void StartReconnect(ConnectionOptions lost)
    {
        if (!_services.Settings.AutoReconnect || lost.Kind == ConnectionKind.Simulator) return;
        var controller = _services.Controller;
        var state = _services.State;
        var disconnects = controller.DisconnectCount;
        _services.Console.Warning("Connection lost. Trying to reconnect.");
        RunConnector(new AutoConnector(
            CurrentAutoConnectPolicy,
            () => true,
            // Stop if someone connected (or is connecting), or the user chose Disconnect in the meantime.
            () => state.Get<string>(StatePaths.ConnectionState) != "Disconnected" || controller.DisconnectCount != disconnects,
            async token =>
            {
                try
                {
                    await controller.ConnectAsync(lost, token);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { return false; } // ConnectAsync has logged the reason
            },
            _services.Console.Info));
    }

    // ------------------------------------------------------------------ settings

    private readonly Border _settingsHost = new();
    private Button? _settingsButton;
    private readonly Border _settingsStrip = new() { Width = 44, VerticalAlignment = VerticalAlignment.Stretch };

    public bool IsSettingsOpen => _settingsHost.IsVisible;

    /// <summary>The settings page; it covers the whole window until closed. Null while the layout is showing.</summary>
    public SettingsView? SettingsPage => _settingsHost.Child as SettingsView;

    /// <summary>
    /// A small fixed button in the top-right corner, present in every layout, that flips to the settings page.
    /// It is not part of any layout file so a layout cannot hide the way to the settings.
    /// </summary>
    private Button CreateSettingsButton()
    {
        var button = new Button
        {
            Content = new TextBlock { Text = "⚙", FontSize = 18, FontFamily = new FontFamily("Segoe UI Symbol"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(5, 6, 5, 0),
            Background = Brush.Parse("#E6FFFFFF"),
            BorderBrush = Brush.Parse("#B9C3D0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Name = "SettingsButton",
        };
        ToolTip.SetTip(button, "Settings (Ctrl+,)");
        Avalonia.Automation.AutomationProperties.SetName(button, "Settings");
        button.Click += (_, _) => OpenSettings();
        return button;
    }

    public void OpenSettings()
    {
        if (IsSettingsOpen) return;
        _settingsHost.Child = new SettingsView(_services, CloseSettings, name => LoadLayout(name));
        _settingsHost.IsVisible = true;
        _settingsStrip.IsVisible = false;
    }

    public void CloseSettings()
    {
        if (!IsSettingsOpen) return;
        _settingsHost.IsVisible = false;
        _settingsStrip.IsVisible = true;
        _settingsHost.Child = null;
        _services.Settings.Save();
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // ------------------------------------------------------------------ layouts

    /// <summary>Loads a layout by name or path. On failure the current layout stays (or the built-in one is used).</summary>
    public bool LoadLayout(string nameOrPath, bool initial = false)
    {
        var path = _services.Layouts.Find(nameOrPath);
        LayoutLoadResult result;
        if (path is null)
        {
            result = new LayoutLoadResult(null, [new LayoutDiagnostic(DiagnosticSeverity.Error, nameOrPath, $"No layout named '{nameOrPath}' in {string.Join(" or ", _services.Layouts.Folders)}.")]);
        }
        else result = LayoutLoader.LoadFile(path, new LayoutValidator(_services.Commands));
        var text = path is not null ? TryRead(path) : null;

        if (!result.Success)
        {
            ShowErrors(Path.GetFileName(path ?? nameOrPath), result.Diagnostics);
            if (_session is not null && !initial) return false;
            // Fall back to the layout compiled into the program so the machine can always be operated.
            result = LayoutLoader.Load(ReadEmbeddedDefault(), "desktop", Path.Combine(AppContext.BaseDirectory, "layouts"), null, new LayoutValidator(_services.Commands));
            if (result.Document is null) return false;
            path = null;
            text = null;
        }
        else ClearBanner("errors");

        Apply(result.Document!, result.Diagnostics);
        _currentLayoutPath = path;
        _currentLayoutName = path is null ? null : Path.GetFileNameWithoutExtension(path);
        _loadedText = text;
        Watch(path);
        if (path is not null && !string.Equals(_services.Settings.Layout, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
        {
            _services.Settings.Layout = Path.GetFileNameWithoutExtension(path);
            _services.Settings.Save();
        }
        AnnounceLayout();
        return true;
    }

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string ReadEmbeddedDefault()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Carvera.App.DefaultLayout.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Apply(LayoutDocument document, IReadOnlyList<LayoutDiagnostic> diagnostics)
    {
        _services.State.Set(StatePaths.LayoutName, document.SourcePath is null ? document.Name : Path.GetFileNameWithoutExtension(document.SourcePath));
        // A reload of the same layout (live editing) keeps the tab, scroll position, splitters and collapsed panels the user had.
        var viewState = _session is not null && document.SourcePath is not null && string.Equals(_session.Document.SourcePath, document.SourcePath, StringComparison.OrdinalIgnoreCase)
            ? ViewState.Capture(_session) : null;
        _session?.Dispose();
        _session = new LayoutSession(document, _services);
        _layoutHost.Child = _session.Root;
        if (viewState is not null)
        {
            _session.Root.UpdateLayout();
            viewState.Restore(_session);
        }
        _layoutHost.Background = _session.Context.Theme.TokenBrush("background");
        _settingsStrip.Background = _session.Context.Theme.TokenBrush("background");
        _layoutHost.SetValue(TextElement.FontFamilyProperty, _session.Context.Theme.FontFamily);
        _layoutHost.SetValue(TextElement.FontSizeProperty, _session.Context.Theme.FontSize);
        _layoutHost.SetValue(TextElement.ForegroundProperty, _session.Context.Theme.TokenBrush("text"));
        Background = _session.Context.Theme.TokenBrush("background");
        Title = document.Window.Title ?? $"Carvera Controller — {document.Name}";
        if (document.Window.MinWidth is { } mw) MinWidth = mw;
        if (document.Window.MinHeight is { } mh) MinHeight = mh;

        // Missing safety controls get their own banner and message below.
        foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning && !d.Message.StartsWith(LayoutValidator.MissingSafetyPrefix, StringComparison.Ordinal)))
            _services.Console.Warning($"Layout: {d.Path}: {d.Message}");

        var missing = SafetyAnalyzer.FindMissing(document);
        if (missing.Count > 0)
        {
            var list = string.Join(", ", missing);
            _services.Console.Warning($"Safety: this layout has no visible {list} control.");
            ShowBanner("safety", $"Safety warning: the layout “{document.Name}” does not show {Describe(missing)}. Add {(missing.Count == 1 ? "a button" : "buttons")} for {list} so the machine can always be stopped.",
                "#FEF3C7", "#92400E", "builtin:warning");
        }
        else ClearBanner("safety");
    }

    private static string Describe(IReadOnlyList<string> names) => names.Count switch
    {
        1 => $"a {names[0]} control",
        2 => $"{names[0]} or {names[1]} controls",
        _ => string.Join(", ", names.Take(names.Count - 1)) + $" or {names[^1]} controls",
    };

    private void ShowErrors(string file, IReadOnlyList<LayoutDiagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        foreach (var e in errors) _services.Console.Error($"Layout {file}: {e.Path}: {e.Message}");
        var shown = string.Join("\n", errors.Take(6).Select(e => $"• {e.Path}: {e.Message}"));
        if (errors.Count > 6) shown += $"\n• …and {errors.Count - 6} more (see the console)";
        ShowBanner("errors", $"Layout “{file}” has errors and was not applied:\n{shown}", "#FEE2E2", "#991B1B", "builtin:warning");
    }

    private readonly Dictionary<string, Control> _bannerControls = new();

    private void ShowBanner(string key, string message, string background, string foreground, string icon)
    {
        ClearBanner(key);
        var fg = Brush.Parse(foreground);
        var close = new Button { Content = "Dismiss", VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 2), Background = Brushes.Transparent, Foreground = fg, BorderBrush = fg, BorderThickness = new Thickness(1) };
        var iconControl = new Avalonia.Controls.Shapes.Path { Data = Icons.Get(icon["builtin:".Length..]), Fill = fg, Width = 18, Height = 18, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = fg };
        var dock = new DockPanel();
        DockPanel.SetDock(iconControl, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        dock.Children.Add(iconControl);
        dock.Children.Add(close);
        dock.Children.Add(text);
        var banner = new Border
        {
            Background = Brush.Parse(background),
            BorderBrush = fg,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 8),
            Child = dock,
            Tag = key,
        };
        close.Click += (_, _) => ClearBanner(key);
        _bannerControls[key] = banner;
        _banners.Children.Add(banner);
    }

    private void ClearBanner(string key)
    {
        if (_bannerControls.Remove(key, out var banner)) _banners.Children.Remove(banner);
    }

    public bool HasBanner(string key) => _bannerControls.ContainsKey(key);

    /// <summary>
    /// Watches every layout folder (and the layout's own folder), not only the file in use: the editor saves a customised
    /// copy of a built-in layout into the user's folder, and that copy takes over from the built-in one.
    /// </summary>
    private void Watch(string? path)
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        var folders = _services.Layouts.Folders.ToList();
        if (path is not null) folders.Add(Path.GetDirectoryName(path)!);
        var roots = folders.Where(Directory.Exists).Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in roots.Where(r => !roots.Any(o => o != r && r.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                // Watch the folder: editors often replace files rather than write them in place.
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler changed = (_, e) => ScheduleReload(e.FullPath);
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, e) => ScheduleReload(e.FullPath);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                _services.Console.Warning($"Live layout reload is unavailable for {root}: {ex.Message}");
            }
        }
    }

    private static readonly string[] AssetExtensions = [".svg", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    private static bool IsAsset(string file) => AssetExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    private void ScheduleReload(string changedFile) => Dispatcher.UIThread.Post(() =>
    {
        if (IsAsset(changedFile)) _assetsChanged = true;
        _reloadTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer!.Stop();
            ReloadIfChanged();
        });
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    /// <summary>Reloads when the file now in force for the current layout differs from what was loaded, or when a picture or other asset changed.</summary>
    private void ReloadIfChanged()
    {
        if (_currentLayoutPath is null) return;
        var target = (_currentLayoutName is not null ? _services.Layouts.Find(_currentLayoutName) : null) ?? _currentLayoutPath;
        var assets = _assetsChanged;
        _assetsChanged = false;
        if (!assets && string.Equals(target, _currentLayoutPath, StringComparison.OrdinalIgnoreCase) && TryRead(target) is { } now && now == _loadedText) return;
        if (LoadLayout(target)) _services.Console.Info($"Reloaded layout {Path.GetFileName(target)}.");
    }

    // ------------------------------------------------------------------ layout editor link

    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _outline = new() { BorderBrush = Brush.Parse("#F97316"), BorderThickness = new Thickness(3), Background = Brush.Parse("#33F97316"), IsVisible = false, CornerRadius = new CornerRadius(3) };
    private readonly Border _pickBadge = new()
    {
        Background = Brush.Parse("#F97316"), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 6), IsVisible = false,
        Child = new TextBlock { Text = "Layout editor: click an element to pick it (Esc to stop)", Foreground = Brushes.White, FontWeight = FontWeight.SemiBold },
    };
    private DispatcherTimer? _flashTimer;
    private bool _picking;

    private Control CreateEditorOverlay()
    {
        _overlay.Children.Add(_outline);
        _overlay.Children.Add(_pickBadge);
        Canvas.SetLeft(_pickBadge, 16);
        Canvas.SetTop(_pickBadge, 12);
        return _overlay;
    }

    private void StartEditorLink(string[] args)
    {
        if (args.Any(a => a.Equals("--no-editor-link", StringComparison.OrdinalIgnoreCase))) return;
        _editorLink = new EditorLink(ArgValue(args, "--editor-pipe"));
        _editorLink.Message += m => Dispatcher.UIThread.Post(() => OnEditorMessage(m));
        _editorLink.ConnectionChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            if (connected) AnnounceLayout();
            else SetPicking(false, notify: false);
        });
        _editorLink.Start();
        AddHandler(PointerMovedEvent, OnPickMove, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPickPress, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPickRelease, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void AnnounceLayout() => _editorLink?.Send(new System.Text.Json.Nodes.JsonObject
    {
        ["event"] = "layout",
        ["name"] = _currentLayoutName ?? _session?.Document.Name,
        ["file"] = _currentLayoutPath,
    });

    private void OnEditorMessage(System.Text.Json.Nodes.JsonObject message)
    {
        switch (message["cmd"]?.GetValue<string>())
        {
            case "reveal" when message["path"]?.GetValue<string>() is { } path && _session is not null:
                if (IsSettingsOpen) CloseSettings();
                Show(LayoutInspector.Reveal(_session, path), flash: true);
                break;
            case "inspect":
                SetPicking(message["on"]?.GetValue<bool>() ?? false, notify: false);
                break;
            case "load" when message["layout"]?.GetValue<string>() is { Length: > 0 } layout:
                LoadLayout(layout);
                break;
        }
    }

    private void SetPicking(bool on, bool notify)
    {
        _picking = on;
        _pickBadge.IsVisible = on;
        if (!on) _outline.IsVisible = false;
        if (notify) _editorLink?.Send(new System.Text.Json.Nodes.JsonObject { ["event"] = "inspect", ["on"] = on });
    }

    /// <summary>Outlines an element; a flash fades after a moment, hover in pick mode stays.</summary>
    private void Show(ComponentHost? host, bool flash)
    {
        _flashTimer?.Stop();
        if (host is null || Content is not Panel root || host.TranslatePoint(new Point(0, 0), root) is not { } origin)
        {
            _outline.IsVisible = false;
            return;
        }
        Canvas.SetLeft(_outline, origin.X);
        Canvas.SetTop(_outline, origin.Y);
        _outline.Width = host.Bounds.Width;
        _outline.Height = host.Bounds.Height;
        _outline.IsVisible = true;
        if (!flash) return;
        _flashTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1800), DispatcherPriority.Background, (_, _) =>
        {
            _flashTimer!.Stop();
            if (!_picking) _outline.IsVisible = false;
        });
        _flashTimer.Start();
    }

    private ComponentHost? PickTarget(PointerEventArgs e) =>
        _session is not null && Content is Panel root ? LayoutInspector.HitTest(_session, root, e.GetPosition(root)) : null;

    private void OnPickMove(object? sender, PointerEventArgs e)
    {
        if (_picking) Show(PickTarget(e), flash: false);
    }

    private void OnPickPress(object? sender, PointerPressedEventArgs e)
    {
        if (!_picking) return;
        e.Handled = true; // the click picks; it must not press the button under it
        if (PickTarget(e) is { } host)
        {
            Show(host, flash: false);
            _editorLink?.Send(new System.Text.Json.Nodes.JsonObject
            {
                ["event"] = "picked",
                ["layout"] = _currentLayoutName,
                ["path"] = host.Node.Path,
            });
        }
    }

    private void OnPickRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (_picking) e.Handled = true;
    }

    // ------------------------------------------------------------------ keyboard shortcuts

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (_picking && e.Key == Key.Escape)
        {
            e.Handled = true;
            SetPicking(false, notify: true);
            return;
        }
        // Built in like Ctrl+L: flips between the layout and the settings page.
        if (e.Key == Key.OemComma && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            if (IsSettingsOpen) CloseSettings(); else OpenSettings();
            return;
        }
        if (IsSettingsOpen)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseSettings();
            }
            return; // layout shortcuts must not act while the settings page covers the layout
        }
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            _ = ReloadLayoutAsync();
            e.Handled = true;
            return;
        }
        // Built in so every layout can be left, even one without a layoutSelector.
        if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = ChooseLayoutAsync();
            return;
        }
        // Plain keys go to text boxes; shortcuts with Ctrl/Alt (or function keys) always apply.
        var typing = FocusManager?.GetFocusedElement() is TextBox or AutoCompleteBox;
        if (_session?.MatchShortcut(e, typing) is { } shortcut)
        {
            e.Handled = true;
            if (shortcut.Release is not null) _heldKeys[e.Key] = shortcut;
            if (!shortcut.Repeat && _autoRepeating.Contains(e.Key)) return;
            _autoRepeating.Add(e.Key);
            _ = _services.Commands.ExecuteAsync(shortcut.Command, shortcut.Args);
        }
    }

    // Keys whose shortcut has a release command (jogging while a key is held), and keys already down (so repeat:false can ignore auto-repeat).
    private readonly Dictionary<Key, LayoutSession.KeyBinding> _heldKeys = [];
    private readonly HashSet<Key> _autoRepeating = [];

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _autoRepeating.Remove(e.Key);
        if (_heldKeys.Remove(e.Key, out var binding) && binding.Release is { } release)
            _ = _services.Commands.ExecuteAsync(release, binding.ReleaseArgs);
    }

    /// <summary>Lets go of every held key, e.g. when the window loses focus, so a continuous jog cannot run on.</summary>
    private void ReleaseHeldKeys()
    {
        _autoRepeating.Clear();
        foreach (var binding in _heldKeys.Values.DistinctBy(b => b.Release))
            if (binding.Release is { } release) _ = _services.Commands.ExecuteAsync(release, binding.ReleaseArgs);
        _heldKeys.Clear();
    }

    public async Task ChooseLayoutAsync()
    {
        var names = _services.Layouts.List().Select(l => l.Name).ToList();
        var choice = await Dialogs.PickAsync(this, "Switch layout", names, _services.State.Get<string>(StatePaths.LayoutName));
        if (choice is not null) LoadLayout(choice);
    }

    // ------------------------------------------------------------------ IAppHost

    /// <summary>The folder the file pickers open in: the one last used for a local file, when it still exists.</summary>
    private async Task<IStorageFolder?> StartFolderAsync() =>
        _services.Settings.LastFolder is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;

    /// <summary>Remembers the folder of a file the user picked, for the next picker.</summary>
    private void RememberFolderOf(string? path)
    {
        if (path is null) return;
        _services.Settings.RememberFolder(System.IO.Directory.Exists(path) ? path : Path.GetDirectoryName(path));
    }

    public async Task OpenFileAsync(string? path)
    {
        if (path is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open G-code",
                AllowMultiple = false,
                SuggestedStartLocation = await StartFolderAsync(),
                FileTypeFilter =
                [
                    new FilePickerFileType("G-code") { Patterns = ["*.nc", "*.gcode", "*.gc", "*.ngc", "*.cnc", "*.tap", "*.txt"] },
                    FilePickerFileTypes.All,
                ],
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            RememberFolderOf(path);
        }
        var program = await Task.Run(() => GcodeProgram.Load(path));
        _services.SetProgram(program);
        _services.Console.Info($"Opened {Path.GetFileName(path)}: {program.Lines.Count} lines, {program.Segments.Count} path segments.");
    }

    public Task CloseFileAsync()
    {
        _services.SetProgram(null);
        return Task.CompletedTask;
    }

    public Task LoadLayoutAsync(string name)
    {
        Dispatcher.UIThread.Post(() => LoadLayout(name));
        return Task.CompletedTask;
    }

    public Task ReloadLayoutAsync()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (LoadLayout(_currentLayoutName ?? _currentLayoutPath ?? _services.Settings.Layout)) _services.Console.Info("Layout reloaded.");
        });
        return Task.CompletedTask;
    }

    public Task ExitAsync()
    {
        Dispatcher.UIThread.Post(Close);
        return Task.CompletedTask;
    }

    public async Task SaveFileAsync(string? path)
    {
        var program = _services.Program ?? throw new InvalidOperationException("No G-code file is open.");
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save G-code as",
                SuggestedFileName = program.Path is { } p ? Path.GetFileNameWithoutExtension(p) + "-edited" + Path.GetExtension(p) : "program.nc",
                DefaultExtension = program.Path is { } q ? Path.GetExtension(q).TrimStart('.') : "nc",
                SuggestedStartLocation = program.Path is { } origin ? await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(origin)!) : await StartFolderAsync(),
            });
            path = file?.TryGetLocalPath();
            if (path is null) return;
            RememberFolderOf(path);
        }
        await File.WriteAllLinesAsync(path, program.Lines);
        _services.SetProgram(GcodeProgram.Parse(program.Lines, path));
        _services.Console.Info($"Saved {Path.GetFileName(path)}.");
    }

    public Task SetOperationToolAsync(int operation, int tool)
    {
        var program = _services.Program ?? throw new InvalidOperationException("No G-code file is open.");
        if (operation < 0 || operation >= program.Operations.Count) throw new ArgumentException($"There is no operation {operation}.");
        var op = program.Operations[operation];
        if (op.Tool == tool) return Task.CompletedTask;
        var edited = GcodeEditor.ChangeOperationTool(program.Lines, program.Operations, operation, tool);
        Dispatcher.UIThread.Post(() => _services.EditProgram(edited));
        _services.Console.Info($"“{op.Name}” now uses T{tool} (was {(op.Tool is { } t ? $"T{t}" : "no tool")}). Save the file to keep the change.");
        return Task.CompletedTask;
    }

    public Task StepPreviewAsync(int? delta)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (delta is null) _services.SetPreviewSegment(-1);
            else
            {
                var current = _services.State.Get(StatePaths.PreviewSegment, -1);
                if (current < 0) current = delta > 0 ? -1 : _services.Program?.Segments.Count ?? 0;
                _services.SetPreviewSegment(Math.Max(0, current + delta.Value));
            }
        });
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string message) =>
        Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(this, message));

    public Task<string?> PromptAsync(string title, string label, string initial) =>
        Dispatcher.UIThread.InvokeAsync(() => Dialogs.PromptAsync(this, title, label, initial));

    public async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var file = await Dispatcher.UIThread.InvokeAsync(async () => await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the file from the machine",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = await StartFolderAsync(),
        }));
        var path = file?.TryGetLocalPath();
        RememberFolderOf(path);
        return path;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Dispatcher.UIThread.InvokeAsync(async () =>
            await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = await StartFolderAsync() }));
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        RememberFolderOf(path);
        return path;
    }

    public async Task<string?> PickOpenPathAsync(string title, params string[] patterns)
    {
        var files = await Dispatcher.UIThread.InvokeAsync(async () => await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Files") { Patterns = patterns }, FilePickerFileTypes.All],
            SuggestedStartLocation = await StartFolderAsync(),
        }));
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        RememberFolderOf(path);
        return path;
    }

    public async Task UploadFileAsync(string? path, string? remoteDirectory)
    {
        // Pressing Upload again while a transfer is running cancels it.
        if (_services.Transfers.Active)
        {
            _services.Transfers.Cancel();
            return;
        }
        var state = _services.State;
        if (path is null)
        {
            path = _services.Program?.Path;
            if (path is not null && state.Get(StatePaths.FileModified, false)
                && !await Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(this, "The open file has changes that are not saved. Upload the saved version from disk?", "Upload")))
                return;
        }
        if (path is null)
        {
            var files = await Dispatcher.UIThread.InvokeAsync(async () => await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Upload G-code to the machine",
                AllowMultiple = false,
                SuggestedStartLocation = await StartFolderAsync(),
                FileTypeFilter =
                [
                    new FilePickerFileType("G-code") { Patterns = ["*.nc", "*.gcode", "*.gc", "*.ngc", "*.cnc", "*.tap", "*.txt"] },
                    FilePickerFileTypes.All,
                ],
            }));
            path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            RememberFolderOf(path);
        }

        var settings = _services.Settings;
        var compress = settings.UploadCompression switch
        {
            "On" => true,
            "Off" => false,
            _ => UploadOptions.MachineAcceptsLz(state.Get<string>(StatePaths.MachineFileType)),
        };
        var cts = _services.Transfers.TryBegin();
        if (cts is null) return; // another transfer started while the file was being chosen
        try
        {
            await FileUploader.UploadAsync(_services.Controller, path, new UploadOptions(remoteDirectory ?? settings.UploadDirectory, compress), cts.Token);
        }
        finally
        {
            _services.Transfers.End(cts);
        }
    }

    public Task SelectOperationAsync(int operation)
    {
        Dispatcher.UIThread.Post(() => _services.SelectOperation(operation));
        return Task.CompletedTask;
    }
}
