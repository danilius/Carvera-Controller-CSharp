using System.Globalization;
using System.Text.RegularExpressions;

namespace Carvera.Core.Gcode;

/// <summary>A tool declared or used in a G-code file.</summary>
public sealed record GcodeTool(int Number, string Description, double? Diameter, string? Type);

/// <summary>
/// One CAM operation (toolpath). Line numbers are 0-based indexes into <see cref="GcodeProgram.Lines"/>.
/// </summary>
public sealed record GcodeOperation(
    int Index,
    string Name,
    int StartLine,
    int EndLine,
    int? Tool,
    /// <summary>Line of this operation's own tool change, or -1 when it inherits the previous tool.</summary>
    int ToolChangeLine,
    /// <summary>Where a tool change for this operation would be inserted.</summary>
    int InsertLine);

/// <summary>
/// Finds operations and tools in G-code. Understands:
/// <list type="bullet">
/// <item>MakeraStudio markers: <c>;@MKR|TOOL|...</c>, <c>;@MKR|TOOLPATH|...</c>, <c>;@MKR|TOOLPATH_START|...</c>.</item>
/// <item>The Carvera post for Fusion: a header tool table <c>(T1  desc  D=3.175 ... - flat end mill)</c> and an
/// operation comment such as <c>(2D Adaptive1)</c> at the start of each section.</item>
/// <item>Anything else: one operation per tool change, or the whole program.</item>
/// </list>
/// </summary>
public static partial class GcodeStructure
{
    public static (IReadOnlyList<GcodeTool> Tools, IReadOnlyList<GcodeOperation> Operations) Analyze(IReadOnlyList<string> lines)
    {
        var declared = new Dictionary<int, GcodeTool>();
        List<(int Start, string Name)> starts;
        if (lines.Any(l => l.StartsWith(";@MKR|TOOLPATH_START", StringComparison.Ordinal)))
            starts = MakeraStarts(lines, declared);
        else
        {
            starts = FusionStarts(lines, declared);
            if (starts.Count == 0) starts = ToolChangeStarts(lines);
        }

        var operations = new List<GcodeOperation>();
        int? previousTool = null;
        for (var i = 0; i < starts.Count; i++)
        {
            var (start, name) = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1].Start - 1 : lines.Count - 1;
            var changeLine = -1;
            int? tool = previousTool;
            for (var l = start; l <= end; l++)
            {
                if (ToolChange(lines[l]) is { } t)
                {
                    changeLine = l;
                    tool = t;
                    break;
                }
            }
            var insert = IsComment(lines[start]) ? start + 1 : start;
            operations.Add(new GcodeOperation(i, name, start, end, tool, changeLine, insert));
            previousTool = tool;
        }

        var tools = new Dictionary<int, GcodeTool>(declared);
        foreach (var op in operations)
            if (op.Tool is { } n && !tools.ContainsKey(n)) tools[n] = new GcodeTool(n, "", null, null);
        return (tools.Values.OrderBy(t => t.Number).ToList(), operations);
    }

    // ------------------------------------------------------------------ tool changes

    /// <summary>The tool number of a tool change (T1M6, T1 M6, M6 T1...) on this line, or null.</summary>
    public static int? ToolChange(string line)
    {
        var code = StripComments(line).ToUpperInvariant();
        var m = ToolChangeRegex().Match(code);
        if (!m.Success) return null;
        var number = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    /// <summary>Replaces the tool number of the tool change on this line, keeping everything else.</summary>
    public static string ReplaceToolNumber(string line, int tool)
    {
        var comment = line.IndexOfAny([';', '(']);
        var code = comment >= 0 ? line[..comment] : line;
        var rest = comment >= 0 ? line[comment..] : "";
        var replaced = ToolChangeRegex().Replace(code, m =>
        {
            var g = m.Groups[1].Success ? m.Groups[1] : m.Groups[2];
            return m.Value[..(g.Index - m.Index)] + tool.ToString(CultureInfo.InvariantCulture) + m.Value[(g.Index - m.Index + g.Length)..];
        }, 1);
        return replaced + rest;
    }

    [GeneratedRegex(@"(?<![A-Z])T\s*(\d+)\s*M0*6(?!\d)|(?<![A-Z])M0*6\s*T\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ToolChangeRegex();

    public static string StripComments(string line)
    {
        var semicolon = line.IndexOf(';');
        if (semicolon >= 0) line = line[..semicolon];
        return ParenCommentRegex().Replace(line, " ");
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParenCommentRegex();

    public static bool IsComment(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && (t[0] == ';' || t[0] == '(' && t[^1] == ')');
    }

    private static string CommentText(string line)
    {
        var t = line.Trim();
        return t[0] == ';' ? t[1..].Trim() : t[1..^1].Trim();
    }

    private static bool IsCode(string line) => StripComments(line).Trim().Length > 0;

    // ------------------------------------------------------------------ MakeraStudio

    private static Dictionary<string, string> Fields(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in line.Split('|').Skip(2))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) result[part[..eq]] = part[(eq + 1)..];
        }
        return result;
    }

    private static List<(int, string)> MakeraStarts(IReadOnlyList<string> lines, Dictionary<int, GcodeTool> tools)
    {
        var names = new Dictionary<string, string>();
        var starts = new List<(int, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(";@MKR|", StringComparison.Ordinal)) continue;
            var f = Fields(line);
            if (line.StartsWith(";@MKR|TOOL|", StringComparison.Ordinal) && f.TryGetValue("number", out var n) && int.TryParse(n, out var number))
                tools[number] = new GcodeTool(number, f.GetValueOrDefault("name") ?? "", Number(f.GetValueOrDefault("diameter")), f.GetValueOrDefault("type"));
            else if (line.StartsWith(";@MKR|TOOLPATH|", StringComparison.Ordinal) && f.TryGetValue("number", out var tp))
                names[tp] = f.GetValueOrDefault("name") ?? $"Toolpath {tp}";
            else if (line.StartsWith(";@MKR|TOOLPATH_START", StringComparison.Ordinal))
            {
                var id = f.GetValueOrDefault("toolpath_number") ?? (starts.Count + 1).ToString(CultureInfo.InvariantCulture);
                starts.Add((i, names.GetValueOrDefault(id) ?? $"Toolpath {id}"));
            }
        }
        return starts;
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // ------------------------------------------------------------------ Fusion (Carvera post)

    [GeneratedRegex(@"^T(\d+)\s+(.*)$")]
    private static partial Regex FusionToolRegex();

    [GeneratedRegex(@"(?:^|\s)D=([-\d.]+)")]
    private static partial Regex DiameterRegex();

    /// <summary>Comments the post writes that are not operation names.</summary>
    [GeneratedRegex(@"^(ZMIN|Stop$|Tool Break Test|Calibrate TLO|Setup for tool change|setup for tool change|Paused|Manual Tool|Retracting|Optional Stop|as a result|Load tool number|Machine$|vendor:|model:|description:|\*\*\*|\d+$|T\d+\s)", RegexOptions.IgnoreCase)]
    private static partial Regex PostCommentRegex();

    private static List<(int, string)> FusionStarts(IReadOnlyList<string> lines, Dictionary<int, GcodeTool> tools)
    {
        var firstCode = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsComment(lines[i]))
            {
                var m = FusionToolRegex().Match(CommentText(lines[i]));
                if (m.Success && int.TryParse(m.Groups[1].Value, out var number) && firstCode < 0)
                {
                    var text = m.Groups[2].Value;
                    var d = DiameterRegex().Match(text);
                    var dash = text.LastIndexOf(" - ", StringComparison.Ordinal);
                    var description = Regex.Replace(d.Success ? text[..d.Index] : text, @"\s{2,}", " ").Trim();
                    tools[number] = new GcodeTool(number, description, d.Success ? Number(d.Groups[1].Value) : null, dash >= 0 ? text[(dash + 3)..].Trim() : null);
                }
            }
            else if (firstCode < 0 && IsCode(lines[i])) firstCode = i;
        }
        if (firstCode < 0) return [];

        var starts = new List<(int, string)>();
        string? previous = null;
        for (var i = firstCode; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (IsComment(line))
            {
                var text = CommentText(line);
                var afterCode = previous is not null && !IsComment(previous) && ToolChange(previous) is null;
                if (afterCode && text.Length > 0 && !PostCommentRegex().IsMatch(text)) starts.Add((i, text));
            }
            previous = line;
        }
        return starts;
    }

    // ------------------------------------------------------------------ fallback

    private static List<(int, string)> ToolChangeStarts(IReadOnlyList<string> lines)
    {
        var starts = new List<(int, string)>();
        for (var i = 0; i < lines.Count; i++)
            if (ToolChange(lines[i]) is { } t) starts.Add((i, $"Tool {t}"));
        if (starts.Count > 0) return starts;
        var first = Enumerable.Range(0, lines.Count).FirstOrDefault(i => IsCode(lines[i]), -1);
        return first < 0 ? [] : [(first, "Program")];
    }
}
