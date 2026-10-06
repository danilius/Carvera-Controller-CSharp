using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Carvera.App.Components.Bed;
using Carvera.App.Services;
using Carvera.Core.State;
using Xunit;

namespace Carvera.App.Tests;

public class DesktopTwoTests
{
    private const string Safety = """{ "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }""";

    public DesktopTwoTests() =>
        Environment.SetEnvironmentVariable("CARVERA_CS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "carvera-cs-tests", Guid.NewGuid().ToString("N")));

    [AvaloniaFact]
    public void PrimaryTabsDrawTheSelectedPageInTheAccentColour()
    {
        using var h = new Harness($$"""
            { "root": { "type": "stack", "children": [
              { "type": "tabs", "id": "pages", "prominence": "primary", "children": [
                { "type": "text", "title": "One", "text": "1" }, { "type": "text", "title": "Two", "text": "2" } ] }, {{Safety}} ] } }
            """, 600, 300);
        var tabs = h.Host("pages").GetVisualDescendants().OfType<TabControl>().Single();
        var headers = tabs.Items.OfType<TabItem>().Select(t => Assert.IsType<Border>(t.Header)).ToList();
        var accent = h.Session.Context.Theme.ResolveColor("@accent")!.Value;
        Assert.Equal(accent, ((ISolidColorBrush)headers[0].Background!).Color);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)headers[1].Background!).Color);
        tabs.SelectedIndex = 1;
        h.Pump();
        Assert.Equal(accent, ((ISolidColorBrush)headers[1].Background!).Color);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)headers[0].Background!).Color);
        Assert.True(headers[0].Bounds.Height >= 40); // large enough to hit easily
    }

    [AvaloniaFact]
    public void TheBedPictureFollowsTheModelAndTheSwitch()
    {
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ { "type": "bedView", "id": "bed" }, {{Safety}} ] } }""", 600, 500);
        var ctx = h.Session.Context;
        Assert.NotNull(BedImages.Find(ctx, null));
        var c1 = BedImages.Find(ctx, null);
        h.Services.State.Set(StatePaths.MachineModelName, "CA1");
        var air = BedImages.Find(ctx, null);
        Assert.NotNull(air);
        Assert.NotEqual(c1!.Size, air!.Size); // the Air picture is a different picture
        h.Services.State.Set(StatePaths.ViewBedImage, false);
        Assert.Null(BedImages.Find(ctx, null));
        Assert.Null(BedImages.Find(ctx, "assets/bed/air-bolt-holes.png"));
    }

    [AvaloniaFact]
    public void TheBedViewDrawsWithoutAFileAndWithOne()
    {
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ { "type": "bedView", "id": "bed" }, {{Safety}} ] } }""", 700, 520);
        var state = h.Services.State;
        h.Pump();
        Assert.True(h.BoundsOf("bed").Width > 100);
        state.Set(StatePaths.Connected, true);
        state.Set(StatePaths.AxisOffset("x"), -300.0);
        state.Set(StatePaths.AxisOffset("y"), -200.0);
        state.Set(StatePaths.AxisMachine("x"), -290.0);
        state.Set(StatePaths.AxisMachine("y"), -190.0);
        state.Set(StatePaths.FileHasBounds, true);
        state.Set(StatePaths.FileXMax, 60.0);
        state.Set(StatePaths.FileYMax, 40.0);
        state.Set(StatePaths.WcsRotation, 7.5);
        state.Set(StatePaths.JobLeveling, true);
        state.Set(StatePaths.JobZProbe, true);
        h.Pump();
        h.Window.UpdateLayout();
        // Rendering the frame runs the whole draw path (picture, anchors, outline, grid of points, markers).
        Assert.NotNull(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(h.Window));
    }

    [AvaloniaFact]
    public void TheJogButtonsAreLabelledForTheReversedYAxis()
    {
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ { "type": "jogPad", "id": "pad" }, {{Safety}} ] } }""", 400, 400);
        string? Caption(string key) => h.Host("pad").GetVisualDescendants().OfType<Carvera.App.Layout.ComponentHost>().Single(x => x.Node.Path.EndsWith("." + key)).EffectiveText;
        h.Services.State.Set(StatePaths.JogInvertY, false);
        h.Pump();
        Assert.Equal("Y+", Caption("Y+"));
        Assert.Equal("Y−", Caption("Y-"));
        h.Services.State.Set(StatePaths.JogInvertY, true);
        h.Pump();
        Assert.Equal("Y−", Caption("Y+")); // the button that points up now moves Y-
        Assert.Equal("Y+", Caption("Y-"));
        Assert.Equal("X+", Caption("X+"));
    }

    [AvaloniaFact]
    public void TheJogSettingsAreKeptInTheSettings()
    {
        var settings = new Settings();
        using var h = new Harness($$"""{ "root": { "type": "stack", "children": [ {{Safety}} ] } }""", 300, 200, settings);
        var state = h.Services.State;
        Assert.Equal("step", state.Get<string>(StatePaths.JogButtonMode));
        Assert.True(state.Get<bool>(StatePaths.JogInvertY)); // reversed by default
        state.Set(StatePaths.JogButtonMode, "continuous");
        state.Set(StatePaths.JogKeyboard, false);
        state.Set(StatePaths.JogInvertY, false);
        state.Set(StatePaths.ViewBedImage, false);
        h.Pump();
        Assert.Equal("continuous", settings.JogButtonMode);
        Assert.False(settings.JogKeyboard);
        Assert.False(settings.JogInvertY);
        Assert.False(settings.ShowBedImage);
    }
}
