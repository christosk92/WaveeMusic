// ── Theming/Roles/LyricsInkRoles.cs — the lyrics surface's ink decisions (themes plan 5.4, T2b) ────────────────────────────
//
// The lyrics view mounts on two surfaces (the immersive stage, where it paints with the stage arm's ink, and the now-playing panel,
// where it paints with the theme's own tokens). WHICH ink is the seam `Lyrics.Ink(OnMedia)`; the numbers and the one polarity
// decision that seam used to make from the stage arm (the held-note bloom) are the style's roles.

using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The lyrics ink roles of a style. Every style defines all of them (themes plan S2). Equality is by value.</summary>
/// <param name="ThemePlateAlpha">The alpha of the reading surface's one plate (the resync pill, the video note) on a THEME surface.</param>
/// <param name="RingTrackThemeAlpha">The alpha of the resync ring's track on a THEME surface.</param>
/// <param name="RingTrackOnMediaAlpha">The alpha of the resync ring's track on the STAGE (plain ink at this alpha).</param>
/// <param name="BloomOnMedia">The held-note BLOOM on the stage: where the stage is dark the ink (a halo adds luminance), where it is light the VEIL
/// (blurred near-black under near-black subtracts and reads as a smudge).</param>
/// <param name="BloomScale">How much of the bloom survives on the stage: half where it is light.</param>
public sealed record LyricsInkRoles(
    float ThemePlateAlpha, float RingTrackThemeAlpha, float RingTrackOnMediaAlpha,
    ColorRole BloomOnMedia, ModePair<float> BloomScale);

/// <summary>The lyrics roles as the call syntax the lyrics ink reads (<c>R.Lyrics.ThemePlateAlpha</c>). Colour and per-kind members
/// evaluate for the ACTIVE theme kind at the call: nothing is cached.</summary>
public readonly struct LyricsReads
{
    private readonly LyricsInkRoles _roles;

    internal LyricsReads(LyricsInkRoles roles) => _roles = roles;

    /// <summary>The role table behind these reads.</summary>
    public LyricsInkRoles Roles => _roles;

    /// <inheritdoc cref="LyricsInkRoles.ThemePlateAlpha"/>
    public float ThemePlateAlpha => _roles.ThemePlateAlpha;
    /// <inheritdoc cref="LyricsInkRoles.RingTrackThemeAlpha"/>
    public float RingTrackThemeAlpha => _roles.RingTrackThemeAlpha;
    /// <inheritdoc cref="LyricsInkRoles.RingTrackOnMediaAlpha"/>
    public float RingTrackOnMediaAlpha => _roles.RingTrackOnMediaAlpha;
    /// <inheritdoc cref="LyricsInkRoles.BloomOnMedia"/>
    public ColorF BloomOnMedia => _roles.BloomOnMedia.Now;
    /// <inheritdoc cref="LyricsInkRoles.BloomScale"/>
    public float BloomScale => _roles.BloomScale.Now;
}
