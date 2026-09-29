using Carvera.Core.Config;
using Carvera.Core.State;

namespace Carvera.Core.Commands;

/// <summary>Commands for the machine's configuration (config.txt): read it, send edits, restore or save the defaults.</summary>
public static class ConfigCommands
{
    public static void Register(CommandRegistry registry, MachineConfigStore store, IAppHost host)
    {
        static bool Idle(CommandContext c) => c.State.Get<string>(StatePaths.MachineState) == "Idle";

        void Add(string id, string title, Func<CommandContext, CommandArgs, Task> execute, string description, Func<CommandContext, CommandArgs, bool>? canExecute = null, string[]? dependsOn = null, CommandParameter[]? parameters = null) =>
            registry.Register(new CommandDefinition
            {
                Id = id, Title = title, Category = "Machine configuration", Description = description, Execute = execute,
                CanExecute = canExecute, DependsOn = dependsOn ?? [StatePaths.MachineState], Parameters = parameters ?? [],
            });

        Add("configLoad", "Read machine settings", async (c, _) =>
        {
            if (store.Pending.Count > 0 && !await host.ConfirmAsync($"Discard the {store.Pending.Count} change{(store.Pending.Count == 1 ? "" : "s")} you have not sent and read the machine's settings again?")) return;
            await store.LoadAsync();
        }, "Reads /sd/config.txt from the machine and lists its settings. Only while the machine is idle.", (c, _) => Idle(c));

        Add("configApply", "Send changes", async (c, _) =>
        {
            var count = await store.ApplyAsync();
            if (count > 0) c.Console.Info($"Sent {count} setting{(count == 1 ? "" : "s")}. They take effect after the machine is reset.");
        }, "Sends the edited settings to the machine (config-set sd). They take effect after a reset.",
            (c, _) => store.Pending.Count > 0 && Idle(c), [StatePaths.MachineState, StatePaths.ConfigPending]);

        Add("configDiscard", "Discard changes", (_, _) => { store.Discard(); return Task.CompletedTask; }, "Forgets the edits that have not been sent.",
            (_, _) => store.Pending.Count > 0, [StatePaths.ConfigPending]);

        Add("configRestore", "Restore default settings", async (c, a) =>
        {
            if (a.GetBool("confirmed") != true && !await host.ConfirmAsync("Restore the machine's settings from its saved defaults? The machine needs a reset afterwards.")) return;
            await c.Controller.SendLineAsync("config-restore");
        }, "Restores the machine's settings from its saved defaults (config-restore), after asking.", (c, _) => Idle(c),
            parameters: [new("confirmed", "bool", "Skip the question")]);

        Add("configSaveDefault", "Save settings as default", async (c, a) =>
        {
            if (a.GetBool("confirmed") != true && !await host.ConfirmAsync("Save the machine's current settings as its defaults?")) return;
            await c.Controller.SendLineAsync("config-default");
        }, "Saves the machine's current settings as its defaults (config-default), after asking.", (c, _) => Idle(c),
            parameters: [new("confirmed", "bool", "Skip the question")]);
    }
}
