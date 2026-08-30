using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The logged-out inner of the Spotify source's switchable hydrator (design §1.3). It must answer purely from
/// the store, never network, and report <see cref="HydrationStatus.Unsupported"/> — not <c>Failed</c> — for a level it
/// cannot reach, so an offline open still paints everything the cache holds and nothing retries in a loop.</summary>
public class OfflineEntityHydratorTests
{
    static Image Art => new("https://i.scdn.co/image/abc", 300, 300);
    static ArtistRef Named => new("a1", "spotify:artist:a1", "Artist One");

    static Track FullTrack(string uri) =>
        new(EntityUri.IdOf(uri), uri, "Song", new[] { Named }, new AlbumRef("al1", "spotify:album:al1", "Album One"),
            210_000, false, Art);

    static Episode OpenEpisode(string uri) =>
        new(EntityUri.IdOf(uri), uri, "Ep", "The Show", Art, 1000, DateTimeOffset.UnixEpoch);

    static (InMemoryStore Store, OfflineEntityHydrator Hydrator) New()
    {
        var store = new InMemoryStore();
        return (store, new OfflineEntityHydrator(store));
    }

    [Fact]
    public void LevelOf_EmptyStore_IsNone()
    {
        var (_, h) = New();
        Assert.Equal(HydrationLevel.None, h.LevelOf("spotify:track:t1"));
        Assert.Equal(HydrationLevel.None, h.LevelOf("spotify:album:al1"));
    }

    [Fact]
    public void LevelOf_UnroutableUri_IsNone()
    {
        var (_, h) = New();
        Assert.Equal(HydrationLevel.None, h.LevelOf("not-a-uri"));
        Assert.Equal(HydrationLevel.None, h.LevelOf(""));
    }

    [Fact]
    public void LevelOf_KindsWithNoOfflineAnswer_AreNone()
    {
        // A user/collection/prerelease/concert has no resident entity to measure — the ladder owns them, not the store.
        var (_, h) = New();
        Assert.Equal(HydrationLevel.None, h.LevelOf("spotify:user:bob"));
        Assert.Equal(HydrationLevel.None, h.LevelOf("spotify:collection:tracks"));
        Assert.Equal(HydrationLevel.None, h.LevelOf("spotify:prerelease:pr1"));
    }

    [Fact]
    public void LevelOf_ReadsEveryEntityPlane()
    {
        var (store, h) = New();
        store.UpsertTrack(FullTrack("spotify:track:t1"));
        store.UpsertEpisode(OpenEpisode("spotify:episode:e1"));
        store.UpsertArtist(new Artist("a1", "spotify:artist:a1", "Artist One", Art));
        store.UpsertPlaylist(new Playlist("p1", "spotify:playlist:p1", "My List", null, "me", Art, 0));

        Assert.Equal(HydrationLevel.Rich, h.LevelOf("spotify:track:t1"));
        Assert.Equal(HydrationLevel.Rich, h.LevelOf("spotify:episode:e1"));
        Assert.Equal(HydrationLevel.Identity, h.LevelOf("spotify:artist:a1"));
        Assert.Equal(HydrationLevel.Identity, h.LevelOf("spotify:playlist:p1"));
    }

    [Fact]
    public void LevelOf_Show_CountsResidentEpisodesFromTheMembershipPlane()
    {
        var (store, h) = New();
        const string show = "spotify:show:s1";
        store.UpsertShow(new Show("s1", show, "The Show", "Publisher", Art));
        var members = new List<PlaylistMember>();
        for (int i = 0; i < 3; i++) members.Add(new PlaylistMember($"m{i}", $"spotify:episode:e{i}", null, 0));
        store.SetMembership(show, members, null);

        Assert.Equal(HydrationLevel.Identity, h.LevelOf(show));       // header + baseline, no episodes resident

        for (int i = 0; i < 3; i++) store.UpsertEpisode(OpenEpisode($"spotify:episode:e{i}"));
        Assert.Equal(HydrationLevel.Full, h.LevelOf(show));           // every member resident ⇒ nothing left to page
    }

    [Fact]
    public async Task EnsureAsync_Resident_Reaches()
    {
        var (store, h) = New();
        store.UpsertTrack(FullTrack("spotify:track:t1"));

        var o = await h.EnsureAsync("spotify:track:t1", HydrationLevel.Open);
        Assert.True(o.Ok);
        Assert.Equal(HydrationStatus.Reached, o.Status);
        Assert.Equal(HydrationLevel.Rich, o.Reached);
    }

    [Fact]
    public async Task EnsureAsync_Missing_IsUnsupportedNotFailed()
    {
        var (_, h) = New();
        var o = await h.EnsureAsync("spotify:track:t1", HydrationLevel.Open);
        Assert.False(o.Ok);
        Assert.Equal(HydrationStatus.Unsupported, o.Status);          // "no transport", not "the transport broke"
        Assert.Equal(HydrationLevel.None, o.Reached);
        Assert.Null(o.Error);
    }

    [Fact]
    public async Task EnsureAsync_LevelNone_AlwaysReaches()
    {
        var (_, h) = New();
        Assert.Equal(HydrationStatus.Reached, (await h.EnsureAsync("spotify:track:t1", HydrationLevel.None)).Status);
    }

    [Fact]
    public async Task EnsureManyAsync_SplitsReachedFromMissing()
    {
        var (store, h) = New();
        store.UpsertTrack(FullTrack("spotify:track:t1"));

        var batch = await h.EnsureManyAsync(new[] { "spotify:track:t1", "spotify:track:t2" }, HydrationLevel.Open);
        Assert.Equal(new[] { "spotify:track:t1" }, batch.Reached);
        Assert.Equal(new[] { "spotify:track:t2" }, batch.Missing);
        Assert.Equal(HydrationStatus.Unsupported, batch.Status);
    }

    [Fact]
    public async Task EnsureManyAsync_AllResident_Reaches()
    {
        var (store, h) = New();
        store.UpsertTrack(FullTrack("spotify:track:t1"));

        var batch = await h.EnsureManyAsync(new[] { "spotify:track:t1" }, HydrationLevel.Open);
        Assert.Equal(HydrationStatus.Reached, batch.Status);
        Assert.Empty(batch.Missing);
    }

    [Fact]
    public async Task Traits_AndInvalidate_AreNoOps()
    {
        var (store, h) = New();
        store.UpsertTrack(FullTrack("spotify:track:t1"));
        long before = store.Version("spotify:track:t1");

        await h.EnsureTraitsAsync(new[] { "spotify:track:t1" }, TraitSurface.Queue);
        await h.EnsureTraitsAsync(new[] { "spotify:track:t1" }, TraitSet.RowBundle, TraitSurface.Queue);
        h.Invalidate("spotify:track:t1");

        Assert.Equal(before, store.Version("spotify:track:t1"));   // offline traits never write, so nothing re-renders
    }
}

/// <summary>The portable hydrator implementations (design §1.3) — the reason no seam anywhere is nullable.</summary>
public class PortableHydratorTests
{
    [Fact]
    public async Task Complete_ReachesEveryLevel()
    {
        var h = CompleteEntityHydrator.Instance;
        Assert.Equal(HydrationLevel.Full, h.LevelOf("spotify:album:a"));
        Assert.True((await h.EnsureAsync("spotify:album:a", HydrationLevel.Full)).Ok);
        var batch = await h.EnsureManyAsync(new[] { "a", "b" }, HydrationLevel.Full);
        Assert.Equal(HydrationStatus.Reached, batch.Status);
        Assert.Empty(batch.Missing);
    }

    [Fact]
    public async Task NotOwned_IsUnsupportedAndListsEverythingMissing()
    {
        var h = NotOwnedEntityHydrator.Instance;
        Assert.Equal(HydrationLevel.None, h.LevelOf("whatever"));
        Assert.Equal(HydrationStatus.Unsupported, (await h.EnsureAsync("whatever", HydrationLevel.Identity)).Status);
        var batch = await h.EnsureManyAsync(new[] { "a", "b" }, HydrationLevel.Identity);
        Assert.Empty(batch.Reached);
        Assert.Equal(2, batch.Missing.Count);
    }

    [Fact]
    public async Task Switchable_ForwardsToTheCurrentInner()
    {
        var s = new SwitchableEntityHydrator(NotOwnedEntityHydrator.Instance);
        Assert.Equal(HydrationLevel.None, s.LevelOf("spotify:album:a"));

        s.SetInner(CompleteEntityHydrator.Instance);
        Assert.Same(CompleteEntityHydrator.Instance, s.Inner);
        Assert.Equal(HydrationLevel.Full, s.LevelOf("spotify:album:a"));
        Assert.True((await s.EnsureAsync("spotify:album:a", HydrationLevel.Full)).Ok);
    }

    [Fact]
    public void Switchable_RefusesANullInner()   // wiring discipline: there is no null state to fall into
    {
        Assert.Throws<ArgumentNullException>(() => new SwitchableEntityHydrator(null!));
        Assert.Throws<ArgumentNullException>(() => new SwitchableEntityHydrator(CompleteEntityHydrator.Instance).SetInner(null!));
    }

    [Fact]
    public void CatalogSource_DefaultsToTheCompleteHydrator()
    {
        // Every complete-at-construction source (export / local / fake / user playlists / test fakes) keeps compiling
        // AND answers Reached — the default interface member is what makes the façade non-nullable everywhere.
        ICatalogSource fake = new FakeSource();
        Assert.Same(CompleteEntityHydrator.Instance, fake.Hydrator);
    }
}

/// <summary>The episode → playable projection (design §1.5) — the join that finally renders episode rows in playlists.</summary>
public class EpisodeAsTrackTests
{
    static Episode Ep => new("e1", "spotify:episode:e1", "Ep 1", "The Show",
        new Image("https://i.scdn.co/image/abc"), 1234, DateTimeOffset.UnixEpoch, "About");

    [Fact]
    public void From_Null_IsNull() => Assert.Null(EpisodeAsTrack.From(null));

    [Fact]
    public void From_CarriesTheEpisodeIdentityAndTheShowAsItsAlbum()
    {
        var t = EpisodeAsTrack.From(Ep, "spotify:show:s1")!;
        Assert.Equal("e1", t.Id);                       // TrackRow.StateOf compares the EPISODE id
        Assert.Equal("spotify:episode:e1", t.Uri);
        Assert.Equal("Ep 1", t.Title);
        Assert.Empty(t.Artists);
        Assert.Equal("The Show", t.Album.Name);
        Assert.Equal("spotify:show:s1", t.Album.Uri);   // so the subtitle links to the podcast
        Assert.Equal(1234, t.DurationMs);
        Assert.False(t.IsExplicit);
        Assert.Null(t.Availability);
        Assert.Equal("podcast", t.Source);
    }

    [Fact]
    public void From_WithoutAShowUri_StillCarriesTheShowName()
    {
        var t = EpisodeAsTrack.From(Ep)!;
        Assert.Equal("", t.Album.Uri);
        Assert.Equal("The Show", t.Album.Name);
    }

    [Fact]
    public void From_IsARenderProjection_NotAHydrationSubject()
    {
        // A podcast has a SHOW, not artists, so the projected row has none — which means Of(Track) (whose Open rung
        // requires named artists) is structurally the wrong ruler for it. The episode's own rung is the answer any
        // ladder or shimmer gate must read, and this row is never upserted, so the two never disagree in the store.
        var row = EpisodeAsTrack.From(Ep)!;
        Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(row));
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Ep));
    }
}
