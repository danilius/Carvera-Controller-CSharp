using System.Security.Cryptography;
using System.Text;
using Carvera.Core.Transfer;

namespace Carvera.Core.Simulation;

/// <summary>The simulator's side of an upload: an XMODEM receiver that checks CRCs and the MD5 like the machine does.</summary>
public sealed partial class SimulatedMachine
{
    private readonly Dictionary<string, byte[]> _uploads = new(StringComparer.Ordinal);
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
                data = LzFile.ReadStored(data);
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
        Reply("ok");
    }

    private void Output(byte[] bytes) => _output.Writer.TryWrite(bytes);

    private sealed class UploadReceiver(string path)
    {
        private enum Stage { Header, Sequence, Complement, Body, Crc }

        private Stage _stage = Stage.Header;
        private int _bodySize;
        private byte _sequence, _expected;
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
                    if (_expected == 0) Md5 = Encoding.ASCII.GetString(payload);
                    else _data.Write(payload);
                    _expected++;
                    return Ack;
            }
        }
    }
}
