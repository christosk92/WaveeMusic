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

}
