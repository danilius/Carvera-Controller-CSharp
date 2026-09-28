using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class ControllerTests
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

    private static async Task<(CarveraController Controller, CommandRegistry Commands)> ConnectSimulator()
    {
        var controller = new CarveraController { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
        return (controller, commands);
    }

    [Fact]
    public async Task ConnectsPollsAndDisconnects()
    {
        var (controller, _) = await ConnectSimulator();
        await using var _c = controller;
        Assert.True(controller.State.Get<bool>(StatePaths.Connected));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.FirmwareVersion) == "2.1.0"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineModelName) == "CA1"));
        Assert.True(await WaitFor(() => controller.State.Contains("switch.light")));
        await controller.DisconnectAsync();
        Assert.False(controller.State.Get<bool>(StatePaths.Connected));
        Assert.Equal("N/A", controller.State.Get<string>(StatePaths.MachineState));
    }

    [Fact]
    public async Task JogMovesTheSimulatedMachine()
    {
        var (controller, commands) = await ConnectSimulator();
        await using var _c = controller;
        var start = controller.State.Get<double>(StatePaths.AxisWork("x"));
        await commands.ExecuteAsync("setJogStep", CommandArgs.Empty.With("value", 10));
        Assert.True(await commands.ExecuteAsync("jog", CommandArgs.Empty.With("axis", "X+")));
        Assert.True(await WaitFor(() => Math.Abs(controller.State.Get<double>(StatePaths.AxisWork("x")) - (start + 10)) < 1e-6));
        Assert.Contains(controller.Console.Entries, e => e.Kind == ConsoleEntryKind.Sent && e.Text.StartsWith("$J X10"));
    }

    [Fact]
    public async Task SetWorkZeroAndSwitchesRoundTrip()
    {
        var (controller, commands) = await ConnectSimulator();
        await using var _c = controller;
        await commands.ExecuteAsync("setWorkZero", CommandArgs.Empty.With("axes", "XY"));
        Assert.True(await WaitFor(() => Math.Abs(controller.State.Get<double>(StatePaths.AxisWork("x"))) < 1e-6 && Math.Abs(controller.State.Get<double>(StatePaths.AxisWork("y"))) < 1e-6));

        Assert.True(await WaitFor(() => controller.State.Contains("switch.light")));
        Assert.False(controller.State.Get<bool>("switch.light"));
        await commands.ExecuteAsync("setLight"); // omitted "on" toggles
        Assert.True(await WaitFor(() => controller.State.Get<bool>("switch.light")));
        await commands.ExecuteAsync("setLight", CommandArgs.Empty.With("on", false));
        Assert.True(await WaitFor(() => !controller.State.Get<bool>("switch.light")));
    }

    [Fact]
    public async Task FeedHoldAndResume()
    {
        var (controller, commands) = await ConnectSimulator();
        await using var _c = controller;
        await commands.ExecuteAsync("feedHold");
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Hold"));
        Assert.False(commands.CanExecute("jog", CommandArgs.Empty.With("axis", "X")));
        await commands.ExecuteAsync("pauseResume");
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
    }

    [Fact]
    public async Task CommandsNeedAConnection()
    {
        await using var controller = new CarveraController();
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        Assert.False(commands.CanExecute("feedHold", CommandArgs.Empty));
        Assert.False(await commands.ExecuteAsync("feedHold"));
        Assert.Contains(controller.Console.Entries, e => e.Text.Contains("not connected"));
        Assert.True(commands.CanExecute("setJogStep", CommandArgs.Empty));
    }

    [Fact]
    public void DetectorParsesAnnouncements()
    {
        var m = MachineDetector.Parse("Carvera_01,192.168.1.50,2222,0");
        Assert.NotNull(m);
        Assert.Equal("192.168.1.50", m.Endpoint);
        Assert.False(m.Busy);
        Assert.Equal("10.0.0.2:2300", MachineDetector.Parse("X,10.0.0.2,2300,1")!.Endpoint);
        Assert.Null(MachineDetector.Parse("garbage"));
        Assert.Equal(("10.1.1.1", 2222), TcpMachineStream.ParseAddress(" 10.1.1.1 "));
        Assert.Equal(("host", 23), TcpMachineStream.ParseAddress("host:23"));
    }

    [Fact]
    public void StateStoreBatchesNotifications()
    {
        var store = new StateStore();
        var calls = new List<string[]>();
        store.Changed += paths => calls.Add(paths.Order().ToArray());
        using (store.BeginBatch())
        {
            store.Set("a", 1);
            store.Set("b", 2);
            store.Set("a", 1);
        }
        store.Set("b", 2); // unchanged: no notification
        Assert.Single(calls);
        Assert.Equal(["a", "b"], calls[0]);
    }

    /// <summary>A stream the test can kill, to look like a machine that drops the connection.</summary>
    private sealed class DroppableStream : IMachineStream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _incoming = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public string Description => "droppable";
        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var chunk = await _incoming.Reader.ReadAsync(cancellationToken);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }
        public void Drop() => _incoming.Writer.Complete();
        public ValueTask DisposeAsync() { _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task LosingTheConnectionRaisesConnectionLostOnceWithTheOptionsUsed()
    {
        var stream = new DroppableStream();
        await using var controller = new CarveraController(streamFactory: _ => stream) { DiagnosePolling = false };
        var lost = new List<ConnectionOptions?>();
        controller.ConnectionLost += lost.Add;
        var options = new ConnectionOptions(ConnectionKind.WiFi, "10.0.0.5");
        await controller.ConnectAsync(options);
        Assert.True(controller.IsConnected);

        stream.Drop();
        Assert.True(await WaitFor(() => lost.Count == 1));
        Assert.Equal(options, lost[0]);
        Assert.False(controller.IsConnected);
        Assert.Equal("Disconnected", controller.State.Get<string>(StatePaths.ConnectionState));
        Assert.Equal(0, controller.DisconnectCount); // a lost connection is not a request to disconnect
        await Task.Delay(200);
        Assert.Single(lost);
    }

    [Fact]
    public async Task ADeliberateDisconnectDoesNotRaiseConnectionLost()
    {
        var (controller, _) = await ConnectSimulator();
        await using var _c = controller;
        var raised = false;
        controller.ConnectionLost += _ => raised = true;
        await controller.DisconnectAsync();
        await Task.Delay(300);
        Assert.False(raised);
        Assert.Equal(1, controller.DisconnectCount);
    }
}
