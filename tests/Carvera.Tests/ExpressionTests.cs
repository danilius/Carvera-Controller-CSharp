using Carvera.Core.Expressions;
using Xunit;

namespace Carvera.Tests;

public class ExpressionTests
{
    private static readonly Dictionary<string, object?> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["machine.state"] = "Alarm",
        ["feed.override"] = 120.0,
        ["connection.connected"] = true,
        ["tool.current"] = 3,
        ["switch.light"] = false,
    };

    private static object? Eval(string text) => Expression.Parse(text).Evaluate(p => Values.GetValueOrDefault(p));

    [Theory]
    [InlineData("machine.state == 'Alarm'", true)]
    [InlineData("machine.state == 'alarm'", true)]
    [InlineData("machine.state != \"Idle\"", true)]
    [InlineData("feed.override > 100 && connection.connected", true)]
    [InlineData("feed.override >= 121 or switch.light", false)]
    [InlineData("!switch.light", true)]
    [InlineData("not connection.connected", false)]
    [InlineData("tool.current == 3", true)]
    [InlineData("tool.current * 2 + 1 == 7", true)]
    [InlineData("(1 + 2) * 3 == 9", true)]
    [InlineData("missing.path == null", true)]
    [InlineData("missing.path", false)]
    public void EvaluatesBooleans(string text, bool expected) =>
        Assert.Equal(expected, Expression.Parse(text).EvaluateBool(p => Values.GetValueOrDefault(p)));

    [Fact]
    public void CollectsPaths() =>
        Assert.Equal(["feed.override", "machine.state"], Expression.Parse("machine.state == 'Run' && feed.override > 1").Paths.Order());

    [Fact]
    public void ArithmeticAndStrings()
    {
        Assert.Equal(240.0, Eval("feed.override * 2"));
        Assert.Equal("Tool 3", Eval("'Tool ' + tool.current"));
        Assert.Null(Eval("1 / 0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a ==")]
    [InlineData("(a")]
    [InlineData("'open")]
    [InlineData("a b")]
    [InlineData("machine.")]
    public void RejectsInvalid(string text) => Assert.False(Expression.TryParse(text, out _, out _));

    [Fact]
    public void TemplatesFormatValues()
    {
        var t = TextTemplate.Parse("Feed {feed.override:0}% · {machine.state} · {{literal}}");
        Assert.Equal("Feed 120% · Alarm · {literal}", t.Render(p => Values.GetValueOrDefault(p)));
        Assert.Equal(["feed.override", "machine.state"], t.Paths.Order());
        Assert.True(TextTemplate.Parse("plain").IsConstant);
        Assert.Equal("On", TextTemplate.Parse("{connection.connected}").Render(p => Values.GetValueOrDefault(p)));
        Assert.Equal("–", TextTemplate.Parse("{nothing.here:0.00}").Render(_ => null));
        Assert.False(TextTemplate.TryParse("{unclosed", out _, out _));
    }
}
