using System;
using System.Collections.Concurrent;
using System.Threading;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Media.Windows;
using FluentGpu.WindowsApi.Media.PlayReady;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

// Both namespaces declare a SeekMode: the app's transport enum (Wavee.Core, framework-neutral, what the IMediaHost seam
// speaks) and the engine's (FluentGpu.Media, what IMediaPlayer.SeekAsync takes). Aliased rather than fully qualified so
// every use below reads unambiguously; this file is the boundary where one maps to the other.
using SeekMode = Wavee.Core.SeekMode;
using EngineSeekMode = FluentGpu.Media.SeekMode;

namespace Wavee.SpotifyLive.Audio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The VIDEO half of the ONE current media (Milestone B). Implements the app's common IMediaHost seam over the unified
// FluentGpu.Media engine MediaPlayer configured with the MF (+ native PlayReady) backend — this host now OWNS that player
// (the M0 ownership inversion; the builder moved here out of PopOutVideoStage, which only PRESENTS it). The
// PlaybackController swaps its current host to THIS one at a video boundary and drives the
// common transport verbs (Play/Pause/Stop/Seek/SetVolume/PositionMs/IsPlaying) here; the video-specific LoadVideo(source)
// is called at the switch (NOT via IMediaHost). State is polled off the engine's reactive signals and translated into the
// SAME AudioHostSignal channel the audio host emits, so the source-agnostic NowPlayingProjection is unchanged.
//
// MF-PUMP CAVEAT (important): the MediaFoundation video session only ADVANCES while a mounted MediaPlayerElement pumps it
// (IMediaPlayer.PumpVideo, driven from a composited surface's frame loop). This host builds the MediaPlayer and reports
// whatever the session publishes; it does NOT itself pump. A surface (the in-window PiP or the detached pop-out) must be
// mounted and bound to THIS player for frames/position to advance — the video-placement state guarantees one is mounted
// whenever a video is the current media. Surfaces bind to the exact same player instance through CurrentPlayer +
// PlayerChanged (the app mirrors them onto PlaybackBridge.VideoPlayer, a UI-thread signal the surfaces read); EXACTLY ONE
// mounted surface may pump a given player at a time, which the single-placement state guarantees.
//
// SUPERSESSION IS SERIALIZED (the video→video wedge fix). The native PlayReady/CENC session is a PROCESS-GLOBAL singleton
// with a session-less ABI, so a predecessor's teardown Stop lands on whatever session holds the latch. When LoadVideo tore
// the old player down fire-and-forget and immediately opened the successor (a video→video track skip: two LoadVideo calls
// ~250ms apart, no host swap), the predecessor's Stop could shut the SUCCESSOR down; that returned a SUCCESS hr, settled
// the snapshot on native "stopped" → PlaybackState.Idle, and Idle is a state Tick's switch has no case for — so the host
// went silent forever and the transport stayed paused at 0:00 with no Fault to recover from. Every load/stop now runs on
// the single VideoLoadPump worker (teardown awaited to completion before the successor is built, only the LATEST request
// built), and every load arms a VideoStartWatchdog so a session that never reaches a playing/advancing state raises a
// Fault on the normal signal channel instead of wedging silently.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The video-media host: the app's common <see cref="IMediaHost"/> seam over a FluentGpu.Media <see cref="MediaPlayer"/>
/// with the MF/PlayReady backend. Zero-alloc-friendly (struct signals, a single reused timer) and fail-soft (every engine
/// call is guarded — a video failure surfaces as an <see cref="AudioHostSignalKind.Error"/>, never a throw across the seam).
/// </summary>
public sealed class FluentVideoMediaHost : IMediaHost
{
    readonly WaveeLogger _log;
    readonly SimpleEvent<AudioHostSignal> _signals = new();
    readonly object _gate = new();
    readonly Timer _ticker;
    readonly IAppSettings? _settings;

    // Every load/stop is serialized through ONE worker (teardown→build, latest-wins) so a predecessor's process-global
    // native Stop can never land on a successor session. See VideoLoadPump for the wedge this removes.
    readonly VideoLoadPump<VideoLoadRequest> _pump;
    // Players handed over for disposal off the pump (Stop nulls the player synchronously so IsPlaying is false at once,
    // but the ACTUAL native teardown is queued so it stays ordered against the next load).
    readonly ConcurrentQueue<MediaPlayer> _toDispose = new();

    MediaPlayer? _player;
    string _sourceKey = "";           // the PopOutVideoSource.Key the live player was built for ("" = none)
    double _volume = 1.0;
    bool _muted;                      // the app's mute intent — re-applied to EVERY player this host builds (see BuildAndOpenAsync)
    PlaybackState _lastState = PlaybackState.Idle;
    bool _errorReported;
    // The duration last relayed for the CURRENT load (0 = none yet). NOT a one-shot bool: Media Foundation publishes a
    // duration at first LOADEDMETADATA, which for an adaptive/DASH manifest is commonly still 0, and a manifest can
    // later revise it — so a latch-on-first-positive would freeze the wrong length for the whole track.
    long _reportedDurMs;
    // The live timeline last relayed for the CURRENT load, and whether anything has been relayed at all. Value-gated the
    // same way the duration is: the engine republishes a TimelineInfo at 10 Hz and the window's two ends creep forward
    // continuously, so an ungated relay would wake the projection (and every player-bar consumer) ten times a second
    // forever. A flag flip or a > 250 ms move in any field is a real change; anything smaller is the edge drifting.
    LiveWindow _reportedLive;
    bool _liveReported;
    // Once-per-load diagnostics for the ONE state that makes a playing video look permanently stuck: an empty
    // NaturalSize. MediaPlayerElement reads that as audio-only, so it never punches a video hole and keeps the
    // "Starting playback…" spinner up over a session that is otherwise fine. Logged so the log alone tells the two
    // apart next time (see MfMediaSession's LATE NATURAL SIZE block, which is what keeps re-asking for it).
    bool _sizeLogged, _noSizeLogged;
    // The last COMPOSITED PLACEMENT geometry logged (natural/content/place). Value-gated so the 10Hz tick logs only
    // when the realized placement actually moves — which is exactly when a letterbox appears or disappears.
    VideoSurfaceGeometry _loggedGeometry = VideoSurfaceGeometry.Empty;
    bool _disposed;
    int _lastAppliedAutoCap = -1;
    QualitySelection _lastObservedQuality;
    bool _hasObservedQuality;

    // ── the per-load start watchdog (guarded by _gate; evaluated on the existing 200ms ticker, zero-alloc) ────────────
    VideoStartWatchdog _watchdog;
    bool _playIntent;                 // does the controller/user want the CURRENT load playing right now?
    bool _progressed;                 // has the CURRENT load demonstrably started/advanced? (latched, reset per load)
    // The start position this load was opened at (0 = from the beginning), kept ONLY until the real duration arrives so
    // Tick can re-clamp it: a position carried from the audio edit can exceed a shorter video edit, which would
    // otherwise open at/past the end. Cleared once clamped (or once duration proves it needed no clamp).
    long _startAtMs;
    bool _startSeekPending;
    // Remaining play re-assertions for the CURRENT load (see the Ready/Paused arm in Tick). Bounded so a genuinely
    // paused-by-the-user session can never be nudged back into playing, and a wedged one cannot spin.
    int _playReassertsLeft;

    /// <summary>Bounded teardown budget for one player. Larger than the 3s native thread join inside the protected
    /// player's Dispose, so a healthy teardown always completes inside it and a wedged one still cannot block forever.</summary>
    const int TeardownTimeoutMs = 5_000;
    /// <summary>Re-relay <see cref="DurationKnown"/> only when the engine's duration moves more than this, so a
    /// jittering adaptive estimate does not spam the projection (and the seek bar) every tick.</summary>
    const long DurationRelayEpsilonMs = 250;
    /// <summary>Re-relay <see cref="LiveWindowKnown"/> only when a window edge moves more than this. The live edge
    /// advances continuously, so without a band the 10 Hz timeline signal would republish forever.</summary>
    const long LiveRelayEpsilonMs = 250;
    /// <summary>A carried start position within this much of the end counts as "past the end" and is pulled back.</summary>
    const long StartClampGuardMs = 250;
    /// <summary>How far back from the end a clamped carried position lands — enough to see that playback resumed.</summary>
    const long StartClampBackoffMs = 2_000;
    /// <summary>How many times one load may re-assert play when the engine sits Ready with the play command unlanded.
    /// At the 200ms tick that is ~1.6s of nudging — long enough to cover a lost command, far short of fighting a user.</summary>
    const int PlayReassertBudget = 8;

    /// <summary>Bounded budget for the OPEN the pump awaits. It exists only so a pathological open cannot stall every
    /// later skip; the start watchdog is what turns a genuinely stuck session into a fault.</summary>
    const int OpenTimeoutMs = 15_000;

    /// <summary>The start-watchdog budget: the engine's OWN start timeout (ProtectedMediaSession + the native player both
    /// use <c>FG_VIDEO_START_TIMEOUT_MS</c>, default 20s) plus a 5s margin. Deliberately OUTSIDE it, so whenever the engine
    /// can diagnose the failure its richer typed DRM message wins; this host-level net only catches what those two are
    /// structurally unable to see — a session that settled on <c>PlaybackState.Idle</c> (native "stopped"), where the
    /// session watchdog's Opening/Buffering precondition and the native watchdog's state ≤ 1 precondition are both false.
    /// Erring large is deliberate: a false fault (a cold CDM spin-up, an unmounted/idle surface) is worse than a slow one.</summary>
    static int DefaultStartWatchdogMs =>
        (int.TryParse(Environment.GetEnvironmentVariable("FG_VIDEO_START_TIMEOUT_MS"), out int t) && t > 0 ? t : 20_000) + 5_000;

    public FluentVideoMediaHost(WaveeLogger log = default, int startWatchdogMs = 0, IAppSettings? settings = null)
    {
        if (startWatchdogMs <= 0) startWatchdogMs = DefaultStartWatchdogMs;
        _log = log;
        _settings = settings;
        _watchdog = new VideoStartWatchdog(startWatchdogMs);
        _ticker = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _pump = new VideoLoadPump<VideoLoadRequest>(TeardownAsync, BuildAndOpenAsync, IsAlreadyLive, log);
    }

    /// <summary>The live engine player for the current video source (null before the first <see cref="LoadVideo"/> / after a
    /// clear). A mounted video surface (PiP / pop-out) MUST bind to this instance so it pumps the MF session — see the MF-pump
    /// caveat above. Rebuilt on every source change (a clear↔DRM / track switch), announced on <see cref="PlayerChanged"/>.</summary>
    public MediaPlayer? CurrentPlayer { get { lock (_gate) return _player; } }

    /// <summary>The <c>PopOutVideoSource.Key</c> the live <see cref="CurrentPlayer"/> was built for ("" = no player). Used by
    /// <see cref="LoadVideo"/> to make a redundant load of the SAME source a no-op, so re-entering the video path for a track
    /// already playing can never restart it from 0.</summary>
    public string CurrentSourceKey { get { lock (_gate) return _sourceKey; } }

    /// <summary>Fires (on the caller's thread) whenever <see cref="CurrentPlayer"/> is rebuilt/cleared, so a mounted surface
    /// can re-bind to the new player instance. Carries the new player (null after a stop/clear).</summary>
    public event Action<MediaPlayer?>? PlayerChanged;

    /// <summary>Fires ONCE per loaded source (on the ticker thread) the first time the media engine reports a positive
    /// duration, carrying <c>(sourceKey, durationMs)</c>. It exists because a user-attached local video is a DIFFERENT
    /// EDIT with its OWN length: the Track's Spotify duration would mis-scale the seek bar and mis-report on the Connect
    /// wire. The app routes it to <c>NowPlayingProjection.SetDurationOverride</c> for <c>local:video:</c> keys only —
    /// a Spotify music video keeps publishing the catalog duration exactly as before.</summary>
    public event Action<string, long>? DurationKnown;

    /// <summary>Fires on the ticker thread whenever the media engine's LIVE TIMELINE changes materially for the current
    /// load, carrying <c>(sourceKey, window)</c>. The app routes it to <c>NowPlayingProjection.SetLiveWindow</c>.
    /// <para>It exists because live-ness has a SHAPE, not just a flag. Media Foundation reports a broadcast's sliding
    /// DVR window as an ordinary finite duration — which is precisely how a six-hour YouTube live stream rendered as
    /// <c>0:03 / -3:22</c> — so the only honest reading of "how much of this can I rewind?" is the engine's own
    /// seekable range, published as one atomic record so the bar can never mix a window from the previous variant with
    /// this one's edge.</para>
    /// <para>Value-gated (see <see cref="LiveRelayEpsilonMs"/>): the underlying signal republishes at 10 Hz.</para></summary>
    public event Action<string, LiveWindow>? LiveWindowKnown;

    // ── IMediaHost common transport (forwarded to the routed MediaPlayer) ────────────────────────────────────────────
    public long PositionMs
    {
        get { var p = CurrentPlayer; return p is null ? 0 : Math.Max(0, (long)p.Position.Peek().TotalMilliseconds); }
    }

    public bool IsPlaying { get { var p = CurrentPlayer; return p is not null && p.IsPlaying.Peek(); } }

    // No player loaded → no clock to trust; PositionMs's own null branch already reports 0 in that case, and this is
    // the seam-level signal that the 0 is unknown, not a real position (see IMediaHost.ClockValid).
    public bool ClockValid => CurrentPlayer is not null;

    public IObservable<AudioHostSignal> Signals => _signals;

    public void Play()
    {
        lock (_gate) _playIntent = true;
        var p = CurrentPlayer;
        if (p is not null) { try { _ = p.PlayAsync(); } catch (Exception ex) { _log.Info($"video-host play failed: {ex.Message}"); } }
        StartTicker();
    }

    public void Pause()
    {
        // Play intent drops FIRST: a deliberate pause must never age toward a start-watchdog fault ("loaded, user paused
        // before the first frame" is not a failure). Stopping the ticker is the second, belt-and-braces half of that.
        lock (_gate) _playIntent = false;
        var p = CurrentPlayer;
        if (p is not null) { try { _ = p.PauseAsync(); } catch (Exception ex) { _log.Info($"video-host pause failed: {ex.Message}"); } }
        StopTicker();
    }

    public void Stop()
    {
        StopTicker();
        RetractLiveWindow();   // a live window must never outlive the session it described (see TeardownAsync)
        MediaPlayer? old;
        lock (_gate)
        {
            old = _player;
            _player = null;
            _sourceKey = "";
            _lastState = PlaybackState.Idle;
            _errorReported = false;
            _reportedDurMs = 0;
            _reportedLive = default;
            _liveReported = false;
            _sizeLogged = false;
            _noSizeLogged = false;
            _playIntent = false;
            _progressed = false;
            _startAtMs = 0;
            _startSeekPending = false;
            _playReassertsLeft = 0;
            _watchdog.Disarm();
        }
        if (old is not null)
        {
            try { old.Stop(); } catch (Exception ex) { _log.Info($"video-host stop failed: {ex.Message}"); }
            // The observable state (CurrentPlayer/IsPlaying) is already clear; the NATIVE teardown is queued so it stays
            // ordered against whatever load comes next instead of racing it on the process-global session.
            _toDispose.Enqueue(old);
            PlayerChanged?.Invoke(null);
        }
        // Unconditional: this also bumps the pump epoch, so a load that was queued or mid-build is invalidated and can
        // never overtake the stop.
        _pump.RequestClear();
    }

    public void Seek(long positionMs, SeekMode mode) => SeekPlayer(CurrentPlayer, positionMs, mode);

    /// <summary>Seek one player instance, fail-soft. Shared by the transport <see cref="Seek"/> and by the load path, so
    /// a repositioning request lands identically whether it arrives on the live session or with a fresh load.
    /// <para>The MODE is passed straight through to the engine: this is the one host that can actually honour it — a
    /// keyframe seek skips the decode-to-exact-PTS pass, which is what makes a scrub preview cheap enough to issue
    /// repeatedly during a drag (and what a DRM/CENC session needs to stay responsive while the thumb moves).</para></summary>
    void SeekPlayer(MediaPlayer? p, long positionMs, SeekMode mode)
    {
        if (p is null) return;
        long target = Math.Max(0, positionMs);

        // GO LIVE, without a second transport verb. A committed seek AT (or past) the live edge of a live source is the
        // "GO LIVE" gesture by construction — that is exactly what PlaybackBridge.GoLive issues — and the right way to
        // honour it is the engine's own GoLiveAsync, which lands a few seconds INSIDE the edge instead of on top of it.
        // Seeking literally to the edge of a moving window is how a live session stalls waiting for segments that do not
        // exist yet. Scrub previews are deliberately excluded: a drag that passes over the edge must not fire this.
        if (mode == SeekMode.Accurate && IsAtOrPastLiveEdge(p, target))
        {
            try { _ = p.GoLiveAsync(); return; }
            catch (Exception ex) { _log.Info($"video-host go-live failed, falling back to a seek: {ex.Message}"); }
        }

        EngineSeekMode engineMode = mode == SeekMode.Keyframe ? EngineSeekMode.Keyframe : EngineSeekMode.Accurate;
        try { _ = p.SeekAsync(TimeSpan.FromMilliseconds(target), engineMode); }
        catch (Exception ex) { _log.Info($"video-host seek failed: {ex.Message}"); }
    }

    /// <summary>How close to the live edge a committed seek counts AS "go live". One tick of slack plus the engine's own
    /// go-live backoff, so the button's target (the edge as the bar last saw it) still reads as the edge after the few
    /// hundred milliseconds it took to travel through the controller.</summary>
    const long GoLiveToleranceMs = 2_000;

    static bool IsAtOrPastLiveEdge(MediaPlayer p, long targetMs)
    {
        TimelineInfo t;
        try { t = p.Timeline.Peek(); } catch { return false; }
        if (!t.IsLive) return false;
        long edge = (long)t.LiveEdge.TotalMilliseconds;
        return edge > 0 && targetMs >= edge - GoLiveToleranceMs;
    }

    /// <summary>Has the timeline moved enough to be worth waking the projection? A flag flip always has; a numeric drift
    /// under <see cref="LiveRelayEpsilonMs"/> never has (the live edge advances continuously at 10 Hz).</summary>
    static bool LiveWindowChanged(in LiveWindow a, in LiveWindow b)
        => a.IsLive != b.IsLive
        || a.IsAtLiveEdge != b.IsAtLiveEdge
        || Math.Abs(a.SeekableStartMs - b.SeekableStartMs) > LiveRelayEpsilonMs
        || Math.Abs(a.SeekableEndMs - b.SeekableEndMs) > LiveRelayEpsilonMs
        || Math.Abs(a.LiveEdgeMs - b.LiveEdgeMs) > LiveRelayEpsilonMs
        || Math.Abs(a.PositionMs - b.PositionMs) > LiveRelayEpsilonMs;

    /// <summary>Publish "no longer a live broadcast" for the load that is going away, iff a live window was ever
    /// published for it. Idempotent and silent when nothing was live.</summary>
    void RetractLiveWindow()
    {
        if (!_reportedLive.IsLive) return;
        string key;
        lock (_gate) key = _sourceKey;
        _reportedLive = default;
        _liveReported = false;
        try { LiveWindowKnown?.Invoke(key, default); }
        catch (Exception ex) { _log.Info($"video-host live retract failed: {ex.Message}"); }
    }

    /// <summary>Is the session provably able to accept a seek yet? Readiness is the direct statement of what a positive
    /// duration used to stand in for — and unlike a duration it exists on a live source, which has none.</summary>
    static bool IsSeekReady(MediaPlayer p, long durMs)
    {
        if (durMs > 0) return true;
        PlaybackState st;
        try { st = p.State.Peek(); } catch { return false; }
        return st is PlaybackState.Ready or PlaybackState.Playing or PlaybackState.Paused;
    }

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0, 1);
        var p = CurrentPlayer;
        if (p is not null) { try { p.SetVolume(_volume); } catch (Exception ex) { _log.Info($"video-host volume failed: {ex.Message}"); } }
    }

    /// <summary>Mute/unmute the VIDEO half of the current media. Mirrors <see cref="SetVolume"/>: the intent is STORED, so a
    /// player built later (a track skip, a placement flip, a video that starts while the app is already muted) opens muted
    /// too — <see cref="BuildAndOpenAsync"/> re-applies it to every player. Without this the app's mute was silently lost the
    /// moment a music video became the current media and the video played at full volume.</summary>
    public void SetMuted(bool muted)
    {
        _muted = muted;
        var p = CurrentPlayer;
        if (p is not null) { try { p.SetMuted(_muted); } catch (Exception ex) { _log.Info($"video-host mute failed: {ex.Message}"); } }
    }

    /// <summary>Apply and persist the app-wide protected-video preference. Zero selects true Auto; a height pins the
    /// matching manifest rung. If no video is active, the preference is picked up by the next load.</summary>
    public void SetPreferredQuality(int height)
    {
        height = Math.Max(0, height);
        _settings?.Set(Wavee.WaveeSettings.VideoQuality, height);
        var p = CurrentPlayer;
        if (p is null) return;
        if (height == 0) { _ = p.SelectQualityAsync(QualitySelection.Auto); return; }
        for (int i = 0; i < p.Qualities.Variants.Count; i++)
            if (p.Qualities.Variants[i].Resolution.Height == height)
            { _ = p.SelectQualityAsync(QualitySelection.Pin(p.Qualities.Variants[i].Id)); return; }
    }

    /// <summary>Re-evaluate the metered Auto cap for the live player.</summary>
    public void RefreshQualityPolicy() => ApplyQualityPolicy(CurrentPlayer);

    // ── video-specific load (called by the controller at the switch, NOT via IMediaHost) ─────────────────────────────

    /// <summary>Build (or rebuild) the engine <see cref="MediaPlayer"/> for a resolved <see cref="PopOutVideoSource"/> and open
    /// it — the clear MF backend for a Canvas/clear URL, or the clear+DRM backend (native in-process PlayReady CDM) for a DRM
    /// descriptor. THIS HOST OWNS THE PLAYER (the M0 ownership inversion): the surfaces only present <see cref="CurrentPlayer"/>,
    /// so a placement flip re-binds a presenter instead of rebuilding a player — no restart from 0. The prior player (if any)
    /// is torn down first so two sessions never coexist, and a redundant load of the SAME <see cref="PopOutVideoSource.Key"/> is
    /// a no-op for the same reason. Playback advances only once a surface pumps the MF session (see the MF-pump caveat).
    /// <para>NON-BLOCKING AND SERIALIZED: the request is handed to the <see cref="VideoLoadPump{T}"/>, which tears the
    /// previous session down TO COMPLETION before building this one, and drops this request entirely if a newer load
    /// arrives while it is queued (latest-wins coalescing). That ordering is what keeps the process-global native
    /// PlayReady session from being stopped out from under its own successor on a video→video track skip.</para></summary>
    public void LoadVideo(PopOutVideoSource src, long startAtMs = 0)
    {
        if (_disposed || src is null) return;
        // The idempotence check lives in the pump (IsAlreadyLive), evaluated at DEQUEUE time — checking it here would
        // wrongly drop a load that follows a queued teardown, when the "already playing" key is about to be gone.
        _pump.Request(new VideoLoadRequest(src, startAtMs));
    }

    // The pump's liveness probe: is this exact source ALREADY the live player? The controller may re-enter the video path
    // for the track that is already playing (a placement flip, a re-published source, a kind re-evaluation) — rebuilding
    // would restart it from 0, so that request is dropped without a teardown or a rebuild.
    bool IsAlreadyLive(VideoLoadRequest req)
    {
        MediaPlayer? live;
        lock (_gate)
        {
            if (_player is null || !string.Equals(_sourceKey, req.Source.Key, StringComparison.Ordinal)) return false;
            live = _player;
        }
        // Same source, already playing: never rebuild — that is what would restart it from 0 on a placement flip or a
        // re-published source. But a request carrying an explicit start position is a deliberate reposition (a retry
        // checkpoint, a forced same-kind reload), so honor it on the LIVE session instead of dropping it silently.
        if (req.StartAtMs > 0)
        {
            _log.Info($"video-host load ignored (already playing key={req.Source.Key}) — seeking live session to {req.StartAtMs}ms");
            // A carried start position is a committed reposition, never a scrub.
            SeekPlayer(live, req.StartAtMs, SeekMode.Accurate);
        }
        else _log.Info($"video-host load ignored — already playing key={req.Source.Key}");
        return true;
    }

    // ── the pump's two steps: teardown (always first, always complete) then build+open ────────────────────────────────

    /// <summary>Step 1 — release the CURRENT session completely, bounded. Nothing may open a new native session until this
    /// has returned: the native PlayReady session is a process-global singleton whose Stop carries no session identity, so
    /// an un-awaited teardown is exactly what used to shut a freshly-started successor down.
    /// <para>UNBIND BEFORE DISPOSE: the mounted <c>MediaPlayerElement</c> keeps pumping whatever
    /// <see cref="PlayerChanged"/> last published. Clearing <see cref="_player"/> without notifying left the surface
    /// pumping a player mid-dispose on every video→video skip (track change while already on the video host) — MF then
    /// never published duration/NaturalSize on the successor, and the PiP sat on the Opening/Loading poster at 0:00.
    /// <see cref="Stop"/> already fires <c>PlayerChanged(null)</c>; teardown must do the same.</para></summary>
    async System.Threading.Tasks.Task TeardownAsync(long epoch)
    {
        StopTicker();
        // The window dies WITH the session it described. The ticker stops here, so nothing else would ever retract a
        // live window — and a stale one is not a cosmetic leak: it keeps CanSeek armed and the DVR rail on screen over
        // whatever plays next.
        RetractLiveWindow();
        MediaPlayer? old;
        lock (_gate)
        {
            old = _player;
            _player = null;
            _sourceKey = "";
            _lastState = PlaybackState.Idle;
            _errorReported = false;
            _reportedDurMs = 0;
            _reportedLive = default;
            _liveReported = false;
            _sizeLogged = false;
            _noSizeLogged = false;
            _progressed = false;
            _startAtMs = 0;
            _startSeekPending = false;
            _playReassertsLeft = 0;
            _watchdog.Disarm();
        }
        if (old is not null)
        {
            try { old.Stop(); } catch (Exception ex) { _log.Info($"video-host stop failed: {ex.Message}"); }
            // Drop the surface binding BEFORE native dispose — same contract as Stop(). Without this, video→video
            // LoadVideo pumps a dying session and the successor never receives a pump (no duration, stuck Loading).
            try { PlayerChanged?.Invoke(null); }
            catch (Exception ex) { _log.Info($"video-host PlayerChanged(null) failed: {ex.Message}"); }
            _log.Info("video-host teardown — unbound surface before dispose");
            await DisposeBoundedAsync(old).ConfigureAwait(false);
        }
        // Anything Stop() handed over (its observable state was cleared synchronously) is released here, in order.
        while (_toDispose.TryDequeue(out var queued)) await DisposeBoundedAsync(queued).ConfigureAwait(false);
    }

    /// <summary>Step 2 — build the player for <paramref name="src"/>, announce it, and AWAIT the open. Awaiting the open
    /// inside the pump is the second half of the fix: a later teardown can then never land on a half-opened session whose
    /// <c>IMediaSession</c> had not been assigned yet (which would leak the native session and wedge every later video on
    /// the singleton latch).</summary>
    MediaPlayer BuildProtectedPlayer(PopOutVideoSource src)
    {
        var descriptor = src.DrmDescriptor!;
        int policyCap = _settings is null ? int.MaxValue : Wavee.NetworkPolicy.EffectiveVideoMaxHeight(_settings);
        var abr = new AdaptiveBitrateController
        {
            MaxHeight = policyCap,
        };
        int preferredHeight = _settings?.Get(Wavee.WaveeSettings.VideoQuality) ?? 0;
        if (preferredHeight > 0 && descriptor.Catalog is { } catalog)
        {
            for (int t = 0; t < catalog.Tracks.Count; t++)
            {
                var track = catalog.Tracks[t];
                if (track.Kind != TrackKind.Video) continue;
                for (int r = 0; r < track.Representations.Count; r++)
                    if (track.Representations[r].Quality.Resolution.Height == preferredHeight)
                    {
                        abr.Selection = QualitySelection.Pin(track.Representations[r].Id);
                        break;
                    }
                if (!abr.Selection.IsAuto) break;
            }
        }
        return MediaPlayer.Build()
            .WithBackend(MediaKind.MfVideoOrFile,
                new MfMediaPlayer(new ProtectedMediaBackend(src.LicenseRelay!, descriptor)))
            .WithAbr(abr)
            .WithDrm(src.LicenseRelay!)
            .Build();
    }

    void ApplyQualityPolicy(MediaPlayer? player)
    {
        if (player is null || _settings is null) return;
        int cap = Wavee.NetworkPolicy.EffectiveVideoMaxHeight(_settings);
        if (cap != _lastAppliedAutoCap)
        {
            _lastAppliedAutoCap = cap;
            player.SetAdaptiveMaxHeight(cap == int.MaxValue ? 0 : cap);
        }

        QualitySelection selection = player.Qualities.Selected.Peek();
        if (!_hasObservedQuality)
        {
            _lastObservedQuality = selection;
            _hasObservedQuality = true;
            return;
        }
        if (selection == _lastObservedQuality) return;
        _lastObservedQuality = selection;
        int height = 0;
        if (!selection.IsAuto && selection.VariantId is { } id)
            for (int i = 0; i < player.Qualities.Variants.Count; i++)
                if (string.Equals(player.Qualities.Variants[i].Id, id, StringComparison.Ordinal))
                { height = player.Qualities.Variants[i].Resolution.Height; break; }
        _settings.Set(Wavee.WaveeSettings.VideoQuality, height);
    }

    async System.Threading.Tasks.Task BuildAndOpenAsync(VideoLoadRequest req, long epoch)
    {
        PopOutVideoSource src = req.Source;
        MediaPlayer built;
        try
        {
            built = src.FilePath is not null
                // A user-attached local file: the plain clear MF backend, exactly like a Canvas URL. No DRM plumbing is
                // built for it even if a DRM source was playing a moment ago — the player is rebuilt per source.
                ? MediaPlayer.Build()
                    .WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer())
                    .Build()
                : src.IsDrm
                // MfMediaPlayer routes a DrmConfig-carrying source to the injected DRM backend (native CDM); ProtectedMediaBackend
                // carries the parsed Spotify descriptor (init/segment/stride/PSSH) and the relay POSTs the license challenge.
                ? BuildProtectedPlayer(src)
                : MediaPlayer.Build()
                    .WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer())
                    .Build();
        }
        catch (Exception ex)
        {
            _log.Info($"video-host build failed key={src.Key}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, ex.Message));
            return;
        }

        built.SetVolume(_volume);
        built.SetMuted(_muted);

        // Superseded while we were building (a third skip) — never publish or open a session we already know is stale;
        // hand it to the disposal queue so the pump's next teardown releases it in order.
        if (_disposed || _pump.IsStale(epoch))
        {
            _log.Info($"video-host build superseded before publish key={src.Key}");
            _toDispose.Enqueue(built);
            return;
        }

        lock (_gate)
        {
            // The pump guarantees the previous player is already fully torn down here — there is no `old` to race.
            _player = built;
            _sourceKey = src.Key ?? "";
            _lastState = PlaybackState.Idle;
            _errorReported = false;
            _reportedDurMs = 0;
            _reportedLive = default;
            _liveReported = false;
            _sizeLogged = false;
            _noSizeLogged = false;
            _playIntent = true;
            _progressed = false;
            _startAtMs = Math.Max(0, req.StartAtMs);
            _startSeekPending = _startAtMs > 0;   // applied+clamped in Tick once the duration proves the session is seekable
            _playReassertsLeft = PlayReassertBudget;
            _watchdog.Arm(Environment.TickCount64);   // armed per load; disarmed by progress or by the next teardown
            _lastAppliedAutoCap = -1;
            _hasObservedQuality = false;
        }
        // Announce the new player so the mounted surface re-binds its MediaPlayerElement to THIS instance (the app marshals
        // this onto the UI thread; the event fires on the pump's worker thread).
        PlayerChanged?.Invoke(built);

        MediaSource source;
        try
        {
            source = src.FilePath is { } file
                ? MediaSource.FromFile(file)
                : src.IsDrm
                ? MediaSource.FromUri(src.DrmDescriptor!.InitUrl).With(new DrmConfig(DrmSystem.PlayReady, src.LicenseServerUri))
                // A LIVE broadcast is opened as one. Without this the backend infers live-ness from the container, and
                // Media Foundation simply cannot: it reports the sliding DVR window as a finite GetDuration, which the
                // session then latches and publishes as the track's length. SourceLiveness.Live tells the session up
                // front, so it never publishes a duration for this source at all and the bar reads the timeline
                // instead. Auto keeps every finite source on exactly today's inference.
                : MediaSource.FromUri(src.ClearUrl ?? "")
                    .WithLiveness(src.IsLive ? SourceLiveness.Live : SourceLiveness.Auto);
        }
        catch (Exception ex)
        {
            _log.Info($"video-host source build failed key={src.Key}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, ex.Message));
            return;
        }

        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
        StartTicker();   // BEFORE the open: the watchdog is already counting while the CDM/license handshake runs
        _log.Info($"video-host loaded key={src.Key} drm={src.IsDrm}{(src.FilePath is null ? "" : " local-file")}");
        // A DRM music video plays its OWN soundtrack: the manifest carries an AAC representation under the same content
        // key, and the native CENC source demuxes it alongside the video so Media Foundation renders both under one
        // clock. That is why the song's audio host is stopped while video is the current media — the plain audio track is
        // a DIFFERENT edit (no intro, no spoken pre/post-roll) and would drift against the picture.
        // Logged either way so a silent video is diagnosable: "audio=no" means the manifest offered no AAC representation
        // under the PlayReady index (the parser refuses Opus, which the protected pipeline cannot decode).
        // A clear/Canvas source is unaffected — the MF media engine renders its audio itself.
        if (src.IsDrm)
            _log.Info($"video-host: DRM video is now the current media; the song's audio host is stopped. " +
                $"own-soundtrack={(string.IsNullOrEmpty(src.DrmDescriptor?.AudioInitUrl) ? "NO (video-only manifest)" : "yes " + src.DrmDescriptor!.AudioCodecs)}");

        // The OPEN is awaited HERE, on the pump, so the next teardown is ordered after it. Bounded so a pathological open
        // can never stall every later skip; errors surface as a Fault (or via the Error poll in Tick), never as a throw.
        try
        {
            await built.OpenAsync(source).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Info($"video-host open did not complete within {OpenTimeoutMs}ms key={src.Key} — the start watchdog now owns this load");
            return;
        }
        catch (Exception ex)
        {
            _log.Info($"video-host open failed key={src.Key}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, ex.Message));
            return;
        }

        // Superseded during the open — leave it alone; the pump's next teardown disposes it in order.
        if (_disposed || _pump.IsStale(epoch)) return;
        if (built.Error.Peek() is not null) return;   // Tick's Error poll raises the Fault
        // NOTE: the carried position is deliberately NOT seeked here. `OpenAsync` returning does NOT mean the session can
        // accept a seek — on the protected (PlayReady/CENC) path the open completes in ~30ms while the native session is
        // still spinning up, and a seek issued in that window is silently DROPPED (observed: a carried 126034ms seek
        // followed by a put-state reporting pos=1145, i.e. it started at 0 regardless). Seeking a not-yet-running
        // protected session is also a plausible cause of the "video sits buffering until I press play" stall.
        // It is applied in Tick instead, at the first moment the engine reports a positive duration — which proves
        // metadata is loaded and the session is live and seekable.
        // Play is NOT awaited: the protected transport ack can take up to 5s and must not hold the pump (a skip would
        // queue behind it). Observed via the helper so a faulted ack can never surface as an unobserved task exception.
        _ = PlayQuietlyAsync(built);
    }

    async System.Threading.Tasks.Task PlayQuietlyAsync(MediaPlayer p)
    {
        try { await p.PlayAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Info($"video-host play failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── the poll tick: derive AudioHostSignals from the engine's reactive state (mirrors FluentMediaAudioHost.Tick) ────

    void StartTicker() { if (!_disposed) _ticker.Change(200, 200); }
    void StopTicker() => _ticker.Change(Timeout.Infinite, Timeout.Infinite);

    void Tick()
    {
        if (_disposed) return;
        var p = CurrentPlayer;
        if (p is null) return;
        ApplyQualityPolicy(p);

        long pos = PositionMs;

        // The source's REAL length, the moment the engine knows it. Polled here rather than awaited at open because the MF
        // session only resolves a duration once a mounted surface has pumped it (the MF-pump caveat above).
        // Re-relayed when it CHANGES, not once: MF's first publish lands at LOADEDMETADATA, which for an adaptive/DASH
        // manifest is commonly still 0, and a manifest can revise it afterwards. Value-gated on a ~250ms band so a
        // jittering estimate does not spam the projection.
        {
            TimelineInfo timeline;
            try { timeline = p.Timeline.Peek(); } catch { timeline = TimelineInfo.Empty; }
            bool isLive = timeline.IsLive;

            long durMs = 0;
            try { durMs = (long)p.Duration.Peek().TotalMilliseconds; } catch { }

            // DURATION IS SUPPRESSED WHILE LIVE, unconditionally. A broadcast has no length, and every number the
            // backend could offer here is a lie of a different kind: MF hands back the DVR window (the "-3:22 remaining"
            // on a stream that has been running for six hours), an HLS source hands back the segment list so far. The
            // projection's duration override is exactly the wrong home for that, because it also scales the seek bar.
            // The window travels on LiveWindowKnown instead, where it is labelled as what it is.
            if (!isLive && durMs > 0 && Math.Abs(durMs - _reportedDurMs) > DurationRelayEpsilonMs)
            {
                _reportedDurMs = durMs;
                string key;
                lock (_gate) key = _sourceKey;
                try { DurationKnown?.Invoke(key, durMs); }
                catch (Exception ex) { _log.Info($"video-host duration relay failed: {ex.Message}"); }
            }

            // THE LIVE TIMELINE. Relayed as one record on a material change (a flag flip, or any edge moving more than
            // LiveRelayEpsilonMs), and relayed ONCE more when live-ness ends so a stale window cannot outlive the
            // broadcast it described.
            {
                var window = new LiveWindow(
                    IsLive: isLive,
                    SeekableStartMs: (long)timeline.SeekableStart.TotalMilliseconds,
                    SeekableEndMs: (long)timeline.SeekableEnd.TotalMilliseconds,
                    LiveEdgeMs: (long)timeline.LiveEdge.TotalMilliseconds,
                    PositionMs: pos,   // the tick's own reading, taken above — one Peek per tick, not two
                    IsAtLiveEdge: timeline.IsAtLiveEdge);
                // The FIRST relay only happens for a source that is actually live — an ordinary video would otherwise
                // publish one "not live" record per load, which is noise the projection already assumes.
                bool relay = _liveReported ? LiveWindowChanged(_reportedLive, window) : window.IsLive;
                if (relay)
                {
                    bool firstLive = !_reportedLive.IsLive && window.IsLive;
                    _liveReported = true;
                    _reportedLive = window;
                    string key;
                    lock (_gate) key = _sourceKey;
                    if (firstLive && window.IsLive)
                        _log.Info($"video-host live timeline for key={key}: window {window.WindowMs}ms " +
                                  $"({window.SeekableStartMs}..{window.SeekableEndMs}), edge {window.LiveEdgeMs}ms, " +
                                  $"{(window.HasWindow ? "DVR rail" : "no rewind")}");
                    try { LiveWindowKnown?.Invoke(key, window); }
                    catch (Exception ex) { _log.Info($"video-host live relay failed: {ex.Message}"); }
                }
            }

            // APPLY the position carried in from the audio edit, at the first moment the session is provably seekable.
            // The PROOF is the engine's own readiness, not a positive duration: duration was only ever a proxy for
            // "metadata loaded and the native session is running", and it is a proxy that a LIVE source never satisfies
            // (there is no duration to publish), which would strand a carried position forever. Ready/Playing/Paused say
            // the same thing directly, and say it for every source. Seeking at the open instead silently did nothing
            // (see BuildAndOpenAsync). Runs at most once per load.
            // Clamped against the duration when there IS one, for free: a music video is a different — often shorter —
            // edit, so a carried position can sit at or past its end, which would land on a dead frame or fire Ended.
            if (_startSeekPending && IsSeekReady(p, durMs))
            {
                long start;
                lock (_gate) { start = _startAtMs; _startSeekPending = false; _startAtMs = 0; }
                if (durMs > 0 && start > durMs - StartClampGuardMs)
                {
                    long clamped = Math.Max(0, durMs - StartClampBackoffMs);
                    _log.Info($"video-host carried position {start}ms exceeds this edit ({durMs}ms) — clamping to {clamped}ms");
                    start = clamped;
                }
                if (start > 0)
                {
                    _log.Info($"video-host applying carried position {start}ms (duration {durMs}ms, session now seekable)");
                    SeekPlayer(p, start, SeekMode.Accurate);
                }
            }
        }

        // Video geometry, once per load: which it is decides whether the surface can ever show a picture.
        if (!_sizeLogged)
        {
            SizeI natural = default;
            try { natural = p.NaturalSize.Peek(); } catch { }
            if (natural.Width > 0 && natural.Height > 0)
            {
                _sizeLogged = true;
                _log.Info($"video-host natural size {natural.Width}x{natural.Height} for key={CurrentSourceKey}");
            }
            else if (!_noSizeLogged && (_reportedDurMs > 0 || _reportedLive.IsLive))
            {
                // Metadata is loaded (a duration exists) but the decoder still reports no picture size — the exact
                // signature of a surface that will sit under the opening spinner. Still recoverable (the session re-asks
                // the engine), so this is a note, not a fault.
                _noSizeLogged = true;
                _log.Info($"video-host has NO natural size yet although duration is known ({_reportedDurMs}ms) " +
                          $"key={CurrentSourceKey} — the surface stays on the opening spinner until the decoder reports one");
            }
        }

        // ALWAYS-ON video PLACEMENT geometry (no env switch). natural = what the decoder reports; content = the size the
        // backend renders the frame at inside its own swap chain (its ASPECT must match natural's, or the backend
        // letterboxes inside its own destination); place = the rect the compositor visual was put at. place/content per
        // axis IS the compositor's stretch: equal ratios == uniform, unequal == a deliberate Fill.
        {
            VideoSurfaceGeometry geo = default;
            try { geo = p.SurfaceGeometry.Peek(); } catch { }
            if (geo.IsPlaced && geo != _loggedGeometry)
            {
                _loggedGeometry = geo;
                float ca = geo.Content.Height > 0 ? (float)geo.Content.Width / geo.Content.Height : 0f;
                float pa = geo.Place.H > 0f ? geo.Place.W / geo.Place.H : 0f;
                _log.Info($"video geometry {geo} contentAspect={ca:0.###} placeAspect={pa:0.###} " +
                          $"{(MathF.Abs(ca - pa) <= 0.01f ? "uniform" : "stretched")} key={CurrentSourceKey}");
            }
        }

        if (!_errorReported && p.Error.Peek() is { } err)
        {
            _errorReported = true;
            _signals.OnNext(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None, err.Message));
            return;
        }

        var state = p.State.Peek();

        // ── the per-load START WATCHDOG (piggybacks this ticker — no extra timer, no allocation) ──────────────────────
        // A load that reported "loaded" but never reaches a playing/advancing state must not sit silent forever. The
        // wedge that motivated this publishes NO state the switch below reacts to (PlaybackState.Idle) and NO error, so
        // without this the host would never speak again and the transport would stay paused at 0:00.
        bool progressed = _progressed || _errorReported
            || state is PlaybackState.Playing or PlaybackState.Ended or PlaybackState.Failed
            || pos > 0;
        bool enginePlayRequested;
        try { enginePlayRequested = p.IsPlayRequested.Peek(); } catch { enginePlayRequested = true; }
        bool fault;
        lock (_gate)
        {
            _progressed |= progressed;
            // Ages on THIS HOST's intent only. It used to require the engine's IsPlayRequested as well, which made the
            // exact failure it exists to catch invisible: when the fire-and-forget PlayAsync never takes, the engine's
            // play-request stays FALSE, so the budget never aged, no fault was ever raised, and the transport sat
            // paused at 0:00 forever with nothing to recover from ("it just buffers until I click play"). A deliberate
            // pause is still never a fault — Pause() clears _playIntent, and the watchdog re-bases while it is false.
            fault = _watchdog.ShouldFault(Environment.TickCount64, _playIntent, progressed);
            if (fault) _errorReported = true;
        }
        if (fault)
        {
            _log.Info($"video-host start watchdog fired after {_watchdog.TimeoutMs}ms — state={state} pos={pos} " +
                      $"key={CurrentSourceKey}; the session never started. Raising a Fault so the controller can recover.");
            _lastState = state;
            _signals.OnNext(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None,
                "the video session never started playing (no progress within the start budget)"));
            return;
        }

        switch (state)
        {
            case PlaybackState.Playing:
                _signals.OnNext(_lastState == PlaybackState.Playing
                    ? new AudioHostSignal(AudioHostSignalKind.PositionTick, pos)
                    : new AudioHostSignal(AudioHostSignalKind.Playing, pos));
                break;
            case PlaybackState.Paused:
            case PlaybackState.Ready:
                // Ready/Paused WHILE we want to play is a session that has not started yet, not a paused one. Reporting
                // Paused here is what surfaced the stall as a transport that looked deliberately paused at 0:00 — the
                // engine settles on Ready right after the open, ~200ms before the fire-and-forget PlayAsync lands, and
                // if that command is lost it never leaves Ready. Re-assert play a bounded number of times (the protected
                // transport ack can genuinely take seconds, so this is a nudge, not a spin) and report Buffering, which
                // is what is actually happening. Only a state with NO play intent is published as Paused.
                if (_playIntent && !enginePlayRequested)
                {
                    bool nudge = false;
                    lock (_gate) { if (_playReassertsLeft > 0) { _playReassertsLeft--; nudge = true; } }
                    if (nudge)
                    {
                        _log.Info($"video-host re-asserting play — session is {state} with play intent unset " +
                                  $"(key={CurrentSourceKey}); the open's play command did not take");
                        _ = PlayQuietlyAsync(p);
                    }
                    if (_lastState != state) _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, pos));
                }
                else if (_lastState is not (PlaybackState.Paused or PlaybackState.Ready))
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Paused, pos));
                break;
            case PlaybackState.Opening:
            case PlaybackState.Buffering:
            case PlaybackState.Stalled:
                if (_lastState != state) _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, pos));
                break;
            case PlaybackState.Ended:
                if (_lastState != PlaybackState.Ended)
                {
                    StopTicker();
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
                }
                break;
            case PlaybackState.Failed:
                if (!_errorReported)
                {
                    _errorReported = true;
                    _signals.OnNext(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None, "video playback failed"));
                }
                break;
        }
        _lastState = state;
    }

    /// <summary>Release one player, BOUNDED. The protected player's own Dispose already joins its native thread for 3s;
    /// this outer budget is larger so a healthy teardown always finishes inside it, while a wedged native session cannot
    /// hold the pump — and the next load — forever. Bounded joins only: the track-end deadlock discipline.</summary>
    async System.Threading.Tasks.Task DisposeBoundedAsync(MediaPlayer player)
    {
        try
        {
            await player.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(TeardownTimeoutMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Info($"video-host dispose did not complete within {TeardownTimeoutMs}ms — proceeding " +
                      "(a still-releasing native session self-heals through the backend's BUSY retry)");
        }
        catch (Exception ex) { _log.Info($"video-host dispose failed: {ex.Message}"); }
    }

    public async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopTicker();
        // Drain the pump first (bounded) so no build is in flight while we tear the last player down — the same ordering
        // guarantee the pump gives every load.
        _pump.RequestClear();
        try { await _pump.WhenIdleAsync().WaitAsync(TimeSpan.FromMilliseconds(TeardownTimeoutMs * 2)).ConfigureAwait(false); }
        catch (Exception ex) { _log.Info($"video-host pump drain: {ex.GetType().Name}"); }
        try { await _ticker.DisposeAsync().ConfigureAwait(false); } catch { }
        MediaPlayer? old;
        lock (_gate) { old = _player; _player = null; _sourceKey = ""; _watchdog.Disarm(); }
        if (old is not null) { try { await old.DisposeAsync().ConfigureAwait(false); } catch { } }
        while (_toDispose.TryDequeue(out var queued)) { try { await queued.DisposeAsync().ConfigureAwait(false); } catch { } }
    }
}

/// <summary>The output-device control for the WHOLE local media stack, not just its audio half.
/// <para>Mute reaches the app through <see cref="IAudioOutputDeviceControl"/> (the player bar and the device picker both go
/// through <c>LocalAudioDeviceService.SetMuted</c>), and only the AUDIO host implements that interface — so a mute set while
/// a music video was the current media, or set before one started, was silently dropped and the video played at full volume.
/// This composite hands every other member to the audio host verbatim and additionally fans <see cref="SetOutputMuted"/> out
/// to the video host, which stores the intent and re-applies it to every player it builds.</para>
/// <para>Both dependencies are REQUIRED: the composition root constructs this only when a real audio host exists, so there is
/// no nullable-with-silent-default half here.</para></summary>
public sealed class LocalMediaOutputControl : IAudioOutputDeviceControl
{
    readonly IAudioOutputDeviceControl _audio;
    readonly FluentVideoMediaHost _video;

    public LocalMediaOutputControl(IAudioOutputDeviceControl audio, FluentVideoMediaHost video)
    {
        _audio = audio;
        _video = video;
    }

    public event Action<OutputDeviceNotice>? OutputDeviceNotice
    {
        add => _audio.OutputDeviceNotice += value;
        remove => _audio.OutputDeviceNotice -= value;
    }

    public event Action<double, bool>? ExternalVolumeChanged
    {
        add => _audio.ExternalVolumeChanged += value;
        remove => _audio.ExternalVolumeChanged -= value;
    }

    /// <summary>Endpoint selection is an audio-stack concern: the video session renders through Media Foundation's own
    /// default-endpoint routing, so there is nothing to fan out here.</summary>
    public void SetOutputDevice(string? deviceId) => _audio.SetOutputDevice(deviceId);

    public void SetOutputMuted(bool muted)
    {
        _audio.SetOutputMuted(muted);
        _video.SetMuted(muted);
    }
}
