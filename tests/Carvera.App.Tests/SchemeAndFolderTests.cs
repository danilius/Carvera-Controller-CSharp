using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Carvera.App.Layout;
using Carvera.App.Services;
using Xunit;

namespace Carvera.App.Tests;

public class SchemeAndFolderTests
{
    public SchemeAndFolderTests() =>
        Environment.SetEnvironmentVariable("CARVERA_CS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "carvera-cs-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void EverySchemeFillsAllTenOperationColours()
    {
        Assert.Equal(ColorSchemes.Layout, ColorSchemes.Names[0]);
        foreach (var name in ColorSchemes.Names.Skip(1))
        {
            var tokens = ColorSchemes.Tokens(name);
            Assert.Equal(Theme.OperationColorCount, tokens.Count);
            for (var i = 1; i <= Theme.OperationColorCount; i++) Assert.True(Color.TryParse(tokens[$"operation{i}"], out _), $"{name} operation{i}");
        }
        Assert.Empty(ColorSchemes.Tokens(ColorSchemes.Layout));
        Assert.Empty(ColorSchemes.Tokens("no such scheme"));
    }

    [AvaloniaFact]
    public void ASchemeReplacesTheLayoutsOperationColours()
    {
        var json = """{ "theme": { "operation1": "#010203" }, "root": { "type": "stack", "children": [ { "type": "button", "command": "feedHold" }, { "type": "button", "command": "stop" }, { "type": "button", "command": "reset" } ] } }""";
        using (var plain = new Harness(json))
            Assert.Equal(Color.Parse("#010203"), plain.Session.Context.Theme.OperationColor(0));
        var settings = new Settings { GcodeColorScheme = "Colour-blind safe" };
        using var scheme = new Harness(json, settings: settings);
        Assert.Equal(Color.Parse("#0072B2"), scheme.Session.Context.Theme.OperationColor(0));
    }

    [Fact]
    public void RecentFoldersKeepTheLatestFirstWithoutRepeats()
    {
        var settings = new Settings();
        var folders = Enumerable.Range(0, 7).Select(i => Path.Combine(Path.GetTempPath(), "carvera-recent-" + i)).ToList();
        foreach (var folder in folders) settings.RememberFolder(folder);
        settings.RememberFolder(folders[4].ToUpperInvariant());
        settings.RememberFolder(null);
        settings.RememberFolder("  ");

        Assert.Equal(Settings.RecentFolderCount, settings.RecentFolders.Count);
        Assert.Equal(folders[4].ToUpperInvariant(), settings.RecentFolders[0]);
        Assert.Equal(folders[6], settings.RecentFolders[1]);
        Assert.Single(settings.RecentFolders, f => f.Equals(folders[4], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheLastFolderSkipsOnesThatNoLongerExist()
    {
        var existing = Directory.CreateTempSubdirectory("carvera-last-").FullName;
        try
        {
            var settings = new Settings();
            settings.RememberFolder(existing);
            settings.RememberFolder(Path.Combine(existing, "gone"));
            Assert.Equal(existing, settings.LastFolder);
        }
        finally { Directory.Delete(existing); }
    }
}
