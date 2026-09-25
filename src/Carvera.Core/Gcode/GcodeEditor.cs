using System.Globalization;
using System.Text.RegularExpressions;

namespace Carvera.Core.Gcode;

/// <summary>Edits G-code text while keeping the machine state around the edit consistent.</summary>
public static partial class GcodeEditor
{
    /// <summary>
    /// Makes operation <paramref name="operationIndex"/> use <paramref name="tool"/>.
    /// <list type="bullet">
    /// <item>If the operation has its own tool change, only its tool number changes (other parameters stay).</item>
    /// <item>Otherwise a tool change is inserted at the start of the operation, followed by the spindle and
    /// coolant commands that were active there, because a tool change stops the spindle.</item>
    /// <item>If the next operation relied on inheriting the previous tool, it gets an explicit change back to
    /// that tool (again with spindle and coolant), so later operations are unaffected.</item>
    /// </list>
    /// </summary>
    public static List<string> ChangeOperationTool(IReadOnlyList<string> lines, IReadOnlyList<GcodeOperation> operations, int operationIndex, int tool)
    {
        if (operationIndex < 0 || operationIndex >= operations.Count) throw new ArgumentOutOfRangeException(nameof(operationIndex));
        if (tool < 0) throw new ArgumentOutOfRangeException(nameof(tool), "Tool numbers cannot be negative.");
        var op = operations[operationIndex];
        var result = lines.ToList();
        if (op.Tool == tool) return result;

        // Work from the end of the file backwards so earlier line indexes stay valid.
        if (operationIndex + 1 < operations.Count)
        {
            var next = operations[operationIndex + 1];
            if (next.ToolChangeLine < 0 && next.Tool is { } inherited && inherited != tool)
                result.InsertRange(next.InsertLine, ToolChangeBlock(lines, next.InsertLine, inherited));
        }

        if (op.ToolChangeLine >= 0) result[op.ToolChangeLine] = GcodeStructure.ReplaceToolNumber(lines[op.ToolChangeLine], tool);
        else result.InsertRange(op.InsertLine, ToolChangeBlock(lines, op.InsertLine, tool));
        return result;
    }

    /// <summary>A tool change plus the spindle and coolant state active just before <paramref name="beforeLine"/>.</summary>
    internal static List<string> ToolChangeBlock(IReadOnlyList<string> lines, int beforeLine, int tool)
    {
        var block = new List<string> { $"T{tool.ToString(CultureInfo.InvariantCulture)} M6" };
        var (spindle, speed, coolant) = ModalState(lines, beforeLine);
        if (spindle is not null) block.Add(speed is { } s ? $"S{s.ToString("0.###", CultureInfo.InvariantCulture)} {spindle}" : spindle);
        if (coolant is not null) block.Add(coolant);
        return block;
    }

    /// <summary>Spindle direction (M3/M4 or null when stopped), last S value and coolant (M7/M8 or null) before a line.</summary>
    internal static (string? Spindle, double? Speed, string? Coolant) ModalState(IReadOnlyList<string> lines, int beforeLine)
    {
        string? spindle = null, coolant = null;
        double? speed = null;
        for (var i = 0; i < Math.Min(beforeLine, lines.Count); i++)
        {
            var code = GcodeStructure.StripComments(lines[i]).ToUpperInvariant();
            foreach (Match m in WordRegex().Matches(code))
            {
                var letter = m.Groups[1].Value[0];
                if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                if (letter == 'S') speed = value;
                else if (letter == 'M')
                {
                    switch (value)
                    {
                        case 3: spindle = "M3"; break;
                        case 4: spindle = "M4"; break;
                        case 5 or 6: spindle = null; break; // the Carvera stops the spindle for a tool change
                        case 7: coolant = "M7"; break;
                        case 8: coolant = "M8"; break;
                        case 9: coolant = null; break;
                    }
                }
            }
        }
        return (spindle, speed, coolant);
    }

    [GeneratedRegex(@"([A-Z])\s*([-+]?\d*\.?\d+)")]
    private static partial Regex WordRegex();
}
