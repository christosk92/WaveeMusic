using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Xunit;

namespace Wavee.Tests;

// "Server says N, we hold M": the per-logical-set comparison behind the reconcile pass. Shielded members (pending intent,
// recently added) are never drift in either direction.
public class CollectionDriftTests
{
    const long Walk = 1_800_000_000_000L;
    static SavedItem Local(string uri, long at) => new(uri, at);
    static HashSet<string> Server(params string[] uris) => new(uris, System.StringComparer.Ordinal);

    [Fact]
    public void Identical_IsNoDrift()
    {
        var r = CollectionDrift.Compare("liked", new[] { Local("spotify:track:a", 1), Local("spotify:track:b", 2) },
            Server("spotify:track:a", "spotify:track:b"), hasPending: null, Walk);

        Assert.False(r.HasDrift);
        Assert.Equal(2, r.Local);
        Assert.Equal(2, r.Server);
        Assert.Empty(r.Missing);
        Assert.Empty(r.Extra);
        Assert.Equal("liked", r.SetId);
    }

    [Fact]
    public void MissingAndExtra_AreReportedByUri()
    {
        // The 274-vs-293 shape: the server holds newer members we never got; we hold one it dropped long ago.
        var r = CollectionDrift.Compare("liked", new[] { Local("spotify:track:a", 1), Local("spotify:track:stale", Walk - 3_600_000) },
            Server("spotify:track:a", "spotify:track:new1", "spotify:track:new2"), hasPending: null, Walk);

        Assert.True(r.HasDrift);
        Assert.Equal(2, r.Local);
        Assert.Equal(3, r.Server);
        Assert.Equal(new[] { "spotify:track:new1", "spotify:track:new2" }, r.Missing);
        Assert.Equal(new[] { "spotify:track:stale" }, r.Extra);
    }

    [Fact]
    public void ShieldedMembers_AreNotDrift_InEitherDirection()
    {
        // pendingLike: local, not on the server, intent in flight → not extra.
        // recent:      local, not on the server, liked a minute before the walk → not extra (write→page lag).
        // pendingUnsave: on the server, not local, intent in flight → not missing.
        var r = CollectionDrift.Compare("liked",
            new[] { Local("spotify:track:a", 1), Local("spotify:track:pendingLike", Walk), Local("spotify:track:recent", Walk - 60_000) },
            Server("spotify:track:a", "spotify:track:pendingUnsave"),
            hasPending: u => u is "spotify:track:pendingLike" or "spotify:track:pendingUnsave", Walk);

        Assert.False(r.HasDrift);
        Assert.Equal(3, r.Local);
        Assert.Equal(2, r.Server);
    }
}
