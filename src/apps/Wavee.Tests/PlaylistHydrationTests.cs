using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using static Wavee.Tests.HydrationTestSupport;

namespace Wavee.Tests;

// The playlist ladder (design §2.3). Its two load-bearing rules: the ladder NEVER writes membership (LibrarySync owns
// that plane), and a playlist that already has a baseline is REVALIDATED rather than re-opened.
public class PlaylistHydrationTests
{
    const string Uri = "spotify:playlist:p1";

    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly RecordingTraitPipeline Traits = new();
        public readonly FakePlaylistOpener Opener = new();
        public readonly FakeCatalogFetch Catalog;
        public readonly SpotifyProviderHydrator Hydrator;

        public Harness()
        {
            Catalog = new FakeCatalogFetch(Store, (uris, store) =>
            {
                foreach (var u in uris)
                    if (u.Kind == EntityKind.Playlist)
                        store.UpsertPlaylist(new Playlist(u.Id, u.Uri, "List " + u.Id, null, "me", null, 0));
            });
            var policy = new TraitPolicy();
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, Traits, Pump,
                [new PlaylistHydration(Store, Opener, policy)], traitPolicy: policy);
        }

        public void Dispose() => Pump.Dispose();
    }

    // Seeds a baseline AND resident member rows: since PlaylistHydration.LevelOf now scans membership for thinness
    // (design §2.3 — a container is only as hydrated as its thinnest row), a member left resident-less would read the
    // playlist back at Identity instead of Full/Open and flip an EnsureAsync outcome from Reached to Partial — a
    // correct result for a THIN member, but not what these open/revalidate-mechanics tests are exercising. The
    // dedicated thin-member coverage (Open_WithBaselineAndThinMember_AsksIdentityForThatMemberOnly and neighbors,
    // below) seeds membership WITHOUT a resident row on purpose.
    static void Seed(InMemoryStore store, params string[] members)
    {
        store.SetMembership(Uri, members.Select((m, i) => new PlaylistMember("i" + i, m, null, 0)).ToArray(), null);
        foreach (var m in members)
        {
            if (EntityUri.KindOf(m) == EntityKind.Track) store.UpsertTrack(TrackAt(m, HydrationLevel.Full));
            else if (EntityUri.KindOf(m) == EntityKind.Episode) store.UpsertEpisode(EpisodeAt(m, HydrationLevel.Full));
        }
    }

    [Fact]
    public void RootlistOpenPlan_IncludesThinRows_ButNotKnownEmptyPlaylists()
    {
        const string missing = "spotify:playlist:missing";
        const string thin = "spotify:playlist:thin";
        const string headerless = "spotify:playlist:headerless";
        const string empty = "spotify:playlist:empty";
        var store = new InMemoryStore();
        store.SetRootlist([
            new RootlistEntry(0, 1, "spotify:start-group:g:Folder", "Folder", 0),
            new RootlistEntry(1, 0, missing, null, 1),
            new RootlistEntry(2, 0, thin, null, 1),
            new RootlistEntry(3, 0, headerless, null, 1),
            new RootlistEntry(4, 0, empty, null, 1),
            new RootlistEntry(5, 2, "spotify:end-group:g", null, 0),
            new RootlistEntry(6, 0, thin, null, 0),   // malformed duplicate cannot schedule duplicate work
        ]);
        store.UpsertPlaylist(new Playlist("thin", thin, "Thin", null, "me", null, 152));
        store.SetMembership(headerless, [new PlaylistMember("i1", "spotify:track:t1", null, 0)], null);
        store.UpsertPlaylist(new Playlist("empty", empty, "Actually empty", null, "me", null, 0));
        store.SetMembership(empty, Array.Empty<PlaylistMember>(), null);

        Assert.Equal([missing, thin, headerless], PlaylistHydration.RootlistOpenPlan(store));
    }

    [Fact]
    public async Task NoBaseline_AwaitsTheRealOpen()
    {
        using var h = new Harness();
        h.Opener.OnOpen = uri => Seed(h.Store, "spotify:track:t1");

        var outcome = await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(1, h.Opener.OpenCalls);
        Assert.Equal(0, h.Opener.RevalidateCalls);
        Assert.Equal(HydrationStatus.Reached, outcome.Status);
    }

    [Fact]
    public async Task WithBaseline_RevalidatesOnly()
    {
        using var h = new Harness();
        Seed(h.Store, "spotify:track:t1");

        await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        // There IS something to paint, so the open is a background revalidation the sync loop's own 5-minute window
        // gets to veto — never a blocking re-fetch.
        Assert.Equal(0, h.Opener.OpenCalls);
        Assert.Equal(1, h.Opener.RevalidateCalls);
    }

    [Fact]
    public async Task NeverWritesMembership()
    {
        using var h = new Harness();
        Seed(h.Store, "spotify:track:t1", "spotify:episode:e1");
        var before = h.Store.Membership(Uri).Select(m => m.ItemUri).ToArray();

        await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open, new HydrationOptions(Revalidate: true));
        await DrainAsync(h.Pump);

        // The opener did nothing (it is a fake), so if the plane changed at all the LADDER wrote it — which it must never do.
        Assert.Equal(before, h.Store.Membership(Uri).Select(m => m.ItemUri).ToArray());
    }

    [Fact]
    public async Task Open_AsksTraitsForEveryMember_EpisodesIncluded()
    {
        using var h = new Harness();
        Seed(h.Store, "spotify:track:t1", "spotify:episode:e1");

        await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        var call = Assert.Single(h.Traits.Calls);
        Assert.Equal(TraitSurface.PlaylistOpen, call.Surface);
        // The episode is NOT filtered out — that per-service `spotify:track:` gate is the bug this replaces.
        Assert.Contains("spotify:episode:e1", call.Uris);
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount, call.Traits);
    }

    [Fact]
    public async Task Identity_UsesTheHeaderGet_OnlyForAnUnnamedRootlistMember()
    {
        using var h = new Harness();
        h.Store.SetRootlist([new RootlistEntry(0, 0, Uri, null, 0)]);
        // Step 0's 205 answers nothing for this uri, so the rootlist member falls back to the header GET.
        var catalog = h.Catalog;
        await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Identity);
        await DrainAsync(h.Pump);
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(0, h.Opener.HeaderCalls);   // the 205 DID name it (the fake projects a header), so no fallback
    }

    [Fact]
    public async Task Identity_FallsBackToTheHeaderGet_WhenTheCatalogueCannotName()
    {
        var store = new InMemoryStore();
        using var pump = new HydrationPump(CancellationToken.None);
        var opener = new FakePlaylistOpener();
        var catalog = new FakeCatalogFetch(store);   // projects nothing — the 205 miss
        var policy = new TraitPolicy();
        var hydrator = HydrationTestSupport.Hydrator(store, catalog, new RecordingTraitPipeline(), pump,
            [new PlaylistHydration(store, opener, policy)], traitPolicy: policy);
        store.SetRootlist([new RootlistEntry(0, 0, Uri, null, 0)]);

        await hydrator.EnsureAsync(Uri, HydrationLevel.Identity);
        await DrainAsync(pump);

        Assert.Equal(1, opener.HeaderCalls);
    }

    // ── the ledger never TTL-seals a playlist Open (design §2.1, plan §4 risk 2) ─────────────────────────────────────
    // The playlist plane has ONE freshness authority — the LibrarySync writer loop (its in-flight map, its 5-minute
    // window, its dirty set). A hydration seal on top of it is a second, disagreeing gate, and both directions of the
    // disagreement were live bugs: a successful open sealed Open Reached for an hour, so `Revalidate` was never asked
    // again; a FAILED first open sealed Open Exhausted for ten minutes, so re-navigating to the playlist did not retry
    // and the page stayed empty. Identity IS sealed — a header is a catalogue fact.

    [Fact]
    public async Task Open_IsNeverTtlSealed_SoASecondOpenStillRevalidates()
    {
        using var h = new Harness();
        Seed(h.Store, "spotify:track:t1");

        await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open);
        await DrainAsync(h.Pump);
        await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        // Twice — the loop's own gates decide whether either one fetches, which is exactly the point.
        Assert.Equal(2, h.Opener.RevalidateCalls);
    }

    [Fact]
    public async Task Open_ThatFailed_IsRetriedByTheNextOpen_NotSealedExhausted()
    {
        using var h = new Harness();
        // The open lands nothing (a transport failure the opener swallowed): no baseline, so the rung is not reached.
        await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open);
        await DrainAsync(h.Pump);
        Assert.Equal(1, h.Opener.OpenCalls);

        h.Opener.OnOpen = _ => Seed(h.Store, "spotify:track:t1");
        var second = await h.Hydrator.EnsureAsync(Uri, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(2, h.Opener.OpenCalls);                       // retried, not suppressed for the exhausted TTL
        Assert.Equal(HydrationStatus.Reached, second.Status);
    }

    [Fact]
    public async Task Identity_IsStillSealed_SoAWarmHeaderAskCostsNothing()
    {
        using var h = new Harness();

        await h.Hydrator.EnsureManyAsync([Uri], HydrationLevel.Identity);
        await DrainAsync(h.Pump);
        int after = h.Catalog.Calls;
        await h.Hydrator.EnsureManyAsync([Uri], HydrationLevel.Identity);
        await DrainAsync(h.Pump);

        Assert.Equal(after, h.Catalog.Calls);
    }

    // ── LevelOf: the container is only as hydrated as its thinnest member (design parity with CollectionHydration /
    // ShowHydration) ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LevelOf_MemberRowUnnamed_ReportsIdentity()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", Uri, "List", null, "me", null, 1));
        store.SetMembership(Uri, [new PlaylistMember("i0", "spotify:track:t1", null, 0)], null);
        // The row is RESIDENT but unnamed (Title == uri placeholder) — a real header/body split, not a missing row.
        store.UpsertTrack(TrackAt("spotify:track:t1", HydrationLevel.None));
        var ladder = new PlaylistHydration(store, new FakePlaylistOpener(), new TraitPolicy());

        Assert.Equal(HydrationLevel.Identity, ladder.LevelOf(Uri));
    }

    [Fact]
    public void LevelOf_AllMembersNamed_ReportsFull()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", Uri, "List", null, "me", null, 1));
        store.SetMembership(Uri, [new PlaylistMember("i0", "spotify:track:t1", null, 0)], null);
        store.UpsertTrack(TrackAt("spotify:track:t1", HydrationLevel.Identity));
        var ladder = new PlaylistHydration(store, new FakePlaylistOpener(), new TraitPolicy());
        // Bringing the row up to Identity (named) is enough — the scan only brakes below Identity, so the container's
        // own rung (Full, since a baseline is resident) is what comes back.
        Assert.Equal(HydrationLevel.Full, ladder.LevelOf(Uri));
    }

    [Fact]
    public void LevelOf_MissingMemberRow_ReportsIdentity()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", Uri, "List", null, "me", null, 1));
        // The row was never joined at all — GetTrack answers null, which HydrationLevels.Of reads as None.
        store.SetMembership(Uri, [new PlaylistMember("i0", "spotify:track:missing", null, 0)], null);
        var ladder = new PlaylistHydration(store, new FakePlaylistOpener(), new TraitPolicy());

        Assert.Equal(HydrationLevel.Identity, ladder.LevelOf(Uri));
    }

    // ── ContinueAsync's Open step: the thin-member repair (design gap — the fetcher's hydrate delegate is one-shot,
    // so a row that stayed thin across an earlier pass was never re-asked until this closed it) ────────────────────────
    // These drive PlaylistHydration.ContinueAsync directly against a RecordingHydrator (LadderHarness, the same shape
    // AlbumHydrationTests/ArtistHydrationTests use) so the exact EnsureManyAsync batch shape is assertable, independent
    // of the full SpotifyProviderHydrator façade the rest of this file exercises.

    [Fact]
    public async Task Open_WithBaselineAndThinMember_AsksIdentityForThatMemberOnly()
    {
        using var h = new LadderHarness();
        var opener = new FakePlaylistOpener();
        h.Store.UpsertPlaylist(new Playlist("p1", Uri, "List", null, "me", null, 2));
        h.Store.SetMembership(Uri, [
            new PlaylistMember("i0", "spotify:track:t1", null, 0),
            new PlaylistMember("i1", "spotify:track:t2", null, 0),
        ], null);
        h.Store.UpsertTrack(TrackAt("spotify:track:t1", HydrationLevel.Full));   // t2 stays unresident — the thin one

        var ladder = new PlaylistHydration(h.Store, opener, new TraitPolicy());
        await ladder.ContinueAsync(LadderHarness.Batch(Uri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);
        await h.DrainAsync();

        var batch = Assert.Single(h.Hydrator.Batches);
        Assert.Equal(HydrationLevel.Identity, batch.Level);
        Assert.Equal(["spotify:track:t2"], batch.Uris);
        // The member-traits post-step still ran on the pump — the repair does not replace it.
        Assert.Single(h.Hydrator.TraitCalls);
    }

    [Fact]
    public async Task Open_AllMembersNamed_NoMemberAsk()
    {
        using var h = new LadderHarness();
        var opener = new FakePlaylistOpener();
        h.Store.UpsertPlaylist(new Playlist("p1", Uri, "List", null, "me", null, 1));
        h.Store.SetMembership(Uri, [new PlaylistMember("i0", "spotify:track:t1", null, 0)], null);
        h.Store.UpsertTrack(TrackAt("spotify:track:t1", HydrationLevel.Full));

        var ladder = new PlaylistHydration(h.Store, opener, new TraitPolicy());
        await ladder.ContinueAsync(LadderHarness.Batch(Uri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);
        await h.DrainAsync();

        Assert.Empty(h.Hydrator.Batches);
    }

    [Fact]
    public async Task Open_ThinMembersOver300_PagedAt300()
    {
        using var h = new LadderHarness();
        var opener = new FakePlaylistOpener();
        h.Store.UpsertPlaylist(new Playlist("p1", Uri, "List", null, "me", null, 301));
        var members = new List<PlaylistMember>(301);
        for (int i = 0; i < 301; i++) members.Add(new PlaylistMember("i" + i, "spotify:track:t" + i, null, 0));
        h.Store.SetMembership(Uri, members, null);   // every row is thin — none was ever upserted

        var ladder = new PlaylistHydration(h.Store, opener, new TraitPolicy());
        await ladder.ContinueAsync(LadderHarness.Batch(Uri), HydrationLevel.Open, default, h.Ctx, CancellationToken.None);
        await h.DrainAsync();

        Assert.Equal(2, h.Hydrator.Batches.Count);
        Assert.Equal(300, h.Hydrator.Batches[0].Uris.Count);
        Assert.Equal(1, h.Hydrator.Batches[1].Uris.Count);
    }

    // ── RootlistOpenPlan: a restored header can be non-null AND have a baseline while still reading back below
    // Identity (design §2.3 — see the doc comment on RootlistOpenPlan) ───────────────────────────────────────────────

    [Fact]
    public void RootlistOpenPlan_NamelessHeaderWithMembership_IsPlanned()
    {
        const string nameless = "spotify:playlist:nameless";
        var store = new InMemoryStore();
        store.SetRootlist([new RootlistEntry(0, 0, nameless, null, 0)]);
        // Both presence tests pass (a header row exists, a baseline exists) — only the LEVEL check catches this.
        store.UpsertPlaylist(new Playlist("nameless", nameless, "", null, "me", null, 1));
        store.SetMembership(nameless, [new PlaylistMember("i0", "spotify:track:t1", null, 0)], null);

        Assert.Equal([nameless], PlaylistHydration.RootlistOpenPlan(store));
    }
}
