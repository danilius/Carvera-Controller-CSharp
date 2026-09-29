using Carvera.Core.State;
using Carvera.Core.Transfer;

namespace Carvera.Core.Commands;

/// <summary>Commands that drive the remote file browser (the files on the machine's SD card).</summary>
public static class RemoteCommands
{
    private static readonly string[] SelectionPaths = [StatePaths.RemoteSelected, StatePaths.RemoteSelectedIsDirectory];

    private static bool HasFile(CommandContext c, CommandArgs _) =>
        c.State.Get<string>(StatePaths.RemoteSelected) is not null && !c.State.Get(StatePaths.RemoteSelectedIsDirectory, false);

    private static bool HasSelection(CommandContext c, CommandArgs _) => c.State.Get<string>(StatePaths.RemoteSelected) is not null;

    private static bool CanTransfer(CommandContext c) =>
        c.State.Get(StatePaths.TransferActive, false) || c.State.Get<string>(StatePaths.MachineState) == "Idle";

    public static void Register(CommandRegistry registry, RemoteBrowser browser, TransferGate gate, IAppHost host)
    {
        // Pressing a transfer button while one runs cancels it instead of starting another.
        async Task RunTransfer(Func<CancellationToken, Task> run)
        {
            var source = gate.TryBegin();
            if (source is null)
            {
                gate.Cancel();
                return;
            }
            try { await run(source.Token); }
            finally { gate.End(source); }
        }

        void Add(string id, string title, Func<CommandContext, CommandArgs, Task> execute, string description, CommandParameter[]? parameters = null,
            Func<CommandContext, CommandArgs, bool>? canExecute = null, string[]? dependsOn = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Machine files", Execute = execute, Description = description,
                Parameters = parameters ?? [], CanExecute = canExecute, DependsOn = dependsOn ?? [],
            });

        Add("remoteRefresh", "Refresh file list", (_, _) => browser.RefreshAsync(), "Lists the folder shown in the file browser again.");
        Add("remoteUp", "Up one folder", (_, _) => browser.UpAsync(), "Shows the folder above the current one (never above /sd).",
            canExecute: (c, _) => (c.State.Get<string>(StatePaths.RemoteDirectory) ?? RemoteFiles.Root) is var dir && RemoteFiles.Parent(dir) != RemoteFiles.Normalize(dir),
            dependsOn: [StatePaths.RemoteDirectory]);
        Add("remoteUpload", "Upload here", (_, _) => host.UploadFileAsync(null, browser.Directory),
            "Uploads the open G-code file (or asks for one) into the folder the file browser shows. Press it again during an upload to cancel.",
            canExecute: (c, _) => c.State.Get(StatePaths.TransferActive, false) || c.State.Get<string>(StatePaths.MachineState) == "Idle",
            dependsOn: [StatePaths.MachineState, StatePaths.TransferActive]);
        Add("remoteOpen", "Open folder", (c, a) =>
        {
            var path = a.GetString("path") ?? (c.State.Get(StatePaths.RemoteSelectedIsDirectory, false) ? c.State.Get<string>(StatePaths.RemoteSelected) : null);
            return path is null ? Task.CompletedTask : browser.OpenAsync(path);
        }, "Shows a folder: 'path', or the selected folder.", [new("path", "string", "Folder on the machine, e.g. /sd/gcodes")]);
        Add("remoteSelect", "Select file", (_, a) =>
        {
            browser.Select(a.GetString("path"));
            return Task.CompletedTask;
        }, "Selects an entry in the file browser (or clears the selection when 'path' is omitted).", [new("path", "string", "Path of the entry")]);
        Add("remoteMkdir", "New folder", async (_, a) =>
        {
            var name = a.GetString("name") ?? await host.PromptAsync("New folder", "Name of the new folder", "");
            if (!string.IsNullOrWhiteSpace(name)) await browser.MakeDirectoryAsync(name.Trim());
        }, "Creates a folder in the folder shown; asks for the name unless 'name' is given.", [new("name", "string", "Folder name")]);
        Add("remoteRename", "Rename", async (c, a) =>
        {
            var selected = browser.Selected ?? throw new InvalidOperationException("Select a file or folder first.");
            var name = a.GetString("name") ?? await host.PromptAsync("Rename", $"New name for {selected.Name}", selected.Name);
            if (!string.IsNullOrWhiteSpace(name) && name.Trim() != selected.Name) await browser.RenameAsync(selected.Path, name.Trim());
        }, "Renames the selected file or folder; asks for the name unless 'name' is given.", [new("name", "string", "New name")],
            HasSelection, SelectionPaths);
        Add("remoteDelete", "Delete", async (_, a) =>
        {
            var selected = browser.Selected ?? throw new InvalidOperationException("Select a file or folder first.");
            if (a.GetBool("confirmed") != true && !await host.ConfirmAsync($"Delete {(selected.IsDirectory ? "the folder" : "the file")} “{selected.Name}” from the machine? This cannot be undone.")) return;
            await browser.DeleteAsync(selected.Path);
        }, "Deletes the selected file, or an empty folder, from the machine after asking to confirm.", [new("confirmed", "bool", "true to skip the question")],
            HasSelection, SelectionPaths);
        registry.Register(new CommandDefinition
        {
            Id = "remoteDownload", Title = "Download", Category = "Machine files",
            Description = "Saves the selected file from the machine to your computer (asks where). Press it again during a transfer to cancel. Only available while the machine is idle.",
            Parameters = [new("path", "string", "Where to save it; asks when omitted")],
            CanExecute = (c, a) => HasFile(c, a) && CanTransfer(c),
            DependsOn = [.. SelectionPaths, StatePaths.MachineState, StatePaths.TransferActive],
            Execute = (c, a) => RunTransfer(async token =>
            {
                var selected = browser.Selected ?? throw new InvalidOperationException("Select a file first.");
                var target = a.GetString("path") ?? await host.PickSavePathAsync(selected.Name);
                if (target is null) return;
                await FileDownloader.DownloadAsync(c.Controller, selected.Path, target, selected.Size, token);
            }),
        });
        registry.Register(new CommandDefinition
        {
            Id = "remoteView", Title = "View in 3D", Category = "Machine files",
            Description = "Downloads the selected file to a temporary folder and opens it in the G-code view, without keeping a copy. Only available while the machine is idle.",
            CanExecute = (c, a) => HasFile(c, a) && CanTransfer(c),
            DependsOn = [.. SelectionPaths, StatePaths.MachineState, StatePaths.TransferActive],
            Execute = (c, _) => RunTransfer(async token =>
            {
                var selected = browser.Selected ?? throw new InvalidOperationException("Select a file first.");
                var folder = Path.Combine(Path.GetTempPath(), "CarveraController", "remote");
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, selected.Name);
                if (await FileDownloader.DownloadAsync(c.Controller, selected.Path, target, selected.Size, token) == DownloadResult.Success)
                    await host.OpenFileAsync(target);
            }),
        });
        registry.Register(new CommandDefinition
        {
            Id = "remotePlay", Title = "Run selected file", Category = "Machine files",
            Description = "Runs the selected file on the machine (like playFile). Only available while the machine is idle.",
            CanExecute = (c, a) => HasFile(c, a) && c.State.Get<string>(StatePaths.MachineState) == "Idle",
            DependsOn = [.. SelectionPaths, StatePaths.MachineState],
            Execute = async (c, _) =>
            {
                var selected = browser.Selected ?? throw new InvalidOperationException("Select a file first.");
                if (!await host.ConfirmAsync($"Run “{selected.Name}” on the machine now?")) return;
                await c.Controller.SendLineAsync(Protocol.MachineCommands.Play(selected.Path));
            },
        });
    }
}
