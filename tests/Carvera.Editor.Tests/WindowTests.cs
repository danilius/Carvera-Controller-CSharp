using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.Editor.Model;
using Carvera.Editor.Views;
using Xunit;

namespace Carvera.Editor.Tests;

public class WindowTests
{
    private static string ScreenshotDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CARVERA_SCREENSHOT_DIR");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetTempPath(), "carvera-editor-shots") : dir;
        }
    }

    private static EditorWindow Open(string layout, double width = 1600, double height = 950)
    {
        var settings = new EditorSettings { WindowWidth = width, WindowHeight = height, PreviewSize = "Fill the pane" };
        var window = new EditorWindow(["--layout", layout], settings, new ControllerLink(), connect: false);
        window.Width = width;
        window.Height = height;
        window.Show();
        Pump(window);
        return window;
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void Shoot(Window window, string name)
    {
        Directory.CreateDirectory(ScreenshotDir);
        Pump(window);
        var frame = HeadlessWindowExtensions.CaptureRenderedFrame(window);
        frame?.Save(Path.Combine(ScreenshotDir, name + ".png"));
    }

    private static async Task Settle(Window window, int ms = 400)
    {
        for (var i = 0; i < ms / 50; i++)
        {
            Pump(window);
            await Task.Delay(50);
        }
        Pump(window);
    }

    [AvaloniaFact]
    public async Task TheEditorOpensAShippedLayoutAndDrawsThePreview()
    {
        var window = Open("desktop2");
        try
        {
            Assert.True(window.Context.Doc.IsValid, string.Join("; ", window.Context.Doc.Diagnostics));
            Assert.NotNull(window.Preview.Session);
            Assert.True(window.Preview.Session!.Hosts.Count > 20);
            await Settle(window);
            Shoot(window, "editor-desktop2");

            // Selecting a button shows its properties.
            var button = window.Preview.Session.Hosts.First(h => h.Node.Type == "button" && h.Node.Has("visuals"));
            window.Context.Select(button.Node.Path);
            await Settle(window);
            Shoot(window, "editor-desktop2-button");

            window.Tabs.SelectedIndex = 1;
            await Settle(window);
            Shoot(window, "editor-desktop2-code");
        }
        finally { window.Close(); }
    }
}
