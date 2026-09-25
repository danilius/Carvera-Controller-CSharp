using System.Globalization;
using System.Text;

namespace Carvera.Core.Expressions;

/// <summary>
/// Text with embedded expressions: <c>"Feed {feed.current:0} mm/min"</c>. Each <c>{...}</c> holds an
/// expression optionally followed by <c>:format</c> (a .NET numeric format such as 0.000 or 0%).
/// Use <c>{{</c> and <c>}}</c> for literal braces.
/// </summary>
public sealed class TextTemplate
{
    private readonly List<(string? Text, Expression? Expr, string? Format)> _parts = [];

    private TextTemplate(string source) => Source = source;

    public string Source { get; }
    public bool IsConstant => _parts.All(p => p.Expr is null);
    public IReadOnlyCollection<string> Paths => _parts.Where(p => p.Expr is not null).SelectMany(p => p.Expr!.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static TextTemplate Parse(string source)
    {
        var template = new TextTemplate(source);
        var literal = new StringBuilder();
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '{' && i + 1 < source.Length && source[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < source.Length && source[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c != '{') { literal.Append(c); continue; }
            var close = FindClose(source, i + 1);
            if (close < 0) throw new ExpressionException("Missing '}' in text", i);
            if (literal.Length > 0) { template._parts.Add((literal.ToString(), null, null)); literal.Clear(); }
            var body = source[(i + 1)..close];
            var colon = FindFormatColon(body);
            var exprText = colon >= 0 ? body[..colon] : body;
            var format = colon >= 0 ? body[(colon + 1)..] : null;
            template._parts.Add((null, Expression.Parse(exprText), format));
            i = close;
        }
        if (literal.Length > 0) template._parts.Add((literal.ToString(), null, null));
        return template;
    }

    public static bool TryParse(string source, out TextTemplate? template, out string? error)
    {
        try
        {
            template = Parse(source);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            template = null;
            error = $"{ex.Message} (at character {ex.Position + 1})";
            return false;
        }
    }

    public string Render(Func<string, object?> resolve)
    {
        var sb = new StringBuilder();
        foreach (var (text, expr, format) in _parts)
        {
            if (text is not null) { sb.Append(text); continue; }
            sb.Append(FormatValue(expr!.Evaluate(resolve), format));
        }
        return sb.ToString();
    }

    public static string FormatValue(object? value, string? format)
    {
        if (value is null) return "–";
        if (!string.IsNullOrEmpty(format) && Expression.ToNumber(value) is { } number && value is not bool)
            return number.ToString(format, CultureInfo.InvariantCulture);
        return value switch
        {
            bool b => b ? "On" : "Off",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    private static int FindClose(string s, int start)
    {
        char? quote = null;
        for (var i = start; i < s.Length; i++)
        {
            if (quote is not null) { if (s[i] == quote) quote = null; continue; }
            if (s[i] is '\'' or '"') quote = s[i];
            else if (s[i] == '}') return i;
        }
        return -1;
    }

    private static int FindFormatColon(string body)
    {
        char? quote = null;
        for (var i = 0; i < body.Length; i++)
        {
            if (quote is not null) { if (body[i] == quote) quote = null; continue; }
            if (body[i] is '\'' or '"') quote = body[i];
            else if (body[i] == ':') return i;
        }
        return -1;
    }
}
