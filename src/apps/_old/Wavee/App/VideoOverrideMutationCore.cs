namespace Wavee;

/// <summary>What the warm override roster just did to one playable.</summary>
public enum OverrideMutationKind : byte
{
    Attach = 0,
    Replace = 1,
    Remove = 2,
}

/// <summary>The pure bridge plan for one override mutation — latch clears, whether to commit a has-video upgrade,
/// whether to force a same-kind video reload, and whether RevealIfCurrent may OpenAt after the commit.</summary>
public readonly record struct OverrideMutationPlan(
    bool ClearHasVideoLatch,
    bool ClearDeadVideoLatch,
    bool CommitHasVideoUpgrade,
    bool ForceReloadIfVideo,
    bool RevealSurfaceIfCurrent);

/// <summary>
/// Engine-free decision layer for local video-override mutations and the track-boundary / reveal rules that used to
/// live inline in <c>PlaybackBridge</c>. Extracted so Wavee.Tests can pin "attach must not clear the has-video latch"
/// and "null CurrentTrack is not a track boundary" without compiling the engine-bound bridge.
/// </summary>
public static class VideoOverrideMutationCore
{
    /// <summary>Plan the bridge side-effects for one attach/replace/remove.</summary>
    /// <param name="kind">What the roster just did.</param>
    /// <param name="isCurrentPlayable">Is the mutated uri the now-playing track?</param>
    /// <param name="videoAlreadyActive">Is the video surface already resolved (media should already be / stay Video)?</param>
    /// <param name="previousSourceKey">The resolved source key before this mutation (null = none published for this playable).</param>
    /// <param name="nextSourceKey">The override's source key after the mutation (null on remove).</param>
    public static OverrideMutationPlan Plan(
        OverrideMutationKind kind,
        bool isCurrentPlayable,
        bool videoAlreadyActive,
        string? previousSourceKey,
        string? nextSourceKey)
    {
        bool remove = kind == OverrideMutationKind.Remove;
        // Attach must NOT clear the has-video latch — that latch absorbs transient has=false / null-uri glitches so we
        // do not pay a Video→Audio→Video round trip. Only a real user removal ends it.
        bool clearHas = remove;
        // Dead latch always clears: attach/replace re-arms a prior failed open; remove drops the playable entirely.
        bool clearDead = true;
        bool commitUpgrade = true;   // override mutations are explicit user actions — never deferred
        // Force only when video is already live AND the source identity really changed (a replace). First attach that
        // flips Audio→Video is handled by the availability edge alone — forcing would double-load.
        bool sourceChanged = nextSourceKey is { Length: > 0 }
            && !string.Equals(previousSourceKey, nextSourceKey, System.StringComparison.Ordinal);
        bool force = !remove && isCurrentPlayable && videoAlreadyActive && sourceChanged;
        // Reveal after the has-video commit so OpenAt never runs against Available=None.
        bool reveal = !remove && isCurrentPlayable;
        return new OverrideMutationPlan(clearHas, clearDead, commitUpgrade, force, reveal);
    }

    /// <summary>Real track boundary for latch teardown. A null/empty next (or previous) uri is a mid-push glitch — the
    /// has-video latch's own null-suppression only works if we do NOT clear the latch first.</summary>
    public static bool IsRealTrackBoundary(string? previousUri, string? nextUri)
        => previousUri is { Length: > 0 }
           && nextUri is { Length: > 0 }
           && !string.Equals(previousUri, nextUri, System.StringComparison.Ordinal);

    /// <summary>Whether RevealIfCurrent may OpenAt: the playable is current, has-video is already committed, and the
    /// surface is not already active (opening an already-active surface is a no-op for media).</summary>
    public static bool CanReveal(bool isCurrent, bool hasVideoCommitted, bool alreadyActive)
        => isCurrent && hasVideoCommitted && !alreadyActive;
}
