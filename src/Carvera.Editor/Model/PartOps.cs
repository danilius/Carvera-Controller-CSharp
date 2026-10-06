using System.Text.Json.Nodes;

namespace Carvera.Editor.Model;

/// <summary>Edits of the parts of a layout that are not elements: named styles, shortcuts, theme tokens.</summary>
public static class PartOps
{
    public static string? AddStyle(JsonObject data, string name, JsonObject? content = null)
    {
        var styles = data["styles"] as JsonObject ?? (JsonObject)(data["styles"] = new JsonObject())!;
        name = ElementOps.UniqueName(styles, name);
        styles[name] = content ?? new JsonObject();
        return "styles." + name;
    }

    public static string? DuplicateStyle(JsonObject data, string name)
    {
        if (data["styles"] is not JsonObject styles || styles[name] is not JsonObject style) return null;
        return AddStyle(data, name + "-copy", (JsonObject)style.DeepClone());
    }

    /// <summary>Removes a style and takes its name out of the 'class' of every element that used it.</summary>
    public static string? RemoveStyle(JsonObject data, string name)
    {
        if (data["styles"] is not JsonObject styles || !styles.Remove(name)) return null;
        if (styles.Count == 0) data.Remove("styles");
        foreach (var (_, element) in ElementOps.AllElements(data))
            ReplaceClass(element, name, null);
        return "root";
    }

    public static string? RenameStyle(JsonObject data, string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName.Contains('.') || newName.Contains('[')) return null;
        if (data["styles"] is not JsonObject styles || styles[oldName] is null) return null;
        if (!newName.Equals(oldName, StringComparison.Ordinal) && styles.ContainsKey(newName)) return null;
        var entries = styles.Select(p => (p.Key, p.Value)).ToList();
        styles.Clear();
        foreach (var (key, value) in entries) styles[key == oldName ? newName : key] = value;
        foreach (var (_, element) in ElementOps.AllElements(data))
            ReplaceClass(element, oldName, newName);
        return "styles." + newName;
    }

    private static void ReplaceClass(JsonObject element, string oldName, string? newName)
    {
        switch (element["class"])
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
            {
                var names = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(n => n == oldName ? newName : n).Where(n => n is not null).ToArray();
                if (names.Length == 0) element.Remove("class");
                else element["class"] = names.Length == 1 ? names[0] : new JsonArray(names.Select(n => (JsonNode)n!).ToArray());
                break;
            }
            case JsonArray array:
            {
                var names = array.Select(n => n?.ToString()).Select(n => n == oldName ? newName : n).Where(n => n is not null).ToArray();
                if (names.Length == 0) element.Remove("class");
                else element["class"] = new JsonArray(names.Select(n => (JsonNode)n!).ToArray());
                break;
            }
        }
    }

    public static string? AddShortcut(JsonObject data, string key = "Ctrl+K", string command = "")
    {
        var shortcuts = data["shortcuts"] as JsonArray ?? (JsonArray)(data["shortcuts"] = new JsonArray())!;
        shortcuts.Add(new JsonObject { ["key"] = key, ["command"] = command });
        return $"shortcuts[{shortcuts.Count - 1}]";
    }

    public static string? RemoveShortcut(JsonObject data, int index)
    {
        if (data["shortcuts"] is not JsonArray shortcuts || index < 0 || index >= shortcuts.Count) return null;
        shortcuts.RemoveAt(index);
        if (shortcuts.Count == 0) data.Remove("shortcuts");
        return "root";
    }

    public static string? MoveShortcut(JsonObject data, int index, int delta)
    {
        if (data["shortcuts"] is not JsonArray shortcuts || index < 0 || index >= shortcuts.Count) return null;
        var target = index + delta;
        if (target < 0 || target >= shortcuts.Count) return null;
        var item = shortcuts[index];
        shortcuts.RemoveAt(index);
        shortcuts.Insert(target, item);
        return $"shortcuts[{target}]";
    }

    /// <summary>Sets one property of a top-level object or the layout itself (theme, window, name, description).</summary>
    public static void SetTop(JsonObject data, string section, string name, JsonNode? value)
    {
        if (section.Length == 0)
        {
            if (value is null) data.Remove(name); else data[name] = value;
            return;
        }
        var target = data[section] as JsonObject ?? (JsonObject)(data[section] = new JsonObject())!;
        if (value is null) target.Remove(name); else target[name] = value;
        if (target.Count == 0) data.Remove(section);
    }
}
