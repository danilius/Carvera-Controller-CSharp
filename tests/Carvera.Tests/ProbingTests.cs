using Carvera.Core.Commands;
using Carvera.Core.Probing;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class ProbingTests
{
    private static Dictionary<string, string> Cfg(params (string, string)[] items) => items.ToDictionary(i => i.Item1, i => i.Item2);

    [Fact]
    public void BoreCentreUsesM461AndOnlyTheNeededAxes()
    {
        var x = ProbeOperations.Build("bore", "CenterX", Cfg(("X", "20"), ("Y", "30"), ("D", "3.175")));
        Assert.Equal("M461 X20 D3.175", x.Gcode);
        var both = ProbeOperations.Build("bore", "CenterBore", Cfg(("X", "20"), ("Y", "30"), ("S", "1")));
        Assert.Equal("M461 X20 Y30 S1", both.Gcode);
    }

    [Fact]
    public void ABoreNeedsItsDistance()
    {
        var result = ProbeOperations.Build("bore", "CenterBore", Cfg(("X", "20")));
        Assert.False(result.Ok);
        Assert.Contains("Y distance", result.Problem);
    }

    [Theory]
    [InlineData("TopLeft", "M464 X10 Y-10")]
    [InlineData("TopRight", "M464 X-10 Y-10")]
    [InlineData("BottomRight", "M464 X-10 Y10")]
    [InlineData("BottomLeft", "M464 X10 Y10")]
    public void OutsideCornersProbeTowardsTheCorner(string operation, string expected) =>
        Assert.Equal(expected, ProbeOperations.Build("outsideCorner", operation, Cfg(("X", "10"), ("Y", "10"))).Gcode);

    [Fact]
    public void InsideCornersUseM463()
    {
        Assert.Equal("M463 X-5 Y5 S1", ProbeOperations.Build("insideCorner", "BottomRight", Cfg(("X", "5"), ("Y", "5"), ("S", "1"))).Gcode);
    }

    [Theory]
    [InlineData("Top", "M466 Y-15 S2")]
    [InlineData("Bottom", "M466 Y15 S2")]
    [InlineData("Left", "M466 X15 S2")]
    [InlineData("Right", "M466 X-15 S2")]
    [InlineData("WorkpieceTop", "M466 Z-15 S2")]
    public void SingleAxisProbesOneDirection(string operation, string expected)
    {
        var axis = operation is "Top" or "Bottom" ? "Y" : operation is "WorkpieceTop" ? "Z" : "X";
        var config = ProbeOperations.Defaults(ProbeOperations.FindFamily("singleAxis")!);
        config[axis] = "15";
        config["Q"] = "";
        // Every axis given: the operation must drop the ones it does not use.
        foreach (var other in new[] { "X", "Y", "Z" }.Where(a => a != axis)) config[other] = "99";
        Assert.Equal(expected, ProbeOperations.Build("singleAxis", operation, config).Gcode);
    }

    [Fact]
    public void AnglesNegateTheProbeDepthWhenAbove()
    {
        Assert.Equal("M465 X10 E2", ProbeOperations.Build("angle", "XBelow", Cfg(("X", "10"), ("E", "2"), ("Y", "5"))).Gcode);
        Assert.Equal("M465 Y10 E-2", ProbeOperations.Build("angle", "YRight", Cfg(("Y", "10"), ("E", "2"))).Gcode);
        Assert.False(ProbeOperations.Build("angle", "XBelow", Cfg(("X", "10"))).Ok);
    }

    [Fact]
    public void ProbeTipAndCalibrationUseTheirOwnCodes()
    {
        Assert.Equal("M460.1 X20", ProbeOperations.Build("probeTip", "Bore", Cfg(("X", "20"))).Gcode);
        Assert.Equal("M460.2 Y20", ProbeOperations.Build("probeTip", "BossY", Cfg(("Y", "20"), ("X", "1"))).Gcode);
        var anchor = ProbeOperations.Build("calibration", "Anchor1", Cfg(("X", "1"), ("D", "3"), ("R", "9")));
        Assert.Equal("M469.1 D3", anchor.Gcode);
        Assert.NotNull(anchor.Note);
    }

    [Fact]
    public void FourthAxisNeedsItsTwoRequiredValues()
    {
        Assert.False(ProbeOperations.Build("fourthAxis", "Stock", Cfg(("Y", "40"))).Ok);
        Assert.Equal("M465.1 Y40 H10 V1 S1", ProbeOperations.Build("fourthAxis", "Stock", Cfg(("Y", "40"), ("H", "10"), ("V", "1"), ("S", "1"))).Gcode);
    }

    [Fact]
    public void NonNumbersAreRefusedBeforeReachingTheMachine()
    {
        var result = ProbeOperations.Build("bore", "CenterX", Cfg(("X", "twenty")));
        Assert.False(result.Ok);
        Assert.Contains("not a number", result.Problem);
    }

    [Fact]
    public void EveryFamilyAndOperationHasAUniqueIdAndAKnownCommand()
    {
        Assert.Equal(ProbeOperations.Families.Count, ProbeOperations.Families.Select(f => f.Id).Distinct().Count());
        foreach (var family in ProbeOperations.Families)
        {
            Assert.Equal(family.Operations.Count, family.Operations.Select(o => o.Id).Distinct().Count());
            foreach (var op in family.Operations)
            {
                Assert.Matches(@"^M46\d(\.\d)?$", op.Command);
                foreach (var code in op.Required.Concat(op.Omit).Concat(op.Negate)) Assert.Contains(family.Parameters, p => p.Code == code);
            }
        }
    }

    private static StateStore Bounds(double xmin = 10, double ymin = 20, double xmax = 110, double ymax = 80)
    {
        var state = new StateStore();
        state.Set(StatePaths.FileHasBounds, true);
        state.Set(StatePaths.FileXMin, xmin);
        state.Set(StatePaths.FileXMax, xmax);
        state.Set(StatePaths.FileYMin, ymin);
        state.Set(StatePaths.FileYMax, ymax);
        return state;
    }

    [Fact]
    public void TheMarginIsASeparateCommandFromTheLevelling()
    {
        var lines = ProbeCommands.AutoRun(Bounds(), new ProbeCommands.AutoOptions(Margin: true, Leveling: true, PointsX: 4, PointsY: 3, Height: 5, GotoOrigin: true));
        Assert.Equal(["M495 X10Y20C110D80\n", "M495 X10Y20A100B60I4J3H5P1\n"], lines);
    }

    [Fact]
    public void ZProbeUsesTheOffsetFromTheOrigin()
    {
        var lines = ProbeCommands.AutoRun(Bounds(), new ProbeCommands.AutoOptions(ZProbe: true, ZProbeOffsetX: -5, ZProbeOffsetY: 2.5, GotoOrigin: true, UpcomingTool: 2));
        Assert.Equal(["M495 X10Y20O-5F2.5P1T2\n"], lines);
        Assert.Equal(["M495 X10Y20O0\n"], ProbeCommands.AutoRun(Bounds(), new ProbeCommands.AutoOptions(ZProbe: true, ZProbeAbsolute: true)));
    }

    [Fact]
    public void LevellingOffsetsShrinkTheArea()
    {
        var lines = ProbeCommands.AutoRun(Bounds(), new ProbeCommands.AutoOptions(Leveling: true, LevelOffsets: [2, 3, 4, 5]));
        Assert.Equal(["M495 X12Y24A95B51I3J3H5\n"], lines);
    }

    [Fact]
    public void NothingIsSentWithoutAProgramOrWhenItIsOutsideTheWorkArea()
    {
        Assert.Empty(ProbeCommands.AutoRun(new StateStore(), new ProbeCommands.AutoOptions(Margin: true)));
        Assert.Empty(ProbeCommands.AutoRun(Bounds(), new ProbeCommands.AutoOptions()));
        Assert.Null(ProbeCommands.GotoPathOrigin(Bounds(xmin: 500)));
        Assert.Equal("M496.5 X10Y20\n", ProbeCommands.GotoPathOrigin(Bounds()));
    }

    [Fact]
    public void TheXyzProbeCommandMatchesThePythonController() =>
        Assert.Equal("M495.3 H9 D3.175\n", ProbeCommands.XyzProbe(9, 3.175));
}

public class WorkCommandTests
{
    [Fact]
    public void AxisValuesAreReadFromTypedText()
    {
        var values = WorkCommands.ParseAxisValues("X10 y -2.5, Z0");
        Assert.Equal(10, values['X']);
        Assert.Equal(-2.5, values['Y']);
        Assert.Equal(0, values['Z']);
        Assert.Empty(WorkCommands.ParseAxisValues("nothing"));
    }
}

public class WorkOriginTests
{
    private static double? Coordinates(string name) => name switch
    {
        "anchor1_x" => -360.0, "anchor1_y" => -230.0, "anchor2_offset_x" => 90.0, "anchor2_offset_y" => 45.0,
        "rotation_offset_x" => 10.0, "rotation_offset_y" => -5.0, _ => null,
    };

    [Theory]
    [InlineData("anchor1 10 5", WorkCommands.OriginAnchor.Anchor1, 10.0, 5.0)]
    [InlineData("2 0,0", WorkCommands.OriginAnchor.Anchor2, 0.0, 0.0)]
    [InlineData("current -3 2.5", WorkCommands.OriginAnchor.Current, -3.0, 2.5)]
    [InlineData("rotation 1 1", WorkCommands.OriginAnchor.Rotation, 1.0, 1.0)]
    public void AnOriginIsReadFromTypedText(string text, WorkCommands.OriginAnchor anchor, double x, double y) =>
        Assert.Equal((anchor, x, y), WorkCommands.ParseOrigin(text));

    [Theory]
    [InlineData("")]
    [InlineData("anchor1 10")]
    [InlineData("left 1 2")]
    [InlineData("anchor1 a b")]
    public void BadOriginTextIsRefused(string text) => Assert.Null(WorkCommands.ParseOrigin(text));

    [Fact]
    public void TheOriginIsMeasuredFromTheChosenAnchor()
    {
        Assert.Equal((-350.0, -225.0), WorkCommands.OriginPosition(WorkCommands.OriginAnchor.Anchor1, 10, 5, Coordinates, 0, 0));
        Assert.Equal((-260.0, -180.0), WorkCommands.OriginPosition(WorkCommands.OriginAnchor.Anchor2, 10, 5, Coordinates, 0, 0));
        Assert.Equal((-340.0, -230.0), WorkCommands.OriginPosition(WorkCommands.OriginAnchor.Rotation, 10, 5, Coordinates, 0, 0));
        Assert.Equal((-95.0, -48.0), WorkCommands.OriginPosition(WorkCommands.OriginAnchor.Current, 5, 2, Coordinates, -100, -50));
    }

    [Fact]
    public void AMissingAnchorCoordinateGivesNoPosition()
    {
        Assert.Null(WorkCommands.OriginPosition(WorkCommands.OriginAnchor.Anchor1, 0, 0, _ => null, 0, 0));
        Assert.NotNull(WorkCommands.OriginPosition(WorkCommands.OriginAnchor.Current, 0, 0, _ => null, 1, 2));
    }

    [Fact]
    public void TheOffsetIsSentInMachineCoordinates() =>
        Assert.Equal("G10L2P0X-350Y-225.5\n", Carvera.Core.Protocol.MachineCommands.SetWorkOffset(-350, -225.5));
}
