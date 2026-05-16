using System.Text.Json.Serialization;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Library.Spotify.Outbox;

/// <summary>
/// Payload for <c>library.save</c> and <c>library.remove</c> outbox entries —
/// the URI is carried on the entry's <c>PrimaryUri</c>, the item-type goes
/// here so handlers can pick the right collection set.
/// </summary>
public sealed record LibraryOpPayload(SpotifyLibraryItemType ItemType);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LibraryOpPayload))]
internal sealed partial class LibraryOpJson : JsonSerializerContext;
