using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Holds the user's Recently Played list. As of 2026-04-28 the list is sourced
/// from the Home GraphQL response (HomeRecentlyPlayedSectionData) — see
/// HomeViewModel where each Home parse result is dispatched here via
/// <see cref="ApplyHomeRecents"/>. The legacy SpClient.GetRecentlyPlayedAsync
/// REST endpoint is no longer called from this service (kept in SpClient as
/// dead code per memory:feedback_keep_api_wrappers).
///
/// On top of the Home-sourced list, this service still listens to live
/// playback-context-changed messages and bumps a freshly-started context to
/// position 0 so the carousel reorders without waiting for the next Home
/// re-fetch. New contexts not in the last Home response are placeholder-
/// rendered immediately and enriched via Pathfinder in the background.
/// </summary>
public sealed class RecentlyPlayedService : IDisposable
{
    private readonly ISession _session;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;

    private readonly List<HomeSectionItem> _items = [];
    private bool _disposed;

    // Keep the recently-played list bounded so a long session of starting many
    // distinct contexts doesn't grow it indefinitely. The Home GraphQL
    // response usually returns 10; the cap matters for the live-reorder path
    // (OnPlaybackContextChanged) which prepends fresh contexts.
    private const int MaxItems = 50;

    public event Action? ItemsChanged;

    public IReadOnlyList<HomeSectionItem> Items => _items;

    public RecentlyPlayedService(
        ISession session,
        IPlaybackStateService playbackStateService,
        IMessenger messenger,
        DispatcherQueue dispatcherQueue,
        ILogger<RecentlyPlayedService>? logger = null)
    {
        _session = session;
        _playbackStateService = playbackStateService;
        _messenger = messenger;
        _dispatcherQueue = dispatcherQueue;
        _logger = logger;

        _messenger.Register<PlaybackContextChangedMessage>(this, OnPlaybackContextChanged);
    }

    /// <summary>
    /// Replaces the current Recents list with items extracted from the
    /// Home GraphQL response (HomeRecentlyPlayedSectionData). Items arrive
    /// pre-mapped to <see cref="HomeSectionItem"/> by the Home parser; no
    /// further enrichment / dedup / filtering is required here (the Home
    /// response handles that server-side and is strictly richer than the
    /// legacy /recently-played/v3 REST endpoint).
    ///
    /// If a track is currently playing in a context that matches one of the
    /// items, that item is bumped to position 0 so the live "now playing"
    /// always sits at the head of the carousel.
    /// </summary>
    public void ApplyHomeRecents(IReadOnlyList<HomeSectionItem>? items)
    {
        if (_disposed) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;

            _items.Clear();
            if (items != null && items.Count > 0)
                _items.AddRange(items);

            // Live "now playing" on top
            var currentContextUri = _playbackStateService.CurrentContext?.ContextUri;
            if (!string.IsNullOrEmpty(currentContextUri))
            {
                var playingItem = _items.FirstOrDefault(i =>
                    string.Equals(i.Uri, currentContextUri, StringComparison.OrdinalIgnoreCase));
                if (playingItem != null && _items[0] != playingItem)
                {
                    _items.Remove(playingItem);
                    _items.Insert(0, playingItem);
                }
            }

            ItemsChanged?.Invoke();

            // Liked Songs "stack of 3" thumbnails: the Home parser dropped the
            // 3 track URIs in here from group_metadata; resolve them to album
            // cover URLs and write them back so the LikedSongsRecentCard's
            // Composition sprites can pick up the brushes. Fire-and-forget —
            // the foreground heart tile renders immediately; the fan fades in
            // when the metadata batch returns.
            foreach (var item in _items)
            {
                if (item.IsRecentlySaved
                    && item.RecentlyAddedThumbnailUris != null
                    && item.RecentlyAddedThumbnailUris.Count > 0
                    && string.IsNullOrEmpty(item.RecentlyAddedThumbnail1Url))
                {
                    _ = ResolveLikedSongsThumbnailsAsync(item);
                }
            }
        });
    }

    /// <summary>
    /// Batch-fetches the album-cover URL for each of the (up to 3) recently-
    /// added track URIs and writes them back to the
    /// <see cref="HomeSectionItem.RecentlyAddedThumbnail1Url"/> /
    /// <see cref="HomeSectionItem.RecentlyAddedThumbnail2Url"/> /
    /// <see cref="HomeSectionItem.RecentlyAddedThumbnail3Url"/> properties.
    /// Goes through the existing <see cref="TrackImagesEnrichmentRequest"/>
    /// path — which checks the SQLite cache first and only hits the
    /// extended-metadata endpoint for misses, batched up to 500 per request.
    /// </summary>
    private async Task ResolveLikedSongsThumbnailsAsync(HomeSectionItem item)
    {
        try
        {
            var uris = item.RecentlyAddedThumbnailUris;
            if (uris == null || uris.Count == 0)
            {
                _logger?.LogDebug("[recents-thumb] {Uri}: no URIs in RecentlyAddedThumbnailUris — skipping resolve",
                    item.Uri);
                return;
            }

            _logger?.LogDebug("[recents-thumb] {Uri}: resolving {Count} URIs: {Uris}",
                item.Uri, uris.Count, string.Join(", ", uris));

            var request = _messenger.Send(new TrackImagesEnrichmentRequest
            {
                TrackUris = uris,
                CancellationToken = CancellationToken.None
            });
            if (!request.HasReceivedResponse)
            {
                _logger?.LogDebug("[recents-thumb] {Uri}: TrackImagesEnrichmentRequest got no handler response",
                    item.Uri);
                return;
            }
            var resolved = await request.Response.ConfigureAwait(false);

            _logger?.LogDebug("[recents-thumb] {Uri}: resolver returned {Count} entries: {Pairs}",
                item.Uri, resolved.Count,
                string.Join(", ", resolved.Select(kv => $"{kv.Key}={kv.Value ?? "<null>"}")));

            // Map to HTTPS — the cache returns spotify:image:{hex} form which
            // BitmapImage / LoadedImageSurface can't load directly. Order by
            // the original URI list so the fan keeps the same most-recent-
            // first-on-top order Spotify intended.
            string?[] httpsUrls = new string?[3];
            for (var i = 0; i < uris.Count && i < 3; i++)
            {
                if (resolved.TryGetValue(uris[i], out var raw) && !string.IsNullOrEmpty(raw))
                    httpsUrls[i] = SpotifyImageHelper.ToHttpsUrl(raw) ?? raw;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                item.RecentlyAddedThumbnail1Url = httpsUrls[0];
                item.RecentlyAddedThumbnail2Url = httpsUrls[1];
                item.RecentlyAddedThumbnail3Url = httpsUrls[2];
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve Liked Songs thumbnails for {Count} URIs",
                item.RecentlyAddedThumbnailUris?.Count ?? 0);
        }
    }

    private void OnPlaybackContextChanged(object recipient, PlaybackContextChangedMessage message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var context = message.Value;
            if (context == null || string.IsNullOrEmpty(context.ContextUri))
            {
                ItemsChanged?.Invoke();
                return;
            }

            // Find or create the item and move to top
            var existing = _items.FirstOrDefault(i =>
                string.Equals(i.Uri, context.ContextUri, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _items.Remove(existing);
                _items.Insert(0, existing);
            }
            else
            {
                // Build from context info (placeholder — will be enriched below)
                var newItem = new HomeSectionItem
                {
                    Uri = context.ContextUri,
                    Title = context.Name ?? GetFallbackTitle(context.ContextUri),
                    ImageUrl = context.ImageUrl,
                    ContentType = MapContextType(context.Type)
                };
                _items.Insert(0, newItem);

                // Resolve full metadata if the placeholder is incomplete
                if (string.IsNullOrEmpty(context.Name) || string.IsNullOrEmpty(context.ImageUrl))
                {
                    _ = ResolveEntityMetadataAsync(newItem);
                }
            }

            // Cap the list so playing many distinct contexts in a session
            // doesn't grow it indefinitely. Drop oldest from the tail.
            while (_items.Count > MaxItems)
                _items.RemoveAt(_items.Count - 1);

            ItemsChanged?.Invoke();
        });
    }

    /// <summary>
    /// Resolves metadata for a placeholder item (created by the live-reorder
    /// path when a brand-new context starts playing) via Pathfinder and
    /// updates it in-place. Only called for items the Home response didn't
    /// already cover.
    /// </summary>
    private async Task ResolveEntityMetadataAsync(HomeSectionItem placeholder)
    {
        try
        {
            var entities = await Task.Run(async () =>
                await _session.Pathfinder.FetchEntitiesForRecentlyPlayedAsync([placeholder.Uri!]).ConfigureAwait(false)).ConfigureAwait(false);
            if (entities?.Data?.Lookup == null || entities.Data.Lookup.Count == 0)
                return;

            var entry = entities.Data.Lookup[0];
            var uri = entry.Uri;
            if (string.IsNullOrEmpty(uri) && entry.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } el
                && el.TryGetProperty("uri", out var uriProp))
            {
                uri = uriProp.GetString();
            }

            if (string.IsNullOrEmpty(uri)) return;

            var resolved = MapPathfinderEntry(uri, entry);
            if (resolved == null) return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                var index = _items.IndexOf(placeholder);
                if (index >= 0)
                {
                    _items[index] = resolved;
                    ItemsChanged?.Invoke();
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve metadata for {Uri}", placeholder.Uri);
        }
    }

    // Pathfinder lookup → HomeSectionItem. Used only for live-reorder
    // placeholder enrichment (above), not for the main list which comes
    // pre-mapped from the Home parser.
    private static HomeSectionItem? MapPathfinderEntry(string uri, RecentlyPlayedEntityEntry entity)
    {
        if (uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
        {
            return new HomeSectionItem
            {
                Uri = uri,
                Title = "Liked Songs",
                ContentType = HomeContentType.Playlist,
                ColorHex = "#4B2A8A",
                PlaceholderGlyph = FluentGlyphs.HeartFilled
            };
        }

        return entity.TypeName switch
        {
            "ArtistResponseWrapper" => MapArtistEntity(uri, entity),
            "PlaylistResponseWrapper" => MapPlaylistEntity(uri, entity),
            "AlbumResponseWrapper" => MapAlbumEntity(uri, entity),
            _ => InferContentType(uri) switch
            {
                HomeContentType.Album => MapAlbumEntity(uri, entity),
                HomeContentType.Artist => MapArtistEntity(uri, entity),
                HomeContentType.Playlist => MapPlaylistEntity(uri, entity),
                _ => new HomeSectionItem { Uri = uri, Title = GetFallbackTitle(uri), ContentType = InferContentType(uri) }
            }
        };
    }

    private static HomeSectionItem? MapArtistEntity(string uri, RecentlyPlayedEntityEntry entity)
    {
        var data = entity.GetArtistData();
        if (data == null) return new HomeSectionItem { Uri = uri, Title = GetFallbackTitle(uri), ContentType = HomeContentType.Artist };

        var imageUrl = data.Visuals?.AvatarImage?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Profile?.Name,
            Subtitle = "Artist",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Artist,
            ColorHex = data.Visuals?.AvatarImage?.ExtractedColors?.ColorDark?.Hex
        };
    }

    private static HomeSectionItem? MapPlaylistEntity(string uri, RecentlyPlayedEntityEntry entity)
    {
        var data = entity.GetPlaylistData();
        if (data == null) return new HomeSectionItem { Uri = uri, Title = GetFallbackTitle(uri), ContentType = HomeContentType.Playlist };

        var imageUrl = data.Images?.Items?.FirstOrDefault()?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = data.OwnerV2?.Data?.Name,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Playlist,
            ColorHex = data.Images?.Items?.FirstOrDefault()?.ExtractedColors?.ColorDark?.Hex
        };
    }

    private static HomeSectionItem? MapAlbumEntity(string uri, RecentlyPlayedEntityEntry entity)
    {
        var data = entity.GetAlbumData();
        if (data == null) return new HomeSectionItem { Uri = uri, Title = GetFallbackTitle(uri), ContentType = HomeContentType.Album };

        var imageUrl = data.CoverArt?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var artistName = data.Artists?.Items?.FirstOrDefault()?.Profile?.Name;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = artistName ?? "Album",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Album,
            ColorHex = data.CoverArt?.ExtractedColors?.ColorDark?.Hex
        };
    }

    private static string GetFallbackTitle(string uri)
    {
        if (uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
            return "Liked Songs";

        var parts = uri.Split(':');
        return parts.Length >= 2 ? parts[1] switch
        {
            "artist" => "Artist",
            "album" => "Album",
            "playlist" => "Playlist",
            _ => "Unknown"
        } : "Unknown";
    }

    private static HomeContentType InferContentType(string uri)
    {
        if (uri.Contains(":artist:", StringComparison.OrdinalIgnoreCase)) return HomeContentType.Artist;
        if (uri.Contains(":album:", StringComparison.OrdinalIgnoreCase)) return HomeContentType.Album;
        if (uri.Contains(":playlist:", StringComparison.OrdinalIgnoreCase)) return HomeContentType.Playlist;
        if (uri.Contains(":collection", StringComparison.OrdinalIgnoreCase)) return HomeContentType.Playlist;
        return HomeContentType.Unknown;
    }

    private static HomeContentType MapContextType(PlaybackContextType type) => type switch
    {
        PlaybackContextType.Album => HomeContentType.Album,
        PlaybackContextType.Artist => HomeContentType.Artist,
        PlaybackContextType.Playlist => HomeContentType.Playlist,
        PlaybackContextType.LikedSongs => HomeContentType.Playlist,
        _ => HomeContentType.Unknown
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messenger.UnregisterAll(this);
    }
}
