// ── Wavee.Tests/AiLyricsLedgerTests.cs — which job and which load are current ────────────────────────────────────────
//
// The host drops every worker post whose ticket is no longer current. These are the cases that produced real bugs: a
// progress post after a cancel brought "Working" back; a load that finished after the feature went off flipped the card;
// the idle unload counted from app start and threw away the boot preload.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AiLyricsLedgerTests
{
    const long Minute = 60_000;

    [Fact]
    public void A_post_from_a_cancelled_job_is_stale()
    {
        var l = new AiLyrics.Ledger();
        int t = l.BeginJob("a");
        Assert.True(l.IsCurrentJob(t, "a"));
        l.EndJob();                                   // cancel: people-made word timing arrived
        Assert.False(l.IsCurrentJob(t, "a"));
        Assert.False(l.JobRunning);
    }

    [Fact]
    public void A_restarted_job_for_the_same_track_does_not_accept_the_old_jobs_posts()
    {
        var l = new AiLyrics.Ledger();
        int first = l.BeginJob("a");
        l.EndJob();
        int second = l.BeginJob("a");
        Assert.False(l.IsCurrentJob(first, "a"));
        Assert.True(l.IsCurrentJob(second, "a"));
        Assert.False(l.IsCurrentJob(second, "b"));
    }

    [Fact]
    public void A_load_that_finishes_after_the_feature_went_off_is_stale()
    {
        var l = new AiLyrics.Ledger();
        int load = l.BeginLoad();
        l.InvalidateLoads();                              // SetEnabled(false), RemoveFiles, RemoveLanguage
        Assert.False(l.IsCurrentLoad(load));
        int next = l.BeginLoad();
        Assert.True(l.IsCurrentLoad(next));
    }
}
