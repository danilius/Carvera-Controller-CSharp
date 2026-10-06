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

    /// <summary>How the 3D view draws the toolpath: Auto (GPU for large programs), GPU, or CPU.</summary>
    public string ViewerRenderer { get; set; } = "Auto";
    /// <summary>Colours of the operations in the G-code views: "Layout" (whatever the layout's theme says) or a name from <c>ColorSchemes</c>.</summary>
    public string GcodeColorScheme { get; set; } = "Layout";
    /// <summary>The folders last used to open or save local files, most recent first (at most <see cref="RecentFolderCount"/>).</summary>
    public List<string> RecentFolders { get; set; } = [];
    public const int RecentFolderCount = 5;

    /// <summary>Moves <paramref name="folder"/> to the front of <see cref="RecentFolders"/>.</summary>
    public void RememberFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > RecentFolderCount) RecentFolders.RemoveRange(RecentFolderCount, RecentFolders.Count - RecentFolderCount);
        Save();
    }

    /// <summary>The most recent folder that still exists, or null.</summary>
    public string? LastFolder => RecentFolders.FirstOrDefault(System.IO.Directory.Exists);

    // File upload to the machine.
    public string UploadDirectory { get; set; } = "/sd/gcodes";
    /// <summary>Auto (compress when the machine accepts .lz files), On or Off.</summary>
    public string UploadCompression { get; set; } = "Auto";

    // CYD pendant. The host and port default to what the pendant firmware advertises.
    public bool CydEnabled { get; set; }
    public string CydHost { get; set; } = "cyd-pendant.local";
    public int CydPort { get; set; } = 9876;

    // What a pendant may do; the same preferences as the Python controller.
    public bool PendantJoggingDefault { get; set; } = true;
    public bool AllowJoggingWhileRunning { get; set; }
    public bool AllowJoggingWhileSpindleOn { get; set; }

    // Gamepad. The button mapping itself lives in gamepad-bindings.json next to settings.json.
    public bool GamepadEnabled { get; set; }
    public string GamepadPreset { get; set; } = "Xbox 360 / Xbox One";
    public double GamepadDeadzone { get; set; } = 0.15;
    public double GamepadMaxJogSpeed { get; set; } = 3000;
    public bool GamepadInvertX { get; set; }
    public bool GamepadInvertY { get; set; }
    public bool GamepadInvertZ { get; set; }
    public bool GamepadInvertA { get; set; }

    /// <summary>The values typed into the probing panel, per probing family: parameter code to text.</summary>
    public Dictionary<string, Dictionary<string, string>> ProbeSettings { get; set; } = [];

    // Jogging from the on-screen pad and the keys.
    /// <summary>"step" or "continuous".</summary>
    public string JogButtonMode { get; set; } = "step";
    public bool JogKeyboard { get; set; } = true;
    /// <summary>Y jogs the other way round on the jog pad and keys (the Carvera's Y axis runs opposite to what the pad's arrows suggest).</summary>
    public bool JogInvertY { get; set; } = true;

    /// <summary>The ring-gauge drift correction: applied after XY-zeroing probes while enabled.</summary>
    public bool RingGaugeEnabled { get; set; }
    public double RingGaugeX { get; set; }
    public double RingGaugeY { get; set; }

    /// <summary>Read the machine's config.txt once per connection (for the bed picture and anchor positions), when the machine is idle.</summary>
    public bool AutoReadConfig { get; set; } = true;

    /// <summary>What the job setup page remembers: the origin choice, the steps to run and the auto-level grid.</summary>
    public Carvera.Core.Job.JobSettings? JobSetup { get; set; }
    /// <summary>Draw the machine bed picture under the toolpath and on the job setup page.</summary>
    public bool ShowBedImage { get; set; } = true;

    /// <summary>Where the Z probe is: "work" or "path" origin, and the X and Y offset from it.</summary>
    public string ZProbeOrigin { get; set; } = "work";
    public double ZProbeX { get; set; }
    public double ZProbeY { get; set; }

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
