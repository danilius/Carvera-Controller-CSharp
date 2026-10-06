using Avalonia.Controls;
using Avalonia.Input;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>The menu offered on an element in the tree and in the preview.</summary>
public static class ElementMenu
{
    public static ContextMenu Build(EditorContext ctx, string path)
    {
        var a = ctx.Actions;
        var menu = new ContextMenu();
        var element = ctx.Doc.Element(path);
        if (element is null) return menu;

        var isReference = ElementOps.IsRegionReference(element);
        var isTop = ElementOps.IsTopLevel(path);
        var canContain = ElementOps.CanContain(element);

        if (canContain)
        {
            var add = new MenuItem { Header = "Add inside" };
            foreach (var (group, types) in NewElements.Groups)
            {
                var sub = new MenuItem { Header = group };
                foreach (var type in types) sub.Items.Add(Item(type, () => a.Add(type, path)));
                add.Items.Add(sub);
            }
            if (ctx.Doc.RegionNames.Any())
            {
                var regions = new MenuItem { Header = "Region" };
                foreach (var name in ctx.Doc.RegionNames) regions.Items.Add(Item(name, () => a.AddRegionUse(name, path)));
                add.Items.Add(regions);
            }
            menu.Items.Add(add);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Item("Duplicate", () => a.Duplicate(path), "Ctrl+D", !(path == "root")));
        menu.Items.Add(Item("Delete", () => a.Delete(path), "Del", path != "root" && !(isTop && ElementOps.References(ctx.Doc.Data!, path["regions.".Length..]).Any())));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move up", () => a.MoveBy(-1, path), "Alt+↑", !isTop));
        menu.Items.Add(Item("Move down", () => a.MoveBy(1, path), "Alt+↓", !isTop));
        menu.Items.Add(new Separator());

        var wrap = new MenuItem { Header = "Wrap in" };
        foreach (var type in new[] { "stack", "panel", "scroll", "grid", "split", "tabs" }) wrap.Items.Add(Item(type, () => a.WrapIn(type, path)));
        menu.Items.Add(wrap);
        menu.Items.Add(Item("Unwrap (replace by its contents)", () => a.Unwrap(path), null, ElementOps.Children(path, element).Count > 0 && !isReference));
        if (isReference) menu.Items.Add(Item("Turn into a copy (inline the region)", () => a.InlineRegion(path)));
        else if (!isTop) menu.Items.Add(Item("Make a region…", () => _ = a.ExtractRegionAsync(path)));
        if (isTop && path.StartsWith("regions.", StringComparison.Ordinal)) menu.Items.Add(Item("Rename region…", () => _ = a.RenameRegionAsync(path)));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy", () => _ = a.CopyAsync(path), "Ctrl+C"));
        menu.Items.Add(Item("Cut", () => _ = a.CutAsync(path), "Ctrl+X", path != "root"));
        menu.Items.Add(Item("Paste", () => _ = a.PasteAsync(path), "Ctrl+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show in the Controller", () => ctx.Link?.Reveal(path), null, ctx.Link is { Connected: true }));
        menu.Items.Add(Item("Show in the code", () => ctx.RequestCodeAt?.Invoke(path)));
        return menu;
    }

    private static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture.Replace("Del", "Delete").Replace("↑", "Up").Replace("↓", "Down"));
        item.Click += (_, _) => action();
        return item;
    }
}
