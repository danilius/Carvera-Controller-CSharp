using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Components;
using Carvera.App.Services;
using Carvera.App.Shell;
using Carvera.Core.State;
using Xunit;

namespace Carvera.App.Tests;

public class RemoteFilesViewTests
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

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void SizesAreShownInHumanUnits(long bytes, string expected) => Assert.Equal(expected, RemoteFilesComponent.FormatSize(bytes));

    [AvaloniaFact]
    public async Task TheDesktopLayoutHasAMachineFilesTabThatListsTheSimulatedCard()
    {
        var settings = Settings.Load();
        settings.AutoConnect = false;
        settings.UploadDirectory = "/sd/gcodes";
        settings.Save();
        var window = new MainWindow(["--layout", "desktop", "--connect", "simulator"]) { Width = 1400, Height = 900 };
        window.Show();
        await Settle(window, 200);
        Assert.Contains(window.Session!.Hosts, h => h.Node.Type == "remoteFiles");

        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && window.Services.State.Get(StatePaths.RemoteCount, 0) < 3) await Settle(window, 100);
        await Settle(window, 300);
        Assert.Equal(3, window.Services.State.Get(StatePaths.RemoteCount, 0));

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = tabs.Items.Count - 1; // "Machine files" is the last tab; a tab's content exists only while it is shown
        await Settle(window, 300);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("old jobs", texts);
        Assert.Contains("demo part.nc", texts);
        Assert.Contains("notes.txt", texts);
        Assert.Contains("/sd/gcodes", texts);
        Assert.Contains("3 items", texts);

        // Clicking a row selects it; the toolbar's Delete then applies to it.
        var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "notes.txt");
        var at = label.TranslatePoint(new Avalonia.Point(4, 4), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        await Settle(window, 200);
        Assert.Equal("/sd/gcodes/notes.txt", window.Services.State.Get<string>(StatePaths.RemoteSelected));

        var output = Environment.GetEnvironmentVariable("CARVERA_SCREENSHOT_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(output);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
#pragma warning disable CS0618
        frame!.Save(Path.Combine(output, "machine-files.png"));
#pragma warning restore CS0618
        window.Close();
    }
}
