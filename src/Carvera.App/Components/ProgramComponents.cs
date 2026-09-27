using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Layout;
using Carvera.Core.Commands;
using Carvera.Core.Expressions;
using Carvera.Core.Gcode;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>Components that work on the loaded G-code: scrubber, operations and tools.</summary>
public static class ProgramComponents
{
    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private static void OnProgramChanged(BuildContext ctx, Action handler)
    {
        ctx.Services.ProgramChanged += handler;
        ctx.Track(new Detach(() => ctx.Services.ProgramChanged -= handler));
    }

    private static ComponentHost IconButton(BuildContext ctx, LayoutNode parent, string key, string icon, string tooltip, Action click, JsonObject? visuals)
    {
        var v = (JsonObject?)visuals?.DeepClone() ?? new JsonObject();
        var normal = v["normal"] as JsonObject ?? new JsonObject();
        normal["image"] ??= icon;
        normal["padding"] ??= "4";
        v["normal"] = normal;
        var host = new ComponentHost(new LayoutNode("button", new JsonObject(), $"{parent.Path}.{key}", []), ctx, "button", v);
        host.MakeClickable();
        host.Clicked += click;
        host.Child = new VisualContent(host, ctx.Images);
        ToolTip.SetTip(host, tooltip);
        return host;
    }

    // ------------------------------------------------------------------ scrubber

    /// <summary>
    /// Scrubs through the loaded program: a slider over the path, step and jump buttons, play/pause
    /// animation, and the G-code line at the scrub position.
    /// </summary>
    public static Control Scrubber(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var services = ctx.Services;
        var speed = Math.Max(1, node.GetNumber("speed") ?? 150); // segments per second while playing
        var showLine = node.GetBool("showLine") ?? true;
        var buttonVisuals = node.Get("buttonVisuals") as JsonObject;

        var slider = new Slider { Minimum = 0, Maximum = 0, SmallChange = 1, LargeChange = 50, VerticalAlignment = VerticalAlignment.Center };
        var position = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = ctx.Theme.MonoFontFamily, Foreground = ctx.Theme.TokenBrush("textMuted"), MinWidth = 120, TextAlignment = TextAlignment.Right };
        var lineText = new TextBlock { FontFamily = ctx.Theme.MonoFontFamily, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ctx.Theme.TokenBrush("text"), Margin = new Thickness(2, 4, 0, 0) };
        var operationText = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        var carry = 0.0;
        ComponentHost? play = null;
        void SetPlaying(bool playing)
        {
            if (playing) timer.Start(); else timer.Stop();
            ctx.State.Set(StatePaths.PreviewPlaying, playing);
        }
        timer.Tick += (_, _) =>
        {
            var program = services.Program;
            if (program is null || program.Segments.Count == 0) { SetPlaying(false); return; }
            carry += speed * timer.Interval.TotalSeconds;
            var step = (int)carry;
            carry -= step;
            var current = ctx.State.Get(StatePaths.PreviewSegment, -1);
            var next = Math.Min(program.Segments.Count - 1, (current < 0 ? 0 : current) + step);
            services.SetPreviewSegment(next);
            if (next >= program.Segments.Count - 1) SetPlaying(false);
        };
        ctx.Track(new Detach(() => { timer.Stop(); ctx.State.Set(StatePaths.PreviewPlaying, false); }));

        void Step(int delta)
        {
            SetPlaying(false);
            var count = services.Program?.Segments.Count ?? 0;
            if (count == 0) return;
            var current = ctx.State.Get(StatePaths.PreviewSegment, -1);
            if (current < 0) current = delta >= 0 ? -1 : count;
            services.SetPreviewSegment(Math.Clamp(current + delta, 0, count - 1));
        }
        void JumpOperation(int direction)
        {
            SetPlaying(false);
            var program = services.Program;
            if (program is null || program.Segments.Count == 0) return;
            var current = ctx.State.Get(StatePaths.PreviewSegment, -1);
            var line = current >= 0 ? program.Segments[current].Line : direction > 0 ? 0 : int.MaxValue;
            var op = current >= 0 ? program.OperationOfLine(line) : -1;
            // Jump to the end of this operation (forward) or the end of the previous one (back); ends are where each operation is complete.
            var ends = program.Operations.Select(o => program.LastSegmentAtOrBefore(o.EndLine + 1)).Where(s => s >= 0).Distinct().ToList();
            var target = direction > 0 ? ends.FirstOrDefault(e => e > current, program.Segments.Count - 1) : ends.LastOrDefault(e => e < current, 0);
            services.SetPreviewSegment(target);
        }

        var start = IconButton(ctx, node, "start", "builtin:skip-back", "Back to the end of the previous operation", () => JumpOperation(-1), buttonVisuals);
        var back = IconButton(ctx, node, "back", "builtin:step-back", "Step back (hold Shift for 10)", () => Step(-1), buttonVisuals);
        play = IconButton(ctx, node, "play", "builtin:play", "Play / pause the preview", () =>
        {
            var program = services.Program;
            if (program is null) return;
            if (!timer.IsEnabled && ctx.State.Get(StatePaths.PreviewSegment, -1) >= program.Segments.Count - 1) services.SetPreviewSegment(0);
            SetPlaying(!timer.IsEnabled);
        }, buttonVisuals);
        var forward = IconButton(ctx, node, "forward", "builtin:step-forward", "Step forward", () => Step(1), buttonVisuals);
        var end = IconButton(ctx, node, "end", "builtin:skip-forward", "Forward to the end of this operation", () => JumpOperation(1), buttonVisuals);
        var clear = IconButton(ctx, node, "clear", "builtin:close", "Show the whole program", () => { SetPlaying(false); services.SetPreviewSegment(-1); }, buttonVisuals);

        var updating = false;
        slider.ValueChanged += (_, e) =>
        {
            if (updating) return;
            SetPlaying(false);
            services.SetPreviewSegment((int)Math.Round(e.NewValue));
        };

        void Update()
        {
            var program = services.Program;
            var count = program?.Segments.Count ?? 0;
            var segment = ctx.State.Get(StatePaths.PreviewSegment, -1);
            updating = true;
            slider.Maximum = Math.Max(0, count - 1);
            slider.Value = segment >= 0 ? segment : slider.Maximum;
            updating = false;
            slider.IsEnabled = count > 0;
            var playing = ctx.State.Get(StatePaths.PreviewPlaying, false);
            play.SetStates(playing ? ["active"] : [], ["active"]);
            if (program is null || count == 0)
            {
                position.Text = "";
                lineText.Text = "Open a G-code file to scrub through it.";
                operationText.Text = "";
                return;
            }
            var line = segment >= 0 ? program.Segments[segment].Line : program.Lines.Count;
            position.Text = segment >= 0 ? $"line {line} / {program.Lines.Count}" : $"all {program.Lines.Count} lines";
            lineText.Text = segment >= 0 && line - 1 < program.Lines.Count ? program.Lines[line - 1].Trim() : "";
            var op = program.OperationOfLine(line);
            operationText.Text = op >= 0 ? program.Operations[op].Name + (program.Operations[op].Tool is { } t ? $"  ·  T{t}" : "") : "";
            operationText.Foreground = op >= 0 ? new SolidColorBrush(ctx.Theme.OperationColor(op)) : ctx.Theme.TokenBrush("textMuted");
        }
        ctx.Watch([StatePaths.PreviewSegment, StatePaths.PreviewPlaying], Update);
        OnProgramChanged(ctx, Update);
        Update();

        var row = new FlexPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void Add(Control c, SizeSpec width)
        {
            FlexPanel.SetSlot(c, new SlotSpec(width, SizeSpec.Pixels(30), VAlign: SlotAlign.Center));
            row.Children.Add(c);
        }
        foreach (var b in new[] { start, back, play, forward, end }) Add(b, SizeSpec.Pixels(32));
        Add(slider, SizeSpec.Fill);
        Add(position, SizeSpec.Auto);
        Add(clear, SizeSpec.Pixels(32));

        var info = new DockPanel();
        DockPanel.SetDock(operationText, Dock.Left);
        operationText.Margin = new Thickness(2, 4, 12, 0);
        info.Children.Add(operationText);
        info.Children.Add(lineText);
        info.IsVisible = showLine;
        return new StackPanel { Spacing = 0, Children = { row, info } };
    }

    // ------------------------------------------------------------------ operations

    /// <summary>Each operation with its colour, line range and tool, which can be changed.</summary>
    public static Control Operations(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var services = ctx.Services;
        var list = new StackPanel { Spacing = 4 };
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var extraTools = node.GetNumberList("tools").Select(t => (int)t).ToList();
        if (extraTools.Count == 0) extraTools = [1, 2, 3, 4, 5, 6];
        var rows = new List<(Border Row, int Index)>();

        void Highlight()
        {
            var selected = ctx.State.Get(StatePaths.PreviewOperation, -1);
            var line = ctx.State.Get(StatePaths.PreviewLine, -1);
            var current = line >= 0 ? services.Program?.OperationOfLine(line) ?? -1 : -1;
            foreach (var (row, index) in rows)
            {
                row.Background = index == selected ? ctx.Theme.TokenBrush("accentSoft") : index == current ? ctx.Theme.TokenBrush("hover") : Brushes.Transparent;
                row.BorderBrush = index == selected ? ctx.Theme.TokenBrush("accent") : Brushes.Transparent;
            }
        }

        void Build()
        {
            list.Children.Clear();
            rows.Clear();
            var program = services.Program;
            if (program is null || program.Operations.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = program is null ? "No G-code file loaded." : "No operations found.", Foreground = ctx.Theme.TokenBrush("textMuted"), Margin = new Thickness(4) });
                return;
            }
            var toolNumbers = program.Tools.Select(t => t.Number).Concat(extraTools).Distinct().Order().ToList();
            foreach (var op in program.Operations)
            {
                var index = op.Index;
                var swatch = new Rectangle { Width = 6, Fill = new SolidColorBrush(ctx.Theme.OperationColor(index)), RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 8, 0) };
                var name = new TextBlock { Text = op.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                var detail = new TextBlock
                {
                    Text = $"lines {op.StartLine + 1}–{op.EndLine + 1}" + (op.ToolChangeLine < 0 && op.Tool is not null ? "  ·  same tool as before" : ""),
                    Foreground = ctx.Theme.TokenBrush("textMuted"), FontSize = ctx.Theme.FontSize - 2,
                };
                var tools = new ComboBox { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
                foreach (var n in toolNumbers)
                {
                    var t = program.Tools.FirstOrDefault(x => x.Number == n);
                    tools.Items.Add(new ComboBoxItem { Content = t is null || t.Description.Length == 0 ? $"T{n}" : $"T{n}  {t.Description}", Tag = n });
                }
                tools.SelectedItem = tools.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag! == op.Tool);
                tools.SelectionChanged += (_, _) =>
                {
                    if (tools.SelectedItem is ComboBoxItem { Tag: int tool } && tool != op.Tool)
                        ctx.Run("setOperationTool", CommandArgs.Empty.With("operation", index).With("tool", tool));
                };
                ToolTip.SetTip(tools, "Tool for this operation. Changing it edits the G-code in memory; save the file to keep it.");

                // Name on its own line so long operation names stay readable; tool selector and lines below.
                tools.HorizontalAlignment = HorizontalAlignment.Stretch;
                tools.Margin = new Thickness(0, 4, 0, 2);
                var text = new StackPanel { Children = { name, tools, detail }, VerticalAlignment = VerticalAlignment.Center };
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Grid.SetColumn(text, 1);
                grid.Children.Add(swatch);
                grid.Children.Add(text);
                var row = new Border { Child = grid, Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) };
                ToolTip.SetTip(row, "Click to show only this operation; double-click to scrub to its end.");
                row.PointerPressed += (_, e) =>
                {
                    if (e.Source is Visual v && v.FindAncestorOfType<ComboBox>(includeSelf: true) is not null) return;
                    if (e.ClickCount == 2) services.SetPreviewLine(op.EndLine + 1);
                    else services.SelectOperation(ctx.State.Get(StatePaths.PreviewOperation, -1) == index ? -1 : index);
                };
                rows.Add((row, index));
                list.Children.Add(row);
            }
            Highlight();
        }

        OnProgramChanged(ctx, Build);
        ctx.Watch([StatePaths.PreviewOperation, StatePaths.PreviewLine], Highlight);
        Build();
        return scroll;
    }

    // ------------------------------------------------------------------ tools

    /// <summary>The tools the G-code uses, with diameter, type, the operations using them and which is in the spindle.</summary>
    public static Control Tools(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var services = ctx.Services;
        var list = new StackPanel { Spacing = 4 };
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var badges = new List<(Border Badge, int Number)>();

        void Highlight()
        {
            var inSpindle = ctx.State.Get(StatePaths.ToolCurrent, -1);
            var line = ctx.State.Get(StatePaths.PreviewLine, -1);
            var program = services.Program;
            var op = line >= 0 && program is not null ? program.OperationOfLine(line) : -1;
            var previewTool = op >= 0 ? program!.Operations[op].Tool : null;
            foreach (var (badge, number) in badges)
            {
                var loaded = number == inSpindle && ctx.State.Get(StatePaths.Connected, false);
                badge.Background = loaded ? ctx.Theme.TokenBrush("success") : number == previewTool ? ctx.Theme.TokenBrush("accent") : ctx.Theme.TokenBrush("surfaceAlt");
                ((TextBlock)badge.Child!).Foreground = loaded || number == previewTool ? Brushes.White : ctx.Theme.TokenBrush("text");
                ToolTip.SetTip(badge, loaded ? "In the spindle" : number == previewTool ? "Used at the scrub position" : "");
            }
        }

        void Build()
        {
            list.Children.Clear();
            badges.Clear();
            var program = services.Program;
            if (program is null || program.Tools.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = program is null ? "No G-code file loaded." : "The file does not name any tools.", Foreground = ctx.Theme.TokenBrush("textMuted"), Margin = new Thickness(4) });
                return;
            }
            foreach (var tool in program.Tools)
            {
                var users = program.Operations.Where(o => o.Tool == tool.Number).ToList();
                var badge = new Border
                {
                    Width = 42, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = $"T{tool.Number}", FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                badges.Add((badge, tool.Number));
                var facts = new List<string>();
                if (tool.Diameter is { } d) facts.Add($"Ø{d.ToString("0.###", CultureInfo.InvariantCulture)} mm");
                if (!string.IsNullOrWhiteSpace(tool.Type)) facts.Add(tool.Type!);
                var text = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = tool.Description.Length > 0 ? tool.Description : $"Tool {tool.Number}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = string.Join("  ·  ", facts), Foreground = ctx.Theme.TokenBrush("textMuted"), FontSize = ctx.Theme.FontSize - 1, IsVisible = facts.Count > 0 },
                    },
                };
                var chips = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
                foreach (var op in users)
                    chips.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(ctx.Theme.OperationColor(op.Index), 0.15),
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1), Margin = new Thickness(0, 0, 4, 2),
                        Child = new TextBlock { Text = op.Name, FontSize = ctx.Theme.FontSize - 2, Foreground = new SolidColorBrush(ctx.Theme.OperationColor(op.Index)) },
                    });
                if (users.Count == 0) chips.Children.Add(new TextBlock { Text = "listed but not used", FontSize = ctx.Theme.FontSize - 2, Foreground = ctx.Theme.TokenBrush("textMuted") });
                text.Children.Add(chips);
                var dock = new DockPanel { Margin = new Thickness(4) };
                DockPanel.SetDock(badge, Dock.Left);
                dock.Children.Add(badge);
                dock.Children.Add(text);
                list.Children.Add(dock);
            }
            Highlight();
        }

        OnProgramChanged(ctx, Build);
        ctx.Watch([StatePaths.ToolCurrent, StatePaths.Connected, StatePaths.PreviewLine], Highlight);
        Build();
        return scroll;
    }
}
