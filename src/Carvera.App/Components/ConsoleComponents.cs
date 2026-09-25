using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Expressions;
using Carvera.Layout;

namespace Carvera.App.Components;

public static class ConsoleComponents
{
    public static Control Mdi(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var history = new List<string>();
        var index = 0;
        var box = new TextBox
        {
            PlaceholderText = node.GetString("placeholder") ?? "G-code or command, Enter to send",
            FontFamily = ctx.Theme.MonoFontFamily,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Send()
        {
            var line = box.Text?.Trim();
            if (string.IsNullOrEmpty(line)) return;
            ctx.Run("sendGcode", CommandArgs.Empty.With("line", line));
            if (history.Count == 0 || history[^1] != line) history.Add(line);
            index = history.Count;
            box.Text = "";
        }
        box.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter: Send(); e.Handled = true; break;
                case Key.Up when history.Count > 0: index = Math.Max(0, index - 1); box.Text = history[index]; box.CaretIndex = box.Text.Length; e.Handled = true; break;
                case Key.Down when history.Count > 0:
                    index = Math.Min(history.Count, index + 1);
                    box.Text = index < history.Count ? history[index] : "";
                    box.CaretIndex = box.Text.Length;
                    e.Handled = true;
                    break;
            }
        };
        var buttonText = node.Has("buttonText") ? node.GetString("buttonText") : "Send";
        if (string.IsNullOrEmpty(buttonText)) return box;

        var buttonNode = new LayoutNode("button", new JsonObject(), $"{node.Path}.send", []);
        var send = new ComponentHost(buttonNode, ctx, "button");
        send.SetDefaultText(TextTemplate.Parse(buttonText));
        send.MakeClickable();
        send.AddEnabledCondition(() => ctx.Commands.CanExecute("sendGcode", CommandArgs.Empty), ctx.Commands.AvailabilityPaths("sendGcode"));
        send.Clicked += Send;
        send.Child = new VisualContent(send, ctx.Images);
        var panel = new FlexPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        FlexPanel.SetSlot(box, new SlotSpec(SizeSpec.Fill, SizeSpec.Auto, VAlign: SlotAlign.Center));
        FlexPanel.SetSlot(send, new SlotSpec(SizeSpec.Auto, SizeSpec.Fill));
        panel.Children.Add(box);
        panel.Children.Add(send);
        return panel;
    }

    private sealed record Line(string Text, IBrush Brush);

    public static Control Console(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var maxLines = (int)(node.GetNumber("maxLines") ?? 500);
        var showSent = node.GetBool("showSent") ?? true;
        var timestamps = node.GetBool("showTimestamps") == true;
        var fontSize = node.GetNumber("fontSize") ?? ctx.Theme.FontSize - 1;
        var lines = new ObservableCollection<Line>();
        var theme = ctx.Theme;
        IBrush BrushFor(ConsoleEntryKind kind) => kind switch
        {
            ConsoleEntryKind.Sent => theme.TokenBrush("accent"),
            ConsoleEntryKind.Error => theme.TokenBrush("danger"),
            ConsoleEntryKind.Warning => theme.TokenBrush("warning"),
            ConsoleEntryKind.Info => theme.TokenBrush("textMuted"),
            _ => theme.TokenBrush("text"),
        };
        Line ToLine(ConsoleEntry e) => new(
            (timestamps ? e.Time.ToString("HH:mm:ss ") : "") + (e.Kind == ConsoleEntryKind.Sent ? "> " : "") + e.Text,
            BrushFor(e.Kind));

        var list = new ListBox
        {
            ItemsSource = lines,
            Background = Brushes.Transparent,
            FontFamily = theme.MonoFontFamily,
            FontSize = fontSize,
            ItemTemplate = new FuncDataTemplate<Line>((line, _) => new SelectableTextBlock
            {
                Text = line?.Text, Foreground = line?.Brush, TextWrapping = TextWrapping.Wrap,
            }),
        };
        list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(6, 1)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        var pending = new List<ConsoleEntry>();
        var scheduled = false;
        void Flush()
        {
            List<ConsoleEntry> batch;
            lock (pending) { batch = [.. pending]; pending.Clear(); scheduled = false; }
            foreach (var e in batch) lines.Add(ToLine(e));
            while (lines.Count > maxLines) lines.RemoveAt(0);
            if (lines.Count > 0) list.ScrollIntoView(lines.Count - 1);
        }
        void OnEntry(ConsoleEntry e)
        {
            if (!showSent && e.Kind == ConsoleEntryKind.Sent) return;
            lock (pending)
            {
                pending.Add(e);
                if (scheduled) return;
                scheduled = true;
            }
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
        void OnCleared() => Dispatcher.UIThread.Post(lines.Clear);
        foreach (var e in ctx.Console.Entries.TakeLast(maxLines))
            if (showSent || e.Kind != ConsoleEntryKind.Sent) lines.Add(ToLine(e));
        ctx.Console.EntryAdded += OnEntry;
        ctx.Console.Cleared += OnCleared;
        ctx.Track(new Unsubscribe(() => { ctx.Console.EntryAdded -= OnEntry; ctx.Console.Cleared -= OnCleared; }));
        return list;
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
