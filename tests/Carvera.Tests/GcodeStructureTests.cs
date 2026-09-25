using Carvera.Core.Gcode;
using Xunit;

namespace Carvera.Tests;

public class GcodeStructureTests
{
    // Shaped like the output of the Carvera Community post for Fusion (Carvera.cps).
    private static readonly string[] Fusion =
    [
        "(Bracket)",
        "(T1  3.175 flat  Makera  M1  D=3.175 CR=0 - ZMIN=-6 - flat end mill)",
        "(T2  6mm ball  D=6 CR=3 - ZMIN=-4 - ball end mill)",
        "G90 G94",
        "G17",
        "G21",
        "",
        "(2D Adaptive1)",
        "T1M6",
        "(ZMIN=-6)",
        "S12000 M3",
        "M7",
        "G0 X0 Y0",
        "G1 Z-1 F300",
        "G1 X10",
        "(2D Contour1)",
        "G1 X20 Y5",
        "G0 Z15",
        "M5",
        "",
        "(Scallop1)",
        "T2 M6 S3",
        "S9000 M3",
        "G1 X0 Y0 Z-2",
        "M30",
    ];

    [Fact]
    public void FindsFusionOperationsAndTools()
    {
        var p = GcodeProgram.Parse(Fusion);
        Assert.Equal(["2D Adaptive1", "2D Contour1", "Scallop1"], p.Operations.Select(o => o.Name));
        Assert.Equal([1, 1, 2], p.Operations.Select(o => o.Tool));
        Assert.Equal([8, -1, 21], p.Operations.Select(o => o.ToolChangeLine));
        Assert.Equal(7, p.Operations[0].StartLine);
        Assert.Equal(14, p.Operations[0].EndLine);
        var t1 = p.Tools.Single(t => t.Number == 1);
        Assert.Equal("3.175 flat Makera M1", t1.Description);
        Assert.Equal(3.175, t1.Diameter);
        Assert.Equal("flat end mill", t1.Type);
        Assert.Equal("ball end mill", p.Tools.Single(t => t.Number == 2).Type);
        // lines are 1-based for OperationOfLine
        Assert.Equal(-1, p.OperationOfLine(4));
        Assert.Equal(1, p.OperationOfLine(17));
        Assert.All(p.Segments, s => Assert.True(p.OperationOfLine(s.Line) >= 0));
    }

    [Fact]
    public void FindsMakeraStudioToolpaths()
    {
        string[] lines =
        [
            ";@MKR|BEGIN",
            ";@MKR|TOOL|number=1|id=1|name=3.175*12mm Flat End(Metal)|type=Flat End|diameter=3.175",
            ";@MKR|TOOLPATH|number=1|tool_number=1|name=[T1]3D Pocket",
            ";@MKR|TOOLPATH|number=2|tool_number=1|name=[T1]3D Contour",
            ";@MKR|END",
            "G90 G21",
            ";@MKR|TOOLPATH_START|toolpath_number=1",
            "T1 M6",
            "G1 X1",
            ";@MKR|TOOLPATH_START|toolpath_number=2",
            "G1 X2",
        ];
        var p = GcodeProgram.Parse(lines);
        Assert.Equal(["[T1]3D Pocket", "[T1]3D Contour"], p.Operations.Select(o => o.Name));
        Assert.Equal([1, 1], p.Operations.Select(o => o.Tool));
        var tool = Assert.Single(p.Tools);
        Assert.Equal("3.175*12mm Flat End(Metal)", tool.Description);
        Assert.Equal("Flat End", tool.Type);
    }

    [Fact]
    public void FallsBackToToolChangesOrWholeProgram()
    {
        var byTool = GcodeProgram.Parse(["G21", "T1 M6", "G1 X1", "M6 T3", "G1 X2"]);
        Assert.Equal(["Tool 1", "Tool 3"], byTool.Operations.Select(o => o.Name));
        var plain = GcodeProgram.Parse(["(just moves)", "G21", "G1 X1"]);
        Assert.Equal("Program", Assert.Single(plain.Operations).Name);
        Assert.Null(plain.Operations[0].Tool);
    }

    [Theory]
    [InlineData("T1M6", 1)]
    [InlineData("T12 M6", 12)]
    [InlineData("M6 T3", 3)]
    [InlineData("M06T4 S1", 4)]
    [InlineData("T5 (no change)", null)]
    [InlineData("(T1M6)", null)]
    [InlineData("M60", null)]
    public void DetectsToolChanges(string line, int? tool) => Assert.Equal(tool, GcodeStructure.ToolChange(line));

    [Fact]
    public void ReplacesToolNumberKeepingParameters()
    {
        Assert.Equal("T7 M6 S3 (keep)", GcodeStructure.ReplaceToolNumber("T2 M6 S3 (keep)", 7));
        Assert.Equal("T7M6", GcodeStructure.ReplaceToolNumber("T1M6", 7));
        Assert.Equal("M6 T7 H-12", GcodeStructure.ReplaceToolNumber("M6 T1 H-12", 7));
    }

    [Fact]
    public void ChangingAnOperationWithItsOwnToolChangeEditsOnlyThatLine()
    {
        var p = GcodeProgram.Parse(Fusion);
        var edited = GcodeEditor.ChangeOperationTool(p.Lines, p.Operations, 2, 5);
        Assert.Equal(Fusion.Length, edited.Count);
        Assert.Equal("T5 M6 S3", edited[21]);
        Assert.Equal([1, 1, 5], p.WithLines(edited).Operations.Select(o => o.Tool));
    }

    [Fact]
    public void ChangingAnInheritingOperationInsertsToolChangeAndRestoresSpindle()
    {
        var p = GcodeProgram.Parse(Fusion);
        var edited = GcodeEditor.ChangeOperationTool(p.Lines, p.Operations, 1, 4);
        var reparsed = p.WithLines(edited);
        Assert.Equal([1, 4, 2], reparsed.Operations.Select(o => o.Tool));
        var op = reparsed.Operations[1];
        Assert.Equal("(2D Contour1)", edited[op.StartLine]);
        Assert.Equal(["T4 M6", "S12000 M3", "M7"], edited.Skip(op.StartLine + 1).Take(3));
        // The same moves are still there.
        Assert.Equal(p.Segments.Count, reparsed.Segments.Count);
    }

    [Fact]
    public void ChangingAnOperationRestoresTheToolForAnInheritingSuccessor()
    {
        var p = GcodeProgram.Parse(Fusion);
        var edited = GcodeEditor.ChangeOperationTool(p.Lines, p.Operations, 0, 3);
        var reparsed = p.WithLines(edited);
        Assert.Equal([3, 1, 2], reparsed.Operations.Select(o => o.Tool));
        Assert.Contains("T1 M6", edited);
        Assert.Equal("T3M6", edited[8]);
    }

    [Fact]
    public void ModalStateStopsSpindleAtToolChange()
    {
        var (spindle, speed, coolant) = GcodeEditor.ModalState(["S8000 M3", "M8", "T2 M6"], 3);
        Assert.Null(spindle);
        Assert.Equal(8000, speed);
        Assert.Equal("M8", coolant);
    }
}
