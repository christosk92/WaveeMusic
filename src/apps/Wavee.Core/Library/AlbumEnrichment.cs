namespace Wavee.Core;

/// <summary>Track-scoped data used by short releases. Spotify returns the video signal and the lead artist's related
/// artists together from <c>getTrack</c>, so one request supplies both consumers.</summary>
public sealed record AlbumTrackContext(bool HasVideo, IReadOnlyList<Artist> RelatedArtists)
{
    public static readonly AlbumTrackContext Empty = new(false, Array.Empty<Artist>());
}

/// <summary>Secondary album-page reads. These are deliberately separate from <see cref="IMusicLibrary.GetAlbumAsync"/>:
/// the hero and track list must not wait for below-the-fold recommendations or commerce data.</summary>
public interface IAlbumEnrichmentService
{
    Task<NowPlayingInfo?> GetNowPlayingInfoAsync(string artistUri, string trackUri, CancellationToken ct = default);
    Task<Artist?> GetAboutArtistAsync(string artistUri, string leadTrackUri, CancellationToken ct = default);
    Task<IReadOnlyList<Artist>> GetRelatedArtistsAsync(string artistUri, CancellationToken ct = default);
    Task<AlbumTrackContext?> GetTrackContextAsync(string trackUri, CancellationToken ct = default);
    Task<IReadOnlyList<MerchItem>> GetMerchAsync(string albumUri, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetSimilarAlbumsAsync(string seedTrackUri, int limit = 24, CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistSummary>> GetRecommendedPlaylistsAsync(string albumUri, CancellationToken ct = default);
}

/// <summary>A stable service identity whose live provider can be installed after login without rebuilding the UI tree.</summary>
public sealed class SwitchableAlbumEnrichmentService : IAlbumEnrichmentService
{
    IAlbumEnrichmentService _inner;
    public SwitchableAlbumEnrichmentService(IAlbumEnrichmentService inner) => _inner = inner;
    public void SetInner(IAlbumEnrichmentService inner)
        => System.Threading.Volatile.Write(ref _inner, inner ?? throw new ArgumentNullException(nameof(inner)));

    IAlbumEnrichmentService Current => System.Threading.Volatile.Read(ref _inner);
    public Task<NowPlayingInfo?> GetNowPlayingInfoAsync(string artistUri, string trackUri, CancellationToken ct = default)
        => Current.GetNowPlayingInfoAsync(artistUri, trackUri, ct);
    public Task<Artist?> GetAboutArtistAsync(string artistUri, string leadTrackUri, CancellationToken ct = default)
        => Current.GetAboutArtistAsync(artistUri, leadTrackUri, ct);
    public Task<IReadOnlyList<Artist>> GetRelatedArtistsAsync(string artistUri, CancellationToken ct = default)
        => Current.GetRelatedArtistsAsync(artistUri, ct);
    public Task<AlbumTrackContext?> GetTrackContextAsync(string trackUri, CancellationToken ct = default)
        => Current.GetTrackContextAsync(trackUri, ct);
    public Task<IReadOnlyList<MerchItem>> GetMerchAsync(string albumUri, CancellationToken ct = default)
        => Current.GetMerchAsync(albumUri, ct);
    public Task<IReadOnlyList<Album>> GetSimilarAlbumsAsync(string seedTrackUri, int limit = 24, CancellationToken ct = default)
        => Current.GetSimilarAlbumsAsync(seedTrackUri, limit, ct);
    public Task<IReadOnlyList<PlaylistSummary>> GetRecommendedPlaylistsAsync(string albumUri, CancellationToken ct = default)
        => Current.GetRecommendedPlaylistsAsync(albumUri, ct);
}

/// <summary>Offline/fake fallback: real artist records remain useful, while provider-only recommendation calls are empty.</summary>
public sealed class CatalogAlbumEnrichmentService : IAlbumEnrichmentService
{
    readonly IMusicLibrary _library;
    public CatalogAlbumEnrichmentService(IMusicLibrary library) => _library = library;

    public async Task<NowPlayingInfo?> GetNowPlayingInfoAsync(string artistUri, string trackUri, CancellationToken ct = default)
        => new(string.IsNullOrEmpty(artistUri) ? null : await _library.GetArtistAsync(artistUri, ct).ConfigureAwait(false), null);

    public async Task<Artist?> GetAboutArtistAsync(string artistUri, string leadTrackUri, CancellationToken ct = default)
        => string.IsNullOrEmpty(artistUri) ? null : await _library.GetArtistAsync(artistUri, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Artist>> GetRelatedArtistsAsync(string artistUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(artistUri)) return Array.Empty<Artist>();
        var artist = await _library.GetArtistAsync(artistUri, ct).ConfigureAwait(false);
        if (artist.Extras?.Related is not { Count: > 0 } related) return Array.Empty<Artist>();
        var result = new List<Artist>(related.Count);
        foreach (var a in related) result.Add(new Artist(a.Id, a.Uri, a.Name, a.Image));
        return result;
    }

    public Task<AlbumTrackContext?> GetTrackContextAsync(string trackUri, CancellationToken ct = default)
        => Task.FromResult<AlbumTrackContext?>(null);
    public Task<IReadOnlyList<MerchItem>> GetMerchAsync(string albumUri, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MerchItem>>(Array.Empty<MerchItem>());
    public Task<IReadOnlyList<Album>> GetSimilarAlbumsAsync(string seedTrackUri, int limit = 24, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Album>>(Array.Empty<Album>());
    public Task<IReadOnlyList<PlaylistSummary>> GetRecommendedPlaylistsAsync(string albumUri, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlaylistSummary>>(Array.Empty<PlaylistSummary>());
}
