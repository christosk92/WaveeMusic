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
                if (!hot.HasContentsSnapshot)
                    return await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: false).WaitAsync(ct);

                if (ShouldRefresh(hot))
                    _ = RefreshPlaylistSafeAsync(playlistUri);

                return hot;
            }

            var persisted = await _database.GetPlaylistCacheEntryAsync(playlistUri, touchAccess: false, ct);
            if (persisted != null)
            {
                var cachedPersisted = DeserializePlaylist(persisted);
                _hotCache.Set(playlistUri, cachedPersisted);
                SchedulePlaylistAccessTouch(playlistUri);

                if (!cachedPersisted.HasContentsSnapshot)
                    return await GetOrCreatePlaylistRefreshTask(playlistUri, emitChange: false).WaitAsync(ct);

                if (ShouldRefresh(cachedPersisted))
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
            foreach (var entry in entries)
            {
                _hotCache.Set(entry.Uri, DeserializePlaylist(entry));
            }

            if (entries.Count > 0)
            {
                _logger?.LogInformation(
                    "PlaylistCache warmup hydrated {Count} playlists from SQLite",
                    entries.Count);
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

            if (emitChange && (existing == null || !RevisionsEqual(existing.Revision, merged.Revision)))
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
                IsPublic = playlist.IsPublic,
                IsCollaborative = playlist.IsCollaborative,
                Revision = playlist.Revision,
                OrderedItemsJson = itemsJson,
                HasContentsSnapshot = playlist.HasContentsSnapshot,
                BasePermission = playlist.BasePermission,
                CapabilitiesJson = capabilitiesJson,
                DeletedByOwner = playlist.DeletedByOwner,
                AbuseReportingEnabled = playlist.AbuseReportingEnabled,
                CachedAt = playlist.FetchedAt,
                LastAccessedAt = DateTimeOffset.UtcNow
            },
            ct);
    }

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
            LastAccessedAt = existing?.LastAccessedAt
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

        return new CachedPlaylist
        {
            Uri = entry.Uri,
            Revision = entry.Revision ?? [],
            Name = entry.Name ?? "Playlist",
            Description = entry.Description,
            ImageUrl = entry.ImageUrl,
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
            FetchedAt = entry.CachedAt,
            LastAccessedAt = entry.LastAccessedAt
        };
    }

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

    private async Task HandleDealerRefreshAsync(string uri)
    {
        try
        {
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

    private static bool IsStale(DateTimeOffset fetchedAt) => DateTimeOffset.UtcNow - fetchedAt > CacheTtl;

    private static bool ShouldRefresh(CachedPlaylist playlist) =>
        !playlist.HasContentsSnapshot || IsStale(playlist.FetchedAt);

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
            _dealerSubscription = _libraryChangeManager.Changes
                .Where(static change => change.Set == "playlists" || change.IsRootlist)
                .Select(static change => change.IsRootlist
                    ? PlaylistCacheUris.Rootlist
                    : change.PlaylistUri ?? PlaylistCacheUris.Rootlist)
                .GroupBy(static key => key)
                .SelectMany(group => group.Throttle(DealerDebounce))
                .Subscribe(key => _ = HandleDealerRefreshAsync(key));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _dealerSubscription?.Dispose();
        _libraryChangeManager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _changes.OnCompleted();
        _changes.Dispose();
    }
}
