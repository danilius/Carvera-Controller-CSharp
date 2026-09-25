using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Carvera.App.Layout;
using Carvera.Core.Expressions;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

public static class BasicComponents
{
    public static Control Text(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        host.SetDefaultText(ctx.TemplateProp(node, "text"));
        return new VisualContent(host, ctx.Images, wrap: node.GetBool("wrap") == true);
    }

    public static Control Value(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var bind = ctx.ExpressionProp(node, "bind");
        var format = node.GetString("format");
        var unit = node.GetString("unit");
        var label = ctx.TemplateProp(node, "label");
        var vertical = node.GetString("orientation")?.Equals("vertical", StringComparison.OrdinalIgnoreCase) == true;

        var caption = new TextBlock { Foreground = ctx.Theme.TokenBrush("textMuted"), VerticalAlignment = VerticalAlignment.Center, FontSize = ctx.Theme.FontSize - 1 };
        var value = new TextBlock { FontFamily = ctx.Theme.MonoFontFamily, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        var unitText = new TextBlock { Text = unit, Foreground = ctx.Theme.TokenBrush("textMuted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 0), IsVisible = !string.IsNullOrEmpty(unit) };
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { value, unitText } };
        var panel = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = vertical ? 0 : 8, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(caption);
        panel.Children.Add(valueRow);

        void Update()
        {
            caption.Text = label?.Render(ctx.Resolve);
            caption.IsVisible = label is not null;
            value.Text = TextTemplate.FormatValue(bind?.Evaluate(ctx.Resolve), format);
        }
        ctx.Watch((bind?.Paths ?? []).Concat(label?.Paths ?? []), Update);
        Update();
        return panel;
    }

    private static readonly string[] StatusStates = ["idle", "run", "hold", "alarm", "home", "tool", "wait", "pause", "sleep", "disable", "disconnected"];

    public static Control MachineStatus(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        host.SetDefaultText(ctx.TemplateProp(node, "text") ?? TextTemplate.Parse("{machine.state}"));
        void Update()
        {
            var state = ctx.State.Get<string>(StatePaths.MachineState)?.ToLowerInvariant() ?? "n/a";
            if (state == "n/a") state = "disconnected";
            host.SetStates(StatusStates.Contains(state) ? [state] : [], StatusStates);
        }
        ctx.Watch([StatePaths.MachineState], Update);
        Update();
        var content = new VisualContent(host, ctx.Images);
        content.TextBlock.TextAlignment = TextAlignment.Center;
        return content;
    }

    public static Control Indicator(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var bind = ctx.ExpressionProp(node, "bind");
        var text = ctx.TemplateProp(node, "text");
        var onText = ctx.TemplateProp(node, "onText");
        var offText = ctx.TemplateProp(node, "offText");
        var lampSize = node.GetNumber("lampSize") ?? 12;
        var on = false;

        Control? Lamp() => lampSize <= 0 ? null : new Ellipse
        {
            Width = lampSize, Height = lampSize,
            Fill = ctx.Theme.TokenBrush(on ? "lampOn" : "lampOff"),
            Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), StrokeThickness = 1,
        };

        void Update()
        {
            on = bind?.EvaluateBool(ctx.Resolve) ?? false;
            host.SetDefaultText(on ? onText ?? text : offText ?? text);
            host.SetStates(on ? ["on"] : ["off"], ["on", "off"]);
        }
        ctx.Watch(bind?.Paths ?? [], Update);
        Update();
        // The lamp is rebuilt with the current colour whenever the image key changes (state change).
        return new VisualContent(host, ctx.Images, Lamp);
    }

    public static Control Progress(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var bind = ctx.ExpressionProp(node, "bind") ?? Expression.Parse(StatePaths.JobPercent);
        var text = ctx.TemplateProp(node, "text");
        var bar = new ProgressBar
        {
            Minimum = node.GetNumber("min") ?? 0,
            Maximum = node.GetNumber("max") ?? 100,
            MinHeight = 6,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(4),
            Background = ctx.Theme.TokenBrush("surfaceAlt"),
            Foreground = ctx.Theme.Brush(node.GetString("barColor")) ?? ctx.Theme.TokenBrush("accent"),
        };
        var label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        void Update()
        {
            var value = Expression.ToNumber(bind.Evaluate(ctx.Resolve));
            bar.Value = value is { } v && v >= 0 ? v : bar.Minimum;
            host.SetState("active", value is > 0);
            label.Text = text?.Render(ctx.Resolve);
            label.IsVisible = text is not null;
        }
        ctx.Watch(bind.Paths.Concat(text?.Paths ?? []), Update);
        Update();
        return new Panel { Children = { bar, label } };
    }

    public static Control Image(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var source = node.GetString("source");
        var stretch = node.GetString("stretch")?.ToLowerInvariant() switch
        {
            "none" => Stretch.None,
            "fill" => Stretch.Fill,
            "uniformtofill" => Stretch.UniformToFill,
            _ => Stretch.Uniform,
        };
        return new VisualContent(host, ctx.Images, () => ctx.Images.CreateControl(source, null, null, null, stretch));
    }
}
