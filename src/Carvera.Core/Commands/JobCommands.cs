using System.Globalization;
using Carvera.Core.Config;
using Carvera.Core.Job;
using Carvera.Core.Probing;
using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Commands;

/// <summary>
/// The job setup page: choose where the work origin comes from (anchor 1, anchor 2 or a probed position), pick the preparation
/// steps and run them. The page itself is ordinary layout elements over the state <see cref="JobSetup"/> publishes.
/// </summary>
public static class JobCommands
{
    private static string G(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Reads "10 5" or "10, 5" into two numbers.</summary>
    public static (double X, double Y)? ParsePair(string text)
    {
        var tokens = text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 2
            && double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? (x, y) : null;
    }

    /// <summary>Reads "3 3 5" or "3 3 5 2 2 2 2": points along X and Y, the height, and optionally the margins -X +X -Y +Y.</summary>
    public static JobSettings? ParseLevelling(string text, JobSettings current)
    {
        var tokens = text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is not (3 or 7)) return null;
        var numbers = new double[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return null;
        if (numbers[0] < 2 || numbers[1] < 2 || numbers[0] != Math.Floor(numbers[0]) || numbers[1] != Math.Floor(numbers[1])) return null;
        var updated = current with { LevelPointsX = (int)numbers[0], LevelPointsY = (int)numbers[1], LevelHeight = numbers[2] };
        return tokens.Length == 7 ? updated with { LevelXn = numbers[3], LevelXp = numbers[4], LevelYn = numbers[5], LevelYp = numbers[6] } : updated;
    }

    public static void Register(CommandRegistry registry, IAppHost host, IProbeStore store, MachineConfigStore? config = null)
    {
        var state = registry.Context.State;
        var setup = new JobSetup(state, store);
        if (config is not null)
        {
            config.Reset += () => { if (config.Loaded) setup.UseConfig(config.Values); };
            if (config.Loaded) setup.UseConfig(config.Values);
        }

        void Add(string id, string title, Func<CommandContext, CommandArgs, Task> execute, string description, CommandParameter[]? parameters = null,
            Func<CommandContext, CommandArgs, bool>? canExecute = null, string[]? dependsOn = null, bool requiresConnection = true) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Job setup", Description = description, Parameters = parameters ?? [],
                Execute = execute, CanExecute = canExecute, DependsOn = dependsOn ?? [], RequiresConnection = requiresConnection,
            });

        Add("jobOrigin", "Choose work origin", async (c, a) =>
        {
            var anchor = a.GetString("anchor")?.ToLowerInvariant();
            if (!OriginChoice.IsValid(anchor)) throw new ArgumentException("The work origin comes from anchor1, anchor2 or probe.");
            var current = store.Job;
            var x = a.GetDouble("x") ?? current.OriginX;
            var y = a.GetDouble("y") ?? current.OriginY;
            if (anchor != OriginChoice.Probe)
            {
                if (!c.Controller.IsConnected) throw new InvalidOperationException("Connect to the machine first.");
                if (!ToolCommands.IsIdle(c)) throw new InvalidOperationException("The machine must be idle to set the work origin.");
                var position = WorkCommands.OriginPosition(anchor == OriginChoice.Anchor1 ? WorkCommands.OriginAnchor.Anchor1 : WorkCommands.OriginAnchor.Anchor2,
                    x, y, setup.Geometry.Coordinate, 0, 0)!.Value;
                var name = anchor == OriginChoice.Anchor1 ? "anchor 1" : "anchor 2";
                if (a.GetBool("confirmed") != true && !await host.ConfirmAsync($"Put the work origin of {c.State.Get<string>(StatePaths.WcsActiveName) ?? "the active coordinate system"} at {name}" +
                    (x != 0 || y != 0 ? $" + ({G(x)}, {G(y)}) mm" : "") + "? The machine does not move."))
                    return;
                await c.Controller.SendLineAsync(MachineCommands.SetWorkOffset(position.X, position.Y));
            }
            store.Job = current with { Origin = anchor!, OriginX = x, OriginY = y };
            setup.Publish();
        }, "Chooses where the work origin comes from. Anchor 1 and anchor 2 set it at once (after asking; the machine does not move) from the anchor position plus the offset; 'probe' only selects the probed-position way, which is then measured with xyzProbe.",
            [new("anchor", "string", "anchor1, anchor2 or probe", true), new("x", "number", "X offset from the anchor, mm"), new("y", "number", "Y offset from the anchor, mm"),
             new("confirmed", "bool", "Skip the question")], requiresConnection: false);

        Add("jobOffset", "Origin offset...", async (c, a) =>
        {
            var current = store.Job;
            var text = a.GetString("text") ?? await host.PromptAsync("Origin offset",
                $"Offset of the work origin from {(current.Origin == OriginChoice.Anchor2 ? "anchor 2" : "anchor 1")}: X and Y in mm", $"{G(current.OriginX)} {G(current.OriginY)}");
            if (string.IsNullOrWhiteSpace(text)) return;
            var pair = ParsePair(text) ?? throw new ArgumentException("Type the X and Y offset in mm, for example: 10 5");
            if (current.Origin == OriginChoice.Probe)
            {
                store.Job = current with { OriginX = pair.X, OriginY = pair.Y };
                setup.Publish();
                return;
            }
            await registry.ExecuteAsync("jobOrigin", CommandArgs.Empty.With("anchor", current.Origin).With("x", pair.X).With("y", pair.Y));
        }, "Changes the offset from the chosen anchor and, for an anchor, sets the work origin again. Asks for 'text' (\"10 5\") when not given.",
            [new("text", "string", "For example \"10 5\"")], requiresConnection: false);

        Add("jobToggle", "Toggle a job step", (c, a) =>
        {
            var name = a.GetString("name")?.ToLowerInvariant();
            var current = store.Job;
            bool? On(bool now) => a.GetBool("on") ?? !now;
            store.Job = name switch
            {
                "margin" => current with { Margin = On(current.Margin)!.Value },
                "zprobe" => current with { ZProbe = On(current.ZProbe)!.Value },
                "leveling" => current with { Leveling = On(current.Leveling)!.Value },
                "gotoorigin" => current with { GotoOrigin = On(current.GotoOrigin)!.Value },
                _ => throw new ArgumentException("The steps are margin, zprobe, leveling and gotoOrigin."),
            };
            setup.Publish();
            return Task.CompletedTask;
        }, "Turns a preparation step of jobRun on or off (toggles without 'on').",
            [new("name", "string", "margin, zprobe, leveling or gotoOrigin", true), new("on", "bool", "Omit to toggle")], requiresConnection: false);

        Add("jobLevelSetup", "Auto-level grid...", async (c, a) =>
        {
            var current = store.Job;
            var text = a.GetString("text") ?? await host.PromptAsync("Auto-level grid",
                "Points along X, points along Y and the height in mm; optionally the margins -X +X -Y +Y",
                $"{current.LevelPointsX} {current.LevelPointsY} {G(current.LevelHeight)}" + (current.LevelOffsets.Any(o => o != 0) ? $" {G(current.LevelXn)} {G(current.LevelXp)} {G(current.LevelYn)} {G(current.LevelYp)}" : ""));
            if (string.IsNullOrWhiteSpace(text)) return;
            store.Job = ParseLevelling(text, current) ?? throw new ArgumentException("Type at least 2 points in each direction and a height, for example: 3 3 5");
            setup.Publish();
        }, "Sets the auto-level grid. Asks for 'text' (\"3 3 5\" or \"3 3 5 2 2 2 2\") when not given.",
            [new("text", "string", "For example \"3 3 5\"")], requiresConnection: false);

        Add("jobRun", "Run job preparation", async (c, a) =>
        {
            var job = store.Job;
            if (!(job.Margin || job.ZProbe || job.Leveling || job.GotoOrigin))
                throw new InvalidOperationException("Choose at least one step: margin, Z probe, auto-level or go to origin.");
            var args = CommandArgs.Empty.With("margin", job.Margin).With("zProbe", job.ZProbe).With("leveling", job.Leveling).With("gotoOrigin", job.GotoOrigin)
                .With("pointsX", job.LevelPointsX).With("pointsY", job.LevelPointsY).With("height", job.LevelHeight)
                .With("levelOffsets", string.Join(',', job.LevelOffsets.Select(G)));
            if (a.GetBool("confirmed") == true) args = args.With("confirmed", true);
            if (a.GetBool("buffer") == true) args = args.With("buffer", true);
            await registry.ExecuteAsync("autoRun", args);
        }, "Runs the chosen preparation steps (margin, Z probe, auto-level, go to the path origin) in the order the Python controller does, after asking.",
            [new("confirmed", "bool", "Skip the question"), new("buffer", "bool", "Queue behind the running program")],
            (c, _) => ToolCommands.IsIdle(c), [StatePaths.MachineState, StatePaths.AtcState, StatePaths.JobLines]);

        Add("setBedImage", "Show the bed picture", (c, a) =>
        {
            c.State.Set(StatePaths.ViewBedImage, a.GetBool("on") ?? !c.State.Get(StatePaths.ViewBedImage, true));
            return Task.CompletedTask;
        }, "Shows or hides the machine bed picture under the toolpath and on the job setup page (toggles without 'on').",
            [new("on", "bool", "Omit to toggle")], requiresConnection: false);
    }
}
