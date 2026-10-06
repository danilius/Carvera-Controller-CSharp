using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Carvera.Core.Connection;
using Carvera.Core.State;
using Xunit;

namespace Carvera.App.Tests;

public class ProbePanelTests
{
    private const string Safety = """{ "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }""";

    private static Harness Bore() => new($$"""
        { "root": { "type": "stack", "children": [
          { "type": "probePanel", "id": "probe", "family": "bore" }, {{Safety}} ] } }
        """, 700, 900);

    private static TextBox Box(Harness h, string code) => h.Host("probe").GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == code);

    [AvaloniaFact]
    public void TheCommandPreviewFollowsTheFieldsAndTheValuesAreRemembered()
    {
        using var h = Bore();
        var previews = () => h.Host("probe").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(previews(), t => t is not null && t.StartsWith("Missing required parameter"));

        Box(h, "X").Text = "20";
        Box(h, "D").Text = "3.175";
        h.Pump();
        Assert.Contains("M461 X20 S1 D3.175", previews());
        Assert.Equal("20", h.Services.Settings.ProbeSettings["bore"]["X"]);
        Assert.Equal("1", h.Services.Settings.ProbeSettings["bore"]["S"]); // the family's default

        Box(h, "X").Text = "";
        h.Pump();
        Assert.False(h.Services.Settings.ProbeSettings["bore"].ContainsKey("X"));
    }

    [AvaloniaFact]
    public async Task AnOperationButtonRunsTheProbeCommandOnAnIdleMachine()
    {
        using var h = Bore();
        Box(h, "X").Text = "20";
        Box(h, "Y").Text = "30";
        h.Pump();
        var button = h.Host("probe").GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Centre of bore"));
        Assert.False(h.Host("probe").GetVisualDescendants().OfType<WrapPanel>().First().IsEnabled); // not connected

        await h.Controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until && h.Controller.State.Get<string>(StatePaths.MachineState) != "Idle") { await Task.Delay(20); h.Pump(); }
        h.Pump();
        Assert.True(h.Host("probe").GetVisualDescendants().OfType<WrapPanel>().First().IsEnabled);

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && !h.Controller.Console.Entries.Any(e => e.Text == "M461 X20 Y30 S1")) { await Task.Delay(20); h.Pump(); }
        Assert.Contains(h.Controller.Console.Entries, e => e.Text == "M461 X20 Y30 S1");
        await h.Controller.DisconnectAsync();
    }
}
