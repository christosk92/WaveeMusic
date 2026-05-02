using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class GetEpisodeOrChapterResponse
{
    [JsonPropertyName("data")]
    public GetEpisodeOrChapterData? Data { get; init; }
}

public sealed class GetEpisodeOrChapterData
{
    [JsonPropertyName("episodeUnionV2")]
    public PathfinderEpisode? EpisodeUnionV2 { get; init; }
}

public sealed class PathfinderEpisode
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("htmlDescription")]
    public string? HtmlDescription { get; init; }

    [JsonPropertyName("mediaTypes")]
    public List<string>? MediaTypes { get; init; }

    [JsonPropertyName("duration")]
    public PathfinderEpisodeDuration? Duration { get; init; }

    [JsonPropertyName("coverArt")]
    public PathfinderEpisodeCoverArt? CoverArt { get; init; }

    [JsonPropertyName("displaySegments")]
    public PathfinderEpisodeDisplaySegments? DisplaySegments { get; init; }

    [JsonPropertyName("contentRating")]
    public PathfinderEpisodeContentRating? ContentRating { get; init; }

    [JsonPropertyName("playability")]
    public PathfinderEpisodePlayability? Playability { get; init; }

    [JsonPropertyName("playedState")]
    public PathfinderEpisodePlayedState? PlayedState { get; init; }

    [JsonPropertyName("podcastV2")]
    public PathfinderPodcastResponseWrapper? PodcastV2 { get; init; }

    [JsonPropertyName("previewPlayback")]
    public PathfinderEpisodePreviewPlayback? PreviewPlayback { get; init; }

    [JsonPropertyName("releaseDate")]
    public PathfinderEpisodeReleaseDate? ReleaseDate { get; init; }

    [JsonPropertyName("restrictions")]
    public PathfinderEpisodeRestrictions? Restrictions { get; init; }

    [JsonPropertyName("sharingInfo")]
    public PathfinderEpisodeSharingInfo? SharingInfo { get; init; }

    [JsonPropertyName("transcripts")]
    public PathfinderEpisodeTranscripts? Transcripts { get; init; }
}

public sealed class PathfinderEpisodeDuration
{
    [JsonPropertyName("totalMilliseconds")]
    public long TotalMilliseconds { get; init; }
}

public sealed class PathfinderEpisodeCoverArt
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }

    [JsonPropertyName("extractedColors")]
    public PathfinderEpisodeExtractedColors? ExtractedColors { get; init; }
}

public sealed class PathfinderEpisodeExtractedColors
{
    [JsonPropertyName("colorDark")]
    public PathfinderEpisodeHexColor? ColorDark { get; init; }
}

public sealed class PathfinderEpisodeHexColor
{
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }
}

public sealed class PathfinderEpisodeContentRating
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed class PathfinderEpisodePlayability
{
    [JsonPropertyName("playable")]
    public bool Playable { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class PathfinderEpisodePlayedState
{
    [JsonPropertyName("playPositionMilliseconds")]
    public long PlayPositionMilliseconds { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }
}

public sealed class PathfinderPodcastResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("data")]
    public PathfinderPodcast? Data { get; init; }
}

public sealed class PathfinderPodcast
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("htmlDescription")]
    public string? HtmlDescription { get; init; }

    [JsonPropertyName("publisher")]
    public PathfinderPodcastPublisher? Publisher { get; init; }

    [JsonPropertyName("showTypes")]
    public List<string>? ShowTypes { get; init; }

    [JsonPropertyName("coverArt")]
    public PathfinderEpisodeCoverArt? CoverArt { get; init; }

    [JsonPropertyName("trailerV2")]
    public PathfinderEpisodeResponseWrapper? TrailerV2 { get; init; }
}

public sealed class PathfinderPodcastPublisher
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class PathfinderEpisodeResponseWrapper
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("data")]
    public PathfinderEpisode? Data { get; init; }
}

public sealed class PathfinderEpisodePreviewPlayback
{
    [JsonPropertyName("audioPreview")]
    public PathfinderEpisodeAudioPreview? AudioPreview { get; init; }
}

public sealed class PathfinderEpisodeAudioPreview
{
    [JsonPropertyName("cdnUrl")]
    public string? CdnUrl { get; init; }
}

public sealed class PathfinderEpisodeReleaseDate
{
    [JsonPropertyName("isoString")]
    public string? IsoString { get; init; }

    [JsonPropertyName("precision")]
    public string? Precision { get; init; }
}

public sealed class PathfinderEpisodeRestrictions
{
    [JsonPropertyName("paywallContent")]
    public bool PaywallContent { get; init; }
}

public sealed class PathfinderEpisodeSharingInfo
{
    [JsonPropertyName("shareId")]
    public string? ShareId { get; init; }

    [JsonPropertyName("shareUrl")]
    public string? ShareUrl { get; init; }
}

public sealed class PathfinderEpisodeTranscripts
{
    [JsonPropertyName("items")]
    public List<PathfinderEpisodeTranscript>? Items { get; init; }
}

public sealed class PathfinderEpisodeTranscript
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("cdnUrl")]
    public string? CdnUrl { get; init; }

    [JsonPropertyName("readAlongUrlV2")]
    public string? ReadAlongUrlV2 { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; init; }
}

public sealed class PathfinderEpisodeDisplaySegments
{
    [JsonPropertyName("chapterTags")]
    public List<string>? ChapterTags { get; init; }

    [JsonPropertyName("displaySegments")]
    public PathfinderEpisodeDisplaySegmentPage? Segments { get; init; }
}

public sealed class PathfinderEpisodeDisplaySegmentPage
{
    [JsonPropertyName("items")]
    public List<PathfinderEpisodeDisplaySegment>? Items { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PathfinderEpisodeDisplaySegmentPaging? PagingInfo { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class PathfinderEpisodeDisplaySegmentPaging
{
    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }
}

public sealed class PathfinderEpisodeDisplaySegment
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("seekStart")]
    public PathfinderEpisodeSeekTime? SeekStart { get; init; }

    [JsonPropertyName("seekStop")]
    public PathfinderEpisodeSeekTime? SeekStop { get; init; }
}

public sealed class PathfinderEpisodeSeekTime
{
    [JsonPropertyName("milliseconds")]
    public long Milliseconds { get; init; }
}

[JsonSerializable(typeof(GetEpisodeOrChapterResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class GetEpisodeOrChapterJsonContext : JsonSerializerContext
{
}
