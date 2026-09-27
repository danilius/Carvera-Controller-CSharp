using System.Numerics;
using Avalonia;

namespace Carvera.App.Components.Viewer;

public enum ViewPreset { User, Top, Bottom, Front, Back, Right, Left }

/// <summary>
/// A Z-up turntable camera with Blender's view conventions: yaw about Z, pitch (elevation) clamped to
/// ±90°, perspective or orthographic projection with the same apparent size at the target, axis views
/// that switch to orthographic automatically ("auto perspective") and restore perspective on orbit.
/// </summary>
public sealed class OrbitCamera
{
    public const double FieldOfView = 35 * Math.PI / 180;

    public Vector3 Target { get; set; }
    /// <summary>Degrees about Z. -90 looks along +Y (front view).</summary>
    public double Yaw { get; set; } = -60;
    /// <summary>Degrees of elevation; 90 looks straight down.</summary>
    public double Pitch { get; set; } = 30;
    public double Distance { get; set; } = 200;
    public bool Orthographic { get; set; }
    public ViewPreset Preset { get; private set; } = ViewPreset.User;
    /// <summary>Projection to return to when leaving an axis view that switched to orthographic.</summary>
    private bool? _autoPerspectiveRestore;

    public Size Viewport { get; set; } = new(800, 600);

    private static double Rad(double deg) => deg * Math.PI / 180;

    /// <summary>Unit vector from the target towards the eye.</summary>
    public Vector3 Offset => new(
        (float)(Math.Cos(Rad(Pitch)) * Math.Cos(Rad(Yaw))),
        (float)(Math.Cos(Rad(Pitch)) * Math.Sin(Rad(Yaw))),
        (float)Math.Sin(Rad(Pitch)));

    public Vector3 Eye => Target + Offset * (float)Distance;
    public Vector3 Forward => -Offset;
    public Vector3 Right => new((float)-Math.Sin(Rad(Yaw)), (float)Math.Cos(Rad(Yaw)), 0);
    public Vector3 Up => Vector3.Cross(Right, Forward);

    /// <summary>World units per screen pixel at the target's depth.</summary>
    public double WorldPerPixel => 2 * Distance * Math.Tan(FieldOfView / 2) / Math.Max(1, Viewport.Height);

    public string Description => $"{(Preset == ViewPreset.User ? "User" : Preset.ToString())} {(Orthographic ? "Orthographic" : "Perspective")}";

    /// <summary>Camera-space coordinates: x right, y up, z depth along the view direction.</summary>
    public Vector3 ToView(Vector3 world)
    {
        var v = world - Eye;
        return new Vector3(Vector3.Dot(v, Right), Vector3.Dot(v, Up), Vector3.Dot(v, Forward));
    }

    public double NearPlane => Math.Max(1e-3, Distance * 1e-3);

    /// <summary>Projects a camera-space point to the screen; null when behind the near plane in perspective.</summary>
    public Point? ViewToScreen(Vector3 view)
    {
        var cx = Viewport.Width / 2;
        var cy = Viewport.Height / 2;
        if (Orthographic)
        {
            var scale = 1 / WorldPerPixel;
            return new Point(cx + view.X * scale, cy - view.Y * scale);
        }
        if (view.Z < NearPlane) return null;
        var focal = Viewport.Height / 2 / Math.Tan(FieldOfView / 2);
        return new Point(cx + view.X * focal / view.Z, cy - view.Y * focal / view.Z);
    }

    public Point? Project(Vector3 world) => ViewToScreen(ToView(world));

    /// <summary>Projects a segment, clipping it at the near plane. Returns false when nothing is visible.</summary>
    public bool ProjectSegment(Vector3 a, Vector3 b, out Point pa, out Point pb)
    {
        var va = ToView(a);
        var vb = ToView(b);
        if (!Orthographic)
        {
            var near = (float)NearPlane;
            if (va.Z < near && vb.Z < near) { pa = pb = default; return false; }
            if (va.Z < near) va = Vector3.Lerp(va, vb, (near - va.Z) / (vb.Z - va.Z));
            else if (vb.Z < near) vb = Vector3.Lerp(vb, va, (near - vb.Z) / (va.Z - vb.Z));
        }
        pa = ViewToScreen(va) ?? default;
        pb = ViewToScreen(vb) ?? default;
        return true;
    }

    // ---------------------------------------------------------------- navigation

    public void Orbit(double deltaYaw, double deltaPitch)
    {
        Yaw = Normalize(Yaw + deltaYaw);
        Pitch = Math.Clamp(Pitch + deltaPitch, -90, 90);
        LeaveAxisView();
    }

    /// <summary>Moves the view so the scene follows the pointer by the given pixel delta.</summary>
    public void Pan(double dxPixels, double dyPixels)
    {
        var k = (float)WorldPerPixel;
        Target += -Right * (float)dxPixels * k + Up * (float)dyPixels * k;
    }

    /// <summary>factor &lt; 1 zooms in.</summary>
    public void Zoom(double factor) => Distance = Math.Clamp(Distance * factor, 0.05, 1e6);

    public void ToggleProjection()
    {
        Orthographic = !Orthographic;
        _autoPerspectiveRestore = null;
    }

    public void SetView(ViewPreset preset)
    {
        (double yaw, double pitch) = preset switch
        {
            ViewPreset.Top => (-90, 90),
            ViewPreset.Bottom => (-90, -90),
            ViewPreset.Front => (-90, 0),
            ViewPreset.Back => (90, 0),
            ViewPreset.Right => (0, 0),
            ViewPreset.Left => (180, 0),
            _ => (Yaw, Pitch),
        };
        Yaw = yaw;
        Pitch = pitch;
        if (preset != ViewPreset.User && Preset == ViewPreset.User)
        {
            _autoPerspectiveRestore = !Orthographic;
            Orthographic = true;
        }
        Preset = preset;
    }

    /// <summary>Numpad 9: look from the opposite side.</summary>
    public void Opposite()
    {
        var opposite = Preset switch
        {
            ViewPreset.Top => ViewPreset.Bottom,
            ViewPreset.Bottom => ViewPreset.Top,
            ViewPreset.Front => ViewPreset.Back,
            ViewPreset.Back => ViewPreset.Front,
            ViewPreset.Right => ViewPreset.Left,
            ViewPreset.Left => ViewPreset.Right,
            _ => ViewPreset.User,
        };
        if (opposite != ViewPreset.User) SetView(opposite);
        else
        {
            Yaw = Normalize(Yaw + 180);
            Pitch = -Pitch;
        }
    }

    private void LeaveAxisView()
    {
        if (Preset == ViewPreset.User) return;
        Preset = ViewPreset.User;
        if (_autoPerspectiveRestore == true) Orthographic = false;
        _autoPerspectiveRestore = null;
    }

    /// <summary>Centres on the box and sets the distance so it fits the viewport with a margin.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        Target = (min + max) / 2;
        var radius = Math.Max(1, (max - min).Length() / 2);
        var aspect = Viewport.Width / Math.Max(1, Viewport.Height);
        var halfFov = FieldOfView / 2;
        var fitVertical = radius / Math.Sin(halfFov);
        var fitHorizontal = radius / Math.Sin(Math.Atan(Math.Tan(halfFov) * Math.Max(0.1, aspect)));
        Distance = Math.Max(fitVertical, fitHorizontal) * 1.1;
    }

    private static double Normalize(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        return degrees;
    }
}
