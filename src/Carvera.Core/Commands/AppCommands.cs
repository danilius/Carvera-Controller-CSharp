using Carvera.Core.State;

namespace Carvera.Core.Commands;

/// <summary>Application services that some commands need (implemented by the desktop app).</summary>
public interface IAppHost
{
    Task OpenFileAsync(string? path);
    Task CloseFileAsync();
    Task LoadLayoutAsync(string name);
    Task ReloadLayoutAsync();
    Task ExitAsync();
    Task SaveFileAsync(string? path);
    Task SetOperationToolAsync(int operation, int tool);
    /// <summary>Moves the scrub position by <paramref name="delta"/> segments; null clears the preview.</summary>
    Task StepPreviewAsync(int? delta);
    Task SelectOperationAsync(int operation);
    /// <summary>
    /// Sends a local file to the machine, or cancels the upload that is running. Without a path it uses the open G-code
    /// file, asking for one when none is open; without a directory it uses the upload folder from the settings.
    /// </summary>
    Task UploadFileAsync(string? path, string? remoteDirectory);
    /// <summary>Asks a yes/no question. Returns false when the user says no.</summary>
    Task<bool> ConfirmAsync(string message);
    /// <summary>Asks for a line of text. Returns null when the user cancels.</summary>
    Task<string?> PromptAsync(string title, string label, string initial);
    /// <summary>Asks where to save a file. Returns the chosen path, or null when the user cancels.</summary>
    Task<string?> PickSavePathAsync(string suggestedName);
    /// <summary>Asks which existing file to use. Returns its path, or null when the user cancels (or the host cannot ask).</summary>
    Task<string?> PickOpenPathAsync(string title, params string[] patterns) => Task.FromResult<string?>(null);
    /// <summary>Asks which folder to use. Returns its path, or null when the user cancels (or the host cannot ask).</summary>
    Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
}

public static class AppCommands
{
    public static void Register(CommandRegistry registry, IAppHost host)
    {
        void Add(string id, string title, Func<CommandArgs, Task> execute, string description, CommandParameter[]? parameters = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Application", Description = description, RequiresConnection = false,
                Parameters = parameters ?? [], Execute = (_, a) => execute(a),
            });

        Add("openFile", "Open G-code file", a => host.OpenFileAsync(a.GetString("path")), "Opens a local G-code file for preview; asks for a file when 'path' is omitted.",
            [new("path", "string", "File to open")]);
        Add("closeFile", "Close file", _ => host.CloseFileAsync(), "Closes the local G-code file.");
        Add("openLayout", "Switch layout", a => host.LoadLayoutAsync(a.GetString("name") ?? throw new ArgumentException("No layout name given.")),
            "Loads another layout file by name.", [new("name", "string", "Layout file name without .json", true)]);
        Add("reloadLayout", "Reload layout", _ => host.ReloadLayoutAsync(), "Reloads the current layout file from disk.");
        Add("exit", "Exit", _ => host.ExitAsync(), "Closes the application.");
        Add("saveFile", "Save G-code as", a => host.SaveFileAsync(a.GetString("path")), "Saves the (edited) local G-code file; asks for a name when 'path' is omitted.",
            [new("path", "string", "File to write")]);
        Add("setOperationTool", "Change operation tool", a => host.SetOperationToolAsync(
                a.GetInt("operation") ?? throw new ArgumentException("No operation given."),
                a.GetInt("tool") ?? throw new ArgumentException("No tool given.")),
            "Makes one operation of the local G-code use another tool. Only the copy in memory changes until you save it.",
            [new("operation", "number", "Operation index (0-based)", true), new("tool", "number", "New tool number", true)]);
        Add("previewStep", "Scrub G-code", a => host.StepPreviewAsync(a.GetInt("delta") ?? 1),
            "Moves the preview scrub position by 'delta' path segments.", [new("delta", "number", "Segments to move (negative goes back)")]);
        registry.Register(new CommandDefinition
        {
            Id = "uploadFile", Title = "Upload file", Category = "Files",
            Description = "Sends the open G-code file (or 'path') to the machine over the connection; press it again during an upload to cancel. Only available while the machine is idle.",
            Parameters = [new("path", "string", "Local file; defaults to the open file, or asks"), new("remoteDirectory", "string", "Folder on the machine; defaults to the upload folder in the settings")],
            CanExecute = (c, _) => c.State.Get(StatePaths.TransferActive, false) || c.State.Get<string>(StatePaths.MachineState) == "Idle",
            DependsOn = [StatePaths.MachineState, StatePaths.TransferActive],
            Execute = (_, a) => host.UploadFileAsync(a.GetString("path"), a.GetString("remoteDirectory")),
        });
        Add("previewClear", "Show whole G-code", _ => host.StepPreviewAsync(null), "Leaves scrubbing and shows the whole program.");
        Add("selectOperation", "Show one operation", a => host.SelectOperationAsync(a.GetInt("index") ?? -1),
            "Shows only one operation in the viewer (-1 shows all).", [new("index", "number", "Operation index (0-based) or -1")]);
    }

    /// <summary>A registry holding every command, for validating layouts without a running app.</summary>
    public static CommandRegistry CreateCatalog()
    {
        var controller = new CarveraController();
        var registry = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(registry);
        Register(registry, new NullHost());
        RemoteCommands.Register(registry, new Transfer.RemoteBrowser(controller), new Transfer.TransferGate(), new NullHost());
        ToolCommands.Register(registry, new NullHost());
        ConfigCommands.Register(registry, new Config.MachineConfigStore(controller), new NullHost());
        ProbeCommands.Register(registry, new NullHost());
        WorkCommands.Register(registry, new NullHost());
        return registry;
    }

    private sealed class NullHost : IAppHost
    {
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
        public Task<bool> ConfirmAsync(string message) => Task.FromResult(true);
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(initial);
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);
    }
}
