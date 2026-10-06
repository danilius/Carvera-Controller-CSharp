using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.Editor.Model;
using Carvera.Editor.Preview;
using Carvera.Editor.Views;
using Xunit;

namespace Carvera.Editor.Tests;

/// <summary>Drives the editor window like a person would: clicks, drags, typing.</summary>
public class InteractionTests
{
    private const string Safety = """{ "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }""";

    private static string TempLayout(string children, string name = "t")
    {
        var dir = Path.Combine(Path.GetTempPath(), "carvera-editor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".json");
        File.WriteAllText(file, $$"""{ "root": { "type": "stack", "children": [ {{children}}, {{Safety}} ] } }""");
        return file;
    }

    private static EditorWindow OpenFile(string file, double width = 1400, double height = 900)
    {
        var settings = new EditorSettings { WindowWidth = width, WindowHeight = height, PreviewSize = "Fill the pane" };
        var window = new EditorWindow([file], settings, new ControllerLink(), connect: false) { Width = width, Height = height };
        window.Show();
        Pump(window);
        return window;
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static async Task Settle(Window window, int ms = 400)
    {
        for (var i = 0; i < ms / 50; i++)
        {
            Pump(window);
            await Task.Delay(50);
        }
        Pump(window);
    }

    private static Point WindowPoint(EditorWindow window, Rect overlayRect, double fx = 0.5, double fy = 0.5) =>
        window.Preview.Overlay.TranslatePoint(new Point(overlayRect.X + overlayRect.Width * fx, overlayRect.Y + overlayRect.Height * fy), window)!.Value;

    private static Rect BoundsOf(EditorWindow window, string id)
    {
        var host = window.Preview.Session!.Hosts.First(h => h.Node.Id == id);
        return new Rect(host.TranslatePoint(new Point(0, 0), window.Preview.Overlay)!.Value, host.Bounds.Size);
    }

    private const string TwoTexts = """{ "type": "text", "id": "a", "text": "A", "height": 40 }, { "type": "text", "id": "b", "text": "B", "height": 40 }""";

    [AvaloniaFact]
    public async Task ClickingThePreviewSelectsTheElementUnderThePointer()
    {
        var window = OpenFile(TempLayout(TwoTexts));
        try
        {
            await Settle(window);
            var point = WindowPoint(window, BoundsOf(window, "b"));
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal("root.children[1]", window.Context.Selection);
            await Settle(window);
            Assert.NotNull(window.Preview.Overlay.Selection);
            Assert.True(window.Preview.Overlay.ShowHandles);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggingTheRightHandleSetsAWidth()
    {
        var window = OpenFile(TempLayout("""{ "type": "text", "id": "a", "text": "A", "width": 200, "height": 40, "align": "start" }"""));
        try
        {
            await Settle(window);
            window.Context.Select("root.children[0]");
            await Settle(window);
            var selection = window.Preview.Overlay.Selection!.Value;
            var start = window.Preview.Overlay.TranslatePoint(new Point(selection.Right, selection.Center.Y), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(30, 0));
            window.MouseMove(start + new Point(60, 0));
            window.MouseUp(start + new Point(60, 0), MouseButton.Left);
            await Settle(window);
            var width = window.Context.Doc.Element("root.children[0]")!["width"]!.GetValue<int>();
            Assert.InRange(width, 255, 265);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DroppingAPaletteItemBetweenTwoElementsInsertsItThere()
    {
        var window = OpenFile(TempLayout(TwoTexts));
        try
        {
            await Settle(window);
            var a = BoundsOf(window, "a");
            var b = BoundsOf(window, "b");
            var between = window.Preview.Overlay.TranslatePoint(new Point(a.Center.X, (a.Bottom + b.Top) / 2 + 2), window)!.Value;
            var target = (IDropTarget)window.Preview;
            var payload = new DragPayload("button", null, NewElements.Create("button"));
            Assert.True(target.Hover(between, payload));
            Assert.NotNull(window.Preview.Overlay.Drop);
            target.Drop(between, payload);
            await Settle(window);
            Assert.Equal("button", ElementOps.TypeOf(window.Context.Doc.Element("root.children[1]")!));
            Assert.Equal("b", window.Context.Doc.Element("root.children[2]")!["id"]!.GetValue<string>());
            Assert.Equal("root.children[1]", window.Context.Selection);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MovingAnElementInThePreviewReordersIt()
    {
        var window = OpenFile(TempLayout(TwoTexts));
        try
        {
            await Settle(window);
            var a = BoundsOf(window, "a");
            var b = BoundsOf(window, "b");
            var from = WindowPoint(window, a);
            var to = window.Preview.Overlay.TranslatePoint(new Point(b.Center.X, b.Bottom - 4), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 12), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            Assert.True(window.Drag.IsDragging, "the drag should have started");
            Assert.NotNull(window.Preview.Overlay.Drop);
            window.MouseUp(to, MouseButton.Left);
            await Settle(window);
            Assert.True(window.Context.Doc.CanUndo, "nothing was changed; last error: " + window.Context.Doc.LastError + " / " + ElementOps.LastError);
            Assert.Equal("b", window.Context.Doc.Element("root.children[0]")!["id"]!.GetValue<string>());
            Assert.Equal("a", window.Context.Doc.Element("root.children[1]")!["id"]!.GetValue<string>());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AValidEditIsWrittenToTheFileAndABrokenOneIsNot()
    {
        var file = TempLayout("""{ "type": "text", "id": "a", "text": "A" }""");
        var window = OpenFile(file);
        try
        {
            await Settle(window);
            window.Context.SetProperty("root.children[0]", "text", JsonValue.Create("Changed"));
            await Settle(window, 700);
            Assert.Contains("Changed", File.ReadAllText(file));
            Assert.False(window.Context.Doc.IsDirty);

            var good = File.ReadAllText(file);
            window.Context.SetProperty("root.children[0]", "type", JsonValue.Create("banana"));
            await Settle(window, 700);
            Assert.False(window.Context.Doc.IsValid);
            Assert.Equal(good, File.ReadAllText(file)); // the Controller keeps the last good layout
            Assert.True(window.Context.Doc.IsDirty);

            window.Context.Undo();
            await Settle(window, 700);
            Assert.True(window.Context.Doc.IsValid);
            Assert.False(window.Context.Doc.IsDirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TypingInTheInspectorEditsTheLayout()
    {
        var window = OpenFile(TempLayout("""{ "type": "text", "id": "a", "text": "Hello" }"""));
        try
        {
            await Settle(window);
            window.Context.Select("root.children[0]");
            await Settle(window);
            var box = window.InspectorPanel.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "Hello");
            box.Text = "Goodbye";
            await Settle(window, 900);
            Assert.Equal("Goodbye", window.Context.Doc.Element("root.children[0]")!["text"]!.GetValue<string>());
            Assert.Contains(window.Preview.Session!.Hosts, h => h.Node.GetString("text") == "Goodbye");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheCodeTabFollowsVisualEditsAndFeedsBack()
    {
        var window = OpenFile(TempLayout("""{ "type": "text", "id": "a", "text": "Hello" }"""));
        try
        {
            await Settle(window);
            window.Context.SetProperty("root.children[0]", "text", JsonValue.Create("Visual"));
            await Settle(window);
            Assert.Equal(window.Context.Doc.Text, window.Code.Editor.Text);
            Assert.Contains("Visual", window.Code.Editor.Text);

            // Typing in the code changes the layout after a pause.
            window.Code.Editor.Text = window.Code.Editor.Text.Replace("Visual", "Typed");
            await Settle(window, 800);
            Assert.Equal("Typed", window.Context.Doc.Element("root.children[0]")!["text"]!.GetValue<string>());
            Assert.Contains(window.Preview.Session!.Hosts, h => h.Node.GetString("text") == "Typed");

            // Broken code is reported and the preview keeps the last good layout.
            window.Code.Editor.Text = window.Code.Editor.Text.Replace("\"Typed\"", "\"Typed\" oops");
            await Settle(window, 800);
            Assert.NotNull(window.Context.Doc.ParseError);
            Assert.Contains(window.Preview.Session!.Hosts, h => h.Node.GetString("text") == "Typed");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheTreeListsTheElementsAndSelectingOneSelectsIt()
    {
        var window = OpenFile(TempLayout("""{ "type": "text", "id": "a", "text": "Alpha" }, { "type": "text", "id": "b", "text": "Beta" }"""));
        try
        {
            await Settle(window);
            var tree = window.TreeView.GetVisualDescendants().OfType<TreeView>().First();
            var beta = window.TreeView.GetVisualDescendants().OfType<TreeViewItem>().First(i => i.Tag as string == "root.children[1]");
            tree.SelectedItem = beta;
            await Settle(window);
            Assert.Equal("root.children[1]", window.Context.Selection);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AFormThatHasGoneStaleCannotWriteOverTheElementNowAtItsPath()
    {
        var window = OpenFile(TempLayout(TwoTexts));
        try
        {
            await Settle(window);
            window.Context.Select("root.children[0]");
            await Settle(window);
            var idBox = window.InspectorPanel.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "a");
            idBox.Text = "typed-into-a"; // the pause before it is applied has not ended yet
            // Meanwhile the element moves: what is at root.children[0] is now b.
            window.Context.Edit("Move", d => ElementOps.Move(d, "root.children[0]", "root", 2));
            await Settle(window, 900);
            Assert.Equal("b", window.Context.Doc.Element("root.children[0]")!["id"]!.GetValue<string>());
            Assert.Equal("a", window.Context.Doc.Element("root.children[1]")!["id"]!.GetValue<string>());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task APageAddedToATabContainerGetsAHeader()
    {
        var window = OpenFile(TempLayout("""{ "type": "tabs", "id": "t", "children": [ { "type": "text", "title": "One", "text": "1" } ] }"""));
        try
        {
            await Settle(window);
            window.Context.Actions.Add("stack", "root.children[0]");
            await Settle(window);
            Assert.Equal("Page 2", window.Context.Doc.Element("root.children[0].children[1]")!["title"]!.GetValue<string>());
            Assert.True(window.Context.Doc.IsValid, string.Join("; ", window.Context.Doc.Diagnostics));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClickingATabHeaderInThePreviewShowsThatPage()
    {
        var window = OpenFile(TempLayout("""{ "type": "tabs", "id": "t", "children": [ { "type": "text", "title": "One", "id": "p1", "text": "1" }, { "type": "text", "title": "Two", "id": "p2", "text": "2" } ] }"""));
        try
        {
            await Settle(window);
            var tabs = window.Preview.Session!.Hosts.First(h => h.Node.Id == "t").GetVisualDescendants().OfType<TabControl>().First();
            var second = tabs.Items.OfType<TabItem>().Skip(1).First();
            var origin = second.TranslatePoint(new Point(second.Bounds.Width / 2, second.Bounds.Height / 2), window.Preview.Overlay)!.Value;
            var point = window.Preview.Overlay.TranslatePoint(origin, window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await Settle(window);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal("root.children[0].children[1]", window.Context.Selection);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheScreenSizeAndZoomChangeTheFrame()
    {
        var file = TempLayout(TwoTexts);
        var settings = new EditorSettings { PreviewSize = "1920 × 1080 (Full HD)", PreviewZoom = "50%" };
        var window = new EditorWindow([file], settings, new ControllerLink(), connect: false) { Width = 1600, Height = 950 };
        window.Show();
        try
        {
            await Settle(window);
            var host = window.Preview.Session!.Root;
            var frame = host.GetVisualAncestors().OfType<Grid>().First(g => g.Width == 1920);
            Assert.Equal(1080, frame.Height);
            // At 50% the frame occupies half its size on screen.
            var size = frame.TransformToVisual(window)!.Value.Transform(new Point(1920, 0)).X - frame.TransformToVisual(window)!.Value.Transform(new Point(0, 0)).X;
            Assert.InRange(size, 950, 970);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheZoomButtonsWorkFromFitAndFromFill()
    {
        var settings = new EditorSettings { PreviewSize = "1920 × 1080 (Full HD)" };
        var window = new EditorWindow([TempLayout(TwoTexts)], settings, new ControllerLink(), connect: false) { Width = 1400, Height = 900 };
        window.Show();
        try
        {
            await Settle(window);
            Exception? crash = null;
            Dispatcher.UIThread.UnhandledException += (_, e) => { crash ??= e.Exception; e.Handled = true; };
            void Press(string label)
            {
                var b = window.Preview.GetVisualDescendants().OfType<Button>().First(x => x.Content as string == label);
                var p = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), window)!.Value;
                window.MouseDown(p, MouseButton.Left);
                window.MouseUp(p, MouseButton.Left);
            }
            Press("+");
            await Settle(window);
            Assert.Null(crash);
            Assert.True(window.Context.Settings.PreviewZoom != "Fit", "zoom setting still " + window.Context.Settings.PreviewZoom);
            Assert.True(window.Preview.Session!.Root.GetVisualAncestors().Any(v => v is LayoutTransformControl), string.Join(" > ", window.Preview.Session!.Root.GetVisualAncestors().Select(v => v.GetType().Name)) + " zoom=" + window.Context.Settings.PreviewZoom);
            Press("−");
            Press("−");
            await Settle(window);
            Assert.Null(crash);
            Assert.NotNull(window.Preview.Session);
        }
        finally { window.Close(); }
    }
}
