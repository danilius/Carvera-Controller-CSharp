using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Config;
using Carvera.Core.Connection;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class MachineConfigTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 8000)
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
    public void ConfigTextIsParsedLikeThePythonController()
    {
        var values = ConfigFile.Parse("# comment\nswitch.light.startup_state   true\nlight.turn_off_min\t10  # idle\n\nwifi.machine_name  My Carvera\nempty_key\n");
        Assert.Equal("true", values["switch.light.startup_state"]);
        Assert.Equal("10", values["light.turn_off_min"]);
        Assert.Equal("My Carvera", values["wifi.machine_name"]);
        Assert.Equal("", values["empty_key"]);
        Assert.Equal(4, values.Count);
    }

    [Theory]
    [InlineData("C1")]
    [InlineData("CA1")]
    public void BothModelsHaveASettingsListWithEditableItems(string model)
    {
        var items = ConfigSchema.Load(model);
        Assert.NotEmpty(items);
        Assert.NotEmpty(ConfigSchema.Editable(items).Where(i => !i.IsTitle));
        Assert.All(items.Where(i => !i.IsTitle && i.Section is "Basic" or "Advanced"), i => Assert.NotEmpty(i.Key));
        Assert.Empty(ConfigSchema.Load("nonsense"));
    }

    private sealed class Host : IAppHost
    {
        public bool Confirm { get; set; } = true;
        public Task<bool> ConfirmAsync(string message) => Task.FromResult(Confirm);
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

    [Fact]
    public async Task SettingsAreReadEditedAndSentToTheSimulator()
    {
        var machine = new SimulatedMachine();
        await using var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var store = new MachineConfigStore(controller);
        var host = new Host();
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        ConfigCommands.Register(commands, store, host);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle" && controller.State.Get<string>(StatePaths.MachineModelName) is not null));

        Assert.True(await commands.ExecuteAsync("configLoad"));
        Assert.True(store.Loaded);
        var vacuum = store.Items.First(i => i.Key == "light.turn_off_min");
        var light = store.Items.First(i => i.Key == "switch.light.startup_state");
        var cover = store.Items.First(i => i.Key == "stop_on_cover_open");
        Assert.Equal("10", store.Current(vacuum));
        Assert.Equal("true", store.Current(light));
        Assert.Equal("false", store.Current(cover));

        store.Edit(vacuum, "25");
        store.Edit(cover, "true");
        store.Edit(light, "true"); // unchanged: not a pending change
        Assert.Equal(2, store.Pending.Count);
        Assert.Equal(2, controller.State.Get(StatePaths.ConfigPending, 0));
        store.Edit(cover, "false"); // put back: no longer pending
        Assert.Single(store.Pending);

        Assert.True(await commands.ExecuteAsync("configApply"));
        Assert.Empty(store.Pending);
        Assert.True(await WaitFor(() => System.Text.Encoding.ASCII.GetString(machine.Files["/sd/config.txt"]).Contains("light.turn_off_min  25")));

        Assert.True(await commands.ExecuteAsync("configLoad"));
        Assert.Equal("25", store.Current(vacuum));
    }

    [Fact]
    public async Task RestoreAndDefaultAskFirst()
    {
        var machine = new SimulatedMachine();
        await using var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30) };
        var host = new Host { Confirm = false };
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        ConfigCommands.Register(commands, new MachineConfigStore(controller), host);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));

        await commands.ExecuteAsync("configRestore");
        await Task.Delay(200);
        Assert.DoesNotContain(controller.Console.Entries, e => e.Text == "config-restore");
        host.Confirm = true;
        await commands.ExecuteAsync("configRestore");
        Assert.True(await WaitFor(() => controller.Console.Entries.Any(e => e.Text == "config-restore")));
    }
}
