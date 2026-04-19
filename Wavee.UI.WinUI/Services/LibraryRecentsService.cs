using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Thin wrapper around <c>/recently-played/v3/user/{userId}/recently-played</c> that exposes a
/// URI → last-played-timestamp lookup for library sorting. Two independent caches are kept
/// (album and artist filters) with a short TTL; both are dropped whenever the active playback
/// context changes so a freshly-played item moves to the top on the next sort re-apply.
/// </summary>
public sealed class LibraryRecentsService : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly ISession _session;
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;

    private readonly SemaphoreSlim _albumFetchLock = new(1, 1);
    private readonly SemaphoreSlim _artistFetchLock = new(1, 1);

    private IReadOnlyDictionary<string, DateTimeOffset>? _albumCache;
    private DateTimeOffset _albumCachedAt;
    private IReadOnlyDictionary<string, DateTimeOffset>? _artistCache;
    private DateTimeOffset _artistCachedAt;

    private bool _disposed;

    /// <summary>
    /// Raised on the UI dispatcher whenever a cache entry is replaced (successful fetch or
    /// invalidation). Consumers should re-apply their current sort in response.
    /// </summary>
    public event Action? RecentsChanged;

    public LibraryRecentsService(
        ISession session,
        IMessenger messenger,
        DispatcherQueue dispatcherQueue,
        ILogger<LibraryRecentsService>? logger = null)
    {
        _session = session;
        _messenger = messenger;
        _dispatcherQueue = dispatcherQueue;
        _logger = logger;

        _messenger.Register<PlaybackContextChangedMessage>(this, (_, _) => Invalidate());
    }

    /// <summary>
    /// Returns a URI → last-played-timestamp map for the user's recently-played albums.
    /// Empty dictionary on network/auth failure so sorting falls back to AddedAt cleanly.
    /// </summary>
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAlbumRecentsAsync(CancellationToken ct = default)
        => GetOrFetchAsync("album", _albumFetchLock, () => _albumCache, v => { _albumCache = v; _albumCachedAt = DateTimeOffset.UtcNow; }, () => _albumCachedAt, ct);

    /// <summary>
    /// Returns a URI → last-played-timestamp map for the user's recently-played artists.
    /// </summary>
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetArtistRecentsAsync(CancellationToken ct = default)
        => GetOrFetchAsync("artist", _artistFetchLock, () => _artistCache, v => { _artistCache = v; _artistCachedAt = DateTimeOffset.UtcNow; }, () => _artistCachedAt, ct);

    /// <summary>
    /// Drops both caches and notifies subscribers so the next access refetches.
    /// </summary>
    public void Invalidate()
    {
        _albumCache = null;
        _albumCachedAt = default;
        _artistCache = null;
        _artistCachedAt = default;
        RaiseChanged();
    }

    private async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetOrFetchAsync(
        string filter,
        SemaphoreSlim fetchLock,
        Func<IReadOnlyDictionary<string, DateTimeOffset>?> read,
        Action<IReadOnlyDictionary<string, DateTimeOffset>> write,
        Func<DateTimeOffset> readCachedAt,
        CancellationToken ct)
    {
        var existing = read();
        if (existing != null && DateTimeOffset.UtcNow - readCachedAt() < CacheTtl)
            return existing;

        await fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            existing = read();
            if (existing != null && DateTimeOffset.UtcNow - readCachedAt() < CacheTtl)
                return existing;

            var userId = _session.GetUserData()?.Username;
            if (string.IsNullOrEmpty(userId))
                return EmptyDictionary;

            var response = await _session.SpClient.GetRecentlyPlayedAsync(userId, limit: 100, filter: filter, cancellationToken: ct).ConfigureAwait(false);
            var contexts = response.PlayContexts;
            if (contexts is null || contexts.Count == 0)
            {
                write(EmptyDictionary);
                RaiseChanged();
                return EmptyDictionary;
            }

            var dict = new Dictionary<string, DateTimeOffset>(contexts.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var ctx in contexts)
            {
                if (string.IsNullOrEmpty(ctx.Uri)) continue;
                // Keep the newest entry if a URI appears twice (shouldn't, but be safe).
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(ctx.LastPlayedTime);
                if (!dict.TryGetValue(ctx.Uri, out var existingTs) || ts > existingTs)
                    dict[ctx.Uri] = ts;
            }

            write(dict);
            RaiseChanged();
            return dict;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to fetch recently-played (filter={Filter}) — falling back to empty", filter);
            return EmptyDictionary;
        }
        finally
        {
            fetchLock.Release();
        }
    }

    private void RaiseChanged()
    {
        if (_disposed) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            RecentsChanged?.Invoke();
        });
    }

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> EmptyDictionary =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _messenger.UnregisterAll(this);
        _albumFetchLock.Dispose();
        _artistFetchLock.Dispose();
        RecentsChanged = null;
    }
}
