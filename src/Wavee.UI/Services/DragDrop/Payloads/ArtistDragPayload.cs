using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Json;

namespace Wavee.UI.Services.DragDrop.Payloads;

public sealed class ArtistDragPayload : IDragPayload
{
    public string ArtistUri { get; }
    public string Name { get; }

    public DragPayloadKind Kind => DragPayloadKind.Artist;
    public string InternalFormat => DragFormats.Artist;
    public int ItemCount => 1;
    public IReadOnlyList<string> HttpsUrls => Wavee.UI.Helpers.SpotifyUriHelper.ToHttps(ArtistUri) is { } u
        ? [u]
        : Array.Empty<string>();

    [JsonConstructor]
    public ArtistDragPayload(string artistUri, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(artistUri);
        ArtistUri = artistUri;
        Name = name ?? string.Empty;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new ArtistDto(ArtistUri, Name), DragPayloadJsonContext.Default.ArtistDto);

    public static ArtistDragPayload Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize(raw, DragPayloadJsonContext.Default.ArtistDto)
                  ?? throw new InvalidOperationException("ArtistDragPayload deserialization returned null");
        return new ArtistDragPayload(dto.ArtistUri ?? string.Empty, dto.Name ?? string.Empty);
    }

    internal sealed record ArtistDto(string? ArtistUri, string? Name);
}
