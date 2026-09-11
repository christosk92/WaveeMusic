using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Signals;
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
    readonly CancellationTokenSource _reads = new();
    int _closed;

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
    internal IAudioReadStream ReadStream => _stream;

    /// <summary>Open a fresh forward decode view (the codec owns it). <see cref="PrefetchingReadStream"/> presents byte
    /// <c>skipOffset</c> as logical 0 (past the Spotify container header) — the same offset remap <see cref="SkipStream"/>
    /// did — but pulls through <see cref="IAudioReadStream.TryRead"/> so a CDN range miss kicks an async prefetch
    /// instead of the decode thread synchronously driving the fetch.</summary>
    public Stream OpenDecodeStream() => new PrefetchingReadStream(_stream, _skipOffset, _reads.Token, leaveOpen: true);

    // The decoder reads via OpenDecodeStream, not this seam — these satisfy the interface but are inert on this path.
    public bool TryOpen(in DataSpec spec) => true;
    public int Read(Span<byte> dst) => 0;
    public long Seek(long offset) => 0;
    public long? Length => _stream.KnownSize > 0 ? _stream.KnownSize : null;
    public SourceCaps Caps => new() { Seekable = _stream.AsStream().CanSeek, KnownLength = _stream.KnownSize > 0 };
    public void Cancel() => _reads.Cancel();
    internal void DisposeSource()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Cancel();
        _stream.Dispose();
    }
    public void Close() => DisposeSource();   // decoder ownership closes only after producer retirement
}

/// <summary>The app-side <see cref="IAudioDecoder"/> that plugs the kept codec leaves (<see cref="ISampleSource"/> —
/// Vorbis/FLAC/MP3) into the engine's decode edge. Reads interleaved f32 from the codec at the SOURCE rate, conforms to
/// the target channel count, and resamples INTO the fixed mix format via the engine's <see cref="LinearResampler"/> — so
/// the engine mixer/DSP/output stay codec-agnostic (spec §5.5). Per-track normalization gain is baked here (matching the
/// old DecodePipeline), so engine ReplayGain stays unity.</summary>
internal sealed class SpotifyEngineAudioDecoder : IAudioDecoder, IDisposable
{
    private const int MaxSrcFramesPerRead = 4096;

    private WaveeDecoderKind _kind;
    private long _durationMs;
    private float _gainLinear;

    private ISampleSource? _reader;
    private Stream? _decodeView;
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
        Dispose();
        _eof = false;
        var stream = _decodeView = sp.OpenDecodeStream();
        try
        {
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
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var reader = Interlocked.Exchange(ref _reader, null);
        var view = Interlocked.Exchange(ref _decodeView, null);
        _eof = true;
        _holdFrames = 0;
        try { reader?.Dispose(); }
        finally { view?.Dispose(); }
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
        if (_reader is null) throw new InvalidOperationException("The decoder is not open.");
        double seconds = _target.SampleRate > 0 ? (double)Math.Max(0, frame) / _target.SampleRate : 0;
        long actualSourceFrame = _reader.SeekTo(TimeSpan.FromSeconds(seconds));
        _resampler?.Reset();
        _holdFrames = 0;
        _eof = false;
        return ToMixFrames(actualSourceFrame, _reader.SampleRate, _target.SampleRate);
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

            case WaveeDecoderKind.Mp3 when hasMp3Gapless && mp3.DecoderAppliesTrim:
                return new GaplessInfo(0, 0,
                    mp3.TotalSamples > 0 ? ToMixFrames(mp3.TotalSamples, srcRate, mixRate) : -1,
                    TailKnown: mp3.TotalSamples > 0);

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
    ILiveMetadataSource, IAudioLevelSource
{
    const int MaxCrossfadeMs = 12_000;

    readonly WaveeLogger _log;
    readonly ChunkDiskCache? _bodyDisk;
    readonly System.Net.Http.HttpClient _http;
    readonly Func<string, byte[], CdnDecryptor?> _nativeDecryptorFactory;

    readonly AudioEffects _effects = new();
    readonly MediaPlayerCore _core;
    readonly MediaSignalSink _sink;
    Task<PcmAudioPlayer>? _backendTask;
    PcmAudioPlayer? _backend;                        // built on FIRST USE, never in the ctor — see Backend

    readonly SimpleEvent<AudioHostSignal> _signals = new();
    readonly object _gate = new();
    readonly AudioHostMailbox _mailbox;
    readonly Func<AudioEffects, PcmAudioPlayer> _backendFactory;
    readonly Func<string, CancellationToken, Task<LiveHttpAudioStream>> _openLiveSource;
    CancellationTokenSource _loadCancellation = new();
    CancellationTokenSource? _prepareCancellation;
    Task _loadStarted = Task.CompletedTask;
    QueueItemId _prepTarget;
    PlaybackCommandId _commandSnapshot;
    PlaybackCommandId _latestCommand
    {
        get { lock (_gate) return _commandSnapshot; }
        set { lock (_gate) _commandSnapshot = value; }
    }
    QueueItemId _activeQueueItem;
    QueueItemId _joinTarget;
    long _seekRevision;
    long _requestedStartMs;
    SpotifyMediaByteSource? _openingBytes;
    AudioFastStart _lastStart;
    PlaybackCommandId _loadCommand;
    long _activeStartFrame;
    int _joinFadeMs;
    AudioTransitionGate? _joinGate;
    VoiceScheduler? _voiceScheduler;
    PcmAudioSession? _schedulerSession;
    int _schedulerRate;
    int _committedFadeMs;
    long _retiringVoiceId;
    SpotifyMediaByteSource? _retiringBytes;
    readonly Timer _ticker;

    IMediaSession? _session;
    SpotifyAudioStream? _activeStream;               // the kept fast-start stream (head now, body later); null for external
    SpotifyMediaByteSource? _activeBytes;            // the current session's byte source — re-opened on a device-rate soft reload
    string _activeFileIdHex = "";
    long _loadEpoch;
    int _softReloading;                              // 1 while a mid-track device-rate soft-reload drain is queued/running (single-drainer token)
    PcmAudioSession? _deviceCallbackSession;
    Action<MixFormat, long>? _deviceRebuiltHandler;
    PcmAudioSession? _recoverySession;
    long _recoveryPositionMs;
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
    volatile bool _playIntent;
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
    bool _crossfadeInFlight;      // set at commit, cleared on the Completed edge — guards a single commit per hand-off
    string? _committedToken;      // the token whose crossfade is committed (CancelPrepared → AlreadyStarted)
    SpotifyAudioStream? _retiringStream;   // track A's stream, disposed on the Completed edge once its voice retires
    // Finding A (crossfade TOCTOU): CommitCrossfade bumps this seq + records this snapshot under _gate, so a SoftReloadAsync
    // whose await raced a commit onto the OLD session detects it (seq changed) at its post-await re-check and restores the
    // live crossfade bookkeeping that OpenSessionAsync's reset clobbered — instead of disposing B's session out of the mixer.

    // ── W2: the seam-correct 0 ms hand-off (the engine's gapless butt-join, never CommitCrossfade with fade 0) ─────────
    // Phase 1 (CommitGaplessJoin): shortly before A's end, B's PREPARED voice is added to the LIVE mixer at A's estimated
    // natural-end FRAME with a CONSTANT envelope — the same mixer edit VoiceScheduler.Commit's TransitionOutcome.Gapless
    // arm performs. A is never faded or truncated; the WASAPI client never stops. Phase 2 (AnnounceGaplessJoin): when the
    // write clock crosses the join frame, the bookkeeping/identity flips to B and ONE AudioTransitionKind.Started with
    // EffectiveFadeMs=0 advances the controller WITHOUT a reload. All frames are mixer-domain (PcmAudioSession.SampleClock),
    // so pauses/underruns cannot drift the join the way wall-clock scheduling would.
    const int GaplessCommitLeadMs = 1500;   // commit inside the last ~1.5 s (several 200 ms ticks before the boundary)
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
        Func<AudioEffects, PcmAudioPlayer> backendFactory, WaveeLogger log = default, ChunkDiskCache? bodyDisk = null)
        : this(decryptors, http, backendFactory,
            (url, cancellation) => LiveHttpAudioStream.OpenAsync(url, log, cancellation), log, bodyDisk)
    {
    }

    internal FluentMediaAudioHost(Func<IPlayPlayCdnDecryptorFactory?> decryptors, System.Net.Http.HttpClient http,
        Func<AudioEffects, PcmAudioPlayer> backendFactory,
        Func<string, CancellationToken, Task<LiveHttpAudioStream>> openLiveSource,
        WaveeLogger log = default, ChunkDiskCache? bodyDisk = null)
    {
        _log = log;
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _openLiveSource = openLiveSource ?? throw new ArgumentNullException(nameof(openLiveSource));
        _mailbox = new AudioHostMailbox(error =>
        {
            _log.Info($"fluent-audio-host op failed track={_activeUri} file={_activeFileIdHex}: {error.GetType().Name}: {error.Message}");
            PublishSignal(AudioHostSignal.Fault(PositionMs, AudioKeyFailureReason.None, error.Message));
        });
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
        _ticker = new Timer(_ => { if (!_disposed) Enqueue(() => { Tick(); return Task.CompletedTask; }); }, null, Timeout.Infinite, Timeout.Infinite);
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
    /// Safe without a lock: both readers (OpenSessionAsync, PrepareNextCoreAsync) run on the owner mailbox, and
    /// PrepareNextCoreAsync early-returns while _session is null, so OpenSessionAsync is always the first toucher.</summary>
    async Task<PcmAudioPlayer> GetBackendAsync()
    {
        if (_backend is not null) return _backend;
        _backendTask ??= Task.Run(CreateBackendTimed);
        return _backend = await _backendTask;
    }

    public static PcmAudioPlayer CreateWasapiBackend(AudioEffects effects) =>
        WasapiPcm.CreateBackend(effects, decoderFactory: static _ => new SpotifyEngineAudioDecoder());

    PcmAudioPlayer CreateBackendTimed()
    {
        long start = Environment.TickCount64;
        var backend = _backendFactory(_effects);
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
    public void WarmBackend() => Enqueue(async () => { _ = await GetBackendAsync(); });

    // The raw session clock (track A's decode position, in ms). After an overlapping crossfade the active track is a
    // later mixer voice, so PositionMs subtracts the active track's start offset to stay active-track-relative.
    long RawPositionMs => _clockStale ? 0L : (long)_core.Position.Peek().TotalMilliseconds;
    public long PositionMs => Math.Max(0, RawPositionMs - _activeStartMs);
    public bool IsPlaying => !_clockStale && _core.IsPlaying.Peek();
    // The inverse of RawPositionMs's own short-circuit: while the clock is stale, PositionMs is reporting 0 as a LIE
    // (unknown), not a real position — a caller (PlaybackController.EmitState/EmitSnap) must fall back to its own
    // projected position instead of publishing that 0 as fact.
    public bool ClockValid => !_clockStale;
    public bool IsBuffering => _playIntent && (_clockStale || _core.IsBuffering.Peek());
    // Read, not Interlocked: _playIntent is only ever written from the serialized-pump/transport-verb call sites
    // (Play/Pause/Stop, all UI-thread-driven), and a stale-by-one-write read here is no different from any other
    // racy bool the controller polls — the PlaybackController.SupplyBodyWhenReadyAsync call site that reads this is
    // fine with "as of a moment ago", never with a torn value (bool reads/writes are atomic on every supported arch).
    public bool PlayIntent => _playIntent;
    void PublishSignal(AudioHostSignal signal)
    {
        if (_disposed) return;
        _signals.OnNext(signal with
        {
            Command = signal.Command == default ? _latestCommand : signal.Command,
            PlayWhenReady = signal.PlayWhenReady ?? _playIntent,
            PositionUpperBoundMs = PositionUpperBoundMs
        });
    }

    long? PositionUpperBoundMs
    {
        get
        {
            if (_clockStale || _session is not PcmAudioSession session || session.Format.SampleRate <= 0) return null;
            long upper = Math.Max(0, session.SubmittedFrames - _activeStartFrame) * 1000 / session.Format.SampleRate;
            upper = Math.Max(PositionMs, upper);
            return _activeDurMs > 0 ? Math.Min(_activeDurMs, upper) : upper;
        }
    }

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

    public void Load(AudioLoadRequest request)
    {
        lock (_gate)
        {
            if (IsOlder(request.Command, _commandSnapshot))
            {
                // Finding #4 (library-v3-1): this used to be a silent no-op — no media opens, no signal, nothing for
                // the controller to react to. The controller now always mints a fresh command for a real Load
                // (PlaybackController.MintLoadCommand), so this should never legitimately fire; keep it loud instead
                // of silent in case some other caller still races a load behind a newer transport command.
                _log.Info($"[audio] Load dropped as older seq={request.Command.Sequence} gen={request.Command.ItemGeneration} " +
                    $"(current seq={_commandSnapshot.Sequence} gen={_commandSnapshot.ItemGeneration})");
                return;
            }
            _commandSnapshot = request.Command;
        }
        _loadCommand = request.Command;
        _activeQueueItem = request.Item.QueueItemId;
        _playIntent = request.PlayWhenReady;
        _requestedStartMs = Math.Max(0, request.StartPositionMs);
        LoadFastStart(request.Source.Start);
        long epoch = Volatile.Read(ref _loadEpoch);
        var token = _loadCancellation.Token;
        var started = _loadStarted;
        Enqueue(async () =>
        {
            var body = await request.Source.Body.WaitAsync(token);
            await started;
            if (epoch == Volatile.Read(ref _loadEpoch)) await SupplyBodyAsync(body, epoch);
        });
    }

    public void LoadFastStart(in AudioFastStart start)
    {
        _clockStale = true;
        long epoch = Interlocked.Increment(ref _loadEpoch);
        _loadCancellation.Cancel();
        _loadCancellation = new CancellationTokenSource();
        _openingBytes?.Cancel();
        // Finding #1: a seek parked waiting for a PREVIOUS load's session to attach is moot the instant a newer load
        // starts (a takeover, a track change while B was still resolving) — leaving it would let ResolveParkedSeekAfterOpen
        // apply it to whatever track opens next instead of the one it was actually issued against.
        _parkedSeek = null;
        var setup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadStarted = setup.Task;
        var copy = start;
        Enqueue(async () =>
        {
            try { await LoadFastStartAsync(copy, epoch, setup); }
            finally { setup.TrySetResult(); }
        });
    }

    public void SupplyBody(in AudioStreamHandle body)
    {
        var copy = body;
        long epoch = Volatile.Read(ref _loadEpoch);
        var started = _loadStarted;
        Enqueue(async () =>
        {
            await started;
            if (epoch == Volatile.Read(ref _loadEpoch)) await SupplyBodyAsync(copy, epoch);
        });
    }

    public void Play() => Submit(new AudioTransportRequest(
        new PlaybackCommandId(_latestCommand.ItemGeneration, _latestCommand.Sequence + 1), AudioTransportAction.Play, true));

    public void Pause() => Submit(new AudioTransportRequest(
        new PlaybackCommandId(_latestCommand.ItemGeneration, _latestCommand.Sequence + 1), AudioTransportAction.Pause, false));

    // TEMP DIAGNOSTIC (#3 resume overshoot): log raw/derived position for a few ticks after a resume, then self-disable.
    int _diagResumeTicks;

    // Gapless hand-off snapshot (Tick / CommitCrossfade / OpenSession only — never the WASAPI Write path).
    // Pre-allocated fields only; WaveeLogger's interpolated Info handler builds the string IFF Info is enabled.
    long _gaplessXrunsAtArm;
    long _gaplessAEndClock;
    long _gaplessAEndWall;
    int _gaplessArmed;            // 1 once the approaching-boundary snapshot has been taken
    int _gaplessHardCutPending;   // 1 after Ended without a mixer commit — next OpenSession is B of a hard cut

    public void Stop() => Submit(new AudioTransportRequest(
        new PlaybackCommandId(_latestCommand.ItemGeneration, _latestCommand.Sequence + 1), AudioTransportAction.Stop, false));

    public void Seek(long positionMs, SeekMode mode) => Submit(new AudioTransportRequest(
        new PlaybackCommandId(_latestCommand.ItemGeneration, _latestCommand.Sequence + 1), AudioTransportAction.Seek,
        _playIntent, positionMs, mode, mode == SeekMode.Accurate ? PlaybackSeekKind.Commit : PlaybackSeekKind.Preview));

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0, 1);
        _core.Volume.Value = (float)_volume;
        var v = _volume;
        Enqueue(() => { _session?.SetVolume(v); return Task.CompletedTask; });
    }

    // ── IAudioLevelSource ────────────────────────────────────────────────────────────────────────────────────────────
    // _effects is always constructed (never null), so this is never null for THIS host; the nullable shape on the
    // interface is for hosts that never have a tap at all (SilentAudioHost, a Connect-viewer session).
    public IReadSignal<VisualizerFrame>? Levels => _effects.Visualizer;

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

    void Enqueue(Func<Task> operation)
    {
        if (_disposed) return;
        long epoch = Volatile.Read(ref _loadEpoch);
        _mailbox.Enqueue(async () =>
        {
            try { await operation(); }
            catch (Exception) when (epoch != Volatile.Read(ref _loadEpoch)) { }
        });
    }

    async Task LoadFastStartAsync(AudioFastStart start, long epoch, TaskCompletionSource setup)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) return;
        if (_session is PcmAudioSession outgoing && _playIntent)
            await outgoing.FadeOutAsync(TimeSpan.FromMilliseconds(50), _loadCancellation.Token);
        if (epoch != Volatile.Read(ref _loadEpoch)) return;
        DetachLive(dispose: false);
        AbandonPendingJoin(_session as PcmAudioSession, "load");
        await DisposePreparedSlotAsync();
        if (epoch != Volatile.Read(ref _loadEpoch)) return;
        _lastStart = start;
        _errorReported = false;
        _lastState = PlaybackState.Idle;

        if (start.HeadBytes.Length == 0)
        {
            // No clear head → defer session open until SupplyBody attaches the body (Spotify non-fast / external).
            _activeStream = null;
            _activeFileIdHex = start.FileIdHex;
            _pendingFmt = start.Format; _pendingDurMs = start.DurationMs; _pendingGainDb = start.NormalizationGainDb;
            setup.TrySetResult();
            // A load nobody asked to hear (no play intent — the launch-recovery paused restore) announces nothing:
            // see PlayIntentGate. Attaching a clear head/body is work the host does regardless; the buffering BAR is
            // only ever honest while there is something the user is waiting to hear.
            if (PlayIntentGate.ShouldAnnounceBuffering(_playIntent))
                PublishSignal(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
            return;
        }

        var kind = KindOf(start.Format);
        int skip = DetectSkipOffset(start.HeadBytes.Span, start.Format);
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, start.HeadBytes, start.HeadBytes.Length, start.FileIdHex, _log, _bodyDisk);
        _activeStream = stream;
        _activeFileIdHex = start.FileIdHex;
        var bytes = new SpotifyMediaByteSource(stream, skip, kind, start.DurationMs, DbToLinear(start.NormalizationGainDb));
        _openingBytes = bytes;
        setup.TrySetResult();
        if (PlayIntentGate.ShouldAnnounceBuffering(_playIntent))
            PublishSignal(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
        await OpenSessionAsync(bytes, epoch);
    }

    AudioFormat _pendingFmt;
    long _pendingDurMs;
    float _pendingGainDb;

    async Task SupplyBodyAsync(AudioStreamHandle body, long epoch)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) { _log.Info($"supply-body ignored stale epoch file={body.FileIdHex}"); return; }
        if (PlayIntentGate.ShouldAnnounceBuffering(_playIntent))
            PublishSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, PositionMs));

        // Local file (a "Play file…" pick / a shell drop) — open the file and the session now. Same deferred-open shape
        // as the external branch below: the plan carried an EMPTY head, so LoadFastStart parked the load and THIS is
        // where the session actually opens. The decoder kind comes from the resolver's extension map (never a sniff —
        // a local file's extension is the only thing we have, and the provider already refused anything unsupported).
        if (body.SourceKind == AudioSourceKind.LocalFile)
        {
            LocalFileAudioStream file;
            try { file = await Task.Run(() => LocalFileAudioStream.Open(body.CdnUrl), _loadCancellation.Token); }
            catch (Exception ex)
            {
                // The resolver checked existence, so this is the narrow window where the file vanished (or is
                // unreadable) between resolve and open. Surface it typed rather than leaving a silent "playing" state —
                // the serialized pump's own catch would only have logged it.
                _log.Info($"local file open failed path={body.CdnUrl}: {ex.GetType().Name}: {ex.Message}");
                PublishSignal(AudioHostSignal.Fault(0, AudioKeyFailureReason.Restricted, ex.Message));
                return;
            }
            var localBytes = new SpotifyMediaByteSource(file, 0, KindOf(body.Format), body.DurationMs, 1f) { ReopenBody = body };
            _activeStream = null;
            await OpenSessionAsync(localBytes, epoch);
            return;
        }

        // External plain-HTTP (podcast MP3) — open a plain stream and the session now.
        if (body.SourceKind == AudioSourceKind.ExternalPlain)
        {
            var http = await PlainHttpAudioStream.OpenAsync(_http, body.CdnUrl, _log, _loadCancellation.Token);
            var kind = SniffExternalKind(http.ContentType) ?? WaveeDecoderKind.Mp3;
            var extBytes = new SpotifyMediaByteSource(http, 0, kind, body.DurationMs, 1f) { ReopenBody = body };
            _activeStream = null;
            await OpenSessionAsync(extBytes, epoch);
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
                live = await _openLiveSource(body.CdnUrl, _loadCancellation.Token);
            }
            catch (Exception ex)
            {
                // ONE connect attempt, then a typed failure: a station that is down should say so immediately rather
                // than spin for the reconnect budget the RUNNING stream is entitled to.
                _log.Info($"live stream open failed url={body.CdnUrl}: {ex.GetType().Name}: {ex.Message}");
                var reason = ex is IOException ? AudioKeyFailureReason.Network : AudioKeyFailureReason.Restricted;
                PublishSignal(AudioHostSignal.Fault(0, reason, ex.Message));
                return;
            }
            if (epoch != Volatile.Read(ref _loadEpoch)) { await live.DisposeAsync(); return; }

            // Content-Type first (authoritative when the station bothers), then the first bytes (most do not bother).
            var liveKind = await IdentifyLiveSourceAsync(live, _loadCancellation.Token);
            if (epoch != Volatile.Read(ref _loadEpoch)) { await live.DisposeAsync(); return; }
            if (liveKind == WaveeDecoderKind.Aac && !MfAacDecoder.IsAvailable())
            {
                await live.DisposeAsync();
                PublishSignal(AudioHostSignal.Fault(0, AudioKeyFailureReason.ArchUnsupported,
                    "this Windows edition has no AAC decoder (install the Media Feature Pack)"));
                return;
            }

            AttachLive(live);
            _activeStream = null;
            // Duration 0 is load-bearing, not laziness: it keeps every ending-soon / gapless-join / prepared-next arm
            // switched off for a stream that has no end to approach (LiveSessionRules.SessionDurationMs states it).
            var liveBytes = new SpotifyMediaByteSource(live, 0, liveKind,
                LiveSessionRules.SessionDurationMs(isLive: true, body.DurationMs), 1f) { ReopenBody = body };
            await OpenSessionAsync(liveBytes, epoch);
            return;
        }

        // Module-served bytes (stream/open|read|close over the module RPC).
        if (body.SourceKind == AudioSourceKind.ModuleStream)
        {
            await SupplyModuleStreamAsync(body, epoch);
            return;
        }

        var decryptor = BuildDecryptor(body);
        var cdnUrls = body.CdnUrls ?? (string.IsNullOrEmpty(body.CdnUrl) ? Array.Empty<string>() : new[] { body.CdnUrl });

        if (_activeStream is { } s)
        {
            // Fast path: attach the encrypted body to the already-open, already-playing head stream.
            await s.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, null, _loadCancellation.Token);
            if (epoch != Volatile.Read(ref _loadEpoch)) return;
            // Size the CDN read-ahead window to this track's bitrate + connection cost now that the body (and its
            // RangedHttpSource) exist — dropout mitigation: an undersized window under-fetches on a fast connection,
            // an oversized one wastes the shared read-ahead budget on a metered one.
            s.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(body.Format), Wavee.NetworkPolicy.IsMetered);
            // Retain the body handle on the active source so a mid-track device-rate change can rebuild an INDEPENDENT stream.
            if (_openingBytes is not null) _openingBytes.ReopenBody = body;
            else if (_activeBytes is not null) _activeBytes.ReopenBody = body;
            // Finding #2: ReopenBody just became available — if a seek was parked waiting for exactly this (the
            // clear head was already playing, but there was nothing to independently reopen yet), run it for real now.
            await FlushParkedSeekAfterBodyAttachAsync();
            return;
        }

        // Deferred (Load / non-fast): build a head-less stream, attach the body, then open the session.
        var kind2 = KindOf(_pendingFmt);
        int skip = SpotifyAesCtr.SpotifyHeaderSize;   // no head to inspect → the standard Spotify container offset
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, ReadOnlyMemory<byte>.Empty, 0, body.FileIdHex, _log, _bodyDisk);
        await stream.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, null, _loadCancellation.Token);
        // _pendingFmt (captured back in LoadFastStartAsync's deferred branch), not body.Format — this path has no
        // fast-start head to have carried the negotiated rung on.
        stream.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(_pendingFmt), Wavee.NetworkPolicy.IsMetered);
        _activeStream = stream;
        // Retain the body handle so a mid-track device-rate change can rebuild an INDEPENDENT stream (see SoftReloadAsync).
        var bytes = new SpotifyMediaByteSource(stream, skip, kind2, _pendingDurMs, DbToLinear(_pendingGainDb)) { ReopenBody = body };
        await OpenSessionAsync(bytes, epoch);
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
        if (epoch != Volatile.Read(ref _loadEpoch)) { bytes.DisposeSource(); return; }
        var loadCommand = _loadCommand;
        long requestedStart = _requestedStartMs;
        bool replacing = _session is PcmAudioSession;
        try
        {
            var source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
            var cancellation = _loadCancellation.Token;
            using var cancellationRegistration = cancellation.Register(bytes.Cancel);
            IMediaSession session;
            if (_session is PcmAudioSession existing)
            {
                var prepared = await (await GetBackendAsync()).PrepareAtAsync(source,
                    PrepareContext.For(existing.Format, existing.NormalizationMode, existing.ReferenceLufsValue),
                    MsToFrames(requestedStart, existing.Format.SampleRate), cancellation);
                if (epoch != Volatile.Read(ref _loadEpoch)) { await prepared.DisposeAsync(); return; }
                var oldBytes = _activeBytes;
                long achieved = prepared is AudioPreparedItem audio ? audio.StartPositionFrames : 0;
                await existing.ReplacePreparedAsync(prepared, achieved, cancellation);
                oldBytes?.Cancel();
                session = existing;
            }
            else
            {
                var backend = await GetBackendAsync();
                session = await Task.Run(async () => await backend.OpenAsync(source,
                    new MediaOpenOptions { StartPaused = true }, cancellation), cancellation);
            }
            if (epoch != Volatile.Read(ref _loadEpoch)) { if (!ReferenceEquals(session, _session)) await session.DisposeAsync(); return; }
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
            if (!replacing)
            {
                if (session is PcmAudioSession pcmSession) pcmSession.BindEffects(_effects);
                session.ConnectSignals(_sink);
            }
            _session = session;
            _core.SetError(null);
            _activeBytes = bytes;
            _openingBytes = null;   // retained so a mid-track device-rate change can re-open the SAME stream at the new rate
            // Fresh active track: reset the crossfade/offset bookkeeping to this session's primary voice.
            _activeStartMs = 0;
            _clockStale = false;   // THIS session now owns _core.Position — the reported clock is honest again
            _activeDurMs = bytes.DurationMs;
            _activeUri = _lastStart.TrackUri;
            _crossfadeInFlight = false;
            _endedHold = 0;
            _prepRearmSent = 0;
            _promotePending = false;
            _activeJoinFrame = 0;
            if (session is PcmAudioSession pcm)
            {
                _activePrimaryId = pcm.PrimaryVoiceIdValue;
                _activeStartFrame = pcm.SampleClock;
                // W2 / device-reopen fix (A1): express the join frame relative to THIS session's clock, not track-absolute
                // — a fresh open's clock is 0 so this is normally the same number, but going through GaplessJoinClock keeps
                // every writer of _activeJoinFrame on the one formula (see SoftReloadAsync, which reopens at a NON-zero clock).
                _activeJoinFrame = pcm.VoiceTotalFrames > 0 ? pcm.SampleClock + pcm.VoiceTotalFrames : GaplessJoinClock.JoinFrameFor(pcm.SampleClock, bytes.DurationMs, 0, pcm.Format.SampleRate);
                // Fix 2: re-arm THIS track if a mid-track default-endpoint switch adopts a different sample rate. The engine
                // raises DeviceFormatChanged off its cold device thread; the handler enqueues a soft reload. Unsubscribed when
                // this session is disposed (DisposeSessionAsync / the soft reload) so no handler leaks across loads.
                ObserveDeviceRebuilds(pcm);
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
            if (requestedStart > 0 && !replacing)
                await session.SeekAsync(TimeSpan.FromMilliseconds(requestedStart), EngineSeekMode.Accurate);
            if (session is PcmAudioSession positioned)
                _activeStartFrame = positioned.SampleClock - MsToFrames(PositionMs, positioned.Format.SampleRate);
            if (_playIntent && autoResume)
            {
                if (session is PcmAudioSession ready) ready.FadeIn(TimeSpan.FromMilliseconds(50));
                await session.PlayAsync();
            }
            StartTicker();
            if (epoch != Volatile.Read(ref _loadEpoch)) return;
            PublishSignal(new AudioHostSignal(_playIntent ? AudioHostSignalKind.Playing : AudioHostSignalKind.Paused,
                PositionMs, IsPlaying, IsBuffering, false)
            { Command = loadCommand, OperationStatus = PlaybackOperationStatus.Applied });
            ResolveParkedSeekAfterOpen();   // finding #1: this session is what a parked seek (see ApplySeekAsync) was waiting on
        }
        catch (OperationCanceledException) when (epoch != Volatile.Read(ref _loadEpoch) || _loadCancellation.IsCancellationRequested) { bytes.Cancel(); }
        catch (Exception ex)
        {
            if (epoch != Volatile.Read(ref _loadEpoch)) return;
            _log.Info($"fluent-audio-host open failed track={_activeUri} file={_activeFileIdHex}: {ex.GetType().Name}: {ex.Message}");
            PublishSignal(AudioHostSignal.Fault(PositionMs, AudioKeyFailureReason.None, ex.Message));
        }
    }

    // ── Fix 2: mid-track device sample-rate change → soft reload at the NEW device rate ──────────────────────────────
    // A default-endpoint switch to a device that clocks at a different rate keeps audio alive (the engine swaps the sink in
    // PcmAudioSession.RebuildSink) but leaves the decoder/graph/mixer/rings frozen at the OLD rate, so the currently-playing
    // track drifts off pitch. The engine raises DeviceFormatChanged fire-and-forget OFF its cold device thread; we coalesce
    // it and enqueue a soft reload onto the serialized pump. NOT called for a same-endpoint control-panel rate change (no
    // WASAPI notification) — that self-corrects on the next load via Fix 1. Runs on a ThreadPool thread; keep it minimal.
    void ObserveDeviceRebuilds(PcmAudioSession session)
    {
        DetachDeviceRebuilds();
        _deviceCallbackSession = session;
        _deviceRebuiltHandler = (format, frame) => Enqueue(() =>
        {
            if (!ReferenceEquals(session, _session)) return Task.CompletedTask;
            _recoverySession = session;
            _recoveryPositionMs = frame * 1000 / Math.Max(1, format.SampleRate);
            _clockStale = true;
            Volatile.Write(ref _softReloadPending, 1);
            TryStartSoftReloadDrain();
            return Task.CompletedTask;
        });
        session.DeviceRebuilt += _deviceRebuiltHandler;
    }

    void DetachDeviceRebuilds()
    {
        if (_deviceCallbackSession is { } session && _deviceRebuiltHandler is { } handler)
            session.DeviceRebuilt -= handler;
        _deviceCallbackSession = null;
        _deviceRebuiltHandler = null;
    }

    // Device callbacks only post observations. The owner coalesces bursts while preparation yields to controls.
    void TryStartSoftReloadDrain()
    {
        if (_disposed || Interlocked.CompareExchange(ref _softReloading, 1, 0) != 0) return;
        long epoch = Volatile.Read(ref _loadEpoch);
        Enqueue(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _softReloadPending, 0) == 1)
                {
                    if (_disposed || epoch != Volatile.Read(ref _loadEpoch)) break;
                    await SoftReloadAsync(epoch, _recoverySession, _recoveryPositionMs);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _softReloading, 0);
                if (Volatile.Read(ref _softReloadPending) == 1) TryStartSoftReloadDrain();
            }
        });
    }

    // Reopening a cursor restores audio discarded from the old device cushion. A different negotiated format needs
    // a new graph; a same-format rebuild adopts ready PCM into the endpoint the engine has already replaced.
    async Task SoftReloadAsync(long epoch, PcmAudioSession? expectedSession, long position)
    {
        var cancellation = _loadCancellation.Token;
        await _seekWorker.WaitAsync(cancellation);
        SpotifyMediaByteSource? fresh = null;
        IPreparedItem? item = null;
        PcmAudioSession? opened = null;
        bool installed = false;
        try
        {
            if (epoch != Volatile.Read(ref _loadEpoch) || _session is not PcmAudioSession session
                || !ReferenceEquals(session, expectedSession) || _activeBytes is not { } original) return;

            // Finding #3 (library-v3-1): a non-seekable source (a live/ICY stream outside the device-recovery
            // allowance, or a module stream that never reports Seekable) or one whose encrypted body has not
            // attached yet (the fast-start window) cannot be independently reopened — ReopenSourceAsync would throw,
            // and an uncaught throw here used to reach the mailbox as a Fault that stopped playback over a device
            // change the user never asked about. Keep the CURRENT session exactly as it is instead: the engine
            // already kept it alive across the device swap (PcmAudioSession.RebuildSink) — all this owes it is
            // clearing the now-stale clock and re-affirming playback so it isn't left silent at ramp 0.
            bool canReopen = original.ReadStream.AsStream().CanSeek
                || original.ReopenBody?.SourceKind == AudioSourceKind.LiveStream;
            if (!canReopen || original.ReopenBody is null)
            {
                await KeepExistingSessionAfterFailedSoftReloadAsync(session, "source not independently reopenable yet");
                return;
            }
            string token = _prepToken ?? "";
            string uri = _prepUri;
            await DisposePreparedSlotAsync();
            AbandonPendingJoin(session, "device-rebuilt");
            _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Invalidated, token, uri, position, 0, "device-rebuilt"));
            bool restorePosition = original.ReadStream.AsStream().CanSeek;
            if (!restorePosition) position = 0; // Reconnect an endless station at its new live edge.
            fresh = await ReopenSourceAsync(original, cancellation, forDeviceRecovery: true);
            using var registration = cancellation.Register(fresh.Cancel);
            var source = MediaSource.FromPull(fresh).WithKind(MediaKind.PcmAudio);
            var backend = await GetBackendAsync();
            long achieved;
            if (session.RequiresGraphRebuild)
            {
                opened = (PcmAudioSession)await Task.Run(async () => await backend.OpenAsync(source,
                    new MediaOpenOptions { StartPaused = true }, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (epoch != Volatile.Read(ref _loadEpoch) || !ReferenceEquals(session, _session)) return;
                DetachDeviceRebuilds();
                _session = null;
                original.Cancel();
                await Task.Run(async () => await session.DisposeAsync());
                cancellation.ThrowIfCancellationRequested();
                if (epoch != Volatile.Read(ref _loadEpoch) || _session is not null) return;
                opened.BindEffects(_effects);
                _session = opened;
                _activeBytes = fresh;
                installed = true;
                ObserveDeviceRebuilds(opened);
                opened.ConnectSignals(_sink);
                opened.SetVolume(_volume);
                opened.SetMuted(_muted);
                if (restorePosition)
                    await opened.SeekAsync(TimeSpan.FromMilliseconds(position), EngineSeekMode.Accurate);
                session = opened;
                achieved = restorePosition
                    ? MsToFrames((long)_core.Position.Peek().TotalMilliseconds, session.Format.SampleRate) : 0;
            }
            else
            {
                item = await backend.PrepareAtAsync(source,
                    PrepareContext.For(session.Format, session.NormalizationMode, session.ReferenceLufsValue),
                    MsToFrames(position, session.Format.SampleRate), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (epoch != Volatile.Read(ref _loadEpoch) || !ReferenceEquals(session, _session)) return;
                achieved = item is AudioPreparedItem audio ? audio.StartPositionFrames : MsToFrames(position, session.Format.SampleRate);
                await session.ReplacePreparedAsync(item, achieved, cancellation);
                installed = true;
                original.Cancel();
                if (epoch != Volatile.Read(ref _loadEpoch) || !ReferenceEquals(session, _session)) return;
                _activeBytes = fresh;
            }
            if (epoch != Volatile.Read(ref _loadEpoch)) return;
            _activeStream = fresh.ReadStream as SpotifyAudioStream;
            if (fresh.ReadStream is LiveHttpAudioStream live)
            {
                // The retired decoder still owns the old stream until its producer exits.
                DetachLive(dispose: false);
                AttachLive(live);
            }
            _activeStartMs = 0;
            _activePrimaryId = session.PrimaryVoiceIdValue;
            _activeStartFrame = session.SampleClock - achieved;
            _activeJoinFrame = session.SampleClock + Math.Max(0, session.VoiceTotalFrames - achieved);
            _crossfadeInFlight = false;
            _gaplessArmed = 0;
            _endedHold = 0;
            _prepRearmSent = 0;
            _clockStale = false;
            _core.SetError(null);
            if (_playIntent) { session.FadeIn(TimeSpan.FromMilliseconds(20)); await session.PlayAsync(); }
            StartTicker();
            _log.Info($"[audio] device recovered track={_activeUri} file={_activeFileIdHex} positionMs={achieved * 1000 / session.Format.SampleRate} rate={session.Format.SampleRate}");
        }
        // Finding #3: a race past the guard above (the body/source stopped being reopenable between the check and the
        // actual reopen) lands here instead of the mailbox's onError → Fault. Same benign no-op: keep playing.
        catch (Exception ex) when ((ex is NotSupportedException or InvalidOperationException)
            && epoch == Volatile.Read(ref _loadEpoch) && ReferenceEquals(_session, expectedSession))
        {
            if (_session is PcmAudioSession current)
                await KeepExistingSessionAfterFailedSoftReloadAsync(current, $"reopen failed ({ex.GetType().Name})");
        }
        finally
        {
            if (!installed)
            {
                fresh?.Cancel();
                if (opened is not null) await Task.Run(async () => await opened.DisposeAsync());
                if (item is not null) await Task.Run(async () => await item.DisposeAsync());
                fresh?.DisposeSource();
            }
            _seekWorker.Release();
        }
    }

    // Finding #3 (library-v3-1): the shared no-op tail for a device-rebuild soft reload that could not (yet)
    // independently reopen its source. Clears the clock staleness ObserveDeviceRebuilds set before the drain started
    // and re-affirms playback so the session is never left ticking silently at a stale/faded state.
    async Task KeepExistingSessionAfterFailedSoftReloadAsync(PcmAudioSession session, string reason)
    {
        _clockStale = false;
        if (_playIntent) { session.FadeIn(TimeSpan.FromMilliseconds(20)); await session.PlayAsync(); }
        StartTicker();
        _log.Info($"[audio] device rebuild kept the current session ({reason}) track={_activeUri} file={_activeFileIdHex}");
    }

    async Task DisposeSessionAsync()
    {
        // FIRST, before anything awaits: a live transport's reader may be parked inside the ring buffer's blocking Read
        // on the feed thread, and AudioFeedThread.Stop only joins that worker for 500 ms. Disposing the live stream here
        // wakes it (ObjectDisposedException reads as EOF at the decode edge) so the teardown below completes in time.
        DetachLive();
        var old = _session;
        _session = null;
        var oldBytes = _activeBytes;
        oldBytes?.Cancel();
        _openingBytes?.Cancel();
        _openingBytes = null;
        _activeBytes = null;
        DetachDeviceRebuilds();
        _activeStream = null;
        _retiringStream = null;
        var retiringBytes = _retiringBytes;
        _retiringBytes = null;
        retiringBytes?.Cancel();
        // A manual load/stop supersedes any prepared next, any in-flight crossfade, and any pending gapless join —
        // the join's mixer voice dies with the session; only its kept stream needs an explicit dispose.
        lock (_gate)
        {
            _joinBytes?.Cancel();
            _joinPending = false;
            _joinStream = null; _joinBytes = null; _joinToken = null; _joinUri = ""; _joinDurMs = 0;
            _joinFrame = 0; _joinVoiceId = 0; _joinVoice = null; _joinTotalFrames = 0;
        }
        _endedHold = 0;
        _prepRearmSent = 0;
        _promotePending = false;
        _activeJoinFrame = 0;
        await DisposePreparedSlotAsync();
        Volatile.Write(ref _softReloadPending, 0);   // drop any device-rate reload deferred for the track we're tearing down
        _crossfadeInFlight = false;
        _committedToken = null;
        _activeUri = "";
        _activeStartMs = 0;
        _activeDurMs = 0;
        _gaplessArmed = 0;
        _lastState = PlaybackState.Idle;   // a later legitimate session must never inherit a stale Playing/Ended edge
        if (old is not null) await Task.Run(async () => await old.DisposeAsync());
        oldBytes?.Cancel();
        retiringBytes?.Cancel();
    }

    // Dispose the prepared (not-yet-committed) slot and clear its fields. The prepared voice has NOT entered the mixer,
    // so disposing the IPreparedItem here is correct; once committed we clear the fields WITHOUT disposing (see Tick).
    async Task DisposePreparedSlotAsync()
    {
        lock (_gate)
        {
            _prepareCancellation?.Cancel();
            _prepareCancellation = null;
        }
        var bytes = _prepBytes;
        bytes?.Cancel();
        var item = _prepItem;
        _prepItem = null;
        _prepStream = null;
        _prepBytes = null;
        _prepToken = null;
        _prepUri = "";
        _prepDurMs = 0;
        _prepOverlap = false;
        if (item is not null) await Task.Run(async () => await item.DisposeAsync());
        // An in-flight prepare owns its source until its cancellation continuation unwinds.
        // An installed item closes the source through DecoderAudioSource after producer retirement.
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

        _liveRecoveryHandler = e => Enqueue(() =>
        {
            if (!ReferenceEquals(_activeLive, live)) return Task.CompletedTask;   // a superseded session's transport still finishing up
            if (LiveSessionRules.IsTerminal(e.Stage)) { _liveRecovering = false; ReportLiveDrop(e.Error); return Task.CompletedTask; }
            bool recovering = LiveSessionRules.IsRecovering(e.Stage);
            if (recovering == _liveRecovering) return Task.CompletedTask;         // edge-triggered: Attempt after Started is not new news
            _liveRecovering = recovering;
            if (recovering)
                PublishSignal(new AudioHostSignal(AudioHostSignalKind.Recovering, PositionMs, true, false, false,
                    PlaybackRecoveryKind.Network));
            else if (_playIntent)
                // Recovered: clear the banner by re-asserting Playing. Gated on play intent so a reconnect that lands
                // while the user has paused does not announce playback that is not happening.
                PublishSignal(new AudioHostSignal(AudioHostSignalKind.Playing, PositionMs, true, false, false));
            return Task.CompletedTask;
        });
        ((IAudioNetworkRecoverySource)live).NetworkRecovery += _liveRecoveryHandler;

        _liveTitleHandler = title => Enqueue(() =>
        {
            if (ReferenceEquals(_activeLive, live)) MetadataKnown?.Invoke(title, live.StationName);
            return Task.CompletedTask;
        });
        live.StreamTitleChanged += _liveTitleHandler;
    }

    /// <summary>Unwire and dispose the live transport. Idempotent, and safe to call for a non-live session.</summary>
    void DetachLive(bool dispose = true)
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
        if (dispose) { try { live.Dispose(); } catch { /* the socket is going away regardless */ } }
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
        PublishSignal(AudioHostSignal.Fault(PositionMs, reason, error?.Message ?? "the live stream dropped"));
    }

    // ── IPreparedAudioHost: prepared-next + real overlapping crossfade ───────────────────────────────────────────────

    public Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _prepInFlight);
        return _mailbox.InvokeAsync(async () =>
        {
            try { await PrepareNextCoreAsync(request, ct); }
            finally { Interlocked.Decrement(ref _prepInFlight); }
        });
    }

    async Task PrepareNextCoreAsync(AudioPrepareRequest request, CancellationToken ct)
    {
        if (_session is not PcmAudioSession session || request.Owner.Generation != _latestCommand.ItemGeneration) return;
        await DisposePreparedSlotAsync();
        if (request.Owner.Generation != _latestCommand.ItemGeneration || !ReferenceEquals(session, _session)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _loadCancellation.Token);
        lock (_gate) _prepareCancellation = cancellation;
        var start = request.Start;
        _prepToken = request.Token;
        _prepTarget = request.TargetItem;
        _prepUri = start.TrackUri;
        _prepDurMs = start.DurationMs;
        _prepOverlap = request.AllowOverlap;
        SpotifyMediaByteSource? bytes = null;
        IPreparedItem? prepared = null;
        Task? bodyAttachment = null;
        try
        {
            if (start.HeadBytes.IsEmpty)
            {
                var body = await request.Source.Body.WaitAsync(cancellation.Token);
                int skip = body.SourceKind == AudioSourceKind.SpotifyEncrypted ? SpotifyAesCtr.SpotifyHeaderSize : 0;
                bytes = await OpenBodySourceAsync(body, skip, KindOf(body.Format), body.DurationMs,
                    DbToLinear(body.NormalizationGainDb), cancellation.Token);
            }
            else
            {
                var stream = SpotifyAudioStream.CreateHeadOnly(_http, start.HeadBytes, start.HeadBytes.Length, start.FileIdHex, _log, _bodyDisk);
                bytes = new SpotifyMediaByteSource(stream, DetectSkipOffset(start.HeadBytes.Span, start.Format),
                    KindOf(start.Format), start.DurationMs, DbToLinear(start.NormalizationGainDb));
                // Start attachment before decoder preparation. Neither operation waits behind the other.
                bodyAttachment = AttachPreparedBodyAsync(request.Source.Body, stream, bytes, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_prepToken != request.Token) throw new OperationCanceledException(cancellation.Token);
            _prepStream = bytes.ReadStream as SpotifyAudioStream;
            _prepBytes = bytes;
            using var registration = cancellation.Token.Register(bytes.Cancel);
            prepared = await (await GetBackendAsync()).PrepareAsync(MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio),
                PrepareContext.For(session.Format, session.NormalizationMode, session.ReferenceLufsValue), cancellation.Token);
            if (bodyAttachment is not null) await bodyAttachment;
            cancellation.Token.ThrowIfCancellationRequested();
            if (_prepToken != request.Token || !ReferenceEquals(session, _session))
                throw new OperationCanceledException(cancellation.Token);
            _prepItem = prepared;
            _log.Info($"[gapless] prepare-primed token={request.Token} ready={prepared.IsReady} leadIn={prepared.Gapless.LeadInFrames} trailPad={prepared.Gapless.TrailPadFrames} overlap={request.AllowOverlap} dur={start.DurationMs}");
        }
        catch
        {
            cancellation.Cancel();
            bytes?.Cancel();
            if (bodyAttachment is not null) { try { await bodyAttachment; } catch { } }
            if (_prepToken == request.Token)
            {
                _prepToken = null; _prepBytes = null; _prepStream = null; _prepItem = null;
            }
            if (prepared is not null) await prepared.DisposeAsync();
            bytes?.DisposeSource();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_prepareCancellation, cancellation)) _prepareCancellation = null;
            }
        }
    }

    async Task AttachPreparedBodyAsync(Task<AudioStreamHandle> pendingBody, SpotifyAudioStream stream,
        SpotifyMediaByteSource bytes, CancellationToken ct)
    {
        try
        {
            var body = await pendingBody.WaitAsync(ct);
            var urls = body.CdnUrls ?? (string.IsNullOrEmpty(body.CdnUrl) ? [] : new[] { body.CdnUrl });
            await stream.AttachBodyWithNativeDecryptorAsync(BuildDecryptor(body), urls, null, ct);
            stream.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(body.Format), Wavee.NetworkPolicy.IsMetered);
            bytes.ReopenBody = body;
        }
        catch (Exception error)
        {
            stream.AbortBody(error);
            throw;
        }
    }

    public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<AudioPrepareCancelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (token == _prepToken)
                {
                    await DisposePreparedSlotAsync();
                    completion.TrySetResult(AudioPrepareCancelResult.Cancelled);
                }
                else if (token == _joinToken && _joinGate is { } gate && gate.TryCancel())
                {
                    AbandonPendingJoin(_session as PcmAudioSession, "queue-cancelled");
                    completion.TrySetResult(AudioPrepareCancelResult.Cancelled);
                }
                else if (token == _committedToken || token == _joinToken)
                    completion.TrySetResult(AudioPrepareCancelResult.AlreadyStarted);
                else completion.TrySetResult(AudioPrepareCancelResult.NotFound);
            }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    public Task<bool> TryPromotePreparedAsync(AudioPromoteRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            IPreparedItem? item = null;
            SpotifyMediaByteSource? bytes = null;
            bool installed = false;
            try
            {
                if (IsOlder(request.Command, _latestCommand)
                    && request.Command.ItemGeneration != _latestCommand.ItemGeneration)
                { completion.TrySetResult(false); return; }
                // The render boundary already consumed B even if the slower played-clock report has not arrived.
                // Reconcile that one commit before satisfying the controller's already-selected target.
                if (_joinPending && request.Token == _joinToken && request.TargetItem == _joinTarget
                    && _joinGate is { IsCommitted: true } && _session is PcmAudioSession committed)
                    AnnounceGaplessJoin(committed, RawPositionMs);
                if (request.Token == _committedToken && request.TargetItem == _activeQueueItem)
                {
                    if (!IsOlder(request.Command, _latestCommand))
                    {
                        _latestCommand = request.Command;
                        _playIntent = request.PlayWhenReady;
                        if (_session is { } active)
                        {
                            if (_playIntent) await active.PlayAsync();
                            else await active.PauseAsync();
                        }
                    }
                    completion.TrySetResult(true);
                    return;
                }
                if (_joinPending && request.Token == _joinToken && request.TargetItem == _joinTarget
                    && _session is PcmAudioSession scheduled && _joinVoice is not null)
                {
                    await PromoteScheduledNextAsync(request, scheduled, ct);
                    completion.TrySetResult(true);
                    return;
                }
                if (_session is not PcmAudioSession session || request.Token != _prepToken
                    || request.TargetItem != _prepTarget || _prepItem is not { IsReady: true } prepared
                    || prepared.MixRate != session.Format.SampleRate)
                { completion.TrySetResult(false); return; }
                long epoch = Volatile.Read(ref _loadEpoch);
                var oldBytes = _activeBytes;
                item = prepared;
                bytes = _prepBytes;
                var stream = _prepStream;
                var uri = _prepUri;
                long duration = _prepDurMs;
                _prepToken = null; _prepItem = null; _prepStream = null; _prepBytes = null;
                if (!IsOlder(request.Command, _latestCommand))
                {
                    _latestCommand = request.Command;
                    _playIntent = request.PlayWhenReady;
                }
                using var promotion = CancellationTokenSource.CreateLinkedTokenSource(ct, _loadCancellation.Token);
                await session.FadeOutAsync(TimeSpan.FromMilliseconds(50), promotion.Token);
                await session.ReplacePreparedAsync(item, 0, promotion.Token);
                installed = true;
                oldBytes?.Cancel();
                if (epoch != Volatile.Read(ref _loadEpoch) || !ReferenceEquals(session, _session))
                { completion.TrySetResult(false); return; }
                _activeBytes = bytes; _activeStream = stream;
                _activeUri = uri; _activeDurMs = duration; _activeStartMs = 0;
                _activeQueueItem = request.TargetItem;
                _activePrimaryId = session.PrimaryVoiceIdValue;
                _activeStartFrame = session.SampleClock;
                _activeJoinFrame = session.SampleClock + (item.TotalFrames > 0 ? item.TotalFrames : MsToFrames(duration, session.Format.SampleRate));
                _clockStale = false; _crossfadeInFlight = false; _committedToken = request.Token;
                _gaplessArmed = 0; _prepRearmSent = 0; _endedHold = 0;
                if (_playIntent && !promotion.IsCancellationRequested)
                { session.FadeIn(TimeSpan.FromMilliseconds(50)); await session.PlayAsync(); }
                StartTicker();
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
            finally
            {
                if (!installed && item is not null)
                {
                    bytes?.Cancel();
                    await Task.Run(async () => await item.DisposeAsync());
                }
            }
        });
        return completion.Task;
    }

    async Task PromoteScheduledNextAsync(AudioPromoteRequest request, PcmAudioSession session, CancellationToken ct)
    {
        var voice = _joinVoice!;
        long voiceId = _joinVoiceId;
        long duration = _joinDurMs;
        long totalFrames = _joinTotalFrames;
        var gate = _joinGate;
        var bytes = _joinBytes;
        var stream = _joinStream;
        string uri = _joinUri;
        var outgoingBytes = _activeBytes;
        long epoch = Volatile.Read(ref _loadEpoch);
        // The scheduled ring stays renderer-owned until adoption. Cancelling its gate before acknowledgement
        // would let the renderer retire it while the manual fade was still draining.
        _joinPending = false;
        _joinVoice = null; _joinVoiceId = 0; _joinFrame = 0; _joinTotalFrames = 0;
        _joinToken = null; _joinUri = ""; _joinDurMs = 0; _joinStream = null; _joinBytes = null; _joinGate = null;
        if (!IsOlder(request.Command, _latestCommand))
        {
            _latestCommand = request.Command;
            _playIntent = request.PlayWhenReady;
        }
        _clockStale = true;
        bool adopted = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _loadCancellation.Token);
        try
        {
            await session.FadeOutAsync(TimeSpan.FromMilliseconds(50), cancellation.Token);
            long achieved = await session.PromoteScheduledVoiceAsync(voiceId, voice,
                TimeSpan.FromMilliseconds(duration), totalFrames, cancellation.Token);
            adopted = true;
            gate?.TryCancel();
            outgoingBytes?.Cancel();
            if (epoch != Volatile.Read(ref _loadEpoch) || !ReferenceEquals(session, _session)) return;
            _activeBytes = bytes; _activeStream = stream;
            _activeUri = uri; _activeDurMs = duration; _activeStartMs = 0;
            _activeQueueItem = request.TargetItem;
            _activePrimaryId = session.PrimaryVoiceIdValue;
            _activeStartFrame = session.SampleClock - achieved;
            _activeJoinFrame = session.SampleClock + Math.Max(0, totalFrames - achieved);
            _clockStale = false; _crossfadeInFlight = false; _committedToken = request.Token;
            _gaplessArmed = 0; _prepRearmSent = 0; _endedHold = 0;
            if (_playIntent && !cancellation.IsCancellationRequested)
            { session.FadeIn(TimeSpan.FromMilliseconds(50)); await session.PlayAsync(); }
            StartTicker();
        }
        finally
        {
            if (!adopted)
            {
                gate?.TryCancel();
                try { await session.RemoveVoiceAsync(voiceId, CancellationToken.None); }
                finally
                {
                    bytes?.Cancel();
                    if (epoch == Volatile.Read(ref _loadEpoch)) _clockStale = false;
                }
            }
        }
    }

    // VoiceScheduler owns both transition shapes. The host only transfers leases and observes played markers.
    bool SchedulePreparedTransition(PcmAudioSession session, IPreparedItem item)
    {
        if (item.MixRate != session.Format.SampleRate) { _ = DisposePreparedSlotAsync(); return false; }
        long clock = session.SampleClock;
        int fadeMs = _prepOverlap ? EffectiveFadeMs : 0;
        long length = session.ExactVoiceEndFrame;
        if (length < 0)
        {
            // A metadata estimate can position an intentional overlap. A butt join requires exact EOF,
            // otherwise rounding inserts silence or sums the outgoing tail into the next track.
            if (fadeMs <= 0) return false;
            length = MsToFrames(_activeDurMs, session.Format.SampleRate);
            if (clock > _activeStartFrame + length - MsToFrames(fadeMs, session.Format.SampleRate)) return false;
        }
        if (length <= 0) return false;
        long lead = MsToFrames(Math.Max(GaplessCommitLeadMs, fadeMs + 1500), session.Format.SampleRate);
        if (clock < _activeStartFrame + length - lead) return false;
        if (item.TotalFrames > 0) fadeMs = (int)Math.Min(fadeMs, item.TotalFrames * 1000 / session.Format.SampleRate);
        fadeMs = (int)Math.Min(fadeMs, length * 1000 / session.Format.SampleRate);
        if (_voiceScheduler is null || !ReferenceEquals(_schedulerSession, session) || _schedulerRate != session.Format.SampleRate)
        {
            _voiceScheduler = new VoiceScheduler(session.Format.SampleRate, session.Format.Channels,
                declickMs: 5, voiceChainFactory: session.BuildVoiceChain);
            _schedulerSession = session;
            _schedulerRate = session.Format.SampleRate;
        }
        var scheduler = _voiceScheduler;
        scheduler.SetVoiceInstaller(session.AddCrossfadeVoice);
        scheduler.SetEnvelopeInstaller((id, envelope) =>
        {
            if (!session.SetVoiceEnvelope(id, envelope))
                throw new InvalidOperationException("The output command queue could not accept a transition envelope.");
        });
        scheduler.BeginActive(_activePrimaryId, _activeStartFrame, length,
            fadeMs > 0 ? ScheduledTransition.Crossfade(TimeSpan.FromMilliseconds(fadeMs)) : ScheduledTransition.Gapless,
            session.Format.SampleRate * 8, session.NormalizationMode, session.ReferenceLufsValue);
        if (!scheduler.SubmitPrepared(scheduler.MarkPreparing(), item)) return false;
        TransitionOutcome outcome = scheduler.ScheduleReady(clock, session.MixerRef);
        if (outcome == TransitionOutcome.None) return false;
        _joinPending = true;
        _joinGate = scheduler.TransitionGate;
        _joinFadeMs = outcome == TransitionOutcome.Crossfaded ? fadeMs : 0;
        _joinFrame = _joinFadeMs > 0 ? scheduler.CrossfadeStartFrame : Math.Max(clock, scheduler.JoinEndFrame);
        _joinVoiceId = scheduler.IncomingVoiceId;
        _joinVoice = item.AudioVoice;
        _joinTotalFrames = item.TotalFrames;
        _joinToken = _prepToken; _joinTarget = _prepTarget; _joinUri = _prepUri; _joinDurMs = _prepDurMs;
        _joinStream = _prepStream; _joinBytes = _prepBytes;
        if (item is AudioPreparedItem audio) audio.TransferOwnership();
        _prepItem = null; _prepStream = null; _prepBytes = null; _prepToken = null;
        _prepUri = ""; _prepDurMs = 0; _prepOverlap = false;
        _log.Info($"[gapless] scheduled outcome={outcome} frame={_joinFrame} fadeMs={_joinFadeMs} clock={clock}");
        return true;
    }

    void AnnounceGaplessJoin(PcmAudioSession sess, long rawPos)
    {
        string token, uri;
        lock (_gate)
        {
            if (!_joinPending) return;
            _joinPending = false;
            _crossfadeInFlight = true;
            _retiringStream = _activeStream;
            _retiringBytes = _activeBytes;
            _retiringVoiceId = _activePrimaryId;
            _activeStream = _joinStream;
            _activeBytes = _joinBytes;
            _committedToken = _joinToken;
            token = _joinToken ?? "";
            uri = _joinUri;
            _activeStartMs = rawPos - Math.Max(0, sess.PlayedFrames - _joinFrame) * 1000 / sess.Format.SampleRate;
            _activeStartFrame = _joinFrame;
            _committedFadeMs = _joinFadeMs;
            _activePrimaryId = _joinVoiceId;
            _activeQueueItem = _joinTarget;
            _activeDurMs = _joinDurMs;
            _activeUri = _joinUri;
            _activeJoinFrame = _joinFrame + MsToFrames(_joinDurMs, sess.Format.SampleRate);
            if (_joinVoice is { } voice)
                sess.SetActiveVoice(_joinVoiceId, voice, TimeSpan.FromMilliseconds(_joinDurMs), _joinTotalFrames);
            _joinToken = null; _joinUri = ""; _joinDurMs = 0; _joinFrame = 0; _joinVoiceId = 0;
            _joinVoice = null; _joinTotalFrames = 0; _joinStream = null; _joinBytes = null;
        }
        _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, PositionMs, _committedFadeMs));
        long xruns = SessionXruns();
        _log.Info($"[gapless] join-live token={token} uri={uri} clock={SessionClock()} raw={rawPos} xruns={xruns} xrunDelta={xruns - _gaplessXrunsAtArm}");
        _gaplessArmed = 0;
        _gaplessHardCutPending = 0;
        _prepRearmSent = 0;
    }

    // Remove an invalid future join through the render command acknowledgement before cancelling its byte reads.
    void AbandonPendingJoin(PcmAudioSession? sess, string reason)
    {
        SpotifyAudioStream? stream;
        SpotifyMediaByteSource? bytes;
        long voiceId;
        lock (_gate)
        {
            if (!_joinPending) return;
            _joinGate?.TryCancel();
            _joinGate = null;
            _joinPending = false;
            voiceId = _joinVoiceId;
            stream = _joinStream;
            bytes = _joinBytes;
            _joinStream = null; _joinBytes = null; _joinToken = null; _joinUri = ""; _joinDurMs = 0;
            _joinFrame = 0; _joinVoiceId = 0; _joinVoice = null; _joinTotalFrames = 0;
        }
        if (sess is not null)
            Enqueue(async () => { await sess.RemoveVoiceAsync(voiceId, CancellationToken.None); bytes?.Cancel(); });
        else { bytes?.DisposeSource(); stream?.Dispose(); }
        _log.Info($"[gapless] join-abandoned id={voiceId} reason={reason}");
    }

    // A late ready slot adopts its existing PCM ring after EOF while retaining the endpoint. The asynchronous
    // promotion owns the hold/reset acknowledgement; suppress Ended until that transfer succeeds or fails.
    bool TryPromoteAtEnd()
    {
        if (_promotePending) return true;
        if (_session is not PcmAudioSession || _prepItem is not { IsReady: true } || _prepToken is null) return false;
        string token = _prepToken;
        string uri = _prepUri;
        var target = _prepTarget;
        _promotePending = true;
        Enqueue(async () =>
        {
            try
            {
                bool promoted = await TryPromotePreparedAsync(new AudioPromoteRequest(token, _latestCommand, target, _playIntent), _loadCancellation.Token);
                if (promoted)
                    _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, PositionMs, 0));
                else PublishSignal(new AudioHostSignal(AudioHostSignalKind.Ended, PositionMs, false, false, false));
            }
            finally { _promotePending = false; }
        });
        return true;
    }

    void StartTicker() => _ticker.Change(50, 50);
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
        long rawPos = RawPositionMs;
        long pos = PositionMs;

        // Per-xrun diagnostics (always-on, house rule — never gated, never debugger-only). Drained here, off the RT
        // feed thread, on every 200 ms tick: each incident the RT thread actually recorded (a ring underrun — real
        // dropped audio, not merely "a callback in which something starved") becomes its OWN Warning line with when,
        // how much, and why, instead of the old "xruns=17" cumulative count that only ever surfaced at the NEXT track
        // boundary. Keep the existing [gapless] xrun fields as they are — they still answer "what happened right at
        // the hand-off"; this answers "what happened mid-track that nothing else could see".
        if (_session is PcmAudioSession xrunSession) DrainXruns(xrunSession, pos, state);
        if (_clockStale) return;

        if (_diagResumeTicks > 0)   // TEMP (#3): trace position for a few ticks after resume to locate the overshoot
        {
            _diagResumeTicks--;
            _log.Info($"[posdiag] tick raw={rawPos} pos={pos} activeStart={_activeStartMs} state={state} lastState={_lastState}");
        }

        if (!_errorReported && _core.Error.Peek() is { } err)
        {
            _errorReported = true;
            PublishSignal(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None, err.Message));
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
        if (_playIntent && state == PlaybackState.Playing && !_clockStale && !_crossfadeInFlight && !_joinPending
            && _prepItem is { IsReady: true } item
            && _session is PcmAudioSession session && Volatile.Read(ref _softReloading) == 0)
            SchedulePreparedTransition(session, item);

        if (_joinPending && _session is PcmAudioSession joinSess)
        {
            if (state == PlaybackState.Ended)
            {
                // B's voice died before the join (its decode faulted to EOF) — only then can the mixer drain while a join
                // is pending. Fall back to the ordinary Ended path (the controller hard-cuts).
                AbandonPendingJoin(joinSess, "ended-before-join");
            }
            else if (_joinGate is { IsCommitted: true } && joinSess.PlayedFrames >= _joinFrame)
            {
                AnnounceGaplessJoin(joinSess, rawPos);
                pos = PositionMs;   // re-read: now B-relative (≈0 at the hand-off)
            }
        }
        // ── close the hand-off: once the fade has elapsed (0 for a gapless join), retire A's stream, report Completed ──
        else if (_crossfadeInFlight && (rawPos - _activeStartMs) >= _committedFadeMs)
        {
            _crossfadeInFlight = false;
            _retiringStream = null;
            var retiringBytes = _retiringBytes;
            _retiringBytes = null;
            long retiringVoice = _retiringVoiceId;
            if (_session is PcmAudioSession retiringSession)
                Enqueue(async () =>
                {
                    await retiringSession.RemoveVoiceAsync(retiringVoice, CancellationToken.None);
                    retiringBytes?.Cancel();
                });
            _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Completed, _committedToken ?? "", _activeUri, PositionMs, _committedFadeMs));
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
                    PublishSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, pos, true, false, false,
                        LiveSessionRules.TickRecoveryKind(_activeIsLive, _liveRecovering)));
                    break;
                }
                if (_lastState == PlaybackState.Playing)
                {
                    // A3 (clock-flicker fix): a device-format soft reload's own OpenSessionAsync briefly reports
                    // _activeStartMs=0/_clockStale=false BEFORE the restoring seek lands (the new session opens at
                    // clock 0, seeks to the saved playhead only after), so a steady-state tick landing in that window
                    // would carry pos≈0 — the second, wrong, value behind the reported "flicker between two values"
                    // (A3). Skip the repeat PositionTick while a reload is in flight or the clock is provably stale;
                    // the one-shot Playing edge below still fires so the UI is never stuck reporting the OLD state.
                    if (Volatile.Read(ref _softReloading) != 0 || _clockStale) break;
                    PublishSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, pos));
                }
                else PublishSignal(new AudioHostSignal(AudioHostSignalKind.Playing, pos));
                break;
            case PlaybackState.Paused:
                if (_lastState != PlaybackState.Paused) PublishSignal(new AudioHostSignal(AudioHostSignalKind.Paused, pos));
                if (!_playIntent && !_clockStale) StopTicker();
                break;
            case PlaybackState.Opening:
            case PlaybackState.Buffering:
            case PlaybackState.Stalled:
                if (_lastState != state) PublishSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, pos));
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

                // Keep the endpoint and wait for the owned preparation. Network/source cancellation owns
                // the timeout; a UI poll count must never reopen an endpoint while its next ring is filling.
                if (Volatile.Read(ref _prepInFlight) > 0)
                {
                    _endedHold = 1;
                    if (endedEdge) PublishSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, pos, false, true, false));
                    break;
                }
                _endedHold = 0;

                _gaplessAEndClock = SessionClock();
                _gaplessAEndWall = Environment.TickCount64;
                int primed = _prepItem is { IsReady: true } ? 1 : 0;
                int body = _prepBytes?.ReopenBody is not null ? 1 : 0;
                _gaplessHardCutPending = _crossfadeInFlight ? 0 : 1;
                _log.Info($"[gapless] ended clock={_gaplessAEndClock} raw={rawPos} pos={pos} fadeMs={fadeMs} overlap={_prepOverlap} primed={primed} body={body} inFlight={_crossfadeInFlight} xruns={SessionXruns()} xrunDelta={SessionXruns() - _gaplessXrunsAtArm}");
                StopTicker();
                PublishSignal(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
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
                long ageMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(ev.Timestamp).TotalMilliseconds;
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
        _loadCancellation.Cancel();
        CancelPendingSeek();
        lock (_gate) _prepareCancellation?.Cancel();
        Interlocked.Increment(ref _loadEpoch);
        StopTicker();
        await _ticker.DisposeAsync();
        await _mailbox.InvokeAsync(DisposeSessionAsync);
        await _mailbox.DisposeAsync();
    }
}
