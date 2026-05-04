using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;

namespace Wavee.Core.Http;

/// <summary>
/// Interface for Spotify extended metadata API client with caching.
/// </summary>
public interface IExtendedMetadataClient
{
    /// <summary>
    /// Gets extension data for a single entity, using cache when available.
    /// </summary>
    /// <param name="entityUri">The Spotify entity URI.</param>
    /// <param name="extensionKind">The extension kind to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extension data bytes, or null if not found.</returns>
    Task<byte[]?> GetExtensionAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audio files for a track.
    /// </summary>
    /// <param name="trackUri">The track URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Track with audio files, or null if not found.</returns>
    Task<Track?> GetTrackAudioFilesAsync(
        string trackUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets full track metadata (TRACK_V4) including audio files.
    /// Also stores queryable properties in the database.
    /// </summary>
    /// <param name="trackUri">The track URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Track metadata, or null if not found.</returns>
    Task<Track?> GetTrackAsync(
        string trackUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches multiple extensions for multiple entities in a single batch request.
    /// More efficient than individual calls.
    /// </summary>
    /// <param name="requests">List of (entityUri, extensionKinds) tuples.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Batched extension response.</returns>
    Task<BatchedExtensionResponse> GetBatchedExtensionsAsync(
        IEnumerable<(string EntityUri, IEnumerable<ExtensionKind> Extensions)> requests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cached data for an entity.
    /// </summary>
    /// <param name="entityUri">The entity URI to invalidate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InvalidateCacheAsync(string entityUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up expired cache entries.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of expired entries removed.</returns>
    Task<int> CleanupExpiredCacheAsync(CancellationToken cancellationToken = default);
}
