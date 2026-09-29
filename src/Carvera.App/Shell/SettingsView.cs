using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.App.Services;
using Carvera.Core.Pendant;
using Carvera.Core.State;

namespace Carvera.App.Shell;

/// <summary>
/// The settings page. It takes over the whole main window; the "Back" button returns to the layout.
/// One scrolling column holds every setting, grouped under headers, and the list on the left jumps to a group.
/// Changes apply immediately and are saved when a field loses focus or the page closes.
/// </summary>
public sealed class SettingsView : UserControl
{
    private static readonly IBrush PageBackground = Brush.Parse("#F1F4F8");
    private static readonly IBrush CardBackground = Brushes.White;
    private static readonly IBrush Border = Brush.Parse("#D8DEE7");
    private static readonly IBrush Text = Brush.Parse("#1E293B");
    private static readonly IBrush Muted = Brush.Parse("#64748B");

    private readonly AppServices _services;
    private readonly Settings _settings;
    private readonly Action _close;
    private readonly Action<string> _loadLayout;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly StackPanel _content = new() { Spacing = 8, Margin = new Thickness(24, 16, 24, 32), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ListBox _groups = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(8, 12) };
    private readonly Dictionary<string, Control> _headers = new();
    private TextBlock? _pendantStatus, _gamepadStatus;

    public SettingsView(AppServices services, Action close, Action<string> loadLayout)
    {
        _services = services;
        _settings = services.Settings;
        _close = close;
        _loadLayout = loadLayout;
        Background = PageBackground;
        Foreground = Text;

        var back = new Button { Content = "‹ Back to main view", Padding = new Thickness(14, 6), VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => Close();
        var title = new TextBlock { Text = "Settings", FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var bar = new Border
        {
            Background = CardBackground,
            BorderBrush = Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { back, title } },
        };

        BuildGeneral();
        BuildConnection();
        Build3dView();
        BuildUpload();
        BuildSharedPendantRules();
        BuildPendant();
        BuildGamepad();
        BuildMacros();

        foreach (var name in _headers.Keys) _groups.Items.Add(name);
        _groups.SelectedIndex = 0;
        _groups.SelectionChanged += (_, _) =>
        {
            if (_groups.SelectedItem is not string key || !_headers.TryGetValue(key, out var header)) return;
            header.BringIntoView();
        };
        _scroll.Content = _content;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
        var side = new Border { Background = CardBackground, BorderBrush = Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = _groups };
        Grid.SetColumn(_scroll, 1);
        body.Children.Add(side);
        body.Children.Add(_scroll);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;

        AttachedToVisualTree += (_, _) =>
        {
            _services.State.Changed += OnStateChanged;
            RefreshPendantStatus();
        };
        DetachedFromVisualTree += (_, _) => _services.State.Changed -= OnStateChanged;
    }

    public void Close()
    {
        _settings.Save();
        _close();
    }

    // ------------------------------------------------------------------ building blocks

    private Control Section(string name, out StackPanel rows)
    {
        var header = new TextBlock { Text = name, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 4), Tag = name };
        _headers[name] = header;
        _content.Children.Add(header);
        rows = new StackPanel { Spacing = 0 };
        _content.Children.Add(new Border
        {
            Background = CardBackground,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = rows,
        });
        return header;
    }

    private static Control Row(StackPanel rows, string label, string? description, Control editor, bool wide = false)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } } };
        if (description is not null) text.Children.Add(new TextBlock { Text = description, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(wide ? "*,360" : "*,240"), Margin = new Thickness(16, 10) };
        grid.Children.Add(text);
        Grid.SetColumn(editor, 1);
        if (editor is TextBox or ComboBox or NumericUpDown) editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Children.Add(editor);
        var row = new Border { BorderBrush = Border, BorderThickness = new Thickness(0, rows.Children.Count > 0 ? 1 : 0, 0, 0), Child = grid };
        rows.Children.Add(row);
        return row;
    }

    private ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = "On", OffContent = "Off" };
        toggle.IsCheckedChanged += (_, _) =>
        {
            set(toggle.IsChecked == true);
            _settings.Save();
            _services.Pendants.Apply();
            RefreshPendantStatus();
        };
        return toggle;
    }

    private NumericUpDown Number(int value, int min, int max, Action<int> set)
    {
        var box = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Value = Math.Clamp(value, min, max) };
        box.ValueChanged += (_, _) =>
        {
            if (box.Value is { } v) set((int)v);
            _settings.Save();
        };
        return box;
    }

    private TextBox Field(string value, Action<string> set, string? watermark = null, bool multiline = false)
    {
        var box = new TextBox { Text = value, PlaceholderText = watermark, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) box.MinHeight = 64;
        box.TextChanged += (_, _) => set(box.Text ?? "");
        box.LostFocus += (_, _) => _settings.Save();
        return box;
    }

    // ------------------------------------------------------------------ groups

    private void BuildGeneral()
    {
        Section("General", out var rows);
        var layouts = new ComboBox();
        var names = _services.Layouts.List().Select(l => l.Name).ToList();
        foreach (var name in names) layouts.Items.Add(name);
        layouts.SelectedItem = _services.State.Get<string>(StatePaths.LayoutName) ?? _settings.Layout;
        layouts.SelectionChanged += (_, _) =>
        {
            if (layouts.SelectedItem is string name && !name.Equals(_services.State.Get<string>(StatePaths.LayoutName), StringComparison.OrdinalIgnoreCase)) _loadLayout(name);
        };
        Row(rows, "Layout", "The layout shown in the main view. Ctrl+L also switches layouts.", layouts);
    }

    private void BuildConnection()
    {
        Section("Connection", out var rows);
        Row(rows, "Connect when the program starts", "Connects to the machine you used last. A simulator is never connected automatically.",
            Toggle(_settings.AutoConnect, v => _settings.AutoConnect = v));
        Row(rows, "Reconnect if the connection drops", "Uses the same retries and interval. Choosing Disconnect yourself is never undone.",
            Toggle(_settings.AutoReconnect, v => _settings.AutoReconnect = v));
        Row(rows, "Retry attempts", "How many more times to try if the machine does not answer. 0 tries once.",
            Number(_settings.AutoConnectRetries, 0, 999, v => _settings.AutoConnectRetries = v));
        Row(rows, "Seconds between attempts", "At least 1.",
            Number(_settings.AutoConnectIntervalSeconds, 1, 3600, v => _settings.AutoConnectIntervalSeconds = v));
    }

    private void Build3dView()
    {
        Section("3D view", out var rows);
        var renderer = new ComboBox();
        foreach (var option in new[] { "Auto", "GPU", "CPU" }) renderer.Items.Add(option);
        renderer.SelectedItem = _settings.ViewerRenderer;
        renderer.SelectionChanged += (_, _) =>
        {
            if (renderer.SelectedItem is string choice) _settings.ViewerRenderer = choice;
            _settings.Save();
        };
        var scheme = new ComboBox();
        foreach (var name in Layout.ColorSchemes.Names) scheme.Items.Add(name);
        scheme.SelectedItem = Layout.ColorSchemes.Names.Contains(_settings.GcodeColorScheme) ? _settings.GcodeColorScheme : Layout.ColorSchemes.Layout;
        scheme.SelectionChanged += (_, _) =>
        {
            if (scheme.SelectedItem is not string choice || choice == _settings.GcodeColorScheme) return;
            _settings.GcodeColorScheme = choice;
            _settings.Save();
            if (_services.State.Get<string>(StatePaths.LayoutName) is { Length: > 0 } current) _loadLayout(current); // rebuild with the new colours
        };
        Row(rows, "Operation colours", "The colours the operations of a G-code file are drawn in, in the 3D view, the G-code list and the operation list. " +
            "Layout uses whatever the layout's theme defines (operation1 to operation10).", scheme);
        Row(rows, "Draw the toolpath with", $"Auto uses the GPU for programs of {Components.Viewer.ToolpathView.AutoGpuSegments:N0} path segments or more, where the CPU renderer gets slow, and the CPU for smaller ones. " +
            "Choose CPU if the GPU view looks wrong; the app also falls back to the CPU by itself when the GPU cannot be used. The change applies when the view next redraws.", renderer);
    }

    private void BuildUpload()
    {
        Section("File upload", out var rows);
        Row(rows, "Folder on the machine", "Where the Upload button puts files, e.g. /sd/gcodes.",
            Field(_settings.UploadDirectory, v => _settings.UploadDirectory = v.Trim(), "/sd/gcodes"));
        var compression = new ComboBox();
        foreach (var option in new[] { "Auto", "On", "Off" }) compression.Items.Add(option);
        compression.SelectedItem = _settings.UploadCompression;
        compression.SelectionChanged += (_, _) =>
        {
            if (compression.SelectedItem is string choice) _settings.UploadCompression = choice;
            _settings.Save();
        };
        Row(rows, "Send as .lz files", "Auto uses .lz when the machine says it accepts it, as the Python controller does. The machine unpacks the file after the upload. Turn it off if uploads fail.", compression);
    }

    private void BuildPendant()
    {
        Section("CYD pendant", out var rows);
        Row(rows, "Use the CYD pendant", "Connects to the pendant over the network. The controller keeps retrying until it answers.",
            Toggle(_settings.CydEnabled, v => _settings.CydEnabled = v));
        Row(rows, "Host name or IP address", "Where the pendant listens, e.g. cyd-pendant.local.",
            Field(_settings.CydHost, v => _settings.CydHost = v.Trim(), "cyd-pendant.local"));
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Value = _settings.CydPort };
        port.ValueChanged += (_, _) =>
        {
            if (port.Value is { } v) _settings.CydPort = (int)v;
            _settings.Save();
        };
        Row(rows, "Port", "TCP port of the pendant firmware (default 9876).", port);
        _pendantStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Row(rows, "Status", null, _pendantStatus);
    }

    private void BuildSharedPendantRules()
    {
        Section("Pendants", out var rows);
        Row(rows, "Jogging from a pendant", "When off, no pendant can jog the machine. Stop, reset and pause always work.",
            Toggle(_settings.PendantJoggingDefault, v => _settings.PendantJoggingDefault = v));
        Row(rows, "Allow jogging while the machine is running", "Off by default: a pendant may only jog when the machine is idle or paused.",
            Toggle(_settings.AllowJoggingWhileRunning, v => _settings.AllowJoggingWhileRunning = v));
        Row(rows, "Allow jogging while the spindle is on", null,
            Toggle(_settings.AllowJoggingWhileSpindleOn, v => _settings.AllowJoggingWhileSpindleOn = v));
    }

    private void BuildGamepad()
    {
        Section("Gamepad", out var rows);
        Row(rows, "Use a gamepad", "Any pad SDL knows works: Xbox, PlayStation, Switch Pro and generic USB pads. Press a button on the pad you want to use.",
            Toggle(_settings.GamepadEnabled, v => _settings.GamepadEnabled = v));
        _gamepadStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Row(rows, "Status", null, _gamepadStatus);

        var preset = new ComboBox();
        foreach (var name in GamepadBindingsStore.PresetNames) preset.Items.Add(name);
        preset.SelectedItem = GamepadBindingsStore.PresetNames.Contains(_settings.GamepadPreset) ? _settings.GamepadPreset : GamepadBindingsStore.CustomPreset;
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedItem is not string choice) return;
            _settings.GamepadPreset = choice;
            if (choice != GamepadBindingsStore.CustomPreset) GamepadBindingsStore.WritePreset(choice);
            _settings.Save();
            _services.Pendants.ReloadGamepadBindings();
        };
        Row(rows, "Button layout", "Choosing a preset writes it to the bindings file, replacing any changes made there.", preset);

        var edit = new Button { Content = "Open the bindings file", HorizontalAlignment = HorizontalAlignment.Left };
        edit.Click += (_, _) =>
        {
            if (!File.Exists(GamepadBindingsStore.FilePath)) _services.Pendants.ReloadGamepadBindings(); // creates it from the preset
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GamepadBindingsStore.FilePath) { UseShellExecute = true }); }
            catch (Exception ex) { _services.Console.Error($"Could not open {GamepadBindingsStore.FilePath}: {ex.Message}"); }
        };
        var reload = new Button { Content = "Reload the bindings" };
        reload.Click += (_, _) =>
        {
            _settings.GamepadPreset = GamepadBindingsStore.CustomPreset;
            preset.SelectedItem = GamepadBindingsStore.CustomPreset;
            _services.Pendants.ReloadGamepadBindings();
        };
        Row(rows, "Bindings file", GamepadBindingsStore.FilePath + ". Edit it, save, then reload. Actions are listed in docs/pendants.md.",
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { edit, reload } });

        Row(rows, "Stick dead zone", "Fraction of the stick's travel that is ignored (0.01 to 0.99).",
            Decimal(_settings.GamepadDeadzone, 0.01, 0.99, 0.05, v => _settings.GamepadDeadzone = v));
        Row(rows, "Fastest continuous jog (mm/min)", "Reached with the largest step size; Z is capped at 800.",
            Decimal(_settings.GamepadMaxJogSpeed, 100, 10000, 100, v => _settings.GamepadMaxJogSpeed = v));
        Row(rows, "Invert X", null, Toggle(_settings.GamepadInvertX, v => _settings.GamepadInvertX = v));
        Row(rows, "Invert Y", null, Toggle(_settings.GamepadInvertY, v => _settings.GamepadInvertY = v));
        Row(rows, "Invert Z", null, Toggle(_settings.GamepadInvertZ, v => _settings.GamepadInvertZ = v));
        Row(rows, "Invert A", null, Toggle(_settings.GamepadInvertA, v => _settings.GamepadInvertA = v));
    }

    private NumericUpDown Decimal(double value, double min, double max, double step, Action<double> set)
    {
        var box = new NumericUpDown { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = step < 1 ? "0.00" : "0", Value = (decimal)Math.Clamp(value, min, max) };
        box.ValueChanged += (_, _) =>
        {
            if (box.Value is { } v) set((double)v);
            _settings.Save();
        };
        return box;
    }

    private void BuildMacros()
    {
        var macros = _settings.EnsureMacroSlots();
        Section("Macros", out var rows);
        rows.Children.Add(new TextBlock
        {
            Text = "Macros appear on the pendant by name. A macro needs both a name and G-code to be offered; one G-code line per line.",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 10, 16, 2),
        });
        for (var i = 0; i < macros.Count; i++)
        {
            var macro = macros[i];
            var editor = new StackPanel { Spacing = 6 };
            editor.Children.Add(Field(macro.Name, v => macro.Name = v.Trim(), "Name (up to 24 characters)"));
            editor.Children.Add(Field(macro.Gcode, v => macro.Gcode = v, "G-code", multiline: true));
            Row(rows, $"Macro {i + 1}", null, editor, wide: true);
        }
    }

    // ------------------------------------------------------------------ live status

    private void OnStateChanged(IReadOnlyCollection<string> paths)
    {
        if (paths.Contains(StatePaths.PendantConnected) || paths.Contains(PendantStatus.ConnectedPath("cyd")) || paths.Contains(PendantStatus.ConnectedPath("gamepad")))
            Dispatcher.UIThread.Post(RefreshPendantStatus);
    }

    private void RefreshPendantStatus()
    {
        Show(_pendantStatus, _settings.CydEnabled, _services.State.Get(PendantStatus.ConnectedPath("cyd"), false), "Waiting for the pendant", null);
        Show(_gamepadStatus, _settings.GamepadEnabled, _services.State.Get(PendantStatus.ConnectedPath("gamepad"), false), "Waiting: press a button on the gamepad",
            _services.State.Get<string>(PendantStatus.NamePath("gamepad")));
    }

    private static void Show(TextBlock? label, bool enabled, bool connected, string waiting, string? name)
    {
        if (label is null) return;
        if (!enabled)
        {
            label.Text = "Not in use";
            label.Foreground = Muted;
        }
        else if (connected)
        {
            label.Text = "● Connected" + (name is null ? "" : $": {name}");
            label.Foreground = Brush.Parse("#16A34A");
        }
        else
        {
            label.Text = "○ " + waiting;
            label.Foreground = Brush.Parse("#D97706");
        }
    }
}
