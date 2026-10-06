using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.Editor.Model;
using Carvera.Editor.Preview;
using Carvera.Editor.Views;
using Xunit;

namespace Carvera.Editor.Tests;

/// <summary>Renders the editor in several states to PNG files (only when CARVERA_SCREENSHOT_DIR is set), for looking at and for the documentation.</summary>
public class ScreenshotTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("CARVERA_SCREENSHOT_DIR") is { Length: > 0 } d ? d : null;

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static async Task Settle(Window window, int ms = 500)
    {
        for (var i = 0; i < ms / 50; i++)
        {
            Pump(window);
            await Task.Delay(50);
        }
        Pump(window);
    }

    private static void Shoot(Window window, string name)
    {
        if (Dir is not { } dir) return;
        Directory.CreateDirectory(dir);
        Pump(window);
        HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(dir, "editor-" + name + ".png"));
    }

    private static EditorWindow Open(string layout, double width = 1600, double height = 950)
    {
        var settings = new EditorSettings { WindowWidth = width, WindowHeight = height, PreviewSize = "Fill the pane" };
        var window = new EditorWindow(["--layout", layout], settings, new ControllerLink(), connect: false) { Width = width, Height = height };
        window.Show();
        Pump(window);
        return window;
    }

    [AvaloniaFact]
    public async Task Gallery()
    {
        if (Dir is null) return;
        var window = Open("desktop2");
        try
        {
            await Settle(window, 800);
            var ctx = window.Context;

            // A container with the structure outlines on.
            var container = window.Preview.Session!.Hosts.First(h => h.Node.Type == "grid");
            ctx.Select(container.Node.Path);
            await Settle(window);
            Shoot(window, "grid-selected");

            // Conditions and visuals of the Hold button.
            var hold = window.Preview.Session.Hosts.First(h => h.Node.Id == "hold");
            ctx.Select(hold.Node.Path);
            await Settle(window);
            Shoot(window, "button-conditions");

            // Theme, a style, a shortcut, window.
            ctx.Select("theme");
            await Settle(window);
            Shoot(window, "theme");
            ctx.Select("styles.run-button");
            await Settle(window);
            Shoot(window, "style");

            // A layout with a problem.
            ctx.Select("root");
            ctx.Edit("Break", d =>
            {
                d["root"]!["children"]![0]!["bogus"] = 1;
                ((JsonObject)d["root"]!["children"]![1]!)["type"] = "banana";
                return "";
            });
            await Settle(window);
            window.Tabs.SelectedIndex = 0;
            Shoot(window, "problems");
            ctx.Undo();
            await Settle(window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SmallWindow()
    {
        if (Dir is null) return;
        var window = Open("desktop", 1100, 700);
        try
        {
            await Settle(window, 800);
            Shoot(window, "small");
        }
        finally { window.Close(); }
    }
}
