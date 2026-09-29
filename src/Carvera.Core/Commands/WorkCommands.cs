using System.Globalization;
using System.Text.RegularExpressions;
using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Commands;

/// <summary>Dialog-driven work-coordinate commands (rotation, typed-in origin) and wireless probe pairing.</summary>
public static partial class WorkCommands
{
    [GeneratedRegex(@"([XYZA])\s*([-+]?\d*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AxisValue();

    /// <summary>Reads "X10 Y-2.5" (or "x 10, y -2.5") into axis values. Empty when nothing usable is found.</summary>
    public static Dictionary<char, double> ParseAxisValues(string text) =>
        AxisValue().Matches(text).ToDictionary(m => char.ToUpperInvariant(m.Groups[1].Value[0]), m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));

    /// <summary>What a typed-in work origin is measured from.</summary>
    public enum OriginAnchor { Anchor1, Anchor2, Current, Rotation }

    public static string AnchorName(OriginAnchor anchor) => anchor switch
    {
        OriginAnchor.Anchor1 => "anchor1", OriginAnchor.Anchor2 => "anchor2", OriginAnchor.Current => "current", _ => "rotation",
    };

    /// <summary>Reads "anchor1 10 5", "2 0 0", "current -3,2" or "rotation 0 0": the anchor, then the X and Y offset from it.</summary>
    public static (OriginAnchor Anchor, double X, double Y)? ParseOrigin(string text)
    {
        var tokens = text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 3) return null;
        OriginAnchor? anchor = tokens[0].ToLowerInvariant() switch
        {
            "1" or "anchor1" or "a1" => OriginAnchor.Anchor1,
            "2" or "anchor2" or "a2" => OriginAnchor.Anchor2,
            "c" or "current" or "here" => OriginAnchor.Current,
            "r" or "rotation" or "4" or "4axis" => OriginAnchor.Rotation,
            _ => null,
        };
        if (anchor is null
            || !double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return null;
        return (anchor.Value, x, y);
    }

    /// <summary>
    /// The machine coordinates a work origin is set to (Python's <c>set_work_origin</c>): the offset added to the anchor's
    /// position, or to where the machine is now. Null when a coordinate the anchor needs is missing from <paramref name="coordinates"/>.
    /// </summary>
    public static (double X, double Y)? OriginPosition(OriginAnchor anchor, double x, double y, Func<string, double?> coordinates, double machineX, double machineY)
    {
        double? a1x = coordinates("anchor1_x"), a1y = coordinates("anchor1_y");
        switch (anchor)
        {
            case OriginAnchor.Current:
                return (x + machineX, y + machineY);
            case OriginAnchor.Anchor1 when a1x is not null && a1y is not null:
                return (x + a1x.Value, y + a1y.Value);
            case OriginAnchor.Anchor2 when a1x is not null && a1y is not null && coordinates("anchor2_offset_x") is { } ox && coordinates("anchor2_offset_y") is { } oy:
                return (x + a1x.Value + ox, y + a1y.Value + oy);
            case OriginAnchor.Rotation when a1x is not null && a1y is not null && coordinates("rotation_offset_x") is { } rx && coordinates("rotation_offset_y") is { } ry:
                return (x + a1x.Value + rx, y + a1y.Value + ry);
            default:
                return null;
        }
    }

    public static void Register(CommandRegistry registry, IAppHost host, Config.MachineConfigStore? config = null)
    {
        static bool Idle(CommandContext c, CommandArgs _) => c.State.Get<string>(StatePaths.MachineState) is "Idle";

        void Add(string id, string title, Func<CommandContext, CommandArgs, Task> execute, string description, CommandParameter[]? parameters = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Work offsets", Description = description, Parameters = parameters ?? [],
                CanExecute = Idle, DependsOn = [StatePaths.MachineState], Execute = execute,
            });

        Add("setRotation", "Set WCS rotation", async (c, a) =>
        {
            var angle = a.GetDouble("angle");
            if (angle is null)
            {
                var text = await host.PromptAsync("Rotate work coordinates", "Rotation of the active work coordinate system, in degrees",
                    c.State.Get(StatePaths.WcsRotation, 0.0).ToString("0.###", CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(text)) return;
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) throw new ArgumentException($"'{text.Trim()}' is not a number of degrees.");
                angle = parsed;
            }
            await c.Controller.SendLineAsync(MachineCommands.SetRotation(angle.Value));
        }, "Rotates the active work coordinate system; asks for the angle unless 'angle' is given. clearRotation removes it.",
            [new("angle", "number", "Degrees")]);

        Add("setWorkPositionPrompt", "Set work position...", async (c, a) =>
        {
            var text = a.GetString("text") ?? await host.PromptAsync("Set work position", "What the machine's current position should read, e.g. X10 Y20 Z0 (axes left out do not change)", "");
            if (string.IsNullOrWhiteSpace(text)) return;
            var values = ParseAxisValues(text);
            if (values.Count == 0) throw new ArgumentException("Type axis values such as X10 Y20 Z0.");
            double? V(char axis) => values.TryGetValue(axis, out var v) ? v : null;
            await c.Controller.SendLineAsync(MachineCommands.SetWorkPosition(V('X'), V('Y'), V('Z'), V('A')));
        }, "Makes the current position read the typed-in coordinates in the active work coordinate system (G10 L20).", [new("text", "string", "e.g. X10 Y20")]);

        var lastOrigin = "anchor1 0 0";
        Add("setWorkOrigin", "Set work origin...", async (c, a) =>
        {
            var text = a.GetString("text") ?? await host.PromptAsync("Set work origin",
                "Where the work origin goes: anchor1, anchor2, current (where the machine is now) or rotation (4th axis), then the X and Y offset from it in mm", lastOrigin);
            if (string.IsNullOrWhiteSpace(text)) return;
            var origin = ParseOrigin(text) ?? throw new ArgumentException("Type an anchor and two offsets, for example: anchor1 10 5");
            lastOrigin = $"{AnchorName(origin.Anchor)} {origin.X.ToString("0.####", CultureInfo.InvariantCulture)} {origin.Y.ToString("0.####", CultureInfo.InvariantCulture)}";

            // The anchor positions live in the machine's config.txt; read it once when they are needed.
            if (origin.Anchor != OriginAnchor.Current && config is { Loaded: false }) await config.LoadAsync();
            double? Coordinate(string name) => config is not null && config.Values.TryGetValue("coordinate." + name, out var v)
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            var position = OriginPosition(origin.Anchor, origin.X, origin.Y, Coordinate,
                c.State.Get(StatePaths.AxisMachine("x"), 0.0), c.State.Get(StatePaths.AxisMachine("y"), 0.0))
                ?? throw new InvalidOperationException("The anchor positions could not be read from the machine's settings (coordinate.anchor1_x and friends).");
            await c.Controller.SendLineAsync(MachineCommands.SetWorkOffset(position.X, position.Y));
        }, "Sets the work origin to an offset from an anchor (anchor1, anchor2, rotation centre) or from the current position (G10 L2 P0). Asks for 'text' when not given.",
            [new("text", "string", "For example \"anchor1 10 5\"")]);

        Add("pairProbe", "Pair wireless probe", async (c, a) =>
        {
            if (a.GetBool("confirmed") != true && !await host.ConfirmAsync("Pair the wireless probe now? Switch the probe on and hold it close to the machine first.")) return;
            await c.Controller.SendLineAsync("M471");
        }, "Starts pairing with the wireless workpiece probe (M471), after asking.", [new("confirmed", "bool", "Skip the question")]);
    }
}
