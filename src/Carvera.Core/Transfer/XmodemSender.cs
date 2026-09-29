namespace Carvera.Core.Transfer;

public enum TransferOutcome { Success, Failed, Cancelled }

/// <summary>
/// The XMODEM sender used for uploads to the machine, ported from <c>XMODEM.send_legacy</c> in the Python
/// controller (8192-byte packets, CRC or checksum, the machine's own CAN byte 0x16). Differences from plain XMODEM:
/// packet 0 carries the MD5 of the original file as text, and every packet's payload is preceded by its length
/// (two bytes for 8 KiB packets). The framed Makera variant is not implemented.
/// </summary>
public static class XmodemSender
{
    public const byte Soh = 0x01, Stx = 0x02, Eot = 0x04, Ack = 0x06, Nak = 0x15, Can = 0x16, CrcRequest = (byte)'C', Pad = 0x1A;
    public const int PacketSize = 8192;

    private static readonly ushort[] CrcTable = BuildCrcTable();

    private static ushort[] BuildCrcTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            table[i] = crc;
        }
        return table;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data) crc = (ushort)((crc << 8) ^ CrcTable[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }

    public static byte Checksum(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        foreach (var b in data) sum += b;
        return (byte)sum;
    }

    /// <summary>Builds one packet: header, length-prefixed padded payload, then CRC or checksum.</summary>
    public static byte[] BuildPacket(ReadOnlySpan<byte> payload, byte sequence, bool crcMode)
    {
        var body = new byte[2 + PacketSize];
        body[0] = (byte)(payload.Length >> 8);
        body[1] = (byte)payload.Length;
        payload.CopyTo(body.AsSpan(2));
        body.AsSpan(2 + payload.Length).Fill(Pad);
        var trailer = crcMode ? 2 : 1;
        var packet = new byte[3 + body.Length + trailer];
        packet[0] = Stx;
        packet[1] = sequence;
        packet[2] = (byte)(0xFF - sequence);
        body.CopyTo(packet, 3);
        if (crcMode)
        {
            var crc = Crc16(body);
            packet[^2] = (byte)(crc >> 8);
            packet[^1] = (byte)crc;
        }
        else packet[^1] = Checksum(body);
        return packet;
    }

    /// <param name="progress">Called after each reply with (data packets acknowledged, retries so far).</param>
    public static async Task<TransferOutcome> SendAsync(Stream data, string md5, IByteLink link, Action<int, int>? progress,
        CancellationToken cancellationToken, int retry = 10, TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(5);

        // Wait for the receiver to ask for CRC (or checksum) mode.
        var errors = 0;
        var cancelSeen = false;
        bool crcMode;
        try
        {
            while (true)
            {
                var c = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                if (c == Nak) { crcMode = false; break; }
                if (c == CrcRequest) { crcMode = true; break; }
                if (c == Can)
                {
                    if (cancelSeen) return TransferOutcome.Cancelled;
                    cancelSeen = true;
                }
                else if (c == Eot) return TransferOutcome.Failed;
                if (++errors > retry)
                {
                    await AbortAsync(link).ConfigureAwait(false);
                    return TransferOutcome.Failed;
                }
            }

            errors = 0;
            var dataAcknowledged = 0;
            var isData = false;
            var retries = 0;
            byte sequence = 0;
            var md5Sent = false;
            var buffer = new byte[PacketSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> payload;
                if (!md5Sent)
                {
                    payload = System.Text.Encoding.ASCII.GetBytes(md5);
                    isData = false;
                    md5Sent = true;
                }
                else
                {
                    var count = await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    isData = true;
                    payload = buffer.AsMemory(0, count);
                }

                var packet = BuildPacket(payload.Span, sequence, crcMode);
                while (true)
                {
                    await link.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                    var reply = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                    if (reply == Ack)
                    {
                        if (isData) dataAcknowledged++;
                        progress?.Invoke(dataAcknowledged, retries);
                        errors = 0;
                        break;
                    }
                    if (reply == Can)
                    {
                        if (cancelSeen) return TransferOutcome.Failed;
                        cancelSeen = true;
                    }
                    errors++;
                    retries++;
                    progress?.Invoke(dataAcknowledged, retries);
                    if (errors > retry)
                    {
                        await AbortAsync(link).ConfigureAwait(false);
                        return TransferOutcome.Failed;
                    }
                }
                sequence++;
            }

            // End of transmission: an ACK must come back.
            errors = 0;
            while (true)
            {
                await link.WriteAsync(new[] { Eot }, cancellationToken).ConfigureAwait(false);
                if (await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false) == Ack) return TransferOutcome.Success;
                if (++errors > retry)
                {
                    await AbortAsync(link).ConfigureAwait(false);
                    return TransferOutcome.Failed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Tell the machine to stop, then swallow whatever it was still sending.
            for (var i = 0; i < 3; i++) await link.WriteAsync(new[] { Can }, CancellationToken.None).ConfigureAwait(false);
            await link.DrainAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None).ConfigureAwait(false);
            return TransferOutcome.Cancelled;
        }
    }

    private static async Task AbortAsync(IByteLink link)
    {
        for (var i = 0; i < 2; i++) await link.WriteAsync(new[] { Can }, CancellationToken.None).ConfigureAwait(false);
    }
}
