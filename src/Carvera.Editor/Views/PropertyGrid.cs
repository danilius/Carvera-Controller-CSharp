using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>A form for the properties of one JSON object (an element, a visual block, an arguments object...), built from property specs.</summary>
public sealed class PropertyGrid : StackPanel
{
    public PropertyGrid(EditorContext ctx, string objectPath, IEnumerable<PropSpec> specs, string? ownerType, bool compact = false)
    {
        Spacing = compact ? 1 : 2;
        var target = JsonPath.ResolveObject(ctx.Doc.Data, objectPath);
        foreach (var spec in specs)
        {
            var current = target?[spec.Name];
            var isSet = current is not null;
            var name = spec.Name;
            var editor = PropertyEditors.Create(new PropContext
            {
                Ctx = ctx, Spec = spec, ObjectPath = objectPath, Current = current, OwnerType = ownerType,
                Commit = value => ctx.SetProperty(objectPath, name, value),
            });
            void Clear() => ctx.SetProperty(objectPath, name, null, $"Remove {name}");

            Control row;
            if (IsWide(spec.Kind))
            {
                var header = new DockPanel { Margin = new Thickness(0, 4, 0, 1) };
                var clear = new Button { Content = "✕", Padding = new Thickness(5, 1), FontSize = 10, Background = Brushes.Transparent, IsVisible = isSet };
                clear.Click += (_, _) => Clear();
                DockPanel.SetDock(clear, Dock.Right);
                header.Children.Add(clear);
                var label = new TextBlock { Text = spec.Name, FontWeight = isSet ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(label, spec.Description);
                header.Children.Add(label);
                row = new StackPanel { Children = { header, editor } };
            }
            else row = PropertyEditors.FieldRow(spec.Name, spec.Description, editor, isSet, Clear);

            var problems = ctx.Doc.Diagnostics.Where(d => d.Path == objectPath + "." + spec.Name).ToList();
            if (problems.Count > 0)
            {
                var stack = new StackPanel { Children = { row } };
                foreach (var problem in problems)
                    stack.Children.Add(new TextBlock
                    {
                        Text = problem.Message, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(104, 0, 0, 2),
                        Foreground = problem.Severity == DiagnosticSeverity.Error ? PropertyEditors.ErrorBrush : Brush.Parse("#B45309"),
                    });
                row = stack;
            }
            Children.Add(row);
        }
    }

    private static bool IsWide(PropKind kind) => kind is PropKind.Style or PropKind.Visuals or PropKind.Conditions or PropKind.Options or PropKind.Args or PropKind.Any or PropKind.StringList;
}
