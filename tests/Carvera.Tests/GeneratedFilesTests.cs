using Carvera.Core.Commands;
using Carvera.Layout;
using Xunit;

namespace Carvera.Tests;

/// <summary>
/// The schema and reference are generated from the catalogs. Run the tests with UPDATE_GENERATED=1 to
/// rewrite them after changing components or commands.
/// </summary>
public class GeneratedFilesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CarveraController.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Theory]
    [InlineData("schema/layout.schema.json")]
    [InlineData("docs/layout-reference.md")]
    public void GeneratedFileIsUpToDate(string relative)
    {
        var commands = AppCommands.CreateCatalog();
        var expected = relative.EndsWith(".json") ? SchemaGenerator.Schema(commands) : SchemaGenerator.Reference(commands);
        var path = Path.Combine(RepoRoot(), relative);
        if (Environment.GetEnvironmentVariable("UPDATE_GENERATED") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
        }
        Assert.True(File.Exists(path), $"{relative} is missing; run the tests with UPDATE_GENERATED=1.");
        Assert.True(File.ReadAllText(path).ReplaceLineEndings() == expected.ReplaceLineEndings(), $"{relative} is out of date; run the tests with UPDATE_GENERATED=1.");
    }
}
