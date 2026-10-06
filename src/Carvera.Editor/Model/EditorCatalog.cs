using System.Reflection;
using Carvera.App.Layout;
using Carvera.Core.Commands;
using Carvera.Core.State;

namespace Carvera.Editor.Model;

/// <summary>Lists of names the property editors offer: commands, state paths, built-in pictures, theme tokens.</summary>
public static class EditorCatalog
{
    private static IReadOnlyList<CommandDefinition>? _commands;
    private static IReadOnlyList<string>? _statePaths;
    private static CommandRegistry? _registry;

    public static CommandRegistry Registry => _registry ??= AppCommands.CreateCatalog();

    public static IReadOnlyList<CommandDefinition> Commands => _commands ??= Registry.All.ToList();

    public static CommandDefinition? Command(string? id) => id is not null && Registry.TryGet(id, out var c) ? c : null;

    /// <summary>Every state path the program sets, for binding and expressions.</summary>
    public static IReadOnlyList<string> StatePathNames => _statePaths ??= BuildStatePaths();

    private static List<string> BuildStatePaths()
    {
        var paths = typeof(StatePaths).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        foreach (var axis in StatePaths.Axes)
        {
            paths.Add(StatePaths.AxisMachine(axis));
            paths.Add(StatePaths.AxisWork(axis));
            paths.Add(StatePaths.AxisOffset(axis));
        }
        paths.AddRange(["switch.light", "switch.air", "switch.vacuum", "switch.spindle"]);
        return paths.Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<string> BuiltinImages => Icons.PathData.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> ThemeDefaults => Theme.LightDefaults;

    /// <summary>Colour tokens (values that look like colours) for the colour picker.</summary>
    public static IEnumerable<string> ColorTokens => Theme.LightDefaults.Where(p => p.Value.StartsWith('#')).Select(p => p.Key);

    public static readonly string[] Expressions = ["true", "false", "connection.connected", "machine.state == 'Idle'", "machine.state == 'Run'", "job.playing", "file.hasBounds"];
}
