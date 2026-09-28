using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Pendant;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

/// <summary>A stand-in for the CYD firmware: a TCP server that acknowledges heartbeats and records messages.</summary>
internal sealed class FakeCyd : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<JsonElement> _messages = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _client;
    public bool AckHeartbeats { get; set; } = true;

    public FakeCyd()
    {
        _listener.Start();
        _ = Task.Run(Accept);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task Accept()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                _client = socket;
                _ = Task.Run(() => Read(socket));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private void Read(Socket socket)
    {
        var decoder = new NdjsonDecoder();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = socket.Receive(buffer);
                if (count == 0) return;
                foreach (var message in decoder.Feed(buffer.AsSpan(0, count)))
                {
                    if (message.GetProperty("type").GetString() == "heartbeat")
                    {
                        if (AckHeartbeats) Send($"{{\"type\":\"heartbeat_ack\",\"seq\":{message.GetProperty("seq").GetInt64()}}}");
                        continue;
                    }
                    lock (_gate) _messages.Add(message);
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    public void Send(string json) => _client?.Send(Encoding.UTF8.GetBytes(json + "\n"));

    public List<JsonElement> Messages
    {
        get { lock (_gate) return [.. _messages]; }
    }

    public JsonElement? Last(string type) => Messages.LastOrDefault(m => m.GetProperty("type").GetString() == type) is { ValueKind: JsonValueKind.Object } m ? m : null;

    public void DropConnection()
    {
        try { _client?.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
        _client?.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _client?.Dispose();
    }
}

public class CydPendantTests
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
        public required CommandRegistry Commands { get; init; }
        public required CydPendant Pendant { get; init; }
        public required FakeCyd Cyd { get; init; }
        public List<PendantMacro> Macros { get; } = [];
        public PendantPolicy Policy { get; set; } = new();

        public async Task Send(string json)
        {
            Cyd.Send(json);
            await Task.Delay(120);
        }

        public async ValueTask DisposeAsync()
        {
            Pendant.Dispose();
            Cyd.Dispose();
            await Controller.DisposeAsync();
        }
    }

    private static async Task<Rig> Start(bool ackHeartbeats = true)
    {
        const bool connectMachine = true;
        var controller = new CarveraController { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        if (connectMachine)
        {
            await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
            Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
            Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.FirmwareVersion) == "2.1.0"));
        }
        var cyd = new FakeCyd { AckHeartbeats = ackHeartbeats };
        Rig? rig = null;
        var pendant = new CydPendant(controller, commands, new CydPendantOptions
        {
            Host = () => "127.0.0.1",
            Port = () => cyd.Port,
            Macros = () => rig!.Macros,
            Policy = () => rig!.Policy,
            Timings = new CydClientTimings(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100)),
        });
        rig = new Rig { Controller = controller, Commands = commands, Pendant = pendant, Cyd = cyd };
        pendant.Start();
        Assert.True(await WaitFor(() => pendant.IsConnected && (pendant.LinkReady || !ackHeartbeats)));
        return rig;
    }

    [Fact]
    public async Task SendsInitialStateOnConnect()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.Cyd.Last("machine_state") is not null && rig.Cyd.Last("macro_list") is not null && rig.Cyd.Last("pos") is not null));
        var state = rig.Cyd.Messages.First(m => m.GetProperty("type").GetString() == "machine_state");
        Assert.Equal("Idle", state.GetProperty("state").GetString());
        Assert.Equal("idle", state.GetProperty("activity").GetString());
        Assert.Equal("T1", state.GetProperty("tool_label").GetString()); // the simulator starts with T1 loaded
        Assert.True(rig.Controller.State.Get<bool>(StatePaths.PendantConnected));
        Assert.Equal("CYD", rig.Controller.State.Get<string>(StatePaths.PendantName));
    }

    [Fact]
    public async Task StepJogMovesTheMachineWithinLimits()
    {
        await using var rig = await Start();
        var start = rig.Controller.State.Get<double>(StatePaths.AxisWork("x"));
        await rig.Send("{\"type\":\"jog\",\"axis\":\"X\",\"delta\":1.5}");
        Assert.True(await WaitFor(() => rig.Cyd.Last("jog_result") is { } r && r.GetProperty("ok").GetBoolean()));
        Assert.Equal("X1.500", rig.Cyd.Last("jog_result")!.Value.GetProperty("command").GetString());
        Assert.True(await WaitFor(() => Math.Abs(rig.Controller.State.Get<double>(StatePaths.AxisWork("x")) - (start + 1.5)) < 1e-6));

        await rig.Send("{\"type\":\"jog\",\"axis\":\"X\",\"delta\":2.5}");
        Assert.StartsWith("outside_first_test_limit", rig.Cyd.Last("jog_result")!.Value.GetProperty("reason").GetString());
        await rig.Send("{\"type\":\"jog\",\"axis\":\"Q\",\"delta\":1}");
        Assert.Equal("unsupported_axis", rig.Cyd.Last("jog_result")!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task JogIsRefusedWhenThePendantPolicyForbidsIt()
    {
        await using var rig = await Start();
        rig.Policy = new PendantPolicy(JoggingEnabled: false);
        await rig.Send("{\"type\":\"jog\",\"axis\":\"X\",\"delta\":1}");
        Assert.Equal("jogging_disabled", rig.Cyd.Last("jog_result")!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task JogIsRefusedWithoutAHeartbeat()
    {
        // A pendant that never acknowledges heartbeats must not be able to move the machine.
        await using var rig = await Start(ackHeartbeats: false);
        Assert.False(rig.Pendant.LinkReady);
        await rig.Send("{\"type\":\"jog\",\"axis\":\"X\",\"delta\":1}");
        Assert.Equal("jogging_disabled", rig.Cyd.Last("jog_result")!.Value.GetProperty("reason").GetString());
        Assert.DoesNotContain(rig.Controller.Console.Entries, e => e.Text.StartsWith("$J X1"));
    }

    [Fact]
    public async Task ContinuousJogStartsAndStopsWithFirmwareAcknowledgement()
    {
        await using var rig = await Start();
        await rig.Send("{\"type\":\"jog_cont\",\"action\":\"start\",\"axis\":\"X\",\"dir\":1,\"feed\":1200,\"seq\":1}");
        Assert.True(await WaitFor(() => rig.Controller.ContinuousJogActive));
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text == "$J -c X1 F1200");

        await rig.Send("{\"type\":\"jog_cont\",\"action\":\"stop\",\"seq\":2}");
        Assert.True(await WaitFor(() => !rig.Controller.ContinuousJogActive)); // cleared by the simulator's ^Y
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text.Contains("Ctrl-Y"));
    }

    [Fact]
    public async Task StaleContinuousStartAfterFullStopIsIgnored()
    {
        await using var rig = await Start();
        await rig.Send("{\"type\":\"jog_cont\",\"action\":\"full_stop\",\"seq\":10}");
        await rig.Send("{\"type\":\"jog_cont\",\"action\":\"start\",\"axis\":\"X\",\"dir\":1,\"feed\":500,\"seq\":9}");
        await Task.Delay(200);
        Assert.False(rig.Controller.ContinuousJogActive);
        Assert.DoesNotContain(rig.Controller.Console.Entries, e => e.Text.StartsWith("$J -c"));
    }

    [Fact]
    public async Task OnlyProbingGcodeIsAllowed()
    {
        await using var rig = await Start();
        await rig.Send("{\"type\":\"gcode\",\"line\":\"G0 X1000\"}");
        Assert.Equal("not_allowed", rig.Cyd.Last("gcode_result")!.Value.GetProperty("reason").GetString());
        await rig.Send("{\"type\":\"gcode\",\"line\":\"M461 X1\\nG0 X5\"}");
        Assert.Equal("not_allowed", rig.Cyd.Last("gcode_result")!.Value.GetProperty("reason").GetString());
        await rig.Send("{\"type\":\"gcode\",\"line\":\"M462 X-1.5 Y2 S1\"}");
        Assert.True(rig.Cyd.Last("gcode_result")!.Value.GetProperty("ok").GetBoolean());
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text == "M462 X-1.5 Y2 S1");
    }

    [Fact]
    public async Task MacrosAreListedAndRun()
    {
        await using var rig = await Start();
        rig.Macros.Add(new PendantMacro(1, "Light on", "M821"));
        rig.Macros.Add(new PendantMacro(2, "Macro 2", "M9")); // default names are hidden, as in the Python controller
        Assert.True(await WaitFor(() => rig.Cyd.Last("macro_list") is { } l && l.GetProperty("macros").GetArrayLength() == 1));
        Assert.Equal("Light on", rig.Cyd.Last("macro_list")!.Value.GetProperty("macros")[0].GetProperty("name").GetString());

        await rig.Send("{\"type\":\"macro\",\"action\":\"run\",\"id\":1}");
        Assert.True(rig.Cyd.Last("macro_result")!.Value.GetProperty("ok").GetBoolean());
        Assert.True(await WaitFor(() => rig.Controller.State.Get<bool>("switch.light")));

        await rig.Send("{\"type\":\"macro\",\"action\":\"run\",\"id\":2}");
        Assert.Equal("not_named", rig.Cyd.Last("macro_result")!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task OverridesAndStopRunThroughCommands()
    {
        await using var rig = await Start();
        await rig.Send("{\"type\":\"runtime\",\"action\":\"override\",\"target\":\"feed\",\"delta\":1}");
        Assert.True(rig.Cyd.Last("runtime_result")!.Value.GetProperty("ok").GetBoolean());
        Assert.True(await WaitFor(() => rig.Controller.Console.Entries.Any(e => e.Text.Contains("S110"))));
        await rig.Send("{\"type\":\"runtime\",\"action\":\"override\",\"target\":\"nope\",\"delta\":1}");
        Assert.Equal("bad_override_target", rig.Cyd.Last("runtime_result")!.Value.GetProperty("reason").GetString());
        await rig.Send("{\"type\":\"runtime\",\"action\":\"stop\"}");
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text == "abort");
    }

    [Fact]
    public async Task ToolAndPositionActionsAreValidated()
    {
        await using var rig = await Start();
        await rig.Send("{\"type\":\"tool\",\"action\":\"change\",\"tool\":42}");
        Assert.Equal("unsupported_tool", rig.Cyd.Last("tool_result")!.Value.GetProperty("reason").GetString());
        await rig.Send("{\"type\":\"tool\",\"action\":\"change\",\"tool\":3}");
        Assert.True(rig.Cyd.Last("tool_result")!.Value.GetProperty("ok").GetBoolean());
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text == "M6T3");

        await rig.Send("{\"type\":\"position\",\"action\":\"set_origin\",\"axes\":\"xy\"}");
        Assert.True(rig.Cyd.Last("position_result")!.Value.GetProperty("ok").GetBoolean());
        Assert.Contains(rig.Controller.Console.Entries, e => e.Text == "G10L20P0X0Y0");
    }

    [Fact]
    public async Task ManualModeNeedsAC1AndIsRefusedOtherwise()
    {
        await using var rig = await Start(); // the simulator is a CA1
        await rig.Send("{\"type\":\"manual\",\"action\":\"enter\",\"request_id\":7}");
        var result = rig.Cyd.Last("manual_result")!.Value;
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("manual_controls_gated", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnswersPingAndQueries()
    {
        await using var rig = await Start();
        await rig.Send("{\"type\":\"ping\",\"ts\":12345}");
        Assert.Equal(12345, rig.Cyd.Last("pong")!.Value.GetProperty("ts").GetInt64());
        await rig.Send("{\"type\":\"state_query\"}");
        Assert.Equal("state_query", rig.Cyd.Last("machine_state")!.Value.GetProperty("response_to").GetString());
    }

    [Fact]
    public async Task ReconnectsAndReportsTheLinkInState()
    {
        await using var rig = await Start();
        rig.Cyd.DropConnection();
        Assert.True(await WaitFor(() => !rig.Controller.State.Get<bool>(StatePaths.PendantConnected)));
        Assert.True(await WaitFor(() => rig.Controller.State.Get<bool>(StatePaths.PendantConnected), 8000));
    }

    [Fact]
    public void DecoderDropsBadFramesAndKeepsGoodOnes()
    {
        var decoder = new NdjsonDecoder(64);
        var messages = decoder.Feed("{\"a\":1}\nnot json\n[1,2]\n\r\n{\"b\":2}\r\n{\"c\":"u8);
        Assert.Equal(2, messages.Count);
        Assert.Equal(2, messages[1].GetProperty("b").GetInt32());
        messages = decoder.Feed("3}\n"u8);
        Assert.Equal(3, messages.Single().GetProperty("c").GetInt32());
        Assert.Empty(decoder.Feed(new byte[200])); // an oversized frame without a newline is discarded
        Assert.Single(decoder.Feed("{\"d\":4}\n"u8));
    }

    [Theory]
    [InlineData("Run", 0, true, "running_gcode")]
    [InlineData("Run", 0, false, "running")]
    [InlineData("Run", 5, false, "probing")]
    [InlineData("Run", 2, false, "changing_tool")]
    [InlineData("Idle", 5, false, "idle")]
    [InlineData("Pause", 0, true, "paused")]
    public void ActivityIsDerivedLikeThePythonController(string state, int atc, bool playing, string expected) =>
        Assert.Equal(expected, CydPendant.DeriveActivity(state, atc, playing));
}
