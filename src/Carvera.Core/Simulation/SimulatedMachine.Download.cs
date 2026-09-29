using System.Security.Cryptography;
using System.Text;
using Carvera.Core.Transfer;

namespace Carvera.Core.Simulation;

/// <summary>The simulator's side of a download: an XMODEM sender that offers the MD5 first, as the machine does.</summary>
public sealed partial class SimulatedMachine
{
    private DownloadSender? _download;

    /// <summary>What the simulator advertises as the MD5 of the next download; null sends the real digest. Tests use it to make the check fail.</summary>
    public string? AdvertisedMd5Override { get; set; }

    private void StartDownload(string escapedPath)
    {
        var path = escapedPath.Replace('\x01', ' ').Trim();
        if (!_files.TryGetValue(path, out var file))
        {
            Reply($"error: file {path} not found");
            return;
        }
        var digest = AdvertisedMd5Override ?? Convert.ToHexStringLower(MD5.HashData(file.Data));
        _download = new DownloadSender(file.Data, digest);
    }

    private void DownloadByte(byte b)
    {
        var sender = _download!;
        var reply = sender.Feed(b);
        if (reply.Length > 0) Output(reply);
        if (sender.Finished) _download = null;
    }

    private sealed class DownloadSender(byte[] data, string md5)
    {
        private bool _started, _crc;
        private int _index = -1;          // -1: md5 packet; then the data packets; then EOT
        private byte[] _last = [];
        private bool _eotSent;
        public bool Finished { get; private set; }

        private byte[] PacketFor(int index)
        {
            var payload = index < 0 ? Encoding.ASCII.GetBytes(md5) : data.AsSpan(index * XmodemSender.PacketSize, Math.Min(XmodemSender.PacketSize, data.Length - index * XmodemSender.PacketSize)).ToArray();
            return XmodemSender.BuildPacket(payload, (byte)(index + 1), _crc);
        }

        private int PacketCount => (data.Length + XmodemSender.PacketSize - 1) / XmodemSender.PacketSize;

        public byte[] Feed(byte b)
        {
            if (b == XmodemSender.Can) { Finished = true; return []; }
            if (!_started)
            {
                if (b is not (XmodemSender.CrcRequest or XmodemSender.Nak)) return [];
                _started = true;
                _crc = b == XmodemSender.CrcRequest;
                return _last = PacketFor(_index);
            }
            if (b == XmodemSender.Nak) return _eotSent ? [XmodemSender.Eot] : _last;
            if (b != XmodemSender.Ack) return [];
            if (_eotSent) { Finished = true; return []; }
            _index++;
            if (_index >= PacketCount)
            {
                _eotSent = true;
                return _last = [XmodemSender.Eot];
            }
            return _last = PacketFor(_index);
        }
    }
}
