// ── Wavee.Tests/ResumeStartTests.cs — A3's pure gate: where a Resume press actually starts ──────────────────────────
//
// The old rule was "resume at PosMs, whatever it is". Fine for a row the local pump tracked continuously; wrong the
// moment PosMs can be a single stale snapshot (a mirrored row — A2) or simply already at the row's own end: reloading
// exactly there is an inaudible instant. `Playback.ResumeStart.For` is the pure verdict `DoResume`'s parked branch
// (`PlaybackStepTests`) folds; this file tests the verdict alone, no reducer, no queue.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ResumeStartTests
{
    [Fact]
    public void A_position_well_short_of_the_end_resumes_exactly_there()
    {
        var verdict = Playback.ResumeStart.For(posMs: 90_000, durationMs: 244_960);

        Assert.Equal(Playback.ResumeStart.VerdictKind.StartAt, verdict.Kind);
        Assert.Equal(90_000, verdict.Ms);
    }

    [Fact]
    public void A_non_positive_position_starts_over_at_zero()
    {
        Assert.Equal(Playback.ResumeStart.VerdictKind.StartAtZero, Playback.ResumeStart.For(0, 200_000).Kind);
        Assert.Equal(Playback.ResumeStart.VerdictKind.StartAtZero, Playback.ResumeStart.For(-1, 200_000).Kind);
    }

    [Fact]
    public void Within_the_epsilon_of_a_known_duration_means_start_the_next_row()
    {
        // 244_960 - 1_500 = 243_460: exactly on the line still counts as "the end".
        var verdict = Playback.ResumeStart.For(posMs: 243_460, durationMs: 244_960);
        Assert.Equal(Playback.ResumeStart.VerdictKind.StartNext, verdict.Kind);
    }

    [Fact]
    public void One_millisecond_outside_the_epsilon_still_resumes_in_place()
    {
        var verdict = Playback.ResumeStart.For(posMs: 243_459, durationMs: 244_960);
        Assert.Equal(Playback.ResumeStart.VerdictKind.StartAt, verdict.Kind);
        Assert.Equal(243_459, verdict.Ms);
    }

    [Fact]
    public void A_position_past_the_duration_also_means_start_the_next_row()
    {
        var verdict = Playback.ResumeStart.For(posMs: 300_000, durationMs: 244_960);
        Assert.Equal(Playback.ResumeStart.VerdictKind.StartNext, verdict.Kind);
    }

    [Fact]
    public void An_unknown_duration_can_never_be_judged_near_the_end()
    {
        // The exact case a MIRRORED row can be resumed with no duration ever reported: it must not be treated as
        // "the end" just because there is nothing to compare against.
        var verdict = Playback.ResumeStart.For(posMs: 999_999, durationMs: 0);
        Assert.Equal(Playback.ResumeStart.VerdictKind.StartAt, verdict.Kind);
        Assert.Equal(999_999, verdict.Ms);
    }
}
