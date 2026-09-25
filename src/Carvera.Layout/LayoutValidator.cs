using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Carvera.Core.Commands;
using Carvera.Core.Expressions;

namespace Carvera.Layout;

/// <summary>Checks a loaded layout against the <see cref="ComponentCatalog"/> and the command registry.</summary>
public sealed partial class LayoutValidator
{
    public const string MissingSafetyPrefix = "The layout does not show a visible";

    private readonly CommandRegistry _commands;

    public LayoutValidator(CommandRegistry? commands = null) => _commands = commands ?? AppCommands.CreateCatalog();

    public void Validate(LayoutDocument document, DiagnosticBag bag)
    {
        foreach (var (node, ancestors) in document.Root.Walk())
            ValidateNode(document, node, ancestors.Count > 0 ? ancestors[^1] : null, bag);

        foreach (var (name, style) in document.Styles)
            ValidateVisual(style, $"styles.{name}", bag);

        foreach (var shortcut in document.Shortcuts)
        {
            if (string.IsNullOrWhiteSpace(shortcut.Key)) bag.Error($"{shortcut.Path}.key", "Shortcut key is empty.");
            ValidateCommand(shortcut.Command, shortcut.Args, $"{shortcut.Path}", bag);
        }

        foreach (var missing in SafetyAnalyzer.FindMissing(document))
            bag.Warning("root", $"{MissingSafetyPrefix} {missing} control.");
    }

    private void ValidateNode(LayoutDocument document, LayoutNode node, LayoutNode? parent, DiagnosticBag bag)
    {
        if (!ComponentCatalog.TryGet(node.Type, out var spec))
        {
            bag.Error($"{node.Path}.type", $"Unknown element type '{node.Type}'.{Suggest(node.Type, ComponentCatalog.All.Select(c => c.Type))}");
            return;
        }

        switch (spec.Children)
        {
            case ChildRule.None when node.Children.Count > 0:
                bag.Error(node.Path, $"'{node.Type}' cannot contain children.");
                break;
            case ChildRule.Single when node.Children.Count > 1:
                bag.Error(node.Path, $"'{node.Type}' holds a single 'child'.");
                break;
        }

        foreach (var (key, value) in node.Properties)
        {
            var path = $"{node.Path}.{key}";
            if (LayoutLoader.IsCommentKey(key)) continue;
            var prop = spec.Find(key);
            if (prop is null)
            {
                var names = spec.Properties.Select(p => p.Name).Concat(ComponentCatalog.Common.Select(p => p.Name));
                bag.Warning(path, $"'{node.Type}' has no property '{key}'; it is ignored.{Suggest(key, names)}");
                continue;
            }
            ValidateValue(prop, value, path, bag);
        }

        if (parent is not null) CheckPlacement(node, parent, bag);

        foreach (var cls in node.GetStringList("class"))
            if (!document.Styles.ContainsKey(cls)) bag.Warning($"{node.Path}.class", $"There is no style named '{cls}'.");

        var command = node.GetString("command");
        if (command is not null) ValidateCommand(command, node.Get("args") as JsonObject, node.Path, bag);
        else if (node.Type is "button") bag.Warning(node.Path, "This button has no 'command'.");

        if (node.Type is "toggle" or "indicator" && !node.Has("bind"))
            bag.Warning(node.Path, $"A {node.Type} needs 'bind' to know whether it is on.");
        if (node.Type is "choice" && node.Get("options") is not JsonArray)
            bag.Error($"{node.Path}.options", "A choice needs an 'options' array.");
    }

    private static void CheckPlacement(LayoutNode node, LayoutNode parent, DiagnosticBag bag)
    {
        void Warn(string property, string parentType)
        {
            if (node.Has(property) && !parent.Type.Equals(parentType, StringComparison.OrdinalIgnoreCase))
                bag.Warning($"{node.Path}.{property}", $"'{property}' only applies inside a {parentType}.");
        }
        foreach (var p in new[] { "row", "column", "rowSpan", "columnSpan" }) Warn(p, "grid");
        foreach (var p in new[] { "x", "y" }) Warn(p, "canvas");
    }

    private void ValidateCommand(string command, JsonObject? args, string path, DiagnosticBag bag)
    {
        if (!_commands.TryGet(command, out var definition))
        {
            bag.Error($"{path}.command", $"Unknown command '{command}'.{Suggest(command, _commands.Ids)}");
            return;
        }
        var names = definition.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (args is not null)
            foreach (var (key, _) in args)
                if (!names.Contains(key)) bag.Warning($"{path}.args.{key}", $"'{command}' has no argument '{key}'.{Suggest(key, names)}");
    }

    private void ValidateValue(PropSpec prop, JsonNode? value, string path, DiagnosticBag bag)
    {
        string? error;
        switch (prop.Kind)
        {
            case PropKind.Size:
                if (!SizeSpec.TryParse(value, out _, out error)) bag.Error(path, error!);
                break;
            case PropKind.SizeList:
                if (value is not JsonArray sizes) { bag.Error(path, "Expected an array of sizes."); break; }
                for (var i = 0; i < sizes.Count; i++)
                    if (!SizeSpec.TryParse(sizes[i], out _, out error)) bag.Error($"{path}[{i}]", error!);
                break;
            case PropKind.Edges:
                if (!Edges.TryParse(value, out _, out error)) bag.Error(path, error!);
                break;
            case PropKind.Number:
                if (value is not JsonValue n || !n.TryGetValue<double>(out _)) bag.Error(path, "Expected a number.");
                break;
            case PropKind.Bool:
                if (value is not JsonValue b || !b.TryGetValue<bool>(out _)) bag.Error(path, "Expected true or false.");
                break;
            case PropKind.String or PropKind.Command or PropKind.Image:
                if (value is not JsonValue s || !s.TryGetValue<string>(out _)) bag.Error(path, "Expected a string.");
                break;
            case PropKind.Color:
                if (value is not JsonValue c || !c.TryGetValue<string>(out var color) || !IsColor(color)) bag.Error(path, "Expected a colour such as #1F6FEB, #801F6FEB, a colour name or a theme token (@accent).");
                break;
            case PropKind.Enum:
                if (value is not JsonValue e || !e.TryGetValue<string>(out var ev) || !prop.Values!.Contains(ev, StringComparer.OrdinalIgnoreCase))
                    bag.Error(path, $"Expected one of: {string.Join(", ", prop.Values!)}.");
                break;
            case PropKind.StringList:
                if (value is not (JsonArray or JsonValue)) bag.Error(path, "Expected a string or an array of strings.");
                else if (prop.Values is not null)
                    foreach (var item in value is JsonArray arr ? arr.Select(x => x?.ToString() ?? "") : value.ToString().Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                        if (!prop.Values.Contains(item, StringComparer.OrdinalIgnoreCase)) bag.Error(path, $"'{item}' is not one of: {string.Join(", ", prop.Values)}.");
                break;
            case PropKind.NumberList:
                if (value is not JsonArray nums || nums.Any(x => x is not JsonValue v || !v.TryGetValue<double>(out _))) bag.Error(path, "Expected an array of numbers.");
                break;
            case PropKind.Expression:
                if (value is JsonValue ex && ex.TryGetValue<bool>(out _)) break;
                if (value is JsonValue exs && exs.TryGetValue<string>(out var exprText))
                {
                    if (!Expression.TryParse(exprText, out _, out error)) bag.Error(path, $"Invalid expression: {error}");
                }
                else bag.Error(path, "Expected true/false or an expression string.");
                break;
            case PropKind.Template:
                if (value is JsonValue t && t.TryGetValue<string>(out var templateText))
                {
                    if (!TextTemplate.TryParse(templateText, out _, out error)) bag.Error(path, $"Invalid text: {error}");
                }
                else if (value is not JsonValue) bag.Error(path, "Expected text.");
                break;
            case PropKind.Args:
                if (value is not JsonObject) bag.Error(path, "Expected an object of named arguments.");
                break;
            case PropKind.Style:
                if (value is JsonObject style) ValidateVisual(style, path, bag);
                else bag.Error(path, "Expected an object of visual properties.");
                break;
            case PropKind.Visuals:
                if (value is not JsonObject visuals) { bag.Error(path, "Expected an object keyed by state name."); break; }
                foreach (var (state, visual) in visuals)
                    if (visual is JsonObject v) ValidateVisual(v, $"{path}.{state}", bag);
                    else bag.Error($"{path}.{state}", "Expected an object of visual properties.");
                break;
            case PropKind.Conditions:
                if (value is not JsonArray conditions) { bag.Error(path, "Expected an array of { \"when\": ..., ... }."); break; }
                for (var i = 0; i < conditions.Count; i++)
                {
                    var cpath = $"{path}[{i}]";
                    if (conditions[i] is not JsonObject condition) { bag.Error(cpath, "Expected an object."); continue; }
                    if (condition["when"] is not JsonValue w || !w.TryGetValue<string>(out var when)) bag.Error($"{cpath}.when", "Each condition needs a 'when' expression.");
                    else if (!Expression.TryParse(when, out _, out error)) bag.Error($"{cpath}.when", $"Invalid expression: {error}");
                    var visual = condition["visual"] as JsonObject ?? condition;
                    ValidateVisual(visual, cpath, bag, "when", "visual");
                }
                break;
            case PropKind.Options:
                if (value is not JsonArray) bag.Error(path, "Expected an array of options.");
                break;
        }
    }

    private void ValidateVisual(JsonObject visual, string path, DiagnosticBag bag, params string[] ignore)
    {
        foreach (var (key, value) in visual)
        {
            if (ignore.Contains(key) || LayoutLoader.IsCommentKey(key)) continue;
            var prop = ComponentCatalog.VisualProperties.FirstOrDefault(p => p.Name == key);
            if (prop is null)
            {
                bag.Warning($"{path}.{key}", $"'{key}' is not a visual property.{Suggest(key, ComponentCatalog.VisualProperties.Select(p => p.Name))}");
                continue;
            }
            ValidateValue(prop, value, $"{path}.{key}", bag);
        }
    }

    public static bool IsColor(string text) => ColorRegex().IsMatch(text.Trim());

    [GeneratedRegex(@"^(#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})|@[A-Za-z][\w-]*|[A-Za-z]+|transparent)$")]
    private static partial Regex ColorRegex();

    private static string Suggest(string input, IEnumerable<string> candidates)
    {
        var best = candidates
            .Select(c => (Name: c, Distance: Distance(input.ToLowerInvariant(), c.ToLowerInvariant())))
            .Where(x => x.Distance <= Math.Max(2, input.Length / 3))
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        return best.Name is null ? "" : $" Did you mean '{best.Name}'?";
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
