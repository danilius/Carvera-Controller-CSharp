using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor;

/// <summary>The things you can do to the selected element, shared by the menu bar, the context menus and the keyboard.</summary>
public sealed class EditorActions(EditorContext ctx)
{
    /// <summary>The window used for dialogs and the clipboard; set by the main window.</summary>
    public Window? Owner { get; set; }

    private JsonObject? Data => ctx.Doc.Data;

    public bool IsElement(string? path) => path is not null && JsonPath.IsElementPath(path) && ctx.Doc.Element(path) is not null;

    public bool CanRemove(string? path) => IsElement(path) && path != "root";

    public bool CanContain(string? path) => IsElement(path) && ElementOps.CanContain(ctx.Doc.Element(path!)!);

    // ------------------------------------------------------------------ structure

    /// <summary>Adds a new element inside the selected container, or after the selected element when that cannot hold it.</summary>
    public void Add(string type, string? at = null)
    {
        var element = NewElements.Create(type);
        var target = IsElement(at ?? ctx.Selection) ? (at ?? ctx.Selection)! : "root";
        ctx.Edit($"Add {type}", data => InsertInOrAfter(data, target, element));
    }

    public void AddRegionUse(string region, string? at = null)
    {
        var target = IsElement(at ?? ctx.Selection) ? (at ?? ctx.Selection)! : "root";
        ctx.Edit($"Use region {region}", data => InsertInOrAfter(data, target, new JsonObject { ["region"] = region }));
    }

    /// <summary>Puts an element inside the container at <paramref name="target"/> if it can take one, else right after <paramref name="target"/>.</summary>
    private static string? InsertInOrAfter(JsonObject data, string target, JsonObject element)
    {
        var container = ElementOps.Element(data, target);
        if (container is null) return null;
        if (ElementOps.CanContain(container) && (ElementOps.RuleFor(container) == ChildRule.Many || !ElementOps.Children(target, container).Any()))
            return ElementOps.Insert(data, target, -1, element);
        return ElementOps.ParentOf(data, target) is { } p ? ElementOps.Insert(data, p.ParentPath, p.Index + 1, element) : ElementOps.Insert(data, target, -1, element);
    }

    public void Delete(string? path = null)
    {
        path ??= ctx.Selection;
        if (!IsElement(path)) return;
        if (path == "root") { ctx.Notify("The layout needs a root element.", warning: true); return; }
        ctx.Edit("Delete", data => ElementOps.Remove(data, path!));
    }

    public void Duplicate(string? path = null)
    {
        path ??= ctx.Selection;
        if (IsElement(path)) ctx.Edit("Duplicate", data => ElementOps.Duplicate(data, path!));
    }

    public void MoveBy(int delta, string? path = null)
    {
        path ??= ctx.Selection;
        if (IsElement(path)) ctx.Edit(delta < 0 ? "Move up" : "Move down", data => ElementOps.MoveBy(data, path!, delta));
    }

    public void WrapIn(string type, string? path = null)
    {
        path ??= ctx.Selection;
        if (IsElement(path)) ctx.Edit($"Wrap in {type}", data => ElementOps.WrapIn(data, path!, type));
    }

    public void Unwrap(string? path = null)
    {
        path ??= ctx.Selection;
        if (IsElement(path)) ctx.Edit("Unwrap", data => ElementOps.Unwrap(data, path!));
    }

    public async Task ExtractRegionAsync(string? path = null)
    {
        path ??= ctx.Selection;
        if (!IsElement(path) || Owner is null) return;
        var element = ctx.Doc.Element(path!)!;
        var suggested = element["id"]?.ToString() ?? ElementOps.TypeOf(element);
        var name = await Dialogs.PromptAsync(Owner, "Make a region", "Name of the new region. It can then be used in several places, and changed in one.", suggested, "Make region");
        if (string.IsNullOrWhiteSpace(name)) return;
        ctx.Edit("Make region", data => ElementOps.ExtractRegion(data, path!, name.Trim()));
    }

    public void InlineRegion(string? path = null)
    {
        path ??= ctx.Selection;
        if (IsElement(path)) ctx.Edit("Inline region", data => ElementOps.InlineRegion(data, path!));
    }

    public async Task AddRegionAsync()
    {
        if (Owner is null) return;
        var name = await Dialogs.PromptAsync(Owner, "New region", "Name of the region:", "region", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        ctx.Edit("Add region", data => ElementOps.AddRegion(data, name.Trim(), NewElements.Create("stack")));
    }

    public async Task RenameRegionAsync(string regionPath)
    {
        if (Owner is null || !regionPath.StartsWith("regions.", StringComparison.Ordinal)) return;
        var old = regionPath["regions.".Length..];
        var name = await Dialogs.PromptAsync(Owner, "Rename region", $"New name for '{old}' (uses are renamed too):", old, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == old) return;
        ctx.Edit("Rename region", data => ElementOps.RenameRegion(data, old, name.Trim()));
    }

    // ------------------------------------------------------------------ clipboard

    public async Task CopyAsync(string? path = null)
    {
        path ??= ctx.Selection;
        if (!IsElement(path) || Owner?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(JsonFormatter.Format(ctx.Doc.Element(path!)!).TrimEnd());
        ctx.Notify("Copied to the clipboard.");
    }

    public async Task CutAsync(string? path = null)
    {
        path ??= ctx.Selection;
        if (!CanRemove(path)) return;
        await CopyAsync(path);
        Delete(path);
    }

    public async Task PasteAsync(string? at = null)
    {
        var target = IsElement(at ?? ctx.Selection) ? (at ?? ctx.Selection)! : "root";
        if (Owner?.Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        JsonObject? pasted = null;
        try { pasted = JsonNode.Parse(text ?? "", documentOptions: LayoutLoader.DocumentOptions) as JsonObject; }
        catch (System.Text.Json.JsonException) { }
        if (pasted is null || pasted["type"] is null && pasted["region"] is null)
        {
            ctx.Notify("The clipboard does not hold a layout element.", warning: true);
            return;
        }
        ctx.Edit("Paste", data =>
        {
            ElementOps.MakeIdsUnique(data, pasted);
            return InsertInOrAfter(data, target, pasted);
        });
    }

    // ------------------------------------------------------------------ dropping

    /// <summary>Where a drag would land: the container, the position in it, and the cell or offset for grids and canvases.</summary>
    public sealed record DropLocation(string ParentPath, int Index, int? Row = null, int? Column = null, double? X = null, double? Y = null);

    public bool ApplyDrop(DragPayload payload, DropLocation where)
    {
        return ctx.Edit(payload.NewElement is not null ? $"Add {payload.Label}" : "Move", data =>
        {
            string? path;
            if (payload.NewElement is not null)
            {
                var element = (JsonObject)payload.NewElement.DeepClone();
                ElementOps.MakeIdsUnique(data, element);
                path = ElementOps.Insert(data, where.ParentPath, where.Index, element);
            }
            else path = ElementOps.Move(data, payload.SourcePath!, where.ParentPath, where.Index);
            if (path is null) return null;
            if (ElementOps.Element(data, path) is { } placed) ApplyPlacement(data, placed, where);
            return path;
        });
    }

    /// <summary>Sets grid cell or canvas position on the dropped element, and clears placement that means nothing in the new parent.</summary>
    private static void ApplyPlacement(JsonObject data, JsonObject element, DropLocation where)
    {
        var parent = ElementOps.Element(data, where.ParentPath);
        var parentType = parent is null ? "" : ElementOps.TypeOf(parent).ToLowerInvariant();
        if (parentType != "grid") foreach (var key in new[] { "row", "column", "rowSpan", "columnSpan" }) element.Remove(key);
        if (parentType != "canvas") { element.Remove("x"); element.Remove("y"); }
        if (parentType == "grid")
        {
            if (where.Row is { } row) element["row"] = row;
            if (where.Column is { } column) element["column"] = column;
        }
        else if (parentType == "canvas")
        {
            if (where.X is { } x) element["x"] = (int)Math.Round(Math.Max(0, x));
            if (where.Y is { } y) element["y"] = (int)Math.Round(Math.Max(0, y));
            if (element["width"] is null && ElementOps.TypeOf(element) is not ("stack" or "panel" or "grid")) element["width"] = 120;
            if (element["height"] is null && ElementOps.TypeOf(element) is not ("stack" or "panel" or "grid")) element["height"] = 40;
        }
    }
}
