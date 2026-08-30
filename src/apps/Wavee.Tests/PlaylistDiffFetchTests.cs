using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// Phase 5 (§2.6, RC5) — revision-gated /diff revalidation: the wire revision string, the outcome tree (Applied /
// UpToDate(304 + up_to_date) / FellBackToFull on 509, torn apply, missing baseline), added-uris-only hydration, the zstd
// response guard, and the LibrarySync revalidate path riding it.
public class PlaylistDiffFetchTests
{
    const string Uri = "spotify:playlist:x";
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A STORABLE wire revision (I1): the 24-byte playlist4 head — 4-byte big-endian counter + a 20-byte hash whose
    // LEADING bytes are the ones a test names (the rest stay zero, so the "counter,hexprefix" assertions still hold).
    static byte[] Rev(int counter, params byte[] hash)
    {
        var b = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, counter);
        hash.CopyTo(b, 4);
        return b;
    }

    // The unpadded shape, for the pure FormatRevision string test (it asserts the WHOLE string, not a prefix).
    static byte[] RawRev(int counter, params byte[] hash)
    {
        var b = new byte[4 + hash.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, counter);
        hash.CopyTo(b, 4);
        return b;
    }

    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    static HttpResp Status(int status) => new(status, new Dictionary<string, string>(), Array.Empty<byte>());

    static byte[] FullSlc(byte[] rev, params string[] uris)
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev) };
        var c = new Pl.ListItems { Pos = 0, Truncated = false };
        foreach (var u in uris) c.Items.Add(new Pl.Item { Uri = u });
        slc.Contents = c;
        return slc.ToByteArray();
    }

    static byte[] DiffSlc(byte[] fromRev, byte[] toRev, params Pl.Op[] ops)
    {
        var diff = new Pl.Diff { FromRevision = ByteString.CopyFrom(fromRev), ToRevision = ByteString.CopyFrom(toRev) };
        foreach (var op in ops) diff.Ops.Add(op);
        return new Pl.SelectedListContent { Diff = diff }.ToByteArray();
    }

    static Pl.Op AddLast(string uri)
    {
        var add = new Pl.Add { AddLast = true };
        add.Items.Add(new Pl.Item { Uri = uri });
        return new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = add };
    }

    static Pl.Op UpdateListAttributes()
        => new()
        {
            Kind = Pl.Op.Types.Kind.UpdateListAttributes,
            UpdateListAttributes = new Pl.UpdateListAttributes(),
        };

    static (PlaylistFetcher Fetcher, InMemoryStore Store, List<string> Hydrated, List<HttpReq> Reqs) Rig(Func<HttpReq, int, HttpResp> respond)
    {
        var store = new InMemoryStore();
        var hydrated = new List<string>();
        var reqs = new List<HttpReq>();
        var http = new FakeExchange((req, n) => { lock (reqs) reqs.Add(req); return respond(req, n); });
        Task Hydrate(IReadOnlyList<string> uris, CancellationToken c) { lock (hydrated) hydrated.AddRange(uris); return Task.CompletedTask; }
        return (new PlaylistFetcher(http, () => "https://x", store, Hydrate, () => ""), store, hydrated, reqs);
    }

    // A resident baseline stamped WITH item attributes (added_by + added_at) — a normally-hydrated playlist, so the
    // LibrarySync open path takes the intended /diff revalidate, not the attribute-less heal-to-full fetch.
    static void Seed(IStore store, byte[] rev, params string[] uris)
    {
        var rows = new List<PlaylistMember>(uris.Length);
        for (int i = 0; i < uris.Length; i++) rows.Add(new PlaylistMember("id" + i, uris[i], "seed", 1_700_000_000_000L));
        store.SetMembership(Uri, rows, rev);
    }

    // ── the revision wire string: "{int32BE counter},{lowerhex hash}", comma %2C-encoded on the query ────────────────
    [Fact]
    public void FormatRevision_CounterCommaLowerHex()
    {
        Assert.Equal("123,ab12cd", PlaylistFetcher.FormatRevision(RawRev(123, 0xAB, 0x12, 0xCD)));
        Assert.Contains("%2C", System.Uri.EscapeDataString(PlaylistFetcher.FormatRevision(RawRev(1, 0xFF))));
    }

    // ── 200 + diff → ops applied in place, revision advances, ONLY added uris hydrate ─────────────────────────────────
    [Fact]
    public async Task Diff_Applied_InPlace_RevisionAdvances_AddedOnlyHydration()
    {
        var from = Rev(1, 0xAA);
        var to = Rev(2, 0xBB);
        var (f, store, hydrated, reqs) = Rig((req, _) => Ok(DiffSlc(from, to, AddLast("spotify:track:new"))));
        Seed(store, from, "spotify:track:t1", "spotify:track:t2");

        var outcome = await f.FetchPlaylistDiffAsync(Uri, Ct);

        Assert.Equal(DiffOutcome.Applied, outcome);
        var url = Assert.Single(reqs).Url;
        Assert.Contains("/playlist/v2/playlist/x/diff?revision=1%2Caa", url);   // %2C-encoded "counter,hex"
        Assert.Contains("&handlesContent=", url);                                // required empty param
        Assert.Contains("hint_revision=1%2Caa", url);
        var members = store.Membership(Uri);
        Assert.Equal(3, members.Count);
        Assert.Equal("spotify:track:new", members[2].ItemUri);
        Assert.Equal(to, store.PlaylistRevision(Uri));
        Assert.Equal(new[] { "spotify:track:new" }, hydrated);                   // NOT the whole list
    }

    // ── 304 and up_to_date both resolve UpToDate with the store untouched ─────────────────────────────────────────────
    [Fact]
    public async Task Diff_UpdateListAttributes_RefetchesHeader()
    {
        var from = Rev(1, 0xAA);
        var to = Rev(2, 0xBB);
        var header = new Pl.SelectedListContent { Length = 3, OwnerUsername = "bob" };
        header.Attributes = new Pl.ListAttributes { Name = "Renamed", Description = "fresh" };
        var (f, store, hydrated, reqs) = Rig((req, _) =>
            req.Url.Contains("/diff?") ? Ok(DiffSlc(from, to, UpdateListAttributes())) : Ok(header.ToByteArray()));
        Seed(store, from, "spotify:track:t1");

        var outcome = await f.FetchPlaylistDiffAsync(Uri, Ct);

        Assert.Equal(DiffOutcome.Applied, outcome);
        Assert.Equal(2, reqs.Count);
        Assert.Contains("/diff?", reqs[0].Url);
        Assert.DoesNotContain("/diff?", reqs[1].Url);
        Assert.Empty(hydrated);
        var playlist = store.GetPlaylist(Uri);
        Assert.NotNull(playlist);
        Assert.Equal("Renamed", playlist.Name);
        Assert.Equal("fresh", playlist.Description);
        Assert.Equal(3, playlist.TrackCount);
    }

    [Fact]
    public async Task Diff_304_And_UpToDate_LeaveStoreUntouched()
    {
        foreach (var respond in new Func<HttpReq, int, HttpResp>[]
        {
            (_, _) => Status(304),
            (_, _) => Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()),
        })
        {
            var rev = Rev(7, 0x01);
            var (f, store, hydrated, _) = Rig(respond);
            Seed(store, rev, "spotify:track:t1");
            long ver = store.Version(Uri);

            Assert.Equal(DiffOutcome.UpToDate, await f.FetchPlaylistDiffAsync(Uri, Ct));
            Assert.Equal(ver, store.Version(Uri));   // no membership write
            Assert.Empty(hydrated);
        }
    }

    // ── a ROLLING-IDENTITY playlist (a daylist) still needs its header re-read on an UpToDate verdict (cause 4 of the
    // stale-daylist-header defect): the server can swap the whole edition without a single op a /diff would carry, so
    // "nothing changed" per the diff body is not "the header is current" for this shape of playlist. ─────────────────
    [Fact]
    public async Task Diff_UpToDate_OnARollingIdentityPlaylist_StillRefreshesTheHeader()
    {
        var rev = Rev(7, 0x01);
        var header = new Pl.SelectedListContent { Length = 1, OwnerUsername = "spotify" };
        header.Attributes = new Pl.ListAttributes { Name = "New Edition", Format = "daylist" };
        var (f, store, _, reqs) = Rig((req, _) =>
            req.Url.Contains("/diff?") ? Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()) : Ok(header.ToByteArray()));
        Seed(store, rev, "spotify:track:t1");
        store.UpsertPlaylist(new Playlist("x", Uri, "Old Edition", null, "spotify", null, 1) { Format = "daylist" });

        var outcome = await f.FetchPlaylistDiffAsync(Uri, Ct);

        Assert.Equal(DiffOutcome.UpToDate, outcome);
        // Diff_304_And_UpToDate_LeaveStoreUntouched (above) pins the NON-rolling half: one request, store untouched.
        // This playlist rolls, so the same UpToDate verdict still costs the header GET and adopts its new name.
        Assert.Equal(2, reqs.Count);
        Assert.Equal("New Edition", store.GetPlaylist(Uri)!.Name);
    }

    // ── 509 (revision too stale) falls back to the full fetch ─────────────────────────────────────────────────────────
    [Fact]
    public async Task Diff_509_FallsBackToFullFetch()
    {
        var fresh = Rev(9, 0x99);
        var (f, store, _, reqs) = Rig((req, _) =>
            req.Url.Contains("/diff?") ? Status(509) : Ok(FullSlc(fresh, "spotify:track:f1", "spotify:track:f2")));
        Seed(store, Rev(1, 0x01), "spotify:track:old");

        Assert.Equal(DiffOutcome.FellBackToFull, await f.FetchPlaylistDiffAsync(Uri, Ct));
        Assert.Equal(2, reqs.Count);                                             // diff, then full
        Assert.Contains("/diff?", reqs[0].Url);
        Assert.DoesNotContain("/diff?", reqs[1].Url);
        Assert.Equal(2, store.Membership(Uri).Count);
        Assert.Equal(fresh, store.PlaylistRevision(Uri));
    }

    // ── a torn apply (out-of-range op) falls back to the full fetch ───────────────────────────────────────────────────
    [Fact]
    public async Task Diff_TornApply_FallsBackToFullFetch()
    {
        var fresh = Rev(3, 0x33);
        var torn = new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = 5, Length = 2 } };   // baseline has 1 row
        var (f, store, _, reqs) = Rig((req, _) =>
            req.Url.Contains("/diff?") ? Ok(DiffSlc(Rev(1, 0x01), fresh, torn)) : Ok(FullSlc(fresh, "spotify:track:f1")));
        Seed(store, Rev(1, 0x01), "spotify:track:t1");

        Assert.Equal(DiffOutcome.FellBackToFull, await f.FetchPlaylistDiffAsync(Uri, Ct));
        Assert.Equal(2, reqs.Count);
        Assert.Equal("spotify:track:f1", Assert.Single(store.Membership(Uri)).ItemUri);
    }

    // ── no stored revision / no resident baseline → straight to the full fetch (no /diff round-trip) ─────────────────
    [Fact]
    public async Task Diff_NoBaseline_GoesStraightToFull()
    {
        var (f, store, _, reqs) = Rig((req, _) => Ok(FullSlc(Rev(1, 0x01), "spotify:track:t1")));

        Assert.Equal(DiffOutcome.FellBackToFull, await f.FetchPlaylistDiffAsync(Uri, Ct));
        Assert.DoesNotContain(reqs, r => r.Url.Contains("/diff?"));
        Assert.Single(store.Membership(Uri));
    }

    // ── a zstd-compressed diff body decodes through the magic-sniff guard ─────────────────────────────────────────────
    [Fact]
    public async Task Diff_ZstdCompressedBody_Decodes()
    {
        var from = Rev(1, 0xAA);
        var to = Rev(2, 0xBB);
        var plain = DiffSlc(from, to, AddLast("spotify:track:new"));
        using var comp = new ZstdSharp.Compressor();
        var zstd = comp.Wrap(plain).ToArray();
        var (f, store, _, _) = Rig((req, _) => Ok(zstd));
        Seed(store, from, "spotify:track:t1");

        Assert.Equal(DiffOutcome.Applied, await f.FetchPlaylistDiffAsync(Uri, Ct));
        Assert.Equal(2, store.Membership(Uri).Count);
        Assert.Equal(to, store.PlaylistRevision(Uri));
    }

    // ── a full fetch seeds a minimal owner chip so the owner name renders before the profile service resolves ─────────
    [Fact]
    public async Task FullFetch_SeedsOwnerChip_NameSet_AvatarNull()
    {
        var slc = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Rev(1, 0x01)),
            OwnerUsername = "catherine",
            Attributes = new Pl.ListAttributes { Name = "Summer 2016 vibes" },
        };
        var c = new Pl.ListItems { Pos = 0, Truncated = false };
        c.Items.Add(new Pl.Item { Uri = "spotify:track:t1" });
        slc.Contents = c;
        var (f, store, _, _) = Rig((req, _) => Ok(slc.ToByteArray()));

        await f.FetchPlaylistAsync(Uri, Ct);

        var pl = store.GetPlaylist(Uri);
        Assert.NotNull(pl);
        Assert.NotNull(pl!.Owner);
        Assert.Equal("catherine", pl.Owner!.Name);   // renders the name immediately, before profile resolution
        Assert.Equal("catherine", pl.Owner.Id);      // RawOwnerId stays the username → overlay resolution unchanged
        Assert.Null(pl.Owner.Avatar);                // upgrades to the avatar when the profile resolves
        Assert.Equal("catherine", pl.OwnerName);
    }

    // ── the LibrarySync revalidate path rides /diff: an open stale playlist costs one up-to-date probe ───────────────
    [Fact]
    public async Task LibrarySync_OpenStalePlaylist_RevalidatesViaDiff()
    {
        int diffs = 0, fulls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?")) { Interlocked.Increment(ref diffs); return SyncHarness.Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()); }
            Interlocked.Increment(ref fulls);
            return SyncHarness.Ok(FullSlc(Rev(1, 0x01), "spotify:track:t1"));
        });
        Seed(h.Store, Rev(1, 0x01), "spotify:track:t1");   // resident + revision, never revalidated → stale on open

        await h.Sync.OpenPlaylistAsync(Uri, Ct);

        Assert.Equal(1, diffs);
        Assert.Equal(0, fulls);
        Assert.Equal(1, h.Sync.DiffUpToDate);
        Assert.Equal(0, h.Sync.DiffFellBack);

        // a second immediate open is inside the 5-min freshness window → no request at all.
        await h.Sync.OpenPlaylistAsync(Uri, Ct);
        Assert.Equal(1, diffs);
    }

    // ── cause 3 of the stale-daylist-header defect: the 5-minute open-revalidate window must not veto a re-open of a
    // ROLLING-IDENTITY playlist. Two opens back-to-back (well inside the window) still both revalidate, and the
    // second one's fresh name/description/cover — the server having rolled the edition between the two opens —
    // actually lands. ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task LibrarySync_RollingIdentityPlaylist_RevalidatesInsideTheWindow_AndAdoptsTheNewHeader()
    {
        int headerCalls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?")) return SyncHarness.Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray());
            headerCalls++;
            bool second = headerCalls > 1;
            // Capabilities.CanView must ride every header response — an absent one reads back as default(PlaylistCapabilities)
            // and would (rightly, but not what THIS test is pinning) also trip the unrelated header-heal gate on the next open.
            var header = new Pl.SelectedListContent
            { Length = 1, OwnerUsername = "spotify", Capabilities = new Pl.Capabilities { CanView = true, CanEditItems = true } };
            header.Attributes = new Pl.ListAttributes
            {
                Name = second ? "New Edition" : "Old Edition",
                Description = second ? "Fresh picks for you" : "Yesterday's picks",
                Format = "daylist",
            };
            header.Attributes.PictureSize.Add(new Pl.PictureSize
            { Url = second ? "https://i.scdn.co/image/new" : "https://i.scdn.co/image/old" });
            return SyncHarness.Ok(header.ToByteArray());
        });
        Seed(h.Store, Rev(1, 0x01), "spotify:track:t1");
        // Capabilities set to something other than default so the header-heal gate (a SEPARATE, unrelated repair) does
        // not also fire here and confuse what is under test.
        h.Store.UpsertPlaylist(new Playlist("x", Uri, "Old Edition", "Yesterday's picks", "spotify",
            new Image("https://i.scdn.co/image/old"), 1)
        {
            Format = "daylist",
            Capabilities = new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: false,
                IsCollaborative: false, IsOwner: false),
        });

        await h.Sync.OpenPlaylistAsync(Uri, Ct);
        Assert.Equal("Old Edition", h.Store.GetPlaylist(Uri)!.Name);

        // Immediately again — well inside OpenRevalidateWindow. A non-rolling playlist would be skipped outright
        // (LibrarySync_OpenStalePlaylist_RevalidatesViaDiff, above, pins exactly that).
        await h.Sync.OpenPlaylistAsync(Uri, Ct);

        Assert.Equal(2, headerCalls);
        var adopted = h.Store.GetPlaylist(Uri)!;
        Assert.Equal("New Edition", adopted.Name);
        Assert.Equal("Fresh picks for you", adopted.Description);
        Assert.Equal("https://i.scdn.co/image/new", adopted.Cover?.Url);
    }
}
