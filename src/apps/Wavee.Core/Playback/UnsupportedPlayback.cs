using System.ComponentModel;

namespace Wavee.Core;

/// <summary>
/// The remote-only player: LOCAL audio playback is not supported yet. Every PLAY intent raises
/// <see cref="OnPlayIntentRejected"/> synchronously (the composition root wires it to the critical
/// "choose a remote device" toast + the device-picker action); every other verb no-ops. State is
/// permanently empty. This replaces the old in-process FakePlaybackProvider as the pre-login / offline
/// player — after a live login the switchable facade swaps in the real <c>PlaybackController</c>, which
/// forwards playback to the active Connect device (and rejects LOCAL play with the same toast).
/// </summary>
public sealed class UnsupportedPlaybackPlayer : IPlaybackPlayer, IPlaybackState
{
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<long> _ticks = new();

    /// <summary>Raised (synchronously, on the caller's thread) whenever a play intent is attempted. The app layer wires
    /// this to the "playback on this device isn't supported yet — choose a remote device" toast; null = silent.</summary>
    public Action? OnPlayIntentRejected { get; set; }

    // ── the LOCAL half: playables that need no session ──────────────────────────────────────────────────────────────
    // "Remote-only" was never true of every playable — a file on this disk, an internet radio station and a playback
    // MODULE's link all resolve with no account, no token and no cluster. What genuinely needs a live Spotify session
    // is a spotify: uri, and that is the only thing this player still refuses.
    //
    // Both seams arrive together from the composition root or not at all: <see cref="LocalPlayback"/> is the real local
    // player (an audio-host-backed PlaybackController) and <see cref="CanPlayLocally"/> is the routing question its
    // provider registry answers ("does anything own this uri?"). With neither set this class behaves exactly as it
    // always did — which is what a backend with no audio stack (the fake wiring) honestly is.

    /// <summary>The local player for playables that need no session, or null when this build has no audio stack.</summary>
    public IPlaybackPlayer? LocalPlayback { get; set; }

    /// <summary>"Can this uri be played locally, right now?" — the provider registry's ownership answer. Null means no.</summary>
    public Func<string, bool>? CanPlayLocally { get; set; }

    /// <summary>The local player's state when one is wired (so the player bar shows a locally-playing radio station or
    /// module link), else the permanently-empty state below.</summary>
    public IPlaybackState State => LocalPlayback?.State ?? this;

    // ── IPlaybackState — permanently empty (nothing ever plays locally) ─────────────────────────────────────────────
    public Track? CurrentTrack => null;
    public string? ContextUri => null;
    public bool IsPlaying => false;
    public bool IsBuffering => false;
    public long PositionMs => 0;
    public long DurationMs => 0;
    public double Volume => 0.7;                     // matches the bridge's default so the volume slider doesn't jump
    public bool IsShuffle => false;
    public RepeatMode Repeat => RepeatMode.Off;
    public IReadOnlyList<QueueEntry> Queue => Array.Empty<QueueEntry>();
    // No skipping / seeking when nothing can play (the widened surface; the player bar disables them via NoTrack anyway).
    public bool IsLoading => false;
    public string? Error => null;
    public bool CanSkipNext => false;
    public bool CanSkipPrev => false;
    public bool CanSeek => false;
    public string? ActiveDeviceId => null;
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<long> PositionTicks => _ticks;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }   // consumers use Changes

    static readonly Task Done = Task.CompletedTask;
    Task Reject() { OnPlayIntentRejected?.Invoke(); return Done; }

    /// <summary>Is this uri playable without a session? False when either seam is unwired.</summary>
    /// <param name="uri">The playable uri.</param>
    public bool IsLocallyPlayable(string? uri)
        => LocalPlayback is not null && uri is { Length: > 0 } u && (CanPlayLocally?.Invoke(u) ?? false);

    // ── IPlaybackPlayer — a locally-playable uri goes to the local player; everything else rejects ──────────────────
    // A CONTEXT (a playlist, an album) is always remote: nothing local has one. Only the two single-playable verbs and
    // the queue verbs route locally, which is exactly the surface "Play file…" / "Play ▸ Link…" use.
    public Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default) => Reject();
    public Task PlayContextTrackAsync(string contextUri, PlaybackContextTrack track, int fallbackIndex = 0, CancellationToken ct = default) => Reject();
    public Task PlayOrderedAsync(string contextUri, IReadOnlyList<PlaybackContextTrack> tracks, int startIndex = 0, CancellationToken ct = default) => Reject();

    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default)
        => IsLocallyPlayable(trackUri) ? LocalPlayback!.PlayTrackAsync(trackUri, ct) : Reject();

    public Task PlayTrackAsync(Track track, CancellationToken ct = default)
        => IsLocallyPlayable(track.Uri) ? LocalPlayback!.PlayTrackAsync(track, ct) : Reject();

    public Task ResumeAsync(CancellationToken ct = default)
        => LocalPlayback is { } p ? p.ResumeAsync(ct) : Reject();

    public Task EnqueueAsync(string trackUri, CancellationToken ct = default)
        => IsLocallyPlayable(trackUri) ? LocalPlayback!.EnqueueAsync(trackUri, ct) : Reject();

    public Task EnqueueAsync(Track track, CancellationToken ct = default)
        => IsLocallyPlayable(track.Uri) ? LocalPlayback!.EnqueueAsync(track, ct) : Reject();

    public Task PlayNextAsync(IReadOnlyList<PlaybackContextTrack> tracks, CancellationToken ct = default) => Reject();

    // Radio is a Spotify play intent → fire the "choose a remote device" prompt and report "no radio" (null) so the
    // caller shows the graceful "couldn't start radio" affordance rather than a phantom "Radio started".
    public Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default)
    { OnPlayIntentRejected?.Invoke(); return Task.FromResult<string?>(null); }

    // Transport verbs act on whatever is CURRENTLY playing — locally, that is the local player; with none wired there
    // is nothing playing and a no-op is the honest answer (unchanged behaviour).
    public Task PauseAsync(CancellationToken ct = default) => LocalPlayback?.PauseAsync(ct) ?? Done;
    public Task NextAsync(CancellationToken ct = default) => LocalPlayback?.NextAsync(ct) ?? Done;
    public Task PreviousAsync(CancellationToken ct = default) => LocalPlayback?.PreviousAsync(ct) ?? Done;
    public Task SeekAsync(long positionMs, SeekMode mode, CancellationToken ct = default) => LocalPlayback?.SeekAsync(positionMs, mode, ct) ?? Done;
    public Task SetVolumeAsync(double volume01, CancellationToken ct = default) => LocalPlayback?.SetVolumeAsync(volume01, ct) ?? Done;
    public Task SetShuffleAsync(bool on, CancellationToken ct = default) => LocalPlayback?.SetShuffleAsync(on, ct) ?? Done;
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default) => LocalPlayback?.SetRepeatAsync(mode, ct) ?? Done;
    public Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default) => LocalPlayback is { } p ? p.SkipToQueueItemAsync(id, ct) : Reject();
    public Task MoveQueueItemAsync(QueueItemId id, int newPos, CancellationToken ct = default) => LocalPlayback?.MoveQueueItemAsync(id, newPos, ct) ?? Done;
    public Task RemoveQueueItemAsync(QueueItemId id, CancellationToken ct = default) => LocalPlayback?.RemoveQueueItemAsync(id, ct) ?? Done;
    public Task ClearQueueAsync(CancellationToken ct = default) => LocalPlayback?.ClearQueueAsync(ct) ?? Done;
    public Task ClearHistoryAsync(CancellationToken ct = default) => LocalPlayback?.ClearHistoryAsync(ct) ?? Done;
}

/// <summary>The empty Connect roster (pre-login / logged out). Real devices arrive only from the live Connect cluster
/// after login — before that the device picker shows its empty state.</summary>
public sealed class NoConnectDevices : IConnectDevices
{
    readonly SimpleSubject<IReadOnlyList<PlaybackDevice>> _changed = new(Array.Empty<PlaybackDevice>());
    public IReadOnlyList<PlaybackDevice> Devices => Array.Empty<PlaybackDevice>();
    public IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged => _changed;
    public Task TransferAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>No lyrics (replaces the old fake's Lyrics facet). The live <c>AggregatingLyricsProvider</c> swaps in on login.</summary>
public sealed class NoLyricsProvider : ILyricsProvider
{
    public Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
        => Task.FromResult<LyricsDocument?>(null);
}
