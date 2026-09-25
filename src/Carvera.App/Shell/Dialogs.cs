using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Carvera.App.Shell;

public static class Dialogs
{
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
