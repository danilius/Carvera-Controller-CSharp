using System.Text;
using System.Text.Json;

namespace Carvera.Editor.Model;

/// <summary>Where each JSON path sits in the text (character offsets): for the code view's squiggles and for jumping between a path and its text.</summary>
public sealed class PathIndex
{
    public readonly record struct Span(int Start, int End)
    {
        public int Length => End - Start;
        public bool Contains(int offset) => offset >= Start && offset <= End;
    }

    private readonly Dictionary<string, Span> _spans = new(StringComparer.Ordinal);
    private readonly List<(string Path, Span Span)> _ordered = [];

    /// <summary>An index of what could be read; text that is not valid JSON gives an index of the part before the problem.</summary>
    public static PathIndex Build(string text)
    {
        var index = new PathIndex();
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var offsets = new OffsetConverter(bytes);
        var stack = new List<Frame>();
        try
        {
            while (reader.Read())
            {
                var start = (int)reader.TokenStartIndex;
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        stack[^1].Key = reader.GetString();
                        stack[^1].KeyStart = offsets.ToChars(start);
                        continue;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    {
                        var frame = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        index.Add(frame.Path, frame.KeyOrValueStart, offsets.ToChars((int)reader.TokenStartIndex + 1));
                        continue;
                    }
                }

                var parent = stack.Count > 0 ? stack[^1] : null;
                string path;
                int keyOrValueStart;
                if (parent is null) { path = ""; keyOrValueStart = offsets.ToChars(start); }
                else if (parent.IsArray) { path = parent.Path + "[" + parent.Count++ + "]"; keyOrValueStart = offsets.ToChars(start); }
                else { path = parent.Path.Length == 0 ? parent.Key! : parent.Path + "." + parent.Key; keyOrValueStart = parent.KeyStart; }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    stack.Add(new Frame { Path = path, IsArray = reader.TokenType == JsonTokenType.StartArray, KeyOrValueStart = keyOrValueStart });
                else
                    index.Add(path, keyOrValueStart, offsets.ToChars((int)reader.BytesConsumed));
            }
        }
        catch (JsonException) { /* keep what was indexed before the error */ }
        return index;
    }

    private void Add(string path, int start, int end)
    {
        var span = new Span(start, end);
        _spans[path] = span;
        _ordered.Add((path, span));
    }

    public bool TryGet(string path, out Span span) => _spans.TryGetValue(path, out span);

    /// <summary>The span for the path, or for the nearest enclosing path that exists.</summary>
    public Span? Nearest(string path)
    {
        var segments = JsonPath.Parse(path);
        for (var length = segments.Count; length >= 1; length--)
            if (_spans.TryGetValue(JsonPath.Format(segments.Take(length)), out var span)) return span;
        return null;
    }

    /// <summary>The innermost element (see <see cref="JsonPath.IsElementPath(string)"/>) whose text contains the offset.</summary>
    public string? ElementAt(int offset)
    {
        string? best = null;
        var bestLength = int.MaxValue;
        foreach (var (path, span) in _ordered)
        {
            if (!span.Contains(offset) || span.Length >= bestLength || !JsonPath.IsElementPath(path)) continue;
            best = path;
            bestLength = span.Length;
        }
        return best;
    }

    /// <summary>The innermost path of any kind whose text contains the offset.</summary>
    public string? PathAt(int offset)
    {
        string? best = null;
        var bestLength = int.MaxValue;
        foreach (var (path, span) in _ordered)
            if (span.Contains(offset) && span.Length < bestLength && path.Length > 0) { best = path; bestLength = span.Length; }
        return best;
    }

    private sealed class Frame
    {
        public string Path = "";
        public bool IsArray;
        public string? Key;
        public int KeyStart;
        public int KeyOrValueStart;
        public int Count;
    }

    /// <summary>Converts UTF-8 byte offsets to character offsets; offsets arrive in increasing order, so it counts incrementally.</summary>
    private sealed class OffsetConverter(byte[] bytes)
    {
        private int _byte, _char;

        public int ToChars(int byteOffset)
        {
            if (byteOffset < _byte) { _byte = 0; _char = 0; }
            if (byteOffset > _byte)
            {
                _char += Encoding.UTF8.GetCharCount(bytes, _byte, byteOffset - _byte);
                _byte = byteOffset;
            }
            return _char;
        }
    }
}
