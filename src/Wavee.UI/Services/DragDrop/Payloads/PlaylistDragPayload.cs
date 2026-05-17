using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Json;

namespace Wavee.UI.Services.DragDrop.Payloads;

public sealed class PlaylistDragPayload : IDragPayload
{
    public string PlaylistUri { get; }
    public string Name { get; }
    public bool IsOwned { get; }

    public DragPayloadKind Kind => DragPayloadKind.Playlist;
    public string InternalFormat => DragFormats.Playlist;
    public int ItemCount => 1;
    public IReadOnlyList<string> HttpsUrls => Wavee.UI.Helpers.SpotifyUriHelper.ToHttps(PlaylistUri) is { } u
        ? [u]
        : Array.Empty<string>();

    [JsonConstructor]
    public PlaylistDragPayload(string playlistUri, string name, bool isOwned = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(playlistUri);
        PlaylistUri = playlistUri;
        Name = name ?? string.Empty;
        IsOwned = isOwned;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new PlaylistDto(PlaylistUri, Name, IsOwned), DragPayloadJsonContext.Default.PlaylistDto);

    public static PlaylistDragPayload Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize(raw, DragPayloadJsonContext.Default.PlaylistDto)
                  ?? throw new InvalidOperationException("PlaylistDragPayload deserialization returned null");
        return new PlaylistDragPayload(dto.PlaylistUri ?? string.Empty, dto.Name ?? string.Empty, dto.IsOwned);
    }

    internal sealed record PlaylistDto(string? PlaylistUri, string? Name, bool IsOwned);
}
