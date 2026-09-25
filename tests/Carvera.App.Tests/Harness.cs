using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Carvera.App.Layout;
using Carvera.App.Services;
using Carvera.App.Shell;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Layout;

namespace Carvera.App.Tests;

/// <summary>Builds a layout from JSON into a headless window.</summary>
public sealed class Harness : IDisposable
{
    public Harness(string json, double width = 800, double height = 600)
    {
        Controller = new CarveraController();
        Services = new AppServices(Controller, new NullHost(), new LayoutLibrary([Path.Combine(AppContext.BaseDirectory, "layouts")]), new Settings());
        var result = LayoutLoader.Load(json, "test", Path.Combine(AppContext.BaseDirectory, "layouts"), null, new LayoutValidator(Services.Commands));
        if (result.Document is null) throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
        Session = new LayoutSession(result.Document, Services);
        Window = new Window { Width = width, Height = height, Content = Session.Root };
        Window.Show();
        Pump();
    }

    public CarveraController Controller { get; }
    public AppServices Services { get; }
    public LayoutSession Session { get; }
    public Window Window { get; }

    public ComponentHost Host(string id) => Session.FindById(id) ?? throw new InvalidOperationException($"No element with id '{id}'.");

    public void Pump()
    {
        Services.Binder.Flush();
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    public Rect BoundsOf(string id)
    {
        var host = Host(id);
        var origin = host.TranslatePoint(new Point(0, 0), Window) ?? default;
        return new Rect(origin, host.Bounds.Size);
    }

    public void Dispose()
    {
        Window.Close();
        Session.Dispose();
        Services.Dispose();
    }

    private sealed class NullHost : IAppHost
    {
        public Task OpenFileAsync(string? path) => Task.CompletedTask;
        public Task CloseFileAsync() => Task.CompletedTask;
        public Task LoadLayoutAsync(string name) => Task.CompletedTask;
        public Task ReloadLayoutAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
    }
}
