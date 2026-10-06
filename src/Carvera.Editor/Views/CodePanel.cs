using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor.Views;

/// <summary>The layout's JSON: coloured, checked as you type, with completion for property names and values, and kept in step with the visual editor.</summary>
public sealed class CodePanel : DockPanel
{
    private readonly EditorContext _ctx;
    private readonly TextEditor _editor;
    private readonly TextBlock _position = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _typingTimer;
    private readonly DispatcherTimer _caretTimer;
    private readonly DispatcherTimer _foldTimer;
    private readonly DiagnosticRenderer _squiggles;
    internal TextEditor Editor => _editor;
    private FoldingManager? _folding;
    private CompletionWindow? _completion;
    private bool _syncing;
    private bool _typedSinceSync;

    public CodePanel(EditorContext ctx)
    {
        _ctx = ctx;
        _editor = new TextEditor
        {
            ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 13, WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Background = Brushes.White,
            Padding = new Thickness(4, 2),
        };
        _editor.Options.IndentationSize = 2;
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.TextArea.TextView.LineTransformers.Add(new JsonColorizer());
        _squiggles = new DiagnosticRenderer(_editor, ComputeMarks);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);
        _editor.Text = ctx.Doc.Text;
        SearchPanel.Install(_editor);
        _folding = FoldingManager.Install(_editor.TextArea);

        var format = new Button { Content = "Tidy up", Padding = new Thickness(8, 2), FontSize = 12 };
        ToolTip.SetTip(format, "Rewrite the file with regular indentation. Comments in the file are lost.");
        format.Click += (_, _) =>
        {
            if (_ctx.Doc.Data is null) return;
            FlushTyping();
            _ctx.Doc.SetText(JsonFormatter.Format(_ctx.Doc.Data), ChangeOrigin.Visual, "Tidy up");
        };
        var wrap = new ToggleButton { Content = "Wrap lines", Padding = new Thickness(8, 2), FontSize = 12 };
        wrap.IsCheckedChanged += (_, _) => _editor.WordWrap = wrap.IsChecked == true;
        var bar = new DockPanel { Margin = new Thickness(8, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { format, wrap, _position } };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);
        var toolbar = new Border { Child = bar, Background = Brushes.White, BorderBrush = Brush.Parse("#E2E8F0"), BorderThickness = new Thickness(0, 0, 0, 1) };
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        Children.Add(_editor);

        _typingTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => FlushTyping());
        _caretTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _caretTimer!.Stop(); FollowCaret(); });
        _foldTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => { _foldTimer!.Stop(); UpdateFolding(); });

        _editor.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _typedSinceSync = true;
            _typingTimer.Stop();
            _typingTimer.Start();
            _foldTimer.Stop();
            _foldTimer.Start();
        };
        _editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            var c = _editor.TextArea.Caret;
            _position.Text = $"Ln {c.Line}, Col {c.Column}";
            if (!_syncing) { _caretTimer.Stop(); _caretTimer.Start(); }
        };
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.KeyDown += OnKeyDown;
        _editor.TextArea.TextView.PointerMoved += OnHover;

        ctx.DocumentChanged += OnDocumentChanged;
        UpdateStatus();
        UpdateFolding();
    }

    // ------------------------------------------------------------------ text in step with the document

    private void OnDocumentChanged(ChangeOrigin origin)
    {
        if (origin != ChangeOrigin.Code && _editor.Text != _ctx.Doc.Text)
        {
            // Type and Ctrl+Z from the code view reach here too, so only text that differs is written.
            var caret = _editor.CaretOffset;
            _syncing = true;
            try { ReplaceText(_ctx.Doc.Text); }
            finally { _syncing = false; }
            _editor.CaretOffset = Math.Min(caret, _editor.Document.TextLength);
            _typedSinceSync = false;
            UpdateFolding();
        }
        UpdateStatus();
        _squiggles.Invalidate();
    }

    /// <summary>Changes only the part of the text that differs, so the caret, scroll position and selection survive a visual edit.</summary>
    private void ReplaceText(string text)
    {
        var old = _editor.Document.Text;
        var prefix = 0;
        var limit = Math.Min(old.Length, text.Length);
        while (prefix < limit && old[prefix] == text[prefix]) prefix++;
        var suffix = 0;
        while (suffix < limit - prefix && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix]) suffix++;
        _editor.Document.Replace(prefix, old.Length - prefix - suffix, text.Substring(prefix, text.Length - prefix - suffix));
    }

    private void FlushTyping()
    {
        _typingTimer.Stop();
        if (!_typedSinceSync) return;
        _typedSinceSync = false;
        _ctx.Doc.SetText(_editor.Text);
    }

    private void UpdateStatus()
    {
        var doc = _ctx.Doc;
        if (doc.ParseError is { } error)
        {
            _status.Text = "✕ Not valid JSON — " + error + "  (the Controller keeps its last layout)";
            _status.Foreground = PropertyEditors.ErrorBrush;
        }
        else if (doc.Result.Errors.FirstOrDefault() is { } first)
        {
            _status.Text = $"✕ {first.Path}: {first.Message}";
            _status.Foreground = PropertyEditors.ErrorBrush;
        }
        else
        {
            var warnings = doc.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            _status.Text = warnings == 0 ? "✓ Valid" : $"✓ Valid, {warnings} warning{(warnings == 1 ? "" : "s")}";
            _status.Foreground = warnings == 0 ? Brush.Parse("#15803D") : Brush.Parse("#B45309");
        }
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>Puts the caret at the text of a path.</summary>
    public void GoTo(string path)
    {
        FlushTyping();
        var span = _ctx.Doc.Index.Nearest(path);
        if (span is null) return;
        _syncing = true;
        try
        {
            _editor.CaretOffset = Math.Min(span.Value.Start, _editor.Document.TextLength);
            var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            _editor.ScrollTo(line.LineNumber, 1);
            _editor.TextArea.Selection = Selection.Create(_editor.TextArea, line.Offset, line.EndOffset);
        }
        finally { _syncing = false; }
        _editor.Focus();
    }

    private void FollowCaret()
    {
        if (_ctx.Doc.Text != _editor.Text || !_editor.IsKeyboardFocusWithin) return;
        if (_ctx.Doc.Index.ElementAt(_editor.CaretOffset) is { } path) _ctx.Select(path, this);
    }

    // ------------------------------------------------------------------ folding

    private void UpdateFolding()
    {
        if (_folding is null) return;
        var foldings = new List<NewFolding>();
        var starts = new Stack<int>();
        var text = _editor.Text;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[': starts.Push(i); break;
                case '}' or ']':
                    if (starts.Count > 0)
                    {
                        var start = starts.Pop();
                        if (_editor.Document.GetLineByOffset(start).LineNumber != _editor.Document.GetLineByOffset(i).LineNumber)
                            foldings.Add(new NewFolding(start, i + 1));
                    }
                    break;
            }
        }
        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        _folding.UpdateFoldings(foldings, -1);
    }

    // ------------------------------------------------------------------ marks

    private IEnumerable<(int Start, int End, bool IsError)> ComputeMarks()
    {
        var doc = _ctx.Doc;
        var length = _editor.Document.TextLength;
        if (doc.ParseError is not null && doc.ParseErrorLine > 0 && doc.ParseErrorLine <= _editor.Document.LineCount)
        {
            var line = _editor.Document.GetLineByNumber(doc.ParseErrorLine);
            var offset = Math.Min(line.Offset + Math.Max(0, doc.ParseErrorColumn - 1), line.EndOffset);
            yield return (Math.Max(line.Offset, offset - 1), Math.Max(offset, Math.Min(line.EndOffset, offset + 1)), true);
            yield break;
        }
        if (doc.Text != _editor.Text) yield break; // the text moved on; marks come back once it has been read again
        foreach (var d in doc.Diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Info || doc.Index.Nearest(d.Path) is not { } span) continue;
            var start = Math.Min(span.Start, length);
            var end = Math.Min(span.End, length);
            // Long values (a whole element) are marked on their first line only.
            var line = _editor.Document.GetLineByOffset(start);
            yield return (start, Math.Min(end, line.EndOffset) is var e && e > start ? e : Math.Min(start + 1, length), d.Severity == DiagnosticSeverity.Error);
        }
    }

    private void OnHover(object? sender, PointerEventArgs e)
    {
        var view = _editor.TextArea.TextView;
        var position = view.GetPosition(e.GetPosition(view) + view.ScrollOffset);
        if (position is null || _ctx.Doc.Text != _editor.Text) { ToolTip.SetTip(_editor, null); return; }
        var offset = _editor.Document.GetOffset(position.Value.Location);
        var lines = new List<string>();
        foreach (var d in _ctx.Doc.Diagnostics)
            if (_ctx.Doc.Index.Nearest(d.Path) is { } span && span.Contains(offset) && _editor.Document.GetLineByOffset(span.Start).LineNumber == position.Value.Line)
                lines.Add((d.Severity == DiagnosticSeverity.Error ? "✕ " : "⚠ ") + d.Message);
        if (lines.Count == 0 && DescribeProperty(offset) is { } description) lines.Add(description);
        ToolTip.SetTip(_editor, lines.Count == 0 ? null : string.Join("\n", lines.Distinct()));
    }

    /// <summary>The catalog's explanation of the property the offset is on.</summary>
    private string? DescribeProperty(int offset)
    {
        if (_ctx.Doc.Index.PathAt(offset) is not { } path) return null;
        var segments = JsonPath.Parse(path);
        if (segments.Count < 2 || segments[^1] is not string name) return null;
        if (JsonPath.ElementPrefix(path) is { } elementPath && elementPath.Length < path.Length && _ctx.Doc.Element(elementPath) is { } element)
        {
            var type = ElementOps.TypeOf(element);
            if (name == "type" && ComponentCatalog.TryGet(element["type"]?.ToString() ?? "", out var component)) return component.Description;
            if (ComponentCatalog.TryGet(type, out var spec) && spec.Find(name) is { } prop && JsonPath.Parent(path) == elementPath) return $"{name}: {prop.Description}";
        }
        var visual = ComponentCatalog.VisualProperties.FirstOrDefault(p => p.Name == name);
        if (visual is not null && segments.Count >= 3 && segments[^2] is not "children") return $"{name}: {visual.Description}";
        return null;
    }

    // ------------------------------------------------------------------ keyboard and completion

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            ShowCompletion(force: true);
        }
        else if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
        {
            // One history for the visual and the code view.
            e.Handled = true;
            FlushTyping();
            _ctx.Undo();
        }
        else if ((e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control) || (e.Key == Key.Z && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)))
        {
            e.Handled = true;
            FlushTyping();
            _ctx.Redo();
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_completion is not null || string.IsNullOrEmpty(e.Text)) return;
        var c = e.Text[0];
        if (c == '"' || char.IsLetter(c) || c is '@' or ':' or '.') ShowCompletion(force: false, typed: c);
    }

    private void ShowCompletion(bool force, char? typed = null)
    {
        if (_completion is not null) return;
        var text = _editor.Text;
        var caret = _editor.CaretOffset;
        var names = new CompletionNames(_ctx.Doc.RegionNames.ToList(), _ctx.Doc.StyleNames.ToList());
        var result = CompletionEngine.Suggest(text, caret, names);
        if (result is null) return;
        // Typing letters offers names only where a name is expected, not after every letter of free text.
        if (!force && typed is { } ch && char.IsLetter(ch) && result.Items.Count > 120) return;

        var window = new CompletionWindow(_editor.TextArea) { CloseWhenCaretAtBeginning = false };
        var startOffset = result.Start;
        var quoted = startOffset < text.Length && text[startOffset] == '"';
        window.StartOffset = quoted && startOffset + 1 <= caret ? startOffset + 1 : startOffset;
        var caretAtOpen = caret;
        foreach (var item in result.Items.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase).Take(400))
            window.CompletionList.CompletionData.Add(new JsonCompletion(item, result.Start, result.End, caretAtOpen, ThenValue));
        _completion = window;
        window.Closed += (_, _) => _completion = null;
        window.Show();
    }

    private void ThenValue() => Dispatcher.UIThread.Post(() => ShowCompletion(force: true), DispatcherPriority.Background);

    private sealed class JsonCompletion(Suggestion item, int start, int end, int caretAtOpen, Action then) : ICompletionData
    {
        public Avalonia.Media.IImage? Image => null;
        public string Text => item.Label;
        public object Content => item.Label;
        public object? Description => item.Detail;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            // The window replaces what was typed since it opened; the start and end recorded then are moved by however much was typed.
            var delta = completionSegment.EndOffset - caretAtOpen;
            var replaceEnd = Math.Min(textArea.Document.TextLength, Math.Max(completionSegment.EndOffset, end + delta));
            textArea.Document.Replace(start, replaceEnd - start, item.Insert);
            textArea.Caret.Offset = start + item.Insert.Length;
            if (item.ThenValue) then();
        }
    }
}

/// <summary>Colours JSON: property names, strings, numbers, true/false/null and comments.</summary>
internal sealed partial class JsonColorizer : DocumentColorizingTransformer
{
    private static readonly IBrush KeyBrush = Brush.Parse("#1D4ED8");
    private static readonly IBrush StringBrush = Brush.Parse("#15803D");
    private static readonly IBrush NumberBrush = Brush.Parse("#B45309");
    private static readonly IBrush LiteralBrush = Brush.Parse("#7C3AED");
    private static readonly IBrush CommentBrush = Brush.Parse("#94A3B8");

    [GeneratedRegex("""(?<comment>//.*$)|(?<key>"(?:[^"\\]|\\.)*"(?=\s*:))|(?<string>"(?:[^"\\]|\\.)*")|(?<number>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(?<literal>\b(?:true|false|null)\b)""")]
    private static partial Regex Token();

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        foreach (Match match in Token().Matches(text))
        {
            IBrush brush = match.Groups["comment"].Success ? CommentBrush : match.Groups["key"].Success ? KeyBrush : match.Groups["string"].Success ? StringBrush
                : match.Groups["number"].Success ? NumberBrush : LiteralBrush;
            ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}

/// <summary>Wavy underlines under the text a diagnostic points at.</summary>
internal sealed class DiagnosticRenderer(TextEditor editor, Func<IEnumerable<(int Start, int End, bool IsError)>> marks) : IBackgroundRenderer
{
    private static readonly IPen ErrorPen = new Pen(Brush.Parse("#DC2626"), 1.2);
    private static readonly IPen WarningPen = new Pen(Brush.Parse("#D97706"), 1.2);

    public KnownLayer Layer => KnownLayer.Selection;

    public void Invalidate() => editor.TextArea.TextView.InvalidateLayer(Layer);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is null) return;
        foreach (var (start, end, isError) in marks().ToList())
        {
            if (end <= start) continue;
            var segment = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                var y = rect.Bottom - 1;
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(rect.Left, y), false);
                    var up = false;
                    for (var x = rect.Left + 2; x < rect.Right; x += 2)
                    {
                        context.LineTo(new Point(x, up ? y : y - 2));
                        up = !up;
                    }
                    context.EndFigure(false);
                }
                drawingContext.DrawGeometry(null, isError ? ErrorPen : WarningPen, geometry);
            }
        }
    }
}
