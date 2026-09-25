using System.Text.Json.Nodes;

namespace Carvera.Layout;

/// <summary>One element of a layout tree after region references have been expanded.</summary>
public sealed class LayoutNode
{
    public LayoutNode(string type, JsonObject properties, string path, IReadOnlyList<LayoutNode> children, string? region = null)
    {
        Type = type;
        Properties = properties;
        Path = path;
        Children = children;
        Region = region;
    }

    public string Type { get; }
    /// <summary>All properties except "type", "children" and "child".</summary>
    public JsonObject Properties { get; }
    /// <summary>Where this node came from in the file, for error messages.</summary>
    public string Path { get; }
    public IReadOnlyList<LayoutNode> Children { get; }
    /// <summary>The region this node was expanded from, if any.</summary>
    public string? Region { get; }

    public string? Id => GetString("id");

    public JsonNode? Get(string name) => Properties.TryGetPropertyValue(name, out var v) ? v : null;
    public bool Has(string name) => Properties.ContainsKey(name);

    public string? GetString(string name) => Get(name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : Get(name)?.ToJsonString();

    public double? GetNumber(string name)
    {
        if (Get(name) is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : null;
    }

    public bool? GetBool(string name) => Get(name) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public IReadOnlyList<string> GetStringList(string name) => Get(name) switch
    {
        JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToJsonString() ?? "").ToArray(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
        _ => [],
    };

    public IReadOnlyList<double> GetNumberList(string name) => Get(name) is JsonArray a
        ? a.Select(x => x is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN).Where(double.IsFinite).ToArray()
        : [];

    public SizeSpec Width => SizeSpec.TryParse(Get("width"), out var s, out _) ? s : SizeSpec.Fill;
    public SizeSpec Height => SizeSpec.TryParse(Get("height"), out var s, out _) ? s : SizeSpec.Fill;

    /// <summary>Enumerates this node and all descendants with their ancestors (nearest last).</summary>
    public IEnumerable<(LayoutNode Node, IReadOnlyList<LayoutNode> Ancestors)> Walk()
    {
        var stack = new Stack<(LayoutNode, LayoutNode[])>();
        stack.Push((this, []));
        while (stack.Count > 0)
        {
            var (node, ancestors) = stack.Pop();
            yield return (node, ancestors);
            var next = ancestors.Append(node).ToArray();
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push((node.Children[i], next));
        }
    }

    public override string ToString() => $"{Type} ({Path})";
}

public sealed record LayoutShortcut(string Key, string Command, JsonObject? Args, string Path);

public sealed record LayoutWindow(double? Width, double? Height, double? MinWidth, double? MinHeight, string? Title);

public sealed class LayoutDocument
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>The file this layout was read from, or null for layouts loaded from text.</summary>
    public string? SourcePath { get; init; }
    /// <summary>Folder that relative image paths are resolved against.</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;
    public LayoutWindow Window { get; init; } = new(null, null, null, null, null);
    /// <summary>Theme tokens such as background, surface, accent, fontFamily, fontSize.</summary>
    public IReadOnlyDictionary<string, string> Theme { get; init; } = new Dictionary<string, string>();
    /// <summary>Named styles that components refer to through "class".</summary>
    public IReadOnlyDictionary<string, JsonObject> Styles { get; init; } = new Dictionary<string, JsonObject>();
    public IReadOnlyList<LayoutShortcut> Shortcuts { get; init; } = [];
    public required LayoutNode Root { get; init; }
}
