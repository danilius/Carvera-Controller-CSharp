using System.Security.Cryptography;
using System.Text;
using Carvera.Core.Transfer;

namespace Carvera.Core.Simulation;

/// <summary>The simulator's side of an upload: an XMODEM receiver that checks CRCs and the MD5 like the machine does.</summary>
public sealed partial class SimulatedMachine
{
    private readonly Dictionary<string, byte[]> _uploads = new(StringComparer.Ordinal);

    // The simulated SD card: every file (uploaded or built in) and every folder, by full path.
    private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new(StringComparer.Ordinal)
    {
        ["/sd/gcodes/demo part.nc"] = (System.Text.Encoding.ASCII.GetBytes("G21 G90\nG1 X10 Y10 F800\n"), new DateTime(2026, 9, 1, 10, 30, 0)),
        ["/sd/config.txt"] = (System.Text.Encoding.ASCII.GetBytes("# Simulated machine configuration\nswitch.vacuum.default_on_value       80\nswitch.light.startup_state           true\nlight.turn_off_min                   10   # idle minutes\nstop_on_cover_open                   false\nmain_button_long_press_enable        Sleep\n"), new DateTime(2026, 7, 1, 8, 0, 0)),
        ["/sd/gcodes/notes.txt"] = (System.Text.Encoding.ASCII.GetBytes("hello"), new DateTime(2026, 8, 15, 9, 0, 0)),
    };
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { "/sd", "/sd/gcodes", "/sd/gcodes/old jobs" };

    /// <summary>Everything on the simulated SD card, by path (files that came with the simulator and uploaded ones).</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get { lock (_gate) return _files.ToDictionary(f => f.Key, f => f.Value.Data); } }
    public IReadOnlyCollection<string> Folders { get { lock (_gate) return [.. _folders]; } }

    /// <summary>Puts a file on the simulated SD card exactly as given (for tests: for example data already in .lz format).</summary>
    public void PutFile(string path, byte[] data)
    {
        lock (_gate) _files[path] = (data, DateTime.Now);
    }

    private static string ParentOf(string path) => path.LastIndexOf('/') is var cut and > 0 ? path[..cut] : "/";

    /// <summary>Handles ls, rm, mkdir and mv the way the machine answers them. Returns false for other commands.</summary>
    private bool TryFileCommand(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var arguments = words.Skip(1).Where(w => !w.StartsWith('-')).Select(w => w.Replace('\x01', ' ')).ToList();
        void Done() => Output([0x04]);
        void Fail() => Output([0x16]);
        switch (words[0].ToLowerInvariant())
        {
            case "ls":
                if (arguments.Count != 1 || !_folders.Contains(arguments[0].TrimEnd('/'))) { Fail(); return true; }
                var dir = arguments[0].TrimEnd('/');
                foreach (var folder in _folders.Where(f => f != dir && ParentOf(f) == dir).Order())
                    Reply($"{folder[(dir.Length + 1)..].Replace(' ', '\x01')}/ 0 {new DateTime(2026, 9, 1).ToString("yyyyMMddHHmmss")}");
                foreach (var (path, file) in _files.Where(f => ParentOf(f.Key) == dir).OrderBy(f => f.Key))
                    Reply($"{path[(dir.Length + 1)..].Replace(' ', '\x01')} {file.Data.Length} {file.Modified:yyyyMMddHHmmss}");
                Reply(".hidden 5 20260101000000");
                Done();
                return true;
            case "rm":
                if (arguments.Count != 1) { Fail(); return true; }
                if (_files.Remove(arguments[0])) { _uploads.Remove(arguments[0]); Done(); }
                else if (_folders.Contains(arguments[0]) && !_files.Keys.Any(f => ParentOf(f) == arguments[0]) && !_folders.Any(f => f != arguments[0] && ParentOf(f) == arguments[0]) && arguments[0] != "/sd")
                { _folders.Remove(arguments[0]); Done(); }
                else Fail();
                return true;
            case "config-set":
                // config-set sd <key> <value>: rewrites the line for the key in config.txt, or appends one
                if (words.Length < 4 || words[1] != "sd") { Fail(); return true; }
                var configText = Encoding.ASCII.GetString(_files["/sd/config.txt"].Data);
                var configLines = configText.Split('\n').ToList();
                var newLine = $"{words[2]}  {string.Join(' ', words.Skip(3))}";
                var at = configLines.FindIndex(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries) is [var k, ..] && k == words[2]);
                if (at >= 0) configLines[at] = newLine; else configLines.Insert(Math.Max(0, configLines.Count - 1), newLine);
                _files["/sd/config.txt"] = (Encoding.ASCII.GetBytes(string.Join('\n', configLines)), DateTime.Now);
                Reply($"{words[2]} has been set to {string.Join(' ', words.Skip(3))}");
                return true;
            case "config-restore":
            case "config-default":
                Reply(words[0].ToLowerInvariant() == "config-restore" ? "Restored default configuration" : "Saved current configuration as default");
                return true;
            case "mkdir":
                if (arguments.Count != 1 || _folders.Contains(arguments[0]) || _files.ContainsKey(arguments[0]) || !_folders.Contains(ParentOf(arguments[0]))) Fail();
                else { _folders.Add(arguments[0]); Done(); }
                return true;
            case "mv":
                if (arguments.Count != 2 || _files.ContainsKey(arguments[1]) || _folders.Contains(arguments[1]) || !_folders.Contains(ParentOf(arguments[1]))) { Fail(); return true; }
                if (_files.Remove(arguments[0], out var moved)) { _files[arguments[1]] = moved; if (_uploads.Remove(arguments[0], out var data)) _uploads[arguments[1]] = data; Done(); }
                else if (_folders.Contains(arguments[0]) && arguments[0] != "/sd")
                {
                    var from = arguments[0];
                    var to = arguments[1];
                    foreach (var folder in _folders.Where(f => f == from || f.StartsWith(from + "/")).ToList()) { _folders.Remove(folder); _folders.Add(to + folder[from.Length..]); }
                    foreach (var file in _files.Keys.Where(f => f.StartsWith(from + "/")).ToList()) { _files[to + file[from.Length..]] = _files[file]; _files.Remove(file); }
                    Done();
                }
                else Fail();
                return true;
            default:
                return false;
        }
    }
    private UploadReceiver? _upload;
    private const byte XmodemCancel = XmodemSender.Can;

    /// <summary>The value the simulator reports for "ftype": which upload file types it accepts.</summary>
    public string FileType { get; set; } = "lz";

    /// <summary>Files received by upload, keyed by their path on the machine (after unpacking any .lz).</summary>
    public IReadOnlyDictionary<string, byte[]> UploadedFiles { get { lock (_gate) return new Dictionary<string, byte[]>(_uploads); } }

    private void StartUpload(string escapedPath)
    {
        var path = escapedPath.Replace('\x01', ' ').Trim();
        _upload = new UploadReceiver(path);
        Output([XmodemSender.CrcRequest]);
    }

    private void UploadByte(byte b)
    {
        var receiver = _upload!;
        var reply = receiver.Feed(b);
        if (reply.Length > 0) Output(reply);
        if (!receiver.Finished) return;
        _upload = null;
        if (receiver.Failure is { } failure) { Reply($"error: {failure}"); return; }
        var data = receiver.Data;
        var path = receiver.Path;
        if (path.EndsWith(".lz", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
            try
            {
                var blocks = 0;
                for (var position = 0; position + 2 < data.Length;)
                {
                    position += 4 + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
                    blocks++;
                }
                data = LzFile.Read(data);
                // The real machine unpacks after the transfer has ended, reporting as it goes.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(400);
                    for (var i = 1; i <= blocks; i++)
                    {
                        Reply($"decompart = {i}");
                        await Task.Delay(5);
                    }
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
            {
                Reply($"error: could not unpack {path}: {ex.Message}");
                return;
            }
        }
        if (Convert.ToHexStringLower(MD5.HashData(data)) != receiver.Md5)
        {
            Reply($"error: md5 mismatch for {path}");
            return;
        }
        _uploads[path] = data;
        _files[path] = (data, DateTime.Now);
        // An upload creates the folders on its way, like the machine's SD card tools do not; the browser needs them to exist.
        for (var folder = ParentOf(path); folder.Length > 1 && _folders.Add(folder); folder = ParentOf(folder)) { }
        Reply("ok");
    }

    private void Output(byte[] bytes) => _output.Writer.TryWrite(bytes);

    private sealed class UploadReceiver(string path)
    {
        private enum Stage { Header, Sequence, Complement, Body, Crc }

        private Stage _stage = Stage.Header;
        private int _bodySize;
        private byte _sequence, _expected;
        private bool _md5Seen;
        private readonly List<byte> _body = [];
        private int _crcBytes;
        private ushort _crc;
        private readonly MemoryStream _data = new();
        public string Path { get; } = path;
        public string Md5 { get; private set; } = "";
        public bool Finished { get; private set; }
        public string? Failure { get; private set; }
        public byte[] Data => _data.ToArray();

        private static byte[] Ack => [XmodemSender.Ack];
        private static byte[] Nak => [XmodemSender.Nak];

        public byte[] Feed(byte b)
        {
            switch (_stage)
            {
                case Stage.Header:
                    if (b == XmodemSender.Stx) { _bodySize = 2 + XmodemSender.PacketSize; _stage = Stage.Sequence; }
                    else if (b == XmodemSender.Soh) { _bodySize = 1 + 128; _stage = Stage.Sequence; }
                    else if (b == XmodemSender.Eot) { Finished = true; return Ack; }
                    else if (b == XmodemSender.Can) { Finished = true; Failure = "upload cancelled"; }
                    return [];
                case Stage.Sequence: _sequence = b; _stage = Stage.Complement; return [];
                case Stage.Complement:
                    _stage = (byte)(_sequence + b) == 0xFF ? Stage.Body : Stage.Header;
                    _body.Clear();
                    return _stage == Stage.Header ? Nak : [];
                case Stage.Body:
                    _body.Add(b);
                    if (_body.Count == _bodySize) { _stage = Stage.Crc; _crcBytes = 0; _crc = 0; }
                    return [];
                default:
                    _crc = (ushort)((_crc << 8) | b);
                    if (++_crcBytes < 2) return [];
                    _stage = Stage.Header;
                    var body = _body.ToArray();
                    if (XmodemSender.Crc16(body) != _crc) return Nak;
                    if (_sequence == (byte)(_expected - 1)) return Ack; // a retransmission of a packet already taken
                    if (_sequence != _expected) return Nak;
                    var length = _bodySize == 2 + XmodemSender.PacketSize ? (body[0] << 8) | body[1] : body[0];
                    var payload = body.AsSpan(_bodySize == 2 + XmodemSender.PacketSize ? 2 : 1, length);
                    // The first packet carries the MD5. Sequence numbers wrap after 255, so packet 0 is not always the first.
                    if (!_md5Seen)
                    {
                        _md5Seen = true;
                        Md5 = Encoding.ASCII.GetString(payload);
                    }
                    else _data.Write(payload);
                    _expected++;
                    return Ack;
            }
        }
    }
}
