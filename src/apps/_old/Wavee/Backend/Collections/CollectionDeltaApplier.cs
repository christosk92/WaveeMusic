using System.Collections.Generic;

namespace Wavee.Backend.Collections;

// ── Collection (library set) sync — the token-delta model + applier ───────────────────────────────────────────────────
// The unordered library sets (liked/albums/artists/shows/episodes) sync via an opaque delta token: the server returns the
// items changed since `last_sync_token` plus a `new_revision`. This is the unordered counterpart to PlaylistDiffApplier;
// the SpotifyLive layer maps the collection2v2 protos onto these domain types so this stays unit-testable without a wire.

/// <summary>One changed collection item: the entity <see cref="Uri"/>, whether it was <see cref="Removed"/>, and the
/// add timestamp (unix ms; 0 = unknown).</summary>
public readonly record struct CollectionItem(string Uri, bool Removed, long AddedAt);

/// <summary>A batch of changes for one set plus the opaque <see cref="NewRevision"/> token that seeds the next delta.</summary>
public sealed record CollectionDelta(string SetId, string? NewRevision, IReadOnlyList<CollectionItem> Items);

public static class CollectionDeltaApplier
{
    /// <summary>Folds the delta onto the Store's set membership inside one bulk scope, so a 10k-item delta emits a single
    /// change signal (not one per item). Does NOT persist the revision — the caller advances it after a successful apply.</summary>
    public static void Apply(IStore store, CollectionDelta delta)
    {
        using var _ = store.BeginBulk();
        var items = delta.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            store.SetSaved(delta.SetId, it.Uri, !it.Removed, SyncState.Confirmed, it.AddedAt);   // ms; 0 preserves stored
        }
    }
}
