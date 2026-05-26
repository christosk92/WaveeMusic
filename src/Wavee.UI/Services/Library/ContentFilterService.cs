using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Library.Spotify.Outbox;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.Contracts;

namespace Wavee.UI.Services.Library;

/// <summary>
/// O(1) sync queries + async writes against Spotify's <c>ban</c> (track) and
/// <c>artistban</c> (artist) server-side collections. Reads come from
/// <see cref="IMetadataDatabase"/> tables that <see cref="ISpotifyLibraryService"/>
/// already syncs from Spotify's collection-v2 endpoint on every library
/// refresh; writes optimistically update the local rows and enqueue an
/// outbox entry so the change syncs back to Spotify (and to every other
/// client signed into the same account).
///
/// This mirrors the <see cref="TrackLikeService"/> pattern but for the
/// "blocked / hidden" inverse — IsArtistBlocked / IsTrackHidden are read
/// hot from in-memory sets; mutations route through the library outbox.
/// </summary>
public sealed class ContentFilterService : IContentFilterService
{
    private const string TrackPrefix = "spotify:track:";
    private const string ArtistPrefix = "spotify:artist:";

    private readonly IMetadataDatabase _database;
    private readonly ILogger? _logger;

    private readonly HashSet<string> _bannedTracks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bannedArtists = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _initialized;

    public event Action? FilterChanged;

    public ContentFilterService(IMetadataDatabase database, ILogger<ContentFilterService>? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            await ReloadAsync(SpotifyLibraryItemType.Ban, _bannedTracks, ct).ConfigureAwait(false);
            await ReloadAsync(SpotifyLibraryItemType.ArtistBan, _bannedArtists, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ContentFilterService init failed; ban lists will be empty until next sync.");
        }

        _logger?.LogInformation(
            "ContentFilterService initialized: {Tracks} hidden tracks, {Artists} blocked artists.",
            _bannedTracks.Count, _bannedArtists.Count);
    }

    public bool IsArtistBlocked(string? artistUri)
    {
        if (string.IsNullOrEmpty(artistUri)) return false;
        lock (_gate)
            return _bannedArtists.Contains(artistUri);
    }

    public bool IsTrackHidden(string? trackUri)
    {
        if (string.IsNullOrEmpty(trackUri)) return false;
        lock (_gate)
            return _bannedTracks.Contains(trackUri);
    }

    public Task SetArtistBlockedAsync(string artistUri, bool blocked, CancellationToken ct = default)
        => SetMembershipAsync(artistUri, blocked, _bannedArtists, SpotifyLibraryItemType.ArtistBan, ArtistPrefix, "artist ban", ct);

    public Task SetTrackHiddenAsync(string trackUri, bool hidden, CancellationToken ct = default)
        => SetMembershipAsync(trackUri, hidden, _bannedTracks, SpotifyLibraryItemType.Ban, TrackPrefix, "track hide", ct);

    private async Task SetMembershipAsync(
        string uri,
        bool inSet,
        HashSet<string> cache,
        SpotifyLibraryItemType itemType,
        string requiredPrefix,
        string displayName,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            _logger?.LogWarning("{Op}: refusing non-{Prefix} uri '{Uri}'.", displayName, requiredPrefix, uri);
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = inSet ? cache.Add(uri) : cache.Remove(uri);
        }
        if (!changed) return;

        FilterChanged?.Invoke();

        try
        {
            if (inSet)
            {
                var addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _database.AddToSpotifyLibraryAsync(uri, itemType, addedAt, ct).ConfigureAwait(false);
            }
            else
            {
                await _database.RemoveFromSpotifyLibraryAsync(uri, ct).ConfigureAwait(false);
            }

            // Outbox dispatch — LibraryOpDispatch routes Ban / ArtistBan to
            // their corresponding collection sets (added earlier in this
            // commit so this enqueue actually lands server-side). Payload
            // shape is `{"ItemType": <int>}` per LibraryOpPayload —
            // hand-rolled here so we don't depend on the internal
            // serializer context from Wavee.Core.
            var payload = $"{{\"ItemType\":{(int)itemType}}}";
            var handlerKind = inSet ? LibrarySaveHandler.Kind : LibraryRemoveHandler.Kind;
            await _database.EnqueueOutboxAsync(handlerKind, uri, payload, ct).ConfigureAwait(false);

            _logger?.LogInformation("{Op}: {Uri} → {State} (queued for sync).", displayName, uri, inSet ? "set" : "cleared");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "{Op} persist failed for {Uri}; reverting cache.", displayName, uri);
            lock (_gate)
            {
                if (inSet) cache.Remove(uri);
                else cache.Add(uri);
            }
            FilterChanged?.Invoke();
        }
    }

    private async Task ReloadAsync(SpotifyLibraryItemType type, HashSet<string> cache, CancellationToken ct)
    {
        cache.Clear();
        // Pull every row of the type from SQLite (paged to avoid surprises on
        // power-users with thousands of bans). Same access pattern as
        // TrackLikeService.LoadItemsAsync.
        const int pageSize = 500;
        var offset = 0;
        while (true)
        {
            var page = await _database.GetSpotifyLibraryItemsAsync(type, pageSize, offset, ct).ConfigureAwait(false);
            if (page.Count == 0) break;
            foreach (var e in page)
            {
                if (!string.IsNullOrEmpty(e.Uri)) cache.Add(e.Uri);
            }
            if (page.Count < pageSize) break;
            offset += page.Count;
        }
    }
}
