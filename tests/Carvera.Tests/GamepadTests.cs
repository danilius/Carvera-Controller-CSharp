using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Pendant;
using Carvera.Core.Pendant.Gamepad;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

internal sealed class FakeGamepad : IGamepadSource
{
    public event Action<GamepadEvent>? Event;
    public bool Started, Disposed;
    public void Start() => Started = true;
    public void Dispose() => Disposed = true;
    public void Emit(GamepadEvent e) => Event?.Invoke(e);
    public void Stick(int axis, double fraction) => Emit(new GamepadAxis(axis, (int)(fraction * 32767)));
}

public class GamepadTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required CarveraController Controller { get; init; }
        public required FakeGamepad Pad { get; init; }
        public required GamepadPendant Pendant { get; init; }
        public GamepadBindings Bindings { get; set; } = GamepadBindings.Default;
        public PendantPolicy Policy { get; set; } = new();
        public (bool X, bool Y, bool Z, bool A) Invert { get; set; }
        public List<PendantMacro> Macros { get; } = [];
        public StateStore State => Controller.State;
        public double X => State.Get<double>(StatePaths.AxisWork("x"));
        public IEnumerable<string> Sent => Controller.Console.Entries.Where(e => e.Kind == ConsoleEntryKind.Sent).Select(e => e.Text);

        public async ValueTask DisposeAsync()
        {
            Pendant.Dispose();
            await Controller.DisposeAsync();
        }
    }

    private static async Task<Rig> Start()
    {
        var controller = new CarveraController { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
        Assert.True(await WaitFor(() => controller.State.Get<bool>(StatePaths.CommunityFirmware)));
        controller.State.Set(StatePaths.JogStep, 0.1);
        var pad = new FakeGamepad();
        Rig? rig = null;
        var pendant = new GamepadPendant(controller, commands, pad, new GamepadOptions
        {
            Bindings = () => rig!.Bindings,
            Policy = () => rig!.Policy,
            Macros = () => rig!.Macros,
            Invert = () => rig!.Invert,
        });
        rig = new Rig { Controller = controller, Pad = pad, Pendant = pendant };
        pendant.Start();
        pad.Emit(new GamepadConnected("Test pad"));
        return rig;
    }

    [Fact]
    public void ThePresetsAreValidAndRoundTripThroughJson()
    {
        foreach (var (name, bindings) in GamepadBindings.Presets)
        {
            Assert.True(bindings.Validate().Count == 0, $"{name}: {string.Join("; ", bindings.Validate())}");
            var again = GamepadBindings.FromJson(bindings.ToJson());
            Assert.Equal(bindings.Axes, again.Axes);
            Assert.Equal(bindings.Triggers, again.Triggers);
            Assert.Equal(bindings.Buttons, again.Buttons);
        }
    }

    [Fact]
    public void BadBindingsAreReportedInPlainWords()
    {
        var bindings = GamepadBindings.FromJson("""{ "axes": { "0": "jog_xx", "1": "stop", "x": "jog_y" }, "hat": { "north": "stop" }, "triggers": { "2:*": "feed_plus" } }""");
        var problems = bindings.Validate();
        Assert.Contains(problems, p => p.Contains("jog_xx") && p.Contains("did you mean 'jog_x'"));
        Assert.Contains(problems, p => p.StartsWith("axes.1") && p.Contains("not a jog action"));
        Assert.Contains(problems, p => p.StartsWith("axes.x"));
        Assert.Contains(problems, p => p.StartsWith("hat.north"));
        Assert.Contains(problems, p => p.StartsWith("triggers.2:*"));
        Assert.Throws<FormatException>(() => GamepadBindings.FromJson("[1,2]"));
        Assert.Throws<FormatException>(() => GamepadBindings.FromJson("{ nope"));
        Assert.Throws<FormatException>(() => GamepadBindings.FromJson("""{ "buttons": { "1": 5 } }"""));
    }

    [Fact]
    public async Task ConnectionIsPublishedAndReleasedOnDispose()
    {
        await using var rig = await Start();
        Assert.True(rig.Pendant.IsConnected);
        Assert.Equal("Test pad", rig.State.Get<string>(StatePaths.PendantName));
        Assert.True(rig.State.Get<bool>(StatePaths.PendantConnected));
        rig.Pad.Emit(new GamepadDisconnected());
        Assert.False(rig.State.Get<bool>(StatePaths.PendantConnected));
    }

    [Fact]
    public async Task AStickPushJogsOneStepAndOnlyOnePerPush()
    {
        await using var rig = await Start();
        var start = rig.X;
        rig.Pad.Stick(0, 1.0);
        rig.Pad.Stick(0, 0.9);
        rig.Pad.Stick(0, 1.0);
        Assert.True(await WaitFor(() => Math.Abs(rig.X - (start + 0.1)) < 1e-6));
        await Task.Delay(200);
        Assert.Equal(1, rig.Sent.Count(s => s.StartsWith("$J X")));

        rig.Pad.Stick(0, 0); // let go
        rig.Pad.Stick(0, -1.0); // push the other way
        Assert.True(await WaitFor(() => Math.Abs(rig.X - start) < 1e-6));
        Assert.Equal(2, rig.Sent.Count(s => s.StartsWith("$J X")));
    }

    [Fact]
    public async Task TheDeadzoneIgnoresSmallMovementsAndInversionFlipsTheDirection()
    {
        await using var rig = await Start();
        rig.Pad.Stick(0, 0.10);
        await Task.Delay(150);
        Assert.DoesNotContain(rig.Sent, s => s.StartsWith("$J"));

        rig.Invert = (true, false, false, false);
        var start = rig.X;
        rig.Pad.Stick(0, 1.0);
        Assert.True(await WaitFor(() => Math.Abs(rig.X - (start - 0.1)) < 1e-6)); // inverted: pushing right moves X the other way
    }

    [Fact]
    public async Task JoggingIsRefusedWhenThePolicyForbidsIt()
    {
        await using var rig = await Start();
        rig.Policy = new PendantPolicy(JoggingEnabled: false);
        rig.Pad.Stick(0, 1.0);
        await Task.Delay(200);
        Assert.DoesNotContain(rig.Sent, s => s.StartsWith("$J"));
    }

    [Fact]
    public async Task ButtonsChangeTheStepSizeAndSwitchTheJogMode()
    {
        await using var rig = await Start();
        rig.Pad.Emit(new GamepadButton(4, true)); // step down (LB)
        Assert.Equal(0.01, rig.State.Get<double>(StatePaths.JogStep));
        rig.Pad.Emit(new GamepadButton(4, true));
        Assert.Equal(0.01, rig.State.Get<double>(StatePaths.JogStep)); // already the smallest
        rig.Pad.Emit(new GamepadButton(5, true));
        rig.Pad.Emit(new GamepadButton(5, true));
        Assert.Equal(1.0, rig.State.Get<double>(StatePaths.JogStep));

        Assert.Equal("step", rig.State.Get<string>(StatePaths.JogMode));
        rig.Pad.Emit(new GamepadButton(6, true)); // mode toggle (Back)
        Assert.Equal("continuous", rig.State.Get<string>(StatePaths.JogMode));
        rig.Pad.Emit(new GamepadButton(6, true));
        Assert.Equal("step", rig.State.Get<string>(StatePaths.JogMode));
    }

    [Fact]
    public async Task ContinuousModeJogsWhileHeldAtASpeedFromTheStepSize()
    {
        await using var rig = await Start();
        rig.Pad.Emit(new GamepadButton(6, true)); // continuous
        rig.Pad.Stick(1, -1.0);                   // stick Y, pushed the other way
        Assert.True(await WaitFor(() => rig.Controller.ContinuousJogActive));
        Assert.Contains(rig.Sent, s => s == "$J -c Y-1 F300"); // step 0.1 = 10% of 3000 mm/min

        rig.Pad.Stick(1, 0);
        Assert.True(await WaitFor(() => !rig.Controller.ContinuousJogActive)); // stopped once the machine's ^Y arrived
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text.Contains("Ctrl-Y"));
    }

    [Fact]
    public async Task ZIsCappedInContinuousMode()
    {
        await using var rig = await Start();
        rig.State.Set(StatePaths.JogStep, 10.0);
        rig.Pad.Emit(new GamepadButton(6, true));
        rig.Pad.Stick(4, 1.0); // stick for Z
        Assert.True(await WaitFor(() => rig.Sent.Contains("$J -c Z1 F800")));
    }

    [Fact]
    public async Task DisconnectingThePadStopsAContinuousJog()
    {
        await using var rig = await Start();
        rig.Pad.Emit(new GamepadButton(6, true));
        rig.Pad.Stick(0, 1.0);
        Assert.True(await WaitFor(() => rig.Controller.ContinuousJogActive));
        rig.Pad.Emit(new GamepadDisconnected());
        Assert.True(await WaitFor(() => !rig.Controller.ContinuousJogActive));
    }

    [Fact]
    public async Task TriggersFireOncePerPressAndAdjustTheFeedOverride()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Contains(StatePaths.FeedOverride)));
        rig.Pad.Stick(5, 1.0); // RT: feed +
        rig.Pad.Stick(5, 0.9);
        Assert.True(await WaitFor(() => rig.Sent.Any(s => s.Contains("S110"))));
        Assert.Equal(1, rig.Sent.Count(s => s.Contains("S110")));
        rig.Pad.Stick(5, 0);
        rig.Pad.Stick(2, 1.0); // LT: feed -
        Assert.True(await WaitFor(() => rig.Sent.Any(s => s.Contains("S90"))));
    }

    [Fact]
    public async Task StopIsNeverGatedByThePolicy()
    {
        await using var rig = await Start();
        rig.Policy = new PendantPolicy(JoggingEnabled: false);
        rig.Bindings = GamepadBindings.FromJson("""{ "buttons": { "0": "stop", "1": "reset", "2": "start_pause" } }""");
        rig.Pad.Emit(new GamepadButton(0, true));
        rig.Pad.Emit(new GamepadButton(1, true));
        Assert.True(await WaitFor(() => rig.Sent.Contains("abort") && rig.Controller.Console.Entries.Any(e => e.Text.Contains("reset"))));
    }

    [Fact]
    public async Task TheDpadJogsAndMacrosRun()
    {
        await using var rig = await Start();
        rig.Bindings = GamepadBindings.FromJson("""{ "hat": { "left": "jog_x", "right": "jog_x", "up": "macro_1" }, "buttons": { "0": "macro_2" } }""");
        rig.Macros.Add(new PendantMacro(1, "Light on", "M821"));
        var start = rig.X;
        rig.Pad.Emit(new GamepadHat(1, 0));
        Assert.True(await WaitFor(() => Math.Abs(rig.X - (start + 0.1)) < 1e-6));
        rig.Pad.Emit(new GamepadHat(0, 0));
        rig.Pad.Emit(new GamepadHat(0, 1)); // up: macro 1
        Assert.True(await WaitFor(() => rig.State.Get<bool>("switch.light")));
        rig.Pad.Emit(new GamepadButton(0, true)); // macro 2 does not exist
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text.Contains("no macro 2"));
    }

    [Fact]
    public async Task DisposingTheDriverDisposesItsSource()
    {
        var rig = await Start();
        Assert.True(rig.Pad.Started);
        await rig.DisposeAsync();
        Assert.True(rig.Pad.Disposed);
    }
}
