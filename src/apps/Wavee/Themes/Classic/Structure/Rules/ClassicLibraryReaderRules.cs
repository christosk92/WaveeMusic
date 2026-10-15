// ── Themes/Classic/Structure/Rules/ClassicLibraryReaderRules.cs — the numbers of Classic's library artist reader (themes plan 6.1, T10c1; P22) ──
//
// What `Entities\Artist.Reader.cs` held beside the picture it draws: the spine's dot and its pitch, the block rows' lane set and options, and the sizes of the
// head's shimmer bars. They are data, not decisions (WVTH003 allows literals only in `Rules\**`). The band, block and head METRICS that the extent estimator also
// reads (`Artist.ReaderShape`: the avatar, the circles, the paddings, the stated heights, the two width arms) stay core and the view reads them: renderer ==
// estimator by identity.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's library artist reader.</summary>
internal static class ClassicLibraryReaderRules
{
    // ── the spine ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One spine cover's edge, its accent ring's width and its corner radius (the art inside is a notch smaller).</summary>
    public const float DotEdge = 36f, DotRing = 2f, DotRadius = 4f, DotArtRadius = 3f;

    /// <summary>The dot's resting opacities: the current block, a block in your library, a catalogue block; and its hover.</summary>
    public const float DotOpacityCurrent = 1f, DotOpacitySaved = 0.5f, DotOpacityCatalogue = 0.3f, DotOpacityHover = 0.9f;

    /// <summary>The spine's row pitch. <c>RepeatLayout.Stack</c> carries NO gap of its own, so the item extent IS the pitch and the gap under the cover is part of the item.</summary>
    public static float DotPitch => DotEdge + Spacing.S;

    // ── the block rows ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The prototype's block row, exactly: <c>28 | 1fr | 32 | 52</c> — number, title, the TRAILING heart, duration. No per-row "…" (the head owns the album's menu
    /// and the row's own context menu is the right button), and the heart moved OUT of the leading lane pair: in a reader the only thing a row needs to say about itself is
    /// that you hold it, which belongs beside the duration, not between the number and the title.
    /// <para>The lane width is <c>Track.Lane.HeartTrailing</c> (32), not <c>Track.Lane.Heart</c> (28): the latter is the INLINE heart's slot inside the full table's lane
    /// ladder, and the trailing slot is the wider one. The two are mutually exclusive by construction (<c>Track.RowMetrics.ShowHeartTrailing</c>), which is why
    /// <c>Heart</c> is explicitly false here rather than merely omitted.</para></summary>
    public static readonly Track.ColumnSet Cols = new(Album: false, By: false, Date: false, Plays: false,
                                                      Heart: false, Thumb: false, Actions: false, HeartTrailing: true);

    /// <summary>The lane widths of <see cref="Cols"/>: the number, the title, the trailing heart and the duration.</summary>
    public static readonly TrackSize[] Tracks =
        [TrackSize.Px(28f), TrackSize.Star(1f), TrackSize.Px(Track.Lane.HeartTrailing), TrackSize.Px(52f)];

    /// <summary>The row's options, hoisted: the reader never prints a track's artist (the block IS the credit). The trailing heart wears every row's ROW-HOVER reveal
    /// (tuned Classic L2), so a block of twelve rows is not twelve hearts.</summary>
    public static readonly Track.EagerRowOptions RowOptions = new(ShowTrackArtist: false);

    // ── the album head ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The head's title link type size (20 over the 26 line <c>ReaderShape.HeadTitleLine</c> states).</summary>
    public const float HeadTitleSize = 20f;

    // ── the head's shimmer face ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The meta bar and the title bar of the counted skeleton's head: sized boxes (the deriver needs a measurable shape).</summary>
    public const float ShimmerMetaW = 90f, ShimmerMetaH = 16f, ShimmerTitleW = 180f;
}
