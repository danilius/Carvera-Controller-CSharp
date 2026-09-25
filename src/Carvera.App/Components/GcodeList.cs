using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using Carvera.App.Layout;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>The loaded G-code file, numbered, with the machine's current line selected while a job runs.</summary>
public static class GcodeList
{
    private sealed record Row(int Number, string Text);

    public static Control Create(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var fontSize = node.GetNumber("fontSize") ?? ctx.Theme.FontSize - 1;
        var muted = ctx.Theme.TokenBrush("textMuted");
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            FontFamily = ctx.Theme.MonoFontFamily,
            FontSize = fontSize,
            ItemTemplate = new FuncDataTemplate<Row>((row, _) => new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = row?.Number.ToString(), Foreground = muted, MinWidth = 48, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0) },
                    new TextBlock { Text = row?.Text },
                },
            }),
        };
        list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(4, 0)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        List<Row> rows = [];
        void Load()
        {
            rows = ctx.Services.Program?.Lines.Select((l, i) => new Row(i + 1, l)).ToList() ?? [];
            list.ItemsSource = rows;
        }
        void Follow()
        {
            var line = ctx.State.Get(StatePaths.JobLines, -1);
            if (line < 1 || line > rows.Count || !ctx.State.Get(StatePaths.JobPlaying, false)) return;
            list.SelectedIndex = line - 1;
            list.ScrollIntoView(line - 1);
        }
        ctx.Services.ProgramChanged += Load;
        ctx.Track(new Detach(() => ctx.Services.ProgramChanged -= Load));
        ctx.Watch([StatePaths.JobLines], Follow);
        Load();
        return list;
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
