using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.App.Services;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Gcode;
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
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _reloadTimer;
    private string? _currentLayoutPath;

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
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel();
        DockPanel.SetDock(_banners, Dock.Top);
        root.Children.Add(_banners);
        root.Children.Add(_layoutHost);
        Content = root;

        var requested = ArgValue(args, "--layout") ?? settings.Layout;
        LoadLayout(requested, initial: true);
        if (ArgValue(args, "--connect") is { } connect)
            _ = _services.Commands.ExecuteAsync("connect", CommandArgs.Empty.With("kind", connect.Equals("simulator", StringComparison.OrdinalIgnoreCase) ? "simulator" : settings.ConnectionKind).With("address", connect));

        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.Save();
        };
        Closed += async (_, _) =>
        {
            _watcher?.Dispose();
            _session?.Dispose();
            await controller.DisposeAsync();
            _services.Dispose();
        };
    }

    public AppServices Services => _services;
    public LayoutSession? Session => _session;

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

        if (!result.Success)
        {
            ShowErrors(Path.GetFileName(path ?? nameOrPath), result.Diagnostics);
            if (_session is not null && !initial) return false;
            // Fall back to the layout compiled into the program so the machine can always be operated.
            result = LayoutLoader.Load(ReadEmbeddedDefault(), "desktop", Path.Combine(AppContext.BaseDirectory, "layouts"), null, new LayoutValidator(_services.Commands));
            if (result.Document is null) return false;
            path = null;
        }
        else ClearBanner("errors");

        Apply(result.Document!, result.Diagnostics);
        _currentLayoutPath = path;
        Watch(path);
        if (path is not null)
        {
            _services.Settings.Layout = Path.GetFileNameWithoutExtension(path);
            _services.Settings.Save();
        }
        return true;
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
        _session?.Dispose();
        _session = new LayoutSession(document, _services);
        _layoutHost.Child = _session.Root;
        _layoutHost.Background = _session.Context.Theme.TokenBrush("background");
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

    private void Watch(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (path is null) return;
        try
        {
            // Watch the folder: editors often replace files rather than write them in place.
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _services.Console.Warning($"Live layout reload is unavailable: {ex.Message}");
        }
    }

    private void ScheduleReload() => Dispatcher.UIThread.Post(() =>
    {
        _reloadTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer!.Stop();
            if (_currentLayoutPath is not null && LoadLayout(_currentLayoutPath))
                _services.Console.Info($"Reloaded layout {Path.GetFileName(_currentLayoutPath)}.");
        });
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    // ------------------------------------------------------------------ keyboard shortcuts

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            _ = ReloadLayoutAsync();
            e.Handled = true;
            return;
        }
        // Plain keys go to text boxes; shortcuts with Ctrl/Alt (or function keys) always apply.
        var typing = FocusManager?.GetFocusedElement() is TextBox or AutoCompleteBox;
        if (_session?.MatchShortcut(e, typing) is { } shortcut)
        {
            e.Handled = true;
            _ = _services.Commands.ExecuteAsync(shortcut.Command, shortcut.Args);
        }
    }

    // ------------------------------------------------------------------ IAppHost

    public async Task OpenFileAsync(string? path)
    {
        if (path is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open G-code",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("G-code") { Patterns = ["*.nc", "*.gcode", "*.gc", "*.ngc", "*.cnc", "*.tap", "*.txt"] },
                    FilePickerFileTypes.All,
                ],
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
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
            if (LoadLayout(_currentLayoutPath ?? _services.Settings.Layout)) _services.Console.Info("Layout reloaded.");
        });
        return Task.CompletedTask;
    }

    public Task ExitAsync()
    {
        Dispatcher.UIThread.Post(Close);
        return Task.CompletedTask;
    }
}
