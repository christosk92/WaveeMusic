using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.Tracks;

/// <summary>Primary + accent colours (hex <c>#RRGGBB</c>) extracted from a track's album art.</summary>
public readonly record struct TrackPalette(string PrimaryHex, string AccentHex);

/// <summary>
/// Resolves a per-track colour palette for the immersive refresh background. Implemented in the
/// WinUI layer (image decode + palette extraction); cached by URI and prefetched for upcoming cards
/// so the swipe colour-morph is instant.
/// </summary>
public interface ITrackColorResolver
{
    /// <summary>Palette for a track, or null when art is unavailable / extraction fails.</summary>
    Task<TrackPalette?> ResolveAsync(string trackUri, string? imageUrl, CancellationToken ct = default);

    /// <summary>Warm the cache for upcoming cards. Never throws per item.</summary>
    Task PrefetchAsync(IReadOnlyList<(string Uri, string? ImageUrl)> tracks, CancellationToken ct = default);
}
