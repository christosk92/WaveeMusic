using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Json;

namespace Wavee.UI.Services.DragDrop.Payloads;

/// <summary>
/// One or more tracks being dragged. Carries full <c>spotify:track:…</c> URIs
/// (not bare ids) so handlers don't have to reconstruct them. Source playlist
/// context is optional and informational — useful for handlers that want to
/// preserve provider context when enqueueing.
/// </summary>
public sealed class TrackDragPayload : IDragPayload
{
    public IReadOnlyList<string> TrackUris { get; }

    /// <summary>
    /// URI of the playlist / album / etc the tracks came from. When this matches
    /// the drop target's URI the registry routes the drop to
    /// <see cref="Handlers.ReorderPlaylistTracksHandler"/> instead of "add tracks".
    /// </summary>
    public string? SourceContextUri { get; }

    /// <summary>
    /// 0-based index of the FIRST dragged track inside <see cref="SourceContextUri"/>.
    /// Only meaningful when the source is an ordered list and the dragged items
    /// were a contiguous block. Used by intra-list reorder.
    /// </summary>
    public int? SourceStartIndex { get; }

    public DragPayloadKind Kind => DragPayloadKind.Tracks;
    public string InternalFormat => DragFormats.Tracks;
    public int ItemCount => TrackUris.Count;

    public IReadOnlyList<string> HttpsUrls => TrackUris
        .Select(Wavee.UI.Helpers.SpotifyUriHelper.ToHttps)
        .Where(u => !string.IsNullOrEmpty(u))
        .Cast<string>()
        .ToArray();

    [JsonConstructor]
    public TrackDragPayload(
        IReadOnlyList<string> trackUris,
        string? sourceContextUri = null,
        int? sourceStartIndex = null)
    {
        ArgumentNullException.ThrowIfNull(trackUris);
        TrackUris = trackUris;
        SourceContextUri = sourceContextUri;
        SourceStartIndex = sourceStartIndex;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new TrackDto(TrackUris, SourceContextUri, SourceStartIndex), DragPayloadJsonContext.Default.TrackDto);

    public static TrackDragPayload Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize(raw, DragPayloadJsonContext.Default.TrackDto)
                  ?? throw new InvalidOperationException("TrackDragPayload deserialization returned null");
        return new TrackDragPayload(dto.TrackUris ?? Array.Empty<string>(), dto.SourceContextUri, dto.SourceStartIndex);
    }

    internal sealed record TrackDto(IReadOnlyList<string>? TrackUris, string? SourceContextUri, int? SourceStartIndex);

    /// <summary>
    /// Bare track ids (legacy <c>WaveeTrackIds</c> format = pipe-joined ids,
    /// not full URIs). Provided so <c>DragPackageWriter</c> can keep emitting
    /// the legacy format for one release.
    /// </summary>
    public string LegacyPipeJoinedIds => string.Join('|', TrackUris.Select(Wavee.UI.Helpers.SpotifyUriHelper.BareId));
}
