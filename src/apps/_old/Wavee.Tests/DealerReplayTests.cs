using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

// ── The dealer-archive replay gate (plan P0 items 8/9) ───────────────────────────────────────────────────────────────
// A byte-exact hm://playlist* subset of a REAL 2026-08-15 session (75 frames: 18 rootlist heads x2 copies, the P1 create
// + edit stream, the P3 deleted_by_owner tombstone, 2 permission/state pushes, 19 editorial head-only pushes) is replayed
// through a real DealerRouter + real LibrarySync + an in-memory store + a scripted fetcher, in capture order.
//
// These encode the P0 invariants: I1 (a stored rootlist revision is ALWAYS the 24-B head, never the 50-B uri bytes),
// one enqueue per head across the duplicated v2/non-v2 pair, and "a head-only playlist push is a new head, not a
// full-GET storm". Replay_RootlistRevisionAlways24B + Replay_RootlistPairs_OneEnqueuePerHead are the ACCEPTANCE GATE
// for the P0 dealer-correctness change and are deliberately NOT skipped. The tombstone + permission arms are P1.
public class DealerReplayTests
{
    const string P1 = "spotify:playlist:6EVbQZBiAg9zHzMjChxvRd";
    const string P2 = "spotify:playlist:6QbD3n4hCF6uP8jqyiDsS5";
    const string P3 = "spotify:playlist:4vkIrispQ6gcMNIojGPd0L";

    // ── canned wire builders ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A well-formed 24-B playlist4 revision: a 4-B big-endian counter + filler (I1 is exactly "length == 24").</summary>
    static byte[] Rev24(uint counter, byte fill = 0xAB)
    {
        var r = new byte[24];
        r[0] = (byte)(counter >> 24); r[1] = (byte)(counter >> 16); r[2] = (byte)(counter >> 8); r[3] = (byte)counter;
        for (int i = 4; i < 24; i++) r[i] = fill;
        return r;
    }

    static byte[] Slc(byte[]? rev, params string[] uris)
    {
        var slc = new Pl.SelectedListContent();
        if (rev is not null) slc.Revision = ByteString.CopyFrom(rev);
        var c = new Pl.ListItems { Pos = 0, Truncated = false };
        foreach (var u in uris) c.Items.Add(new Pl.Item { Uri = u });
        slc.Contents = c;
        return slc.ToByteArray();
    }

    static byte[] SlcWithHeader(byte[]? rev, string name, params string[] uris)
    {
        var slc = Pl.SelectedListContent.Parser.ParseFrom(Slc(rev, uris));
        slc.Attributes = new Pl.ListAttributes { Name = name };
        slc.Capabilities = new Pl.Capabilities { CanView = true };
        slc.OwnerUsername = "31testuser000000000000000000";
        return slc.ToByteArray();
    }

    static bool IsRootlist(WireEvent e) => e.Topic.EndsWith("/rootlist", StringComparison.Ordinal);

    /// <summary>The head a frame announces. Rootlist AND playlist pushes are both PlaylistModificationInfo on the wire.</summary>
    static byte[]? NewRevOf(WireEvent e)
    {
        try
        {
            var info = Pl.PlaylistModificationInfo.Parser.ParseFrom(e.Payload);
            return info.HasNewRevision ? info.NewRevision.ToByteArray() : null;
        }
        catch { return null; }
    }

    /// <summary>True when a playlist frame carries UPDATE_LIST new{deleted_by_owner=1} (the remote-delete shape).</summary>
    static bool CarriesTombstone(WireEvent e)
    {
        if (IsRootlist(e)) return false;
        try
        {
            var info = Pl.PlaylistModificationInfo.Parser.ParseFrom(e.Payload);
            foreach (var op in info.Ops)
                if (op.Kind == Pl.Op.Types.Kind.UpdateListAttributes
                    && op.UpdateListAttributes?.NewAttributes?.Values is { HasDeletedByOwner: true, DeletedByOwner: true })
                    return true;
        }
        catch { }
        return false;
    }

    static string UriOf(WireEvent e)
    {
        try
        {
            var info = Pl.PlaylistModificationInfo.Parser.ParseFrom(e.Payload);
            return info.HasUri ? Encoding.UTF8.GetString(info.Uri.Span) : "";
        }
        catch { return ""; }
    }

    // ── the scripted server ──────────────────────────────────────────────────────────────────────────────────────────
    // Answers a rootlist GET with the NEWEST head pushed so far plus a small rootlist of P1/P2/P3, and a playlist full
    // GET with that playlist's newest head. /diff answers up-to-date. This is what makes the replay converge the way the
    // real server did, without replaying HTTP.
    sealed class ReplayServer
    {
        public byte[]? RootlistHead;
        public readonly Dictionary<string, byte[]> PlaylistHead = new(StringComparer.Ordinal);
        public readonly List<string> RootlistEntries = new() { P1, P2, P3 };
        public int RootlistGets, PlaylistFullGets, PlaylistDiffs, PermissionGets;

        /// <summary>Advance the scripted state to the head the next frame is about to announce. A deleted_by_owner
        /// frame also drops that playlist from the scripted ROOTLIST — that is what the real server did (the capture's
        /// very next rootlist head is the delete's own), and without it a later full GET would resurrect the row.</summary>
        public void Observe(WireEvent e)
        {
            if (CarriesTombstone(e) && UriOf(e) is { Length: > 0 } deleted) RootlistEntries.Remove(deleted);
            var rev = NewRevOf(e);
            if (rev is null) return;
            if (IsRootlist(e)) RootlistHead = rev;
            else
            {
                var uri = UriOf(e);
                if (uri.Length > 0) PlaylistHead[uri] = rev;
            }
        }

        public HttpResp Respond(HttpReq req)
        {
            if (req.Url.Contains("/permission/")) { PermissionGets++; return SyncHarness.Ok(Array.Empty<byte>()); }
            if (req.Url.Contains("/rootlist")) { RootlistGets++; return SyncHarness.Ok(Slc(RootlistHead, RootlistEntries.ToArray())); }
            if (req.Url.Contains("/diff?")) { PlaylistDiffs++; return SyncHarness.Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()); }
            if (req.Url.Contains("/playlist/v2/"))
            {
                PlaylistFullGets++;
                PlaylistHead.TryGetValue(PlaylistUriFromUrl(req.Url), out var head);
                return SyncHarness.Ok(SlcWithHeader(head ?? Rev24(1), "Replayed", "spotify:track:t1", "spotify:track:t2"));
            }
            return SyncHarness.Ok(Array.Empty<byte>());
        }

        static string PlaylistUriFromUrl(string url)
        {
            const string marker = "/playlist/v2/playlist/";
            int i = url.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return "";
            var rest = url[(i + marker.Length)..];
            int cut = rest.IndexOfAny(new[] { '?', '/' });
            return "spotify:playlist:" + (cut < 0 ? rest : rest[..cut]);
        }
    }

    // Replay the whole capture. `seed` primes the store before the first push; `beforeEach` observes store state between
    // frames (that is how the "after EVERY rootlist frame" invariant is sampled).
    static async Task<Replayed> ReplayAllAsync(Action<SyncHarness>? seed = null,
                                               Action<SyncHarness, WireEvent>? beforeEach = null)
    {
        var server = new ReplayServer();
        var h = new SyncHarness(server.Respond);
        seed?.Invoke(h);
        var router = new DealerRouter(h.Dealer, h.Sync);
        foreach (var e in DealerArchiveReplay.Frames())
        {
            server.Observe(e);
            h.Dealer.PushEvent(e);
            await h.Sync.WaitForIdleAsync();      // FIFO barrier: the loop has drained this push before we sample
            beforeEach?.Invoke(h, e);
        }
        await h.Sync.WaitForIdleAsync();
        return new Replayed(h, server, router);
    }

    sealed record Replayed(SyncHarness H, ReplayServer Server, DealerRouter Router) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Router.Dispose(); await H.DisposeAsync(); }
    }

    // ── fixture integrity: a bad re-extract must fail HERE, not inside a behavioural assert ───────────────────────────
    [Fact]
    public void Replay_Fixture_HasExpectedShape()
    {
        var rows = DealerArchiveReplay.Load();
        Assert.Equal(75, rows.Count);
        Assert.All(rows, r => Assert.StartsWith("hm://playlist", r.Uri, StringComparison.Ordinal));

        var frames = DealerArchiveReplay.Frames().ToList();
        Assert.Equal(75, frames.Count);                     // every archived row is a MESSAGE frame carrying a topic

        var rootlist = frames.Where(IsRootlist).ToList();
        Assert.Equal(36, rootlist.Count);
        Assert.All(rootlist, f => Assert.Equal(24, NewRevOf(f)!.Length));      // 24 B on the wire, always
        var heads = rootlist.Select(f => Convert.ToHexString(NewRevOf(f)!)).ToList();
        Assert.Equal(18, heads.Distinct().Count());                            // 18 distinct heads …
        Assert.All(heads.GroupBy(x => x, StringComparer.Ordinal), g => Assert.Equal(2, g.Count()));   // … each twice

        Assert.Equal(2, frames.Count(f => f.Topic.StartsWith("hm://playlist-permission/", StringComparison.Ordinal)));
        foreach (var uri in new[] { P1, P2, P3 })
            Assert.Contains(frames, f => !IsRootlist(f) && UriOf(f) == uri);
    }

    // ── I1: the stored rootlist revision is ALWAYS the 24-B head, never the 50-B uri bytes ───────────────────────────
    // P0 ACCEPTANCE GATE.
    [Fact]
    public async Task Replay_RootlistRevisionAlways24B()
    {
        var bad = new List<string>();
        await using var r = await ReplayAllAsync(beforeEach: (h, e) =>
        {
            var rev = h.Store.RootlistRevision();
            if (rev is not null && rev.Length != 24) bad.Add($"{e.Topic} -> len={rev.Length}");
        });

        Assert.True(bad.Count == 0, "rootlist revision was not 24 B after: " + string.Join(" | ", bad.Take(3)));
        var final = r.H.Store.RootlistRevision();
        Assert.True(final is null || final.Length == 24, $"final rootlist revision len={final?.Length}");
    }

    // ── one enqueue per head across the duplicated v2 / non-v2 pair ──────────────────────────────────────────────────
    // P0 ACCEPTANCE GATE. 18 distinct heads => at most 18 rootlist GETs, and the 18 duplicate copies are deduped.
    [Fact]
    public async Task Replay_RootlistPairs_OneEnqueuePerHead()
    {
        await using var r = await ReplayAllAsync();

        Assert.True(r.Server.RootlistGets <= 18,
            $"18 distinct heads must cost at most 18 rootlist GETs, saw {r.Server.RootlistGets}");
        Assert.Equal(18, r.Router.RootlistPushDeduped);   // exactly the 18 duplicate copies were dropped
    }

    // ── head-only playlist pushes must not trigger a full-GET storm ──────────────────────────────────────────────────
    [Fact]
    public async Task Replay_HeadOnlyPlaylistPush_DoesNotFullRefresh()
    {
        // COLD: nothing is on screen, so every head-only push may only mark dirty — zero playlist network at all.
        await using (var cold = await ReplayAllAsync())
        {
            Assert.Equal(0, cold.Server.PlaylistFullGets);
            Assert.Equal(0, cold.Server.PlaylistDiffs);
        }

        // OPEN: with P1 on screen and a resident baseline, its 11 pushes must not cost a full GET each.
        await using var open = await ReplayAllAsync(seed: h =>
        {
            h.Store.SetMembership(P1, new[]
            {
                new PlaylistMember("i1", "spotify:track:t1", null, 0),
                new PlaylistMember("i2", "spotify:track:t2", null, 0),
            }, Rev24(1));
            h.Sync.SetOpenContext(P1);
        });

        int p1Pushes = DealerArchiveReplay.Frames().Count(f => UriOf(f) == P1);
        Assert.Equal(11, p1Pushes);
        Assert.True(open.Server.PlaylistFullGets < p1Pushes,
            $"an open playlist must not full-GET once per push ({open.Server.PlaylistFullGets} full GETs for {p1Pushes} pushes)");
    }

    // ── P1-dependent: the deleted_by_owner tombstone evicts P3 ───────────────────────────────────────────────────────
    [Fact]
    public async Task Replay_P3Tombstone_RemovesP3()
    {
        await using var r = await ReplayAllAsync(seed: h =>
        {
            h.Store.UpsertPlaylist(new Playlist("4vkIrispQ6gcMNIojGPd0L", P3, "Doomed", null,
                "31testuser000000000000000000", null, 1));
            h.Store.SetMembership(P3, new[] { new PlaylistMember("i1", "spotify:track:t1", null, 0) }, Rev24(1));
            h.Store.SetSaved("playlists", P3, true, SyncState.Confirmed);
        });

        Assert.DoesNotContain(r.H.Store.Rootlist(), e => e.Uri == P3);   // gone from the rootlist entries …
        Assert.Empty(r.H.Store.Membership(P3));                          // … and from membership
        Assert.False(r.H.Store.IsSaved("playlists", P3));
        Assert.True(r.H.Store.GetPlaylist(P3)!.DeletedByOwner);   // the header latches so an open page can say so
    }

    // ── P1-dependent: permission/state pushes flip IsPublic with ZERO permission GETs ────────────────────────────────
    [Fact]
    public async Task Replay_P1PermissionFlips_BlockedThenViewer_NoGet()
    {
        await using var r = await ReplayAllAsync(seed: h =>
            h.Store.UpsertPlaylist(new Playlist("6EVbQZBiAg9zHzMjChxvRd", P1, "Daily Mix 1 (2)", null,
                "31testuser000000000000000000", null, 0, IsPublic: false)));

        Assert.Equal(0, r.Server.PermissionGets);                 // the pushes alone carry the state
        Assert.True(r.H.Store.GetPlaylist(P1)!.IsPublic);         // BLOCKED then VIEWER: the LAST push wins
    }
}
