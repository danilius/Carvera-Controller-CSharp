using System.Text.Json;
using System.Text.Json.Nodes;
using Carvera.Layout;

namespace Carvera.Editor.Model;

public enum ChangeOrigin { Load, Visual, Code, Undo, External }

/// <summary>
/// The layout being edited. The text is the truth: visual edits change a JSON tree that is written back as text, code edits replace the
/// text and re-read the tree. Every change is checked with the same loader and validator the Controller uses, so what the editor calls
/// valid is what the Controller will apply. Undo and redo keep snapshots of the text.
/// </summary>
public sealed class EditorDocument
{
    private static readonly LayoutValidator Validator = new();
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private PathIndex? _index;
    private string? _lastCoalesce;
    private DateTime _lastChange = DateTime.MinValue;

    private sealed record Snapshot(string Text, string Label);

    public EditorDocument(string text, string name, string? filePath = null, string? baseDirectory = null)
    {
        Name = name;
        FilePath = filePath;
        BaseDirectory = baseDirectory ?? (filePath is null ? AppContext.BaseDirectory : Path.GetDirectoryName(filePath)!);
        SavedText = text;
        Text = text;
        Evaluate();
    }

    /// <summary>Name used when the file has none.</summary>
    public string Name { get; }
    /// <summary>Where saving writes. Null until the layout has been saved somewhere.</summary>
    public string? FilePath { get; set; }
    /// <summary>The folder relative pictures are found in.</summary>
    public string BaseDirectory { get; set; }
    public string Text { get; private set; }
    /// <summary>The text last written to or read from disk; the layout is unsaved when it differs.</summary>
    public string SavedText { get; set; }
    public JsonObject? Data { get; private set; }
    public string? ParseError { get; private set; }
    /// <summary>Where the text stopped being JSON (1-based), or 0.</summary>
    public int ParseErrorLine { get; private set; }
    public int ParseErrorColumn { get; private set; }
    public LayoutLoadResult Result { get; private set; } = new(null, []);
    public int Revision { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The most recent layout that loaded without errors: what the preview shows while the current text is broken.</summary>
    public LayoutDocument? GoodDocument { get; private set; }

    public bool IsDirty => Text != SavedText;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>True when the Controller would apply this text: it parses and has no validation errors.</summary>
    public bool IsValid => ParseError is null && Result.Success;

    public IReadOnlyList<LayoutDiagnostic> Diagnostics => Result.Diagnostics;

    /// <summary>The text has comments, which the visual editor cannot keep when it rewrites the file.</summary>
    public bool HasComments { get; private set; }

    public PathIndex Index => _index ??= PathIndex.Build(Text);

    public event Action<ChangeOrigin>? Changed;

    // ------------------------------------------------------------------ changes

    /// <summary>
    /// Applies a visual edit to a copy of the layout and adopts it when the edit succeeds. <paramref name="edit"/> returns null to
    /// refuse (see <see cref="LastError"/>) or a string for the caller (typically the path to select afterwards). Edits with the same
    /// <paramref name="coalesce"/> key within a moment share one undo step, so typing in a field is one undo, not one per letter.
    /// </summary>
    public string? Mutate(string label, Func<JsonObject, string?> edit, string? coalesce = null)
    {
        LastError = null;
        if (Data is null)
        {
            LastError = "The JSON has an error; fix it in the Code tab first.";
            return null;
        }
        var copy = (JsonObject)Data.DeepClone();
        string? result;
        try { result = edit(copy); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            LastError = ex.Message;
            return null;
        }
        if (result is null)
        {
            LastError = ElementOps.LastError ?? "That edit is not possible.";
            return null;
        }
        var text = JsonFormatter.Format(copy);
        if (text == Text) return result;
        Adopt(text, label, coalesce, ChangeOrigin.Visual);
        return result;
    }

    /// <summary>Replaces the whole text (from the code view, or a reload from disk).</summary>
    public void SetText(string text, ChangeOrigin origin = ChangeOrigin.Code, string label = "Edit code")
    {
        if (text == Text) return;
        Adopt(text, label, origin == ChangeOrigin.Code ? "code" : null, origin);
    }

    private void Adopt(string text, string label, string? coalesce, ChangeOrigin origin)
    {
        var now = DateTime.UtcNow;
        var merge = coalesce is not null && coalesce == _lastCoalesce && (now - _lastChange).TotalMilliseconds < 1500 && _undo.Count > 0;
        if (!merge)
        {
            _undo.Add(new Snapshot(Text, label));
            if (_undo.Count > 200) _undo.RemoveAt(0);
        }
        _redo.Clear();
        _lastCoalesce = coalesce;
        _lastChange = now;
        Text = text;
        Evaluate();
        Changed?.Invoke(origin);
    }

    /// <summary>Marks the text as written to disk.</summary>
    public void MarkSaved() => SavedText = Text;

    /// <summary>The file changed on disk under us and the layout has no unsaved edits: take the new text without an undo step.</summary>
    public void Reload(string text)
    {
        SavedText = text;
        if (text == Text) return;
        _undo.Clear();
        _redo.Clear();
        _lastCoalesce = null;
        Text = text;
        Evaluate();
        Changed?.Invoke(ChangeOrigin.External);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new Snapshot(Text, step.Label));
        Restore(step.Text);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new Snapshot(Text, step.Label));
        Restore(step.Text);
        return true;
    }

    private void Restore(string text)
    {
        _lastCoalesce = null;
        Text = text;
        Evaluate();
        Changed?.Invoke(ChangeOrigin.Undo);
    }

    // ------------------------------------------------------------------ checking

    private void Evaluate()
    {
        Revision++;
        _index = null;
        ParseError = null;
        ParseErrorLine = ParseErrorColumn = 0;
        try
        {
            Data = JsonNode.Parse(Text, documentOptions: LayoutLoader.DocumentOptions) as JsonObject;
            if (Data is null) ParseError = "A layout file must contain a JSON object.";
        }
        catch (JsonException ex)
        {
            Data = null;
            ParseErrorLine = (int)(ex.LineNumber ?? 0) + 1;
            ParseErrorColumn = (int)(ex.BytePositionInLine ?? 0) + 1;
            var where = ex.LineNumber is { } line ? $"line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}" : "document";
            ParseError = $"{where}: {FirstSentence(ex.Message)}";
        }
        HasComments = ContainsComments(Text);
        Result = LayoutLoader.Load(Text, Name, BaseDirectory, FilePath, Validator);
        if (Result.Success) GoodDocument = Result.Document;
    }

    private static string FirstSentence(string message)
    {
        var i = message.IndexOf(". ", StringComparison.Ordinal);
        return i > 0 ? message[..(i + 1)] : message;
    }

    private static bool ContainsComments(string text)
    {
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*') return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers for the views

    public JsonObject? Element(string path) => Data is null ? null : ElementOps.Element(Data, path);

    public IEnumerable<string> RegionNames => (Data?["regions"] as JsonObject)?.Select(p => p.Key) ?? [];
    public IEnumerable<string> StyleNames => (Data?["styles"] as JsonObject)?.Select(p => p.Key) ?? [];

    public IEnumerable<LayoutDiagnostic> DiagnosticsFor(string path) =>
        Diagnostics.Where(d => d.Path.Equals(path, StringComparison.Ordinal) || JsonPath.IsWithin(d.Path, path));

    public DiagnosticSeverity? WorstFor(string path)
    {
        DiagnosticSeverity? worst = null;
        foreach (var d in DiagnosticsFor(path))
            if (worst is null || d.Severity > worst) worst = d.Severity;
        return worst;
    }
}
