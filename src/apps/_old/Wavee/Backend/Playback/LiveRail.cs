using System;
using Wavee.Core;

namespace Wavee.Backend.Playback;

/// <summary>
/// The DVR rail's arithmetic — the pure map between a live broadcast's SEEKABLE WINDOW and the 0..1 fraction a seek bar
/// draws and drags.
///
/// <para><b>Why this is not the ordinary <c>position / duration</c>.</b> An ordinary track's rail is anchored at zero
/// and scaled by a fixed length. A live broadcast has neither: the window's LEFT end is a wall-clock position that
/// keeps moving forward, and its right end is the live edge, also moving. Scrubbing such a source with the
/// position/duration formula puts the thumb in a place that means nothing (a 4-hour DVR window whose start is at
/// 11,400,000 ms would peg the thumb at the far right forever) and commits seeks to milliseconds the source will
/// refuse. The rail therefore maps the WINDOW, and the window alone.</para>
///
/// <para><b>Engine-free and pure by design</b> — no signals, no player, no engine types — so the rail's behaviour at
/// its two hard edges (a zero-width window, a playhead that has fallen off the back of a sliding window) is decided by
/// unit tests instead of by a live stream nobody can replay. <c>Wavee.Tests</c> source-includes <c>Backend/**</c>.</para>
/// </summary>
public static class LiveRail
{
    /// <summary>Where the playhead sits inside the seekable window, as a 0..1 fraction (0 = the window's oldest
    /// position, 1 = the live edge).
    /// <para>Clamped at both ends, and honest about the degenerate case: a window with no width answers 1 — a station
    /// with nothing to rewind IS at the live edge, and answering 0 would draw an empty rail under audio that is
    /// playing. A playhead that has fallen behind a window which slid past it answers 0 rather than a negative
    /// fraction.</para></summary>
    /// <param name="startMs">The window's oldest seekable position, in ms.</param>
    /// <param name="endMs">The window's newest seekable position (the live edge), in ms.</param>
    /// <param name="positionMs">The playhead, in ms, on the same clock.</param>
    public static double Frac(long startMs, long endMs, long positionMs)
    {
        long span = endMs - startMs;
        if (span <= 0) return 1.0;
        double f = (double)(positionMs - startMs) / span;
        return f <= 0 ? 0.0 : f >= 1 ? 1.0 : f;
    }

    /// <summary>The fraction the DVR rail actually DRAWS — <see cref="Frac"/> with the live edge snapped.
    ///
    /// <para><b>Why the draw differs from the measurement.</b> At the edge, <see cref="Frac"/> answers a number that
    /// is honest about milliseconds and dishonest about MEANING: a healthy playhead rides a few seconds inside a
    /// window whose two ends are both moving, so it answers ~0.88 on a 50-second window and it answers a DIFFERENT
    /// ~0.88 on every one of the four window reports a second. Drawn literally, that is a rail that is never full
    /// under a stream that IS live, and a fill that slides left and right forever under a playhead that is not
    /// moving. Once <see cref="LiveEdgeState"/> has decided the playable is AT the edge, the rail says so: full fill,
    /// thumb on the live tick, and — because the answer no longer depends on the window's two moving ends — dead
    /// still between reports.</para>
    ///
    /// <para>BEHIND the edge the measurement is the whole point (it is what the user is about to scrub back through),
    /// so it is drawn exactly as <see cref="Frac"/> gives it.</para></summary>
    /// <param name="startMs">The window's oldest seekable position, in ms.</param>
    /// <param name="endMs">The window's newest seekable position (the live edge), in ms.</param>
    /// <param name="positionMs">The playhead, in ms, on the same clock.</param>
    /// <param name="isBehind">The decided edge state — <see cref="LiveEdgeState.IsBehind"/>, never a raw comparison.</param>
    public static double DisplayFrac(long startMs, long endMs, long positionMs, bool isBehind)
        => isBehind ? Frac(startMs, endMs, positionMs) : 1.0;

    /// <summary>The position a rail fraction commits to, in ms on the broadcast's own clock — the inverse of
    /// <see cref="Frac"/>.
    /// <para>Clamped INTO the window at both ends, because a seek even a millisecond outside it is a request the source
    /// rejects (and, at the right end, one that can wedge a live session against a moving edge). A zero-width window
    /// commits to its own single position, which makes "scrub a station with no DVR" a no-op rather than an error.</para></summary>
    /// <param name="startMs">The window's oldest seekable position, in ms.</param>
    /// <param name="endMs">The window's newest seekable position (the live edge), in ms.</param>
    /// <param name="frac">The rail fraction, 0..1 (clamped).</param>
    public static long Seek(long startMs, long endMs, double frac)
    {
        if (endMs <= startMs) return startMs;
        double f = double.IsNaN(frac) ? 0 : frac <= 0 ? 0 : frac >= 1 ? 1 : frac;
        long span = endMs - startMs;
        return startMs + (long)Math.Round(f * span);
    }

    /// <summary><see cref="Frac"/> against a whole <see cref="LiveWindow"/> (its own start/end/position).</summary>
    /// <param name="w">The engine's live timeline.</param>
    public static double Frac(in LiveWindow w) => Frac(w.SeekableStartMs, w.SeekableEndMs, w.PositionMs);

    /// <summary><see cref="Seek"/> against a whole <see cref="LiveWindow"/>.</summary>
    /// <param name="w">The engine's live timeline.</param>
    /// <param name="frac">The rail fraction, 0..1 (clamped).</param>
    public static long Seek(in LiveWindow w, double frac) => Seek(w.SeekableStartMs, w.SeekableEndMs, frac);

    /// <summary>How far behind the live edge a rail fraction would land, in ms — what the "GO LIVE −m:ss" label reads
    /// while a drag is in flight, before anything is committed.</summary>
    /// <param name="w">The engine's live timeline.</param>
    /// <param name="frac">The rail fraction, 0..1 (clamped).</param>
    public static long BehindAt(in LiveWindow w, double frac) => Math.Max(0, w.LiveEdgeMs - Seek(in w, frac));
}
