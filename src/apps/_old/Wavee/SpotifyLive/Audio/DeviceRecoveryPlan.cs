namespace Wavee.SpotifyLive.Audio;

/// <summary>What the host does with the live session after the engine swapped the output sink underneath it (#112).</summary>
internal enum DeviceRecoveryAction
{
    /// <summary>The device rate changed and the body can be reopened: build a NEW session/graph at the live rate and
    /// restore the playhead (the <c>SoftReloadAsync</c> success path).</summary>
    ReopenNewGraph,
    /// <summary>Same rate, reopenable body: the existing graph can still render on the new sink — reopening is optional
    /// (this host reopens anyway; the HEAD host adopts the session in place).</summary>
    AdoptIntoExistingGraph,
    /// <summary>Same rate, body not reopenable: the old session is still able to render — keep it and make sure it is
    /// audible (a swap can leave the transport parked).</summary>
    KeepSession,
    /// <summary>Rate changed, body not reopenable: the engine keeps the session silent until a new graph exists and the
    /// host cannot build one in place — surface an honest Fault so <c>PlaybackController</c> records its failure
    /// checkpoint and offers Retry (a reload through the ordinary load path binds a new graph at the live rate).</summary>
    ReloadThroughController,
}

/// <summary>The pure decision behind a device-format recovery (#112), split out of <c>FluentMediaAudioHost</c> the same way
/// <see cref="GaplessJoinClock"/> is so it has a name and a test. On the current engine a rate-changed
/// <c>PcmAudioSession.RebuildSink</c> latches <c>RequiresGraphRebuild</c> one-way — the mixer/decoder graph bound at prepare
/// time cannot render on the new device and the session stays SILENT until a new graph is built — so "leave the old session
/// playing" is only a benign no-op for a same-rate swap. Every early exit of the soft reload routes through
/// <see cref="Decide"/> so a rate change ends in an audible session or a Retry, never in silence until the next track.</summary>
internal static class DeviceRecoveryPlan
{
    /// <summary>What to do after the engine swapped the output sink.</summary>
    /// <param name="requiresGraphRebuild">The new device's rate differs and the mixer/decoder graph bound at prepare time
    /// cannot render on it (the engine keeps the session silent until a new graph exists).</param>
    /// <param name="canReopen">The active body can be reopened at the saved position (a Spotify CDN body — SourceKind
    /// SpotifyEncrypted with a ReopenBody and a live independent stream).</param>
    public static DeviceRecoveryAction Decide(bool requiresGraphRebuild, bool canReopen)
        => canReopen ? (requiresGraphRebuild ? DeviceRecoveryAction.ReopenNewGraph : DeviceRecoveryAction.AdoptIntoExistingGraph)
         : requiresGraphRebuild ? DeviceRecoveryAction.ReloadThroughController : DeviceRecoveryAction.KeepSession;
}
