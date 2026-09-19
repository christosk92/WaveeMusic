// ── Wavee.Tests/MirrorSnapshotTests.cs — A2's pure gate: what a mirrored row's snapshot projects to ────────────────
//
// The bug (docs/plans/wavee/explain-why-palyback-is-indexed-cat.md, "what happened" #1-4): `MirrorRemote` used to
// stamp a mirrored position with OUR local clock and hand it to `DoTick`, which folded `Position()` back into
// `PosMs` every second with no owner test — a "playing" mirror nobody heard from again simply ratcheted, one second
// at a time, straight into the duration clamp and sat there. `Playback.MirrorSnapshot.Project` is the pure half of
// the fix: extrapolate ONCE, off the cluster's own clock, and say when the report is already too old to trust.
// `DoTick`'s owner gate (tested at the reducer level in `PlaybackStepTests`) is the other half.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class MirrorSnapshotTests
{
    [Fact]
    public void A_fresh_report_extrapolates_forward_by_the_server_clocks_own_gap()
    {
        // The push itself is 4 s old by the server's own clock (ServerTs - TimestampMs) — the position it carries
        // is exactly that far behind "now".
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: 10_000, wireTimestampMs: 1_000,
            serverNowMs: 5_000, playing: true, durationMs: 200_000);

        Assert.Equal(14_000, proj.PosMs);
        Assert.True(proj.Playing);
        Assert.False(proj.Stale);
    }

    [Fact]
    public void A_paused_report_never_extrapolates_or_goes_stale()
    {
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: 199_000, wireTimestampMs: 1_000,
            serverNowMs: 999_000_000, playing: false, durationMs: 200_000);

        Assert.Equal(199_000, proj.PosMs);
        Assert.False(proj.Playing);
        Assert.False(proj.Stale);                          // not advancing: there is nothing to run past the end
    }

    [Fact]
    public void A_report_whose_age_exceeds_what_was_left_of_the_row_is_stale_and_clamps_to_the_end()
    {
        // 90 s into a 245 s row, but the push carrying that fact is already 200 s old by the server's own clock —
        // more than the 155 s that was left. The track ended on the owner; mirror it PAUSED at the end, not
        // extrapolated past it.
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: 90_000, wireTimestampMs: 1_000,
            serverNowMs: 201_000, playing: true, durationMs: 245_000);

        Assert.Equal(245_000, proj.PosMs);
        Assert.False(proj.Playing);
        Assert.True(proj.Stale);
    }

    [Fact]
    public void Three_hours_of_silence_never_ratchets_past_the_duration_it_marks_stale_instead()
    {
        // The exact shape of the bug trace: a report taken once, then nothing for hours. `Project` is called again
        // here only to prove IT would answer "stale, clamped" rather than a number past the duration — `DoTick`'s
        // owner gate (PlaybackStepTests) is what stops this from ever being asked on every tick in production.
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: 90_000, wireTimestampMs: 1_000,
            serverNowMs: 1_000 + 3 * 3_600_000, playing: true, durationMs: 244_960);

        Assert.Equal(244_960, proj.PosMs);
        Assert.True(proj.PosMs <= 244_960);                // never past the duration, whatever the age
        Assert.False(proj.Playing);
        Assert.True(proj.Stale);
    }

    [Fact]
    public void An_unknown_duration_cannot_be_judged_stale_and_is_left_unextrapolated()
    {
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: 10_000, wireTimestampMs: 1_000,
            serverNowMs: 999_000, playing: true, durationMs: 0);

        Assert.Equal(10_000, proj.PosMs);                  // no duration to extrapolate against — the raw report
        Assert.True(proj.Playing);
        Assert.False(proj.Stale);
    }

    [Fact]
    public void No_server_time_on_the_push_means_no_extrapolation_rather_than_a_guess()
    {
        // serverNowMs = 0: "not carried" (ClusterFrame.ServerTs's own convention), never aged against.
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: 10_000, wireTimestampMs: 1_000,
            serverNowMs: 0, playing: true, durationMs: 200_000);

        Assert.Equal(10_000, proj.PosMs);
        Assert.True(proj.Playing);
        Assert.False(proj.Stale);
    }

    [Fact]
    public void A_position_past_the_duration_on_arrival_clamps_into_it()
    {
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: 999_999, wireTimestampMs: 1_000,
            serverNowMs: 1_000, playing: false, durationMs: 200_000);

        Assert.Equal(200_000, proj.PosMs);
    }

    [Fact]
    public void A_negative_position_clamps_to_zero()
    {
        var proj = Playback.MirrorSnapshot.Project(positionAsOfMs: -5, wireTimestampMs: 1_000,
            serverNowMs: 1_000, playing: false, durationMs: 200_000);

        Assert.Equal(0, proj.PosMs);
    }
}
