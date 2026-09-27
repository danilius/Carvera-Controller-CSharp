using System.Numerics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Carvera.App.Components.Viewer;
using Carvera.Core.Gcode;
using Xunit;

namespace Carvera.App.Tests;

public class ViewerTests
{
    private static OrbitCamera Camera() => new() { Viewport = new Size(800, 600), Target = Vector3.Zero, Distance = 100 };

    [Fact]
    public void AxisViewsMatchBlender()
    {
        var c = Camera();
        c.SetView(ViewPreset.Top);
        var right = c.Project(new Vector3(10, 0, 0))!.Value;
        var up = c.Project(new Vector3(0, 10, 0))!.Value;
        Assert.True(right.X > 400 && Math.Abs(right.Y - 300) < 1e-3, "+X points right in top view");
        Assert.True(up.Y < 300 && Math.Abs(up.X - 400) < 1e-3, "+Y points up in top view");

        c.SetView(ViewPreset.Front);
        Assert.True(c.Project(new Vector3(0, 0, 10))!.Value.Y < 300, "+Z points up in front view");
        Assert.True(c.Project(new Vector3(10, 0, 0))!.Value.X > 400, "+X points right in front view");

        c.SetView(ViewPreset.Right);
        Assert.True(c.Project(new Vector3(0, 10, 0))!.Value.X > 400, "+Y points right in right view");
    }

    [Fact]
    public void AxisViewsAreOrthographicUntilOrbited()
    {
        var c = Camera();
        Assert.False(c.Orthographic);
        c.SetView(ViewPreset.Top);
        Assert.True(c.Orthographic);
        Assert.Equal("Top Orthographic", c.Description);
        c.SetView(ViewPreset.Front);
        Assert.True(c.Orthographic);
        c.Orbit(10, 0);
        Assert.False(c.Orthographic);
        Assert.Equal("User Perspective", c.Description);

        // A view the user made orthographic stays orthographic.
        c.ToggleProjection();
        c.SetView(ViewPreset.Top);
        c.Orbit(10, 0);
        Assert.True(c.Orthographic);
    }

    [Fact]
    public void OrthographicKeepsTheApparentSizeAtTheTarget()
    {
        var c = Camera();
        var persp = c.Project(new Vector3(0, 0, 0) + c.Right * 10)!.Value;
        c.ToggleProjection();
        var ortho = c.Project(new Vector3(0, 0, 0) + c.Right * 10)!.Value;
        Assert.Equal(persp.X, ortho.X, 3);
    }

    [Fact]
    public void PanFollowsThePointerAndFrameFitsTheBox()
    {
        var c = Camera();
        c.SetView(ViewPreset.Top);
        var before = c.Project(Vector3.Zero)!.Value;
        c.Pan(50, 20);
        var after = c.Project(Vector3.Zero)!.Value;
        Assert.Equal(before.X + 50, after.X, 3);
        Assert.Equal(before.Y + 20, after.Y, 3);

        c.Frame(new Vector3(0, 0, 0), new Vector3(200, 100, 10));
        foreach (var corner in new[] { new Vector3(0, 0, 0), new Vector3(200, 100, 10), new Vector3(200, 0, 0), new Vector3(0, 100, 10) })
        {
            var p = c.Project(corner)!.Value;
            Assert.InRange(p.X, 0, 800);
            Assert.InRange(p.Y, 0, 600);
        }
    }

    [Fact]
    public void SegmentsBehindTheCameraAreClipped()
    {
        var c = Camera();
        c.SetView(ViewPreset.Front);
        c.ToggleProjection(); // perspective
        var eye = c.Eye;
        Assert.False(c.ProjectSegment(eye - c.Forward * 10, eye - c.Forward * 20, out _, out _));
        Assert.True(c.ProjectSegment(eye - c.Forward * 10, c.Target, out var a, out var b));
        Assert.True(double.IsFinite(a.X) && double.IsFinite(b.X));
    }

    private const string ViewerLayout = """
        { "root": { "type": "stack", "children": [
          { "type": "toolpath", "id": "view" },
          { "type": "button", "command": "feedHold", "height": 1 }, { "type": "button", "command": "stop", "height": 1 }, { "type": "button", "command": "reset", "height": 1 }
        ] } }
        """;

    private static ToolpathView View(Harness h) => h.Host("view").GetVisualDescendants().OfType<ToolpathView>().Single();

    [AvaloniaFact]
    public void MouseNavigationFollowsBlender()
    {
        using var h = new Harness(ViewerLayout, 800, 600);
        var view = View(h);
        var cam = view.Camera;
        var yaw = cam.Yaw;
        var pitch = cam.Pitch;

        // Middle-drag orbits.
        h.Window.MouseDown(new Point(300, 300), MouseButton.Middle);
        h.Window.MouseMove(new Point(350, 320));
        h.Window.MouseUp(new Point(350, 320), MouseButton.Middle);
        Assert.NotEqual(yaw, cam.Yaw);
        Assert.NotEqual(pitch, cam.Pitch);

        // Shift+middle-drag pans.
        var target = cam.Target;
        h.Window.MouseDown(new Point(300, 300), MouseButton.Middle, RawInputModifiers.Shift);
        h.Window.MouseMove(new Point(340, 300), RawInputModifiers.Shift | RawInputModifiers.MiddleMouseButton);
        h.Window.MouseUp(new Point(340, 300), MouseButton.Middle, RawInputModifiers.Shift);
        Assert.NotEqual(target, cam.Target);

        // Wheel zooms in.
        var distance = cam.Distance;
        h.Window.MouseWheel(new Point(300, 300), new Avalonia.Vector(0, 1));
        Assert.True(cam.Distance < distance);

        // Alt+left-drag emulates the middle button.
        yaw = cam.Yaw;
        h.Window.MouseDown(new Point(300, 300), MouseButton.Left, RawInputModifiers.Alt);
        h.Window.MouseMove(new Point(380, 300), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(new Point(380, 300), MouseButton.Left, RawInputModifiers.Alt);
        Assert.NotEqual(yaw, cam.Yaw);
    }

    [AvaloniaFact]
    public void NumpadAndGizmoAlignViews()
    {
        using var h = new Harness(ViewerLayout, 800, 600);
        var view = View(h);
        view.Focus();
        h.Window.KeyPress(Key.NumPad7, RawInputModifiers.None, PhysicalKey.NumPad7, null);
        Assert.Equal(ViewPreset.Top, view.Camera.Preset);
        h.Window.KeyPress(Key.NumPad1, RawInputModifiers.Control, PhysicalKey.NumPad1, null);
        Assert.Equal(ViewPreset.Back, view.Camera.Preset);
        h.Window.KeyPress(Key.NumPad5, RawInputModifiers.None, PhysicalKey.NumPad5, null);
        Assert.False(view.Camera.Orthographic);

        // Clicking the gizmo's Z ball (straight up from its centre in front view) aligns to top.
        h.Window.KeyPress(Key.NumPad1, RawInputModifiers.None, PhysicalKey.NumPad1, null);
        var origin = view.TranslatePoint(new Point(0, 0), h.Window)!.Value;
        var centre = new Point(origin.X + view.Bounds.Width - 42 - 14, origin.Y + 42 + 14);
        var zBall = new Point(centre.X, centre.Y - 32);
        h.Window.MouseDown(zBall, MouseButton.Left);
        h.Window.MouseUp(zBall, MouseButton.Left);
        Assert.Equal(ViewPreset.Top, view.Camera.Preset);
    }
}
