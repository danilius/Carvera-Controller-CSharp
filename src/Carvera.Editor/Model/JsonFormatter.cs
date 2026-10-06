using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Carvera.Editor.Model;

/// <summary>
/// Writes a layout as the hand-written layouts look: two-space indentation, and small objects and arrays on one line
/// ({ "type": "button", "text": "Hold" }) unless they hold child elements. Keeps the diff small when the editor saves a file
/// that was written by hand.
/// </summary>
public static class JsonFormatter
{
    private const int InlineLimit = 110;

    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(JsonNode node)
    {
        var sb = new StringBuilder();
        Write(sb, node, 0, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int indent, int column)
    {
        switch (node)
        {
            case JsonObject o when o.Count == 0:
                sb.Append("{}");
                break;
            case JsonArray a when a.Count == 0:
                sb.Append("[]");
                break;
            case JsonObject o:
            {
                var inline = TryInline(o, column);
                if (inline is not null) { sb.Append(inline); break; }
                sb.Append("{\n");
                var i = 0;
                foreach (var (key, value) in o)
                {
                    sb.Append(' ', indent + 2);
                    var head = Quote(key) + ": ";
                    sb.Append(head);
                    Write(sb, value, indent + 2, indent + 2 + head.Length);
                    if (++i < o.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', indent).Append('}');
                break;
            }
            case JsonArray a:
            {
                var inline = TryInline(a, column);
                if (inline is not null) { sb.Append(inline); break; }
                sb.Append("[\n");
                for (var i = 0; i < a.Count; i++)
                {
                    sb.Append(' ', indent + 2);
                    Write(sb, a[i], indent + 2, indent + 2);
                    if (i < a.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', indent).Append(']');
                break;
            }
            case null:
                sb.Append("null");
                break;
            default:
                sb.Append(node.ToJsonString(StringOptions));
                break;
        }
    }

    /// <summary>The one-line form, or null when the value is too long or holds child elements.</summary>
    private static string? TryInline(JsonNode node, int column)
    {
        if (Depth(node) > 2 || ContainsKey(node, "children") || ContainsKey(node, "child")) return null;
        var text = Compact(node);
        return column + text.Length <= InlineLimit ? text : null;
    }

    private static string Compact(JsonNode? node) => node switch
    {
        JsonObject o when o.Count == 0 => "{}",
        JsonArray a when a.Count == 0 => "[]",
        JsonObject o => "{ " + string.Join(", ", o.Select(p => Quote(p.Key) + ": " + Compact(p.Value))) + " }",
        JsonArray a => "[" + string.Join(", ", a.Select(Compact)) + "]",
        null => "null",
        _ => node.ToJsonString(StringOptions),
    };

    private static int Depth(JsonNode? node) => node switch
    {
        JsonObject o => 1 + (o.Count == 0 ? 0 : o.Max(p => Depth(p.Value))),
        JsonArray a => 1 + (a.Count == 0 ? 0 : a.Max(Depth)),
        _ => 0,
    };

    private static bool ContainsKey(JsonNode node, string key) => node is JsonObject o && o.ContainsKey(key);

    private static string Quote(string text) => JsonSerializer.Serialize(text, StringOptions);
}
