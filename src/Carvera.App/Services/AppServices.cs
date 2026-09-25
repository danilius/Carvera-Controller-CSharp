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

    public void SetProgram(GcodeProgram? program, bool modified = false)
    {
        var keepPreview = modified && Program is not null;
        Program = program;
        using (State.BeginBatch())
        {
            State.Set(StatePaths.LocalFile, program?.Path);
            State.Set(StatePaths.LocalFileName, program?.Path is { } p ? System.IO.Path.GetFileName(p) : null);
            State.Set(StatePaths.LocalFileLines, program?.Lines.Count ?? 0);
            State.Set(StatePaths.FileOperations, program?.Operations.Count ?? 0);
            State.Set(StatePaths.FileModified, modified);
            if (!keepPreview)
            {
                SetPreviewSegment(-1);
                SelectOperation(-1);
            }
        }
        ProgramChanged?.Invoke();
    }

    /// <summary>Replaces the program text after an edit, keeping the preview position.</summary>
    public void EditProgram(IReadOnlyList<string> lines)
    {
        if (Program is null) return;
        SetProgram(Program.WithLines(lines), modified: true);
        SetPreviewSegment(State.Get(StatePaths.PreviewSegment, -1));
    }

    /// <summary>Sets the scrub position to a path segment (clamped); -1 shows the whole program.</summary>
    public void SetPreviewSegment(int segment)
    {
        var program = Program;
        if (program is null || program.Segments.Count == 0) segment = -1;
        else if (segment >= program.Segments.Count) segment = program.Segments.Count - 1;
        if (segment < -1) segment = -1;
        using var _ = State.BeginBatch();
        State.Set(StatePaths.PreviewSegment, segment);
        State.Set(StatePaths.PreviewActive, segment >= 0);
        State.Set(StatePaths.PreviewLine, segment >= 0 ? program!.Segments[segment].Line : -1);
    }

    /// <summary>Scrubs to the last path segment at or before a 1-based line.</summary>
    public void SetPreviewLine(int line)
    {
        if (Program is null) return;
        var segment = Program.LastSegmentAtOrBefore(line);
        SetPreviewSegment(segment < 0 && Program.Segments.Count > 0 ? 0 : segment);
    }

    public void SelectOperation(int index)
    {
        var operations = Program?.Operations ?? [];
        if (index < 0 || index >= operations.Count) index = -1;
        using var _ = State.BeginBatch();
        State.Set(StatePaths.PreviewOperation, index);
        State.Set(StatePaths.PreviewOperationName, index >= 0 ? operations[index].Name : null);
    }

    public void Dispose() => Binder.Dispose();
}
