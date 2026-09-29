namespace Carvera.Core.State;

/// <summary>Well-known state paths. Layout files may bind to any of these.</summary>
public static class StatePaths
{
    public const string ConnectionState = "connection.state";      // Disconnected | Connecting | Connected
    public const string ConnectionAddress = "connection.address";
    public const string ConnectionKind = "connection.kind";        // wifi | usb | simulator
    public const string Connected = "connection.connected";        // bool

    public const string MachineState = "machine.state";            // Idle, Run, Hold, Alarm, Home, Tool, Wait, Pause, Sleep, Disable, N/A
    public const string MachineModel = "machine.model";
    public const string MachineModelName = "machine.modelName";
    public const string FirmwareVersion = "machine.firmware";
    public const string CommunityFirmware = "machine.communityFirmware";
    public const string HaltReason = "machine.haltReason";
    /// <summary>True from connecting until the first status report arrives: the "Wait" shown meanwhile is a placeholder, not the machine's own.</summary>
    public const string AwaitingStatus = "machine.awaitingStatus";
    /// <summary>True when the machine has stopped answering status queries for longer than <see cref="CarveraController.StallTimeout"/>.</summary>
    public const string Stalled = "machine.stalled";
    /// <summary>Whole seconds since the last status report while <see cref="Stalled"/>, else 0.</summary>
    public const string SilentSeconds = "machine.silentSeconds";
    public const string InchMode = "units.inch";
    public const string AbsoluteMode = "units.absolute";

    public static readonly string[] Axes = ["x", "y", "z", "a"];
    public static string AxisMachine(string axis) => $"axis.{axis.ToLowerInvariant()}.machine";
    public static string AxisWork(string axis) => $"axis.{axis.ToLowerInvariant()}.work";
    public static string AxisOffset(string axis) => $"axis.{axis.ToLowerInvariant()}.offset";

    public const string WcsActive = "wcs.active";                  // 0 = G54 ...
    public const string WcsActiveName = "wcs.activeName";
    public const string WcsRotation = "wcs.rotation";

    public const string FeedCurrent = "feed.current";
    public const string FeedTarget = "feed.target";
    public const string FeedOverride = "feed.override";
    public const string SpindleCurrent = "spindle.current";
    public const string SpindleTarget = "spindle.target";
    public const string SpindleOverride = "spindle.override";
    public const string SpindleTemperature = "spindle.temperature";
    public const string VacuumMode = "vacuum.mode";

    public const string ToolCurrent = "tool.current";
    public const string ToolOffset = "tool.offset";
    public const string ToolTarget = "tool.target";
    public const string AtcState = "atc.state";
    /// <summary>Derived: "T3", "Probe", "Laser", "3D Probe" or "No Tool".</summary>
    public const string ToolLabel = "tool.label";
    /// <summary>Derived: the same naming for the tool being changed to, or empty when no change is under way.</summary>
    public const string ToolTargetLabel = "tool.targetLabel";
    /// <summary>Derived: what the automatic tool changer is doing, or empty.</summary>
    public const string AtcLabel = "atc.label";
    public const string ProbeVoltage = "probe.voltage";

    // Ring-gauge drift check (RingGaugeSession) and the Z probe position (see ProbeCommands).
    public const string DriftEnabled = "probe.drift.enabled", DriftX = "probe.drift.x", DriftY = "probe.drift.y";
    public const string DriftStep = "probe.drift.step", DriftDone = "probe.drift.done", DriftRunning = "probe.drift.running";
    public const string DriftTitle = "probe.drift.title", DriftText = "probe.drift.text", DriftPrimary = "probe.drift.primary";
    public const string DriftPoints = "probe.drift.points", DriftResult = "probe.drift.result", DriftStored = "probe.drift.stored";
    public const string DriftApplyTip = "probe.drift.applyTip", DriftPersist = "probe.drift.persist";
    public const string ZProbeOrigin = "zprobe.origin", ZProbeX = "zprobe.x", ZProbeY = "zprobe.y", ZProbeLabel = "zprobe.label";

    public const string LaserMode = "laser.mode";
    public const string LaserState = "laser.state";
    public const string LaserTesting = "laser.testing";
    public const string LaserPower = "laser.power";
    public const string LaserScale = "laser.scale";

    public const string JobLines = "job.lines";
    public const string JobPercent = "job.percent";
    public const string JobSeconds = "job.seconds";
    public const string JobPlaying = "job.playing";

    public const string JogStep = "jog.step";
    public const string JogFeed = "jog.feed";
    /// <summary>"step" or "continuous": how a pendant jogs (a gamepad can switch modes).</summary>
    public const string JogMode = "jog.mode";

    public const string LocalFile = "file.local";
    public const string LocalFileName = "file.localName";
    public const string LocalFileLines = "file.lineCount";
    public const string FileModified = "file.modified";
    /// <summary>True when the open program has moves, so the extents below are valid.</summary>
    public const string FileHasBounds = "file.hasBounds";
    /// <summary>Extents of the open program's moves in its own (work) coordinates.</summary>
    public const string FileXMin = "file.xmin", FileXMax = "file.xmax", FileYMin = "file.ymin", FileYMax = "file.ymax", FileZMin = "file.zmin", FileZMax = "file.zmax";
    public const string FileOperations = "file.operationCount";

    /// <summary>Scrub position: index of the last previewed path segment, or -1 to show everything.</summary>
    public const string PreviewSegment = "preview.segment";
    /// <summary>1-based G-code line of the scrub position, or -1.</summary>
    public const string PreviewLine = "preview.line";
    public const string PreviewActive = "preview.active";
    public const string PreviewPlaying = "preview.playing";
    /// <summary>Operation shown alone in the viewer (0-based), or -1 for all.</summary>
    public const string PreviewOperation = "preview.operation";
    public const string PreviewOperationName = "preview.operationName";

    public const string LayoutName = "app.layout";

    /// <summary>File types the machine accepts for upload, from its "ftype = ..." reply (e.g. "lz").</summary>
    public const string MachineFileType = "machine.fileType";

    public const string TransferActive = "transfer.active";        // bool
    public const string TransferName = "transfer.name";            // file being sent
    public const string TransferPhase = "transfer.phase";          // Preparing | Uploading | Decompressing
    public const string TransferPercent = "transfer.percent";      // 0-100
    public const string TransferMessage = "transfer.message";      // last result, e.g. "Uploaded part.nc"
    public const string TransferDecompressed = "transfer.decompressed"; // blocks the machine has unpacked so far

    public const string RemoteDirectory = "remote.dir";                // folder the file browser shows, e.g. /sd/gcodes
    public const string RemoteCount = "remote.count";                  // entries in it
    public const string RemoteLoading = "remote.loading";              // bool
    public const string RemoteError = "remote.error";                  // last listing or file-operation error, or null
    public const string RemoteSelected = "remote.selected";            // path of the selected entry, or null
    public const string RemoteSelectedName = "remote.selectedName";
    public const string RemoteSelectedIsDirectory = "remote.selectedIsDirectory";

    public const string ConfigLoaded = "config.loaded";            // bool: the machine's settings have been read
    public const string ConfigPending = "config.pending";          // number of edited settings not yet sent

    public const string PendantConnected = "pendant.connected";    // bool
    public const string PendantName = "pendant.name";              // e.g. "CYD"
}
