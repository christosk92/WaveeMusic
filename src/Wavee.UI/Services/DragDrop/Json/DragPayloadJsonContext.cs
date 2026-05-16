using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop.Json;

/// <summary>
/// Source-generated <see cref="System.Text.Json.JsonSerializerContext"/> for
/// every drag payload DTO. Keeps trimming / AOT happy and avoids per-call
/// reflection startup cost.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TrackDragPayload.TrackDto))]
[JsonSerializable(typeof(AlbumDragPayload.AlbumDto))]
[JsonSerializable(typeof(PlaylistDragPayload.PlaylistDto))]
[JsonSerializable(typeof(ArtistDragPayload.ArtistDto))]
[JsonSerializable(typeof(SidebarReorderPayload.SidebarDto))]
[JsonSerializable(typeof(LikedSongsDragPayload.LikedSongsDto))]
[JsonSerializable(typeof(ShowDragPayload.ShowDto))]
internal sealed partial class DragPayloadJsonContext : JsonSerializerContext;
