using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.Backend;

// ── The AUDIO-HOST seam (the deferral boundary) ──────────────────────────────────────────────────────────────────────
// Everything UP TO this seam is in scope: Connect control plane, state projection, track resolution, audio-key fetch,
// storage-resolve. The seam receives a fully-resolved AudioStreamHandle (CDN + key + format) and reports a coalesced
// position clock + Ended. Implementations handle AES/native CDN decrypt, PCM decode, mixer/DSP, WASAPI output, and
// optional PlayPlay key derivation. The default impl in this scope is SilentAudioHost.

public enum AudioFormat { OggVorbis96, OggVorbis160, OggVorbis320, Flac, Flac24, Mp3, Aac }

/// <summary>How the host fetches/decrypts a body: the Spotify encrypted CDN path (AES-CTR / native PlayPlay), an
/// external plain-HTTP source (RSS/podcast, no decrypt), or a file on this device. Explicit so we never overload an
/// empty <c>Key</c> as a discriminator (empty Key still means "derive the PlayPlay key" on the Spotify path).
/// <para><see cref="LocalFile"/> carries its absolute path in <see cref="AudioStreamHandle.CdnUrl"/> — the same field
/// <see cref="ExternalPlain"/> uses for its URL — because both are "the one string that locates the bytes".</para></summary>
public enum AudioSourceKind
{
    /// <summary>Spotify's encrypted CDN body (AES-CTR / native PlayPlay decrypt).</summary>
    SpotifyEncrypted = 0,
    /// <summary>A plain-HTTP body of KNOWN, FINITE length (an RSS/podcast episode) — ranged, seekable.</summary>
    ExternalPlain = 1,
    /// <summary>A file on this device; the absolute path travels in <see cref="AudioStreamHandle.CdnUrl"/>.</summary>
    LocalFile = 2,
    /// <summary>An ENDLESS stream (internet radio / an ICY or otherwise live locator). Its URL travels in
    /// <see cref="AudioStreamHandle.CdnUrl"/> and its <c>DurationMs</c> is 0. Deliberately NOT
    /// <see cref="ExternalPlain"/>: that path assumes a finite, rangeable body and would buffer an endless one into
    /// memory. Live bodies are read forward-only, cannot seek, and a socket drop is a RECONNECT, not an end.</summary>
    LiveStream = 3,
    /// <summary>Bytes served by a playback MODULE over its <c>stream/open|read|close</c> RPC; the stream id travels in
    /// <see cref="AudioStreamHandle.CdnUrl"/>. Seekability and length come from the module's <c>stream/open</c> answer.</summary>
    ModuleStream = 4,
}

/// <summary>The user-facing streaming-quality preference (persisted as <c>playback.quality</c>) — the Spotify tier
/// ladder. The resolver aims at the chosen rung and falls back to the nearest available file (lower first), never to
/// silence. <see cref="Lossless"/> is reserved, not offered in the picker: the value stays in the enum because the
/// preference persists as an int and must never re-mean, but Settings ▸ Playback and the setup wizard both offer three
/// rungs and nothing selects it.</summary>
public enum AudioQualityPreference { Normal96 = 0, High160 = 1, VeryHigh320 = 2, Lossless = 3 }

/// <summary>Pure POD crossing the seam. An EMPTY <see cref="Key"/> means the host must derive it (PlayPlay path).</summary>
public readonly record struct AudioStreamHandle(
    string TrackUri, string FileIdHex, string CdnUrl,
    ReadOnlyMemory<byte> Key, AudioFormat Format, long DurationMs, float NormalizationGainDb,
    string[]? CdnUrls = null, int HeadBoundary = 0, ReadOnlyMemory<byte> NativeCdnSeed = default,
    AudioSourceKind SourceKind = AudioSourceKind.SpotifyEncrypted);

/// <summary>Instant-start payload: clear head bytes cross the seam before the key exists.</summary>
public readonly record struct AudioFastStart(
    string TrackUri, string FileIdHex, AudioFormat Format, long DurationMs, float NormalizationGainDb,
    ReadOnlyMemory<byte> HeadBytes);

/// <summary>The output of a fast-first resolve: the clear head is ready NOW (play immediately); the encrypted body
/// (key + CDN) is still resolving in <see cref="Body"/> and is supplied to the host when it lands.</summary>
public readonly record struct FastStartPlan(AudioFastStart Start, System.Threading.Tasks.Task<AudioStreamHandle> Body);

/// <summary>Fast-first resolver seam: return the head (instant-start) while the key + CDN resolve in parallel. When set on
/// the controller it supersedes the plain <see cref="ITrackResolver"/> path for local play. Implemented live by
/// FastTrackPlayback (SpotifyLive); the controller stays portable via this Backend interface.</summary>
public interface IFastTrackResolver
{
    System.Threading.Tasks.Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default);
}

public interface IFastTrackWarmer
{
    void Warm(Track track, string reason = "");
}

public enum AudioHostSignalKind { PositionTick, Ended, Buffering, Prebuffering, Playing, Paused, Recovering, Error }

/// <summary>The boxing-free, coalesced report channel from the host. State flags are explicit so play intent can remain
/// true while buffering, and a network-recovery state can coexist with either audible queued audio or a drained output.
/// The two-argument constructor preserves the old inferred-state call sites used by simple hosts and tests.</summary>
public readonly record struct AudioHostSignal
{
    public AudioHostSignalKind Kind { get; init; }
    public long PositionMs { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsBuffering { get; init; }
    public bool IsPrebuffering { get; init; }
    public PlaybackRecoveryKind RecoveryKind { get; init; }
    public AudioKeyFailureReason FailureReason { get; init; }
    public string? Detail { get; init; }

    public AudioHostSignal(AudioHostSignalKind kind, long positionMs)
    {
        Kind = kind;
        PositionMs = positionMs;
        IsPlaying = kind is AudioHostSignalKind.Playing or AudioHostSignalKind.PositionTick or AudioHostSignalKind.Recovering;
        IsBuffering = kind == AudioHostSignalKind.Buffering;
        IsPrebuffering = kind == AudioHostSignalKind.Prebuffering;
        RecoveryKind = kind == AudioHostSignalKind.Recovering ? PlaybackRecoveryKind.Network : PlaybackRecoveryKind.None;
        FailureReason = AudioKeyFailureReason.None;
        Detail = null;
    }

    public AudioHostSignal(AudioHostSignalKind kind, long positionMs, bool isPlaying, bool isBuffering,
        bool isPrebuffering, PlaybackRecoveryKind recoveryKind = PlaybackRecoveryKind.None,
        AudioKeyFailureReason failureReason = AudioKeyFailureReason.None, string? detail = null)
    {
        Kind = kind;
        PositionMs = positionMs;
        IsPlaying = isPlaying;
        IsBuffering = isBuffering;
        IsPrebuffering = isPrebuffering;
        RecoveryKind = recoveryKind;
        FailureReason = failureReason;
        Detail = detail;
    }

    public static AudioHostSignal Fault(long positionMs, AudioKeyFailureReason reason, string? detail = null) =>
        new(AudioHostSignalKind.Error, positionMs, false, false, false, PlaybackRecoveryKind.None, reason, detail);
}

/// <summary>The COMMON current-media host surface (Milestone B): the transport + clock verbs shared by BOTH the audio
/// host and the video-media host, so the app's ONE current media can be swapped between them under a single clock. LOADING
/// is kind-specific and deliberately NOT here — audio loads via <see cref="IAudioHost"/> (<see cref="IAudioHost.Load"/> /
/// <see cref="IAudioHost.LoadFastStart"/> / <see cref="IAudioHost.SupplyBody"/>), video via its own host's video load.
/// Both hosts report on the same <see cref="AudioHostSignal"/> channel so the source-agnostic projection is unchanged.</summary>
public interface IMediaHost : IAsyncDisposable
{
    void Play();
    void Pause();
    void Stop();
    /// <summary>Reposition the current media. <paramref name="mode"/> carries the seek FIDELITY end-to-end:
    /// <see cref="SeekMode.Keyframe"/> is a throttled scrub PREVIEW (snap to the nearest keyframe, cheap, repeated many
    /// times per drag) and <see cref="SeekMode.Accurate"/> is the single committed seek at the end of the gesture. A host
    /// whose backend has no keyframe fast path (the PCM audio host) may treat both identically — but it must ACCEPT the
    /// mode rather than have the caller drop it, so the distinction survives to whichever host CAN honour it.</summary>
    void Seek(long positionMs, SeekMode mode);
    void SetVolume(double volume01);                  // realtime, host-side (buffered-PCM-independent)
    long PositionMs { get; }
    bool IsPlaying { get; }
    /// <summary>Whether <see cref="PositionMs"/> is a real reading right now. A host with no loaded session (or a
    /// session it just tore down) reports <c>0</c> from <see cref="PositionMs"/> for lack of anything else to say — that
    /// 0 means UNKNOWN, never "at the top of the track". Callers that publish position as fact (the controller's
    /// EmitState/EmitSnap, the volume paths that stamp the projection's timeline) must check this first and fall back
    /// to their own projected position instead of trusting a stale/absent clock.</summary>
    bool ClockValid { get; }
    IObservable<AudioHostSignal> Signals { get; }     // the clock + Ended report
}

/// <summary>The reshaped audio seam (replaces the old <c>IAudioEngine</c> in Stage E). Takes a resolved handle, not a
/// bare Track — resolution lives in front of the seam (the controller), in scope. Extends the common
/// <see cref="IMediaHost"/> with the AUDIO-specific loading verbs (the video host does not have these).</summary>
public interface IAudioHost : IMediaHost
{
    void Load(in AudioStreamHandle stream);
    void LoadFastStart(in AudioFastStart start);
    void SupplyBody(in AudioStreamHandle body);
    bool IsBuffering { get; }
    /// <summary>Whether the user has actually asked to hear this — true from <see cref="IMediaHost.Play"/> until the
    /// next <see cref="IMediaHost.Pause"/>/<see cref="IMediaHost.Stop"/>, independent of whether audio is audibly
    /// flowing yet. A <c>Load</c>/<c>LoadFastStart</c> for a paused (launch-recovery) restore never touches this, so
    /// it stays whatever it already was — false for a fresh host. The controller reads it (never <see cref="IMediaHost.IsPlaying"/>,
    /// which lags behind while genuinely buffering) to decide whether a stray transient-buffering flag is safe to
    /// clear (PlaybackController.SupplyBodyWhenReadyAsync) — see the buffering-bar-on-a-paused-restored-track fix.
    /// Defaults false (the conservative "OK to clear" answer) so every existing <see cref="IAudioHost"/> fake that has
    /// no reason to track it — none of them drive the one call site that reads this through a real Play() — keeps
    /// compiling untouched; the two real hosts and the shared test recorder override it properly.</summary>
    bool PlayIntent => false;
}

public interface IAudioDspControl
{
    void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb, float preampDb = 0f);
    void SetCrossfade(bool enabled, int durationMs);
}

/// <summary>Optional host capability (the <see cref="IAudioDspControl"/> precedent — discovered by interface, never a
/// new member on the core <see cref="IAudioHost"/> seam): a source whose NOW-PLAYING metadata arrives in-band, after the
/// track is already loaded. Internet radio is the case that forces it — the ICY <c>StreamTitle</c> block that names the
/// current song only appears mid-stream and changes every few minutes, so there is no resolve-time answer to project.
/// <para>Arguments are <c>(streamTitle, stationName)</c>: the raw title exactly as the station wrote it (split with
/// <c>IcyMetadata.SplitStreamTitle</c>), and the station's own name as the fallback attribution.</para></summary>
public interface ILiveMetadataSource
{
    event Action<string, string?>? MetadataKnown;
}

/// <summary>A stable, controller-minted description of the exact session item prepared after the active track.</summary>
public readonly record struct AudioPrepareRequest(
    string Token,
    AudioFastStart Start,
    bool AllowOverlap);

/// <summary><see cref="Invalidated"/> (device-reopen gapless fix, A2): the prepared-next slot was built for a mixer
/// rate that no longer matches the live session (a mid-track output-device/format soft reload rebuilt the session at a
/// new rate) and has already been disposed by the host — the controller must re-run <c>SchedulePreparedNext</c> for the
/// same upcoming item exactly as it does for <see cref="Missed"/>, this time because the OLD prepare is provably stale
/// rather than because none ever landed.</summary>
public enum AudioTransitionKind { Started, Completed, Missed, Invalidated }

/// <summary>Host-to-controller hand-off notification. Tokens make stale async resolves harmless after queue edits.</summary>
public readonly record struct AudioTransitionSignal(
    AudioTransitionKind Kind,
    string Token,
    string TrackUri,
    long PositionMs,
    int EffectiveFadeMs = 0,
    string? Reason = null);

public enum AudioPrepareCancelResult { Cancelled, AlreadyStarted, NotFound }

/// <summary>
/// Optional prepared-next capability. Manual next/row-click continues to use the active load API and stays immediate;
/// this seam is consumed only for a natural-end hand-off.
/// </summary>
public interface IPreparedAudioHost
{
    Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default);
    Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default);
    Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default);
    IObservable<AudioTransitionSignal> Transitions { get; }
}

// ── Output-device control (Phase A/B) — an OPTIONAL host capability discovered by interface (the IAudioDspControl
//    precedent): implemented by both real hosts, NOT by SilentAudioHost. Keeps the core IAudioHost seam untouched. ──────
public enum OutputDeviceNoticeKind { DeviceLost, SwitchedToDefault, DeviceRestored, OutputFailed }

/// <summary>A user-facing device event (toast). <see cref="DeviceName"/> is best-effort (the device may be gone).</summary>
public readonly record struct OutputDeviceNotice(OutputDeviceNoticeKind Kind, string DeviceId, string DeviceName, bool WasExplicit);

/// <summary>Optional host capability: choose the WASAPI output endpoint + reflect Windows session volume/mute. The audio
/// stack routes/persists/toasts through this; hosts without it (SilentAudioHost / fake backends) simply don't expose it,
/// and the UI hides the affordances.</summary>
public interface IAudioOutputDeviceControl
{
    void SetOutputDevice(string? deviceId);              // null or empty = system default
    void SetOutputMuted(bool muted);                     // Phase B
    event Action<OutputDeviceNotice>? OutputDeviceNotice;
    event Action<double, bool>? ExternalVolumeChanged;   // Phase B (slider01, muted)
}

/// <summary>The default in-scope host: a SILENT renderer that reports synthetic position/Ended with zero decrypt/decode/
/// output, so the whole control-plane → resolve → host → projection → UI pipeline runs and is testable headlessly today.
/// Position uses the same wall-clock anchor math the UI seekbar uses (AnchorPos + (now − AnchorWall)). Ticks fire only
/// while playing, honouring the engine's zero-frames-when-paused guardrail.</summary>
public sealed class SilentAudioHost : IAudioHost
{
    readonly Func<long> _now;
    readonly SimpleEvent<AudioHostSignal> _signals = new();
    readonly object _gate = new();
    long _anchorWall, _anchorPos, _durationMs;
    bool _playing, _buffering;
    Timer? _ticker;

    public SilentAudioHost(Func<long>? clock = null) => _now = clock ?? (() => Environment.TickCount64);

    public long PositionMs { get { lock (_gate) return Pos(); } }
    public bool IsPlaying { get { lock (_gate) return _playing; } }
    public bool IsBuffering { get { lock (_gate) return _buffering; } }
    // The synthetic clock is never stale — it has no real session to lose, so 0 here is always a genuine position.
    public bool ClockValid => true;
    // A silent host has no separate "asked to hear it" moment distinct from actually playing (Load/SupplyBody's own
    // Buffering window is cleared synchronously, right below, before this could ever be read mid-buffer) — _playing
    // IS the play intent here.
    public bool PlayIntent { get { lock (_gate) return _playing; } }
    public IObservable<AudioHostSignal> Signals => _signals;

    long Pos() => _playing ? Math.Min(_durationMs <= 0 ? long.MaxValue : _durationMs, _anchorPos + (_now() - _anchorWall)) : _anchorPos;

    public void Load(in AudioStreamHandle s)
    {
        lock (_gate) { _anchorPos = 0; _anchorWall = _now(); _durationMs = s.DurationMs; _playing = false; _buffering = true; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
        lock (_gate) _buffering = false;
    }

    public void LoadFastStart(in AudioFastStart s)
    {
        lock (_gate) { _anchorPos = 0; _anchorWall = _now(); _durationMs = s.DurationMs; _playing = false; _buffering = false; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
    }

    public void SupplyBody(in AudioStreamHandle body)
    {
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
        lock (_gate) _buffering = false;
    }

    public void Play()
    {
        lock (_gate) { _anchorWall = _now(); _playing = true; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Playing, PositionMs));
        StartTicker();
    }

    public void Pause()
    {
        lock (_gate) { _anchorPos = Pos(); _playing = false; }
        StopTicker();
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Paused, PositionMs));
    }

    public void Stop()
    {
        lock (_gate) { _anchorPos = 0; _playing = false; }
        StopTicker();
    }

    // A silent host has no decoder, so keyframe and accurate are the same wall-clock anchor move; the mode is still
    // accepted at the seam so it is never dropped at the boundary.
    public void Seek(long ms, SeekMode mode)
    {
        lock (_gate) { _anchorPos = _durationMs > 0 ? Math.Clamp(ms, 0, _durationMs) : Math.Max(0, ms); _anchorWall = _now(); }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.PositionTick, PositionMs));
    }

    public void SetVolume(double volume01) { /* silent host: real host-side volume lands with the real host */ }

    void StartTicker() { StopTicker(); _ticker = new Timer(_ => Tick(), null, 1000, 1000); }
    void StopTicker() { _ticker?.Dispose(); _ticker = null; }

    void Tick()
    {
        long pos; bool playing, ended;
        lock (_gate) { pos = Pos(); playing = _playing; ended = _durationMs > 0 && pos >= _durationMs; }
        if (!playing) return;
        if (ended)
        {
            lock (_gate) { _playing = false; _anchorPos = _durationMs; }
            StopTicker();
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
        }
        else _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.PositionTick, pos));
    }

    public ValueTask DisposeAsync() { StopTicker(); return ValueTask.CompletedTask; }
}
