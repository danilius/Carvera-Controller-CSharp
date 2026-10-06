using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Carvera.Editor;

/// <summary>Small modal dialogs: a line of text, a yes/no, a choice from a list.</summary>
public static class Dialogs
{
    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial = "", string ok = "OK")
    {
        var box = new TextBox { Text = initial, MinWidth = 320, SelectionStart = 0, SelectionEnd = initial.Length };
        var accept = new Button { Content = ok, IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var window = Create(title, new StackPanel
        {
            Spacing = 10, Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { accept, cancel } },
            },
        });
        string? result = null;
        accept.Click += (_, _) => { result = box.Text; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) => box.Focus();
        await window.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string ok = "OK")
    {
        var accept = new Button { Content = ok, IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var window = Create(title, new StackPanel
        {
            Spacing = 14, Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 440 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { accept, cancel } },
            },
        });
        var result = false;
        accept.Click += (_, _) => { result = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>Three-way question: returns the label of the button pressed, or null when the dialog was closed.</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string message, params string[] buttons)
    {
        string? result = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var window = Create(title, new StackPanel
        {
            Spacing = 14, Margin = new Thickness(18),
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 460 }, row },
        });
        foreach (var label in buttons)
        {
            var button = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += (_, _) => { result = label; window.Close(); };
            row.Children.Add(button);
        }
        await window.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> PickAsync(Window owner, string title, IReadOnlyList<string> items, string? selected = null)
    {
        var list = new ListBox { ItemsSource = items, SelectedItem = selected, MinWidth = 280, MaxHeight = 360 };
        string? result = null;
        var window = Create(title, new StackPanel { Spacing = 10, Margin = new Thickness(14), Children = { list } });
        list.DoubleTapped += (_, _) => { result = list.SelectedItem as string; window.Close(); };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = list.SelectedItem as string; window.Close(); }
        };
        await window.ShowDialog(owner);
        return result;
    }

    private static Window Create(string title, Control content) => new()
    {
        Title = title,
        Content = content,
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };
}
