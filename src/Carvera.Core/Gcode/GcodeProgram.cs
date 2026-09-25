using System.Globalization;

namespace Carvera.Core.Gcode;

public readonly record struct Point3(double X, double Y, double Z);

public readonly record struct PathSegment(int Line, bool Rapid, Point3 Start, Point3 End);

/// <summary>
/// A G-code file parsed into straight segments for previewing. Understands G0/G1/G2/G3 (XY-plane arcs
/// with I/J or R), G90/G91, G20/G21 and comments. Other words are ignored.
/// </summary>
public sealed class GcodeProgram
{
    private GcodeProgram(string? path, IReadOnlyList<string> lines, IReadOnlyList<PathSegment> segments)
    {
        Path = path;
        Lines = lines;
        Segments = segments;
        if (segments.Count > 0)
        {
            Min = new Point3(segments.Min(s => Math.Min(s.Start.X, s.End.X)), segments.Min(s => Math.Min(s.Start.Y, s.End.Y)), segments.Min(s => Math.Min(s.Start.Z, s.End.Z)));
            Max = new Point3(segments.Max(s => Math.Max(s.Start.X, s.End.X)), segments.Max(s => Math.Max(s.Start.Y, s.End.Y)), segments.Max(s => Math.Max(s.Start.Z, s.End.Z)));
        }
    }

    public string? Path { get; }
    public IReadOnlyList<string> Lines { get; }
    public IReadOnlyList<PathSegment> Segments { get; }
    public Point3 Min { get; }
    public Point3 Max { get; }

    public static GcodeProgram Load(string path) => Parse(File.ReadAllLines(path), path);

    public static GcodeProgram Parse(IReadOnlyList<string> lines, string? path = null)
    {
        var segments = new List<PathSegment>();
        var pos = new Point3(0, 0, 0);
        var motion = 0;
        var absolute = true;
        var scale = 1.0;
        for (var index = 0; index < lines.Count; index++)
        {
            var words = Tokenize(lines[index]);
            if (words.Count == 0) continue;
            var machineCoordinates = false;
            double? x = null, y = null, z = null, i = null, j = null, r = null;
            var hasMotionWord = false;
            foreach (var (letter, value) in words)
            {
                switch (letter)
                {
                    case 'G':
                        var g = Math.Round(value, 1);
                        if (g is 0 or 1 or 2 or 3) { motion = (int)g; hasMotionWord = true; }
                        else if (g == 90) absolute = true;
                        else if (g == 91) absolute = false;
                        else if (g == 20) scale = 25.4;
                        else if (g == 21) scale = 1;
                        else if (g == 53) machineCoordinates = true;
                        break;
                    case 'X': x = value * scale; break;
                    case 'Y': y = value * scale; break;
                    case 'Z': z = value * scale; break;
                    case 'I': i = value * scale; break;
                    case 'J': j = value * scale; break;
                    case 'R': r = value * scale; break;
                }
            }
            if (x is null && y is null && z is null) continue;
            if (machineCoordinates) continue; // machine-coordinate moves are not part of the work path
            if (!hasMotionWord && motion is not (0 or 1 or 2 or 3)) continue;
            var target = absolute
                ? new Point3(x ?? pos.X, y ?? pos.Y, z ?? pos.Z)
                : new Point3(pos.X + (x ?? 0), pos.Y + (y ?? 0), pos.Z + (z ?? 0));
            if (motion is 2 or 3) AddArc(segments, index + 1, pos, target, motion == 2, i, j, r);
            else segments.Add(new PathSegment(index + 1, motion == 0, pos, target));
            pos = target;
        }
        return new GcodeProgram(path, lines, segments);
    }

    private static void AddArc(List<PathSegment> segments, int line, Point3 start, Point3 end, bool clockwise, double? i, double? j, double? r)
    {
        double cx, cy;
        if (i is not null || j is not null)
        {
            cx = start.X + (i ?? 0);
            cy = start.Y + (j ?? 0);
        }
        else if (r is { } radius)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-9) { segments.Add(new PathSegment(line, false, start, end)); return; }
            var h = Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
            // Positive R takes the shorter arc; the sign of h picks the side of the chord.
            var sign = (clockwise ? 1 : -1) * (radius > 0 ? 1 : -1);
            cx = (start.X + end.X) / 2 - sign * h * dy / chord;
            cy = (start.Y + end.Y) / 2 + sign * h * dx / chord;
        }
        else { segments.Add(new PathSegment(line, false, start, end)); return; }

        var a0 = Math.Atan2(start.Y - cy, start.X - cx);
        var a1 = Math.Atan2(end.Y - cy, end.X - cx);
        var sweep = a1 - a0;
        if (clockwise && sweep >= -1e-9) sweep -= 2 * Math.PI;
        if (!clockwise && sweep <= 1e-9) sweep += 2 * Math.PI;
        var rad = Math.Sqrt((start.X - cx) * (start.X - cx) + (start.Y - cy) * (start.Y - cy));
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) * rad / 0.5), 4, 360);
        var previous = start;
        for (var k = 1; k <= steps; k++)
        {
            var t = (double)k / steps;
            var a = a0 + sweep * t;
            var p = k == steps ? end : new Point3(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a), start.Z + (end.Z - start.Z) * t);
            segments.Add(new PathSegment(line, false, previous, p));
            previous = p;
        }
    }

    internal static List<(char Letter, double Value)> Tokenize(string line)
    {
        var words = new List<(char, double)>();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == ';') break;
            if (c == '(') { var close = line.IndexOf(')', i); if (close < 0) break; i = close + 1; continue; }
            if (!char.IsLetter(c)) { i++; continue; }
            var letter = char.ToUpperInvariant(c);
            var start = ++i;
            while (i < line.Length && (char.IsDigit(line[i]) || line[i] is '.' or '-' or '+' || line[i] == ' ' && start == i)) i++;
            if (double.TryParse(line.AsSpan(start, i - start).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) words.Add((letter, value));
        }
        return words;
    }
}
