using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// VideoSwitchPolicy is the pure decision table behind FluentVideoMediaHost.ApplyAsync: given what the host currently
// knows about its live session, what should a new load DO? Table-driven over every HasPlayer/LiveFaulted/key/StartAtMs
// combination that matters, so the four actions (None / SeekOnly / Switch / Rebuild) stay exhaustively pinned against
// production code rather than re-derived ad hoc inside the host.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class VideoSwitchPolicyTests
{
    [Theory]
    // ── no player yet (the very first video of the session) — always a Rebuild, key/start irrelevant ────────────────
    [InlineData(false, false, "", "A", 0, VideoSwitchAction.Rebuild)]
    [InlineData(false, false, "", "A", 5_000, VideoSwitchAction.Rebuild)]
    [InlineData(false, false, "A", "A", 0, VideoSwitchAction.Rebuild)]      // stale LiveKey left over from before a Stop
    [InlineData(false, true, "", "A", 0, VideoSwitchAction.Rebuild)]        // HasPlayer false already forces Rebuild

    // ── a live but FAULTED session — always a Rebuild, even for the SAME key (its native/MF state is not trustworthy) ─
    [InlineData(true, true, "A", "A", 0, VideoSwitchAction.Rebuild)]
    [InlineData(true, true, "A", "A", 5_000, VideoSwitchAction.Rebuild)]
    [InlineData(true, true, "A", "B", 0, VideoSwitchAction.Rebuild)]

    // ── a healthy live session, SAME key — a genuine re-entry, not a track change ───────────────────────────────────
    [InlineData(true, false, "A", "A", 0, VideoSwitchAction.None)]          // redundant re-entry: never restart from 0
    [InlineData(true, false, "A", "A", -1, VideoSwitchAction.None)]         // a non-positive carried position is still "no reposition"
    [InlineData(true, false, "A", "A", 1, VideoSwitchAction.SeekOnly)]      // any positive StartAtMs is a deliberate reposition
    [InlineData(true, false, "A", "A", 126_034, VideoSwitchAction.SeekOnly)]

    // ── a healthy live session, DIFFERENT key — the video-smooth-switching win: switch in place, never a rebuild ────
    [InlineData(true, false, "A", "B", 0, VideoSwitchAction.Switch)]
    [InlineData(true, false, "A", "B", 5_000, VideoSwitchAction.Switch)]    // a carried start rides the Switch's own open, not a seek
    [InlineData(true, false, "", "B", 0, VideoSwitchAction.Switch)]         // an (unexpected) empty live key still differs from "B"

    // ── empty-key edge cases: "" vs "" is still equality, not a mismatch ────────────────────────────────────────────
    [InlineData(true, false, "", "", 0, VideoSwitchAction.None)]
    [InlineData(true, false, "", "", 250, VideoSwitchAction.SeekOnly)]
    public void Plan_MatchesTheDecisionTable(bool hasPlayer, bool liveFaulted, string liveKey, string requestKey,
        long startAtMs, VideoSwitchAction expected)
    {
        var input = new VideoSwitchInput(hasPlayer, liveFaulted, liveKey, requestKey, startAtMs);
        Assert.Equal(expected, VideoSwitchPolicy.Plan(input));
    }

    [Fact]
    public void KeyComparison_IsOrdinal_NotCaseInsensitive()
    {
        // Source keys are manifest ids / URLs (or "local:video:<id>") — never meant to case-fold. A policy that folded
        // case could wrongly treat two DISTINCT case-differing manifest ids as "the same track" (None/SeekOnly) instead
        // of switching.
        var input = new VideoSwitchInput(HasPlayer: true, LiveFaulted: false, LiveKey: "spotify:manifest:ABC",
            RequestKey: "spotify:manifest:abc", StartAtMs: 0);
        Assert.Equal(VideoSwitchAction.Switch, VideoSwitchPolicy.Plan(input));
    }
}
