using System.Linq;
using System.Runtime.CompilerServices;

namespace Wavee.Core;

/// <summary>The local-files peer source (docs/plans/wavee/architecture.md §1, §2 "Local files", §7). Owns the <c>local:</c> /
/// <c>wavee:local:*</c> uri namespace and serves a synthetic imported library — tracks with <see cref="TrackOrigin.Local"/>
/// + <c>Source="local"</c> (direct-decode, no CDN/decrypt), surfaced as a single "Local Files" collection. It is the
/// concrete proof of the seam's two-axis model: a SECOND catalog source the aggregate merges and routes to by uri,
/// declaring <see cref="SourceCapabilities.LocalDecode"/>. A real source swaps in a folder scan + ATL tag read here.</summary>
public sealed class LocalSource : ICatalogSource
{
    public string Id => "local";
    // `local:*` AND `wavee:local:*` both parse to EntityProviders.Local — one parser, one routing question
    // (hydration-facade-design.md §1.1).
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == EntityProviders.Local;
    public SourceCapabilities Capabilities =>
        SourceCapabilities.Catalog | SourceCapabilities.Search | SourceCapabilities.LocalDecode;

    static Task<T?> Ok<T>(T value) => Task.FromResult<T?>(value);

    // The whole local library presented as one "Local Files" playlist (the sidebar's Local row opens it). User-owned +
    // editable in principle (a real local source can rename/reorder its own lists); the cover is a generated gradient.
    public Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        var tracks = FakeData.LocalTracks();
        return Ok(new Playlist("local-all", uri, "Local Files", "Music imported from this computer.", "On this device",
            null, tracks.Count, tracks,
            Owner: new Owner("local", "On this device", null),
            Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: true, IsCollaborative: false, IsOwner: true),
            Source: "local"));
    }

    public Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        var tracks = FakeData.LocalTracks().Where(t => t.Album.Uri == uri).ToArray();
        string name = tracks.Length > 0 ? tracks[0].Album.Name : "Local Album";
        var artists = tracks.Length > 0
            ? tracks[0].Artists.Select(a => new ArtistRef(a.Id, a.Uri, a.Name)).ToArray()
            : System.Array.Empty<ArtistRef>();
        return Ok(new Album("localal", uri, name, null, artists, 0, tracks.Length, tracks, AlbumKind.Album));
    }

    public Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        var tracks = FakeData.LocalTracks().Where(t => t.Artists.Any(a => a.Uri == uri)).ToArray();
        string name = tracks.Length > 0 ? tracks[0].Artists.First(a => a.Uri == uri).Name : "Local Artist";
        return Ok(new Artist("localar", uri, name, null));
    }

    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var all = FakeData.LocalTracks();
        yield return new TrackPage(all, all.Count, all.Count);
    }

    // The local library is reached through its own sidebar row, not merged into the streamed collections — so the
    // collection reads stay EMPTY (no duplication in the merged union); only owned single-item reads + search contribute.
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryItem>>(System.Array.Empty<LibraryItem>());
    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlaylistSummary>>(System.Array.Empty<PlaylistSummary>());
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Album>>(System.Array.Empty<Album>());
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Artist>>(System.Array.Empty<Artist>());
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Track>>(System.Array.Empty<Track>());

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return Task.FromResult(SearchResults.Empty);
        var tracks = FakeData.LocalTracks().Where(t =>
            t.Title.Contains(q, System.StringComparison.OrdinalIgnoreCase) ||
            t.Artists.Any(a => a.Name.Contains(q, System.StringComparison.OrdinalIgnoreCase))).ToArray();
        return Task.FromResult(new SearchResults(tracks, System.Array.Empty<Album>(), System.Array.Empty<Artist>(), System.Array.Empty<Playlist>()));
    }

    public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default)
        => Task.FromResult(new HomeContribution(System.Array.Empty<HomeGroup>(), Priority: 50));
    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new LibraryStats(0, 0, 0, 0));
}
