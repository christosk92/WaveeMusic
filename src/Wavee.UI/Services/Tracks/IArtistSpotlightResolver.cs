using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.Tracks;

/// <summary>One credit row on the artist spotlight card (e.g. "Jukjae — Writer").</summary>
public sealed record ArtistCredit(string Name, string Role);

/// <summary>
/// Rich "who am I auditioning" context for the current refresh card: the track's primary artist
/// (avatar, monthly listeners, short bio), this track's credits, and its looping Canvas video.
/// </summary>
public sealed record ArtistSpotlight(
    string ArtistName,
    string? AvatarUrl,
    long MonthlyListeners,
    string? Bio,
    IReadOnlyList<ArtistCredit> Credits,
    string? CanvasUrl);

/// <summary>
/// Resolves the <see cref="ArtistSpotlight"/> for a track via Spotify's NPV ("Now Playing View")
/// query. Cached by track URI and prefetched for upcoming cards so the panel is ready on swipe.
/// </summary>
public interface IArtistSpotlightResolver
{
    /// <summary>Spotlight for a track, or null when the artist URI is missing / the query fails.</summary>
    Task<ArtistSpotlight?> ResolveAsync(string trackUri, string? artistUri, CancellationToken ct = default);

    /// <summary>Warm the cache for upcoming cards. Never throws per item.</summary>
    Task PrefetchAsync(IReadOnlyList<(string TrackUri, string? ArtistUri)> tracks, CancellationToken ct = default);
}
