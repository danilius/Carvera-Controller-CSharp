using Carvera.Core.Gcode;
using Xunit;

namespace Carvera.Tests;

public class GcodeTests
{
    [Fact]
    public void ParsesLinesRapidsAndModes()
    {
        var p = GcodeProgram.Parse([
            "(header comment)",
            "G21 G90",
            "G0 X10 Y0 ; rapid",
            "G1 X10 Y10 F500",
            "X0",
            "G91 G1 Y-5",
            "G20 G90 G1 X1",
            "G53 G0 Z-2",
        ]);
        Assert.Equal(5, p.Segments.Count);
        Assert.True(p.Segments[0].Rapid);
        Assert.Equal(new Point3(10, 0, 0), p.Segments[0].End);
        Assert.Equal(new Point3(0, 10, 0), p.Segments[2].End);
        Assert.Equal(new Point3(0, 5, 0), p.Segments[3].End);
        Assert.Equal(new Point3(25.4, 5, 0), p.Segments[4].End);
        Assert.Equal(7, p.Segments[4].Line);
        Assert.Equal(0, p.Min.X);
        Assert.Equal(25.4, p.Max.X, 6);
    }

    [Fact]
    public void TessellatesArcsWithIJ()
    {
        var p = GcodeProgram.Parse(["G0 X10 Y0", "G3 X-10 Y0 I-10 J0"]);
        var arc = p.Segments.Skip(1).ToList();
        Assert.True(arc.Count > 4);
        Assert.All(arc, s => Assert.InRange(Math.Sqrt(s.End.X * s.End.X + s.End.Y * s.End.Y), 9.99, 10.01));
        Assert.True(arc.Max(s => s.End.Y) > 9.9); // counter-clockwise goes through +Y
        Assert.Equal(new Point3(-10, 0, 0), arc[^1].End);
    }

    [Fact]
    public void ClockwiseArcWithRadiusStaysOnCircle()
    {
        var p = GcodeProgram.Parse(["G0 X0 Y0", "G2 X10 Y0 R5"]);
        var arc = p.Segments.Skip(1).ToList();
        Assert.All(arc, s => Assert.InRange(Math.Sqrt((s.End.X - 5) * (s.End.X - 5) + s.End.Y * s.End.Y), 4.99, 5.01));
        Assert.True(arc.Max(s => s.End.Y) > 4.9); // clockwise from (0,0) to (10,0) passes over the top
    }
}
