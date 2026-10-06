using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Carvera.App.Layout;
using Carvera.Core.Commands;
using Carvera.Core.Probing;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>
/// Probing: a family picker, one text box per parameter, the operations as buttons, and the command that would be
/// sent. The values are remembered per family in the settings. Pressing an operation runs the <c>probe</c> command.
/// </summary>
public static class ProbePanelComponent
{
    public static Control Create(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var settings = ctx.Services.Settings;
        var theme = ctx.Theme;
        var fixedFamily = node.GetString("family") is { } f ? ProbeOperations.FindFamily(f) : null;
        var showDescriptions = node.GetBool("showDescriptions") ?? false;
        var families = ProbeOperations.Families;

        var picker = new ComboBox { ItemsSource = families.Select(x => x.Title).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = fixedFamily is null };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = theme.TokenBrush("textMuted"), FontSize = theme.FontSize - 1 };
        var fields = new StackPanel { Spacing = 4 };
        var operations = new WrapPanel { Orientation = Orientation.Horizontal };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas,Menlo,monospace") };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = theme.TokenBrush("warning"), FontSize = theme.FontSize - 1 };
        var previewBox = new Border
        {
            Child = new StackPanel { Spacing = 4, Children = { preview, note } }, Padding = new Thickness(8), CornerRadius = new CornerRadius(6),
            Background = theme.TokenBrush("accentSoft"),
        };

        var boxes = new Dictionary<string, TextBox>();
        ProbeFamily current = fixedFamily ?? families[0];
        var building = false;

        Dictionary<string, string> Stored(ProbeFamily family)
        {
            if (!settings.ProbeSettings.TryGetValue(family.Id, out var values))
            {
                values = ProbeOperations.Defaults(family);
                settings.ProbeSettings[family.Id] = values;
            }
            return values;
        }

        Dictionary<string, string> Current() => boxes.ToDictionary(b => b.Key, b => b.Value.Text ?? "");

        void UpdatePreview(ProbeOperation? selected = null)
        {
            var op = selected ?? current.Operations[0];
            var config = Current();
            var result = op.Build(config, current.Parameters);
            preview.Text = result.Ok ? result.Gcode : result.Problem;
            preview.Foreground = result.Ok ? theme.TokenBrush("text") : theme.TokenBrush("danger");
            note.Text = result.Note ?? "";
            note.IsVisible = result.Note is not null;
        }

        void Rebuild()
        {
            building = true;
            description.Text = current.Description;
            var stored = Stored(current);
            fields.Children.Clear();
            boxes.Clear();
            foreach (var parameter in current.Parameters)
            {
                var box = new TextBox { Text = stored.GetValueOrDefault(parameter.Code, ""), Width = 96, PlaceholderText = parameter.Code, HorizontalAlignment = HorizontalAlignment.Right };
                ToolTip.SetTip(box, parameter.Description);
                var label = new TextBlock { Text = parameter.Label + (parameter.Required ? " *" : ""), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(label, parameter.Description);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(box, 1);
                row.Children.Add(label);
                row.Children.Add(box);
                fields.Children.Add(row);
                if (showDescriptions)
                    fields.Children.Add(new TextBlock { Text = parameter.Description, TextWrapping = TextWrapping.Wrap, FontSize = theme.FontSize - 2, Foreground = theme.TokenBrush("textMuted"), Margin = new Thickness(0, 0, 0, 4) });
                boxes[parameter.Code] = box;
                box.TextChanged += (_, _) =>
                {
                    if (building) return;
                    var values = Stored(current);
                    if (string.IsNullOrWhiteSpace(box.Text)) values.Remove(parameter.Code); else values[parameter.Code] = box.Text.Trim();
                    settings.Save();
                    UpdatePreview();
                };
            }

            operations.Children.Clear();
            foreach (var op in current.Operations)
            {
                var button = new Button { Content = op.Title, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 5) };
                button.PointerEntered += (_, _) => UpdatePreview(op);
                button.GotFocus += (_, _) => UpdatePreview(op);
                ToolTip.SetTip(button, $"Runs {op.Command}. Asks first, and only while the machine is idle.");
                var operationId = op.Id;
                button.Click += (_, _) =>
                {
                    var args = CommandArgs.Empty.With("family", current.Id).With("operation", operationId);
                    foreach (var (code, box) in boxes)
                        if (!string.IsNullOrWhiteSpace(box.Text)) args = args.With(code, box.Text.Trim());
                    ctx.Run("probe", args);
                };
                operations.Children.Add(button);
            }
            building = false;
            UpdatePreview();
        }

        void EnableByState() => operations.IsEnabled = ctx.Commands.CanExecute("probe", CommandArgs.Empty);

        picker.SelectedIndex = 0;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0 || fixedFamily is not null) return;
            current = families[picker.SelectedIndex];
            Rebuild();
        };
        Rebuild();
        EnableByState();
        ctx.Watch(ctx.Commands.AvailabilityPaths("probe"), EnableByState);

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(picker);
        root.Children.Add(description);
        root.Children.Add(operations);
        root.Children.Add(fields);
        root.Children.Add(previewBox);
        return new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) };
    }
}
