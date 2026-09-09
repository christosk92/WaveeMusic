using Wavee;
using Wavee.Backend.Playlists;
using Xunit;

namespace Wavee.Tests;

// The pure rule behind the header's "still syncing with Spotify" chip (§2.5.2). System + the Backend entry record only.
public class PlaylistSyncHealthRulesTests
{
    static PlaylistResyncQueue.Entry Entry(PlaylistResyncQueue.Phase phase, long sinceUtcMs, int attempts = 0)
        => new(phase, sinceUtcMs, attempts);

    [Fact]
    public void None_Decides_None()
    {
        var e = Entry(PlaylistResyncQueue.Phase.None, 0);
        Assert.Equal(PlaylistSyncHealth.None, PlaylistSyncHealthRules.Decide(e, nowUtcMs: 999_999));
    }

    [Fact]
    public void Failed_Decides_Failed_RegardlessOfAge()
    {
        var justNow = Entry(PlaylistResyncQueue.Phase.Failed, sinceUtcMs: 1000, attempts: 1);
        var longAgo = Entry(PlaylistResyncQueue.Phase.Failed, sinceUtcMs: 0, attempts: 4);
        Assert.Equal(PlaylistSyncHealth.Failed, PlaylistSyncHealthRules.Decide(justNow, nowUtcMs: 1000));
        Assert.Equal(PlaylistSyncHealth.Failed, PlaylistSyncHealthRules.Decide(longAgo, nowUtcMs: 1_000_000));
    }

    [Fact]
    public void Marked_BelowThreshold_IsNone_AtThreshold_IsSyncing()
    {
        var e = Entry(PlaylistResyncQueue.Phase.Marked, sinceUtcMs: 0);
        Assert.Equal(PlaylistSyncHealth.None, PlaylistSyncHealthRules.Decide(e, nowUtcMs: PlaylistSyncHealthRules.SyncingAfterMs - 1));
        Assert.Equal(PlaylistSyncHealth.Syncing, PlaylistSyncHealthRules.Decide(e, nowUtcMs: PlaylistSyncHealthRules.SyncingAfterMs));
    }

    [Fact]
    public void Revalidating_BehavesLikeMarked_ForTheThreshold()
    {
        var e = Entry(PlaylistResyncQueue.Phase.Revalidating, sinceUtcMs: 0);
        Assert.Equal(PlaylistSyncHealth.None, PlaylistSyncHealthRules.Decide(e, nowUtcMs: PlaylistSyncHealthRules.SyncingAfterMs - 1));
        Assert.Equal(PlaylistSyncHealth.Syncing, PlaylistSyncHealthRules.Decide(e, nowUtcMs: PlaylistSyncHealthRules.SyncingAfterMs));
    }

    [Fact]
    public void MsUntilSyncing_CountsDownAndClampsAtZero()
    {
        var e = Entry(PlaylistResyncQueue.Phase.Marked, sinceUtcMs: 0);
        Assert.Equal(PlaylistSyncHealthRules.SyncingAfterMs, PlaylistSyncHealthRules.MsUntilSyncing(e, nowUtcMs: 0));
        Assert.Equal(1, PlaylistSyncHealthRules.MsUntilSyncing(e, nowUtcMs: PlaylistSyncHealthRules.SyncingAfterMs - 1));
        Assert.Equal(0, PlaylistSyncHealthRules.MsUntilSyncing(e, nowUtcMs: PlaylistSyncHealthRules.SyncingAfterMs));
        Assert.Equal(0, PlaylistSyncHealthRules.MsUntilSyncing(e, nowUtcMs: PlaylistSyncHealthRules.SyncingAfterMs + 500));   // never negative
    }

    [Fact]
    public void MsUntilSyncing_IsZero_ForNoneAndFailed()
    {
        Assert.Equal(0, PlaylistSyncHealthRules.MsUntilSyncing(Entry(PlaylistResyncQueue.Phase.None, 0), nowUtcMs: 0));
        Assert.Equal(0, PlaylistSyncHealthRules.MsUntilSyncing(Entry(PlaylistResyncQueue.Phase.Failed, 0), nowUtcMs: 0));
    }
}
