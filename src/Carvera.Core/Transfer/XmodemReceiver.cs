using System.Security.Cryptography;

namespace Carvera.Core.Transfer;

public sealed record ReceiveResult(TransferOutcome Outcome, long Bytes, string? AdvertisedMd5, string ActualMd5, bool Md5Mismatch, bool LooksCompressed);

/// <summary>
/// The XMODEM receiver used for downloads from the machine, ported from <c>XMODEM.recv_legacy</c>. It asks for CRC mode
/// (falling back to checksums), takes the MD5 the machine advertises in packet 0 and the file from the packets after it,
/// and checks the MD5 at the end: a mismatch fails the download, "no usable digest" (the placeholder some firmware sends)
/// skips the check, and data that starts with two zero bytes is QuickLZ-compressed and is not checked.
/// </summary>
public static class XmodemReceiver
{
    /// <summary>Anything that is not 32 hex digits counts as no digest at all.</summary>
    public static string? NormalizeMd5(string? advertised)
    {
        var text = advertised?.Trim().ToLowerInvariant();
        return text is { Length: 32 } && text.All(Uri.IsHexDigit) ? text : null;
    }

    private static async Task<byte[]?> ReadExactlyAsync(IByteLink link, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var data = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var b = await link.ReadByteAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (b < 0) return null;
            data[i] = (byte)b;
        }
        return data;
    }

    private static async Task PurgeAsync(IByteLink link, TimeSpan timeout, CancellationToken cancellationToken)
    {
        while (await link.ReadByteAsync(timeout, cancellationToken).ConfigureAwait(false) >= 0) { }
    }

    /// <param name="progress">Called after each accepted data packet with the bytes received so far.</param>
    public static async Task<ReceiveResult> ReceiveAsync(Stream output, IByteLink link, Action<long>? progress, CancellationToken cancellationToken,
        int retry = 16, TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(1);
        var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var firstBytes = new List<byte>(2);
        long received = 0;
        string? advertised = null;
        ReceiveResult Result(TransferOutcome outcome, bool mismatch = false) =>
            new(outcome, received, advertised, "", mismatch, firstBytes.Count >= 2 && firstBytes[0] == 0 && firstBytes[1] == 0);
        try
        {
            // Start: ask for CRC mode, then checksums, until the sender's first header arrives.
            var errors = 0;
            var crcMode = true;
            var cancelSeen = false;
            int header;
            while (true)
            {
                if (errors >= retry)
                {
                    await AbortAsync(link).ConfigureAwait(false);
                    return Result(TransferOutcome.Failed);
                }
                if (crcMode && errors >= retry / 2) crcMode = false;
                await link.WriteAsync(new[] { crcMode ? XmodemSender.CrcRequest : XmodemSender.Nak }, cancellationToken).ConfigureAwait(false);
                header = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                if (header is XmodemSender.Soh or XmodemSender.Stx) break;
                if (header == XmodemSender.Can)
                {
                    if (cancelSeen) return Result(TransferOutcome.Failed);
                    cancelSeen = true;
                }
                else errors++;
            }

            byte sequence = 0;
            var retransmissions = retry + 1;
            var md5Seen = false;
            errors = 0;
            cancelSeen = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Find the start of the next packet.
                while (header is not (XmodemSender.Soh or XmodemSender.Stx))
                {
                    if (header == XmodemSender.Eot)
                    {
                        await link.WriteAsync(new[] { XmodemSender.Ack }, cancellationToken).ConfigureAwait(false);
                        return Finish(output, md5, advertised, received, firstBytes);
                    }
                    if (header == XmodemSender.Can)
                    {
                        if (cancelSeen) return Result(TransferOutcome.Failed);
                        cancelSeen = true;
                        header = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (++errors > retry)
                    {
                        await AbortAsync(link).ConfigureAwait(false);
                        return Result(TransferOutcome.Failed);
                    }
                    if (header >= 0)
                    {
                        await PurgeAsync(link, wait, cancellationToken).ConfigureAwait(false);
                        await link.WriteAsync(new[] { XmodemSender.Nak }, cancellationToken).ConfigureAwait(false);
                    }
                    header = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                }
                errors = 0;
                cancelSeen = false;

                var size = header == XmodemSender.Stx ? XmodemSender.PacketSize : 128;
                var prefix = header == XmodemSender.Stx ? 2 : 1;
                var trailer = crcMode ? 2 : 1;
                var seq1 = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                var seq2 = seq1 < 0 ? -1 : await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                var body = seq2 < 0 ? null : await ReadExactlyAsync(link, prefix + size + trailer, wait, cancellationToken).ConfigureAwait(false);
                var valid = body is not null && seq1 >= 0 && seq2 >= 0 && (byte)(seq1 + seq2) == 0xFF && Verify(body, prefix + size, crcMode);

                if (valid && seq1 == sequence)
                {
                    retransmissions = retry + 1;
                    var length = prefix == 2 ? (body![0] << 8) | body[1] : body![0];
                    if (length > size) valid = false;
                    else
                    {
                        var payload = body.AsMemory(prefix, length);
                        if (!md5Seen)
                        {
                            md5Seen = true;
                            advertised = System.Text.Encoding.ASCII.GetString(payload.Span).Trim();
                        }
                        else
                        {
                            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                            md5.AppendData(payload.Span);
                            received += length;
                            for (var i = 0; i < length && firstBytes.Count < 2; i++) firstBytes.Add(payload.Span[i]);
                            progress?.Invoke(received);
                        }
                        await link.WriteAsync(new[] { XmodemSender.Ack }, cancellationToken).ConfigureAwait(false);
                        sequence++;
                        header = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                if (valid && seq1 == (byte)(sequence - 1))
                {
                    // The sender missed our ACK and repeated the packet: acknowledge it again, keep only one copy.
                    await link.WriteAsync(new[] { XmodemSender.Ack }, cancellationToken).ConfigureAwait(false);
                    header = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Something is wrong with the packet: drain what is still coming and ask for it again.
                await PurgeAsync(link, wait, cancellationToken).ConfigureAwait(false);
                if (--retransmissions <= 0)
                {
                    await AbortAsync(link).ConfigureAwait(false);
                    return Result(TransferOutcome.Failed);
                }
                await link.WriteAsync(new[] { XmodemSender.Nak }, cancellationToken).ConfigureAwait(false);
                header = await link.ReadByteAsync(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            for (var i = 0; i < 3; i++) await link.WriteAsync(new[] { XmodemSender.Can }, CancellationToken.None).ConfigureAwait(false);
            await link.DrainAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None).ConfigureAwait(false);
            return Result(TransferOutcome.Cancelled);
        }
    }

    private static bool Verify(byte[] body, int dataLength, bool crcMode)
    {
        var data = body.AsSpan(0, dataLength);
        if (crcMode) return XmodemSender.Crc16(data) == (ushort)((body[dataLength] << 8) | body[dataLength + 1]);
        return XmodemSender.Checksum(data) == body[dataLength];
    }

    private static ReceiveResult Finish(Stream output, IncrementalHash md5, string? advertised, long received, List<byte> firstBytes)
    {
        output.Flush();
        var actual = Convert.ToHexStringLower(md5.GetHashAndReset());
        var compressed = firstBytes.Count >= 2 && firstBytes[0] == 0 && firstBytes[1] == 0;
        var digest = NormalizeMd5(advertised);
        var mismatch = digest is not null && !compressed && digest != actual;
        return new ReceiveResult(mismatch ? TransferOutcome.Failed : TransferOutcome.Success, received, digest, actual, mismatch, compressed);
    }

    private static async Task AbortAsync(IByteLink link)
    {
        for (var i = 0; i < 2; i++) await link.WriteAsync(new[] { XmodemSender.Can }, CancellationToken.None).ConfigureAwait(false);
    }
}
