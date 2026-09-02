using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Windows.Wasapi;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.Sdk.Streams;

// Both namespaces declare a SeekMode: the app's transport enum (Wavee.Core, framework-neutral, what the IMediaHost seam
// speaks) and the engine's (FluentGpu.Media, what the session's SeekAsync takes). Aliased rather than fully qualified so
// every use below reads unambiguously.
using SeekMode = Wavee.Core.SeekMode;
using EngineSeekMode = FluentGpu.Media.SeekMode;

namespace Wavee.SpotifyLive.Audio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The ONE real audio host (Milestone M6). Implements the app's IAudioHost seam over the unified FluentGpu.Media engine:
// PcmAudioPlayer (the graph — mixer/DSP/limiter/clock) + WasapiPcm (the device leaf) with an APP-supplied IAudioDecoder
// factory (Vorbis/FLAC/MP3) plugged into the engine's decode edge. Encrypted-stream FETCH + DECRYPT + head/body fast-start
// reuse the kept app seams (SpotifyAudioStream + SpotifyAesCtr + the PlayPlay CdnDecryptor) verbatim, in-proc; the engine
// owns decode→mix→output. This REPLACES the old AudioPlayEngine/DecodePipeline/WasapiRenderer/InProcessAudioHost path.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The app AES-CTR primitive behind the engine's <see cref="ICtrCipher"/> seam (spec §5.4): the counter is
/// re-derived from the byte offset per call, so any range decrypts without replay. Reuses <see cref="SpotifyAesCtr"/> —
/// the exact in-proc decrypt the old path used.</summary>
public sealed class SpotifyCtrCipher : ICtrCipher
{
    private readonly byte[] _key;
    private long _pos;

    public SpotifyCtrCipher(ReadOnlyMemory<byte> key) => _key = key.ToArray();

    public void SeekCounter(long bytePosition) => _pos = bytePosition;

    public void XorInPlace(Span<byte> buffer)
    {
        SpotifyAesCtr.DecryptInPlace(buffer, _key, _pos);
        _pos += buffer.Length;
    }
}

/// <summary>Resolves an <see cref="AudioKey"/> for a track behind the engine's <see cref="IAudioKeyProvider"/> seam, over
/// the app's <see cref="AudioKeyResolver"/>. NOTE: in the live flow the key is pre-resolved during track resolution and
/// delivered on the <see cref="AudioStreamHandle"/> (the engine contract — "prefetched at Prepare time, never inside a
/// read"), so this adapter is the portable-seam form; the hot path consumes the handle-carried key directly.</summary>
public sealed class WaveeAudioKeyProvider : IAudioKeyProvider
{
    private readonly AudioKeyResolver _resolver;
    private readonly Func<string, (ReadOnlyMemory<byte> FileId, ReadOnlyMemory<byte> Gid)?> _lookup;

    public WaveeAudioKeyProvider(AudioKeyResolver resolver,
        Func<string, (ReadOnlyMemory<byte> FileId, ReadOnlyMemory<byte> Gid)?> fileLookup)
    { _resolver = resolver; _lookup = fileLookup; }

    public async ValueTask<AudioKey> ResolveKeyAsync(FluentGpu.Foundation.StringId trackUri, CancellationToken ct)
    {
        var id = _lookup(trackUri.ToString() ?? "");
        if (id is not { } ids) throw new InvalidOperationException("no resolved file id for " + trackUri);
        var key = await _resolver.GetKeyAsync(ids.FileId, ids.Gid, ct).ConfigureAwait(false);
        return new AudioKey(key);
    }
}

/// <summary>The decoder kind for a Spotify/podcast file or a live stream.</summary>
internal enum WaveeDecoderKind { Vorbis, Flac, Mp3, Aac }

/// <summary>The fast-start bridge (spec §5.1) — the engine's <see cref="IMediaByteSource"/> front door. Carries ONE kept
/// <see cref="IAudioReadStream"/> (a <see cref="SpotifyAudioStream"/> whose clear head is present from <c>LoadFastStart</c>
/// and whose encrypted body is attached later by <c>SupplyBody</c>, or a <see cref="PlainHttpAudioStream"/> for external
/// podcasts) plus the codec kind/duration/gain the decoder needs. The engine passes THIS to the injected decoder's
/// <c>TryOpen</c>, which pulls the decoded stream via <see cref="OpenDecodeStream"/>. Decrypt happens inside the kept
/// stream (in-proc) — invisible above this seam.</summary>
internal sealed class SpotifyMediaByteSource : IMediaByteSource
{
    private readonly IAudioReadStream _stream;
    private readonly int _skipOffset;

    public SpotifyMediaByteSource(IAudioReadStream stream, int skipOffset, WaveeDecoderKind kind, long durationMs, float gainLinear)
    { _stream = stream; _skipOffset = skipOffset; Kind = kind; DurationMs = durationMs; GainLinear = gainLinear; }

    public WaveeDecoderKind Kind { get; }
    public long DurationMs { get; }
    public float GainLinear { get; }

    /// <summary>The container skip offset (the <see cref="SkipStream"/> logical-0). Retained so a mid-track device-rate
    /// soft reload can rebuild a FRESH independent stream+source with the same offset (see FluentMediaAudioHost.SoftReloadAsync).</summary>
    internal int SkipOffset => _skipOffset;

    /// <summary>The resolved encrypted-body handle (CDN mirrors + key/seed), set once the body is attached, so a mid-track
    /// device-rate soft reload can build a FRESH INDEPENDENT stream — the kept stream is single-cursor and MUST NOT be shared
    /// across two concurrently-live sessions. Null for external/plain or not-yet-attached sources (treated as not re-openable).</summary>
    internal AudioStreamHandle? ReopenBody { get; set; }

    /// <summary>Open a fresh forward decode view (the codec owns it). <see cref="PrefetchingReadStream"/> presents byte
    /// <c>skipOffset</c> as logical 0 (past the Spotify container header) — the same offset remap <see cref="SkipStream"/>
    /// did — but pulls through <see cref="IAudioReadStream.TryRead"/> so a CDN range miss kicks an async prefetch
    /// instead of the decode thread synchronously driving the fetch.</summary>
    public Stream OpenDecodeStream() => new PrefetchingReadStream(_stream, _skipOffset);

    // The decoder reads via OpenDecodeStream, not this seam — these satisfy the interface but are inert on this path.
    public bool TryOpen(in DataSpec spec) => true;
    public int Read(Span<byte> dst) => 0;
    public long Seek(long offset) => 0;
    public long? Length => _stream.KnownSize > 0 ? _stream.KnownSize : null;
    public SourceCaps Caps => new() { Seekable = false, KnownLength = false };
    public void Cancel() { }
    public void Close() { }   // the host owns the underlying stream lifecycle
}

/// <summary>The app-side <see cref="IAudioDecoder"/> that plugs the kept codec leaves (<see cref="ISampleSource"/> —
/// Vorbis/FLAC/MP3) into the engine's decode edge. Reads interleaved f32 from the codec at the SOURCE rate, conforms to
/// the target channel count, and resamples INTO the fixed mix format via the engine's <see cref="LinearResampler"/> — so
/// the engine mixer/DSP/output stay codec-agnostic (spec §5.5). Per-track normalization gain is baked here (matching the
/// old DecodePipeline), so engine ReplayGain stays unity.</summary>
internal sealed class SpotifyEngineAudioDecoder : IAudioDecoder
{
    private const int MaxSrcFramesPerRead = 4096;

    private WaveeDecoderKind _kind;
    private long _durationMs;
    private float _gainLinear;

    private ISampleSource? _reader;
    private MixFormat _target;
    private int _srcChannels;
    private LinearResampler? _resampler;
    private float[] _srcScratch = Array.Empty<float>();      // codec-native channels, source rate
    private float[] _conformed = Array.Empty<float>();       // target channels, source rate; [0.._holdFrames) is unread
    private int _holdFrames;                                 // unconsumed conformed frames retained for the next Process
    private bool _eof;

    // Parsed per-file gapless trim (W2 fix §3), in MIX-domain frames — resolved in TryOpen, consumed by
    // PcmAudioPlayer's TrimmingSource wrap. None until a file proves otherwise.
    GaplessInfo _gapless = GaplessInfo.None;

    public GaplessInfo Gapless => _gapless;

    public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
    {
        info = default;
        if (src is not SpotifyMediaByteSource sp)
            throw new NotSupportedException("SpotifyEngineAudioDecoder requires a SpotifyMediaByteSource.");
        _target = target;
        _kind = sp.Kind;
        _durationMs = sp.DurationMs;
        _gainLinear = sp.GainLinear;
        var stream = sp.OpenDecodeStream();
        // MP3 only: read the Xing/LAME gapless tag out of the header BEFORE the codec owns the stream (seekable streams
        // only — the probe restores Position; a live forward-only stream skips it). Source-rate values, converted below.
        Mp3GaplessProbe.Result mp3Gapless = default;
        bool hasMp3Gapless = _kind == WaveeDecoderKind.Mp3 && Mp3GaplessProbe.TryProbe(stream, out mp3Gapless);
        _reader = _kind switch
        {
            WaveeDecoderKind.Flac => new FlacSampleSource(stream),
            WaveeDecoderKind.Mp3 => new Mp3SampleSource(stream),
            WaveeDecoderKind.Aac => new AacSampleSource(stream),
            _ => new VorbisSampleSource(stream),
        };
        _srcChannels = Math.Max(1, _reader.Channels);
        int srcRate = _reader.SampleRate > 0 ? _reader.SampleRate : target.SampleRate;

        _resampler = srcRate != target.SampleRate ? new LinearResampler(srcRate, target.SampleRate, target.Channels) : null;
        _srcScratch = new float[MaxSrcFramesPerRead * _srcChannels];
        _conformed = new float[MaxSrcFramesPerRead * target.Channels];
        _gapless = ResolveGapless(hasMp3Gapless, mp3Gapless, srcRate, target.SampleRate);

        WaveeLog.Instance.Event(WaveeLogLevel.Debug, "audio", "audiodiag.decoder",
            $"[audiodiag] decoder kind={_kind} srcRate={srcRate} targetRate={target.SampleRate} srcCh={_srcChannels} targetCh={target.Channels} resampler={(_resampler is { IsActive: true } ? "active" : "passthrough")} gain={_gainLinear:0.000} leadIn={_gapless.LeadInFrames} trailPad={_gapless.TrailPadFrames} exact={_gapless.ExactFrames}");

        var codec = _kind switch
        {
            WaveeDecoderKind.Flac => new MediaContentType(Container.Flac, CodecId.None, CodecId.Flac),
            WaveeDecoderKind.Mp3 => new MediaContentType(Container.Mp3, CodecId.None, CodecId.Mp3),
            WaveeDecoderKind.Aac => new MediaContentType(Container.Adts, CodecId.None, CodecId.Aac),
            _ => new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis),
        };
        var dur = _durationMs > 0 ? TimeSpan.FromMilliseconds(_durationMs) : TimeSpan.Zero;
        info = new DecodedInfo(codec, new MixFormat(srcRate, _srcChannels), dur, default);
        return true;
    }

    public int Read(Span<float> dst)
    {
        if (_reader is null || _eof) return 0;
        // A late worker pump against a stream torn down by a concurrent session dispose is silence/EOF, never a throw — the
        // engine's per-loop containment is the outer net; this keeps the decode edge itself non-fatal.
        try
        {
            int ch = _target.Channels;
            int wantFrames = dst.Length / ch;
            if (wantFrames <= 0) return 0;

            if (_resampler is { IsActive: true } rs)
            {
                // Top up the retained prefix so Process has enough source; retain src[Consumed..] after (spec: caller holds unread).
                int wantAvail = Math.Min(MaxSrcFramesPerRead, rs.SrcFramesForOutput(wantFrames));
                int wantPull = Math.Min(MaxSrcFramesPerRead - _holdFrames, Math.Max(0, wantAvail - _holdFrames));
                if (wantPull > 0) _holdFrames += AppendSource(wantPull);
                if (_holdFrames <= 0) { _eof = true; return 0; }

                ResampleResult rr = rs.Process(_conformed.AsSpan(0, _holdFrames * ch), _holdFrames, dst);
                int unread = _holdFrames - rr.Consumed;
                if (unread > 0 && rr.Consumed > 0)
                    Array.Copy(_conformed, rr.Consumed * ch, _conformed, 0, unread * ch);
                _holdFrames = Math.Max(0, unread);

                if (rr.Produced <= 0) { if (_holdFrames <= 0) _eof = true; return 0; }
                ApplyGain(dst, rr.Produced, ch);
                return rr.Produced;
            }

            int srcFrames = Math.Min(MaxSrcFramesPerRead, wantFrames);
            int gotSrc = AppendSource(srcFrames);   // hold is always 0 on the passthrough path
            if (gotSrc <= 0) { _eof = true; return 0; }
            _conformed.AsSpan(0, gotSrc * ch).CopyTo(dst);
            ApplyGain(dst, gotSrc, ch);
            return gotSrc;
        }
        catch (ObjectDisposedException) { _eof = true; return 0; }
    }

    void ApplyGain(Span<float> dst, int frames, int ch)
    {
        if (_gainLinear == 1f) return;
        int n = frames * ch;
        for (int i = 0; i < n; i++) dst[i] *= _gainLinear;
    }

    // Pull up to srcFrames codec frames and channel-conform into _conformed starting at _holdFrames.
    private int AppendSource(int srcFrames)
    {
        if (srcFrames <= 0) return 0;
        int wantSamples = srcFrames * _srcChannels;
        int got = _reader!.ReadSamples(_srcScratch, 0, wantSamples);
        int framesGot = got / _srcChannels;
        if (framesGot <= 0) return 0;

        int ch = _target.Channels;
        int baseF = _holdFrames;
        for (int f = 0; f < framesGot; f++)
        {
            int ib = f * _srcChannels;
            float l = _srcScratch[ib];
            float r = _srcChannels >= 2 ? _srcScratch[ib + 1] : l;
            int ob = (baseF + f) * ch;
            if (ch == 1) _conformed[ob] = _srcChannels >= 2 ? (l + r) * 0.5f : l;
            else { _conformed[ob] = l; _conformed[ob + 1] = r; for (int c = 2; c < ch; c++) _conformed[ob + c] = 0f; }
        }
        return framesGot;
    }

    public long Seek(long frame)
    {
        if (_reader is null) return -1;
        double sec = _target.SampleRate > 0 ? (double)frame / _target.SampleRate : 0;
        try { _reader.SeekTo(TimeSpan.FromSeconds(sec)); } catch { /* streaming source not seekable yet — best effort */ }
        _resampler?.Reset();
        _holdFrames = 0;
        _eof = false;
        return frame;
    }

    // The honest per-codec gapless trim (W2 fix §3), reported in MIX-domain frames so the engine's TrimmingSource wrap
    // and join arming stay codec-agnostic:
    // - Vorbis: NO additional trim. The vendored NVorbis already consumes the spec priming packet (the first audio packet
    //   emits nothing — StreamDecoder.cs ~520–524) and applies the EOS granule-position end trim (validLen backoff,
    //   StreamDecoder.cs ~503–511), which is exactly Vorbis's gapless accounting. A blind "conservative lead-in" here
    //   would cut real audio, so 0/0 IS the truthful value; ExactFrames stays unknown (probing the last granule would
    //   seek to the stream end — a blocking read on a head-only fast-start stream).
    // - FLAC: lossless (no encoder delay/pad), but STREAMINFO's total sample count pins ExactFrames so the emitted
    //   length is exact and never depends on catalog duration metadata.
    // - MP3: LAME encoder delay + end padding from the Xing header (when present + seekable), with the standard
    //   529-sample decoder offset folded in (skip delay+529, trim padding−529).
    GaplessInfo ResolveGapless(bool hasMp3Gapless, in Mp3GaplessProbe.Result mp3, int srcRate, int mixRate)
    {
        switch (_kind)
        {
            case WaveeDecoderKind.Flac when _reader is FlacSampleSource { TotalFrames: > 0 } flac:
                return new GaplessInfo(0, 0, ToMixFrames(flac.TotalFrames, srcRate, mixRate), TailKnown: true);

            case WaveeDecoderKind.Mp3 when hasMp3Gapless:
                int leadSrc = mp3.DelaySamples + Mp3GaplessProbe.DecoderDelaySamples;
                int padSrc = Math.Max(0, mp3.PaddingSamples - Mp3GaplessProbe.DecoderDelaySamples);
                long exactSrc = mp3.TotalSamples;   // already frames×spf − delay − padding, or −1
                return new GaplessInfo(
                    (int)ToMixFrames(leadSrc, srcRate, mixRate),
                    (int)ToMixFrames(padSrc, srcRate, mixRate),
                    exactSrc > 0 ? ToMixFrames(exactSrc, srcRate, mixRate) : -1,
                    TailKnown: exactSrc > 0);

            default:
                return GaplessInfo.None;
        }
    }

    static long ToMixFrames(long srcFrames, int srcRate, int mixRate)
        => srcRate <= 0 || srcRate == mixRate ? srcFrames : (long)Math.Round(srcFrames * (double)mixRate / srcRate);
}

/// <summary>
/// The ONE real audio host: the app's <see cref="IAudioHost"/> seam over the unified FluentGpu.Media engine. A single
/// <see cref="PcmAudioPlayer"/>/<see cref="WasapiPcm"/> backend (the graph + device) with the app's Vorbis/FLAC/MP3
/// decoder plugged into its decode edge; encrypted fetch+decrypt+fast-start reuse the kept app seams in-proc. Transport,
/// EQ, volume/mute, and the clock are forwarded to/derived from the engine; a per-track engine session is opened (and the
/// prior one disposed) on each load. Crossfade/prepared-next (engine PlayQueue) and per-endpoint device selection are the
/// documented follow-ups — this host delivers correct single-track decode→mix→output with graceful natural-end advance.
/// </summary>
public sealed partial class FluentMediaAudioHost : IAudioHost, IAudioDspControl, IAudioOutputDeviceControl, IPreparedAudioHost,
    ILiveMetadataSource
{
    const int MaxCrossfadeMs = 12_000;

    readonly WaveeLogger _log;
    readonly ChunkDiskCache? _bodyDisk;
    readonly System.Net.Http.HttpClient _http;
    readonly Func<string, byte[], CdnDecryptor?> _nativeDecryptorFactory;

    readonly AudioEffects _effects = new();
    readonly MediaPlayerCore _core;
    readonly MediaSignalSink _sink;
    PcmAudioPlayer? _backend;                        // built on FIRST USE, never in the ctor — see Backend

    readonly SimpleEvent<AudioHostSignal> _signals = new();
    readonly object _gate = new();
    Task _tail = Task.CompletedTask;                 // serializes session transitions (Load → Play → SupplyBody order)
    readonly Timer _ticker;

    IMediaSession? _session;
    SpotifyAudioStream? _activeStream;               // the kept fast-start stream (head now, body later); null for external
    SpotifyMediaByteSource? _activeBytes;            // the current session's byte source — re-opened on a device-rate soft reload
    string _activeFileIdHex = "";
    long _loadEpoch;
    int _softReloading;                              // 1 while a mid-track device-rate soft-reload drain is queued/running (single-drainer token)
    int _softReloadPending;                          // 1 when a device-rate change awaits processing (set on coalesce / crossfade defer)

    // -- the LIVE session (internet radio) ---------------------------------------------------------------------------
    // Kept apart from _activeStream on purpose: a live transport is not a body that can be re-opened, ranged or seeked,
    // and the two facts the rest of this class needs from it (is it reconnecting, has it died) are read from the poll
    // tick on another thread.
    LiveHttpAudioStream? _activeLive;
    volatile bool _activeIsLive;
    volatile bool _liveRecovering;
    bool _liveDropReported;                          // one drop report per session - a repeat re-arms the retry ladder
    Action<AudioNetworkRecoveryEvent>? _liveRecoveryHandler;
    Action<string>? _liveTitleHandler;

    /// <inheritdoc cref="ILiveMetadataSource.MetadataKnown"/>
    public event Action<string, string?>? MetadataKnown;

    // intents (applied to the session as it becomes ready)
    bool _playIntent;
    double _volume = 1.0;
    bool _muted;
    bool _crossfadeEnabled;
    int _crossfadeMs;

    // last-published state (for edge-triggered signal emission off the poll tick)
    PlaybackState _lastState = PlaybackState.Idle;
    bool _errorReported;
    bool _disposed;

    // ── prepared-next / real overlapping crossfade (IPreparedAudioHost) ──────────────────────────────────────────────
    readonly SimpleEvent<AudioTransitionSignal> _transitions = new();
    // the prepared slot (track B) — built/attached ahead of the active track's natural end
    string? _prepToken;
    SpotifyAudioStream? _prepStream;
    SpotifyMediaByteSource? _prepBytes;   // B's byte source — becomes _activeBytes at commit so a device-rate soft reload re-opens B
    IPreparedItem? _prepItem;
    string _prepUri = "";
    long _prepDurMs;
    bool _prepOverlap;
    // TRUE from the instant a NEW load (or a Stop) is REQUESTED until the replacement session is live. MediaPlayerCore's
    // Position keeps ticking the OUTGOING track's clock until the new session opens on the serialized pump, so every
    // reader in that window — the Connect PutState snapshot, an EmitSnap/EmitState publish, this class's own diagnostics —
    // would report the previous track's position for the track that is starting (observed: a track restarting at 0 was
    // announced at 190488 ms). Load/Stop are synchronous entry points, so setting the gate there closes the window
    // completely; OpenSessionAsync clears it at the one place a session becomes live. Starts true: a fresh host that
    // has never opened a session has no honest clock either (ClockValid must read false, not "true until the first
    // Stop"), which matters to callers gating a host-level call on "does this host actually hold something" before
    // it has ever been loaded.
    volatile bool _clockStale = true;
    // the CURRENTLY-PLAYING (active) track's mixer state, so PositionMs reports active-relative time
    long _activeStartMs;          // raw session ms at which the active track's frame-0 played (0 for a fresh load)
    long _activeDurMs;            // the active track's duration (drives the fade-window trigger)
    long _activePrimaryId;        // the mixer voice id currently carrying the active track
    string _activeUri = "";       // the active track uri (for the Completed edge)
    long _nextVoiceId;            // monotonic crossfade voice id source
    bool _crossfadeInFlight;      // set at commit, cleared on the Completed edge — guards a single commit per hand-off
    string? _committedToken;      // the token whose crossfade is committed (CancelPrepared → AlreadyStarted)
    SpotifyAudioStream? _retiringStream;   // track A's stream, disposed on the Completed edge once its voice retires
    // Finding A (crossfade TOCTOU): CommitCrossfade bumps this seq + records this snapshot under _gate, so a SoftReloadAsync
    // whose await raced a commit onto the OLD session detects it (seq changed) at its post-await re-check and restores the
    // live crossfade bookkeeping that OpenSessionAsync's reset clobbered — instead of disposing B's session out of the mixer.
    long _crossfadeCommitSeq;
    (SpotifyMediaByteSource? Bytes, long StartMs, long DurMs, string Uri, long PrimaryId)? _committedActive;

    // ── W2: the seam-correct 0 ms hand-off (the engine's gapless butt-join, never CommitCrossfade with fade 0) ─────────
    // Phase 1 (CommitGaplessJoin): shortly before A's end, B's PREPARED voice is added to the LIVE mixer at A's estimated
    // natural-end FRAME with a CONSTANT envelope — the same mixer edit VoiceScheduler.Commit's TransitionOutcome.Gapless
    // arm performs. A is never faded or truncated; the WASAPI client never stops. Phase 2 (AnnounceGaplessJoin): when the
    // write clock crosses the join frame, the bookkeeping/identity flips to B and ONE AudioTransitionKind.Started with
    // EffectiveFadeMs=0 advances the controller WITHOUT a reload. All frames are mixer-domain (PcmAudioSession.SampleClock),
    // so pauses/underruns cannot drift the join the way wall-clock scheduling would.
    const int GaplessCommitLeadMs = 1500;   // commit inside the last ~1.5 s (several 200 ms ticks before the boundary)
    const int EndedHoldMaxTicks = 20;       // Ended is held ≤ ~4 s while a prepare is still filling (degraded join > hard cut)
    long _activeJoinFrame;        // the ACTIVE track's estimated natural-end frame (duration/seek-derived; write domain)
    bool _joinPending;            // B is committed in the mixer, waiting for the clock to cross the join frame
    string? _joinToken;           // pending-join identity (announce emits Started with these)
    string _joinUri = "";
    long _joinDurMs;
    long _joinFrame;              // the mixer frame B starts sounding at
    long _joinVoiceId;
    IAudioSource? _joinVoice;     // B's (possibly trimmed) voice — becomes the session's transport target at announce
    long _joinTotalFrames;
    SpotifyAudioStream? _joinStream;          // B's kept stream — becomes _activeStream at announce
    SpotifyMediaByteSource? _joinBytes;
    int _endedHold;               // ticks left holding the Ended signal while the prepared slot is still filling
    int _prepInFlight;            // >0 while a PrepareNextAsync op is queued/running on the pump ("the slot is filling")
    int _prepRearmSent;           // once-per-track latch for the remaining-ms re-arm nudge (reset on open/seek/commit)
    bool _promotePending;         // a degraded end-promote resumed the session; clear _clockStale on the next Playing tick

    // The fade the mixer actually applies: the stored duration counts ONLY while crossfade is enabled — 0 == gapless join.
    int EffectiveFadeMs => _crossfadeEnabled ? _crossfadeMs : 0;

    static long MsToFrames(long ms, int rate) => ms <= 0 ? 0 : ms * rate / 1000;

    // CS0067 (declared, never raised HERE) is suppressed deliberately, NOT because the members are dead: both are REQUIRED
    // by IAudioOutputDeviceControl (Backend/AudioHost.cs) and both already have a live subscriber (LiveSessionHost turns a
    // notice into a toast and an external volume/mute into the projection + UI reflect). What is missing is the PRODUCER
    // inside this host: the notices come from OutputDeviceRouter.Notice and the external volume/mute from
    // AudioSessionEventsSink.OnSimpleVolumeChanged, neither of which is attached to this host yet because the engine WASAPI
    // leaf does not expose per-endpoint selection (see the SetOutputDevice note below). Wiring the router is that follow-up
    // feature; keeping the contracted members declared is what lets it land without touching every consumer.
#pragma warning disable CS0067
    public event Action<OutputDeviceNotice>? OutputDeviceNotice;
    public event Action<double, bool>? ExternalVolumeChanged;
#pragma warning restore CS0067

    public FluentMediaAudioHost(Func<IPlayPlayCdnDecryptorFactory?> decryptors, System.Net.Http.HttpClient http,
        WaveeLogger log = default, ChunkDiskCache? bodyDisk = null)
    {
        _log = log;
        _bodyDisk = bodyDisk;
        _http = http;
        _nativeDecryptorFactory = (_, seed) => decryptors()?.CreateCdnDecryptor(seed);
        _core = new MediaPlayerCore(_effects);
        _sink = new MediaSignalSink(_core);
        // Always-on: the negotiated WASAPI device format is the one fact that finally answers "what sample rate is
        // this user's device actually running at" — FormatSink fires exactly ONCE per device Open, off the RT path,
        // so wiring it straight into the app log costs nothing and is never gated (house rule: diagnostics are
        // always-on, never debugger-only). The 1 Hz feed/play DiagSink counters stay UNWIRED here on purpose — they
        // run ON the RT audio thread inside an alloc-free contract; wiring them to the logger from the RT path would
        // violate it (another change moves them off-RT).
        WasapiAudioDevice.FormatSink = line => _log.Info($"[audio] format {line}");
        // The PCM backend is deliberately NOT built here; see Backend.
        _ticker = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The PCM backend, built on FIRST USE rather than in the ctor. <c>WasapiPcm.CreateBackend</c> calls
    /// <c>ProbeFormat</c>, which opens a COMPLETE WASAPI endpoint (CoCreateInstance → GetDefaultAudioEndpoint → Activate →
    /// GetMixFormat → Initialize → GetService×2) purely to read the device mix rate, then throws the device away. On a cold
    /// start — a sleeping Bluetooth/USB endpoint, a driver the OS still has to spin up — that is seconds of stall, and it
    /// used to sit on the go-live path for a device the session may never play to. It is warmed off-path instead, from
    /// <c>AudioPlaybackStack.StartProvisioning</c>, so first play is still hot.
    ///
    /// Deliberately NOT a timeout + 48k fallback: WasapiPcm's own comment notes a wrong rate is a second route to a
    /// decoder/hardware rate divergence (slowed/pitched playback). The fallback must keep firing only on genuine device
    /// FAILURE, never on device SLOWNESS — which is exactly the cold-start case.
    ///
    /// Safe without a lock: both readers (OpenSessionAsync, PrepareNextCoreAsync) run on the serialized _tail pump, and
    /// PrepareNextCoreAsync early-returns while _session is null, so OpenSessionAsync is always the first toucher.</summary>
    PcmAudioPlayer Backend => _backend ??= CreateBackendTimed();

    PcmAudioPlayer CreateBackendTimed()
    {
        long start = Environment.TickCount64;
        var backend = WasapiPcm.CreateBackend(_effects, decoderFactory: static _ => new SpotifyEngineAudioDecoder());
        long elapsedMs = Environment.TickCount64 - start;
        // States the negotiated mix format instead of the old content-free "(WASAPI mix-format probe)" line — this
        // is the rate/channel count the decode/mixer graph is built for, BEFORE the real device opens. Paired with
        // WasapiAudioDevice.FormatSink (wired above, fires at Open) that finally answers "what rate is this user's
        // device actually running at" end to end.
        _log.Event(WaveeLogLevel.Info, "audio.backend_init",
            $"PCM backend built mixRate={backend.Format.SampleRate} mixCh={backend.Format.Channels}",
            elapsedMs: elapsedMs,
            fields: [WaveeLogField.Of("audio.backend_init_ms", elapsedMs),
                     WaveeLogField.Of("audio.mix_rate", backend.Format.SampleRate),
                     WaveeLogField.Of("audio.mix_channels", backend.Format.Channels)]);
        return backend;
    }

    /// <summary>Build the backend off the login/play path (called from AudioPlaybackStack's background provision). Rides the
    /// same serialized pump the readers use, so it can never race them.</summary>
    public void WarmBackend() => Enqueue(() => { _ = Backend; return Task.CompletedTask; });

    // The raw session clock (track A's decode position, in ms). After an overlapping crossfade the active track is a
    // later mixer voice, so PositionMs subtracts the active track's start offset to stay active-track-relative.
    long RawPositionMs => _clockStale ? 0L : (long)_core.Position.Peek().TotalMilliseconds;
    public long PositionMs => Math.Max(0, RawPositionMs - _activeStartMs);
    public bool IsPlaying => _core.IsPlaying.Peek();
    // The inverse of RawPositionMs's own short-circuit: while the clock is stale, PositionMs is reporting 0 as a LIE
    // (unknown), not a real position — a caller (PlaybackController.EmitState/EmitSnap) must fall back to its own
    // projected position instead of publishing that 0 as fact.
    public bool ClockValid => !_clockStale;
    public bool IsBuffering => _core.IsBuffering.Peek();
    // Read, not Interlocked: _playIntent is only ever written from the serialized-pump/transport-verb call sites
    // (Play/Pause/Stop, all UI-thread-driven), and a stale-by-one-write read here is no different from any other
    // racy bool the controller polls — the PlaybackController.SupplyBodyWhenReadyAsync call site that reads this is
    // fine with "as of a moment ago", never with a torn value (bool reads/writes are atomic on every supported arch).
    public bool PlayIntent => _playIntent;
    public IObservable<AudioHostSignal> Signals => _signals;
    public IObservable<AudioTransitionSignal> Transitions => _transitions;

    // ── IAudioHost transport ─────────────────────────────────────────────────────────────────────────────────────────

    public void Load(in AudioStreamHandle stream)
    {
        // Non-fast path (ghost resume / tests): no clear head — open once the encrypted body is attached.
        var head = new AudioFastStart(stream.TrackUri, stream.FileIdHex, stream.Format, stream.DurationMs,
            stream.NormalizationGainDb, default);
        LoadFastStart(head);
        SupplyBody(stream);
    }

    public void LoadFastStart(in AudioFastStart start)
    {
        _clockStale = true;   // the outgoing track's clock stops being ours the moment a new load is asked for
        long epoch = Interlocked.Increment(ref _loadEpoch);
        var s = start;   // capture (can't use 'in' inside async)
        Enqueue(() => LoadFastStartAsync(s, epoch));
    }

    public void SupplyBody(in AudioStreamHandle body)
    {
        var b = body;
        long epoch = Volatile.Read(ref _loadEpoch);
        Enqueue(() => SupplyBodyAsync(b, epoch));
    }

    public void Play() { _playIntent = true; _log.Info($"[posdiag] play-intent raw={RawPositionMs} pos={PositionMs} activeStart={_activeStartMs} lastState={_lastState}"); _diagResumeTicks = 12; Enqueue(async () => { if (_session is null) { _log.Warn("play() arrived over an empty session — nothing loaded, ticker not started"); return; } await _session.PlayAsync().ConfigureAwait(false); StartTicker(); }); }
    // Stop the poll tick once paused: position is frozen and no crossfade commit / Ended / Error can occur while paused
    // (all Playing-only), and the paused UI state is driven by the controller's optimistic EmitState — not this tick — so
    // quiescing the 200ms wakeups here is free idle CPU. StartTicker resumes it on the next Play.
    public void Pause() { _playIntent = false; _log.Info($"[posdiag] pause raw={RawPositionMs} pos={PositionMs} activeStart={_activeStartMs} lastState={_lastState}"); Enqueue(async () => { if (_session is not null) await _session.PauseAsync().ConfigureAwait(false); StopTicker(); }); }

    // TEMP DIAGNOSTIC (#3 resume overshoot): log raw/derived position for a few ticks after a resume, then self-disable.
    int _diagResumeTicks;

    // Gapless hand-off snapshot (Tick / CommitCrossfade / OpenSession only — never the WASAPI Write path).
    // Pre-allocated fields only; WaveeLogger's interpolated Info handler builds the string IFF Info is enabled.
    long _gaplessXrunsAtArm;
    long _gaplessAEndClock;
    long _gaplessAEndWall;
    int _gaplessArmed;            // 1 once the approaching-boundary snapshot has been taken
    int _gaplessHardCutPending;   // 1 after Ended without a mixer commit — next OpenSession is B of a hard cut

    public void Stop()
    {
        _playIntent = false;
        _clockStale = true;   // nothing is playing → report 0, never the torn-down session's last position
        long epoch = Interlocked.Increment(ref _loadEpoch);   // invalidate any in-flight open
        Enqueue(async () =>
        {
            StopTicker();
            await DisposeSessionAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Reposition the PCM audio session. The PCM graph decodes from a container that has no keyframe grid, so
    /// a <see cref="SeekMode.Keyframe"/> scrub preview and an <see cref="SeekMode.Accurate"/> commit resolve to the SAME
    /// engine seek here — the parameter still exists (rather than being dropped at the boundary) so the fidelity survives
    /// to a host that can honour it, and so the endgame bookkeeping below can tell a preview from a commit if it ever
    /// needs to. What differs is upstream: the controller never emits a cluster event for a preview.</summary>
    public void Seek(long positionMs, SeekMode mode)
    {
        long ms = Math.Max(0, positionMs);
        Enqueue(async () =>
        {
            if (_session is null) return;
            await _session.SeekAsync(TimeSpan.FromMilliseconds(ms), EngineSeekMode.Accurate).ConfigureAwait(false);
            if (_session is PcmAudioSession pcm)
            {
                // W2: the seek moved the active track's natural-end FRAME, so a join scheduled at the old frame would butt
                // B into the middle of A — abandon it (the controller re-arms prepared-next) and re-derive the estimate.
                // The session rebased its clock to the seek target (active-track-relative), so the position base resets too.
                AbandonPendingJoin(pcm, "seek");
                _activeStartMs = 0;
                _activeJoinFrame = pcm.SampleClock + MsToFrames(Math.Max(0, _activeDurMs - ms), pcm.Format.SampleRate);
                _gaplessArmed = 0;      // re-log the arm snapshot for the new endgame
                _prepRearmSent = 0;     // a seek into the endgame may need a fresh remaining-ms re-arm nudge
                _endedHold = 0;
            }
        });
    }

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0, 1);
        _core.Volume.Value = (float)_volume;
        var v = _volume;
        Enqueue(() => { _session?.SetVolume(v); return Task.CompletedTask; });
    }

    // ── IAudioDspControl ─────────────────────────────────────────────────────────────────────────────────────────────

    public void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb, float preampDb = 0f)
    {
        // 10-band graphic EQ (matches the app's persisted band set). A gain-only change ramps in the live graph; enable/
        // disable toggles the topology. Frequencies mirror the classic 10-band layout.
        var eq = _effects.Equalizer;
        if (eq.Bands.Length != 10)
        {
            var freqs = new[] { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
            eq.Apply(new EqPreset(freqs, new float[10]));
        }
        for (int i = 0; i < eq.Bands.Length && i < gainsDb.Length; i++)
            eq.Bands[i].GainDb.Value = Math.Clamp(gainsDb[i], -12f, 12f);
        eq.Enabled.Value = enabled;
    }

    public void SetCrossfade(bool enabled, int durationMs)
    {
        _crossfadeMs = Math.Clamp(durationMs, 0, MaxCrossfadeMs);
        _crossfadeEnabled = enabled && _crossfadeMs > 0;
        // Publish to the engine effects surface (consumed once prepared-next/queue crossfade is wired). 0 == gapless.
        _effects.CrossfadeMs.Value = _crossfadeEnabled ? _crossfadeMs : 0f;
    }

    // ── IAudioOutputDeviceControl ────────────────────────────────────────────────────────────────────────────────────

    public void SetOutputDevice(string? deviceId)
    {
        // v1: the engine WASAPI leaf follows the default endpoint (auto device-loss rebuild). Per-endpoint selection is a
        // follow-up (the WasapiAudioDevice leaf must accept a device id). Store the intent so the picker round-trips.
    }

    public void SetOutputMuted(bool muted)
    {
        _muted = muted;
        Enqueue(() => { _session?.SetMuted(muted); return Task.CompletedTask; });
    }

    // ── the serialized session pump ──────────────────────────────────────────────────────────────────────────────────

    void Enqueue(Func<Task> op)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(async _ =>
            {
                try { await op().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // The serialized session pump is the last line of defense: a failed body attach or decryptor build
                    // here otherwise leaves playback "on" (no session, no signal) with nothing but this log line to
                    // find it by. Surface it typed too, so the UI/controller can react instead of silently sitting on
                    // a dead host.
                    _log.Info($"fluent-audio-host op failed: {ex.GetType().Name}: {ex.Message}");
                    _signals.OnNext(AudioHostSignal.Fault(PositionMs, AudioKeyFailureReason.None, ex.Message));
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    async Task LoadFastStartAsync(AudioFastStart start, long epoch)
    {
        await DisposeSessionAsync().ConfigureAwait(false);
        _errorReported = false;
        _lastState = PlaybackState.Idle;

        if (start.HeadBytes.Length == 0)
        {
            // No clear head → defer session open until SupplyBody attaches the body (Spotify non-fast / external).
            _activeStream = null;
            _activeFileIdHex = start.FileIdHex;
            _pendingFmt = start.Format; _pendingDurMs = start.DurationMs; _pendingGainDb = start.NormalizationGainDb;
            // A load nobody asked to hear (no play intent — the launch-recovery paused restore) announces nothing:
            // see PlayIntentGate. Attaching a clear head/body is work the host does regardless; the buffering BAR is
            // only ever honest while there is something the user is waiting to hear.
            if (PlayIntentGate.ShouldAnnounceBuffering(_playIntent))
                _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
            return;
        }

        var kind = KindOf(start.Format);
        int skip = DetectSkipOffset(start.HeadBytes.Span, start.Format);
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, start.HeadBytes, start.HeadBytes.Length, start.FileIdHex, _log, _bodyDisk);
        _activeStream = stream;
        _activeFileIdHex = start.FileIdHex;
        var bytes = new SpotifyMediaByteSource(stream, skip, kind, start.DurationMs, DbToLinear(start.NormalizationGainDb));
        await OpenSessionAsync(bytes, epoch).ConfigureAwait(false);
        if (PlayIntentGate.ShouldAnnounceBuffering(_playIntent))
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
    }

    AudioFormat _pendingFmt;
    long _pendingDurMs;
    float _pendingGainDb;

    async Task SupplyBodyAsync(AudioStreamHandle body, long epoch)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) { _log.Info($"supply-body ignored stale epoch file={body.FileIdHex}"); return; }
        if (PlayIntentGate.ShouldAnnounceBuffering(_playIntent))
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, PositionMs));

        // Local file (a "Play file…" pick / a shell drop) — open the file and the session now. Same deferred-open shape
        // as the external branch below: the plan carried an EMPTY head, so LoadFastStart parked the load and THIS is
        // where the session actually opens. The decoder kind comes from the resolver's extension map (never a sniff —
        // a local file's extension is the only thing we have, and the provider already refused anything unsupported).
        if (body.SourceKind == AudioSourceKind.LocalFile)
        {
            LocalFileAudioStream file;
            try { file = LocalFileAudioStream.Open(body.CdnUrl); }
            catch (Exception ex)
            {
                // The resolver checked existence, so this is the narrow window where the file vanished (or is
                // unreadable) between resolve and open. Surface it typed rather than leaving a silent "playing" state —
                // the serialized pump's own catch would only have logged it.
                _log.Info($"local file open failed path={body.CdnUrl}: {ex.GetType().Name}: {ex.Message}");
                _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.Restricted, ex.Message));
                return;
            }
            var localBytes = new SpotifyMediaByteSource(file, 0, KindOf(body.Format), body.DurationMs, 1f);
            _activeStream = null;
            await OpenSessionAsync(localBytes, epoch).ConfigureAwait(false);
            return;
        }

        // External plain-HTTP (podcast MP3) — open a plain stream and the session now.
        if (body.SourceKind == AudioSourceKind.ExternalPlain)
        {
            var http = await PlainHttpAudioStream.OpenAsync(_http, body.CdnUrl, _log).ConfigureAwait(false);
            var kind = SniffExternalKind(http.ContentType) ?? WaveeDecoderKind.Mp3;
            var extBytes = new SpotifyMediaByteSource(http, 0, kind, body.DurationMs, 1f);
            _activeStream = null;
            await OpenSessionAsync(extBytes, epoch).ConfigureAwait(false);
            return;
        }

        // LIVE (internet radio, or any locator the resolver marked endless) - a forward-only ICY socket. This branch
        // exists precisely because the ExternalPlain path above is built on a RANGED source whose "server ignored
        // Range" fallback buffers the WHOLE body: on a stream with no end that is an OOM, not a fallback.
        if (body.SourceKind == AudioSourceKind.LiveStream)
        {
            LiveHttpAudioStream live;
            try
            {
                live = await LiveHttpAudioStream.OpenAsync(body.CdnUrl, _log).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ONE connect attempt, then a typed failure: a station that is down should say so immediately rather
                // than spin for the reconnect budget the RUNNING stream is entitled to.
                _log.Info($"live stream open failed url={body.CdnUrl}: {ex.GetType().Name}: {ex.Message}");
                var reason = ex is IOException ? AudioKeyFailureReason.Network : AudioKeyFailureReason.Restricted;
                _signals.OnNext(AudioHostSignal.Fault(0, reason, ex.Message));
                return;
            }
            if (epoch != Volatile.Read(ref _loadEpoch)) { await live.DisposeAsync().ConfigureAwait(false); return; }

            // Content-Type first (authoritative when the station bothers), then the first bytes (most do not bother).
            var head = new byte[512];
            int headLength = live.PeekHead(head);
            var liveKind = SniffExternalKind(live.ContentType) ?? SniffLiveKind(head.AsSpan(0, headLength)) ?? WaveeDecoderKind.Mp3;
            if (liveKind == WaveeDecoderKind.Aac && !MfAacDecoder.IsAvailable())
            {
                await live.DisposeAsync().ConfigureAwait(false);
                _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.ArchUnsupported,
                    "this Windows edition has no AAC decoder (install the Media Feature Pack)"));
                return;
            }

            AttachLive(live);
            _activeStream = null;
            // Duration 0 is load-bearing, not laziness: it keeps every ending-soon / gapless-join / prepared-next arm
            // switched off for a stream that has no end to approach (LiveSessionRules.SessionDurationMs states it).
            var liveBytes = new SpotifyMediaByteSource(live, 0, liveKind,
                LiveSessionRules.SessionDurationMs(isLive: true, body.DurationMs), 1f);
            await OpenSessionAsync(liveBytes, epoch).ConfigureAwait(false);
            return;
        }

        // Module-served bytes (stream/open|read|close over the module RPC).
        if (body.SourceKind == AudioSourceKind.ModuleStream)
        {
            await SupplyModuleStreamAsync(body, epoch).ConfigureAwait(false);
            return;
        }

        var decryptor = BuildDecryptor(body);
        var cdnUrls = body.CdnUrls ?? (string.IsNullOrEmpty(body.CdnUrl) ? Array.Empty<string>() : new[] { body.CdnUrl });

        if (_activeStream is { } s)
        {
            // Fast path: attach the encrypted body to the already-open, already-playing head stream.
            await s.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
            // Size the CDN read-ahead window to this track's bitrate + connection cost now that the body (and its
            // RangedHttpSource) exist — dropout mitigation: an undersized window under-fetches on a fast connection,
            // an oversized one wastes the shared read-ahead budget on a metered one.
            s.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(body.Format), Wavee.NetworkPolicy.IsMetered);
            // Retain the body handle on the active source so a mid-track device-rate change can rebuild an INDEPENDENT stream.
            if (_activeBytes is not null) _activeBytes.ReopenBody = body;
            return;
        }

        // Deferred (Load / non-fast): build a head-less stream, attach the body, then open the session.
        var kind2 = KindOf(_pendingFmt);
        int skip = SpotifyAesCtr.SpotifyHeaderSize;   // no head to inspect → the standard Spotify container offset
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, ReadOnlyMemory<byte>.Empty, 0, body.FileIdHex, _log, _bodyDisk);
        await stream.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
        // _pendingFmt (captured back in LoadFastStartAsync's deferred branch), not body.Format — this path has no
        // fast-start head to have carried the negotiated rung on.
        stream.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(_pendingFmt), Wavee.NetworkPolicy.IsMetered);
        _activeStream = stream;
        // Retain the body handle so a mid-track device-rate change can rebuild an INDEPENDENT stream (see SoftReloadAsync).
        var bytes = new SpotifyMediaByteSource(stream, skip, kind2, _pendingDurMs, DbToLinear(_pendingGainDb)) { ReopenBody = body };
        await OpenSessionAsync(bytes, epoch).ConfigureAwait(false);
    }

    CdnDecryptor BuildDecryptor(in AudioStreamHandle body)
    {
        var seed = body.NativeCdnSeed;
        if (seed.Length > 0)
        {
            var native = _nativeDecryptorFactory(body.FileIdHex, seed.ToArray());
            if (native is null) throw new InvalidOperationException("native PlayPlay CDN seed supplied but no native decryptor is available");
            return native;
        }
        // AP-key path: decrypt in-proc through the ICtrCipher (SpotifyAesCtr). A fresh cipher per chunk keeps read-ahead
        // threads race-free (the counter is re-derived from the byte offset anyway).
        var key = body.Key.ToArray();
        return (buffer, streamOffset) =>
        {
            var cipher = new SpotifyCtrCipher(key);
            cipher.SeekCounter(streamOffset);
            cipher.XorInPlace(buffer);
        };
    }

    async Task OpenSessionAsync(SpotifyMediaByteSource bytes, long epoch, bool autoResume = true)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) return;
        try
        {
            var source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
            var session = await Backend.OpenAsync(source, new MediaOpenOptions { StartPaused = true }, CancellationToken.None).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _loadEpoch)) { await session.DisposeAsync().ConfigureAwait(false); return; }
            // Bind the live effects surface (EQ/crossfade/balance/normalization) so PcmAudioSession.Advance's per-pump
            // ReconcileEffects() actually folds a SetEqualizer/SetCrossfade write into this session's graph — mirrors
            // QueuePlaybackCoordinator.OpenAtAsync's audio.BindEffects(_effects) (the engine's own reference call site;
            // same ordering there — bind right after OpenAsync returns the session, before anything else touches it).
            // MUST run BEFORE ConnectSignals: on a real device ConnectSignals starts the session's own RT feeder thread
            // (driveWithOwnThread → StartFeeder), which begins calling Advance/ReconcileEffects immediately. BindEffects
            // only stashes a plain reference field on the session, so binding it first — same thread, program order —
            // makes Thread.Start()'s happens-before guarantee cover the write; binding AFTER ConnectSignals would race
            // the freshly-started feeder thread's very first reconcile against this write.
            // No unbind on close: BindEffects's reference lives on THIS session instance only, and every re-open builds
            // a brand-new PcmAudioSession (this same method, above) — the old one (and its own _liveEffects field) is
            // disposed wholesale, never reused, so there is nothing to clear.
            if (session is PcmAudioSession pcmSession) pcmSession.BindEffects(_effects);
            session.ConnectSignals(_sink);
            _session = session;
            _activeBytes = bytes;   // retained so a mid-track device-rate change can re-open the SAME stream at the new rate
            // Fresh active track: reset the crossfade/offset bookkeeping to this session's primary voice.
            _activeStartMs = 0;
            _clockStale = false;   // THIS session now owns _core.Position — the reported clock is honest again
            _activeDurMs = bytes.DurationMs;
            _activeUri = "";
            _crossfadeInFlight = false;
            _endedHold = 0;
            _prepRearmSent = 0;
            _promotePending = false;
            _activeJoinFrame = 0;
            if (session is PcmAudioSession pcm)
            {
                _activePrimaryId = pcm.PrimaryVoiceIdValue;
                // W2: the fresh primary voice starts at mixer frame 0 and is estimated to end at its declared duration.
                _activeJoinFrame = MsToFrames(bytes.DurationMs, pcm.Format.SampleRate);
                // Fix 2: re-arm THIS track if a mid-track default-endpoint switch adopts a different sample rate. The engine
                // raises DeviceFormatChanged off its cold device thread; the handler enqueues a soft reload. Unsubscribed when
                // this session is disposed (DisposeSessionAsync / the soft reload) so no handler leaks across loads.
                pcm.DeviceFormatChanged += OnDeviceFormatChanged;
            }
            _core.Volume.Value = (float)_volume;
            session.SetVolume(_volume);
            session.SetMuted(_muted);
            if (_gaplessHardCutPending != 0)
            {
                long bClock = session is PcmAudioSession opened ? opened.SampleClock : 0;
                long wall = Environment.TickCount64;
                _log.Info($"[gapless] hardcut-b-open clock={bClock} wallGapMs={wall - _gaplessAEndWall} aEndClock={_gaplessAEndClock} xruns={SessionXruns()}");
                _gaplessHardCutPending = 0;
            }
            _gaplessArmed = 0;
            if (_playIntent && autoResume) { await session.PlayAsync().ConfigureAwait(false); StartTicker(); }
        }
        catch (Exception ex)
        {
            _log.Info($"fluent-audio-host open failed file={_activeFileIdHex}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(PositionMs, AudioKeyFailureReason.None, ex.Message));
        }
    }

    // ── Fix 2: mid-track device sample-rate change → soft reload at the NEW device rate ──────────────────────────────
    // A default-endpoint switch to a device that clocks at a different rate keeps audio alive (the engine swaps the sink in
    // PcmAudioSession.RebuildSink) but leaves the decoder/graph/mixer/rings frozen at the OLD rate, so the currently-playing
    // track drifts off pitch. The engine raises DeviceFormatChanged fire-and-forget OFF its cold device thread; we coalesce
    // it and enqueue a soft reload onto the serialized pump. NOT called for a same-endpoint control-panel rate change (no
    // WASAPI notification) — that self-corrects on the next load via Fix 1. Runs on a ThreadPool thread; keep it minimal.
    void OnDeviceFormatChanged(MixFormat newFormat)
    {
        if (_disposed) return;
        Volatile.Write(ref _softReloadPending, 1);   // Finding #1: record the change unconditionally, THEN try to drain it —
        TryStartSoftReloadDrain();                   // so a change arriving while a reload runs is never dropped (see the drain).
    }

    // Acquire the single-drainer token and enqueue the drain onto the serialized pump. A no-op if a drain is already active
    // (it will pick up _softReloadPending itself) or after dispose. Also called from the crossfade Completed edge (Tick) and
    // from the drain's own finally to re-arm a reload that was deferred / arrived at the check-then-clear boundary.
    void TryStartSoftReloadDrain()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _softReloading, 1, 0) != 0) return;   // a drain is already active — it drains pending
        long epoch = Volatile.Read(ref _loadEpoch);                              // a genuine track change (bumped epoch) supersedes it
        Enqueue(async () =>
        {
            try
            {
                // Finding #1: drain trailing device-rate changes. Each pass re-opens at the CURRENT live device rate (Fix 1),
                // so a burst converges in at most one extra pass — it cannot spin. A real track change (epoch) ends the drain.
                while (Interlocked.Exchange(ref _softReloadPending, 0) == 1)
                {
                    if (_disposed || epoch != Volatile.Read(ref _loadEpoch)) break;
                    if (!await SoftReloadAsync(epoch).ConfigureAwait(false))
                    {
                        // Finding #4: deferred (a crossfade holds both voices). Re-mark pending and stop — the crossfade
                        // Completed edge in Tick re-arms the drain once the fade finishes. This does NOT spin.
                        Volatile.Write(ref _softReloadPending, 1);
                        break;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _softReloading, 0);
                // Close the check-then-clear race: a change that set _softReloadPending after our last claim but before we
                // released the token must still be serviced. Skip while a crossfade is in flight — Tick will re-arm instead.
                if (Volatile.Read(ref _softReloadPending) == 1 && !_crossfadeInFlight) TryStartSoftReloadDrain();
            }
        });
    }

    // Re-open the current track on a FRESH, INDEPENDENT stream (never the shared single-cursor kept stream — Finding #1)
    // through the now-rate-correct open path (Fix 1 binds the decoder/graph to the LIVE device rate), preserving the playhead
    // and play intent. Runs on the serialized pump. Returns true when handled (re-opened or a benign no-op — superseded epoch /
    // no re-openable kept stream); returns FALSE when deferred because a crossfade is in flight (Finding #4) so the caller keeps
    // the pending flag for the Completed edge to re-arm.
    async Task<bool> SoftReloadAsync(long epoch)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) return true;   // superseded by a real track change — nothing to do
        var bytes = _activeBytes;
        if (bytes is null || _activeStream is null) return true;   // external/podcast (PlainHttpAudioStream) — not re-openable
        if (bytes.ReopenBody is not { } body) return true;         // no captured body handle (ghost/not-attached) — leave old playing
        if (body.SourceKind != AudioSourceKind.SpotifyEncrypted) return true;   // only the encrypted CDN path is re-openable

        // Finding #4 + Finding A: atomically (under _gate, synchronous-only) DEFER if a crossfade already holds both voices,
        // else capture the old session + the commit sequence. Tearing the session down mid-fade would silently drop track B;
        // deferring keeps the pending flag so Tick's Completed edge re-arms the drain once the (short) fade finishes. Capturing
        // the seq under the same lock CommitCrossfade bumps lets the post-await re-check tell whether a crossfade committed onto
        // the old session DURING our await (Finding A) — that commit adds B's voice to the old session, so we must not dispose it.
        IMediaSession? old;
        long seqBefore;
        lock (_gate)
        {
            // W2: a PENDING gapless join holds B's voice in the live mixer exactly like an in-flight fade does — tearing
            // the session down would drop B. Defer identically; the Completed edge re-arms the drain.
            if (_crossfadeInFlight || _joinPending) return false;   // the current track stays only briefly off-pitch; B is never dropped
            old = _session;
            seqBefore = _crossfadeCommitSeq;
        }
        if (old is null) return true;   // no live session to reload

        long savedPos = PositionMs;   // active-track-relative; captured before the re-open resets the timeline to 0
        // Finding B: OpenSessionAsync blanks _activeUri/_activeDurMs on every open, but a track made active via a committed
        // crossfade carries B's identity here — snapshot it and restore after a SUCCESSFUL reopen so URI-dependent signals keep it.
        string savedUri = _activeUri;
        long savedDurMs = _activeDurMs;

        // Finding #1: the kept stream is a SINGLE-CURSOR object — its AsStream() returns `this` with one Position — so it
        // CANNOT be shared across two concurrently-live sessions: the new decoder's open seeks the shared cursor (garbling the
        // still-decoding old session) and disposing EITHER session closes the shared stream out from under the other. Give the
        // re-opened session an INDEPENDENT read view: a FRESH head-less SpotifyAudioStream (the encrypted body serves the header
        // region and decrypts to the same clear head) with its OWN body attach + RangedHttpSource, wrapped in a fresh source.
        // The old session keeps its own stream; we dispose old only AFTER the new session is confirmed, and dispose the fresh
        // stream if the re-open fails — so both the shared-cursor race and Finding #2's keep-old-on-failure guarantee hold.
        var freshStream = SpotifyAudioStream.CreateHeadOnly(_http, ReadOnlyMemory<byte>.Empty, 0, body.FileIdHex, _log, _bodyDisk);
        var freshBytes = new SpotifyMediaByteSource(freshStream, bytes.SkipOffset, bytes.Kind, bytes.DurationMs, bytes.GainLinear)
        {
            ReopenBody = body,   // retain so a SUBSEQUENT device-rate change can reload the fresh stream again
        };

        // Finding #2: re-open the NEW session (still PAUSED via autoResume:false) BEFORE disposing the OLD one, so a failed
        // re-open never leaves _session null with playback dead. Capture old + detach its DeviceFormatChanged; OpenSessionAsync
        // installs the new session (and re-subscribes the event there) on success, or leaves _session == old on failure. The
        // new session's WASAPI client is activated-but-not-Started until PlayAsync, so there is no double-audio window.
        if (old is PcmAudioSession op) op.DeviceFormatChanged -= OnDeviceFormatChanged;

        try
        {
            // Attach the encrypted body to the fresh independent stream (re-primes from the disk cache / CDN), then re-open
            // PAUSED (autoResume:false) so the playhead is restored BEFORE audio starts — otherwise the track would audibly
            // play from 0 for a beat before the seek lands. OpenSessionAsync re-applies volume/mute; _playIntent survives.
            var cdnUrls = body.CdnUrls ?? (string.IsNullOrEmpty(body.CdnUrl) ? Array.Empty<string>() : new[] { body.CdnUrl });
            await freshStream.AttachBodyWithNativeDecryptorAsync(BuildDecryptor(body), cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
            freshStream.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(body.Format), Wavee.NetworkPolicy.IsMetered);
            await OpenSessionAsync(freshBytes, epoch, autoResume: false).ConfigureAwait(false);
        }
        catch
        {
            // The body attach (or open) threw before installing the new session: dispose the fresh independent stream (it owns
            // network/read-ahead resources) and KEEP the OLD session playing — re-subscribe its DeviceFormatChanged so a later
            // switch still re-arms. Better a brief wrong-pitch than silent death.
            try { freshStream.Dispose(); } catch { }
            if (old is PcmAudioSession opx) opx.DeviceFormatChanged += OnDeviceFormatChanged;
            return true;
        }

        // Finding A: re-check under _gate whether a crossfade COMMITTED onto the old session while we awaited. If it did, B's
        // voice was just added to `old` — disposing `old` (the normal success path) would silently drop B. ABORT instead:
        // discard the freshly-opened session, restore `old` with the live crossfade bookkeeping that OpenSessionAsync clobbered,
        // and re-arm the drain (return false → the caller re-marks _softReloadPending; Tick's Completed edge retries the reload
        // once the fade finishes). The old session's WASAPI sink is still clocking A→B, so playback continues uninterrupted.
        bool committedDuringAwait;
        lock (_gate) { committedDuringAwait = _crossfadeCommitSeq != seqBefore; }
        if (committedDuringAwait)
        {
            var fresh = _session;
            if (fresh is PcmAudioSession fp) fp.DeviceFormatChanged -= OnDeviceFormatChanged;   // OpenSessionAsync subscribed it
            if (!ReferenceEquals(fresh, old) && fresh is not null) { try { await fresh.DisposeAsync().ConfigureAwait(false); } catch { } }
            try { freshStream.Dispose(); } catch { }
            lock (_gate)
            {
                _session = old;   // keep the live crossfade session; never leave B stranded
                if (_committedActive is { } snap)
                {
                    // Restore only the fields OpenSessionAsync's reset clobbered; _activeStream/_retiringStream/_committedToken
                    // survived it and still point at B/A. _crossfadeInFlight goes back true so Tick's Completed edge closes the fade.
                    _activeBytes = snap.Bytes;
                    _activeStartMs = snap.StartMs;
                    _activeDurMs = snap.DurMs;
                    _activeUri = snap.Uri;
                    _activePrimaryId = snap.PrimaryId;
                    _crossfadeInFlight = true;
                }
            }
            if (old is PcmAudioSession op3) op3.DeviceFormatChanged += OnDeviceFormatChanged;   // re-arm device-rate switches on old
            return false;
        }

        var reopened = _session;
        if (!ReferenceEquals(reopened, old) && reopened is not null)
        {
            // Re-open SUCCEEDED: the fresh independent session is live and PAUSED. Adopt its stream, then retire the OLD session
            // (Finding #2 — disposing old closes only OLD's stream, never the fresh one), then, if still the current track,
            // restore the playhead and resume (seek-then-play → no start-from-0 blip).
            _activeStream = freshStream;
            // Finding B: OpenSessionAsync just blanked _activeUri/_activeDurMs — restore the pre-reload identity (only when it was
            // non-default), so a track made active via a committed crossfade keeps reporting B's uri/duration after the reload.
            if (savedUri.Length != 0) _activeUri = savedUri;
            if (savedDurMs != 0) _activeDurMs = savedDurMs;
            if (old is not null) { try { await old.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (epoch == Volatile.Read(ref _loadEpoch))
            {
                if (savedPos > 0)
                    // Restoring the playhead after a device-format reload is a committed reposition, never a scrub.
                    try { await reopened.SeekAsync(TimeSpan.FromMilliseconds(savedPos), EngineSeekMode.Accurate).ConfigureAwait(false); } catch { }
                if (_playIntent) { try { await reopened.PlayAsync().ConfigureAwait(false); StartTicker(); } catch { } }
            }
        }
        else
        {
            // Re-open did NOT install a new session (OpenSessionAsync's epoch guard skipped/disposed the new one, or it faulted):
            // dispose the fresh stream (idempotent — safe even if the guarded session already tore it down) and KEEP the OLD
            // session playing — never leave _session null — re-subscribing its DeviceFormatChanged so a later switch re-arms.
            try { freshStream.Dispose(); } catch { }
            if (old is PcmAudioSession op2) op2.DeviceFormatChanged += OnDeviceFormatChanged;
        }
        return true;
    }

    async Task DisposeSessionAsync()
    {
        // FIRST, before anything awaits: a live transport's reader may be parked inside the ring buffer's blocking Read
        // on the feed thread, and AudioFeedThread.Stop only joins that worker for 500 ms. Disposing the live stream here
        // wakes it (ObjectDisposedException reads as EOF at the decode edge) so the teardown below completes in time.
        DetachLive();
        var old = _session;
        _session = null;
        _activeBytes = null;
        if (old is PcmAudioSession p) p.DeviceFormatChanged -= OnDeviceFormatChanged;   // detach the soft-reload subscription
        var stream = _activeStream;
        _activeStream = null;
        var retiring = _retiringStream;
        _retiringStream = null;
        // A manual load/stop supersedes any prepared next, any in-flight crossfade, and any pending gapless join —
        // the join's mixer voice dies with the session; only its kept stream needs an explicit dispose.
        SpotifyAudioStream? joinStream;
        lock (_gate)
        {
            joinStream = _joinStream;
            _joinPending = false;
            _joinStream = null; _joinBytes = null; _joinToken = null; _joinUri = ""; _joinDurMs = 0;
            _joinFrame = 0; _joinVoiceId = 0; _joinVoice = null; _joinTotalFrames = 0;
        }
        _endedHold = 0;
        _prepRearmSent = 0;
        _promotePending = false;
        _activeJoinFrame = 0;
        await DisposePreparedSlotAsync().ConfigureAwait(false);
        Volatile.Write(ref _softReloadPending, 0);   // drop any device-rate reload deferred for the track we're tearing down
        _crossfadeInFlight = false;
        _committedToken = null;
        _activeUri = "";
        _activeStartMs = 0;
        _activeDurMs = 0;
        _gaplessArmed = 0;
        _lastState = PlaybackState.Idle;   // a later legitimate session must never inherit a stale Playing/Ended edge
        if (old is not null) { try { await old.DisposeAsync().ConfigureAwait(false); } catch { } }
        if (stream is not null) { try { stream.Dispose(); } catch { } }
        if (retiring is not null) { try { retiring.Dispose(); } catch { } }
        if (joinStream is not null) { try { joinStream.Dispose(); } catch { } }
    }

    // Dispose the prepared (not-yet-committed) slot and clear its fields. The prepared voice has NOT entered the mixer,
    // so disposing the IPreparedItem here is correct; once committed we clear the fields WITHOUT disposing (see Tick).
    async Task DisposePreparedSlotAsync()
    {
        var item = _prepItem;
        var stream = _prepStream;
        _prepItem = null;
        _prepStream = null;
        _prepBytes = null;
        _prepToken = null;
        _prepUri = "";
        _prepDurMs = 0;
        _prepOverlap = false;
        if (item is not null) { try { await item.DisposeAsync().ConfigureAwait(false); } catch { } }
        if (stream is not null) { try { stream.Dispose(); } catch { } }
    }

    // -- the LIVE session: recovery state, in-band metadata, and the "Ended means dropped" re-reading ----------------
    // A live transport reports two things no finite body ever does. It RECONNECTS - which has to reach the UI as
    // "reconnecting" rather than as a stall or (worse) silence. And it can DIE - which has to reach the controller as an
    // ERROR, because the controller's error arm retries the SAME playable (for a live stream that is reconnect-from-
    // scratch) while its Ended arm auto-advances off the station the user chose.

    void AttachLive(LiveHttpAudioStream live)
    {
        DetachLive();
        _activeLive = live;
        _activeIsLive = true;
        _liveRecovering = false;
        _liveDropReported = false;

        _liveRecoveryHandler = e =>
        {
            if (!ReferenceEquals(_activeLive, live)) return;   // a superseded session's transport still finishing up
            if (LiveSessionRules.IsTerminal(e.Stage)) { _liveRecovering = false; ReportLiveDrop(e.Error); return; }
            bool recovering = LiveSessionRules.IsRecovering(e.Stage);
            if (recovering == _liveRecovering) return;         // edge-triggered: Attempt after Started is not new news
            _liveRecovering = recovering;
            if (recovering)
                _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Recovering, PositionMs, true, false, false,
                    PlaybackRecoveryKind.Network));
            else if (_playIntent)
                // Recovered: clear the banner by re-asserting Playing. Gated on play intent so a reconnect that lands
                // while the user has paused does not announce playback that is not happening.
                _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Playing, PositionMs, true, false, false));
        };
        ((IAudioNetworkRecoverySource)live).NetworkRecovery += _liveRecoveryHandler;

        _liveTitleHandler = title =>
        {
            if (!ReferenceEquals(_activeLive, live)) return;
            MetadataKnown?.Invoke(title, live.StationName);
        };
        live.StreamTitleChanged += _liveTitleHandler;
    }

    /// <summary>Unwire and dispose the live transport. Idempotent, and safe to call for a non-live session.</summary>
    void DetachLive()
    {
        var live = _activeLive;
        _activeLive = null;
        _activeIsLive = false;
        _liveRecovering = false;
        _liveDropReported = false;
        if (live is null) { _liveRecoveryHandler = null; _liveTitleHandler = null; return; }
        if (_liveRecoveryHandler is not null) ((IAudioNetworkRecoverySource)live).NetworkRecovery -= _liveRecoveryHandler;
        if (_liveTitleHandler is not null) live.StreamTitleChanged -= _liveTitleHandler;
        _liveRecoveryHandler = null;
        _liveTitleHandler = null;
        try { live.Dispose(); } catch { /* the socket is going away regardless */ }
    }

    /// <summary>Report a live drop as a typed ERROR. Once per session - a second report would re-arm the retry ladder
    /// the first one already started.</summary>
    void ReportLiveDrop(Exception? error)
    {
        if (!LiveSessionRules.ShouldReportDropInsteadOfEnded(_activeIsLive, _liveDropReported)) return;
        _liveDropReported = true;
        var reason = LiveSessionRules.DropReason(error);
        _log.Info($"live stream dropped reason={reason}: {error?.GetType().Name}: {error?.Message}");
        StopTicker();
        _signals.OnNext(AudioHostSignal.Fault(PositionMs, reason, error?.Message ?? "the live stream dropped"));
    }

    // ── IPreparedAudioHost: prepared-next + real overlapping crossfade ───────────────────────────────────────────────

    public Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default)
    {
        var req = request;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _prepInFlight);   // W2: Ended HOLDS (never hard-cuts) while the slot is still filling
        Enqueue(async () =>
        {
            try { await PrepareNextCoreAsync(req, ct).ConfigureAwait(false); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { Interlocked.Decrement(ref _prepInFlight); }
        });
        return tcs.Task;
    }

    async Task PrepareNextCoreAsync(AudioPrepareRequest req, CancellationToken ct)
    {
        if (_session is not PcmAudioSession session) return;   // no live session to hand off from — nothing to prepare
        var start = req.Start;
        var kind = KindOf(start.Format);
        int skip = DetectSkipOffset(start.HeadBytes.Span, start.Format);
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, start.HeadBytes, start.HeadBytes.Length, start.FileIdHex, _log, _bodyDisk);
        var bytes = new SpotifyMediaByteSource(stream, skip, kind, start.DurationMs, DbToLinear(start.NormalizationGainDb));
        var source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
        var pctx = PrepareContext.For(session.Format, session.NormalizationMode, session.ReferenceLufsValue);
        var result = await Backend.PrepareAsync(source, pctx, ct).ConfigureAwait(false);

        // Supersede any stale prepared slot (a queue edit re-prepares); keep only the newest.
        if (_prepToken is not null && !ReferenceEquals(_prepStream, stream))
            await DisposePreparedSlotAsync().ConfigureAwait(false);
        _prepToken = req.Token;
        _prepStream = stream;
        _prepBytes = bytes;
        _prepItem = result;
        _prepUri = start.TrackUri;
        _prepDurMs = start.DurationMs;
        _prepOverlap = req.AllowOverlap;
        _log.Info($"[gapless] prepare-primed token={req.Token} ready={result.IsReady} leadIn={result.Gapless.LeadInFrames} trailPad={result.Gapless.TrailPadFrames} overlap={req.AllowOverlap} dur={start.DurationMs}");
    }

    public Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default)
    {
        var b = body;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                if (token == _prepToken && _prepStream is { } s)
                {
                    var cdnUrls = b.CdnUrls ?? (string.IsNullOrEmpty(b.CdnUrl) ? Array.Empty<string>() : new[] { b.CdnUrl });
                    await s.AttachBodyWithNativeDecryptorAsync(BuildDecryptor(b), cdnUrls, null, ct).ConfigureAwait(false);
                    s.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(b.Format), Wavee.NetworkPolicy.IsMetered);
                    // Retain B's body handle so a device-rate reload after the crossfade commit can rebuild B independently.
                    if (_prepBytes is not null) _prepBytes.ReopenBody = b;
                    _log.Info($"[gapless] next-body token={token} attached=1");
                }
                else _log.Info($"[gapless] next-body token={token} attached=0 prep={_prepToken ?? "-"}");
                tcs.TrySetResult();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AudioPrepareCancelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                AudioPrepareCancelResult result;
                if (token == _prepToken && _prepItem is not null)
                {
                    await DisposePreparedSlotAsync().ConfigureAwait(false);
                    result = AudioPrepareCancelResult.Cancelled;
                }
                else if (token == _committedToken)
                {
                    result = AudioPrepareCancelResult.AlreadyStarted;   // crossfade already committed — too late to cancel
                }
                else result = AudioPrepareCancelResult.NotFound;
                tcs.TrySetResult(result);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // Commit the overlapping crossfade IN the live session (called from Tick, RT-safe): add B's voice fading in and fade
    // the active voice out over the same window, then re-point the active state at B. Runs exactly once per hand-off.
    void CommitCrossfade(PcmAudioSession sess, IPreparedItem item, long rawPos)
    {
        long id = ++_nextVoiceId;
        long start = sess.SampleClock;
        int fadeFrames = _crossfadeMs * sess.Format.SampleRate / 1000;
        var curve = CrossCurve.EqualPower;
        float rg = ReplayGain.ScalarLinear(item.Loudness, sess.NormalizationMode, sess.ReferenceLufsValue);
        string token = _prepToken ?? "";
        string uri = _prepUri;

        // Finding A (crossfade TOCTOU): add B's voice, swap the active bookkeeping to B, and bump the commit sequence ATOMICALLY
        // under _gate. This runs on the Tick/Timer thread, NOT the serialized pump, so a concurrent SoftReloadAsync must either
        // DEFER (it saw _crossfadeInFlight under this same lock) or, if it already captured the sequence and is mid-await, observe
        // the bumped seq at its post-await re-check and KEEP this session — never dispose it and drop B. Adding the voice and the
        // seq bump share one lock so the reloader can never observe "B's voice added to old, but seq not yet bumped".
        int bodySupplied = _prepBytes?.ReopenBody is not null ? 1 : 0;
        lock (_gate)
        {
            _crossfadeInFlight = true;
            sess.AddCrossfadeVoice(item.AudioVoice!, GainEnvelope.Fade(FadeKind.In, start, fadeFrames, curve), start, rg, sess.BuildVoiceChain(), id);
            sess.SetVoiceEnvelope(_activePrimaryId, GainEnvelope.Fade(FadeKind.Out, start, fadeFrames, curve));

            // Hand streams over: A retires (disposed on the Completed edge), B's kept stream becomes the active stream.
            _retiringStream = _activeStream;
            _activeStream = _prepStream;
            _activeBytes = _prepBytes;   // keep the (stream, bytes) pair consistent so a device-rate soft reload re-opens B, not A
            _committedToken = _prepToken;

            // Re-point the active bookkeeping at B so PositionMs reads B-relative from here on.
            _activeStartMs = rawPos;
            _activePrimaryId = id;
            _activeDurMs = _prepDurMs;
            _activeUri = uri;
            // W2: transport now addresses B (post-hand-off seeks reach the audible track, not the retired primary), and
            // the NEXT join arms off B's end frame.
            if (item.AudioVoice is { } fadeVoice)
                sess.SetActiveVoice(id, fadeVoice, TimeSpan.FromMilliseconds(_prepDurMs), item.TotalFrames);
            _activeJoinFrame = start + MsToFrames(_prepDurMs, sess.Format.SampleRate);
            _prepRearmSent = 0;

            // Snapshot the committed state so an aborting SoftReloadAsync can restore it (OpenSessionAsync's reset clobbers these).
            _committedActive = (_prepBytes, rawPos, _prepDurMs, uri, id);
            _crossfadeCommitSeq++;

            // Clear the prepared slot WITHOUT disposing the item — its voice is now live in the mixer.
            _prepItem = null;
            _prepStream = null;
            _prepBytes = null;
            _prepToken = null;
            _prepUri = "";
            _prepDurMs = 0;
            _prepOverlap = false;
        }

        _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, 0, _crossfadeMs));
        long xruns = sess.XrunCount;
        _log.Info($"[gapless] commit-crossfade clock={start} fadeFrames={fadeFrames} fadeMs={_crossfadeMs} raw={rawPos} primed=1 body={bodySupplied} xruns={xruns} xrunDelta={xruns - _gaplessXrunsAtArm}");
        _gaplessArmed = 0;
        _gaplessHardCutPending = 0;
    }

    // ── W2 phase 1: the 0 ms gapless join. Add B's prepared voice into the LIVE mixer at A's estimated natural-end
    // frame with a CONSTANT envelope (the engine's VoiceScheduler TransitionKind.Gapless butt-join shape): A is never
    // faded or truncated, the IAudioClient never stops, and B stays silent until the clock reaches the join. NEVER route
    // 0 ms through CommitCrossfade — GainEnvelope.Fade(…, 0 frames) folds to Constant, which is two voices at unity for
    // the whole tail, not a butt-join. Runs on the Tick/Timer thread; the mixer edits go through the session's SPSC.
    void CommitGaplessJoin(PcmAudioSession sess, IPreparedItem item)
    {
        long id = ++_nextVoiceId;
        long clock = sess.SampleClock;
        long join = Math.Max(_activeJoinFrame, clock);   // never in the past — a late commit degrades to a micro-gap join
        float rg = ReplayGain.ScalarLinear(item.Loudness, sess.NormalizationMode, sess.ReferenceLufsValue);
        int bodySupplied = _prepBytes?.ReopenBody is not null ? 1 : 0;
        long durMs;
        lock (_gate)
        {
            _joinPending = true;   // SoftReloadAsync defers on this exactly like on _crossfadeInFlight (B is in the mixer)
            sess.AddCrossfadeVoice(item.AudioVoice!, GainEnvelope.Constant, join, rg, sess.BuildVoiceChain(), id);
            _joinToken = _prepToken; _joinUri = _prepUri; _joinDurMs = durMs = _prepDurMs;
            _joinFrame = join; _joinVoiceId = id; _joinVoice = item.AudioVoice; _joinTotalFrames = item.TotalFrames;
            _joinStream = _prepStream; _joinBytes = _prepBytes;
            // Clear the prepared slot WITHOUT disposing the item — its voice is now live in the mixer.
            _prepItem = null; _prepStream = null; _prepBytes = null;
            _prepToken = null; _prepUri = ""; _prepDurMs = 0; _prepOverlap = false;
        }
        long xruns = sess.XrunCount;
        _log.Info($"[gapless] commit-join clock={clock} join={join} id={id} body={bodySupplied} durMs={durMs} xruns={xruns} xrunDelta={xruns - _gaplessXrunsAtArm}");
    }

    // ── W2 phase 2: the join went live — the write clock crossed B's first frame (the playhead follows within the
    // in-flight buffer, ≲ a tick). Flip the active identity to B, rebase PositionMs via _activeStartMs exactly the way
    // the fade commit does, and emit ONE Started with EffectiveFadeMs=0 so CommitPreparedTransitionAsync advances the
    // session WITHOUT reloading. _crossfadeInFlight goes true so the existing Completed edge retires A's stream.
    void AnnounceGaplessJoin(PcmAudioSession sess, long rawPos)
    {
        string token, uri;
        lock (_gate)
        {
            if (!_joinPending) return;
            _joinPending = false;
            _crossfadeInFlight = true;
            _retiringStream = _activeStream;
            _activeStream = _joinStream;
            _activeBytes = _joinBytes;
            _committedToken = _joinToken;
            token = _joinToken ?? "";
            uri = _joinUri;
            _activeStartMs = rawPos;
            _activePrimaryId = _joinVoiceId;
            _activeDurMs = _joinDurMs;
            _activeUri = _joinUri;
            _activeJoinFrame = _joinFrame + MsToFrames(_joinDurMs, sess.Format.SampleRate);
            if (_joinVoice is { } voice)
                sess.SetActiveVoice(_joinVoiceId, voice, TimeSpan.FromMilliseconds(_joinDurMs), _joinTotalFrames);
            _committedActive = (_joinBytes, rawPos, _joinDurMs, _joinUri, _joinVoiceId);
            _crossfadeCommitSeq++;
            _joinToken = null; _joinUri = ""; _joinDurMs = 0; _joinFrame = 0; _joinVoiceId = 0;
            _joinVoice = null; _joinTotalFrames = 0; _joinStream = null; _joinBytes = null;
        }
        _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, 0, 0));
        long xruns = SessionXruns();
        _log.Info($"[gapless] join-live token={token} uri={uri} clock={SessionClock()} raw={rawPos} xruns={xruns} xrunDelta={xruns - _gaplessXrunsAtArm}");
        _gaplessArmed = 0;
        _gaplessHardCutPending = 0;
        _prepRearmSent = 0;
    }

    // ── W2: a pending join whose frame is no longer valid (seek moved A's end / B's decode died before the join). The
    // mixer has no voice removal, so pin B's envelope at 0 (a 1-frame fade-out in the past) and cut its byte source —
    // the ring decode faults to EOF and the voice retires silently. A keeps playing untouched.
    void AbandonPendingJoin(PcmAudioSession? sess, string reason)
    {
        SpotifyAudioStream? stream;
        long voiceId;
        lock (_gate)
        {
            if (!_joinPending) return;
            _joinPending = false;
            voiceId = _joinVoiceId;
            stream = _joinStream;
            _joinStream = null; _joinBytes = null; _joinToken = null; _joinUri = ""; _joinDurMs = 0;
            _joinFrame = 0; _joinVoiceId = 0; _joinVoice = null; _joinTotalFrames = 0;
        }
        sess?.SetVoiceEnvelope(voiceId, GainEnvelope.Fade(FadeKind.Out, 0, 1, CrossCurve.Linear));
        if (stream is not null) { try { stream.Dispose(); } catch { } }
        _log.Info($"[gapless] join-abandoned id={voiceId} reason={reason}");
    }

    // ── W2 degraded promote ("never LoadAndPlayCurrent while the slot can be consumed"): A drained to Ended before a
    // join committed (prepare landed late / commit window missed), but a READY prepared voice exists — promote it as the
    // live session's NEW PRIMARY voice (SetVoice replaces the drained one; the ring is re-wrapped; the IAudioClient
    // never stops) and resume. A bounded micro-gap (the Ended-detection ticks), never a device teardown. Returns true
    // when promoted — the Ended signal is then suppressed (the Started transition advances the controller instead).
    bool TryPromoteAtEnd()
    {
        if (_session is not PcmAudioSession sess) return false;
        if (_prepItem is not { IsReady: true } item || !_prepOverlap || item.AudioVoice is not { } voice) return false;

        string token, uri;
        long durMs;
        SpotifyAudioStream? endedStream;
        lock (_gate)
        {
            long clock = sess.SampleClock;
            sess.SetVoice(voice, TimeSpan.FromMilliseconds(_prepDurMs), item.TotalFrames,
                sess.NormalizationMode, sess.ReferenceLufsValue, (float)_volume);
            token = _prepToken ?? "";
            uri = _prepUri;
            durMs = _prepDurMs;
            endedStream = _activeStream;      // A drained — its decode side is idle; safe to retire immediately
            _activeStream = _prepStream;
            _activeBytes = _prepBytes;
            _committedToken = _prepToken;
            _activeStartMs = 0;
            _clockStale = true;               // raw still reads A's end until the resume rebases it — report 0, not A's dur
            _promotePending = true;
            _activePrimaryId = sess.PrimaryVoiceIdValue;
            _activeDurMs = durMs;
            _activeUri = uri;
            _activeJoinFrame = clock + MsToFrames(durMs, sess.Format.SampleRate);
            _committedActive = (_prepBytes, 0, durMs, uri, sess.PrimaryVoiceIdValue);
            _crossfadeCommitSeq++;
            // Clear the prepared slot WITHOUT disposing the item — its voice is now the session's primary.
            _prepItem = null; _prepStream = null; _prepBytes = null;
            _prepToken = null; _prepUri = ""; _prepDurMs = 0; _prepOverlap = false;
        }
        if (endedStream is not null) { try { endedStream.Dispose(); } catch { } }
        if (_playIntent) { Enqueue(async () => { if (_session is not null) await _session.PlayAsync().ConfigureAwait(false); }); StartTicker(); }
        _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, 0, 0));
        _log.Info($"[gapless] promote-at-end token={token} uri={uri} clock={SessionClock()} xruns={SessionXruns()}");
        _gaplessArmed = 0;
        _gaplessHardCutPending = 0;
        _prepRearmSent = 0;
        return true;
    }

    // ── the poll tick: derive AudioHostSignals from the engine's reactive state ──────────────────────────────────────

    void StartTicker() => _ticker.Change(200, 200);
    void StopTicker() => _ticker.Change(Timeout.Infinite, Timeout.Infinite);

    void Tick()
    {
        if (_disposed) return;
        // No session → nothing to poll. Without this guard a session torn down between ticks (DisposeSessionAsync nulls
        // _session before the async Stop/Load continuation reaches StopTicker) would keep firing a zombie pos=0,
        // IsPlaying=true tick off the stale _core state below — the ticker is the one thing that must never outlive
        // the session it was reporting on. Mirrors FluentVideoMediaHost.Tick's CurrentPlayer-null guard.
        if (_session is null) { StopTicker(); return; }
        var state = _core.State.Peek();
        if (_promotePending && state == PlaybackState.Playing)
        {
            // The degraded end-promote resumed (the session rebased its clock to B's 0) — the reported clock is honest again.
            _promotePending = false;
            _clockStale = false;
        }
        long rawPos = RawPositionMs;
        long pos = PositionMs;

        // Per-xrun diagnostics (always-on, house rule — never gated, never debugger-only). Drained here, off the RT
        // feed thread, on every 200 ms tick: each incident the RT thread actually recorded (a ring underrun — real
        // dropped audio, not merely "a callback in which something starved") becomes its OWN Warning line with when,
        // how much, and why, instead of the old "xruns=17" cumulative count that only ever surfaced at the NEXT track
        // boundary. Keep the existing [gapless] xrun fields as they are — they still answer "what happened right at
        // the hand-off"; this answers "what happened mid-track that nothing else could see".
        if (_session is PcmAudioSession xrunSession) DrainXruns(xrunSession, pos, state);

        if (_diagResumeTicks > 0)   // TEMP (#3): trace position for a few ticks after resume to locate the overshoot
        {
            _diagResumeTicks--;
            _log.Info($"[posdiag] tick raw={rawPos} pos={pos} activeStart={_activeStartMs} state={state} lastState={_lastState}");
        }

        if (!_errorReported && _core.Error.Peek() is { } err)
        {
            _errorReported = true;
            _signals.OnNext(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None, err.Message));
            return;
        }

        // ── prepared-next hand-off: fade > 0 commits an overlapping crossfade at the fade window; fade == 0 commits the
        //    engine-seam gapless butt-join (W2 — B at A's natural-end frame, Constant envelope) inside the commit lead ──
        long activePos = rawPos - _activeStartMs;
        int fadeMs = EffectiveFadeMs;
        long armAt = _activeDurMs > 0 ? Math.Max(0, _activeDurMs - Math.Max(fadeMs, 2000)) : long.MaxValue;
        if (state == PlaybackState.Playing && !_crossfadeInFlight && !_joinPending && _activeDurMs > 0 && activePos >= armAt && _gaplessArmed == 0)
        {
            _gaplessArmed = 1;
            _gaplessXrunsAtArm = SessionXruns();
            int primed = _prepItem is { IsReady: true } ? 1 : 0;
            int body = _prepBytes?.ReopenBody is not null ? 1 : 0;
            int reason = primed == 0 ? (_prepToken is null ? 4 : 2) : !_prepOverlap ? 3 : 0;
            _log.Info($"[gapless] arm remainMs={_activeDurMs - activePos} fadeMs={fadeMs} overlap={_prepOverlap} primed={primed} body={body} reason={reason} clock={SessionClock()} xruns={_gaplessXrunsAtArm}");
        }
        // ── W2 remaining-ms re-arm (once per track): the endgame opened with NOTHING prepared or in flight — nudge the
        //    controller to re-resolve. Missed with an empty token: ClearPreparedToken("") is a no-op and the schedule's
        //    signature guard makes a duplicate nudge free, so this can never cancel a live prepare.
        if (state == PlaybackState.Playing && _prepRearmSent == 0 && _activeDurMs > 0
            && !_crossfadeInFlight && !_joinPending
            && _prepToken is null && Volatile.Read(ref _prepInFlight) == 0
            && activePos >= _activeDurMs - EndingSoonMs(_activeDurMs))
        {
            _prepRearmSent = 1;
            _log.Info($"[gapless] rearm remainMs={_activeDurMs - activePos} fadeMs={fadeMs} clock={SessionClock()}");
            _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Missed, "", _activeUri, pos, 0, "ending-soon-unprepared"));
        }
        if (state == PlaybackState.Playing && !_crossfadeInFlight && !_joinPending
            && _prepItem is { IsReady: true } item && _prepOverlap && _activeDurMs > 0
            && _session is PcmAudioSession sess)
        {
            if (fadeMs > 0 && activePos >= _activeDurMs - fadeMs)
            {
                CommitCrossfade(sess, item, rawPos);
                pos = PositionMs;   // re-read: now B-relative (≈0 at the hand-off)
            }
            else if (fadeMs <= 0 && activePos >= _activeDurMs - GaplessCommitLeadMs
                     && Volatile.Read(ref _softReloading) == 0)   // never commit into a session a soft reload may replace
            {
                CommitGaplessJoin(sess, item);
            }
        }
        // ── W2 phase 2: the scheduled join goes LIVE when the write clock crosses B's first frame ─────────────────────
        if (_joinPending && _session is PcmAudioSession joinSess)
        {
            if (state == PlaybackState.Ended)
            {
                // B's voice died before the join (its decode faulted to EOF) — only then can the mixer drain while a join
                // is pending. Fall back to the ordinary Ended path (the controller hard-cuts).
                AbandonPendingJoin(joinSess, "ended-before-join");
            }
            else if (joinSess.SampleClock >= _joinFrame)
            {
                AnnounceGaplessJoin(joinSess, rawPos);
                pos = PositionMs;   // re-read: now B-relative (≈0 at the hand-off)
            }
        }
        // ── close the hand-off: once the fade has elapsed (0 for a gapless join), retire A's stream, report Completed ──
        else if (_crossfadeInFlight && (rawPos - _activeStartMs) >= fadeMs)
        {
            _crossfadeInFlight = false;
            var retiring = _retiringStream;
            _retiringStream = null;
            if (retiring is not null) { try { retiring.Dispose(); } catch { } }
            _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Completed, _committedToken ?? "", _activeUri, PositionMs, fadeMs));
            // Finding #4: a device-rate change deferred while this crossfade held both voices now re-arms — the session is
            // back to a single active voice, so a soft reload can safely re-open it at the live rate.
            if (Volatile.Read(ref _softReloadPending) == 1) TryStartSoftReloadDrain();
        }

        switch (state)
        {
            case PlaybackState.Playing:
                if (LiveSessionRules.ShouldEmitRecoveringTick(_activeIsLive, _liveRecovering))
                {
                    // The 2-argument tick infers RecoveryKind.None, which the projection writes straight over the
                    // "reconnecting" state - so while a live transport is actually reconnecting the tick must be the
                    // 6-argument form that keeps carrying Network, or the banner flickers off every 200 ms.
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.PositionTick, pos, true, false, false,
                        LiveSessionRules.TickRecoveryKind(_activeIsLive, _liveRecovering)));
                    break;
                }
                _signals.OnNext(_lastState == PlaybackState.Playing
                    ? new AudioHostSignal(AudioHostSignalKind.PositionTick, pos)
                    : new AudioHostSignal(AudioHostSignalKind.Playing, pos));
                break;
            case PlaybackState.Paused:
                if (_lastState != PlaybackState.Paused) _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Paused, pos));
                break;
            case PlaybackState.Opening:
            case PlaybackState.Buffering:
            case PlaybackState.Stalled:
                if (_lastState != state) _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, pos));
                break;
            case PlaybackState.Ended:
            {
                bool endedEdge = _lastState != PlaybackState.Ended;
                if (!endedEdge && _endedHold <= 0) break;   // steady-state Ended, already reported

                // A LIVE session can never legitimately end: an endless stream that stopped producing DROPPED. Report
                // it as an error (the controller retries this playable = a fresh connect) instead of letting the Ended
                // arm auto-advance off the station. Deliberately ahead of the W2 promote/hold logic - none of which can
                // apply to a source with no duration and no prepared next.
                if (_activeIsLive) { ReportLiveDrop(null); break; }

                // W2 degraded path: a READY prepared voice at (or after) the boundary promotes INTO the live session —
                // a bounded micro-gap, never a device teardown. The Started transition advances the controller instead
                // of the Ended signal.
                if (TryPromoteAtEnd()) { _endedHold = 0; break; }

                // W2: "never LoadAndPlayCurrent while the slot is still filling" — hold the Ended signal (bounded) while
                // a PrepareNextAsync is queued/running; the promote above consumes the slot the moment it lands ready.
                if (Volatile.Read(ref _prepInFlight) > 0 && (endedEdge || _endedHold > 0))
                {
                    if (endedEdge)
                    {
                        _endedHold = EndedHoldMaxTicks;
                        _log.Info($"[gapless] ended-hold clock={SessionClock()} raw={rawPos} holdTicks={_endedHold}");
                    }
                    else _endedHold--;
                    if (_endedHold > 0) break;   // ticker stays alive; re-checked next tick
                }
                _endedHold = 0;

                _gaplessAEndClock = SessionClock();
                _gaplessAEndWall = Environment.TickCount64;
                int primed = _prepItem is { IsReady: true } ? 1 : 0;
                int body = _prepBytes?.ReopenBody is not null ? 1 : 0;
                _gaplessHardCutPending = _crossfadeInFlight ? 0 : 1;
                _log.Info($"[gapless] ended clock={_gaplessAEndClock} raw={rawPos} pos={pos} fadeMs={fadeMs} overlap={_prepOverlap} primed={primed} body={body} inFlight={_crossfadeInFlight} xruns={SessionXruns()} xrunDelta={SessionXruns() - _gaplessXrunsAtArm}");
                StopTicker();
                _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
                break;
            }
        }
        _lastState = state;
    }

    // ── helpers (codec kind + skip-offset detection extracted from the old DecodePipeline) ───────────────────────────

    static WaveeDecoderKind KindOf(AudioFormat fmt) => fmt switch
    {
        AudioFormat.Flac or AudioFormat.Flac24 => WaveeDecoderKind.Flac,
        AudioFormat.Mp3 => WaveeDecoderKind.Mp3,
        AudioFormat.Aac => WaveeDecoderKind.Aac,
        _ => WaveeDecoderKind.Vorbis,
    };

    static WaveeDecoderKind? SniffExternalKind(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        var ct = contentType.ToLowerInvariant();
        // "aac" is tested FIRST and deliberately: it covers audio/aac, audio/aacp and audio/x-aac, and testing "mpeg"
        // first would swallow nothing today but would the moment a station reports "audio/mpeg-aac". What is NEVER
        // mapped here is audio/mp4 — that is an MP4 container, not the raw ADTS the AAC leaf reads.
        // audio/mp4 (and mp4a-latm) is an MP4 CONTAINER, not the raw ADTS the AAC leaf reads - never route it here.
        if (ct.Contains("mp4")) return null;
        if (ct.Contains("aac")) return WaveeDecoderKind.Aac;
        if (ct.Contains("mpeg") || ct.Contains("mp3")) return WaveeDecoderKind.Mp3;
        if (ct.Contains("ogg") || ct.Contains("vorbis")) return WaveeDecoderKind.Vorbis;
        if (ct.Contains("flac")) return WaveeDecoderKind.Flac;
        return null;
    }

    /// <summary>Second-chance codec detection for a LIVE stream from its first bytes, for the (common) station that
    /// reports a useless Content-Type such as <c>application/octet-stream</c>. Only the two codecs radio actually uses
    /// are recognised; anything else falls through to the caller's default.</summary>
    internal static WaveeDecoderKind? SniffLiveKind(ReadOnlySpan<byte> head)
    {
        // An ID3v2 tag can precede MP3 frames on a stream that was pushed from files.
        if (head.Length >= 3 && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
            return WaveeDecoderKind.Mp3;
        for (int i = 0; i + 1 < head.Length && i < 4096; i++)
        {
            if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0) continue;
            // Both ADTS and MPEG audio start with a 11-bit sync; the LAYER field separates them (00 = ADTS/AAC).
            int layer = (head[i + 1] >> 1) & 0x03;
            if (layer == 0) return WaveeDecoderKind.Aac;
            return WaveeDecoderKind.Mp3;
        }
        return null;
    }

    long SessionClock() => _session is PcmAudioSession s ? s.SampleClock : 0;
    long SessionXruns() => _session is PcmAudioSession s ? s.XrunCount : 0;

    // Drain every per-xrun event the RT feed thread recorded since the last tick and log ONE Warning line per
    // incident — never a cumulative count. A bounded stack buffer + a drain loop (not a single call) so a burst
    // larger than the buffer still gets logged in full, in order, within this one tick.
    void DrainXruns(PcmAudioSession sess, long posMs, PlaybackState state)
    {
        int sampleRate = sess.Format.SampleRate;
        string stateText = state.ToString();
        Span<AudioFeedThread.XrunEvent> buf = stackalloc AudioFeedThread.XrunEvent[16];
        int n;
        while ((n = sess.DrainXrunEvents(buf)) > 0)
        {
            long totalFramesLost = sess.XrunFramesLost;
            for (int i = 0; i < n; i++)
            {
                var ev = buf[i];
                double gapMs = XrunLogLine.GapMs(ev.GapFrames, sampleRate);
                long ageMs = XrunLogLine.AgeMs(ev.Timestamp, Environment.TickCount64);
                _log.Warn(XrunLogLine.Format(ev.VoiceId, ev.GapFrames, totalFramesLost, ev.RingFrames,
                    ev.GcPauseTicksDelta, gapMs, ageMs, posMs, stateText));
            }
            if (n < buf.Length) break;   // drained everything currently available
        }
    }

    // The ending-soon margin (W2 fix §1): the overlap plus a worst-case prime budget (key + CDN + TryOpen + ring
    // prefill), clamped to the full duration on shorter tracks. Mirrors Wavee.Backend.PreparedNextPolicy — the
    // controller-side twin that decides WHEN to (re-)schedule; this host-side copy only times the re-arm nudge.
    long EndingSoonMs(long durMs)
    {
        long margin = EffectiveFadeMs + 8000L;
        return durMs > 0 && durMs < margin ? durMs : margin;
    }

    static float DbToLinear(float db) => db == 0f ? 1f : (float)Math.Pow(10, db / 20.0);

    static int DetectSkipOffset(ReadOnlySpan<byte> clearHead, AudioFormat format)
    {
        if (format is AudioFormat.Flac or AudioFormat.Flac24)
        {
            ReadOnlySpan<byte> flac = "fLaC"u8;
            if (clearHead.Length >= flac.Length && clearHead[..flac.Length].SequenceEqual(flac)) return 0;
            return SpotifyAesCtr.SpotifyHeaderSize;
        }
        if (HasVorbisHeaderAt(clearHead, 0)) return 0;
        return SpotifyAesCtr.SpotifyHeaderSize;
    }

    static bool HasVorbisHeaderAt(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || bytes.Length < offset + 27) return false;
        var page = bytes[offset..];
        if (!page[..4].SequenceEqual(SpotifyAesCtr.OggMagic)) return false;
        int segments = page[26];
        if (page.Length < 27 + segments) return false;
        var lacing = page.Slice(27, segments);
        int packetLength = 0;
        for (int i = 0; i < lacing.Length; i++) { packetLength += lacing[i]; if (lacing[i] < 255) break; }
        if (packetLength < 7 || page.Length < 27 + segments + 7) return false;
        return page[27 + segments] == 1 && page.Slice(28 + segments, 6).SequenceEqual("vorbis"u8);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _loadEpoch);
        StopTicker();
        try { await _ticker.DisposeAsync().ConfigureAwait(false); } catch { }
        await DisposeSessionAsync().ConfigureAwait(false);
    }
}
