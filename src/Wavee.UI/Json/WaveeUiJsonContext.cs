using System.Text.Json.Serialization;
using Wavee.UI.Services.Actions;

namespace Wavee.UI.Json;

/// <summary>
/// AOT-friendly <see cref="JsonSerializerContext"/> for the undoable action
/// payloads in <see cref="Wavee.UI.Services.Actions"/>. Each undoable action
/// persists its parameters as a JSON blob inside
/// <see cref="UserActionDescriptor.PayloadJson"/>; this context exposes a
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> for
/// each payload so the serializer never falls back to runtime reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SetLibrarySavedPayload))]
[JsonSerializable(typeof(SetPinnedPayload))]
[JsonSerializable(typeof(SetPlaylistFollowedPayload))]
[JsonSerializable(typeof(PlaylistTracksPayload))]
[JsonSerializable(typeof(CreatePlaylistPayload))]
[JsonSerializable(typeof(DeletePlaylistPayload))]
internal sealed partial class WaveeUiJsonContext : JsonSerializerContext
{
}
