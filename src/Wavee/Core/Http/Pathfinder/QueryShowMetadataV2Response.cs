using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// DTO for the queryShowMetadataV2 persisted GraphQL query. Mirrors the shape
// Spotify returns under data.podcastUnionV2 — only the fields the Show detail
// page actually consumes are modelled; everything else is left out so AOT
// serialization stays cheap.

public sealed class QueryShowMetadataV2Response
{
    [JsonPropertyName("data")]
    public QueryShowMetadataV2Data? Data { get; init; }
}

public sealed class QueryShowMetadataV2Data
{
    [JsonPropertyName("podcastUnionV2")]
    public PathfinderShow? PodcastUnionV2 { get; init; }
}

public sealed class PathfinderShow
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("htmlDescription")]
    public string? HtmlDescription { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("musicAndTalk")]
    public bool MusicAndTalk { get; init; }

    [JsonPropertyName("consumptionOrderV2")]
    public string? ConsumptionOrderV2 { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("saved")]
    public bool Saved { get; init; }

    [JsonPropertyName("publisher")]
    public PathfinderShowPublisher? Publisher { get; init; }

    [JsonPropertyName("coverArt")]
    public PathfinderShowCoverArt? CoverArt { get; init; }

    [JsonPropertyName("contentRatingV2")]
    public PathfinderShowContentRatingV2? ContentRatingV2 { get; init; }

    [JsonPropertyName("rating")]
    public PathfinderShowRatingContainer? Rating { get; init; }

    [JsonPropertyName("topics")]
    public PathfinderShowTopics? Topics { get; init; }

    [JsonPropertyName("showTypes")]
    public List<string>? ShowTypes { get; init; }

    [JsonPropertyName("playability")]
    public PathfinderShowPlayability? Playability { get; init; }

    [JsonPropertyName("sharingInfo")]
    public PathfinderShowSharingInfo? SharingInfo { get; init; }

    [JsonPropertyName("trailerV2")]
    public PathfinderShowTrailerWrapper? TrailerV2 { get; init; }

    [JsonPropertyName("episodesV2")]
    public PathfinderShowEpisodePage? EpisodesV2 { get; init; }

    [JsonPropertyName("visualIdentity")]
    public PathfinderShowVisualIdentity? VisualIdentity { get; init; }
}

public sealed class PathfinderShowPublisher
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class PathfinderShowCoverArt
{
    [JsonPropertyName("sources")]
    public List<PathfinderShowImageSource>? Sources { get; init; }
}

public sealed class PathfinderShowImageSource
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }
}

public sealed class PathfinderShowContentRatingV2
{
    [JsonPropertyName("labels")]
    public List<string>? Labels { get; init; }
}

public sealed class PathfinderShowRatingContainer
{
    [JsonPropertyName("averageRating")]
    public PathfinderShowAverageRating? AverageRating { get; init; }

    [JsonPropertyName("canRate")]
    public bool CanRate { get; init; }

    [JsonPropertyName("rating")]
    public PathfinderShowUserRating? Rating { get; init; }
}

public sealed class PathfinderShowAverageRating
{
    [JsonPropertyName("average")]
    public double Average { get; init; }

    [JsonPropertyName("showAverage")]
    public bool ShowAverage { get; init; }

    [JsonPropertyName("totalRatings")]
    public long TotalRatings { get; init; }
}

public sealed class PathfinderShowUserRating
{
    [JsonPropertyName("rating")]
    public double Rating { get; init; }
}

public sealed class PathfinderShowTopics
{
    [JsonPropertyName("items")]
    public List<PathfinderShowTopicItem>? Items { get; init; }
}

public sealed class PathfinderShowTopicItem
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class PathfinderShowPlayability
{
    [JsonPropertyName("playable")]
    public bool Playable { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class PathfinderShowSharingInfo
{
    [JsonPropertyName("shareId")]
    public string? ShareId { get; init; }

    [JsonPropertyName("shareUrl")]
    public string? ShareUrl { get; init; }
}

public sealed class PathfinderShowTrailerWrapper
{
    [JsonPropertyName("data")]
    public PathfinderShowTrailerData? Data { get; init; }
}

public sealed class PathfinderShowTrailerData
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class PathfinderShowEpisodePage
{
    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("items")]
    public List<PathfinderShowEpisodeEntry>? Items { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PathfinderShowEpisodePaging? PagingInfo { get; init; }
}

public sealed class PathfinderShowEpisodePaging
{
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }
}

public sealed class PathfinderShowEpisodeEntry
{
    [JsonPropertyName("entity")]
    public PathfinderShowEpisodeEntity? Entity { get; init; }
}

public sealed class PathfinderShowEpisodeEntity
{
    [JsonPropertyName("data")]
    public PathfinderShowEpisodeRef? Data { get; init; }
}

public sealed class PathfinderShowEpisodeRef
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class PathfinderShowVisualIdentity
{
    [JsonPropertyName("squareCoverImage")]
    public PathfinderShowVisualImage? SquareCoverImage { get; init; }
}

public sealed class PathfinderShowVisualImage
{
    [JsonPropertyName("extractedColorSet")]
    public PathfinderShowExtractedColorSet? ExtractedColorSet { get; init; }
}

public sealed class PathfinderShowExtractedColorSet
{
    [JsonPropertyName("encoreBaseSetTextColor")]
    public PathfinderShowColor? EncoreBaseSetTextColor { get; init; }

    [JsonPropertyName("highContrast")]
    public PathfinderShowColorTier? HighContrast { get; init; }

    [JsonPropertyName("higherContrast")]
    public PathfinderShowColorTier? HigherContrast { get; init; }

    [JsonPropertyName("minContrast")]
    public PathfinderShowColorTier? MinContrast { get; init; }
}

public sealed class PathfinderShowColorTier
{
    [JsonPropertyName("backgroundBase")]
    public PathfinderShowColor? BackgroundBase { get; init; }

    [JsonPropertyName("backgroundTintedBase")]
    public PathfinderShowColor? BackgroundTintedBase { get; init; }

    [JsonPropertyName("textBase")]
    public PathfinderShowColor? TextBase { get; init; }

    [JsonPropertyName("textBrightAccent")]
    public PathfinderShowColor? TextBrightAccent { get; init; }

    [JsonPropertyName("textSubdued")]
    public PathfinderShowColor? TextSubdued { get; init; }
}

public sealed class PathfinderShowColor
{
    [JsonPropertyName("alpha")]
    public byte Alpha { get; init; }

    [JsonPropertyName("red")]
    public byte Red { get; init; }

    [JsonPropertyName("green")]
    public byte Green { get; init; }

    [JsonPropertyName("blue")]
    public byte Blue { get; init; }
}

[JsonSerializable(typeof(QueryShowMetadataV2Response))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class QueryShowMetadataV2JsonContext : JsonSerializerContext
{
}
