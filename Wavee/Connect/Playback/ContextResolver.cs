using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Storage;
using Wavee.Protocol.Context;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Connect.Playback;

/// <summary>
/// Resolves Spotify context URIs (playlists, albums, etc.) to track lists.
/// Handles pagination and metadata enrichment via batch fetching.
/// </summary>
public sealed class ContextResolver
{
    private readonly SpClient _spClient;
    private readonly IExtendedMetadataClient _metadataClient;
    private readonly ICacheService _cacheService;
    private readonly ILogger? _logger;

    // Context cache - caches resolved context (track URIs) to avoid API calls
    private readonly HotCache<ContextCacheEntry> _contextCache;

    // TTL values for different context types
    private static readonly TimeSpan PlaylistTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlbumTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan StationTtl = TimeSpan.FromMinutes(2);

    private const int BatchSize = 500;  // API supports up to 500 items per request

    public ContextResolver(
        SpClient spClient,
        IExtendedMetadataClient metadataClient,
        ICacheService cacheService,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(spClient);
        ArgumentNullException.ThrowIfNull(metadataClient);
        ArgumentNullException.ThrowIfNull(cacheService);

        _spClient = spClient;
        _metadataClient = metadataClient;
        _cacheService = cacheService;
        _logger = logger;

        // Context cache is smaller since contexts are larger objects
        _contextCache = new HotCache<ContextCacheEntry>(maxSize: 50, logger);
    }

    /// <summary>
    /// Loads a context (playlist, album, etc.) and returns tracks with metadata.
    /// </summary>
    /// <param name="contextUri">Spotify context URI (e.g., "spotify:playlist:xxx").</param>
    /// <param name="maxTracks">Maximum tracks to load initially (null for all).</param>
    /// <param name="enrichMetadata">Whether to batch fetch full metadata for sorting.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Context load result with tracks and pagination info.</returns>
    public async Task<ContextLoadResult> LoadContextAsync(
        string contextUri,
        int? maxTracks = null,
        bool enrichMetadata = true,
        CancellationToken ct = default)
    {
        _logger?.LogDebug("Loading context: {ContextUri}, maxTracks={MaxTracks}, enrich={Enrich}",
            contextUri, maxTracks, enrichMetadata);

        // Phase 0: Check context cache first
        var cachedContext = _contextCache.Get(contextUri);
        if (cachedContext != null && cachedContext.IsValid)
        {
            _logger?.LogDebug("Context cache hit: {ContextUri}, {TrackCount} tracks",
                contextUri, cachedContext.Tracks.Count);

            // Use cached track URIs, still enrich metadata (that cache is separate)
            var cachedTrackInfos = cachedContext.Tracks.ToList();
            if (maxTracks.HasValue && cachedTrackInfos.Count > maxTracks.Value)
            {
                cachedTrackInfos = cachedTrackInfos.Take(maxTracks.Value).ToList();
            }

            IReadOnlyList<QueueTrack> cachedTracks;
            if (enrichMetadata && cachedTrackInfos.Count > 0)
            {
                cachedTracks = await EnrichTracksAsync(cachedTrackInfos, ct);
            }
            else
            {
                cachedTracks = cachedTrackInfos.Select(t => new QueueTrack(t.Uri, t.Uid)).ToList();
            }

            return new ContextLoadResult(
                Tracks: cachedTracks,
                TotalCount: cachedContext.TotalCount,
                NextPageUrl: cachedContext.NextPageUrl,
                IsInfinite: cachedContext.IsInfinite
            );
        }

        // Phase A: Get track URIs from context-resolve API (cache miss)
        _logger?.LogDebug("Context cache miss, fetching from API: {ContextUri}", contextUri);
        var context = await _spClient.ResolveContextAsync(contextUri, ct);

        var trackInfos = new List<(string Uri, string? Uid)>();
        string? nextPageUrl = null;

        foreach (var page in context.Pages)
        {
            foreach (var track in page.Tracks)
            {
                if (string.IsNullOrEmpty(track.Uri))
                    continue;

                trackInfos.Add((track.Uri, track.Uid));

                if (maxTracks.HasValue && trackInfos.Count >= maxTracks.Value)
                    break;
            }

            // Store next page URL for lazy loading
            if (!string.IsNullOrEmpty(page.NextPageUrl))
            {
                nextPageUrl = page.NextPageUrl;
            }
            else if (!string.IsNullOrEmpty(page.PageUrl) && page.Tracks.Count > 0)
            {
                nextPageUrl = page.PageUrl;
            }

            if (maxTracks.HasValue && trackInfos.Count >= maxTracks.Value)
                break;
        }

        _logger?.LogDebug("Context resolved: {TrackCount} tracks, nextPage={HasNext}",
            trackInfos.Count, nextPageUrl != null);

        // Cache the raw context result (before metadata enrichment)
        var ttl = GetContextTtl(contextUri);
        var totalCount = GetTotalFromMetadata(context);
        var isInfinite = IsInfiniteContext(contextUri);

        _contextCache.Set(contextUri, new ContextCacheEntry
        {
            Uri = contextUri,
            ExpiresAt = DateTimeOffset.UtcNow + ttl,
            Tracks = trackInfos,
            NextPageUrl = nextPageUrl,
            TotalCount = totalCount,
            IsInfinite = isInfinite
        });

        _logger?.LogDebug("Context cached: {ContextUri}, TTL={TtlMinutes}m",
            contextUri, ttl.TotalMinutes);

        // Phase B: Batch enrich with metadata (uses cache)
        IReadOnlyList<QueueTrack> tracks;
        if (enrichMetadata && trackInfos.Count > 0)
        {
            tracks = await EnrichTracksAsync(trackInfos, ct);
        }
        else
        {
            tracks = trackInfos.Select(t => new QueueTrack(t.Uri, t.Uid)).ToList();
        }

        return new ContextLoadResult(
            Tracks: tracks,
            TotalCount: totalCount,
            NextPageUrl: nextPageUrl,
            IsInfinite: isInfinite
        );
    }

    /// <summary>
    /// Loads the next page of tracks.
    /// </summary>
    /// <param name="pageUrl">Page URL from previous result's NextPageUrl.</param>
    /// <param name="enrichMetadata">Whether to batch fetch full metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next page of tracks with pagination info.</returns>
    public async Task<ContextLoadResult> LoadNextPageAsync(
        string pageUrl,
        bool enrichMetadata = true,
        CancellationToken ct = default)
    {
        _logger?.LogDebug("Loading next page: {PageUrl}", pageUrl);

        var page = await _spClient.GetNextPageAsync(pageUrl, ct);

        var trackInfos = page.Tracks
            .Where(t => !string.IsNullOrEmpty(t.Uri))
            .Select(t => (t.Uri, t.Uid))
            .ToList();

        IReadOnlyList<QueueTrack> tracks;
        if (enrichMetadata && trackInfos.Count > 0)
        {
            tracks = await EnrichTracksAsync(trackInfos, ct);
        }
        else
        {
            tracks = trackInfos.Select(t => new QueueTrack(t.Uri, t.Uid)).ToList();
        }

        var nextPageUrl = page.NextPageUrl ?? page.PageUrl;

        return new ContextLoadResult(
            Tracks: tracks,
            TotalCount: null,  // Not available from page
            NextPageUrl: string.IsNullOrEmpty(nextPageUrl) ? null : nextPageUrl,
            IsInfinite: false
        );
    }

    /// <summary>
    /// Batch fetches metadata for tracks. Uses cache hierarchy:
    /// 1. Hot cache (in-memory)
    /// 2. SQLite cache
    /// 3. API (batched, 500 per request)
    /// </summary>
    public async Task<IReadOnlyList<QueueTrack>> EnrichTracksAsync(
        IList<(string Uri, string? Uid)> trackInfos,
        CancellationToken ct)
    {
        // Check cache first
        var cachedTracks = await _cacheService.GetTracksAsync(
            trackInfos.Select(t => t.Uri),
            ct);

        var uncachedUris = trackInfos
            .Where(t => !cachedTracks.ContainsKey(t.Uri))
            .Select(t => t.Uri)
            .ToList();

        _logger?.LogDebug("Enriching tracks: {Total} total, {Cached} cached, {Uncached} to fetch",
            trackInfos.Count, cachedTracks.Count, uncachedUris.Count);

        // Batch fetch uncached tracks
        if (uncachedUris.Count > 0)
        {
            for (int i = 0; i < uncachedUris.Count; i += BatchSize)
            {
                var batch = uncachedUris.Skip(i).Take(BatchSize).ToList();
                var requests = batch.Select(uri =>
                    (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackV4 }));

                try
                {
                    // ExtendedMetadataClient handles caching automatically
                    await _metadataClient.GetBatchedExtensionsAsync(requests, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to fetch metadata for batch of {Count} tracks (offset {Offset})",
                        batch.Count, i);
                    // Continue with partial metadata
                }
            }

            // Re-fetch from cache (now populated)
            var newCached = await _cacheService.GetTracksAsync(uncachedUris, ct);
            foreach (var (uri, entry) in newCached)
            {
                cachedTracks[uri] = entry;
            }
        }

        // Build QueueTrack list preserving order
        var result = new List<QueueTrack>();
        var missingCount = 0;

        foreach (var (uri, uid) in trackInfos)
        {
            if (cachedTracks.TryGetValue(uri, out var cached))
            {
                result.Add(new QueueTrack(
                    Uri: uri,
                    Uid: uid,
                    Title: cached.Title,
                    Artist: cached.Artist,
                    Album: cached.Album,
                    DurationMs: cached.DurationMs,
                    AddedAt: null,  // Not available from track metadata
                    IsPlayable: cached.IsPlayable,
                    IsExplicit: cached.IsExplicit
                ));
            }
            else
            {
                // Track not found - keep in queue but mark as potentially unplayable
                missingCount++;
                result.Add(new QueueTrack(
                    Uri: uri,
                    Uid: uid,
                    Title: null,
                    Artist: null,
                    IsPlayable: false
                ));
            }
        }

        if (missingCount > 0)
        {
            _logger?.LogWarning("Failed to enrich metadata for {Count} tracks", missingCount);
        }

        return result;
    }

    private static int? GetTotalFromMetadata(Context context)
    {
        // Try to get total from context metadata
        if (context.Metadata.TryGetValue("track_count", out var trackCountStr) &&
            int.TryParse(trackCountStr, out var trackCount))
        {
            return trackCount;
        }

        if (context.Metadata.TryGetValue("length", out var lengthStr) &&
            int.TryParse(lengthStr, out var length))
        {
            return length;
        }

        return null;
    }

    private static bool IsInfiniteContext(string uri)
    {
        return uri.Contains(":station:") ||
               uri.Contains(":radio:") ||
               uri.Contains(":autoplay:");
    }

    /// <summary>
    /// Gets the appropriate TTL for a context based on its type.
    /// </summary>
    private static TimeSpan GetContextTtl(string uri)
    {
        if (uri.Contains(":station:") || uri.Contains(":radio:"))
            return StationTtl;
        if (uri.Contains(":album:"))
            return AlbumTtl;
        return PlaylistTtl;
    }

    /// <summary>
    /// Invalidates a cached context (e.g., when playlist is modified).
    /// </summary>
    /// <param name="contextUri">Context URI to invalidate.</param>
    public void InvalidateContext(string contextUri)
    {
        _contextCache.Remove(contextUri);
        _logger?.LogDebug("Context cache invalidated: {ContextUri}", contextUri);
    }
}

/// <summary>
/// Result of loading a context.
/// </summary>
public sealed record ContextLoadResult(
    IReadOnlyList<QueueTrack> Tracks,
    int? TotalCount,
    string? NextPageUrl,
    bool IsInfinite
)
{
    /// <summary>
    /// Gets whether there are more tracks to load.
    /// </summary>
    public bool HasMoreTracks => !string.IsNullOrEmpty(NextPageUrl) ||
                                  (TotalCount.HasValue && Tracks.Count < TotalCount.Value);
}
