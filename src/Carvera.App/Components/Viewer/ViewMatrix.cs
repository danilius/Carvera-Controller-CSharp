using System.Numerics;

namespace Carvera.App.Components.Viewer;

/// <summary>
/// The camera as a 4×4 matrix for the GPU renderer. Multiplying a world point (row vector) by it gives clip coordinates that
/// land on the same pixel as <see cref="OrbitCamera.Project"/>; uploaded as-is (no transpose) it works as
/// <c>uMvp * vec4(position, 1)</c> in GLSL.
/// </summary>
public static class ViewMatrix
{
    public static Matrix4x4 For(OrbitCamera camera)
    {
        var eye = camera.Eye;
        var right = camera.Right;
        var up = camera.Up;
        var forward = camera.Forward;
        var view = new Matrix4x4(
            right.X, up.X, forward.X, 0,
            right.Y, up.Y, forward.Y, 0,
            right.Z, up.Z, forward.Z, 0,
            -Vector3.Dot(eye, right), -Vector3.Dot(eye, up), -Vector3.Dot(eye, forward), 1);

        var width = Math.Max(1, camera.Viewport.Width);
        var height = Math.Max(1, camera.Viewport.Height);
        Matrix4x4 projection;
        if (camera.Orthographic)
        {
            // Screen x = cx + vx / worldPerPixel, so NDC x = vx / (worldPerPixel * width / 2).
            var fx = (float)(1 / (camera.WorldPerPixel * width / 2));
            var fy = (float)(1 / (camera.WorldPerPixel * height / 2));
            const float extent = 1e7f;
            projection = new Matrix4x4(
                fx, 0, 0, 0,
                0, fy, 0, 0,
                0, 0, 1 / extent, 0,
                0, 0, 0, 1);
        }
        else
        {
            var near = (float)camera.NearPlane;
            var far = (float)Math.Max(1e5, camera.Distance * 1e4);
            var f = (float)(1 / Math.Tan(OrbitCamera.FieldOfView / 2));
            var a = (far + near) / (far - near);
            var b = -2 * far * near / (far - near);
            projection = new Matrix4x4(
                (float)(f * height / width), 0, 0, 0,
                0, f, 0, 0,
                0, 0, a, 1,
                0, 0, b, 0);
        }
        return view * projection;
    }

    /// <summary>Clip-space to pixels, the inverse of what the GPU does; used to check the matrix against the CPU projection.</summary>
    public static (double X, double Y)? ToPixels(Matrix4x4 mvp, Vector3 world, double width, double height)
    {
        var clip = Vector4.Transform(new Vector4(world, 1), mvp);
        if (clip.W <= 0) return null;
        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        return ((ndcX + 1) / 2 * width, (1 - ndcY) / 2 * height);
    }
}
