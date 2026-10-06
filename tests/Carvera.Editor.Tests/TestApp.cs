using Avalonia;
using Avalonia.Headless;
using Carvera.Editor;
using Carvera.Editor.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Carvera.Editor.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("CARVERA_CS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "carvera-editor-tests", Guid.NewGuid().ToString("N")));
        return AppBuilder.Configure<EditorApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
