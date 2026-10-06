using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Carvera.App.Layout;
using Carvera.Core.Job;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components.Bed;

/// <summary>
/// A plan view of the machine bed for job setup: the bed picture (or the anchors drawn plainly), the work origin, the open
/// program's outline with its path origin, the Z probe position, the auto-level points and the tool. Everything is drawn from the
/// state store, in the bed's own millimetres, so it follows the machine's offsets and the WCS rotation as they change.
/// </summary>
public sealed class BedView : Control
{
    private readonly BuildContext _ctx;
    private readonly string? _imageSource;
    private readonly bool _showAnchors, _showPath, _showZProbe, _showTool, _showLeveling;
    private readonly IBrush _bedBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
    private readonly IBrush _anchorBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));

    public BedView(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        _ctx = ctx;
        _imageSource = node.GetString("bedImage");
        _showAnchors = node.GetBool("showAnchors") ?? true;
        _showPath = node.GetBool("showPath") ?? true;
        _showZProbe = node.GetBool("showZProbe") ?? true;
        _showTool = node.GetBool("showTool") ?? true;
        _showLeveling = node.GetBool("showLeveling") ?? true;
        IsHitTestVisible = false;
        ClipToBounds = true;
        ctx.Watch([
            StatePaths.ViewBedImage, StatePaths.MachineModelName, StatePaths.Connected, StatePaths.WcsRotation,
            StatePaths.AxisOffset("x"), StatePaths.AxisOffset("y"), StatePaths.AxisMachine("x"), StatePaths.AxisMachine("y"),
            StatePaths.FileHasBounds, StatePaths.FileXMin, StatePaths.FileXMax, StatePaths.FileYMin, StatePaths.FileYMax,
            StatePaths.BedSizeX, StatePaths.BedSizeY, StatePaths.BedAnchor1X, StatePaths.BedAnchor1Y, StatePaths.BedAnchorWidth, StatePaths.BedAnchorLength, StatePaths.BedAnchor2X, StatePaths.BedAnchor2Y,
            StatePaths.JobOrigin, StatePaths.JobMargin, StatePaths.JobZProbe, StatePaths.JobLeveling, StatePaths.JobLevelX, StatePaths.JobLevelY,
            StatePaths.JobLevelXn, StatePaths.JobLevelXp, StatePaths.JobLevelYn, StatePaths.JobLevelYp,
            StatePaths.ZProbeOrigin, StatePaths.ZProbeX, StatePaths.ZProbeY,
        ], InvalidateVisual);
    }

    private IBrush Token(string name) => _ctx.Theme.TokenBrush(name);

    public override void Render(DrawingContext context)
    {
        var state = _ctx.State;
        var geometry = BedGeometry.FromState(state);
        var pad = 6.0;
        var scale = Math.Min((Bounds.Width - 2 * pad) / geometry.SizeX, (Bounds.Height - 2 * pad) / geometry.SizeY);
        if (scale <= 0 || double.IsNaN(scale)) return;
        var left = (Bounds.Width - geometry.SizeX * scale) / 2;
        var bottom = (Bounds.Height + geometry.SizeY * scale) / 2;
        Point P(double bedX, double bedY) => new(left + bedX * scale, bottom - bedY * scale);
        var bedRect = new Rect(P(0, geometry.SizeY), P(geometry.SizeX, 0));

        // ---- the bed itself
        context.DrawRectangle(_bedBrush, null, bedRect, 4, 4);
        var image = BedImages.Find(_ctx, _imageSource);
        if (image is not null) context.DrawImage(image, new Rect(image.Size), bedRect);
        var origin1 = state.Get<string>(StatePaths.JobOrigin) ?? OriginChoice.Anchor1;
        if (_showAnchors)
        {
            if (image is null) DrawAnchor(context, geometry, P, scale, 0, 0, origin1 == OriginChoice.Anchor1, false);
            if (image is null) DrawAnchor(context, geometry, P, scale, geometry.Anchor2OffsetX, geometry.Anchor2OffsetY, origin1 == OriginChoice.Anchor2, false);
            DrawAnchorLabel(context, P, "Anchor 1", geometry.AnchorWidth + 2, geometry.AnchorWidth + 2, origin1 == OriginChoice.Anchor1);
            DrawAnchorLabel(context, P, "Anchor 2", geometry.Anchor2OffsetX + geometry.AnchorWidth + 2, geometry.Anchor2OffsetY + geometry.AnchorWidth + 2, origin1 == OriginChoice.Anchor2);
            if (image is not null)
            {
                // With the picture, the chosen anchor is marked by an outline of its L.
                if (origin1 == OriginChoice.Anchor1) DrawAnchor(context, geometry, P, scale, 0, 0, true, true);
                if (origin1 == OriginChoice.Anchor2) DrawAnchor(context, geometry, P, scale, geometry.Anchor2OffsetX, geometry.Anchor2OffsetY, true, true);
            }
        }

        // ---- the work origin, and everything measured from it
        var wcoX = state.Get(StatePaths.AxisOffset("x"), 0.0);
        var wcoY = state.Get(StatePaths.AxisOffset("y"), 0.0);
        var rotation = state.Get(StatePaths.WcsRotation, 0.0) * Math.PI / 180;
        var (originX, originY) = geometry.ToBed(wcoX, wcoY);
        Point Work(double wx, double wy)
        {
            var mx = wcoX + Math.Cos(rotation) * wx - Math.Sin(rotation) * wy;
            var my = wcoY + Math.Sin(rotation) * wx + Math.Cos(rotation) * wy;
            var (bx, by) = geometry.ToBed(mx, my);
            return P(bx, by);
        }

        var hasPath = state.Get(StatePaths.FileHasBounds, false);
        double xmin = state.Get(StatePaths.FileXMin, 0.0), xmax = state.Get(StatePaths.FileXMax, 0.0);
        double ymin = state.Get(StatePaths.FileYMin, 0.0), ymax = state.Get(StatePaths.FileYMax, 0.0);
        var green = Token("success");
        if (_showPath && hasPath)
        {
            var outline = new StreamGeometry();
            using (var g = outline.Open())
            {
                g.BeginFigure(Work(xmin, ymin), true);
                g.LineTo(Work(xmax, ymin));
                g.LineTo(Work(xmax, ymax));
                g.LineTo(Work(xmin, ymax));
                g.EndFigure(true);
            }
            var margin = state.Get(StatePaths.JobMargin, false);
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(46, 34, 197, 94)), new Pen(green, margin ? 3 : 1.6), outline);

            if (_showLeveling && state.Get(StatePaths.JobLeveling, false))
            {
                int nx = state.Get(StatePaths.JobLevelX, 3), ny = state.Get(StatePaths.JobLevelY, 3);
                double x0 = xmin + state.Get(StatePaths.JobLevelXn, 0.0), x1 = xmax - state.Get(StatePaths.JobLevelXp, 0.0);
                double y0 = ymin + state.Get(StatePaths.JobLevelYn, 0.0), y1 = ymax - state.Get(StatePaths.JobLevelYp, 0.0);
                var yellow = new SolidColorBrush(Color.FromRgb(0xF4, 0xD0, 0x3F));
                for (var i = 0; i < nx; i++)
                    for (var j = 0; j < ny; j++)
                    {
                        var wx = nx > 1 ? x0 + (x1 - x0) * i / (nx - 1) : (x0 + x1) / 2;
                        var wy = ny > 1 ? y0 + (y1 - y0) * j / (ny - 1) : (y0 + y1) / 2;
                        context.DrawEllipse(yellow, new Pen(Brushes.Black, 0.8), Work(wx, wy), 4, 4);
                    }
            }

            // the path origin: the lower-left corner of the outline
            var pathOrigin = Work(xmin, ymin);
            context.DrawRectangle(green, new Pen(Brushes.White, 1.2), new Rect(pathOrigin.X - 5, pathOrigin.Y - 5, 10, 10));
            Label(context, "Path origin", new Point(pathOrigin.X + 12, pathOrigin.Y + 24), Brushes.White);
        }

        if (_showZProbe && state.Get(StatePaths.JobZProbe, false))
        {
            var fromWork = (state.Get<string>(StatePaths.ZProbeOrigin) ?? "work") != "path";
            var zx = state.Get(StatePaths.ZProbeX, 0.0) + (fromWork || !hasPath ? 0 : xmin);
            var zy = state.Get(StatePaths.ZProbeY, 0.0) + (fromWork || !hasPath ? 0 : ymin);
            var p = Work(zx, zy);
            var red = Token("danger");
            context.DrawEllipse(red, new Pen(Brushes.White, 1.4), p, 7, 7);
            context.DrawLine(new Pen(Brushes.White, 1.4), new Point(p.X - 4, p.Y), new Point(p.X + 4, p.Y));
            context.DrawLine(new Pen(Brushes.White, 1.4), new Point(p.X, p.Y - 4), new Point(p.X, p.Y + 4));
            Label(context, "Z probe", new Point(p.X + 10, p.Y - 16), Brushes.White);
        }

        var origin = P(originX, originY);
        var blue = Token("accent");
        context.DrawEllipse(blue, new Pen(Brushes.White, 1.6), origin, 9, 9);
        context.DrawLine(new Pen(Brushes.White, 1.6), new Point(origin.X - 13, origin.Y), new Point(origin.X + 13, origin.Y));
        context.DrawLine(new Pen(Brushes.White, 1.6), new Point(origin.X, origin.Y - 13), new Point(origin.X, origin.Y + 13));
        Label(context, "Work origin", new Point(origin.X + 12, origin.Y + 8), Brushes.White);

        if (_showTool && state.Get(StatePaths.Connected, false))
        {
            var (tx, ty) = geometry.ToBed(state.Get(StatePaths.AxisMachine("x"), 0.0), state.Get(StatePaths.AxisMachine("y"), 0.0));
            var tool = P(tx, ty);
            context.DrawEllipse(null, new Pen(Brushes.OrangeRed, 2.4), tool, 8, 8);
            context.DrawEllipse(Brushes.OrangeRed, null, tool, 2.5, 2.5);
        }
    }

    private void DrawAnchor(DrawingContext context, BedGeometry g, Func<double, double, Point> p, double scale, double offsetX, double offsetY, bool selected, bool outlineOnly)
    {
        // an L: a bar along X and a bar along Y, each anchor-width thick and anchor-length long
        var w = g.AnchorWidth;
        var l = g.AnchorLength;
        var shape = new StreamGeometry();
        using (var s = shape.Open())
        {
            s.BeginFigure(p(offsetX, offsetY), true);
            s.LineTo(p(offsetX + l, offsetY));
            s.LineTo(p(offsetX + l, offsetY + w));
            s.LineTo(p(offsetX + w, offsetY + w));
            s.LineTo(p(offsetX + w, offsetY + l));
            s.LineTo(p(offsetX, offsetY + l));
            s.EndFigure(true);
        }
        var accent = _ctx.Theme.ResolveColor("@accent") ?? Colors.CornflowerBlue;
        var fill = outlineOnly ? null : selected ? new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)) : _anchorBrush;
        context.DrawGeometry(fill, selected ? new Pen(new SolidColorBrush(accent), 3) : null, shape);
    }

    private void DrawAnchorLabel(DrawingContext context, Func<double, double, Point> p, string text, double bedX, double bedY, bool selected)
    {
        var at = p(bedX, bedY);
        Label(context, text, new Point(at.X + 2, at.Y - 18), selected ? new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD)) : Brushes.White, selected);
    }

    private void Label(DrawingContext context, string text, Point at, IBrush color, bool bold = true)
    {
        var size = _ctx.Theme.FontSize - 1;
        var typeface = new Typeface(_ctx.Theme.FontFamily, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal);
        var shadow = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black);
        context.DrawText(shadow, new Point(at.X + 1, at.Y + 1));
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, color), at);
    }
}
