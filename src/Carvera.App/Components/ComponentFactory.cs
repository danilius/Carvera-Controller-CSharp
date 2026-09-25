using Avalonia.Controls;
using Carvera.App.Layout;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>Creates the content of a non-container element inside its <see cref="ComponentHost"/>.</summary>
public static class ComponentFactory
{
    public static Control? Create(LayoutNode node, ComponentHost host, BuildContext ctx) => node.Type.ToLowerInvariant() switch
    {
        "text" => BasicComponents.Text(node, host, ctx),
        "value" => BasicComponents.Value(node, host, ctx),
        "machinestatus" => BasicComponents.MachineStatus(node, host, ctx),
        "indicator" => BasicComponents.Indicator(node, host, ctx),
        "progress" => BasicComponents.Progress(node, host, ctx),
        "image" => BasicComponents.Image(node, host, ctx),
        "spacer" => null,
        "button" => ButtonComponents.Button(node, host, ctx),
        "toggle" => ButtonComponents.Toggle(node, host, ctx),
        "choice" => ButtonComponents.Choice(node, host, ctx),
        "jogstep" => ButtonComponents.JogStep(node, host, ctx),
        "wcsselector" => ButtonComponents.WcsSelector(node, host, ctx),
        "axisreadout" => AxisReadout.Create(node, host, ctx),
        "slider" => MachineControls.Slider(node, host, ctx),
        "override" => MachineControls.Override(node, host, ctx),
        "jogpad" => MachineControls.JogPad(node, host, ctx),
        "mdi" => ConsoleComponents.Mdi(node, host, ctx),
        "console" => ConsoleComponents.Console(node, host, ctx),
        "connection" => ConnectionComponent.Create(node, host, ctx),
        "toolpath" => new ToolpathView(node, host, ctx),
        "gcodelist" => GcodeList.Create(node, host, ctx),
        "layoutselector" => ButtonComponents.LayoutSelector(node, host, ctx),
        _ => throw new NotSupportedException($"Unknown element type '{node.Type}'."),
    };
}
