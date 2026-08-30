namespace Wavee.Core;

public enum RepeatMode { Off, Context, Track }

/// <summary>A recoverable interruption affecting the active playback stream. This is intentionally coarse: byte-range
/// and retry details stay in diagnostics while playback surfaces only a user-meaningful state.</summary>
public enum PlaybackRecoveryKind { None, Network }

public readonly record struct PlaybackContextTrack(string Uri, string Uid = "", IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// The engine-authoritative shape of a LIVE broadcast's timeline: whether it is live at all, the seekable (DVR) window
/// it exposes, where the live edge is, and where the playhead sits inside it.
/// <para><b>Why a record and not three loose numbers.</b> "Live" is not one fact but four that must agree — a YouTube
/// channel with a 4-hour DVR window and a SHOUTcast station with no rewind are both live, and the difference decides
/// whether the rail scrubs or breathes. Publishing them together means the bar can never render a half-updated mix (a
/// window from the previous variant against this one's edge), which is exactly what a set of independent signals
/// produces at a variant switch.</para>
/// <para><b>Not inferred.</b> Every field is what the media pipeline STATED. A zero duration is not evidence of
/// live-ness (it is also what "unknown length" looks like), and a finite <c>GetDuration</c> is not evidence against it
/// (Media Foundation reports a sliding DVR window as a finite number — that is the defect this type exists to end).</para>
/// </summary>
/// <param name="IsLive">Is this a broadcast with a moving live edge?</param>
/// <param name="SeekableStartMs">The earliest position the source will accept, in ms.</param>
/// <param name="SeekableEndMs">The latest position the source will accept, in ms (the DVR window's right end).</param>
/// <param name="LiveEdgeMs">Where "now" is on the broadcast's own clock, in ms.</param>
/// <param name="PositionMs">Where the playhead sits, in ms, on the same clock.</param>
/// <param name="IsAtLiveEdge">Is the playhead riding the edge (as the source judges it)?</param>
public readonly record struct LiveWindow(
    bool IsLive,
    long SeekableStartMs,
    long SeekableEndMs,
    long LiveEdgeMs,
    long PositionMs,
    bool IsAtLiveEdge)
{
    /// <summary>The narrowest DVR window worth offering a rail for. Below this a scrub is a worse affordance than no
    /// scrub at all: the thumb would cover seconds per pixel and the window would slide out from under the gesture.</summary>
    public const long MinWindowMs = 30_000;

    /// <summary>Does this broadcast expose a REWINDABLE window (≥ <see cref="MinWindowMs"/>)? This — not
    /// <see cref="IsLive"/> — is what decides whether the seek bar is a DVR rail or a breathing line, and it is the
    /// whole of <c>CanSeek</c> while live.</summary>
    public bool HasWindow => SeekableEndMs - SeekableStartMs >= MinWindowMs;

    /// <summary>How far behind the live edge the playhead is, in ms (never negative).</summary>
    public long BehindMs => Math.Max(0, LiveEdgeMs - PositionMs);

    /// <summary>The window's span in ms (0 when there is none).</summary>
    public long WindowMs => Math.Max(0, SeekableEndMs - SeekableStartMs);

    /// <summary>"Not a live broadcast" — the honest default for every non-live playable.</summary>
    public static LiveWindow None => default;
}

/// <summary>Seek fidelity, carried end-to-end from the seek bar to the media host. <see cref="Keyframe"/> is a SCRUB
/// PREVIEW — the throttled seeks a drag issues while the thumb is moving: it snaps to the nearest keyframe (fast, cheap)
/// and is NOT a committed transport event, so it never reaches the Connect cluster and never re-arms prepared-next.
/// <see cref="Accurate"/> is the COMMIT — one per gesture, decoded to the exact PTS, emitted as a <c>Seeked</c> event.
/// <para>Member order mirrors <c>FluentGpu.Media.SeekMode</c> deliberately, but this enum is declared HERE because
/// Wavee.Core (and the source-included <c>Wavee/Backend</c>) are framework-neutral and must never reference the engine.
/// The app maps between the two at its one boundary (<c>MediaSeekInterop</c>).</para></summary>
public enum SeekMode : byte
{
    /// <summary>Snap to the nearest keyframe — a fast scrub PREVIEW; local host only, never a committed event.</summary>
    Keyframe,
    /// <summary>Decode to the exact requested position — the committed seek (one per gesture).</summary>
    Accurate
}

/// <summary>Playback command surface. The real implementation marshals these to the out-of-process
/// x64 AudioHost over a named pipe; the fake implementation is in-process. State is observed via
/// <see cref="IPlaybackState"/>, never returned from commands.</summary>
public interface IPlaybackPlayer
{
    Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default);
    Task PlayContextTrackAsync(string contextUri, PlaybackContextTrack track, int fallbackIndex = 0, CancellationToken ct = default);
    Task PlayOrderedAsync(string contextUri, IReadOnlyList<PlaybackContextTrack> tracks, int startIndex = 0, CancellationToken ct = default);
    Task PlayTrackAsync(string trackUri, CancellationToken ct = default);
    Task PlayTrackAsync(Track track, CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task NextAsync(CancellationToken ct = default);
    Task PreviousAsync(CancellationToken ct = default);
    /// <summary>Seek the current media. <paramref name="mode"/> decides whether this is a scrub PREVIEW
    /// (<see cref="SeekMode.Keyframe"/> — local host only) or the committed seek (<see cref="SeekMode.Accurate"/>).</summary>
    Task SeekAsync(long positionMs, SeekMode mode, CancellationToken ct = default);
    Task SetVolumeAsync(double volume01, CancellationToken ct = default);
    Task SetShuffleAsync(bool on, CancellationToken ct = default);
    Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default);
    /// <summary>Skip-in-place to a queue/history row by its stable session id — the queue-panel verb (Spotify parity: a
    /// cursor move within the live session, never a rebuild). Active device: session cursor move + fast-start. Viewer:
    /// forwards next_track with the target row. Idle: no-op.</summary>
    Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default);
    Task MoveQueueItemAsync(QueueItemId id, int newPos, CancellationToken ct = default);
    Task RemoveQueueItemAsync(QueueItemId id, CancellationToken ct = default);
    /// <summary>Drop the entire user queue (the queue panel's "Clear"). Active device: a local session op. Viewer: no-op
    /// (Spotify has no clear verb — the UI hides the button while a remote device is active).</summary>
    Task ClearQueueAsync(CancellationToken ct = default);
    /// <summary>Drop the local play history (the History section's "Clear"). Active device: a local session op; viewer: no-op.</summary>
    Task ClearHistoryAsync(CancellationToken ct = default);
    /// <summary>Append a track to the user-queue (the "Add to queue" affordance).</summary>
    Task EnqueueAsync(string trackUri, CancellationToken ct = default);
    /// <summary>Append a known track to the user-queue without discarding already-rendered metadata.</summary>
    Task EnqueueAsync(Track track, CancellationToken ct = default);
    /// <summary>Insert tracks at the FRONT of the user-queue ("play next" — before already-queued items).</summary>
    Task PlayNextAsync(IReadOnlyList<PlaybackContextTrack> tracks, CancellationToken ct = default);
    /// <summary>Insert tracks into the user-queue at a QUEUE-RELATIVE index (0 = play next, ≥ the queue length =
    /// append) — the drag-and-drop "drop at this slot" verb, of which <see cref="PlayNextAsync"/> is the index-0 case.
    /// <para>The default implementation is honest about what the two older verbs can do on their own: a front insert
    /// for slot 0, an append for anything else. A player that owns a real queue model (the live controller) overrides
    /// it with the exact insert.</para></summary>
    Task InsertIntoQueueAsync(IReadOnlyList<PlaybackContextTrack> tracks, int index, CancellationToken ct = default)
    {
        if (index <= 0) return PlayNextAsync(tracks, ct);
        return Append();

        async Task Append()
        {
            for (int i = 0; i < tracks.Count; i++)
                await EnqueueAsync(tracks[i].Uri, ct).ConfigureAwait(false);
        }
    }
    /// <summary>Start a radio seeded by a track/artist uri (Apple-Music-style): resolve the seed → a concrete radio
    /// playlist (<c>inspiredby-mix/v2/seed_to_playlist</c>), then either play it immediately (nothing playing) or PARK it
    /// as the new context so the currently-playing track finishes first and playback flows into the radio on track-end
    /// (the seed track is skipped if it leads the radio). Returns the resolved radio playlist uri (for the "Open playlist"
    /// toast), or <c>null</c> when no radio is available. Never interrupts the current track.</summary>
    Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default);
    IPlaybackState State { get; }

    /// <summary>Whether an OS suspend should pause THIS player right now (PowerBridge's suspend handler). True only
    /// for a session that is both routing locally AND has audible media (a live host clock) — a viewer session (some
    /// OTHER Connect device is active) must never forward a pause to that device just because this machine's OS is
    /// about to sleep. Default true preserves the old unconditional-pause behavior for any implementation with no
    /// local/remote distinction of its own (the logged-out stub, test shims).</summary>
    bool ShouldPauseOnSuspend => true;
}

/// <summary>Observable playback state. Position is authoritative-frame + 1 Hz interpolation
/// (mirrors the real IPC snapshot cadence — the UI interpolates per-frame between ticks).</summary>
public interface IPlaybackState : System.ComponentModel.INotifyPropertyChanged
{
    Track? CurrentTrack { get; }
    /// <summary>The URI of the context currently playing (the playlist/album/liked uri) — what a card compares its own
    /// uri against to show the now-playing equalizer. Null when nothing was started from a context.</summary>
    string? ContextUri { get; }
    bool IsPlaying { get; }
    bool IsBuffering { get; }
    /// <summary>A recoverable interruption currently being handled automatically by the local audio host.</summary>
    PlaybackRecoveryKind RecoveryKind => PlaybackRecoveryKind.None;
    long PositionMs { get; }
    long DurationMs { get; }
    double Volume { get; }
    bool IsShuffle { get; }
    RepeatMode Repeat { get; }
    IReadOnlyList<QueueEntry> Queue { get; }

    // ── Stage G — widened surface (default members so existing providers keep working; the live projection overrides) ────
    /// <summary>The initial track-resolve before audio begins — a loading affordance distinct from mid-stream buffering.</summary>
    bool IsLoading => false;
    /// <summary>A user-facing playback error (null = none); the player bar surfaces it + offers retry on the primary action.</summary>
    string? Error => null;
    /// <summary>Whether skip-next / skip-prev / seek are currently allowed (queue position / context restrictions — ads
    /// typically disallow skip + seek).</summary>
    bool CanSkipNext => true;
    bool CanSkipPrev => true;
    bool CanSeek => true;

    /// <summary>Is what is playing a LIVE stream — a broadcast with no end and no seekable past (an internet radio
    /// station, a YouTube/Twitch live channel)? Distinct from "<see cref="DurationMs"/> is 0", which is also what an
    /// unknown length looks like: live-ness is a fact the SOURCE stated, not something inferred from a missing number.
    /// <para>Drives the LIVE chip, the disabled seek bar and the "a socket drop is a reconnect, not a track end"
    /// policy. Default false — every provider that has no live content keeps working unchanged.</para></summary>
    bool IsLive => false;

    /// <summary>The live broadcast's TIMELINE — the DVR window, the live edge and where the playhead sits in it, as the
    /// media pipeline stated them. <c>default</c> (<see cref="LiveWindow.None"/>) means "not a live broadcast", which is
    /// the right answer for every ordinary track and the honest one for a provider that cannot tell.
    /// <para>This is strictly richer than <see cref="IsLive"/> and does not replace it: a source can state live-ness
    /// (a module's <c>isLive</c>, an ICY stream) long before — or without ever — knowing a window. Consumers that only
    /// need the LIVE chip read <see cref="IsLive"/>; the ones that draw a rail read this and branch on
    /// <see cref="LiveWindow.HasWindow"/>.</para></summary>
    LiveWindow Live => LiveWindow.None;
    /// <summary>The Connect device currently active (null/empty = this device / nobody) — drives the "playing on X" label.</summary>
    string? ActiveDeviceId => null;

    /// <summary>The bitrate of the stream ACTUALLY PLAYING, or 0 when it is not known. 0 is the honest default and the
    /// only correct answer for a provider that does not resolve wire metadata — it is not "assume 160".</summary>
    int StreamBitrateKbps => 0;

    /// <summary>A display name for the format actually playing ("FLAC", "Vorbis 320 kbps"), or null when unknown.
    /// <para>Both of these describe THE PLAYING STREAM and nothing else. They are deliberately not the user's quality
    /// PREFERENCE (which says what was asked for, not what arrived) and not a track's available format ladder (which
    /// says what exists, not what is decoding). A surface that showed either of those in their place would be stating
    /// something it cannot know.</para></summary>
    string? StreamFormat => null;

    /// <summary>Coarse "something changed" signal (track / play-state / queue / palette).</summary>
    IObservable<IPlaybackState> Changes { get; }

    /// <summary>Emits the current position in ms ~once per second while playing; re-anchors on track change.</summary>
    IObservable<long> PositionTicks { get; }
}

public enum DeviceKind { ThisDevice, Phone, Computer, Speaker, Tv }
public sealed record PlaybackDevice(string Id, string Name, DeviceKind Kind, bool IsActive, int VolumePercent);

/// <summary>Spotify Connect device list + transfer seam.</summary>
public interface IConnectDevices
{
    IReadOnlyList<PlaybackDevice> Devices { get; }
    IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged { get; }
    Task TransferAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>How tightly the lyric is timed — drives whether the view can do karaoke (Syllable), line-follow (Line),
/// or only static display (Unsynced/None). Set by the provider/normalizer.</summary>
public enum LyricsSyncKind { None, Unsynced, Line, Syllable }

public sealed record LyricSyllable(long StartMs, long EndMs, string Text);

/// <summary>One lyric line. <paramref name="Syllables"/> carry word/syllable timing when present (word-by-word).
/// The trailing args are additive (back-compat with positional <c>new LyricLine(start, text, syllables)</c> sites):
/// <paramref name="EndMs"/> is the line's end (else derived = next line's StartMs); <paramref name="Translation"/>
/// and <paramref name="Romanization"/> feed the multi-layer view; <paramref name="IsWordByWord"/> flags real syllable timing.</summary>
public sealed record LyricLine(
    long StartMs,
    string Text,
    IReadOnlyList<LyricSyllable> Syllables,
    long? EndMs = null,
    string? Translation = null,
    string? Romanization = null,
    bool IsWordByWord = false);

public sealed record LyricsDocument(
    string TrackId,
    bool IsSynced,
    IReadOnlyList<LyricLine> Lines,
    LyricsSyncKind Sync = LyricsSyncKind.Line,
    string? Provider = null,
    long OffsetMsApplied = 0);

public interface ILyricsProvider
{
    Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default);
}

/// <summary>Optional extension for providers that can return a fast usable lyric first, then publish a richer winner
/// when slower sources complete. Consumers must still call <see cref="ILyricsProvider.GetLyricsAsync"/> for the initial
/// document; this stream is only for same-track replacements with better timing/detail.</summary>
public interface IUpgradingLyricsProvider : ILyricsProvider
{
    IObservable<LyricsDocument> LyricsUpgraded { get; }
}
