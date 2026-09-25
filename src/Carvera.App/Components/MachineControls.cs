using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.Core.Commands;
using Carvera.Core.Expressions;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

public static class MachineControls
{
    /// <summary>A slider that shows a bound value and sends a command when the user moves it.</summary>
    public static Control Slider(LayoutNode node, ComponentHost host, BuildContext ctx) =>
        BuildSlider(ctx, ctx.ExpressionProp(node, "bind"), node.GetString("command"), node.GetString("argName") ?? "value", ctx.Args(node),
            node.GetNumber("min") ?? 0, node.GetNumber("max") ?? 100, node.GetNumber("step") ?? 1,
            node.GetString("orientation")?.Equals("vertical", StringComparison.OrdinalIgnoreCase) == true, host);

    private static Slider BuildSlider(BuildContext ctx, Expression? bind, string? command, string argName, CommandArgs args,
        double min, double max, double step, bool vertical, ComponentHost host)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10,
            TickFrequency = step, IsSnapToTickEnabled = step > 0,
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var updating = false;
        var dragging = false;
        void Update()
        {
            if (dragging) return;
            updating = true;
            slider.Value = Expression.ToNumber(bind?.Evaluate(ctx.Resolve)) is { } v ? Math.Clamp(v, min, max) : min;
            updating = false;
        }
        // Coalesce rapid changes so a drag sends a handful of commands, not hundreds.
        var send = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        send.Tick += (_, _) =>
        {
            send.Stop();
            if (command is not null) ctx.Run(command, args.With(argName, slider.Value));
        };
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            send.Stop();
            send.Start();
        };
        slider.AddHandler(InputElement.PointerPressedEvent, (_, _) => dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { dragging = false; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (command is not null)
            host.AddEnabledCondition(() => ctx.Commands.CanExecute(command, args), ctx.Commands.AvailabilityPaths(command));
        ctx.Watch(bind?.Paths ?? [], Update);
        Update();
        return slider;
    }

    /// <summary>Copies a visuals object, adding a default to its "normal" block unless the layout set one.</summary>
    private static JsonObject WithNormal(JsonObject? visuals, string property, JsonNode value)
    {
        var copy = (JsonObject?)visuals?.DeepClone() ?? new JsonObject();
        var normal = copy["normal"] as JsonObject ?? new JsonObject();
        normal[property] ??= value;
        copy["normal"] = normal;
        return copy;
    }

    private static ComponentHost SmallButton(BuildContext ctx, LayoutNode parent, string key, string text, string command, CommandArgs args, JsonObject? visuals, string? image = null, string visualType = "button")
    {
        var node = new LayoutNode("button", new JsonObject(), $"{parent.Path}.{key}", []);
        var v = (JsonObject?)visuals?.DeepClone() ?? new JsonObject();
        if (image is not null)
        {
            var normal = v["normal"] as JsonObject ?? new JsonObject();
            normal["image"] ??= image;
            v["normal"] = normal;
        }
        var host = new ComponentHost(node, ctx, visualType, v);
        host.SetDefaultText(TextTemplate.Parse(text));
        ButtonComponents.AttachCommand(host, ctx, command, () => args);
        host.Child = new VisualContent(host, ctx.Images);
        return host;
    }

    public static Control Override(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var kind = (node.GetString("kind") ?? "feed").ToLowerInvariant();
        var (path, command, defaultLabel, defaultMax) = kind switch
        {
            "spindle" => (StatePaths.SpindleOverride, "spindleOverride", "Spindle", 300.0),
            "laser" => (StatePaths.LaserScale, "laserScale", "Laser", 200.0),
            _ => (StatePaths.FeedOverride, "feedOverride", "Feed", 300.0),
        };
        var increment = node.GetNumber("increment") ?? 10;
        var label = ctx.TemplateProp(node, "label") ?? TextTemplate.Parse(defaultLabel);
        var buttonVisuals = node.Get("buttonVisuals") as JsonObject;

        var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = ctx.Theme.TokenBrush("textMuted"), MinWidth = 56 };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = ctx.Theme.MonoFontFamily, FontWeight = FontWeight.SemiBold, MinWidth = 48, TextAlignment = TextAlignment.Right };
        void Update()
        {
            caption.Text = label.Render(ctx.Resolve);
            value.Text = TextTemplate.FormatValue(ctx.State.Get(path), "0") + "%";
        }
        ctx.Watch(label.Paths.Append(path), Update);
        Update();

        var slider = BuildSlider(ctx, Expression.Parse(path), command, "value", CommandArgs.Empty,
            node.GetNumber("min") ?? 10, node.GetNumber("max") ?? defaultMax, 1, false, host);
        var iconVisuals = WithNormal(buttonVisuals, "padding", "4");
        var minus = SmallButton(ctx, node, "minus", "", command, CommandArgs.Empty.With("delta", -increment), iconVisuals, "builtin:minus");
        var plus = SmallButton(ctx, node, "plus", "", command, CommandArgs.Empty.With("delta", increment), iconVisuals, "builtin:plus");
        var reset = SmallButton(ctx, node, "reset", "", command, CommandArgs.Empty.With("value", 100), iconVisuals, "builtin:reset");
        ToolTip.SetTip(reset, "Back to 100%");

        var vertical = node.GetString("orientation")?.Equals("vertical", StringComparison.OrdinalIgnoreCase) == true;
        var panel = new FlexPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Add(Control c, SizeSpec width)
        {
            FlexPanel.SetSlot(c, new SlotSpec(width, SizeSpec.Auto, VAlign: SlotAlign.Center));
            panel.Children.Add(c);
        }
        if (!vertical) Add(caption, SizeSpec.Auto);
        Add(minus, SizeSpec.Pixels(30));
        Add(slider, SizeSpec.Fill);
        Add(plus, SizeSpec.Pixels(30));
        Add(value, SizeSpec.Auto);
        Add(reset, SizeSpec.Pixels(30));
        if (!vertical) return panel;
        return new StackPanel { Spacing = 4, Children = { caption, panel } };
    }

    public static Control JogPad(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var axes = node.GetStringList("axes").Select(a => a.ToUpperInvariant()).ToHashSet();
        if (axes.Count == 0) axes = ["X", "Y", "Z"];
        var diagonals = node.GetBool("diagonals") == true;
        var size = node.GetNumber("buttonSize");
        var spacing = node.GetNumber("spacing") ?? 6;
        var buttonVisuals = node.Get("buttonVisuals") as JsonObject;
        var images = node.Get("images") as JsonObject;

        var grid = new Grid { RowSpacing = spacing, ColumnSpacing = spacing };
        GridLength Track() => size is { } s ? new GridLength(s) : new GridLength(1, GridUnitType.Star);
        var hasXY = axes.Contains("X") || axes.Contains("Y");
        var columns = 0;
        if (hasXY) columns = 3;
        var zColumn = axes.Contains("Z") ? columns++ : -1;
        var aColumn = axes.Contains("A") ? columns++ : -1;
        for (var c = 0; c < columns; c++)
        {
            // a narrow gap before the Z/A columns separates them from the XY pad
            if ((c == zColumn || c == aColumn) && hasXY) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(spacing * 2)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(Track()));
        }
        for (var r = 0; r < 3; r++) grid.RowDefinitions.Add(new RowDefinition(Track()));
        int Col(int logical) => logical + (hasXY && logical >= 3 ? logical - 2 : 0);

        void Button(string key, string label, string icon, int row, int column)
        {
            var image = images?[key]?.ToString() ?? icon;
            var visuals = WithNormal(buttonVisuals, "imagePlacement", "top");
            var b = SmallButton(ctx, node, key, label, "jog", CommandArgs.Empty.With("axis", key), visuals, image, "jogButton");
            Grid.SetRow(b, row);
            Grid.SetColumn(b, Col(column));
            grid.Children.Add(b);
        }

        if (hasXY)
        {
            if (axes.Contains("Y")) { Button("Y+", "Y+", "builtin:arrow-up", 0, 1); Button("Y-", "Y−", "builtin:arrow-down", 2, 1); }
            if (axes.Contains("X")) { Button("X-", "X−", "builtin:arrow-left", 1, 0); Button("X+", "X+", "builtin:arrow-right", 1, 2); }
            if (diagonals && axes.Contains("X") && axes.Contains("Y"))
            {
                Button("X-Y+", "", "builtin:arrow-up-left", 0, 0);
                Button("X+Y+", "", "builtin:arrow-up-right", 0, 2);
                Button("X-Y-", "", "builtin:arrow-down-left", 2, 0);
                Button("X+Y-", "", "builtin:arrow-down-right", 2, 2);
            }
            var step = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = ctx.Theme.TokenBrush("textMuted") };
            void UpdateStep() => step.Text = $"{TextTemplate.FormatValue(ctx.State.Get(StatePaths.JogStep), null)}\nmm";
            ctx.Watch([StatePaths.JogStep], UpdateStep);
            UpdateStep();
            Grid.SetRow(step, 1);
            Grid.SetColumn(step, 1);
            grid.Children.Add(step);
        }
        void AxisColumn(string axis, int column)
        {
            Button($"{axis}+", $"{axis}+", "builtin:arrow-up", 0, column);
            Button($"{axis}-", $"{axis}−", "builtin:arrow-down", 2, column);
            var caption = new TextBlock { Text = axis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold, Foreground = ctx.Theme.TokenBrush("axis" + axis) };
            Grid.SetRow(caption, 1);
            Grid.SetColumn(caption, Col(column));
            grid.Children.Add(caption);
        }
        if (zColumn >= 0) AxisColumn("Z", zColumn);
        if (aColumn >= 0) AxisColumn("A", aColumn);
        return grid;
    }
}
