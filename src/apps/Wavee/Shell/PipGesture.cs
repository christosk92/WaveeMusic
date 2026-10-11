// ── Shell/PipGesture.cs ────────────────────────────────────────────────────────────────────────────────────────────────
// the in-window mini player's geometry, as pure rules: the constants and the fit (`Pip`), the drag / resize / clamp /
// layout reservation (`PipGesture`), the overlay's placement per surface (`PipOverlay`), and the BOUNDS all of them
// work inside (`PipBounds`)
//
// Role: CORE (`System` only: no signal, no engine type, no scene)
// Spec: docs/themes/plan/technical-plan.md §5.1 C6 (X16); ch 24 W8/W11/W12
//
// MOVED out of `Video.cs` (the `Pip`, `PipEdge` and `PipGesture` blocks, verbatim) plus two additions:
//
//   · `PipBounds` — the minimum size, the player-bar and chrome reserves and the edge margin as a VALUE. Every rule that
//     used a constant now has an overload taking the bounds; the original signatures are kept and answer for
//     `PipBounds.Classic` (0.2.9's numbers, which a rect restored from a 0.2.9 profile must still land on). A theme's tier
//     policy (T3) will hand its own bounds instead of editing the constants.
//   · `PipOverlay` — what `Video.UI.cs`'s `PipLayer` switched over in three private methods: the overlay surface's width,
//     height and offset per resolved placement (fullscreen fills the window, docked follows the reservation, the
//     floating card is clamped inside the viewport).

namespace Wavee;

public static partial class Video
{
    // ── the in-window mini player's geometry ────────────────────────────────────────────────────────────────────────

    /// <summary>The PiP's constants and its fit. The numbers are 0.2.9's verbatim, including the deliberately-inexact
    /// default (<see cref="DefaultW"/>×<see cref="DefaultH"/> is 0.5611, not 9/16 = 0.5625): a rect restored from a
    /// 0.2.9 profile must land identically, so the 0.25 % is kept rather than "corrected".</summary>
    public static class Pip
    {
        public const float DefaultW = 360f, DefaultH = 202f;
        public const float MinW = 240f, MinH = 135f;
        /// <summary>The resize hit bands. Corner rows are 12 tall while the edge bands stay 6, so the bottom band
        /// grazes only the transport's 8-DIP bottom padding. The chrome strip is inset by <see cref="CornerW"/>
        /// left/right and <see cref="EdgeBand"/> top so neither the drag surface nor the ✕ sits under a resize band —
        /// a ✕ whose corner is stolen by the NE zone is the classic overlay bug.</summary>
        public const float EdgeBand = 6f, CornerW = 14f, CornerH = 12f;
        /// <summary>The viewport margins the fit reserves (the player bar below, the chrome above).</summary>
        public const float ReserveBottom = 72f, ReserveTop = 32f;
        /// <summary>The no-report fallback ratio — <see cref="DefaultH"/>/<see cref="DefaultW"/>, NOT 9/16.</summary>
        public const float FallbackRatio = DefaultH / DefaultW;

        /// <summary>The card's height for a given width and source ratio, with BOTH a floor and a CEILING. Drop the
        /// free term and a 9:16 portrait source in a 360-wide mini player asks for 640 DIP of height in a 900-tall
        /// window and the card walks off the bottom. The floor wins the tie, so a very short window still gets a
        /// 135-tall card rather than a degenerate one.</summary>
        public static float FitHeight(float w, float ratio, float viewportH) => FitHeight(w, ratio, viewportH, PipBounds.Classic);

        /// <summary><see cref="FitHeight(float, float, float)"/> inside explicit <paramref name="bounds"/>.</summary>
        public static float FitHeight(float w, float ratio, float viewportH, in PipBounds bounds)
        {
            float r = ratio > 0f && float.IsFinite(ratio) ? ratio : FallbackRatio;
            float free = MathF.Max(bounds.MinH, viewportH - bounds.ReserveBottom - bounds.ReserveTop);
            return MathF.Max(bounds.MinH, MathF.Min(w * r, free));
        }

        /// <summary>Clamp a placed rect back inside the viewport — what a window resize re-applies so a card parked at
        /// the old bottom-right does not end up off-screen.</summary>
        public static void ClampToViewport(ref float x, ref float y, float w, float h, float viewportW, float viewportH)
        {
            x = MathF.Max(0f, MathF.Min(x, MathF.Max(0f, viewportW - w)));
            y = MathF.Max(0f, MathF.Min(y, MathF.Max(0f, viewportH - h)));
        }

        /// <summary>The anchored resting position: bottom-right, above the player bar. While the card is not yet
        /// user-placed this is re-derived from the viewport every frame, which is what makes it track the corner.</summary>
        public static (float X, float Y) Anchor(float w, float h, float viewportW, float viewportH)
            => Anchor(w, h, viewportW, viewportH, PipBounds.Classic);

        /// <summary><see cref="Anchor(float, float, float, float)"/> inside explicit <paramref name="bounds"/>.</summary>
        public static (float X, float Y) Anchor(float w, float h, float viewportW, float viewportH, in PipBounds bounds)
            => (MathF.Max(0f, viewportW - w - bounds.Margin), MathF.Max(0f, viewportH - h - bounds.ReserveBottom - bounds.Margin));
    }

    /// <summary>The numbers the mini player works inside, as a value: its floor size, the space it keeps clear below (the
    /// player bar) and above (the chrome), and the gap it keeps from every window edge. <see cref="Classic"/> is the shipped
    /// set; a theme's tier policy supplies its own.</summary>
    /// <param name="MinW">The narrowest the card may be resized to.</param>
    /// <param name="MinH">The shortest the card may be resized or fitted to.</param>
    /// <param name="ReserveBottom">The viewport space kept clear below the card (the player bar).</param>
    /// <param name="ReserveTop">The viewport space the fit keeps clear above (the chrome).</param>
    /// <param name="Margin">The gap kept from every window edge.</param>
    public readonly record struct PipBounds(float MinW, float MinH, float ReserveBottom, float ReserveTop, float Margin)
    {
        /// <summary>0.2.9's numbers (<see cref="Pip.MinW"/>, <see cref="Pip.MinH"/>, <see cref="Pip.ReserveBottom"/>,
        /// <see cref="Pip.ReserveTop"/>, <see cref="PipGesture.Margin"/>).</summary>
        public static readonly PipBounds Classic = new(Pip.MinW, Pip.MinH, Pip.ReserveBottom, Pip.ReserveTop, PipGesture.Margin);
    }

    // ── the mini player's gestures ──────────────────────────────────────────────────────────────────────────────────
    //
    // 0.2.9 kept these inside `InWindowVideoPip` / `WaveeShell`; stage B lifted them here so `Video.UI.cs` decides
    // nothing. Values are 0.2.9's verbatim (ch 24 W8/W11/W12, `InWindowVideoPip.cs:452-534`, `WaveeShell.cs:793-807`).

    /// <summary>Which edges a resize gesture drags; the anchored edges are the ones NOT named. Two edges at once is a
    /// corner, so one handler covers all eight zones.</summary>
    [Flags]
    public enum PipEdge : byte { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    /// <summary>The in-window mini player's drag, resize, clamp and layout reservation.</summary>
    public static class PipGesture
    {
        /// <summary>The gap kept from every window edge (and above the player bar).</summary>
        public const float Margin = 16f;

        /// <summary>A card may sit no closer than <see cref="Margin"/> to either side.</summary>
        public static float ClampX(float x, float viewportW, float w) => ClampX(x, viewportW, w, PipBounds.Classic);

        /// <summary><see cref="ClampX(float, float, float)"/> inside explicit <paramref name="bounds"/>.</summary>
        public static float ClampX(float x, float viewportW, float w, in PipBounds bounds)
            => Math.Clamp(x, bounds.Margin, MathF.Max(bounds.Margin, viewportW - w - bounds.Margin));

        /// <summary>…and no closer than <see cref="Margin"/> to the top or to the player bar.</summary>
        public static float ClampY(float y, float viewportH, float h) => ClampY(y, viewportH, h, PipBounds.Classic);

        /// <summary><see cref="ClampY(float, float, float)"/> inside explicit <paramref name="bounds"/>.</summary>
        public static float ClampY(float y, float viewportH, float h, in PipBounds bounds)
            => Math.Clamp(y, bounds.Margin, MathF.Max(bounds.Margin, viewportH - h - bounds.ReserveBottom - bounds.Margin));

        /// <summary>The bottom space the page keeps clear: the card's height + the gap while it sits ANCHORED, 0 once the
        /// user has placed it (a deliberately placed card is a free-floating overlay) or when it is not mounted.</summary>
        public static float Reserve(bool mounted, bool placed, float h) => Reserve(mounted, placed, h, PipBounds.Classic);

        /// <summary><see cref="Reserve(bool, bool, float)"/> inside explicit <paramref name="bounds"/>.</summary>
        public static float Reserve(bool mounted, bool placed, float h, in PipBounds bounds) => mounted && !placed ? h + bounds.Margin : 0f;

        /// <summary>Should the card's HEIGHT follow the content? Always until the user sizes it; after that only when the
        /// content's own shape changes by more than 0.01 (the user owns the width, the content owns the shape).</summary>
        public static bool ShouldRefit(bool userSized, float fittedRatio, float contentRatio)
            => !userSized || (fittedRatio > 0f && MathF.Abs(contentRatio - fittedRatio) > 0.01f);

        /// <summary>One resize sample. The ANCHORED edge is the one not being dragged: growing right/bottom is bounded by
        /// the viewport, growing left/top by the frozen far edge. Floors are <see cref="Pip.MinW"/> × <see cref="Pip.MinH"/>.</summary>
        public static (float X, float Y, float W, float H) Resize(PipEdge edge, float startX, float startY, float startW,
            float startH, float dx, float dy, float viewportW, float viewportH)
            => Resize(edge, startX, startY, startW, startH, dx, dy, viewportW, viewportH, PipBounds.Classic);

        /// <summary><see cref="Resize(PipEdge, float, float, float, float, float, float, float, float)"/> inside explicit
        /// <paramref name="bounds"/>.</summary>
        public static (float X, float Y, float W, float H) Resize(PipEdge edge, float startX, float startY, float startW,
            float startH, float dx, float dy, float viewportW, float viewportH, in PipBounds bounds)
        {
            float x = startX, y = startY, w = startW, h = startH;
            float right = startX + startW, bottom = startY + startH;
            if ((edge & PipEdge.Left) != 0)
            {
                w = Math.Clamp(startW - dx, bounds.MinW, MathF.Max(bounds.MinW, right - bounds.Margin));
                x = right - w;
            }
            else if ((edge & PipEdge.Right) != 0)
            {
                w = Math.Clamp(startW + dx, bounds.MinW, MathF.Max(bounds.MinW, viewportW - bounds.Margin - startX));
            }
            if ((edge & PipEdge.Top) != 0)
            {
                h = Math.Clamp(startH - dy, bounds.MinH, MathF.Max(bounds.MinH, bottom - bounds.Margin));
                y = bottom - h;
            }
            else if ((edge & PipEdge.Bottom) != 0)
            {
                h = Math.Clamp(startH + dy, bounds.MinH, MathF.Max(bounds.MinH, viewportH - bounds.Margin - bounds.ReserveBottom - startY));
            }
            return (x, y, w, h);
        }
    }

    // ── the overlay surface's placement ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Where the overlay surface sits: the identity (fullscreen fills the window from the origin) or a translation.</summary>
    public readonly record struct OverlayOffset(bool Identity, float X, float Y);

    /// <summary>What the one overlay surface (the stay-mounted presenter that hosts the picture for every non-detached
    /// placement) is sized and placed to, per RESOLVED placement: fullscreen fills the window, docked follows the docked
    /// reservation's slot, and anything else is the floating card — clamped inside the viewport, parked at the anchored
    /// corner until the user has placed it.</summary>
    public static class PipOverlay
    {
        public static float Width(SurfacePlacement resolved, float viewportW, float pipW, float slotW) => resolved switch
        {
            SurfacePlacement.Fullscreen => viewportW,
            SurfacePlacement.Docked => slotW,
            _ => pipW,
        };

        public static float Height(SurfacePlacement resolved, float viewportH, float pipH, float slotH) => resolved switch
        {
            SurfacePlacement.Fullscreen => viewportH,
            SurfacePlacement.Docked => slotH,
            _ => pipH,
        };

        public static OverlayOffset Offset(SurfacePlacement resolved, float viewportW, float viewportH, bool placed,
            float x, float y, float w, float h, float slotX, float slotY)
            => Offset(resolved, viewportW, viewportH, placed, x, y, w, h, slotX, slotY, PipBounds.Classic);

        /// <summary><see cref="Offset(SurfacePlacement, float, float, bool, float, float, float, float, float, float)"/> inside
        /// explicit <paramref name="bounds"/>.</summary>
        public static OverlayOffset Offset(SurfacePlacement resolved, float viewportW, float viewportH, bool placed,
            float x, float y, float w, float h, float slotX, float slotY, in PipBounds bounds)
        {
            if (resolved == SurfacePlacement.Fullscreen) return new OverlayOffset(true, 0f, 0f);
            if (resolved == SurfacePlacement.Docked) return new OverlayOffset(false, slotX, slotY);
            var (ax, ay) = placed ? (x, y) : Pip.Anchor(w, h, viewportW, viewportH, in bounds);
            return new OverlayOffset(false, PipGesture.ClampX(ax, viewportW, w, in bounds), PipGesture.ClampY(ay, viewportH, h, in bounds));
        }
    }
}
