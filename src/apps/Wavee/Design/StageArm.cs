using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>
/// THE IMMERSIVE STAGE'S INK — the one place in the app that knows which POLARITY the stage is painted in.
///
/// <para><b>What changed, and why this file exists.</b> The stage used to be single-theme: always art-dark, in both
/// themes, on the argument that "the room is lit by the playing track". That is a defensible design, but it is not the
/// one this product wants — in light theme it read as a black slab bolted under light chrome. The stage now flips with
/// the theme, and the whole of that decision lives HERE so the four renderers stay provably theme-blind.</para>
///
/// <para><b>It is a MODE, not a captured colour</b> — the argument <see cref="LyricsInk"/> already makes, and it is
/// now load-bearing for BOTH arms rather than only the rail's: every rung resolves its token at the point of
/// consumption, so a live theme flip re-reads correctly. A <c>ColorF</c> frozen into a component's constructor would
/// not (component props freeze at mount). The engine does the rest: <c>Tok.Epoch</c> bumps, <c>AppHost</c> calls
/// <c>Reconciler.RethemeAll()</c>, every mounted render re-runs in place and every fill/text diff cross-fades.</para>
///
/// <para><b>The dark arm delegates to <see cref="WaveeOnMedia"/> VERBATIM.</b> That is deliberate and it is pinned by
/// a test: "dark theme is byte-identical to what shipped" is then an executable claim rather than a promise. It also
/// keeps <see cref="WaveeOnMedia"/> exactly what it was — a THEME-INVARIANT ladder for everything that paints on top of
/// actual artwork (<c>MediaCard</c>'s covers, row FABs, the bar). That ladder must NOT become theme-aware; on a cover
/// thumbnail white-on-scrim is right in both themes. The stage is the one surface whose "media" is a full-bleed
/// blurred backdrop it also owns the scrim of, which is exactly why it — and only it — gets a polarity.</para>
///
/// <para><b>The alphas are shared, the GROUND is mirrored.</b> Every light rung reads its opacity off its dark twin
/// rather than restating it, so the two arms cannot drift into two different ladders. What differs is only what the
/// alpha is applied TO: white ink over a dark veil, or near-black ink over a light one.</para>
///
/// <para><b>The scrim ALPHAS need no light arm at all</b> — see the contrast table in <see cref="StageLayout"/>. The
/// sRGB transfer curve does the work: mixing toward black at a partial alpha destroys far more perceptual luminance
/// than mixing toward white, so the light arm's alpha'd ink clears a HIGHER contrast ratio than the dark arm we
/// already ship. <see cref="StageLayout"/> stays one set of numbers.</para>
/// </summary>
readonly record struct StageArm(bool Dark)
{
    /// <summary>The arm for a theme, as a PURE function of it — the entry point value tests use, so both arms can be
    /// driven without mutating global theme state (the <c>WaveePalette</c> pattern).</summary>
    internal static StageArm For(ThemeKind theme) => new(theme == ThemeKind.Dark);

    // ── the ground ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The colour every scrim alpha is mixed FROM, and the opaque floor under the backdrop. Light takes the
    /// app's own canon achromatic light ground rather than minting a near-duplicate; achromatic on purpose, so the
    /// cover's own colour comes through the veil instead of fighting a tint underneath it.</summary>
    public ColorF Veil => Dark ? Tok.MediaStage : WaveePalette.PageToneNeutralLight;

    /// <summary>The opaque plate beneath the (possibly missing, still-decoding) cover. It IS the veil — which is what
    /// every comment on the surface already claimed it was, while the code actually painted <c>Tok.FillSolidBase</c>
    /// and so flashed near-white under the dark scrim in light theme.</summary>
    public ColorF Floor => Veil;

    // ── ink ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Light ink is Tok.MediaStage — an ON-MEDIA token, deliberately NOT Tok.TextPrimary. Two reasons: the stage's ink
    // must stay opaque (the theme text rung is black @ 0.894, which is a second, quieter ladder), and the surface's
    // "no theme-flipping text rung" guard stays true by construction.

    /// <summary>The loudest thing on the surface: the title, a sung glyph, the active line.</summary>
    public ColorF Ink => Dark ? WaveeOnMedia.Ink : Tok.MediaStage;
    /// <inheritdoc cref="Ink"/>
    public ColorF InkSecondary => Dark ? WaveeOnMedia.InkSecondary : Tok.MediaStage with { A = Tok.OnMediaSecondary.A };
    /// <inheritdoc cref="Ink"/>
    public ColorF InkTertiary => Dark ? WaveeOnMedia.InkTertiary : Tok.MediaStage with { A = Tok.OnMediaTertiary.A };

    // ── glass: the stage's interaction ramp for a control carrying NO resting plate ──────────────────────────────────

    /// <summary>Rest: nothing. Stated as a rung so a call site never spells <c>Transparent</c> and reads as "no state
    /// model".</summary>
    public ColorF GlassRest => ColorF.Transparent;
    /// <inheritdoc cref="GlassRest"/>
    public ColorF GlassHover => Ink with { A = WaveeOnMedia.GlassHover.A };
    /// <inheritdoc cref="GlassRest"/>
    public ColorF GlassPressed => Ink with { A = WaveeOnMedia.GlassPressed.A };

    // ── the ink PLATE: a resting ground for the one control that must be found (the way out) ─────────────────────────

    /// <inheritdoc cref="WaveeOnMedia.GlassPlate"/>
    public ColorF GlassPlate => Ink with { A = WaveeOnMedia.GlassPlate.A };
    /// <inheritdoc cref="WaveeOnMedia.GlassPlate"/>
    public ColorF GlassPlateHover => Ink with { A = WaveeOnMedia.GlassPlateHover.A };
    /// <inheritdoc cref="WaveeOnMedia.GlassPlate"/>
    public ColorF GlassPlatePressed => Ink with { A = WaveeOnMedia.GlassPlatePressed.A };

    // ── the scrim plate + its hairline ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A small plate floated over ARTWORK (the secondary-line toggle). Same alphas, mirrored ground.</summary>
    public ColorF ScrimRest => Dark ? WaveeOnMedia.ScrimRest : Veil with { A = WaveeOnMedia.ScrimRest.A };
    /// <inheritdoc cref="ScrimRest"/>
    public ColorF ScrimHover => Dark ? WaveeOnMedia.ScrimHover : Veil with { A = WaveeOnMedia.ScrimHover.A };
    /// <inheritdoc cref="ScrimRest"/>
    public ColorF ScrimPressed => Dark ? WaveeOnMedia.ScrimPressed : Veil with { A = WaveeOnMedia.ScrimPressed.A };

    /// <summary>The hairline that rings a plate. It inverts WITH the ink — a white hairline on a light plate is not a
    /// quiet ring, it is an absent one.</summary>
    public ColorF Stroke => Dark ? WaveeOnMedia.Stroke : Ink with { A = WaveeOnMedia.Stroke.A };

    // ── the ONE filled control (play/pause) ──────────────────────────────────────────────────────────────────────────
    // Named ButtonFill rather than the dark arm's "LightButton": on a light stage the loudest affordance is a DARK
    // disc, so the old name would be a lie in half the product. Same 0.08 / 0.157 ramp, mirrored direction.

    /// <summary>The stage's play/pause — the only plate on the surface that is not a hover state.</summary>
    public ColorF ButtonFill => Dark ? WaveeOnMedia.LightButton : Ink;
    /// <inheritdoc cref="ButtonFill"/>
    public ColorF ButtonFillHover => Dark ? WaveeOnMedia.LightButtonHover : ColorRamp.Lighten(Ink, LightButtonHoverLift);
    /// <inheritdoc cref="ButtonFill"/>
    public ColorF ButtonFillPressed => Dark ? WaveeOnMedia.LightButtonPressed : ColorRamp.Lighten(Ink, LightButtonPressedLift);
    /// <summary>The glyph ON that disc — the ground it stands on, so the two can never collide.</summary>
    public ColorF ButtonInk => Dark ? WaveeOnMedia.LightButtonInk : Veil;

    /// <summary>The dark arm's ramp DROPS, reused as the light arm's LIFTS — one ramp, two directions.</summary>
    internal const float LightButtonHoverLift = 0.08f;
    /// <inheritdoc cref="LightButtonHoverLift"/>
    internal const float LightButtonPressedLift = 0.157f;

    // ── the art-derived accent ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stage spends its one art-derived colour on exactly four jobs: a latched satellite, the saved
    /// heart, the pivot underline + section rule, and the ∞ when autoplay is on.
    ///
    /// <para>Dark takes <c>ChromeAccent</c> — the lifted, saturation-FLOORED role, correct for a colour that must hold
    /// its own as a lit mark on a dark ground. Light cannot: a lifted accent on a pale veil is a highlighter. So the
    /// light arm re-solves the SAME hue as INK against the brightest ground this stage can produce, which is the app's
    /// existing role-3 answer rather than a fourth accent solve invented here. The honest cost is chroma —
    /// <c>TextInk</c> caps saturation below <c>ChromeAccent</c>'s floor, so the light stage's accent is a quieter
    /// version of the same hue, and some mid-blues fall through to a near-neutral. Both are properties of
    /// accent-as-ink, and both are already documented on the functions being reused.</para></summary>
    public ColorF AccentFrom(ColorF chrome) =>
        Dark ? chrome : WaveePalette.TextInk(chrome, ThemeKind.Light, AccentGround);

    /// <summary>The BRIGHTEST ground the light stage can put an accent on: the plateau veil over a white cover. Solving
    /// against the worst case is what stops the accent going illegible on a pale sleeve.</summary>
    ColorF AccentGround => ColorContrast.Over(Veil with { A = StageLayout.ScrimBaseA }, Tok.OnMediaPrimary);

    // ── two non-colour helpers, so the renderers make ZERO polarity reads of their own ───────────────────────────────

    /// <summary>The lyrics loading shimmer. It used to paint a theme fill (<c>Tok.FillSubtleSecondary</c>) straight
    /// onto the stage, which is a light bar on a dark surface in dark theme.</summary>
    public ColorF SkeletonBar => Ink with { A = SkeletonA };

    const float SkeletonA = 0.12f;
}

