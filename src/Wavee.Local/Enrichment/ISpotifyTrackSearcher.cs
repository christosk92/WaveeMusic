namespace Wavee.Local.Enrichment;

/// <summary>
/// Wavee.Local-side abstraction for searching Spotify's catalog. Wavee.Local
/// can't reference Wavee.dll (would be circular), so the actual GraphQL call
/// lives in <c>Wavee.UI.WinUI.Services.PathfinderSpotifyTrackSearcher</c>.
/// Same pattern as <see cref="Subtitles.IEmbeddedTrackProber"/>.
///
/// <para>When no implementation is registered — or the user is signed out —
/// implementations should return an empty list rather than throw. The
/// enrichment service treats "no match" identically whether it's an
/// auth failure, a network drop, or a genuinely missing recording.</para>
/// </summary>
public interface ISpotifyTrackSearcher
{
    Task<IReadOnlyList<SpotifyTrackMatch>> SearchTracksAsync(
        string query, int limit, CancellationToken ct);
}

/// <summary>
/// Lightweight projection of one Spotify search hit — just the URIs and
/// surface metadata we need to link a local track. We never pull full track
/// detail here; clicking through the linked album / artist URI opens
/// AlbumPage / ArtistPage which load their own rich data via Pathfinder.
/// </summary>
public sealed record SpotifyTrackMatch(
    string TrackUri,
    string Title,
    long DurationMs,
    string? AlbumUri,
    string? AlbumName,
    string? CoverImageUri,
    string? FirstArtistUri,
    string? FirstArtistName);
