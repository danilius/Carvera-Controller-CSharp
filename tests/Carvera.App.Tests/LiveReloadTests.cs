using System.IO.Pipes;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Carvera.App.Layout;
using Carvera.App.Services;
using Carvera.App.Shell;
using Carvera.Core.State;
using Xunit;

namespace Carvera.App.Tests;

/// <summary>What the layout editor relies on in the Controller: files saved in the user's folder take over live, view state survives, and the editor link works.</summary>
public class LiveReloadTests
{
    private const string Safety = """{ "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" }""";

    public LiveReloadTests() =>
        Environment.SetEnvironmentVariable("CARVERA_CS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "carvera-cs-tests", Guid.NewGuid().ToString("N")));

    private static async Task Settle(MainWindow window, int milliseconds = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            window.Services.Binder.Flush();
            await Task.Delay(15);
        }
        window.UpdateLayout();
    }

    private static string UserFolder()
    {
        var folder = Path.Combine(Settings.Directory, "layouts");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Layout(string title, int selectedTab = 0) => $$"""
        { "name": "{{title}}", "root": { "type": "stack", "children": [
          { "type": "tabs", "id": "pages", "selected": {{selectedTab}}, "children": [
            { "type": "text", "title": "One", "id": "one", "text": "{{title}} 1" },
            { "type": "stack", "title": "Two", "id": "two", "children": [ { "type": "text", "id": "deep", "text": "{{title}} 2" } ] },
            { "type": "text", "title": "Three", "text": "3" } ] },
          {{Safety}} ] } }
        """;

    /// <summary>Waits for a task while the window's dispatcher keeps running (a plain await would never be resumed by the headless dispatcher).</summary>
    private static async Task<T> Await<T>(MainWindow window, Task<T> task, int milliseconds = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!task.IsCompleted && DateTime.UtcNow < until) await Settle(window, 30);
        Assert.True(task.IsCompleted, "timed out waiting");
        return task.Result;
    }

    private static async Task WaitFor(MainWindow window, Func<bool> condition, int milliseconds = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until && !condition()) await Settle(window, 50);
    }

    [AvaloniaFact]
    public async Task AFileSavedInTheUserFolderReplacesTheRunningLayoutAndKeepsTheTab()
    {
        File.WriteAllText(Path.Combine(UserFolder(), "live.json"), Layout("First"));
        var window = new MainWindow(["--layout", "live", "--no-editor-link"]) { Width = 900, Height = 600 };
        window.Show();
        await Settle(window);
        Assert.Contains(window.Session!.Hosts, h => h.Node.GetString("text") == "First 1");

        // The user goes to the second tab and drags nothing else.
        var tabs = window.Session.Hosts.Single(h => h.Node.Id == "pages").GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 1;
        await Settle(window);

        // The editor saves a change.
        File.WriteAllText(Path.Combine(UserFolder(), "live.json"), Layout("Second"));
        await WaitFor(window, () => window.Session!.Hosts.Any(h => h.Node.GetString("text") == "Second 2"));
        Assert.Contains(window.Session!.Hosts, h => h.Node.GetString("text") == "Second 2");
        var tabsAfter = window.Session.Hosts.Single(h => h.Node.Id == "pages").GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal(1, tabsAfter.SelectedIndex);

        // A broken save is held back and the last good layout stays.
        File.WriteAllText(Path.Combine(UserFolder(), "live.json"), """{ "root": { "type": "stak" } }""");
        await WaitFor(window, () => window.HasBanner("errors"));
        Assert.True(window.HasBanner("errors"));
        Assert.Contains(window.Session.Hosts, h => h.Node.GetString("text") == "Second 2");

        // Fixing it applies again and clears the banner.
        File.WriteAllText(Path.Combine(UserFolder(), "live.json"), Layout("Third"));
        await WaitFor(window, () => window.Session!.Hosts.Any(h => h.Node.GetString("text") == "Third 2"));
        Assert.False(window.HasBanner("errors"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ACustomisedCopyInTheUserFolderTakesOverFromABuiltInLayout()
    {
        var window = new MainWindow(["--layout", "desktop", "--no-editor-link"]) { Width = 1400, Height = 900 };
        window.Show();
        await Settle(window);
        Assert.Equal("desktop", window.Services.State.Get<string>(StatePaths.LayoutName));
        var original = window.Session!.Document.Description;

        // The editor saves "desktop" into the user's folder with a new description; the Controller picks it up without being told.
        var shipped = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "layouts", "desktop.json"));
        var node = JsonNode.Parse(shipped, documentOptions: Carvera.Layout.LayoutLoader.DocumentOptions)!.AsObject();
        node["description"] = "edited live";
        File.WriteAllText(Path.Combine(UserFolder(), "desktop.json"), node.ToJsonString());
        await WaitFor(window, () => window.Session!.Document.Description == "edited live");
        Assert.Equal("edited live", window.Session!.Document.Description);
        Assert.NotEqual(original, window.Session.Document.Description);

        // Removing the copy returns to the built-in layout.
        File.Delete(Path.Combine(UserFolder(), "desktop.json"));
        await WaitFor(window, () => window.Session!.Document.Description == original);
        Assert.Equal(original, window.Session!.Document.Description);
        window.Close();
    }

    [AvaloniaFact]
    public async Task UnrelatedFilesInTheFolderDoNotRebuildTheLayout()
    {
        File.WriteAllText(Path.Combine(UserFolder(), "live.json"), Layout("First"));
        var window = new MainWindow(["--layout", "live", "--no-editor-link"]) { Width = 900, Height = 600 };
        window.Show();
        await Settle(window);
        var session = window.Session;
        File.WriteAllText(Path.Combine(UserFolder(), "other.json"), Layout("Other"));
        File.WriteAllText(Path.Combine(UserFolder(), ".live.json.tmp"), "partial");
        await Settle(window, 800);
        Assert.Same(session, window.Session);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheEditorCanRevealAnElementOnAnotherTab()
    {
        File.WriteAllText(Path.Combine(UserFolder(), "live.json"), Layout("First"));
        var pipe = "carvera-test-" + Guid.NewGuid().ToString("N");
        var window = new MainWindow(["--layout", "live", "--editor-pipe", pipe]) { Width = 900, Height = 600 };
        window.Show();
        await Settle(window);

        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Await(window, Task.Run(async () => { await client.ConnectAsync(3000); return true; }));
        using var reader = new StreamReader(client);
        using var writer = new StreamWriter(client) { AutoFlush = true };

        // The Controller says which layout it shows.
        var hello = JsonNode.Parse((await Await(window, Task.Run(reader.ReadLineAsync)))!)!.AsObject();
        Assert.Equal("layout", hello["event"]!.GetValue<string>());
        Assert.Equal("live", hello["name"]!.GetValue<string>());

        // Reveal an element on the second tab: the Controller switches to it.
        var deep = window.Session!.Hosts.Single(h => h.Node.Id == "deep").Node.Path;
        await Await(window, Task.Run(async () => { await writer.WriteLineAsync(new JsonObject { ["cmd"] = "reveal", ["path"] = deep }.ToJsonString()); return true; }));
        var tabs = window.Session.Hosts.Single(h => h.Node.Id == "pages").GetVisualDescendants().OfType<TabControl>().First();
        await WaitFor(window, () => tabs.SelectedIndex == 1);
        Assert.Equal(1, tabs.SelectedIndex);

        // Pick mode: a click on an element is reported instead of pressing it.
        await Await(window, Task.Run(async () => { await writer.WriteLineAsync(new JsonObject { ["cmd"] = "inspect", ["on"] = true }.ToJsonString()); return true; }));
        await Settle(window);
        var target = window.Session.Hosts.Single(h => h.Node.Id == "deep");
        var point = target.TranslatePoint(new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, point, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, point, Avalonia.Input.MouseButton.Left);
        var picked = JsonNode.Parse((await Await(window, Task.Run(reader.ReadLineAsync)))!)!.AsObject();
        Assert.Equal("picked", picked["event"]!.GetValue<string>());
        Assert.Equal(deep, picked["path"]!.GetValue<string>());

        // The editor can also ask the Controller to show a different layout.
        File.WriteAllText(Path.Combine(UserFolder(), "another.json"), Layout("Another"));
        await Await(window, Task.Run(async () => { await writer.WriteLineAsync(new JsonObject { ["cmd"] = "inspect", ["on"] = false }.ToJsonString()); return true; }));
        await Await(window, Task.Run(async () => { await writer.WriteLineAsync(new JsonObject { ["cmd"] = "load", ["layout"] = "another" }.ToJsonString()); return true; }));
        await WaitFor(window, () => window.Services.State.Get<string>(StatePaths.LayoutName) == "another");
        Assert.Equal("another", window.Services.State.Get<string>(StatePaths.LayoutName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ViewStateSurvivesAScrollAndACollapsedPanel()
    {
        var layout = $$"""
            { "root": { "type": "stack", "children": [
              { "type": "panel", "id": "p", "title": "Folded", "collapsible": true, "children": [ { "type": "text", "text": "inside" } ] },
              {{Safety}} ] } }
            """;
        File.WriteAllText(Path.Combine(UserFolder(), "fold.json"), layout);
        var window = new MainWindow(["--layout", "fold", "--no-editor-link"]) { Width = 700, Height = 400 };
        window.Show();
        await Settle(window);
        var panel = window.Session!.Hosts.Single(h => h.Node.Id == "p");
        Assert.NotNull(panel.CollapseToggle);
        panel.CollapseToggle!(true);
        Assert.True(panel.HasState("collapsed"));

        File.WriteAllText(Path.Combine(UserFolder(), "fold.json"), layout.Replace("inside", "changed"));
        await WaitFor(window, () => window.Session!.Hosts.Any(h => h.Node.GetString("text") == "changed"));
        Assert.True(window.Session!.Hosts.Single(h => h.Node.Id == "p").HasState("collapsed"));
        window.Close();
    }
}
