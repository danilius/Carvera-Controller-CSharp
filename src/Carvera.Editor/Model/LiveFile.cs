using System.Text;

namespace Carvera.Editor.Model;

/// <summary>
/// Writes the layout to its file so the Controller (which watches the folder) picks the change up. The write is atomic: the text goes to a
/// temporary file that then replaces the layout, so the Controller never reads a half-written file.
/// </summary>
public static class LiveFile
{
    public static void Write(string path, string text)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + ".tmp");
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
