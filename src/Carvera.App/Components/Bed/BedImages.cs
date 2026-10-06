using Avalonia.Media;
using Carvera.App.Layout;
using Carvera.Core.State;

namespace Carvera.App.Components.Bed;

/// <summary>Finds the picture of the machine bed: the layout's own, or the ones that ship with the app (Carvera and Carvera Air).</summary>
public static class BedImages
{
    public const string C1 = "assets/bed/c1-bolt-holes.png", Air = "assets/bed/air-bolt-holes.png";

    /// <summary>
    /// The picture to use. <paramref name="source"/> is a path from the layout, or null/"auto" for the picture that fits the machine
    /// model. Returns null when the picture is switched off (<c>view.bedImage</c>) or cannot be found.
    /// </summary>
    public static IImage? Find(BuildContext ctx, string? source)
    {
        if (!ctx.State.Get(StatePaths.ViewBedImage, true)) return null;
        var wanted = string.IsNullOrWhiteSpace(source) || source.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? (ctx.State.Get<string>(StatePaths.MachineModelName)?.Equals("CA1", StringComparison.OrdinalIgnoreCase) == true ? Air : C1)
            : source;
        // A layout in the user's folder has no assets next to it: fall back to the ones installed with the app.
        if (!File.Exists(ctx.Images.ResolvePath(wanted)))
        {
            var installed = Path.Combine(AppContext.BaseDirectory, "layouts", wanted.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(installed)) return ctx.Images.Load(installed);
        }
        return ctx.Images.Load(wanted);
    }
}
