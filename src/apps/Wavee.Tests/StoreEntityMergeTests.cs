using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Repro asserts for StoreEntityMerge.{Playlist,Show,Episode,Track,Album} — the ClearDescription / ClearPicture /
// dead-letter / thin-ShowName / IsPublic-without-permission cases that blocked a blanket-NonEmpty Playlist merge, plus
// the Phase 1 row-fold hardening: Album.Tracks/Track.Artists must fold per-uri instead of blanket-replacing (a shorter
// getAlbum envelope or an AlbumV4 disc stub used to truncate/blank a richer list), and Playlist.Tracks/Collaborators +
// Show.Episodes are READ-MODEL lists a merge must never adopt from incoming (only compose-time joins write them).
public class StoreEntityMergeTests
{
    const string PlUri = "spotify:playlist:p1";
    const string EpUri = "spotify:episode:e1";
    const string TrUri = "spotify:track:t1";
    const string AlUri = "spotify:album:a1";

    // A minimal named album-track row for the Album.Tracks fold tests. Named Album/Artists mirror the AlbumV4 disc-stub
    // shape (the row itself can still be thin — see Album_Merge_BlankStubRow_KeepsNamedRow).
    static Track TRow(int n, string title = "T", long durationMs = 200_000, long playCount = 0) =>
        new(n.ToString(), $"spotify:track:t{n}", title, [new ArtistRef("ar1", "spotify:artist:ar1", "Artist One")],
            new AlbumRef("a1", AlUri, "A1"), durationMs, false, null, PlayCount: playCount);

    static Album Al(IReadOnlyList<Track>? tracks) =>
        new("a1", AlUri, "A1", null, Array.Empty<ArtistRef>(), 2020, tracks?.Count ?? 0, tracks);

    // coverUrl is a URL rather than an Image so that `coverUrl: null` genuinely means COVER-LESS. Taking an `Image?` and
    // defaulting it with `?? new Image(…)` made the ClearPicture case unexpressible — passing null silently produced the
    // default cover, so the test asserted a clear it had never actually asked for.
    const string DefaultCoverUrl = "https://i.scdn.co/image/abc";

    static Playlist Pl(
        string name = "My List",
        string? description = "hello",
        string? coverUrl = DefaultCoverUrl,
        bool isPublic = true,
        string? basePermRev = null,
        PlaylistCapabilities? caps = null) =>
        new("p1", PlUri, name, description, "owner",
            coverUrl is null ? null : new Image(coverUrl),
            TrackCount: 3,
            Capabilities: caps ?? new PlaylistCapabilities(true, true, true, false, true),
            IsPublic: isPublic,
            BasePermissionRevision: basePermRev);

    [Fact]
    public void ClearDescription_NullSticks()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(Pl(description: "keep me"));
        store.UpsertPlaylist(Pl(description: null));   // ClearDescription
        Assert.Null(store.GetPlaylist(PlUri)!.Description);
    }

    [Fact]
    public void ClearPicture_NullCoverSticks()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(Pl(coverUrl: "https://i.scdn.co/image/keep"));
        store.UpsertPlaylist(Pl(coverUrl: null));   // ClearPicture
        Assert.Null(store.GetPlaylist(PlUri)!.Cover);
    }

    [Fact]
    public void DeadLetterRollback_RestoresPriorHeader()
    {
        var store = new InMemoryStore();
        var prior = Pl(name: "Before", description: "old");
        store.UpsertPlaylist(prior);
        var snapshot = store.GetPlaylist(PlUri)!;
        store.UpsertPlaylist(Pl(name: "Broken", description: "oops"));
        store.UpsertPlaylist(snapshot);   // Mutation dead-letter rollback
        var got = store.GetPlaylist(PlUri)!;
        Assert.Equal("Before", got.Name);
        Assert.Equal("old", got.Description);
    }

    [Fact]
    public void ThinEpisodeShowName_CannotClobberKnownName()
    {
        var store = new InMemoryStore();
        store.UpsertEpisode(new Episode("e1", EpUri, "Ep", "Real Show", null, 60_000, DateTimeOffset.UtcNow));
        store.UpsertEpisode(new Episode("e1", EpUri, "Ep", "", null, 60_000, DateTimeOffset.UtcNow));
        Assert.Equal("Real Show", store.GetEpisode(EpUri)!.ShowName);
    }

    [Fact]
    public void HeaderRefetchWithoutPermissionFields_CannotResetIsPublic()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(Pl(isPublic: false, basePermRev: "rev-1"));
        // Header-only refetch: no BasePermissionRevision → IsPublic stays private.
        store.UpsertPlaylist(Pl(isPublic: true, basePermRev: null));
        Assert.False(store.GetPlaylist(PlUri)!.IsPublic);
        Assert.Equal("rev-1", store.GetPlaylist(PlUri)!.BasePermissionRevision);
    }

    [Fact]
    public void Track_TitleUriEcho_DoesNotClobberResolvedTitle()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(new Track("t1", TrUri, "Real Title", [], new AlbumRef("", "", ""), 1000, false, null));
        store.UpsertTrack(new Track("t1", TrUri, TrUri, [], new AlbumRef("", "", ""), 1000, false, null));
        Assert.Equal("Real Title", store.GetTrack(TrUri)!.Title);
    }

    [Fact]
    public void Track_SameSourceCover_PrefersIncoming()
    {
        const string url = "https://i.scdn.co/image/same";
        var current = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image(url, Width: 64, Height: 64));
        var incoming = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image(url, Width: 640, Height: 640));
        var merged = StoreEntityMerge.Track(current, incoming);
        Assert.Equal(640, merged.Image!.Width);
    }

    [Fact]
    public void Track_DifferentSource_ChoosesHigherQuality()
    {
        var current = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image("https://i.scdn.co/image/hi", Width: 640, Height: 640));
        var incoming = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image("https://i.scdn.co/image/lo", Width: 64, Height: 64));
        var merged = StoreEntityMerge.Track(current, incoming);
        Assert.Equal("https://i.scdn.co/image/hi", merged.Image!.Url);
    }

    [Fact]
    public void Track_ThinUpsert_KeepsNonZeroYear()
    {
        var current = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false, null, Year: 2014);
        var incoming = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false, null, Year: 0);
        var merged = StoreEntityMerge.Track(current, incoming);
        Assert.Equal(2014, merged.Year);
    }

    [Fact]
    public void Track_IncomingYear_WinsWhenKnown()
    {
        var current = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false, null, Year: 2010);
        var incoming = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false, null, Year: 2014);
        var merged = StoreEntityMerge.Track(current, incoming);
        Assert.Equal(2014, merged.Year);
    }

    // ── Album.Tracks fold (MergeTrackRows) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Album_Merge_ShorterIncomingList_DoesNotTruncate()
    {
        // The getAlbum envelope caps at 50 rows; a healthy 100-row AlbumV4 list must survive it intact.
        var current = Enumerable.Range(1, 100).Select(n => TRow(n, $"Title{n}")).ToArray();
        var incoming = Enumerable.Range(1, 50).Select(n => TRow(n, $"Title{n}")).ToArray();

        var merged = StoreEntityMerge.Album(Al(current), Al(incoming));

        Assert.Equal(100, merged.Tracks!.Count);
        for (int i = 0; i < 50; i++) Assert.Equal($"spotify:track:t{i + 1}", merged.Tracks[i].Uri);          // incoming order
        for (int i = 50; i < 100; i++) Assert.Equal($"spotify:track:t{i + 1}", merged.Tracks[i].Uri);        // current tail, in order
    }

    [Fact]
    public void Album_Merge_BlankStubRow_KeepsNamedRow()
    {
        // The AlbumV4 disc-stub shape: incoming knows the row's uri + album/artist names but not its own title/duration
        // (Title=="", DurationMs==0, PlayCount==0) — the same "thin write must not downgrade a rich row" the Track
        // merge already enforces, now reachable through the Album fold.
        var current = new[] { TRow(1, title: "Real Title", durationMs: 200_000, playCount: 500) };
        var incoming = new[] { TRow(1, title: "", durationMs: 0, playCount: 0) };

        var merged = StoreEntityMerge.Album(Al(current), Al(incoming));

        var row = Assert.Single(merged.Tracks!);
        Assert.Equal("Real Title", row.Title);
        Assert.Equal(200_000, row.DurationMs);
        Assert.Equal(500, row.PlayCount);
    }

    [Fact]
    public void Album_Merge_IncomingPlayCount_Adopted()
    {
        var current = new[] { TRow(1, playCount: 0) };
        var incoming = new[] { TRow(1, playCount: 777) };

        var merged = StoreEntityMerge.Album(Al(current), Al(incoming));

        Assert.Equal(777, Assert.Single(merged.Tracks!).PlayCount);
    }

    [Fact]
    public void Album_Merge_NewUris_Adopted()
    {
        var current = new[] { TRow(1) };
        var incoming = new[] { TRow(1), TRow(2) };

        var merged = StoreEntityMerge.Album(Al(current), Al(incoming));

        Assert.Equal(2, merged.Tracks!.Count);
        Assert.Equal("spotify:track:t1", merged.Tracks[0].Uri);
        Assert.Equal("spotify:track:t2", merged.Tracks[1].Uri);
    }

    // ── Track.Artists name-aware fold ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Track_Merge_UriOnlyArtists_KeepNames()
    {
        var current = new Track("t1", TrUri, "T", [new ArtistRef("ar1", "spotify:artist:ar1", "Real Artist")],
            new AlbumRef("", "", ""), 1000, false, null);

        // A lean TrackV4 upsert carrying only the uri (no name) must not blank the resolved artist name.
        var thinIncoming = current with { Artists = [new ArtistRef("ar1", "spotify:artist:ar1", "")] };
        var thinMerged = StoreEntityMerge.Track(current, thinIncoming);
        Assert.Equal("Real Artist", Assert.Single(thinMerged.Artists).Name);

        // A genuinely named incoming artist (a real correction/rename) still replaces.
        var namedIncoming = current with { Artists = [new ArtistRef("ar1", "spotify:artist:ar1", "Renamed Artist")] };
        var namedMerged = StoreEntityMerge.Track(current, namedIncoming);
        Assert.Equal("Renamed Artist", Assert.Single(namedMerged.Artists).Name);
    }

    // ── Playlist/Show read-model lists must never adopt incoming ───────────────────────────────────────────────────

    [Fact]
    public void Playlist_Merge_EmptyName_KeepsCurrentName()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(Pl(name: "Real Name"));
        store.UpsertPlaylist(Pl(name: ""));   // playlist4 header patch that omitted name
        Assert.Equal("Real Name", store.GetPlaylist(PlUri)!.Name);
    }

    [Fact]
    public void Playlist_Merge_IncomingTracks_Ignored()
    {
        var currentTracks = new[] { TRow(1) };
        var current = Pl() with { Tracks = currentTracks };
        var incoming = Pl() with { Tracks = new[] { TRow(2) } };

        var merged = StoreEntityMerge.Playlist(current, incoming);

        Assert.Same(currentTracks, merged.Tracks);
    }

    [Fact]
    public void Playlist_Merge_IncomingCollaborators_Ignored()
    {
        var currentCollaborators = new[] { new Owner("o1", "Alice", null) };
        var current = Pl() with { Collaborators = currentCollaborators };
        var incoming = Pl() with { Collaborators = new[] { new Owner("o2", "Bob", null) } };

        var merged = StoreEntityMerge.Playlist(current, incoming);

        Assert.Same(currentCollaborators, merged.Collaborators);
    }

    [Fact]
    public void Show_Merge_IncomingEpisodes_Ignored()
    {
        var currentEpisodes = new[] { new Episode("e1", EpUri, "Ep One", "Show", null, 60_000, DateTimeOffset.UtcNow) };
        var current = new Show("s1", "spotify:show:s1", "Show", "Publisher", null, Episodes: currentEpisodes);
        var incoming = current with
        {
            Episodes = new[] { new Episode("e2", "spotify:episode:e2", "Ep Two", "Show", null, 60_000, DateTimeOffset.UtcNow) },
        };

        var merged = StoreEntityMerge.Show(current, incoming);

        Assert.Same(currentEpisodes, merged.Episodes);
    }
}
