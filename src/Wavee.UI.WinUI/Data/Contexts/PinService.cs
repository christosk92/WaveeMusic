using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.Services.Infra;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IPinService"/>. Reads from <see cref="IMetadataDatabase"/>
/// (<c>SpotifyLibraryItemType.YlPin</c> rows) and mutates via
/// <see cref="ISpotifyLibraryService"/>. Publishes <see cref="ChangeScope.Library"/>
/// on every successful Pin/Unpin.
/// </summary>
public sealed class PinService : IPinService
{
    private readonly IMetadataDatabase _database;
    private readonly ISpotifyLibraryService? _spotifyLibraryService;
    private readonly IChangeBus _changeBus;
    private readonly ILogger<PinService>? _logger;

    private readonly HashSet<string> _pinnedUris = new(StringComparer.Ordinal);
    private readonly object _pinnedUrisGate = new();

    public PinService(
        IMetadataDatabase database,
        IChangeBus changeBus,
        ISpotifyLibraryService? spotifyLibraryService = null,
        ILogger<PinService>? logger = null)
    {
        _database = database;
        _changeBus = changeBus;
        _spotifyLibraryService = spotifyLibraryService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PinnedItemDto>> GetPinnedItemsAsync(CancellationToken ct = default)
    {
        var entities = await _database
            .GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.YlPin, int.MaxValue, 0, ct)
            .ConfigureAwait(false);

        var results = new List<PinnedItemDto>(entities.Count);
        foreach (var e in entities)
        {
            // Canonical pseudo-URIs first — Spotify uses these as "pin pointers"
            // to its own library destinations. The entity row's Title is the URI
            // string (placeholder seated by FetchMixedTypeMetadataAsync), so the
            // title has to be synthesized here. Mirrors ContentCard.xaml.cs:1447–1464.
            if (e.Uri == "spotify:collection"
                || (e.Uri.StartsWith("spotify:user:", StringComparison.Ordinal)
                    && e.Uri.EndsWith(":collection", StringComparison.Ordinal)))
            {
                results.Add(new PinnedItemDto
                {
                    Uri = e.Uri,
                    Title = Wavee.UI.WinUI.Services.AppLocalization.GetString("Shell_SidebarLikedSongs"),
                    ImageUrl = null,
                    AddedAtUnixSeconds = e.AddedAt?.ToUnixTimeSeconds() ?? 0,
                    Kind = PinnedItemKind.LikedSongs
                });
                continue;
            }
            if (e.Uri == "spotify:collection:your-episodes")
            {
                results.Add(new PinnedItemDto
                {
                    Uri = e.Uri,
                    Title = "Your Episodes",
                    ImageUrl = null,
                    AddedAtUnixSeconds = e.AddedAt?.ToUnixTimeSeconds() ?? 0,
                    Kind = PinnedItemKind.YourEpisodes
                });
                continue;
            }

            PinnedItemKind kind;
            if (e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) kind = PinnedItemKind.Playlist;
            else if (e.Uri.StartsWith("spotify:album:", StringComparison.Ordinal)) kind = PinnedItemKind.Album;
            else if (e.Uri.StartsWith("spotify:artist:", StringComparison.Ordinal)) kind = PinnedItemKind.Artist;
            else if (e.Uri.StartsWith("spotify:show:", StringComparison.Ordinal)) kind = PinnedItemKind.Show;
            else continue;

            results.Add(new PinnedItemDto
            {
                Uri = e.Uri,
                Title = !string.IsNullOrWhiteSpace(e.Title) ? e.Title! : e.Uri,
                ImageUrl = e.ImageUrl,
                AddedAtUnixSeconds = e.AddedAt?.ToUnixTimeSeconds() ?? 0,
                Kind = kind
            });
        }

        results.Sort(static (a, b) => b.AddedAtUnixSeconds.CompareTo(a.AddedAtUnixSeconds));

        lock (_pinnedUrisGate)
        {
            _pinnedUris.Clear();
            foreach (var item in results)
                _pinnedUris.Add(item.Uri);
        }

        return results;
    }

    public async Task<bool> PinAsync(string uri, CancellationToken ct = default)
    {
        if (_spotifyLibraryService is null) return false;
        var ok = await _spotifyLibraryService.PinToSidebarAsync(uri, ct).ConfigureAwait(false);
        if (ok)
        {
            lock (_pinnedUrisGate) _pinnedUris.Add(uri);
            _changeBus.Publish(ChangeScope.Library);
        }
        return ok;
    }

    public async Task<bool> UnpinAsync(string uri, CancellationToken ct = default)
    {
        if (_spotifyLibraryService is null) return false;
        var ok = await _spotifyLibraryService.UnpinFromSidebarAsync(uri, ct).ConfigureAwait(false);
        if (ok)
        {
            lock (_pinnedUrisGate) _pinnedUris.Remove(uri);
            _changeBus.Publish(ChangeScope.Library);
        }
        return ok;
    }

    public bool IsPinned(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        lock (_pinnedUrisGate) return _pinnedUris.Contains(uri);
    }
}
