using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents a playlist from the Spotify API
/// </summary>
public sealed class PlaylistDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("images")]
    public ImageItemsContainerDto? Images { get; set; }

    [JsonPropertyName("ownerV2")]
    public OwnerV2Dto? OwnerV2 { get; set; }

    [JsonPropertyName("collaborative")]
    public bool? Collaborative { get; set; }

    [JsonPropertyName("public")]
    public bool? Public { get; set; }

    [JsonPropertyName("totalTracks")]
    public int? TotalTracks { get; set; }
}

/// <summary>
/// Owner wrapper with data
/// </summary>
public sealed class OwnerV2Dto
{
    [JsonPropertyName("data")]
    public UserDto? Data { get; set; }
}

/// <summary>
/// User information
/// </summary>
public sealed class UserDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
