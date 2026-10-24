// ── Platform/Controls.Pending.cs ───────────────────────────────────────────────────────────────────────────────────
// PendingBar — the quiet stand-in for a text line whose facts have not landed
//
// Role: UI
// Owner: L
// Wave: 4 (the shared-surface plan's last twin delete)
// Budget: 60 lines
// Spec: shared-media-surface-implementation.md §4 wave 4 (the "pending bar" row)
//
// ── ONE BAR ──────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Four files had grown their own filled rounded bar for "this line is coming" — the Recents row's two-bar text column,
// the track drawer's reserved video row, the library pane's header placeholder (album and show twins) and its rows' bars.
// They differed only by a corner radius (a half-height capsule, a fixed 4, a fixed 3) and by whether the bar may shrink.
// The bar is GEOMETRY, never an invented string: it is a plain filled box with no text, no handlers and no role, so a
// screen reader reads nothing and the pointer falls through to whatever is under it.
//
// The fill is the engine's own skeleton fill (`SkeletonStyle.Default` = `Tok.FillSubtleSecondary`), so a pending bar and
// a derived-shimmer bar beside it are the same grey.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Controls
{
    /// <summary>A stand-in bar <paramref name="width"/> × <paramref name="height"/>: the skeleton grey, never shrinking (a
    /// reserved line keeps its extent), its corners the half-height capsule up to the 4-DIP control radius — a 6-high bar is
    /// a capsule, an 11-high line and a 26-high title block are both 4-radius rectangles. Returned as a <see cref="BoxEl"/>
    /// so a site can still <c>with</c> a key or an alignment onto it.</summary>
    public static BoxEl PendingBar(float width, float height) => new()
    {
        Width = width, Height = height, Shrink = 0f,
        Corners = CornerRadius4.All(MathF.Min(height / 2f, Radii.Control)), Fill = Tok.FillSubtleSecondary,
    };

    /// <summary>The floor of a text slot that must hold <paramref name="lines"/> lines whatever text lands in it (audit B7: "skeletons of the
    /// final size"): that many lines of <paramref name="style"/>'s own type (font, size, line height, stacking), unseen and never hit, as a layer of
    /// the slot's ZStack. The loading skeleton does not know the words, so a title that may wrap reserves its lines by route kind, on the skeleton
    /// and on the loaded page alike, and the words landing move nothing. The same element on the skeleton arm, drawn as is (derived, it would be
    /// bars). Lines past the reserve still grow the slot.</summary>
    public static Element LineReserve(TextEl style, int lines)
    {
        var rows = new Element[Math.Max(1, lines)];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = style with
            {
                Text = "M", MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.None, MinSize = 0f, Key = null,
                Width = float.NaN, MinWidth = 0f, MaxWidth = float.NaN, Grow = 0f, Shrink = 0f, Margin = default, AlignSelf = FlexAlign.Auto,
            };
        Element reserve = new BoxEl
        {
            Direction = 1, MinWidth = 0f, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Opacity = 0f, HitTestVisible = false,
            IsEnabled = false, Children = rows,
        };
        return reserve.Skel(reserve);
    }
}
