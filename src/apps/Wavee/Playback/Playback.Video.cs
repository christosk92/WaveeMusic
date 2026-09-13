// ── Playback/Playback.Video.cs ─────────────────────────────────────────────────────────────────────────────────────
// the decode/host only (A13). Not one pixel of UI — the four video surfaces are owner K's Shell/Video.*
//
// Role: SHELL
// Owner: H
// Wave: 3
// Budget: 1100 lines
// Spec: plan
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. The decode half of video: resolve a playable into a `VideoSource`, open ONE long-lived
// `MediaPlayer` on it, keep it alive across a source switch, report position / duration / liveness / faults back as
// posted `Input`s, and publish the (player, generation) binding the UI mounts. A13 (2026-09-12): every video SURFACE
// — the docked cap, the in-window PiP, the pop-out window, the app's own fullscreen presentation, the placement
// ladder, the override manager — is owner K's `Shell/Video.{cs,UI.cs,Host.cs}` in Wave 4. There is not one `Element`
// in this file and there must never be one.
//
// THE SEAM, WHOLE, IN THREE LINES (ch 24 §7 + the 0.2.9 decode/UI contract):
//   1. `Video.Player` is a `Signal<Binding>` of (player, generation). The UI keys its `MediaPlayerElement` on the
//      GENERATION, never on the source, so a video→video skip keeps the element mounted and the engine cross-fades
//      the last frame to the new source's first frame. The instance changes only on a first load, a rebuild, or a
//      teardown.
//   2. SOMETHING MUST PUMP. An MF session only advances — and only publishes duration and `NaturalSize` — while a
//      mounted element calls `IMediaPlayer.PumpVideo`. This host builds and reports; it never pumps. Exactly one
//      mounted surface may pump a given player (`OneSurfacePerPlayerGuard`).
//   3. Nothing else. No HWND, no swapchain, no monitor, no placement state. `IDetachedVideoWindow` is the engine's
//      hook and owner K's `Shell/Video.Host.cs` is its only caller (§7 below is the one-line seam that lets the
//      Wave-3 build compile and the Wave-4 owner attach).
//
// WHY THE PUMP IS SERIALIZED AND EPOCHED (0.2.9's `VideoLoadPump`, kept). The native PlayReady/CENC session is a
// PROCESS-GLOBAL singleton with a session-less ABI — `FgPlayReadyRunEx` / `FgPlayReadyStop` / `FgPlayReadyPlay` take
// no session handle — so a predecessor's Stop lands on whatever session holds the latch and silently shuts down a
// freshly-started successor. The observed failure: `RunEx` returned success, the snapshot settled on native state 4
// (`Stopped` → `PlaybackState.Idle`), a state the tick switch has no case for, and the managed side wedged at
// Opening while the native log showed the video licensed and playing. One worker, one coalescing slot, one epoch.
//
// Rules: threads and async are allowed (SHELL), but nothing here writes a table, and every result goes back as a
// posted `Input` carrying the epoch it was started for (C1/C4). No unbounded queue: the pending slot holds exactly
// one request and the latest wins (C8).

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Media.Windows;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Media.PlayReady;
using TrackKind = FluentGpu.Media.TrackKind;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The video decode host. One player, one pump, one watchdog.</summary>
    public static partial class Video
    {
        // ── 0. the values the seam carries ──────────────────────────────────────────────────────────────────────────

        /// <summary>A resolved, playable video: either a clear URL, a local file, or a PlayReady DASH descriptor plus
        /// the licence relay that answers its challenges. NOT a column and never entity state (ch 24 DATA GAP 1): it
        /// carries a live delegate and a parsed descriptor, so it is session state on this host. The per-PLAYABLE
        /// facts worth caching — <see cref="NaturalWidth"/>, <see cref="NaturalHeight"/>, <see cref="IsLive"/> — are
        /// mirrored into `TrackTable` by owner A so a re-watch sizes the card correctly BEFORE the round trip.</summary>
        /// <param name="Key">The content identity: the manifest id, the clear url, or `local:video:&lt;path&gt;`. The
        /// switch policy compares THIS, never the track slot — a relinked counterpart is the same video.</param>
        public sealed record VideoSource(
            string Key,
            string? ClearUrl = null,
            string? FilePath = null,
            DashSourceDescriptor? DrmDescriptor = null,
            Func<LicenseRequest, ValueTask<LicenseResponse>>? LicenseRelay = null,
            string? LicenseServerUri = null,
            int NaturalWidth = 0,
            int NaturalHeight = 0,
            bool IsLive = false)
        {
            public bool IsDrm => DrmDescriptor is not null && LicenseRelay is not null;

            public static VideoSource Clear(string url, bool isLive = false)
                => new(url, ClearUrl: url, IsLive: isLive);

            public static VideoSource LocalFile(string path)
                => new("local:video:" + path, FilePath: path);

            public static VideoSource PlayReady(string manifestId, DashSourceDescriptor descriptor,
                Func<LicenseRequest, ValueTask<LicenseResponse>> relay, string? licenseServerUri)
                => new(manifestId, DrmDescriptor: descriptor, LicenseRelay: relay, LicenseServerUri: licenseServerUri);
        }

        /// <summary>What the UI mounts. <see cref="Generation"/> is the element `Key`: it bumps ONLY when the player
        /// instance changes, so a source switch never remounts and never tears down the MF pump.</summary>
        public readonly record struct Binding(MediaPlayer? Player, long Generation);

        /// <summary>The player binding the four surfaces bind to. Written on the UI thread only (C1).</summary>
        public static readonly Signal<Binding> Player = new(default);

        /// <summary>The resolved source. A null source NEVER unmounts the stage (`ShouldMountPlayerStage` reads the
        /// PLAYER, not this) — it is the poster/loading discriminator and the aspect seed.</summary>
        public static readonly Signal<VideoSource?> Source = new(null);

        // ── 1. the pure decisions (unit-testable, engine-free) ──────────────────────────────────────────────────────

        /// <summary>What a load request means for the session that already exists.</summary>
        public enum SwitchAction : byte
        {
            /// <summary>The same key is already live and healthy: log it and do nothing.</summary>
            None,
            /// <summary>Same key, different position: one seek, no open.</summary>
            SeekOnly,
            /// <summary>A different key on a healthy player: re-open IN PLACE. No `PlayerChanged`, no unmount.</summary>
            Switch,
            /// <summary>No player, or the live one faulted: tear down and build.</summary>
            Rebuild,
        }

        /// <summary>The three facts the switch decision needs. A record struct so a test is one line.</summary>
        public readonly record struct SwitchInput(bool HasPlayer, bool Faulted, string LiveKey, string RequestKey, long StartAtMs);

        /// <summary>Ported from 0.2.9's `VideoSwitchPolicy`. The ordering is the whole content: a faulted player can
        /// never be switched in place (the native singleton would refuse), and "same key, same position" must not
        /// re-open — a docked→fullscreen placement move re-asks for the video that is already playing.</summary>
        public static SwitchAction Plan(in SwitchInput i)
        {
            if (!i.HasPlayer || i.Faulted) return SwitchAction.Rebuild;
            if (!string.Equals(i.LiveKey, i.RequestKey, StringComparison.Ordinal)) return SwitchAction.Switch;
            return i.StartAtMs > 0 ? SwitchAction.SeekOnly : SwitchAction.None;
        }

        /// <summary>The net under both engine watchdogs. It catches the one failure they cannot see: a session that
        /// settled on <c>PlaybackState.Idle</c> — the engine's session watchdog needs a published Opening/Buffering
        /// and the native one needs state ≤ 1. Allocation-free POD, evaluated on the host's existing 200 ms ticker.</summary>
        public struct StartWatchdog
        {
            /// <summary>Deliberately ABOVE the engine's own budgets (30 s licence + 45 s CANPLAY + 12 s surface), so
            /// the engine's richer typed DRM error wins whenever it can diagnose the failure itself.</summary>
            public const int DefaultTimeoutMs = 25_000;

            int _timeoutMs;
            long _armedAtMs;
            bool _armed, _fired;

            public StartWatchdog(int timeoutMs = DefaultTimeoutMs) { _timeoutMs = timeoutMs; }

            public readonly int TimeoutMs => _timeoutMs > 0 ? _timeoutMs : DefaultTimeoutMs;

            public readonly bool IsArmed => _armed && !_fired;

            public void Arm(long nowMs) { _armedAtMs = nowMs; _armed = true; _fired = false; }

            public void Disarm() { _armed = false; _fired = false; }

            /// <summary>True exactly once, and only once, per load. Real progress disarms it; a DELIBERATE pause
            /// re-bases the budget instead of ageing it — the failure this exists to catch is a lost fire-and-forget
            /// `PlayAsync`, which leaves the engine's own play-request FALSE, so ageing on the engine's flag made the
            /// budget never expire.</summary>
            public bool ShouldFault(long nowMs, bool playIntent, bool progressed)
            {
                if (!_armed || _fired) return false;
                if (progressed) { Disarm(); return false; }
                if (!playIntent) { _armedAtMs = nowMs; return false; }
                if (nowMs - _armedAtMs <= TimeoutMs) return false;
                _fired = true;
                return true;
            }
        }

        // ── 2. host state ───────────────────────────────────────────────────────────────────────────────────────────

        const int TickMs = 200;                  // the ONE named video timer (P10); position, liveness, the watchdog
        const int OpenTimeoutMs = 15_000;
        const int TeardownTimeoutMs = 5_000;
        const long DurationRelayEpsilonMs = 250; // MF revises a DASH duration after LOADEDMETADATA; relay the moves
        const long LiveRelayEpsilonMs = 250;
        const long StartClampGuardMs = 250;      // a carried position within this of the end is clamped back…
        const long StartClampBackoffMs = 2_000;  // …to here, so a restore never lands on the credits
        const int PlayReassertBudget = 8;        // 8 × 200 ms ≈ 1.6 s of "Ready but not play-requested"
        const long GoLiveToleranceMs = 2_000;    // a committed seek this close to the edge is a GoLive, not a seek

        static readonly object s_gate = new();
        static readonly ConcurrentQueue<MediaPlayer> s_toDispose = new();

        static MediaPlayer? s_player;
        static long s_generation;
        static AdaptiveBitrateController? s_abr;
        static Func<LicenseRequest, ValueTask<LicenseResponse>>? s_activeRelay;
        static Timer? s_ticker;
        static bool s_disposed;

        // per-load state (all under s_gate)
        static string s_key = "";
        static uint s_epoch;                     // the reducer's LoadEpoch this load belongs to
        static long s_startAtMs;
        static bool s_startSeekPending, s_playIntent, s_progressed, s_errorReported, s_firstFrameFired;
        static long s_reportedDurMs;
        static LiveWindow s_reportedLive;
        static bool s_liveReported;
        static int s_playReassertsLeft;
        static StartWatchdog s_watchdog = new();
        static PlaybackState s_lastState = PlaybackState.Idle;
        static double s_volume = 1.0;
        static bool s_muted;

        // the pump
        static (VideoSource Source, long StartAtMs, uint Epoch)? s_pending;
        static bool s_pendingClear, s_running;
        static long s_pumpEpoch;
        static Task s_worker = Task.CompletedTask;

        // ── 3. the host API the reducer's Execute calls ─────────────────────────────────────────────────────────────

        static bool s_warmed;

        /// <summary>Warm the native PlayReady CDM. Called on FIRST USE, not at `Playback.Boot`: loading the CDM costs
        /// real milliseconds and a session that never watches a video must not pay them. Idempotent, and a missing
        /// DLL degrades to a typed DRM error rather than a `DllNotFoundException` in the frame loop.</summary>
        public static void Boot()
        {
            if (s_warmed) return;
            s_warmed = true;
            try { ProtectedMediaBackend.WarmupNative(); }
            catch (Exception ex) { Log.Warn("video", "playready warmup failed", ex); }
        }

        /// <summary>Play <paramref name="source"/> from <paramref name="fromMs"/>, for reducer epoch
        /// <paramref name="epoch"/>. Returns immediately: the physical work is one coalesced request on the pump, and
        /// a request that arrives while another is in flight REPLACES it (latest wins).</summary>
        public static void Load(VideoSource source, uint epoch, int fromMs = 0)
        {
            Boot();
            lock (s_gate)
            {
                s_pumpEpoch++;
                s_pending = (source, Math.Max(0, fromMs), epoch);
                s_pendingClear = false;
                EnsureWorker();
            }
        }

        /// <summary>Stop and release. A clear INVALIDATES and overtakes a queued load — the native singleton cannot be
        /// stopped and started concurrently.</summary>
        public static void Stop()
        {
            lock (s_gate)
            {
                s_pumpEpoch++;
                s_pending = null;
                s_pendingClear = true;
                EnsureWorker();
            }
        }

        public static void Play()
        {
            MediaPlayer? p;
            lock (s_gate) { s_playIntent = true; p = s_player; }
            if (p is null) return;
            try { _ = p.PlayAsync(); } catch (Exception ex) { Log.Warn("video", "play failed", ex); }
            StartTicker();
        }

        public static void Pause()
        {
            MediaPlayer? p;
            // The intent flag drops FIRST, so a deliberate pause never ages toward a watchdog fault.
            lock (s_gate) { s_playIntent = false; p = s_player; }
            if (p is null) return;
            try { _ = p.PauseAsync(); } catch (Exception ex) { Log.Warn("video", "pause failed", ex); }
            StopTicker();
        }

        /// <summary>Seek, in the engine's two modes. A COMMITTED (accurate) seek at or past the live edge becomes
        /// `GoLiveAsync` instead — a DVR window's right end is not a position, and seeking to it lands behind the edge
        /// and stays there. A scrub PREVIEW (keyframe) is excluded from that rule on purpose.</summary>
        public static void Seek(long ms, bool accurate = true)
        {
            MediaPlayer? p;
            lock (s_gate) { p = s_player; }
            if (p is null) return;
            try
            {
                if (accurate && IsAtOrPastLiveEdge(p, ms)) { _ = p.GoLiveAsync(); return; }
                _ = p.SeekAsync(TimeSpan.FromMilliseconds(Math.Max(0, ms)), accurate ? SeekMode.Accurate : SeekMode.Keyframe);
            }
            catch (Exception ex) { Log.Warn("video", "seek failed", ex); }
        }

        public static void SetVolume(float volume)
        {
            MediaPlayer? p;
            lock (s_gate) { s_volume = Math.Clamp(volume, 0f, 1f); p = s_player; }
            try { p?.SetVolume(s_volume); } catch { /* fail-soft */ }
        }

        public static void SetMuted(bool muted)
        {
            MediaPlayer? p;
            lock (s_gate) { s_muted = muted; p = s_player; }
            try { p?.SetMuted(muted); } catch { /* fail-soft */ }
        }

        /// <summary>Pin a video height, or 0 for auto. Persisted by owner R's Settings tab; applied to the ABR
        /// controller AND to the engine's own quality selection so a manual pin survives a switch.</summary>
        public static void SetPreferredHeight(int height)
        {
            MediaPlayer? p;
            lock (s_gate) { p = s_player; }
            if (s_abr is { } abr) abr.MaxHeight = height > 0 ? height : int.MaxValue;
            if (p is null) return;
            try
            {
                if (height <= 0) { _ = p.SelectQualityAsync(QualitySelection.Auto); return; }
                foreach (QualityVariant v in p.Qualities.Variants)
                {
                    if (v.Resolution.Height != height) continue;
                    _ = p.SelectQualityAsync(QualitySelection.Pin(v.Id));
                    return;
                }
            }
            catch (Exception ex) { Log.Warn("video", "quality pin failed", ex); }
        }

        /// <summary>Shut down for good. Drains the pump so the native singleton is not stopped mid-open.</summary>
        public static void Shutdown()
        {
            s_disposed = true;
            Stop();
            try { s_worker.Wait(TeardownTimeoutMs * 2); } catch { }
            StopTicker();
            try { s_ticker?.Dispose(); } catch { }
            s_ticker = null;
            DrainDisposals();
        }

        // ── 4. the pump ─────────────────────────────────────────────────────────────────────────────────────────────

        static void EnsureWorker()
        {
            if (s_running) return;
            s_running = true;
            s_worker = Task.Run(RunAsync);
        }

        static async Task RunAsync()
        {
            while (true)
            {
                (VideoSource Source, long StartAtMs, uint Epoch)? next;
                bool clear;
                long epoch;
                lock (s_gate)
                {
                    next = s_pending;
                    clear = s_pendingClear;
                    epoch = s_pumpEpoch;
                    s_pending = null;
                    s_pendingClear = false;
                    if (next is null && !clear) { s_running = false; return; }
                }

                // Both arms are wrapped: a throwing load can never kill the worker, and it must never be silent
                // either — the reducer gets a typed fault for the epoch it asked about.
                try
                {
                    if (clear) await TeardownAsync(epoch).ConfigureAwait(false);
                    else await ApplyAsync(next!.Value, epoch).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn("video", "load pump op failed", ex);
                    if (next is { } req) ReportFault(req.Epoch, Fault.Unknown, ex.Message);
                }
            }
        }

        static bool IsStale(long epoch) { lock (s_gate) return s_pumpEpoch != epoch; }

        static async Task ApplyAsync((VideoSource Source, long StartAtMs, uint Epoch) req, long epoch)
        {
            MediaPlayer? live;
            string liveKey;
            bool faulted;
            lock (s_gate) { live = s_player; liveKey = s_key; faulted = s_errorReported; }

            SwitchAction plan = Plan(new SwitchInput(live is not null, faulted, liveKey, req.Source.Key, req.StartAtMs));
            Log.Info("video", $"load plan={plan} key={Tail(req.Source.Key)} from={req.StartAtMs} epoch={req.Epoch}");

            switch (plan)
            {
                case SwitchAction.None:
                    return;
                case SwitchAction.SeekOnly:
                    Seek(req.StartAtMs);
                    return;
                case SwitchAction.Switch:
                    await SwitchInPlaceAsync(req, epoch, live!).ConfigureAwait(false);
                    return;
                default:
                    await TeardownAsync(epoch).ConfigureAwait(false);
                    if (IsStale(epoch)) return;
                    await BuildAndOpenAsync(req, epoch).ConfigureAwait(false);
                    return;
            }
        }

        static async Task BuildAndOpenAsync((VideoSource Source, long StartAtMs, uint Epoch) req, long epoch)
        {
            MediaPlayer built = BuildPlayer();
            try { built.SetVolume(s_volume); built.SetMuted(s_muted); } catch { }

            if (IsStale(epoch)) { s_toDispose.Enqueue(built); DrainDisposals(); return; }

            long generation;
            lock (s_gate)
            {
                s_player = built;
                generation = ++s_generation;
            }
            ResetPerLoad(req);
            PublishBinding(built, generation);
            ToUi(() => Source.Value = req.Source);
            SetPreferredHeight(Platform.Settings.Get(Platform.Keys.VideoQuality));

            MediaSource source = BuildMediaSource(req.Source);
            PostSignal(req.Epoch, AudioSignal.Buffering);
            StartTicker();                         // BEFORE the open: a DRM open that never completes must still tick
            try
            {
                await built.OpenAsync(source).AsTask().WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMs)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Hand over to the watchdog rather than faulting here: a slow PlayReady licence round trip is normal
                // and the open frequently returns long after the native session is actually playing.
                Log.Warn("video", "open timed out — the start watchdog owns it now");
                return;
            }
            if (IsStale(epoch)) return;
            if (built.Error.Peek() is { } err) { ReportFault(req.Epoch, MapError(err), err.Message); return; }

            // Fire-and-forget: PlayAsync on the protected path can take seconds, and awaiting it here would hold the
            // pump against the next skip.
            _ = PlayQuietlyAsync(built);
        }

        /// <summary>A different video on a HEALTHY player: re-open the same instance. No `PlayerChanged`, so the
        /// mounted element is never remounted and the engine cross-fades the last frame to the new first frame. On a
        /// timeout the watchdog takes it; on any other failure this degrades ONCE to a full rebuild.</summary>
        static async Task SwitchInPlaceAsync((VideoSource Source, long StartAtMs, uint Epoch) req, long epoch, MediaPlayer live)
        {
            RetractLiveWindow(req.Epoch);          // must precede ResetPerLoad: it reads the OLD key
            ResetPerLoad(req);
            ToUi(() => Source.Value = req.Source);
            SetPreferredHeight(Platform.Settings.Get(Platform.Keys.VideoQuality));
            MediaSource source = BuildMediaSource(req.Source);
            PostSignal(req.Epoch, AudioSignal.Buffering);
            StartTicker();
            long startedAt = FrameNowMs();
            try
            {
                await live.OpenAsync(source).AsTask().WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMs)).ConfigureAwait(false);
                Log.Info("video", $"switch ok key={Tail(req.Source.Key)} switchMs={FrameNowMs() - startedAt}");
                if (!IsStale(epoch)) _ = PlayQuietlyAsync(live);
            }
            catch (TimeoutException)
            {
                Log.Warn("video", "switch timed out — the start watchdog owns it now");
            }
            catch (Exception ex)
            {
                Log.Warn("video", "switch failed — degrading to a rebuild", ex);
                await TeardownAsync(epoch).ConfigureAwait(false);
                if (!IsStale(epoch)) await BuildAndOpenAsync(req, epoch).ConfigureAwait(false);
            }
        }

        static async Task PlayQuietlyAsync(MediaPlayer p)
        {
            try { await p.PlayAsync().ConfigureAwait(false); } catch (Exception ex) { Log.Warn("video", "initial play failed", ex); }
        }

        /// <summary>Tear the session down. `PublishBinding(null)` runs BEFORE the dispose: the pumping surface must
        /// unbind first, or the successor is never pumped and the next video sits at Opening forever.</summary>
        static async Task TeardownAsync(long epoch)
        {
            MediaPlayer? old;
            lock (s_gate)
            {
                old = s_player;
                s_player = null;
                s_key = "";
                s_startSeekPending = false;
                s_playIntent = false;
                s_progressed = false;
                s_errorReported = false;
                s_firstFrameFired = false;
                s_reportedDurMs = 0;
                s_lastState = PlaybackState.Idle;
                s_watchdog.Disarm();
            }
            StopTicker();
            RetractLiveWindow(s_epoch);
            ToUi(() => Source.Value = null);
            if (old is null) return;

            try { old.Stop(); } catch { }
            PublishBinding(null, ++s_generation);
            await DisposeBoundedAsync(old).ConfigureAwait(false);
            DrainDisposals();
            _ = epoch;
        }

        static async Task DisposeBoundedAsync(MediaPlayer p)
        {
            try { await p.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(TeardownTimeoutMs)).ConfigureAwait(false); }
            catch (TimeoutException) { Log.Warn("video", "player dispose exceeded its budget — leaking one session"); }
            catch (Exception ex) { Log.Warn("video", "player dispose failed", ex); }
        }

        static void DrainDisposals()
        {
            while (s_toDispose.TryDequeue(out MediaPlayer? p)) _ = DisposeBoundedAsync(p);
        }

        static void ResetPerLoad((VideoSource Source, long StartAtMs, uint Epoch) req)
        {
            lock (s_gate)
            {
                s_key = req.Source.Key;
                s_epoch = req.Epoch;
                s_startAtMs = req.StartAtMs;
                s_startSeekPending = req.StartAtMs > 0;
                s_playIntent = true;
                s_progressed = false;
                s_errorReported = false;
                s_firstFrameFired = false;
                s_reportedDurMs = 0;
                s_reportedLive = default;
                s_liveReported = false;
                s_lastState = PlaybackState.Idle;
                s_playReassertsLeft = PlayReassertBudget;
                s_activeRelay = req.Source.LicenseRelay;
                s_watchdog.Arm(FrameNowMs());
            }
        }

        // ── 5. building the player and the source ───────────────────────────────────────────────────────────────────

        /// <summary>ONE long-lived player for clear, local and every DRM source. The relay and the descriptor are
        /// PER-LOAD (read through <see cref="RelayForward"/> and carried on `DrmConfig.SourceDescriptor`), never baked
        /// into the backend — a backend built around one track's relay cannot serve the next one.</summary>
        static MediaPlayer BuildPlayer()
        {
            // The pin is the user's own ceiling; the METERED cap is owner S's `NetworkPolicy` fold in Wave 6 and
            // lands on the same property through `SetPreferredHeight`.
            int pinned = Platform.Settings.Get(Platform.Keys.VideoQuality);
            s_abr = new AdaptiveBitrateController { MaxHeight = pinned > 0 ? pinned : int.MaxValue };
            return MediaPlayer.Build()
                .WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer(new ProtectedMediaBackend(defaultRelay: null, descriptor: null)))
                .WithAbr(s_abr)
                .WithDrm(RelayForward)
                .Build();
        }

        static ValueTask<LicenseResponse> RelayForward(LicenseRequest request)
        {
            Func<LicenseRequest, ValueTask<LicenseResponse>>? relay;
            lock (s_gate) relay = s_activeRelay;
            if (relay is null)
                throw new InvalidOperationException("video: a DRM challenge arrived with no active licence relay for the current load");
            return relay(request);
        }

        static MediaSource BuildMediaSource(VideoSource src)
            => src.FilePath is { } file ? MediaSource.FromFile(file)
             : src.IsDrm ? MediaSource.FromUri(src.DrmDescriptor!.InitUrl)
                   .With(new DrmConfig(DrmSystem.PlayReady, src.LicenseServerUri) { SourceDescriptor = src.DrmDescriptor })
             : MediaSource.FromUri(src.ClearUrl ?? "")
                   .WithLiveness(src.IsLive ? SourceLiveness.Live : SourceLiveness.Auto);

        static void PublishBinding(MediaPlayer? p, long generation)
            => ToUi(() => Player.Value = new Binding(p, generation));

        // ── 6. the 200 ms tick: position, duration, liveness, the watchdog ──────────────────────────────────────────

        static void StartTicker()
        {
            s_ticker ??= new Timer(static _ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            try { s_ticker.Change(TickMs, TickMs); } catch { }
        }

        static void StopTicker()
        {
            try { s_ticker?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        static void Tick()
        {
            MediaPlayer? p;
            uint epoch;
            lock (s_gate) { p = s_player; epoch = s_epoch; }
            if (p is null || s_disposed) { StopTicker(); return; }

            PlaybackState state = p.State.Peek();
            long pos = Math.Max(0, (long)p.Position.Peek().TotalMilliseconds);
            long durMs = (long)p.Duration.Peek().TotalMilliseconds;

            // (a) the engine's own error is the richest diagnosis we will get; it wins over the watchdog.
            if (!s_errorReported && p.Error.Peek() is { } err)
            {
                ReportFault(epoch, MapError(err), err.Message);
                return;
            }

            // (b) liveness, from the engine's timeline and NEVER from a finite duration (MF reports a sliding DVR
            // window as a finite number — the defect that rendered a six-hour broadcast as `0:03 / -3:22`).
            TimelineInfo tl = p.Timeline.Peek();
            var window = new LiveWindow(tl.IsLive,
                (long)tl.SeekableStart.TotalMilliseconds, (long)tl.SeekableEnd.TotalMilliseconds,
                (long)tl.LiveEdge.TotalMilliseconds, pos, tl.IsAtLiveEdge);
            if (LiveWindowMoved(window))
            {
                s_reportedLive = window;
                s_liveReported = true;
                Post(Input.LiveReport(in window));
            }

            // (c) duration. Suppressed entirely while live, and RE-relayed whenever it moves: MF's first publish at
            // LOADEDMETADATA is commonly 0 for DASH and is revised afterwards.
            if (!window.IsLive && durMs > 0 && Math.Abs(durMs - s_reportedDurMs) > DurationRelayEpsilonMs)
            {
                s_reportedDurMs = durMs;
                Post(Input.Duration((int)Math.Min(durMs, int.MaxValue), epoch));
            }

            // (d) the carried start position, applied at the first moment the session can honour a seek. The open
            // returns in ~30 ms on PlayReady while the native session is still spinning up, and a seek issued there
            // is silently dropped.
            if (s_startSeekPending && IsSeekReady(state, durMs))
            {
                long target = s_startAtMs;
                if (durMs > 0 && target > durMs - StartClampGuardMs) target = Math.Max(0, durMs - StartClampBackoffMs);
                s_startSeekPending = false;
                Seek(target);
            }

            // (e) the watchdog. `progressed` is deliberately generous: anything that proves the session is alive.
            bool progressed = s_progressed || s_errorReported || pos > 0
                || state is PlaybackState.Playing or PlaybackState.Ended or PlaybackState.Failed;
            if (progressed) s_progressed = true;
            if (s_watchdog.ShouldFault(FrameNowMs(), s_playIntent, progressed))
            {
                Log.Warn("video", $"start watchdog fired after {s_watchdog.TimeoutMs}ms — state={state} pos={pos} key={Tail(s_key)}");
                ReportFault(epoch, Fault.DrmRequired, "the video session never started playing (no progress within the start budget)");
                return;
            }

            // (f) the state fold. Every arm is edge-triggered on `_lastState` except the position tick.
            switch (state)
            {
                case PlaybackState.Playing:
                    if (!s_firstFrameFired) { s_firstFrameFired = true; PostSignal(epoch, AudioSignal.Started, pos); }
                    else if (s_lastState == PlaybackState.Playing) PostSignal(epoch, AudioSignal.Position, pos);
                    else PostSignal(epoch, AudioSignal.Started, pos);
                    break;

                case PlaybackState.Ready:
                case PlaybackState.Paused:
                    // A Ready/Paused session with live play intent and no play request is a LOST fire-and-forget
                    // PlayAsync. Re-assert, bounded — an unbounded re-assert fights a user's own pause.
                    if (s_playIntent && !p.IsPlayRequested.Peek() && s_playReassertsLeft > 0)
                    {
                        s_playReassertsLeft--;
                        try { _ = p.PlayAsync(); } catch { }
                        PostSignal(epoch, AudioSignal.Buffering, pos);
                    }
                    else if (s_lastState != state) PostSignal(epoch, AudioSignal.Paused, pos);
                    break;

                case PlaybackState.Opening:
                case PlaybackState.Buffering:
                case PlaybackState.Stalled:
                    if (s_lastState != state) PostSignal(epoch, AudioSignal.Buffering, pos);
                    break;

                case PlaybackState.Ended:
                    if (s_lastState == PlaybackState.Ended) break;
                    StopTicker();
                    Post(Input.Ended(epoch, FrameNowMs()));
                    break;

                case PlaybackState.Failed:
                    ReportFault(epoch, Fault.DecodeFailed, "video playback failed");
                    break;
            }
            s_lastState = state;
        }

        static bool LiveWindowMoved(in LiveWindow w)
        {
            if (!s_liveReported) return w.IsLive || w.SeekableEndMs > 0;
            LiveWindow last = s_reportedLive;
            if (w.IsLive != last.IsLive || w.IsAtLiveEdge != last.IsAtLiveEdge) return true;
            return Math.Abs(w.SeekableStartMs - last.SeekableStartMs) > LiveRelayEpsilonMs
                || Math.Abs(w.SeekableEndMs - last.SeekableEndMs) > LiveRelayEpsilonMs
                || Math.Abs(w.LiveEdgeMs - last.LiveEdgeMs) > LiveRelayEpsilonMs;
        }

        /// <summary>Publish one empty live window when the session dies, so the bar's DVR rail does not keep the dead
        /// stream's edge.</summary>
        static void RetractLiveWindow(uint epoch)
        {
            if (!s_liveReported) return;
            s_liveReported = false;
            s_reportedLive = default;
            LiveWindow none = LiveWindow.None;
            Post(Input.LiveReport(in none));
            _ = epoch;
        }

        static bool IsSeekReady(PlaybackState state, long durMs)
            => durMs > 0 || state is PlaybackState.Ready or PlaybackState.Playing or PlaybackState.Paused;

        static bool IsAtOrPastLiveEdge(MediaPlayer p, long ms)
        {
            TimelineInfo tl = p.Timeline.Peek();
            if (!tl.IsLive) return false;
            long edge = (long)tl.SeekableEnd.TotalMilliseconds;
            return edge > 0 && ms >= edge - GoLiveToleranceMs;
        }

        static void PostSignal(uint epoch, AudioSignal signal, long posMs = 0)
            => Post(Input.Audio(signal, epoch, FrameNowMs(), posMs));

        static void ReportFault(uint epoch, Fault kind, string message)
        {
            if (s_errorReported) return;
            s_errorReported = true;
            StopTicker();
            Log.Warn("video", $"fault={kind} key={Tail(s_key)} — {message}");
            Post(Input.Audio(AudioSignal.Failed, epoch, FrameNowMs(), (long)kind));
        }

        static Fault MapError(MediaError err) => err.Category switch
        {
            MediaErrorCategory.Network => Fault.Network,
            MediaErrorCategory.Drm => Fault.DrmRequired,
            MediaErrorCategory.Decode or MediaErrorCategory.UnsupportedCodec => Fault.DecodeFailed,
            MediaErrorCategory.Source => Fault.Unavailable,
            _ => Fault.Unknown,
        };

        /// <summary>The last 24 characters of a key. A module playable is 60+ characters of base64 that differs from
        /// its neighbours only in the tail, so a full log line is unreadable and a prefix is useless.</summary>
        static string Tail(string key) => key.Length <= 24 ? key : key[^24..];

        // ── 7. the detached-window seam (owner K attaches in Wave 4) ────────────────────────────────────────────────

        /// <summary>How a pop-out window is opened. The engine owns `IDetachedVideoWindow` and `InputHooks`; owner K's
        /// `Shell/Video.Host.cs` is its ONLY caller and sets this so `Playback.Video` never names a window type. It is
        /// here rather than there purely so the Wave-3 build has the seam and the Wave-4 owner has somewhere to
        /// attach: this file never invokes it.</summary>
        public static Func<bool>? CanOpenDetachedWindow { get; set; }

        /// <summary>True when a second window could host the picture. The player bar's placement menu disables the
        /// rung with its reason rather than hiding it, so this must answer even before a host attaches.</summary>
        public static bool DetachedAvailable => CanOpenDetachedWindow?.Invoke() ?? false;

        // ── 8. the manifest resolve (Spotify music video → a PlayReady DASH descriptor) ─────────────────────────────

        /// <summary>Spotify's v9 video manifest: the fetch, the parse, the rung pick, and the DASH descriptor the
        /// native PlayReady path consumes. No MPD is synthesised — Spotify's manifest carries the templates directly
        /// and the segments are addressed by ABSOLUTE TIME, which is why the descriptor's stride is the segment length
        /// in seconds rather than 1.</summary>
        public static class Manifest
        {
            /// <summary>The route. `supports_drm` is not optional: the clear variant is not served to a desktop
            /// client and asking for it answers 404.</summary>
            public static string Route(string manifestId)
                => "/manifests/v9/json/sources/" + manifestId + "/options/supports_drm";

            /// <summary>Fetch + parse + build. Blocks on the network: called on the pump, never on the UI thread.
            /// Returns null for every failure — a missing manifest is "this track has no video", not an error.</summary>
            public static VideoSource? Resolve(string manifestId, CancellationToken ct)
            {
                string route = Route(manifestId);
                Spotify.Api.Result result;
                try
                {
                    var args = new Spotify.RequestArgs
                    {
                        Path = route,
                        Host = Spotify.ApiHost.Spclient,
                        Verb = Spotify.Verb.Get,
                        // Origin/Referer are the CORS fence the gateway checks on this route; Bearer + client token
                        // are stamped by the runner from the session.
                        Headers = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken | Spotify.HeaderSet.Identity
                                | Spotify.HeaderSet.AcceptJson | Spotify.HeaderSet.Origin,
                    };
                    result = Spotify.Api.Send(Spotify.RequestKind.Custom, in args, ct);
                }
                catch (Exception ex) { Log.Warn("video", "manifest fetch failed", ex); return null; }

                if (!result.Ok || result.Body.Length == 0)
                {
                    Log.Info("video", $"manifest {Tail(manifestId)} refused: HTTP {result.Status}");
                    return null;
                }

                Parsed? parsed = Parse(result.Bytes);
                if (parsed is not { } m) return null;
                DashSourceDescriptor? descriptor = ToDescriptor(in m);
                if (descriptor is null) return null;
                var relay = License.Relay(m.LicenseEndpoint);
                return VideoSource.PlayReady(manifestId, descriptor, relay, m.LicenseEndpoint)
                    with { NaturalWidth = m.NaturalWidth, NaturalHeight = m.NaturalHeight };
            }

            /// <summary>One compatible video or audio rung.</summary>
            public readonly record struct Rung(string Id, string Codec, int Width, int Height, int Bitrate, string? KeyId);

            /// <summary>What the parse yields: the addressing templates, the PlayReady init data, and the two rung
            /// lists (H.264 only, and AAC only — Opus is advertised under the same PlayReady index and the protected
            /// MF pipeline cannot decode it).</summary>
            public readonly record struct Parsed(
                string BaseUrl, string InitTemplate, string SegmentTemplate,
                int SegmentLengthSeconds, long DurationMs,
                byte[] Pssh, string? LicenseEndpoint, string? DefaultKid,
                Rung[] Video, Rung Audio, bool HasAudio,
                int NaturalWidth, int NaturalHeight);

            /// <summary>Parse the v9 JSON. Both shapes are handled: templates at the root beside `contents[0]`, or
            /// everything nested under `sources[0]`.</summary>
            public static Parsed? Parse(ReadOnlySpan<byte> json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json.ToArray());
                    JsonElement root = doc.RootElement;
                    JsonElement content = root;
                    if (root.TryGetProperty("contents", out JsonElement contents) && contents.GetArrayLength() > 0)
                        content = contents[0];
                    else if (root.TryGetProperty("sources", out JsonElement sources) && sources.GetArrayLength() > 0)
                        content = sources[0];

                    string baseUrl = FirstString(content, root, "base_urls");
                    string initTpl = Str(content, "initialization_template") ?? Str(root, "initialization_template") ?? "";
                    string segTpl = Str(content, "segment_template") ?? Str(root, "segment_template") ?? "";
                    if (initTpl.Length == 0 || segTpl.Length == 0) return null;

                    int segLen = Int(content, "segment_length") ?? Int(root, "segment_length") ?? 4;
                    if (segLen <= 0) segLen = 4;
                    long durMs = Long(content, "duration") ?? Long(root, "duration") ?? 0;
                    if (durMs <= 0 && Long(content, "end_time_millis") is { } end && Long(content, "start_time_millis") is { } start)
                        durMs = end - start;

                    // The PlayReady encryption info's INDEX is what gates rung selection: a profile is compatible only
                    // when it carries that index.
                    int prIndex = -1;
                    byte[] pssh = [];
                    string? license = null;
                    if (Arr(content, "encryption_infos") is { } infos)
                    {
                        for (int i = 0; i < infos.GetArrayLength(); i++)
                        {
                            JsonElement info = infos[i];
                            string system = Str(info, "key_system") ?? "";
                            if (!system.Equals("playready", StringComparison.OrdinalIgnoreCase)) continue;
                            prIndex = i;
                            if (Str(info, "encryption_data") is { Length: > 0 } b64)
                            {
                                try { pssh = Convert.FromBase64String(b64); } catch { pssh = []; }
                            }
                            license = Str(info, "license_server_endpoint");
                            break;
                        }
                    }
                    if (prIndex < 0) return null;      // no PlayReady rung ⇒ nothing this pipeline can play

                    var video = new List<Rung>(8);
                    Rung audio = default;
                    bool hasAudio = false;
                    string? defaultKid = null;
                    if (Arr(content, "profiles") is { } profiles)
                    {
                        for (int i = 0; i < profiles.GetArrayLength(); i++)
                        {
                            JsonElement pr = profiles[i];
                            if ((Str(pr, "file_type") ?? "") is not "mp4") continue;
                            if (!CarriesIndex(pr, prIndex)) continue;
                            string id = Str(pr, "id") ?? "";
                            if (id.Length == 0) continue;
                            int bitrate = Int(pr, "max_bitrate") ?? Int(pr, "bandwidth_estimate")
                                ?? Int(pr, "video_bitrate") ?? Int(pr, "audio_bitrate") ?? 0;
                            string? kid = Str(pr, "key_id");

                            string vcodec = Str(pr, "video_codec") ?? "";
                            if (vcodec.Length > 0)
                            {
                                if (!IsH264(vcodec)) continue;       // H.264 only: the protected path decodes nothing else
                                int w = Int(pr, "video_width") ?? Int(pr, "width") ?? 0;
                                int h = Int(pr, "video_height") ?? Int(pr, "height") ?? 0;
                                video.Add(new Rung(id, vcodec, w, h, bitrate, kid));
                                defaultKid ??= kid;
                                continue;
                            }
                            string acodec = Str(pr, "audio_codec") ?? "";
                            if (acodec.Length == 0 || !IsAac(acodec)) continue;
                            if (!hasAudio || bitrate > audio.Bitrate) { audio = new Rung(id, acodec, 0, 0, bitrate, kid); hasAudio = true; }
                        }
                    }
                    if (video.Count == 0) return null;

                    Rung[] rungs = [.. video];
                    Array.Sort(rungs, static (a, b) => a.Height != b.Height ? a.Height - b.Height : a.Bitrate - b.Bitrate);
                    Rung top = rungs[^1];

                    return new Parsed(baseUrl, initTpl, segTpl, segLen, durMs, pssh, license, defaultKid,
                        rungs, audio, hasAudio, top.Width, top.Height);
                }
                catch (Exception ex) { Log.Warn("video", "manifest parse failed", ex); return null; }
            }

            /// <summary>Build the native descriptor. Returns null when the addressing cannot be resolved — the caller
            /// then reports "no video" rather than handing the native side a half-built open.</summary>
            public static DashSourceDescriptor? ToDescriptor(in Parsed m)
            {
                Rung top = m.Video[^1];
                // The conservative INITIAL pick is ≤ 480p: a music video that opens at 1080p on a cold connection
                // buffers visibly, and the ABR controller climbs within seconds. Every compatible rung stays in the
                // catalog so it can.
                Rung initial = m.Video[0];
                for (int i = 0; i < m.Video.Length; i++)
                {
                    if (m.Video[i].Height is > 0 and <= 480) initial = m.Video[i];
                }

                Addressing? va = Address(m, initial.Id);
                if (va is not { } v) return null;
                Addressing? aa = m.HasAudio ? Address(m, m.Audio.Id) : null;

                int segmentCount = m.SegmentLengthSeconds > 0 && m.DurationMs > 0
                    ? (int)Math.Ceiling(m.DurationMs / 1000.0 / m.SegmentLengthSeconds)
                    : 0;
                if (segmentCount <= 0) return null;

                var reps = new ProtectedRepresentationDescriptor[m.Video.Length];
                int kept = 0;
                for (int i = 0; i < m.Video.Length; i++)
                {
                    Rung r = m.Video[i];
                    if (Address(m, r.Id) is not { } a) continue;
                    reps[kept++] = new ProtectedRepresentationDescriptor
                    {
                        Id = r.Id,
                        Quality = new QualityVariant(r.Id, r.Bitrate, new SizeI(r.Width, r.Height), 0,
                            new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None), Label: r.Height + "p"),
                        InitUrl = a.Init,
                        SegmentBaseUrl = a.Base,
                        SegmentPrefix = a.Prefix,
                        SegmentSuffix = a.Suffix,
                        StartNumber = 0,
                        SegmentCount = segmentCount,
                        SegmentStride = m.SegmentLengthSeconds,
                        DefaultKid = r.KeyId,
                    };
                }
                if (kept == 0) return null;
                Array.Resize(ref reps, kept);

                var tracks = new List<ProtectedTrackDescriptor>(2)
                {
                    new()
                    {
                        Id = 1, Kind = TrackKind.Video, Label = top.Height + "p",
                        IsDefault = true, Representations = reps,
                    },
                };
                if (aa is { } a2)
                {
                    tracks.Add(new ProtectedTrackDescriptor
                    {
                        Id = 2, Kind = TrackKind.Audio, Label = "audio", Role = TrackRole.Main, IsDefault = true,
                        Representations = new[]
                        {
                            new ProtectedRepresentationDescriptor
                            {
                                Id = m.Audio.Id,
                                Quality = new QualityVariant(m.Audio.Id, m.Audio.Bitrate, new SizeI(0, 0), 0,
                                    new MediaContentType(Container.Mp4, CodecId.None, CodecId.Aac)),
                                InitUrl = a2.Init, SegmentBaseUrl = a2.Base,
                                SegmentPrefix = a2.Prefix, SegmentSuffix = a2.Suffix,
                                StartNumber = 0, SegmentCount = segmentCount, SegmentStride = m.SegmentLengthSeconds,
                                DefaultKid = m.Audio.KeyId,
                            },
                        },
                    });
                }

                return new DashSourceDescriptor
                {
                    Catalog = new ProtectedAdaptiveCatalog { Tracks = tracks },
                    InitUrl = v.Init,
                    SegmentBaseUrl = v.Base,
                    SegmentPrefix = v.Prefix,
                    SegmentSuffix = v.Suffix,
                    StartNumber = 0,
                    SegmentCount = segmentCount,
                    // Spotify names segments by ABSOLUTE TIME: segment i = start + i × (segment length in seconds).
                    SegmentStride = m.SegmentLengthSeconds,
                    Pssh = m.Pssh,
                    DefaultKid = m.DefaultKid,
                    RepresentationId = initial.Id,
                    Codecs = initial.Codec,
                    AudioInitUrl = aa?.Init,
                    AudioSegmentBaseUrl = aa?.Base,
                    AudioSegmentPrefix = aa?.Prefix,
                    AudioSegmentSuffix = aa?.Suffix,
                    AudioCodecs = m.HasAudio ? m.Audio.Codec : null,
                };
            }

            /// <summary>The four URL parts the native demuxer walks.</summary>
            public readonly record struct Addressing(string Init, string Base, string Prefix, string Suffix);

            /// <summary>Substitute the profile id and the file type, then split the media template at the literal
            /// `{{segment_timestamp}}` token — at the LAST '/' before it, so the base stays a directory and the signed
            /// query parameters survive byte for byte.</summary>
            public static Addressing? Address(in Parsed m, string profileId)
            {
                const string Token = "{{segment_timestamp}}";
                string init = Fill(m.BaseUrl, m.InitTemplate, profileId);
                string media = Fill(m.BaseUrl, m.SegmentTemplate, profileId);
                int at = media.IndexOf(Token, StringComparison.Ordinal);
                if (at < 0) return null;
                int slash = media.LastIndexOf('/', at);
                if (slash < 0) return null;
                return new Addressing(init, media[..(slash + 1)], media[(slash + 1)..at], media[(at + Token.Length)..]);
            }

            static string Fill(string baseUrl, string template, string profileId)
            {
                string s = template.Replace("{{profile_id}}", profileId, StringComparison.Ordinal)
                                   .Replace("{{file_type}}", "mp4", StringComparison.Ordinal);
                if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s;
                if (baseUrl.Length == 0) return s;
                return baseUrl.EndsWith('/') || s.StartsWith('/') ? baseUrl + s : baseUrl + "/" + s;
            }

            static bool IsH264(string codec)
                => codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("avc3", StringComparison.OrdinalIgnoreCase)
                || codec.Contains("h264", StringComparison.OrdinalIgnoreCase);

            static bool IsAac(string codec)
                => codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("aac", StringComparison.OrdinalIgnoreCase);

            static bool CarriesIndex(JsonElement profile, int index)
            {
                if (Arr(profile, "encryption_indices") is { } arr)
                {
                    for (int i = 0; i < arr.GetArrayLength(); i++)
                        if (arr[i].TryGetInt32(out int v) && v == index) return true;
                    return false;
                }
                return Int(profile, "encryption_index") is { } single ? single == index : true;
            }

            static string FirstString(JsonElement a, JsonElement b, string name)
            {
                if (Arr(a, name) is { } arr && arr.GetArrayLength() > 0) return arr[0].GetString() ?? "";
                if (Arr(b, name) is { } arr2 && arr2.GetArrayLength() > 0) return arr2[0].GetString() ?? "";
                return "";
            }

            static JsonElement? Arr(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Array ? v : null;

            static string? Str(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            static int? Int(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

            static long? Long(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;
        }

        /// <summary>The PlayReady licence relay. The native CDM raises an opaque SOAP challenge; this POSTs it to the
        /// manifest's own endpoint over the authenticated session and hands the bytes back. The content key never
        /// crosses into managed code.</summary>
        public static class License
        {
            const string DefaultRoute = "/playready-license";

            public static Func<LicenseRequest, ValueTask<LicenseResponse>> Relay(string? endpoint)
            {
                string route = Normalize(endpoint);
                return request => new ValueTask<LicenseResponse>(Acquire(route, request));
            }

            static LicenseResponse Acquire(string route, LicenseRequest request)
            {
                byte[] challenge = request.Challenge.ToArray();
                var args = new Spotify.RequestArgs
                {
                    Path = route,
                    Host = Spotify.ApiHost.Spclient,
                    Verb = Spotify.Verb.Post,
                    Body = challenge,
                    Headers = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken | Spotify.HeaderSet.Identity
                            | Spotify.HeaderSet.Origin,
                };
                Spotify.Api.Result result = Spotify.Api.Send(Spotify.RequestKind.Custom, in args, CancellationToken.None);
                if (!result.Ok || result.Body.Length == 0)
                    throw new InvalidOperationException($"Spotify PlayReady licence POST to {route} failed (HTTP {result.Status}).");
                return new LicenseResponse(result.Body);
            }

            /// <summary>The manifest gives an absolute URL or a path; `Spotify.Api` speaks paths on a named host.</summary>
            static string Normalize(string? endpoint)
            {
                if (endpoint is not { Length: > 0 }) return DefaultRoute;
                if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return endpoint.StartsWith('/') ? endpoint : "/" + endpoint;
                return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ? uri.PathAndQuery : DefaultRoute;
            }
        }
    }
}
