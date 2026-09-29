using System.Text.Json;
using System.Text.Json.Nodes;

namespace Carvera.Core.Pendant.Gamepad;

/// <summary>
/// Maps raw gamepad inputs to action names. The JSON form is the Python controller's, so bindings can be copied across:
/// <c>axes</c> (sticks; only the jog actions make sense), <c>triggers</c> (<c>"axis:+"</c> or <c>"axis:-"</c>, fire once per
/// press), <c>buttons</c> (fire on press) and <c>hat</c> (D-pad directions; jog actions repeat while held, others fire once).
/// Axis, button and hat numbers are SDL joystick indices.
/// </summary>
public sealed class GamepadBindings
{
    public static readonly IReadOnlyList<string> Actions =
    [
        "jog_x", "jog_y", "jog_z", "jog_a",
        "feed_plus", "feed_minus", "spindle_plus", "spindle_minus",
        "start_pause", "stop", "reset", "mode_toggle", "probe_z", "m_home", "w_home", "safe_z", "spindle_on_off",
        "step_size_up", "step_size_down",
        "macro_1", "macro_2", "macro_3", "macro_4", "macro_5", "macro_6", "macro_7", "macro_8", "macro_9", "macro_10",
    ];

    public Dictionary<string, string> Axes { get; init; } = new();
    public Dictionary<string, string> Triggers { get; init; } = new();
    public Dictionary<string, string> Buttons { get; init; } = new();
    public Dictionary<string, string> Hat { get; init; } = new();

    public static readonly IReadOnlyList<(string Name, GamepadBindings Bindings)> Presets =
    [
        ("Xbox 360 / Xbox One", new GamepadBindings
        {
            Axes = { ["0"] = "jog_x", ["1"] = "jog_y", ["4"] = "jog_z", ["3"] = "jog_a" },
            Triggers = { ["2:+"] = "feed_minus", ["5:+"] = "feed_plus" },
            Buttons = { ["4"] = "step_size_down", ["5"] = "step_size_up", ["6"] = "mode_toggle", ["7"] = "spindle_on_off" },
        }),
        ("PlayStation (DS4 / DualSense)", new GamepadBindings
        {
            Axes = { ["0"] = "jog_x", ["1"] = "jog_y", ["5"] = "jog_z", ["2"] = "jog_a" },
            Triggers = { ["3:+"] = "feed_minus", ["4:+"] = "feed_plus" },
            Buttons = { ["4"] = "step_size_down", ["5"] = "step_size_up", ["8"] = "mode_toggle", ["9"] = "spindle_on_off" },
        }),
        ("Nintendo Switch Pro", new GamepadBindings
        {
            Axes = { ["0"] = "jog_x", ["1"] = "jog_y", ["3"] = "jog_z", ["2"] = "jog_a" },
            Buttons = { ["4"] = "step_size_down", ["5"] = "step_size_up", ["6"] = "feed_minus", ["7"] = "feed_plus", ["8"] = "mode_toggle", ["9"] = "spindle_on_off" },
        }),
    ];

    public static GamepadBindings Default => Presets[0].Bindings.Clone();

    public GamepadBindings Clone() => FromJson(ToJson());

    public string? AxisAction(int axis) => Axes.GetValueOrDefault(axis.ToString());

    /// <summary>The action for one direction ("+" or "-") of an axis used as a trigger. A bare axis number counts as "+".</summary>
    public string? TriggerAction(int axis, string direction) =>
        Triggers.GetValueOrDefault($"{axis}:{direction}") ?? (direction == "+" ? Triggers.GetValueOrDefault(axis.ToString()) : null);

    public string? ButtonAction(int button) => Buttons.GetValueOrDefault(button.ToString());

    public string? HatAction(int dx, int dy) =>
        dy > 0 ? Hat.GetValueOrDefault("up") : dy < 0 ? Hat.GetValueOrDefault("down") : dx < 0 ? Hat.GetValueOrDefault("left") : dx > 0 ? Hat.GetValueOrDefault("right") : null;

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["axes"] = ToObject(Axes),
            ["triggers"] = ToObject(Triggers),
            ["buttons"] = ToObject(Buttons),
            ["hat"] = ToObject(Hat),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject ToObject(Dictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map) obj[key] = value;
        return obj;
    }

    /// <summary>Parses bindings JSON; throws <see cref="FormatException"/> with a readable message when it is wrong.</summary>
    public static GamepadBindings FromJson(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new FormatException($"Not valid JSON: {ex.Message}"); }
        if (root is not JsonObject obj) throw new FormatException("The bindings must be a JSON object.");
        var result = new GamepadBindings();
        foreach (var (section, target) in new[] { ("axes", result.Axes), ("triggers", result.Triggers), ("buttons", result.Buttons), ("hat", result.Hat) })
        {
            if (obj[section] is null) continue;
            if (obj[section] is not JsonObject map) throw new FormatException($"'{section}' must be an object.");
            foreach (var (key, value) in map)
            {
                if (value?.GetValueKind() != JsonValueKind.String) throw new FormatException($"'{section}.{key}' must be an action name.");
                target[key] = value.GetValue<string>();
            }
        }
        return result;
    }

    /// <summary>Problems worth telling the user about: unknown actions, bad keys, jog actions in the wrong place.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void CheckActions(string section, Dictionary<string, string> map, Func<string, string?> keyProblem)
        {
            foreach (var (key, action) in map)
            {
                if (keyProblem(key) is { } bad) problems.Add($"{section}.{key}: {bad}");
                if (!Actions.Contains(action)) problems.Add($"{section}.{key}: unknown action '{action}'" + Suggest(action));
            }
        }
        CheckActions("axes", Axes, k => int.TryParse(k, out _) ? null : "the key must be an axis number");
        CheckActions("triggers", Triggers, k => TriggerKeyOk(k) ? null : "the key must be like \"2:+\" or \"2:-\"");
        CheckActions("buttons", Buttons, k => int.TryParse(k, out _) ? null : "the key must be a button number");
        CheckActions("hat", Hat, k => k is "up" or "down" or "left" or "right" ? null : "the key must be up, down, left or right");
        foreach (var (key, action) in Axes.Where(a => !a.Value.StartsWith("jog_", StringComparison.Ordinal)))
            problems.Add($"axes.{key}: '{action}' is not a jog action; put one-shot actions under triggers or buttons");
        return problems;
    }

    private static bool TriggerKeyOk(string key)
    {
        var parts = key.Split(':');
        return int.TryParse(parts[0], out _) && (parts.Length == 1 || (parts.Length == 2 && parts[1] is "+" or "-"));
    }

    private static string Suggest(string action)
    {
        var best = Actions.OrderBy(a => Distance(a, action)).First();
        return Distance(best, action) <= 3 ? $" (did you mean '{best}'?)" : "";
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
