using Avalonia.Controls;
using Carvera.App.Layout;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Gcode;
using Carvera.Core.State;

namespace Carvera.App.Services;

/// <summary>Long-lived application objects shared by every layout.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(CarveraController controller, IAppHost host, LayoutLibrary layouts, Settings settings)
    {
        Controller = controller;
        Layouts = layouts;
        Settings = settings;
        Binder = new StateBinder(controller.State);
        Commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(Commands);
        AppCommands.Register(Commands, host);
    }

    public CarveraController Controller { get; }
    public StateStore State => Controller.State;
    public ConsoleLog Console => Controller.Console;
    public StateBinder Binder { get; }
    public CommandRegistry Commands { get; }
    public LayoutLibrary Layouts { get; }
    public Settings Settings { get; }
    public Window? MainWindow { get; set; }

    /// <summary>The local G-code file being previewed.</summary>
    public GcodeProgram? Program { get; private set; }
    public event Action? ProgramChanged;

    public void SetProgram(GcodeProgram? program)
    {
        Program = program;
        using (State.BeginBatch())
        {
            State.Set(StatePaths.LocalFile, program?.Path);
            State.Set(StatePaths.LocalFileName, program?.Path is { } p ? System.IO.Path.GetFileName(p) : null);
            State.Set(StatePaths.LocalFileLines, program?.Lines.Count ?? 0);
        }
        ProgramChanged?.Invoke();
    }

    public void Dispose() => Binder.Dispose();
}
