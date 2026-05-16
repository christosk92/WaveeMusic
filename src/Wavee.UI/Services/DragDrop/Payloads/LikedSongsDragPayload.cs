using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Json;

namespace Wavee.UI.Services.DragDrop.Payloads;

/// <summary>
/// Drag payload for the user's Liked Songs collection. URI is the fixed
/// <c>spotify:collection</c> pseudo-URI; <see cref="HttpsUrls"/> points at the
/// public Liked Songs page so drag-out to a browser navigates correctly.
/// </summary>
public sealed class LikedSongsDragPayload : IDragPayload
{
    public const string LikedSongsUri = "spotify:collection";

    public DragPayloadKind Kind => DragPayloadKind.LikedSongs;
    public string InternalFormat => DragFormats.LikedSongs;
    public int ItemCount => 1;

    public IReadOnlyList<string> HttpsUrls => ["https://open.spotify.com/collection/tracks"];

    [JsonConstructor]
    public LikedSongsDragPayload() { }

    public string Serialize() =>
        JsonSerializer.Serialize(new LikedSongsDto(true), DragPayloadJsonContext.Default.LikedSongsDto);

    public static LikedSongsDragPayload Deserialize(string raw)
    {
        _ = JsonSerializer.Deserialize(raw, DragPayloadJsonContext.Default.LikedSongsDto)
            ?? throw new InvalidOperationException("LikedSongsDragPayload deserialization returned null");
        return new LikedSongsDragPayload();
    }

    // The DTO carries a single sentinel field so STJ source-gen has a real type
    // to attach to — the payload itself has no per-instance state.
    internal sealed record LikedSongsDto(bool Marker);
}
