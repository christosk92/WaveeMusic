namespace Wavee;

/// <summary>
/// The Connect-wire half of the same rule: which video facts the cluster has ALREADY been told about the current track.
///
/// <para>A badge-only association land (see <see cref="VideoUpgradeGate"/>) changes no media host and fires no playback
/// event, so nothing would otherwise re-publish the player state — remote controllers would never see the
/// <c>associated_video_id</c> / <c>switch-to-video</c> offer for the song they are watching us play. This tracker turns
/// that land into EXACTLY ONE extra PutState: the FIRST observation of a track is only a baseline (its facts ride the
/// track-change PutState the publisher already sends), and afterwards only a real gain — has-video false→true, or a gid
/// arriving/changing — announces. Idempotent re-observations are silent.</para>
/// </summary>
public sealed class ConnectVideoFacts
{
    string? _uri;
    bool _hasVideo;
    string? _gidHex;

    /// <summary>Fold the current track's video facts. Returns true iff a PLAYER_STATE_CHANGED PutState must be enqueued.</summary>
    public bool Observe(string? trackUri, bool hasVideo, string? videoGidHex)
    {
        bool sameTrack = string.Equals(trackUri, _uri, System.StringComparison.Ordinal);
        bool gained = sameTrack
            && ((hasVideo && !_hasVideo)
                || (videoGidHex is { Length: > 0 } && !string.Equals(videoGidHex, _gidHex, System.StringComparison.Ordinal)));
        _uri = trackUri;
        _hasVideo = hasVideo;
        _gidHex = videoGidHex;
        return gained;
    }
}

/// <summary>
/// The pure half of the "no mid-track auto-swap" rule (the engine-free decision layer <c>PlaybackBridge</c> folds).
///
/// <para>A music-video association is detected ASYNCHRONOUSLY, so it routinely lands while the song is already playing.
/// Committing that as an availability UPGRADE swaps the media host and restarts the track at position 0 — the reported
/// "it jumped back to the start on its own". The product rule (and the wire behavior of the reference desktop client) is
/// the opposite: the badge lights, playback stays exactly where it is, and the user's click is what starts the video.
/// DOWNGRADES are never deferred — a video-less track, a proven-dead playable and the ✕ must unmount immediately.</para>
/// </summary>
public static class VideoUpgradeGate
{
    /// <summary>Content availability × HOST capability → the placement set. A playable WITHOUT a video makes every
    /// placement unavailable; one WITH a video is further masked by what the host can actually do right now (can the
    /// rail fit it, can a second window/swapchain open, does the fullscreen hook exist) — the same bit-set carries
    /// both "this track has no video" and "this host cannot do that placement right now" through the exact same
    /// path.</summary>
    public static PlacementSet AvailabilityFor(bool hasVideo, PlacementSet hostCapable)
        => hasVideo ? PlacementPolicy.Video.Allowed & hostCapable : PlacementSet.None;

    /// <summary>Re-stamp a state with the availability THIS playable actually has, masked by <paramref name="hostCapable"/>.
    /// Required before acting on a user intent: a deferred upgrade leaves <c>Available</c> stale at
    /// <see cref="PlacementSet.None"/>, and both <c>PlacementCore.Resolve</c> and <c>IsActive</c> consult it — so a lit
    /// badge's toggle would otherwise resolve to <see cref="SurfacePlacement.None"/> and do nothing at all.</summary>
    public static PlacementState FoldAvailability(in PlacementState s, bool hasVideo, PlacementSet hostCapable)
        => PlacementCore.WithAvailability(s, AvailabilityFor(hasVideo, hostCapable));

    /// <summary>True when <paramref name="target"/> would turn an inactive surface ON and the caller is not entitled to
    /// commit that (i.e. it is neither a track boundary nor an explicit user action). The badge still updates; only the
    /// surface commit — and with it the media-kind refresh and the pop-out warm — is withheld.</summary>
    public static bool DeferUpgrade(in PlacementState before, in PlacementState target, bool commitUpgrade)
        => !commitUpgrade && !PlacementCore.IsActive(before) && PlacementCore.IsActive(target);

    /// <summary>The primary affordance's next state, folded onto this playable's real availability.
    /// <para>A deferred upgrade is why this is not simply <c>TogglePrimary(FoldAvailability(...))</c>: after a mid-track
    /// land the standing <c>Requested</c> intent is still ON while the surface is deliberately off, so the naive fold
    /// would read the state as "already watching" and the user's FIRST click would turn video off — they would have to
    /// click twice to start it. The click therefore COMMITS exactly what <see cref="DeferUpgrade"/> withheld; every other
    /// state toggles as before (and the player bar agrees, because it draws its lit state from the un-folded state).</para></summary>
    public static PlacementState PrimaryClick(in PlacementState s, bool hasVideo, PlacementSet hostCapable)
    {
        var folded = FoldAvailability(s, hasVideo, hostCapable);
        return DeferUpgrade(s, folded, commitUpgrade: false) ? folded : PlacementCore.TogglePrimary(folded);
    }
}
