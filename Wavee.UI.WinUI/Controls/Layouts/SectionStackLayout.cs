using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// Virtualizing vertical layout for page-level section lists (HomePage and friends)
/// that acts as a layout firewall: caches each section's measured height and only
/// updates the cache when the delta exceeds <see cref="CacheEpsilon"/>. Transient
/// <c>DesiredSize</c> hiccups inside a section — the kind that otherwise cascade
/// into layout cycles — don't change the reported extent, so the outer page stays
/// stable.
/// </summary>
/// <remarks>
/// <para>
/// Use in place of <see cref="StackLayout"/> (vertical) when the items are complex
/// nested layouts (other repeaters, cards with async content, etc.) and the outer
/// page is reporting <c>LayoutCycleException</c> or visible flicker on scroll.
/// </para>
/// <para>
/// Items outside <see cref="VirtualizingLayoutContext.RealizationRect"/> are not
/// realized. Their positions are computed from cached heights plus an
/// <see cref="EstimatedItemHeight"/> fallback for never-measured indices.
/// </para>
/// </remarks>
public sealed class SectionStackLayout : VirtualizingLayout
{
    /// <summary>
    /// Deltas below this threshold are treated as layout noise and don't update the
    /// height cache. 1 px is enough to suppress subpixel rounding oscillation while
    /// still picking up real content changes.
    /// </summary>
    private const double CacheEpsilon = 1.0;

    /// <summary>
    /// Fallback height used for items that haven't been measured yet. Better a
    /// plausible guess than 0, which would put all unmeasured items at the same
    /// Y and upset the realization math.
    /// </summary>
    private const double EstimatedItemHeight = 400.0;

    /// <summary>
    /// Extra pixels on top and bottom of <see cref="VirtualizingLayoutContext.RealizationRect"/>
    /// in which we still keep items realized. Home sections (baseline cards, shelves)
    /// are expensive to re-instantiate; generous buffer avoids the "flash in / flash out"
    /// effect on vertical scroll. Cost is memory for a few extra realized sections —
    /// acceptable for a page that never has more than ~30 sections total.
    /// </summary>
    private const double RealizationBufferPx = 800.0;

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(SectionStackLayout),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SectionStackLayout layout)
            layout.InvalidateMeasure();
    }

    // Parallel lists indexed by item position. NaN in _heights means "not yet
    // measured" so we can distinguish a legitimately 0-height section from an
    // unknown one. _identities tracks WHICH section's height is cached at each
    // slot so an ApplyDiff Move (count unchanged, but the data identity at
    // index i now points to a different section) re-measures only the affected
    // slot — without it, a 200 px artist row's cached height would persist at
    // the slot of a 400 px baseline section that just moved away.
    private readonly List<double> _heights = new();
    private readonly List<double> _offsets = new();
    private readonly List<WeakReference<object>?> _identities = new();
    private int _lastItemCount = -1;
    private double _lastAvailableWidth = double.NaN;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        try
        {
            var count = context.ItemCount;
            if (count == 0)
            {
                _heights.Clear();
                _offsets.Clear();
                _identities.Clear();
                _lastItemCount = 0;
                _lastAvailableWidth = availableSize.Width;
                return new Size(0, 0);
            }

            var spacing = Math.Max(0, Spacing);
            var width = availableSize.Width;
            var widthChanged = !AreClose(width, _lastAvailableWidth);

            // Reset cache when the item count changes (indices shift) or the
            // available width changes (all heights are now stale because flow
            // layout may re-wrap inside sections).
            if (count != _lastItemCount || widthChanged)
            {
                _heights.Clear();
                _offsets.Clear();
                _identities.Clear();
                _lastItemCount = count;
                _lastAvailableWidth = width;
            }

            // Grow cache to current count. NaN = never measured.
            while (_heights.Count < count) _heights.Add(double.NaN);
            while (_offsets.Count < count) _offsets.Add(0);
            while (_identities.Count < count) _identities.Add(null);

            var realization = context.RealizationRect;
            var realizeTop = realization.Top - RealizationBufferPx;
            var realizeBottom = realization.Bottom + RealizationBufferPx;
            double y = 0;
            var measureSize = new Size(width, double.PositiveInfinity);

            for (int i = 0; i < count; i++)
            {
                _offsets[i] = y;

                // Identity check: if the data item at slot i differs from the
                // one we last measured here (an ApplyDiff Move shuffled the
                // sections without changing the count), invalidate the cached
                // height for this slot so it re-measures fresh. context.GetItemAt
                // can throw mid-mutation — fall through to the existing path
                // and let the outer try/catch handle it.
                try
                {
                    var currentItem = context.GetItemAt(i);
                    if (currentItem is not null)
                    {
                        var prev = _identities[i];
                        object? prevItem = null;
                        prev?.TryGetTarget(out prevItem);
                        if (prev is not null && !ReferenceEquals(prevItem, currentItem))
                            _heights[i] = double.NaN;
                        if (prev is null || !ReferenceEquals(prevItem, currentItem))
                            _identities[i] = new WeakReference<object>(currentItem);
                    }
                }
                catch (ArgumentException)
                {
                    // Mid-mutation read — fall through, NaN-on-mismatch will
                    // come on the next pass when the collection is stable.
                }

                var cached = _heights[i];
                var height = double.IsNaN(cached) ? EstimatedItemHeight : cached;

                var itemTop = y;
                var itemBottom = y + height;
                var inRealization = itemBottom >= realizeTop && itemTop <= realizeBottom;

                // Only realize sections near the viewport. Unknown offscreen
                // sections keep EstimatedItemHeight until the user approaches
                // them; otherwise the first layout pass instantiates every
                // section just to warm the height cache.
                if (inRealization)
                {
                    var child = context.GetOrCreateElementAt(i);
                    child.Measure(measureSize);
                    var desired = child.DesiredSize.Height;
                    if (double.IsNaN(desired) || double.IsInfinity(desired) || desired < 0)
                        desired = height;

                    // First measurement or a real change — update the cache.
                    // Deltas within epsilon are ignored to break the oscillation
                    // path described in the fix plan.
                    if (double.IsNaN(cached) || Math.Abs(desired - cached) > CacheEpsilon)
                    {
                        _heights[i] = desired;
                        height = desired;
                    }
                }

                y += height;
                if (i < count - 1) y += spacing;
            }

            return new Size(width, y);
        }
        catch (ArgumentException)
        {
            // WinUI can throw during collection mutations — let the next pass
            // run fresh with reset cache.
            _heights.Clear();
            _offsets.Clear();
            _identities.Clear();
            _lastItemCount = -1;
            _lastAvailableWidth = double.NaN;
            return new Size(availableSize.Width, 0);
        }
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        try
        {
            var count = context.ItemCount;
            if (count == 0) return finalSize;

            var realization = context.RealizationRect;
            var realizeTop = realization.Top - RealizationBufferPx;
            var realizeBottom = realization.Bottom + RealizationBufferPx;

            for (int i = 0; i < count && i < _offsets.Count && i < _heights.Count; i++)
            {
                var top = _offsets[i];
                var cached = _heights[i];
                var height = double.IsNaN(cached) ? EstimatedItemHeight : cached;

                // Arrange only items whose realization window (with buffer)
                // overlaps the viewport — must match the MeasureOverride rule.
                if (top + height < realizeTop || top > realizeBottom)
                    continue;

                var child = context.GetOrCreateElementAt(i);
                child.Arrange(new Rect(0, top, finalSize.Width, height));
            }
        }
        catch (ArgumentException)
        {
            // Same defensive posture as MeasureOverride.
        }

        return finalSize;
    }

    private static bool AreClose(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b);
        return Math.Abs(a - b) < 0.5;
    }
}
