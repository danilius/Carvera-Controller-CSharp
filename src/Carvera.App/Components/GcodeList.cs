using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using Carvera.App.Layout;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>
/// The loaded G-code file, numbered, with a colour bar per operation. It follows the scrub position (or the
/// machine's current line while a job runs), and clicking a line scrubs the preview to it.
/// </summary>
public static class GcodeList
{
    private sealed record Row(int Number, string Text, IBrush? Operation);

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
                    new Rectangle { Width = 4, Fill = row?.Operation ?? Brushes.Transparent, Margin = new Thickness(0, 0, 6, 0) },
                    new TextBlock { Text = row?.Number.ToString(), Foreground = muted, MinWidth = 48, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0) },
                    new TextBlock { Text = row?.Text },
                },
            }),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(2, 0)), new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        List<Row> rows = [];
        var syncing = false;
        void Load()
        {
            var program = ctx.Services.Program;
            var brushes = new Dictionary<int, IBrush>();
            IBrush? BrushFor(int op) => op < 0 ? null : brushes.TryGetValue(op, out var b) ? b : brushes[op] = new SolidColorBrush(ctx.Theme.OperationColor(op));
            rows = program?.Lines.Select((l, i) => new Row(i + 1, l, BrushFor(program.OperationOfLine(i + 1)))).ToList() ?? [];
            list.ItemsSource = rows;
            Follow();
        }
        void Select(int line)
        {
            if (line < 1 || line > rows.Count) return;
            syncing = true;
            list.SelectedIndex = line - 1;
            list.ScrollIntoView(line - 1);
            syncing = false;
        }
        void Follow()
        {
            if (ctx.State.Get(StatePaths.JobPlaying, false)) Select(ctx.State.Get(StatePaths.JobLines, -1));
            else Select(ctx.State.Get(StatePaths.PreviewLine, -1));
        }
        list.SelectionChanged += (_, _) =>
        {
            if (!syncing && list.SelectedIndex >= 0) ctx.Services.SetPreviewLine(list.SelectedIndex + 1);
        };
        ctx.Services.ProgramChanged += Load;
        ctx.Track(new Detach(() => ctx.Services.ProgramChanged -= Load));
        ctx.Watch([StatePaths.JobLines, StatePaths.JobPlaying, StatePaths.PreviewLine], Follow);
        Load();
        return list;
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
