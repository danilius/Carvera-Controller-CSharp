using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Job;
using Carvera.Core.Probing;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class JobSetupTests
{
    private sealed class Host : IAppHost
    {
        public string? Prompted;
        public bool Answer = true;
        public List<string> Questions { get; } = [];
        public Task OpenFileAsync(string? path) => Task.CompletedTask;
        public Task CloseFileAsync() => Task.CompletedTask;
        public Task LoadLayoutAsync(string name) => Task.CompletedTask;
        public Task ReloadLayoutAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
        public Task SaveFileAsync(string? path) => Task.CompletedTask;
        public Task SetOperationToolAsync(int operation, int tool) => Task.CompletedTask;
        public Task StepPreviewAsync(int? delta) => Task.CompletedTask;
        public Task SelectOperationAsync(int operation) => Task.CompletedTask;
        public Task UploadFileAsync(string? path, string? remoteDirectory) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message) { Questions.Add(message); return Task.FromResult(Answer); }
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(Prompted ?? initial);
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);
    }

    private static async Task<(CarveraController Controller, CommandRegistry Commands, MemoryProbeStore Store, Host Host)> Start()
    {
        var machine = new SimulatedMachine();
        var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var commands = new CommandRegistry(new CommandContext(controller));
        var host = new Host();
        var store = new MemoryProbeStore();
        StandardCommands.Register(commands);
        ProbeCommands.Register(commands, host, store);
        WorkCommands.Register(commands, host);
        JobCommands.Register(commands, host, store);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        var until = DateTime.UtcNow.AddSeconds(6);
        while (controller.State.Get<string>(StatePaths.MachineState) != "Idle" && DateTime.UtcNow < until) await Task.Delay(20);
        return (controller, commands, store, host);
    }

    private static List<string> Sent(CarveraController controller) =>
        controller.Console.Entries.Where(e => e.Kind == ConsoleEntryKind.Sent).Select(e => e.Text.Trim()).ToList();

    [Fact]
    public void TheBedGeometryComesFromTheMachineSettingsWithDefaultsForWhatIsMissing()
    {
        var geometry = BedGeometry.From(new Dictionary<string, string>
        {
            ["coordinate.anchor1_x"] = "-300.5", ["coordinate.worksize_x"] = "300  # width", ["coordinate.anchor_width"] = "12",
        });
        Assert.Equal(-300.5, geometry.Anchor1X);
        Assert.Equal(300, geometry.SizeX);
        Assert.Equal(12, geometry.AnchorWidth);
        Assert.Equal(BedGeometry.Default.Anchor1Y, geometry.Anchor1Y);
        // A machine position maps to the bed picture, which starts one anchor width outside the anchor's corner.
        var (x, y) = geometry.ToBed(-300.5, BedGeometry.Default.Anchor1Y);
        Assert.Equal((12, 12), (x, y));
        Assert.Equal((-312.5, BedGeometry.Default.Anchor1Y - 12), geometry.BedCorner);
    }

    [Fact]
    public void TheGeometryRoundTripsThroughTheState()
    {
        var state = new StateStore();
        var geometry = BedGeometry.Default with { SizeX = 300, Anchor2OffsetY = 50 };
        geometry.Publish(state);
        Assert.Equal(geometry with { RotationOffsetX = BedGeometry.Default.RotationOffsetX }, BedGeometry.FromState(state) with { RotationOffsetX = geometry.RotationOffsetX });
    }

    [Fact]
    public void TheSetupPublishesTheChoiceTheOriginAndThePath()
    {
        var state = new StateStore();
        var store = new MemoryProbeStore { Job = new JobSettings(Origin: OriginChoice.Anchor2, OriginX: 5, LevelPointsX: 4, LevelPointsY: 3, LevelXn: 2) };
        using var setup = new JobSetup(state, store);
        Assert.Equal("anchor2", state.Get<string>(StatePaths.JobOrigin));
        Assert.Equal(5.0, state.Get<double>(StatePaths.JobOriginOffsetX));
        Assert.Contains("4 x 3 points", state.Get<string>(StatePaths.JobLevelText));
        Assert.Contains("-X 2", state.Get<string>(StatePaths.JobLevelText));
        Assert.Equal("No file open", state.Get<string>(StatePaths.JobBoundsText));

        state.Set(StatePaths.FileHasBounds, true);
        state.Set(StatePaths.FileXMin, -1.5);
        state.Set(StatePaths.FileXMax, 61.5);
        state.Set(StatePaths.FileYMin, 2.0);
        state.Set(StatePaths.FileYMax, 12.0);
        Assert.StartsWith("63 x 10 x 0 mm", state.Get<string>(StatePaths.JobBoundsText));
        Assert.Equal("(-1.5, 2) from the work origin", state.Get<string>(StatePaths.JobPathOriginText));

        // The work origin's place is worked out against the anchors.
        state.Set(StatePaths.AxisOffset("x"), BedGeometry.Default.Anchor1X + 20);
        state.Set(StatePaths.AxisOffset("y"), BedGeometry.Default.Anchor1Y + 10);
        Assert.Contains("(20, 10) from anchor 1", state.Get<string>(StatePaths.JobOriginText));
        Assert.Contains("(-70, -35) from anchor 2", state.Get<string>(StatePaths.JobOriginText));
    }

    [Theory]
    [InlineData("10 5", 10.0, 5.0)]
    [InlineData("-1.5, 2", -1.5, 2.0)]
    public void PairsAreRead(string text, double x, double y) => Assert.Equal((x, y), JobCommands.ParsePair(text));

    [Theory]
    [InlineData("")] [InlineData("10")] [InlineData("a b")] [InlineData("1 2 3")]
    public void BadPairsAreRefused(string text) => Assert.Null(JobCommands.ParsePair(text));

    [Fact]
    public void TheLevellingGridIsReadWithOrWithoutMargins()
    {
        var current = new JobSettings();
        var plain = JobCommands.ParseLevelling("5 4 3", current)!;
        Assert.Equal((5, 4, 3.0), (plain.LevelPointsX, plain.LevelPointsY, plain.LevelHeight));
        var margins = JobCommands.ParseLevelling("3 3 5 1 2 3 4", current)!;
        Assert.Equal([1.0, 2.0, 3.0, 4.0], margins.LevelOffsets);
        Assert.Null(JobCommands.ParseLevelling("1 3 5", current)); // fewer than two points make no grid
        Assert.Null(JobCommands.ParseLevelling("3 3", current));
        Assert.Null(JobCommands.ParseLevelling("3.5 3 5", current));
    }

    [Fact]
    public void WithoutAProgramOnlyTheZProbeCanRun()
    {
        var state = new StateStore();
        Assert.Equal(["M495 X0Y0O5F6\n"], ProbeCommands.AutoRun(state, new ProbeCommands.AutoOptions(ZProbe: true, ZProbeOffsetX: 5, ZProbeOffsetY: 6)));
        Assert.Empty(ProbeCommands.AutoRun(state, new ProbeCommands.AutoOptions(ZProbe: true, Margin: true)));
        Assert.Empty(ProbeCommands.AutoRun(state, new ProbeCommands.AutoOptions(ZProbe: true, GotoOrigin: true)));
    }

    [Fact]
    public async Task AnAnchorSetsTheWorkOriginAtOnceAfterAskingAndProbeOnlySelects()
    {
        var (controller, commands, store, host) = await Start();
        await using var _ = controller;

        Assert.True(await commands.ExecuteAsync("jobOrigin", CommandArgs.Empty.With("anchor", "anchor2").With("x", 10).With("y", -5)));
        Assert.Single(host.Questions);
        Assert.Contains("anchor 2", host.Questions[0]);
        await Task.Delay(100);
        // anchor 1 + anchor 2's offset + the typed offset, in machine coordinates
        Assert.Contains("G10L2P0X-260.158Y-194.568", Sent(controller));
        Assert.Equal(new JobSettings(Origin: OriginChoice.Anchor2, OriginX: 10, OriginY: -5), store.Job);
        Assert.Equal("anchor2", controller.State.Get<string>(StatePaths.JobOrigin));

        var before = Sent(controller).Count(l => l.StartsWith("G10L2"));
        host.Answer = false;
        await commands.ExecuteAsync("jobOrigin", CommandArgs.Empty.With("anchor", "anchor1"));
        Assert.Equal("anchor2", store.Job.Origin); // declined: nothing changed
        host.Answer = true;
        await commands.ExecuteAsync("jobOrigin", CommandArgs.Empty.With("anchor", "probe"));
        await Task.Delay(100);
        Assert.Equal("probe", store.Job.Origin);
        Assert.Equal(before, Sent(controller).Count(l => l.StartsWith("G10L2"))); // choosing the probe sends nothing

        Assert.False(await commands.ExecuteAsync("jobOrigin", CommandArgs.Empty.With("anchor", "left")));
    }

    [Fact]
    public async Task TheOffsetIsAskedForAndAppliedToTheChosenAnchor()
    {
        var (controller, commands, store, host) = await Start();
        await using var _ = controller;
        await commands.ExecuteAsync("jobOrigin", CommandArgs.Empty.With("anchor", "anchor1"));
        host.Prompted = "3 4";
        await commands.ExecuteAsync("jobOffset");
        await Task.Delay(100);
        Assert.Equal((3.0, 4.0), (store.Job.OriginX, store.Job.OriginY));
        Assert.Contains("G10L2P0X-357.158Y-230.568", Sent(controller));
    }

    [Fact]
    public async Task TheStepsToggleAndTheRunUsesTheTickedOnes()
    {
        var (controller, commands, store, _) = await Start();
        await using var _c = controller;
        await commands.ExecuteAsync("jobToggle", CommandArgs.Empty.With("name", "zprobe").With("on", false));
        await commands.ExecuteAsync("jobToggle", CommandArgs.Empty.With("name", "gotoOrigin").With("on", false));
        Assert.False(store.Job.ZProbe);
        Assert.False(store.Job.GotoOrigin);
        Assert.False(await commands.ExecuteAsync("jobRun")); // nothing ticked: refused with a message

        await commands.ExecuteAsync("jobToggle", CommandArgs.Empty.With("name", "zprobe"));
        Assert.True(store.Job.ZProbe);
        controller.State.Set(StatePaths.ZProbeOrigin, "work");
        store.ZProbe = new ZProbeSetting("work", 3, 4);
        await commands.ExecuteAsync("jobRun", CommandArgs.Empty.With("confirmed", true));
        await Task.Delay(100);
        // no program is open, so the area is the work origin itself and only the Z probe runs
        Assert.Contains("M495 X0Y0O3F4", Sent(controller));
        Assert.False(await commands.ExecuteAsync("jobToggle", CommandArgs.Empty.With("name", "bogus")));
    }

    [Fact]
    public async Task TheGridCanBeSetFromText()
    {
        var (controller, commands, store, host) = await Start();
        await using var _ = controller;
        host.Prompted = "4 5 6 1 1 2 2";
        await commands.ExecuteAsync("jobLevelSetup");
        Assert.Equal((4, 5, 6.0), (store.Job.LevelPointsX, store.Job.LevelPointsY, store.Job.LevelHeight));
        Assert.Equal([1.0, 1.0, 2.0, 2.0], store.Job.LevelOffsets);
        host.Prompted = "nonsense";
        Assert.False(await commands.ExecuteAsync("jobLevelSetup"));
        Assert.Equal(4, store.Job.LevelPointsX);
    }
}
