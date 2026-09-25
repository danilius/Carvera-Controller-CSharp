using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Carvera.App.Shell;

namespace Carvera.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        // Numeric and text inputs without spinners; flat light controls to match the layout visuals.
        Styles.Add(new Style(s => s.OfType<NumericUpDown>()) { Setters = { new Setter(NumericUpDown.ShowButtonSpinnerProperty, false) } });
        Styles.Add(new Style(s => s.OfType<ToolTip>())
        {
            Setters =
            {
                new Setter(ToolTip.BackgroundProperty, Brushes.White),
                new Setter(ToolTip.ForegroundProperty, Brush.Parse("#1E293B")),
                new Setter(ToolTip.BorderBrushProperty, Brush.Parse("#D8DEE7")),
                new Setter(ToolTip.BorderThicknessProperty, new Thickness(1)),
                new Setter(ToolTip.CornerRadiusProperty, new CornerRadius(6)),
            },
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }
}
