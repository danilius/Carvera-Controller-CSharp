using Carvera.Core.Commands;
using Carvera.Core.Protocol;
using Xunit;

namespace Carvera.Tests;

public class MachineCommandTests
{
    [Fact]
    public void BuildsCommandsLikeThePythonController()
    {
        Assert.Equal("$J X1.5 Y-2 F3000\n", MachineCommands.Jog(new Dictionary<char, double> { ['X'] = 1.5, ['Y'] = -2 }, 3000));
        Assert.Equal("$J Z0.1\n", MachineCommands.Jog(new Dictionary<char, double> { ['Z'] = 0.1 }, null));
        Assert.Equal("G10L20P0X0Y0\n", MachineCommands.SetWorkPosition(0, 0));
        Assert.Equal("G10L20P0Z0\n", MachineCommands.SetWorkPosition(z: 0));
        Assert.Equal("G90G0X10Y-5.25\n", MachineCommands.Goto(10, -5.25));
        Assert.Equal("M220 S120\n", MachineCommands.FeedOverride(120, instant: false));
        Assert.Equal("$F S120\n", MachineCommands.FeedOverride(120, instant: true));
        Assert.Equal("M223 S80\n", MachineCommands.SpindleOverride(80, instant: false));
        Assert.Equal("M821\n", MachineCommands.Light(true));
        Assert.Equal("M822\n", MachineCommands.Light(false));
        Assert.Equal("M3 S12000\n", MachineCommands.Spindle(true, 12000));
        Assert.Equal("M5\n", MachineCommands.Spindle(false));
        Assert.Equal("M6T3\n", MachineCommands.ChangeTool(3));
        Assert.Equal("G55\n", MachineCommands.SelectWcs(1));
        Assert.Equal("G10L2R12.500P0\n", MachineCommands.SetRotation(12.5));
    }

    [Fact]
    public void EscapesFileNames()
    {
        Assert.Equal("play /sd/gcodes/my\u0001part\u0004.nc\n", MachineCommands.Play("\\sd\\gcodes\\my part!.nc"));
    }

    [Theory]
    [InlineData("X", 'X', 1)]
    [InlineData("z-", 'Z', -1)]
    [InlineData("X+Y-", 'X', 1)]
    [InlineData("X+Y-", 'Y', -1)]
    public void ParsesJogAxes(string text, char axis, double sign) => Assert.Equal(sign, StandardCommands.ParseJogAxes(text)[axis]);

    [Fact]
    public void SafetyControlsReferToRegisteredCommands()
    {
        var registry = AppCommands.CreateCatalog();
        foreach (var (_, ids) in StandardCommands.SafetyControls)
            foreach (var id in ids) Assert.True(registry.TryGet(id, out _), id);
    }
}
