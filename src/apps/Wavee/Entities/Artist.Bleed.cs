// ── Entities/Artist.Bleed.cs — EXPERIMENTAL: the artist hero photo as one image from the window top ────────────────────
//
// Role: CORE (every rule is pure) plus a UI note: the shell draws the photo (Shell.Masthead.UI.cs MaterialLayerView, behind
//       the title bar and the Zune band) and the content card's top region fades out over it (Shell.UI.cs ContentRegion).
// EXPERIMENTAL: revert = revert this commit, or set `ArtistBleed.Enabled = false` (the ONE read, in Artist.Page.cs, then
//       publishes nothing, so the strip and the clip in the shell have height 0 and the page is today's page).
//
// WHAT THIS IS. On an artist page with a header photo and tinted surfaces on, the photo runs from the window top, behind the
// title bar and the Zune pivots and row 2 (or the Classic/Library title bar), down to the hero's end. The page publishes it
// as DATA through the shell material channel (`ShellBackdrop`, carried by `ShellMaterial.Publish` on the tint's ownership
// outcome, so a successor's claim always drops it). Only the card's TOP region changes: its fill and its top/left stroke over
// the hero fade out and return as the hero scrolls away; the solid fill below the hero stays, its top edge riding the hero's
// bottom through a paint-only translation. The card rect never changes, so nothing here lays anything out per scroll.

using FluentGpu.Foundation;

namespace Wavee;

public static class ArtistBleed
{
    /// <summary>The experiment's switch. Read in ONE place (<c>ArtistPage.Render</c>); false leaves no behavioural trace.
    /// OFF until the hero's horizontal veil is drawn by the shell over the photo too: with the photo bleeding, the card's own
    /// veil starts at the card top and leaves a hard seam there across the left half of the page, and a docked pane's 8-DIP
    /// card corner notches the photo.</summary>
    public const bool Enabled = false;

    /// <summary>The scrim's strongest alpha, at the window top (over <c>Design.Colors.ShellGround</c>). Light needs the heavier
    /// veil: dark chrome ink on a dark photo is the failure, light ink on a bright photo the milder one.</summary>
    const float ScrimTopLight = 0.62f, ScrimTopDark = 0.55f;

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

    /// <summary>The scrim's alpha at the window top, per theme; it falls to 0 by the card's top edge.</summary>
    public static float ScrimTopAlpha(bool light) => light ? ScrimTopLight : ScrimTopDark;

    /// <summary>The scrim's height: the chrome's whole extent above the card (the title bar, plus the Zune band when present).</summary>
    public static float ScrimHeight(float chromeBottom) => MathF.Max(0f, chromeBottom);
}
