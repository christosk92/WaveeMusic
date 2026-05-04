using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root ──

public sealed class NpvArtistResponse
{
    [JsonPropertyName("data")]
    public NpvArtistData? Data { get; init; }
}

public sealed class NpvArtistData
{
    /// <summary>
    /// Reuses the existing ArtistUnion from ArtistOverviewResponse — same shape.
    /// </summary>
    [JsonPropertyName("artistUnion")]
    public ArtistUnion? ArtistUnion { get; init; }

    [JsonPropertyName("trackUnion")]
    public NpvTrackUnion? TrackUnion { get; init; }
}

// ── Track Union ──

public sealed class NpvTrackUnion
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("credits")]
    public List<NpvCredit>? Credits { get; init; }

    [JsonPropertyName("creditsTrait")]
    public NpvCreditsTrait? CreditsTrait { get; init; }

    [JsonPropertyName("canvas")]
    public NpvCanvas? Canvas { get; init; }

    [JsonPropertyName("relatedVideos")]
    public NpvRelatedVideosPage? RelatedVideos { get; init; }

    [JsonPropertyName("merch")]
    public NpvMerch? Merch { get; init; }

    [JsonPropertyName("associationsV3")]
    public NpvAssociationsV3? AssociationsV3 { get; init; }
}

// ── Credits (flat list) ──

public sealed class NpvCredit
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    [JsonPropertyName("artistUri")]
    public string? ArtistUri { get; init; }

    [JsonPropertyName("isArtistUriLinkable")]
    public bool IsArtistUriLinkable { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

// ── Credits Trait (grouped by role) ──

public sealed class NpvCreditsTrait
{
    [JsonPropertyName("contributors")]
    public NpvContributorsContainer? Contributors { get; init; }

    [JsonPropertyName("sources")]
    public NpvSourcesContainer? Sources { get; init; }
}

public sealed class NpvContributorsContainer
{
    [JsonPropertyName("items")]
    public List<NpvContributor>? Items { get; init; }
}

public sealed class NpvContributor
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("roleGroup")]
    public NpvRoleGroup? RoleGroup { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class NpvRoleGroup
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class NpvSourcesContainer
{
    [JsonPropertyName("items")]
    public List<NpvSource>? Items { get; init; }
}

public sealed class NpvSource
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

// ── Canvas ──

public sealed class NpvCanvas
{
    [JsonPropertyName("fileId")]
    public string? FileId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

// ── Related Videos ──

public sealed class NpvRelatedVideosPage
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("items")]
    public List<NpvRelatedVideo>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class NpvRelatedVideo
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("trackOfVideo")]
    public NpvTrackOfVideo? TrackOfVideo { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class NpvTrackOfVideo
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("_uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("data")]
    public NpvTrackOfVideoData? Data { get; init; }
}

public sealed class NpvTrackOfVideoData
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("albumOfTrack")]
    public NpvVideoAlbum? AlbumOfTrack { get; init; }

    [JsonPropertyName("artists")]
    public NpvVideoArtists? Artists { get; init; }

    [JsonPropertyName("contentRating")]
    public NpvContentRating? ContentRating { get; init; }

    [JsonPropertyName("associationsV3")]
    public NpvVideoAssociations? AssociationsV3 { get; init; }
}

public sealed class NpvVideoAlbum
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("coverArt")]
    public NpvVideoCoverArt? CoverArt { get; init; }
}

public sealed class NpvVideoCoverArt
{
    [JsonPropertyName("extractedColors")]
    public NpvVideoExtractedColors? ExtractedColors { get; init; }

    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class NpvVideoExtractedColors
{
    [JsonPropertyName("colorDark")]
    public ArtistColorHex? ColorDark { get; init; }
}

public sealed class NpvVideoArtists
{
    [JsonPropertyName("items")]
    public List<NpvVideoArtistItem>? Items { get; init; }
}

public sealed class NpvVideoArtistItem
{
    [JsonPropertyName("profile")]
    public NpvVideoArtistProfile? Profile { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class NpvVideoArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class NpvContentRating
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed class NpvVideoAssociations
{
    [JsonPropertyName("audioAssociations")]
    public NpvAudioAssociations? AudioAssociations { get; init; }
}

public sealed class NpvAudioAssociations
{
    [JsonPropertyName("items")]
    public List<NpvAudioAssociationItem>? Items { get; init; }
}

public sealed class NpvAudioAssociationItem
{
    [JsonPropertyName("trackAudio")]
    public NpvTrackAudioRef? TrackAudio { get; init; }
}

public sealed class NpvTrackAudioRef
{
    [JsonPropertyName("_uri")]
    public string? Uri { get; init; }
}

// ── Merch ──

public sealed class NpvMerch
{
    [JsonPropertyName("items")]
    public List<object>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

// ── Associations ──

public sealed class NpvAssociationsV3
{
    [JsonPropertyName("unmappedVideoTrackAssociations")]
    public NpvUnmappedVideoAssociations? UnmappedVideoTrackAssociations { get; init; }
}

public sealed class NpvUnmappedVideoAssociations
{
    [JsonPropertyName("items")]
    public List<NpvUnmappedVideoItem>? Items { get; init; }
}

public sealed class NpvUnmappedVideoItem
{
    [JsonPropertyName("associatedTrack")]
    public NpvAssociatedTrackRef? AssociatedTrack { get; init; }
}

public sealed class NpvAssociatedTrackRef
{
    [JsonPropertyName("_uri")]
    public string? Uri { get; init; }
}

// ── JSON Source Generation ──

[JsonSerializable(typeof(NpvArtistResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class NpvArtistJsonContext : JsonSerializerContext
{
}
