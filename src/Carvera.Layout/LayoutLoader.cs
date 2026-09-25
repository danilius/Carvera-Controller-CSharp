using System.Text.Json;
using System.Text.Json.Nodes;

namespace Carvera.Layout;

public sealed record LayoutLoadResult(LayoutDocument? Document, IReadOnlyList<LayoutDiagnostic> Diagnostics)
{
    public bool Success => Document is not null && Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
    public IEnumerable<LayoutDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Reads layout JSON (comments and trailing commas allowed), expands region references and runs the
/// <see cref="LayoutValidator"/>.
/// </summary>
public static class LayoutLoader
{
    public static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly HashSet<string> StructuralKeys = new(StringComparer.Ordinal) { "type", "children", "child", "region" };

    public static LayoutLoadResult LoadFile(string path, LayoutValidator? validator = null)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, [new(DiagnosticSeverity.Error, Path.GetFileName(path), $"Cannot read file: {ex.Message}")]);
        }
        return Load(text, Path.GetFileNameWithoutExtension(path), Path.GetDirectoryName(Path.GetFullPath(path)), path, validator);
    }

    public static LayoutLoadResult Load(string json, string fallbackName = "layout", string? baseDirectory = null, string? sourcePath = null, LayoutValidator? validator = null)
    {
        var bag = new DiagnosticBag();
        JsonObject? rootObject;
        try
        {
            rootObject = JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject;
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $"line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}" : "document";
            bag.Error(where, $"Invalid JSON: {FirstSentence(ex.Message)}");
            return new(null, bag.Items);
        }
        if (rootObject is null)
        {
            bag.Error("$", "A layout file must contain a JSON object.");
            return new(null, bag.Items);
        }

        var regions = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (rootObject["regions"] is JsonObject regionObject)
            foreach (var (name, value) in regionObject)
                if (value is JsonObject r) regions[name] = r;
                else bag.Error($"regions.{name}", "A region must be a layout element object.");
        else if (rootObject["regions"] is not null) bag.Error("regions", "'regions' must be an object of named elements.");

        if (rootObject["root"] is not JsonObject rootNodeJson)
        {
            bag.Error("root", "The layout needs a 'root' element.");
            return new(null, bag.Items);
        }

        var root = Expand(rootNodeJson, "root", regions, [], bag);
        if (root is null) return new(null, bag.Items);

        var styles = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (rootObject["styles"] is JsonObject styleObject)
            foreach (var (name, value) in styleObject)
                if (value is JsonObject s) styles[name] = s;
                else bag.Error($"styles.{name}", "A style must be an object of visual properties.");

        var theme = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rootObject["theme"] is JsonObject themeObject)
            foreach (var (name, value) in themeObject)
                theme[name] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";

        var shortcuts = new List<LayoutShortcut>();
        if (rootObject["shortcuts"] is JsonArray shortcutArray)
            for (var i = 0; i < shortcutArray.Count; i++)
            {
                var path = $"shortcuts[{i}]";
                if (shortcutArray[i] is JsonObject o && o["key"] is JsonValue k && k.TryGetValue<string>(out var key) &&
                    o["command"] is JsonValue c && c.TryGetValue<string>(out var command))
                    shortcuts.Add(new LayoutShortcut(key, command, o["args"] as JsonObject, path));
                else bag.Error(path, "A shortcut needs 'key' and 'command' strings.");
            }

        LayoutWindow window = new(null, null, null, null, null);
        if (rootObject["window"] is JsonObject w)
        {
            double? N(string name) => w[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
            window = new LayoutWindow(N("width"), N("height"), N("minWidth"), N("minHeight"), w["title"]?.GetValue<string>());
        }

        var document = new LayoutDocument
        {
            Name = rootObject["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : fallbackName,
            Description = rootObject["description"] is JsonValue dv && dv.TryGetValue<string>(out var d) ? d : "",
            SourcePath = sourcePath,
            BaseDirectory = baseDirectory ?? AppContext.BaseDirectory,
            Window = window,
            Theme = theme,
            Styles = styles,
            Shortcuts = shortcuts,
            Root = root,
        };

        foreach (var key in rootObject.Select(p => p.Key))
            if (key is not ("$schema" or "name" or "description" or "theme" or "styles" or "regions" or "root" or "shortcuts" or "window") && !IsCommentKey(key))
                bag.Warning(key, $"Unknown top-level property '{key}' is ignored.");

        (validator ?? new LayoutValidator()).Validate(document, bag);
        return new(document, bag.Items);
    }

    internal static bool IsCommentKey(string key) => key.StartsWith("//", StringComparison.Ordinal) || key.StartsWith('_') || key == "$comment";

    private static LayoutNode? Expand(JsonObject json, string path, Dictionary<string, JsonObject> regions, List<string> regionStack, DiagnosticBag bag)
    {
        string? regionName = null;
        if (json["region"] is JsonNode regionRef)
        {
            if (regionRef is not JsonValue rv || !rv.TryGetValue<string>(out var name))
            {
                bag.Error($"{path}.region", "'region' must be the name of a region.");
                return null;
            }
            if (!regions.TryGetValue(name, out var definition))
            {
                bag.Error($"{path}.region", $"There is no region named '{name}'. Known regions: {string.Join(", ", regions.Keys.DefaultIfEmpty("(none)"))}.");
                return null;
            }
            if (regionStack.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                bag.Error($"{path}.region", $"Region '{name}' refers to itself ({string.Join(" → ", regionStack.Append(name))}).");
                return null;
            }
            // Properties written next to the reference override the region's own (e.g. a different width).
            var merged = (JsonObject)definition.DeepClone();
            foreach (var (key, value) in json)
                if (key != "region") merged[key] = value?.DeepClone();
            regionStack.Add(name);
            var expanded = Expand(merged, $"regions.{name}", regions, regionStack, bag);
            regionStack.RemoveAt(regionStack.Count - 1);
            if (expanded is null) return null;
            return new LayoutNode(expanded.Type, expanded.Properties, path, expanded.Children, name);
        }

        if (json["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type) || string.IsNullOrWhiteSpace(type))
        {
            bag.Error(path, "Each element needs a 'type' (or a 'region' reference).");
            return null;
        }

        var properties = new JsonObject();
        foreach (var (key, value) in json)
            if (!StructuralKeys.Contains(key)) properties[key] = value?.DeepClone();

        var children = new List<LayoutNode>();
        void AddChild(JsonNode? node, string childPath)
        {
            if (node is JsonObject o)
            {
                var child = Expand(o, childPath, regions, regionStack, bag);
                if (child is not null) children.Add(child);
            }
            else bag.Error(childPath, "Children must be layout element objects.");
        }
        if (json["children"] is JsonArray array)
            for (var i = 0; i < array.Count; i++) AddChild(array[i], $"{path}.children[{i}]");
        else if (json["children"] is not null) bag.Error($"{path}.children", "'children' must be an array.");
        if (json["child"] is JsonNode single)
        {
            if (json["children"] is not null) bag.Error($"{path}.child", "Use either 'child' or 'children', not both.");
            else AddChild(single, $"{path}.child");
        }

        return new LayoutNode(type.Trim(), properties, path, children, regionName);
    }

    private static string FirstSentence(string message)
    {
        var index = message.IndexOf(". ", StringComparison.Ordinal);
        return index > 0 ? message[..(index + 1)] : message;
    }
}
