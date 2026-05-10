using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Root response from Pathfinder search query.
/// </summary>
public sealed class PathfinderSearchResponse
{
    [JsonPropertyName("data")]
    public SearchData? Data { get; init; }
}

/// <summary>
/// Search data container.
/// </summary>
public sealed class SearchData
{
    [JsonPropertyName("searchV2")]
    public SearchV2? SearchV2 { get; init; }
}

/// <summary>
/// Main search results container.
/// </summary>
public sealed class SearchV2
{
    [JsonPropertyName("tracksV2")]
    public TrackPage? TracksV2 { get; init; }

    [JsonPropertyName("artists")]
    public ArtistPage? Artists { get; init; }

    [JsonPropertyName("albumsV2")]
    public AlbumPage? AlbumsV2 { get; init; }

    [JsonPropertyName("playlists")]
    public PlaylistPage? Playlists { get; init; }

    [JsonPropertyName("topResultsV2")]
    public TopResults? TopResultsV2 { get; init; }

    // Podcasts/Users/Genres/Episodes have dedicated chip ops that return full pages,
    // so they're modeled as full *Page types. The same fields appear in the
    // searchTopResultsList response carrying only `totalCount` — Items + PagingInfo
    // simply deserialize as null in that case.
    [JsonPropertyName("podcasts")]
    public PodcastPage? Podcasts { get; init; }

    [JsonPropertyName("users")]
    public UserPage? Users { get; init; }

    [JsonPropertyName("genres")]
    public GenrePage? Genres { get; init; }

    [JsonPropertyName("episodes")]
    public EpisodePage? Episodes { get; init; }

    // Audiobooks + Authors don't have captured chip ops yet — keep as count-only
    // until we have hashes to fire dedicated chip queries.
    [JsonPropertyName("audiobooks")]
    public SectionTotalCount? Audiobooks { get; init; }

    [JsonPropertyName("authors")]
    public SectionTotalCount? Authors { get; init; }
}

/// <summary>
/// Minimal section page that carries only a total count — used for sections we
/// don't render full pages for yet but want to expose chip badges for.
/// </summary>
public sealed class SectionTotalCount
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

/// <summary>
/// Pagination info shared by every section page — drives "load more" decisions.
/// <c>NextOffset == null</c> means the section is fully fetched.
/// </summary>
public sealed class PagingInfo
{
    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }
}

#region Podcasts (shows)

public sealed class PodcastPage
{
    [JsonPropertyName("items")]
    public List<PodcastResponseWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

public sealed class PodcastResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public PodcastData? Data { get; init; }
}

public sealed class PodcastData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("coverArt")]
    public CoverArt? CoverArt { get; init; }

    [JsonPropertyName("publisher")]
    public PodcastPublisher? Publisher { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }
}

public sealed class PodcastPublisher
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

#endregion

#region Users

public sealed class UserPage
{
    [JsonPropertyName("items")]
    public List<UserResponseWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

public sealed class UserResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public UserData? Data { get; init; }
}

public sealed class UserData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("avatar")]
    public CoverArt? Avatar { get; init; }
}

#endregion

#region Genres

public sealed class GenrePage
{
    [JsonPropertyName("items")]
    public List<GenreResponseWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

public sealed class GenreResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public GenreData? Data { get; init; }
}

public sealed class GenreData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("image")]
    public CoverArt? Image { get; init; }
}

#endregion

#region Episodes

public sealed class EpisodePage
{
    [JsonPropertyName("items")]
    public List<EpisodeResponseWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

public sealed class EpisodeResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public EpisodeData? Data { get; init; }
}

public sealed class EpisodeData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("coverArt")]
    public CoverArt? CoverArt { get; init; }

    [JsonPropertyName("duration")]
    public Duration? Duration { get; init; }

    [JsonPropertyName("podcastV2")]
    public PodcastResponseWrapper? PodcastV2 { get; init; }
}

#endregion

#region Tracks

/// <summary>
/// Page of track results.
/// </summary>
public sealed class TrackPage
{
    [JsonPropertyName("items")]
    public List<TrackWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

/// <summary>
/// Wrapper for track response.
/// </summary>
public sealed class TrackWrapper
{
    [JsonPropertyName("item")]
    public TrackItemWrapper? Item { get; init; }

    [JsonPropertyName("matchedFields")]
    public List<string>? MatchedFields { get; init; }
}

/// <summary>
/// Track item wrapper containing actual data.
/// </summary>
public sealed class TrackItemWrapper
{
    [JsonPropertyName("data")]
    public TrackData? Data { get; init; }
}

/// <summary>
/// Track data.
/// </summary>
public sealed class TrackData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("duration")]
    public Duration? Duration { get; init; }

    [JsonPropertyName("albumOfTrack")]
    public AlbumOfTrack? AlbumOfTrack { get; init; }

    [JsonPropertyName("artists")]
    public ArtistList? Artists { get; init; }

    [JsonPropertyName("playability")]
    public Playability? Playability { get; init; }
}

/// <summary>
/// Album of a track (simplified).
/// </summary>
public sealed class AlbumOfTrack
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("coverArt")]
    public CoverArt? CoverArt { get; init; }
}

/// <summary>
/// Duration in milliseconds.
/// </summary>
public sealed class Duration
{
    [JsonPropertyName("totalMilliseconds")]
    public long TotalMilliseconds { get; init; }
}

#endregion

#region Artists

/// <summary>
/// Page of artist results.
/// </summary>
public sealed class ArtistPage
{
    [JsonPropertyName("items")]
    public List<ArtistResponseWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

/// <summary>
/// Artist response wrapper.
/// </summary>
public sealed class ArtistResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public ArtistData? Data { get; init; }
}

/// <summary>
/// Artist data.
/// </summary>
public sealed class ArtistData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public ArtistProfile? Profile { get; init; }

    [JsonPropertyName("visuals")]
    public ArtistVisuals? Visuals { get; init; }
}

/// <summary>
/// Artist profile.
/// </summary>
public sealed class ArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }
}

/// <summary>
/// Artist visuals (images).
/// </summary>
public sealed class ArtistVisuals
{
    [JsonPropertyName("avatarImage")]
    public ImageWithColors? AvatarImage { get; init; }
}

/// <summary>
/// List of artists.
/// </summary>
public sealed class ArtistList
{
    [JsonPropertyName("items")]
    public List<ArtistItem>? Items { get; init; }
}

/// <summary>
/// Artist item in a list.
/// </summary>
public sealed class ArtistItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public ArtistProfile? Profile { get; init; }
}

#endregion

#region Albums

/// <summary>
/// Page of album results.
/// </summary>
public sealed class AlbumPage
{
    [JsonPropertyName("items")]
    public List<AlbumResponseWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

/// <summary>
/// Album response wrapper.
/// </summary>
public sealed class AlbumResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public AlbumData? Data { get; init; }
}

/// <summary>
/// Album data.
/// </summary>
public sealed class AlbumData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("artists")]
    public ArtistList? Artists { get; init; }

    [JsonPropertyName("coverArt")]
    public CoverArt? CoverArt { get; init; }

    [JsonPropertyName("date")]
    public ReleaseDate? Date { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("playability")]
    public Playability? Playability { get; init; }
}

/// <summary>
/// Release date.
/// </summary>
public sealed class ReleaseDate
{
    [JsonPropertyName("year")]
    public int Year { get; init; }
}

#endregion

#region Playlists

/// <summary>
/// Page of playlist results.
/// </summary>
public sealed class PlaylistPage
{
    [JsonPropertyName("items")]
    public List<PlaylistResponseWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PagingInfo? PagingInfo { get; init; }
}

/// <summary>
/// Playlist response wrapper.
/// </summary>
public sealed class PlaylistResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public PlaylistData? Data { get; init; }
}

/// <summary>
/// Playlist data.
/// </summary>
public sealed class PlaylistData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("ownerV2")]
    public OwnerWrapper? OwnerV2 { get; init; }

    [JsonPropertyName("images")]
    public ImageList? Images { get; init; }
}

/// <summary>
/// Owner wrapper.
/// </summary>
public sealed class OwnerWrapper
{
    [JsonPropertyName("data")]
    public OwnerData? Data { get; init; }
}

/// <summary>
/// Owner data.
/// </summary>
public sealed class OwnerData
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

#endregion

#region Top Results

/// <summary>
/// Top search results (mixed types).
/// </summary>
public sealed class TopResults
{
    [JsonPropertyName("featured")]
    public List<TopResultWrapper>? Featured { get; init; }

    [JsonPropertyName("itemsV2")]
    public List<TopResultWrapper>? ItemsV2 { get; init; }
}

/// <summary>
/// Wrapper for top result items.
/// </summary>
public sealed class TopResultWrapper
{
    [JsonPropertyName("item")]
    public TopResultItem? Item { get; init; }

    [JsonPropertyName("matchedFields")]
    public List<string>? MatchedFields { get; init; }
}

/// <summary>
/// Top result item data.
/// </summary>
public sealed class TopResultItem
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

#endregion

#region Shared

/// <summary>
/// Cover art with images.
/// </summary>
public sealed class CoverArt
{
    [JsonPropertyName("sources")]
    public List<ImageSource>? Sources { get; init; }
}

/// <summary>
/// Image source with URL and dimensions.
/// </summary>
public sealed class ImageSource
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }
}

/// <summary>
/// Image with extracted colors.
/// </summary>
public sealed class ImageWithColors
{
    [JsonPropertyName("sources")]
    public List<ImageSource>? Sources { get; init; }
}

/// <summary>
/// List of images.
/// </summary>
public sealed class ImageList
{
    [JsonPropertyName("items")]
    public List<ImageListItem>? Items { get; init; }
}

/// <summary>
/// Image list item.
/// </summary>
public sealed class ImageListItem
{
    [JsonPropertyName("sources")]
    public List<ImageSource>? Sources { get; init; }
}

/// <summary>
/// Playability information.
/// </summary>
public sealed class Playability
{
    [JsonPropertyName("playable")]
    public bool Playable { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

#endregion

#region Simplified Search Result

/// <summary>
/// Simplified search result for easy consumption.
/// </summary>
public sealed class SearchResult
{
    public List<SearchResultItem> Items { get; init; } = new();
    public SearchResultItem? TopResult { get; set; }
    public int TotalTracks { get; init; }
    public int TotalArtists { get; init; }
    public int TotalAlbums { get; init; }
    public int TotalPlaylists { get; init; }
    public int TotalPodcasts { get; init; }
    public int TotalAudiobooks { get; init; }
    public int TotalUsers { get; init; }
    public int TotalAuthors { get; init; }
    public int TotalEpisodes { get; init; }
    public int TotalGenres { get; init; }

    /// <summary>
    /// Creates a SearchResult from a PathfinderSearchResponse.
    /// </summary>
    public static SearchResult FromResponse(PathfinderSearchResponse response)
    {
        var sv = response.Data?.SearchV2;
        var result = new SearchResult
        {
            TotalTracks = sv?.TracksV2?.TotalCount ?? 0,
            TotalArtists = sv?.Artists?.TotalCount ?? 0,
            TotalAlbums = sv?.AlbumsV2?.TotalCount ?? 0,
            TotalPlaylists = sv?.Playlists?.TotalCount ?? 0,
            TotalPodcasts = sv?.Podcasts?.TotalCount ?? 0,
            TotalAudiobooks = sv?.Audiobooks?.TotalCount ?? 0,
            TotalUsers = sv?.Users?.TotalCount ?? 0,
            TotalAuthors = sv?.Authors?.TotalCount ?? 0,
            TotalEpisodes = sv?.Episodes?.TotalCount ?? 0,
            TotalGenres = sv?.Genres?.TotalCount ?? 0
        };
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        void AddItem(SearchResultItem? item)
        {
            if (item == null)
                return;

            var key = $"{item.Type}:{item.Uri}";
            if (seenKeys.Add(key))
                result.Items.Add(item);
        }

        // Add tracks
        if (response.Data?.SearchV2?.TracksV2?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.TracksV2.Items)
            {
                var track = wrapper.Item?.Data;
                if (track?.Uri == null) continue;

                var trackArtistRefs = track.Artists?.Items?
                    .Where(a => a.Profile?.Name != null && a.Uri != null)
                    .ToList() ?? new List<ArtistItem>();
                var artistNames = trackArtistRefs.Select(a => a.Profile!.Name!).ToList();
                var artistUris = trackArtistRefs.Select(a => a.Uri!).ToList();

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.Track,
                    Uri = track.Uri,
                    Name = track.Name ?? "Unknown",
                    ArtistNames = artistNames,
                    ArtistUris = artistUris,
                    AlbumName = track.AlbumOfTrack?.Name,
                    ImageUrl = track.AlbumOfTrack?.CoverArt?.Sources?.FirstOrDefault()?.Url,
                    DurationMs = track.Duration?.TotalMilliseconds ?? 0
                });
            }
        }

        // Add artists
        if (response.Data?.SearchV2?.Artists?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.Artists.Items)
            {
                var artist = wrapper.Data;
                if (artist?.Uri == null) continue;

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.Artist,
                    Uri = artist.Uri,
                    Name = artist.Profile?.Name ?? "Unknown",
                    ImageUrl = artist.Visuals?.AvatarImage?.Sources?.FirstOrDefault()?.Url,
                    IsVerified = artist.Profile?.Verified ?? false
                });
            }
        }

        // Add albums
        if (response.Data?.SearchV2?.AlbumsV2?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.AlbumsV2.Items)
            {
                var album = wrapper.Data;
                if (album?.Uri == null || album.TypeName == "PreRelease") continue;

                var albumArtistRefs = album.Artists?.Items?
                    .Where(a => a.Profile?.Name != null && a.Uri != null)
                    .ToList() ?? new List<ArtistItem>();
                var albumArtistNames = albumArtistRefs.Select(a => a.Profile!.Name!).ToList();
                var albumArtistUris = albumArtistRefs.Select(a => a.Uri!).ToList();

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.Album,
                    Uri = album.Uri,
                    Name = album.Name ?? "Unknown",
                    ArtistNames = albumArtistNames,
                    ArtistUris = albumArtistUris,
                    ImageUrl = album.CoverArt?.Sources?.FirstOrDefault()?.Url,
                    ReleaseYear = album.Date?.Year,
                    AlbumType = album.Type
                });
            }
        }

        // Add playlists
        if (response.Data?.SearchV2?.Playlists?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.Playlists.Items)
            {
                var playlist = wrapper.Data;
                if (playlist?.Uri == null) continue;

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.Playlist,
                    Uri = playlist.Uri,
                    Name = playlist.Name ?? "Unknown",
                    OwnerName = playlist.OwnerV2?.Data?.Name,
                    Description = playlist.Description,
                    ImageUrl = playlist.Images?.Items?.FirstOrDefault()?.Sources?.FirstOrDefault()?.Url
                });
            }
        }

        // Add podcasts (shows)
        if (response.Data?.SearchV2?.Podcasts?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.Podcasts.Items)
            {
                var podcast = wrapper.Data;
                if (podcast?.Uri == null) continue;

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.Podcast,
                    Uri = podcast.Uri,
                    Name = podcast.Name ?? "Unknown",
                    PublisherName = podcast.Publisher?.Name,
                    ImageUrl = podcast.CoverArt?.Sources?.FirstOrDefault()?.Url
                });
            }
        }

        // Add users
        if (response.Data?.SearchV2?.Users?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.Users.Items)
            {
                var user = wrapper.Data;
                if (user?.Uri == null) continue;

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.User,
                    Uri = user.Uri,
                    Name = user.DisplayName ?? user.Username ?? "Unknown",
                    ImageUrl = user.Avatar?.Sources?.FirstOrDefault()?.Url
                });
            }
        }

        // Add genres
        if (response.Data?.SearchV2?.Genres?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.Genres.Items)
            {
                var genre = wrapper.Data;
                if (genre?.Uri == null) continue;

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.Genre,
                    Uri = genre.Uri,
                    Name = genre.Name ?? "Unknown",
                    ImageUrl = genre.Image?.Sources?.FirstOrDefault()?.Url
                });
            }
        }

        // Add episodes
        if (response.Data?.SearchV2?.Episodes?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.Episodes.Items)
            {
                var episode = wrapper.Data;
                if (episode?.Uri == null) continue;

                AddItem(new SearchResultItem
                {
                    Type = SearchResultType.Episode,
                    Uri = episode.Uri,
                    Name = episode.Name ?? "Unknown",
                    Description = episode.Description,
                    DurationMs = episode.Duration?.TotalMilliseconds ?? 0,
                    ImageUrl = episode.CoverArt?.Sources?.FirstOrDefault()?.Url,
                    ParentName = episode.PodcastV2?.Data?.Name,
                    ParentUri = episode.PodcastV2?.Data?.Uri
                });
            }
        }

        // Extract top result from topResultsV2
        var topItems = response.Data?.SearchV2?.TopResultsV2?.ItemsV2;
        if (topItems is { Count: > 0 })
        {
            foreach (var topWrapper in topItems)
            {
                var topItem = topWrapper.Item;
                if (topItem?.Data is not JsonElement je) continue;

                // A SearchSectionEntity wraps a group of items (e.g. "Featuring {artist}"
                // playlists, "Music videos" tracks). Flatten its items into the main list,
                // tagging each with the section's display label so the UI can group them.
                if (topItem.TypeName == "SearchSectionEntity")
                {
                    var label = GetNestedString(je, "title", "transformedLabel");
                    if (je.TryGetProperty("items", out var sectionItems)
                        && sectionItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sectionItem in sectionItems.EnumerateArray())
                        {
                            if (!sectionItem.TryGetProperty("__typename", out var stn)) continue;
                            if (!sectionItem.TryGetProperty("data", out var sdata)) continue;
                            AddItem(MapSearchItemFromJson(stn.GetString(), sdata, label));
                        }
                    }
                    continue;
                }

                var mapped = MapSearchItemFromJson(topItem.TypeName, je);
                AddItem(mapped);

                if (result.TopResult == null && mapped != null)
                    result.TopResult = mapped;
            }
        }

        return result;
    }

    private static SearchResultItem? MapSearchItemFromJson(string? typeName, JsonElement data, string? sectionLabel = null)
    {
        try
        {
            if (!data.TryGetProperty("uri", out var uriProp)) return null;
            var uri = uriProp.GetString();
            if (string.IsNullOrEmpty(uri)) return null;

            return typeName switch
            {
                "ArtistResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Artist,
                    Uri = uri,
                    Name = GetNestedString(data, "profile", "name") ?? "Unknown",
                    ImageUrl = GetFirstImageUrl(data, "visuals", "avatarImage"),
                    SectionLabel = sectionLabel,
                },
                "TrackResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Track,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                    ArtistNames = GetArtistNames(data),
                    ArtistUris = GetArtistUris(data),
                    AlbumName = GetNestedString(data, "albumOfTrack", "name"),
                    ImageUrl = GetFirstImageUrl(data, "albumOfTrack", "coverArt"),
                    DurationMs = data.TryGetProperty("duration", out var d)
                        && d.TryGetProperty("totalMilliseconds", out var ms) ? ms.GetInt64() : 0,
                    SectionLabel = sectionLabel,
                },
                "AlbumResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Album,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var an) ? an.GetString() ?? "Unknown" : "Unknown",
                    ArtistNames = GetArtistNames(data),
                    ArtistUris = GetArtistUris(data),
                    ImageUrl = GetFirstImageUrl(data, "coverArt"),
                    ReleaseYear = data.TryGetProperty("date", out var dt)
                        && dt.TryGetProperty("year", out var yr) ? yr.GetInt32() : null,
                    AlbumType = data.TryGetProperty("type", out var at) ? at.GetString() : null,
                    SectionLabel = sectionLabel,
                },
                "PlaylistResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Playlist,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var pn) ? pn.GetString() ?? "Unknown" : "Unknown",
                    OwnerName = data.TryGetProperty("ownerV2", out var ov)
                        && ov.TryGetProperty("data", out var od)
                        && od.TryGetProperty("name", out var on) ? on.GetString() : null,
                    ImageUrl = data.TryGetProperty("images", out var imgs)
                        && imgs.TryGetProperty("items", out var imgItems)
                        && imgItems.GetArrayLength() > 0
                        && imgItems[0].TryGetProperty("sources", out var srcs)
                        && srcs.GetArrayLength() > 0
                        && srcs[0].TryGetProperty("url", out var pu) ? pu.GetString() : null,
                    SectionLabel = sectionLabel,
                },
                "PodcastResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Podcast,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var podn) ? podn.GetString() ?? "Unknown" : "Unknown",
                    PublisherName = GetNestedString(data, "publisher", "name"),
                    ImageUrl = GetFirstImageUrl(data, "coverArt"),
                    SectionLabel = sectionLabel,
                },
                "EpisodeResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Episode,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var en) ? en.GetString() ?? "Unknown" : "Unknown",
                    Description = data.TryGetProperty("description", out var ed) ? ed.GetString() : null,
                    DurationMs = data.TryGetProperty("duration", out var edur)
                        && edur.TryGetProperty("totalMilliseconds", out var ems) ? ems.GetInt64() : 0,
                    ImageUrl = GetFirstImageUrl(data, "coverArt"),
                    ParentName = data.TryGetProperty("podcastV2", out var pv)
                        && pv.TryGetProperty("data", out var pvd)
                        && pvd.TryGetProperty("name", out var pvn) ? pvn.GetString() : null,
                    ParentUri = data.TryGetProperty("podcastV2", out var pv2)
                        && pv2.TryGetProperty("data", out var pv2d)
                        && pv2d.TryGetProperty("uri", out var pv2u) ? pv2u.GetString() : null,
                    SectionLabel = sectionLabel,
                },
                "UserResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.User,
                    Uri = uri,
                    Name = (data.TryGetProperty("displayName", out var udn) ? udn.GetString() : null)
                        ?? (data.TryGetProperty("username", out var un) ? un.GetString() : null)
                        ?? "Unknown",
                    ImageUrl = GetFirstImageUrl(data, "avatar"),
                    SectionLabel = sectionLabel,
                },
                "GenreResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Genre,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var gn) ? gn.GetString() ?? "Unknown" : "Unknown",
                    ImageUrl = GetFirstImageUrl(data, "image"),
                    SectionLabel = sectionLabel,
                },
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetNestedString(JsonElement root, string prop1, string prop2)
    {
        if (root.TryGetProperty(prop1, out var p1) && p1.TryGetProperty(prop2, out var p2))
            return p2.GetString();
        return null;
    }

    private static string? GetFirstImageUrl(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }
        // current is now the image container (e.g., coverArt or avatarImage)
        if (current.TryGetProperty("sources", out var sources) && sources.GetArrayLength() > 0)
        {
            if (sources[0].TryGetProperty("url", out var url))
                return url.GetString();
        }
        return null;
    }

    private static List<string> GetArtistNames(JsonElement data)
    {
        var names = new List<string>();
        if (data.TryGetProperty("artists", out var artists)
            && artists.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("profile", out var profile)
                    && profile.TryGetProperty("name", out var name))
                {
                    var n = name.GetString();
                    if (n != null) names.Add(n);
                }
            }
        }
        return names;
    }

    private static List<string> GetArtistUris(JsonElement data)
    {
        var uris = new List<string>();
        if (data.TryGetProperty("artists", out var artists)
            && artists.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("uri", out var uriProp))
                {
                    var u = uriProp.GetString();
                    if (u != null) uris.Add(u);
                }
            }
        }
        return uris;
    }
}

/// <summary>
/// Type of search result.
/// </summary>
public enum SearchResultType
{
    Track,
    Artist,
    Album,
    Playlist,
    Podcast,
    Episode,
    User,
    Genre
}

public enum SearchScope
{
    All,
    Artists
}

/// <summary>
/// A single search result item.
/// </summary>
public sealed class SearchResultItem
{
    public SearchResultType Type { get; init; }
    public  string Uri { get; init; }
    public  string Name { get; init; }

    // Track/Album specific
    public List<string>? ArtistNames { get; init; }
    public List<string>? ArtistUris { get; init; }
    public string? AlbumName { get; init; }
    public long DurationMs { get; init; }

    // Artist specific
    public bool IsVerified { get; init; }

    // Album specific
    public int? ReleaseYear { get; init; }
    public string? AlbumType { get; init; }

    // Playlist specific
    public string? OwnerName { get; init; }
    public string? Description { get; init; }

    // Podcast specific
    public string? PublisherName { get; init; }

    // Episode specific — links back to its parent show.
    public string? ParentName { get; init; }
    public string? ParentUri { get; init; }

    // Common
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Non-null when this item came from a <c>SearchSectionEntity</c> top-result group
    /// (e.g. "Featuring JJ Lin", "Music videos"). The value is the section's display label
    /// from <c>title.transformedLabel</c>. Null for standalone top-result items.
    /// </summary>
    public string? SectionLabel { get; init; }

    /// <summary>
    /// Computed subtitle suitable for binding in card/row templates.
    /// Equivalent to <see cref="GetSubtitle"/> but exposed as a property so x:Bind works
    /// without method-call syntax. Re-evaluated each access — cheap string construction.
    /// </summary>
    public string DisplaySubtitle => GetSubtitle();

    /// <summary>
    /// Gets a formatted display string for the item.
    /// </summary>
    public string GetDisplayString()
    {
        return Type switch
        {
            SearchResultType.Track => $"{Name} - {string.Join(", ", ArtistNames ?? new())}",
            SearchResultType.Artist => IsVerified ? $"{Name} ✓" : Name,
            SearchResultType.Album => $"{Name} - {string.Join(", ", ArtistNames ?? new())} ({ReleaseYear})",
            SearchResultType.Playlist => $"{Name} by {OwnerName ?? "Unknown"}",
            _ => Name
        };
    }

    public string GetSubtitle()
    {
        return Type switch
        {
            SearchResultType.Track => BuildTrackSubtitle(),
            SearchResultType.Artist => "Artist",
            SearchResultType.Album => BuildAlbumSubtitle(),
            SearchResultType.Playlist => string.IsNullOrWhiteSpace(OwnerName)
                ? "Playlist"
                : $"Playlist · {OwnerName}",
            SearchResultType.Podcast => string.IsNullOrWhiteSpace(PublisherName)
                ? "Podcast"
                : $"Podcast · {PublisherName}",
            SearchResultType.Episode => string.IsNullOrWhiteSpace(ParentName)
                ? "Episode"
                : $"Episode · {ParentName}",
            SearchResultType.User => "Profile",
            SearchResultType.Genre => "Genre",
            _ => Name
        };
    }

    public string GetTypeTag()
    {
        return Type switch
        {
            SearchResultType.Track => "Song",
            SearchResultType.Artist => "Artist",
            SearchResultType.Album => "Album",
            SearchResultType.Playlist => "Playlist",
            SearchResultType.Podcast => "Podcast",
            SearchResultType.Episode => "Episode",
            SearchResultType.User => "Profile",
            SearchResultType.Genre => "Genre",
            _ => "Result"
        };
    }

    public string GetActionGlyph()
    {
        return Type switch
        {
            SearchResultType.Track or SearchResultType.Episode => "\uE768",
            _ => "\uE76C"
        };
    }

    public string GetPlaceholderGlyph()
    {
        return Type switch
        {
            SearchResultType.Track => "\uE189",
            SearchResultType.Artist => "\uE77B",
            SearchResultType.Album => "\uE93C",
            SearchResultType.Playlist => "\uE142",
            SearchResultType.Podcast => "\uE9E9",
            SearchResultType.Episode => "\uE93B",
            SearchResultType.User => "\uE77B",
            SearchResultType.Genre => "\uE8FD",
            _ => "\uE721"
        };
    }

    private string BuildTrackSubtitle()
    {
        var artists = ArtistNames is { Count: > 0 }
            ? string.Join(", ", ArtistNames)
            : null;

        return string.IsNullOrWhiteSpace(artists)
            ? "Song"
            : $"Song · {artists}";
    }

    private string BuildAlbumSubtitle()
    {
        var artists = ArtistNames is { Count: > 0 }
            ? string.Join(", ", ArtistNames)
            : null;

        if (!string.IsNullOrWhiteSpace(artists) && ReleaseYear is int year)
            return $"Album · {artists} · {year}";

        if (!string.IsNullOrWhiteSpace(artists))
            return $"Album · {artists}";

        return ReleaseYear is int releaseYear
            ? $"Album · {releaseYear}"
            : "Album";
    }
}

#endregion
