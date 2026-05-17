using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Json;

namespace Wavee.UI.Services.DragDrop.Payloads;

public sealed class AlbumDragPayload : IDragPayload
{
    public string AlbumUri { get; }
    public string Name { get; }
    public string? ImageUrl { get; }

    public DragPayloadKind Kind => DragPayloadKind.Album;
    public string InternalFormat => DragFormats.Album;
    public int ItemCount => 1;
    public IReadOnlyList<string> HttpsUrls => Wavee.UI.Helpers.SpotifyUriHelper.ToHttps(AlbumUri) is { } u
        ? [u]
        : Array.Empty<string>();

    [JsonConstructor]
    public AlbumDragPayload(string albumUri, string name, string? imageUrl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumUri);
        AlbumUri = albumUri;
        Name = name ?? string.Empty;
        ImageUrl = imageUrl;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new AlbumDto(AlbumUri, Name, ImageUrl), DragPayloadJsonContext.Default.AlbumDto);

    public static AlbumDragPayload Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize(raw, DragPayloadJsonContext.Default.AlbumDto)
                  ?? throw new InvalidOperationException("AlbumDragPayload deserialization returned null");
        return new AlbumDragPayload(dto.AlbumUri ?? string.Empty, dto.Name ?? string.Empty, dto.ImageUrl);
    }

    internal sealed record AlbumDto(string? AlbumUri, string? Name, string? ImageUrl);
}
