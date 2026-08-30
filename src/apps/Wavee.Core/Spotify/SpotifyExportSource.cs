using System.Linq;
using System.Runtime.CompilerServices;
using static Wavee.Core.SpotifyExportMapper;

namespace Wavee.Core;

/// <summary>The Spotify catalog adapter (docs/plans/wavee/architecture.md §4.4, §9): owns <c>spotify:*</c> URIs and serves real
/// data from the bundled export — the 22 library playlists, the grouped home feed, the Iced Americano playlist with its
/// 15 real tracks, and real CDN cover art. Where the export has only a header/card (every other playlist/album/artist),
/// it SYNTHESIZES tracks deterministically (seeded by the URI hash) via <see cref="FakeData"/> so the page is still
/// populated. Catalog-only this pass; playback/remote/session stay the existing fakes.</summary>
public sealed class SpotifyExportSource : ICatalogSource
{
    readonly SpotifyExport _x;
    public SpotifyExportSource(SpotifyExport export) => _x = export;

    public string Id => "spotify";
    // Routing asks the ONE parser who the provider is (hydration-facade-design.md §1.1) — adding a namespace is a
    // one-line change in EntityProviders, not a sweep of hand-rolled prefix tests.
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == EntityProviders.Spotify;
    public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Home | SourceCapabilities.Search;

    static Task<T?> Ok<T>(T value) => Task.FromResult<T?>(value);

    // ── playlists ──────────────────────────────────────────────────────────────────────────────────────────
    public Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        if (_x.TryGetFullPlaylist(uri, out var full)) return Ok(full);   // Iced Americano — real tracks
        Playlist header =
            _x.TryGetHeader(uri, out var h) ? h
            : _x.TryGetCard(uri, out var card) && card.Kind == HomeCardKind.Playlist
                ? new Playlist(EntityUri.IdOf(uri), uri, card.Title, card.Subtitle, card.Subtitle ?? "Spotify", card.Image, SynthCount(uri), System.Array.Empty<Track>(), null, default, null, "spotify")
                : new Playlist(EntityUri.IdOf(uri), uri, "Playlist", null, "Spotify", null, SynthCount(uri), System.Array.Empty<Track>(), null, default, null, "spotify");
        return Ok(header with { Tracks = SynthPlaylistTracks(uri, header.TrackCount) });
    }

    public Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        // A home card → real name/cover + themed synth tracks. No card → FakeData's own album (consistent with the
        // synthetic "Your Albums" collection the Fake source contributes), keyed by the URI so it's deterministic.
        var fake = FakeData.Album(FakeData.IndexFromUri(uri));
        if (_x.TryGetCard(uri, out var card))
        {
            string artistName = card.Subtitle ?? "Various Artists";
            var tracks = SynthAlbumTracks(uri, card.Title, artistName, card.Image);
            return Ok(new Album(EntityUri.IdOf(uri), uri, card.Title, card.Image, new[] { ArtistRefFor(artistName) },
                2014 + Hash(uri) % 11, tracks.Count, tracks, AlbumKind.Album));
        }
        return Ok(new Album(EntityUri.IdOf(uri), uri, fake.Name, fake.Cover, fake.Artists, fake.Year,
            fake.TrackCount, fake.Tracks ?? System.Array.Empty<Track>(), fake.Kind));
    }

    public Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        var fake = FakeData.Artist(FakeData.IndexFromUri(uri));
        // A real artist export (artist-*.json) → the full magazine page. The overview query lacks stats / top cities /
        // music videos, so backfill those from the deterministic fake so every section still populates.
        if (_x.TryGetArtist(uri, out var real))
            return Ok(real with
            {
                MonthlyListeners = real.MonthlyListeners > 0 ? real.MonthlyListeners : fake.MonthlyListeners,
                Followers = real.Followers > 0 ? real.Followers : fake.Followers,
                WorldRank = real.WorldRank > 0 ? real.WorldRank : fake.WorldRank,
                Extras = MergeExtras(real.Extras, fake.Extras),
            });
        _x.TryGetCard(uri, out var card);
        return Ok(card is null ? fake : fake with { Name = card.Title, Image = card.Image });
    }

    // Real lists win; the synthesized fallback fills the facets the overview query doesn't carry (videos / cities).
    static ArtistExtras? MergeExtras(ArtistExtras? real, ArtistExtras? fake)
    {
        if (real is null) return fake;
        if (fake is null) return real;
        return real with
        {
            MusicVideos = real.MusicVideos ?? fake.MusicVideos,
            TopCities = real.TopCities ?? fake.TopCities,
        };
    }

    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var all = ResolveTracks(contextUri);
        int total = all.Count;
        if (total == 0) { yield return new TrackPage(System.Array.Empty<Track>(), 0, 0); yield break; }
        const int page = 25;
        var acc = new List<Track>(total);
        for (int i = 0; i < total; i += page)
        {
            await Task.Delay(120, ct).ConfigureAwait(false);
            for (int j = i; j < Math.Min(i + page, total); j++) acc.Add(all[j]);
            yield return new TrackPage(acc.ToArray(), acc.Count, total);
        }
    }

    IReadOnlyList<Track> ResolveTracks(string uri)
    {
        if (_x.TryGetFullPlaylist(uri, out var full) && full.Tracks is { } t) return t;
        int count = _x.TryGetHeader(uri, out var h) ? h.TrackCount : SynthCount(uri);
        return SynthPlaylistTracks(uri, count);
    }

    // ── collections ────────────────────────────────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
    {
        var list = new List<LibraryItem>
        {
            new("spotify:collection:tracks", "Liked Songs", $"Playlist · {_x.LikedCount} songs", null, LibraryItemKind.Playlist),
        };
        foreach (var p in _x.LibraryPlaylists)
            list.Add(new LibraryItem(p.Uri, p.Name, $"Playlist · {p.OwnerName}", p.Cover, LibraryItemKind.Playlist));
        return Task.FromResult<IReadOnlyList<LibraryItem>>(list);
    }

    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
        => Task.FromResult(_x.LibraryPlaylists);

    // No saved-albums / saved-artists data in the export → the Fake source contributes those synthetic collections.
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Album>>(System.Array.Empty<Album>());
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Artist>>(System.Array.Empty<Artist>());

    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Track>>(FakeData.LikedSongs(Math.Max(1, _x.LikedCount)));

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return Task.FromResult(SearchResults.Empty);
        var matches = _x.LibraryPlaylists
            .Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(p => new Playlist(EntityUri.IdOf(p.Uri), p.Uri, p.Name, null, p.OwnerName, p.Cover, p.TrackCount))
            .ToList();
        var fd = FakeData.Search(q);
        // Surface real exported artists (e.g. Maroon 5) by name so they're reachable from search.
        var artistHits = _x.Artists.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        IReadOnlyList<Artist> artists = artistHits.Count > 0 ? artistHits.Concat(fd.Artists).ToList() : fd.Artists;
        return Task.FromResult(new SearchResults(fd.Tracks, fd.Albums, artists, matches.Count > 0 ? matches : fd.Playlists));
    }

    public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default) => Task.FromResult(_x.Home);

    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new LibraryStats(0, 0, _x.LikedCount, 0));

    // ── synthesis helpers (deterministic per URI) ───────────────────────────────────────────────────────────
    static IReadOnlyList<Track> SynthPlaylistTracks(string uri, int count)
        => FakeData.Tracks(Math.Max(0, count), Hash(uri) % 800);

    static IReadOnlyList<Track> SynthAlbumTracks(string uri, string albumName, string artistName, Image? cover)
    {
        int n = 6 + Hash(uri) % 8;
        var albumRef = new AlbumRef(EntityUri.IdOf(uri), uri, albumName);
        var artist = ArtistRefFor(artistName);
        int seed = Hash(uri);
        var list = new List<Track>(n);
        for (int i = 0; i < n; i++)
        {
            var b = FakeData.Track(seed + i);
            list.Add(b with { Album = albumRef, Artists = new[] { artist }, Image = cover ?? b.Image, Source = "spotify" });
        }
        return list;
    }

    static ArtistRef ArtistRefFor(string name)
    {
        string uri = "spotify:artist:" + Hash(name);
        return new ArtistRef(EntityUri.IdOf(uri), uri, name);
    }
}
