using System;
using System.IO;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Xunit;

namespace Wavee.Tests;

// The Store spine now also holds ordered playlist membership + the rootlist (the queryable lists the catalog joins on).
public class StoreMembershipTests
{
    static PlaylistMember M(string id) => new(id, "spotify:track:" + id, null, 0);
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    [Fact]
    public void InMemory_Membership_RoundTrips()
    {
        var s = new InMemoryStore();
        var rev = new byte[] { 1, 2 };
        s.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, rev);
        var m = s.Membership("spotify:playlist:p");
        Assert.Equal(2, m.Count);
        Assert.Equal("spotify:track:a", m[0].ItemUri);
        Assert.Equal(rev, s.PlaylistRevision("spotify:playlist:p"));
    }

    [Fact]
    public void InMemory_KnownEmptyMembership_IsDistinctFromMissing()
    {
        var s = new InMemoryStore();
        Assert.False(s.HasMembership("spotify:playlist:missing"));
        s.SetMembership("spotify:playlist:empty", Array.Empty<PlaylistMember>(), null);
        Assert.True(s.HasMembership("spotify:playlist:empty"));
        Assert.Empty(s.Membership("spotify:playlist:empty"));
    }

    [Fact]
    public void InMemory_Rootlist_RoundTrips()
    {
        var s = new InMemoryStore();
        s.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0) });
        Assert.Equal("spotify:playlist:p1", Assert.Single(s.Rootlist()).Uri);
    }

    [Fact]
    public void Cached_Membership_PersistsAndLazyLoadsOnReopen()
    {
        var path = TempDb();
        try
        {
            using (var s = new CachedStore(new SqliteColdStore(path)))
                s.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 9 });   // ReplaceMembership is synchronous

            using var s2 = new CachedStore(new SqliteColdStore(path));
            var m = s2.Membership("spotify:playlist:p");   // not resident → lazy-load from the cold tier
            Assert.Equal(2, m.Count);
            Assert.Equal("spotify:track:b", m[1].ItemUri);
            Assert.Equal(new byte[] { 9 }, s2.PlaylistRevision("spotify:playlist:p"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Membership_WarmLru_EvictsLeastRecentlyUsed_AndReloadsFromCold()
    {
        var path = TempDb();
        try
        {
            using var store = new CachedStore(new SqliteColdStore(path), maxResidentPlaylists: 2);
            store.SetMembership("spotify:playlist:p1", new[] { M("a") }, null);
            store.SetMembership("spotify:playlist:p2", new[] { M("b") }, null);
            store.Membership("spotify:playlist:p1");                              // touch p1 → p2 is now the LRU
            store.SetMembership("spotify:playlist:p3", new[] { M("c") }, null);   // count 3 > cap 2 → evict the LRU (p2)

            Assert.Equal(2, store.ResidentMembershipCount);                       // bounded to the cap
            // p2 was evicted from the resident mirror but the cold tier still has it (correctness preserved on reload).
            Assert.Equal("spotify:track:b", Assert.Single(store.Membership("spotify:playlist:p2")).ItemUri);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Cached_Rootlist_PersistsAndLazyLoadsOnReopen()
    {
        var path = TempDb();
        try
        {
            using (var s = new CachedStore(new SqliteColdStore(path)))
                s.SetRootlist(new[]
                {
                    new RootlistEntry(0, 1, "spotify:start-group:g:F", "F", 0),
                    new RootlistEntry(1, 0, "spotify:playlist:p", null, 1),
                });

            using var s2 = new CachedStore(new SqliteColdStore(path));
            var rl = s2.Rootlist();
            Assert.Equal(2, rl.Count);
            Assert.Equal("F", rl[0].GroupName);
            Assert.Equal("spotify:playlist:p", rl[1].Uri);
        }
        finally { TryDelete(path); }
    }
}
