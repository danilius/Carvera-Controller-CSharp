using Carvera.Core.Pendant.Gamepad;

namespace Carvera.App.Services;

/// <summary>
/// The gamepad's button mapping, kept in <c>gamepad-bindings.json</c> next to the settings so it can be edited in any text
/// editor. It is written from the chosen preset when it does not exist yet.
/// </summary>
public static class GamepadBindingsStore
{
    public const string CustomPreset = "Custom (edit the bindings file)";

    public static string FilePath => Path.Combine(Settings.Directory, "gamepad-bindings.json");

    public static IEnumerable<string> PresetNames => GamepadBindings.Presets.Select(p => p.Name).Append(CustomPreset);

    /// <summary>Writes a preset over the bindings file. Custom bindings in the file are replaced.</summary>
    public static void WritePreset(string name)
    {
        var preset = GamepadBindings.Presets.FirstOrDefault(p => p.Name == name);
        if (preset.Bindings is null) return;
        Directory.CreateDirectory(Settings.Directory);
        File.WriteAllText(FilePath, preset.Bindings.ToJson());
    }

    /// <summary>
    /// Loads the bindings. A missing file is created from the preset; an unreadable one falls back to the default
    /// bindings, and <paramref name="problems"/> says what was wrong so it can be shown to the user.
    /// </summary>
    public static GamepadBindings Load(Settings settings, out IReadOnlyList<string> problems)
    {
        var list = new List<string>();
        problems = list;
        try
        {
            if (!File.Exists(FilePath)) WritePreset(GamepadBindings.Presets.Any(p => p.Name == settings.GamepadPreset) ? settings.GamepadPreset : GamepadBindings.Presets[0].Name);
            var bindings = GamepadBindings.FromJson(File.ReadAllText(FilePath));
            list.AddRange(bindings.Validate());
            return bindings;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            list.Add($"{Path.GetFileName(FilePath)}: {ex.Message} Using the default bindings instead.");
            return GamepadBindings.Default;
        }
    }
}
