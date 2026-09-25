using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Carvera.App.Layout;
using Carvera.Core.Gcode;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>
/// Top-down (XY) preview of the loaded G-code with the tool position in work coordinates.
/// Wheel zooms about the pointer, dragging pans, double-click fits the drawing.
/// </summary>
public sealed class ToolpathView : Control
{
    private readonly BuildContext _ctx;
    private readonly bool _showGrid;
    private readonly double _gridSize;
    private readonly IBrush _pathBrush, _rapidBrush, _positionBrush, _gridBrush, _axisBrush, _textBrush;
    private StreamGeometry? _feed, _rapid;
    private GcodeProgram? _program;
    private double _scale = 2;
    private Point _center;
    private bool _fitted;
    private Point? _dragStart;
    private Point _dragCenter;

    public ToolpathView(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        _ctx = ctx;
        _showGrid = node.GetBool("showGrid") ?? true;
        _gridSize = Math.Max(0.1, node.GetNumber("gridSize") ?? 10);
        _pathBrush = ctx.Theme.Brush(node.GetString("pathColor")) ?? ctx.Theme.TokenBrush("accent");
        _rapidBrush = ctx.Theme.Brush(node.GetString("rapidColor")) ?? ctx.Theme.TokenBrush("borderStrong");
        _positionBrush = ctx.Theme.Brush(node.GetString("positionColor")) ?? ctx.Theme.TokenBrush("danger");
        _gridBrush = new SolidColorBrush(Color.FromArgb(28, 30, 41, 59));
        _axisBrush = new SolidColorBrush(Color.FromArgb(70, 30, 41, 59));
        _textBrush = ctx.Theme.TokenBrush("textMuted");
        ClipToBounds = true;
        Focusable = true;

        void OnProgram()
        {
            Load(ctx.Services.Program);
            InvalidateVisual();
        }
        ctx.Services.ProgramChanged += OnProgram;
        ctx.Track(new Detach(() => ctx.Services.ProgramChanged -= OnProgram));
        ctx.Watch([StatePaths.AxisWork("x"), StatePaths.AxisWork("y")], InvalidateVisual);
        Load(ctx.Services.Program);
    }

    private void Load(GcodeProgram? program)
    {
        _program = program;
        _feed = _rapid = null;
        _fitted = false;
        if (program is null || program.Segments.Count == 0) return;
        _feed = new StreamGeometry();
        _rapid = new StreamGeometry();
        using (var feed = _feed.Open())
        using (var rapid = _rapid.Open())
        {
            foreach (var s in program.Segments)
            {
                var target = s.Rapid ? rapid : feed;
                target.BeginFigure(new Point(s.Start.X, -s.Start.Y), false);
                target.LineTo(new Point(s.End.X, -s.End.Y));
                target.EndFigure(false);
            }
        }
    }

    private void Fit()
    {
        _fitted = true;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (_program is not { Segments.Count: > 0 } p)
        {
            _center = new Point(0, 0);
            _scale = 2;
            return;
        }
        var width = Math.Max(1, p.Max.X - p.Min.X);
        var height = Math.Max(1, p.Max.Y - p.Min.Y);
        _scale = Math.Min((Bounds.Width - 40) / width, (Bounds.Height - 40) / height);
        _scale = Math.Clamp(_scale, 0.01, 1000);
        _center = new Point((p.Min.X + p.Max.X) / 2, (p.Min.Y + p.Max.Y) / 2);
    }

    private Point ToScreen(double x, double y) => new((x - _center.X) * _scale + Bounds.Width / 2, Bounds.Height / 2 - (y - _center.Y) * _scale);
    private Point ToWorld(Point screen) => new((screen.X - Bounds.Width / 2) / _scale + _center.X, (Bounds.Height / 2 - screen.Y) / _scale + _center.Y);

    public override void Render(DrawingContext context)
    {
        if (!_fitted) Fit();
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_showGrid) DrawGrid(context);

        var transform = Matrix.CreateTranslation(-_center.X, _center.Y) * Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);
        using (context.PushTransform(transform))
        {
            if (_rapid is not null) context.DrawGeometry(null, new Pen(_rapidBrush, 1 / _scale, new DashStyle([4, 3], 0)), _rapid);
            if (_feed is not null) context.DrawGeometry(null, new Pen(_pathBrush, 1.5 / _scale), _feed);
        }

        if (_ctx.State.Get(StatePaths.Connected, false))
        {
            var p = ToScreen(_ctx.State.Get(StatePaths.AxisWork("x"), 0.0), _ctx.State.Get(StatePaths.AxisWork("y"), 0.0));
            var pen = new Pen(_positionBrush, 2);
            context.DrawEllipse(null, pen, p, 7, 7);
            context.DrawLine(pen, p - new Point(11, 0), p + new Point(11, 0));
            context.DrawLine(pen, p - new Point(0, 11), p + new Point(0, 11));
        }

        var caption = _program is null ? "No file loaded — use Open to preview G-code" : $"{System.IO.Path.GetFileName(_program.Path)}  ·  {_program.Max.X - _program.Min.X:0.#} × {_program.Max.Y - _program.Min.Y:0.#} mm";
        var text = new FormattedText(caption, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(_ctx.Theme.FontFamily), _ctx.Theme.FontSize - 1, _textBrush);
        context.DrawText(text, new Point(10, Bounds.Height - text.Height - 8));
    }

    private void DrawGrid(DrawingContext context)
    {
        var step = _gridSize;
        while (step * _scale < 8) step *= 10;
        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(Bounds.Width, Bounds.Height));
        var pen = new Pen(_gridBrush, 1);
        for (var x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            var sx = ToScreen(x, 0).X;
            context.DrawLine(Math.Abs(x) < step / 2 ? new Pen(_axisBrush, 1) : pen, new Point(sx, 0), new Point(sx, Bounds.Height));
        }
        for (var y = Math.Floor(bottomRight.Y / step) * step; y <= topLeft.Y; y += step)
        {
            var sy = ToScreen(0, y).Y;
            context.DrawLine(Math.Abs(y) < step / 2 ? new Pen(_axisBrush, 1) : pen, new Point(0, sy), new Point(Bounds.Width, sy));
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_fitted || e.PreviousSize.Width <= 0) _fitted = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var anchor = e.GetPosition(this);
        var world = ToWorld(anchor);
        _scale = Math.Clamp(_scale * Math.Pow(1.2, e.Delta.Y), 0.01, 1000);
        var after = ToWorld(anchor);
        _center = new Point(_center.X + world.X - after.X, _center.Y + world.Y - after.Y);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2) { Fit(); InvalidateVisual(); return; }
        _dragStart = e.GetPosition(this);
        _dragCenter = _center;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start) return;
        var delta = e.GetPosition(this) - start;
        _center = new Point(_dragCenter.X - delta.X / _scale, _dragCenter.Y + delta.Y / _scale);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
