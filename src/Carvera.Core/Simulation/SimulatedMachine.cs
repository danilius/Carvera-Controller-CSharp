using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Carvera.Core.Connection;
using Carvera.Core.Protocol;

namespace Carvera.Core.Simulation;

/// <summary>
/// An in-process stand-in for a Carvera machine. It answers status and diagnose queries and understands
/// enough commands (jogging, G0 moves, work offsets, switches, hold/resume/reset) to exercise layouts
/// without hardware. It is not a G-code interpreter.
/// </summary>
public sealed partial class SimulatedMachine : IMachineStream
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly StringBuilder _input = new();
    private readonly object _gate = new();
    private readonly double[] _machine = [-200, -150, -5, 0];
    private readonly double[][] _wcs = Enumerable.Range(0, 6).Select(_ => new double[] { -180, -120, -40, 0 }).ToArray();
    private int _activeWcs;
    private string _state = "Idle";
    private bool _held;
    private bool _light, _air, _spindle, _toolSensor, _wpCharge, _vacuum;
    private double _spindleRpm;
    private int _feedOverride = 100, _spindleOverride = 100;
    private int _tool = 1;
    private readonly Queue<(int Axis, double Target)> _pendingMoves = new();
    private Timer? _motion;

    public string Description => "Simulator";

    public double[] MachinePosition { get { lock (_gate) return (double[])_machine.Clone(); } }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        _motion = new Timer(_ => Step(), null, 50, 50);
        Reply("Carvera simulator ready");
        return Task.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        foreach (var b in data.Span) Receive(b);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await _output.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
        var chunk = await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        chunk.AsSpan(0, Math.Min(chunk.Length, buffer.Length)).CopyTo(buffer.Span);
        return Math.Min(chunk.Length, buffer.Length);
    }

    public ValueTask DisposeAsync()
    {
        _motion?.Dispose();
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Reply(string line) => _output.Writer.TryWrite(Encoding.ASCII.GetBytes(line + "\n"));

    private void Receive(byte b)
    {
        switch (b)
        {
            case (byte)'?': Reply(StatusReport()); return;
            case MachineCommands.FeedHold: lock (_gate) { if (_state is "Run" or "Idle") { _held = true; _state = "Hold"; } } return;
            case MachineCommands.CycleStart: lock (_gate) { if (_held) { _held = false; _state = _pendingMoves.Count > 0 ? "Run" : "Idle"; } } return;
            case MachineCommands.SoftReset:
                lock (_gate) { _pendingMoves.Clear(); _held = false; _spindle = false; _state = "Idle"; }
                Reply("ok");
                return;
            case MachineCommands.StopContinuousJog: return;
            case (byte)'\r': return;
            case (byte)'\n':
                var line = _input.ToString().Trim();
                _input.Clear();
                if (line.Length > 0) Execute(line);
                return;
            default:
                if (b == '1' && _input.Length == 0) return; // "?1" status variant used during continuous jog
                _input.Append((char)b);
                return;
        }
    }

    private void Execute(string line)
    {
        var upper = line.ToUpperInvariant();
        lock (_gate)
        {
            if (upper == "DIAGNOSE") { Reply(DiagnoseReport()); return; }
            if (upper == "VERSION") { Reply("version = 2.1.0c-sim"); return; }
            if (upper == "MODEL") { Reply("model = CA1"); return; }
            if (upper == "GET WCS") { Reply($"[current WCS: {ResponseParser.WcsNames[_activeWcs]}]"); return; }
            if (upper is "ABORT") { _pendingMoves.Clear(); _held = false; _state = "Idle"; Reply("ok"); return; }
            if (upper is "$X") { if (_state == "Alarm") _state = "Idle"; Reply("ok"); return; }
            if (upper is "$H") { Enqueue(2, 0); Enqueue(0, 0); Enqueue(1, 0); Reply("ok"); return; }
            if (upper.StartsWith("$J", StringComparison.Ordinal)) { Jog(upper); Reply("ok"); return; }
            if (upper.StartsWith("G10L20P0", StringComparison.Ordinal)) { SetWork(upper[8..]); Reply("ok"); return; }
            var wcs = Array.IndexOf(ResponseParser.WcsNames, upper);
            if (wcs is >= 0 and < 6) { _activeWcs = wcs; Reply("ok"); return; }
            switch (upper)
            {
                case "M821": _light = true; break;
                case "M822": _light = false; break;
                case "M7": _air = true; break;
                case "M9": _air = false; break;
                case "M831": _toolSensor = true; break;
                case "M832": _toolSensor = false; break;
                case "M841": _wpCharge = true; break;
                case "M842": _wpCharge = false; break;
                case "M5": _spindle = false; break;
                case "M802": _vacuum = false; break;
                default:
                    if (Regex.IsMatch(upper, @"^M3(?![0-9.])")) { _spindle = true; _spindleRpm = Number(upper, 'S') ?? 10000; }
                    else if (upper.StartsWith("M801", StringComparison.Ordinal)) _vacuum = true;
                    else if (upper.StartsWith("M220", StringComparison.Ordinal) || upper.StartsWith("$F", StringComparison.Ordinal)) _feedOverride = (int)(Number(upper, 'S') ?? 100);
                    else if (upper.StartsWith("M223", StringComparison.Ordinal) || upper.StartsWith("$O", StringComparison.Ordinal)) _spindleOverride = (int)(Number(upper, 'S') ?? 100);
                    else if (MoveRegex().IsMatch(upper)) Move(upper);
                    else if (upper.StartsWith("M6T", StringComparison.Ordinal) && int.TryParse(upper[3..], out var t)) _tool = t;
                    else if (!upper.StartsWith('M') && !upper.StartsWith('G')) { Reply($"error: unknown command '{line}' (simulator)"); return; }
                    break;
            }
            Reply("ok");
        }
    }

    private static double? Number(string line, char letter)
    {
        var m = Regex.Match(line, $@"{letter}\s*([-+]?\d*\.?\d+)");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private void Jog(string line)
    {
        for (var i = 0; i < 4; i++)
            if (Number(line[2..], "XYZA"[i]) is { } delta) Enqueue(i, Current(i) + delta);
    }

    private void Move(string line)
    {
        var machineCoordinates = line.Contains("G53", StringComparison.Ordinal);
        for (var i = 0; i < 4; i++)
        {
            if (Number(line.Replace("G53", "").Replace("G90", "").Replace("G0", " "), "XYZA"[i]) is not { } value) continue;
            Enqueue(i, machineCoordinates ? value : value + _wcs[_activeWcs][i]);
        }
    }

    private double Current(int axis) => _pendingMoves.Where(m => m.Axis == axis).Select(m => m.Target).DefaultIfEmpty(_machine[axis]).Last();

    private void Enqueue(int axis, double target)
    {
        _pendingMoves.Enqueue((axis, target));
        if (!_held) _state = "Run";
    }

    private void SetWork(string args)
    {
        for (var i = 0; i < 4; i++)
            if (Number(args, "XYZA"[i]) is { } value) _wcs[_activeWcs][i] = _machine[i] - value;
    }

    private void Step()
    {
        lock (_gate)
        {
            if (_held || _pendingMoves.Count == 0) return;
            var (axis, target) = _pendingMoves.Peek();
            var speed = 60.0 * _feedOverride / 100.0; // mm per tick
            var delta = target - _machine[axis];
            if (Math.Abs(delta) <= speed) { _machine[axis] = target; _pendingMoves.Dequeue(); }
            else _machine[axis] += Math.Sign(delta) * speed;
            if (_pendingMoves.Count == 0) _state = "Idle";
        }
    }

    private string StatusReport()
    {
        lock (_gate)
        {
            string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
            var w = _wcs[_activeWcs];
            var moving = _state == "Run";
            return $"<{_state}|MPos:{F(_machine[0])},{F(_machine[1])},{F(_machine[2])},{F(_machine[3])}" +
                   $"|WPos:{F(_machine[0] - w[0])},{F(_machine[1] - w[1])},{F(_machine[2] - w[2])},{F(_machine[3] - w[3])}" +
                   $"|R:0.0|G:{_activeWcs}|F:{(moving ? 3000 * _feedOverride / 100 : 0)},3000,{_feedOverride}" +
                   $"|S:{(_spindle ? _spindleRpm * _spindleOverride / 100 : 0)},{(_spindle ? _spindleRpm : 0)},{_spindleOverride},{(_vacuum ? 1 : 0)},32.5" +
                   $"|T:{_tool},-12.345|W:3.95|L:0,0,0,0,100>";
        }
    }

    private string DiagnoseReport()
    {
        static int B(bool v) => v ? 1 : 0;
        return $"{{S:{B(_spindle)},{(int)_spindleRpm}|L:0,0|F:{B(_spindle)},0|V:{B(_vacuum)},0|G:{B(_light)}|T:{B(_toolSensor)}|R:{B(_air)}|C:{B(_wpCharge)}" +
               $"|E:0,0,0,0,0,0|P:0,0|A:1,0|I:0}}";
    }

    [GeneratedRegex(@"^(G53\s*)?(G90\s*)?G0?[01](?![0-9])")]
    private static partial Regex MoveRegex();
}
