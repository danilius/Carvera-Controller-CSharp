using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Services;
using Carvera.App.Shell;
using Carvera.Core.State;
using Xunit;

namespace Carvera.App.Tests;

public class WindowTests
{
    private static async Task Settle(MainWindow window, int milliseconds = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            window.Services.Binder.Flush();
            await Task.Delay(15);
        }
        window.UpdateLayout();
    }

    public static TheoryData<string> Layouts() => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "layouts"), "*.json").Select(Path.GetFileNameWithoutExtension)!);

    [AvaloniaTheory]
    [MemberData(nameof(Layouts))]
    public async Task ShippedLayoutsBuildWithoutErrors(string name)
    {
        var window = new MainWindow(["--layout", name]) { Width = 1400, Height = 900 };
        window.Show();
        await Settle(window);
        Assert.Equal(name, window.Services.State.Get<string>(StatePaths.LayoutName));
        Assert.False(window.HasBanner("errors"));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("⚠") == true);
        Assert.False(window.HasBanner("safety"));
        // Layout switching lives in the settings page and Ctrl+L, not in the layouts.
        Assert.DoesNotContain(window.Session!.Hosts, h => h.Node.Type == "layoutSelector");
        Assert.DoesNotContain(window.Services.Console.Entries, e => e.Kind == Core.ConsoleEntryKind.Error);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BrokenLayoutShowsErrorsAndFallsBack()
    {
        var folder = Path.Combine(Settings.Directory, "layouts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "broken.json"), """{ "root": { "type": "stak" } }""");
        var window = new MainWindow(["--layout", "broken"]);
        window.Show();
        await Settle(window);
        Assert.True(window.HasBanner("errors"));
        Assert.NotNull(window.Session); // the built-in layout keeps the machine operable
        Assert.Contains(window.Services.Console.Entries, e => e.Text.Contains("Did you mean 'stack'"));

        // A failed reload keeps the current layout. A second good layout is a copy of the shipped one, in the user's folder.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "layouts", "desktop.json"), Path.Combine(folder, "second.json"), overwrite: true);
        Assert.True(window.LoadLayout("second"));
        Assert.False(window.HasBanner("errors"));
        Assert.False(window.LoadLayout("broken"));
        Assert.Equal("second", window.Services.State.Get<string>(StatePaths.LayoutName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALayoutWithoutResetShowsTheSafetyWarning()
    {
        var folder = Path.Combine(Settings.Directory, "layouts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "no-reset.json"), """
            { "root": { "type": "stack", "children": [
              { "type": "button", "text": "Hold", "command": "feedHold" }, { "type": "button", "text": "Stop", "command": "stop" }
            ] } }
            """);
        var window = new MainWindow(["--layout", "no-reset"]);
        window.Show();
        await Settle(window);
        Assert.False(window.HasBanner("errors"));
        Assert.True(window.HasBanner("safety"));
        Assert.Contains(window.Services.Console.Entries, e => e.Text.StartsWith("Safety: this layout has no visible Reset"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task RendersLayoutPreviews()
    {
        // Off-screen renders of each layout, connected to the simulator (useful for documentation and review).
        var output = Environment.GetEnvironmentVariable("CARVERA_SCREENSHOT_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(output);
        foreach (var (name, width, height) in new[] { ("desktop", 1400, 900), ("desktop2", 1400, 900) })
        {
            var window = new MainWindow(["--layout", name, "--connect", "simulator"]) { Width = width, Height = height };
            window.Show();
            await Settle(window, 800);
            var sample = Path.Combine(AppContext.BaseDirectory, "samples", "demo.nc");
            if (File.Exists(sample)) await window.OpenFileAsync(sample);
            // Scrub part-way through so the preview shows executed, pending and per-operation colours.
            if (window.Services.Program is { } program) window.Services.SetPreviewLine(program.Operations[2].StartLine + 10);
            window.Services.State.Set("switch.light", true);
            await Settle(window, 300);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            #pragma warning disable CS0618 // the options overload has no public implementation to pass yet
            frame!.Save(Path.Combine(output, $"{name}.png"));
#pragma warning restore CS0618
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SettingsSearchFindsWordsInLabelsAndDescriptions()
    {
        var window = new MainWindow(["--layout", "desktop2"]) { Width = 1280, Height = 800 };
        window.Show();
        await Settle(window);
        window.OpenSettings();
        await Settle(window, 100);
        var page = window.SettingsPage!;
        var search = page.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText is not null && t.PlaceholderText.StartsWith("Search"));
        var groups = page.GetVisualDescendants().OfType<ListBox>().First();
        var allGroups = groups.Items.Cast<object>().ToList();
        Assert.Contains("Machine settings", allGroups.Cast<string>());

        // A word that is only in a description: "answer" is in the retry setting's explanation, not in its label.
        search.Text = "does not answer";
        await Settle(window, 100);
        Assert.Equal(["Connection"], groups.Items.Cast<string>());

        // A word in a label of another group, in any letter case.
        search.Text = "STICK dead ZONE";
        await Settle(window, 100);
        Assert.Equal(["Gamepad"], groups.Items.Cast<string>());

        // A group's own name shows the whole group.
        search.Text = "macros";
        await Settle(window, 100);
        Assert.Equal(["Macros"], groups.Items.Cast<string>());

        search.Text = "zzzz nothing like this";
        await Settle(window, 100);
        Assert.Empty(groups.Items.Cast<string>());

        search.Text = "";
        await Settle(window, 100);
        Assert.Equal(allGroups, groups.Items.Cast<object>().ToList());
        window.Close();
    }

    [AvaloniaFact]
    public async Task RendersEveryPageOfDesktop2()
    {
        var output = Environment.GetEnvironmentVariable("CARVERA_SCREENSHOT_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(output);
        var window = new MainWindow(["--layout", "desktop2", "--connect", "simulator"]) { Width = 1400, Height = 900 };
        window.Show();
        await Settle(window, 800);
        var sample = Path.Combine(AppContext.BaseDirectory, "samples", "demo.nc");
        if (File.Exists(sample)) await window.OpenFileAsync(sample);
        await Settle(window, 300);
        var pages = window.GetVisualDescendants().OfType<TabControl>().First();
        for (var i = 0; i < pages.ItemCount; i++)
        {
            pages.SelectedIndex = i;
            await Settle(window, 250);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            #pragma warning disable CS0618
            frame!.Save(Path.Combine(output, $"desktop2-page{i}.png"));
            #pragma warning restore CS0618
        }
        window.Close();

        // While a job runs the right-hand column swaps the jog controls for the overrides. (Not connected, so no status report resets the state.)
        var idle = new MainWindow(["--layout", "desktop2"]) { Width = 1400, Height = 900 };
        idle.Show();
        await Settle(idle, 300);
        if (File.Exists(sample)) await idle.OpenFileAsync(sample);
        Assert.NotNull(idle.Session!.FindById("jogPanel"));
        Assert.True(idle.Session.FindById("jogPanel")!.IsVisible);
        Assert.False(idle.Session.FindById("runningPanels")!.IsVisible);
        idle.Services.State.Set("job.playing", true);
        await Settle(idle, 300);
        Assert.False(idle.Session.FindById("jogPanel")!.IsVisible);
        Assert.True(idle.Session.FindById("runningPanels")!.IsVisible);
        var running = idle.CaptureRenderedFrame();
        #pragma warning disable CS0618
        running!.Save(Path.Combine(output, "desktop2-running.png"));
        #pragma warning restore CS0618
        idle.Close();
    }

    [AvaloniaFact]
    public async Task SettingsButtonFlipsBetweenLayoutAndSettingsInEveryLayout()
    {
        foreach (var name in new[] { "desktop", "desktop2" })
        {
            var window = new MainWindow(["--layout", name]) { Width = 1280, Height = 800 };
            window.Show();
            await Settle(window);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SettingsButton");
            Assert.True(button.IsVisible);
            Assert.False(window.IsSettingsOpen);

            // The button has its own column: it must not overlap the layout or any of its controls.
            var origin = button.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
            var buttonRect = new Avalonia.Rect(origin, button.Bounds.Size);
            var rootOrigin = window.Session!.Root.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
            var layoutRect = new Avalonia.Rect(rootOrigin, window.Session.Root.Bounds.Size);
            Assert.False(buttonRect.Intersects(layoutRect), $"{name}: the settings button overlaps the layout");
            foreach (var host in window.Session.Hosts)
            {
                var p = host.TranslatePoint(new Avalonia.Point(0, 0), window);
                if (p is null) continue;
                Assert.False(buttonRect.Intersects(new Avalonia.Rect(p.Value, host.Bounds.Size)), $"{name}: the settings button overlaps a {host.Node.Type}");
            }

            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle(window, 100);
            Assert.True(window.IsSettingsOpen);
            Assert.False(button.IsEffectivelyVisible); // the settings page covers the button while it is open
            var page = window.SettingsPage!;
            Assert.True(page.IsEffectivelyVisible);
            // Each group has a header, and the list on the left names the same groups.
            var texts = page.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            string[] groups = ["General", "Connection", "Jogging", "3D view", "File upload", "Pendants", "CYD pendant", "Gamepad", "Macros", "Machine settings"];
            foreach (var group in groups) Assert.Contains(group, texts);
            Assert.Contains(page.GetVisualDescendants().OfType<ListBox>(), l => l.Items.Cast<object>().SequenceEqual(groups));

            var back = page.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "‹ Back to main view");
            back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle(window, 100);
            Assert.False(window.IsSettingsOpen);
            Assert.Equal(name, window.Services.State.Get<string>(StatePaths.LayoutName));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EnablingThePendantInSettingsStartsAndStopsIt()
    {
        var window = new MainWindow(["--layout", "desktop"]) { Width = 1280, Height = 800 };
        window.Show();
        await Settle(window);
        var settings = window.Services.Settings;
        var wasEnabled = settings.CydEnabled;
        window.OpenSettings();
        await Settle(window, 100);
        var label = window.SettingsPage!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Use the CYD pendant");
        var toggle = label.GetVisualAncestors().OfType<Grid>().First().GetVisualDescendants().OfType<ToggleSwitch>().First();
        settings.CydHost = "127.0.0.1";
        settings.CydPort = 1; // nothing listens here; the pendant just keeps retrying
        toggle.IsChecked = true;
        await Settle(window, 100);
        Assert.NotNull(window.Services.Pendants.Cyd);
        toggle.IsChecked = false;
        await Settle(window, 100);
        Assert.Null(window.Services.Pendants.Cyd);
        settings.CydEnabled = wasEnabled;
        window.Close();
    }

    [AvaloniaFact]
    public async Task RendersSettingsPreview()
    {
        var output = Environment.GetEnvironmentVariable("CARVERA_SCREENSHOT_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(output);
        var window = new MainWindow(["--layout", "desktop"]) { Width = 1280, Height = 800 };
        window.Show();
        await Settle(window);
        window.OpenSettings();
        await Settle(window, 300);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
#pragma warning disable CS0618
        frame!.Save(Path.Combine(output, "settings.png"));
#pragma warning restore CS0618
        window.Close();
    }

    [AvaloniaFact]
    public async Task WindowPlacementIsRememberedBetweenRuns()
    {
        var settingsFile = Path.Combine(Settings.Directory, "settings.json");
        if (File.Exists(settingsFile)) File.Delete(settingsFile);
        var first = new MainWindow(["--layout", "desktop"]) { Width = 1111, Height = 777 };
        first.Position = new Avalonia.PixelPoint(120, 90);
        first.Show();
        await Settle(first);
        first.Position = new Avalonia.PixelPoint(140, 100);
        first.Width = 1000;
        first.Height = 700;
        await Settle(first);
        first.Close();

        var saved = Settings.Load();
        Assert.Equal(140, saved.WindowX);
        Assert.Equal(100, saved.WindowY);
        Assert.True(saved.WindowWidth is > 900 and < 1100, $"width {saved.WindowWidth}");
        Assert.False(saved.WindowMaximized);

        var second = new MainWindow(["--layout", "desktop"]);
        second.Show();
        await Settle(second);
        Assert.Equal(new Avalonia.PixelPoint(140, 100), second.Position);
        Assert.InRange(second.ClientSize.Width, 900, 1100);
        second.Close();
    }

    [AvaloniaFact]
    public async Task MaximisedStateIsRememberedAndTheNormalSizeKept()
    {
        var settings = Settings.Load();
        settings.WindowX = 60;
        settings.WindowY = 60;
        settings.WindowWidth = 1000;
        settings.WindowHeight = 700;
        settings.WindowMaximized = true;
        settings.Save();
        var window = new MainWindow(["--layout", "desktop"]);
        window.Show();
        await Settle(window);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        window.Close();
        var saved = Settings.Load();
        Assert.True(saved.WindowMaximized);
        Assert.Equal(1000, saved.WindowWidth);
        Assert.Equal(60, saved.WindowX);
    }

    [AvaloniaFact]
    public async Task ASavedPositionOffAllScreensFallsBackToTheCentre()
    {
        var settings = Settings.Load();
        settings.WindowX = 500000;
        settings.WindowY = 500000;
        settings.WindowMaximized = false;
        settings.Save();
        var window = new MainWindow(["--layout", "desktop"]);
        Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ConnectsAutomaticallyAtStartUpAndRetriesUntilTheMachineAnswers()
    {
        // Reserve a port, then let the "machine" start listening only after the first attempts have failed.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var settings = Settings.Load();
        settings.AutoConnect = true;
        settings.AutoConnectRetries = 6;
        settings.AutoConnectIntervalSeconds = 1;
        settings.ConnectionKind = "wifi";
        settings.ConnectionAddress = $"127.0.0.1:{port}";
        settings.Save();

        var window = new MainWindow(["--layout", "desktop"]);
        window.Show();
        await Settle(window, 200);
        Assert.False(window.Services.Controller.IsConnected);

        var machine = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        await Task.Delay(1600);
        machine.Start();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && !window.Services.Controller.IsConnected) await Settle(window, 100);
        Assert.True(window.Services.Controller.IsConnected);
        window.Close();
        machine.Stop();
    }

    [AvaloniaFact]
    public async Task UploadButtonSendsTheOpenFileToTheMachine()
    {
        var settings = Settings.Load();
        settings.AutoConnect = false;
        settings.Save();
        var window = new MainWindow(["--layout", "desktop", "--connect", "simulator"]) { Width = 1400, Height = 900 };
        window.Show();
        await Settle(window, 800);
        var button = window.Session!.Hosts.Single(h => h.Node.Type == "button" && h.Node.GetString("command") == "uploadFile");
        Assert.Equal("Upload", window.Session.Hosts.Single(h => h.Node.GetString("command") == "uploadFile").Node.GetString("text"));
        Assert.True(button.IsEffectivelyEnabled);

        var sample = Path.Combine(AppContext.BaseDirectory, "samples", "demo.nc");
        Assert.True(File.Exists(sample));
        Assert.True(await window.Services.Commands.ExecuteAsync("uploadFile", Carvera.Core.Commands.CommandArgs.Empty.With("path", sample)));
        await Settle(window, 200);
        Assert.Equal("Uploaded demo.nc.", window.Services.State.Get<string>(StatePaths.TransferMessage));
        Assert.False(window.Services.State.Get<bool>(StatePaths.TransferActive));
        Assert.DoesNotContain(window.Services.Console.Entries, e => e.Kind == Core.ConsoleEntryKind.Error);
        window.Close();
    }

    [AvaloniaFact]
    public async Task UploadIsUnavailableWithoutAMachine()
    {
        var settings = Settings.Load();
        settings.AutoConnect = false;
        settings.Save();
        var window = new MainWindow(["--layout", "desktop"]) { Width = 1400, Height = 900 };
        window.Show();
        await Settle(window);
        Assert.False(window.Services.Commands.CanExecute("uploadFile", Carvera.Core.Commands.CommandArgs.Empty));
        var button = window.Session!.Hosts.Single(h => h.Node.Type == "button" && h.Node.GetString("command") == "uploadFile");
        Assert.False(button.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AutoConnectCanBeSwitchedOff()
    {
        var settings = Settings.Load();
        settings.AutoConnect = false;
        settings.ConnectionKind = "wifi";
        settings.ConnectionAddress = "127.0.0.1:1";
        settings.Save();
        var window = new MainWindow(["--layout", "desktop"]);
        window.Show();
        await Settle(window, 500);
        Assert.DoesNotContain(window.Services.Console.Entries, e => e.Text.StartsWith("Auto-connect") || e.Text.StartsWith("Connection to"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReconnectsWhenTheMachineDropsTheConnection()
    {
        var machine = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        machine.Start();
        var port = ((System.Net.IPEndPoint)machine.LocalEndpoint).Port;
        var accepted = new List<System.Net.Sockets.TcpClient>();
        _ = Task.Run(async () =>
        {
            try { while (true) { var client = await machine.AcceptTcpClientAsync(); lock (accepted) accepted.Add(client); } }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException) { }
        });

        var settings = Settings.Load();
        settings.AutoConnect = true;
        settings.AutoReconnect = true;
        settings.AutoConnectRetries = 5;
        settings.AutoConnectIntervalSeconds = 1;
        settings.ConnectionKind = "wifi";
        settings.ConnectionAddress = $"127.0.0.1:{port}";
        settings.Save();

        var window = new MainWindow(["--layout", "desktop"]);
        window.Show();
        async Task<bool> Until(Func<bool> condition, int seconds = 10)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && !condition()) await Settle(window, 100);
            return condition();
        }
        Assert.True(await Until(() => window.Services.Controller.IsConnected));

        // The machine hangs up.
        System.Net.Sockets.TcpClient first;
        lock (accepted) first = accepted[0];
        first.Close();
        Assert.True(await Until(() => window.Services.Console.Entries.Any(e => e.Text == "Connection lost. Trying to reconnect.")));
        Assert.True(await Until(() => { lock (accepted) return accepted.Count >= 2; }));
        Assert.True(await Until(() => window.Services.Controller.IsConnected));

        // Choosing Disconnect is respected: no reconnecting afterwards.
        await window.Services.Controller.DisconnectAsync();
        await Task.Delay(1800);
        lock (accepted) Assert.Equal(2, accepted.Count);
        Assert.False(window.Services.Controller.IsConnected);
        window.Close();
        machine.Stop();
    }
}
