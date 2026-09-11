using System;

namespace Wavee.Backend;

// Bug 3 — video duration adopted from the previous video. FluentVideoMediaHost.DurationKnown names the SOURCE KEY
// it measured, but the event is delivered asynchronously (off the media engine's own thread) — by the time it
// arrives, the host may already be building/playing a DIFFERENT key (a fast switch racing the old player's own
// late duration report). LiveConnect used to adopt any DurationKnown unconditionally:
//   video duration adopted for 48YTx…: 232231 ms (source d397…)
//   video duration adopted for 48YTx…: 256000 ms (source <old key>)
// — the second line is the PREVIOUS video's length landing on the CURRENT track. Engine-free pure decision so it is
// unit-testable without FluentVideoMediaHost/the video engine (per CLAUDE.md — no source-text tests; the decision
// lives in a pure class).
public static class VideoDurationAdoption
{
    /// <summary><paramref name="currentKey"/> is the video host's live source key RIGHT NOW
    /// (<c>FluentVideoMediaHost.CurrentSourceKey</c>); <paramref name="eventKey"/> is the key the DurationKnown
    /// event itself named. Fail-open when nothing is currently known (null/empty <paramref name="currentKey"/> — an
    /// unwired/cold host must not silently drop every duration); refuse only a KNOWN mismatch.</summary>
    public static bool ShouldAdopt(string? currentKey, string? eventKey)
        => string.IsNullOrEmpty(currentKey) || string.Equals(currentKey, eventKey, StringComparison.Ordinal);
}
