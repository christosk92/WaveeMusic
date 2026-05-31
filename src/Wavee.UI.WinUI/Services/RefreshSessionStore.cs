using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.Services.Playlists;
using Wavee.UI.WinUI.Json;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Durable, per-playlist persistence of an in-progress refresh session, backed by the
/// <c>playlist_refresh_sessions</c> table in <c>metadata.db</c>. Serialises the session via the
/// source-gen <see cref="RefreshSessionJsonContext"/> (AOT-safe — no reflection JSON).
/// </summary>
public sealed class RefreshSessionStore : IRefreshSessionStore
{
    private readonly IMetadataDatabase _db;

    public RefreshSessionStore(IMetadataDatabase db) => _db = db;

    public async Task<RefreshSessionState?> LoadAsync(string playlistId, CancellationToken ct = default)
    {
        var row = await _db.GetPlaylistRefreshSessionAsync(playlistId, ct).ConfigureAwait(false);
        if (row is not { } r) return null;

        PersistedRefreshSession? dto;
        try { dto = JsonSerializer.Deserialize(r.PayloadJson, RefreshSessionJsonContext.Default.PersistedRefreshSession); }
        catch { return null; }
        if (dto is null) return null;

        var decisions = new Dictionary<string, SwipeDecision>(StringComparer.Ordinal);
        foreach (var (uri, value) in dto.Decisions)
            if (Enum.TryParse<SwipeDecision>(value, out var d))
                decisions[uri] = d;

        return new RefreshSessionState(dto.PlaylistId, dto.BaseRevision, dto.SnapshotUris, decisions);
    }

    public async Task SaveAsync(RefreshSessionState state, int remaining, CancellationToken ct = default)
    {
        var decisions = state.Decisions.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
        var dto = new PersistedRefreshSession(state.PlaylistId, state.BaseRevision, state.SnapshotUris.ToList(), decisions);
        var json = JsonSerializer.Serialize(dto, RefreshSessionJsonContext.Default.PersistedRefreshSession);
        await _db.UpsertPlaylistRefreshSessionAsync(state.PlaylistId, json, remaining, ct).ConfigureAwait(false);
    }

    public Task ClearAsync(string playlistId, CancellationToken ct = default)
        => _db.DeletePlaylistRefreshSessionAsync(playlistId, ct);

    public Task<int?> GetRemainingAsync(string playlistId, CancellationToken ct = default)
        => _db.GetPlaylistRefreshRemainingAsync(playlistId, ct);
}
