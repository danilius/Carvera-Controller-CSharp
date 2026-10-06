using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class JogCommandTests
{
    private static async Task<(CarveraController Controller, CommandRegistry Commands)> Start()
    {
        var machine = new SimulatedMachine();
        var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        var until = DateTime.UtcNow.AddSeconds(6);
        while (controller.State.Get<string>(StatePaths.MachineState) != "Idle" && DateTime.UtcNow < until) await Task.Delay(20);
        return (controller, commands);
    }

    private static List<string> Sent(CarveraController controller) =>
        controller.Console.Entries.Where(e => e.Kind == ConsoleEntryKind.Sent).Select(e => e.Text.Trim()).ToList();

    private static async Task Settle() => await Task.Delay(120);

    [Fact]
    public async Task ScreenDirectionsFollowTheReverseYSetting()
    {
        var (controller, commands) = await Start();
        await using var _ = controller;
        controller.State.Set(StatePaths.JogStep, 2.0);
        controller.State.Set(StatePaths.JogFeed, 1000.0);
        controller.State.Set(StatePaths.JogInvertY, true);
        await commands.ExecuteAsync("jog", CommandArgs.Empty.With("axis", "Y+").With("screen", true));
        await commands.ExecuteAsync("jog", CommandArgs.Empty.With("axis", "X+").With("screen", true));
        await commands.ExecuteAsync("jog", CommandArgs.Empty.With("axis", "Y+")); // not a screen direction: unchanged
        controller.State.Set(StatePaths.JogInvertY, false);
        await commands.ExecuteAsync("jog", CommandArgs.Empty.With("axis", "Y+").With("screen", true));
        await Settle();
        var sent = Sent(controller).Where(l => l.StartsWith("$J")).ToList();
        Assert.Equal(["$J Y-2 F1000", "$J X2 F1000", "$J Y2 F1000", "$J Y2 F1000"], sent);
    }

    [Fact]
    public async Task ContinuousJoggingStartsAndStopsAndCapsZ()
    {
        var (controller, commands) = await Start();
        await using var _ = controller;
        controller.State.Set(StatePaths.JogFeed, 3000.0);
        controller.State.Set(StatePaths.JogInvertY, true);
        await commands.ExecuteAsync("jogStart", CommandArgs.Empty.With("axis", "Y+").With("screen", true));
        Assert.True(controller.ContinuousJogActive);
        await commands.ExecuteAsync("jogStop");
        await Settle();
        Assert.True(await WaitFor(() => !controller.ContinuousJogActive)); // the simulator answers ^Y
        await commands.ExecuteAsync("jogStart", CommandArgs.Empty.With("axis", "Z-"));
        await commands.ExecuteAsync("jogStop");
        await Settle();
        var sent = Sent(controller);
        Assert.Contains("$J -c Y-1 F3000", sent);
        Assert.Contains("$J -c Z-1 F800", sent);
    }

    [Fact]
    public async Task KeysFollowTheJogModeAndCanBeSwitchedOff()
    {
        var (controller, commands) = await Start();
        await using var _ = controller;
        controller.State.Set(StatePaths.JogStep, 1.0);
        controller.State.Set(StatePaths.JogFeed, 1500.0);
        controller.State.Set(StatePaths.JogInvertY, false);
        controller.State.Set(StatePaths.JogButtonMode, "step");
        await commands.ExecuteAsync("jogKey", CommandArgs.Empty.With("axis", "X+"));
        await commands.ExecuteAsync("jogKeyStop"); // nothing to stop in step mode
        Assert.False(controller.ContinuousJogActive);

        await commands.ExecuteAsync("setJogMode", CommandArgs.Empty.With("value", "continuous"));
        Assert.Equal("continuous", controller.State.Get<string>(StatePaths.JogButtonMode));
        await commands.ExecuteAsync("jogKey", CommandArgs.Empty.With("axis", "X-"));
        Assert.True(controller.ContinuousJogActive);
        await commands.ExecuteAsync("jogKeyStop");
        Assert.True(await WaitFor(() => !controller.ContinuousJogActive));

        await commands.ExecuteAsync("setJogKeyboard", CommandArgs.Empty.With("on", false));
        await commands.ExecuteAsync("jogKey", CommandArgs.Empty.With("axis", "X-"));
        Assert.False(controller.ContinuousJogActive);
        await Settle();
        var sent = Sent(controller);
        Assert.Contains("$J X1 F1500", sent);
        Assert.Single(sent, l => l.StartsWith("$J -c"));
    }

    [Fact]
    public async Task TheModeToggles()
    {
        var (controller, commands) = await Start();
        await using var _ = controller;
        controller.State.Set(StatePaths.JogButtonMode, "step");
        await commands.ExecuteAsync("setJogMode");
        Assert.Equal("continuous", controller.State.Get<string>(StatePaths.JogButtonMode));
        await commands.ExecuteAsync("setJogMode");
        Assert.Equal("step", controller.State.Get<string>(StatePaths.JogButtonMode));
        Assert.False(await commands.ExecuteAsync("setJogMode", CommandArgs.Empty.With("mode", "sideways")));
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }
}
