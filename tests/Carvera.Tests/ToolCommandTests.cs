using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Protocol;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class ToolCommandTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private sealed class Host : IAppHost
    {
        public bool Confirm { get; set; } = true;
        public string? Answer { get; set; }
        public List<string> Questions { get; } = [];
        public Task<bool> ConfirmAsync(string message) { Questions.Add(message); return Task.FromResult(Confirm); }
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(Answer);
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task UploadFileAsync(string? path, string? remoteDirectory) => Task.CompletedTask;
        public Task OpenFileAsync(string? path) => Task.CompletedTask;
        public Task CloseFileAsync() => Task.CompletedTask;
        public Task LoadLayoutAsync(string name) => Task.CompletedTask;
        public Task ReloadLayoutAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
        public Task SaveFileAsync(string? path) => Task.CompletedTask;
        public Task SetOperationToolAsync(int operation, int tool) => Task.CompletedTask;
        public Task StepPreviewAsync(int? delta) => Task.CompletedTask;
        public Task SelectOperationAsync(int operation) => Task.CompletedTask;
    }

    private static async Task<(CarveraController Controller, CommandRegistry Commands, Host Host)> Start()
    {
        var machine = new SimulatedMachine();
        var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var host = new Host();
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        ToolCommands.Register(commands, host);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
        Assert.True(await WaitFor(() => controller.State.Get(StatePaths.ToolCurrent, -1) == 1));
        return (controller, commands, host);
    }

    [Theory]
    [InlineData(0, "Probe")]
    [InlineData(8888, "Laser")]
    [InlineData(999991, "3D Probe")]
    [InlineData(3, "T3")]
    [InlineData(-1, "No Tool")]
    public void ToolsHaveReadableLabels(int tool, string label) => Assert.Equal(label, ToolInfo.Label(tool));

    [Fact]
    public async Task StatusReportsPublishToolAndAtcLabels()
    {
        var (controller, _, _) = await Start();
        await using var _ = controller;
        Assert.Equal("T1", controller.State.Get<string>(StatePaths.ToolLabel));
        Assert.Equal("", controller.State.Get<string>(StatePaths.ToolTargetLabel));
        Assert.Equal("", controller.State.Get<string>(StatePaths.AtcLabel));
    }

    [Fact]
    public async Task ChangingToolAsksFirstAndSendsTheChange()
    {
        var (controller, commands, host) = await Start();
        await using var _ = controller;

        host.Confirm = false;
        await commands.ExecuteAsync("toolChange", new CommandArgs(new Dictionary<string, object?> { ["tool"] = 3.0 }));
        Assert.Single(host.Questions);
        await Task.Delay(200);
        Assert.Equal(1, controller.State.Get(StatePaths.ToolCurrent, -1));

        host.Confirm = true;
        await commands.ExecuteAsync("toolChange", new CommandArgs(new Dictionary<string, object?> { ["tool"] = 3.0 }));
        Assert.True(await WaitFor(() => controller.State.Get(StatePaths.ToolCurrent, -1) == 3));
        Assert.Equal("T3", controller.State.Get<string>(StatePaths.ToolLabel));
    }

    [Fact]
    public async Task ToolsTheMachineCannotChangeToAreRefused()
    {
        var (controller, commands, host) = await Start();
        await using var _ = controller;
        await commands.ExecuteAsync("toolChange", new CommandArgs(new Dictionary<string, object?> { ["tool"] = 7.0 }));
        Assert.Empty(host.Questions);
        await Task.Delay(200);
        Assert.Equal(1, controller.State.Get(StatePaths.ToolCurrent, -1));
    }

    [Fact]
    public async Task ToolActionsAreUnavailableWhileTheMachineIsNotIdle()
    {
        var (controller, commands, _) = await Start();
        await using var _ = controller;
        Assert.True(commands.CanExecute("toolDrop", CommandArgs.Empty));
        controller.State.Set(StatePaths.MachineState, "Run");
        Assert.False(commands.CanExecute("toolDrop", CommandArgs.Empty));
        controller.State.Set(StatePaths.MachineState, "Idle");
        controller.State.Set(StatePaths.AtcState, 2);
        Assert.False(commands.CanExecute("toolChange", CommandArgs.Empty));
    }

    [Fact]
    public async Task TheToolNumberCanBeTypedInAndSetWithoutMoving()
    {
        var (controller, commands, host) = await Start();
        await using var _ = controller;
        host.Answer = "5";
        await commands.ExecuteAsync("toolSetNumber");
        Assert.Empty(host.Questions);
        Assert.True(await WaitFor(() => controller.Console.Entries.Any(e => e.Text.Contains("M493.2T5"))));
    }
}

public class StallDetectionTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task SilenceIsReportedAndClearsWhenTheMachineAnswersAgain()
    {
        var machine = new SimulatedMachine();
        await using var controller = new CarveraController(streamFactory: _ => machine)
        {
            StatusInterval = TimeSpan.FromMilliseconds(30), StallTimeout = TimeSpan.FromMilliseconds(300),
        };
        machine.IgnoreStatusQueries = true;
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(controller.State.Get(StatePaths.AwaitingStatus, false));
        Assert.Equal("Wait", controller.State.Get<string>(StatePaths.MachineState));

        Assert.True(await WaitFor(() => controller.State.Get(StatePaths.Stalled, false)));
        Assert.True(controller.State.Get(StatePaths.AwaitingStatus, false));

        machine.IgnoreStatusQueries = false;
        Assert.True(await WaitFor(() => !controller.State.Get(StatePaths.Stalled, true)));
        Assert.False(controller.State.Get(StatePaths.AwaitingStatus, true));
        Assert.Equal("Idle", controller.State.Get<string>(StatePaths.MachineState));
    }

    [Fact]
    public async Task AHealthyMachineNeverStalls()
    {
        var machine = new SimulatedMachine();
        await using var controller = new CarveraController(streamFactory: _ => machine)
        {
            StatusInterval = TimeSpan.FromMilliseconds(30), StallTimeout = TimeSpan.FromMilliseconds(300),
        };
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => !controller.State.Get(StatePaths.AwaitingStatus, true)));
        await Task.Delay(700);
        Assert.False(controller.State.Get(StatePaths.Stalled, false));
    }
}
