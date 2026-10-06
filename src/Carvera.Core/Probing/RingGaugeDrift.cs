using System.Globalization;
using Carvera.Core.State;

namespace Carvera.Core.Probing;

/// <summary>The stored ring-gauge correction: how far the probe's centre reads from the marked cable position.</summary>
public sealed record DriftCorrection(bool Enabled, double X, double Y);

/// <summary>Where the Z probe sits: an offset from the work origin ("work") or the open program's lower-left corner ("path").</summary>
public sealed record ZProbeSetting(string Origin, double X, double Y)
{
    public static readonly ZProbeSetting Default = new("work", 0, 0);
    public bool FromWorkOrigin => !Origin.Equals("path", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where probing preferences live: the values typed into each family, the drift correction and the Z probe position.</summary>
public interface IProbeStore
{
    IReadOnlyDictionary<string, string> ProbeSettings(string family);
    DriftCorrection Drift { get; set; }
    ZProbeSetting ZProbe { get; set; }
    Job.JobSettings Job { get; set; }
}

/// <summary>An in-memory store, for tests and for validating layouts.</summary>
public sealed class MemoryProbeStore : IProbeStore
{
    public Dictionary<string, Dictionary<string, string>> Families { get; } = [];
    public IReadOnlyDictionary<string, string> ProbeSettings(string family) =>
        Families.TryGetValue(family, out var values) ? values : new Dictionary<string, string>();
    public DriftCorrection Drift { get; set; } = new(false, 0, 0);
    public ZProbeSetting ZProbe { get; set; } = ZProbeSetting.Default;
    public Job.JobSettings Job { get; set; } = new();
}

/// <summary>What three ring-gauge measurements say about the probe.</summary>
public sealed record DriftResult(double MaxShift, int First, int Second, double LowerBound, double CorrectionX, double CorrectionY, string Quality);

/// <summary>
/// The ring-gauge drift check of the Python controller's probing addon: the probe measures a clamped ring gauge three
/// times, turned about 120 degrees between measurements. The spread shows how far the probe's electrical centre wanders
/// as it turns; the average of the three centres, relative to the first (the marked cable position), is a correction
/// to apply after later XY-zeroing probes.
/// </summary>
public static class RingGaugeDrift
{
    public static readonly (string Title, string Body)[] Steps =
    [
        ("Marked cable position", "Rotate the probe to the USB cable position you will use for normal probing."),
        ("Rotate 120 degrees left", "Return to the marked cable position, rotate the probe roughly 120 degrees to the left, then probe the ring again."),
        ("Rotate 120 degrees right", "Return to the marked cable position, rotate the probe roughly 120 degrees to the right, then probe the ring one more time."),
    ];

    public const string Introduction = "Clamp the ring gauge so it cannot move.";

    /// <summary>Parameters of the probe-tip family that a ring-gauge probe copies; the tip diameter is added when the tip is to be compensated.</summary>
    private static readonly string[] CopiedCodes = ["X", "Y", "H", "F", "K", "L", "R", "Q", "C", "I"];

    /// <summary>The bore-centre command for one measurement, without zeroing XY.</summary>
    public static ProbeResult BuildProbe(IReadOnlyDictionary<string, string> probeTipSettings, bool includeTipDiameter)
    {
        var config = new Dictionary<string, string>();
        foreach (var code in CopiedCodes)
            if (probeTipSettings.TryGetValue(code, out var value)) config[code] = value;
        if (includeTipDiameter && probeTipSettings.TryGetValue("D", out var diameter)) config["D"] = diameter;
        config["S"] = "0";
        return ProbeOperations.Build("bore", "CenterBore", config);
    }

    public static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static DriftResult? Analyse(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3) return null;
        double max = 0, min = double.MaxValue;
        int first = 0, second = 0;
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
            {
                var d = Distance(points[i], points[j]);
                if (d > max) { max = d; first = i; second = j; }
                min = Math.Min(min, d);
            }
        var centreX = points.Average(p => p.X);
        var centreY = points.Average(p => p.Y);
        string quality;
        if (max <= 0) quality = "Repeat the check if the result is close to your tolerance limit.";
        else if (min / max < 0.35) quality = "Result quality: rough. Rotations may not have been close to 120 degrees, but correction was still estimated.";
        else if (max < 0.02) quality = "Result quality: small shift. Correction may be dominated by probe repeatability.";
        else quality = "Result quality: usable for a practical drift correction.";
        return new DriftResult(max, first, second, max / 2, centreX - points[0].X, centreY - points[0].Y, quality);
    }

    public static string FormatPoints(IReadOnlyList<(double X, double Y)> points) =>
        points.Count == 0 ? "No measurements yet"
            : string.Join('\n', points.Select((p, i) => string.Create(CultureInfo.InvariantCulture, $"{Steps[i].Title}: X{p.X:0.0000} Y{p.Y:0.0000}")));

    public static string Format(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"X{x:0.0000} Y{y:0.0000}");

    /// <summary>
    /// The lines to send for a probing command. After an XY-zeroing bore, boss or corner probe (M461-M464 with a non-zero S)
    /// the stored correction shifts the work zero by the measured drift.
    /// </summary>
    public static IReadOnlyList<string> WithCorrection(string gcode, IReadOnlyDictionary<string, string> config, DriftCorrection correction)
    {
        if (!correction.Enabled) return [gcode];
        if (!config.TryGetValue("S", out var s) || string.IsNullOrWhiteSpace(s) || s.Trim() == "0") return [gcode];
        if (!(gcode.StartsWith("M461") || gcode.StartsWith("M462") || gcode.StartsWith("M463") || gcode.StartsWith("M464"))) return [gcode];
        if (Math.Abs(correction.X) < 0.000001 && Math.Abs(correction.Y) < 0.000001) return [gcode];
        return [gcode, string.Create(CultureInfo.InvariantCulture, $"G10L20P0 X{-correction.X:0.0000} Y{-correction.Y:0.0000}")];
    }

    public static string StoredText(DriftCorrection correction) =>
        correction.Enabled ? "Stored correction: " + Format(correction.X, correction.Y) : "Stored correction: off";
}
