// ── Entities/Artist.Bleed.cs — EXPERIMENTAL (on): the artist hero photo as one image from the window top ─────────────────
//
// Role: CORE (every rule is pure) plus a UI note: the shell draws the photo (Shell.Masthead.UI.cs MaterialLayerView, behind
//       the title bar and the Zune band) and the content card's top region fades out over it (Shell.UI.cs ContentRegion).
// EXPERIMENTAL: the experiment is ON. Revert = set `ArtistBleed.Enabled = false` (the ONE switch; its one read, in Artist.Page.cs,
//       then publishes nothing, so the strip and the clip in the shell have height 0 and the page is today's page).
//
// WHAT THIS IS. On an artist page with a header photo and tinted surfaces on, the photo runs from the window top, behind the
// title bar and the Zune pivots and row 2 (or the Classic/Library title bar), down to the hero's end. The page publishes it
// as DATA through the shell material channel (`ShellBackdrop`, carried by `ShellMaterial.Publish` on the tint's ownership
// outcome, so a successor's claim always drops it). Only the card's TOP region changes: its fill and its top/left stroke over
// the hero fade out and return as the hero scrolls away; the solid fill below the hero stays, its top edge riding the hero's
// bottom through a paint-only translation. The card rect never changes, so nothing here lays anything out per scroll.
//
// ONE FIELD, ONE FRAME. The shell draws the photo, a DARK top scrim and the hero's horizontal veil as one field; the card's own
// photo and veil are drawn at exactly the same frame, crop and colour (<see cref="FrameFor"/>), so the card layers yield to the
// shell's with no seam, no content shift and no dip. The field is dark in both themes (<see cref="FieldBase"/>).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

public static class ArtistBleed
{
    /// <summary>The experiment's switch: ON. Read in ONE place (<c>ArtistPage.Render</c>); setting this to false reverts the whole
    /// experiment and leaves no behavioural trace.</summary>
    public const bool Enabled = true;

    /// <summary>The bleed field's base colour. The field (photo, scrim, veil) is DARK in both themes, so the chrome and the hero
    /// copy over it read as light-on-dark everywhere.</summary>
    public static ColorF FieldBase => Tok.MediaStage;

    /// <summary>The scrim's strongest alpha, at the window top (over <see cref="FieldBase"/>), the same in both themes.</summary>
    public const float ScrimTop = 0.62f;

    /// <summary>The bleed applies: the experiment is on, tinted surfaces are on (the photo is part of the tinted material), and
    /// the artist has a header photo (an avatar-only artist keeps today's hero).</summary>
    public static bool Applies(bool enabled, WashLevel surfaces, string? headerUrl)
        => enabled && surfaces != WashLevel.Off && headerUrl is { Length: > 0 };

    /// <summary>How much of the photo (and how little of the card's top fill) shows at <paramref name="offset"/>: 1 until the
    /// hero's own expanded fade starts (<see cref="ArtistHeroLayout.ExpandedFadeStart"/>, the same number the hero's Fade
    /// channel reads, so the photo holds exactly as long as today's hero does), eased to 0 at <paramref name="collapseDistance"/>.
    /// Clamped both ways (an overscroll is 1).</summary>
    public static float HeroVisible(double offset, float collapseDistance)
    {
        float start = ArtistHeroLayout.ExpandedFadeStart(collapseDistance);
        if (offset <= start) return 1f;
        if (collapseDistance <= start || offset >= collapseDistance) return 0f;
        float t = (float)((offset - start) / (collapseDistance - start));
        return 1f - Easings.Ease(Easing.FluentStandard, Math.Clamp(t, 0f, 1f));
    }

    /// <summary>The hero's presented bottom in card coordinates: it rises 1:1 with the scroll until the collapsed remnant
    /// (<paramref name="floor"/>, A2's latched floor). The solid ground's top edge rides this.</summary>
    public static float HeroBottom(double offset, float heroH, float floor) => MathF.Max(floor, heroH - (float)offset);

    /// <summary>Where the solid ground's top edge sits inside the photo's clip: the hero's presented bottom, never below the
    /// photo's own end (past it the ground below the clip already stands). The strip above it and the riser below it TILE the
    /// clip at this line, so the translucent card fill is never drawn twice.</summary>
    public static float RiserTop(double offset, float heroH, float floor, float photoH)
        => MathF.Min(MathF.Max(0f, photoH), HeroBottom(offset, heroH, floor));

    /// <summary>The line snapped to a device pixel, so the strip's and the riser's anti-aliased edges meet without a hairline.</summary>
    public static float SnapToPixel(float v, float scale) => scale > 0f ? MathF.Round(v * scale) / scale : v;

    /// <summary>The strip's translation: its bottom edge (a clip-tall box shifted up by this) lands on <paramref name="riserTop"/>.</summary>
    public static float StripShift(float riserTop, float photoH) => riserTop - photoH;

    /// <summary>The photo's vertical translation: the hero slides up with the scroll and its media is parallaxed back by
    /// <see cref="ArtistHeroLayout.PhotoParallaxFraction"/>, so the photo nets <c>1 - fraction</c> of the scroll. A top overpan
    /// (a negative offset) is clamped: the hero stretches from the top instead, and a photo that slid down would unveil a bare
    /// band behind the chrome.</summary>
    public static float ParallaxY(double offset) => -(float)Math.Max(0.0, offset) * (1f - ArtistHeroLayout.PhotoParallaxFraction);

    /// <summary>The scrim's alpha at the window top; it falls to 0 by the card's top edge.</summary>
    public static float ScrimTopAlpha() => ScrimTop;

    /// <summary>The scrim's height: the chrome's whole extent above the card (the title bar, plus the Zune band when present).</summary>
    public static float ScrimHeight(float chromeBottom) => MathF.Max(0f, chromeBottom);

    // ── THE SHARED PHOTO FRAME ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The photo's focus, shared by the card's photo and the shell's: both crop the same pixels.</summary>
    public const float PhotoFocusY = 0f;

    /// <summary>The photo's scale, shared by both renderers. While the bleed applies the card's frame scale, lift and entrance
    /// zoom do not apply (the shell's node cannot share a component's keyframes); the photo's entrance is its opacity fade.</summary>
    public const float PhotoScale = 1f;

    /// <summary>The photo's box in CARD-LOCAL coordinates (the card's presented top is 0): <paramref name="Top"/> is
    /// <c>-poseY</c>, <paramref name="Width"/> the card's final width and <paramref name="Height"/> the taller of the final and
    /// presented chrome plus the photo.</summary>
    public readonly record struct PhotoFrame(float Top, float Width, float Height);

    /// <summary>THE ONE PHOTO FRAME, used by both renderers. Translated by the card's presented top it is exactly the shell's
    /// inner photo box in window coordinates (top 0, width = the card's final width, height = the taller of the final and
    /// presented chrome plus the photo). Both draw it with FocusX = <see cref="ArtistHeroLayout.PhotoFocusX"/>, FocusY =
    /// <see cref="PhotoFocusY"/>, Cover and the page's latched decode, so the overlap is pixel-identical.</summary>
    public static PhotoFrame FrameFor(float cardW, float poseY, float rectY, float photoH)
        => new(-poseY, MathF.Max(0f, cardW), MathF.Max(rectY, poseY) + MathF.Max(0f, photoH));

    // ── THE HAND-OVER ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The in-card photo and veil's opacity. They stay fully drawn until the shell's identical layers are fully present
    /// (<paramref name="presence"/> 1), so a first reveal or a KeepAlive reactivation never dips; then they EASE out with
    /// <paramref name="handover"/> (0..1, <see cref="Shell.Ui.BleedHandover"/>) instead of snapping. Over the opaque region the
    /// shell's pixels are the same, so the ease is invisible there; in the photo's feathered bottom band, where the card's own
    /// feather sits over the shell's, the ease is what turns the two feathers into one without a step.</summary>
    public static float CardLayerOpacity(bool drawn, float presence, float handover = 1f)
        => drawn && presence >= 0.999f ? 1f - Math.Clamp(handover, 0f, 1f) : 1f;

    /// <summary>How far the chrome's ink has moved from the theme's toward the on-media ink: the shell's presence times how
    /// much of the hero is still showing, 0..1.</summary>
    public static float ChromeInkMix(float presence, float heroVisible)
        => Math.Clamp(presence, 0f, 1f) * Math.Clamp(heroVisible, 0f, 1f);

    /// <summary>The chrome ink at <paramref name="mix"/> (<see cref="ChromeInkMix"/>).</summary>
    public static ColorF Ink(ColorF theme, ColorF media, float mix) => ColorF.Lerp(theme, media, Math.Clamp(mix, 0f, 1f));
}
