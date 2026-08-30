using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// The dealer router now DECODES + ENQUEUES onto the LibrarySync loop (the single writer); the in-place apply / mark-dirty /
// refetch policy lives in the loop. Drive real pushes through a real router + real loop (StubTransport.PushEvent) and assert
// the store outcome. Barrier: LibrarySync.WaitForIdleAsync (a FIFO no-op behind the enqueued push).
public class DealerRouterTests
{
    // A Spotify user id is 28 chars — with it the head-only rootlist PMI is the exact 78-byte frame the dealer sends.
    const string User = "31abcdefghijklmnopqrstuvwxyz";
    static string RootlistTopicV2 => "hm://playlist/v2/user/" + User + "/rootlist";
    static string RootlistTopic => "hm://playlist/user/" + User + "/rootlist";

    static PlaylistMember M(string id) => new(id, "spotify:track:" + id, null, 0);

    /// <summary>I1 — a real playlist4 head: 4-byte big-endian counter + 20-byte hash.</summary>
    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }

    static Pl.PlaylistModificationInfo Mod(string uri, byte[]? parent, byte[]? newRev, params Pl.Op[] ops)
    {
        var info = new Pl.PlaylistModificationInfo { Uri = ByteString.CopyFromUtf8(uri) };
        if (parent is not null) info.ParentRevision = ByteString.CopyFrom(parent);
        if (newRev is not null) info.NewRevision = ByteString.CopyFrom(newRev);
        info.Ops.AddRange(ops);
        return info;
    }

    static Pl.Op Rem(int from, int len) => new() { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = from, Length = len } };

    // ── playlist pushes (§2.2 gate tree) ──
    [Fact]
    public async Task PlaylistPush_ParentRevMatch_AppliesOpsInPlace_AndAdvancesRevision()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", Rev24(1), Rev24(2), Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        var m = h.Store.Membership("spotify:playlist:p");
        Assert.Equal("spotify:track:b", Assert.Single(m).ItemUri);
        Assert.Equal(Rev24(2), h.Store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal(1, h.Sync.PushApplied);
        Assert.Equal(0, h.PlaylistGets);   // zero network
    }

    [Fact]
    public async Task PlaylistPush_ParentRevMismatch_MarksDirty_NoFetch()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", Rev24(9), Rev24(10), Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, h.Store.Membership("spotify:playlist:p").Count);   // unchanged
        Assert.Equal(1, h.Sync.PushMarkedDirty);
        Assert.Equal(0, h.PlaylistGets);                                   // anti-herd: no fetch
    }

    [Fact]
    public async Task PlaylistPush_Echo_StoredEqualsNewRev_NoOp()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(5));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        // new_revision == stored → an echo of our own write → dropped before any store work.
        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", Rev24(4), Rev24(5), Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, h.Store.Membership("spotify:playlist:p").Count);
        Assert.Equal(1, h.Sync.EchoDropped);
        Assert.Equal(0, h.Sync.PushApplied);
    }

    [Fact]
    public async Task PlaylistPush_TornApply_FallsToDirty()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        // parent matches but REM [0,+5] doesn't fit → torn apply → gate 5 (not open) → mark dirty, no fetch.
        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", Rev24(1), Rev24(2), Rem(0, 5)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, h.Store.Membership("spotify:playlist:p").Count);   // unchanged
        Assert.Equal(1, h.Sync.PushMarkedDirty);
        Assert.Equal(0, h.PlaylistGets);
    }

    // A frame with no storable head AND no ops is unactionable — the dealer sends these (a liked-songs-artist list topic
    // carries a non-playlist uri and no revision at all). It must be a logged drop, never a throw and never a store write.
    [Fact]
    public async Task PlaylistPush_NoHeadNoOps_LogsDrop_NoEnqueue()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        var info = new Pl.PlaylistModificationInfo { Uri = ByteString.CopyFromUtf8("spotify:user:" + User + ":collection:artist:x") };
        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/list/liked-songs-artist/" + User, info.ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, router.DealerDrops);
        Assert.Equal(0, h.Sync.PushMarkedDirty);
        Assert.Equal(0, h.PlaylistGets);
        Assert.Equal(Rev24(1), h.Store.PlaylistRevision("spotify:playlist:p"));
    }

    // ── rootlist pushes (§2.2 / §2.8) ──
    // The REAL wire shape: a head-only PlaylistModificationInfo (uri + 24-byte new_revision, NO parent, NO ops),
    // delivered twice — once per topic. 78 bytes exactly.
    static byte[] RootlistHeadPmi(byte[] newRev)
    {
        var info = new Pl.PlaylistModificationInfo
        {
            Uri = ByteString.CopyFromUtf8("spotify:user:" + User + ":rootlist"),
            NewRevision = ByteString.CopyFrom(newRev),
        };
        return info.ToByteArray();
    }

    // The sibling shape, still supported: a genuine RootlistModificationInfo carrying ops.
    static byte[] RootMod(byte[] parent, byte[] newRev, params Pl.Op[] ops)
    {
        var info = new Pl.RootlistModificationInfo { ParentRevision = ByteString.CopyFrom(parent), NewRevision = ByteString.CopyFrom(newRev) };
        info.Ops.AddRange(ops);
        return info.ToByteArray();
    }

    static Func<HttpReq, HttpResp> RootlistResponder(byte[] rev, params string[] uris)
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev) };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        foreach (var u in uris) contents.Items.Add(new Pl.Item { Uri = u });
        slc.Contents = contents;
        var body = slc.ToByteArray();
        return req => req.Url.Contains("/rootlist") ? SyncHarness.Ok(body) : SyncHarness.Ok(Array.Empty<byte>());
    }

    [Fact]
    public async Task RootlistPush_HeadOnlyPMI_RevMismatch_FullGets_OncePerPair()
    {
        await using var h = new SyncHarness(RootlistResponder(Rev24(7), "spotify:playlist:fresh"));
        h.Store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:old", null, 0) }, Rev24(1));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        var payload = RootlistHeadPmi(Rev24(2));
        Assert.Equal(78, payload.Length);                                   // the exact frame the dealer sends

        h.Dealer.PushEvent(new WireEvent(RootlistTopicV2, payload));        // both copies of ONE head
        h.Dealer.PushEvent(new WireEvent(RootlistTopic, payload));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, router.RootlistPushDeduped);                        // second copy never reached the loop
        Assert.Equal(1, h.RootlistGets);                                    // ONE full GET for the pair
        Assert.Equal("spotify:playlist:fresh", Assert.Single(h.Store.Rootlist()).Uri);
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:fresh"));   // fold ran after the fetch
        Assert.Equal(Rev24(7), h.Store.RootlistRevision());
    }

    [Fact]
    public async Task RootlistPush_HeadOnly_StoredEqualsNew_EchoDrops()
    {
        await using var h = new SyncHarness(RootlistResponder(Rev24(7), "spotify:playlist:fresh"));
        h.Store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0) }, Rev24(3));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent(RootlistTopicV2, RootlistHeadPmi(Rev24(3))));   // the echo of our own write
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.RootlistEchoDropped);
        Assert.Equal(0, h.RootlistGets);
        Assert.Equal(Rev24(3), h.Store.RootlistRevision());
        Assert.Equal("spotify:playlist:p1", Assert.Single(h.Store.Rootlist()).Uri);
    }

    // The regression itself: parsed as a RootlistModificationInfo, field 1 of this frame (the URI) lands in
    // new_revision and used to be PERSISTED as the rootlist revision (and pushed into SQLite meta).
    [Fact]
    public async Task RootlistPush_UriBytesNeverPersistAsRevision()
    {
        await using var h = new SyncHarness(RootlistResponder(Rev24(7), "spotify:playlist:fresh"));
        h.Store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:old", null, 0) }, Rev24(1));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent(RootlistTopicV2, RootlistHeadPmi(Rev24(2))));
        await h.Sync.WaitForIdleAsync();

        var stored = h.Store.RootlistRevision();
        Assert.True(PlaylistRevisions.IsWellFormed(stored));
        Assert.NotEqual(Encoding.UTF8.GetBytes("spotify:user:" + User + ":rootlist"), stored);
        Assert.Equal(Rev24(7), stored);   // the head from the authoritative GET, never wire junk
    }

    [Fact]
    public async Task RootlistPush_RmiWithOps_ParentMatch_AppliesInPlace()
    {
        await using var h = new SyncHarness(RootlistResponder(Rev24(7), "spotify:playlist:fresh"));
        h.Store.SetRootlist(new[]
        {
            new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0),
            new RootlistEntry(1, 0, "spotify:playlist:p2", null, 0),
        }, Rev24(1));
        h.Store.SetSaved("playlists", "spotify:playlist:p1", true, SyncState.Confirmed);   // will be swept by the fold
        h.Mut.Save("playlists", "spotify:playlist:shield", true);                          // pending → shielded from removal
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent(RootlistTopic, RootMod(Rev24(1), Rev24(2), Rem(0, 1))));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal("spotify:playlist:p2", Assert.Single(h.Store.Rootlist()).Uri);         // p1 removed in place
        Assert.Equal(Rev24(2), h.Store.RootlistRevision());
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p2"));                    // fold ADDED the survivor
        Assert.False(h.Store.IsSaved("playlists", "spotify:playlist:p1"));                   // fold REMOVED the departed
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:shield"));                // pending → survived the fold
        Assert.Equal(1, h.Sync.RootlistApplied);
        Assert.Equal(0, h.RootlistGets);
    }

    [Fact]
    public async Task RootlistPush_Unparseable_LogsDrop_NoStoreWrite()
    {
        await using var h = new SyncHarness(RootlistResponder(Rev24(7), "spotify:playlist:fresh"));
        h.Store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0) }, Rev24(3));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent(RootlistTopicV2, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));   // not a protobuf
        h.Dealer.PushEvent(new WireEvent(RootlistTopic, RootlistHeadPmi(new byte[] { 1, 2, 3 })));   // head that can never be stored
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, router.DealerDrops);
        Assert.Equal(0, router.RootlistPushDeduped);
        Assert.Equal(0, h.RootlistGets);                          // nothing was enqueued at all
        Assert.Equal(Rev24(3), h.Store.RootlistRevision());        // store untouched
        Assert.Equal("spotify:playlist:p1", Assert.Single(h.Store.Rootlist()).Uri);
    }

    // ── permission pushes (P1) ────────────────────────────────────────────────────────────────────────────────────────
    // hm://playlist-permission/v1/playlist/{id}/permission/state carries the whole answer. The topic supplies the uri
    // (the payload has none) and the base permission supplies public/private — so a resident header converges with ZERO
    // network. This is what replaces the detail page's own permission GET.
    const string PermTopic = "hm://playlist-permission/v1/playlist/p/permission/state";

    static byte[] PermissionState(Pl.PermissionLevel level, byte[]? revision = null,
                                  bool isPrivate = false, bool isCollaborative = false)
    {
        var basePerm = new Pl.Permission { PermissionLevel = level };
        if (revision is not null) basePerm.Revision = ByteString.CopyFrom(revision);
        return new Pl.PermissionStatePub
        {
            PermissionState = new Pl.PermissionState
            {
                Permissions = new Pl.Permissions { BasePermission = basePerm },
                IsPrivate = isPrivate,
                IsCollaborative = isCollaborative,
            },
        }.ToByteArray();
    }

    static Playlist OwnedHeader(bool isPublic) => new(
        "p", "spotify:playlist:p", "Mix", null, "bob", null, 0, IsPublic: isPublic,
        Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: true,
            IsCollaborative: false, IsOwner: true, CanAdministratePermissions: true));

    [Fact]
    public async Task PermissionPush_FlipsIsPublic_NoGet()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.UpsertPlaylist(OwnedHeader(isPublic: false));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent(PermTopic,
            PermissionState(Pl.PermissionLevel.Viewer, new byte[] { 0xDE, 0xAD }, isCollaborative: true)));
        await h.Sync.WaitForIdleAsync();

        var header = h.Store.GetPlaylist("spotify:playlist:p")!;
        Assert.True(header.IsPublic);                                   // BLOCKED -> VIEWER means public
        Assert.Equal("dead", header.BasePermissionRevision);            // the permission chain revision, as hex
        Assert.True(header.Capabilities.IsCollaborative);
        Assert.Equal(1, h.Sync.PermissionPushesApplied);
        Assert.Equal(0, h.PlaylistGets);                                // zero HTTP …
        Assert.Empty(h.TransportRoutes);                                // … and zero transport round-trips
    }

    [Fact]
    public async Task PermissionPush_Blocked_MakesItPrivate()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.UpsertPlaylist(OwnedHeader(isPublic: true));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent(PermTopic, PermissionState(Pl.PermissionLevel.Blocked, new byte[] { 1 }, isPrivate: true)));
        await h.Sync.WaitForIdleAsync();

        Assert.False(h.Store.GetPlaylist("spotify:playlist:p")!.IsPublic);
    }

    // No base_permission = nothing to adopt. I7: a logged drop, never a guess and never a store write.
    [Fact]
    public async Task PermissionPush_MissingBase_LogsDrop()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.UpsertPlaylist(OwnedHeader(isPublic: false));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        var empty = new Pl.PermissionStatePub { PermissionState = new Pl.PermissionState { IsPrivate = true } }.ToByteArray();
        h.Dealer.PushEvent(new WireEvent(PermTopic, empty));
        h.Dealer.PushEvent(new WireEvent(PermTopic, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));   // not a protobuf either
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, router.DealerDrops);
        Assert.Equal(0, h.Sync.PermissionPushesApplied);
        Assert.False(h.Store.GetPlaylist("spotify:playlist:p")!.IsPublic);   // untouched
    }

    // A cold header cannot be patched, and fetching one per push for a playlist nobody is looking at is pure herd:
    // the state is seeded on open instead.
    [Fact]
    public async Task PermissionPush_ColdHeader_IsIgnored_NoFetch()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent(PermTopic, PermissionState(Pl.PermissionLevel.Viewer)));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PermissionPushesIgnored);
        Assert.Equal(0, h.PlaylistGets);
        Assert.Empty(h.TransportRoutes);
        Assert.Null(h.Store.GetPlaylist("spotify:playlist:p"));
    }
}
