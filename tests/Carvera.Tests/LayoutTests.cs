using Carvera.Layout;
using Xunit;

namespace Carvera.Tests;

public class LayoutTests
{
    private static LayoutLoadResult Load(string json) => LayoutLoader.Load(json);

    private const string Safety = """
        { "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }
        """;

    [Theory]
    [InlineData(null, SizeKind.Fill, 1)]
    [InlineData("120", SizeKind.Pixels, 120)]
    [InlineData("120px", SizeKind.Pixels, 120)]
    [InlineData("30%", SizeKind.Percent, 30)]
    [InlineData("2*", SizeKind.Star, 2)]
    [InlineData("*", SizeKind.Fill, 1)]
    [InlineData("auto", SizeKind.Auto, 0)]
    [InlineData("fill", SizeKind.Fill, 1)]
    public void ParsesSizes(string? text, SizeKind kind, double value)
    {
        Assert.True(SizeSpec.TryParse(text ?? "", out var size, out _));
        Assert.Equal(kind, size.Kind);
        Assert.Equal(value, size.Value);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("wide")]
    [InlineData("0*")]
    public void RejectsBadSizes(string text) => Assert.False(SizeSpec.TryParse(text, out _, out _));

    [Fact]
    public void ParsesEdgesInCssOrder()
    {
        Assert.True(Edges.TryParse(System.Text.Json.Nodes.JsonValue.Create("1 2 3 4"), out var e, out _));
        Assert.Equal(new Edges(Left: 4, Top: 1, Right: 2, Bottom: 3), e);
        Assert.True(Edges.TryParse(System.Text.Json.Nodes.JsonValue.Create("8 4"), out e, out _));
        Assert.Equal(new Edges(4, 8, 4, 8), e);
    }

    [Fact]
    public void ExpandsRegionsWithOverrides()
    {
        var result = Load($$"""
            {
              // comments and trailing commas are allowed
              "regions": {
                "dro": { "type": "stack", "orientation": "vertical", "children": [ { "type": "axisReadout", "axis": "X" } ] },
                "twice": { "type": "stack", "children": [ { "region": "dro" }, { "region": "dro", "width": 200 } ] },
              },
              "root": { "type": "stack", "children": [ { "region": "twice" }, {{Safety}} ] },
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var twice = result.Document!.Root.Children[0];
        Assert.Equal("twice", twice.Region);
        Assert.Equal("stack", twice.Children[1].Type);
        Assert.Equal(SizeSpec.Pixels(200), twice.Children[1].Width);
        Assert.Equal(SizeSpec.Fill, twice.Children[0].Width);
        Assert.Equal("axisReadout", twice.Children[1].Children[0].Type);
    }

    [Fact]
    public void ReportsRegionCycles()
    {
        var result = Load("""{ "regions": { "a": { "region": "b" }, "b": { "region": "a" } }, "root": { "region": "a" } }""");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, d => d.Message.Contains("refers to itself"));
    }

    [Fact]
    public void ReportsUnknownTypesPropertiesAndCommandsWithSuggestions()
    {
        var result = Load($$"""
            { "root": { "type": "stack", "children": [
                { "type": "buton", "command": "feedHold" },
                { "type": "button", "command": "fedHold", "colour": "red" },
                { "type": "text", "text": "{unclosed" },
                { "type": "stack", "width": "wide", "visible": "machine.state ==" },
                { "type": "axisReadout", "axis": "Q" },
                {{Safety}}
            ] } }
            """);
        Assert.False(result.Success);
        var messages = result.Diagnostics.Select(d => $"{d.Path}: {d.Message}").ToList();
        Assert.Contains(messages, m => m.Contains("Unknown element type 'buton'") && m.Contains("Did you mean 'button'"));
        Assert.Contains(messages, m => m.Contains("Unknown command 'fedHold'") && m.Contains("feedHold"));
        Assert.Contains(messages, m => m.Contains("no property 'colour'"));
        Assert.Contains(messages, m => m.StartsWith("root.children[2].text") && m.Contains("Invalid text"));
        Assert.Contains(messages, m => m.StartsWith("root.children[3].width"));
        Assert.Contains(messages, m => m.StartsWith("root.children[3].visible"));
        Assert.Contains(messages, m => m.StartsWith("root.children[4].axis"));
    }

    [Fact]
    public void ReportsInvalidJsonWithPosition()
    {
        var result = Load("{ \"root\": { \"type\": \"stack\" ");
        Assert.Null(result.Document);
        Assert.Contains("line", result.Errors.Single().Path);
    }

    [Fact]
    public void ReservedShortcutsAreReported()
    {
        var result = Load($$"""{ "root": { "type": "stack", "children": [ {{Safety}} ] }, "shortcuts": [ { "key": "Ctrl+L", "command": "stop" } ] }""");
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("reserved"));
    }

    [Fact]
    public void ComponentsCannotHaveChildren()
    {
        var result = Load("""{ "root": { "type": "button", "command": "stop", "children": [ { "type": "text" } ] } }""");
        Assert.Contains(result.Errors, d => d.Message.Contains("cannot contain children"));
    }

    [Fact]
    public void SafetyAnalyzerFindsHiddenAndMissingControls()
    {
        string Layout(string children) => $$"""{ "root": { "type": "stack", "children": [ {{children}} ] } }""";

        Assert.Empty(SafetyAnalyzer.FindMissing(Load(Layout(Safety)).Document!));

        var none = SafetyAnalyzer.FindMissing(Load(Layout("""{ "type": "text", "text": "hi" }""")).Document!);
        Assert.Equal(["Feed Hold", "Stop", "Reset"], none);

        // pauseResume satisfies Feed Hold; hidden elements (or hidden ancestors) do not count.
        var hidden = SafetyAnalyzer.FindMissing(Load(Layout("""
            { "type": "button", "command": "pauseResume" },
            { "type": "button", "command": "stop", "visible": false },
            { "type": "stack", "width": 0, "children": [ { "type": "button", "command": "reset" } ] }
            """)).Document!);
        Assert.Equal(["Stop", "Reset"], hidden);

        // Conditional visibility counts as shown.
        var conditional = SafetyAnalyzer.FindMissing(Load(Layout("""
            { "type": "button", "command": "feedHold", "visible": "machine.state == 'Run'" },
            { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }
            """)).Document!);
        Assert.Empty(conditional);
    }

    [Fact]
    public void MissingSafetyControlsAreWarningsNotErrors()
    {
        var result = Load("""{ "root": { "type": "text", "text": "hello" } }""");
        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("Reset"));
    }

    public static TheoryData<string> ShippedLayouts() => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "layouts"), "*.json").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(ShippedLayouts))]
    public void ShippedLayoutsAreValid(string file)
    {
        var result = LayoutLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "layouts", file));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warnings = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
        if (file == "canvas-demo.json")
            Assert.Equal(["Reset"], SafetyAnalyzer.FindMissing(result.Document!));
        else
            Assert.True(warnings.Count == 0, string.Join("\n", warnings));
    }

    [Fact]
    public void ShippedLayoutImagesExist()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "layouts");
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"(assets/[^\"]+)\""))
                Assert.True(File.Exists(Path.Combine(dir, m.Groups[1].Value)), $"{Path.GetFileName(file)} refers to missing {m.Groups[1].Value}");
        }
    }

    [Fact]
    public void CatalogStatesIncludeBaseStates()
    {
        foreach (var spec in ComponentCatalog.All.Where(s => s.Type != "spacer"))
            Assert.Contains("normal", spec.States);
    }
}
