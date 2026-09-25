using System.Text.Json;

namespace Carvera.App.Services;

/// <summary>User settings stored in %APPDATA%\CarveraControllerCS\settings.json.</summary>
public sealed class Settings
{
    public string Layout { get; set; } = "desktop";
    public string ConnectionKind { get; set; } = "wifi";
    public string ConnectionAddress { get; set; } = "";
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    public static string Directory =>
        Environment.GetEnvironmentVariable("CARVERA_CS_SETTINGS_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CarveraControllerCS");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
