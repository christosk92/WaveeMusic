using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Bulk-fetches Spotify <c>TRACK_DESCRIPTOR</c> extension data for a list of track URIs,
/// parses descriptors into tags, and persists them via the shared extension cache.
/// </summary>
public interface ITrackDescriptorFetcher
{
    /// <summary>
    /// Whether a bulk fetch is currently in flight.
    /// </summary>
    bool IsFetching { get; }

    /// <summary>
    /// Enqueues a bulk fetch for the given track URIs. Safe to call concurrently — a
    /// single in-flight task coalesces new requests. URIs already present (with real data
    /// or a negative-cache marker) within the cache TTL are skipped.
    /// </summary>
    Task EnqueueAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default);

    /// <summary>
    /// Raised when a bulk fetch run completes (successfully or partially). Consumers
    /// should re-read tags from <c>extension_cache</c> to pick up newly populated entries.
    /// </summary>
    event EventHandler? FetchCompleted;
}
