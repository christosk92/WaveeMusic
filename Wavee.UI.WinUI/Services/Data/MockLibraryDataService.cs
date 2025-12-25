using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Services.Data;

/// <summary>
/// Mock implementation of ILibraryDataService for demo/offline mode.
/// </summary>
public sealed class MockLibraryDataService : ILibraryDataService
{
    private readonly List<LibraryItemDto> _mockItems;
    private readonly List<PlaylistSummaryDto> _mockPlaylists;

    public MockLibraryDataService()
    {
        _mockItems = GenerateMockItems();
        _mockPlaylists = GenerateMockPlaylists();
    }

    public Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        var stats = new LibraryStatsDto
        {
            AlbumCount = 42,
            ArtistCount = 15,
            LikedSongsCount = _mockItems.Count,
            PlaylistCount = _mockPlaylists.Count,
            TotalPlayCount = _mockItems.Sum(x => x.PlayCount)
        };
        return Task.FromResult(stats);
    }

    public Task<IReadOnlyList<LibraryItemDto>> GetAllItemsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LibraryItemDto>>(_mockItems);
    }

    public Task<IReadOnlyList<LibraryItemDto>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default)
    {
        var recent = _mockItems
            .Where(x => x.LastPlayedAt.HasValue)
            .OrderByDescending(x => x.LastPlayedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<LibraryItemDto>>(recent);
    }

    public Task<IReadOnlyList<PlaylistSummaryDto>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PlaylistSummaryDto>>(_mockPlaylists);
    }

    private static List<LibraryItemDto> GenerateMockItems()
    {
        var now = DateTimeOffset.UtcNow;
        var artists = new[] { "The Beatles", "Pink Floyd", "Led Zeppelin", "Queen", "David Bowie" };
        var albums = new[] { "Abbey Road", "The Dark Side of the Moon", "Led Zeppelin IV", "A Night at the Opera", "Hunky Dory" };

        var items = new List<LibraryItemDto>();
        for (int i = 1; i <= 50; i++)
        {
            items.Add(new LibraryItemDto
            {
                Id = $"spotify:track:{i}",
                Title = $"Track {i}",
                Artist = artists[(i - 1) % artists.Length],
                Album = albums[(i - 1) % albums.Length],
                ImageUrl = null,
                Duration = TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(i * 7 % 120),
                PlayCount = (50 - i) * 2,
                LastPlayedAt = i <= 20 ? now.AddHours(-i * 3) : null,
                AddedAt = now.AddDays(-i)
            });
        }
        return items;
    }

    private static List<PlaylistSummaryDto> GenerateMockPlaylists()
    {
        return
        [
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:1",
                Name = "Favorites",
                TrackCount = 25,
                IsOwner = true
            },
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:2",
                Name = "Chill Vibes",
                TrackCount = 42,
                IsOwner = true
            },
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:3",
                Name = "Workout Mix",
                TrackCount = 18,
                IsOwner = true
            },
            new PlaylistSummaryDto
            {
                Id = "spotify:playlist:4",
                Name = "Discover Weekly",
                TrackCount = 30,
                IsOwner = false
            }
        ];
    }
}
