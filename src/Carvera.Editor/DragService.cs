using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Carvera.Editor;

/// <summary>What is being dragged: an existing element (by path) or a new one from the palette.</summary>
public sealed record DragPayload(string Label, string? SourcePath, JsonObject? NewElement);

/// <summary>Somewhere an element can be dropped. Points are in window coordinates.</summary>
public interface IDropTarget
{
    Control Surface { get; }
    /// <summary>Shows where the element would land; returns false when it cannot be dropped here.</summary>
    bool Hover(Point windowPoint, DragPayload payload);
    void Drop(Point windowPoint, DragPayload payload);
    void Leave();
}

/// <summary>
/// Drag and drop inside the editor window (palette to preview or tree, tree to tree, preview to preview). It follows the pointer itself
/// rather than using the system drag, so the drop indicators can be drawn by the panels and it behaves the same everywhere.
/// </summary>
public sealed class DragService
{
    private readonly Window _window;
    private readonly Canvas _layer;
    private readonly List<IDropTarget> _targets = [];
    private readonly Border _ghost;
    private IDropTarget? _current;
    private DragPayload? _payload;
    private IPointer? _pointer;
    private Control? _source;

    public DragService(Window window, Canvas layer)
    {
        _window = window;
        _layer = layer;
        _ghost = new Border
        {
            Background = Brush.Parse("#E62563EB"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3), IsVisible = false, IsHitTestVisible = false,
            Child = new TextBlock { Foreground = Brushes.White, FontSize = 12 },
        };
        _layer.Children.Add(_ghost);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsDragging) { Cancel(); e.Handled = true; }
        };
    }

    public bool IsDragging => _payload is not null && _pointer is not null;

    public void Register(IDropTarget target) => _targets.Add(target);

    /// <summary>Makes a control a drag source. The factory runs once the pointer has moved far enough; return null to refuse.</summary>
    public void Attach(Control source, Func<DragPayload?> factory)
    {
        Point start = default;
        var down = false;
        source.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
            start = e.GetPosition(_window);
            down = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(InputElement.PointerMovedEvent, (object? _, PointerEventArgs e) =>
        {
            if (!down) return;
            var point = e.GetPosition(_window);
            if (!IsDragging)
            {
                if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) { down = false; return; }
                if (Math.Abs(point.X - start.X) < 6 && Math.Abs(point.Y - start.Y) < 6) return;
                if (factory() is not { } payload) { down = false; return; }
                Begin(source, e.Pointer, payload);
            }
            if (_source == source) Move(point);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            var wasDragging = IsDragging && _source == source;
            down = false;
            if (wasDragging) Finish(e.GetPosition(_window));
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        source.PointerCaptureLost += (_, _) =>
        {
            down = false;
            if (IsDragging && _source == source) Cancel();
        };
    }

    private void Begin(Control source, IPointer pointer, DragPayload payload)
    {
        _payload = payload;
        _pointer = pointer;
        _source = source;
        pointer.Capture(source);
        ((TextBlock)_ghost.Child!).Text = payload.Label;
        _ghost.IsVisible = true;
    }

    private void Move(Point point)
    {
        Canvas.SetLeft(_ghost, point.X + 14);
        Canvas.SetTop(_ghost, point.Y + 10);
        var target = _targets.FirstOrDefault(t => Contains(t.Surface, point));
        if (target != _current) _current?.Leave();
        _current = target != null && target.Hover(point, _payload!) ? target : null;
        if (target != null && _current is null) target.Leave();
        _window.Cursor = _current is null ? new Cursor(StandardCursorType.No) : new Cursor(StandardCursorType.DragMove);
    }

    private void Finish(Point point)
    {
        var payload = _payload!;
        var target = _current;
        _current = null; // the target still needs what it worked out while hovering, so End must not tell it to let go
        End();
        target?.Drop(point, payload);
    }

    public void Cancel() => End();

    private void End()
    {
        _current?.Leave();
        _current = null;
        var pointer = _pointer;
        _pointer = null;
        _payload = null;
        _source = null;
        pointer?.Capture(null);
        _ghost.IsVisible = false;
        _window.Cursor = Cursor.Default;
    }

    private bool Contains(Control surface, Point windowPoint)
    {
        if (!surface.IsEffectivelyVisible || surface.TranslatePoint(new Point(0, 0), _window) is not { } origin) return false;
        return new Rect(origin, surface.Bounds.Size).Contains(windowPoint);
    }
}
