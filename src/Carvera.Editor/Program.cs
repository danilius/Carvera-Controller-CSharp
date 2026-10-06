using Avalonia;

namespace Carvera.Editor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The preview builds real Controller components; keep them away from the Controller's own settings.
        Model.EditorSettings.Initialise();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EditorApp>().UsePlatformDetect().LogToTrace();
}
