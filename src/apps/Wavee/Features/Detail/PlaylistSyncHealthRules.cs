using System;

namespace Wavee;

/// <summary>What the playlist header's sync chip shows. <see cref="None"/> is the overwhelmingly normal state.</summary>
public enum PlaylistSyncHealth : byte { None = 0, Syncing = 1, Failed = 2 }

/// <summary>The PURE rule behind the header's "still syncing with Spotify" chip. Engine-free (System + the Backend
/// entry record) so PlaylistSyncHealthRulesTests pins it against production code.
/// <para>A torn /changes reply is normal and resolves in a few hundred milliseconds; showing a chip for that would be
/// noise. It becomes news when the uri has been unresolved longer than <see cref="SyncingAfterMs"/>, or when a
/// revalidate actually FAILED (that is lasting news at once — a retry is minutes away, or never).</para></summary>
static class PlaylistSyncHealthRules
{
    public const long SyncingAfterMs = 3_000;

    public static PlaylistSyncHealth Decide(in Wavee.Backend.Playlists.PlaylistResyncQueue.Entry entry, long nowUtcMs)
        => entry.Phase switch
        {
            Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.None => PlaylistSyncHealth.None,
            Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.Failed => PlaylistSyncHealth.Failed,
            _ => nowUtcMs - entry.SinceUtcMs >= SyncingAfterMs ? PlaylistSyncHealth.Syncing : PlaylistSyncHealth.None,
        };

    /// <summary>How long until an unresolved entry crosses the threshold (0 when it already has, or is not pending).
    /// The chip arms one UseTimeout for exactly this, so it appears the frame the threshold passes without polling.</summary>
    public static long MsUntilSyncing(in Wavee.Backend.Playlists.PlaylistResyncQueue.Entry entry, long nowUtcMs)
        => entry.Phase is Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.Marked or Wavee.Backend.Playlists.PlaylistResyncQueue.Phase.Revalidating
            ? Math.Max(0, SyncingAfterMs - (nowUtcMs - entry.SinceUtcMs))
            : 0;
}
