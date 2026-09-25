using System.Globalization;
using System.Text;

namespace Carvera.Core.Expressions;

public sealed class ExpressionException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>
/// A small expression language used by layouts for visibility, enabled state and visual conditions,
/// e.g. <c>machine.state == 'Alarm' || !connection.connected</c> or <c>feed.override &gt; 100</c>.
/// Supports numbers, 'strings', true/false/null, dotted state paths, arithmetic (+ - * / %),
/// comparisons (== != &lt; &lt;= &gt; &gt;=) and logic (&amp;&amp; || ! and the words and/or/not).
/// String comparisons ignore case.
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    private Expression(string source, Node root, IReadOnlyCollection<string> paths)
    {
        Source = source;
        _root = root;
        Paths = paths;
    }

    public string Source { get; }
    /// <summary>The state paths this expression reads.</summary>
    public IReadOnlyCollection<string> Paths { get; }

    public static Expression Parse(string source)
    {
        var parser = new Parser(source);
        var root = parser.ParseAll();
        return new Expression(source, root, parser.Paths);
    }

    public static bool TryParse(string source, out Expression? expression, out string? error)
    {
        try
        {
            expression = Parse(source);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            expression = null;
            error = $"{ex.Message} (at character {ex.Position + 1})";
            return false;
        }
    }

    public object? Evaluate(Func<string, object?> resolve) => _root.Evaluate(resolve);

    public bool EvaluateBool(Func<string, object?> resolve) => State.StateStore.ToBoolean(Evaluate(resolve));

    public override string ToString() => Source;

    // ---------------------------------------------------------------- evaluation

    private abstract record Node
    {
        public abstract object? Evaluate(Func<string, object?> resolve);
    }

    private sealed record Literal(object? Value) : Node
    {
        public override object? Evaluate(Func<string, object?> resolve) => Value;
    }

    private sealed record PathRef(string Path) : Node
    {
        public override object? Evaluate(Func<string, object?> resolve) => resolve(Path);
    }

    private sealed record Unary(string Op, Node Operand) : Node
    {
        public override object? Evaluate(Func<string, object?> resolve)
        {
            var v = Operand.Evaluate(resolve);
            return Op == "!" ? !State.StateStore.ToBoolean(v) : ToNumber(v) is { } n ? -n : null;
        }
    }

    private sealed record Binary(string Op, Node Left, Node Right) : Node
    {
        public override object? Evaluate(Func<string, object?> resolve)
        {
            switch (Op)
            {
                case "&&": return State.StateStore.ToBoolean(Left.Evaluate(resolve)) && State.StateStore.ToBoolean(Right.Evaluate(resolve));
                case "||": return State.StateStore.ToBoolean(Left.Evaluate(resolve)) || State.StateStore.ToBoolean(Right.Evaluate(resolve));
            }
            var l = Left.Evaluate(resolve);
            var r = Right.Evaluate(resolve);
            switch (Op)
            {
                case "==": return AreEqual(l, r);
                case "!=": return !AreEqual(l, r);
                case "<" or "<=" or ">" or ">=":
                    var c = Compare(l, r);
                    if (c is null) return false;
                    return Op switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 };
                case "+" when l is string || r is string: return $"{Format(l)}{Format(r)}";
            }
            if (ToNumber(l) is not { } a || ToNumber(r) is not { } b) return null;
            return Op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => b == 0 ? null : a / b,
                "%" => b == 0 ? null : a % b,
                _ => null,
            };
        }
    }

    public static double? ToNumber(object? value) => value switch
    {
        null => null,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Format(object? value) => value switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Equality as used by ==: numbers compare numerically, strings ignore case.</summary>
    public static bool ValuesEqual(object? l, object? r) => AreEqual(l, r);

    private static bool AreEqual(object? l, object? r)
    {
        if (l is null || r is null) return l is null && r is null;
        if (l is bool || r is bool) return State.StateStore.ToBoolean(l) == State.StateStore.ToBoolean(r);
        if (ToNumber(l) is { } a && ToNumber(r) is { } b) return Math.Abs(a - b) < 1e-9;
        return string.Equals(Format(l), Format(r), StringComparison.OrdinalIgnoreCase);
    }

    private static int? Compare(object? l, object? r)
    {
        if (ToNumber(l) is { } a && ToNumber(r) is { } b) return a.CompareTo(b);
        if (l is null || r is null) return null;
        return string.Compare(Format(l), Format(r), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- parsing

    private sealed class Parser(string text)
    {
        private int _pos;
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Node ParseAll()
        {
            SkipSpace();
            if (_pos >= text.Length) throw new ExpressionException("Expression is empty", 0);
            var node = ParseOr();
            SkipSpace();
            if (_pos < text.Length) throw new ExpressionException($"Unexpected '{text[_pos]}'", _pos);
            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Match("||") || MatchWord("or")) left = new Binary("||", left, ParseAnd());
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseComparison();
            while (Match("&&") || MatchWord("and")) left = new Binary("&&", left, ParseComparison());
            return left;
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
                if (Match(op)) return new Binary(op, left, ParseAdditive());
            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (Match("+")) left = new Binary("+", left, ParseMultiplicative());
                else if (Match("-")) left = new Binary("-", left, ParseMultiplicative());
                else return left;
            }
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match("*")) left = new Binary("*", left, ParseUnary());
                else if (Match("/")) left = new Binary("/", left, ParseUnary());
                else if (Match("%")) left = new Binary("%", left, ParseUnary());
                else return left;
            }
        }

        private Node ParseUnary()
        {
            SkipSpace();
            if (_pos < text.Length && text[_pos] == '!' && !Peek("!=")) { _pos++; return new Unary("!", ParseUnary()); }
            if (MatchWord("not")) return new Unary("!", ParseUnary());
            if (Match("-")) return new Unary("-", ParseUnary());
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipSpace();
            if (_pos >= text.Length) throw new ExpressionException("Unexpected end of expression", _pos);
            var c = text[_pos];
            if (c == '(')
            {
                _pos++;
                var inner = ParseOr();
                if (!Match(")")) throw new ExpressionException("Missing ')'", _pos);
                return inner;
            }
            if (c is '\'' or '"') return new Literal(ReadString(c));
            if (char.IsDigit(c) || c == '.') return new Literal(ReadNumber());
            if (char.IsLetter(c) || c == '_')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '.')) _pos++;
                var word = text[start.._pos];
                if (word.EndsWith('.')) throw new ExpressionException($"Incomplete path '{word}'", start);
                switch (word.ToLowerInvariant())
                {
                    case "true": return new Literal(true);
                    case "false": return new Literal(false);
                    case "null": return new Literal(null);
                }
                Paths.Add(word);
                return new PathRef(word);
            }
            throw new ExpressionException($"Unexpected '{c}'", _pos);
        }

        private string ReadString(char quote)
        {
            var start = _pos++;
            var sb = new StringBuilder();
            while (_pos < text.Length && text[_pos] != quote)
            {
                if (text[_pos] == '\\' && _pos + 1 < text.Length) _pos++;
                sb.Append(text[_pos++]);
            }
            if (_pos >= text.Length) throw new ExpressionException("Unterminated string", start);
            _pos++;
            return sb.ToString();
        }

        private double ReadNumber()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.')) _pos++;
            if (!double.TryParse(text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new ExpressionException($"Invalid number '{text[start.._pos]}'", start);
            return value;
        }

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private bool Peek(string token) => string.CompareOrdinal(text, _pos, token, 0, token.Length) == 0;

        private bool Match(string token)
        {
            SkipSpace();
            if (!Peek(token)) return false;
            _pos += token.Length;
            return true;
        }

        private bool MatchWord(string word)
        {
            SkipSpace();
            if (_pos + word.Length > text.Length || string.Compare(text, _pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            var end = _pos + word.Length;
            if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '.')) return false;
            _pos = end;
            return true;
        }
    }
}
