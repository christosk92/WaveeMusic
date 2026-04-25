using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;
using Wavee.Core.Storage.Entities;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Core.Playlists;

public sealed class PlaylistCacheService : IPlaylistCacheService, IDisposable
{
    private static readonly string[] RootlistDecorations =
        ["revision", "attributes", "length", "owner", "capabilities", "status_code"];
    private static readonly string[] PlaylistDecorations =
        ["revision", "attributes", "length", "owner", "capabilities"];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan DealerDebounce = TimeSpan.FromMilliseconds(250);

    // Settle window for dealer-driven refreshes per playlist URI.
    // Spotify's curation pipeline pushes Mercury notifications for editorial Mix
    // playlists faster than the diff endpoint can keep up — the diff returns 509,
    // we fall back to a full fetch, the full fetch returns a freshly-bumped
    // revision, the cache emits Updated, and Spotify echoes another Mercury
    // notification a moment later, restarting the cycle. The throttle in
    // EnsureDealerSubscription handles burst coalescing in a 250 ms window;
    // this longer window suppresses the echo loop without dropping legitimate
    // user-driven edits (which arrive many seconds apart).
    private static readonly TimeSpan DealerSettleWindow = TimeSpan.FromSeconds(5);
    // Negative cache window: once SpClient has told us a URI is unfetchable
    // (404 / 403 / 401), remember that for NegativeCacheTtl so repeat callers
    // (sidebar mosaic + home enrichment + wherever) don't each fire their own
    // round-trip + retry + cancellation cascade against a dead resource.
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

    private readonly ISession _session;
    private readonly IMetadataDatabase _database;
    private readonly HotCache<CachedPlaylist> _hotCache;
    private readonly Subject<PlaylistChangeEvent> _changes = new();
    // Lazy-wrapped so GetOrAdd+factory can be invoked twice under contention
    // without firing the factory's work twice: only the Lazy that wins the
    // dictionary slot ever has .Value accessed. The losing Lazy is discarded
    // before its factory runs. This is the correct dedup primitive for
    // expensive factories that would otherwise race (the HTTP fetch + its
    // "Fetching playlist:" log line was running twice per URI at startup).
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedPlaylist>>> _playlistRefreshes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingAccessTouches =
        new(StringComparer.Ordinal);
    // Per-URI timestamp of the most recent dealer-driven refresh; consulted by
    // URIs of cache rows whose persisted JSON is at an older schema version than
    // the current build. Populated at warmup; entries are removed the first time
    // GetPlaylistAsync (or related) returns the playlist, after a forced fresh
    // fetch lands and re-persists the row at the current version. Hydrating
    // stale rows into the hot cache anyway (rather than skipping them) keeps
    // the sidebar populated on first launch — without this set the page-level
    // schema check in the disk path never fired because hot-cache hits served
    // first.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _staleSchemaUris =
        new(StringComparer.Ordinal);

    // HandleDealerRefreshAsync to enforce DealerSettleWindow.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDealerRefreshAt =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, NegativeCacheEntry> _negativeCache =
        new(StringComparer.Ordinal);

    private readonly record struct NegativeCacheEntry(
        SpClientFailureReason Reason,
        string Message,
        DateTimeOffset ExpiresAt);
    private readonly ILogger? _logger;
    private readonly object _dealerSubscriptionGate = new();
    private readonly object _rootlistGate = new();

    private RootlistSnapshot? _hotRootlist;
    private Task<RootlistSnapshot>? _rootlistRefreshTask;
    private LibraryChangeManager? _libraryChangeManager;
    private IDisposable? _dealerSubscription;
    // Parallel subscription that routes Mercury pushes carrying the full diff
    // payload directly to PlaylistDiffApplier — see EnsureDealerSubscription.
    private IDisposable? _directApplySubscription;
    private bool _disposed;

    public PlaylistCacheService(
        ISession session,
        IMetadataDatabase database,
        ILogger<PlaylistCacheService>? logger = null)
    {
        _session = session;
        _database = database;
        _logger = logger;
        _hotCache = new HotCache<CachedPlaylist>(64);

        _ = WarmupAsync();
    }

    public IObservable<PlaylistChangeEvent> Changes => _changes.AsObservable();

    public async Task<RootlistSnapshot> GetRootlistAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        EnsureDealerSubscription();
        var rootlistUri = GetRootlistUri();

        if (!forceRefresh && _hotRootlist != null)
        {
            if (IsStale(_hotRootlist.FetchedAt))
                _ = RefreshRootlistSafeAsync();

            return _hotRootlist;
        }

        if (!forceRefresh)
        {
            var persisted = await _database.GetRootlistCacheEntryAsync(rootlistUri, touchAccess: false, ct);
            if (persisted != null)
            {
                var snapshot = DeserializeRootlistSnapshot(persisted);
                if (snapshot != null)
                {
                    _hotRootlist = snapshot;
                    ScheduleRootlistAccessTouch(rootlistUri);
                    if (IsStale(snapshot.FetchedAt))
                        _ = RefreshRootlistSafeAsync();

                    return snapshot;
                }
            }
        }

        return await GetOrCreateRootlistRefreshTask(forceRefresh, emitChange: false).WaitAsync(ct);
    }

    public async Task<RootlistTree> GetRootlistTreeAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var snapshot = await GetRootlistAsync(forceRefresh, ct);
        return RootlistTreeBuilder.Build(snapshot.Items, _logger);
    }

    public async Task<CachedPlaylist> GetPlaylistAsync(
        string playlistUri,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        EnsureDealerSubscription();
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        // Negative-cache short-circuit: if the last network attempt for this
        // URI returned NotFound / Unauthorized / Forbidden, throw straight
        // away without hitting the network, the dealer, or the SQLite tier.
        // forceRefresh skips the guard so the caller can retry deliberately.
        if (!forceRefresh && TryGetNegativeCache(playlistUri, out var cached))
            throw new SpClientException(cached.Reason, cached.Message);

        if (!forceRefresh)
        {
            var hot = _hotCache.Get(playlistUri);
            if (hot != null)
            {
                // Stale-schema rows hydrated by warmup should refetch on first
                // user-facing access. Drop the marker BEFORE awaiting so a
                // concurrent caller doesn't queue a second refresh.
                if (_staleSchemaUris.TryRemove(playlistUri, out _))
                {
                    _logger?.LogInformation(
                        "[caps] Hot-cache stale-schema for '{Uri}'; forcing network refresh", playlistUri);
                    return await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: true).WaitAsync(ct);
                }

                _logger?.LogInformation(
                    "[caps] GetPlaylistAsync hot hit '{Uri}': BasePerm={Base} Caps=[EditItems={EI},EditMeta={EM},Admin={AD}]",
                    playlistUri, hot.BasePermission,
                    hot.Capabilities.CanEditItems, hot.Capabilities.CanEditMetadata, hot.Capabilities.CanAdministratePermissions);
            }
            if (hot != null)
            {
                if (!hot.HasContentsSnapshot)
                    return await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: false).WaitAsync(ct);

                if (ShouldRefresh(hot) || ShouldFreshnessCheck(playlistUri))
                    _ = RefreshPlaylistSafeAsync(playlistUri);

                return hot;
            }

            var persisted = await _database.GetPlaylistCacheEntryAsync(playlistUri, touchAccess: false, ct);
            if (persisted != null)
            {
                _logger?.LogInformation(
                    "[caps] GetPlaylistAsync disk hit '{Uri}' (rowSchemaV{V} vs currentV{C})",
                    playlistUri, persisted.CacheSchemaVersion, CurrentCacheSchemaVersion);
                // Stale schema: the row was written before this build's version
                // bump, so its JSON blobs likely deserialize with default values
                // for any new fields. Skip the cached value entirely and force a
                // fresh fetch — preserves correctness over the slightly slower
                // first load post-upgrade.
                if (persisted.CacheSchemaVersion < CurrentCacheSchemaVersion)
                {
                    _logger?.LogInformation(
                        "[caps] Cache schema stale for '{Id}' (row v{Row}, current v{Current}); forcing network refresh",
                        playlistUri, persisted.CacheSchemaVersion, CurrentCacheSchemaVersion);
                    return await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: false).WaitAsync(ct);
                }

                var cachedPersisted = DeserializePlaylist(persisted);
                _hotCache.Set(playlistUri, cachedPersisted);
                SchedulePlaylistAccessTouch(playlistUri);

                if (!cachedPersisted.HasContentsSnapshot)
                    return await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: false).WaitAsync(ct);

                if (ShouldRefresh(cachedPersisted) || ShouldFreshnessCheck(playlistUri))
                    _ = RefreshPlaylistSafeAsync(playlistUri);

                return cachedPersisted;
            }
        }

        return await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: false).WaitAsync(ct);
    }

    private bool TryGetNegativeCache(string playlistUri, out NegativeCacheEntry entry)
    {
        if (_negativeCache.TryGetValue(playlistUri, out entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
                return true;

            // Expired — drop it so the next call tries the network again.
            _negativeCache.TryRemove(playlistUri, out _);
        }

        entry = default;
        return false;
    }

    private void RecordNegativeCache(string playlistUri, SpClientException ex)
    {
        // Only cache deterministic "this URI won't resolve" failures. Server
        // errors, rate limiting, and transport failures stay un-cached so
        // the next request gets a fresh attempt.
        if (ex.Reason is not (SpClientFailureReason.NotFound
                              or SpClientFailureReason.Unauthorized))
            return;

        _negativeCache[playlistUri] = new NegativeCacheEntry(
            ex.Reason,
            ex.Message,
            DateTimeOffset.UtcNow + NegativeCacheTtl);
    }

    public async Task<CachedPlaylist> ApplyFreshContentAsync(
        string playlistUri,
        Wavee.Protocol.Playlist.SelectedListContent content,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);
        ArgumentNullException.ThrowIfNull(content);

        var existing = _hotCache.Get(playlistUri);
        if (existing is null)
        {
            var persisted = await _database.GetPlaylistCacheEntryAsync(playlistUri, touchAccess: false, ct);
            if (persisted is not null)
                existing = DeserializePlaylist(persisted);
        }

        var mapped = SelectedListContentMapper.MapPlaylist(
            playlistUri,
            content,
            GetCurrentUsername(),
            DateTimeOffset.UtcNow);

        var merged = PreserveSummaryFields(existing, mapped);

        await PersistPlaylistAsync(merged, ct);
        _hotCache.Set(playlistUri, merged);

        // Always emit — caller's whole point is to broadcast the new state.
        // Don't gate on revision equality: the server may bump items / chips
        // without bumping the revision counter for some signal types.
        _changes.OnNext(new PlaylistChangeEvent
        {
            Uri = playlistUri,
            Kind = existing == null ? PlaylistChangeKind.Replaced : PlaylistChangeKind.Updated
        });

        return merged;
    }

    public Task InvalidateAsync(string playlistUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        if (IsRootlistKey(playlistUri))
        {
            _hotRootlist = null;
        }
        else
        {
            _hotCache.Remove(playlistUri);
            // Drop any negative-cache entry too so an explicit invalidate
            // forces a real network attempt regardless of prior failures.
            _negativeCache.TryRemove(playlistUri, out _);
        }

        return Task.CompletedTask;
    }

    private async Task WarmupAsync()
    {
        try
        {
            EnsureDealerSubscription();

            // Rootlist hydration requires an authenticated username to form the cache key.
            // Session credentials can land after construction, so skip silently when we can't
            // derive the URI yet — the first authenticated GetRootlistAsync call will hydrate it.
            var username = GetCurrentUsername();
            if (!string.IsNullOrWhiteSpace(username))
            {
                var rootlistUri = $"spotify:user:{username}:rootlist";
                var rootlist = await _database.GetRootlistCacheEntryAsync(rootlistUri, touchAccess: false);
                if (rootlist != null)
                {
                    _hotRootlist = DeserializeRootlistSnapshot(rootlist);
                }
            }

            var entries = await _database.GetRecentPlaylistCacheEntriesAsync(32);
            var hydrated = 0;
            var stale = 0;
            foreach (var entry in entries)
            {
                // Hydrate every row so the sidebar and other read-only consumers
                // get instant content. Tag stale-schema rows so the first
                // GetPlaylistAsync access for them forces a fresh network fetch
                // (re-persisting at the current version) — the user keeps stale
                // metadata visible for a few hundred ms during that refresh, but
                // never sees an empty sidebar.
                _hotCache.Set(entry.Uri, DeserializePlaylist(entry));
                hydrated++;

                if (entry.CacheSchemaVersion < CurrentCacheSchemaVersion)
                {
                    _staleSchemaUris[entry.Uri] = 0;
                    stale++;
                }
            }

            if (hydrated > 0)
            {
                _logger?.LogInformation(
                    "PlaylistCache warmup hydrated {Count} playlists from SQLite ({Stale} need a schema-bump refresh on first access)",
                    hydrated, stale);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Playlist cache warmup failed");
        }
    }

    private void SchedulePlaylistAccessTouch(string playlistUri)
    {
        ScheduleAccessTouch(
            $"playlist:{playlistUri}",
            static async (database, uri) => await database.TouchPlaylistCacheEntryAsync(uri, DateTimeOffset.UtcNow),
            playlistUri);
    }

    private void ScheduleRootlistAccessTouch(string rootlistUri)
    {
        ScheduleAccessTouch(
            $"rootlist:{rootlistUri}",
            static async (database, uri) => await database.TouchRootlistCacheEntryAsync(uri, DateTimeOffset.UtcNow),
            rootlistUri);
    }

    private void ScheduleAccessTouch(
        string dedupeKey,
        Func<IMetadataDatabase, string, Task> touchOperation,
        string uri)
    {
        if (!_pendingAccessTouches.TryAdd(dedupeKey, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await touchOperation(_database, uri);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to update playlist cache access time for {Uri}", uri);
            }
            finally
            {
                _pendingAccessTouches.TryRemove(dedupeKey, out _);
            }
        });
    }

    private Task<CachedPlaylist> GetOrCreatePlaylistRefreshTask(string playlistUri, bool emitChange)
    {
        // Stamp the refresh timestamp on every path (nav-driven and
        // dealer-driven) so HandleDealerRefreshAsync's settle window
        // suppresses Mercury echoes that follow our own fetches by less
        // than DealerSettleWindow. Without this, an editorial Mix open
        // produces an immediate echo: nav-fetch → Mercury push within
        // milliseconds → dealer handler sees no prior entry → diff (509)
        // → full-fetch fallback → second publish to the VM.
        _lastDealerRefreshAt[playlistUri] = DateTimeOffset.UtcNow;

        // GetOrAdd stores the Lazy *before* anyone calls .Value. The factory
        // may be invoked twice under contention, but only the Lazy that wins
        // the slot has its Value accessed — the losing Lazy is discarded
        // before its inner factory runs, so FetchPlaylistFromNetworkAsync
        // (and its "Fetching playlist:" log line) fire exactly once per URI.
        var lazy = _playlistRefreshes.GetOrAdd(playlistUri, uri =>
            new Lazy<Task<CachedPlaylist>>(
                () =>
                {
                    var task = FetchPlaylistFromNetworkAsync(uri, emitChange, CancellationToken.None);
                    _ = task.ContinueWith(
                        static (t, state) =>
                        {
                            var tuple = ((ConcurrentDictionary<string, Lazy<Task<CachedPlaylist>>>, string))state!;
                            tuple.Item1.TryRemove(tuple.Item2, out _);
                        },
                        (_playlistRefreshes, uri),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                    return task;
                },
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private Task<RootlistSnapshot> GetOrCreateRootlistRefreshTask(bool forceRefresh, bool emitChange)
    {
        lock (_rootlistGate)
        {
            if (_rootlistRefreshTask != null && !_rootlistRefreshTask.IsCompleted)
                return _rootlistRefreshTask;

            _rootlistRefreshTask = FetchRootlistFromNetworkAsync(forceRefresh, emitChange, CancellationToken.None);
            _ = _rootlistRefreshTask.ContinueWith(
                _ =>
                {
                    lock (_rootlistGate)
                    {
                        _rootlistRefreshTask = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return _rootlistRefreshTask;
        }
    }

    private async Task<RootlistSnapshot> FetchRootlistFromNetworkAsync(
        bool forceRefresh,
        bool emitChange,
        CancellationToken ct)
    {
        var rootlistUri = GetRootlistUri();
        var previous = _hotRootlist;

        var response = await _session.SpClient.GetPlaylistAsync(
            rootlistUri,
            decorate: RootlistDecorations,
            cancellationToken: ct);

        var snapshot = SelectedListContentMapper.MapRootlist(response, DateTimeOffset.UtcNow);
        await PersistRootlistAsync(rootlistUri, snapshot, ct);
        _hotRootlist = snapshot;

        if (emitChange && previous != null && !RevisionsEqual(previous.Revision, snapshot.Revision))
        {
            _changes.OnNext(new PlaylistChangeEvent
            {
                Uri = PlaylistCacheUris.Rootlist,
                Kind = PlaylistChangeKind.Replaced
            });
        }

        _logger?.LogInformation(
            "PlaylistCache rootlist fetched ({Count} items)",
            snapshot.Items.Count);

        return snapshot;
    }

    private async Task PersistRootlistAsync(string rootlistUri, RootlistSnapshot snapshot, CancellationToken ct)
    {
        var persisted = new PersistedRootlistData
        {
            Revision = snapshot.Revision,
            Items = snapshot.Items.Select(ToPersistedRootlistEntry).ToList(),
            Decorations = new Dictionary<string, RootlistDecoration>(snapshot.Decorations, StringComparer.Ordinal),
            FetchedAt = snapshot.FetchedAt
        };

        var json = JsonSerializer.Serialize(persisted, PlaylistCacheJsonContext.Default.PersistedRootlistData);
        await _database.UpsertRootlistCacheEntryAsync(
            new RootlistCacheEntry
            {
                Uri = rootlistUri,
                Revision = snapshot.Revision,
                JsonData = json,
                CachedAt = snapshot.FetchedAt,
                LastAccessedAt = DateTimeOffset.UtcNow
            },
            ct);

        foreach (var playlistEntry in snapshot.Items.OfType<RootlistPlaylist>())
        {
            snapshot.Decorations.TryGetValue(playlistEntry.Uri, out var decoration);
            var existing = await _database.GetPlaylistCacheEntryAsync(playlistEntry.Uri, touchAccess: false, ct);
            var merged = MergeSummaryEntry(playlistEntry.Uri, decoration, existing, snapshot.FetchedAt);
            await _database.UpsertPlaylistCacheEntryAsync(merged, ct);
        }
    }

    private async Task<CachedPlaylist> FetchPlaylistFromNetworkAsync(
        string playlistUri,
        bool emitChange,
        CancellationToken ct)
    {
        var existingEntry = await _database.GetPlaylistCacheEntryAsync(playlistUri, touchAccess: false, ct);
        var existing = existingEntry != null ? DeserializePlaylist(existingEntry) : null;

        if (existing is { HasContentsSnapshot: true } && existing.Revision.Length > 0)
        {
            try
            {
                var diff = await _session.SpClient.GetPlaylistDiffAsync(playlistUri, existing.Revision, ct);
                if (diff.UpToDate || RevisionsEqual(existing.Revision, diff.Revision?.ToByteArray() ?? []))
                {
                    var refreshed = existing with
                    {
                        FetchedAt = DateTimeOffset.UtcNow,
                        LastAccessedAt = DateTimeOffset.UtcNow
                    };
                    await PersistPlaylistAsync(refreshed, ct);
                    _hotCache.Set(playlistUri, refreshed);
                    return refreshed;
                }

                // Delta form: Spotify returned an Ops array instead of a fresh
                // Contents snapshot. Apply the ops to existing.Items locally —
                // saves transferring the whole item list for a single-track edit
                // (the entire point of the diff endpoint). Falls through to the
                // existing Contents/full-fetch branches if ops application fails
                // (out-of-range index ⇒ "diff too stale", same recovery as
                // before this branch existed).
                if (diff.Diff is { Ops.Count: > 0 } deltaDiff)
                {
                    try
                    {
                        var applied = PlaylistDiffApplier.Apply(existing.Items, deltaDiff.Ops);
                        var newRevision = deltaDiff.ToRevision is { Length: > 0 } toRev
                            ? toRev.ToByteArray()
                            : (diff.Revision?.ToByteArray() ?? existing.Revision);

                        var mergedFromOps = existing with
                        {
                            Items = applied.Items,
                            Length = applied.Items.Count,
                            Revision = newRevision,
                            FetchedAt = DateTimeOffset.UtcNow,
                        };

                        // UPDATE_LIST_ATTRIBUTES ops accumulate into the applier's
                        // result; merge them into the cached playlist's list-level
                        // fields here (Name, Description, Picture, Collaborative,
                        // FormatAttributes, …).
                        if (applied.AccumulatedListAttrs is { } listAttrs)
                            mergedFromOps = ApplyListAttrPartial(mergedFromOps, listAttrs);

                        // Diff responses ship the current capabilities even on
                        // item-only edits — refresh so the cached Capabilities
                        // doesn't go stale until the next full fetch.
                        if (diff.Capabilities is not null)
                        {
                            mergedFromOps = mergedFromOps with
                            {
                                Capabilities = SelectedListContentMapper.MapCapabilities(
                                    diff.Capabilities, diff.AbuseReportingEnabled),
                                AbuseReportingEnabled = diff.AbuseReportingEnabled,
                            };
                        }

                        // Refresh AvailableSignals from the diff response too — otherwise
                        // a SQLite-loaded existing entry (pre-schema-v13, empty signals)
                        // would stay empty across diff-only refreshes, and the chip row
                        // would never get its signal identifiers populated.
                        var diffSignals = SelectedListContentMapper.ExtractAvailableSignals(diff.Contents);
                        if (diffSignals.Count > 0)
                        {
                            mergedFromOps = mergedFromOps with { AvailableSignals = diffSignals };
                        }

                        await PersistPlaylistAsync(mergedFromOps, ct);
                        _hotCache.Set(playlistUri, mergedFromOps);

                        if (emitChange && !RevisionsEqual(existing.Revision, mergedFromOps.Revision))
                        {
                            _changes.OnNext(new PlaylistChangeEvent
                            {
                                Uri = playlistUri,
                                Kind = PlaylistChangeKind.Updated
                            });
                        }

                        return mergedFromOps;
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger?.LogDebug(ex,
                            "Diff ops apply failed for {Uri}, falling back to snapshot/full path", playlistUri);
                        // intentional fall-through to the Contents branch below.
                    }
                }

                if (diff.Contents != null)
                {
                    var mappedFromDiff = SelectedListContentMapper.MapPlaylist(
                        playlistUri,
                        diff,
                        GetCurrentUsername(),
                        DateTimeOffset.UtcNow);

                    var mergedFromDiff = PreserveSummaryFields(existing, mappedFromDiff);
                    await PersistPlaylistAsync(mergedFromDiff, ct);
                    _hotCache.Set(playlistUri, mergedFromDiff);

                    if (emitChange && !RevisionsEqual(existing.Revision, mergedFromDiff.Revision))
                    {
                        _changes.OnNext(new PlaylistChangeEvent
                        {
                            Uri = playlistUri,
                            Kind = PlaylistChangeKind.Updated
                        });
                    }

                    return mergedFromDiff;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Playlist diff fetch failed for {Uri}, falling back to full fetch", playlistUri);
            }
        }

        try
        {
            var full = await _session.SpClient.GetPlaylistAsync(
                playlistUri,
                decorate: PlaylistDecorations,
                cancellationToken: ct);

            var mapped = SelectedListContentMapper.MapPlaylist(
                playlistUri,
                full,
                GetCurrentUsername(),
                DateTimeOffset.UtcNow);

            var merged = PreserveSummaryFields(existing, mapped);
            await PersistPlaylistAsync(merged, ct);
            _hotCache.Set(playlistUri, merged);

            // Emit when revision differs OR when AvailableSignals transitions
            // (e.g. SQLite-loaded existing has empty signals, fresh fetch
            // populated 5). Without the second clause, editorial Mixes that
            // keep the same revision on refresh never tell the store to re-
            // emit, leaving the UI stuck on chips that know their labels but
            // not their signal identifiers.
            if (emitChange && (existing == null
                || !RevisionsEqual(existing.Revision, merged.Revision)
                || existing.AvailableSignals.Count != merged.AvailableSignals.Count))
            {
                _changes.OnNext(new PlaylistChangeEvent
                {
                    Uri = playlistUri,
                    Kind = existing == null ? PlaylistChangeKind.Replaced : PlaylistChangeKind.Updated
                });
            }

            return merged;
        }
        catch (SpClientException ex) when (ex.Reason == SpClientFailureReason.NotFound)
        {
            _hotCache.Remove(playlistUri);
            RecordNegativeCache(playlistUri, ex);
            if (emitChange)
            {
                _changes.OnNext(new PlaylistChangeEvent
                {
                    Uri = playlistUri,
                    Kind = PlaylistChangeKind.Removed
                });
            }

            throw;
        }
        catch (SpClientException ex) when (ex.Reason == SpClientFailureReason.Unauthorized)
        {
            // 401 / 403 — not a deletion, just inaccessible. Remember it
            // for the negative-cache window so repeat callers don't reissue
            // identical doomed round-trips.
            RecordNegativeCache(playlistUri, ex);
            throw;
        }
    }

    private async Task PersistPlaylistAsync(CachedPlaylist playlist, CancellationToken ct)
    {
        var itemsJson = JsonSerializer.Serialize(
            new PersistedPlaylistItems { Items = playlist.Items.ToList() },
            PlaylistCacheJsonContext.Default.PersistedPlaylistItems);
        var capabilitiesJson = JsonSerializer.Serialize(
            playlist.Capabilities,
            PlaylistCacheJsonContext.Default.CachedPlaylistCapabilities);
        string? formatAttributesJson = null;
        if (playlist.FormatAttributes.Count > 0)
        {
            // Convert to the concrete Dictionary<string,string> type the source
            // generator has a marshaller for; IReadOnlyDictionary isn't registered.
            var attrs = new Dictionary<string, string>(playlist.FormatAttributes.Count, StringComparer.Ordinal);
            foreach (var kv in playlist.FormatAttributes) attrs[kv.Key] = kv.Value;
            formatAttributesJson = JsonSerializer.Serialize(
                attrs, PlaylistCacheJsonContext.Default.DictionaryStringString);
        }

        string? availableSignalsJson = null;
        if (playlist.AvailableSignals.Count > 0)
        {
            availableSignalsJson = JsonSerializer.Serialize(
                playlist.AvailableSignals.ToList(),
                PlaylistCacheJsonContext.Default.ListString);
        }

        await _database.UpsertPlaylistCacheEntryAsync(
            new PlaylistCacheEntry
            {
                Uri = playlist.Uri,
                Name = playlist.Name,
                Description = playlist.Description,
                OwnerUri = string.IsNullOrWhiteSpace(playlist.OwnerUsername)
                    ? null
                    : $"spotify:user:{playlist.OwnerUsername}",
                OwnerUsername = playlist.OwnerUsername,
                OwnerName = playlist.OwnerUsername,
                TrackCount = playlist.Length,
                ImageUrl = playlist.ImageUrl,
                HeaderImageUrl = playlist.HeaderImageUrl,
                IsPublic = playlist.IsPublic,
                IsCollaborative = playlist.IsCollaborative,
                Revision = playlist.Revision,
                OrderedItemsJson = itemsJson,
                HasContentsSnapshot = playlist.HasContentsSnapshot,
                BasePermission = playlist.BasePermission,
                CapabilitiesJson = capabilitiesJson,
                FormatAttributesJson = formatAttributesJson,
                AvailableSignalsJson = availableSignalsJson,
                DeletedByOwner = playlist.DeletedByOwner,
                AbuseReportingEnabled = playlist.AbuseReportingEnabled,
                CachedAt = playlist.FetchedAt,
                LastAccessedAt = DateTimeOffset.UtcNow,
                CacheSchemaVersion = CurrentCacheSchemaVersion
            },
            ct);

        _logger?.LogInformation(
            "[caps] Persisted '{Uri}' at v{Ver}: BasePerm={Base} Caps=[View={V},EditItems={EI},EditMeta={EM},Admin={AD}] CapsJsonLen={Len}",
            playlist.Uri, CurrentCacheSchemaVersion, playlist.BasePermission,
            playlist.Capabilities.CanView, playlist.Capabilities.CanEditItems,
            playlist.Capabilities.CanEditMetadata, playlist.Capabilities.CanAdministratePermissions,
            capabilitiesJson.Length);
    }

    /// <summary>
    /// Cache shape version for the persisted JSON blobs on a playlist row.
    /// Bump this whenever any field is added, removed, or has its semantics
    /// change inside <see cref="CachedPlaylistCapabilities"/>,
    /// <see cref="CachedPlaylist"/>, <see cref="CachedPlaylistItem"/>, or any
    /// other shape we serialize into a <c>spotify_playlists</c> row.
    ///
    /// On read, rows whose stored <c>cache_schema_version</c> is below this
    /// value are treated as cache misses — the playlist gets re-fetched from
    /// the network and re-persisted at the current version. This avoids the
    /// "owner sees view-only" / "old enum value treated as zero" failure mode
    /// that happens when System.Text.Json silently fills missing fields with
    /// their CLR defaults.
    ///
    /// History:
    ///   1 — added <c>CachedPlaylistCapabilities.CanEditMetadata</c>
    ///   2 — <c>SelectedListContentMapper.PickImageUrl</c> now falls back to the
    ///       raw <c>attributes.picture</c> ByteString when PictureSize is empty,
    ///       so user-customised playlist covers stop collapsing to mosaic. The
    ///       persisted <c>ImageUrl</c> column on existing rows is null for those
    ///       playlists; bumping forces a refetch so the URL fills in.
    ///   3 — <c>ApplyListAttrPartial</c> (the diff path) now also fires
    ///       <c>PickImageUrl</c> when only <c>Picture</c> is present (no
    ///       PictureSize). v2 only fixed the full-fetch path, so playlists
    ///       cached via diff still had <c>ImageUrl=null</c>. Re-bump invalidates
    ///       those rows and forces a fresh full mapping that picks up the URL.
    /// </summary>
    public const int CurrentCacheSchemaVersion = 3;

    private PlaylistCacheEntry MergeSummaryEntry(
        string playlistUri,
        RootlistDecoration? decoration,
        PlaylistCacheEntry? existing,
        DateTimeOffset fetchedAt)
    {
        var ownerUsername = decoration?.OwnerUsername ?? existing?.OwnerUsername;
        return new PlaylistCacheEntry
        {
            Uri = playlistUri,
            Name = decoration?.Name ?? existing?.Name ?? "Playlist",
            Description = decoration?.Description ?? existing?.Description,
            OwnerUri = !string.IsNullOrWhiteSpace(ownerUsername) ? $"spotify:user:{ownerUsername}" : existing?.OwnerUri,
            OwnerUsername = ownerUsername,
            OwnerName = ownerUsername ?? existing?.OwnerName,
            TrackCount = decoration?.Length > 0 ? decoration.Length : existing?.TrackCount,
            ImageUrl = decoration?.ImageUrl ?? existing?.ImageUrl,
            IsPublic = decoration?.IsPublic ?? existing?.IsPublic ?? false,
            IsCollaborative = decoration?.IsCollaborative ?? existing?.IsCollaborative ?? false,
            Revision = decoration?.Revision.Length > 0 ? decoration.Revision : existing?.Revision,
            OrderedItemsJson = existing?.OrderedItemsJson,
            HasContentsSnapshot = existing?.HasContentsSnapshot ?? false,
            BasePermission = existing?.BasePermission ?? GetSummaryBasePermission(ownerUsername),
            CapabilitiesJson = existing?.CapabilitiesJson,
            DeletedByOwner = existing?.DeletedByOwner ?? false,
            AbuseReportingEnabled = existing?.AbuseReportingEnabled ?? false,
            CachedAt = fetchedAt,
            LastAccessedAt = existing?.LastAccessedAt,
            // Preserve the existing row's stamp — we're merging summary fields,
            // not rewriting the JSON blobs, so an existing v1 row stays v1. A
            // brand-new row from a rootlist-only entry starts at the current
            // version (we just don't have full contents yet, gated separately
            // by HasContentsSnapshot).
            CacheSchemaVersion = existing?.CacheSchemaVersion ?? CurrentCacheSchemaVersion
        };
    }

    private CachedPlaylist PreserveSummaryFields(CachedPlaylist? existing, CachedPlaylist fetched)
    {
        if (existing == null)
            return fetched;

        return fetched with
        {
            IsPublic = existing.IsPublic,
            ImageUrl = fetched.ImageUrl ?? existing.ImageUrl,
            Description = fetched.Description ?? existing.Description
        };
    }

    /// <summary>
    /// Applies a coalesced <see cref="Wavee.Protocol.Playlist.ListAttributesPartialState"/>
    /// (produced by <see cref="PlaylistDiffApplier"/> from a batch of
    /// UPDATE_LIST_ATTRIBUTES ops) to the playlist's list-level fields. Fields
    /// in <c>partial.Values</c> overwrite; kinds in <c>partial.NoValue</c> reset
    /// to defaults; the rest are preserved.
    /// </summary>
    private static CachedPlaylist ApplyListAttrPartial(
        CachedPlaylist current, Wavee.Protocol.Playlist.ListAttributesPartialState partial)
    {
        string name = current.Name;
        string? description = current.Description;
        string? imageUrl = current.ImageUrl;
        string? headerImageUrl = current.HeaderImageUrl;
        bool isCollab = current.IsCollaborative;
        bool deletedByOwner = current.DeletedByOwner;
        var formatAttrs = current.FormatAttributes;

        if (partial.Values is { } v)
        {
            if (!string.IsNullOrEmpty(v.Name)) name = v.Name;
            if (!string.IsNullOrEmpty(v.Description)) description = v.Description;
            // PickImageUrl prefers PictureSize but falls back to the raw `picture`
            // ByteString — gating the call on PictureSize.Count alone meant
            // user-customised covers (which only ship as a Picture id) never
            // flowed into the cache through the diff path, so those playlists
            // collapsed to mosaic forever even after the v2 schema bump.
            if (v.PictureSize.Count > 0 || (v.HasPicture && v.Picture.Length > 0))
                imageUrl = SelectedListContentMapper.PickImageUrl(v) ?? imageUrl;
            if (v.HasCollaborative) isCollab = v.Collaborative;
            if (v.HasDeletedByOwner) deletedByOwner = v.DeletedByOwner;
            if (v.FormatAttributes.Count > 0)
            {
                formatAttrs = SelectedListContentMapper.ExtractFormatAttributes(v.FormatAttributes);
                headerImageUrl = SelectedListContentMapper
                    .PickFormatAttribute(v, "header_image_url_desktop")
                    ?? headerImageUrl;
            }
        }

        foreach (var kind in partial.NoValue)
        {
            switch (kind)
            {
                case Wavee.Protocol.Playlist.ListAttributeKind.ListName: name = "Unknown"; break;
                case Wavee.Protocol.Playlist.ListAttributeKind.ListDescription: description = null; break;
                case Wavee.Protocol.Playlist.ListAttributeKind.ListPicture:
                case Wavee.Protocol.Playlist.ListAttributeKind.ListPictureSize:
                    imageUrl = null;
                    break;
                case Wavee.Protocol.Playlist.ListAttributeKind.ListCollaborative: isCollab = false; break;
                case Wavee.Protocol.Playlist.ListAttributeKind.ListDeletedByOwner: deletedByOwner = false; break;
                case Wavee.Protocol.Playlist.ListAttributeKind.ListFormatAttributes:
                    formatAttrs = _emptyFormatAttributes;
                    headerImageUrl = null;
                    break;
            }
        }

        return current with
        {
            Name = name,
            Description = description,
            ImageUrl = imageUrl,
            HeaderImageUrl = headerImageUrl,
            IsCollaborative = isCollab,
            DeletedByOwner = deletedByOwner,
            FormatAttributes = formatAttrs,
        };
    }

    private CachedPlaylist DeserializePlaylist(PlaylistCacheEntry entry)
    {
        var items = Array.Empty<CachedPlaylistItem>();
        if (!string.IsNullOrWhiteSpace(entry.OrderedItemsJson))
        {
            var persistedItems = JsonSerializer.Deserialize(
                entry.OrderedItemsJson,
                PlaylistCacheJsonContext.Default.PersistedPlaylistItems);
            items = persistedItems?.Items.ToArray() ?? [];
        }

        var capabilities = CachedPlaylistCapabilities.ViewOnly;
        if (!string.IsNullOrWhiteSpace(entry.CapabilitiesJson))
        {
            capabilities = JsonSerializer.Deserialize(
                    entry.CapabilitiesJson,
                    PlaylistCacheJsonContext.Default.CachedPlaylistCapabilities)
                ?? CachedPlaylistCapabilities.ViewOnly;
        }

        _logger?.LogInformation(
            "[caps] Deserialize '{Uri}' (rowSchemaV{V}): BasePerm={Base} Caps=[View={CV},EditItems={EI},EditMeta={EM},Admin={AD}] CapsJson={Has}",
            entry.Uri, entry.CacheSchemaVersion, entry.BasePermission,
            capabilities.CanView, capabilities.CanEditItems, capabilities.CanEditMetadata, capabilities.CanAdministratePermissions,
            string.IsNullOrEmpty(entry.CapabilitiesJson) ? "missing" : $"{entry.CapabilitiesJson.Length}b");

        IReadOnlyDictionary<string, string> formatAttributes = _emptyFormatAttributes;
        if (!string.IsNullOrWhiteSpace(entry.FormatAttributesJson))
        {
            var parsed = JsonSerializer.Deserialize(
                entry.FormatAttributesJson,
                PlaylistCacheJsonContext.Default.DictionaryStringString);
            if (parsed is { Count: > 0 })
                formatAttributes = parsed;
        }

        IReadOnlyList<string> availableSignals = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(entry.AvailableSignalsJson))
        {
            var parsed = JsonSerializer.Deserialize(
                entry.AvailableSignalsJson,
                PlaylistCacheJsonContext.Default.ListString);
            if (parsed is { Count: > 0 })
                availableSignals = parsed;
        }

        return new CachedPlaylist
        {
            Uri = entry.Uri,
            Revision = entry.Revision ?? [],
            Name = entry.Name ?? "Playlist",
            Description = entry.Description,
            ImageUrl = entry.ImageUrl,
            HeaderImageUrl = entry.HeaderImageUrl,
            OwnerUsername = entry.OwnerUsername ?? entry.OwnerName ?? "",
            Length = entry.TrackCount ?? 0,
            IsPublic = entry.IsPublic,
            IsCollaborative = entry.IsCollaborative,
            DeletedByOwner = entry.DeletedByOwner,
            AbuseReportingEnabled = entry.AbuseReportingEnabled,
            HasContentsSnapshot = entry.HasContentsSnapshot,
            BasePermission = entry.BasePermission,
            Capabilities = capabilities,
            Items = items,
            FormatAttributes = formatAttributes,
            AvailableSignals = availableSignals,
            FetchedAt = entry.CachedAt,
            LastAccessedAt = entry.LastAccessedAt
        };
    }

    private static readonly IReadOnlyDictionary<string, string> _emptyFormatAttributes
        = new Dictionary<string, string>(0);

    private RootlistSnapshot? DeserializeRootlistSnapshot(RootlistCacheEntry entry)
    {
        try
        {
            var persisted = JsonSerializer.Deserialize(
                entry.JsonData,
                PlaylistCacheJsonContext.Default.PersistedRootlistData);

            if (persisted == null)
                return null;

            return new RootlistSnapshot
            {
                Revision = persisted.Revision,
                Items = persisted.Items.Select(FromPersistedRootlistEntry).ToArray(),
                Decorations = new Dictionary<string, RootlistDecoration>(persisted.Decorations, StringComparer.Ordinal),
                FetchedAt = persisted.FetchedAt
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to deserialize persisted rootlist cache");
            return null;
        }
    }

    /// <summary>
    /// Direct ops application path for Mercury library-change events that carry
    /// a <see cref="LibraryChangeEvent.FromRevision"/> + <see cref="LibraryChangeEvent.Ops"/>
    /// payload. Skips the <c>/diff</c> network round-trip when the cached
    /// <c>existing.Revision</c> matches the event's <c>FromRevision</c> — the
    /// Mercury push *is* the diff. On revision mismatch (we missed an
    /// intermediate push) or apply failure, returns false so the caller can
    /// fall back to <see cref="HandleDealerRefreshAsync"/> which fetches via
    /// the network.
    /// </summary>
    private async Task<bool> TryApplyMercuryOpsAsync(
        string playlistUri,
        byte[] fromRevision,
        byte[]? newRevision,
        IReadOnlyList<Wavee.Protocol.Playlist.Op> ops)
    {
        try
        {
            var existingEntry = await _database.GetPlaylistCacheEntryAsync(playlistUri, touchAccess: false);
            var existing = existingEntry != null ? DeserializePlaylist(existingEntry) : null;

            // Brand-new playlist: Mercury sends the initial state as ops on top
            // of a zero-revision baseline. Synthesize an empty starting snapshot
            // so the applier can write into it — saves a /diff fetch on
            // playlist creation. UPDATE_LIST_ATTRIBUTES + initial ADD ops in
            // the payload populate Name + Items.
            if (existing is null && IsZeroRevisionCounter(fromRevision))
            {
                existing = new CachedPlaylist
                {
                    Uri = playlistUri,
                    Name = "Playlist", // overwritten by UPDATE_LIST_ATTRIBUTES op below
                    Revision = fromRevision,
                    OwnerUsername = GetCurrentUsername() ?? string.Empty,
                    Items = Array.Empty<CachedPlaylistItem>(),
                    HasContentsSnapshot = true,
                    FetchedAt = DateTimeOffset.UtcNow,
                };
            }
            else if (existing is null || !existing.HasContentsSnapshot)
                return false; // No baseline AND not a brand-new playlist — caller falls back to fetch.
            else if (!RevisionsEqual(existing.Revision, fromRevision))
                return false; // We missed an intermediate revision — fall back to fetch.

            var applied = PlaylistDiffApplier.Apply(existing.Items, ops);
            var resolvedNewRevision = newRevision is { Length: > 0 }
                ? newRevision
                : existing.Revision;

            var merged = existing with
            {
                Items = applied.Items,
                Length = applied.Items.Count,
                Revision = resolvedNewRevision,
                FetchedAt = DateTimeOffset.UtcNow,
            };

            // List-level attribute deltas (UPDATE_LIST_ATTRIBUTES ops) are merged
            // identically to the diff-fetch path so name/description/image renames
            // pushed via Mercury also land without a fetch.
            if (applied.AccumulatedListAttrs is { } listAttrs)
                merged = ApplyListAttrPartial(merged, listAttrs);

            await PersistPlaylistAsync(merged, CancellationToken.None);
            _hotCache.Set(playlistUri, merged);
            // Stamp the settle window so the throttled refresh path that fires
            // in parallel for the same Mercury event sees a fresh entry and
            // bails — otherwise we'd do the work twice (apply locally + fetch).
            _lastDealerRefreshAt[playlistUri] = DateTimeOffset.UtcNow;

            if (!RevisionsEqual(existing.Revision, merged.Revision))
            {
                _changes.OnNext(new PlaylistChangeEvent
                {
                    Uri = playlistUri,
                    Kind = PlaylistChangeKind.Updated
                });
            }

            _logger?.LogDebug(
                "Applied Mercury ops directly for {Uri}: {OpCount} ops, no /diff round-trip",
                playlistUri, ops.Count);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // PlaylistDiffApplier saw a torn op (out-of-range index, etc.) —
            // not a bug in our code, just a baseline drift.
            _logger?.LogDebug(ex,
                "Mercury ops apply failed for {Uri}, falling back to fetch", playlistUri);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unexpected error applying Mercury ops for {Uri}", playlistUri);
            return false;
        }
    }

    private async Task HandleDealerRefreshAsync(string uri)
    {
        try
        {
            // Settle window: drop echo-Mercury notifications that follow a fresh
            // refresh by less than DealerSettleWindow. See the constant's comment
            // for the full diff-509/full-fetch/echo cycle this guards against.
            // Latches even before the refresh runs, so a burst of dealer pushes
            // for the same URI all collapse to one network round-trip.
            var now = DateTimeOffset.UtcNow;
            if (_lastDealerRefreshAt.TryGetValue(uri, out var lastAt)
                && now - lastAt < DealerSettleWindow)
            {
                return;
            }
            _lastDealerRefreshAt[uri] = now;

            if (IsRootlistKey(uri))
            {
                await GetOrCreateRootlistRefreshTask(forceRefresh: true, emitChange: true);
                return;
            }

            await GetOrCreatePlaylistRefreshTask(uri, emitChange: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Dealer-triggered playlist refresh failed for {Uri}", uri);
        }
    }

    private async Task RefreshPlaylistSafeAsync(string playlistUri)
    {
        try
        {
            await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Background playlist refresh failed for {Uri}", playlistUri);
        }
    }

    private async Task RefreshRootlistSafeAsync()
    {
        try
        {
            await GetOrCreateRootlistRefreshTask(forceRefresh: true, emitChange: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Background rootlist refresh failed");
        }
    }

    private static PersistedRootlistEntry ToPersistedRootlistEntry(RootlistEntry entry)
    {
        return entry switch
        {
            RootlistPlaylist playlist => new PersistedRootlistEntry
            {
                Kind = PersistedRootlistEntryKind.Playlist,
                Uri = playlist.Uri
            },
            RootlistFolderStart folder => new PersistedRootlistEntry
            {
                Kind = PersistedRootlistEntryKind.FolderStart,
                Id = folder.Id,
                Name = folder.Name
            },
            RootlistFolderEnd folder => new PersistedRootlistEntry
            {
                Kind = PersistedRootlistEntryKind.FolderEnd,
                Id = folder.Id
            },
            _ => throw new InvalidOperationException($"Unsupported rootlist entry type: {entry.GetType().Name}")
        };
    }

    private static RootlistEntry FromPersistedRootlistEntry(PersistedRootlistEntry entry)
    {
        return entry.Kind switch
        {
            PersistedRootlistEntryKind.Playlist => new RootlistPlaylist(entry.Uri ?? string.Empty),
            PersistedRootlistEntryKind.FolderStart => new RootlistFolderStart(entry.Id ?? string.Empty, entry.Name ?? "Folder"),
            PersistedRootlistEntryKind.FolderEnd => new RootlistFolderEnd(entry.Id ?? string.Empty),
            _ => throw new InvalidOperationException($"Unsupported persisted rootlist entry kind: {entry.Kind}")
        };
    }

    private string GetRootlistUri()
    {
        var username = GetCurrentUsername();
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("User is not authenticated");

        return $"spotify:user:{username}:rootlist";
    }

    private string? GetCurrentUsername() => _session.GetUserData()?.Username;

    private CachedPlaylistBasePermission GetSummaryBasePermission(string? ownerUsername)
    {
        var currentUsername = GetCurrentUsername();
        return !string.IsNullOrWhiteSpace(ownerUsername) &&
               !string.IsNullOrWhiteSpace(currentUsername) &&
               string.Equals(ownerUsername, currentUsername, StringComparison.OrdinalIgnoreCase)
            ? CachedPlaylistBasePermission.Owner
            : CachedPlaylistBasePermission.Viewer;
    }

    private static bool IsRootlistKey(string uri) =>
        string.Equals(uri, PlaylistCacheUris.Rootlist, StringComparison.Ordinal) ||
        uri.Contains(":rootlist", StringComparison.Ordinal);

    private static bool RevisionsEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null)
            return false;
        return left.AsSpan().SequenceEqual(right);
    }

    /// <summary>
    /// True when the revision's 4-byte big-endian counter is zero — i.e. the
    /// revision belongs to a brand-new playlist that has never been mutated.
    /// Used by <see cref="TryApplyMercuryOpsAsync"/> to detect the "create
    /// playlist from scratch" case where there's no existing cache entry but
    /// the Mercury push still carries the initial-state ops.
    /// </summary>
    private static bool IsZeroRevisionCounter(byte[] revision)
    {
        if (revision is null || revision.Length < 4) return false;
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(revision.AsSpan(0, 4)) == 0;
    }

    private static bool IsStale(DateTimeOffset fetchedAt) => DateTimeOffset.UtcNow - fetchedAt > CacheTtl;

    private static bool ShouldRefresh(CachedPlaylist playlist) =>
        !playlist.HasContentsSnapshot || IsStale(playlist.FetchedAt);

    // Time window inside which a freshly-refreshed URI is considered "still
    // fresh enough" — short enough that warm-cache nav-revisits trigger a
    // diff round-trip ("web-client feel" — caller sees the update within
    // ~1 s of any real edit), long enough that a tab-switch + back doesn't
    // hammer the network. Reuses the same _lastDealerRefreshAt dictionary
    // the dealer settle window uses, so dealer pushes and nav refreshes
    // dedup against each other automatically.
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(30);

    private bool ShouldFreshnessCheck(string playlistUri)
    {
        if (_lastDealerRefreshAt.TryGetValue(playlistUri, out var lastAt))
            return DateTimeOffset.UtcNow - lastAt > FreshnessWindow;
        return true; // never refreshed in this process — always do a freshness check.
    }

    private void EnsureDealerSubscription()
    {
        if (_dealerSubscription != null)
            return;

        lock (_dealerSubscriptionGate)
        {
            if (_dealerSubscription != null)
                return;

            if (_session is not Wavee.Core.Session.Session concreteSession || concreteSession.Dealer == null)
                return;

            _libraryChangeManager = new LibraryChangeManager(concreteSession.Dealer, _logger);

            // Direct-apply path: when a Mercury push carries the full diff
            // payload (FromRevision + Ops), apply it locally with zero network
            // round-trip. NOT throttled — applying ops requires processing
            // every event in sequence (skipping intermediate pushes would mean
            // we can't reconcile via a later FromRevision either). Per-event
            // cost is microseconds; the throttle in the refresh path below
            // is for a different purpose (network dedup).
            _directApplySubscription = _libraryChangeManager.Changes
                .Where(static change =>
                    change.Set == "playlists"
                    && !string.IsNullOrEmpty(change.PlaylistUri)
                    && change.FromRevision is { Length: > 0 }
                    && change.Ops is { Count: > 0 })
                .Subscribe(change => _ = TryApplyMercuryOpsAndMaybeRefreshAsync(change));

            // Refresh path (existing): collapses bursts and falls back to a
            // network fetch. Still useful for events without ops (rootlist
            // changes, parser failures) AND as a safety net when the direct
            // apply hits a revision mismatch (TryApplyMercuryOpsAndMaybeRefreshAsync
            // routes through here on its own).
            _dealerSubscription = _libraryChangeManager.Changes
                .Where(static change => change.Set == "playlists" || change.IsRootlist)
                .Where(static change => change.Ops is null || change.Ops.Count == 0)
                .Select(static change => change.IsRootlist
                    ? PlaylistCacheUris.Rootlist
                    : change.PlaylistUri ?? PlaylistCacheUris.Rootlist)
                .GroupBy(static key => key)
                .SelectMany(group => group.Throttle(DealerDebounce))
                .Subscribe(key => _ = HandleDealerRefreshAsync(key));
        }
    }

    /// <summary>
    /// Tries the direct-apply path; if it can't reconcile (revision mismatch /
    /// no baseline / torn op), falls back to <see cref="HandleDealerRefreshAsync"/>
    /// which fetches from the network. Saves wiring two parallel subscriptions
    /// that both have to know about each other.
    /// </summary>
    private async Task TryApplyMercuryOpsAndMaybeRefreshAsync(LibraryChangeEvent change)
    {
        var uri = change.PlaylistUri!;
        if (await TryApplyMercuryOpsAsync(uri, change.FromRevision!, change.NewRevision, change.Ops!))
            return;
        await HandleDealerRefreshAsync(uri);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _dealerSubscription?.Dispose();
        _directApplySubscription?.Dispose();
        _libraryChangeManager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _changes.OnCompleted();
        _changes.Dispose();
    }
}
