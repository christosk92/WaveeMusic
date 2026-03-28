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
}

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

    /// <summary>
    /// Creates a SearchResult from a PathfinderSearchResponse.
    /// </summary>
    public static SearchResult FromResponse(PathfinderSearchResponse response)
    {
        var result = new SearchResult
        {
            TotalTracks = response.Data?.SearchV2?.TracksV2?.TotalCount ?? 0,
            TotalArtists = response.Data?.SearchV2?.Artists?.TotalCount ?? 0,
            TotalAlbums = response.Data?.SearchV2?.AlbumsV2?.TotalCount ?? 0,
            TotalPlaylists = response.Data?.SearchV2?.Playlists?.TotalCount ?? 0
        };

        // Add tracks
        if (response.Data?.SearchV2?.TracksV2?.Items != null)
        {
            foreach (var wrapper in response.Data.SearchV2.TracksV2.Items)
            {
                var track = wrapper.Item?.Data;
                if (track?.Uri == null) continue;

                var artistNames = track.Artists?.Items?
                    .Where(a => a.Profile?.Name != null)
                    .Select(a => a.Profile!.Name!)
                    .ToList() ?? new List<string>();

                result.Items.Add(new SearchResultItem
                {
                    Type = SearchResultType.Track,
                    Uri = track.Uri,
                    Name = track.Name ?? "Unknown",
                    ArtistNames = artistNames,
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

                result.Items.Add(new SearchResultItem
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

                var artistNames = album.Artists?.Items?
                    .Where(a => a.Profile?.Name != null)
                    .Select(a => a.Profile!.Name!)
                    .ToList() ?? new List<string>();

                result.Items.Add(new SearchResultItem
                {
                    Type = SearchResultType.Album,
                    Uri = album.Uri,
                    Name = album.Name ?? "Unknown",
                    ArtistNames = artistNames,
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

                result.Items.Add(new SearchResultItem
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

        // Extract top result from topResultsV2
        var topItems = response.Data?.SearchV2?.TopResultsV2?.ItemsV2;
        if (topItems is { Count: > 0 })
        {
            foreach (var topWrapper in topItems)
            {
                var topItem = topWrapper.Item;
                if (topItem?.Data is not JsonElement je) continue;

                var mapped = MapTopResultFromJson(topItem.TypeName, je);
                if (mapped != null)
                {
                    result.TopResult = mapped;
                    break;
                }
            }
        }

        return result;
    }

    private static SearchResultItem? MapTopResultFromJson(string? typeName, JsonElement data)
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
                },
                "TrackResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Track,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                    ArtistNames = GetArtistNames(data),
                    AlbumName = GetNestedString(data, "albumOfTrack", "name"),
                    ImageUrl = GetFirstImageUrl(data, "albumOfTrack", "coverArt"),
                    DurationMs = data.TryGetProperty("duration", out var d)
                        && d.TryGetProperty("totalMilliseconds", out var ms) ? ms.GetInt64() : 0
                },
                "AlbumResponseWrapper" => new SearchResultItem
                {
                    Type = SearchResultType.Album,
                    Uri = uri,
                    Name = data.TryGetProperty("name", out var an) ? an.GetString() ?? "Unknown" : "Unknown",
                    ArtistNames = GetArtistNames(data),
                    ImageUrl = GetFirstImageUrl(data, "coverArt"),
                    ReleaseYear = data.TryGetProperty("date", out var dt)
                        && dt.TryGetProperty("year", out var yr) ? yr.GetInt32() : null,
                    AlbumType = data.TryGetProperty("type", out var at) ? at.GetString() : null
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
}

/// <summary>
/// Type of search result.
/// </summary>
public enum SearchResultType
{
    Track,
    Artist,
    Album,
    Playlist
}

/// <summary>
/// A single search result item.
/// </summary>
public sealed class SearchResultItem
{
    public SearchResultType Type { get; init; }
    public required string Uri { get; init; }
    public required string Name { get; init; }

    // Track/Album specific
    public List<string>? ArtistNames { get; init; }
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

    // Common
    public string? ImageUrl { get; init; }

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
}

#endregion
