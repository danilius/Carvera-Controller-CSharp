using System.Text.Json.Nodes;
using Carvera.Editor.Model;
using Carvera.Layout;
using Xunit;

namespace Carvera.Editor.Tests;

public class ModelTests
{
    private const string Safety = """{ "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }""";

    private static string Layout(string children) => $$"""{ "root": { "type": "stack", "children": [ {{children}}, {{Safety}} ] } }""";

    private static EditorDocument Doc(string children = """{ "type": "text", "id": "a", "text": "A" }, { "type": "text", "id": "b", "text": "B" }""") =>
        new(Layout(children), "test");

    [Fact]
    public void PathsParseAndFormat()
    {
        var segments = JsonPath.Parse("root.children[2].child.width");
        Assert.Equal(["root", "children", 2, "child", "width"], segments);
        Assert.Equal("root.children[2].child.width", JsonPath.Format(segments));
        Assert.Equal("root.children[2]", JsonPath.Parent("root.children[2].width"));
        Assert.Equal("root.children[2]", JsonPath.ElementPrefix("root.children[2].width"));
        Assert.Equal("regions.x.children[0]", JsonPath.ElementPrefix("regions.x.children[0].visuals.normal"));
        Assert.Null(JsonPath.ElementPrefix("styles.foo"));
        Assert.True(JsonPath.IsWithin("root.children[2].x", "root.children[2]"));
        Assert.False(JsonPath.IsWithin("root.children[20]", "root.children[2]"));
    }

    [Fact]
    public void TheFormatterKeepsSmallObjectsOnOneLine()
    {
        var node = JsonNode.Parse("""{ "name": "x", "root": { "type": "stack", "children": [ { "type": "button", "text": "Go → 1", "args": { "axis": "X" } } ] } }""")!;
        var text = JsonFormatter.Format(node);
        Assert.Contains("""{ "type": "button", "text": "Go → 1", "args": { "axis": "X" } }""", text);
        Assert.Contains("\"children\": [\n", text);
        Assert.Equal(node.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
    }

    [Fact]
    public void ShippedLayoutsSurviveARoundTripThroughTheFormatter()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "layouts");
        Assert.True(Directory.Exists(folder));
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            var original = JsonNode.Parse(File.ReadAllText(file), documentOptions: LayoutLoader.DocumentOptions)!;
            var text = JsonFormatter.Format(original);
            Assert.Equal(original.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
            Assert.True(new EditorDocument(text, "x", null, folder).IsValid, file);
        }
    }

    [Fact]
    public void InsertRemoveAndMoveKeepTheTreeConsistent()
    {
        var doc = Doc();
        var added = doc.Mutate("Add", d => ElementOps.Insert(d, "root", 1, NewElements.Create("button")));
        Assert.Equal("root.children[1]", added);
        Assert.Equal("button", ElementOps.TypeOf(doc.Element("root.children[1]")!));
        Assert.Equal("b", doc.Element("root.children[2]")!["id"]!.GetValue<string>());

        // Move the last text (now index 2) to the front.
        var moved = doc.Mutate("Move", d => ElementOps.Move(d, "root.children[2]", "root", 0));
        Assert.Equal("root.children[0]", moved);
        Assert.Equal("b", doc.Element("root.children[0]")!["id"]!.GetValue<string>());

        // Moving down within the same parent counts the gap left behind.
        var down = doc.Mutate("Move", d => ElementOps.Move(d, "root.children[0]", "root", 2));
        Assert.Equal("root.children[1]", down);
        Assert.Equal("a", doc.Element("root.children[0]")!["id"]!.GetValue<string>());

        Assert.NotNull(doc.Mutate("Remove", d => ElementOps.Remove(d, "root.children[0]")));
        Assert.Equal("b", doc.Element("root.children[0]")!["id"]!.GetValue<string>());
    }

    [Fact]
    public void AnElementCannotMoveIntoItselfOrIntoALeaf()
    {
        var doc = Doc("""{ "type": "stack", "id": "s", "children": [ { "type": "text", "id": "t" } ] }, { "type": "text", "id": "leaf" }""");
        Assert.Null(doc.Mutate("Move", d => ElementOps.Move(d, "root.children[0]", "root.children[0].children[0]", 0)));
        Assert.Null(doc.Mutate("Move", d => ElementOps.Move(d, "root.children[0]", "root.children[1]", 0)));
        Assert.NotNull(ElementOps.LastError);
        Assert.False(doc.CanUndo); // failed edits leave no trace
    }

    [Fact]
    public void MovingBetweenParentsAdjustsThePathOfTheTarget()
    {
        var doc = Doc("""{ "type": "text", "id": "x" }, { "type": "stack", "id": "s", "children": [ { "type": "text", "id": "t" } ] }""");
        // x (index 0) goes into s (index 1, which becomes index 0 once x has left).
        var path = doc.Mutate("Move", d => ElementOps.Move(d, "root.children[0]", "root.children[1]", -1));
        Assert.Equal("root.children[0].children[1]", path);
        Assert.Equal("x", doc.Element(path!)!["id"]!.GetValue<string>());
    }

    [Fact]
    public void ASingleChildContainerRefusesASecondElement()
    {
        var doc = Doc("""{ "type": "scroll", "child": { "type": "text", "id": "t" } }""");
        Assert.Null(doc.Mutate("Add", d => ElementOps.Insert(d, "root.children[0]", -1, NewElements.Create("button"))));
        Assert.Contains("one element", doc.LastError);
        var empty = Doc("""{ "type": "scroll" }""");
        Assert.Equal("root.children[0].child", empty.Mutate("Add", d => ElementOps.Insert(d, "root.children[0]", -1, NewElements.Create("stack"))));
    }

    [Fact]
    public void WrapUnwrapAndDuplicate()
    {
        var doc = Doc();
        Assert.Equal("root.children[0]", doc.Mutate("Wrap", d => ElementOps.WrapIn(d, "root.children[0]", "panel")));
        Assert.Equal("panel", ElementOps.TypeOf(doc.Element("root.children[0]")!));
        Assert.Equal("a", doc.Element("root.children[0].children[0]")!["id"]!.GetValue<string>());
        Assert.Equal("root.children[0]", doc.Mutate("Unwrap", d => ElementOps.Unwrap(d, "root.children[0]")));
        Assert.Equal("a", doc.Element("root.children[0]")!["id"]!.GetValue<string>());

        Assert.Equal("root.children[1]", doc.Mutate("Duplicate", d => ElementOps.Duplicate(d, "root.children[0]")));
        Assert.Equal("a-2", doc.Element("root.children[1]")!["id"]!.GetValue<string>());
    }

    [Fact]
    public void RegionsCanBeExtractedRenamedInlinedAndProtected()
    {
        var doc = Doc("""{ "type": "stack", "id": "s", "width": 200, "children": [ { "type": "text", "text": "in" } ] }""");
        Assert.NotNull(doc.Mutate("Extract", d => ElementOps.ExtractRegion(d, "root.children[0]", "box")));
        Assert.Equal("box", doc.Element("root.children[0]")!["region"]!.GetValue<string>());
        Assert.Equal(200, doc.Element("root.children[0]")!["width"]!.GetValue<int>()); // placement stays with the use
        Assert.Null(doc.Element("regions.box")!["width"]);
        Assert.True(doc.IsValid, string.Join("; ", doc.Diagnostics));

        Assert.Null(doc.Mutate("Remove", d => ElementOps.Remove(d, "regions.box"))); // still used
        Assert.NotNull(doc.Mutate("Rename", d => ElementOps.RenameRegion(d, "box", "frame")));
        Assert.Equal("frame", doc.Element("root.children[0]")!["region"]!.GetValue<string>());
        Assert.True(doc.IsValid, string.Join("; ", doc.Diagnostics));

        Assert.NotNull(doc.Mutate("Inline", d => ElementOps.InlineRegion(d, "root.children[0]")));
        Assert.Equal("stack", ElementOps.TypeOf(doc.Element("root.children[0]")!));
        Assert.NotNull(doc.Mutate("Remove", d => ElementOps.Remove(d, "regions.frame")));
    }

    [Fact]
    public void UndoRedoAndCoalescing()
    {
        var doc = Doc();
        doc.Mutate("Width", d => { d["root"]!["children"]![0]!["width"] = 10; return "ok"; }, "w");
        doc.Mutate("Width", d => { d["root"]!["children"]![0]!["width"] = 20; return "ok"; }, "w");
        doc.Mutate("Width", d => { d["root"]!["children"]![0]!["width"] = 30; return "ok"; }, "w");
        Assert.Equal(30, doc.Element("root.children[0]")!["width"]!.GetValue<int>());
        Assert.True(doc.Undo());
        Assert.Null(doc.Element("root.children[0]")!["width"]); // one undo step for the whole run
        Assert.False(doc.CanUndo);
        Assert.True(doc.Redo());
        Assert.Equal(30, doc.Element("root.children[0]")!["width"]!.GetValue<int>());
    }

    [Fact]
    public void CodeEditsWithErrorsAreReportedAndDoNotBreakTheModel()
    {
        var doc = Doc();
        var good = doc.Text;
        doc.SetText(good.Replace("\"stack\"", "\"stack\", oops"));
        Assert.NotNull(doc.ParseError);
        Assert.False(doc.IsValid);
        Assert.Null(doc.Mutate("x", d => "ok")); // visual edits wait until the JSON is fixed
        Assert.NotNull(doc.GoodDocument); // the preview keeps the last good layout
        doc.SetText(good);
        Assert.Null(doc.ParseError);
        Assert.True(doc.IsValid);
    }

    [Fact]
    public void UnknownTypesAreErrorsAndBlockLiveApply()
    {
        var doc = Doc("""{ "type": "banana" }""");
        Assert.False(doc.IsValid);
        Assert.Contains(doc.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Path == "root.children[0].type");
        Assert.Equal(DiagnosticSeverity.Error, doc.WorstFor("root.children[0]"));
        Assert.Null(doc.WorstFor("root.children[1]"));
    }

    [Fact]
    public void ThePathIndexFindsTextForPaths()
    {
        var doc = Doc();
        var index = doc.Index;
        Assert.True(index.TryGet("root.children[1]", out var span));
        Assert.Contains("\"id\": \"b\"", doc.Text.Substring(span.Start, span.Length));
        Assert.True(index.TryGet("root.children[0].text", out var prop));
        Assert.StartsWith("\"text\"", doc.Text.Substring(prop.Start, prop.Length));
        Assert.Equal("root.children[1]", index.ElementAt(span.Start + 5));
        Assert.NotNull(index.Nearest("root.children[1].nothing"));
    }

    [Fact]
    public void ThePathIndexCountsCharactersNotBytes()
    {
        var doc = new EditorDocument(Layout("""{ "type": "text", "text": "→ ü" }, { "type": "text", "id": "after" }"""), "t");
        Assert.True(doc.Index.TryGet("root.children[1]", out var span));
        Assert.Contains("\"id\": \"after\"", doc.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void SavingIsAtomicAndReadable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "carvera-editor-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "sub", "x.json");
        LiveFile.Write(file, "{ }");
        LiveFile.Write(file, "{ \"a\": 1 }");
        Assert.Equal("{ \"a\": 1 }", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!)); // no temporary file left behind
    }
}
