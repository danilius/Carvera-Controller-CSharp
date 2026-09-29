using System.Numerics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Components.Viewer;
using Carvera.Core;
using Carvera.Core.Gcode;
using Xunit;

namespace Carvera.App.Tests;

public class GpuViewerTests
{
    private const string ViewerLayout = """
        { "root": { "type": "stack", "children": [
          { "type": "toolpath", "id": "view" },
          { "type": "button", "command": "feedHold", "height": 1 }, { "type": "button", "command": "stop", "height": 1 }, { "type": "button", "command": "reset", "height": 1 }
        ] } }
        """;

    private static ToolpathView View(Harness h) => h.Host("view").GetVisualDescendants().OfType<ToolpathView>().Single();

    private static GcodeProgram Program(int lines)
    {
        var text = new List<string> { "G21 G90" };
        for (var i = 0; i < lines; i++) text.Add($"G1 X{(i % 200) * 0.5:0.##} Y{(i / 200) * 0.1:0.##} Z{-(i % 7) * 0.1:0.##} F800");
        return GcodeProgram.Parse(text, null);
    }

    private static async Task Wait(Harness h, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            h.Pump();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task SmallProgramsStayOnTheCpuInAutoMode()
    {
        using var h = new Harness(ViewerLayout, 800, 600);
        h.Services.SetProgram(Program(500));
        await Wait(h, 200);
        Assert.False(View(h).GpuActive);
    }

    [AvaloniaFact]
    public async Task LargeProgramsAskForTheGpuInAutoModeAndFallBackWhenThereIsNoOpenGl()
    {
        var previous = ToolpathView.GpuWait;
        ToolpathView.GpuWait = TimeSpan.FromMilliseconds(300); // the headless platform has no OpenGL
        try
        {
            using var h = new Harness(ViewerLayout, 800, 600);
            h.Services.SetProgram(Program(ToolpathView.AutoGpuSegments + 10));
            await Wait(h, 100);
            Assert.True(View(h).GpuActive, "a large program is handed to the GPU layer");

            await Wait(h, 800);
            var view = View(h);
            Assert.False(view.GpuActive);
            Assert.NotNull(view.GpuFailure);
            Assert.Contains(h.Services.Console.Entries, e => e.Kind == ConsoleEntryKind.Warning && e.Text.Contains("CPU renderer"));

            // A frame is still drawn after falling back, and the layers do not come back.
            h.Services.SetProgram(Program(ToolpathView.AutoGpuSegments + 20));
            await Wait(h, 200);
            Assert.False(View(h).GpuActive);
        }
        finally
        {
            ToolpathView.GpuWait = previous;
        }
    }

    [AvaloniaFact]
    public async Task TheRendererSettingForcesTheChoice()
    {
        using var h = new Harness(ViewerLayout, 800, 600);
        h.Services.Settings.ViewerRenderer = "CPU";
        h.Services.SetProgram(Program(ToolpathView.AutoGpuSegments + 10));
        await Wait(h, 150);
        Assert.False(View(h).GpuActive);

        h.Services.Settings.ViewerRenderer = "GPU";
        h.Services.SetProgram(Program(200)); // even a small one
        await Wait(h, 150);
        Assert.True(View(h).GpuActive);
    }

    [Fact]
    public void VertexDataHoldsPositionsAndTheNumbersTheShaderColoursBy()
    {
        var starts = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) };
        var ends = new[] { new Vector3(7, 8, 9), new Vector3(10, 11, 12) };
        var data = GlPathLayer.Build(starts, ends, [0, 2], [10, 20], [false, true]);
        Assert.Equal(2 * 2 * GlPathLayer.FloatsPerVertex, data.Length);
        // segment 1, start vertex: x y z, segment, operation, line, rapid
        Assert.Equal(new float[] { 4, 5, 6, 1, 2, 20, 1 }, data[(2 * GlPathLayer.FloatsPerVertex)..(3 * GlPathLayer.FloatsPerVertex)]);
        // segment 0, end vertex
        Assert.Equal(new float[] { 7, 8, 9, 0, 0, 10, 0 }, data[GlPathLayer.FloatsPerVertex..(2 * GlPathLayer.FloatsPerVertex)]);
    }
}
