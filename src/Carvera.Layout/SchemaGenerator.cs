using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Carvera.Core.Commands;

namespace Carvera.Layout;

/// <summary>Generates the layout JSON schema and the Markdown reference from the catalogs.</summary>
public static class SchemaGenerator
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Schema(CommandRegistry commands)
    {
        JsonObject Prop(PropSpec p)
        {
            var o = p.Kind switch
            {
                PropKind.Number => new JsonObject { ["type"] = "number" },
                PropKind.Bool => new JsonObject { ["type"] = "boolean" },
                PropKind.Size => Ref("size"),
                PropKind.SizeList => new JsonObject { ["type"] = "array", ["items"] = Ref("size") },
                PropKind.Edges => Ref("edges"),
                PropKind.Color => Ref("color"),
                PropKind.Expression => new JsonObject { ["type"] = new JsonArray("string", "boolean") },
                PropKind.Command => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(commands.Ids.Order().Select(i => (JsonNode)i).ToArray()) },
                PropKind.Args => new JsonObject { ["type"] = "object" },
                PropKind.Style => Ref("visual"),
                PropKind.Visuals => new JsonObject { ["type"] = "object", ["additionalProperties"] = Ref("visual") },
                PropKind.Conditions => new JsonObject { ["type"] = "array", ["items"] = Ref("condition") },
                PropKind.Enum => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(p.Values!.Select(v => (JsonNode)v).ToArray()) },
                PropKind.StringList => p.Values is null
                    ? new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }) }
                    : new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(p.Values.Select(v => (JsonNode)v).ToArray()) } },
                PropKind.NumberList => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } },
                PropKind.Options => new JsonObject { ["type"] = "array" },
                PropKind.Any => new JsonObject(),
                _ => new JsonObject { ["type"] = "string" },
            };
            o["description"] = p.Description;
            return o;
        }
        static JsonObject Ref(string name) => new() { ["$ref"] = $"#/$defs/{name}" };
        JsonObject Props(IEnumerable<PropSpec> specs)
        {
            var o = new JsonObject();
            foreach (var p in specs) o[p.Name] = Prop(p);
            return o;
        }

        var common = Props(ComponentCatalog.Common);
        common["type"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(ComponentCatalog.All.Select(c => (JsonNode)c.Type).ToArray()),
            ["description"] = "Element type.",
        };
        common["children"] = new JsonObject { ["type"] = "array", ["items"] = Ref("node"), ["description"] = "Child elements (containers only)." };
        common["child"] = new JsonObject { ["$ref"] = "#/$defs/node", ["description"] = "Single child (scroll)." };
        common["region"] = new JsonObject { ["type"] = "string", ["description"] = "Use a named region from 'regions'; other properties here override the region's." };

        var cases = new JsonArray();
        foreach (var spec in ComponentCatalog.All)
            cases.Add(new JsonObject
            {
                ["if"] = new JsonObject { ["properties"] = new JsonObject { ["type"] = new JsonObject { ["const"] = spec.Type } }, ["required"] = new JsonArray("type") },
                ["then"] = new JsonObject { ["description"] = spec.Description, ["properties"] = Props(spec.Properties) },
            });

        var visual = Props(ComponentCatalog.VisualProperties);
        var condition = Props(ComponentCatalog.VisualProperties);
        condition["when"] = new JsonObject { ["type"] = "string", ["description"] = "Expression; the visual applies while it is true." };

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = "https://github.com/danilius/carvera-controller-csharp/schema/layout.schema.json",
            ["title"] = "Carvera Controller C# layout",
            ["description"] = "Generated from the component catalog. Do not edit by hand.",
            ["type"] = "object",
            ["required"] = new JsonArray("root"),
            ["properties"] = new JsonObject
            {
                ["$schema"] = new JsonObject { ["type"] = "string" },
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Display name." },
                ["description"] = new JsonObject { ["type"] = "string" },
                ["window"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = new JsonObject { ["type"] = "string" },
                        ["width"] = new JsonObject { ["type"] = "number" }, ["height"] = new JsonObject { ["type"] = "number" },
                        ["minWidth"] = new JsonObject { ["type"] = "number" }, ["minHeight"] = new JsonObject { ["type"] = "number" },
                    },
                },
                ["theme"] = new JsonObject { ["type"] = "object", ["description"] = "Theme tokens, e.g. accent, background, surface, text, fontFamily, fontSize.", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                ["styles"] = new JsonObject { ["type"] = "object", ["description"] = "Named visual styles used through 'class'.", ["additionalProperties"] = Ref("visual") },
                ["regions"] = new JsonObject { ["type"] = "object", ["description"] = "Named, reusable elements.", ["additionalProperties"] = Ref("node") },
                ["root"] = Ref("node"),
                ["shortcuts"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("key", "command"),
                        ["properties"] = new JsonObject
                        {
                            ["key"] = new JsonObject { ["type"] = "string", ["description"] = "Key gesture, e.g. Ctrl+Up, F5, Escape." },
                            ["command"] = Prop(new PropSpec("command", PropKind.Command, "Command to run.")),
                            ["args"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
            },
            ["$defs"] = new JsonObject
            {
                ["node"] = new JsonObject { ["type"] = "object", ["properties"] = common, ["allOf"] = cases },
                ["size"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number", ["minimum"] = 0 }, new JsonObject { ["type"] = "string", ["pattern"] = "^(auto|fill|\\*|\\d+(\\.\\d+)?(px|%|\\*)?)$" }) },
                ["edges"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number" }, new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["maxItems"] = 4 }) },
                ["color"] = new JsonObject { ["type"] = "string", ["description"] = "#RGB, #RRGGBB, #AARRGGBB, a colour name or @themeToken." },
                ["visual"] = new JsonObject { ["type"] = "object", ["properties"] = visual },
                ["condition"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("when"), ["properties"] = condition },
            },
        };
        return schema.ToJsonString(Indented) + "\n";
    }

    public static string Reference(CommandRegistry commands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Layout reference");
        sb.AppendLine();
        sb.AppendLine("_Generated from the component catalog and command registry (`SchemaGenerator`). Do not edit by hand. See [layouts.md](layouts.md) for the guide._");
        sb.AppendLine();
        sb.AppendLine("## Properties every element accepts");
        sb.AppendLine();
        Table(sb, ComponentCatalog.Common);
        sb.AppendLine("## Visual properties");
        sb.AppendLine();
        sb.AppendLine("Used in `style`, in each state of `visuals`, in `conditions` entries and in named `styles`.");
        sb.AppendLine();
        Table(sb, ComponentCatalog.VisualProperties);
        sb.AppendLine("## Containers");
        sb.AppendLine();
        foreach (var spec in ComponentCatalog.All.Where(s => s.IsContainer)) Component(sb, spec);
        sb.AppendLine("## Components");
        sb.AppendLine();
        foreach (var spec in ComponentCatalog.All.Where(s => !s.IsContainer)) Component(sb, spec);
        sb.AppendLine("## Commands");
        sb.AppendLine();
        foreach (var group in commands.All.GroupBy(c => c.Category))
        {
            sb.AppendLine($"### {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| Command | Arguments | Description |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in group)
            {
                var args = string.Join("<br>", c.Parameters.Select(p => $"`{p.Name}`{(p.Required ? " (required)" : "")}: {Escape(p.Description)}"));
                var needs = c.RequiresConnection ? "" : " Works without a machine connection.";
                sb.AppendLine($"| `{c.Id}` | {args} | {Escape(c.Title)}. {Escape(c.Description)}{needs} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void Component(StringBuilder sb, ComponentSpec spec)
    {
        sb.AppendLine($"### `{spec.Type}`");
        sb.AppendLine();
        sb.AppendLine(spec.Description);
        sb.AppendLine();
        sb.AppendLine($"States for `visuals`: {string.Join(", ", spec.States.Select(s => $"`{s}`"))}.");
        sb.AppendLine();
        if (spec.Properties.Count > 0) Table(sb, spec.Properties);
    }

    private static void Table(StringBuilder sb, IEnumerable<PropSpec> props)
    {
        sb.AppendLine("| Property | Kind | Description |");
        sb.AppendLine("|---|---|---|");
        foreach (var p in props)
        {
            var kind = p.Kind.ToString().ToLowerInvariant();
            if (p.Values is not null) kind += ": " + string.Join(", ", p.Values.Select(v => $"`{v}`"));
            sb.AppendLine($"| `{p.Name}` | {kind} | {Escape(p.Description)} |");
        }
        sb.AppendLine();
    }

    private static string Escape(string text) => text.Replace("|", "\\|");
}
