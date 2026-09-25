namespace Carvera.Core.Commands;

/// <summary>Application services that some commands need (implemented by the desktop app).</summary>
public interface IAppHost
{
    Task OpenFileAsync(string? path);
    Task CloseFileAsync();
    Task LoadLayoutAsync(string name);
    Task ReloadLayoutAsync();
    Task ExitAsync();
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
    }

    /// <summary>A registry holding every command, for validating layouts without a running app.</summary>
    public static CommandRegistry CreateCatalog()
    {
        var registry = new CommandRegistry(new CommandContext(new CarveraController()));
        StandardCommands.Register(registry);
        Register(registry, new NullHost());
        return registry;
    }

    private sealed class NullHost : IAppHost
    {
        public Task OpenFileAsync(string? path) => Task.CompletedTask;
        public Task CloseFileAsync() => Task.CompletedTask;
        public Task LoadLayoutAsync(string name) => Task.CompletedTask;
        public Task ReloadLayoutAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
    }
}
