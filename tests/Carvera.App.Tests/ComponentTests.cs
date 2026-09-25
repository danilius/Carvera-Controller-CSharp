using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Carvera.App.Layout;
using Carvera.Core.Connection;
using Carvera.Core.State;
using Xunit;

namespace Carvera.App.Tests;

public class ComponentTests
{
    private const string Safety = """{ "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }""";

    [AvaloniaFact]
    public void ToggleSwapsGraphicsWithMachineState()
    {
        using var h = new Harness($$"""
            { "root": { "type": "stack", "children": [
              { "type": "toggle", "id": "light", "text": "Light", "bind": "switch.light", "command": "setLight",
                "visuals": { "off": { "image": "assets/light-off.svg" }, "on": { "image": "assets/light-on.svg", "background": "#FEFCE8" } } },
              {{Safety}} ] } }
            """);
        var host = h.Host("light");
        Assert.Equal("assets/light-off.svg", host.Effective.Image);
        Assert.Contains("off", host.ActiveStates());

        h.Controller.State.Set("switch.light", true);
        h.Pump();
        Assert.Equal("assets/light-on.svg", host.Effective.Image);
        Assert.Equal("#FEFCE8", host.Effective.Background);
        Assert.Contains(host.GetVisualDescendants().OfType<Image>(), i => i.Source is not null);
    }

    [AvaloniaFact]
    public void ConditionsOverrideTextAndGraphics()
    {
        using var h = new Harness($$"""
            { "root": { "type": "stack", "children": [
              { "type": "button", "id": "hold", "text": "Hold", "command": "pauseResume",
                "visuals": { "normal": { "image": "assets/hold.svg" } },
                "conditions": [ { "when": "machine.state == 'Hold'", "text": "Resume {machine.state}", "image": "assets/resume.svg" } ] },
              {{Safety}} ] } }
            """);
        var hold = h.Host("hold");
        Assert.Equal("Hold", hold.EffectiveText);
        h.Controller.State.Set(StatePaths.MachineState, "Hold");
        h.Pump();
        Assert.Equal("Resume Hold", hold.EffectiveText);
        Assert.Equal("assets/resume.svg", hold.Effective.Image);
    }

    [AvaloniaFact]
    public void ClassesStyleAndStatesLayerInOrder()
    {
        using var h = new Harness($$"""
            {
              "styles": { "big": { "fontSize": 30, "background": "#111111" } },
              "root": { "type": "stack", "children": [
                { "type": "button", "id": "b", "class": "big", "command": "stop", "style": { "background": "#222222" },
                  "visuals": { "disabled": { "background": "#333333" } } },
                { "type": "button", "command": "feedHold" }, { "type": "button", "command": "reset" } ] }
            }
            """);
        var b = h.Host("b");
        // Not connected, so the command is unavailable and the button disabled.
        Assert.False(b.IsEffectivelyEnabled);
        Assert.Equal("#333333", b.Effective.Background);
        Assert.Equal(30, b.Effective.FontSize);
    }

    [AvaloniaFact]
    public async Task ButtonsRunCommandsWhenConnected()
    {
        using var h = new Harness($$"""
            { "root": { "type": "stack", "children": [
              { "type": "button", "id": "light", "command": "setLight", "args": { "on": true } },
              {{Safety}} ] } }
            """);
        var light = h.Host("light");
        Assert.False(light.IsEffectivelyEnabled);
        await h.Controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        h.Pump();
        Assert.True(light.IsEffectivelyEnabled);
        light.PerformClick();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!h.Controller.State.Get<bool>("switch.light") && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(h.Controller.State.Get<bool>("switch.light"));
        await h.Controller.DisconnectAsync();
    }

    [AvaloniaFact]
    public void MachineStatusUsesStateColours()
    {
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ { "type": "machineStatus", "id": "s" }, {{Safety}} ] } }""");
        var s = h.Host("s");
        Assert.Contains("disconnected", s.ActiveStates());
        Assert.Equal("N/A", s.EffectiveText);
        h.Controller.State.Set(StatePaths.MachineState, "Alarm");
        h.Pump();
        Assert.Contains("alarm", s.ActiveStates());
        Assert.Equal("#FEE2E2", s.Effective.Background);
    }

    [AvaloniaFact]
    public void AxisReadoutShowsFormattedValues()
    {
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ { "type": "axisReadout", "id": "x", "axis": "X", "show": ["work", "machine"], "format": "0.00" }, {{Safety}} ] } }""");
        h.Controller.State.Set(StatePaths.AxisWork("x"), 12.345);
        h.Controller.State.Set(StatePaths.AxisMachine("x"), -100.0);
        h.Pump();
        var texts = h.Host("x").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("12.35", texts);
        Assert.Contains("-100.00", texts);
        Assert.Contains("MCS", texts);
    }

    [AvaloniaFact]
    public void ChoiceMarksTheSelectedOption()
    {
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ { "type": "jogStep", "id": "steps", "values": [0.1, 1, 10] }, {{Safety}} ] } }""");
        h.Controller.State.Set(StatePaths.JogStep, 10.0);
        h.Pump();
        var options = h.Host("steps").GetVisualDescendants().OfType<ComponentHost>().ToList();
        Assert.Equal(3, options.Count);
        Assert.True(options[2].HasState("selected"));
        Assert.False(options[0].HasState("selected"));
        options[0].PerformClick(); // setJogStep needs no connection
        var until = DateTime.UtcNow.AddSeconds(2);
        while (h.Controller.State.Get<double>(StatePaths.JogStep) != 0.1 && DateTime.UtcNow < until) h.Pump();
        h.Pump();
        Assert.True(options[0].HasState("selected"));
    }

    [AvaloniaFact]
    public void VisibilityExpressionsFollowState()
    {
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ { "type": "text", "id": "t", "text": "Running", "visible": "job.playing" }, {{Safety}} ] } }""");
        Assert.False(h.Host("t").IsVisible);
        h.Controller.State.Set(StatePaths.JobPlaying, true);
        h.Pump();
        Assert.True(h.Host("t").IsVisible);
    }
}
