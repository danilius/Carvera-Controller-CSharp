using System.Buffers.Binary;

namespace Carvera.Core.Transfer;

/// <summary>
/// Writes the <c>.lz</c> upload format the machine decompresses after receiving a file: 4096-byte blocks, each
/// preceded by its big-endian length and wrapped as a QuickLZ (level 1) block, and a big-endian 16-bit sum of all
/// bytes at the end (the layout <c>compress_file</c> writes in the Python controller).
/// <para>
/// The blocks are stored, not compressed: QuickLZ decompressors copy blocks whose "compressed" flag is clear, so
/// the machine reads them like any other, but the upload is as long as the original. A real compressor can replace
/// <see cref="StoredBlock"/> later without touching the layout.
/// </para>
/// </summary>
public static class LzFile
{
    public const int BlockSize = 4096;

    /// <summary>QuickLZ block header flags for level 1 with no streaming buffer: level bits, the always-set bit 6, and the long-header bit.</summary>
    private const byte ShortStored = 0x44, LongStored = 0x46;

    /// <summary>Wraps <paramref name="data"/> (at most 4096 bytes) as a QuickLZ block that is stored uncompressed.</summary>
    public static byte[] StoredBlock(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) throw new ArgumentException("A QuickLZ block needs at least one byte.", nameof(data));
        if (data.Length < 216)
        {
            var block = new byte[3 + data.Length];
            block[0] = ShortStored;
            block[1] = (byte)(block.Length & 0xFF);
            block[2] = (byte)data.Length;
            data.CopyTo(block.AsSpan(3));
            return block;
        }
        var longBlock = new byte[9 + data.Length];
        longBlock[0] = LongStored;
        BinaryPrimitives.WriteUInt32LittleEndian(longBlock.AsSpan(1), (uint)longBlock.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(longBlock.AsSpan(5), (uint)data.Length);
        data.CopyTo(longBlock.AsSpan(9));
        return longBlock;
    }

    /// <summary>Converts <paramref name="input"/> to the upload format and returns how many blocks it holds.</summary>
    public static async Task<int> WriteAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[BlockSize];
        uint sum = 0;
        var blocks = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            // A short read is not the end of the file: fill the block so block sizes stay uniform.
            while (count < BlockSize)
            {
                var more = await input.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (more == 0) break;
                count += more;
            }
            for (var i = 0; i < count; i++) sum += buffer[i];
            var block = StoredBlock(buffer.AsSpan(0, count));
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)block.Length);
            await output.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(block, cancellationToken).ConfigureAwait(false);
            blocks++;
        }
        var trailer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(trailer, (ushort)(sum & 0xFFFF));
        await output.WriteAsync(trailer, cancellationToken).ConfigureAwait(false);
        return blocks;
    }

    /// <summary>Reads a file in the upload format back (stored blocks only). Used to test the writer and by the simulator.</summary>
    public static byte[] ReadStored(ReadOnlySpan<byte> file)
    {
        var result = new List<byte>();
        uint sum = 0;
        var position = 0;
        while (file.Length - position > 2)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file[position..]);
            position += 4;
            var block = file.Slice(position, length);
            position += length;
            var flags = block[0];
            if ((flags & 1) != 0) throw new NotSupportedException("Compressed QuickLZ blocks are not supported.");
            var header = (flags & 2) != 0 ? 9 : 3;
            foreach (var b in block[header..]) { result.Add(b); sum += b; }
        }
        if (file.Length - position != 2 || BinaryPrimitives.ReadUInt16BigEndian(file[position..]) != (ushort)(sum & 0xFFFF))
            throw new InvalidDataException("The .lz trailer does not match the data.");
        return [.. result];
    }
}
