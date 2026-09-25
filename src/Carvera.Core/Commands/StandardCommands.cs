using Carvera.Core.Connection;
using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Commands;

/// <summary>The machine commands available to layouts. Ported from the actions of the Python controller.</summary>
public static class StandardCommands
{
    /// <summary>
    /// Safety controls that every layout should expose. Each entry lists the command ids that satisfy it.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string[] CommandIds)> SafetyControls =
    [
        ("Feed Hold", ["feedHold", "pauseResume"]),
        ("Stop", ["stop"]),
        ("Reset", ["reset"]),
    ];

    private static readonly string[] MotionPaths = [StatePaths.MachineState];

    private static bool CanMove(CommandContext c, CommandArgs _) =>
        c.State.Get<string>(StatePaths.MachineState) is not ("Alarm" or "Run" or "Hold" or "N/A" or "Sleep");

    /// <summary>Parses "X", "Z-", "X+Y-" into axis letters with +1/-1 signs.</summary>
    public static Dictionary<char, double> ParseJogAxes(string text)
    {
        var result = new Dictionary<char, double>();
        var t = text.Trim().ToUpperInvariant();
        for (var i = 0; i < t.Length; i++)
        {
            if (!"XYZA".Contains(t[i])) continue;
            var sign = 1.0;
            if (i + 1 < t.Length && t[i + 1] is '+' or '-') sign = t[i + 1] == '-' ? -1 : 1;
            result[t[i]] = sign;
        }
        return result;
    }

    private static CommandParameter P(string name, string kind, string description, bool required = false) => new(name, kind, description, required);

    public static void Register(CommandRegistry registry)
    {
        void Add(string id, string title, string category, Func<CommandContext, CommandArgs, Task> execute,
            string description = "", CommandParameter[]? parameters = null, bool requiresConnection = true,
            Func<CommandContext, CommandArgs, bool>? canExecute = null, string[]? dependsOn = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = category, Execute = execute, Description = description,
                Parameters = parameters ?? [], RequiresConnection = requiresConnection, CanExecute = canExecute,
                DependsOn = dependsOn ?? [],
            });

        void Motion(string id, string title, Func<CommandContext, CommandArgs, Task> execute, string description = "", CommandParameter[]? parameters = null) =>
            Add(id, title, "Motion", execute, description, parameters, canExecute: CanMove, dependsOn: MotionPaths);

        void Line(string id, string title, string category, string line, string description = "") =>
            Add(id, title, category, (c, _) => c.Controller.SendLineAsync(line), description);

        // ----------------------------------------------------------- connection
        Add("connect", "Connect", "Connection", async (c, a) =>
        {
            var kindText = a.GetString("kind") ?? c.State.Get<string>(StatePaths.ConnectionKind) ?? "wifi";
            var address = a.GetString("address") ?? c.State.Get<string>(StatePaths.ConnectionAddress) ?? "";
            var kind = kindText.ToLowerInvariant() switch { "usb" => ConnectionKind.Usb, "simulator" or "sim" => ConnectionKind.Simulator, _ => ConnectionKind.WiFi };
            if (kind != ConnectionKind.Simulator && string.IsNullOrWhiteSpace(address))
            {
                c.Console.Warning("Enter a machine address or COM port first.");
                return;
            }
            await c.Controller.ConnectAsync(new ConnectionOptions(kind, kind == ConnectionKind.Simulator ? "simulator" : address));
        }, "Connects using the given or last entered connection settings.",
            [P("kind", "string", "wifi, usb or simulator"), P("address", "string", "IP address[:port] or COM port")],
            requiresConnection: false, canExecute: (c, _) => !c.Controller.IsConnected, dependsOn: [StatePaths.Connected]);
        Add("disconnect", "Disconnect", "Connection", (c, _) => c.Controller.DisconnectAsync());

        // ----------------------------------------------------------- safety & run control
        Add("feedHold", "Feed hold", "Run", (c, _) => c.Controller.SendRealtimeAsync(MachineCommands.FeedHold, "! (feed hold)"),
            "Pauses motion immediately (real-time '!').");
        Add("cycleStart", "Cycle start", "Run", (c, _) => c.Controller.SendRealtimeAsync(MachineCommands.CycleStart, "~ (cycle start)"),
            "Resumes after a feed hold (real-time '~').");
        Add("pauseResume", "Pause / resume", "Run", (c, _) =>
            c.State.Get<string>(StatePaths.MachineState) is "Hold" or "Pause"
                ? c.Controller.SendRealtimeAsync(MachineCommands.CycleStart, "~ (cycle start)")
                : c.Controller.SendRealtimeAsync(MachineCommands.FeedHold, "! (feed hold)"),
            "Feed hold when running, cycle start when held.");
        Line("stop", "Stop", "Run", MachineCommands.Abort, "Aborts the running job.");
        Add("reset", "Reset", "Run", (c, _) => c.Controller.SendRealtimeAsync(MachineCommands.SoftReset, "Ctrl-X (reset)"),
            "Soft reset (Ctrl-X): stops everything and clears the planner.");
        Line("unlock", "Unlock", "Run", MachineCommands.Unlock, "Clears an alarm ($X).");
        Add("home", "Home", "Motion", (c, _) => c.Controller.SendLineAsync(MachineCommands.Home), "Homes all axes ($H).",
            canExecute: (c, _) => c.State.Get<string>(StatePaths.MachineState) is not ("Run" or "Hold"), dependsOn: MotionPaths);
        Add("suspend", "Suspend", "Run", (c, _) => c.Controller.SendLineAsync(MachineCommands.Suspend));
        Add("resume", "Resume", "Run", (c, _) => c.Controller.SendLineAsync(MachineCommands.Resume));
        Add("playFile", "Run file", "Run", (c, a) => c.Controller.SendLineAsync(MachineCommands.Play(a.GetString("path") ?? throw new ArgumentException("No file path given."))),
            "Runs a file already on the machine's SD card.", [P("path", "string", "Remote path, e.g. /sd/gcodes/part.nc", true)],
            canExecute: CanMove, dependsOn: MotionPaths);

        // ----------------------------------------------------------- jogging
        Motion("jog", "Jog", (c, a) =>
        {
            var moves = ParseJogAxes(a.GetString("axis") ?? "X");
            if (moves.Count == 0) throw new ArgumentException($"Unknown jog axis '{a.GetString("axis")}'.");
            var direction = a.GetDouble("direction") ?? 1.0;
            var step = a.GetDouble("distance") ?? c.State.Get(StatePaths.JogStep, 1.0);
            var distances = moves.ToDictionary(m => m.Key, m => m.Value * direction * step);
            var feed = a.GetDouble("feed") ?? c.State.Get(StatePaths.JogFeed, 0.0);
            return c.Controller.SendLineAsync(MachineCommands.Jog(distances, feed));
        }, "Relative jog by the current jog step (or 'distance'). 'axis' is X, Y, Z or A with an optional sign, and may combine axes: \"X+Y-\".",
        [P("axis", "string", "e.g. X, Z-, X+Y+", true), P("direction", "number", "+1 or -1 (multiplies the signs in 'axis')"),
         P("distance", "number", "Overrides the jog step"), P("feed", "number", "mm/min; defaults to jog.feed")]);
        Add("setJogStep", "Set jog step", "Motion", (c, a) => { c.State.Set(StatePaths.JogStep, a.GetDouble("value") ?? 1.0); return Task.CompletedTask; },
            parameters: [P("value", "number", "Step in mm", true)], requiresConnection: false);
        Add("setJogFeed", "Set jog feed", "Motion", (c, a) => { c.State.Set(StatePaths.JogFeed, a.GetDouble("value") ?? 3000.0); return Task.CompletedTask; },
            parameters: [P("value", "number", "mm/min", true)], requiresConnection: false);
        Add("adjustJogStep", "Next jog step", "Motion", (c, a) =>
        {
            double[] steps = [0.01, 0.1, 1, 10, 100];
            var current = c.State.Get(StatePaths.JogStep, 1.0);
            var index = Array.FindIndex(steps, s => Math.Abs(s - current) < 1e-9);
            index = Math.Clamp((index < 0 ? 2 : index) + (int)(a.GetDouble("delta") ?? 1), 0, steps.Length - 1);
            c.State.Set(StatePaths.JogStep, steps[index]);
            return Task.CompletedTask;
        }, parameters: [P("delta", "number", "+1 for larger, -1 for smaller")], requiresConnection: false);

        // ----------------------------------------------------------- go to
        Motion("gotoClearance", "Go to clearance", (c, _) => c.Controller.SendLineAsync(MachineCommands.GotoClearance));
        Motion("gotoWorkOrigin", "Go to work origin", (c, _) => c.Controller.SendLineAsync(MachineCommands.GotoWorkOrigin));
        Motion("gotoAnchor1", "Go to anchor 1", (c, _) => c.Controller.SendLineAsync(MachineCommands.GotoAnchor1));
        Motion("gotoAnchor2", "Go to anchor 2", (c, _) => c.Controller.SendLineAsync(MachineCommands.GotoAnchor2));
        Motion("gotoSafeZ", "Raise to safe Z", (c, _) => c.Controller.SendLineAsync(MachineCommands.GotoSafeZ));
        Motion("gotoMachineHome", "Go to machine home", async (c, _) =>
        {
            await c.Controller.SendLineAsync(MachineCommands.GotoSafeZ);
            await c.Controller.SendLineAsync(MachineCommands.GotoMachineHomeXY);
        });
        Motion("gotoWorkHome", "Go to work XY zero", async (c, _) =>
        {
            await c.Controller.SendLineAsync(MachineCommands.GotoSafeZ);
            await c.Controller.SendLineAsync(MachineCommands.GotoWcsHomeXY(c.State.Get(StatePaths.AxisOffset("x"), 0.0), c.State.Get(StatePaths.AxisOffset("y"), 0.0)));
        });
        Motion("goto", "Go to position", (c, a) => c.Controller.SendLineAsync(MachineCommands.Goto(a.GetDouble("x"), a.GetDouble("y"), a.GetDouble("z"))),
            "Rapid move in work coordinates.", [P("x", "number", "X"), P("y", "number", "Y"), P("z", "number", "Z")]);

        // ----------------------------------------------------------- work coordinates
        Add("setWorkZero", "Set work zero", "Work offsets", (c, a) =>
        {
            var axes = (a.GetString("axes") ?? "XYZ").ToUpperInvariant();
            return c.Controller.SendLineAsync(MachineCommands.SetWorkPosition(
                axes.Contains('X') ? 0 : null, axes.Contains('Y') ? 0 : null, axes.Contains('Z') ? 0 : null, axes.Contains('A') ? 0 : null));
        }, "Makes the current position zero in the active work coordinate system.", [P("axes", "string", "Any of X, Y, Z, A (default XYZ)")],
            canExecute: CanMove, dependsOn: MotionPaths);
        Add("setWorkPosition", "Set work position", "Work offsets", (c, a) =>
            c.Controller.SendLineAsync(MachineCommands.SetWorkPosition(a.GetDouble("x"), a.GetDouble("y"), a.GetDouble("z"), a.GetDouble("a"))),
            parameters: [P("x", "number", "X"), P("y", "number", "Y"), P("z", "number", "Z"), P("a", "number", "A")],
            canExecute: CanMove, dependsOn: MotionPaths);
        Add("selectWcs", "Select work coordinates", "Work offsets", (c, a) =>
        {
            var name = a.GetString("name");
            var index = name is not null ? Array.FindIndex(ResponseParser.WcsNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) : a.GetInt("index") ?? 0;
            if (index < 0) throw new ArgumentException($"Unknown coordinate system '{name}'.");
            return c.Controller.SendLineAsync(MachineCommands.SelectWcs(index));
        }, parameters: [P("index", "number", "0 = G54 ... 5 = G59"), P("name", "string", "G54 ... G59")], canExecute: CanMove, dependsOn: MotionPaths);
        Add("clearRotation", "Clear WCS rotation", "Work offsets", (c, _) => c.Controller.SendLineAsync(MachineCommands.ClearRotation));
        Add("clearAutoLevel", "Clear auto-levelling", "Work offsets", (c, _) => c.Controller.SendLineAsync(MachineCommands.ClearAutoLevel));

        // ----------------------------------------------------------- overrides
        bool Instant(CommandContext c) =>
            c.State.Get(StatePaths.CommunityFirmware, false) && Version.TryParse(c.State.Get<string>(StatePaths.FirmwareVersion), out var v) && v >= new Version(2, 1, 0);
        Task Override(CommandContext c, CommandArgs a, string path, Func<int, bool, string> build, int max)
        {
            var value = a.GetDouble("value") ?? c.State.Get(path, 100.0) + (a.GetDouble("delta") ?? 0);
            return c.Controller.SendLineAsync(build(Math.Clamp((int)Math.Round(value), 10, max), Instant(c)));
        }
        CommandParameter[] overrideParameters = [P("value", "number", "Percent"), P("delta", "number", "Change in percent")];
        Add("feedOverride", "Feed override", "Overrides", (c, a) => Override(c, a, StatePaths.FeedOverride, MachineCommands.FeedOverride, 300), parameters: overrideParameters);
        Add("spindleOverride", "Spindle override", "Overrides", (c, a) => Override(c, a, StatePaths.SpindleOverride, MachineCommands.SpindleOverride, 300), parameters: overrideParameters);
        Add("laserScale", "Laser scale", "Overrides", (c, a) => Override(c, a, StatePaths.LaserScale, (v, _) => MachineCommands.LaserScale(v), 200), parameters: overrideParameters);

        // ----------------------------------------------------------- switches (omit "on" to toggle)
        void Switch(string id, string title, string path, Func<bool, string> build) =>
            Add(id, title, "Switches", (c, a) => c.Controller.SendLineAsync(build(a.GetBool("on") ?? !c.State.Get(path, false))),
                $"Turns {title.ToLowerInvariant()} on or off; toggles when 'on' is omitted.", [P("on", "bool", "true or false; omit to toggle")]);
        Switch("setLight", "Light", "switch.light", MachineCommands.Light);
        Switch("setAir", "Air", "switch.air", MachineCommands.Air);
        Switch("setToolSensorPower", "Tool sensor power", "switch.toolSensor", MachineCommands.ToolSensorPower);
        Switch("setWorkpieceCharge", "Workpiece probe charging", "switch.wpCharge", MachineCommands.WorkpieceChargePower);
        Switch("setVacuumMode", "Vacuum mode", StatePaths.VacuumMode, MachineCommands.VacuumMode);
        Switch("setLaserMode", "Laser mode", StatePaths.LaserMode, MachineCommands.LaserMode);
        Switch("setLaserTest", "Laser test", StatePaths.LaserTesting, MachineCommands.LaserTest);
        Add("setVacuum", "Vacuum", "Switches", (c, a) => c.Controller.SendLineAsync(MachineCommands.Vacuum(
                a.GetInt("power") ?? (a.GetBool("on") ?? !c.State.Get("switch.vacuum", false) ? 100 : 0))),
            parameters: [P("on", "bool", "Omit to toggle"), P("power", "number", "0-100")]);
        Add("setSpindle", "Spindle", "Switches", (c, a) => c.Controller.SendLineAsync(MachineCommands.Spindle(
                a.GetBool("on") ?? !c.State.Get("switch.spindle", false), a.GetDouble("rpm"))),
            parameters: [P("on", "bool", "Omit to toggle"), P("rpm", "number", "Speed")], canExecute: CanMove, dependsOn: MotionPaths);

        // ----------------------------------------------------------- tools
        Add("changeTool", "Change tool", "Tools", (c, a) => c.Controller.SendLineAsync(MachineCommands.ChangeTool(a.GetInt("tool") ?? throw new ArgumentException("No tool number given."))),
            parameters: [P("tool", "number", "Tool number (0 = empty, 8888 = probe)", true)], canExecute: CanMove, dependsOn: MotionPaths);
        Add("setTool", "Set current tool", "Tools", (c, a) => c.Controller.SendLineAsync(MachineCommands.SetTool(a.GetInt("tool") ?? throw new ArgumentException("No tool number given."))),
            parameters: [P("tool", "number", "Tool number", true)]);
        Motion("dropTool", "Drop tool", (c, _) => c.Controller.SendLineAsync(MachineCommands.DropTool));
        Motion("calibrateTool", "Calibrate tool length", (c, _) => c.Controller.SendLineAsync(MachineCommands.CalibrateTool));
        Line("clampTool", "Clamp collet", "Tools", MachineCommands.ClampTool);
        Line("unclampTool", "Release collet", "Tools", MachineCommands.UnclampTool);

        // ----------------------------------------------------------- raw
        Add("sendGcode", "Send G-code", "Console", (c, a) =>
        {
            var line = a.GetString("line")?.Trim();
            return string.IsNullOrEmpty(line) ? Task.CompletedTask : c.Controller.SendLineAsync(line);
        }, parameters: [P("line", "string", "The command to send", true)]);
        Add("clearConsole", "Clear console", "Console", (c, _) => { c.Console.Clear(); return Task.CompletedTask; }, requiresConnection: false);
    }
}
