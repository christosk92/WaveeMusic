using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

public sealed class RecentlyPlayedService : IDisposable
{
    private readonly ISession _session;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;

    private readonly List<HomeSectionItem> _items = [];
    private bool _disposed;

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

    public async Task LoadAsync()
    {
        try
        {
            var userId = _session.GetUserData()?.Username;
            if (string.IsNullOrEmpty(userId)) return;

            // 1. Fetch recently played contexts
            var recentResponse = await Task.Run(async () => await _session.SpClient.GetRecentlyPlayedAsync(userId).ConfigureAwait(false)).ConfigureAwait(false);
            var contexts = recentResponse.PlayContexts;
            if (contexts == null || contexts.Count == 0) return;

            // 2. Filter out episodes/shows, deduplicate collection URIs
            var seenCollection = false;
            var filtered = new List<RecentlyPlayedContext>();
            foreach (var c in contexts)
            {
                if (string.IsNullOrEmpty(c.Uri)) continue;
                if (c.Uri.StartsWith("spotify:episode:", StringComparison.Ordinal)
                    || c.Uri.StartsWith("spotify:show:", StringComparison.Ordinal))
                    continue;

                // Deduplicate Liked Songs (can appear as both user:X:collection and collection:tracks)
                if (c.Uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
                {
                    if (seenCollection) continue;
                    seenCollection = true;
                }

                filtered.Add(c);
            }

            if (filtered.Count == 0) return;

            // 3. Resolve metadata via Pathfinder
            var uris = filtered.Select(c => c.Uri!).ToList();
            RecentlyPlayedEntitiesResponse? entities = null;
            try
            {
                entities = await Task.Run(async () =>
                    await _session.Pathfinder.FetchEntitiesForRecentlyPlayedAsync(uris).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch entities for recently played, using URIs only");
            }

            // 4. Build lookup from entities
            // The Pathfinder wrapper puts __typename at the top level but uri inside the nested data object.
            // Fall back to extracting uri from the inner JSON when the wrapper-level uri is null.
            var entityLookup = new Dictionary<string, RecentlyPlayedEntityEntry>(StringComparer.OrdinalIgnoreCase);
            if (entities?.Data?.Lookup != null)
            {
                foreach (var entry in entities.Data.Lookup)
                {
                    var uri = entry.Uri;
                    if (string.IsNullOrEmpty(uri)
                        && entry.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } el
                        && el.TryGetProperty("uri", out var uriProp))
                    {
                        uri = uriProp.GetString();
                    }

                    if (!string.IsNullOrEmpty(uri))
                        entityLookup[uri] = entry;
                }
            }

            // 5. Merge into HomeSectionItem list, sorted by lastPlayedTime desc
            var items = new List<HomeSectionItem>();
            foreach (var ctx in filtered.OrderByDescending(c => c.LastPlayedTime))
            {
                var item = MapToHomeSectionItem(ctx.Uri!, entityLookup);
                if (item != null)
                    items.Add(item);
            }

            // 6. Check if currently playing context matches any item
            var currentContextUri = _playbackStateService.CurrentContext?.ContextUri;
            if (!string.IsNullOrEmpty(currentContextUri))
            {
                var playingItem = items.FirstOrDefault(i =>
                    string.Equals(i.Uri, currentContextUri, StringComparison.OrdinalIgnoreCase));
                if (playingItem != null)
                {
                    items.Remove(playingItem);
                    items.Insert(0, playingItem);
                }
            }

            _items.Clear();
            _items.AddRange(items);
            ItemsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load recently played");
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

            ItemsChanged?.Invoke();
        });
    }

    /// <summary>
    /// Resolves metadata for a placeholder item via Pathfinder and updates it in-place.
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

            var entityLookup = new Dictionary<string, RecentlyPlayedEntityEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = entry
            };

            var resolved = MapToHomeSectionItem(uri, entityLookup);
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

    private static HomeSectionItem? MapToHomeSectionItem(
        string uri,
        Dictionary<string, RecentlyPlayedEntityEntry> entityLookup)
    {
        // Special case: Liked Songs collection
        if (uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
        {
            return new HomeSectionItem
            {
                Uri = uri,
                Title = "Liked Songs",
                ContentType = HomeContentType.Playlist,
                ColorHex = "#4B2A8A",
                PlaceholderGlyph = "\uEB52" // filled heart
            };
        }

        if (entityLookup.TryGetValue(uri, out var entity))
        {
            // Try type-based dispatch first, then fall back to URI-based inference
            var result = entity.TypeName switch
            {
                "ArtistResponseWrapper" => MapArtistEntity(uri, entity),
                "PlaylistResponseWrapper" => MapPlaylistEntity(uri, entity),
                "AlbumResponseWrapper" => MapAlbumEntity(uri, entity),
                _ => null
            };

            // If TypeName didn't match, try URI-based inference with the entity data
            if (result == null)
            {
                var contentType = InferContentType(uri);
                result = contentType switch
                {
                    HomeContentType.Album => MapAlbumEntity(uri, entity),
                    HomeContentType.Artist => MapArtistEntity(uri, entity),
                    HomeContentType.Playlist => MapPlaylistEntity(uri, entity),
                    _ => new HomeSectionItem { Uri = uri, Title = GetFallbackTitle(uri), ContentType = contentType }
                };
            }

            return result;
        }

        // No metadata — use URI-derived fallback
        return new HomeSectionItem
        {
            Uri = uri,
            Title = GetFallbackTitle(uri),
            ContentType = InferContentType(uri)
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
