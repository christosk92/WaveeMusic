// ── Playback/Playback.Video.cs ─────────────────────────────────────────────────────────────────────────────────────
// the decode/host only (A13). Not one pixel of UI — the four video surfaces are owner K's Shell/Video.*
//
// Role: SHELL
// Owner: H
// Wave: 3 (gap batch B7)
// Budget: 1100 lines
// Spec: plan; docs/plans/wavee/wavee-0.3-video-engine-implementation.md §3.1.5, §3.2.3, §3.4, §6.2 H1-H6
//
// Named partials: `Playback.Video.Source.cs` — what plays (the resolver tiers, the manifest memo, the v9 parse, the
// licence relay); `Playback.Video.Warm.cs` — what is held in hand BEFORE the user asks (D15's keeper: the runtime and
// the next two content keys). The pure video arithmetic is `Playback.Video.Rules.cs` (owner V).
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. The decode half of video: open ONE long-lived `MediaPlayer` on a resolved `VideoSource`, keep it
// alive across a source switch, report position / duration / liveness / faults back as posted `Input`s, and publish
// the (player, generation) binding, the switch phase and the first-frame epoch the UI mounts and reads. A13
// (2026-09-12): every video SURFACE is owner K's `Shell/Video.{cs,UI.cs,Host.cs}`. There is not one `Element` in this
// file and there must never be one.
//
// THE SEAM, WHOLE (ch 24 §7 + the video plan §3.4):
//   1. `Video.Player` is a `Signal<Binding>` of (player, generation). The UI keys its `MediaPlayerElement` on the
//      GENERATION, never on the source, so a video→video skip keeps the element mounted.
//   2. SOMETHING MUST PUMP. A session only advances — and only publishes duration and `NaturalSize` — while a mounted
//      element calls `IMediaPlayer.PumpVideo`. This host builds and reports; it never pumps.
//   3. `Video.Phase` / `Video.FirstFrame` / `Video.Buffered` are written on the UI thread by `Observe`, which the
//      surfaces' host observer runs whenever the bound player published — the poster/spinner discriminator comes from
//      the session's EVENTS, never from a timer guess.
//
// THE ENGINE THIS CODES AGAINST (the combined engine, `ba24aac6d`). Every protected source is a session on the process
// PlayReady runtime (a warm engine, one CDM, a KID-keyed licence cache). `OpenAsync(source, MediaOpenOptions)` carries
// the START POSITION into the native open, so a song→video switch at 1:23 never presents 0:00 and there is no carried
// seek to issue afterwards; transport verbs are acknowledged by events, so there is no Play re-assert. The protected
// backend is PROCESS-LIFETIME here (not per player): a prefetch prepares a session on it and the next open of the same
// init url takes that session — a rebuilt player must not orphan what was prepared.
//
// THE WARM HALF (§6.2 G1, D15). Two rules keep a switch cheap and keep it honest. (1) The PREFETCH/LOAD RACE: a row the
// pump has claimed is never prefetched, and a load ADOPTS the prepare aimed at its own row instead of racing it — before
// this, a lit badge's click opened two protected sessions in the same millisecond and destroyed one. Every prepared
// session is now adopted or disposed; `Landed` is the only place one can come to rest. (2) The WARM KEEPER: while a
// video surface is wanted, the content keys of the playing row and the next queued one are pre-acquired on a 10 s beat
// (the licence is 2 010 ms of a measured 2 477 ms cold switch), which also brings the native runtime back up whenever it
// shed. The beat stops when the surface closes, and the app lets go 30 s later — D15, and the engine's own window.
//
// WHY THE PUMP IS STILL SERIALIZED AND EPOCHED. One worker, one coalescing slot, one epoch: a load that arrives while
// another is in flight REPLACES it (latest wins, C8), and every result goes back as a posted `Input` carrying the epoch
// it was started for (C1/C4). The runtime no longer needs it for correctness; the reducer's epochs still do.

using System.Collections.Concurrent;
using System.Threading;

using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Media.Windows;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Media.PlayReady;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The video decode host. One player, one pump, one watchdog.</summary>
    public static partial class Video
    {
        // ── 0. the values the seam carries ──────────────────────────────────────────────────────────────────────────

        /// <summary>A resolved, playable video: a clear URL, a local file, or a PlayReady DASH descriptor plus the licence
        /// relay that answers its challenges. NOT a column and never entity state (ch 24 DATA GAP 1): it carries a live
        /// delegate and a parsed descriptor, so it is session state on this host.</summary>
        /// <param name="Key">The content identity: the manifest id, the clear url, or `local:video:&lt;path&gt;`. The
        /// switch policy compares THIS, never the track slot — a relinked counterpart is the same video.</param>
        /// <param name="PlayableUri">The playable this source was resolved FOR (a local attachment's quarantine pair, the
        /// log's `track=`). Empty for a source nobody resolved.</param>
        /// <param name="OverrideSourceKey">A local attachment's recorded source key — the other half of the quarantine
        /// pair (<c>Video.Overrides.Quarantined</c>).</param>
        public sealed record VideoSource(
            string Key,
            string? ClearUrl = null,
            string? FilePath = null,
            DashSourceDescriptor? DrmDescriptor = null,
            Func<LicenseRequest, ValueTask<LicenseResponse>>? LicenseRelay = null,
            string? LicenseServerUri = null,
            int NaturalWidth = 0,
            int NaturalHeight = 0,
            bool IsLive = false,
            string PlayableUri = "",
            string? OverrideSourceKey = null)
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
        /// instance changes, so a source switch never remounts and never tears down the pump.</summary>
        public readonly record struct Binding(MediaPlayer? Player, long Generation);

        /// <summary>The player binding the four surfaces bind to. Written on the UI thread only (C1).</summary>
        public static readonly Signal<Binding> Player = new(default);

        /// <summary>The resolved source. A null source NEVER unmounts the stage (`ShouldMountPlayerStage` reads the
        /// PLAYER, not this) — it is the poster/loading discriminator and the aspect seed.</summary>
        public static readonly Signal<VideoSource?> Source = new(null);

        /// <summary>Where the switch is (§3.4): `Idle · Resolving · Licensing · Buffering · Attaching · Presenting ·
        /// Playing · Failed`. UI thread only; mirrored from the session's events by <see cref="Observe"/>.</summary>
        public static readonly Signal<SwitchPhase> Phase = new(SwitchPhase.Idle);

        /// <summary>Bumps once per source the moment its first frame is presentable — a surface drops its poster on a
        /// change of this value, never on a state guess.</summary>
        public static readonly Signal<long> FirstFrame = new(0L);

        /// <summary>Bumps whenever the live session's buffered ranges or keyframe table grew; read the ranges with
        /// <see cref="CopyBuffered"/> (the seek bar's loaded band — what MSE's `buffered` gives a web scrubber).</summary>
        public static readonly Signal<int> Buffered = new(0);

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

        /// <summary>The facts the switch decision needs. A record struct so a test is one line.</summary>
        public readonly record struct SwitchInput(bool HasPlayer, bool Faulted, string LiveKey, string RequestKey, long StartAtMs);

        /// <summary>Ported from 0.2.9's `VideoSwitchPolicy`. A faulted player is never switched in place, and "same key,
        /// same position" never re-opens — a docked→fullscreen placement move re-asks for the video already playing.</summary>
        public static SwitchAction Plan(in SwitchInput i)
        {
            if (!i.HasPlayer || i.Faulted) return SwitchAction.Rebuild;
            if (!string.Equals(i.LiveKey, i.RequestKey, StringComparison.Ordinal)) return SwitchAction.Switch;
            return i.StartAtMs > 0 ? SwitchAction.SeekOnly : SwitchAction.None;
        }

        /// <summary>The net under the engine's own start deadline. It catches the one failure that cannot be seen from
        /// inside the session: an open that never produced a session at all. Allocation-free POD on the 200 ms ticker.</summary>
        public struct StartWatchdog
        {
            /// <summary>Above the engine session's own CANPLAY deadline, so the engine's richer typed error wins whenever
            /// it can diagnose the failure itself.</summary>
            public const int DefaultTimeoutMs = 25_000;

            int _timeoutMs;
            long _armedAtMs;
            bool _armed, _fired;

            public StartWatchdog(int timeoutMs = DefaultTimeoutMs) { _timeoutMs = timeoutMs; }

            public readonly int TimeoutMs => _timeoutMs > 0 ? _timeoutMs : DefaultTimeoutMs;

            public readonly bool IsArmed => _armed && !_fired;

            public void Arm(long nowMs) { _armedAtMs = nowMs; _armed = true; _fired = false; }

            public void Disarm() { _armed = false; _fired = false; }

            /// <summary>True exactly once per load. Real progress disarms it; a DELIBERATE pause re-bases the budget
            /// instead of ageing it.</summary>
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

        /// <summary>The host's own small decisions, pure (`VideoHostRulesTests`).</summary>
        public static class HostRules
        {
            /// <summary>A carried position within this of the end is pulled back…</summary>
            public const long StartClampGuardMs = 250;
            /// <summary>…to this far before the end, so a restore never opens on the credits.</summary>
            public const long StartClampBackoffMs = 2_000;

            /// <summary>Where an open lands for a carried <paramref name="fromMs"/> against a known duration (0 = unknown).
            /// Applied BEFORE the open now that the open carries its start position.</summary>
            public static long StartAt(long fromMs, long durationMs)
            {
                long t = fromMs < 0 ? 0 : fromMs;
                if (durationMs > 0 && t > durationMs - StartClampGuardMs) t = Math.Max(0, durationMs - StartClampBackoffMs);
                return t;
            }

            /// <summary>The engine's protected phase, one-to-one onto the app's (the engine's enum mirrors
            /// <see cref="SwitchPhase"/> name for name; this is the one place that says so).</summary>
            public static SwitchPhase PhaseOf(ProtectedVideoPhase p) => p switch
            {
                ProtectedVideoPhase.Resolving => SwitchPhase.Resolving,
                ProtectedVideoPhase.Licensing => SwitchPhase.Licensing,
                ProtectedVideoPhase.Buffering => SwitchPhase.Buffering,
                ProtectedVideoPhase.Attaching => SwitchPhase.Attaching,
                ProtectedVideoPhase.Presenting => SwitchPhase.Presenting,
                ProtectedVideoPhase.Playing => SwitchPhase.Playing,
                ProtectedVideoPhase.Failed => SwitchPhase.Failed,
                _ => SwitchPhase.Idle,
            };

            /// <summary>A clear (or local) source has no event-derived phase: its transport state and whether a frame
            /// size exists are what it has.</summary>
            public static SwitchPhase PhaseOfClear(PlaybackState state, bool framePresented) => state switch
            {
                PlaybackState.Failed => SwitchPhase.Failed,
                PlaybackState.Idle => SwitchPhase.Idle,
                PlaybackState.Playing => framePresented ? SwitchPhase.Playing : SwitchPhase.Attaching,
                PlaybackState.Paused or PlaybackState.Ready or PlaybackState.Ended
                    => framePresented ? SwitchPhase.Presenting : SwitchPhase.Attaching,
                _ => framePresented ? SwitchPhase.Presenting : SwitchPhase.Buffering,
            };

            /// <summary>One engine seek call, or none.</summary>
            /// <param name="Engine">False for <see cref="SeekVerb.Ride"/>: playback reaches the target on its own.</param>
            /// <param name="TargetMs">Where the call seeks: the target on a commit, the keyframe shown on a preview.</param>
            /// <param name="Accurate">Decode to the exact PTS (a commit) or present the keyframe (a preview).</param>
            /// <param name="KeyframeHintMs">The planner's keyframe, passed down so native does not repeat the search; -1 =
            /// native decides.</param>
            public readonly record struct SeekCall(bool Engine, long TargetMs, bool Accurate, long KeyframeHintMs);

            /// <summary>A plan → the call. A preview never decodes to an exact PTS and shows the keyframe it planned; when
            /// the segment grid is unknown a Fetch's "segment start" is not a real position, so the raw target goes down
            /// with no hint.</summary>
            public static SeekCall SeekCallFor(in SeekPlan plan, long targetMs, SeekIntent intent, long segmentLengthMs)
            {
                bool grid = segmentLengthMs > 0;
                if (plan.Verb == SeekVerb.Ride) return new SeekCall(false, targetMs, true, -1);
                if (intent == SeekIntent.Preview)
                {
                    if (plan.Verb == SeekVerb.Fetch && !grid) return new SeekCall(true, targetMs, false, -1);
                    return plan.KeyframeMs >= 0
                        ? new SeekCall(true, plan.KeyframeMs, false, plan.KeyframeMs)
                        : new SeekCall(true, targetMs, false, -1);
                }
                long hint = plan.Verb == SeekVerb.Instant || (plan.Verb == SeekVerb.Fetch && grid) ? plan.KeyframeMs : -1;
                return new SeekCall(true, targetMs, true, hint);
            }

            /// <summary>The ABR height ceiling: the user's pin (0 = auto) and the metered cap (0 or `int.MaxValue` = none),
            /// the lower of the two.</summary>
            public static int QualityCap(int pinnedHeight, int meteredCap)
            {
                int pin = pinnedHeight > 0 ? pinnedHeight : int.MaxValue;
                int metered = meteredCap > 0 ? meteredCap : int.MaxValue;
                return pin < metered ? pin : metered;
            }
        }

        // ── 2. host state ───────────────────────────────────────────────────────────────────────────────────────────

        const int TickMs = 200;                  // the ONE named video timer (P10); position, liveness, the watchdog
        const int OpenTimeoutMs = 15_000;
        const int TeardownTimeoutMs = 5_000;
        const long DurationRelayEpsilonMs = 250; // MF revises a DASH duration after LOADEDMETADATA; relay the moves
        const long LiveRelayEpsilonMs = 250;
        const long GoLiveToleranceMs = 2_000;    // a committed seek this close to the edge is a GoLive, not a seek

        static readonly object s_gate = new();
        static readonly ConcurrentQueue<MediaPlayer> s_toDispose = new();

        static MediaPlayer? s_player;
        static long s_generation;
        static AdaptiveBitrateController? s_abr;
        static ProtectedMediaBackend? s_backend;
        static Timer? s_ticker;
        static bool s_disposed;

        // per-load state (all under s_gate)
        static string s_key = "";
        static VideoSource? s_live;              // the source the live load opened (fault attribution)
        static uint s_epoch;                     // the reducer's LoadEpoch this load belongs to
        static bool s_playIntent, s_intentPaused, s_progressed, s_errorReported, s_firstFrameFired;
        static float s_desiredRate = 1;
        static long s_reportedDurMs;
        static LiveWindow s_reportedLive;
        static bool s_liveReported;
        static StartWatchdog s_watchdog = new();
        static PlaybackState s_lastState = PlaybackState.Idle;
        static double s_volume = 1.0;
        static bool s_muted;
        static long s_switchAtMs;                // FrameNowMs at switch.begin — first.frame's sinceSwitchMs

        // the pump
        readonly record struct LoadRequest(VideoSource Source, long StartAtMs, uint Epoch);
        static LoadRequest? s_pending;
        static bool s_pendingClear, s_running;
        static long s_pumpEpoch;
        static Task s_worker = Task.CompletedTask;

        /// <summary>The process-lifetime protected backend. Built on first use; its prepared-session table must outlive a
        /// player rebuild, or a prefetch is thrown away by the very switch it was for.</summary>
        static ProtectedMediaBackend Backend
        {
            get { lock (s_gate) return s_backend ??= new ProtectedMediaBackend(License.ByKeyId, descriptor: null); }
        }

        // ── 3. the host API ─────────────────────────────────────────────────────────────────────────────────────────

        static bool s_warmed;

        /// <summary>The Playback tab's switch, DEFAULT ON: may the app fetch a video licence before the user asks for
        /// the video? Off leaves <see cref="Boot"/>'s native preload as the only warm, and the licence — ~80 % of a cold
        /// switch — is then paid on the switch itself. Declared beside its only reader rather than in
        /// <c>Platform.Keys</c>, the shape <c>Detail.SortKeys</c> already uses; the storage name follows the
        /// <c>playback.video.*</c> family it belongs to.</summary>
        public static readonly SettingKey<bool> PrepareAhead = new("playback.video.prepareAhead", true);

        /// <summary>Composition (`Video.Install`): route the engine's always-on <c>[video]</c> / <c>[video.native]</c>
        /// lines into the app's one log — the §4.3 gate reads them from the same file as the app's own lines.</summary>
        public static void InstallLog() => ProtectedVideoRuntime.LogSink = static line => Log.Info("video", line);

        /// <summary>A local attachment failed to open: the shell quarantines the (playable uri, source key) pair for the
        /// session and says so. Installed by `Video.Install`; invoked on the UI thread.</summary>
        public static Action<string, string>? OnOverrideFailed { get; set; }

        /// <summary>Preload the native PlayReady component — the ONE-SHOT half of the app's warm (the continuous half is
        /// <see cref="KeepWarm"/>'s beat). Called by `Video.Install` at composition rather than on the first switch: the
        /// DLL's load plus its whole MF/PlayReady import chain is part of what a cold <c>runtime.create</c> pays for, and
        /// it is the only warm left when the user turns <see cref="PrepareAhead"/> off. Idempotent; a missing DLL
        /// degrades to a typed DRM error rather than a `DllNotFoundException` in the frame loop.</summary>
        public static void Boot()
        {
            if (s_warmed) return;
            s_warmed = true;
            try { ProtectedMediaBackend.WarmupNative(); }
            catch (Exception ex) { Log.Warn("video", "playready warmup failed", ex); }
        }

        /// <summary>Play <paramref name="source"/> from <paramref name="fromMs"/>, for reducer epoch <paramref name="epoch"/>.
        /// UI thread. Returns immediately: the physical work is one coalesced request on the pump, and a request that
        /// arrives while another is in flight REPLACES it. <paramref name="paused"/> opens paused at the position; a
        /// <see cref="Pause"/> or <see cref="Play"/> issued before the open lands is honoured by it.</summary>
        public static void Load(VideoSource source, uint epoch, int fromMs = 0, bool paused = false)
        {
            Boot();
            float rate = RateFor(s_state.CurrentId);
            lock (s_gate)
            {
                s_desiredRate = rate;
                s_pumpEpoch++;
                s_pending = new LoadRequest(source, Math.Max(0, fromMs), epoch);
                s_pendingClear = false;
                s_intentPaused = paused;
                // THE CLAIM (§6.2 G1 rule 1), taken on the UI thread before any of the physical work: from this line on
                // the badge-lit prefetch leaves this row alone, because its load is the fetch.
                s_claim = new RowKey(source.PlayableUri, source.Key);
                EnsureWorker();
            }
            if (Phase.Peek() is SwitchPhase.Idle or SwitchPhase.Failed or SwitchPhase.Playing or SwitchPhase.Presenting)
                Phase.SetIfChanged(SwitchPhase.Buffering);
        }

        /// <summary>Stop and release. A clear INVALIDATES and overtakes a queued load.</summary>
        public static void Stop()
        {
            lock (s_gate)
            {
                s_pumpEpoch++;
                s_pending = null;
                s_pendingClear = true;
                s_claim = RowKey.None;
                EnsureWorker();
            }
        }

        /// <summary>Play. The intent is recorded FIRST, so a load still on the pump opens playing.</summary>
        public static void Play()
        {
            MediaPlayer? p;
            lock (s_gate) { s_playIntent = true; s_intentPaused = false; p = s_player; }
            if (p is null) return;
            try { _ = p.PlayAsync(); } catch (Exception ex) { Log.Warn("video", "play failed", ex); }
            StartTicker();
        }

        /// <summary>Pause. The intent drops FIRST, so a deliberate pause never ages toward a watchdog fault and a load
        /// still on the pump opens paused.</summary>
        public static void Pause()
        {
            MediaPlayer? p;
            lock (s_gate) { s_playIntent = false; s_intentPaused = true; p = s_player; }
            if (p is null) return;
            try { _ = p.PauseAsync(); } catch (Exception ex) { Log.Warn("video", "pause failed", ex); }
            StopTicker();
        }

        /// <summary>The video half of the audio cut (§3.1.4): one exact seek to <paramref name="atMs"/> inside the prepared
        /// window, then play. UI thread.</summary>
        public static void Go(long atMs)
        {
            Seek(atMs, accurate: true);
            Play();
        }

        // the seek index: two fixed buffers, refilled only when the session's index epoch moved (never per frame, P8)
        static readonly long[] s_keyframes = new long[1024];
        static readonly long[] s_bufferedPairs = new long[128];
        static int s_keyframeCount, s_bufferedPairCount;
        static IProtectedVideoPlayer? s_indexOwner;
        static int s_indexEpochRead = -1;

        // the seek in flight, for `[video] seek.done` (UI thread)
        static long s_seekTargetMs = -1, s_seekIssuedAtMs;
        static bool s_seekFetched;

        /// <summary>Seek. UI thread. A COMMITTED (<paramref name="accurate"/>) seek at or past a live edge becomes
        /// `GoLiveAsync`. A protected source is planned against the session's keyframe table and buffered ranges
        /// (`SeekPlanner`): a scrub PREVIEW shows the closest buffered keyframe and never fetches while the pointer is
        /// down; a commit decodes to the target, or rides when playback is about to reach it anyway.</summary>
        public static void Seek(long ms, bool accurate = true)
        {
            MediaPlayer? p;
            VideoSource? live;
            lock (s_gate) { p = s_player; live = s_live; }
            if (p is null) return;
            long target = Math.Max(0, ms);
            try
            {
                if (accurate && IsAtOrPastLiveEdge(p, target)) { _ = p.GoLiveAsync(); return; }
                if (p.Session is not ProtectedMediaSession ps)
                {
                    _ = p.SeekAsync(TimeSpan.FromMilliseconds(target), accurate ? SeekMode.Accurate : SeekMode.Keyframe);
                    return;
                }

                RefillIndex(ps.Player);
                long segLen = live?.DrmDescriptor?.SegmentLengthMs ?? 0;
                SeekIntent intent = accurate ? SeekIntent.Commit : SeekIntent.Preview;
                var index = new SeekIndex(
                    new ReadOnlySpan<long>(s_keyframes, 0, s_keyframeCount),
                    new ReadOnlySpan<long>(s_bufferedPairs, 0, s_bufferedPairCount * 2),
                    segLen, (long)p.Duration.Peek().TotalMilliseconds, (long)p.Position.Peek().TotalMilliseconds, p.IsPlaying.Peek());
                SeekPlan plan = SeekPlanner.Plan(in index, target, intent);
                LogLine(new VideoLog.SeekPlanned(target, intent, plan.Verb, plan.KeyframeMs, plan.SegmentIndex, plan.DecodeToTargetMs));

                HostRules.SeekCall call = HostRules.SeekCallFor(in plan, target, intent, segLen);
                if (!call.Engine)
                {
                    LogLine(new VideoLog.SeekDone(target, target, 0, false));
                    return;
                }
                s_seekTargetMs = call.TargetMs;
                s_seekIssuedAtMs = FrameNowMs();
                s_seekFetched = plan.Verb == SeekVerb.Fetch;
                _ = ps.SeekAsync(TimeSpan.FromMilliseconds(call.TargetMs), call.Accurate ? SeekMode.Accurate : SeekMode.Keyframe,
                    call.KeyframeHintMs);
            }
            catch (Exception ex) { Log.Warn("video", "seek failed", ex); }
        }

        /// <summary>The live session's buffered ranges as ascending (start, end) ms pairs into <paramref name="pairs"/>;
        /// returns the pair count. UI thread; re-reads the session only when its index epoch moved.</summary>
        public static int CopyBuffered(Span<long> pairs)
        {
            if (Player.Peek().Player?.Session is not ProtectedMediaSession ps) return 0;
            RefillIndex(ps.Player);
            int n = Math.Min(s_bufferedPairCount, pairs.Length / 2);
            new ReadOnlySpan<long>(s_bufferedPairs, 0, n * 2).CopyTo(pairs);
            return n;
        }

        static void RefillIndex(IProtectedVideoPlayer player)
        {
            int epoch = player.IndexEpoch;
            if (ReferenceEquals(player, s_indexOwner) && epoch == s_indexEpochRead) return;
            s_indexOwner = player;
            s_indexEpochRead = epoch;
            s_keyframeCount = Math.Min(player.GetKeyframes(s_keyframes), s_keyframes.Length);
            s_bufferedPairCount = Math.Min(player.GetBuffered(s_bufferedPairs), s_bufferedPairs.Length / 2);
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

        /// <summary>Pin a video height, or 0 for auto (the Settings tab writes the key, then calls this). Applied to the
        /// ABR ceiling — under the metered cap — AND to the engine's own quality selection, so a pin survives a switch.</summary>
        public static void SetPreferredHeight(int height)
        {
            ApplyQualityCaps(height);
            MediaPlayer? p;
            lock (s_gate) { p = s_player; }
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

        /// <summary>Re-apply the ABR ceiling from the stored pin and the live metered cap (G-148). Any thread — the host
        /// observer calls it when the network cost or the metered setting moves.</summary>
        public static void ApplyQualityCaps() => ApplyQualityCaps(Platform.Settings.Get(Platform.Keys.VideoQuality));

        static void ApplyQualityCaps(int pinnedHeight)
        {
            int cap = HostRules.QualityCap(pinnedHeight, Platform.Network.EffectiveVideoMaxHeight());
            MediaPlayer? p;
            AdaptiveBitrateController? abr;
            lock (s_gate) { p = s_player; abr = s_abr; }
            if (abr is not null) abr.MaxHeight = cap;
            try { p?.SetAdaptiveMaxHeight(cap); } catch { /* fail-soft */ }
        }

        // ── prefetch (plan §3.1.5, G-146, §6.2 G1) ──────────────────────────────────────────────────────────────────
        //
        // THE PREFETCH SLOT, all under `s_gate`. At most ONE prepare is claimed at a time (`s_preparingGate` non-null,
        // aimed at `s_preparingRow`) and at most one landed session is parked (`s_prepared` for `s_preparedKey`). Every
        // item that lands goes through `Landed`, which either parks it or disposes it on the spot — there is no third
        // path, which is what makes "a prepared session is adopted or disposed, never stranded" a property of the code
        // rather than of the timing.
        //
        // WHY THE GATE IS A SEPARATE TASK FROM THE PREPARE. `ProtectedMediaBackend.PrepareAtAsync` REGISTERS its session
        // (by init url, the key the matching open takes it by) synchronously, before its first await; only the segment
        // download is asynchronous. So the load that adopts a prepare waits on the REGISTRATION and not on the download:
        // the moment the backend knows about the session, the open finds it, and the download the load was going to do
        // anyway continues underneath the attach.

        static EntityId s_prefetchId;
        static PrefetchLevel s_prefetchLevel;
        static TaskCompletionSource? s_preparingGate;
        static RowKey s_preparingRow = RowKey.None;
        static bool s_dropPreparing;
        static IPreparedItem? s_prepared;
        static string s_preparedKey = "";
        static RowKey s_claim = RowKey.None;

        /// <summary>The level already reached for <paramref name="id"/> — the schedule's <c>Already</c>. UI thread.</summary>
        public static PrefetchLevel PrefetchedLevel(EntityId id) => id.Equals(s_prefetchId) ? s_prefetchLevel : PrefetchLevel.None;

        /// <summary>Bring <paramref name="id"/>'s video to <paramref name="level"/> before anyone asks for it (UI thread;
        /// the work runs on an api thread): Manifest = the memoised resolve; ManifestAndLicense = + the licence at
        /// manifest time; Full = + the init and the segments at <paramref name="atMs"/> in a prepared session the next open
        /// of the same source takes. The caller decides the level (<see cref="PrefetchSchedule.Decide"/>).
        /// <para>A row the pump has already CLAIMED is refused outright (G1 rule 1): its load fetches everything this
        /// would, and a prepare racing it opens a second protected session the switch never takes.</para></summary>
        public static void Prefetch(EntityId id, string manifestId, PrefetchLevel level, PrefetchReason why, int atMs)
        {
            if (level == PrefetchLevel.None || id.IsEmpty) return;
            if (id.Equals(s_prefetchId) && s_prefetchLevel >= level) return;
            string uri = id.Text;                  // materialised ONCE: the row, the claim compare and the log all use it
            var row = new RowKey(uri, manifestId);
            RowKey claimed;
            lock (s_gate) claimed = s_claim;
            if (!PrefetchRace.ShouldPrefetch(in claimed, in row)) return;

            s_prefetchId = id;
            s_prefetchLevel = level;
            LogLine(new VideoLog.PrefetchPlanned(Tail(uri), level, why, Platform.Network.IsMetered));
            int at = Math.Max(0, atMs);
            // The slot is claimed HERE, on the UI thread, before the work that fills it is even queued: a load starting
            // in the same frame must SEE the prepare that is about to exist, or it races it all over again.
            bool prepare = level == PrefetchLevel.Full && ArmPrepare(in row);
            if (Spotify.Api.Run(() => PrefetchWork(id, row, level, at, prepare))) return;
            s_prefetchLevel = PrefetchLevel.None;
            if (prepare) DisarmPrepare(in row);
        }

        static void PrefetchWork(EntityId id, RowKey row, PrefetchLevel level, int atMs, bool prepare)
        {
            bool handedOver = false;
            try
            {
                VideoSource? source;
                try { source = ResolveCore(id, row.Key, CancellationToken.None, forPlayback: false); }
                catch (Exception ex) { Log.Warn("video", "prefetch resolve failed", ex); return; }
                if (source is not { IsDrm: true } || level == PrefetchLevel.Manifest) return;

                // The key the manifest named is only known now: the slot learns it, so a load that knows the KEY and
                // never saw the row still recognises this prepare as its own.
                if (prepare) NamePrepare(in row, source.Key);
                StartLicense(source);
                if (!prepare) return;
                handedOver = true;                 // from here the slot belongs to PrepareQuietlyAsync, which always frees it
                _ = PrepareQuietlyAsync(source, atMs);
            }
            finally { if (prepare && !handedOver) DisarmPrepare(in row); }
        }

        /// <summary>The licence at MANIFEST time (PlayReady's proactive acquisition): by the moment the user asks for the
        /// video the key is usable and the switch costs no round trip. Idempotent in the runtime's KID cache.</summary>
        static void StartLicense(VideoSource source)
        {
            DashSourceDescriptor d = source.DrmDescriptor!;
            var request = new ProtectedVideoRequest
            {
                Pssh = d.Pssh,
                DefaultKid = d.DefaultKid,
                LicenseRelay = source.LicenseRelay,
                Drm = new DrmConfig(DrmSystem.PlayReady, source.LicenseServerUri) { SourceDescriptor = d },
            };
            try
            {
                LicenseCacheState state = ProtectedMediaBackend.StartLicense(ProtectedVideoRuntime.Shared, request);
                Log.Info("video", $"[video] license.start key={Tail(source.Key)} state={state}");
            }
            catch (Exception ex) { Log.Warn("video", "licence start failed", ex); }
        }

        /// <summary>Claim the prefetch slot for <paramref name="row"/>, on the UI thread, before the work that fills it
        /// is queued. False when a prepare is already claimed — one at a time, and the schedule re-asks on its next
        /// edge.</summary>
        static bool ArmPrepare(in RowKey row)
        {
            IPreparedItem? superseded = null;
            lock (s_gate)
            {
                if (s_preparingGate is not null) return false;
                s_preparingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                s_preparingRow = row;
                s_dropPreparing = false;
                if (s_prepared is not null && !string.Equals(s_preparedKey, row.Key, StringComparison.Ordinal))
                {
                    superseded = s_prepared;
                    s_prepared = null;
                    s_preparedKey = "";
                }
            }
            if (superseded is not null) _ = superseded.DisposeAsync();
            return true;
        }

        /// <summary>The slot learnt the key its row resolves to (api thread).</summary>
        static void NamePrepare(in RowKey row, string key)
        {
            lock (s_gate)
                if (s_preparingGate is not null && PrefetchRace.Same(in s_preparingRow, in row))
                    s_preparingRow = s_preparingRow with { Key = key };
        }

        /// <summary>Give the slot back unfilled (the work was refused, the row has no DRM video, the resolve failed).</summary>
        static void DisarmPrepare(in RowKey row)
        {
            bool ours;
            lock (s_gate) ours = s_preparingGate is not null && PrefetchRace.Same(in s_preparingRow, in row);
            if (ours) ClosePrepareSlot();
        }

        /// <summary>The registration is done (or will never happen): release anyone waiting to adopt this prepare. The
        /// slot itself stays claimed until the item lands.</summary>
        static void OpenPrepareGate()
        {
            TaskCompletionSource? gate;
            lock (s_gate) gate = s_preparingGate;
            gate?.TrySetResult();
        }

        /// <summary>Free the slot. The gate is completed on the way out whatever happened, so a load waiting to adopt can
        /// never hang on a prepare that died.</summary>
        static void ClosePrepareSlot()
        {
            TaskCompletionSource? gate;
            lock (s_gate)
            {
                gate = s_preparingGate;
                s_preparingGate = null;
                s_preparingRow = RowKey.None;
                s_dropPreparing = false;
            }
            gate?.TrySetResult();
        }

        static async Task PrepareQuietlyAsync(VideoSource source, int atMs)
        {
            ValueTask<IPreparedItem> pending;
            try { pending = Backend.PrepareAtAsync(BuildMediaSource(source), TimeSpan.FromMilliseconds(atMs)); }
            catch (Exception ex) { Log.Warn("video", "prefetch prepare failed", ex); ClosePrepareSlot(); return; }

            OpenPrepareGate();                     // the backend has the session now — an adopting load may stop waiting
            try { Landed(await pending.ConfigureAwait(false), source.Key); }
            catch (Exception ex) { Log.Warn("video", "prefetch prepare failed", ex); ClosePrepareSlot(); }
        }

        /// <summary>THE landing place for a prepared session, and the only one (G1). It is parked for the load it was
        /// fetched for, or disposed where it stands — <see cref="s_dropPreparing"/> is set by a load that claimed a
        /// DIFFERENT row while this prepare was still in the air, so the session is dead before anyone could have seen
        /// it. An item the open already took disposes as a no-op; one nobody opened frees its segment store and its
        /// runtime reference.</summary>
        static void Landed(IPreparedItem item, string key)
        {
            IPreparedItem? dead;
            lock (s_gate)
            {
                bool keep = !s_dropPreparing;
                dead = keep ? (ReferenceEquals(s_prepared, item) ? null : s_prepared) : item;
                if (keep) { s_prepared = item; s_preparedKey = key; }
            }
            ClosePrepareSlot();
            if (dead is not null) _ = dead.DisposeAsync();
        }

        /// <summary>G1 rule 2, the load's half. A prepare aimed at THIS row is adopted — the load waits (briefly, and
        /// only when it is actually going to open) for the backend to have registered it, and the open then takes that
        /// session by its init url. One aimed anywhere else is marked dead on arrival. Every path through here leaves the
        /// in-flight prepare either owned by this load or condemned; none leaves it running for nobody.</summary>
        static async Task ResolvePrepareAsync(RowKey loading, bool opening)
        {
            Task? gate = null;
            lock (s_gate)
            {
                switch (PrefetchRace.FateOf(s_preparingGate is not null, in s_preparingRow, in loading))
                {
                    case PrepareFate.Drop: s_dropPreparing = true; break;
                    case PrepareFate.Adopt when opening: gate = s_preparingGate!.Task; break;
                }
            }
            if (gate is null) return;
            try { await gate.WaitAsync(TimeSpan.FromMilliseconds(WarmPolicy.AdoptBudgetMs)).ConfigureAwait(false); }
            catch (TimeoutException) { Log.Warn("video", "the prepare this switch would adopt never registered — opening cold"); }
            catch (Exception ex) { Log.Warn("video", "adopting the prepared session failed", ex); }
        }

        /// <summary>Drop the PARKED session unless it is for <paramref name="keepKey"/> (a load of something else, a
        /// shutdown, the shed). A session the open already took disposes as a no-op; one nobody opened frees its segment
        /// store and its runtime reference here.</summary>
        static void ReleasePrepared(string? keepKey)
        {
            IPreparedItem? item;
            lock (s_gate)
            {
                item = s_prepared;
                if (item is null || (keepKey is not null && string.Equals(s_preparedKey, keepKey, StringComparison.Ordinal))) return;
                s_prepared = null;
                s_preparedKey = "";
            }
            _ = item.DisposeAsync();
        }

        /// <summary>Condemn whatever is still in the air: it is disposed the moment it lands. The other half of "adopted
        /// or disposed" — <see cref="ResolvePrepareAsync"/> resolves every prepare a LOAD races, and this resolves the
        /// ones nothing will ever load (a shed, a shutdown).</summary>
        static void CondemnPreparing()
        {
            lock (s_gate) if (s_preparingGate is not null) s_dropPreparing = true;
        }

        /// <summary>Shut down for good. Drains the pump so a session is not torn down mid-open.</summary>
        public static void Shutdown()
        {
            s_disposed = true;
            Stop();
            try { s_worker.Wait(TeardownTimeoutMs * 2); } catch { }
            StopTicker();
            try { s_ticker?.Dispose(); } catch { }
            s_ticker = null;
            DisposeWarmTimer();
            CondemnPreparing();                    // whatever is still landing dies on arrival…
            ReleasePrepared(keepKey: null);        // …and whatever landed already is freed here
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
                LoadRequest? next;
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

                // Both arms are wrapped: a throwing load can never kill the worker, and it must never be silent either —
                // the reducer gets a typed fault for the epoch it asked about.
                try
                {
                    if (clear) await TeardownAsync().ConfigureAwait(false);
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

        static async Task ApplyAsync(LoadRequest req, long epoch)
        {
            MediaPlayer? live;
            string liveKey;
            bool faulted;
            lock (s_gate) { live = s_player; liveKey = s_key; faulted = s_errorReported; }

            req = req with { StartAtMs = HostRules.StartAt(req.StartAtMs, req.Source.DrmDescriptor?.DurationMs ?? 0) };
            SwitchAction plan = Plan(new SwitchInput(live is not null, faulted, liveKey, req.Source.Key, req.StartAtMs));
            Interlocked.Exchange(ref s_switchAtMs, FrameNowMs());

            // G1 rule 2, BEFORE the log line and before any open: adopt the prepare this very switch was fetched for, or
            // condemn one aimed elsewhere. `warm=` is then the truth about the open that follows — which is what the
            // §4.3 gate reads it for.
            var row = new RowKey(req.Source.PlayableUri, req.Source.Key);
            await ResolvePrepareAsync(row, plan is SwitchAction.Switch or SwitchAction.Rebuild).ConfigureAwait(false);
            LogLine(new VideoLog.SwitchBegin(Tail(req.Source.Key), req.StartAtMs, plan, WarmFor(req.Source), req.Epoch));
            ReleasePrepared(keepKey: req.Source.Key);

            switch (plan)
            {
                case SwitchAction.None:
                    return;
                case SwitchAction.SeekOnly:
                {
                    long at = req.StartAtMs;
                    ToUi(() => Seek(at));                // the seek index is the UI thread's
                    return;
                }
                case SwitchAction.Switch:
                    await SwitchInPlaceAsync(req, epoch, live!).ConfigureAwait(false);
                    return;
                default:
                    await TeardownAsync().ConfigureAwait(false);
                    if (IsStale(epoch)) return;
                    await BuildAndOpenAsync(req, epoch).ConfigureAwait(false);
                    return;
            }
        }

        /// <summary>Was the warm path ACTUALLY in hand for this load — the `warm=` of `[video] switch.begin`? Either a
        /// prepared session for its row is parked or registered (the open takes it by init url and the switch is one
        /// attach), or its content key is already usable in the runtime's cache (the licence round trip, ~80 % of a cold
        /// switch, is already paid). `warm=false` therefore means exactly one thing: this switch pays for everything.
        /// <para>The runtime's own lock is taken OUTSIDE <see cref="s_gate"/>, never nested under it.</para></summary>
        static bool WarmFor(VideoSource source)
        {
            lock (s_gate)
            {
                if (s_prepared is not null && string.Equals(s_preparedKey, source.Key, StringComparison.Ordinal)) return true;
                if (s_preparingGate is { Task.IsCompleted: true } && !s_dropPreparing
                    && string.Equals(s_preparingRow.Key, source.Key, StringComparison.Ordinal)) return true;
            }
            return source.DrmDescriptor?.DefaultKid is { Length: > 0 } kid
                && ProtectedVideoRuntime.Shared.LicenseStateFor(kid) == LicenseCacheState.Usable;
        }

        static async Task BuildAndOpenAsync(LoadRequest req, long epoch)
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
            MediaOpenOptions options = ResetPerLoad(req);
            PublishBinding(built, generation);
            VideoSource published = req.Source;
            ToUi(() => Source.Value = published);

            PostSignal(req.Epoch, AudioSignal.Buffering);
            StartTicker();                         // BEFORE the open: an open that never completes must still tick
            long startedAt = FrameNowMs();
            try
            {
                await built.OpenAsync(BuildMediaSource(req.Source), options).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMs)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn("video", "open timed out — the start watchdog owns it now");
                return;
            }
            if (IsStale(epoch)) return;
            if (built.Error.Peek() is { } err) { ReportFault(req.Epoch, MapError(err), err.Message); return; }
            Log.Info("video", $"[video] open.ok key={Tail(req.Source.Key)} epoch={req.Epoch} openMs={FrameNowMs() - startedAt} rebuild=true");
            ReleasePrepared(keepKey: null);        // the open took it (the dispose is then a no-op) or never will
            SettleIntent(built);
        }

        /// <summary>A different video on a HEALTHY player: re-open the same instance. No `PlayerChanged`, so the mounted
        /// element is never remounted. On a timeout the watchdog takes it; on any other failure this degrades ONCE to a
        /// full rebuild.</summary>
        static async Task SwitchInPlaceAsync(LoadRequest req, long epoch, MediaPlayer live)
        {
            RetractLiveWindow();                  // must precede ResetPerLoad: it retracts the OLD window
            MediaOpenOptions options = ResetPerLoad(req);
            VideoSource published = req.Source;
            ToUi(() => Source.Value = published);
            PostSignal(req.Epoch, AudioSignal.Buffering);
            StartTicker();
            long startedAt = FrameNowMs();
            try
            {
                await live.OpenAsync(BuildMediaSource(req.Source), options).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMs)).ConfigureAwait(false);
                Log.Info("video", $"[video] open.ok key={Tail(req.Source.Key)} epoch={req.Epoch} openMs={FrameNowMs() - startedAt} rebuild=false");
                ReleasePrepared(keepKey: null);    // the open took it (the dispose is then a no-op) or never will
                if (!IsStale(epoch)) SettleIntent(live);
            }
            catch (TimeoutException)
            {
                Log.Warn("video", "switch timed out — the start watchdog owns it now");
            }
            catch (Exception ex)
            {
                Log.Warn("video", "switch failed — degrading to a rebuild", ex);
                await TeardownAsync().ConfigureAwait(false);
                if (!IsStale(epoch)) await BuildAndOpenAsync(req, epoch).ConfigureAwait(false);
            }
        }

        /// <summary>After the open: the intent the reducer last stated wins — a Pause that arrived during the open leaves
        /// it paused; otherwise play (idempotent on a session that opened playing).</summary>
        public static void SetRate(float rate)
        {
            lock (s_gate) s_desiredRate = rate;
            try { Volatile.Read(ref s_player)?.SetRate(rate); }
            catch (Exception ex) { Log.Warn("video", "speed change failed", ex); }
        }

        static void SettleIntent(MediaPlayer p)
        {
            bool play;
            float rate;
            lock (s_gate) { play = s_playIntent; rate = s_desiredRate; }
            try
            {
                p.SetRate(rate);
                if (play) _ = p.PlayAsync();
                else _ = p.PauseAsync();
            }
            catch (Exception ex) { Log.Warn("video", "initial transport failed", ex); }
        }

        /// <summary>Tear the session down. `PublishBinding(null)` runs BEFORE the dispose: the pumping surface must unbind
        /// first, or the successor is never pumped.</summary>
        static async Task TeardownAsync()
        {
            MediaPlayer? old;
            lock (s_gate)
            {
                old = s_player;
                s_player = null;
                s_key = "";
                s_live = null;
                s_playIntent = false;
                s_progressed = false;
                s_errorReported = false;
                s_firstFrameFired = false;
                s_reportedDurMs = 0;
                s_lastState = PlaybackState.Idle;
                s_watchdog.Disarm();
            }
            StopTicker();
            RetractLiveWindow();
            ToUi(s_clearSource);
            if (old is null) return;

            try { old.Stop(); } catch { }
            PublishBinding(null, ++s_generation);
            await DisposeBoundedAsync(old).ConfigureAwait(false);
            DrainDisposals();
        }

        static readonly Action s_clearSource = static () =>
        {
            Source.Value = null;
            Phase.SetIfChanged(SwitchPhase.Idle);
        };

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

        /// <summary>Reset the per-load state and build the open's options: the position the open lands at, whether it
        /// opens paused (the reducer's LATEST word — a Pause issued after the Load counts), and this source's own relay.</summary>
        static MediaOpenOptions ResetPerLoad(LoadRequest req)
        {
            bool paused;
            lock (s_gate)
            {
                s_key = req.Source.Key;
                s_live = req.Source;
                s_epoch = req.Epoch;
                paused = s_intentPaused;
                s_playIntent = !paused;
                s_progressed = false;
                s_errorReported = false;
                s_firstFrameFired = false;
                s_reportedDurMs = 0;
                s_reportedLive = default;
                s_liveReported = false;
                s_lastState = PlaybackState.Idle;
                s_watchdog.Arm(FrameNowMs());
            }
            ApplyQualityCaps();
            return new MediaOpenOptions
            {
                StartPosition = TimeSpan.FromMilliseconds(req.StartAtMs),
                StartPaused = paused,
                LicenseRelay = req.Source.LicenseRelay,
            };
        }

        // ── 5. building the player and the source ───────────────────────────────────────────────────────────────────

        /// <summary>ONE long-lived player for clear, local and every DRM source, over the process-lifetime protected
        /// backend. The descriptor rides the source (`DrmConfig.SourceDescriptor`) and the relay rides the open
        /// (`MediaOpenOptions.LicenseRelay`); the builder's relay is only the KID-routed fallback (G-145: no per-load
        /// relay slot any more).</summary>
        static MediaPlayer BuildPlayer()
        {
            int cap = HostRules.QualityCap(Platform.Settings.Get(Platform.Keys.VideoQuality), Platform.Network.EffectiveVideoMaxHeight());
            var abr = new AdaptiveBitrateController { MaxHeight = cap };
            lock (s_gate) s_abr = abr;
            return MediaPlayer.Build()
                .WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer(Backend))
                .WithAbr(abr)
                .WithDrm(License.ByKeyId)
                .Build();
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
            bool intent;
            lock (s_gate) { p = s_player; epoch = s_epoch; intent = s_playIntent; }
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

            // (b) liveness, from the engine's timeline and NEVER from a finite duration.
            TimelineInfo tl = p.Timeline.Peek();
            var window = new LiveWindow(tl.IsLive,
                (long)tl.SeekableStart.TotalMilliseconds, (long)tl.SeekableEnd.TotalMilliseconds,
                (long)tl.LiveEdge.TotalMilliseconds, pos, tl.IsAtLiveEdge);
            if (LiveWindowMoved(window))
            {
                s_reportedLive = window;
                s_liveReported = true;
                ReportLiveWindow(in window);
            }

            // (c) duration. Suppressed while live, and RE-relayed whenever it moves.
            if (!window.IsLive && durMs > 0 && Math.Abs(durMs - s_reportedDurMs) > DurationRelayEpsilonMs)
            {
                s_reportedDurMs = durMs;
                Post(Input.Duration((int)Math.Min(durMs, int.MaxValue), epoch));
            }

            // (d) the watchdog. `progressed` is deliberately generous: anything that proves the session is alive — a
            // paused open presenting its first frame included.
            bool progressed = s_progressed || s_errorReported || pos > 0
                || state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended or PlaybackState.Failed;
            if (progressed) s_progressed = true;
            if (s_watchdog.ShouldFault(FrameNowMs(), intent, progressed))
            {
                Log.Warn("video", $"start watchdog fired after {s_watchdog.TimeoutMs}ms — state={state} pos={pos} key={Tail(s_key)}");
                ReportFault(epoch, Fault.DrmRequired, "the video session never started playing (no progress within the start budget)");
                return;
            }

            // (e) the state fold, edge-triggered on the last state except the position tick. Playing is an EVENT the
            // session publishes — nothing here re-asserts Play.
            switch (state)
            {
                case PlaybackState.Playing:
                    if (!s_firstFrameFired) { s_firstFrameFired = true; PostSignal(epoch, AudioSignal.Started, pos); }
                    else if (s_lastState == PlaybackState.Playing) PostSignal(epoch, AudioSignal.Position, pos);
                    else PostSignal(epoch, AudioSignal.Started, pos);
                    break;

                case PlaybackState.Ready:
                case PlaybackState.Paused:
                    if (s_lastState != state) PostSignal(epoch, AudioSignal.Paused, pos);
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
        static void RetractLiveWindow()
        {
            if (!s_liveReported) return;
            s_liveReported = false;
            s_reportedLive = default;
            LiveWindow none = LiveWindow.None;
            ReportLiveWindow(in none);
        }

        static bool IsAtOrPastLiveEdge(MediaPlayer p, long ms)
        {
            TimelineInfo tl = p.Timeline.Peek();
            if (!tl.IsLive) return false;
            long edge = (long)tl.SeekableEnd.TotalMilliseconds;
            return edge > 0 && ms >= edge - GoLiveToleranceMs;
        }

        static void PostSignal(uint epoch, AudioSignal signal, long posMs = 0)
            => Post(Input.Audio(signal, epoch, FrameNowMs(), posMs));

        /// <summary>One typed fault per load. A dead DRM source is forgotten by the memo (its signed urls may be what
        /// died), and a dead LOCAL attachment is quarantined by the shell so the reducer's one retry resolves past it.</summary>
        static void ReportFault(uint epoch, Fault kind, string message)
        {
            VideoSource? live;
            string key;
            lock (s_gate)
            {
                if (s_errorReported) return;
                s_errorReported = true;
                live = s_live;
                key = s_key;
            }
            StopTicker();
            Log.Warn("video", $"[video] fault={kind} key={Tail(key)} — {message}");
            if (live is { IsDrm: true }) ManifestMemo.Invalidate(live.Key);
            if (live is { FilePath: not null, PlayableUri.Length: > 0 } local && OnOverrideFailed is { } quarantine)
            {
                string uri = local.PlayableUri, sourceKey = local.OverrideSourceKey ?? "";
                ToUi(() => quarantine(uri, sourceKey));
            }
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

        /// <summary>The last 24 characters of a key. A module playable is 60+ characters of base64 that differs from its
        /// neighbours only in the tail, so a full log line is unreadable and a prefix is useless.</summary>
        static string Tail(string key) => key.Length <= 24 ? key : key[^24..];

        // ── 7. the UI-thread observation: phase, first frame, buffered, seek landed ─────────────────────────────────

        static IMediaSession? s_observedSession;
        static long s_observedFirstFrame;
        static int s_observedIndexEpoch = -1;

        /// <summary>UI THREAD. Read the bound player's session once and mirror what its last pump learnt: the phase, the
        /// first-frame epoch (with the gate's <c>first.frame … sinceSwitchMs</c> line), the index epoch and a landed seek.
        /// Run by the surfaces' host observer whenever the player published a state, position, size or buffering change
        /// — so it is one host frame behind the engine, never a timer behind it. No allocation on the steady path; every
        /// write is value-gated.</summary>
        public static void Observe()
        {
            MediaPlayer? p = Player.Peek().Player;
            if (p is null)
            {
                if (Phase.Peek() is not (SwitchPhase.Resolving or SwitchPhase.Buffering)) Phase.SetIfChanged(SwitchPhase.Idle);
                return;
            }
            IMediaSession? session = p.Session;
            if (!ReferenceEquals(session, s_observedSession))
            {
                s_observedSession = session;
                s_observedFirstFrame = 0;
                s_observedIndexEpoch = -1;
            }
            if (session is null) { Phase.SetIfChanged(SwitchPhase.Attaching); return; }

            long firstFrame;
            if (session is ProtectedMediaSession ps)
            {
                IProtectedVideoPlayer pv = ps.Player;
                Phase.SetIfChanged(HostRules.PhaseOf(pv.Phase));
                firstFrame = pv.FirstFrameEpoch;
                int index = pv.IndexEpoch;
                if (index != s_observedIndexEpoch)
                {
                    s_observedIndexEpoch = index;
                    Buffered.Value = Buffered.Peek() + 1;
                }
                if (s_seekTargetMs >= 0 && !pv.IsSeeking && pv.LastSeekLandedMs >= 0)
                {
                    LogLine(new VideoLog.SeekDone(s_seekTargetMs, pv.LastSeekLandedMs, FrameNowMs() - s_seekIssuedAtMs, s_seekFetched));
                    s_seekTargetMs = -1;
                }
            }
            else
            {
                bool framed = !p.NaturalSize.Peek().IsEmpty;
                Phase.SetIfChanged(HostRules.PhaseOfClear(p.State.Peek(), framed));
                firstFrame = framed ? 1 : 0;
            }

            if (firstFrame == 0 || firstFrame == s_observedFirstFrame) return;
            s_observedFirstFrame = firstFrame;
            FirstFrame.Value = FirstFrame.Peek() + 1;
            SizeI natural = p.NaturalSize.Peek();
            LogLine(new VideoLog.FirstFrame(Tail(s_key), s_epoch, FrameNowMs() - Interlocked.Read(ref s_switchAtMs), -1,
                (long)p.Position.Peek().TotalMilliseconds, natural.Width, natural.Height));
        }

        // the always-on lines, formatted by `VideoLog` (one shape for the host and the gate) into a per-thread buffer
        [ThreadStatic] static char[]? t_line;

        static void LogLine(in VideoLog.SwitchBegin l) => Emit(VideoLog.Format(in l, Line()));
        static void LogLine(in VideoLog.FirstFrame l) => Emit(VideoLog.Format(in l, Line()));
        static void LogLine(in VideoLog.SeekPlanned l) => Emit(VideoLog.Format(in l, Line()));
        static void LogLine(in VideoLog.SeekDone l) => Emit(VideoLog.Format(in l, Line()));
        static void LogLine(in VideoLog.PrefetchPlanned l) => Emit(VideoLog.Format(in l, Line()));

        static char[] Line() => t_line ??= new char[VideoLog.MaxLineChars];

        static void Emit(int length)
        {
            if (length > 0 && t_line is { } buffer) Log.Info("video", new string(buffer, 0, length));
        }

        // ── 8. the detached-window seam (owner K attaches) ──────────────────────────────────────────────────────────

        /// <summary>How a pop-out window is opened. The engine owns `IDetachedVideoWindow`; owner K's `Shell/Video.Host.cs`
        /// is its ONLY caller and sets this so `Playback.Video` never names a window type.</summary>
        public static Func<bool>? CanOpenDetachedWindow { get; set; }

        /// <summary>True when a second window could host the picture. The placement menu disables the rung with its reason
        /// rather than hiding it, so this must answer even before a host attaches.</summary>
        public static bool DetachedAvailable => CanOpenDetachedWindow?.Invoke() ?? false;
    }
}
