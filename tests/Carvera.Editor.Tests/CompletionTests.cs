using Carvera.Editor.Model;
using Xunit;

namespace Carvera.Editor.Tests;

public class CompletionTests
{
    private static readonly CompletionNames Names = new(["runControls"], ["toolbar-button"]);

    private static CompletionResult? At(string text)
    {
        var caret = text.IndexOf('|');
        return CompletionEngine.Suggest(text.Remove(caret, 1), caret, Names);
    }

    [Fact]
    public void KeysOfAnElementDependOnItsType()
    {
        var result = At("""{ "root": { "type": "button", | } }""");
        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.Label == "command" && i.Insert == "\"command\": ");
        Assert.Contains(result.Items, i => i.Label == "width");
        Assert.DoesNotContain(result.Items, i => i.Label == "type"); // already there
        Assert.DoesNotContain(result.Items, i => i.Label == "orientation"); // a stack property
    }

    [Fact]
    public void ATypedKeyPrefixFiltersAndReplacesTheQuotedKey()
    {
        var text = """{ "root": { "type": "button", "com| } }""";
        var caret = text.IndexOf('|');
        var result = CompletionEngine.Suggest(text.Remove(caret, 1), caret, Names)!;
        Assert.All(result.Items, i => Assert.Contains("com", i.Label, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, i => i.Label == "command");
        Assert.Equal('"', text[result.Start]); // the opening quote is replaced along with the word
    }

    [Fact]
    public void EnumValuesAreOfferedInsideTheString()
    {
        var text = """{ "root": { "type": "stack", "orientation": "|" } }""";
        var caret = text.IndexOf('|');
        var result = CompletionEngine.Suggest(text.Remove(caret, 1), caret, Names)!;
        Assert.Equal(["horizontal", "vertical"], result.Items.Select(i => i.Insert).ToArray());
        Assert.Equal(caret, result.Start);
    }

    [Fact]
    public void CommandsAreOfferedForCommandProperties()
    {
        var result = At("""{ "root": { "type": "button", "command": "feed| } }""");
        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.Label == "feedHold");
    }

    [Fact]
    public void RegionsAndStylesComeFromTheDocument()
    {
        Assert.Contains(At("""{ "root": { "region": "| } }""")!.Items, i => i.Label == "runControls");
        Assert.Contains(At("""{ "root": { "type": "text", "class": "| } }""")!.Items, i => i.Label == "toolbar-button");
    }

    [Fact]
    public void ChildrenOfAContainerAreElementsToo()
    {
        var result = At("""{ "root": { "type": "stack", "children": [ { "type": "value", | } ] } }""");
        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.Label == "bind");
        Assert.Contains(result.Items, i => i.Label == "format");
    }

    [Fact]
    public void VisualsOfferTheStatesOfTheComponent()
    {
        var result = At("""{ "root": { "type": "button", "visuals": { | } } }""");
        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.Label == "pressed");
        Assert.Contains(result.Items, i => i.Label == "active");
    }

    [Fact]
    public void ArgumentNamesComeFromTheCommand()
    {
        var result = At("""{ "root": { "type": "button", "command": "jog", "args": { | } } }""");
        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.Label == "axis");
    }

    [Fact]
    public void ColoursOfferThemeTokens()
    {
        var result = At("""{ "styles": { "a": { "background": "@|" } } }""");
        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.Label == "@accent");
    }

    [Fact]
    public void NothingIsOfferedWhereNothingFits()
    {
        Assert.Null(At("""{ "root": { "type": "text", "text": "hello| } }"""));
    }
}
