using System.Text.Json;
using Carvera.App.Services;

namespace Carvera.Editor.Model;

/// <summary>Where layouts live, and the editor's own remembered choices (window, last layout, live switch).</summary>
public sealed class EditorSettings
{
    /// <summary>The Controller's settings folder, captured before the preview redirects it (the preview must never write the Controller's settings).</summary>
    public static string ControllerSettingsDirectory { get; private set; } = Settings.Directory;

    public static string UserLayoutsDirectory => Path.Combine(ControllerSettingsDirectory, "layouts");
    public static string ShippedLayoutsDirectory => Path.Combine(AppContext.BaseDirectory, "layouts");

    public string? LastLayout { get; set; }
    public string? LastFile { get; set; }
    public bool Live { get; set; } = true;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public string PreviewSize { get; set; } = "1920 × 1080 (Full HD)";
    public string PreviewZoom { get; set; } = "Fit";
    public bool ShowStructure { get; set; }
    public bool FollowInController { get; set; } = true;
    public List<string> CollapsedSections { get; set; } = [];

    private static string FilePath => Path.Combine(ControllerSettingsDirectory, "layout-editor.json");

    /// <summary>Call first thing: remembers the real settings folder and points the preview at a scratch one.</summary>
    public static void Initialise()
    {
        ControllerSettingsDirectory = Settings.Directory;
        Environment.SetEnvironmentVariable("CARVERA_CS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "carvera-layout-editor-preview"));
    }

    public static EditorSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ControllerSettingsDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The layouts the Controller offers, from its own library.</summary>
    public static IReadOnlyList<LayoutEntry> Library() =>
        new LayoutLibrary([ShippedLayoutsDirectory, UserLayoutsDirectory]).List();
}
