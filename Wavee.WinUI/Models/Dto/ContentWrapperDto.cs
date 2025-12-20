using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Base class for content wrappers in section items.
/// Uses polymorphic deserialization based on __typename.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "__typename")]
[JsonDerivedType(typeof(PlaylistResponseWrapperDto), "PlaylistResponseWrapper")]
[JsonDerivedType(typeof(AlbumResponseWrapperDto), "AlbumResponseWrapper")]
[JsonDerivedType(typeof(ArtistResponseWrapperDto), "ArtistResponseWrapper")]
[JsonDerivedType(typeof(ShowResponseWrapperDto), "ShowResponseWrapper")]
[JsonDerivedType(typeof(UnknownTypeWrapperDto), "UnknownType")]
public abstract class ContentWrapperDto
{
}

/// <summary>
/// Wrapper for playlist content
/// </summary>
public sealed class PlaylistResponseWrapperDto : ContentWrapperDto
{
    [JsonPropertyName("data")]
    public PlaylistDto? Data { get; set; }
}

/// <summary>
/// Wrapper for album content
/// </summary>
public sealed class AlbumResponseWrapperDto : ContentWrapperDto
{
    [JsonPropertyName("data")]
    public AlbumDto? Data { get; set; }
}

/// <summary>
/// Wrapper for artist content
/// </summary>
public sealed class ArtistResponseWrapperDto : ContentWrapperDto
{
    [JsonPropertyName("data")]
    public ArtistDto? Data { get; set; }
}

/// <summary>
/// Wrapper for show content
/// </summary>
public sealed class ShowResponseWrapperDto : ContentWrapperDto
{
    [JsonPropertyName("data")]
    public ShowDto? Data { get; set; }
}

/// <summary>
/// Wrapper for unknown/unsupported content types
/// </summary>
public sealed class UnknownTypeWrapperDto : ContentWrapperDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}
