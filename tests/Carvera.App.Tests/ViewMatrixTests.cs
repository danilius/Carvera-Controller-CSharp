using System.Numerics;
using Avalonia;
using Carvera.App.Components.Viewer;
using Xunit;

namespace Carvera.App.Tests;

public class ViewMatrixTests
{
    private static IEnumerable<Vector3> Points()
    {
        var random = new Random(3);
        for (var i = 0; i < 40; i++)
            yield return new Vector3((float)(random.NextDouble() * 200 - 100), (float)(random.NextDouble() * 200 - 100), (float)(random.NextDouble() * 60 - 30));
    }

    public static TheoryData<bool, double, double, double, double, double> Cameras() => new()
    {
        { false, -60, 30, 250, 900, 600 },
        { false, 20, 75, 80, 640, 480 },
        { false, -90, 0, 400, 1200, 500 },
        { true, -60, 30, 250, 900, 600 },
        { true, -90, 90, 120, 700, 700 },
    };

    [Theory]
    [MemberData(nameof(Cameras))]
    public void TheMatrixLandsOnTheSamePixelsAsTheCpuProjection(bool orthographic, double yaw, double pitch, double distance, double width, double height)
    {
        var camera = new OrbitCamera { Orthographic = orthographic, Yaw = yaw, Pitch = pitch, Distance = distance, Target = new Vector3(5, -8, 3), Viewport = new Size(width, height) };
        var mvp = ViewMatrix.For(camera);
        foreach (var point in Points())
        {
            var expected = camera.Project(point);
            var actual = ViewMatrix.ToPixels(mvp, point, width, height);
            if (expected is null)
            {
                Assert.True(actual is null || orthographic); // behind the near plane
                continue;
            }
            Assert.NotNull(actual);
            Assert.Equal(expected.Value.X, actual!.Value.X, 2);
            Assert.Equal(expected.Value.Y, actual.Value.Y, 2);
        }
    }

    [Fact]
    public void PointsBehindThePerspectiveCameraHaveNoPositiveW()
    {
        var camera = new OrbitCamera { Yaw = 0, Pitch = 0, Distance = 100, Target = Vector3.Zero, Viewport = new Size(800, 600) };
        var behind = camera.Eye - camera.Forward * 50; // behind the eye
        Assert.Null(ViewMatrix.ToPixels(ViewMatrix.For(camera), behind, 800, 600));
    }
}
