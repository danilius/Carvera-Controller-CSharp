using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.Core.Config;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>
/// The machine's settings as editable fields, grouped under the titles of the Python controller's settings list.
/// Loading, sending and restoring are the config* commands, which the layout puts on buttons.
/// </summary>
public static class MachineConfigComponent
{
    public static Control Create(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var store = ctx.Services.MachineConfig;
        var theme = ctx.Theme;
        var only = node.GetString("section");
        var list = new StackPanel { Spacing = 2 };
        var building = false;

        void Build()
        {
            building = true;
            list.Children.Clear();
            if (!store.Loaded)
            {
                list.Children.Add(new TextBlock
                {
                    Text = ctx.State.Get(StatePaths.Connected, false) ? "Press “Read from machine” to load the machine's settings." : "Connect to a machine, then read its settings.",
                    Foreground = theme.TokenBrush("textMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6),
                });
                building = false;
                return;
            }
            var currentGroup = "";
            var section = "";
            foreach (var item in store.Items)
            {
                if (item.IsTitle)
                {
                    currentGroup = item.Title;
                    section = item.Section;
                    continue;
                }
                if (item.Section is not ("Basic" or "Advanced")) continue;
                if (only is not null && !item.Section.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                if (currentGroup.Length > 0)
                {
                    list.Children.Add(new TextBlock { Text = (only is null ? item.Section + " · " : "") + currentGroup, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
                    currentGroup = "";
                }
                list.Children.Add(Row(item));
            }
            building = false;
        }

        Control Row(ConfigItem item)
        {
            var edited = store.Pending.ContainsKey(item.Key);
            var label = new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var tip = item.Description.Length > 0 ? $"{item.Description}\n\n{item.Key}" : item.Key;
            ToolTip.SetTip(label, tip);
            Control editor;
            var value = store.Current(item);
            if (item.IsBool)
            {
                var box = new CheckBox { IsChecked = value == "true", HorizontalAlignment = HorizontalAlignment.Right };
                box.IsCheckedChanged += (_, _) => { if (!building) store.Edit(item, box.IsChecked == true ? "true" : "false"); };
                editor = box;
            }
            else if (item.Options.Count > 0)
            {
                var combo = new ComboBox { ItemsSource = item.Options, SelectedItem = item.Options.Contains(value) ? value : null, Width = 160, HorizontalAlignment = HorizontalAlignment.Right };
                combo.SelectionChanged += (_, _) => { if (!building && combo.SelectedItem is string s) store.Edit(item, s); };
                editor = combo;
            }
            else
            {
                var box = new TextBox { Text = value, Width = 160, HorizontalAlignment = HorizontalAlignment.Right };
                box.LostFocus += (_, _) => { if (!building && box.Text is { } t && t != store.Current(item)) store.Edit(item, t); };
                editor = box;
            }
            ToolTip.SetTip(editor, tip);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(editor, 1);
            grid.Children.Add(label);
            grid.Children.Add(editor);
            return new Border
            {
                Child = grid, Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(4),
                Background = edited ? theme.TokenBrush("warningSoft") : Brushes.Transparent,
                Tag = item.Key,
            };
        }

        void Highlight()
        {
            foreach (var row in list.Children.OfType<Border>())
                row.Background = row.Tag is string key && store.Pending.ContainsKey(key) ? theme.TokenBrush("warningSoft") : Brushes.Transparent;
        }

        // Loading, discarding or sending rebuilds the rows; an edit only recolours them, so a field keeps focus while typing.
        void OnReset() => Dispatcher.UIThread.Post(Build);
        void OnChanged() => Dispatcher.UIThread.Post(Highlight);
        store.Reset += OnReset;
        store.Changed += OnChanged;
        ctx.Track(new Detach(() => { store.Reset -= OnReset; store.Changed -= OnChanged; }));
        ctx.Watch([StatePaths.Connected], () => { if (!store.Loaded) Build(); });
        Build();
        return new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) };
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
