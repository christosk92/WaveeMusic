using System;
using Wavee.Core;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>
/// WHY a deck's medium changed track, as an EDGE (set for exactly one fold, then cleared by
/// <c>DeckClock</c>). The record family needs all four combinations because the physical gesture differs: a
/// same-album advance re-cues the arm on the SAME disc, a new album pulls the record and swaps the sleeve, and a
/// USER skip is quicker (300 ms lift) than a natural run-through (450 ms).
/// <para><see cref="RepeatOne"/> is the one non-track-change edge: the uri did not move, but the medium did — the
/// arm re-cues the same disc.</para>
/// </summary>
public enum DeckBoundary : byte { None, NaturalSameAlbum, NaturalNewAlbum, SkipSameAlbum, SkipNewAlbum, RepeatOne }

/// <summary>
/// The transport phase as the DECK sees it. Derived by <c>DeckClock.Fold</c> from the bridge's play/buffering/error
/// signals and the playhead (the 0.2.9 bridge publishes no phase of its own): <see cref="Ended"/> is "not playing with
/// the playhead inside the last 1.5 s", <see cref="Transitioning"/> is reserved for a bridge that can report it.
/// Member set mirrors the catalog branch's <c>PlaybackTransportState.Phase</c> so the models carry over unchanged.
/// </summary>
public enum PlaybackPhase : byte { Idle, Resolving, Buffering, Playing, Pausing, Paused, Seeking, Transitioning, Recovering, Ended, Failed }

/// <summary>
/// ONE fold of <c>PlaybackBridge</c> for ONE tick. Pure data, engine-free (BCL + <c>Wavee.Core</c>), so every deck
/// model is a unit-testable value function of it. Built in exactly one place — <c>DeckClock.Fold</c> — which is also
/// what seeds a freshly mounted deck, so "mounted mid-song" and "ticked mid-song" see the identical shape.
/// </summary>
/// <param name="NowMs">A monotonic clock (<c>FrameTime.NowMs</c> — the frame's present time, never the 15.6 ms-granular
/// <c>Environment.TickCount64</c>) — every timer in every model is relative to it.</param>
/// <param name="Advancing"><c>Transport.OutputAdvancing &amp;&amp; !IsBuffering</c> — audio is actually coming out.</param>
/// <param name="Buffering"><c>IsBuffering</c> OR an intent that is still resolving/buffering.</param>
/// <param name="QueueEnded"><c>Phase == Ended</c> with no standing intent — the run-out/locked-groove trigger.</param>
/// <param name="SeekTargetMs">A committed seek awaiting its acknowledgement (bridge), or a synthesized remote jump (DeckClock).</param>
/// <param name="ScrubTargetMs">Pointer-owned position: non-null means a drag is LIVE right now.</param>
/// <param name="PositionMs">INTERPOLATED between the transport's ~1 Hz anchors — never the raw reported value.</param>
/// <param name="ReducedMotion">A VALUE, read every fold: models collapse their phase durations, they never change shape.</param>
public readonly record struct DeckInput(
    long NowMs, bool HasTrack, PlaybackPhase Phase, bool PlayWhenReady,
    bool Advancing,
    bool Buffering,
    bool Error, bool QueueEnded,
    DeckBoundary Boundary,
    long? SeekTargetMs,
    long? ScrubTargetMs,
    long PositionMs,
    long DurationMs, bool RepeatOne, float Rpm, bool ReducedMotion)
{
    /// <summary>Progress through the track, 0..1. An unknown duration reads 0 (never a fiction).</summary>
    public float Frac => DurationMs > 0 ? Clamp01(PositionMs / (float)DurationMs) : 0f;

    /// <summary>The same mapping for an arbitrary position (a seek target, a scrub target).</summary>
    public float FracOf(long ms) => DurationMs > 0 ? Clamp01(ms / (float)DurationMs) : 0f;

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

/// <summary>
/// The ONE answer to "what just happened to the medium?", as a pure function of the previous and current transport
/// facts. Engine-free and unit-tested (<c>DeckBoundaryRulesTests</c>) because every record-family gesture hangs off
/// it and none of the four cases is observable from a screenshot.
/// </summary>
public static class DeckBoundaryRules
{
    /// <summary>How close to the end counts as "the track ran out" rather than "the user skipped".</summary>
    public const long NaturalEndWindowMs = 1_500;

    /// <summary>How near the start the new position must be for a same-uri change to read as a repeat-one rewind.</summary>
    public const long RepeatRewindMs = 2_000;

    /// <summary>
    /// Classify the edge. <paramref name="prevUri"/>/<paramref name="prevAlbumUri"/> and the three prev* transport
    /// values are the LAST fold's; the unprefixed ones are the fold being built.
    /// <para>Ordering matters: the same-uri arm runs FIRST (a repeat-one rewind is not a track change), and a null on
    /// either side is "we have nothing to compare" — never a synthesized skip, or mounting the deck while nothing
    /// played would fake one.</para>
    /// </summary>
    public static DeckBoundary Classify(string? prevUri, string? prevAlbumUri, long prevPosMs, long prevDurMs, PlaybackPhase prevPhase,
                                        string? uri, string? albumUri, long posMs, bool repeatOne)
    {
        if (string.Equals(prevUri, uri, StringComparison.Ordinal))
            return repeatOne && prevDurMs > 0 && prevPosMs >= prevDurMs - NaturalEndWindowMs && posMs < RepeatRewindMs
                ? DeckBoundary.RepeatOne
                : DeckBoundary.None;
        if (prevUri is null || uri is null) return DeckBoundary.None;
        bool natural = prevPhase == PlaybackPhase.Transitioning || (prevDurMs > 0 && prevPosMs >= prevDurMs - NaturalEndWindowMs);
        bool sameAlbum = albumUri is { Length: > 0 } && string.Equals(prevAlbumUri, albumUri, StringComparison.Ordinal);
        return (natural, sameAlbum) switch
        {
            (true, true) => DeckBoundary.NaturalSameAlbum,
            (true, false) => DeckBoundary.NaturalNewAlbum,
            (false, true) => DeckBoundary.SkipSameAlbum,
            _ => DeckBoundary.SkipNewAlbum,
        };
    }
}
