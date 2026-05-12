namespace Wavee.Local;

/// <summary>
/// Like/favorite state for local tracks. Backed by the
/// <c>entities.is_locally_liked</c> column. Spotify track likes flow through
/// the existing <c>ITrackLikeService</c>; this is the parallel for
/// <c>wavee:local:track:*</c> URIs and never calls the Spotify API.
/// </summary>
public interface ILocalLikeService
{
    Task<bool> IsLikedAsync(string trackUri, CancellationToken ct = default);
    Task SetLikedAsync(string trackUri, bool liked, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLikedTrackUrisAsync(CancellationToken ct = default);
    IObservable<(string TrackUri, bool Liked)> Changes { get; }
}
