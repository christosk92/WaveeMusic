using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// The overlapping-avatar strip, as a SHARED primitive.
//
// ArtistFacePile (the album header's billed artists) and CollaboratorFacePile (a playlist's contributors) each hand-roll
// the identical geometry — a 28 avatar in a 2px ring, siblings pulled back 12, an "+N" frame closing the row. They stay
// as they are: both are full interactive controls (anchor handle, flyout, hydration) whose piles are one small part of a
// bigger component, and rewriting two working controls to reach a third consumer is churn, not convergence.
//
// What this file removes is the THIRD copy. The facts-panel pile is the same geometry with per-face clicks (a lens)
// and an optional overflow click (the rest of the ranking). The constants are the ArtistFacePile values verbatim
// (ArtistFacePile.cs:29 / CollaboratorFacePile.cs:22) precisely so the three piles cannot drift into three different
// overlaps.
internal static class FacePiles
{
    /// <summary>The portrait itself.</summary>
    internal const float Avatar = 28f;
    /// <summary>The ring drawn around each portrait, in the surface fill, so overlapping faces stay separable.</summary>
    internal const float Ring = 2f;
    /// <summary>The framed diameter (portrait + ring on both sides) — what the row actually reserves per face.</summary>
    internal const float Outer = Avatar + Ring * 2f;
    /// <summary>How far each face after the first is pulled back over its predecessor.</summary>
    internal const float Overlap = 12f;
    /// <summary>Advance per extra frame: Outer minus the overlap. Width of n frames is
    /// <c>Outer + (n - 1) * Step</c>.</summary>
    internal const float Step = Outer - Overlap;
    /// <summary>The house default: four faces then a count. Callers whose surface was designed around a different
    /// number (the facts panel's five) pass their own — the GEOMETRY is what must not vary, not the census.</summary>
    internal const int MaxVisible = 4;

    /// <summary>How many overlapping frames (portraits and an optional overflow) fit in <paramref name="width"/>.
    /// Unmeasured / narrower than one frame → 1, never 0.</summary>
    internal static int SlotsIn(float width)
    {
        if (width < Outer) return 1;
        return 1 + (int)MathF.Floor((width - Outer) / Step);
    }

    /// <summary>How many portraits to paint for <paramref name="total"/> people in <paramref name="width"/>.
    /// When anyone would clip, one slot stays the "+N" frame so the strip never overflows.</summary>
    internal static int VisibleFaces(float width, int total)
    {
        if (total <= 0) return 0;
        int slots = SlotsIn(width);
        if (total <= slots) return total;
        return Math.Max(1, Math.Min(slots - 1, total - 1));
    }

    /// <summary>One person in a pile: the display name (initials fallback + accessible label) and a portrait url, which
    /// is null far more often than not — a pile must read correctly from names alone.
    ///
    /// <para><paramref name="OnClick"/> makes the face an affordance rather than an illustration — the Liked facts pile
    /// lenses the track list to that artist. Null (the default) is a pile that is purely a picture: no hand cursor, no
    /// focus stop, no press feedback, exactly as before. <paramref name="Selected"/> rings the face in accent while its
    /// lens is the one that is on, and <paramref name="Tip"/> is the tooltip a portrait otherwise cannot carry (a face
    /// is not a label; without one an overlapped avatar names nobody).</para></summary>
    internal readonly record struct Face(string Name, string? ImageUrl, Action? OnClick = null, bool Selected = false,
                                         string? Tip = null);

    /// <summary>The strip: up to <paramref name="maxVisible"/> framed portraits, then a "+N" frame.
    /// <paramref name="overflow"/> defaults to what the list itself carries beyond the visible cut; pass it explicitly
    /// when the count comes from somewhere the caller can see and this list cannot (ArtistFacePile's track-only
    /// contributors are that case). <paramref name="onOverflow"/> makes the "+N" frame a button (the facts card's
    /// "see the rest" flyout); null is a count, not a control. <paramref name="overflowTip"/> labels that button.
    /// An empty list renders NOTHING — an empty ring is not a face pile.</summary>
    internal static Element Strip(IReadOnlyList<Face> faces, int maxVisible = MaxVisible, int? overflow = null,
                                  Action? onOverflow = null, string? overflowTip = null)
    {
        if (faces is null || faces.Count == 0) return new BoxEl();

        int visible = Math.Min(Math.Max(1, maxVisible), faces.Count);
        int extra = Math.Max(0, overflow ?? faces.Count - visible);
        var kids = new Element[visible + (extra > 0 ? 1 : 0)];
        // Keyed BY POSITION, not by name: two credits can share a display name (and a nameless one has no key at all),
        // and a slot that keeps its identity across a recount updates its portrait in place instead of remounting.
        for (int i = 0; i < visible; i++) kids[i] = AvatarFrame(faces[i], i, i == 0);
        if (extra > 0) kids[visible] = OverflowFrame(extra, visible == 0, onOverflow, overflowTip);
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids };
    }

    static Element AvatarFrame(Face f, int index, bool first)
    {
        bool live = f.OnClick is not null;
        // The RING is the frame's own fill (that is what makes overlapping faces separable), so the selected state is
        // painted by swapping that ring to accent — no extra node, and the face keeps its exact geometry whether it is
        // chosen, hovered or inert. A face that is not an affordance keeps every one of those channels at rest.
        Element frame = new BoxEl
        {
            Key = "face:" + index,
            Width = Outer, Height = Outer, Shrink = 0f, Corners = CornerRadius4.All(Outer / 2f),
            Fill = f.Selected ? Tok.AccentDefault : Tok.FillSolidBase, Padding = Edges4.All(Ring),
            Margin = new Edges4(first ? 0f : -Overlap, 0f, 0f, 0f),
            Role = live ? AutomationRole.Button : AutomationRole.None,
            Focusable = live,
            Cursor = live ? CursorId.Hand : CursorId.Arrow,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            HoverFill = !live ? ColorF.Transparent : f.Selected ? Tok.AccentSecondary : Tok.AccentSubtle,
            HoverScale = live ? WaveeMotion.ScaleStandard.Hover : 1f,
            PressScale = live ? WaveeMotion.ScaleStandard.Press : 1f,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = f.OnClick,
            Children = [PersonPicture.Create("", Avatar, displayName: f.Name, imageSourcePath: f.ImageUrl)],
        };
        // The negative margin stays on the FRAME, under the tooltip wrapper: a flex item's outer size includes its
        // margins, so the wrapper shrink-wraps to (Outer - Overlap) and paints the frame Overlap to the left of its own
        // origin — the same overlap the unwrapped pile has, reached without a Margin on the wrapper (Element carries
        // none) and without a second geometry to keep in step.
        return f.Tip is { Length: > 0 } tip
            ? ToolTip.Wrap(frame, tip, showDelayMs: LikedLens.TipDelayMs) with { Key = "face:" + index }
            : frame;
    }

    static Element OverflowFrame(int n, bool first, Action? onClick, string? tip)
    {
        bool live = onClick is not null;
        Element frame = new BoxEl
        {
            Key = "face:more",
            Width = Outer, Height = Outer, Shrink = 0f, Corners = CornerRadius4.All(Outer / 2f),
            Fill = Tok.FillSolidBase, Padding = Edges4.All(Ring),
            Margin = new Edges4(first ? 0f : -Overlap, 0f, 0f, 0f),
            Role = live ? AutomationRole.Button : AutomationRole.None,
            Focusable = live,
            Cursor = live ? CursorId.Hand : CursorId.Arrow,
            FocusVisualMargin = live ? new Edges4(1f, 1f, 1f, 1f) : default,
            HoverFill = live ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverScale = live ? WaveeMotion.ScaleStandard.Hover : 1f,
            PressScale = live ? WaveeMotion.ScaleStandard.Press : 1f,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Width = Avatar, Height = Avatar, Corners = CornerRadius4.All(Avatar / 2f),
                    Fill = Tok.FillCardDefault, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl("+" + n) { Size = 10f, Weight = 700, Color = Tok.TextSecondary }],
                },
            ],
        };
        return tip is { Length: > 0 }
            ? ToolTip.Wrap(frame, tip, showDelayMs: LikedLens.TipDelayMs) with { Key = "face:more" }
            : frame;
    }
}
