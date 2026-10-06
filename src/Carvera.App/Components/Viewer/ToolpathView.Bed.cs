using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Carvera.App.Components.Bed;
using Carvera.Core.Job;
using Carvera.Core.State;

namespace Carvera.App.Components.Viewer;

/// <summary>Draws the machine bed picture flat on the work plane, under the grid and the path.</summary>
public sealed partial class ToolpathView
{
    private const int BedCellsX = 14, BedCellsY = 10;
    private const double BedOpacity = 0.55;

    private string? _bedImageSource;
    private bool _showBed = true;

    private static readonly string[] BedPaths =
    [
        StatePaths.ViewBedImage, StatePaths.MachineModelName, StatePaths.WcsRotation, StatePaths.AxisOffset("x"), StatePaths.AxisOffset("y"),
        StatePaths.BedSizeX, StatePaths.BedSizeY, StatePaths.BedAnchor1X, StatePaths.BedAnchor1Y, StatePaths.BedAnchorWidth,
    ];

    /// <summary>The corners of the bed in work coordinates (the coordinates of the G-code), lower-left first, going anticlockwise.</summary>
    private Vector2[] BedCorners()
    {
        var state = _ctx.State;
        var g = BedGeometry.FromState(state);
        var wcoX = state.Get(StatePaths.AxisOffset("x"), 0.0);
        var wcoY = state.Get(StatePaths.AxisOffset("y"), 0.0);
        var rotation = state.Get(StatePaths.WcsRotation, 0.0) * Math.PI / 180;
        var (cx, cy) = g.BedCorner;
        Vector2 Work(double u, double v)
        {
            // machine = wco + R(rotation) * work, so work = R(-rotation) * (machine - wco)
            var dx = cx + u * g.SizeX - wcoX;
            var dy = cy + v * g.SizeY - wcoY;
            return new Vector2((float)(Math.Cos(rotation) * dx + Math.Sin(rotation) * dy), (float)(-Math.Sin(rotation) * dx + Math.Cos(rotation) * dy));
        }
        return [Work(0, 0), Work(1, 0), Work(1, 1), Work(0, 1)];
    }

    private Vector2 BedPoint(Vector2[] corners, double u, double v)
    {
        // bilinear over the parallelogram (the corners of a rotated rectangle)
        var origin = corners[0];
        return origin + (corners[1] - origin) * (float)u + (corners[3] - origin) * (float)v;
    }

    private void DrawBed(DrawingContext context)
    {
        if (!_showBed || BedImages.Find(_ctx, _bedImageSource) is not { } image) return;
        var corners = BedCorners();
        Point? Screen(double u, double v)
        {
            var p = BedPoint(corners, u, v);
            return Camera.Project(new Vector3(p.X, p.Y, 0));
        }
        var size = image.Size;
        using var _ = context.PushOpacity(BedOpacity);

        if (Camera.Orthographic)
        {
            // Orthographic projection keeps parallelograms parallelograms, so one transform places the whole picture.
            if (Screen(0, 1) is not { } a || Screen(1, 1) is not { } b || Screen(0, 0) is not { } c) return;
            using var __ = context.PushTransform(new Matrix((b.X - a.X) / size.Width, (b.Y - a.Y) / size.Width, (c.X - a.X) / size.Height, (c.Y - a.Y) / size.Height, a.X, a.Y));
            context.DrawImage(image, new Rect(size), new Rect(size));
            return;
        }

        // Perspective bends it, so draw it as small triangles, each mapped straight (affine) from its patch of the picture.
        var grid = new Point?[BedCellsX + 1, BedCellsY + 1];
        for (var i = 0; i <= BedCellsX; i++)
            for (var j = 0; j <= BedCellsY; j++)
                grid[i, j] = Screen((double)i / BedCellsX, (double)j / BedCellsY);
        for (var i = 0; i < BedCellsX; i++)
            for (var j = 0; j < BedCellsY; j++)
            {
                if (grid[i, j] is not { } p00 || grid[i + 1, j] is not { } p10 || grid[i + 1, j + 1] is not { } p11 || grid[i, j + 1] is not { } p01) continue;
                // picture coordinates: u to the right, v up, so the picture's y runs from v=1 (top) down to v=0
                Point T(int gi, int gj) => new(size.Width * gi / BedCellsX, size.Height * (1 - (double)gj / BedCellsY));
                DrawTriangle(context, image, T(i, j), T(i + 1, j), T(i + 1, j + 1), p00, p10, p11);
                DrawTriangle(context, image, T(i, j), T(i + 1, j + 1), T(i, j + 1), p00, p11, p01);
            }
    }

    private static void DrawTriangle(DrawingContext context, IImage image, Point t0, Point t1, Point t2, Point s0, Point s1, Point s2)
    {
        var d1 = t1 - t0;
        var d2 = t2 - t0;
        var det = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(det) < 1e-9) return;
        var e1 = s1 - s0;
        var e2 = s2 - s0;
        // the linear map L with L*d1 = e1 and L*d2 = e2
        var m11 = (e1.X * d2.Y - e2.X * d1.Y) / det;
        var m21 = (e2.X * d1.X - e1.X * d2.X) / det;
        var m12 = (e1.Y * d2.Y - e2.Y * d1.Y) / det;
        var m22 = (e2.Y * d1.X - e1.Y * d2.X) / det;
        var matrix = new Matrix(m11, m12, m21, m22, s0.X - (m11 * t0.X + m21 * t0.Y), s0.Y - (m12 * t0.X + m22 * t0.Y));

        // Grow the triangle a hair so neighbours overlap and no seams show between them.
        var centre = new Point((s0.X + s1.X + s2.X) / 3, (s0.Y + s1.Y + s2.Y) / 3);
        Point Grow(Point p)
        {
            var v = p - centre;
            var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return len < 1e-6 ? p : p + v * (0.6 / len);
        }
        var clip = new StreamGeometry();
        using (var g = clip.Open())
        {
            g.BeginFigure(Grow(s0), true);
            g.LineTo(Grow(s1));
            g.LineTo(Grow(s2));
            g.EndFigure(true);
        }
        using var _ = context.PushGeometryClip(clip);
        using var __ = context.PushTransform(matrix);
        context.DrawImage(image, new Rect(image.Size), new Rect(image.Size));
    }
}
