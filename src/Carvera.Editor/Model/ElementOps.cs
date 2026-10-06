using System.Text.Json.Nodes;
using Carvera.Layout;

namespace Carvera.Editor.Model;

/// <summary>
/// Structural edits on the layout's JSON: insert, remove, move, duplicate, wrap and unwrap elements and turn elements into regions.
/// Elements are addressed by their path (root.children[2]); each operation returns the path of the element it produced (so the
/// editor can keep it selected) or null when it could not be done, with the reason in <see cref="LastError"/>.
/// </summary>
public static class ElementOps
{
    public static string? LastError { get; private set; }

    private static string? Fail(string message)
    {
        LastError = message;
        return null;
    }

    public static JsonObject? Element(JsonObject data, string path) => JsonPath.ResolveObject(data, path);

    public static string TypeOf(JsonObject element) =>
        element["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : element["region"] is not null ? "region" : "?";

    public static bool IsRegionReference(JsonObject element) => element["region"] is JsonValue && element["type"] is null;

    public static bool IsTopLevel(string path) => JsonPath.Parse(path).Count <= 2 && (path == "root" || path.StartsWith("regions.", StringComparison.Ordinal));

    /// <summary>What a container accepts: a single 'child' (scroll), many, or none.</summary>
    public static ChildRule RuleFor(JsonObject element) =>
        !IsRegionReference(element) && ComponentCatalog.TryGet(TypeOf(element), out var spec) ? spec.Children : ChildRule.None;

    /// <summary>The child elements of an element with their paths, from 'children' or 'child'.</summary>
    public static IReadOnlyList<(string Path, JsonObject Element)> Children(string path, JsonObject element)
    {
        var list = new List<(string, JsonObject)>();
        if (element["children"] is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonObject child) list.Add((JsonPath.Index(path + ".children", i), child));
        if (element["child"] is JsonObject single) list.Add((path + ".child", single));
        return list;
    }

    /// <summary>The element that contains the one at <paramref name="path"/>, its path and the index of the element among its children.</summary>
    public static (string ParentPath, JsonObject Parent, int Index)? ParentOf(JsonObject data, string path)
    {
        var segments = JsonPath.Parse(path);
        if (segments.Count < 2) return null;
        string parentPath;
        int index;
        if (segments[^1] is int i && segments[^2] is "children") { parentPath = JsonPath.Format(segments.Take(segments.Count - 2)); index = i; }
        else if (segments[^1] is "child") { parentPath = JsonPath.Format(segments.Take(segments.Count - 1)); index = 0; }
        else return null;
        return Element(data, parentPath) is { } parent ? (parentPath, parent, index) : null;
    }

    public static bool CanContain(JsonObject parent) =>
        !IsRegionReference(parent) && RuleFor(parent) != ChildRule.None;

    /// <summary>Adds an element to a container at an index (or at the end when index is negative). Returns the new element's path.</summary>
    public static string? Insert(JsonObject data, string parentPath, int index, JsonObject element)
    {
        LastError = null;
        if (Element(data, parentPath) is not { } parent) return Fail("There is no such container.");
        if (IsRegionReference(parent)) return Fail("This is a reference to a region; change the region itself to add to it.");
        var rule = RuleFor(parent);
        if (rule == ChildRule.None) return Fail($"A {TypeOf(parent)} cannot contain other elements.");

        if (rule == ChildRule.Single)
        {
            if (parent["children"] is JsonArray existing)
            {
                if (existing.Count >= 1) return Fail($"A {TypeOf(parent)} holds one element; put a stack inside it first.");
                existing.Add(element);
                return parentPath + ".children[0]";
            }
            if (parent["child"] is not null) return Fail($"A {TypeOf(parent)} holds one element; put a stack inside it first.");
            parent["child"] = element;
            return parentPath + ".child";
        }

        // A tab page is named by its 'title'; without one the header would read as the element's type.
        if (TypeOf(parent).Equals("tabs", StringComparison.OrdinalIgnoreCase) && element["title"] is null && !IsRegionReference(element))
            element["title"] = "Page " + (Children(parentPath, parent).Count + 1);

        var array = parent["children"] as JsonArray;
        if (array is null)
        {
            array = [];
            parent["children"] = array;
        }
        if (parent["child"] is JsonObject strayChild)
        {
            parent.Remove("child");
            array.Add(strayChild);
        }
        index = index < 0 || index > array.Count ? array.Count : index;
        array.Insert(index, element);
        return JsonPath.Index(parentPath + ".children", index);
    }

    /// <summary>Removes an element from its container. A region is removed when nothing refers to it.</summary>
    public static string? Remove(JsonObject data, string path)
    {
        LastError = null;
        if (path == "root") return Fail("The layout needs a root element.");
        if (path.StartsWith("regions.", StringComparison.Ordinal) && IsTopLevel(path))
        {
            var name = path["regions.".Length..];
            if (References(data, name).Any()) return Fail($"Region '{name}' is still used; remove its uses first.");
            (data["regions"] as JsonObject)?.Remove(name);
            return "root";
        }
        if (ParentOf(data, path) is not { } p) return Fail("Nothing to remove.");
        if (p.Parent["children"] is JsonArray array && JsonPath.Parse(path)[^1] is int index)
        {
            array.RemoveAt(index);
            return array.Count == 0 ? p.ParentPath : JsonPath.Index(p.ParentPath + ".children", Math.Min(index, array.Count - 1));
        }
        p.Parent.Remove("child");
        return p.ParentPath;
    }

    /// <summary>Moves an element into a container at an index. Returns the element's new path.</summary>
    public static string? Move(JsonObject data, string path, string newParentPath, int index)
    {
        LastError = null;
        if (IsTopLevel(path)) return Fail("The layout's root and regions cannot be moved into other elements.");
        if (JsonPath.IsWithin(newParentPath, path)) return Fail("An element cannot be moved into itself.");
        if (ParentOf(data, path) is not { } from || Element(data, path) is not { } element) return Fail("Nothing to move.");
        if (Element(data, newParentPath) is not { } target) return Fail("There is no such container.");
        if (!CanContain(target)) return Fail(IsRegionReference(target) ? "That is a reference to a region." : $"A {TypeOf(target)} cannot contain other elements.");
        if (RuleFor(target) == ChildRule.Single && Children(newParentPath, target).Any(c => c.Path != path)) return Fail($"A {TypeOf(target)} holds one element.");

        // Removing first shifts later paths, so work out where the target and index end up.
        var sameParent = from.ParentPath == newParentPath;
        var adjustedParent = newParentPath;
        if (!sameParent) adjustedParent = ShiftAfterRemoval(newParentPath, from.ParentPath, from.Parent, JsonPath.Parse(path)[^1] as int?);
        var adjustedIndex = index;
        if (sameParent && JsonPath.Parse(path)[^1] is int old && index > old) adjustedIndex--;

        var snapshot = element;
        if (Remove(data, path) is null) return null;
        // Remove() may collapse the parent's selection path; ignore its return value and use the recomputed target.
        return Insert(data, adjustedParent, adjustedIndex, snapshot) ?? RestoreOnFailure(data, from, path, snapshot);
    }

    private static string? RestoreOnFailure(JsonObject data, (string ParentPath, JsonObject Parent, int Index) from, string path, JsonObject element)
    {
        var error = LastError;
        Insert(data, from.ParentPath, from.Index, element);
        LastError = error;
        return null;
    }

    /// <summary>The path of <paramref name="target"/> after the child at <paramref name="removedIndex"/> of <paramref name="removedFrom"/> is removed.</summary>
    private static string ShiftAfterRemoval(string target, string removedFrom, JsonObject removedFromElement, int? removedIndex)
    {
        if (removedIndex is null) return target;
        var prefix = removedFrom + ".children";
        if (!target.StartsWith(prefix + "[", StringComparison.Ordinal)) return target;
        var segments = JsonPath.Parse(target);
        var depth = JsonPath.Parse(prefix).Count;
        if (segments.Count > depth && segments[depth] is int i && i > removedIndex)
        {
            segments[depth] = i - 1;
            return JsonPath.Format(segments);
        }
        return target;
    }

    public static string? MoveBy(JsonObject data, string path, int delta)
    {
        LastError = null;
        if (ParentOf(data, path) is not { } p || p.Parent["children"] is not JsonArray array) return Fail("This element has no siblings.");
        var target = p.Index + delta;
        if (target < 0 || target >= array.Count) return Fail("It is already at the end.");
        var element = array[p.Index];
        array.RemoveAt(p.Index);
        array.Insert(target, element);
        return JsonPath.Index(p.ParentPath + ".children", target);
    }

    public static string? Duplicate(JsonObject data, string path)
    {
        LastError = null;
        if (Element(data, path) is not { } element) return Fail("Nothing to duplicate.");
        var copy = (JsonObject)element.DeepClone();
        MakeIdsUnique(data, copy);
        if (IsTopLevel(path))
        {
            if (!path.StartsWith("regions.", StringComparison.Ordinal)) return Fail("The root cannot be duplicated.");
            var regions = (JsonObject)data["regions"]!;
            var name = UniqueName(regions, path["regions.".Length..]);
            regions[name] = copy;
            return "regions." + name;
        }
        if (ParentOf(data, path) is not { } p) return Fail("Nothing to duplicate.");
        return Insert(data, p.ParentPath, p.Index + 1, copy);
    }

    public static string? WrapIn(JsonObject data, string path, string containerType)
    {
        LastError = null;
        if (Element(data, path) is not { } element) return Fail("Nothing to wrap.");
        var container = NewElements.Create(containerType);
        if (RuleFor(container) == ChildRule.None) return Fail($"A {containerType} cannot contain other elements.");
        if (IsTopLevel(path))
        {
            container["children"] = new JsonArray(element.DeepClone());
            if (path == "root") data["root"] = container;
            else ((JsonObject)data["regions"]!)[path["regions.".Length..]] = container;
            return path;
        }
        if (ParentOf(data, path) is not { } p) return Fail("Nothing to wrap.");
        var clone = element.DeepClone();
        if (RuleFor(container) == ChildRule.Single) container["child"] = clone; else container["children"] = new JsonArray(clone);
        if (p.Parent["children"] is JsonArray array) array[p.Index] = container; else p.Parent["child"] = container;
        return path;
    }

    /// <summary>Replaces a container with its children.</summary>
    public static string? Unwrap(JsonObject data, string path)
    {
        LastError = null;
        if (Element(data, path) is not { } element) return Fail("Nothing to unwrap.");
        var children = Children(path, element).Select(c => c.Element.DeepClone()).ToList();
        if (children.Count == 0) return Fail("It has nothing inside.");
        if (IsTopLevel(path))
        {
            if (children.Count != 1) return Fail("The top of the layout needs a single element; unwrap it only when it holds one.");
            if (path == "root") data["root"] = children[0];
            else ((JsonObject)data["regions"]!)[path["regions.".Length..]] = children[0];
            return path;
        }
        if (ParentOf(data, path) is not { } p) return Fail("Nothing to unwrap.");
        if (p.Parent["children"] is JsonArray array)
        {
            array.RemoveAt(p.Index);
            for (var i = 0; i < children.Count; i++) array.Insert(p.Index + i, children[i]);
            return JsonPath.Index(p.ParentPath + ".children", p.Index);
        }
        if (children.Count != 1) return Fail("A single-child container can hold only one element.");
        p.Parent["child"] = children[0];
        return path;
    }

    /// <summary>Turns an element into a named region and leaves a reference to it.</summary>
    public static string? ExtractRegion(JsonObject data, string path, string name)
    {
        LastError = null;
        if (Element(data, path) is not { } element || IsRegionReference(element)) return Fail("This is already a region reference.");
        if (IsTopLevel(path)) return Fail("Extract an element inside the layout.");
        var regions = data["regions"] as JsonObject ?? (JsonObject)(data["regions"] = new JsonObject())!;
        name = UniqueName(regions, name);
        var sizeProps = new[] { "width", "height", "row", "column", "rowSpan", "columnSpan", "x", "y", "title" };
        var reference = new JsonObject { ["region"] = name };
        var definition = (JsonObject)element.DeepClone();
        // Placement stays with the use, so the region can be used elsewhere with a different width or grid cell.
        foreach (var key in sizeProps)
            if (definition[key] is { } value) { reference[key] = value.DeepClone(); definition.Remove(key); }
        regions[name] = definition;
        if (ParentOf(data, path) is not { } p) return Fail("Nothing to extract.");
        if (p.Parent["children"] is JsonArray array) array[p.Index] = reference; else p.Parent["child"] = reference;
        return path;
    }

    /// <summary>Replaces a region reference with a copy of the region's definition.</summary>
    public static string? InlineRegion(JsonObject data, string path)
    {
        LastError = null;
        if (Element(data, path) is not { } reference || !IsRegionReference(reference)) return Fail("This is not a region reference.");
        var name = reference["region"]!.GetValue<string>();
        if (data["regions"] is not JsonObject regions || regions[name] is not JsonObject definition) return Fail($"There is no region named '{name}'.");
        var copy = (JsonObject)definition.DeepClone();
        foreach (var (key, value) in reference)
            if (key != "region") copy[key] = value?.DeepClone();
        MakeIdsUnique(data, copy);
        if (path == "root") { data["root"] = copy; return path; }
        if (ParentOf(data, path) is not { } p) return Fail("Nothing to inline.");
        if (p.Parent["children"] is JsonArray array) array[p.Index] = copy; else p.Parent["child"] = copy;
        return path;
    }

    public static string? AddRegion(JsonObject data, string name, JsonObject definition)
    {
        var regions = data["regions"] as JsonObject ?? (JsonObject)(data["regions"] = new JsonObject())!;
        name = UniqueName(regions, name);
        regions[name] = definition;
        return "regions." + name;
    }

    public static string? RenameRegion(JsonObject data, string oldName, string newName)
    {
        LastError = null;
        newName = newName.Trim();
        if (newName.Length == 0 || newName.Contains('.') || newName.Contains('[')) return Fail("A region name is letters, digits, - or _.");
        if (data["regions"] is not JsonObject regions || regions[oldName] is null) return Fail("There is no such region.");
        if (!newName.Equals(oldName, StringComparison.Ordinal) && regions.ContainsKey(newName)) return Fail($"There is already a region named '{newName}'.");
        var definition = regions[oldName];
        // Keep the order of the regions.
        var entries = regions.Select(p => (p.Key, p.Value)).ToList();
        regions.Clear();
        foreach (var (key, value) in entries)
        {
            var node = value;
            if (key == oldName) { regions[newName] = definition; }
            else regions[key] = node;
        }
        foreach (var (referencePath, _) in References(data, oldName).ToList())
            if (Element(data, referencePath) is { } reference) reference["region"] = newName;
        return "regions." + newName;
    }

    /// <summary>Every element that refers to a region.</summary>
    public static IEnumerable<(string Path, JsonObject Element)> References(JsonObject data, string region)
    {
        foreach (var (path, element) in AllElements(data))
            if (IsRegionReference(element) && string.Equals(element["region"]!.GetValue<string>(), region, StringComparison.OrdinalIgnoreCase))
                yield return (path, element);
    }

    /// <summary>Every element in the file: the root, the regions and all their descendants.</summary>
    public static IEnumerable<(string Path, JsonObject Element)> AllElements(JsonObject data)
    {
        if (data["root"] is JsonObject root)
            foreach (var pair in Descendants("root", root)) yield return pair;
        if (data["regions"] is JsonObject regions)
            foreach (var (name, value) in regions)
                if (value is JsonObject region)
                    foreach (var pair in Descendants("regions." + name, region)) yield return pair;
    }

    public static IEnumerable<(string Path, JsonObject Element)> Descendants(string path, JsonObject element)
    {
        yield return (path, element);
        foreach (var (childPath, child) in Children(path, element))
            foreach (var pair in Descendants(childPath, child)) yield return pair;
    }

    /// <summary>Renames ids in a copy that would clash with ids already in the file.</summary>
    public static void MakeIdsUnique(JsonObject data, JsonObject copy)
    {
        var used = AllElements(data).Select(e => e.Element["id"]?.ToString()).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, element) in Descendants("copy", copy))
        {
            if (element["id"] is not JsonValue v || !v.TryGetValue<string>(out var id) || !used.Contains(id)) continue;
            var n = 2;
            while (used.Contains($"{id}-{n}")) n++;
            element["id"] = $"{id}-{n}";
            used.Add($"{id}-{n}");
        }
    }

    public static string UniqueName(JsonObject collection, string wanted)
    {
        if (!collection.ContainsKey(wanted)) return wanted;
        var n = 2;
        while (collection.ContainsKey($"{wanted}-{n}")) n++;
        return $"{wanted}-{n}";
    }
}
