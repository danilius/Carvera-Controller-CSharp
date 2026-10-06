using Avalonia.Controls;
using Carvera.App.Layout;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Config;
using Carvera.Core.Gcode;
using Carvera.Core.State;
using Carvera.Core.Transfer;

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
        Remote = new RemoteBrowser(controller, settings.UploadDirectory);
        RemoteCommands.Register(Commands, Remote, Transfers, host);
        ToolCommands.Register(Commands, host);
        MachineConfig = new MachineConfigStore(controller);
        ConfigCommands.Register(Commands, MachineConfig, host);
        var probeStore = new SettingsProbeStore(settings);
        ProbeCommands.Register(Commands, host, probeStore);
        WorkCommands.Register(Commands, host, MachineConfig);
        JobCommands.Register(Commands, host, probeStore, MachineConfig);
        // The jog options live in the state store (commands and layouts use them) and are kept in the settings.
        State.Set(StatePaths.JogButtonMode, settings.JogButtonMode);
        State.Set(StatePaths.JogKeyboard, settings.JogKeyboard);
        State.Set(StatePaths.JogInvertY, settings.JogInvertY);
        State.Set(StatePaths.ViewBedImage, settings.ShowBedImage);
        State.Changed += paths =>
        {
            if (paths.Contains(StatePaths.ViewBedImage) && State.Get(StatePaths.ViewBedImage, true) != settings.ShowBedImage)
            {
                settings.ShowBedImage = State.Get(StatePaths.ViewBedImage, true);
                settings.Save();
            }
            if (!paths.Contains(StatePaths.JogButtonMode) && !paths.Contains(StatePaths.JogKeyboard) && !paths.Contains(StatePaths.JogInvertY)) return;
            settings.JogButtonMode = State.Get(StatePaths.JogButtonMode, "step");
            settings.JogKeyboard = State.Get(StatePaths.JogKeyboard, true);
            settings.JogInvertY = State.Get(StatePaths.JogInvertY, true);
            settings.Save();
        };
        // Once per connection, when the machine first reports idle, read its config.txt: the bed picture and the anchor positions need it.
        var configRequested = false;
        State.Changed += paths =>
        {
            if (!paths.Contains(StatePaths.Connected) && !paths.Contains(StatePaths.MachineState)) return;
            if (!State.Get(StatePaths.Connected, false)) { configRequested = false; return; }
            if (configRequested || !settings.AutoReadConfig || MachineConfig.Loaded || State.Get<string>(StatePaths.MachineState) != "Idle") return;
            configRequested = true;
            _ = Task.Run(async () =>
            {
                try { await MachineConfig.LoadAsync(); }
                catch (Exception ex) { Console.Warning("Could not read the machine's settings: " + ex.Message); }
            });
        };
        Pendants = new PendantService(this);
    }

    public PendantService Pendants { get; }
    public MachineConfigStore MachineConfig { get; }

    /// <summary>Holds the cancellation of the file transfer in progress; only one runs at a time.</summary>
    public TransferGate Transfers { get; } = new();

    /// <summary>The file browser's state: the folder shown on the machine, its entries and the selection.</summary>
    public RemoteBrowser Remote { get; }

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
            var hasBounds = program is { Segments.Count: > 0 };
            State.Set(StatePaths.FileHasBounds, hasBounds);
            State.Set(StatePaths.FileXMin, hasBounds ? program!.Min.X : 0.0);
            State.Set(StatePaths.FileXMax, hasBounds ? program!.Max.X : 0.0);
            State.Set(StatePaths.FileYMin, hasBounds ? program!.Min.Y : 0.0);
            State.Set(StatePaths.FileYMax, hasBounds ? program!.Max.Y : 0.0);
            State.Set(StatePaths.FileZMin, hasBounds ? program!.Min.Z : 0.0);
            State.Set(StatePaths.FileZMax, hasBounds ? program!.Max.Z : 0.0);
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

    public void Dispose()
    {
        Remote.Dispose();
        Pendants.Dispose();
        Binder.Dispose();
    }
}
