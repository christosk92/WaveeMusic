using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.UI.WinUI.Json;

/// <summary>
/// Explicit, AOT-safe JSON shape for a persisted refresh session. Decisions are stored as
/// <c>uri → "Keep"/"Remove"</c> strings so the contract is obvious and source-gen-friendly.
/// </summary>
internal sealed record PersistedRefreshSession(
    string PlaylistId,
    string? BaseRevision,
    IReadOnlyList<string> SnapshotUris,
    Dictionary<string, string> Decisions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PersistedRefreshSession))]
internal sealed partial class RefreshSessionJsonContext : JsonSerializerContext;
