using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Carvera.Editor.Preview;

/// <summary>Draws the editor's marks over the preview: hover, selection with resize handles, the drop indicator, and the outlines of containers.</summary>
public sealed class PreviewOverlay : Control
{
    public const double HandleSize = 9;

    private static readonly IBrush SelectionBrush = Brush.Parse("#2563EB");
    private static readonly IPen SelectionPen = new Pen(SelectionBrush, 2);
    private static readonly IBrush HoverFill = Brush.Parse("#142563EB");
    private static readonly IPen HoverPen = new Pen(Brush.Parse("#802563EB"), 1);
    private static readonly IPen StructurePen = new Pen(Brush.Parse("#7094A3B8"), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush DropBrush = Brush.Parse("#16A34A");
    private static readonly IBrush DropFill = Brush.Parse("#2216A34A");
    private static readonly IBrush GhostFill = Brush.Parse("#332563EB");

    public Rect? Hover { get; set; }
    public Rect? Selection { get; set; }
    public Rect? Ghost { get; set; }
    public Rect? Drop { get; set; }
    public bool DropIsLine { get; set; }
    public bool ShowHandles { get; set; }
    public string? Label { get; set; }
    public List<Rect> Structure { get; } = [];

    public enum Handle { None, Right, Bottom, Corner }

    /// <summary>Which resize handle of the selection is under a point.</summary>
    public Handle HandleAt(Point point)
    {
        if (Selection is not { } r || !ShowHandles) return Handle.None;
        bool Near(Point p) => Math.Abs(point.X - p.X) <= HandleSize && Math.Abs(point.Y - p.Y) <= HandleSize;
        if (Near(new Point(r.Right, r.Bottom))) return Handle.Corner;
        if (Near(new Point(r.Right, r.Center.Y))) return Handle.Right;
        if (Near(new Point(r.Center.X, r.Bottom))) return Handle.Bottom;
        return Handle.None;
    }

    public override void Render(DrawingContext dc)
    {
        // A transparent fill makes the whole surface catch the pointer, so nothing in the preview can be operated by accident.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        foreach (var r in Structure) dc.DrawRectangle(null, StructurePen, r.Deflate(0.5));
        if (Hover is { } hover && hover != Selection) dc.DrawRectangle(HoverFill, HoverPen, hover);

        if (Selection is { } sel)
        {
            dc.DrawRectangle(null, SelectionPen, sel.Deflate(1));
            if (ShowHandles)
            {
                foreach (var p in new[] { new Point(sel.Right, sel.Center.Y), new Point(sel.Center.X, sel.Bottom), new Point(sel.Right, sel.Bottom) })
                    dc.DrawRectangle(Brushes.White, SelectionPen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
            }
        }

        if (Ghost is { } ghost) dc.DrawRectangle(GhostFill, new Pen(SelectionBrush, 1.5, new DashStyle([4, 3], 0)), ghost);
        if (Drop is { } drop)
        {
            if (DropIsLine) dc.DrawRectangle(DropBrush, null, drop);
            else dc.DrawRectangle(DropFill, new Pen(DropBrush, 2, new DashStyle([5, 3], 0)), drop);
        }

        if (Label is { Length: > 0 } label)
        {
            var anchor = Ghost ?? Selection ?? Drop;
            if (anchor is { } a)
            {
                var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
                var box = new Rect(a.X, Math.Max(0, a.Y - text.Height - 8), text.Width + 10, text.Height + 4);
                dc.DrawRectangle(SelectionBrush, null, box, 3, 3);
                dc.DrawText(text, new Point(box.X + 5, box.Y + 2));
            }
        }
    }

    public void Invalidate() => InvalidateVisual();
}
