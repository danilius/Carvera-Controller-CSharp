using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Carvera.Editor.Views;

public sealed record PickItem(string Value, string? Detail = null, string? Group = null, Func<Control?>? Icon = null);

/// <summary>A searchable list in a flyout: used for commands, state paths, built-in pictures and colour tokens.</summary>
public static class Pickers
{
    public static void Show(Control anchor, string title, IReadOnlyList<PickItem> items, Action<string> chosen, string? current = null, double width = 380)
    {
        var search = new TextBox { PlaceholderText = "Search…", Margin = new Thickness(0, 0, 0, 6) };
        var list = new ListBox { MaxHeight = 340, MinHeight = 120, BorderThickness = new Thickness(0) };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Content = new StackPanel
        {
            Width = width, Spacing = 2,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) }, search, list },
        };

        void Fill()
        {
            var filter = search.Text?.Trim() ?? "";
            list.Items.Clear();
            string? lastGroup = null;
            foreach (var item in items.Where(i => filter.Length == 0 || i.Value.Contains(filter, StringComparison.OrdinalIgnoreCase) || (i.Detail?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) || (i.Group?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                if (item.Group != lastGroup && item.Group is not null)
                {
                    list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = item.Group.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#64748B") }, IsEnabled = false, Padding = new Thickness(6, 6, 6, 0) });
                    lastGroup = item.Group;
                }
                var text = new StackPanel { Children = { new TextBlock { Text = item.Value, FontWeight = item.Value == current ? FontWeight.Bold : FontWeight.Normal } } };
                if (item.Detail is { Length: > 0 }) text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 11, Foreground = Brush.Parse("#64748B"), TextWrapping = TextWrapping.Wrap, MaxWidth = width - 50 });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (item.Icon?.Invoke() is { } icon) row.Children.Add(icon);
                row.Children.Add(text);
                list.Items.Add(new ListBoxItem { Content = row, Tag = item.Value, Padding = new Thickness(6, 3) });
            }
        }

        void Choose()
        {
            if (list.SelectedItem is ListBoxItem { Tag: string value })
            {
                flyout.Hide();
                chosen(value);
            }
        }
        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.ItemCount > 0) { list.Focus(); if (list.SelectedIndex < 0) list.SelectedIndex = 1 < list.ItemCount && list.Items[0] is ListBoxItem { IsEnabled: false } ? 1 : 0; }
            else if (e.Key == Key.Enter && list.ItemCount > 0)
            {
                if (list.SelectedIndex < 0) list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is string);
                Choose();
            }
        };
        list.DoubleTapped += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Choose(); };
        Fill();
        flyout.Opened += (_, _) => search.Focus();
        flyout.ShowAt(anchor);
    }
}
