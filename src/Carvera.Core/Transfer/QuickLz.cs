using System.Buffers.Binary;

namespace Carvera.Core.Transfer;

/// <summary>
/// QuickLZ 1.4.1 at compression level 3, the level <c>pyquicklz</c> (the Python controller's dependency) is built at.
/// Level 3 is stateless to decode: every match carries its own offset. A block is a header (flags, compressed size,
/// decompressed size), then groups of up to 31 items that each start with a 32-bit control word whose bits say
/// "literal" (0) or "match" (1) from the low bit up, with a marker bit above the last item.
/// </summary>
public static class QuickLz
{
    private const int HashValues = 4096, Pointers = 16, MinOffset = 2, UncompressedEnd = 4, UnconditionalMatchLength = 6;
    private const byte Level3 = 0x0C, AlwaysSet = 0x40;

    /// <summary>Size of the block header for a source of <paramref name="length"/> bytes (the reference switches at 216).</summary>
    private static int HeaderSize(int length) => length >= 216 ? 9 : 3;

    /// <summary>Wraps <paramref name="data"/> as an uncompressed (stored) block.</summary>
    public static byte[] Store(ReadOnlySpan<byte> data)
    {
        var header = HeaderSize(data.Length);
        var block = new byte[header + data.Length];
        WriteHeader(block, header, compressed: false, block.Length, data.Length);
        data.CopyTo(block.AsSpan(header));
        return block;
    }

    private static void WriteHeader(byte[] block, int header, bool compressed, int compressedSize, int size)
    {
        block[0] = (byte)(AlwaysSet | Level3 | (compressed ? 1 : 0) | (header == 9 ? 2 : 0));
        if (header == 9)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(1), (uint)compressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(5), (uint)size);
        }
        else
        {
            block[1] = (byte)compressedSize;
            block[2] = (byte)size;
        }
    }

    /// <summary>Compresses <paramref name="data"/> into one block, or stores it when compressing would not make it smaller.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) throw new ArgumentException("A QuickLZ block needs at least one byte.", nameof(data));
        var header = HeaderSize(data.Length);
        // Worst case: every byte a literal plus one control word per 31 items.
        var output = new byte[header + data.Length + data.Length / 31 * 4 + 16];
        var written = CompressInto(data.ToArray(), output, header);
        if (written >= data.Length + header) return Store(data);
        var block = output.AsSpan(0, written).ToArray();
        WriteHeader(block, header, compressed: true, written, data.Length);
        return block;
    }

    private static int CompressInto(byte[] source, byte[] dest, int header)
    {
        var size = source.Length;
        var table = new int[HashValues * Pointers];
        var counters = new int[HashValues];
        var destination = header;
        var controlPosition = -1;
        uint control = 0;
        var items = 0;

        void StartControl()
        {
            controlPosition = destination;
            destination += 4;
            control = 0;
            items = 0;
        }
        void Item(bool match)
        {
            if (controlPosition < 0 || items == 31)
            {
                if (controlPosition >= 0) BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(controlPosition), control | (1u << 31));
                StartControl();
            }
            if (match) control |= 1u << items;
            items++;
        }

        int Hash(int at) => (int)(((Fetch3(source, at) >> 12) ^ Fetch3(source, at)) & (HashValues - 1));
        void Insert(int at)
        {
            var hash = Hash(at);
            table[hash * Pointers + (counters[hash]++ & (Pointers - 1))] = at;
        }

        var lastMatchStart = size - 1 - UnconditionalMatchLength - UncompressedEnd;
        var src = 0;
        while (src <= lastMatchStart)
        {
            var hash = Hash(src);
            var count = counters[hash];
            var matchLength = 0;
            var matchAt = 0;
            var remaining = Math.Min(size - UncompressedEnd - src, 255);
            var fetch = Fetch3(source, src);
            for (var k = 0; k < Pointers && count > k; k++)
            {
                var o = table[hash * Pointers + k];
                if (o >= src - MinOffset || Fetch3(source, o) != fetch) continue;
                var m = 3;
                while (m < remaining && source[o + m] == source[src + m]) m++;
                if (m > matchLength || (m == matchLength && o > matchAt)) { matchLength = m; matchAt = o; }
            }
            Insert(src);
            if (matchLength >= 3 && src - matchAt < 131071)
            {
                var offset = src - matchAt;
                for (var u = 1; u < matchLength; u++) Insert(src + u);
                src += matchLength;
                Item(true);
                if (matchLength == 3 && offset <= 63) dest[destination++] = (byte)(offset << 2);
                else if (matchLength == 3 && offset <= 16383)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(destination), (ushort)((offset << 2) | 1));
                    destination += 2;
                }
                else if (matchLength <= 18 && offset <= 1023)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(destination), (ushort)(((matchLength - 3) << 2) | (offset << 6) | 2));
                    destination += 2;
                }
                else if (matchLength <= 33)
                {
                    var value = ((matchLength - 2) << 2) | (offset << 7) | 3;
                    dest[destination++] = (byte)value;
                    dest[destination++] = (byte)(value >> 8);
                    dest[destination++] = (byte)(value >> 16);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(destination), (uint)(((matchLength - 3) << 7) | (offset << 15) | 3));
                    destination += 4;
                }
            }
            else
            {
                Item(false);
                dest[destination++] = source[src++];
            }
        }
        for (; src < size; src++)
        {
            Item(false);
            dest[destination++] = source[src];
        }
        BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(controlPosition), control | (1u << items));
        return destination;
    }

    private static uint Fetch3(byte[] data, int at) => (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16));

    /// <summary>Unpacks one block (compressed or stored) and returns its data. Only level 3 is understood.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> block)
    {
        if (block.Length < 3) throw new InvalidDataException("A QuickLZ block is shorter than its header.");
        var flags = block[0];
        var header = (flags & 2) != 0 ? 9 : 3;
        if (block.Length < header) throw new InvalidDataException("A QuickLZ block is shorter than its header.");
        var size = header == 9 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(block[5..]) : block[2];
        if (size < 0 || size > 1 << 26) throw new InvalidDataException("A QuickLZ block claims an implausible size.");
        var result = new byte[size];
        if ((flags & 1) == 0)
        {
            if (block.Length - header < size) throw new InvalidDataException("A stored QuickLZ block is truncated.");
            block.Slice(header, size).CopyTo(result);
            return result;
        }
        if (((flags >> 2) & 3) != 3) throw new NotSupportedException("Only QuickLZ level 3 is supported.");

        var src = header;
        var dst = 0;
        uint control = 1;
        try
        {
            while (dst < size)
            {
                if (control == 1)
                {
                    control = BinaryPrimitives.ReadUInt32LittleEndian(block[src..]);
                    src += 4;
                }
                if ((control & 1) == 0)
                {
                    result[dst++] = block[src++];
                    control >>= 1;
                    continue;
                }
                control >>= 1;
                int offset, length;
                var b0 = block[src];
                if ((b0 & 3) == 0) { offset = b0 >> 2; length = 3; src++; }
                else if ((b0 & 2) == 0) { offset = (block[src] | (block[src + 1] << 8)) >> 2; length = 3; src += 2; }
                else if ((b0 & 1) == 0) { var f = block[src] | (block[src + 1] << 8); offset = f >> 6; length = ((f >> 2) & 15) + 3; src += 2; }
                else if ((b0 & 127) != 3)
                {
                    var f = block[src] | (block[src + 1] << 8) | (block[src + 2] << 16);
                    offset = (f >> 7) & 0x1FFFF; length = ((f >> 2) & 0x1F) + 2; src += 3;
                }
                else
                {
                    var f = BinaryPrimitives.ReadUInt32LittleEndian(block[src..]);
                    offset = (int)(f >> 15); length = (int)((f >> 7) & 255) + 3; src += 4;
                }
                if (offset < 1 || offset > dst || dst + length > size) throw new InvalidDataException("A QuickLZ match points outside the data.");
                for (var i = 0; i < length; i++, dst++) result[dst] = result[dst - offset];
            }
        }
        catch (IndexOutOfRangeException)
        {
            throw new InvalidDataException("A QuickLZ block ends before its data does.");
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("A QuickLZ block ends before its data does.");
        }
        return result;
    }
}
