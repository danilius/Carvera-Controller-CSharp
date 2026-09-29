using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Carvera.Core.Transfer;
using Xunit;

namespace Carvera.Tests;

public class RemoteFilesTests
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
        public string? UploadedInto, SavePath, Opened;
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult(SavePath);
        public Task<bool> ConfirmAsync(string message) { Questions.Add(message); return Task.FromResult(Confirm); }
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(Answer);
        public Task UploadFileAsync(string? path, string? remoteDirectory) { UploadedInto = remoteDirectory; return Task.CompletedTask; }
        public Task OpenFileAsync(string? path) { Opened = path; return Task.CompletedTask; }
        public Task CloseFileAsync() => Task.CompletedTask;
        public Task LoadLayoutAsync(string name) => Task.CompletedTask;
        public Task ReloadLayoutAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
        public Task SaveFileAsync(string? path) => Task.CompletedTask;
        public Task SetOperationToolAsync(int operation, int tool) => Task.CompletedTask;
        public Task StepPreviewAsync(int? delta) => Task.CompletedTask;
        public Task SelectOperationAsync(int operation) => Task.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required CarveraController Controller { get; init; }
        public required SimulatedMachine Machine { get; init; }
        public required RemoteBrowser Browser { get; init; }
        public required CommandRegistry Commands { get; init; }
        public required Host Host { get; init; }
        public StateStore State => Controller.State;

        public async ValueTask DisposeAsync()
        {
            Browser.Dispose();
            await Controller.DisposeAsync();
        }
    }

    private static async Task<Rig> Start(bool connect = true)
    {
        var machine = new SimulatedMachine();
        var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var host = new Host();
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        var browser = new RemoteBrowser(controller, "/sd/gcodes");
        RemoteCommands.Register(commands, browser, gate: new TransferGate(), host: host);
        if (connect) await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        return new Rig { Controller = controller, Machine = machine, Browser = browser, Commands = commands, Host = host };
    }

    [Theory]
    [InlineData("/sd/gcodes", "a.nc", "/sd/gcodes/a.nc")]
    [InlineData("/sd/gcodes/", "/a.nc", "/sd/gcodes/a.nc")]
    [InlineData("\\sd\\gcodes", "a b.nc", "/sd/gcodes/a b.nc")]
    public void PathsAreJoinedWithSingleSlashes(string directory, string name, string expected) =>
        Assert.Equal(expected, RemoteFiles.Combine(directory, name));

    [Theory]
    [InlineData("/sd/gcodes/a.nc", "/sd/gcodes")]
    [InlineData("/sd/gcodes", "/sd")]
    [InlineData("/sd", "/sd")] // never above the SD card
    [InlineData("/", "/sd")]
    [InlineData("/sd/gcodes/../gcodes/./x", "/sd/gcodes")]
    public void TheParentNeverLeavesTheSdCard(string path, string expected) => Assert.Equal(expected, RemoteFiles.Parent(path));

    [Fact]
    public void ListingsAreParsedLikeThePythonController()
    {
        var entries = RemoteFiles.ParseListing("/sd/gcodes", [
            "zeta.nc 1024 20260105123000",
            "old\x01jobs/ 0 20250101000000",
            "Alpha\x01part.nc 5 20260228235959",
            ".hidden 3 20260101000000",   // hidden files are not shown
            "<Idle|MPos:0,0,0>",          // a status report that slipped in
            "not a listing line",
            "broken 12 notatimestamp",
        ]);
        Assert.Equal(["old jobs", "Alpha part.nc", "zeta.nc"], entries.Select(e => e.Name)); // folders first, then by name
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("/sd/gcodes/Alpha part.nc", entries[1].Path);
        Assert.Equal(5, entries[1].Size);
        Assert.Equal(new DateTime(2026, 2, 28, 23, 59, 59), entries[1].Modified);
    }

    [Fact]
    public async Task ListsTheSimulatedSdCard()
    {
        await using var rig = await Start();
        var entries = await rig.Browser.Files.ListAsync("/sd/gcodes");
        Assert.Equal(["old jobs", "demo part.nc", "notes.txt"], entries.Select(e => e.Name));
        Assert.DoesNotContain(entries, e => e.Name.StartsWith('.'));
        Assert.Equal(24, entries.Single(e => e.Name == "demo part.nc").Size);
        await Assert.ThrowsAsync<RemoteFileException>(() => rig.Browser.Files.ListAsync("/sd/missing"));
        Assert.False(rig.Controller.Console.Entries.Any(e => e.Text.Contains("demo part")), "listings stay out of the console");
    }

    [Fact]
    public async Task StatusPollingResumesAfterAListing()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get<string>(StatePaths.MachineState) == "Idle"));
        await rig.Browser.Files.ListAsync("/sd/gcodes");
        await rig.Controller.SendLineAsync("$J X5");
        var before = rig.State.Get<double>(StatePaths.AxisWork("x"));
        Assert.True(await WaitFor(() => Math.Abs(rig.State.Get<double>(StatePaths.AxisWork("x")) - (before + 5)) < 1e-6 || rig.State.Get<double>(StatePaths.AxisWork("x")) != before));
    }

    [Fact]
    public async Task MakesRenamesAndDeletes()
    {
        await using var rig = await Start();
        var files = rig.Browser.Files;
        await files.MakeDirectoryAsync("/sd/gcodes/new folder");
        Assert.Contains("/sd/gcodes/new folder", rig.Machine.Folders);
        await Assert.ThrowsAsync<RemoteFileException>(() => files.MakeDirectoryAsync("/sd/gcodes/new folder")); // exists

        await files.RenameAsync("/sd/gcodes/notes.txt", "/sd/gcodes/new folder/notes 2.txt");
        Assert.Contains("/sd/gcodes/new folder/notes 2.txt", rig.Machine.Files.Keys);
        Assert.DoesNotContain("/sd/gcodes/notes.txt", rig.Machine.Files.Keys);

        await Assert.ThrowsAsync<RemoteFileException>(() => files.DeleteAsync("/sd/gcodes/new folder")); // not empty
        await files.DeleteAsync("/sd/gcodes/new folder/notes 2.txt");
        await files.DeleteAsync("/sd/gcodes/new folder");
        Assert.DoesNotContain("/sd/gcodes/new folder", rig.Machine.Folders);
        await Assert.ThrowsAsync<RemoteFileException>(() => files.DeleteAsync("/sd/gcodes/nothing.nc"));
    }

    [Fact]
    public async Task TheBrowserListsByItselfOnceConnectedAndIdleAndPublishesItsState()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        Assert.Equal("/sd/gcodes", rig.State.Get<string>(StatePaths.RemoteDirectory));
        Assert.False(rig.State.Get<bool>(StatePaths.RemoteLoading));
        Assert.Null(rig.State.Get<string>(StatePaths.RemoteError));

        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/demo part.nc"));
        Assert.Equal("demo part.nc", rig.State.Get<string>(StatePaths.RemoteSelectedName));
        Assert.False(rig.State.Get<bool>(StatePaths.RemoteSelectedIsDirectory));
    }

    [Fact]
    public async Task NavigatesIntoFoldersAndBackUp()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/old jobs"));
        Assert.True(rig.State.Get<bool>(StatePaths.RemoteSelectedIsDirectory));
        await rig.Commands.ExecuteAsync("remoteOpen"); // the selected folder
        Assert.True(await WaitFor(() => rig.State.Get<string>(StatePaths.RemoteDirectory) == "/sd/gcodes/old jobs"));
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, -1) == 0));

        Assert.True(rig.Commands.CanExecute("remoteUp", CommandArgs.Empty));
        await rig.Commands.ExecuteAsync("remoteUp");
        Assert.True(await WaitFor(() => rig.State.Get<string>(StatePaths.RemoteDirectory) == "/sd/gcodes"));
        await rig.Commands.ExecuteAsync("remoteUp");
        Assert.True(await WaitFor(() => rig.State.Get<string>(StatePaths.RemoteDirectory) == "/sd"));
        Assert.False(rig.Commands.CanExecute("remoteUp", CommandArgs.Empty)); // already at the top
    }

    [Fact]
    public async Task ErrorsArePublishedNotThrown()
    {
        await using var rig = await Start();
        await rig.Browser.OpenAsync("/sd/nowhere");
        Assert.Contains("could not list", rig.State.Get<string>(StatePaths.RemoteError));
        await rig.Browser.OpenAsync("/sd/gcodes");
        Assert.Null(rig.State.Get<string>(StatePaths.RemoteError));
    }

    [Fact]
    public async Task DeleteAsksFirstAndDoesNothingWhenDeclined()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/notes.txt"));

        rig.Host.Confirm = false;
        await rig.Commands.ExecuteAsync("remoteDelete");
        Assert.Contains("Delete the file “notes.txt”", rig.Host.Questions.Single());
        Assert.Contains("/sd/gcodes/notes.txt", rig.Machine.Files.Keys);

        rig.Host.Confirm = true;
        await rig.Commands.ExecuteAsync("remoteDelete");
        Assert.True(await WaitFor(() => !rig.Machine.Files.ContainsKey("/sd/gcodes/notes.txt")));
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 2)); // the list refreshed
        Assert.Null(rig.State.Get<string>(StatePaths.RemoteSelected));
    }

    [Fact]
    public async Task RenameAndNewFolderTakeTheirNamesFromThePrompt()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        rig.Host.Answer = "fixtures";
        await rig.Commands.ExecuteAsync("remoteMkdir");
        Assert.True(await WaitFor(() => rig.Machine.Folders.Contains("/sd/gcodes/fixtures")));

        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/notes.txt"));
        rig.Host.Answer = "readme.txt";
        await rig.Commands.ExecuteAsync("remoteRename");
        Assert.True(await WaitFor(() => rig.Machine.Files.ContainsKey("/sd/gcodes/readme.txt")));
        Assert.Equal("readme.txt", rig.State.Get<string>(StatePaths.RemoteSelectedName)); // the renamed file stays selected after the refresh

        rig.Host.Answer = null; // cancelled
        await rig.Commands.ExecuteAsync("remoteMkdir");
        Assert.DoesNotContain(rig.Machine.Folders, f => f.EndsWith("/null"));
    }

    [Fact]
    public async Task RunSendsPlayForTheSelectedFileAfterConfirming()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        Assert.True(await WaitFor(() => rig.State.Get<string>(StatePaths.MachineState) == "Idle"));
        Assert.False(rig.Commands.CanExecute("remotePlay", CommandArgs.Empty)); // nothing selected
        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/demo part.nc"));
        Assert.True(rig.Commands.CanExecute("remotePlay", CommandArgs.Empty));
        await rig.Commands.ExecuteAsync("remotePlay");
        Assert.Contains(rig.Controller.Console.Entries, e => e.Kind == ConsoleEntryKind.Sent && e.Text == "play /sd/gcodes/demo\x01part.nc");
    }

    [Fact]
    public async Task UploadHerePassesTheFolderShown()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get<string>(StatePaths.MachineState) == "Idle")); // uploads need an idle machine
        await rig.Commands.ExecuteAsync("remoteUpload");
        Assert.Equal("/sd/gcodes", rig.Host.UploadedInto);
    }

    [Fact]
    public async Task AnUploadedFileAppearsInTheListing()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        var local = Path.Combine(Path.GetTempPath(), "carvera-browser-" + Guid.NewGuid().ToString("N") + ".nc");
        await File.WriteAllTextAsync(local, "G0 X1\n");
        try
        {
            Assert.Equal(UploadResult.Success, await FileUploader.UploadAsync(rig.Controller, local, new UploadOptions("/sd/gcodes"), default));
            Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 4)); // refreshed by itself
            Assert.Contains(rig.Browser.Entries, e => e.Name == Path.GetFileName(local));
        }
        finally { File.Delete(local); }
    }

    [Fact]
    public async Task DisconnectingForgetsTheListing()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        await rig.Controller.DisconnectAsync();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, -1) == 0));
        Assert.Empty(rig.Browser.Entries);
    }
}
