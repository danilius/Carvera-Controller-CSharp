using System.Globalization;
using System.Text.RegularExpressions;
using Carvera.Core.State;

namespace Carvera.Core.Protocol;

public enum ResponseKind { Empty, Status, Diagnose, Wcs, Interior, JogStopped, Error, Message }

/// <summary>
/// Parses lines received from Carvera firmware and writes the results into a <see cref="StateStore"/>.
/// Ported from Controller.parseLine / parseBracketAngle / parseBigParentheses in the Python controller.
/// </summary>
public static partial class ResponseParser
{
    public static readonly string[] WcsNames = ["G54", "G55", "G56", "G57", "G58", "G59", "G59.1", "G59.2", "G59.3"];

    public static ResponseKind Classify(string line)
    {
        if (string.IsNullOrEmpty(line)) return ResponseKind.Empty;
        return line[0] switch
        {
            '<' when line.EndsWith('>') => ResponseKind.Status,
            '{' when line.EndsWith('}') => ResponseKind.Diagnose,
            '[' => ResponseKind.Wcs,
            '#' => ResponseKind.Interior,
            '^' when line.Length > 1 && line[1] == 'Y' => ResponseKind.JogStopped,
            _ when line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("alarm", StringComparison.OrdinalIgnoreCase) => ResponseKind.Error,
            _ => ResponseKind.Message,
        };
    }

    /// <summary>Splits "A:1,2|B:3" style fields (after the leading state for status reports).</summary>
    internal static Dictionary<string, double[]> ParseFields(IEnumerable<string> fields)
    {
        var result = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var colon = field.IndexOf(':');
            if (colon <= 0) continue;
            var parts = field[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries);
            var values = new double[parts.Length];
            var ok = true;
            for (var i = 0; i < parts.Length; i++)
                ok &= double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
            if (ok) result[field[..colon]] = values;
        }
        return result;
    }

    /// <summary>
    /// Parses a status report such as
    /// <c>&lt;Idle|MPos:68.998,-49.924,40.000,12.3|WPos:68.998,-49.924,40.000,5.3|R:0.0|G:0|F:0,0,100|S:0,0,100|T:1,-12.5|L:0&gt;</c>.
    /// </summary>
    public static bool TryParseStatus(string line, StateStore store)
    {
        if (Classify(line) != ResponseKind.Status) return false;
        var fields = line[1..^1].Split('|');
        if (fields.Length < 2) return false;
        var d = ParseFields(fields.Skip(1));
        if (!d.TryGetValue("MPos", out var mpos) || mpos.Length < 3 || !d.TryGetValue("WPos", out var wpos) || wpos.Length < 3)
            return false;

        using var _ = store.BeginBatch();
        store.Set(StatePaths.MachineState, fields[0]);

        var rotation = d.TryGetValue("R", out var r) && r.Length > 0 ? r[0] : 0.0;
        store.Set(StatePaths.WcsRotation, rotation);
        if (d.TryGetValue("G", out var g) && g.Length > 0) SetActiveWcs(store, (int)g[0]);

        if (d.TryGetValue("C", out var c) && c.Length >= 4)
        {
            store.Set(StatePaths.MachineModel, (int)c[0]);
            store.Set(StatePaths.InchMode, c[2] == 1);
            store.Set(StatePaths.AbsoluteMode, c[3] == 1);
        }

        double At(double[] a, int i) => i < a.Length ? a[i] : 0.0;
        string[] axes = StatePaths.Axes;
        for (var i = 0; i < axes.Length; i++)
        {
            store.Set(StatePaths.AxisMachine(axes[i]), At(mpos, i));
            store.Set(StatePaths.AxisWork(axes[i]), At(wpos, i));
        }

        // Work coordinate offsets, accounting for WCS rotation in XY (as the Python controller does).
        var rad = rotation * Math.PI / 180.0;
        store.Set(StatePaths.AxisOffset("x"), Math.Round(mpos[0] - (Math.Cos(rad) * wpos[0] - Math.Sin(rad) * wpos[1]), 3));
        store.Set(StatePaths.AxisOffset("y"), Math.Round(mpos[1] - (Math.Sin(rad) * wpos[0] + Math.Cos(rad) * wpos[1]), 3));
        store.Set(StatePaths.AxisOffset("z"), Math.Round(mpos[2] - wpos[2], 3));
        store.Set(StatePaths.AxisOffset("a"), Math.Round(At(mpos, 3) - At(wpos, 3), 3));

        if (d.TryGetValue("F", out var f) && f.Length >= 3)
        {
            store.Set(StatePaths.FeedCurrent, f[0]);
            store.Set(StatePaths.FeedTarget, f[1]);
            store.Set(StatePaths.FeedOverride, f[2]);
        }
        if (d.TryGetValue("S", out var s) && s.Length >= 3)
        {
            store.Set(StatePaths.SpindleCurrent, s[0]);
            store.Set(StatePaths.SpindleTarget, s[1]);
            store.Set(StatePaths.SpindleOverride, s[2]);
            if (s.Length > 3) store.Set(StatePaths.VacuumMode, s[3] != 0);
            if (s.Length > 4) store.Set(StatePaths.SpindleTemperature, s[4]);
        }
        if (d.TryGetValue("T", out var t) && t.Length >= 2)
        {
            store.Set(StatePaths.ToolCurrent, (int)t[0]);
            store.Set(StatePaths.ToolOffset, t[1]);
            store.Set(StatePaths.ToolTarget, t.Length > 2 ? (int)t[2] : -1);
        }
        else
        {
            store.Set(StatePaths.ToolCurrent, -1);
            store.Set(StatePaths.ToolOffset, 0.0);
            store.Set(StatePaths.ToolTarget, -1);
        }
        if (d.TryGetValue("W", out var w) && w.Length > 0) store.Set(StatePaths.ProbeVoltage, w[0]);
        if (d.TryGetValue("L", out var l) && l.Length >= 5)
        {
            store.Set(StatePaths.LaserMode, l[0] != 0);
            store.Set(StatePaths.LaserState, l[1] != 0);
            store.Set(StatePaths.LaserTesting, l[2] != 0);
            store.Set(StatePaths.LaserPower, l[3]);
            store.Set(StatePaths.LaserScale, l[4]);
        }
        if (d.TryGetValue("P", out var p) && p.Length >= 3)
        {
            store.Set(StatePaths.JobLines, (int)p[0]);
            store.Set(StatePaths.JobPercent, p[1]);
            store.Set(StatePaths.JobSeconds, (int)p[2]);
            store.Set(StatePaths.JobPlaying, p.Length < 4 || p[3] != 0);
        }
        else
        {
            store.Set(StatePaths.JobLines, -1);
            store.Set(StatePaths.JobPlaying, false);
        }
        store.Set(StatePaths.AtcState, d.TryGetValue("A", out var a) && a.Length > 0 ? (int)a[0] : 0);
        if (d.TryGetValue("H", out var h) && h.Length > 0) store.Set(StatePaths.HaltReason, (int)h[0]);
        return true;
    }

    private static readonly (string Key, int Index, string Path)[] DiagnoseMap =
    [
        ("S", 0, "switch.spindle"), ("S", 1, "level.spindle"),
        ("L", 0, "switch.laser"), ("L", 1, "level.laser"),
        ("F", 0, "switch.spindleFan"), ("F", 1, "level.spindleFan"),
        ("V", 0, "switch.vacuum"), ("V", 1, "level.vacuum"),
        ("G", 0, "switch.light"),
        ("T", 0, "switch.toolSensor"),
        ("R", 0, "switch.air"),
        ("C", 0, "switch.wpCharge"),
        ("E", 0, "sensor.xMin"), ("E", 1, "sensor.xMax"), ("E", 2, "sensor.yMin"),
        ("E", 3, "sensor.yMax"), ("E", 4, "sensor.zMax"), ("E", 5, "sensor.cover"),
        ("P", 0, "sensor.probe"), ("P", 1, "sensor.calibrate"),
        ("A", 0, "sensor.atcHome"), ("A", 1, "sensor.toolSensor"),
        ("I", 0, "sensor.estop"),
    ];

    /// <summary>Parses a diagnose report such as <c>{S:0,5000|L:0,0|F:1,0|V:0,1|G:0|T:0|E:0,0,0,0,0,0|P:0,0|A:1,0}</c>.</summary>
    public static bool TryParseDiagnose(string line, StateStore store)
    {
        if (Classify(line) != ResponseKind.Diagnose) return false;
        var d = ParseFields(line[1..^1].Split('|'));
        if (d.Count == 0) return false;
        using var _ = store.BeginBatch();
        foreach (var (key, index, path) in DiagnoseMap)
        {
            if (!d.TryGetValue(key, out var values) || index >= values.Length) continue;
            // switches and sensors are booleans; levels are numbers
            store.Set(path, path.StartsWith("level.", StringComparison.Ordinal) ? values[index] : values[index] != 0);
        }
        return true;
    }

    /// <summary>Handles "[current WCS: G55]" and "[G54:x,y,z,a,b,r]" responses.</summary>
    public static bool TryParseWcs(string line, StateStore store)
    {
        var current = CurrentWcsRegex().Match(line);
        if (current.Success)
        {
            var index = Array.IndexOf(WcsNames, current.Groups[1].Value);
            if (index >= 0) SetActiveWcs(store, index);
            return true;
        }
        var any = false;
        foreach (Match m in WcsEntryRegex().Matches(line))
        {
            var values = m.Groups[2].Value.Split(',');
            if (values.Length < 5) continue;
            var name = m.Groups[1].Value;
            var prefix = $"wcs.{name.ToLowerInvariant()}";
            using var _ = store.BeginBatch();
            string[] labels = ["x", "y", "z", "a", "b", "rotation"];
            for (var i = 0; i < Math.Min(values.Length, labels.Length); i++)
                if (double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    store.Set($"{prefix}.{labels[i]}", v);
            any = true;
        }
        return any;
    }

    /// <summary>Recognises "version = 1.0.2c", "model = CA1" style informational replies.</summary>
    public static bool TryParseInfo(string line, StateStore store)
    {
        var version = VersionRegex().Match(line);
        if (version.Success)
        {
            store.Set(StatePaths.FirmwareVersion, version.Groups[1].Value);
            store.Set(StatePaths.CommunityFirmware, version.Groups[2].Value.Contains('c'));
            return true;
        }
        var model = ModelRegex().Match(line);
        if (model.Success)
        {
            store.Set(StatePaths.MachineModelName, model.Groups[1].Value);
            return true;
        }
        return false;
    }

    private static void SetActiveWcs(StateStore store, int index)
    {
        store.Set(StatePaths.WcsActive, index);
        store.Set(StatePaths.WcsActiveName, index >= 0 && index < WcsNames.Length ? WcsNames[index] : $"#{index}");
    }

    [GeneratedRegex(@"\[current WCS: (G5[4-9](?:\.[1-3])?)\]")]
    private static partial Regex CurrentWcsRegex();
    [GeneratedRegex(@"\[(G5[4-9](?:\.[1-3])?):([^\]]+)\]")]
    private static partial Regex WcsEntryRegex();
    [GeneratedRegex(@"version = ([0-9]+\.[0-9]+\.[0-9]+)([a-zA-Z0-9\-_]*)")]
    private static partial Regex VersionRegex();
    [GeneratedRegex(@"model = ([a-zA-Z0-9]+)")]
    private static partial Regex ModelRegex();
}
