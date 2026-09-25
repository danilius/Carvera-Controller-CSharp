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
    public const string ProbeVoltage = "probe.voltage";

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

    public const string LocalFile = "file.local";
    public const string LocalFileName = "file.localName";
    public const string LocalFileLines = "file.lineCount";

    public const string LayoutName = "app.layout";
}
