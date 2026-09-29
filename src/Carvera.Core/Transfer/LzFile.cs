using System.Buffers.Binary;

namespace Carvera.Core.Transfer;

/// <summary>
/// Reads and writes the <c>.lz</c> format the machine uses for compressed files: 4096-byte blocks, each preceded by
/// its big-endian length and wrapped as a QuickLZ block (<see cref="QuickLz"/>), and a big-endian 16-bit sum of all
/// bytes at the end (the layout <c>compress_file</c> writes in the Python controller).
/// </summary>
public static class LzFile
{
    public const int BlockSize = 4096;

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
            var block = QuickLz.Compress(buffer.AsSpan(0, count));
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

    /// <summary>Reads a file in the .lz format back, unpacking each block and checking the trailer sum.</summary>
    public static byte[] Read(ReadOnlySpan<byte> file)
    {
        var result = new List<byte>();
        uint sum = 0;
        var position = 0;
        while (file.Length - position > 2)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file[position..]);
            position += 4;
            if (length < 0 || length > file.Length - position) throw new InvalidDataException("A .lz block is longer than the file.");
            var block = file.Slice(position, length);
            position += length;
            foreach (var b in QuickLz.Decompress(block)) { result.Add(b); sum += b; }
        }
        if (file.Length - position != 2 || BinaryPrimitives.ReadUInt16BigEndian(file[position..]) != (ushort)(sum & 0xFFFF))
            throw new InvalidDataException("The .lz trailer does not match the data.");
        return [.. result];
    }
}
