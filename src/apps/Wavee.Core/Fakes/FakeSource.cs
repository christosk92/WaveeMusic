using System.Runtime.CompilerServices;

namespace Wavee.Core;

/// <summary>The synthetic fallback catalog source (docs/plans/wavee/architecture.md §9). The Spotify source owns every
/// <c>spotify:*</c> URI (and synthesizes when it lacks real data), so this source's job is to CONTRIBUTE the
/// collections the export has no data for — the synthetic "Your Albums" / "Your Artists" lists — plus serve as the
/// demo backend's <see cref="SourceCapabilities.Fallback"/>: the LAST RESORT for a single-item read no source owns.
/// Wraps <see cref="FakeData"/>.</summary>
public sealed class FakeSource : ICatalogSource
{
    public string Id => "fake";
    // P4: this OWNS exactly its own namespace — `fake:*` and the bare legacy ids FakeData mints (EntityProviders.Fake).
    // The old catch-all ("anything that isn't spotify:") made this source the routing owner of every peer namespace —
    // local files, user playlists, podcasts — which the hydration router would then have sent HERE instead of to the
    // source that actually holds the data. Being the last resort is a CAPABILITY (SourceCapabilities.Fallback), which
    // AggregateCatalog consults as an explicit final step, not an ownership claim.
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == EntityProviders.Fake;
    public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Fallback;

    public Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        => Task.FromResult<Playlist?>(FakeData.Playlist(FakeData.IndexFromUri(uri)));
    public Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        => Task.FromResult<Album?>(FakeData.Album(FakeData.IndexFromUri(uri)));
    public Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        => Task.FromResult<Artist?>(FakeData.Artist(FakeData.IndexFromUri(uri)));

    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var all = FakeData.ContextTracks(contextUri);
        await Task.CompletedTask;
        yield return new TrackPage(all, all.Count, all.Count);
    }

    // Spotify provides library / playlists / liked / home / search → empty here (no duplication in the merged union).
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryItem>>(System.Array.Empty<LibraryItem>());
    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlaylistSummary>>(System.Array.Empty<PlaylistSummary>());

    /// <summary>The synthetic FOLDER-CAPABLE tree. The interface default would project this source's (empty) flat list, so
    /// the demo backend would have no folders at all and every folder path — recursion, expand/collapse, folder pins,
    /// search flattening — would be untestable against the fakes. <see cref="FakeData.PlaylistTree"/> is the same
    /// structure <c>FakeData.UserPlaylists</c> flattens, so the leaves and the flat list agree by construction.</summary>
    public Task<IReadOnlyList<PlaylistNode>> GetPlaylistTreeAsync(CancellationToken ct = default)
        => Task.FromResult(FakeData.PlaylistTree());
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Track>>(System.Array.Empty<Track>());
    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
        => Task.FromResult(new SearchResults(System.Array.Empty<Track>(), System.Array.Empty<Album>(), System.Array.Empty<Artist>(), System.Array.Empty<Playlist>()));
    public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default)
        => Task.FromResult(new HomeContribution(System.Array.Empty<HomeGroup>(), Priority: 100));

    // The export carries no saved albums/artists → the synthetic catalog provides those collections (the sidebar pages).
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var a = new Album[13];
        for (int i = 0; i < a.Length; i++) a[i] = FakeData.Album(20 + i);
        return Task.FromResult<IReadOnlyList<Album>>(a);
    }
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        var a = new Artist[12];
        for (int i = 0; i < a.Length; i++) a[i] = FakeData.Artist(30 + i);
        return Task.FromResult<IReadOnlyList<Artist>>(a);
    }

    // Albums/Artists counts come from this source; Liked count comes from Spotify; summed by the aggregate.
    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new LibraryStats(13, 12, 0, 7));
}
