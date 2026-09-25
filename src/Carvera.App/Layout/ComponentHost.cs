using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Carvera.Core.Expressions;
using Carvera.Layout;

namespace Carvera.App.Layout;

/// <summary>
/// Wraps every layout element. It resolves the element's look from layered visual blocks — built-in
/// defaults, classes, style, per-state "visuals" and matching "conditions" — and applies it (background,
/// border, fonts, opacity...). Components with image/text content receive the resolved block through
/// <see cref="VisualChanged"/> so each state can carry its own graphics.
/// </summary>
public class ComponentHost : Border
{
    private static readonly string[] TrailingStates = ["hover", "pressed", "disabled"];

    private readonly BuildContext _ctx;
    private readonly IReadOnlyDictionary<string, VisualBlock> _defaults;
    private readonly VisualBlock _classes;
    private readonly VisualBlock _style;
    private readonly Dictionary<string, VisualBlock> _visuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Expression When, VisualBlock Visual)> _conditions = [];
    private readonly List<string> _stateOrder;
    private readonly HashSet<string> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Expression? _enabledExpr;
    private readonly Expression? _visibleExpr;
    private readonly TextTemplate? _tooltip;
    private readonly Dictionary<string, TextTemplate?> _textCache = new();
    private Func<bool>? _extraEnabled;
    private bool _hover, _pressed;
    private DispatcherTimer? _repeat;

    public ComponentHost(LayoutNode node, BuildContext ctx, string? visualType = null, JsonObject? visualsOverride = null)
    {
        Node = node;
        _ctx = ctx;
        var type = visualType ?? node.Type;
        _defaults = VisualDefaults.For(type);
        _classes = ctx.ClassStyle(node);
        _style = VisualBlock.Parse(node.Get("style") as JsonObject);
        // The element-level "padding" property behaves like style.padding.
        if (node.Has("padding") && Edges.TryParse(node.Get("padding"), out var padding, out _)) _style = _style with { Padding = padding };
        foreach (var (state, visual) in (visualsOverride ?? node.Get("visuals") as JsonObject) ?? [])
            if (visual is JsonObject v) _visuals[state] = VisualBlock.Parse(v);
        if (visualsOverride is null && node.Get("conditions") is JsonArray conditions)
            foreach (var item in conditions.OfType<JsonObject>())
                if (item["when"] is JsonValue w && w.TryGetValue<string>(out var when) && Expression.TryParse(when, out var expr, out _))
                    _conditions.Add((expr!, VisualBlock.Parse(item["visual"] as JsonObject ?? item)));

        _stateOrder = ComponentCatalog.TryGet(node.Type, out var spec) ? spec.States.Where(s => s != "normal" && !TrailingStates.Contains(s)).ToList() : [];
        foreach (var extra in new[] { "on", "off", "selected", "active" })
            if (!_stateOrder.Contains(extra)) _stateOrder.Add(extra);

        _enabledExpr = ctx.ExpressionProp(node, "enabled");
        _visibleExpr = ctx.ExpressionProp(node, "visible");
        _tooltip = ctx.TemplateProp(node, "tooltip");

        var paths = _conditions.SelectMany(c => c.When.Paths)
            .Concat(_enabledExpr?.Paths ?? []).Concat(_visibleExpr?.Paths ?? []).Concat(_tooltip?.Paths ?? [])
            .Concat(AllTextTemplates().SelectMany(t => t.Paths));
        ctx.Watch(paths, Refresh);

        if (Edges.TryParse(node.Get("margin"), out var margin, out _)) Margin = margin.ToThickness();
        if (node.GetNumber("minWidth") is { } minW) MinWidth = minW;
        if (node.GetNumber("maxWidth") is { } maxW) MaxWidth = maxW;
        if (node.GetNumber("minHeight") is { } minH) MinHeight = minH;
        if (node.GetNumber("maxHeight") is { } maxH) MaxHeight = maxH;
        FlexPanel.SetSlot(this, new SlotSpec(node.Width, node.Height, ParseAlign(node.GetString("align")), ParseAlign(node.GetString("valign"))));
        if (SizeSpec.TryParse(node.Get("x"), out var x, out _) && node.Has("x")) AbsolutePanel.SetX(this, x);
        if (SizeSpec.TryParse(node.Get("y"), out var y, out _) && node.Has("y")) AbsolutePanel.SetY(this, y);

        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsEffectivelyEnabledProperty) Refresh();
        };
        Refresh();
    }

    public LayoutNode Node { get; }

    /// <summary>The element's own text (e.g. a button caption); a state's "text" replaces it.</summary>
    public TextTemplate? DefaultText { get; private set; }

    public VisualBlock Effective { get; private set; } = VisualBlock.Empty;
    public string? EffectiveText { get; private set; }

    /// <summary>Raised whenever the resolved visual or text changes.</summary>
    public event Action<ComponentHost>? VisualChanged;

    /// <summary>Raised when a clickable host is activated (mouse, touch, Enter or Space).</summary>
    public event Action? Clicked;

    public bool Clickable { get; private set; }
    public bool Repeat { get; set; }

    public Theme LayoutTheme => _ctx.Theme;

    private readonly HashSet<TextTemplate> _watchedTemplates = [];

    public void SetDefaultText(TextTemplate? template)
    {
        if (ReferenceEquals(template, DefaultText)) return;
        DefaultText = template;
        if (template is not null && _watchedTemplates.Add(template)) _ctx.Watch(template.Paths, Refresh);
        Refresh();
    }

    public void MakeClickable()
    {
        Clickable = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Refresh();
    }

    /// <summary>Adds a further enabled condition (e.g. command availability).</summary>
    public void AddEnabledCondition(Func<bool> condition, IEnumerable<string> paths)
    {
        var previous = _extraEnabled;
        _extraEnabled = previous is null ? condition : () => previous() && condition();
        _ctx.Watch(paths, Refresh);
        Refresh();
    }

    public void SetState(string state, bool on)
    {
        if (on ? _states.Add(state) : _states.Remove(state)) Refresh();
    }

    public void SetStates(IEnumerable<string> states, IEnumerable<string> clear)
    {
        var changed = false;
        foreach (var s in clear) changed |= _states.Remove(s);
        foreach (var s in states) changed |= _states.Add(s);
        if (changed) Refresh();
    }

    public bool HasState(string state) => _states.Contains(state);

    public IEnumerable<string> ActiveStates()
    {
        foreach (var s in _stateOrder) if (_states.Contains(s)) yield return s;
        foreach (var s in _states) if (!_stateOrder.Contains(s) && !TrailingStates.Contains(s)) yield return s;
        if (_hover && IsEffectivelyEnabled) yield return "hover";
        if (_pressed && IsEffectivelyEnabled) yield return "pressed";
        if (!IsEffectivelyEnabled) yield return "disabled";
    }

    public void Refresh()
    {
        IsVisible = _visibleExpr?.EvaluateBool(_ctx.Resolve) ?? true;
        IsEnabled = (_enabledExpr?.EvaluateBool(_ctx.Resolve) ?? true) && (_extraEnabled?.Invoke() ?? true);

        var visual = (_defaults.TryGetValue("normal", out var d) ? d : VisualBlock.Empty)
            .Merge(_classes).Merge(_style).Merge(_visuals.GetValueOrDefault("normal"));
        foreach (var state in ActiveStates())
            visual = visual.Merge(_defaults.GetValueOrDefault(state)).Merge(_visuals.GetValueOrDefault(state));
        foreach (var (when, v) in _conditions)
            if (when.EvaluateBool(_ctx.Resolve)) visual = visual.Merge(v);

        Effective = visual;
        var template = visual.Text is not null ? TemplateFor(visual.Text) : DefaultText;
        EffectiveText = template?.Render(_ctx.Resolve);
        Apply(visual);
        if (_tooltip is not null) ToolTip.SetTip(this, _tooltip.Render(_ctx.Resolve));
        VisualChanged?.Invoke(this);
    }

    private TextTemplate? TemplateFor(string text)
    {
        if (!_textCache.TryGetValue(text, out var t)) _textCache[text] = t = BuildContext.TemplateOf(text);
        return t;
    }

    private IEnumerable<TextTemplate> AllTextTemplates()
    {
        var blocks = _visuals.Values.Concat(_conditions.Select(c => c.Visual)).Append(_style).Append(_classes);
        foreach (var block in blocks)
            if (block.Text is not null && TemplateFor(block.Text) is { } t) yield return t;
    }

    private void Apply(VisualBlock v)
    {
        var theme = _ctx.Theme;
        Background = theme.Brush(v.Background) ?? (Clickable ? Brushes.Transparent : null);
        BorderBrush = theme.Brush(v.BorderColor);
        BorderThickness = v.BorderWidth?.ToThickness() ?? default;
        CornerRadius = new CornerRadius(v.CornerRadius ?? 0);
        Padding = v.Padding?.ToThickness() ?? default;
        Opacity = v.Opacity ?? 1;
        SetOrClear(TextElement.ForegroundProperty, theme.Brush(v.Foreground));
        SetOrClear(TextElement.FontSizeProperty, v.FontSize);
        SetOrClear(TextElement.FontWeightProperty, ParseWeight(v.FontWeight));
        SetOrClear(TextElement.FontStyleProperty, v.FontStyle?.Equals("italic", StringComparison.OrdinalIgnoreCase) == true ? FontStyle.Italic : v.FontStyle is null ? null : FontStyle.Normal);
        SetOrClear(TextElement.FontFamilyProperty, v.FontFamily is null ? null : new FontFamily(v.FontFamily));
    }

    private void SetOrClear<T>(AvaloniaProperty<T> property, T? value)
    {
        if (value is null) ClearValue(property);
        else SetValue(property, value);
    }

    private void SetOrClear(AvaloniaProperty<double> property, double? value)
    {
        if (value is null) ClearValue(property);
        else SetValue(property, value.Value);
    }

    private void SetOrClear(AvaloniaProperty<FontWeight> property, FontWeight? value)
    {
        if (value is null) ClearValue(property);
        else SetValue(property, value.Value);
    }

    private void SetOrClear(AvaloniaProperty<FontStyle> property, FontStyle? value)
    {
        if (value is null) ClearValue(property);
        else SetValue(property, value.Value);
    }

    public static FontWeight? ParseWeight(string? text) => text?.ToLowerInvariant() switch
    {
        null => null,
        "light" => FontWeight.Light,
        "medium" => FontWeight.Medium,
        "semibold" => FontWeight.SemiBold,
        "bold" => FontWeight.Bold,
        "black" => FontWeight.Black,
        _ => FontWeight.Normal,
    };

    private static SlotAlign? ParseAlign(string? text) => text?.ToLowerInvariant() switch
    {
        "start" or "left" or "top" => SlotAlign.Start,
        "center" => SlotAlign.Center,
        "end" or "right" or "bottom" => SlotAlign.End,
        "stretch" => SlotAlign.Stretch,
        _ => null,
    };

    // ------------------------------------------------------------------ pointer and keyboard

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        Refresh();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        Refresh();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Clickable || !IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        Focus();
        Refresh();
        if (Repeat)
        {
            Clicked?.Invoke();
            _repeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _repeat.Tick += (_, _) =>
            {
                _repeat!.Interval = TimeSpan.FromMilliseconds(150);
                if (_pressed && IsEffectivelyEnabled) Clicked?.Invoke();
            };
            _repeat.Start();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        var inside = new Rect(Bounds.Size).Contains(e.GetPosition(this));
        EndPress();
        e.Pointer.Capture(null);
        e.Handled = true;
        if (inside && !Repeat && IsEffectivelyEnabled) Clicked?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPress();
    }

    private void EndPress()
    {
        _repeat?.Stop();
        _repeat = null;
        if (!_pressed) return;
        _pressed = false;
        Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Clickable && IsEffectivelyEnabled && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            Clicked?.Invoke();
        }
    }

    /// <summary>Simulates a click (used by tests and keyboard shortcuts).</summary>
    public void PerformClick()
    {
        if (Clickable && IsEffectivelyEnabled) Clicked?.Invoke();
    }
}

public static class EdgesExtensions
{
    public static Thickness ToThickness(this Edges e) => new(e.Left, e.Top, e.Right, e.Bottom);
}
