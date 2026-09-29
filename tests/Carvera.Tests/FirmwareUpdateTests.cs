using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Carvera.Core.Transfer;
using Xunit;

namespace Carvera.Tests;

public class FirmwareUpdateTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 10000)
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
        public List<string> Questions { get; } = [];
        public bool Confirm { get; set; } = true;
        public string? Picked { get; set; }
        public Task<bool> ConfirmAsync(string message) { Questions.Add(message); return Task.FromResult(Confirm); }
        public Task<string?> PickOpenPathAsync(string title, params string[] patterns) => Task.FromResult(Picked);
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);
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

    private static async Task<(SimulatedMachine, CarveraController, CommandRegistry, Host)> Start()
    {
        var machine = new SimulatedMachine();
        var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var host = new Host();
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        RemoteCommands.Register(commands, new RemoteBrowser(controller, "/sd/gcodes"), new TransferGate(), host);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
        return (machine, controller, commands, host);
    }

    [Fact]
    public async Task TheFirmwareGoesToTheExactNameUncompressedThenTheMachineIsReset()
    {
        var (machine, controller, commands, host) = await Start();
        await using var _ = controller;
        var file = Path.Combine(Path.GetTempPath(), $"carvera-fw-{Guid.NewGuid():N}.bin");
        var data = Enumerable.Range(0, 20000).Select(i => (byte)(i * 31 % 251)).ToArray();
        await File.WriteAllBytesAsync(file, data);
        try
        {
            host.Picked = file;
            Assert.True(await commands.ExecuteAsync("updateFirmware"));
            Assert.True(await WaitFor(() => machine.Files.ContainsKey("/sd/firmware.bin")));
            Assert.Equal(data, machine.Files["/sd/firmware.bin"]);
            Assert.Equal(2, host.Questions.Count); // update? then reset now?
            Assert.True(await WaitFor(() => controller.Console.Entries.Any(e => e.Text.Contains("reset", StringComparison.OrdinalIgnoreCase))));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task DecliningTheQuestionSendsNothing()
    {
        var (machine, controller, commands, host) = await Start();
        await using var _ = controller;
        host.Picked = typeof(FirmwareUpdateTests).Assembly.Location;
        host.Confirm = false;
        await commands.ExecuteAsync("updateFirmware");
        await Task.Delay(300);
        Assert.False(machine.Files.ContainsKey("/sd/firmware.bin"));
        Assert.Single(host.Questions);
    }
}
