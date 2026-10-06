using System.Text.Json.Nodes;
using Carvera.Layout;

namespace Carvera.Editor.Model;

/// <summary>The starting point for each element type when it is added from the palette.</summary>
public static class NewElements
{
    public static JsonObject Create(string type)
    {
        var element = new JsonObject { ["type"] = type };
        switch (type.ToLowerInvariant())
        {
            case "stack": element["orientation"] = "vertical"; element["spacing"] = 8; break;
            case "panel": element["title"] = "Panel"; element["orientation"] = "vertical"; break;
            case "grid": element["columns"] = new JsonArray("*", "*"); element["rows"] = new JsonArray("auto", "auto"); element["spacing"] = 8; break;
            case "split": element["orientation"] = "horizontal"; break;
            case "tabs": break;
            case "text": element["text"] = "Text"; break;
            case "value": element["label"] = "Value"; element["bind"] = "axis.x.work"; element["format"] = "0.000"; break;
            case "axisreadout": element["axis"] = "X"; break;
            case "indicator": element["bind"] = "connection.connected"; element["text"] = "Connected"; break;
            case "progress": break;
            case "button": element["text"] = "Button"; break;
            case "toggle": element["text"] = "Toggle"; element["bind"] = "switch.light"; break;
            case "choice": element["options"] = new JsonArray("A", "B"); break;
            case "slider": element["min"] = 0; element["max"] = 100; break;
            case "override": element["kind"] = "feed"; break;
            case "jogpad": element["axes"] = new JsonArray("X", "Y", "Z"); break;
            case "image": element["source"] = "builtin:home"; element["width"] = 48; element["height"] = 48; break;
            case "spacer": element["height"] = 16; break;
            case "mdi": element["placeholder"] = "G-code"; break;
            case "connection": break;
        }
        // Leaf types are sized by their slot; a fresh container fills its space, like on the web.
        return element;
    }

    /// <summary>Groups for the palette, in the order shown.</summary>
    public static readonly (string Group, string[] Types)[] Groups =
    [
        ("Containers", ["stack", "panel", "grid", "split", "tabs", "scroll", "canvas"]),
        ("Display", ["text", "value", "axisReadout", "machineStatus", "indicator", "progress", "image", "spacer"]),
        ("Controls", ["button", "toggle", "choice", "jogStep", "wcsSelector", "slider", "override", "jogPad", "mdi", "console", "connection", "layoutSelector"]),
        ("Views", ["toolpath", "gcodeList", "gcodeScrubber", "operationList", "toolList", "remoteFiles", "machineConfig", "bedView", "probePanel"]),
    ];

    /// <summary>Palette types the catalog does not list under a group, so nothing is left out when a type is added to the catalog.</summary>
    public static IEnumerable<string> Ungrouped =>
        ComponentCatalog.All.Select(c => c.Type).Except(Groups.SelectMany(g => g.Types), StringComparer.OrdinalIgnoreCase);
}
