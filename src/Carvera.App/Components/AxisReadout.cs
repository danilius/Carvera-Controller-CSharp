using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.Core.Expressions;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>An axis letter with work, machine and/or offset coordinates.</summary>
public static class AxisReadout
{
    public static Control Create(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var axis = (node.GetString("axis") ?? "X").ToUpperInvariant();
        var show = node.GetStringList("show").Select(s => s.ToLowerInvariant()).ToList();
        if (show.Count == 0) show = ["work", "machine"];
        var format = node.GetString("format") ?? (axis == "A" ? "0.00" : "0.000");
        var vertical = node.GetString("orientation")?.Equals("vertical", StringComparison.OrdinalIgnoreCase) == true;
        var accent = ctx.Theme.Brush(node.GetString("color")) ?? ctx.Theme.TokenBrush("axis" + axis);

        var letter = new TextBlock
        {
            Text = node.GetString("label") ?? axis,
            Foreground = accent,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Left,
            Margin = vertical ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 12, 0),
        };
        var values = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var rows = new List<(string Kind, TextBlock Text, TextBlock? Caption)>();
        foreach (var kind in show)
        {
            var primary = kind == show[0];
            var text = new TextBlock { FontFamily = ctx.Theme.MonoFontFamily, HorizontalAlignment = HorizontalAlignment.Right, FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal };
            TextBlock? caption = null;
            Control row = text;
            if (!primary)
            {
                caption = new TextBlock { Text = kind switch { "machine" => "MCS", "offset" => "OFS", _ => "WCS" }, Foreground = ctx.Theme.TokenBrush("textMuted"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Foreground = ctx.Theme.TokenBrush("textMuted");
                row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { caption, text } };
            }
            values.Children.Add(row);
            rows.Add((kind, text, caption));
        }

        Control layout;
        if (vertical) layout = new StackPanel { Children = { letter, values } };
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(values, 1);
            grid.Children.Add(letter);
            grid.Children.Add(values);
            layout = grid;
        }

        void Resize()
        {
            var baseSize = host.GetValue(TextElement.FontSizeProperty);
            letter.FontSize = baseSize * 1.9;
            foreach (var (kind, text, caption) in rows)
            {
                var primary = kind == rows[0].Kind;
                text.FontSize = primary ? baseSize * 1.9 : baseSize * 0.9;
                if (caption is not null) caption.FontSize = baseSize * 0.75;
            }
        }
        host.VisualChanged += _ => Resize();
        Resize();

        string Path(string kind) => kind switch
        {
            "machine" => StatePaths.AxisMachine(axis),
            "offset" => StatePaths.AxisOffset(axis),
            _ => StatePaths.AxisWork(axis),
        };
        var moving = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        moving.Tick += (_, _) => { moving.Stop(); host.SetState("moving", false); };
        var last = new Dictionary<string, string>();
        void Update()
        {
            var changed = false;
            foreach (var (kind, text, _) in rows)
            {
                var value = TextTemplate.FormatValue(ctx.State.Get(Path(kind)), format);
                if (last.TryGetValue(kind, out var previous) && previous != value && kind != "offset") changed = true;
                last[kind] = value;
                text.Text = value;
            }
            if (changed)
            {
                host.SetState("moving", true);
                moving.Stop();
                moving.Start();
            }
        }
        ctx.Watch(rows.Select(r => Path(r.Kind)).Append(StatePaths.Connected), Update);
        Update();

        if (node.GetString("command") is { } command)
        {
            var args = ctx.Args(node);
            ButtonComponents.AttachCommand(host, ctx, command, () => args);
        }
        return layout;
    }
}
