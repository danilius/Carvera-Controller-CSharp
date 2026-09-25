using System.Globalization;
using System.Text.Json;
using Carvera.Core.State;

namespace Carvera.Core.Commands;

public sealed record CommandParameter(string Name, string Kind, string Description, bool Required = false);

/// <summary>Arguments passed to a command, typically from a layout's "args" object.</summary>
public sealed class CommandArgs
{
    public static readonly CommandArgs Empty = new(new Dictionary<string, object?>());
    private readonly IReadOnlyDictionary<string, object?> _values;

    public CommandArgs(IReadOnlyDictionary<string, object?> values) =>
        _values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);

    public static CommandArgs FromJson(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return Empty;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
            values[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => p.Value.GetRawText(),
            };
        return new CommandArgs(values);
    }

    public CommandArgs With(string name, object? value)
    {
        var copy = new Dictionary<string, object?>(_values, StringComparer.OrdinalIgnoreCase) { [name] = value };
        return new CommandArgs(copy);
    }

    public IEnumerable<string> Names => _values.Keys;
    public bool Has(string name) => _values.ContainsKey(name) && _values[name] is not null;
    public object? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public string? GetString(string name) => Get(name) switch { null => null, string s => s, IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var o => o.ToString() };
    public double? GetDouble(string name) => Expressions.Expression.ToNumber(Get(name));
    public int? GetInt(string name) => GetDouble(name) is { } d ? (int)Math.Round(d) : null;
    public bool? GetBool(string name) => Has(name) ? StateStore.ToBoolean(Get(name)) : null;
}

public sealed class CommandContext(CarveraController controller)
{
    public CarveraController Controller { get; } = controller;
    public StateStore State => Controller.State;
    public ConsoleLog Console => Controller.Console;
}

public sealed class CommandDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Category { get; init; } = "General";
    public string Description { get; init; } = "";
    public IReadOnlyList<CommandParameter> Parameters { get; init; } = [];
    /// <summary>When true the command is unavailable while no machine is connected.</summary>
    public bool RequiresConnection { get; init; } = true;
    /// <summary>Extra availability test; re-evaluated when any of <see cref="DependsOn"/> changes.</summary>
    public Func<CommandContext, CommandArgs, bool>? CanExecute { get; init; }
    public IReadOnlyCollection<string> DependsOn { get; init; } = [];
    public required Func<CommandContext, CommandArgs, Task> Execute { get; init; }
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.OrdinalIgnoreCase);

    public CommandRegistry(CommandContext context) => Context = context;

    public CommandContext Context { get; }
    public IEnumerable<CommandDefinition> All => _commands.Values.OrderBy(c => c.Category).ThenBy(c => c.Id);
    public IReadOnlyCollection<string> Ids => _commands.Keys;

    public void Register(CommandDefinition command) => _commands[command.Id] = command;

    public bool TryGet(string id, out CommandDefinition command) => _commands.TryGetValue(id, out command!);

    public bool CanExecute(string id, CommandArgs args)
    {
        if (!_commands.TryGetValue(id, out var command)) return false;
        if (command.RequiresConnection && !Context.Controller.IsConnected) return false;
        return command.CanExecute?.Invoke(Context, args) ?? true;
    }

    /// <summary>State paths whose changes may alter <see cref="CanExecute"/> for this command.</summary>
    public IReadOnlyCollection<string> AvailabilityPaths(string id)
    {
        if (!_commands.TryGetValue(id, out var command)) return [];
        var paths = new HashSet<string>(command.DependsOn, StringComparer.OrdinalIgnoreCase);
        if (command.RequiresConnection) paths.Add(StatePaths.Connected);
        return paths;
    }

    /// <summary>Runs a command, reporting failures to the console instead of throwing.</summary>
    public async Task<bool> ExecuteAsync(string id, CommandArgs? args = null)
    {
        args ??= CommandArgs.Empty;
        if (!_commands.TryGetValue(id, out var command))
        {
            Context.Console.Error($"Unknown command '{id}'.");
            return false;
        }
        if (!CanExecute(id, args))
        {
            Context.Console.Warning(command.RequiresConnection && !Context.Controller.IsConnected
                ? $"{command.Title}: not connected to a machine."
                : $"{command.Title} is not available right now.");
            return false;
        }
        try
        {
            await command.Execute(Context, args).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Context.Console.Error($"{command.Title} failed: {ex.Message}");
            return false;
        }
    }
}
