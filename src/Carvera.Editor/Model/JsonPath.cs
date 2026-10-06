using System.Text;
using System.Text.Json.Nodes;

namespace Carvera.Editor.Model;

/// <summary>
/// Paths into the layout file as the layout loader reports them: <c>root.children[2].child.width</c>,
/// <c>regions.name.children[0]</c>, <c>styles.name</c>, <c>shortcuts[1].key</c>. A segment is a property name (string) or an array index (int).
/// </summary>
public static class JsonPath
{
    public static List<object> Parse(string path)
    {
        var segments = new List<object>();
        var name = new StringBuilder();
        void Flush()
        {
            if (name.Length > 0) segments.Add(name.ToString());
            name.Clear();
        }
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '.') Flush();
            else if (c == '[')
            {
                Flush();
                var end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), out var index)) return segments;
                segments.Add(index);
                i = end;
            }
            else name.Append(c);
        }
        Flush();
        return segments;
    }

    public static string Format(IEnumerable<object> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment is int index) sb.Append('[').Append(index).Append(']');
            else
            {
                if (sb.Length > 0) sb.Append('.');
                sb.Append((string)segment);
            }
        }
        return sb.ToString();
    }

    public static JsonNode? Resolve(JsonNode? root, string path)
    {
        var node = root;
        foreach (var segment in Parse(path))
        {
            node = segment switch
            {
                string name when node is JsonObject o => o.TryGetPropertyValue(name, out var v) ? v : null,
                int index when node is JsonArray a && index >= 0 && index < a.Count => a[index],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public static JsonObject? ResolveObject(JsonNode? root, string path) => Resolve(root, path) as JsonObject;

    /// <summary>The path with its last segment removed, or null for a top-level path.</summary>
    public static string? Parent(string path)
    {
        var segments = Parse(path);
        return segments.Count <= 1 ? null : Format(segments.Take(segments.Count - 1));
    }

    public static string Append(string path, string name) => path.Length == 0 ? name : path + "." + name;

    public static string Index(string path, int index) => path + "[" + index + "]";

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or lies inside it.</summary>
    public static bool IsWithin(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.Ordinal) ||
        path.StartsWith(ancestor, StringComparison.Ordinal) && path.Length > ancestor.Length && path[ancestor.Length] is '.' or '[';

    /// <summary>The longest prefix of the path that names an element (root, regions.x, ...children[i], ...child), or null.</summary>
    public static string? ElementPrefix(string path)
    {
        var segments = Parse(path);
        for (var length = segments.Count; length >= 1; length--)
        {
            var prefix = segments.Take(length).ToList();
            if (IsElementPath(prefix)) return Format(prefix);
        }
        return null;
    }

    public static bool IsElementPath(string path) => IsElementPath(Parse(path));

    private static bool IsElementPath(List<object> s)
    {
        if (s.Count == 1) return s[0] is "root";
        if (s.Count == 2 && s[0] is "regions" && s[1] is string) return true;
        if (s.Count < 2) return false;
        // ...children[i]  or  ...child
        if (s[^1] is int && s[^2] is "children") return IsElementPath(s.Take(s.Count - 2).ToList());
        if (s[^1] is "child") return IsElementPath(s.Take(s.Count - 1).ToList());
        return false;
    }
}
