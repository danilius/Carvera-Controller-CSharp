using System.Text.Json.Nodes;
using Carvera.App.Services;
using Carvera.Editor.Model;
using Xunit;

namespace Carvera.Editor.Tests;

/// <summary>The editor's end of the link against the Controller's end, over a real pipe.</summary>
public class LinkTests
{
    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public async Task TheEditorFindsTheControllerAndTheyExchangeMessages()
    {
        var pipe = "carvera-link-test-" + Guid.NewGuid().ToString("N");
        using var server = new EditorLink(pipe);
        var received = new List<JsonObject>();
        server.Message += m => { lock (received) received.Add(m); };
        server.Start();
        using var client = new ControllerLink(pipe);
        client.Start();

        await WaitUntil(() => client.Connected);
        server.Send(new JsonObject { ["event"] = "layout", ["name"] = "desktop2", ["file"] = "x.json" });
        await WaitUntil(() => client.Layout == "desktop2");
        Assert.Equal("x.json", client.LayoutFile);

        client.Reveal("root.children[3]");
        client.SetPicking(true);
        client.Load("another");
        await WaitUntil(() => { lock (received) return received.Count >= 3; });
        lock (received)
        {
            Assert.Equal("reveal", received[0]["cmd"]!.GetValue<string>());
            Assert.Equal("root.children[3]", received[0]["path"]!.GetValue<string>());
            Assert.Equal("inspect", received[1]["cmd"]!.GetValue<string>());
            Assert.Equal("another", received[2]["layout"]!.GetValue<string>());
        }

        string? picked = null;
        client.Picked += (path, _) => picked = path;
        server.Send(new JsonObject { ["event"] = "picked", ["layout"] = "desktop2", ["path"] = "root.children[1]" });
        await WaitUntil(() => picked is not null);
        Assert.Equal("root.children[1]", picked);
    }

    [Fact]
    public async Task TheEditorCarriesOnWhenNoControllerIsRunningAndConnectsWhenOneStarts()
    {
        var pipe = "carvera-link-test-" + Guid.NewGuid().ToString("N");
        using var client = new ControllerLink(pipe);
        client.Start();
        await Task.Delay(300);
        Assert.False(client.Connected);
        client.Reveal("root"); // harmless with nobody there

        using var server = new EditorLink(pipe);
        server.Start();
        await WaitUntil(() => client.Connected, 8000);
    }
}
