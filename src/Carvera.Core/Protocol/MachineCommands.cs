using System.Globalization;
using System.Text;

namespace Carvera.Core.Protocol;

/// <summary>
/// Builders for the text commands understood by Carvera firmware. Ported from the command methods of
/// Controller.py. All returned lines end with a newline; real-time commands are single bytes.
/// </summary>
public static class MachineCommands
{
    public const byte FeedHold = (byte)'!';
    public const byte CycleStart = (byte)'~';
    public const byte SoftReset = 0x18;
    public const byte StopContinuousJog = 0x19;
    public const string StatusQuery = "?";
    public const string DiagnoseQuery = "diagnose\n";

    private static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Line(string command) => command.EndsWith('\n') ? command : command + "\n";

    public const string Abort = "abort\n";
    public const string Unlock = "$X\n";
    public const string Home = "$H\n";
    public const string Reset = "reset\n";
    public const string Version = "version\n";
    public const string Model = "model\n";
    public const string GetWcs = "get wcs\n";
    public const string Suspend = "suspend\n";
    public const string Resume = "resume\n";

    public static string Jog(IReadOnlyDictionary<char, double> moves, double? feed)
    {
        var sb = new StringBuilder("$J");
        foreach (var (axis, distance) in moves) sb.Append(' ').Append(char.ToUpperInvariant(axis)).Append(N(distance));
        if (feed is > 0) sb.Append(" F").Append(N(feed.Value));
        return sb.Append('\n').ToString();
    }

    public static string Goto(double? x = null, double? y = null, double? z = null)
    {
        var sb = new StringBuilder("G90G0");
        if (x is not null) sb.Append('X').Append(N(x.Value));
        if (y is not null) sb.Append('Y').Append(N(y.Value));
        if (z is not null) sb.Append('Z').Append(N(z.Value));
        return sb.Append('\n').ToString();
    }

    public const string GotoSafeZ = "G53 G0 Z-2\n";
    public const string GotoMachineHomeXY = "G53 G0 X-2 Y-2\n";
    public static string GotoWcsHomeXY(double wcoX, double wcoY) => $"G53 G0 X{N(wcoX)} Y{N(wcoY)}\n";
    public const string GotoClearance = "M496.1\n";
    public const string GotoWorkOrigin = "M496.2\n";
    public const string GotoAnchor1 = "M496.3\n";
    public const string GotoAnchor2 = "M496.4\n";

    /// <summary>G10 L20 P0: set the current work position of the given axes (default zero).</summary>
    public static string SetWorkPosition(double? x = null, double? y = null, double? z = null, double? a = null)
    {
        var sb = new StringBuilder("G10L20P0");
        if (x is { } vx && Math.Abs(vx) < 10000) sb.Append('X').Append(N(vx));
        if (y is { } vy && Math.Abs(vy) < 10000) sb.Append('Y').Append(N(vy));
        if (z is { } vz && Math.Abs(vz) < 10000) sb.Append('Z').Append(N(vz));
        if (a is { } va && Math.Abs(va) < 3600000) sb.Append('A').Append(N(va));
        return sb.Append('\n').ToString();
    }

    public static string SelectWcs(int index) => ResponseParser.WcsNames[Math.Clamp(index, 0, ResponseParser.WcsNames.Length - 1)] + "\n";
    public static string SetRotation(double degrees) => $"G10L2R{degrees.ToString("0.000", CultureInfo.InvariantCulture)}P0\n";
    public const string ClearRotation = "G10L2R0P0\n";

    public static string FeedOverride(int percent, bool instant) => instant ? $"$F S{percent}\n" : $"M220 S{percent}\n";
    public static string SpindleOverride(int percent, bool instant) => instant ? $"$O S{percent}\n" : $"M223 S{percent}\n";
    public static string LaserScale(int percent) => $"M325 S{percent}\n";

    public static string Spindle(bool on, double? rpm = null) => !on ? "M5\n" : rpm is { } r ? $"M3 S{(int)r}\n" : "M3\n";
    public static string Light(bool on) => on ? "M821\n" : "M822\n";
    public static string Air(bool on) => on ? "M7\n" : "M9\n";
    public static string ToolSensorPower(bool on) => on ? "M831\n" : "M832\n";
    public static string WorkpieceChargePower(bool on) => on ? "M841\n" : "M842\n";
    public static string VacuumMode(bool on) => on ? "M331\n" : "M332\n";
    public static string LaserMode(bool on) => on ? "M321\n" : "M322\n";
    public static string LaserTest(bool on) => on ? "M323\n" : "M324\n";
    public static string Vacuum(int power) => power > 0 ? $"M801 S{power}\n" : "M802\n";
    public static string SpindleFan(int power) => power > 0 ? $"M811 S{power}\n" : "M812\n";
    public static string ExternalControl(int pwm) => pwm > 0 ? $"M851 S{pwm}\n" : "M852\n";

    public static string ChangeTool(int tool) => $"M6T{tool}\n";
    public static string SetTool(int tool) => $"M493.2T{tool}\n";
    public const string DropTool = "M6T-1\n";
    public const string CalibrateTool = "M491\n";
    public const string ClampTool = "M490.1\n";
    public const string UnclampTool = "M490.2\n";
    public const string ClearAutoLevel = "M370\n";

    /// <summary>Escapes characters that the firmware treats as real-time commands inside file names.</summary>
    public static string EscapeArgument(string value) =>
        value.Replace('\\', '/').Replace(' ', '\x01').Replace('?', '\x02').Replace('&', '\x03').Replace('!', '\x04').Replace('~', '\x05');

    public static string Play(string remotePath) => $"play {EscapeArgument(remotePath)}\n";
    public static string ListDirectory(string remotePath) => $"ls -e -s {EscapeArgument(remotePath)}\n";
}
