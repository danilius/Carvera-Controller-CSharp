using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Carvera.Layout;

namespace Carvera.App.Layout;

public enum SlotAlign { Start, Center, End, Stretch }

/// <summary>How a child wants to be sized and placed within the space its parent gives it.</summary>
public sealed record SlotSpec(SizeSpec Width, SizeSpec Height, SlotAlign? HAlign = null, SlotAlign? VAlign = null)
{
    public static readonly SlotSpec Fill = new(SizeSpec.Fill, SizeSpec.Fill);

    /// <summary>Unsized axes stretch; sized axes sit at the start, as block elements do on the web.</summary>
    public SlotAlign EffectiveH => HAlign ?? (Width.IsFlexible ? SlotAlign.Stretch : SlotAlign.Start);
    public SlotAlign EffectiveV => VAlign ?? (Height.IsFlexible ? SlotAlign.Stretch : SlotAlign.Start);
}

public enum Justify { Start, Center, End, SpaceBetween, SpaceAround, SpaceEvenly }

/// <summary>
/// Lays children out along one axis with web-like sizing (see <see cref="SizeSpec"/>): fixed and percentage
/// sizes are honoured, "auto" fits content, and unsized ("fill") or weighted ("2*") children share whatever
/// space is left. When nothing fills, the leftover space stays empty and is placed according to
/// <see cref="Justify"/>. With a single child it acts as a sizing slot for that child.
/// </summary>
public class FlexPanel : Panel
{
    public static readonly AttachedProperty<SlotSpec> SlotProperty =
        AvaloniaProperty.RegisterAttached<FlexPanel, Control, SlotSpec>("Slot", SlotSpec.Fill);

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<FlexPanel, Orientation>(nameof(Orientation), Orientation.Vertical);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FlexPanel, double>(nameof(Spacing));

    public static readonly StyledProperty<Justify> JustifyProperty =
        AvaloniaProperty.Register<FlexPanel, Justify>(nameof(Justify));

    static FlexPanel()
    {
        AffectsMeasure<FlexPanel>(OrientationProperty, SpacingProperty, JustifyProperty);
        AffectsParentMeasure<FlexPanel>(SlotProperty);
    }

    public FlexPanel() => ClipToBounds = true;

    public static SlotSpec GetSlot(Control c) => c.GetValue(SlotProperty);
    public static void SetSlot(Control c, SlotSpec value) => c.SetValue(SlotProperty, value);

    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Justify Justify { get => GetValue(JustifyProperty); set => SetValue(JustifyProperty, value); }

    private bool Horizontal => Orientation == Orientation.Horizontal;

    private SizeSpec MainSpec(Control c) => Horizontal ? GetSlot(c).Width : GetSlot(c).Height;
    private SizeSpec CrossSpec(Control c) => Horizontal ? GetSlot(c).Height : GetSlot(c).Width;
    private double Main(Size s) => Horizontal ? s.Width : s.Height;
    private double Cross(Size s) => Horizontal ? s.Height : s.Width;
    private Size Make(double main, double cross) => Horizontal ? new Size(main, cross) : new Size(cross, main);
    // Sizes and limits describe the element's border box; its margin is added outside, as in CSS.
    private double MarginMain(Control c) => Horizontal ? c.Margin.Left + c.Margin.Right : c.Margin.Top + c.Margin.Bottom;
    private double MarginCross(Control c) => Horizontal ? c.Margin.Top + c.Margin.Bottom : c.Margin.Left + c.Margin.Right;
    private (double Min, double Max) MainLimits(Control c) => Horizontal
        ? (c.MinWidth + MarginMain(c), c.MaxWidth + MarginMain(c))
        : (c.MinHeight + MarginMain(c), c.MaxHeight + MarginMain(c));
    private (double Min, double Max) CrossLimits(Control c) => Horizontal
        ? (c.MinHeight + MarginCross(c), c.MaxHeight + MarginCross(c))
        : (c.MinWidth + MarginCross(c), c.MaxWidth + MarginCross(c));
    private double? FixedMain(Control c, double available) => MainSpec(c).Resolve(available) is { } v ? v + MarginMain(c) : null;
    private double? FixedCross(Control c, double available) => CrossSpec(c).Resolve(available) is { } v ? v + MarginCross(c) : null;

    private static double Clamp(double value, (double Min, double Max) limits) =>
        Math.Max(limits.Min, Math.Min(limits.Max, value));

    private List<Control> VisibleChildren() => Children.Where(c => c.IsVisible).ToList();

    private double CrossConstraint(Control child, double availableCross)
    {
        var spec = CrossSpec(child);
        return FixedCross(child, availableCross) is { } fixedCross ? Clamp(fixedCross, CrossLimits(child)) : availableCross;
    }

    // True when the last measure had no limit along the main axis (e.g. inside a scroll viewer).
    // Unsized children then start from their content size and only grow into spare space, like
    // "flex: 1 1 auto" in CSS, instead of splitting the whole length equally.
    private bool _unbounded;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren();
        var availableMain = Main(availableSize);
        _unbounded = !double.IsFinite(availableMain);
        var availableCross = Cross(availableSize);
        var gaps = Spacing * Math.Max(0, children.Count - 1);
        double used = 0, totalWeight = 0, desiredCross = 0, desiredMain = gaps;

        foreach (var child in children)
        {
            var spec = MainSpec(child);
            if (spec.IsFlexible) { totalWeight += spec.Weight; continue; }
            var fixedMain = FixedMain(child, availableMain);
            var main = fixedMain ?? double.PositiveInfinity;
            child.Measure(Make(main, CrossConstraint(child, availableCross)));
            var size = fixedMain is { } f ? Clamp(f, MainLimits(child)) : Main(child.DesiredSize);
            used += size;
            desiredMain += size;
            desiredCross = Math.Max(desiredCross, CrossDesired(child, availableCross));
        }

        var remaining = double.IsFinite(availableMain) ? Math.Max(0, availableMain - used - gaps) : double.PositiveInfinity;
        foreach (var child in children)
        {
            var spec = MainSpec(child);
            if (!spec.IsFlexible) continue;
            var share = double.IsFinite(remaining) ? remaining * spec.Weight / totalWeight : double.PositiveInfinity;
            child.Measure(Make(share, CrossConstraint(child, availableCross)));
            desiredMain += Main(child.DesiredSize);
            desiredCross = Math.Max(desiredCross, CrossDesired(child, availableCross));
        }

        return Make(desiredMain, desiredCross);
    }

    private double CrossDesired(Control child, double availableCross) =>
        FixedCross(child, availableCross) is { } fixedCross ? Clamp(fixedCross, CrossLimits(child)) : Cross(child.DesiredSize);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren();
        var finalMain = Main(finalSize);
        var finalCross = Cross(finalSize);
        var gaps = Spacing * Math.Max(0, children.Count - 1);
        var sizes = new double[children.Count];
        double used = 0, totalWeight = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var spec = MainSpec(child);
            if (spec.IsFlexible) { totalWeight += spec.Weight; continue; }
            sizes[i] = FixedMain(child, finalMain) is { } f ? Clamp(f, MainLimits(child)) : Math.Min(Main(child.DesiredSize), finalMain);
            used += sizes[i];
        }

        var flexibleUsed = 0.0;
        if (_unbounded)
        {
            for (var i = 0; i < children.Count; i++)
                if (MainSpec(children[i]).IsFlexible) { sizes[i] = Main(children[i].DesiredSize); flexibleUsed += sizes[i]; }
            var spare = Math.Max(0, finalMain - used - flexibleUsed - gaps);
            for (var i = 0; i < children.Count && spare > 0; i++)
            {
                var spec = MainSpec(children[i]);
                if (!spec.IsFlexible) continue;
                var grown = Clamp(sizes[i] + spare * spec.Weight / totalWeight, MainLimits(children[i]));
                flexibleUsed += grown - sizes[i];
                sizes[i] = grown;
            }
        }
        else
        {
            var remaining = Math.Max(0, finalMain - used - gaps);
            for (var i = 0; i < children.Count; i++)
            {
                var spec = MainSpec(children[i]);
                if (!spec.IsFlexible) continue;
                sizes[i] = Clamp(remaining * spec.Weight / totalWeight, MainLimits(children[i]));
                flexibleUsed += sizes[i];
            }
        }

        var leftover = Math.Max(0, finalMain - used - flexibleUsed - gaps);
        var (offset, extraGap) = Distribute(leftover, children.Count);
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var crossSpec = CrossSpec(child);
            var align = Horizontal ? GetSlot(child).EffectiveV : GetSlot(child).EffectiveH;
            var crossSize = FixedCross(child, finalCross) is { } fc
                ? Clamp(fc, CrossLimits(child))
                : crossSpec.Kind != SizeKind.Auto && align == SlotAlign.Stretch
                    ? Clamp(finalCross, CrossLimits(child))
                    : Math.Min(Cross(child.DesiredSize), finalCross);
            var crossOffset = align switch
            {
                SlotAlign.Center => (finalCross - crossSize) / 2,
                SlotAlign.End => finalCross - crossSize,
                _ => 0,
            };
            // Snap both edges to whole pixels so neighbours tile exactly and the last child is not clipped.
            var start = Math.Round(offset);
            var length = Math.Max(0, Math.Min(Math.Round(offset + sizes[i]), Math.Max(finalMain, start)) - start);
            var rect = Horizontal
                ? new Rect(start, crossOffset, length, crossSize)
                : new Rect(crossOffset, start, crossSize, length);
            child.Arrange(rect);
            offset += sizes[i] + Spacing + extraGap;
        }
        return finalSize;
    }

    private (double Start, double Gap) Distribute(double leftover, int count)
    {
        if (leftover <= 0 || count == 0) return (0, 0);
        return Justify switch
        {
            Justify.Center => (leftover / 2, 0),
            Justify.End => (leftover, 0),
            Justify.SpaceBetween when count > 1 => (0, leftover / (count - 1)),
            Justify.SpaceBetween => (0, 0),
            Justify.SpaceAround => (leftover / count / 2, leftover / count),
            Justify.SpaceEvenly => (leftover / (count + 1), leftover / (count + 1)),
            _ => (0, 0),
        };
    }
}

/// <summary>
/// Free placement: each child is positioned by <see cref="XProperty"/>/<see cref="YProperty"/> (pixels or
/// percent of this panel) and sized by its <see cref="FlexPanel.SlotProperty"/>. A child without a width
/// or height extends to the right or bottom edge. Areas no child covers stay empty.
/// </summary>
public sealed class AbsolutePanel : Panel
{
    public static readonly AttachedProperty<SizeSpec> XProperty = AvaloniaProperty.RegisterAttached<AbsolutePanel, Control, SizeSpec>("X", SizeSpec.Pixels(0));
    public static readonly AttachedProperty<SizeSpec> YProperty = AvaloniaProperty.RegisterAttached<AbsolutePanel, Control, SizeSpec>("Y", SizeSpec.Pixels(0));

    static AbsolutePanel() => AffectsParentArrange<AbsolutePanel>(XProperty, YProperty);

    public AbsolutePanel() => ClipToBounds = true;

    public static void SetX(Control c, SizeSpec value) => c.SetValue(XProperty, value);
    public static void SetY(Control c, SizeSpec value) => c.SetValue(YProperty, value);

    private static (double X, double Y, double W, double H) Place(Control child, Size area)
    {
        var slot = FlexPanel.GetSlot(child);
        var x = child.GetValue(XProperty).Resolve(area.Width) ?? 0;
        var y = child.GetValue(YProperty).Resolve(area.Height) ?? 0;
        double Resolve(SizeSpec spec, double parent, double remaining, double desired, double margin) =>
            spec.Resolve(parent) is { } v ? v + margin : spec.Kind == SizeKind.Auto ? desired : Math.Max(0, remaining);
        var w = Resolve(slot.Width, area.Width, area.Width - x, child.DesiredSize.Width, child.Margin.Left + child.Margin.Right);
        var h = Resolve(slot.Height, area.Height, area.Height - y, child.DesiredSize.Height, child.Margin.Top + child.Margin.Bottom);
        return (x, y, w, h);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double right = 0, bottom = 0;
        foreach (var child in Children)
        {
            var slot = FlexPanel.GetSlot(child);
            var x = child.GetValue(XProperty).Resolve(availableSize.Width) ?? 0;
            var y = child.GetValue(YProperty).Resolve(availableSize.Height) ?? 0;
            var w = slot.Width.Resolve(availableSize.Width) + (child.Margin.Left + child.Margin.Right);
            var h = slot.Height.Resolve(availableSize.Height) + (child.Margin.Top + child.Margin.Bottom);
            child.Measure(new Size(w ?? Math.Max(0, availableSize.Width - x), h ?? Math.Max(0, availableSize.Height - y)));
            right = Math.Max(right, x + (w ?? child.DesiredSize.Width));
            bottom = Math.Max(bottom, y + (h ?? child.DesiredSize.Height));
        }
        // A canvas fills its slot; it only reports its content extent where space is unbounded (e.g. in a scroll).
        return new Size(double.IsFinite(availableSize.Width) ? 0 : right, double.IsFinite(availableSize.Height) ? 0 : bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var (x, y, w, h) = Place(child, finalSize);
            child.Arrange(new Rect(x, y, w, h));
        }
        return finalSize;
    }
}
