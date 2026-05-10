using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

public sealed class SearchService : ISearchService
{
    private readonly IPathfinderClient _pathfinder;

    public SearchService(IPathfinderClient pathfinder)
    {
        _pathfinder = pathfinder;
    }

    public async Task<List<SearchSuggestionItem>> GetRecentSearchesAsync(CancellationToken ct = default)
    {
        var response = await _pathfinder.GetRecentSearchesAsync(ct: ct);
        var items = response.Data?.RecentSearches?.RecentSearchesItems?.Items;
        if (items == null) return [];

        var results = new List<SearchSuggestionItem>();
        foreach (var item in items)
        {
            var mapped = MapRecentSearchItem(item.TypeName, item.Data);
            if (mapped != null) results.Add(mapped);
        }
        return results;
    }

    public async Task<List<SearchSuggestionItem>> GetSuggestionsAsync(string query, CancellationToken ct = default)
    {
        var response = await _pathfinder.GetSearchSuggestionsAsync(query, ct: ct);
        var hits = response.Data?.SearchV2?.TopResultsV2?.ItemsV2;
        if (hits == null) return [];

        var results = new List<SearchSuggestionItem>();
        foreach (var hit in hits)
        {
            var mapped = MapSuggestionItem(hit.Item?.TypeName, hit.Item?.Data);
            if (mapped != null) results.Add(mapped with { QueryText = query });
        }
        return results;
    }

    public Task<ChipPageResult> SearchTracksAsync(string query, int offset = 0, int limit = 20, CancellationToken ct = default)
        => RunChipAsync(_pathfinder.SearchTracksAsync(query, limit, offset, ct), offset, limit, r => r.TotalTracks);

    public Task<ChipPageResult> SearchArtistsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default)
        => RunChipAsync(_pathfinder.SearchAsync(query, Wavee.Core.Http.Pathfinder.SearchScope.Artists, limit, offset, ct), offset, limit, r => r.TotalArtists);

    public Task<ChipPageResult> SearchAlbumsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default)
        => RunChipAsync(_pathfinder.SearchAlbumsAsync(query, limit, offset, ct), offset, limit, r => r.TotalAlbums);

    public Task<ChipPageResult> SearchPlaylistsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default)
        => RunChipAsync(_pathfinder.SearchPlaylistsAsync(query, limit, offset, ct), offset, limit, r => r.TotalPlaylists);

    public Task<ChipPageResult> SearchUsersAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default)
        => RunChipAsync(_pathfinder.SearchUsersAsync(query, limit, offset, ct), offset, limit, r => r.TotalUsers);

    public Task<ChipPageResult> SearchGenresAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default)
        => RunChipAsync(_pathfinder.SearchGenresAsync(query, limit, offset, ct), offset, limit, r => r.TotalGenres);

    public async Task<ChipPageResult> SearchPodcastsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default)
    {
        // Desktop client fires both ops in parallel and merges shows + episodes
        // into a single chip view. Order: shows first, then episodes.
        var podcastsTask = _pathfinder.SearchPodcastsAsync(query, limit, offset, ct);
        var episodesTask = _pathfinder.SearchFullEpisodesAsync(query, limit, offset, ct);
        await Task.WhenAll(podcastsTask, episodesTask);

        var podcasts = podcastsTask.Result;
        var episodes = episodesTask.Result;

        var merged = new List<Wavee.Core.Http.Pathfinder.SearchResultItem>(podcasts.Items.Count + episodes.Items.Count);
        merged.AddRange(podcasts.Items);
        merged.AddRange(episodes.Items);

        var totalCount = podcasts.TotalPodcasts + episodes.TotalEpisodes;
        var hasMore = offset + podcasts.Items.Count < podcasts.TotalPodcasts
                      || offset + episodes.Items.Count < episodes.TotalEpisodes;

        return new ChipPageResult(merged, totalCount, hasMore);
    }

    private static async Task<ChipPageResult> RunChipAsync(
        Task<Wavee.Core.Http.Pathfinder.SearchResult> task,
        int offset,
        int limit,
        System.Func<Wavee.Core.Http.Pathfinder.SearchResult, int> totalSelector)
    {
        var result = await task;
        var total = totalSelector(result);
        var hasMore = offset + result.Items.Count < total;
        return new ChipPageResult(result.Items, total, hasMore);
    }

    private static SearchSuggestionItem? MapRecentSearchItem(string? typeName, JsonElement? data)
    {
        if (typeName == null || data == null) return null;
        var el = data.Value;

        return typeName switch
        {
            "ArtistResponseWrapper" => MapArtist(el),
            "TrackResponseWrapper" => MapTrack(el),
            "AlbumResponseWrapper" => MapAlbum(el),
            "PlaylistResponseWrapper" => MapPlaylist(el),
            _ => null
        };
    }

    private static SearchSuggestionItem? MapSuggestionItem(string? typeName, JsonElement? data)
    {
        if (typeName == null || data == null) return null;
        var el = data.Value;

        return typeName switch
        {
            "SearchAutoCompleteEntity" => new SearchSuggestionItem
            {
                Title = GetString(el, "text") ?? "",
                Uri = GetString(el, "uri") ?? "",
                Type = SearchSuggestionType.TextQuery
            },
            "ArtistResponseWrapper" => MapArtist(el),
            "TrackResponseWrapper" => MapTrack(el),
            "AlbumResponseWrapper" => MapAlbum(el),
            "PlaylistResponseWrapper" => MapPlaylist(el),
            "GenreResponseWrapper" => new SearchSuggestionItem
            {
                Title = GetString(el, "name") ?? "",
                ImageUrl = GetFirstImageUrl(el, "image"),
                Uri = GetString(el, "uri") ?? "",
                Subtitle = "Genre",
                Type = SearchSuggestionType.Genre
            },
            _ => null
        };
    }

    private static SearchSuggestionItem MapArtist(JsonElement el)
    {
        var name = GetNestedString(el, "profile", "name") ?? "";
        var uri = GetString(el, "uri") ?? "";
        var imageUrl = GetArtistImageUrl(el);

        return new SearchSuggestionItem
        {
            Title = name,
            Subtitle = "Artist",
            ImageUrl = imageUrl,
            Uri = uri,
            Type = SearchSuggestionType.Artist
        };
    }

    private static SearchSuggestionItem MapTrack(JsonElement el)
    {
        var name = GetString(el, "name") ?? "";
        var uri = GetString(el, "uri") ?? "";
        var imageUrl = GetAlbumCoverUrl(el);
        var artistName = GetFirstArtistName(el);

        return new SearchSuggestionItem
        {
            Title = name,
            Subtitle = artistName != null ? $"Song \u00B7 {artistName}" : "Song",
            ImageUrl = imageUrl,
            Uri = uri,
            Type = SearchSuggestionType.Track
        };
    }

    private static SearchSuggestionItem MapAlbum(JsonElement el)
    {
        var name = GetString(el, "name") ?? "";
        var uri = GetString(el, "uri") ?? "";
        var imageUrl = GetAlbumCoverUrl(el);
        var artistName = GetFirstArtistName(el);

        return new SearchSuggestionItem
        {
            Title = name,
            Subtitle = artistName != null ? $"Album \u00B7 {artistName}" : "Album",
            ImageUrl = imageUrl,
            Uri = uri,
            Type = SearchSuggestionType.Album
        };
    }

    private static SearchSuggestionItem MapPlaylist(JsonElement el)
    {
        var name = GetString(el, "name") ?? "";
        var uri = GetString(el, "uri") ?? "";
        var ownerName = GetPlaylistOwner(el);
        var imageUrl = GetPlaylistImageUrl(el);

        return new SearchSuggestionItem
        {
            Title = name,
            Subtitle = ownerName != null ? $"Playlist \u00B7 {ownerName}" : "Playlist",
            ImageUrl = imageUrl,
            Uri = uri,
            Type = SearchSuggestionType.Playlist
        };
    }

    // ── JSON helpers ──

    private static string? GetString(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() : null;
    }

    private static string? GetNestedString(JsonElement el, string prop1, string prop2)
    {
        if (el.TryGetProperty(prop1, out var nested) && nested.ValueKind == JsonValueKind.Object)
            return GetString(nested, prop2);
        return null;
    }

    private static string? GetArtistImageUrl(JsonElement el)
    {
        // visuals.avatarImage.sources[0].url
        if (el.TryGetProperty("visuals", out var visuals) &&
            visuals.TryGetProperty("avatarImage", out var avatar) &&
            avatar.TryGetProperty("sources", out var sources) &&
            sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
        {
            return GetString(sources[0], "url");
        }
        return null;
    }

    private static string? GetAlbumCoverUrl(JsonElement el)
    {
        // albumOfTrack.coverArt.sources[0].url  OR  coverArt.sources[0].url
        JsonElement? coverArt = null;
        if (el.TryGetProperty("albumOfTrack", out var album) &&
            album.TryGetProperty("coverArt", out var ca))
        {
            coverArt = ca;
        }
        else if (el.TryGetProperty("coverArt", out var directCa))
        {
            coverArt = directCa;
        }

        if (coverArt?.TryGetProperty("sources", out var sources) == true &&
            sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
        {
            return GetString(sources[0], "url");
        }
        return null;
    }

    private static string? GetFirstArtistName(JsonElement el)
    {
        // artists.items[0].profile.name
        if (el.TryGetProperty("artists", out var artists) &&
            artists.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            return GetNestedString(items[0], "profile", "name");
        }
        return null;
    }

    private static string? GetPlaylistOwner(JsonElement el)
    {
        // ownerV2.data.name
        if (el.TryGetProperty("ownerV2", out var owner) &&
            owner.TryGetProperty("data", out var data))
        {
            return GetString(data, "name");
        }
        return null;
    }

    private static string? GetPlaylistImageUrl(JsonElement el)
    {
        // images.items[0].sources[0].url
        if (el.TryGetProperty("images", out var images) &&
            images.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            var first = items[0];
            if (first.TryGetProperty("sources", out var sources) &&
                sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
            {
                return GetString(sources[0], "url");
            }
        }
        return null;
    }

    private static string? GetFirstImageUrl(JsonElement el, string prop)
    {
        // {prop}.sources[0].url
        if (el.TryGetProperty(prop, out var img) &&
            img.TryGetProperty("sources", out var sources) &&
            sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
        {
            return GetString(sources[0], "url");
        }
        return null;
    }
}
