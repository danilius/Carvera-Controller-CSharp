using System.Globalization;
using Carvera.Core.Probing;
using Carvera.Core.State;

namespace Carvera.Core.Commands;

/// <summary>
/// Probing and "prepare the workpiece" commands: the probing addon's operations (M460-M469), the XYZ probe (M495.3),
/// margin / Z-probe / auto-levelling runs over the open program's extents (M495), and go to the path origin (M496.5).
/// Ported from the Python controller. Everything here moves the machine, so it runs only while the machine is idle and asks first.
/// </summary>
public static class ProbeCommands
{
    /// <summary>The Python controller's default work area, used to reject a program whose extents lie far outside the machine.</summary>
    public const double WorkSizeX = 340, WorkSizeY = 240;

    private static readonly string[] IdlePaths = [StatePaths.MachineState, StatePaths.AtcState, StatePaths.JobLines, StatePaths.FileHasBounds];

    private static string G(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>True when the open program has extents that fit the machine's work area.</summary>
    public static bool PathOriginAvailable(StateStore state) =>
        state.Get(StatePaths.FileHasBounds, false)
        && Math.Abs(state.Get(StatePaths.FileXMin, 0.0)) <= WorkSizeX && Math.Abs(state.Get(StatePaths.FileYMin, 0.0)) <= WorkSizeY;

    /// <summary>M496.5: rapid to the program's lower-left corner. Null when there is no program or it lies outside the work area.</summary>
    public static string? GotoPathOrigin(StateStore state) =>
        PathOriginAvailable(state) ? $"M496.5 X{G(state.Get(StatePaths.FileXMin, 0.0))}Y{G(state.Get(StatePaths.FileYMin, 0.0))}\n" : null;

    /// <summary>M495.3: probe X, Y and Z of a workpiece at the current position.</summary>
    public static string XyzProbe(double height, double diameter) => $"M495.3 H{G(height)} D{G(diameter)}\n";

    /// <summary>Options of <see cref="AutoRun"/>, as in the Python controller's autoCommand.</summary>
    public sealed record AutoOptions(
        bool Margin = false, bool ZProbe = false, bool ZProbeAbsolute = false, bool Leveling = false, bool GotoOrigin = false,
        double ZProbeOffsetX = 0, double ZProbeOffsetY = 0, int PointsX = 3, int PointsY = 3, double Height = 5,
        double[]? LevelOffsets = null, int UpcomingTool = 0);

    /// <summary>
    /// The commands that draw the margin, probe Z, auto-level and go to the origin, in the order they must be sent.
    /// Empty when nothing is asked for or the program has no usable extents.
    /// </summary>
    public static IReadOnlyList<string> AutoRun(StateStore state, AutoOptions o)
    {
        if (!(o.Margin || o.ZProbe || o.Leveling || o.GotoOrigin) || !PathOriginAvailable(state)) return [];
        double xmin = state.Get(StatePaths.FileXMin, 0.0), xmax = state.Get(StatePaths.FileXMax, 0.0);
        double ymin = state.Get(StatePaths.FileYMin, 0.0), ymax = state.Get(StatePaths.FileYMax, 0.0);
        var offsets = o.LevelOffsets is { Length: 4 } l ? l : [0, 0, 0, 0]; // x-, x+, y-, y+
        var commands = new List<string>();
        if (o.Margin) commands.Add($"M495 X{G(xmin)}Y{G(ymin)}C{G(xmax)}D{G(ymax)}\n"); // a separate command so the levelling can start offset from it
        var cmd = $"M495 X{G(xmin + offsets[0])}Y{G(ymin + offsets[2])}";
        if (o.ZProbe) cmd = o.ZProbeAbsolute ? $"M495 X{G(xmin)}Y{G(ymin)}O0" : cmd + $"O{G(o.ZProbeOffsetX)}F{G(o.ZProbeOffsetY)}";
        if (o.Leveling)
            cmd += $"A{G(xmax - (xmin + offsets[1] + offsets[0]))}B{G(ymax - (ymin + offsets[3] + offsets[2]))}I{o.PointsX}J{o.PointsY}H{G(o.Height)}";
        if (o.GotoOrigin)
        {
            cmd += "P1";
            if (o.UpcomingTool > 0) cmd += $"T{o.UpcomingTool}"; // lets the firmware change tool and apply the offset before going to the origin
        }
        commands.Add(cmd + "\n");
        return commands;
    }

    /// <summary>The offsets the Z probe needs for <c>M495 O..F..</c>: from the work origin they are corrected by the program's lower-left corner, and both by the auto-level margin.</summary>
    public static (double X, double Y) ZProbeOffsets(StateStore state, ZProbeSetting setting, double[]? levelOffsets = null)
    {
        var offsets = levelOffsets is { Length: 4 } l ? l : [0, 0, 0, 0];
        var x = setting.X - offsets[0];
        var y = setting.Y - offsets[2];
        if (setting.FromWorkOrigin)
        {
            x -= state.Get(StatePaths.FileXMin, 0.0);
            y -= state.Get(StatePaths.FileYMin, 0.0);
        }
        return (x, y);
    }

    public static string ZProbeText(ZProbeSetting setting) =>
        string.Create(CultureInfo.InvariantCulture, $"({setting.X:0.####}, {setting.Y:0.####}) from {(setting.FromWorkOrigin ? "work origin" : "path origin")}");

    /// <summary>Reads "work 10 5", "path -3,2" or just "10 5" (the origin then stays as it was).</summary>
    public static ZProbeSetting? ParseZProbe(string text, ZProbeSetting current)
    {
        var tokens = text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var origin = current.Origin;
        if (tokens.Length > 0 && (tokens[0].StartsWith("work", StringComparison.OrdinalIgnoreCase) || tokens[0].StartsWith("path", StringComparison.OrdinalIgnoreCase)))
        {
            origin = tokens[0].StartsWith("path", StringComparison.OrdinalIgnoreCase) ? "path" : "work";
            tokens = tokens[1..];
        }
        if (tokens.Length != 2
            || !double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return null;
        return new ZProbeSetting(origin, x, y);
    }

    private static void PublishZProbe(StateStore state, ZProbeSetting setting)
    {
        using var _ = state.BeginBatch();
        state.Set(StatePaths.ZProbeOrigin, setting.Origin);
        state.Set(StatePaths.ZProbeX, setting.X);
        state.Set(StatePaths.ZProbeY, setting.Y);
        state.Set(StatePaths.ZProbeLabel, ZProbeText(setting));
    }

    public static void Register(CommandRegistry registry, IAppHost host, IProbeStore? store = null)
    {
        store ??= new MemoryProbeStore();
        var controller = registry.Context.Controller;
        var drift = new RingGaugeSession(registry.Context.State, store, line => controller.SendLineAsync(line));
        PublishZProbe(registry.Context.State, store.ZProbe);
        static bool Idle(CommandContext c, CommandArgs _) => ToolCommands.IsIdle(c);

        void Add(string id, string title, Func<CommandContext, CommandArgs, Task> execute, string description, CommandParameter[]? parameters = null, Func<CommandContext, CommandArgs, bool>? canExecute = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Probing", Description = description, Parameters = parameters ?? [],
                CanExecute = canExecute ?? Idle, DependsOn = IdlePaths, Execute = execute,
            });

        Add("probe", "Probe", async (c, a) =>
        {
            var family = a.GetString("family") ?? throw new ArgumentException("No probing family given.");
            var operation = a.GetString("operation") ?? throw new ArgumentException("No probing operation given.");
            var def = ProbeOperations.FindFamily(family) ?? throw new ArgumentException($"Unknown probing family '{family}'.");
            var config = ProbeOperations.Defaults(def);
            foreach (var p in def.Parameters)
                if (a.GetString(p.Code) is { } value) config[p.Code] = value;
            var result = ProbeOperations.Build(family, operation, config);
            if (!result.Ok) throw new ArgumentException(result.Problem);
            var question = $"Run this probing command? The machine will move.\n\n{result.Gcode}" + (result.Note is null ? "" : $"\n\n{result.Note}");
            if (a.GetBool("confirmed") != true && !await host.ConfirmAsync(question)) return;
            foreach (var line in RingGaugeDrift.WithCorrection(result.Gcode!, config, store.Drift)) await c.Controller.SendLineAsync(line);
        }, "Runs a probing operation of the community firmware. 'family' and 'operation' pick it (see the probePanel), and any parameter code (X, Y, D, ...) sets a value.",
            [new("family", "string", "singleAxis, outsideCorner, insideCorner, bore, boss, angle, probeTip, calibration or fourthAxis", true),
             new("operation", "string", "The operation within the family, e.g. CenterBore", true), new("confirmed", "bool", "Skip the question")]);

        Add("xyzProbe", "XYZ probe", async (c, a) =>
        {
            var line = XyzProbe(a.GetDouble("height") ?? 9.0, a.GetDouble("diameter") ?? 3.175);
            if (a.GetBool("confirmed") != true && !await host.ConfirmAsync($"Probe X, Y and Z now? The machine will move.\n\n{line.Trim()}")) return;
            await c.Controller.SendLineAsync(line);
        }, "Probes the workpiece in X, Y and Z at the current position (M495.3).",
            [new("height", "number", "Probe height in mm (default 9)"), new("diameter", "number", "Tool or probe diameter in mm (default 3.175)")]);

        Add("gotoPathOrigin", "Go to path origin", async (c, _) =>
        {
            var line = GotoPathOrigin(c.State) ?? throw new InvalidOperationException("Open a G-code file that fits the work area first.");
            await c.Controller.SendLineAsync(line);
        }, "Rapid move to the lower-left corner of the open program (M496.5).",
            canExecute: (c, _) => ToolCommands.IsIdle(c) && PathOriginAvailable(c.State));

        Add("autoRun", "Prepare workpiece", async (c, a) =>
        {
            double[]? levelOffsets = a.Has("levelOffsets") && a.GetString("levelOffsets") is { } text
                ? [.. text.Split(',', StringSplitOptions.TrimEntries).Select(t => double.Parse(t, CultureInfo.InvariantCulture))] : null;
            // Without explicit offsets the Z probe goes where the Z probe setting says.
            var (setX, setY) = ZProbeOffsets(c.State, store.ZProbe, levelOffsets);
            var options = new AutoOptions(
                a.GetBool("margin") ?? false, a.GetBool("zProbe") ?? false, a.GetBool("zProbeAbsolute") ?? false, a.GetBool("leveling") ?? false, a.GetBool("gotoOrigin") ?? false,
                a.GetDouble("zProbeOffsetX") ?? setX, a.GetDouble("zProbeOffsetY") ?? setY, a.GetInt("pointsX") ?? 3, a.GetInt("pointsY") ?? 3, a.GetDouble("height") ?? 5,
                levelOffsets, a.GetInt("tool") ?? 0);
            var lines = AutoRun(c.State, options);
            if (lines.Count == 0) throw new InvalidOperationException("Nothing to do: open a G-code file that fits the work area, and choose margin, Z probe, auto-level or go to origin.");
            if (a.GetBool("confirmed") != true && !await host.ConfirmAsync("Run this on the machine now? It will move.\n\n" + string.Join('\n', lines.Select(l => l.Trim())))) return;
            var buffer = a.GetBool("buffer") == true ? "buffer " : "";
            foreach (var line in lines) await c.Controller.SendLineAsync(buffer + line);
        }, "Draws the margin, probes Z, auto-levels and/or goes to the origin, over the open program's extents (M495).",
            [new("margin", "bool", "Trace the program's outline"), new("zProbe", "bool", "Probe Z at the offset below"), new("zProbeAbsolute", "bool", "Probe at the fixed anchor position (4th-axis setups)"),
             new("leveling", "bool", "Auto-level over the program's area"), new("gotoOrigin", "bool", "Go to the path origin afterwards"),
             new("zProbeOffsetX", "number", "Z probe X offset from the path origin (default: from the Z probe setting)"), new("zProbeOffsetY", "number", "Z probe Y offset from the path origin (default: from the Z probe setting)"),
             new("pointsX", "number", "Auto-level points along X (default 3)"), new("pointsY", "number", "Auto-level points along Y (default 3)"), new("height", "number", "Auto-level height (default 5)"),
             new("levelOffsets", "string", "x-, x+, y-, y+ margins of the levelled area, e.g. \"0,0,0,0\""), new("tool", "number", "Tool to change to before going to the origin"),
             new("buffer", "bool", "Queue behind the running program"), new("confirmed", "bool", "Skip the question")]);

        // The Z probe position: asks for it, remembers it, and publishes zprobe.* for labels.
        registry.Register(new CommandDefinition
        {
            Id = "zProbeSetup", Title = "Z probe position", Category = "Probing", RequiresConnection = false,
            Description = "Sets where the Z probe is: 'work' or 'path' origin, then the X and Y offset in mm. Asks when no 'value' is given.",
            Parameters = [new("value", "string", "For example \"work 10 5\" or \"path -3 2\"")],
            Execute = async (c, a) =>
            {
                var current = store.ZProbe;
                var text = a.GetString("value") ?? await host.PromptAsync("Z probe position",
                    "Origin (work or path), then X and Y offset in mm. Currently " + ZProbeText(current) + ".",
                    string.Create(CultureInfo.InvariantCulture, $"{(current.FromWorkOrigin ? "work" : "path")} {current.X:0.####} {current.Y:0.####}"));
                if (text is null) return;
                var setting = ParseZProbe(text, current) ?? throw new ArgumentException("Type work or path, then the X and Y offset, for example: work 10 5");
                store.ZProbe = setting;
                PublishZProbe(c.State, setting);
            },
        });

        // The ring-gauge drift check.
        void Ring(string id, string title, string description, Func<CommandContext, CommandArgs, Task> execute, Func<CommandContext, CommandArgs, bool>? canExecute = null, CommandParameter[]? parameters = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Probing", Description = description, Parameters = parameters ?? [], Execute = execute,
                RequiresConnection = false, CanExecute = canExecute, DependsOn = [.. IdlePaths, StatePaths.Connected, StatePaths.DriftDone, StatePaths.DriftRunning],
            });
        Ring("ringGaugeProbe", "Probe ring gauge", "Measures the ring gauge for the current step of the drift check (asks first, machine must be idle); when all three steps are done it restarts.",
            (_, a) => drift.ProbeAsync(message => a.GetBool("confirmed") == true ? Task.FromResult(true) : host.ConfirmAsync(message)),
            (c, _) => drift.Done || (ToolCommands.IsIdle(c) && c.State.Get(StatePaths.Connected, false) && !drift.Running),
            [new("confirmed", "bool", "Skip the question")]);
        Ring("ringGaugeBack", "Ring gauge: previous step", "Goes back one step of the drift check.", (_, _) => { drift.Back(); return Task.CompletedTask; });
        Ring("ringGaugeReset", "Ring gauge: restart", "Clears the drift check's measurements.", (_, _) => { drift.Reset(); return Task.CompletedTask; });
        Ring("ringGaugeApplyTip", "Ring gauge: include tip diameter", "Turns the inclusion of the probe tip diameter in the ring-gauge probe on or off (toggles without 'value').",
            (c, a) => { drift.SetApplyTip(a.GetBool("value") ?? !c.State.Get(StatePaths.DriftApplyTip, true)); return Task.CompletedTask; },
            parameters: [new("value", "bool", "On or off")]);
        Ring("ringGaugePersist", "Ring gauge: keep the correction", "Turns the stored correction on or off; when on, XY-zeroing probes apply it (toggles without 'value').",
            (c, a) => { drift.SetPersist(a.GetBool("value") ?? !c.State.Get(StatePaths.DriftPersist, false)); return Task.CompletedTask; },
            parameters: [new("value", "bool", "On or off")]);
    }
}
