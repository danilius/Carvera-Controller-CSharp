using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Carvera.App.Layout;
using Carvera.App.Shell;
using Carvera.Core.Commands;
using Carvera.Core.Expressions;
using Carvera.Core.Protocol;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

public static class ButtonComponents
{
    /// <summary>Makes a host run a command on click and disables it while the command is unavailable.</summary>
    public static void AttachCommand(ComponentHost host, BuildContext ctx, string? command, Func<CommandArgs> args, string? confirm = null)
    {
        host.MakeClickable();
        if (command is null) return;
        host.AddEnabledCondition(() => ctx.Commands.CanExecute(command, args()), ctx.Commands.AvailabilityPaths(command));
        host.Clicked += async () =>
        {
            var confirmText = confirm is null ? null : BuildContext.TemplateOf(confirm)?.Render(ctx.Resolve);
            if (confirmText is not null && !await Dialogs.ConfirmAsync(ctx.Owner, confirmText)) return;
            ctx.Run(command, args());
        };
    }

    public static Control Button(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        host.SetDefaultText(ctx.TemplateProp(node, "text"));
        host.Repeat = node.GetBool("repeat") == true;
        var args = ctx.Args(node);
        AttachCommand(host, ctx, node.GetString("command"), () => args, node.GetString("confirm"));
        if (ctx.ExpressionProp(node, "active") is { } active)
        {
            void Update() => host.SetState("active", active.EvaluateBool(ctx.Resolve));
            ctx.Watch(active.Paths, Update);
            Update();
        }
        return new VisualContent(host, ctx.Images);
    }

    public static Control Toggle(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        host.SetDefaultText(ctx.TemplateProp(node, "text"));
        var bind = ctx.ExpressionProp(node, "bind");
        var args = ctx.Args(node);
        var onArgs = node.Has("onArgs") ? ctx.Args(node, "onArgs") : null;
        var offArgs = node.Has("offArgs") ? ctx.Args(node, "offArgs") : null;
        bool IsOn() => bind?.EvaluateBool(ctx.Resolve) ?? false;
        // Clicking asks for the opposite of the current state.
        CommandArgs NextArgs() => IsOn() ? offArgs ?? args.With("on", false) : onArgs ?? args.With("on", true);
        AttachCommand(host, ctx, node.GetString("command"), NextArgs);
        void Update() => host.SetStates(IsOn() ? ["on"] : ["off"], ["on", "off"]);
        ctx.Watch(bind?.Paths ?? [], Update);
        Update();
        return new VisualContent(host, ctx.Images);
    }

    public sealed record Option(object? Value, string Text, string? Image);

    public static IReadOnlyList<Option> ParseOptions(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        var options = new List<Option>();
        foreach (var item in array)
        {
            if (item is JsonObject o)
            {
                var value = ToValue(o["value"]);
                options.Add(new Option(value, o["text"]?.ToString() ?? TextTemplate.FormatValue(value, null), o["image"]?.ToString()));
            }
            else
            {
                var value = ToValue(item);
                options.Add(new Option(value, TextTemplate.FormatValue(value, null), null));
            }
        }
        return options;
    }

    private static object? ToValue(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node?.ToJsonString(),
    };

    /// <summary>Builds a row/column of option buttons with a "selected" state.</summary>
    public static Control OptionGroup(LayoutNode node, BuildContext ctx, IReadOnlyList<Option> options, Expression? bind,
        string? command, string argName, CommandArgs baseArgs)
    {
        var panel = new FlexPanel
        {
            Orientation = node.GetString("orientation")?.Equals("vertical", StringComparison.OrdinalIgnoreCase) == true ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = node.GetNumber("spacing") ?? 4,
        };
        var optionVisuals = node.Get("optionVisuals") as JsonObject ?? [];
        var hosts = new List<(ComponentHost Host, Option Option)>();
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var optionNode = new LayoutNode("button", new JsonObject(), $"{node.Path}.options[{i}]", []);
            var visuals = (JsonObject)optionVisuals.DeepClone();
            if (option.Image is not null)
            {
                var normal = visuals["normal"] as JsonObject ?? new JsonObject();
                normal["image"] ??= option.Image;
                visuals["normal"] = normal;
            }
            var optionHost = new ComponentHost(optionNode, ctx, "choiceOption", visuals);
            optionHost.SetDefaultText(TextTemplate.Parse(option.Text.Replace("{", "{{").Replace("}", "}}")));
            var args = baseArgs.With(argName, option.Value);
            AttachCommand(optionHost, ctx, command, () => args);
            optionHost.Child = new VisualContent(optionHost, ctx.Images);
            panel.Children.Add(optionHost);
            hosts.Add((optionHost, option));
        }
        void Update()
        {
            var current = bind?.Evaluate(ctx.Resolve);
            foreach (var (h, o) in hosts)
                h.SetState("selected", current is not null && Expression.ValuesEqual(current, o.Value));
        }
        ctx.Watch(bind?.Paths ?? [], Update);
        Update();
        return panel;
    }

    public static Control Choice(LayoutNode node, ComponentHost host, BuildContext ctx) =>
        OptionGroup(node, ctx, ParseOptions(node.Get("options")), ctx.ExpressionProp(node, "bind"),
            node.GetString("command"), node.GetString("argName") ?? "value", ctx.Args(node));

    public static Control JogStep(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var values = node.GetNumberList("values");
        if (values.Count == 0) values = [0.01, 0.1, 1, 10, 100];
        var options = values.Select(v => new Option(v, TextTemplate.FormatValue(v, null), null)).ToList();
        return OptionGroup(node, ctx, options, Expression.Parse(StatePaths.JogStep), "setJogStep", "value", CommandArgs.Empty);
    }

    public static Control WcsSelector(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var systems = node.GetStringList("systems");
        if (systems.Count == 0) systems = ResponseParser.WcsNames.Take(6).ToArray();
        var options = systems.Select(s => new Option(s.ToUpperInvariant(), s.ToUpperInvariant(), null)).ToList();
        return OptionGroup(node, ctx, options, Expression.Parse(StatePaths.WcsActiveName), "selectWcs", "name", CommandArgs.Empty);
    }

    public static Control LayoutSelector(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var entries = ctx.Services.Layouts.List();
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 140 };
        foreach (var e in entries) combo.Items.Add(e.Name);
        var current = ctx.State.Get<string>(StatePaths.LayoutName);
        combo.SelectedItem = entries.FirstOrDefault(e => e.Name.Equals(current, StringComparison.OrdinalIgnoreCase))?.Name;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string name && !name.Equals(ctx.State.Get<string>(StatePaths.LayoutName), StringComparison.OrdinalIgnoreCase))
                ctx.Run("openLayout", CommandArgs.Empty.With("name", name));
        };
        return combo;
    }
}
