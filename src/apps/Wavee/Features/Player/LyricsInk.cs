using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>
/// WHICH INK a <see cref="LyricsView"/> paints with — the one seam between the two surfaces that mount the same view.
///
/// <para>The rail's lyrics panel sits on the PAGE, so it takes the theme's text rungs. The immersive STAGE owns its own
/// ground — a scrim it paints over the artwork — so it takes <see cref="StageInk"/>, which solves the same theme
/// against THAT ground instead. Both arms follow the theme; what differs is which surface the ink has to win against.
/// Before this seam existed there was no choice to make: the view always painted <c>Tok.TextPrimary</c>, which is
/// precisely why the stage's base scrim had to flip white in light theme to keep the lyrics readable, which is what
/// forced every region of stage chrome to bring its own boxed dark veil, which is what made the surface a two-world
/// collage. One flag removes the whole chain.</para>
///
/// <para><b>It is a MODE, not a captured colour</b> — and that is now load-bearing for BOTH arms rather than only the
/// rail's. The struct holds a bool and resolves the token at the point of consumption, so a live theme flip re-reads
/// correctly on either surface; a <c>ColorF</c> frozen into a component's constructor would not, because component
/// props freeze at mount. (The stage's arm used to be theme-INDEPENDENT, which made this property look like belt and
/// braces. It is not: it is the mechanism.)</para>
/// </summary>
readonly record struct LyricsInk(bool OnMedia)
{
    /// <summary>The rail: theme rungs, flips with the theme.</summary>
    public static LyricsInk Theme => new(false);

    /// <summary>The stage: <see cref="StageInk"/>'s ladder, solved against the stage's own veil rather than the page.</summary>
    public static LyricsInk Media => new(true);

    // ── ink ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A SUNG glyph, the active line, a lit interlude dot — the loudest thing on the surface.</summary>
    public ColorF Primary => OnMedia ? StageInk.Ink : Tok.TextPrimary;

    /// <summary>An inactive line's text and the secondary (translation / romanization) layer.</summary>
    public ColorF Secondary => OnMedia ? StageInk.InkSecondary : Tok.TextSecondary;

    /// <summary>Meta — the debug/status captions that are not lyric text.</summary>
    public ColorF Tertiary => OnMedia ? StageInk.InkTertiary : Tok.TextTertiary;

    // ── the resync chip ──────────────────────────────────────────────────────────────────────────────────────────────
    // The one PLATE the reading surface owns (a detached-scroll "back to the song" pill). On media it takes the
    // on-media scrim ladder, because a theme plate under theme-invariant white ink is the same invisibility bug the ink
    // seam exists to remove — one surface, one ladder.

    /// <inheritdoc cref="Plate"/>
    public ColorF Plate => OnMedia ? StageInk.ScrimRest : Tok.FillSolidBase with { A = 0.92f };
    /// <inheritdoc cref="Plate"/>
    public ColorF PlateHover => OnMedia ? StageInk.ScrimHover : Tok.FillSubtleSecondary;
    /// <inheritdoc cref="Plate"/>
    public ColorF PlatePressed => OnMedia ? StageInk.ScrimPressed : Tok.FillSubtleTertiary;
    /// <inheritdoc cref="Plate"/>
    public ColorF PlateStroke => OnMedia ? StageInk.Stroke : Tok.StrokeCardDefault;

    /// <summary>The chip's progress ring: the accent on a theme plate, plain white on media (the stage spends its
    /// art-derived accent on exactly four jobs, and a resync spinner is not one of them).</summary>
    public ColorF RingFill => OnMedia ? StageInk.Ink : Tok.AccentDefault;
    /// <inheritdoc cref="RingFill"/>
    public ColorF RingTrack => OnMedia
        ? StageInk.Ink with { A = 0.30f }
        : Tok.StrokeControlDefault with { A = 0.55f };

    // ── the loading skeleton ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shimmer bar the reading surface shows while a document loads. It used to be <c>Tok.FillSubtleSecondary</c>
    /// unconditionally — a THEME fill painted straight onto the stage, i.e. a light bar on a dark surface in dark
    /// theme. Same seam as every other rung: the rail keeps the theme fill, the stage takes its own ink.</summary>
    public ColorF Skeleton => OnMedia ? StageInk.SkeletonBar : Tok.FillSubtleSecondary;

    // ── the held-note BLOOM ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The colour of the soft second glyph run painted UNDER a held syllable.
    ///
    /// <para>This is the one lyric treatment that does not survive an inverted ink ladder, and it is worth saying why:
    /// a glow is a light-EMITTING claim. Blurred white glyphs under white glyphs on a dark ground ADD luminance and
    /// read as a halo. Blurred near-black glyphs under near-black glyphs on a light ground SUBTRACT it — that is a drop
    /// shadow, and at the line-synced arm's σ it reads as dirty, double-printed text.</para>
    ///
    /// <para>So the light arm blooms in the VEIL rather than in the ink: the halo still claims "lit from behind"
    /// instead of inverting into a smudge. It is inherently a weaker effect than a white bloom on black — see
    /// <see cref="BloomScale"/>.</para></summary>
    public ColorF Bloom => OnMedia ? (StageInk.IsDark ? StageInk.Ink : StageInk.Veil) : Tok.TextPrimary;

    /// <summary>How much of the bloom survives. A near-white halo on a near-white ground cannot carry the same weight
    /// a white halo carries on black, so the light stage damps it rather than pretending the effect transfers.</summary>
    public float BloomScale => OnMedia && !StageInk.IsDark ? LightBloomScale : 1f;

    /// <inheritdoc cref="BloomScale"/>
    const float LightBloomScale = 0.5f;
}
