using System.Text.Json;

namespace Carvera.App.Services;

/// <summary>User settings stored in %APPDATA%\CarveraControllerCS\settings.json.</summary>
public sealed class Settings
{
    public string Layout { get; set; } = "desktop";
    public string ConnectionKind { get; set; } = "wifi";
    public string ConnectionAddress { get; set; } = "";
    // Window placement: size in device-independent pixels and position in screen pixels, both as of the last
    // time the window was in its normal (not maximised) state.
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    // Connect to the last-used machine when the program starts.
    public bool AutoConnect { get; set; } = true;
    /// <summary>Reconnect (with the same retries and interval) when the machine drops the connection mid-session.</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Further attempts after the first one fails; 0 tries once.</summary>
    public int AutoConnectRetries { get; set; } = 5;
    public int AutoConnectIntervalSeconds { get; set; } = 5;

    // CYD pendant. The host and port default to what the pendant firmware advertises.
    public bool CydEnabled { get; set; }
    public string CydHost { get; set; } = "cyd-pendant.local";
    public int CydPort { get; set; } = 9876;

    // What a pendant may do; the same preferences as the Python controller.
    public bool PendantJoggingDefault { get; set; } = true;
    public bool AllowJoggingWhileRunning { get; set; }
    public bool AllowJoggingWhileSpindleOn { get; set; }

    /// <summary>Pendant macros 1-10. Unnamed macros are not offered to the pendant.</summary>
    public List<MacroSetting> Macros { get; set; } = [];

    public const int MacroCount = 10;

    /// <summary>Returns the macro list padded to <see cref="MacroCount"/> entries so editors can show every slot.</summary>
    public List<MacroSetting> EnsureMacroSlots()
    {
        while (Macros.Count < MacroCount) Macros.Add(new MacroSetting());
        if (Macros.Count > MacroCount) Macros.RemoveRange(MacroCount, Macros.Count - MacroCount);
        return Macros;
    }

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

public sealed class MacroSetting
{
    public string Name { get; set; } = "";
    public string Gcode { get; set; } = "";
}
