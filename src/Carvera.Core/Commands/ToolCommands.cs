using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Commands;

/// <summary>
/// Tool changer commands for the tool widgets. Like the pendant's tool actions they run only while the machine is
/// idle with nothing running, and those that move the machine ask first.
/// </summary>
public static class ToolCommands
{
    /// <summary>Tools the machine can be told to change to: probe, 3D probe and the six magazine slots (as on the CYD pendant).</summary>
    public static readonly int[] ChangeableTools = [0, 999990, 1, 2, 3, 4, 5, 6];

    private static readonly string[] IdlePaths = [StatePaths.MachineState, StatePaths.AtcState, StatePaths.JobLines];

    /// <summary>True when nothing is moving: machine idle, tool changer quiet, no program running.</summary>
    public static bool IsIdle(CommandContext c) =>
        c.State.Get<string>(StatePaths.MachineState) == "Idle" && c.State.Get(StatePaths.AtcState, 0) == 0 && c.State.Get(StatePaths.JobLines, -1) <= 0;

    public static void Register(CommandRegistry registry, IAppHost host)
    {
        void Add(string id, string title, Func<CommandContext, CommandArgs, Task> execute, string description, CommandParameter[]? parameters = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Tools", Description = description, Parameters = parameters ?? [],
                CanExecute = (c, _) => IsIdle(c), DependsOn = IdlePaths, Execute = execute,
            });

        async Task<bool> Confirm(string message) => await host.ConfirmAsync(message);

        Add("toolChange", "Change tool", async (c, a) =>
        {
            var tool = a.GetInt("tool") ?? throw new ArgumentException("No tool number given.");
            if (!ChangeableTools.Contains(tool)) throw new ArgumentException($"The machine cannot change to tool {tool}.");
            if (a.GetBool("confirmed") != true && !await Confirm($"Change to {ToolInfo.Label(tool)} now? The machine will move.")) return;
            await c.Controller.SendLineAsync(MachineCommands.ChangeTool(tool));
        }, "Changes to the given tool (0 probe, 999990 3D probe, 1-6) after asking. Only while the machine is idle.",
            [new("tool", "number", "0 = probe, 999990 = 3D probe, 1-6", true), new("confirmed", "bool", "Skip the question")]);

        Add("toolChoose", "Change to tool...", async (c, a) =>
        {
            var text = await host.PromptAsync("Change tool", "Tool number (1-6, 0 = probe, 999990 = 3D probe)", c.State.Get(StatePaths.ToolTarget, -1) >= 0 ? c.State.Get(StatePaths.ToolTarget, -1).ToString() : "1");
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!int.TryParse(text.Trim(), out var tool) || !ChangeableTools.Contains(tool))
                throw new ArgumentException($"'{text.Trim()}' is not a tool the machine can change to.");
            if (!await Confirm($"Change to {ToolInfo.Label(tool)} now? The machine will move.")) return;
            await c.Controller.SendLineAsync(MachineCommands.ChangeTool(tool));
        }, "Asks for a tool number, then changes to it.");

        Add("toolDrop", "Drop tool", async (c, _) =>
        {
            if (!await Confirm("Put the current tool back in the magazine now? The machine will move.")) return;
            await c.Controller.SendLineAsync(MachineCommands.DropTool);
        }, "Returns the tool in the spindle to the magazine, after asking.");

        Add("toolClamp", "Clamp collet", async (c, _) =>
        {
            if (!await Confirm("Clamp the collet? Make sure a tool is in the spindle.")) return;
            await c.Controller.SendLineAsync(MachineCommands.ClampTool);
        }, "Closes the collet, after asking.");

        Add("toolUnclamp", "Release collet", async (c, _) =>
        {
            if (!await Confirm("Release the collet? The tool in the spindle may fall. Hold it first.")) return;
            await c.Controller.SendLineAsync(MachineCommands.UnclampTool);
        }, "Opens the collet, after asking.");

        Add("toolCalibrate", "Calibrate tool length", async (c, _) =>
        {
            if (!await Confirm("Measure the length of the current tool on the tool sensor now? The machine will move.")) return;
            await c.Controller.SendLineAsync(MachineCommands.CalibrateTool);
        }, "Measures the current tool on the tool sensor, after asking.");

        Add("toolSetNumber", "Set current tool number", async (c, a) =>
        {
            var tool = a.GetInt("tool");
            if (tool is null)
            {
                var text = await host.PromptAsync("Set tool number", "Tell the machine the tool in the spindle is number", c.State.Get(StatePaths.ToolCurrent, -1) > 0 ? c.State.Get(StatePaths.ToolCurrent, -1).ToString() : "1");
                if (string.IsNullOrWhiteSpace(text)) return;
                if (!int.TryParse(text.Trim(), out var parsed) || parsed < 0) throw new ArgumentException($"'{text.Trim()}' is not a tool number.");
                tool = parsed;
            }
            await c.Controller.SendLineAsync(MachineCommands.SetTool(tool.Value));
        }, "Tells the machine which tool is in the spindle without moving anything; asks for the number unless 'tool' is given.",
            [new("tool", "number", "Tool number")]);
    }
}
