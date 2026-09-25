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
        Assert.Equal(name == "canvas-demo", window.HasBanner("safety"));
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

        // A failed reload keeps the current layout.
        Assert.True(window.LoadLayout("touch"));
        Assert.False(window.HasBanner("errors"));
        Assert.False(window.LoadLayout("broken"));
        Assert.Equal("touch", window.Services.State.Get<string>(StatePaths.LayoutName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task RendersLayoutPreviews()
    {
        // Off-screen renders of each layout, connected to the simulator (useful for documentation and review).
        var output = Environment.GetEnvironmentVariable("CARVERA_SCREENSHOT_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(output);
        foreach (var (name, width, height) in new[] { ("desktop", 1400, 900), ("touch", 1280, 800), ("canvas-demo", 1280, 720) })
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
}
