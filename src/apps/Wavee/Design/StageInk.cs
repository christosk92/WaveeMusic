using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;

namespace Wavee;

/// <summary>The live arm. <see cref="Arm"/> is the PURE entry point — it takes the theme rather than reading it, so
/// value tests can drive both arms without mutating global theme state (the <c>WaveePalette</c> pattern), which matters
/// because this test project has no parallelism opt-out and a second theme-mutating class would race
/// <c>LightModeOverhaulTests</c>.</summary>
static class StageInk
{
    /// <inheritdoc cref="StageArm"/>
    internal static StageArm Arm(ThemeKind theme) => StageArm.For(theme);

    /// <summary>The arm for the ACTIVE theme. This — and only this — is the stage's theme branch.</summary>
    static StageArm Live => Arm(Tok.Theme);

    /// <summary>Which arm is live, for the handful of decisions that are a POLARITY rather than a colour (the lyrics
    /// bloom's direction and weight). Everything else should take a rung instead of asking this.</summary>
    public static bool IsDark => Live.Dark;

    /// <inheritdoc cref="StageArm.Veil"/>
    public static ColorF Veil => Live.Veil;
    /// <inheritdoc cref="StageArm.Floor"/>
    public static ColorF Floor => Live.Floor;

    /// <inheritdoc cref="StageArm.Ink"/>
    public static ColorF Ink => Live.Ink;
    /// <inheritdoc cref="StageArm.InkSecondary"/>
    public static ColorF InkSecondary => Live.InkSecondary;
    /// <inheritdoc cref="StageArm.InkTertiary"/>
    public static ColorF InkTertiary => Live.InkTertiary;

    /// <inheritdoc cref="StageArm.GlassRest"/>
    public static ColorF GlassRest => Live.GlassRest;
    /// <inheritdoc cref="StageArm.GlassHover"/>
    public static ColorF GlassHover => Live.GlassHover;
    /// <inheritdoc cref="StageArm.GlassPressed"/>
    public static ColorF GlassPressed => Live.GlassPressed;

    /// <inheritdoc cref="StageArm.GlassPlate"/>
    public static ColorF GlassPlate => Live.GlassPlate;
    /// <inheritdoc cref="StageArm.GlassPlateHover"/>
    public static ColorF GlassPlateHover => Live.GlassPlateHover;
    /// <inheritdoc cref="StageArm.GlassPlatePressed"/>
    public static ColorF GlassPlatePressed => Live.GlassPlatePressed;

    /// <inheritdoc cref="StageArm.ScrimRest"/>
    public static ColorF ScrimRest => Live.ScrimRest;
    /// <inheritdoc cref="StageArm.ScrimHover"/>
    public static ColorF ScrimHover => Live.ScrimHover;
    /// <inheritdoc cref="StageArm.ScrimPressed"/>
    public static ColorF ScrimPressed => Live.ScrimPressed;
    /// <inheritdoc cref="StageArm.Stroke"/>
    public static ColorF Stroke => Live.Stroke;

    /// <inheritdoc cref="StageArm.ButtonFill"/>
    public static ColorF ButtonFill => Live.ButtonFill;
    /// <inheritdoc cref="StageArm.ButtonFillHover"/>
    public static ColorF ButtonFillHover => Live.ButtonFillHover;
    /// <inheritdoc cref="StageArm.ButtonFillPressed"/>
    public static ColorF ButtonFillPressed => Live.ButtonFillPressed;
    /// <inheritdoc cref="StageArm.ButtonInk"/>
    public static ColorF ButtonInk => Live.ButtonInk;

    /// <inheritdoc cref="StageArm.AccentFrom"/>
    /// <remarks>The cover LOOKUP lives here rather than on <see cref="StageArm"/> so the arm stays a pure value type
    /// over the token layer — which is what lets value tests drive both arms without a cover plane, a theme or a
    /// window (the <c>WaveePalette</c> pattern this seam is modelled on).</remarks>
    public static ColorF Accent(Track? track) =>
        Surfaces.ChromeSchemeFor(track?.Image?.Url) is { } scheme
            ? Live.AccentFrom(WaveePalette.ChromeAccent(scheme))
            : Tok.AccentDefault;

    /// <summary>The backdrop's stand-in while the cover is missing or still decoding, in the STAGE's polarity rather
    /// than the page's. The cover TINT survives (it is what stops the slot reading as a hole); only the neutral it is
    /// blended toward follows the stage.</summary>
    public static ColorF ArtStandIn(string? url) => Surfaces.PlaceholderFor(url, light: !IsDark);
    /// <inheritdoc cref="StageArm.SkeletonBar"/>
    public static ColorF SkeletonBar => Live.SkeletonBar;
}
