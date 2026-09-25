using Avalonia;
using Avalonia.Headless;
using Carvera.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Carvera.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Keep settings written by the app out of the user's profile.
        Environment.SetEnvironmentVariable("CARVERA_CS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "carvera-cs-tests", Guid.NewGuid().ToString("N")));
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
