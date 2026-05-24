using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Wavee.UI.WinUI.Controls.Reorder;

/// <summary>
/// Shared drag-reorder insertion indicator — the 2&#160;px accent line that
/// appears between rows showing where a dragged item will land. Used by the
/// queue and <c>TrackDataGrid</c>.
///
/// <para>Pure geometry + visuals: the host walks its own realized rows into
/// <see cref="RowBounds"/> (container types differ — <c>ItemsView</c> vs
/// <c>ListView</c>), then calls <see cref="ResolveSlotIndex"/> / <see cref="Show"/>
/// / <see cref="Hide"/>. The "make space + ghost" preview is NOT done here — that
/// is the host's job (it inserts a real placeholder row into its own collection
/// so the list control animates the gap natively).</para>
/// </summary>
public sealed class ReorderDropIndicator
{
    /// <summary>One realized row's bounds (in the overlay Canvas's coordinate
    /// space), the element, and its model index.</summary>
    public readonly record struct RowBounds(UIElement Element, double Top, double Height, int ModelIndex);

    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(140);
    private const double FaintOpacity = 0.5;
    private const double ActiveOpacity = 1.0;
    private const float ActiveScaleY = 1.6f;
    private const double ThicknessPx = 2.0;

    private readonly Canvas _overlay;
    private readonly List<Rectangle> _lines = new();

    public ReorderDropIndicator(Canvas overlay) => _overlay = overlay;

    /// <summary>
    /// The gap index the pointer is over — slot 0 is before the first row, slot
    /// <c>itemCount</c> is after the last. A hovered row resolves to above or
    /// below its own midpoint; outside every row it snaps to the nearest gap.
    /// </summary>
    public static int ResolveSlotIndex(double pointerY, IReadOnlyList<RowBounds> rows, int itemCount)
    {
        RowBounds? hit = null;
        RowBounds? nearest = null;
        var nearestDist = double.PositiveInfinity;

        foreach (var r in rows)
        {
            var bottom = r.Top + r.Height;
            if (hit is null && pointerY >= r.Top && pointerY < bottom)
                hit = r;

            var dist = pointerY < r.Top ? r.Top - pointerY
                : pointerY > bottom ? pointerY - bottom
                : 0;
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = r;
            }
        }

        if (hit is { } h)
            return (pointerY - h.Top) >= h.Height / 2 ? h.ModelIndex + 1 : h.ModelIndex;
        if (nearest is { } n)
            return pointerY >= n.Top + n.Height ? n.ModelIndex + 1 : n.ModelIndex;
        return itemCount;
    }

    /// <summary>
    /// Renders a faint line at every realized-row gap and a bright, Y-scaled line
    /// at <paramref name="activeSlot"/>. The end-of-list line is shown only when
    /// the last realized row is the last item (otherwise it would float
    /// misleadingly inside a virtualized region).
    /// </summary>
    public void Show(int activeSlot, IReadOnlyList<RowBounds> rows, int itemCount, double hostWidth)
    {
        var slots = new List<(int Index, double Y)>(rows.Count + 1);
        var lastBottom = double.NegativeInfinity;
        var lastModelIndex = -1;

        foreach (var r in rows)
        {
            slots.Add((r.ModelIndex, r.Top));
            var bottom = r.Top + r.Height;
            if (bottom > lastBottom)
            {
                lastBottom = bottom;
                lastModelIndex = r.ModelIndex;
            }
        }

        if (itemCount > 0 && lastModelIndex == itemCount - 1)
            slots.Add((itemCount, lastBottom));

        foreach (var line in _lines)
        {
            line.Opacity = 0;
            line.Scale = new Vector3(1f, 1f, 1f);
        }

        for (var i = 0; i < slots.Count; i++)
        {
            var (slotIndex, y) = slots[i];
            var rect = Acquire(i);
            rect.Width = hostWidth;
            Canvas.SetLeft(rect, 0);
            Canvas.SetTop(rect, y - ThicknessPx / 2);
            rect.CenterPoint = new Vector3((float)(hostWidth / 2), (float)(ThicknessPx / 2), 0);

            var isActive = slotIndex == activeSlot;
            rect.Opacity = isActive ? ActiveOpacity : FaintOpacity;
            rect.Scale = isActive ? new Vector3(1f, ActiveScaleY, 1f) : new Vector3(1f, 1f, 1f);
        }
    }

    /// <summary>Parks every indicator line off-screen (opacity 0).</summary>
    public void Hide()
    {
        foreach (var line in _lines)
        {
            line.Opacity = 0;
            line.Scale = new Vector3(1f, 1f, 1f);
        }
    }

    private Rectangle Acquire(int index)
    {
        while (_lines.Count <= index)
        {
            var rect = new Rectangle
            {
                Height = ThicknessPx,
                RadiusX = 1,
                RadiusY = 1,
                Opacity = 0,
                IsHitTestVisible = false,
                Fill = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush,
                OpacityTransition = new ScalarTransition { Duration = TransitionDuration },
                ScaleTransition = new Vector3Transition { Duration = TransitionDuration },
            };
            _overlay.Children.Add(rect);
            _lines.Add(rect);
        }
        return _lines[index];
    }
}
