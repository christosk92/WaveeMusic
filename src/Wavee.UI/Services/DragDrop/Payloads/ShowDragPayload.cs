using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Json;

namespace Wavee.UI.Services.DragDrop.Payloads;

/// <summary>
/// Drag payload for a podcast show (<c>spotify:show:{id}</c>). Dropping on a
/// playlist appends the show's episodes; dropping on Now Playing switches
/// context to the show.
/// </summary>
public sealed class ShowDragPayload : IDragPayload
{
    public string ShowUri { get; }
    public string Name { get; }
    public string? ImageUrl { get; }

    public DragPayloadKind Kind => DragPayloadKind.Show;
    public string InternalFormat => DragFormats.Show;
    public int ItemCount => 1;
    public IReadOnlyList<string> HttpsUrls => Wavee.UI.Helpers.SpotifyUriHelper.ToHttps(ShowUri) is { } u
        ? [u]
        : Array.Empty<string>();

    [JsonConstructor]
    public ShowDragPayload(string showUri, string name, string? imageUrl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(showUri);
        ShowUri = showUri;
        Name = name ?? string.Empty;
        ImageUrl = imageUrl;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new ShowDto(ShowUri, Name, ImageUrl), DragPayloadJsonContext.Default.ShowDto);

    public static ShowDragPayload Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize(raw, DragPayloadJsonContext.Default.ShowDto)
                  ?? throw new InvalidOperationException("ShowDragPayload deserialization returned null");
        return new ShowDragPayload(dto.ShowUri ?? string.Empty, dto.Name ?? string.Empty, dto.ImageUrl);
    }

    internal sealed record ShowDto(string? ShowUri, string? Name, string? ImageUrl);
}
