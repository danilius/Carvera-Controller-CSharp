namespace Carvera.App.Services;

public sealed record LayoutEntry(string Name, string Path, bool IsUser);

/// <summary>
/// Finds layout files: the ones shipped next to the program (layouts\*.json) and the user's own in
/// %APPDATA%\CarveraControllerCS\layouts, which take precedence when names match.
/// </summary>
public sealed class LayoutLibrary(IEnumerable<string> folders)
{
    private readonly string[] _folders = folders.ToArray();

    public static LayoutLibrary Default() => new([
        System.IO.Path.Combine(AppContext.BaseDirectory, "layouts"),
        System.IO.Path.Combine(Settings.Directory, "layouts"),
    ]);

    public IReadOnlyList<string> Folders => _folders;

    public IReadOnlyList<LayoutEntry> List()
    {
        var entries = new Dictionary<string, LayoutEntry>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _folders.Length; i++)
        {
            if (!Directory.Exists(_folders[i])) continue;
            foreach (var file in Directory.EnumerateFiles(_folders[i], "*.json"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                entries[name] = new LayoutEntry(name, file, i > 0);
            }
        }
        return entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Resolves a layout name or a path to a file.</summary>
    public string? Find(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return System.IO.Path.GetFullPath(nameOrPath);
        return List().FirstOrDefault(e => e.Name.Equals(nameOrPath, StringComparison.OrdinalIgnoreCase))?.Path;
    }
}
