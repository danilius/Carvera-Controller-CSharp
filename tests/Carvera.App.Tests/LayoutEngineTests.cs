using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Carvera.App.Tests;

public class LayoutEngineTests
{
    private static string Root(string node) => $$"""{ "root": {{node}} }""";

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1.01, expected + 1.01);

    [AvaloniaFact]
    public void UnsizedChildrenShareTheRemainingSpace()
    {
        using var h = new Harness(Root("""
            { "type": "stack", "orientation": "horizontal", "spacing": 10, "children": [
              { "type": "spacer", "id": "fixed", "width": 100 },
              { "type": "spacer", "id": "fill" },
              { "type": "spacer", "id": "double", "width": "2*" },
              { "type": "spacer", "id": "pct", "width": "25%" }
            ] }
            """), 1030, 400);
        // 1030 wide: percent = 25% of 1030 = 257.5; remaining = 1030 - 100 - 257.5 - 30 = 642.5, split 1:2
        Near(100, h.BoundsOf("fixed").Width);
        Near(214.17, h.BoundsOf("fill").Width);
        Near(428.33, h.BoundsOf("double").Width);
        Near(257.5, h.BoundsOf("pct").Width);
        Near(0, h.BoundsOf("fixed").X);
        Near(110, h.BoundsOf("fill").X);
        Near(1030 - 257.5, h.BoundsOf("pct").X);
        // cross axis: unsized children fill the full height
        Near(400, h.BoundsOf("fill").Height);
    }

    [AvaloniaFact]
    public void FullySizedChildrenLeaveEmptySpace()
    {
        using var h = new Harness(Root("""
            { "type": "stack", "orientation": "horizontal", "children": [
              { "type": "spacer", "id": "a", "width": 100, "height": 50 },
              { "type": "spacer", "id": "b", "width": 150, "height": 80 }
            ] }
            """), 800, 600);
        Assert.Equal(new Rect(0, 0, 100, 50), h.BoundsOf("a"));
        Assert.Equal(new Rect(100, 0, 150, 80), h.BoundsOf("b"));
    }

    [AvaloniaFact]
    public void JustifyAndAlignPlaceFixedChildren()
    {
        using var h = new Harness(Root("""
            { "type": "stack", "orientation": "horizontal", "justify": "end", "children": [
              { "type": "spacer", "id": "a", "width": 100, "height": 40, "valign": "center" },
              { "type": "spacer", "id": "b", "width": 100, "height": 40, "valign": "end" }
            ] }
            """), 800, 600);
        Assert.Equal(new Rect(600, 280, 100, 40), h.BoundsOf("a"));
        Assert.Equal(new Rect(700, 560, 100, 40), h.BoundsOf("b"));
    }

    [AvaloniaFact]
    public void SpaceBetweenDistributesLeftover()
    {
        using var h = new Harness(Root("""
            { "type": "stack", "justify": "space-between", "children": [
              { "type": "spacer", "id": "a", "height": 100 }, { "type": "spacer", "id": "b", "height": 100 }, { "type": "spacer", "id": "c", "height": 100 }
            ] }
            """), 400, 600);
        Near(0, h.BoundsOf("a").Y);
        Near(250, h.BoundsOf("b").Y);
        Near(500, h.BoundsOf("c").Y);
        Near(400, h.BoundsOf("b").Width);
    }

    [AvaloniaFact]
    public void AutoFitsContentAndHiddenElementsTakeNoSpace()
    {
        using var h = new Harness(Root("""
            { "type": "stack", "children": [
              { "type": "text", "id": "auto", "text": "Hello", "height": "auto" },
              { "type": "spacer", "id": "gone", "height": 100, "visible": false },
              { "type": "spacer", "id": "rest" }
            ] }
            """), 400, 600);
        var auto = h.BoundsOf("auto");
        Assert.InRange(auto.Height, 10, 40);
        Assert.False(h.Host("gone").IsVisible);
        Near(auto.Height, h.BoundsOf("rest").Y);
        Near(600 - auto.Height, h.BoundsOf("rest").Height);
    }

    [AvaloniaFact]
    public void MarginsAndMinMaxAreHonoured()
    {
        using var h = new Harness(Root("""
            { "type": "stack", "orientation": "horizontal", "children": [
              { "type": "spacer", "id": "a", "margin": "10 20", "maxWidth": 150 },
              { "type": "spacer", "id": "b" }
            ] }
            """), 800, 600);
        var a = h.BoundsOf("a");
        Assert.Equal(20, a.X);
        Assert.Equal(10, a.Y);
        Assert.Equal(150, a.Width);
        Assert.Equal(580, a.Height);
    }

    [AvaloniaFact]
    public void CanvasPlacesChildrenByPixelsAndPercent()
    {
        using var h = new Harness(Root("""
            { "type": "canvas", "children": [
              { "type": "spacer", "id": "a", "x": 20, "y": 30, "width": 100, "height": 50 },
              { "type": "spacer", "id": "b", "x": "50%", "y": "25%", "width": "25%" },
              { "type": "spacer", "id": "c", "x": 700, "y": 500 }
            ] }
            """), 800, 600);
        Assert.Equal(new Rect(20, 30, 100, 50), h.BoundsOf("a"));
        Assert.Equal(new Rect(400, 150, 200, 450), h.BoundsOf("b"));
        Assert.Equal(new Rect(700, 500, 100, 100), h.BoundsOf("c"));
    }

    [AvaloniaFact]
    public void GridAndSplitPlaceChildren()
    {
        using var h = new Harness(Root("""
            { "type": "split", "orientation": "horizontal", "sizes": ["*", "200"], "splitterSize": 10, "children": [
              { "type": "grid", "columns": ["100", "*"], "rows": ["50", "*"], "children": [
                  { "type": "spacer", "id": "cell", "row": 1, "column": 1 },
                  { "type": "spacer", "id": "small", "row": 0, "column": 0, "width": 40, "height": 20, "align": "end" }
              ] },
              { "type": "spacer", "id": "side" }
            ] }
            """), 810, 600);
        Assert.Equal(new Rect(100, 50, 500, 550), h.BoundsOf("cell"));
        Assert.Equal(new Rect(60, 0, 40, 20), h.BoundsOf("small"));
        Assert.Equal(new Rect(610, 0, 200, 600), h.BoundsOf("side"));
    }

    [AvaloniaFact]
    public void UnsizedChildrenInAScrollTakeTheirContentHeight()
    {
        using var h = new Harness(Root("""
            { "type": "scroll", "child": { "type": "stack", "children": [
              { "type": "spacer", "id": "a", "height": 400 },
              { "type": "text", "id": "b", "text": "content" },
              { "type": "spacer", "id": "c", "height": 400 }
            ] } }
            """), 400, 600);
        var b = h.BoundsOf("b");
        Assert.InRange(b.Height, 10, 40);
        Near(400, b.Y);
        Near(400 + b.Height, h.BoundsOf("c").Y);
    }

    [AvaloniaFact]
    public void UnsizedChildrenInAScrollGrowIntoSpareSpace()
    {
        using var h = new Harness(Root("""
            { "type": "scroll", "child": { "type": "stack", "children": [
              { "type": "spacer", "id": "a", "height": 100 },
              { "type": "text", "id": "b", "text": "content" }
            ] } }
            """), 400, 600);
        Near(500, h.BoundsOf("b").Height);
    }

    [AvaloniaFact]
    public void RegionsCanBeReorderedAndReoriented()
    {
        using var h = new Harness("""
            {
              "regions": { "dro": { "type": "stack", "orientation": "horizontal", "children": [
                  { "type": "axisReadout", "id": "z", "axis": "Z" },
                  { "type": "axisReadout", "id": "x", "axis": "X" },
                  { "type": "axisReadout", "id": "y", "axis": "Y" } ] } },
              "root": { "type": "stack", "children": [ { "region": "dro", "height": 80 }, { "type": "spacer" } ] }
            }
            """, 900, 600);
        Assert.True(h.BoundsOf("z").X < h.BoundsOf("x").X);
        Assert.True(h.BoundsOf("x").X < h.BoundsOf("y").X);
        Near(80, h.BoundsOf("x").Height);
        Near(300, h.BoundsOf("y").Width);
    }
}
