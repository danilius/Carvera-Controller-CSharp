using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Carvera.App.Shell;

public static class Dialogs
{
    /// <summary>
    /// Lets the user pick one of <paramref name="names"/>. Returns the choice, or null when cancelled.
    /// Enter or double-click chooses; Esc cancels.
    /// </summary>
    public static async Task<string?> PickAsync(Window owner, string title, IReadOnlyList<string> names, string? current)
    {
        string? result = null;
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
        };
        var list = new ListBox { MaxHeight = 360 };
        foreach (var name in names) list.Items.Add(name);
        list.SelectedItem = names.FirstOrDefault(n => n.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        var open = new Button { Content = "Open", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        void Choose()
        {
            result = list.SelectedItem as string;
            dialog.Close();
        }
        open.Click += (_, _) => Choose();
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => Choose();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { open, cancel } },
            },
        };
        dialog.Opened += (_, _) => list.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>Asks a yes/no question. Returns true for Yes. Without an owner window it returns true.</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, string message, string title = "Please confirm")
    {
        if (owner is null) return true;
        var result = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
            MinWidth = 320,
        };
        var yes = new Button { Content = "Yes", MinWidth = 80, IsDefault = true };
        var no = new Button { Content = "No", MinWidth = 80, IsCancel = true };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
