using System;
using Wavee.Core;

namespace Wavee.Backend;

/// <summary>
/// The PURE prepared-next decision rules (W2 gapless fix §1/§5) — extracted from <c>PlaybackController.SchedulePreparedNext</c>
/// so the WHAT (prepare this next item? may the boundary overlap? what identity signature dedupes a re-schedule?) and the
/// WHEN (has the ending-soon window opened? must a seek re-arm?) are unit-testable without a host, a session, or a clock.
/// Engine-free by construction (Wavee.Core + <see cref="MediaSwitchLogic"/> + BCL only), exactly like its siblings in
/// <c>App/MediaSwitchLogic.cs</c> — which is what lets Wavee.Tests source-include it. The controller wiring stays a thin
/// caller: it reads the live values (kind, gate, repeat) and acts on the returned decision — no rules live inline anymore.
/// </summary>
public static class PreparedNextPolicy
{
    /// <summary>The worst-case time to make a prepared-next consumable at the join: key resolve + CDN head + decoder
    /// TryOpen + ring prefill (fix design §1 — "suggested worst-case margin: ≥ 8 s").</summary>
    public const int WorstCasePrimeMs = 8000;

    /// <summary>What <see cref="Decide"/> concluded: whether to prepare at all, whether the boundary may OVERLAP
    /// (crossfade / gapless butt-join — false forces the hard cut), and the identity signature a duplicate
    /// re-schedule dedupes on (null when nothing should be prepared — any prior token is cancelled).</summary>
    public readonly record struct PrepareDecision(bool Prepare, bool AllowOverlap, string? Signature);

    /// <summary>Decide the prepared-next action for the (current, next) pair. Mirrors the original inline rules exactly:
    /// no current / a video current cancels; a video next or a gated (<paramref name="nextMayPrepare"/> false) next
    /// cancels; overlap needs music on BOTH sides, no repeat-track, and an Audio→Audio boundary
    /// (<see cref="MediaSwitchLogic.AllowCrossfade"/>).</summary>
    public static PrepareDecision Decide(
        PlayableKind currentKind, QueueEntry? current, QueueEntry? next, PlayableKind nextKind,
        bool nextMayPrepare, RepeatMode repeat)
    {
        if (current is null || currentKind == PlayableKind.Video) next = null;
        if (next is not null && nextKind == PlayableKind.Video) next = null;
        if (next is not null && !nextMayPrepare) next = null;
        bool allowOverlap = current is not null && next is not null
            && repeat != RepeatMode.Track
            && IsMusic(current.Track) && IsMusic(next.Track)
            && MediaSwitchLogic.AllowCrossfade(currentKind, nextKind);
        string? signature = next is null ? null : Signature(current!.ItemId, next.ItemId, allowOverlap);
        return new PrepareDecision(next is not null, allowOverlap, signature);
    }

    /// <summary>The identity signature a duplicate schedule call dedupes on: the (current, next) item ids + the overlap
    /// decision. Stable across position/seek changes by design — a seek re-arm on an unchanged pair is a no-op.</summary>
    public static string Signature(QueueItemId current, QueueItemId next, bool allowOverlap)
        => $"{current.Value:x}:{next.Value:x}:{(allowOverlap ? 1 : 0)}";

    /// <summary>Music (crossfade/gapless-eligible) vs spoken content: episodes/podcasts prepare but never overlap.</summary>
    public static bool IsMusic(Track track)
    {
        if (EntityUri.KindOf(track.Uri) == EntityKind.Episode
            || track.Uri.Contains(":episode:", StringComparison.OrdinalIgnoreCase)
            || track.Uri.Contains(":podcast:", StringComparison.OrdinalIgnoreCase)) return false;
        return track.Source?.Contains("podcast", StringComparison.OrdinalIgnoreCase) != true;
    }

    /// <summary>The ending-soon margin (fix design §1): <c>overlapMs + worst-case prime</c>, clamped to the FULL duration
    /// on tracks shorter than the margin (a short track's whole length is its prepare budget).</summary>
    public static long EndingSoonMarginMs(long durationMs, int overlapMs)
    {
        long margin = Math.Max(0, overlapMs) + WorstCasePrimeMs;
        return durationMs > 0 && durationMs < margin ? durationMs : margin;
    }

    /// <summary>True when the playhead sits inside the ending-soon window — the prepare chain must be armed NOW for the
    /// boundary to be consumable.</summary>
    public static bool IsEndingSoon(long durationMs, long positionMs, int overlapMs)
        => durationMs > 0 && durationMs - positionMs <= EndingSoonMarginMs(durationMs, overlapMs);

    /// <summary>True when a seek LANDED inside the ending-soon window (fix design §1: "on seek … re-prepare when the
    /// remaining time is below the margin"). The schedule's signature dedupe makes this free when the slot is already
    /// prepared for the unchanged (current, next) pair — the re-arm only matters when the earlier prepare failed or never
    /// ran.</summary>
    public static bool SeekRequiresRearm(long durationMs, long seekToMs, int overlapMs)
        => IsEndingSoon(durationMs, seekToMs, overlapMs);
}
