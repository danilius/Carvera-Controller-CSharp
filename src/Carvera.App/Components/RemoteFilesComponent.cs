using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.Core.Commands;
using Carvera.Core.State;
using Carvera.Core.Transfer;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>
/// The files and folders on the machine's SD card: the folder path, then one row per entry with its size and date.
/// Click selects; double-click opens a folder. The buttons live in the layout and use the remote* commands.
/// </summary>
public static class RemoteFilesComponent
{
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public static Control Create(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var browser = ctx.Services.Remote;
        var theme = ctx.Theme;
        var path = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { FontSize = theme.FontSize - 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var header = new DockPanel { Margin = new Thickness(6, 4, 6, 6), LastChildFill = true };
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        header.Children.Add(path);
        var list = new StackPanel { Spacing = 1 };
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(scroll);
        var rows = new List<(Border Row, string Path)>();

        void Highlight()
        {
            var selected = ctx.State.Get<string>(StatePaths.RemoteSelected);
            foreach (var (row, entryPath) in rows)
            {
                var isSelected = entryPath == selected;
                row.Background = isSelected ? theme.TokenBrush("accentSoft") : Brushes.Transparent;
                row.BorderBrush = isSelected ? theme.TokenBrush("accent") : Brushes.Transparent;
            }
        }

        void Build()
        {
            list.Children.Clear();
            rows.Clear();
            var connected = ctx.State.Get(StatePaths.Connected, false);
            var loading = ctx.State.Get(StatePaths.RemoteLoading, false);
            var error = ctx.State.Get<string>(StatePaths.RemoteError);
            path.Text = browser.Directory;
            path.Foreground = theme.TokenBrush("text");
            status.Text = error ?? (loading ? "Loading…" : connected ? $"{browser.Entries.Count} item{(browser.Entries.Count == 1 ? "" : "s")}" : "");
            status.Foreground = error is not null ? theme.TokenBrush("danger") : theme.TokenBrush("textMuted");
            ToolTip.SetTip(status, error);

            if (!connected)
            {
                list.Children.Add(Note("Connect to a machine to see the files on its SD card."));
                return;
            }
            if (browser.Entries.Count == 0)
            {
                if (!loading && error is null) list.Children.Add(Note("This folder is empty."));
                return;
            }
            foreach (var entry in browser.Entries) list.Children.Add(Row(entry));
            Highlight();
        }

        TextBlock Note(string text) => new() { Text = text, Foreground = theme.TokenBrush("textMuted"), Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };

        Border Row(RemoteEntry entry)
        {
            var icon = new Avalonia.Controls.Shapes.Path
            {
                Data = Icons(entry.IsDirectory), Fill = entry.IsDirectory ? theme.TokenBrush("accent") : theme.TokenBrush("textMuted"),
                Width = 16, Height = 16, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var name = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var size = new TextBlock
            {
                Text = entry.IsDirectory ? "" : FormatSize(entry.Size), Foreground = theme.TokenBrush("textMuted"), FontSize = theme.FontSize - 1,
                Width = 74, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            var date = new TextBlock
            {
                Text = entry.Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "", Foreground = theme.TokenBrush("textMuted"), FontSize = theme.FontSize - 1,
                Width = 120, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(size, 2);
            Grid.SetColumn(date, 3);
            grid.Children.Add(icon);
            grid.Children.Add(name);
            grid.Children.Add(size);
            grid.Children.Add(date);
            var row = new Border { Child = grid, Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(row, entry.IsDirectory ? "Click to select; double-click to open" : "Click to select");
            row.PointerPressed += (_, e) =>
            {
                ctx.Run("remoteSelect", CommandArgs.Empty.With("path", entry.Path));
                if (e.ClickCount == 2 && entry.IsDirectory) ctx.Run("remoteOpen", CommandArgs.Empty.With("path", entry.Path));
            };
            rows.Add((row, entry.Path));
            return row;
        }

        Geometry Icons(bool folder) => Geometry.Parse(folder ? Layout.Icons.PathData["folder"] : Layout.Icons.PathData["file"]);

        void OnBrowserChanged() => Dispatcher.UIThread.Post(Build);
        browser.Changed += OnBrowserChanged;
        ctx.Track(new Detach(() => browser.Changed -= OnBrowserChanged));
        ctx.Watch([StatePaths.Connected, StatePaths.RemoteSelected], () => { Build(); });
        Build();
        return root;
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
