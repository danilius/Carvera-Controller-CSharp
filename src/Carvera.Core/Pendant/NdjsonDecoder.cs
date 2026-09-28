using System.Text.Json;

namespace Carvera.Core.Pendant;

/// <summary>Incrementally decodes bounded newline-delimited JSON objects. Bad or oversized frames are dropped.</summary>
public sealed class NdjsonDecoder(int maxLineBytes = 64 * 1024)
{
    private readonly List<byte> _buffer = [];

    public void Reset() => _buffer.Clear();

    public List<JsonElement> Feed(ReadOnlySpan<byte> chunk)
    {
        foreach (var b in chunk) _buffer.Add(b);
        var messages = new List<JsonElement>();
        while (true)
        {
            var newline = _buffer.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (_buffer.Count > maxLineBytes) _buffer.Clear();
                break;
            }
            var line = _buffer.GetRange(0, newline);
            _buffer.RemoveRange(0, newline + 1);
            if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
            if (line.Count == 0 || line.Count > maxLineBytes) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.ToArray());
                if (doc.RootElement.ValueKind == JsonValueKind.Object) messages.Add(doc.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return messages;
    }
}
