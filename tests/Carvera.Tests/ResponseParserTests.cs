using Carvera.Core.Protocol;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class ResponseParserTests
{
    [Fact]
    public void ParsesFullStatusReport()
    {
        var store = new StateStore();
        const string line = "<Run|MPos:68.9980,-49.9240,40.0000,12.3456|WPos:68.9980,-49.9240,40.0000,5.3|R:0.0|G:1|F:1200,1500,110|S:9000,10000,90,1,32.5|T:3,-12.5,4|W:3.9|L:0,0,0,0,100|P:120,45,300,1|A:2|O:0.02|H:3>";
        Assert.True(ResponseParser.TryParseStatus(line, store));
        Assert.Equal("Run", store.Get<string>(StatePaths.MachineState));
        Assert.Equal(68.998, store.Get<double>(StatePaths.AxisMachine("x")), 6);
        Assert.Equal(-49.924, store.Get<double>(StatePaths.AxisWork("y")), 6);
        Assert.Equal(5.3, store.Get<double>(StatePaths.AxisWork("a")), 6);
        Assert.Equal(12.3456 - 5.3, store.Get<double>(StatePaths.AxisOffset("a")), 3);
        Assert.Equal(0.0, store.Get<double>(StatePaths.AxisOffset("x")), 6);
        Assert.Equal(1, store.Get<int>(StatePaths.WcsActive));
        Assert.Equal("G55", store.Get<string>(StatePaths.WcsActiveName));
        Assert.Equal(1200, store.Get<double>(StatePaths.FeedCurrent));
        Assert.Equal(110, store.Get<double>(StatePaths.FeedOverride));
        Assert.Equal(90, store.Get<double>(StatePaths.SpindleOverride));
        Assert.True(store.Get<bool>(StatePaths.VacuumMode));
        Assert.Equal(32.5, store.Get<double>(StatePaths.SpindleTemperature));
        Assert.Equal(3, store.Get<int>(StatePaths.ToolCurrent));
        Assert.Equal(4, store.Get<int>(StatePaths.ToolTarget));
        Assert.Equal(45, store.Get<double>(StatePaths.JobPercent));
        Assert.True(store.Get<bool>(StatePaths.JobPlaying));
        Assert.Equal(2, store.Get<int>(StatePaths.AtcState));
        Assert.Equal(3, store.Get<int>(StatePaths.HaltReason));
    }

    [Fact]
    public void RotatedWcsOffsetsFollowThePythonFormula()
    {
        var store = new StateStore();
        Assert.True(ResponseParser.TryParseStatus("<Idle|MPos:10.0,0.0,0.0|WPos:0.0,10.0,0.0|R:90.0>", store));
        // wcox = mx - (cos r * wx - sin r * wy) = 10 - (0 - 10) = 20; wcoy = my - (sin r * wx + cos r * wy) = 0 - (0 + 0) = 0
        Assert.Equal(20, store.Get<double>(StatePaths.AxisOffset("x")), 3);
        Assert.Equal(0, store.Get<double>(StatePaths.AxisOffset("y")), 3);
        Assert.Equal(90, store.Get<double>(StatePaths.WcsRotation));
    }

    [Fact]
    public void MissingOptionalFieldsResetToolAndJob()
    {
        var store = new StateStore();
        ResponseParser.TryParseStatus("<Idle|MPos:1,2,3|WPos:1,2,3|T:5,1.0|P:1,2,3>", store);
        ResponseParser.TryParseStatus("<Idle|MPos:1,2,3|WPos:1,2,3>", store);
        Assert.Equal(-1, store.Get<int>(StatePaths.ToolCurrent));
        Assert.False(store.Get<bool>(StatePaths.JobPlaying));
        Assert.Equal(0.0, store.Get<double>(StatePaths.AxisMachine("a")));
    }

    [Theory]
    [InlineData("<Idle|WPos:1,2,3>")]
    [InlineData("<Idle>")]
    [InlineData("<Idle|MPos:a,b,c|WPos:1,2,3>")]
    public void RejectsMalformedStatus(string line) => Assert.False(ResponseParser.TryParseStatus(line, new StateStore()));

    [Fact]
    public void ParsesDiagnoseReport()
    {
        var store = new StateStore();
        Assert.True(ResponseParser.TryParseDiagnose("{S:1,5000|L:0,0|F:1,40|V:0,1|G:1|T:0|R:1|C:0|E:0,1,0,0,0,1|P:1,0|A:1,0|I:0}", store));
        Assert.True(store.Get<bool>("switch.spindle"));
        Assert.Equal(5000, store.Get<double>("level.spindle"));
        Assert.True(store.Get<bool>("switch.light"));
        Assert.True(store.Get<bool>("switch.air"));
        Assert.True(store.Get<bool>("sensor.xMax"));
        Assert.True(store.Get<bool>("sensor.cover"));
        Assert.True(store.Get<bool>("sensor.probe"));
        Assert.False(store.Get<bool>("sensor.estop"));
    }

    [Fact]
    public void ParsesWcsAndInfoLines()
    {
        var store = new StateStore();
        Assert.True(ResponseParser.TryParseWcs("[current WCS: G56]", store));
        Assert.Equal("G56", store.Get<string>(StatePaths.WcsActiveName));
        Assert.True(ResponseParser.TryParseWcs("[G54:-123.6800,-100.5,-40,0,0,25.123]", store));
        Assert.Equal(-123.68, store.Get<double>("wcs.g54.x"), 4);
        Assert.Equal(25.123, store.Get<double>("wcs.g54.rotation"), 4);
        Assert.True(ResponseParser.TryParseInfo("version = 2.1.0c", store));
        Assert.Equal("2.1.0", store.Get<string>(StatePaths.FirmwareVersion));
        Assert.True(store.Get<bool>(StatePaths.CommunityFirmware));
        Assert.True(ResponseParser.TryParseInfo("model = CA1", store));
        Assert.Equal("CA1", store.Get<string>(StatePaths.MachineModelName));
    }

    [Theory]
    [InlineData("<Idle|MPos:0,0,0|WPos:0,0,0>", ResponseKind.Status)]
    [InlineData("{S:0,0}", ResponseKind.Diagnose)]
    [InlineData("[G54:0,0,0,0,0]", ResponseKind.Wcs)]
    [InlineData("#debug", ResponseKind.Interior)]
    [InlineData("^Y", ResponseKind.JogStopped)]
    [InlineData("ALARM: Hard limit", ResponseKind.Error)]
    [InlineData("ok", ResponseKind.Message)]
    public void ClassifiesLines(string line, ResponseKind kind) => Assert.Equal(kind, ResponseParser.Classify(line));
}
