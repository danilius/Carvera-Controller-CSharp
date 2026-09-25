using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Carvera.App.Layout;
using Carvera.Core.Gcode;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components.Viewer;

/// <summary>
/// 3D view of the loaded G-code and the tool, navigated like Blender's 3D viewport:
/// middle-drag orbits, Shift+middle-drag pans, Ctrl+middle-drag or the wheel zooms, numpad 1/3/7
/// (Ctrl for the opposite side) align to front/right/top, numpad 5 toggles perspective, numpad
/// 2/4/6/8 orbit in 15° steps (Ctrl pans), numpad 9 flips, numpad . frames the tool and Home frames
/// everything. Alt+left-drag stands in for the middle button, and the axis gizmo in the corner
/// can be clicked or dragged.
/// </summary>
public sealed class ToolpathView : Control
{
    private enum Drag { None, Orbit, Pan, Zoom }

    private const double OrbitDegreesPerPixel = 0.4;
    private const double GizmoRadius = 42;

    private readonly BuildContext _ctx;
    private readonly bool _showGrid, _showGizmo, _colorByOperation;
    private readonly double _gridSize, _toolLength;
    private readonly IBrush _pathBrush, _rapidBrush, _doneBrush, _toolBrush, _textBrush, _gridBrush, _gridMajorBrush;
    private readonly ViewPreset _initialView;
    private readonly bool _initialOrtho;
    private GcodeProgram? _program;
    private Vector3[] _starts = [], _ends = [];
    private bool[] _rapid = [];
    private int[] _lines = [];
    private int[] _operations = [];
    private bool _framed;
    private Drag _drag;
    private Point _last;
    private Point _pressPoint;
    private bool _gizmoPress;

    public ToolpathView(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        _ctx = ctx;
        _showGrid = node.GetBool("showGrid") ?? true;
        _showGizmo = node.GetBool("showGizmo") ?? true;
        _colorByOperation = !string.Equals(node.GetString("colorBy"), "single", StringComparison.OrdinalIgnoreCase);
        _gridSize = Math.Max(0.1, node.GetNumber("gridSize") ?? 10);
        _toolLength = Math.Max(1, node.GetNumber("toolLength") ?? 25);
        var theme = ctx.Theme;
        _pathBrush = theme.Brush(node.GetString("pathColor")) ?? theme.TokenBrush("accent");
        _rapidBrush = theme.Brush(node.GetString("rapidColor")) ?? theme.TokenBrush("borderStrong");
        _doneBrush = theme.Brush(node.GetString("doneColor")) ?? theme.TokenBrush("success");
        _toolBrush = theme.Brush(node.GetString("positionColor")) ?? theme.TokenBrush("danger");
        _textBrush = theme.TokenBrush("textMuted");
        _gridBrush = new SolidColorBrush(Color.FromArgb(22, 30, 41, 59));
        _gridMajorBrush = new SolidColorBrush(Color.FromArgb(48, 30, 41, 59));
        _initialView = node.GetString("view")?.ToLowerInvariant() switch
        {
            "top" => ViewPreset.Top,
            "front" => ViewPreset.Front,
            "right" => ViewPreset.Right,
            _ => ViewPreset.User,
        };
        _initialOrtho = node.GetString("projection")?.Equals("orthographic", StringComparison.OrdinalIgnoreCase) == true;
        Camera.Orthographic = _initialOrtho;
        if (_initialView != ViewPreset.User) Camera.SetView(_initialView);

        ClipToBounds = true;
        Focusable = true;

        void OnProgram()
        {
            Load(ctx.Services.Program);
            InvalidateVisual();
        }
        ctx.Services.ProgramChanged += OnProgram;
        ctx.Track(new Detach(() => ctx.Services.ProgramChanged -= OnProgram));
        ctx.Watch([StatePaths.AxisWork("x"), StatePaths.AxisWork("y"), StatePaths.AxisWork("z"), StatePaths.Connected, StatePaths.JobLines, StatePaths.JobPlaying,
            StatePaths.PreviewSegment, StatePaths.PreviewOperation], InvalidateVisual);
        Load(ctx.Services.Program);
    }

    public OrbitCamera Camera { get; } = new();

    private void Load(GcodeProgram? program)
    {
        _program = program;
        var segments = program?.Segments ?? [];
        _starts = new Vector3[segments.Count];
        _ends = new Vector3[segments.Count];
        _rapid = new bool[segments.Count];
        _lines = new int[segments.Count];
        _operations = new int[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            _starts[i] = new Vector3((float)s.Start.X, (float)s.Start.Y, (float)s.Start.Z);
            _ends[i] = new Vector3((float)s.End.X, (float)s.End.Y, (float)s.End.Z);
            _rapid[i] = s.Rapid;
            _lines[i] = s.Line;
            _operations[i] = program!.OperationOfLine(s.Line);
        }
        _framed = false;
    }

    private (Vector3 Min, Vector3 Max) SceneBounds()
    {
        if (_program is { Segments.Count: > 0 } p)
            return (new Vector3((float)p.Min.X, (float)p.Min.Y, (float)p.Min.Z), new Vector3((float)p.Max.X, (float)p.Max.Y, (float)p.Max.Z));
        return (new Vector3(-50, -50, 0), new Vector3(50, 50, 20));
    }

    public void FrameAll()
    {
        var (min, max) = SceneBounds();
        Camera.Frame(min, max);
        InvalidateVisual();
    }

    private Vector3 ToolPosition => new(
        (float)_ctx.State.Get(StatePaths.AxisWork("x"), 0.0),
        (float)_ctx.State.Get(StatePaths.AxisWork("y"), 0.0),
        (float)_ctx.State.Get(StatePaths.AxisWork("z"), 0.0));

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext context)
    {
        Camera.Viewport = Bounds.Size;
        if (!_framed && Bounds.Width > 0)
        {
            _framed = true;
            var (min, max) = SceneBounds();
            Camera.Frame(min, max);
        }
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_showGrid) DrawGrid(context);
        DrawPaths(context);
        if (_ctx.State.Get(StatePaths.Connected, false)) DrawTool(context);
        if (_showGizmo) DrawGizmo(context);
        DrawText(context, Camera.Description, new Point(10, 8), _ctx.Theme.FontSize - 1);
        var caption = _program is null
            ? "No file loaded. Middle-drag orbits, Shift pans, wheel zooms, numpad 1/3/7 align, Home frames."
            : $"{System.IO.Path.GetFileName(_program.Path)}  ·  {_program.Max.X - _program.Min.X:0.#} × {_program.Max.Y - _program.Min.Y:0.#} × {_program.Max.Z - _program.Min.Z:0.#} mm";
        DrawText(context, caption, new Point(10, Bounds.Height - 22), _ctx.Theme.FontSize - 1);
    }

    private void DrawText(DrawingContext context, string text, Point at, double size, IBrush? brush = null, bool center = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(_ctx.Theme.FontFamily), size, brush ?? _textBrush);
        context.DrawText(formatted, center ? new Point(at.X - formatted.Width / 2, at.Y - formatted.Height / 2) : at);
    }

    private void Line(StreamGeometryContext g, Vector3 a, Vector3 b)
    {
        if (!Camera.ProjectSegment(a, b, out var pa, out var pb)) return;
        g.BeginFigure(pa, false);
        g.LineTo(pb);
        g.EndFigure(false);
    }

    private void DrawGrid(DrawingContext context)
    {
        var (min, max) = SceneBounds();
        var step = _gridSize;
        var span = Math.Max(Math.Max(max.X - min.X, max.Y - min.Y), 100);
        while (span / step > 80) step *= 5;
        var extent = Math.Ceiling(span * 0.75 / step) * step;
        var cx = Math.Round((min.X + max.X) / 2 / step) * step;
        var cy = Math.Round((min.Y + max.Y) / 2 / step) * step;
        var minor = new StreamGeometry();
        var major = new StreamGeometry();
        using (var gm = minor.Open())
        using (var gM = major.Open())
        {
            for (var v = -extent; v <= extent + 1e-9; v += step)
            {
                var isMajor = Math.Abs(Math.IEEERemainder(v + cx, step * 10)) < step / 2;
                Line(isMajor ? gM : gm, new Vector3((float)(cx + v), (float)(cy - extent), 0), new Vector3((float)(cx + v), (float)(cy + extent), 0));
                isMajor = Math.Abs(Math.IEEERemainder(v + cy, step * 10)) < step / 2;
                Line(isMajor ? gM : gm, new Vector3((float)(cx - extent), (float)(cy + v), 0), new Vector3((float)(cx + extent), (float)(cy + v), 0));
            }
        }
        context.DrawGeometry(null, new Pen(_gridBrush, 1), minor);
        context.DrawGeometry(null, new Pen(_gridMajorBrush, 1), major);
        // Work origin axes, coloured like Blender's.
        DrawSegment(context, new Vector3((float)(cx - extent), 0, 0), new Vector3((float)(cx + extent), 0, 0), new Pen(new SolidColorBrush(Color.FromArgb(150, 220, 38, 38)), 1.2));
        DrawSegment(context, new Vector3(0, (float)(cy - extent), 0), new Vector3(0, (float)(cy + extent), 0), new Pen(new SolidColorBrush(Color.FromArgb(150, 22, 163, 74)), 1.2));
    }

    private void DrawSegment(DrawingContext context, Vector3 a, Vector3 b, IPen pen)
    {
        if (Camera.ProjectSegment(a, b, out var pa, out var pb)) context.DrawLine(pen, pa, pb);
    }

    private void DrawPaths(DrawingContext context)
    {
        if (_starts.Length == 0) return;
        var doneLine = _ctx.State.Get(StatePaths.JobPlaying, false) ? _ctx.State.Get(StatePaths.JobLines, -1) : -1;
        var scrub = _ctx.State.Get(StatePaths.PreviewSegment, -1);
        var selected = _ctx.State.Get(StatePaths.PreviewOperation, -1);
        var operationCount = _colorByOperation ? Math.Max(1, _program?.Operations.Count ?? 1) : 1;

        // One geometry per colour: each operation, rapids, executed moves and the faded remainder.
        var feeds = new StreamGeometry[operationCount + 1];
        var contexts = new StreamGeometryContext[operationCount + 1];
        for (var i = 0; i < feeds.Length; i++) contexts[i] = (feeds[i] = new StreamGeometry()).Open();
        var rapid = new StreamGeometry();
        var done = new StreamGeometry();
        var faded = new StreamGeometry();
        using (var r = rapid.Open())
        using (var d = done.Open())
        using (var f = faded.Open())
        {
            for (var i = 0; i < _starts.Length; i++)
            {
                var op = _operations[i];
                StreamGeometryContext target;
                if (scrub >= 0 && i > scrub || selected >= 0 && op != selected) target = f;
                else if (_lines[i] <= doneLine) target = d;
                else if (_rapid[i]) target = r;
                else target = contexts[_colorByOperation && op >= 0 ? op % operationCount + 1 : 0];
                Line(target, _starts[i], _ends[i]);
            }
        }
        foreach (var c in contexts) c.Dispose();

        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 100, 116, 139)), 1), faded);
        context.DrawGeometry(null, new Pen(_rapidBrush, 1, new DashStyle([4, 3], 0)), rapid);
        context.DrawGeometry(null, new Pen(_pathBrush, 1.5), feeds[0]);
        for (var i = 1; i < feeds.Length; i++)
            context.DrawGeometry(null, new Pen(new SolidColorBrush(_ctx.Theme.OperationColor(i - 1)), 1.5), feeds[i]);
        context.DrawGeometry(null, new Pen(_doneBrush, 2), done);

        if (scrub >= 0 && scrub < _ends.Length)
        {
            // Preview tool at the scrub position: hollow, in the colour of its operation.
            var tip = _ends[scrub];
            var color = _colorByOperation && _operations[scrub] >= 0 ? _ctx.Theme.OperationColor(_operations[scrub]) : ((ISolidColorBrush)_pathBrush).Color;
            var pen = new Pen(new SolidColorBrush(color), 2);
            DrawSegment(context, tip, tip + new Vector3(0, 0, (float)_toolLength), pen);
            if (Camera.Project(tip) is { } p) context.DrawEllipse(Brushes.White, pen, p, 5, 5);
        }
    }

    private void DrawTool(DrawingContext context)
    {
        var tip = ToolPosition;
        var pen = new Pen(_toolBrush, 2.5);
        DrawSegment(context, tip, tip + new Vector3(0, 0, (float)_toolLength), pen);
        if (Camera.Project(tip) is { } p) context.DrawEllipse(_toolBrush, null, p, 4, 4);
        // A short cross on the XY plane under the tool helps judge its position.
        var r = (float)(_toolLength / 5);
        var thin = new Pen(_toolBrush, 1);
        DrawSegment(context, tip - new Vector3(r, 0, 0), tip + new Vector3(r, 0, 0), thin);
        DrawSegment(context, tip - new Vector3(0, r, 0), tip + new Vector3(0, r, 0), thin);
    }

    // ---------------------------------------------------------------- navigation gizmo

    private Point GizmoCenter => new(Bounds.Width - GizmoRadius - 14, GizmoRadius + 14);

    private record GizmoBall(Point Position, ViewPreset View, string? Label, Color Color, double Depth, bool Positive);

    private IReadOnlyList<GizmoBall> GizmoBalls()
    {
        var c = GizmoCenter;
        var r = GizmoRadius - 10;
        GizmoBall Ball(Vector3 axis, ViewPreset view, string? label, Color color, bool positive)
        {
            var sx = Vector3.Dot(axis, Camera.Right);
            var sy = Vector3.Dot(axis, Camera.Up);
            return new GizmoBall(new Point(c.X + sx * r, c.Y - sy * r), view, label, color, Vector3.Dot(axis, Camera.Forward), positive);
        }
        var red = Color.Parse("#E5484D");
        var green = Color.Parse("#46A758");
        var blue = Color.Parse("#3E63DD");
        return
        [
            Ball(Vector3.UnitX, ViewPreset.Right, "X", red, true), Ball(-Vector3.UnitX, ViewPreset.Left, null, red, false),
            Ball(Vector3.UnitY, ViewPreset.Back, "Y", green, true), Ball(-Vector3.UnitY, ViewPreset.Front, null, green, false),
            Ball(Vector3.UnitZ, ViewPreset.Top, "Z", blue, true), Ball(-Vector3.UnitZ, ViewPreset.Bottom, null, blue, false),
        ];
    }

    private Point ProjectionButton => new(GizmoCenter.X, GizmoCenter.Y + GizmoRadius + 22);
    private Point FrameButton => new(GizmoCenter.X, GizmoCenter.Y + GizmoRadius + 56);

    private void DrawGizmo(DrawingContext context)
    {
        var c = GizmoCenter;
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(_gizmoPress || IsPointerOver && Distance(_last, c) < GizmoRadius ? (byte)40 : (byte)18, 30, 41, 59)), null, c, GizmoRadius, GizmoRadius);
        // far balls first so near ones overlap them
        foreach (var ball in GizmoBalls().OrderByDescending(b => b.Depth))
        {
            if (ball.Positive) context.DrawLine(new Pen(new SolidColorBrush(ball.Color), 2), c, ball.Position);
            var fill = ball.Positive ? new SolidColorBrush(ball.Color) : new SolidColorBrush(Color.FromArgb(90, ball.Color.R, ball.Color.G, ball.Color.B));
            context.DrawEllipse(fill, ball.Positive ? null : new Pen(new SolidColorBrush(ball.Color), 1.5), ball.Position, ball.Positive ? 9 : 7, ball.Positive ? 9 : 7);
            if (ball.Label is not null) DrawText(context, ball.Label, ball.Position, 11, Brushes.White, center: true);
        }
        void Button(Point at, string text)
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(24, 30, 41, 59)), null, at, 14, 14);
            DrawText(context, text, at, 11, _textBrush, center: true);
        }
        Button(ProjectionButton, Camera.Orthographic ? "Ort" : "Per");
        Button(FrameButton, "⌂");
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        _last = _pressPoint = point.Position;
        var mods = e.KeyModifiers;
        if (props.IsMiddleButtonPressed || props.IsLeftButtonPressed && mods.HasFlag(KeyModifiers.Alt))
        {
            _drag = mods.HasFlag(KeyModifiers.Shift) ? Drag.Pan : mods.HasFlag(KeyModifiers.Control) ? Drag.Zoom : Drag.Orbit;
        }
        else if (props.IsLeftButtonPressed && _showGizmo && Distance(point.Position, GizmoCenter) <= GizmoRadius)
        {
            _gizmoPress = true;
            _drag = Drag.Orbit;
        }
        else if (props.IsLeftButtonPressed && _showGizmo && (Distance(point.Position, ProjectionButton) <= 14 || Distance(point.Position, FrameButton) <= 14))
        {
            if (Distance(point.Position, ProjectionButton) <= 14) Camera.ToggleProjection();
            else FrameAll();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        else return;
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var dx = position.X - _last.X;
        var dy = position.Y - _last.Y;
        _last = position;
        switch (_drag)
        {
            case Drag.Orbit:
                if (_gizmoPress && Distance(position, _pressPoint) < 4) return;
                Camera.Orbit(-dx * OrbitDegreesPerPixel, dy * OrbitDegreesPerPixel);
                break;
            case Drag.Pan:
                Camera.Pan(dx, dy);
                break;
            case Drag.Zoom:
                Camera.Zoom(Math.Exp(dy * 0.01));
                break;
            default:
                if (_showGizmo) InvalidateVisual(); // gizmo hover highlight
                return;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_gizmoPress && Distance(e.GetPosition(this), _pressPoint) < 4)
        {
            // A click (not a drag) on an axis ball aligns the view to it.
            var hit = GizmoBalls().Where(b => Distance(b.Position, _pressPoint) <= 10).OrderBy(b => b.Depth).FirstOrDefault();
            if (hit is not null) Camera.SetView(hit.View);
        }
        _drag = Drag.None;
        _gizmoPress = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Camera.Zoom(Math.Pow(1 / 1.2, e.Delta.Y));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var step = 15.0;
        var panStep = Math.Max(Bounds.Width, Bounds.Height) / 12;
        switch (e.Key)
        {
            case Key.NumPad1: Camera.SetView(ctrl ? ViewPreset.Back : ViewPreset.Front); break;
            case Key.NumPad3: Camera.SetView(ctrl ? ViewPreset.Left : ViewPreset.Right); break;
            case Key.NumPad7: Camera.SetView(ctrl ? ViewPreset.Bottom : ViewPreset.Top); break;
            case Key.NumPad9: Camera.Opposite(); break;
            case Key.NumPad5: Camera.ToggleProjection(); break;
            case Key.NumPad2: if (ctrl) Camera.Pan(0, -panStep); else Camera.Orbit(0, step); break;
            case Key.NumPad8: if (ctrl) Camera.Pan(0, panStep); else Camera.Orbit(0, -step); break;
            case Key.NumPad4: if (ctrl) Camera.Pan(panStep, 0); else Camera.Orbit(step, 0); break;
            case Key.NumPad6: if (ctrl) Camera.Pan(-panStep, 0); else Camera.Orbit(-step, 0); break;
            case Key.Add: Camera.Zoom(1 / 1.2); break;
            case Key.Subtract: Camera.Zoom(1.2); break;
            case Key.Home: FrameAll(); break;
            case Key.Decimal: Camera.Target = ToolPosition; break;
            default: return;
        }
        e.Handled = true;
        InvalidateVisual();
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
