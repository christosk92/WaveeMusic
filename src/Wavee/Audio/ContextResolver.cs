using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wavee.Audio.Queue;
using Wavee.Connect.Commands;
using Wavee.Core.Http;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.Context;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Audio;

/// <summary>
/// Resolves Spotify context URIs (playlists, albums, etc.) to flat track lists.
/// Handles pagination, retries, caching, metadata enrichment, and context merging.
/// </summary>
/// <remarks>
/// Inspired by librespot's ContextResolver pattern:
/// - All pages merged into one flat list (transparent pagination)
/// - Unavailable context tracking with cooldown
/// - Retry with exponential backoff
/// - Context merging from play command page tracks
/// - Static FindTrackIndex for queue-independent track lookup
/// </remarks>
public sealed class ContextResolver
{
    private readonly SpClient _spClient;
    private readonly IExtendedMetadataClient _metadataClient;
    private readonly ICacheService _cacheService;
    private readonly IHotCache<ContextCacheEntry> _contextCache;
    private readonly ILogger? _logger;

    // Retry configuration
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    // Unavailable context tracking (like librespot: 1 hour cooldown)
    private static readonly TimeSpan UnavailableCooldown = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unavailableContexts = new();

    // TTL values for different context types
    private static readonly TimeSpan PlaylistTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlbumTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan StationTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CollectionTtl = TimeSpan.FromMinutes(5);

    // API limits
    private const int BatchSize = 500;
    private const int MaxPagesPerLoad = 10; // Safety limit for page loading

    public ContextResolver(
        SpClient spClient,
        IExtendedMetadataClient metadataClient,
        ICacheService cacheService,
        IHotCache<ContextCacheEntry> contextCache,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(spClient);
        ArgumentNullException.ThrowIfNull(metadataClient);
        ArgumentNullException.ThrowIfNull(cacheService);
        ArgumentNullException.ThrowIfNull(contextCache);

        _spClient = spClient;
        _metadataClient = metadataClient;
        _cacheService = cacheService;
        _contextCache = contextCache;
        _logger = logger;
    }

    // ================================================================
    // PUBLIC API
    // ================================================================

    /// <summary>
    /// Loads a context and returns a flat track list with metadata.
    /// Merges all available pages into one list. Retries on transient failures.
    /// </summary>
    public async Task<ContextLoadResult> LoadContextAsync(
        string contextUri,
        int? maxTracks = null,
        bool enrichMetadata = true,
        CancellationToken ct = default)
    {
        _logger?.LogDebug("Loading context: {ContextUri}, maxTracks={MaxTracks}, enrich={Enrich}",
            contextUri, maxTracks, enrichMetadata);

        // Check unavailable cooldown
        if (_unavailableContexts.TryGetValue(contextUri, out var cooldownUntil) &&
            DateTimeOffset.UtcNow < cooldownUntil)
        {
            _logger?.LogWarning("Context {ContextUri} is unavailable (cooldown until {CooldownUntil})",
                contextUri, cooldownUntil);
            throw new ContextUnavailableException(contextUri, cooldownUntil);
        }

        // Check context cache
        var cached = _contextCache.Get(contextUri);
        if (cached is { IsValid: true })
        {
            _logger?.LogDebug("Context cache hit: {ContextUri}, {TrackCount} tracks",
                contextUri, cached.Tracks.Count);
            return await BuildResultFromCache(cached, maxTracks, enrichMetadata, ct);
        }

        // Resolve from API with retry
        _logger?.LogDebug("Context cache miss, fetching from API: {ContextUri}", contextUri);
        Context context;
        try
        {
            context = await ResolveWithRetryAsync(contextUri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mark as unavailable on persistent failure
            var until = DateTimeOffset.UtcNow + UnavailableCooldown;
            _unavailableContexts[contextUri] = until;
            _logger?.LogError(ex, "Context {ContextUri} marked unavailable until {Until}", contextUri, until);
            throw new ContextUnavailableException(contextUri, until, ex);
        }

        // Extract all tracks from pages (eager load for bounded contexts)
        var trackInfos = await LoadTracksFromPagesAsync(context, contextUri, maxTracks, ct);
        var nextPageUrl = FindNextPageUrl(context);
        var totalCount = GetTotalFromMetadata(context);
        var isInfinite = IsInfiniteContext(contextUri);
        var sortingCriteria = ExtractSortingCriteria(context);
        var contextOwner = ExtractContextOwner(context);
        var contextMetadata = SnapshotContextMetadata(context);

        _logger?.LogDebug("Context resolved: {TrackCount} tracks, nextPage={HasNext}, sort={Sort}",
            trackInfos.Count, nextPageUrl != null, sortingCriteria);

        // Cache the raw result (including rich metadata so cache hits don't
        // lose the context-level decorations PlayerState needs).
        CacheContext(contextUri, trackInfos, nextPageUrl, totalCount, isInfinite, contextMetadata, context.Pages.Count);

        // Enrich with metadata
        var tracks = enrichMetadata && trackInfos.Count > 0
            ? await EnrichTracksAsync(trackInfos, ct)
            : trackInfos.Select(t => new QueueTrack(t.Uri, t.Uid) { Metadata = t.Metadata }).ToList();

        return new ContextLoadResult(
            Tracks: tracks,
            TotalCount: totalCount,
            NextPageUrl: nextPageUrl,
            IsInfinite: isInfinite,
            SortingCriteria: sortingCriteria,
            ContextOwner: contextOwner,
            ContextMetadata: contextMetadata,
            PageCount: context.Pages.Count);
    }

    /// <summary>
    /// Loads the next page of tracks for lazy pagination.
    /// </summary>
    public async Task<ContextLoadResult> LoadNextPageAsync(
        string pageUrl,
        bool enrichMetadata = true,
        CancellationToken ct = default)
    {
        _logger?.LogDebug("Loading next page: {PageUrl}", pageUrl);

        var page = await _spClient.GetNextPageAsync(pageUrl, ct);

        var trackInfos = page.Tracks
            .Where(t => !string.IsNullOrEmpty(t.Uri))
            .Select(t => new CachedContextTrack(t.Uri, t.Uid, SnapshotTrackMetadata(t)))
            .ToList();

        var tracks = enrichMetadata && trackInfos.Count > 0
            ? await EnrichTracksAsync(trackInfos, ct)
            : trackInfos.Select(t => new QueueTrack(t.Uri, t.Uid) { Metadata = t.Metadata }).ToList();

        var nextPageUrl = !string.IsNullOrEmpty(page.NextPageUrl) ? page.NextPageUrl : null;

        return new ContextLoadResult(
            Tracks: tracks,
            TotalCount: null,
            NextPageUrl: nextPageUrl,
            IsInfinite: false,
            SortingCriteria: null,
            ContextOwner: null,
            PageCount: 1);
    }

    /// <summary>
    /// Loads autoplay recommendations for a context that has finished playing.
    /// All returned tracks are tagged with Provider = "autoplay".
    /// </summary>
    /// <param name="contextUri">Original context URI (e.g., "spotify:album:xxx").</param>
    /// <param name="recentTrackUris">Recently played track URIs for recommendation seeding.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Autoplay tracks with next_page_url for further pagination.</returns>
    public async Task<ContextLoadResult> LoadAutoplayAsync(
        string contextUri,
        IReadOnlyList<string> recentTrackUris,
        CancellationToken ct = default)
    {
        _logger?.LogDebug("Loading autoplay for context: {ContextUri}", contextUri);

        var context = await _spClient.ResolveAutoplayAsync(contextUri, recentTrackUris, ct);

        var trackInfos = new List<CachedContextTrack>();
        foreach (var page in context.Pages)
        {
            foreach (var track in page.Tracks)
            {
                if (!string.IsNullOrEmpty(track.Uri))
                    trackInfos.Add(new CachedContextTrack(track.Uri, track.Uid, SnapshotTrackMetadata(track)));
            }
        }

        var nextPageUrl = FindNextPageUrl(context);

        // Enrich with metadata and tag all as autoplay
        IReadOnlyList<QueueTrack> tracks = trackInfos.Count > 0
            ? await EnrichTracksAsync(trackInfos, ct)
            : Array.Empty<QueueTrack>();

        // Tag all tracks as autoplay provider
        var autoplayTracks = tracks
            .Select(t => t with { Provider = "autoplay", IsUserQueued = false })
            .ToList();

        _logger?.LogDebug("Autoplay loaded: {TrackCount} tracks, nextPage={HasNext}",
            autoplayTracks.Count, nextPageUrl != null);

        return new ContextLoadResult(
            Tracks: autoplayTracks,
            TotalCount: null,
            NextPageUrl: nextPageUrl,
            IsInfinite: true,
            SortingCriteria: null,
            ContextOwner: null,
            ContextMetadata: SnapshotContextMetadata(context),
            PageCount: context.Pages.Count,
            // Autoplay response URI is a station URI ("spotify:station:artist:xxx")
            // distinct from the REQUEST URI. The orchestrator uses this to switch
            // the queue's context URI over when autoplay takes over.
            ResolvedContextUri: string.IsNullOrEmpty(context.Uri) ? null : context.Uri);
    }

    /// <summary>
    /// Loads autoplay seeded from a single track via the radio-apollo endpoint —
    /// the path Spotify desktop uses when continuing from a no-context play
    /// (search-result single track, click-row, etc.). Returns the same
    /// <see cref="ContextLoadResult"/> shape as <see cref="LoadAutoplayAsync"/>
    /// so the orchestrator's queue-append pipeline doesn't care which one ran.
    /// </summary>
    public async Task<ContextLoadResult> LoadRadioApolloAutoplayAsync(
        string seedTrackUri,
        IReadOnlyList<string> recentTrackUris,
        CancellationToken ct = default)
    {
        var seedId = StripTrackUriPrefix(seedTrackUri);
        var prevIds = recentTrackUris
            .Select(StripTrackUriPrefix)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        _logger?.LogDebug("Loading radio-apollo autoplay for seed: {SeedUri}", seedTrackUri);

        var resp = await _spClient.GetRadioApolloAutoplayAsync(seedId, prevIds, cancellationToken: ct);

        var trackInfos = resp.Tracks
            .Where(t => !string.IsNullOrEmpty(t.Uri))
            .Select(t => new CachedContextTrack(
                t.Uri,
                t.Uid,
                t.DecisionId is null
                    ? null
                    : new Dictionary<string, string> { ["decision_id"] = t.DecisionId }))
            .ToList();

        IReadOnlyList<QueueTrack> tracks = trackInfos.Count > 0
            ? await EnrichTracksAsync(trackInfos, ct)
            : Array.Empty<QueueTrack>();

        var autoplayTracks = tracks
            .Select(t => t with { Provider = "autoplay", IsUserQueued = false })
            .ToList();

        _logger?.LogDebug("Radio-apollo autoplay loaded: {TrackCount} tracks, nextPage={HasNext}",
            autoplayTracks.Count, !string.IsNullOrEmpty(resp.NextPageUrl));

        return new ContextLoadResult(
            Tracks: autoplayTracks,
            TotalCount: null,
            // hm:// pagination cursor — orchestrator's existing
            // LoadMoreTracksAsync handles hm:// today, so this just works
            // when the autoplay queue runs out.
            NextPageUrl: resp.NextPageUrl,
            IsInfinite: true,
            SortingCriteria: null,
            ContextOwner: null,
            ContextMetadata: null,
            PageCount: 1,
            ResolvedContextUri: $"spotify:station:track:{seedId}");
    }

    private static string StripTrackUriPrefix(string uri) =>
        uri.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? uri["spotify:track:".Length..]
            : uri;

    /// <summary>
    /// Merges page tracks from a play command into existing tracks.
    /// Updates UIDs and metadata for matching URIs without reordering.
    /// Like librespot's merge_context.
    /// </summary>
    public static void MergePageTracks(
        IList<(string Uri, string? Uid)> existingTracks,
        IReadOnlyList<PageTrack>? commandTracks)
    {
        if (commandTracks == null || commandTracks.Count == 0) return;

        // Build URI→index map for fast lookup
        var uriToIndex = new Dictionary<string, int>(existingTracks.Count);
        for (int i = 0; i < existingTracks.Count; i++)
            uriToIndex[existingTracks[i].Uri] = i;

        foreach (var cmdTrack in commandTracks)
        {
            if (uriToIndex.TryGetValue(cmdTrack.Uri, out var idx) &&
                !string.IsNullOrEmpty(cmdTrack.Uid))
            {
                var existing = existingTracks[idx];
                existingTracks[idx] = (existing.Uri, cmdTrack.Uid);
            }
        }
    }

    /// <summary>
    /// Finds the target track index in a track list.
    /// Priority: UID > URI > fallback index > 0.
    /// Static so it can be called without a resolver instance.
    /// </summary>
    public static int FindTrackIndex(
        IReadOnlyList<QueueTrack> tracks,
        string? trackUri,
        string? trackUid,
        int? fallbackIndex)
    {
        // Priority 1: UID (most specific — handles sorted/filtered playlists)
        if (!string.IsNullOrEmpty(trackUid))
        {
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].Uid == trackUid) return i;
        }

        // Priority 2: URI
        if (!string.IsNullOrEmpty(trackUri))
        {
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].Uri == trackUri) return i;
        }

        // Priority 3: Explicit index from command
        if (fallbackIndex.HasValue && fallbackIndex.Value < tracks.Count)
            return fallbackIndex.Value;

        return 0;
    }

    /// <summary>
    /// Invalidates a cached context (e.g., when playlist is modified).
    /// </summary>
    public void InvalidateContext(string contextUri)
    {
        _contextCache.Remove(contextUri);
        _unavailableContexts.TryRemove(contextUri, out _);
        _logger?.LogDebug("Context invalidated: {ContextUri}", contextUri);
    }

    // ================================================================
    // METADATA ENRICHMENT
    // ================================================================

    /// <summary>
    /// Batch fetches metadata for tracks. Uses 3-tier cache:
    /// hot cache → SQLite → API (batched, 500 per request).
    /// Per-track recommender metadata (from <c>/context-resolve/v1/</c>) is
    /// carried through verbatim on the resulting QueueTrack so
    /// <c>ProvidedTrack.metadata</c> survives the enrichment step.
    /// </summary>
    public async Task<IReadOnlyList<QueueTrack>> EnrichTracksAsync(
        IList<CachedContextTrack> trackInfos,
        CancellationToken ct)
    {
        var cachedTracks = await _cacheService.GetTracksAsync(
            trackInfos.Select(t => t.Uri), ct);

        var uncachedUris = trackInfos
            .Where(t => !cachedTracks.ContainsKey(t.Uri))
            .Select(t => t.Uri)
            .ToList();

        _logger?.LogDebug("Enriching tracks: {Total} total, {Cached} cached, {Uncached} to fetch",
            trackInfos.Count, cachedTracks.Count, uncachedUris.Count);

        // Batch fetch uncached tracks with per-batch error handling
        if (uncachedUris.Count > 0)
        {
            for (int i = 0; i < uncachedUris.Count; i += BatchSize)
            {
                var batch = uncachedUris.Skip(i).Take(BatchSize).ToList();
                var requests = batch.Select(uri =>
                    (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackV4 }));

                try
                {
                    await _metadataClient.GetBatchedExtensionsAsync(requests, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex,
                        "Failed to fetch metadata for batch of {Count} tracks (offset {Offset})",
                        batch.Count, i);
                }
            }

            // Re-fetch from cache (now populated by metadata client)
            var newCached = await _cacheService.GetTracksAsync(uncachedUris, ct);
            foreach (var (uri, entry) in newCached)
                cachedTracks[uri] = entry;
        }

        // Build QueueTrack list preserving original order
        var result = new List<QueueTrack>(trackInfos.Count);
        var missingCount = 0;

        foreach (var info in trackInfos)
        {
            if (cachedTracks.TryGetValue(info.Uri, out var cached))
            {
                result.Add(new QueueTrack(
                    Uri: info.Uri, Uid: info.Uid,
                    Title: cached.Title, Artist: cached.Artist, Album: cached.Album,
                    DurationMs: cached.DurationMs, AddedAt: null,
                    IsPlayable: cached.IsPlayable, IsExplicit: cached.IsExplicit)
                {
                    Metadata = info.Metadata
                });
            }
            else
            {
                missingCount++;
                result.Add(new QueueTrack(info.Uri, info.Uid, Title: null, Artist: null, IsPlayable: false)
                {
                    Metadata = info.Metadata
                });
            }
        }

        if (missingCount > 0)
            _logger?.LogWarning("Failed to enrich metadata for {Count} tracks", missingCount);

        return result;
    }

    // ================================================================
    // INTERNAL: RETRY & PAGE LOADING
    // ================================================================

    /// <summary>
    /// Resolves a context URI with exponential backoff retry.
    /// </summary>
    private async Task<Context> ResolveWithRetryAsync(string contextUri, CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                    _logger?.LogDebug("Retry {Attempt}/{Max} for {ContextUri} after {Delay}ms",
                        attempt, MaxRetries, contextUri, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }

                return await _spClient.ResolveContextAsync(contextUri, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastException = ex;
                _logger?.LogWarning(ex, "Context resolve attempt {Attempt} failed for {ContextUri}",
                    attempt + 1, contextUri);
            }
        }

        throw new InvalidOperationException(
            $"Failed to resolve context {contextUri} after {MaxRetries + 1} attempts", lastException);
    }

    /// <summary>
    /// Extracts all tracks from context pages. Loads additional pages eagerly
    /// for bounded contexts, respects maxTracks limit. Captures the server's
    /// per-track metadata dict so it can round-trip through the cache and into
    /// <c>PlayerState.track.metadata</c> on publish.
    /// </summary>
    private async Task<List<CachedContextTrack>> LoadTracksFromPagesAsync(
        Context context, string contextUri, int? maxTracks, CancellationToken ct)
    {
        var trackInfos = new List<CachedContextTrack>();
        var isInfinite = IsInfiniteContext(contextUri);

        // Extract tracks from initial response pages
        foreach (var page in context.Pages)
        {
            foreach (var track in page.Tracks)
            {
                if (string.IsNullOrEmpty(track.Uri)) continue;
                trackInfos.Add(new CachedContextTrack(track.Uri, track.Uid, SnapshotTrackMetadata(track)));
                if (maxTracks.HasValue && trackInfos.Count >= maxTracks.Value) break;
            }
            if (maxTracks.HasValue && trackInfos.Count >= maxTracks.Value) break;
        }

        // For bounded contexts, eagerly load remaining pages if needed
        if (!isInfinite && (!maxTracks.HasValue || trackInfos.Count < maxTracks.Value))
        {
            var nextPageUrl = FindNextPageUrl(context);
            var pagesLoaded = 0;

            while (nextPageUrl != null && pagesLoaded < MaxPagesPerLoad)
            {
                if (maxTracks.HasValue && trackInfos.Count >= maxTracks.Value) break;

                try
                {
                    var page = await _spClient.GetNextPageAsync(nextPageUrl, ct);
                    foreach (var track in page.Tracks)
                    {
                        if (string.IsNullOrEmpty(track.Uri)) continue;
                        trackInfos.Add(new CachedContextTrack(track.Uri, track.Uid, SnapshotTrackMetadata(track)));
                        if (maxTracks.HasValue && trackInfos.Count >= maxTracks.Value) break;
                    }

                    nextPageUrl = !string.IsNullOrEmpty(page.NextPageUrl) ? page.NextPageUrl : null;
                    pagesLoaded++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Failed to load page {PageNum} for {ContextUri}",
                        pagesLoaded + 1, contextUri);
                    break; // Continue with partial data
                }
            }

            if (pagesLoaded > 0)
                _logger?.LogDebug("Loaded {Pages} additional pages for {ContextUri}, total {Tracks} tracks",
                    pagesLoaded, contextUri, trackInfos.Count);
        }

        return trackInfos;
    }

    /// <summary>
    /// Copy a ContextTrack's MapField&lt;string,string&gt; metadata into an
    /// immutable snapshot. Returns null for empty maps so downstream
    /// serializers can skip them without a null-check on every track.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? SnapshotTrackMetadata(ContextTrack track)
    {
        if (track.Metadata is null || track.Metadata.Count == 0) return null;
        var snapshot = new Dictionary<string, string>(track.Metadata.Count, StringComparer.Ordinal);
        foreach (var kv in track.Metadata) snapshot[kv.Key] = kv.Value ?? string.Empty;
        return snapshot;
    }

    /// <summary>
    /// Copy the top-level context.metadata MapField into an immutable
    /// snapshot. Null for empty so consumers can early-out.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? SnapshotContextMetadata(Context context)
    {
        if (context.Metadata is null || context.Metadata.Count == 0) return null;
        var snapshot = new Dictionary<string, string>(context.Metadata.Count, StringComparer.Ordinal);
        foreach (var kv in context.Metadata) snapshot[kv.Key] = kv.Value ?? string.Empty;
        return snapshot;
    }

    // ================================================================
    // INTERNAL: CACHING
    // ================================================================

    private void CacheContext(
        string contextUri, List<CachedContextTrack> trackInfos,
        string? nextPageUrl, int? totalCount, bool isInfinite,
        IReadOnlyDictionary<string, string>? contextMetadata,
        int pageCount)
    {
        var ttl = GetContextTtl(contextUri);

        _contextCache.Set(contextUri, new ContextCacheEntry
        {
            Uri = contextUri,
            ExpiresAt = DateTimeOffset.UtcNow + ttl,
            Tracks = trackInfos,
            ContextMetadata = contextMetadata,
            NextPageUrl = nextPageUrl,
            TotalCount = totalCount,
            IsInfinite = isInfinite,
            PageCount = pageCount
        });

        _logger?.LogDebug("Context cached: {ContextUri}, TTL={TtlMinutes}m", contextUri, ttl.TotalMinutes);
    }

    private async Task<ContextLoadResult> BuildResultFromCache(
        ContextCacheEntry cached, int? maxTracks, bool enrichMetadata, CancellationToken ct)
    {
        var trackInfos = cached.Tracks.ToList();
        if (maxTracks.HasValue && trackInfos.Count > maxTracks.Value)
            trackInfos = trackInfos.Take(maxTracks.Value).ToList();

        var tracks = enrichMetadata && trackInfos.Count > 0
            ? await EnrichTracksAsync(trackInfos, ct)
            : trackInfos.Select(t => new QueueTrack(t.Uri, t.Uid) { Metadata = t.Metadata }).ToList();

        return new ContextLoadResult(
            Tracks: tracks,
            TotalCount: cached.TotalCount,
            NextPageUrl: cached.NextPageUrl,
            IsInfinite: cached.IsInfinite,
            SortingCriteria: null, // Not cached (server authority)
            ContextOwner: null,
            ContextMetadata: cached.ContextMetadata,
            PageCount: cached.PageCount);
    }

    // ================================================================
    // INTERNAL: METADATA EXTRACTION
    // ================================================================

    private static string? FindNextPageUrl(Context context)
    {
        foreach (var page in context.Pages)
        {
            if (!string.IsNullOrEmpty(page.NextPageUrl))
                return page.NextPageUrl;
            if (!string.IsNullOrEmpty(page.PageUrl) && page.Tracks.Count > 0)
                return page.PageUrl;
        }
        return null;
    }

    private static int? GetTotalFromMetadata(Context context)
    {
        if (context.Metadata.TryGetValue("track_count", out var tc) && int.TryParse(tc, out var count))
            return count;
        if (context.Metadata.TryGetValue("length", out var len) && int.TryParse(len, out var length))
            return length;
        return null;
    }

    private static string? ExtractSortingCriteria(Context context)
    {
        // Server returns tracks pre-sorted — we preserve the order and store the criteria
        // for state publishing (so Spotify UI knows the sort order)
        context.Metadata.TryGetValue("sorting.criteria", out var criteria);
        return string.IsNullOrEmpty(criteria) ? null : criteria;
    }

    private static string? ExtractContextOwner(Context context)
    {
        context.Metadata.TryGetValue("context_owner", out var owner);
        return string.IsNullOrEmpty(owner) ? null : owner;
    }

    private static bool IsInfiniteContext(string uri) =>
        uri.Contains(":station:") || uri.Contains(":radio:") || uri.Contains(":autoplay:");

    private static TimeSpan GetContextTtl(string uri)
    {
        if (uri.Contains(":station:") || uri.Contains(":radio:")) return StationTtl;
        if (uri.Contains(":album:")) return AlbumTtl;
        if (uri.Contains(":collection")) return CollectionTtl;
        return PlaylistTtl;
    }
}

/// <summary>
/// Thrown when a context cannot be resolved and is in cooldown.
/// </summary>
public sealed class ContextUnavailableException(
    string contextUri,
    DateTimeOffset cooldownUntil,
    Exception? inner = null)
    : Exception($"Context {contextUri} is unavailable until {cooldownUntil}", inner)
{
    public string ContextUri { get; } = contextUri;
    public DateTimeOffset CooldownUntil { get; } = cooldownUntil;
}

/// <summary>
/// Result of loading a context.
/// </summary>
public sealed record ContextLoadResult(
    IReadOnlyList<QueueTrack> Tracks,
    int? TotalCount,
    string? NextPageUrl,
    bool IsInfinite,
    string? SortingCriteria,
    string? ContextOwner,
    IReadOnlyDictionary<string, string>? ContextMetadata = null,
    int PageCount = 1,
    // Server-assigned URI for the resolved context. For autoplay this is the
    // new station URI (e.g. "spotify:station:artist:xxx") which differs from
    // the REQUEST URI. Null for regular contexts where request and response
    // URIs match.
    string? ResolvedContextUri = null
)
{
    public bool HasMoreTracks => !string.IsNullOrEmpty(NextPageUrl) ||
                                  (TotalCount.HasValue && Tracks.Count < TotalCount.Value);
}
