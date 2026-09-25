using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Shell;
using Carvera.Core.Commands;
using Carvera.Core.State;
using Xunit;

namespace Carvera.App.Tests;

public class ProgramTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "samples", "demo.nc");

    private static async Task Settle(MainWindow window, int milliseconds = 200)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            window.Services.Binder.Flush();
            await Task.Delay(10);
        }
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public async Task OperationsToolsAndScrubbing()
    {
        var window = new MainWindow(["--layout", "desktop"]) { Width = 1400, Height = 900 };
        window.Show();
        await window.OpenFileAsync(Sample);
        await Settle(window);
        var services = window.Services;
        var program = services.Program!;
        Assert.Equal(["Adaptive pocket", "Outer contour", "Engrave circles", "Ellipse finish"], program.Operations.Select(o => o.Name));
        Assert.Equal([1, 1, 2, 2], program.Operations.Select(o => o.Tool));
        Assert.Equal(4, services.State.Get<int>(StatePaths.FileOperations));

        // The operations list shows a tool selector per operation; the tool list shows both tools.
        var combos = window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is int)).ToList();
        Assert.Equal(4, combos.Count);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("T1", texts);
        Assert.Contains("T2", texts);
        Assert.Contains("Ø3.175 mm  ·  flat end mill", texts);

        // Scrubbing.
        await services.Commands.ExecuteAsync("previewStep", CommandArgs.Empty.With("delta", 10));
        await Settle(window);
        Assert.Equal(9, services.State.Get<int>(StatePaths.PreviewSegment));
        Assert.True(services.State.Get<bool>(StatePaths.PreviewActive));
        services.SetPreviewLine(program.Operations[2].StartLine + 5);
        await Settle(window);
        Assert.Equal(2, program.OperationOfLine(services.State.Get<int>(StatePaths.PreviewLine)));
        await services.Commands.ExecuteAsync("previewClear");
        await Settle(window);
        Assert.Equal(-1, services.State.Get<int>(StatePaths.PreviewSegment));

        // Changing the tool of an operation that inherits its tool inserts a tool change.
        Assert.True(await services.Commands.ExecuteAsync("setOperationTool", CommandArgs.Empty.With("operation", 1).With("tool", 3)));
        await Settle(window);
        Assert.Equal([1, 3, 2, 2], services.Program!.Operations.Select(o => o.Tool));
        Assert.True(services.State.Get<bool>(StatePaths.FileModified));

        // Saving writes the edited program.
        var output = Path.Combine(Path.GetTempPath(), $"carvera-edit-{Guid.NewGuid():N}.nc");
        await services.Commands.ExecuteAsync("saveFile", CommandArgs.Empty.With("path", output));
        await Settle(window);
        Assert.Contains("T3 M6", File.ReadAllLines(output));
        Assert.False(services.State.Get<bool>(StatePaths.FileModified));
        File.Delete(output);
        window.Close();
    }
}
