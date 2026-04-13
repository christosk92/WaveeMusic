using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Bulk-fetches <see cref="ExtensionKind.TrackDescriptor"/> data and persists results via
/// the shared <c>extension_cache</c> table. For URIs that come back without descriptor data,
/// writes a zero-length blob as a negative-cache marker so subsequent calls skip them.
/// </summary>
public sealed class TrackDescriptorFetcher : ITrackDescriptorFetcher
{
    // Matches TrackMetadataEnricher's proven batch size.
    private const int BatchSize = 500;

    // Negative-cache TTL: tracks that Spotify didn't return descriptors for aren't re-fetched for a day.
    private const long NegativeCacheTtlSeconds = 86400;

    private readonly IExtendedMetadataClient _metadataClient;
    private readonly IMetadataDatabase _database;
    private readonly ILogger<TrackDescriptorFetcher>? _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _isFetching;

    public TrackDescriptorFetcher(
        IExtendedMetadataClient metadataClient,
        IMetadataDatabase database,
        ILogger<TrackDescriptorFetcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(metadataClient);
        ArgumentNullException.ThrowIfNull(database);

        _metadataClient = metadataClient;
        _database = database;
        _logger = logger;
    }

    public bool IsFetching => _isFetching;

    public event EventHandler? FetchCompleted;

    public async Task EnqueueAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (trackUris == null || trackUris.Count == 0)
            return;

        // Serialize runs so two page opens don't fan out parallel batches at Spotify.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool didFetch = false;
        try
        {
            _isFetching = true;
            didFetch = await RunAsync(trackUris, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown — partial progress is still valuable (cached bytes persist).
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TrackDescriptor bulk fetch failed");
        }
        finally
        {
            _isFetching = false;
            _gate.Release();

            // Only raise the event when we actually fetched something new. Raising it on
            // every no-op call creates an infinite LoadAsync ↔ FetchCompleted bounce because
            // the VM reacts by reloading, which re-enters EnqueueAsync, which fires the event
            // again, ad infinitum.
            if (didFetch)
            {
                try { FetchCompleted?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger?.LogWarning(ex, "FetchCompleted handler threw"); }
            }
        }
    }

    /// <summary>
    /// Returns true iff at least one batch was actually sent to the server (i.e. the
    /// on-disk cache grew or changed). Returns false when all URIs were already cached,
    /// so callers can distinguish "cache changed" from "cache already up to date".
    /// </summary>
    private async Task<bool> RunAsync(IReadOnlyList<string> trackUris, CancellationToken ct)
    {
        // Deduplicate + filter invalid URIs.
        var unique = trackUris
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unique.Count == 0) return false;

        // First pass: read what we already have. The dictionary includes both real descriptor
        // blobs AND zero-length negative-cache entries — either way we don't need to re-fetch.
        var cached = await _database
            .GetExtensionsBulkAsync(unique, ExtensionKind.TrackDescriptor, ct)
            .ConfigureAwait(false);

        var uncached = unique.Where(u => !cached.ContainsKey(u)).ToList();

        _logger?.LogDebug(
            "TrackDescriptor fetch: {Total} requested, {Cached} cached, {Uncached} to fetch",
            unique.Count, cached.Count, uncached.Count);

        if (uncached.Count == 0) return false;

        for (int offset = 0; offset < uncached.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = uncached.Skip(offset).Take(BatchSize).ToList();
            var requests = batch
                .Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackDescriptor }));

            try
            {
                // ExtendedMetadataClient writes positive entries into extension_cache internally.
                await _metadataClient.GetBatchedExtensionsAsync(requests, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "TrackDescriptor batch fetch failed at offset {Offset} (size {Size})",
                    offset, batch.Count);
                // Continue with next batch — one bad batch shouldn't kill 9,500 others.
                continue;
            }

            // Re-read this batch from cache; any URI still missing = Spotify returned nothing for it.
            // Write an empty-blob negative-cache marker so we don't re-fetch it next time.
            var refreshed = await _database
                .GetExtensionsBulkAsync(batch, ExtensionKind.TrackDescriptor, ct)
                .ConfigureAwait(false);

            foreach (var uri in batch)
            {
                if (refreshed.ContainsKey(uri)) continue;

                try
                {
                    await _database.SetExtensionAsync(
                        uri,
                        ExtensionKind.TrackDescriptor,
                        Array.Empty<byte>(),
                        etag: null,
                        ttlSeconds: NegativeCacheTtlSeconds,
                        cancellationToken: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogTrace(ex, "Failed to write negative-cache for {Uri}", uri);
                }
            }
        }

        return true;
    }
}
