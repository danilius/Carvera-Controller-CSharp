using System.Text;
using Carvera.Layout;

namespace Carvera.Editor.Model;

public sealed record Suggestion(string Label, string Insert, string? Detail = null, bool ThenValue = false);

/// <summary>What to replace (start to end, in the text) and the choices to offer.</summary>
public sealed record CompletionResult(int Start, int End, IReadOnlyList<Suggestion> Items);

/// <summary>Names the engine offers that come from the document itself.</summary>
public sealed record CompletionNames(IReadOnlyList<string> Regions, IReadOnlyList<string> Styles);

/// <summary>
/// Works out what may be typed at a position in a layout file: property names for the kind of element around the caret, and values for the
/// property before it (enum values, commands, colours, state paths, region and style names). It reads the text tolerantly, so it works
/// while the JSON is half typed.
/// </summary>
public static class CompletionEngine
{
    private enum Kind { Document, Element, ElementList, RegionsMap, StylesMap, VisualBlock, VisualsMap, Conditions, Condition, ThemeMap, ShortcutList, Shortcut, Window, Args, Other }

    private sealed class Frame
    {
        public bool IsArray;
        public int Start;
        public Kind Kind;
        public Frame? Parent;
        public string? ParentKey;
        public string? LastKey;
        public bool ExpectValue;
        public Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public HashSet<string> Keys = new(StringComparer.Ordinal);
    }

    private sealed record Position(Frame? Top, bool InString, int StringStart, bool IsKey);

    public static CompletionResult? Suggest(string text, int caret, CompletionNames names)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var pos = Scan(text, caret);
        if (pos.Top is not { } frame) return null;

        // Where the replacement goes.
        int start, end;
        var inString = pos.InString;
        if (inString)
        {
            start = pos.StringStart;
            end = caret;
            while (end < text.Length && text[end] != '"' && text[end] != '\n') end++;
        }
        else
        {
            start = caret;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '@' or ':' or '.' or '-')) start--;
            end = caret;
        }

        var expectingKey = !frame.IsArray && !frame.ExpectValue;
        var items = new List<Suggestion>();
        var fullFrame = ShallowObject(text, frame.Start);

        if (expectingKey)
        {
            foreach (var (name, detail) in KeyChoices(frame, fullFrame.Keys, fullFrame.Values, names))
            {
                if (fullFrame.Keys.Contains(name) && !(inString && text.AsSpan(start, Math.Max(0, end - start)).SequenceEqual(name))) continue;
                items.Add(new Suggestion(name, name, detail, ThenValue: true));
            }
            // A quoted key replaces the quotes too, so the insert text carries them.
            if (inString)
            {
                start--;
                if (end < text.Length && text[end] == '"') end++;
                var hasColon = FollowedByColon(text, end);
                items = items.Select(s => s with { Insert = "\"" + s.Insert + "\"" + (hasColon ? "" : ": ") }).ToList();
            }
            else items = items.Select(s => s with { Insert = "\"" + s.Insert + "\": " }).ToList();
        }
        else
        {
            var key = frame.IsArray ? null : frame.LastKey;
            foreach (var choice in ValueChoices(frame, key, fullFrame.Values, names))
                items.Add(choice);
            if (inString)
            {
                // The closing quote stays where it is.
                items = items.Select(s => s with { Insert = s.Insert.Trim('"') }).ToList();
            }
            else
            {
                items = items.Select(s => s.Insert is "true" or "false" || double.TryParse(s.Insert, out _) ? s : s with { Insert = "\"" + s.Insert.Trim('"') + "\"" }).ToList();
            }
        }

        var filterStart = Math.Min(caret, start + (inString && expectingKey ? 1 : 0));
        var filter = text[filterStart..caret];
        filter = filter.Trim('"');
        var filtered = items.Where(i => filter.Length == 0 || i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        return filtered.Count == 0 ? null : new CompletionResult(start, end, filtered);
    }

    private static bool FollowedByColon(string text, int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t') index++;
        return index < text.Length && text[index] == ':';
    }

    // ------------------------------------------------------------------ what can be typed

    private static IEnumerable<(string Name, string? Detail)> KeyChoices(Frame frame, HashSet<string> present, Dictionary<string, string> values, CompletionNames names)
    {
        switch (frame.Kind)
        {
            case Kind.Document:
                foreach (var k in new[] { "name", "description", "window", "theme", "styles", "regions", "root", "shortcuts" }) yield return (k, null);
                break;
            case Kind.Element:
            {
                if (values.ContainsKey("region") && !values.ContainsKey("type"))
                {
                    foreach (var p in ComponentCatalog.Common.Where(p => p.Name is "width" or "height" or "visible" or "enabled" or "row" or "column" or "rowSpan" or "columnSpan" or "x" or "y" or "title" or "margin" or "align" or "valign" or "id"))
                        yield return (p.Name, p.Description);
                    break;
                }
                yield return ("type", "The kind of element.");
                var spec = values.TryGetValue("type", out var type) && ComponentCatalog.TryGet(type, out var s) ? s : null;
                if (spec is not null)
                {
                    foreach (var p in spec.Properties) yield return (p.Name, p.Description);
                    if (spec.Children == ChildRule.Many) yield return ("children", "The elements inside.");
                    if (spec.Children == ChildRule.Single) yield return ("child", "The element inside.");
                }
                else
                {
                    yield return ("children", "The elements inside.");
                    yield return ("region", "Use a named region instead of a type.");
                }
                foreach (var p in ComponentCatalog.Common) yield return (p.Name, p.Description);
                break;
            }
            case Kind.VisualBlock:
                foreach (var p in ComponentCatalog.VisualProperties) yield return (p.Name, p.Description);
                break;
            case Kind.Condition:
                yield return ("when", "The expression that switches this look on.");
                foreach (var p in ComponentCatalog.VisualProperties) yield return (p.Name, p.Description);
                break;
            case Kind.VisualsMap:
            {
                var owner = frame.Parent;
                var ownerType = owner is null ? null : OwnerType(owner);
                var states = ownerType is not null && ComponentCatalog.TryGet(ownerType, out var spec) ? spec.States : ComponentCatalog.BaseStates;
                foreach (var state in states) yield return (state, "Look in the '" + state + "' state.");
                break;
            }
            case Kind.Window:
                foreach (var k in new[] { "width", "height", "minWidth", "minHeight", "title" }) yield return (k, null);
                break;
            case Kind.ThemeMap:
                foreach (var (k, v) in EditorCatalog.ThemeDefaults) yield return (k, "default " + v);
                break;
            case Kind.Shortcut:
                foreach (var k in new[] { "key", "command", "args", "release", "releaseArgs", "repeat" }) yield return (k, null);
                break;
            case Kind.Args:
            {
                var owner = frame.Parent;
                var key = frame.ParentKey == "releaseArgs" ? "release" : "command";
                var id = owner is null ? null : ShallowObjectValue(owner, key);
                if (EditorCatalog.Command(id) is { } command)
                    foreach (var p in command.Parameters) yield return (p.Name, p.Description);
                break;
            }
        }
    }

    private static string? OwnerType(Frame owner) => owner.Values.TryGetValue("type", out var t) ? t : null;
    private static string? ShallowObjectValue(Frame owner, string key) => owner.Values.TryGetValue(key, out var v) ? v : null;

    private static IEnumerable<Suggestion> ValueChoices(Frame frame, string? key, Dictionary<string, string> values, CompletionNames names)
    {
        if (key is null && !frame.IsArray) yield break;
        switch (frame.Kind)
        {
            case Kind.Element when key is not null:
            {
                if (key == "type") { foreach (var c in ComponentCatalog.All) yield return new Suggestion(c.Type, c.Type, c.Description.Split(". ")[0]); yield break; }
                if (key == "region") { foreach (var r in names.Regions) yield return new Suggestion(r, r); yield break; }
                if (key == "class") { foreach (var s in names.Styles) yield return new Suggestion(s, s); yield break; }
                var spec = values.TryGetValue("type", out var type) && ComponentCatalog.TryGet(type, out var cs) ? cs.Find(key) : ComponentCatalog.Common.FirstOrDefault(p => p.Name == key);
                if (spec is null) yield break;
                foreach (var s in ByKind(spec, names)) yield return s;
                break;
            }
            case Kind.VisualBlock or Kind.Condition when key is not null:
            {
                if (key == "when") { foreach (var s in StateExpressions()) yield return s; yield break; }
                var spec = ComponentCatalog.VisualProperties.FirstOrDefault(p => p.Name == key);
                if (spec is null) yield break;
                foreach (var s in ByKind(spec, names)) yield return s;
                break;
            }
            case Kind.Shortcut when key is "command" or "release":
                foreach (var c in EditorCatalog.Commands) yield return new Suggestion(c.Id, c.Id, c.Title);
                break;
            case Kind.ThemeMap or Kind.Window or Kind.Document:
                yield break;
        }
    }

    private static IEnumerable<Suggestion> ByKind(PropSpec spec, CompletionNames names)
    {
        switch (spec.Kind)
        {
            case PropKind.Enum or PropKind.StringList when spec.Values is { Length: > 0 }:
                foreach (var v in spec.Values!) yield return new Suggestion(v, v);
                break;
            case PropKind.Bool:
                yield return new Suggestion("true", "true");
                yield return new Suggestion("false", "false");
                break;
            case PropKind.Command:
                foreach (var c in EditorCatalog.Commands) yield return new Suggestion(c.Id, c.Id, c.Title);
                break;
            case PropKind.Color:
                foreach (var t in EditorCatalog.ColorTokens) yield return new Suggestion("@" + t, "@" + t, EditorCatalog.ThemeDefaults[t]);
                foreach (var n in new[] { "transparent", "white", "black" }) yield return new Suggestion(n, n);
                break;
            case PropKind.Image:
                foreach (var n in EditorCatalog.BuiltinImages) yield return new Suggestion("builtin:" + n, "builtin:" + n);
                break;
            case PropKind.Expression:
                foreach (var s in StateExpressions()) yield return s;
                break;
            case PropKind.Size:
                foreach (var v in new[] { "auto", "fill", "50%", "1*", "2*" }) yield return new Suggestion(v, v);
                break;
            case PropKind.Style:
                break;
        }
        if (spec.Name == "class") foreach (var s in names.Styles) yield return new Suggestion(s, s);
    }

    private static IEnumerable<Suggestion> StateExpressions()
    {
        foreach (var p in EditorCatalog.StatePathNames) yield return new Suggestion(p, p);
        foreach (var e in EditorCatalog.Expressions.Where(e => e.Contains(' '))) yield return new Suggestion(e, e);
    }

    // ------------------------------------------------------------------ reading the text

    private static Position Scan(string text, int caret)
    {
        var stack = new List<Frame>();
        var inString = false;
        var stringStart = 0;
        var sb = new StringBuilder();
        for (var i = 0; i < caret; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\' && i + 1 < caret) { sb.Append(text[i + 1]); i++; continue; }
                if (c == '"')
                {
                    inString = false;
                    var value = sb.ToString();
                    if (stack.Count > 0)
                    {
                        var top = stack[^1];
                        if (!top.IsArray && !top.ExpectValue) { top.LastKey = value; top.Keys.Add(value); }
                        else if (!top.IsArray && top.LastKey is { } k) top.Values[k] = value;
                    }
                    continue;
                }
                sb.Append(c);
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    stringStart = i + 1;
                    sb.Clear();
                    break;
                case '/' when i + 1 < caret && text[i + 1] == '/':
                    while (i < caret && text[i] != '\n') i++;
                    break;
                case '/' when i + 1 < caret && text[i + 1] == '*':
                    i += 2;
                    while (i + 1 < caret && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i++;
                    break;
                case '{' or '[':
                {
                    var parent = stack.Count > 0 ? stack[^1] : null;
                    var key = parent is { IsArray: false } ? parent.LastKey : null;
                    var frame = new Frame { IsArray = c == '[', Start = i, Parent = parent, ParentKey = key, Kind = Classify(parent, key, c == '[') };
                    stack.Add(frame);
                    break;
                }
                case '}' or ']':
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    if (stack.Count > 0 && !stack[^1].IsArray) stack[^1].ExpectValue = true; // the value just closed; a comma follows
                    break;
                case ':':
                    if (stack.Count > 0) stack[^1].ExpectValue = true;
                    break;
                case ',':
                    if (stack.Count > 0) stack[^1].ExpectValue = false;
                    break;
            }
        }
        var topFrame = stack.Count > 0 ? stack[^1] : null;
        var isKey = inString && topFrame is { IsArray: false, ExpectValue: false };
        return new Position(topFrame, inString, stringStart, isKey);
    }

    private static Kind Classify(Frame? parent, string? key, bool isArray)
    {
        if (parent is null) return Kind.Document;
        return parent.Kind switch
        {
            Kind.Document => key switch
            {
                "root" => Kind.Element, "regions" => Kind.RegionsMap, "styles" => Kind.StylesMap, "theme" => Kind.ThemeMap,
                "shortcuts" => Kind.ShortcutList, "window" => Kind.Window, _ => Kind.Other,
            },
            Kind.RegionsMap => Kind.Element,
            Kind.Element => key switch
            {
                "children" => Kind.ElementList, "child" => Kind.Element, "style" => Kind.VisualBlock, "visuals" => Kind.VisualsMap,
                "conditions" => Kind.Conditions, "args" or "onArgs" or "offArgs" => Kind.Args, _ => Kind.Other,
            },
            Kind.ElementList => Kind.Element,
            Kind.StylesMap or Kind.VisualsMap => Kind.VisualBlock,
            Kind.Conditions => Kind.Condition,
            Kind.ShortcutList => Kind.Shortcut,
            Kind.Shortcut when key is "args" or "releaseArgs" => Kind.Args,
            _ => Kind.Other,
        };
    }

    /// <summary>The string values and keys of the object starting at an offset, read to its closing brace (which may lie after the caret).</summary>
    private static (Dictionary<string, string> Values, HashSet<string> Keys) ShallowObject(string text, int start)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        string? lastKey = null;
        var expectValue = false;
        var sb = new StringBuilder();
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                sb.Clear();
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i]);
                }
                if (depth == 1)
                {
                    if (!expectValue) { lastKey = sb.ToString(); keys.Add(lastKey); }
                    else if (lastKey is not null) values[lastKey] = sb.ToString();
                }
                continue;
            }
            switch (c)
            {
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    while (i < text.Length && text[i] != '\n') i++;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return (values, keys);
                    if (depth == 1) expectValue = true;
                    break;
                case ':' when depth == 1:
                    expectValue = true;
                    break;
                case ',' when depth == 1:
                    expectValue = false;
                    break;
            }
        }
        return (values, keys);
    }
}
