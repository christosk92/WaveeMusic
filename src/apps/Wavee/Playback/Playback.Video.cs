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
//   2. THE PLAYER PUMPS ITS OWN STATE (F132). A session's state, position, duration, `NaturalSize` and errors are published
//      by the `MediaPlayer` itself, one coalesced UI post per engine event, with or without a mounted element; the
//      element's `IMediaPlayer.PumpVideo` only binds and places the surface. This host builds and reports; it never
//      pumps, and it WATCHES the heartbeat: `MediaPlayer.StatePumpAgeMs` going stale while a session plays is logged
//      (`HostRules.StatePumpStalled`), because a frozen playhead with no error is otherwise indistinguishable from a
//      stalled decode.
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

using System.Linq;

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
        /// instance changes, so a source switch never remounts and never tears down the pump. F152: the player is built once
        /// (and again only for a fault that cannot re-open in place) and stays bound while idle — a stop closes its
        /// SESSION, not the binding — so <see cref="Player"/> being non-null no longer means "a video is loaded".</summary>
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
            /// <summary>No player, or the live one faulted and cannot be re-opened: tear down and build.</summary>
            Rebuild,
        }

        /// <summary>The facts the switch decision needs. A record struct so a test is one line.</summary>
        /// <param name="FaultRetryable">Meaningful only with <paramref name="Faulted"/>: the fault was a transient one the
        /// SAME player can recover from with one re-open (<see cref="HostRules.IsReopenable"/>, budget unspent).</param>
        public readonly record struct SwitchInput(bool HasPlayer, bool Faulted, string LiveKey, string RequestKey, long StartAtMs,
            bool FaultRetryable = false);

        /// <summary>WHY <see cref="Plan"/> chose what it chose — the `why=` of `[video] switch.begin`. Two facts
        /// (<c>!HasPlayer</c>, <c>Faulted</c>) both drive <see cref="SwitchAction.Rebuild"/>, and a Rebuild pays the
        /// full cold-open cost either way, so the log line collapsing them into one `plan=Rebuild` hid which of "no
        /// player at all" and "the live one died" actually happened.</summary>
        public enum SwitchReason : byte
        {
            /// <summary><see cref="SwitchAction.None"/>: same key, same position, healthy player. Nothing to explain.</summary>
            None,
            /// <summary>No player is bound at all (cold start, or torn down since).</summary>
            NoPlayer,
            /// <summary>A player is bound but the live one faulted: a Rebuild, or (<see cref="SwitchInput.FaultRetryable"/>)
            /// one in-place re-open of the same key.</summary>
            Faulted,
            /// <summary>Healthy player, different content key: re-open in place.</summary>
            KeyChanged,
            /// <summary>Same key, a carried start position: one seek, no open.</summary>
            SeekOnly,
        }

        /// <summary>Ported from 0.2.9's `VideoSwitchPolicy`. A faulted player is rebuilt unless its fault was transient
        /// (one in-place re-open on the same player, F163), and "same key, same position" never re-opens — a
        /// docked→fullscreen placement move re-asks for the video already playing.</summary>
        public static SwitchAction Plan(in SwitchInput i) => PlanWithReason(in i, out _);

        /// <summary>Same law as <see cref="Plan"/>, plus WHY — the two independent <see cref="SwitchAction.Rebuild"/>
        /// causes (no player vs. faulted) are told apart here even though they choose the same action.</summary>
        public static SwitchAction PlanWithReason(in SwitchInput i, out SwitchReason reason)
        {
            if (!i.HasPlayer) { reason = SwitchReason.NoPlayer; return SwitchAction.Rebuild; }
            if (i.Faulted && !i.FaultRetryable) { reason = SwitchReason.Faulted; return SwitchAction.Rebuild; }
            if (!string.Equals(i.LiveKey, i.RequestKey, StringComparison.Ordinal))
            {
                reason = SwitchReason.KeyChanged;
                return SwitchAction.Switch;
            }
            if (i.Faulted) { reason = SwitchReason.Faulted; return SwitchAction.Switch; }   // the same key again, in place
            if (i.StartAtMs > 0) { reason = SwitchReason.SeekOnly; return SwitchAction.SeekOnly; }
            reason = SwitchReason.None;
            return SwitchAction.None;
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
            /// <summary>F215: `first.frame`'s time since the switch began. When the player stamped the first frame itself
            /// (<paramref name="firstFrameQpc"/>, a Stopwatch timestamp, 0 = none) and the switch has its own
            /// (<paramref name="switchAtQpc"/>), it is the time between those two stamps, so a late UI observation (a stage that unmounted the
            /// pump, a busy frame) cannot inflate it; otherwise it is the tick-clock difference <c>now - switchAtMs</c>, the old figure.</summary>
            public static long FirstFrameSinceSwitchMs(long switchAtMs, long switchAtQpc, long firstFrameQpc, long nowMs)
                => switchAtQpc != 0 && firstFrameQpc >= switchAtQpc
                    ? (long)System.Diagnostics.Stopwatch.GetElapsedTime(switchAtQpc, firstFrameQpc).TotalMilliseconds
                    : nowMs - switchAtMs;

            /// <summary>F215: how long after the player's own first-frame stamp this observation ran, in ms; -1 when there is no stamp.</summary>
            public static long ObservedLateMs(long firstFrameQpc, long nowQpc)
                => firstFrameQpc == 0 || nowQpc < firstFrameQpc ? -1
                   : (long)System.Diagnostics.Stopwatch.GetElapsedTime(firstFrameQpc, nowQpc).TotalMilliseconds;

            /// <summary>A carried position within this of the end is pulled back…</summary>
            public const long StartClampGuardMs = 250;
            /// <summary>…to this far before the end, so a restore never opens on the credits.</summary>
            public const long StartClampBackoffMs = 2_000;

            /// <summary>How long a PLAYING clear session may go without a state pump before the host says so (F179). The clear
            /// engine raises on its position tick, about once a second while playing, so three seconds is two missed ticks and a
            /// margin: a control plane nobody is driving, never a quiet source.</summary>
            public const long StatePumpStallMs = 3_000;

            /// <summary>The same budget for a PROTECTED session. Its position samples are deliberately not pump triggers (the
            /// snapshot carries the timestamped sample), so a steady playing session is pumped by buffer and key events, about
            /// one a segment: a budget of several segments keeps a healthy session quiet while still catching the case this
            /// guards against, a session with no pump at all.</summary>
            public const long ProtectedStatePumpStallMs = 15_000;

            /// <summary>Is a session that reads <paramref name="state"/> overdue for a state pump (<paramref name="idleMs"/> since
            /// the last)? Only a PLAYING session is expected to raise on a cadence: a paused, opening or ended one is quiet by
            /// design. The state it reads is the player's own, so a stalled pump is exactly when it still says Playing.</summary>
            public static bool StatePumpStalled(PlaybackState state, long idleMs, bool protectedSession)
                => state == PlaybackState.Playing && idleMs > (protectedSession ? ProtectedStatePumpStallMs : StatePumpStallMs);

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

            /// <summary>Has the seek the reducer is owed a <c>Seeked</c> for landed (V-PA2, V-PA12)? A seek the engine took
            /// lands when the engine stops seeking; a <see cref="SeekVerb.Ride"/> made no engine call, so it lands when
            /// playback reaches the target. Pure: the host's observer reads the facts, this decides.</summary>
            public static bool SeekLanded(bool engineSeeking, bool rode, long positionMs, long targetMs)
                => rode ? positionMs >= targetMs : !engineSeeking;

            /// <summary>The ABR height ceiling: the user's pin (0 = auto) and the metered cap (0 or `int.MaxValue` = none),
            /// the lower of the two.</summary>
            public static int QualityCap(int pinnedHeight, int meteredCap)
            {
                int pin = pinnedHeight > 0 ? pinnedHeight : int.MaxValue;
                int metered = meteredCap > 0 ? meteredCap : int.MaxValue;
                return pin < metered ? pin : metered;
            }

            // ── the fault ladder (F157, F163): what a fault means for the NEXT load ───────────────────────────────────

            // Hardware-context-reset class HRESULTs (Chromium's PIPELINE_ERROR_HARDWARE_CONTEXT_RESET set, plus a lost
            // D3D device and a shut-down engine): the session is dead, the SOURCE is fine, so the answer is a re-open.
            const uint HrTeeInvalidHwDrmState = 0x8004CD12;   // DRM_E_TEE_INVALID_HWDRM_STATE
            const uint HrAsdActiveDisplayFail = 0x8004DD2E;   // DRM_OEM_E_ASD_ACTIVE_DISPLAY_FAIL
            const uint HrDxgiDeviceRemoved = 0x887A0005;      // DXGI_ERROR_DEVICE_REMOVED
            const uint HrDxgiDeviceReset = 0x887A0007;        // DXGI_ERROR_DEVICE_RESET
            const uint HrMfShutdown = 0xC00D3E85;             // MF_E_SHUTDOWN

            // A signed url that expired or was revoked: a bare HTTP status, or its HRESULT_FROM_WIN32 form.
            const uint HrHttpForbidden = 0x80190193, HrHttpNotFound = 0x80190194, HrHttpGone = 0x8019019A;

            static uint Hr(long? code) => code is { } c ? unchecked((uint)c) : 0u;

            /// <summary>Does this raw code mean the device/CDM context was reset under a healthy source?</summary>
            public static bool IsResetClass(long? code)
                => Hr(code) is HrTeeInvalidHwDrmState or HrAsdActiveDisplayFail or HrDxgiDeviceRemoved or HrDxgiDeviceReset or HrMfShutdown;

            /// <summary>Does this raw code point at a signed url (403 / 404 / 410) — the one error a re-fetched manifest cures?</summary>
            public static bool IsSignedUrlError(long? code)
                => Hr(code) is 403u or 404u or 410u or HrHttpForbidden or HrHttpNotFound or HrHttpGone;

            /// <summary>Can the SAME player recover from this error with one re-open? A transient fault (the engine says
            /// <see cref="MediaRecovery.Retryable"/>: a decode hiccup, an aborted open, a hardware reset) or a network
            /// blip (a segment GET that failed) — never a licence, codec or lifecycle verdict.</summary>
            public static bool IsReopenable(MediaError? err)
                => err is { Recovery: MediaRecovery.Retryable } or { Category: MediaErrorCategory.Network, Recovery: MediaRecovery.NeedsNetwork };

            /// <summary>The re-open budget: ONE in-place re-open per key until playback makes progress again, so a source
            /// that is broken for real falls through to the rebuild (and the reducer's demotion) instead of looping.</summary>
            public static bool MayReopenInPlace(bool reopenable, string liveKey, string? reopenedKey)
                => reopenable && !string.Equals(liveKey, reopenedKey, StringComparison.Ordinal);

            /// <summary>Forget the DRM manifest the fault came from? A signed-url error always (a re-fetch is the cure);
            /// an error that is NOT about to be re-opened in place too (the plain re-open already failed, or the error is
            /// not transient, so the manifest is the suspect). A transient first fault keeps it: the re-open needs no api GET.</summary>
            public static bool InvalidateManifest(bool isDrm, long? code, bool reopenNow)
                => isDrm && (IsSignedUrlError(code) || !reopenNow);

            /// <summary>The reducer's fault for an engine error. A hardware reset is the engine's problem and never the
            /// row's: it is <see cref="Fault.Unknown"/> (not terminal, never <see cref="Fault.DrmRequired"/>).</summary>
            public static Fault MapError(MediaError err)
            {
                if (err.Recovery == MediaRecovery.Retryable && IsResetClass(err.UnderlyingCode)) return Fault.Unknown;
                return err.Category switch
                {
                    MediaErrorCategory.Network => Fault.Network,
                    MediaErrorCategory.Drm => Fault.DrmRequired,
                    MediaErrorCategory.Decode or MediaErrorCategory.UnsupportedCodec => Fault.DecodeFailed,
                    MediaErrorCategory.Source => Fault.Unavailable,
                    _ => Fault.Unknown,
                };
            }

            /// <summary>The fault for a start that never made progress. A protected start is named by where it stuck, the
            /// same rule the engine's own CANPLAY deadline uses (<see cref="ProtectedMediaSession.StartFailureCategory"/>):
            /// a store that never received media is a network fault, a pending licence or decoder a protected-path one. A
            /// clear source has no licence to blame.</summary>
            public static Fault WatchdogFault(bool drm, ProtectedVideoPhase phase)
                => !drm || ProtectedMediaSession.StartFailureCategory(phase) == MediaErrorCategory.Network
                    ? Fault.Network
                    : Fault.DrmRequired;

            // ── the tick's stamp (F158): the ticker only believes the session THIS load opened ───────────────────────

            /// <summary>May the tick fold the player's state and position? Only once the player holds a session that is not
            /// the one that was live when this load began: until the new session is assigned the player still reads as the
            /// PREVIOUS video (its state, its position), and folding that posts a `Started` for the new epoch at the old
            /// row's position and disarms the start watchdog.</summary>
            public static bool OwnsTick(object? session, object? baselineSession)
                => session is not null && !ReferenceEquals(session, baselineSession);

            /// <summary>Is this error this load's own? The previous load's error object stays on the player until the new
            /// open's first hop clears it; reading it again would fault the retry before it began.</summary>
            public static bool IsFreshError(MediaError? err, MediaError? baselineError)
                => err is not null && !ReferenceEquals(err, baselineError);

            /// <summary>Has the load proven it is alive? Sticky once true. State and position count only when the tick owns
            /// the session — the previous video's position must never read as this one's progress.</summary>
            public static bool Progressed(bool already, bool ownsTick, long positionMs, PlaybackState state)
                => already || (ownsTick && (positionMs > 0
                    || state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended or PlaybackState.Failed));

            /// <summary>What a tick posts for the player's state, edge-triggered on <paramref name="last"/> except the
            /// position tick. Playing is an EVENT the session publishes — nothing here re-asserts Play.</summary>
            public static TickFold Fold(PlaybackState state, PlaybackState last, bool firstFrameFired) => state switch
            {
                PlaybackState.Playing => !firstFrameFired ? TickFold.Started : last == PlaybackState.Playing ? TickFold.Position : TickFold.Started,
                PlaybackState.Ready or PlaybackState.Paused => last != state ? TickFold.Paused : TickFold.None,
                PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Stalled => last != state ? TickFold.Buffering : TickFold.None,
                PlaybackState.Ended => last == PlaybackState.Ended ? TickFold.None : TickFold.Ended,
                PlaybackState.Failed => TickFold.Failed,
                _ => TickFold.None,
            };
        }

        /// <summary>What one tick posts for the player's state (<see cref="HostRules.Fold"/>).</summary>
        public enum TickFold : byte { None, Started, Position, Paused, Buffering, Ended, Failed }

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
        static MfMediaPlayer? s_mf;
        static Timer? s_ticker;
        static bool s_tickerOn;                  // the ticker is wanted (under s_gate): the one-shot timer re-arms only while true
        static int s_tickRunning;                // 1 while a tick body runs (Interlocked): ticks never overlap
        static bool s_statePumpStallWarned;      // the stall warning fired for this episode (tick-only: ticks never overlap)
        static bool s_disposed;

        // A1/A2 (video stutter fix, 2026-09-22): the process-lifetime ABR controller survives a player rebuild. The
        // cap it ran under (and the viewport-clamped effective one, carried across the rebuild so the opening pick never
        // trusts a stale small-pop-out ceiling), plus the last-persisted link estimate and when it was written.
        static int s_policyCap = int.MaxValue, s_lastViewportCap = int.MaxValue;
        static double s_linkPersisted;
        static long s_linkWriteMs;

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
        // F158: what the player held when THIS load began (ResetPerLoad). Its session and error belong to the previous
        // load until the open replaces them, so the tick folds neither (HostRules.OwnsTick / IsFreshError).
        static IMediaSession? s_baseSession;
        static MediaError? s_baseError;
        // F163: the fault just reported is one the same player can re-open from (HostRules.MayReopenInPlace, decided at
        // the fault), and the key a faulted in-place re-open is already spent on (cleared by progress or any other load).
        static bool s_faultRetryable;
        static string? s_reopenedKey;
        static double s_volume = 1.0;
        static bool s_muted;
        static long s_switchAtMs;                // FrameNowMs at switch.begin — first.frame's sinceSwitchMs
        static long s_switchAtQpc;               // F215: the same instant as a Stopwatch (QPC) timestamp: the origin the NATIVE first-frame stamp is measured from
        // V-PA2: the reducer's seek generation (`State.SeekGen`) every `Position` this host posts is stamped with (`Input.Gen`): the
        // reducer drops a report whose generation is not its current one, so a position read before the user's last drop point can
        // never drag the playhead back over it. SEEDED by every `Load` (the reducer's generation when the load starts), STAMPED by
        // `Seek` AFTER its engine call (a tick that reads the new generation then also reads the seeked position, never the
        // pre-seek one). 0 = never stamped. The UI thread writes it, the tick thread reads it: under `s_gate`.
        static uint s_videoSeekGen;
        // The scrub gesture's PREVIEW (D4): audio is muted for its duration (a keyframe preview restarts playback from the keyframe, so an
        // unmuted drag stutters snippets), and previews are rate-limited. `s_previewMuted` is the gesture's mute, layered over the user's
        // own `s_muted` (the gesture never overwrites it); written under `s_gate`. `s_previewAtMs` is UI-thread only.
        static bool s_previewMuted;
        static long s_previewAtMs = long.MinValue / 2;
        // F151 (park and cut): while a video ATTACHES under a song that is still playing, its player is held MUTED (`s_attachMuted`,
        // layered over the user's own mute exactly like the scrub's) and its transport reports are held back (`s_holdReports`: it
        // posts no Started/Position/Paused/Buffering, the song owns the deck, but every fault and the end still go through). The
        // tick also judges the window (`MediaSwitch.JudgeAttach`): a presented frame asks `ParkAndCut` for the cut. All under s_gate.
        static bool s_attachMuted, s_holdReports, s_attachReadyPosted, s_prerolled;
        static long s_holdSinceMs;
        // F151: the cut's one-shot gap probe. `ParkAndCut.Cut` arms it when the video is started (wall time, the cut position and
        // the song's position then); the first tick after the release that sees the video PLAYING past the cut position measures
        // the silence and logs `[video] audio.cut`. A seek publishes its target as the position at once, so the gap cannot be
        // read at the cut itself. Disarmed by every Load, Stop and Pause. Under s_gate.
        static bool s_cutProbeArmed;
        static long s_cutProbeAtMs, s_cutProbePcMs, s_cutProbeSongPosMs;

        // the pump
        readonly record struct LoadRequest(VideoSource Source, long StartAtMs, uint Epoch);
        static LoadRequest? s_pending;
        static bool s_pendingClear, s_running;
        static long s_pumpEpoch;
        static Task s_worker = Task.CompletedTask;
        // F155: ONE cancellation source per pump epoch. Load and Stop cancel the one they replace; the worker hands the token of
        // the epoch it dequeued to the prepare adopt and the open, so a superseded request stops waiting and stops opening.
        static CancellationTokenSource s_loadCts = new();
        // F155: the reducer's newest load epoch, published by the host the moment the reducer starts a load (`NoteLoadEpoch`).
        // The pump epoch only moves once that load's resolve lands and `Load` runs, so between the two a request for the
        // previous epoch is still "current" to the pump; this is what tells the worker it is already superseded.
        static uint s_latestLoadEpoch;

        /// <summary>The process-lifetime protected backend. Built on first use; its prepared-session table must outlive a
        /// player rebuild, or a prefetch is thrown away by the very switch it was for.</summary>
        static ProtectedMediaBackend Backend
        {
            get { lock (s_gate) return s_backend ??= new ProtectedMediaBackend(License.ByKeyId, descriptor: null); }
        }

        /// <summary>F190: the process-lifetime MF backend, held next to <see cref="Backend"/> for the same reason. A clear
        /// source (a module, a local file) leases the WARM engine from it, and a backend built per player threw that engine
        /// away with the player: every rebuild paid MFStartup + a D3D11 video device + an IMFMediaEngine cold, while the old
        /// backend's engine stayed resident until its own 30 s idle timer. A DRM source routes through <see cref="Backend"/>
        /// and never leases an engine. Disposed once, at <see cref="Shutdown"/>.</summary>
        static MfMediaPlayer ClearBackend
        {
            get { lock (s_gate) return s_mf ??= new MfMediaPlayer(Backend); }
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
        /// lines into the app's one log — the §4.3 gate reads them from the same file as the app's own lines — and pin the
        /// protected runtime's D3D11 video device to the renderer's adapter, as the clear path's engine already does.</summary>
        public static void InstallLog()
        {
            ProtectedVideoRuntime.LogSink = static line => Log.Info("video", line);
            ProtectedVideoRuntime.AdapterLuidProvider = static () => global::FluentGpu.Rhi.D3D12.GpuAdapterInfo.CurrentAdapterLuid;
        }

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

        /// <summary>The reducer started a load for <paramref name="epoch"/> (any kind of host; UI thread, before its resolve):
        /// every pump request for an older epoch is superseded from here on, whether or not its own <see cref="Load"/> /
        /// <see cref="Stop"/> has reached the pump yet (F155).</summary>
        public static void NoteLoadEpoch(uint epoch) => Volatile.Write(ref s_latestLoadEpoch, epoch);

        /// <summary>Play <paramref name="source"/> from <paramref name="fromMs"/>, for reducer epoch <paramref name="epoch"/>.
        /// UI thread. Returns immediately: the physical work is one coalesced request on the pump, and a request that
        /// arrives while another is in flight REPLACES it. <paramref name="paused"/> opens paused at the position; a
        /// <see cref="Pause"/> or <see cref="Play"/> issued before the open lands is honoured by it.
        /// <paramref name="seekGen"/> is the reducer's seek generation (<c>State.SeekGen</c>) at the moment the load starts: the
        /// <c>Position</c> reports of this load carry it (V-PA2), so the first one is already current.</summary>
        public static void Load(VideoSource source, uint epoch, int fromMs, bool paused, uint seekGen, bool muted = false)
        {
            Boot();
            float rate = RateFor(s_state.CurrentId, s_state.VideoWanted);
            s_owed = default;                    // a new load owes the old one's seek nothing
            CancellationTokenSource superseded;
            MediaPlayer? bound;
            lock (s_gate)
            {
                s_previewMuted = false;          // a scrub's muted preview belongs to the player the load replaces
                s_attachMuted = muted;           // F151: an attach under a playing song opens silent; every other load says so too, so a stale mute never outlives its load
                s_cutProbeArmed = false;
                bound = s_player;
                s_desiredRate = rate;
                s_videoSeekGen = seekGen;        // V-PA2: seeded from EVERY load, so its first report is current
                s_pumpEpoch++;
                superseded = s_loadCts;          // F155: the request this replaces stops waiting and stops opening
                s_loadCts = new CancellationTokenSource();
                s_pending = new LoadRequest(source, Math.Max(0, fromMs), epoch);
                s_pendingClear = false;
                s_intentPaused = paused;
                // THE CLAIM (§6.2 G1 rule 1), taken on the UI thread before any of the physical work: from this line on
                // the badge-lit prefetch leaves this row alone, because its load is the fetch.
                s_claim = new RowKey(source.PlayableUri, source.Key);
                EnsureWorker();
            }
            CancelSuperseded(superseded);        // outside the lock; the callbacks run on the pool, never inline on this thread
            ApplyEffectiveMute(bound);           // the process-lifetime player outlives its loads: its mute follows THIS load's intent (UI thread, F185)
            if (Phase.Peek() is SwitchPhase.Idle or SwitchPhase.Failed or SwitchPhase.Playing or SwitchPhase.Presenting)
                Phase.SetIfChanged(SwitchPhase.Buffering);
        }

        /// <summary>Stop and release. A clear INVALIDATES and overtakes a queued load.</summary>
        public static void Stop()
        {
            s_owed = default;
            CancellationTokenSource superseded;
            lock (s_gate)
            {
                s_previewMuted = false;
                s_attachMuted = false;           // F151: the attach window and its mute die with the load they belonged to
                s_holdReports = false;
                s_cutProbeArmed = false;
                s_pumpEpoch++;
                superseded = s_loadCts;          // F155: a clear overtakes the load in flight as well as the queued one
                s_loadCts = new CancellationTokenSource();
                s_pending = null;
                s_pendingClear = true;
                s_claim = RowKey.None;
                EnsureWorker();
            }
            CancelSuperseded(superseded);
        }

        static void CancelSuperseded(CancellationTokenSource superseded)
        {
            // CancelAsync flags the token synchronously (every `ct.IsCancellationRequested` filter holds at once) but runs the
            // callbacks, and so the worker's WaitAsync continuations, on the pool: Cancel() would resume the worker inline
            // here, on the UI thread, and let it start the NEXT request's BuildPlayer / session dispose inside Load/Stop.
            try { _ = superseded.CancelAsync(); }
            catch (Exception ex) { Log.Warn("video", "cancelling the superseded load failed", ex); }
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
            lock (s_gate) { s_playIntent = false; s_intentPaused = true; s_cutProbeArmed = false; p = s_player; }
            if (p is null) return;
            try { _ = p.PauseAsync(); } catch (Exception ex) { Log.Warn("video", "pause failed", ex); }
            StopTicker();
        }

        /// <summary>The video half of the audio cut (§3.1.4, <c>ParkAndCut.Cut</c>): one exact seek to <paramref name="atMs"/>
        /// inside the prepared window, then play. UI thread.</summary>
        public static void Go(long atMs)
        {
            Seek(atMs, accurate: true);
            Play();
        }

        /// <summary>F151: hold (or release) the attach window's report hold and restart its clock. While held the tick posts no
        /// transport state and judges the window. UI thread.</summary>
        public static void HoldReports(bool hold)
        {
            lock (s_gate)
            {
                s_holdReports = hold;
                s_holdSinceMs = hold ? FrameNowMs() : 0;
                s_attachReadyPosted = false;
                s_prerolled = false;
            }
            if (hold) StartTicker();             // a paused load never starts it, and the window is judged on the tick (no player yet: the tick stops itself)
        }

        /// <summary>F151: the open of THIS load begins, so the attach window is judged from here, on this epoch: a verdict the tick
        /// reached for the load before (a frame presented by the paused session this one replaces) must not stand. Under s_gate.</summary>
        static void RestartAttachWindow()
        {
            if (!s_holdReports) return;
            s_holdSinceMs = FrameNowMs();
            s_attachReadyPosted = false;
            s_prerolled = false;
        }

        /// <summary>F151: the attach is over (the cut happened): the player returns to the USER's mute. UI thread.</summary>
        public static void ReleaseAttachMute()
        {
            MediaPlayer? p;
            lock (s_gate) { s_attachMuted = false; p = s_player; }
            ApplyEffectiveMute(p);
        }

        /// <summary>F151: silence the player NOW, because the video is leaving while the song resumes and two decoders must never
        /// both be audible. The next <see cref="Load"/> restores the intent. UI thread.</summary>
        public static void Silence()
        {
            MediaPlayer? p;
            lock (s_gate) { s_attachMuted = true; p = s_player; }
            ApplyEffectiveMute(p);
        }

        /// <summary>F151: stamp the reports from here on with <paramref name="gen"/>, the reducer's CURRENT seek generation. A seek
        /// the listener made while the song played moved it, and a position stamped with the generation this load began on would be
        /// dropped for the rest of the video.</summary>
        public static void SyncSeekGen(uint gen)
        {
            lock (s_gate) s_videoSeekGen = gen;
        }

        /// <summary>F151: arm the cut's gap probe, just before the video is started at <paramref name="pcMs"/> while the song was
        /// at <paramref name="songPosMs"/>. The first tick that sees the video playing past <paramref name="pcMs"/> logs the
        /// measured gap (<see cref="LogAudioCut"/>). UI thread.</summary>
        public static void ArmCutProbe(long pcMs, long songPosMs)
        {
            lock (s_gate)
            {
                s_cutProbeArmed = true;
                s_cutProbeAtMs = FrameNowMs();
                s_cutProbePcMs = pcMs;
                s_cutProbeSongPosMs = songPosMs;
            }
        }

        /// <summary>The `[video] audio.cut` line (<see cref="VideoLog.AudioCut"/>): what the gate reads against
        /// <see cref="AudioHandoff.IsCut"/>, with the warning when the measured gap is audible.</summary>
        static void LogAudioCut(long songPosMs, long videoPosMs, long gapMs)
        {
            Emit(VideoLog.Format(new VideoLog.AudioCut(AudioHandoff.FadeMs, songPosMs, videoPosMs, gapMs), Line()));
            if (!AudioHandoff.IsCut(gapMs)) Log.Warn("video", $"[video] audio.cut left a {gapMs}ms gap (budget {AudioHandoff.CutToleranceMs}ms)");
        }

        /// <summary>The player's mute is the user's, the scrub's and the attach's, layered. UI thread.</summary>
        static void ApplyEffectiveMute(MediaPlayer? p)
        {
            if (p is null) return;
            bool effective;
            lock (s_gate) effective = s_muted || s_previewMuted || s_attachMuted;
            try { p.SetMuted(effective); } catch { /* fail-soft */ }
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
        /// down; a commit decodes to the target, or rides when playback is about to reach it anyway.
        /// <para><paramref name="gen"/> is the reducer's seek generation (<c>State.SeekGen</c>) for a seek IT emitted (V-PA2); 0 is
        /// an internal seek (a scrub preview, a same-key reload's start position, <see cref="Go"/>) and changes neither what the
        /// <c>Position</c> posts are stamped with nor what the reducer is owed. A reducer seek stamps the posts from here on
        /// EVEN WHEN there was no player to take it (otherwise every later report would carry an older generation and be
        /// dropped for the rest of the load), and owes the reducer one <c>Seeked</c> for that generation, posted when the
        /// seek lands (<see cref="Observe"/>) — the release of the seek bar's drop-point hold (V-PA12).</para></summary>
        public static void Seek(long ms, bool accurate = true, uint gen = 0)
        {
            if (accurate) EndPreviewMute();                  // the commit of a scrub (or any real seek) ends the gesture's silence
            SeekOutcome outcome = IssueSeek(ms, accurate);
            if (gen == 0) return;
            uint epoch;
            // AFTER the engine call: it publishes the target as the position at once, so a tick that reads the new generation reads
            // the seeked position with it. Stamped first, the same tick could post the PRE-seek position under the NEW generation
            // and the reducer would accept it — walking the playhead back over the drop point.
            lock (s_gate) { s_videoSeekGen = gen; epoch = s_epoch; }
            s_owed = outcome.Issued ? new OwedSeek(gen, outcome.TargetMs, outcome.Rode, epoch) : default;
        }

        /// <summary>The fastest a scrub preview seeks: ≤ 10 Hz (D4). The reducer's model already coalesces a drag to 20 Hz; a keyframe
        /// preview costs a decode, and a slower cadence is what keeps the bar responsive over it.</summary>
        public const long ScrubPreviewIntervalMs = 100;

        /// <summary>One scrub PREVIEW while the pointer drags over the bar (<c>Effects.ScrubMove</c> on the video host): audio muted for
        /// the whole gesture (first call), then a keyframe-mode seek to the nearest buffered keyframe through the planner
        /// (<see cref="SeekIntent.Preview"/>: it never fetches while the pointer is down), at most one per
        /// <see cref="ScrubPreviewIntervalMs"/>. A preview is an INTERNAL seek (<c>gen == 0</c>): it changes neither what the
        /// <c>Position</c> posts are stamped with nor what the reducer is owed — the release's accurate <see cref="Seek"/> is the one
        /// the reducer minted a generation for, and it unmutes. UI thread.</summary>
        public static void ScrubPreview(long ms)
        {
            MediaPlayer? p;
            bool firstOfGesture;
            lock (s_gate) { p = s_player; firstOfGesture = !s_previewMuted; s_previewMuted = true; }
            if (firstOfGesture) { try { p?.SetMuted(true); } catch { /* fail-soft */ } }
            long now = FrameNowMs();
            if (now - s_previewAtMs < ScrubPreviewIntervalMs) return;
            s_previewAtMs = now;
            Seek(ms, accurate: false);
        }

        /// <summary>The gesture was cancelled (<c>Effects.ScrubCancel</c> on the video host): the audio comes back; the picture stays on the
        /// last previewed keyframe, which is where the playhead really is. UI thread.</summary>
        public static void ScrubPreviewEnd() => EndPreviewMute();

        /// <summary>End the gesture's mute: the player returns to the USER's mute setting, never unconditionally to audible.</summary>
        static void EndPreviewMute()
        {
            MediaPlayer? p;
            bool userMuted;
            lock (s_gate)
            {
                if (!s_previewMuted) return;
                s_previewMuted = false;
                p = s_player;
                userMuted = s_muted || s_attachMuted;
            }
            try { p?.SetMuted(userMuted); } catch { /* fail-soft */ }
        }

        /// <summary>What <see cref="IssueSeek"/> did. <c>Issued</c> false: nothing took the seek (no session, or it threw).
        /// <c>Rode</c>: no engine call was made, playback reaches <c>TargetMs</c> on its own.</summary>
        readonly record struct SeekOutcome(bool Issued, bool Rode, long TargetMs);

        /// <summary>The <c>Seeked</c> the reducer is still owed for the last seek it emitted (V-PA2): its generation, where it
        /// aims, whether it was a ride, and the load epoch of the session that took it. <c>Gen</c> 0 = nothing owed.
        /// UI thread only (written by <see cref="Seek"/>, <see cref="Load"/>, <see cref="Stop"/>, settled by
        /// <see cref="Observe"/>).</summary>
        readonly record struct OwedSeek(uint Gen, long TargetMs, bool Rode, uint Epoch);

        static OwedSeek s_owed;

        static SeekOutcome IssueSeek(long ms, bool accurate)
        {
            MediaPlayer? p;
            VideoSource? live;
            lock (s_gate) { p = s_player; live = s_live; }
            if (p is null) return default;
            long target = Math.Max(0, ms);
            bool engaged = p.Session is not null;            // a player still attaching has no session to take the seek
            try
            {
                if (accurate && IsAtOrPastLiveEdge(p, target)) { _ = p.GoLiveAsync(); return new SeekOutcome(engaged, false, target); }
                if (p.Session is not ProtectedMediaSession ps)
                {
                    _ = p.SeekAsync(TimeSpan.FromMilliseconds(target), accurate ? SeekMode.Accurate : SeekMode.Keyframe);
                    return new SeekOutcome(engaged, false, target);
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
                    return new SeekOutcome(true, true, target);
                }
                s_seekTargetMs = call.TargetMs;
                s_seekIssuedAtMs = FrameNowMs();
                s_seekFetched = plan.Verb == SeekVerb.Fetch;
                _ = ps.SeekAsync(TimeSpan.FromMilliseconds(call.TargetMs), call.Accurate ? SeekMode.Accurate : SeekMode.Keyframe,
                    call.KeyframeHintMs);
                return new SeekOutcome(true, false, call.TargetMs);
            }
            catch (Exception ex) { Log.Warn("video", "seek failed", ex); return default; }
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
            bool effective;
            lock (s_gate) { s_muted = muted; p = s_player; effective = muted || s_previewMuted || s_attachMuted; }   // a scrub's (or an attach's) silence outlives an unmute click
            try { p?.SetMuted(effective); } catch { /* fail-soft */ }
        }

        /// <summary>THE writer of the video quality pin: persist <see cref="Platform.Keys.VideoQuality"/>, then apply it live
        /// (<see cref="SetPreferredHeight"/>). Settings' combo and the on-media ⋯ &gt; Quality rows both land here, because the
        /// session-side pin alone dies with the session: a rebuild derives its ceiling from the stored key and starts Auto,
        /// so a pin that never reached the store reverted on the next track and the menu's radio read Auto. UI thread
        /// (the settings store is UI-thread-affine). 0 = Auto; a negative height is Auto.</summary>
        public static void PinQuality(int height)
        {
            height = Math.Max(0, height);
            Platform.Settings.Set(Platform.Keys.VideoQuality, height);
            SetPreferredHeight(height);
        }

        /// <summary>Apply a video height pin live, or 0 for auto — the LIVE half only; the caller has already persisted the key
        /// (<see cref="PinQuality"/> is the one that does both). Applied to the ABR ceiling — under the metered cap — AND to
        /// the engine's own quality selection, so a pin survives a switch.</summary>
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

        /// <summary>The rung heights the bound manifest actually offers, tallest first, no duplicates. Pure, so "does
        /// the quality menu lie?" is a unit test: a fixed ladder offers rungs no variant matches (picking one cannot
        /// pin, yet it still moves the ABR ceiling, so the row reads as selected while playback is really on Auto) and
        /// hides the ones the manifest does have.</summary>
        public static int[] QualityLadder(ReadOnlySpan<int> heights)
        {
            Span<int> seen = heights.Length <= 32 ? stackalloc int[heights.Length] : new int[heights.Length];
            int n = 0;
            foreach (int h in heights)
            {
                if (h <= 0) continue;
                bool dup = false;
                for (int i = 0; i < n; i++) if (seen[i] == h) { dup = true; break; }
                if (!dup) seen[n++] = h;
            }
            var result = seen[..n].ToArray();
            Array.Sort(result, static (a, b) => b.CompareTo(a));
            return result;
        }

        /// <summary>The live ladder for the bound player — empty while nothing is open, which is also when the menu has
        /// nothing honest to offer beyond Auto.</summary>
        public static int[] QualityRungs()
        {
            MediaPlayer? p;
            lock (s_gate) { p = s_player; }
            if (p is null) return [];
            var heights = new List<int>(8);
            try { foreach (QualityVariant v in p.Qualities.Variants) heights.Add(v.Resolution.Height); }
            catch { return []; }
            return QualityLadder(heights.ToArray());   // menu-open only; not a hot path
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
        // A3: the descriptor the prepare opened, re-pointed at the opening rung — reused by the open that adopts it, so
        // the two compute the same rung, but only while the backend still holds that session (BuildMediaSource asks).
        // Recorded with its key and init url (the backend's claim key) when the prepare is BUILT (BuildMediaSource), i.e.
        // at registration and not at `Landed`, so an open that adopts the prepare while it is still in the air sees them
        // too; cleared wherever s_prepared is, and when the prepare fails or lands dead. The aim position is recorded
        // earlier still, by Prefetch on the UI thread the moment the slot is armed, so PrefetchedLevel never judges an
        // in-flight prepare against the previous one's position.
        static DashSourceDescriptor? s_preparedDescriptor;
        static string? s_preparedInitUrl;
        static int s_preparedAtMs;

        /// <summary>The level in hand for <paramref name="id"/> — the schedule's <c>Already</c>. UI thread. A recorded
        /// <see cref="PrefetchLevel.Full"/> counts only while the session behind it is still one the backend will hand
        /// over (in the air, or parked and inside its expiry) and the playing position is inside the window it fetched;
        /// otherwise the schedule asks for Full again and <see cref="Prefetch"/> re-prepares.</summary>
        public static PrefetchLevel PrefetchedLevel(EntityId id)
        {
            if (!id.Equals(s_prefetchId)) return PrefetchLevel.None;
            PrefetchLevel recorded = s_prefetchLevel;
            if (recorded != PrefetchLevel.Full) return recorded;
            string uri = id.Text;
            bool inFlight;
            string? parkedInitUrl;
            int preparedAt;
            lock (s_gate)
            {
                inFlight = s_preparingGate is not null && !s_dropPreparing
                           && string.Equals(s_preparingRow.Uri, uri, StringComparison.Ordinal);
                parkedInitUrl = s_prepared is not null ? s_preparedInitUrl : null;
                preparedAt = s_preparedAtMs;
            }
            return PrefetchSchedule.EffectiveLevel(recorded, inFlight || PreparedHandOver(parkedInitUrl), preparedAt, PositionMs.Peek());
        }

        /// <summary>Would the backend hand the open of this init url a prepared session right now (registered, unclaimed,
        /// inside its expiry)? The backend's own rule, so "warm" and "re-prepare" agree with what <c>OpenAsync</c> does.
        /// Takes the backend's lock, so call it with <see cref="s_gate"/> NOT held.</summary>
        static bool PreparedHandOver(string? initUrl)
        {
            ProtectedMediaBackend? backend;
            lock (s_gate) backend = s_backend;
            return backend is not null && backend.TryPeekPrepared(initUrl);
        }

        /// <summary>Bring <paramref name="id"/>'s video to <paramref name="level"/> before anyone asks for it (UI thread;
        /// the work runs on an api thread): Manifest = the memoised resolve; ManifestAndLicense = + the licence at
        /// manifest time; Full = + the init and the segments at <paramref name="atMs"/> in a prepared session the next open
        /// of the same source takes. The caller decides the level (<see cref="PrefetchSchedule.Decide"/>).
        /// <para>A row the pump has already CLAIMED is refused outright (G1 rule 1): its load fetches everything this
        /// would, and a prepare racing it opens a second protected session the switch never takes.</para></summary>
        public static void Prefetch(EntityId id, string manifestId, PrefetchLevel level, PrefetchReason why, int atMs)
        {
            if (level == PrefetchLevel.None || id.IsEmpty) return;
            if (PrefetchedLevel(id) >= level) return;
            // F167: the manifest is already being fetched (the load's resolve, or the keeper's): joining it would park an api
            // worker on the owner's task. Nothing is claimed here, so the schedule asks again on its next edge.
            if (PrefetchRace.ShouldSkipInFlight(manifestId, ManifestMemo.IsInFlight(manifestId))) return;
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
            if (prepare) lock (s_gate) s_preparedAtMs = at;     // the aim, from the moment the slot is claimed (PrefetchedLevel reads it)
            // A Full that found the slot taken prepared nothing: record what is really in hand (the licence), so the
            // schedule asks for Full again on its next edge instead of believing a session exists.
            if (level == PrefetchLevel.Full && !prepare) s_prefetchLevel = PrefetchLevel.ManifestAndLicense;
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
                    s_preparedDescriptor = null;
                    s_preparedInitUrl = null;
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
            try
            {
                pending = Backend.PrepareAtAsync(BuildMediaSource(source, forPrepare: true), TimeSpan.FromMilliseconds(atMs));   // BuildMediaSource records the key + descriptor + init url
            }
            catch (Exception ex) { Log.Warn("video", "prefetch prepare failed", ex); ForgetFailedPrepare(source.Key); ClosePrepareSlot(); return; }

            OpenPrepareGate();                     // the backend has the session now — an adopting load may stop waiting
            try { Landed(await pending.ConfigureAwait(false), source.Key); }
            catch (Exception ex) { Log.Warn("video", "prefetch prepare failed", ex); ForgetFailedPrepare(source.Key); ClosePrepareSlot(); }
        }

        /// <summary>A prepare that failed leaves nothing behind: what the registration recorded for it (key, rung, init
        /// url) names no session now. Only when nothing is parked for that key — a parked session keeps its own.</summary>
        static void ForgetFailedPrepare(string key)
        {
            lock (s_gate)
                if (s_prepared is null && string.Equals(s_preparedKey, key, StringComparison.Ordinal))
                {
                    s_preparedKey = "";
                    s_preparedDescriptor = null;
                    s_preparedInitUrl = null;
                }
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
                else if (s_prepared is null && string.Equals(s_preparedKey, key, StringComparison.Ordinal))
                {
                    // Dead on arrival: what the registration recorded for it (key, rung, init url) names nothing now.
                    s_preparedKey = "";
                    s_preparedDescriptor = null;
                    s_preparedInitUrl = null;
                }
            }
            ClosePrepareSlot();
            if (dead is not null) _ = dead.DisposeAsync();
        }

        /// <summary>G1 rule 2, the load's half. A prepare aimed at THIS row is adopted — the load waits (briefly, and
        /// only when it is actually going to open) for the backend to have registered it, and the open then takes that
        /// session by its init url. One aimed anywhere else is marked dead on arrival. Every path through here leaves the
        /// in-flight prepare either owned by this load or condemned; none leaves it running for nobody.</summary>
        static async Task ResolvePrepareAsync(RowKey loading, bool opening, LoadRequest req, long epoch, CancellationToken ct)
        {
            Task? gate = null;
            TaskCompletionSource? slot = null;
            lock (s_gate)
            {
                // F155: a SUPERSEDED request neither condemns nor adopts — the prepare in the air is very likely the newer
                // row's (its prefetch is armed while it is still Loading), and this request is about to be dropped.
                bool stale = IsStale(req, epoch);
                switch (LoadSupersession.FateOf(stale, s_preparingGate is not null, in s_preparingRow, in loading))
                {
                    case PrepareFate.Drop: s_dropPreparing = true; break;
                    case PrepareFate.Adopt when opening: slot = s_preparingGate!; gate = slot.Task; break;
                }
            }
            if (gate is null) return;
            try { await gate.WaitAsync(TimeSpan.FromMilliseconds(WarmPolicy.AdoptBudgetMs), ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Log.Warn("video", "the prepare this switch would adopt never registered — opening cold");
                // F160: the open that follows is cold and owns the content now; a prepare that registers later is a second
                // session for the same row nobody will take. Condemn THIS prepare (not a newer one) so Landed disposes it.
                lock (s_gate)
                    if (LoadSupersession.FateAfterAdoptTimeout(ReferenceEquals(s_preparingGate, slot)) == PrepareFate.Drop)
                    {
                        s_dropPreparing = true;
                        s_preparedDescriptor = null;
                    }
            }
            catch (OperationCanceledException) { }   // superseded while waiting: the next request decides the prepare's fate
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
                s_preparedDescriptor = null;
                s_preparedInitUrl = null;
            }
            _ = item.DisposeAsync();
        }

        /// <summary>Condemn whatever is still in the air: it is disposed the moment it lands. The other half of "adopted
        /// or disposed" — <see cref="ResolvePrepareAsync"/> resolves every prepare a LOAD races, and this resolves the
        /// ones nothing will ever load (a shed, a shutdown).</summary>
        static void CondemnPreparing()
        {
            lock (s_gate) if (s_preparingGate is not null) { s_dropPreparing = true; s_preparedDescriptor = null; }
        }

        /// <summary>Shut down for good. Drains the pump so a session is not torn down mid-open.</summary>
        public static void Shutdown()
        {
            PersistLink(force: true);
            s_disposed = true;
            Stop();
            try { s_worker.Wait(TeardownTimeoutMs * 2); } catch { }
            StopTicker();
            try { s_ticker?.Dispose(); } catch { }
            s_ticker = null;
            DisposeWarmTimer();
            Shed(wasWarm: false);                  // drops the runtime keep-alive too: shutdown must let the engine tear down
            CondemnPreparing();                    // whatever is still landing dies on arrival…
            ReleasePrepared(keepKey: null);        // …and whatever landed already is freed here
            MediaPlayer? leftover;
            MfMediaPlayer? mf;
            lock (s_gate) { leftover = s_player; s_player = null; mf = s_mf; s_mf = null; }
            if (leftover is not null) s_toDispose.Enqueue(leftover);   // the worker ran out of time before it unbound the player
            DrainDisposals();
            if (mf is null) return;
            // F190: the process-lifetime clear backend dies with the process, and its warm engine with it (a join on the
            // engine's MTA thread, bounded here so a wedged engine cannot hold the exit).
            try { mf.DisposeAsync().AsTask().Wait(TeardownTimeoutMs); }
            catch (Exception ex) { Log.Warn("video", "clear backend dispose failed", ex); }
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
                CancellationToken ct;
                lock (s_gate)
                {
                    next = s_pending;
                    clear = s_pendingClear;
                    epoch = s_pumpEpoch;
                    ct = s_loadCts.Token;            // the token of THIS epoch: Load/Stop swap it under the same lock
                    s_pending = null;
                    s_pendingClear = false;
                    if (next is null && !clear) { s_running = false; return; }
                }

                // Both arms are wrapped: a throwing load can never kill the worker, and it must never be silent either —
                // the reducer gets a typed fault for the epoch it asked about.
                try
                {
                    if (clear) await TeardownAsync().ConfigureAwait(false);
                    else await ApplyAsync(next!.Value, epoch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Log.Info("video", "load superseded mid-flight — the newer request is next");   // not a fault: it is the reducer's own move
                }
                catch (Exception ex)
                {
                    Log.Warn("video", "load pump op failed", ex);
                    if (next is { } req) ReportFault(req.Epoch, Fault.Unknown, ex.Message);
                }
            }
        }

        /// <summary>F155: has anything replaced THIS request? The pump epoch moves when a newer <see cref="Load"/> or
        /// <see cref="Stop"/> reaches the pump; the reducer epoch (<see cref="NoteLoadEpoch"/>) moves earlier, the moment
        /// the reducer starts the load that will replace it, while that load is still resolving.</summary>
        static bool IsStale(LoadRequest req, long epoch)
        {
            lock (s_gate) return LoadSupersession.IsStale(s_pumpEpoch, epoch, req.Epoch, Volatile.Read(ref s_latestLoadEpoch));
        }

        /// <summary>F155/F160: what supersession and the adopt budget decide about a prepare in the air — pure, so the
        /// rules are pinned without a CDM.</summary>
        public static class LoadSupersession
        {
            /// <summary>A request is stale when the pump moved past it or the reducer started a newer load.</summary>
            public static bool IsStale(long pumpEpoch, long requestPumpEpoch, uint requestReducerEpoch, uint latestReducerEpoch)
                => pumpEpoch != requestPumpEpoch || requestReducerEpoch != latestReducerEpoch;

            /// <summary><see cref="PrefetchRace.FateOf"/> for a request that is still current; a stale one never
            /// condemns (Drop) and never waits to adopt: the prepare belongs to whoever superseded it.</summary>
            public static PrepareFate FateOf(bool stale, bool preparing, in RowKey preparingRow, in RowKey loading)
                => stale ? PrepareFate.Idle : PrefetchRace.FateOf(preparing, in preparingRow, in loading);

            /// <summary>The adopt wait ran out. The load opens cold, so the prepare it waited for is condemned (Drop)
            /// while it is still the one in the slot; one that already landed or was replaced is left alone.</summary>
            public static PrepareFate FateAfterAdoptTimeout(bool slotStillHoldsThisPrepare)
                => slotStillHoldsThisPrepare ? PrepareFate.Drop : PrepareFate.Idle;
        }

        static async Task ApplyAsync(LoadRequest req, long epoch, CancellationToken ct)
        {
            if (IsStale(req, epoch)) return;       // superseded before it began: nothing below may run for it
            MediaPlayer? live;
            string liveKey;
            bool faulted, faultRetryable;
            lock (s_gate) { live = s_player; liveKey = s_key; faulted = s_errorReported; faultRetryable = s_faultRetryable; }

            req = req with { StartAtMs = HostRules.StartAt(req.StartAtMs, req.Source.DrmDescriptor?.DurationMs ?? 0) };
            SwitchAction plan = PlanWithReason(new SwitchInput(live is not null, faulted, liveKey, req.Source.Key, req.StartAtMs, faultRetryable), out SwitchReason why);
            Interlocked.Exchange(ref s_switchAtMs, FrameNowMs());
            Interlocked.Exchange(ref s_switchAtQpc, System.Diagnostics.Stopwatch.GetTimestamp());
            if (plan is SwitchAction.Switch or SwitchAction.Rebuild) Volatile.Write(ref s_firstFrameAtMs, 0);   // F225: a new session's first frame is still to come
            // F163: a faulted in-place re-open of the same key spends that key's one retry; every other load starts afresh.
            bool reopen = plan == SwitchAction.Switch && faulted && string.Equals(liveKey, req.Source.Key, StringComparison.Ordinal);
            lock (s_gate) s_reopenedKey = reopen ? liveKey : null;

            // G1 rule 2, BEFORE the log line and before any open: adopt the prepare this very switch was fetched for, or
            // condemn one aimed elsewhere. `warm=` is then the truth about the open that follows — which is what the
            // §4.3 gate reads it for.
            var row = new RowKey(req.Source.PlayableUri, req.Source.Key);
            await ResolvePrepareAsync(row, plan is SwitchAction.Switch or SwitchAction.Rebuild, req, epoch, ct).ConfigureAwait(false);
            // F155: superseded while it waited for the prepare — no open (a stale attach is a licence challenge, a native
            // session and a destroy ahead of the newer load's attach), and no ReleasePrepared below, which would dispose a
            // prepare for the newer row that already landed.
            if (IsStale(req, epoch)) return;
            LogLine(new VideoLog.SwitchBegin(Tail(req.Source.Key), req.StartAtMs, plan, why, WarmFor(req.Source), req.Epoch));
            ReleasePrepared(keepKey: req.Source.Key);

            switch (plan)
            {
                case SwitchAction.None:
                    AdoptLiveEpoch(req, epoch, live!);
                    return;
                case SwitchAction.SeekOnly:
                {
                    AdoptLiveEpoch(req, epoch, live!);
                    long at = req.StartAtMs;
                    ToUi(() => Seek(at));                // the seek index is the UI thread's
                    return;
                }
                case SwitchAction.Switch:
                    await SwitchInPlaceAsync(req, epoch, live!, ct).ConfigureAwait(false);
                    return;
                default:
                    await BuildAndOpenAsync(req, epoch, ct).ConfigureAwait(false);
                    return;
            }
        }

        /// <summary>F151: a load that finds its key already live (a re-toggle onto the paused video the park kept, a docked to
        /// fullscreen move) opens nothing, but it IS a new reducer load: the live session's reports must carry ITS epoch, or the
        /// reducer drops every one of them, and its play intent (a paused attach, a playing reload) must reach the player.</summary>
        static void AdoptLiveEpoch(LoadRequest req, long epoch, MediaPlayer live)
        {
            lock (s_gate)
            {
                if (IsStale(req, epoch)) return;
                s_epoch = req.Epoch;
                s_lastState = PlaybackState.Idle;      // the live session announces itself again under the NEW epoch: a Started (or Paused) and the duration,
                s_reportedDurMs = 0;                   // which the reducer's Loading phase waits for (a held window suppresses the fold until its release)
                RestartAttachWindow();
                s_playIntent = !s_intentPaused;
            }
            SettleIntent(live, req, epoch);
        }

        /// <summary>Was the warm path ACTUALLY in hand for this load — the `warm=` of `[video] switch.begin`? Either a
        /// prepared session for its row is parked or registered AND the backend will still hand it over (its own expiry
        /// rule, <see cref="ProtectedMediaBackend.TryPeekPrepared"/>: the open takes it by init url and the switch is one
        /// attach), or its content key is already usable in the runtime's cache (the licence round trip, ~80 % of a cold
        /// switch, is already paid). `warm=false` therefore means exactly one thing: this switch pays for everything.
        /// <para>The runtime's own lock is taken OUTSIDE <see cref="s_gate"/>, never nested under it.</para></summary>
        static bool WarmFor(VideoSource source)
        {
            string? initUrl = null;
            lock (s_gate)
            {
                bool parked = s_prepared is not null && string.Equals(s_preparedKey, source.Key, StringComparison.Ordinal);
                bool registered = s_preparingGate is { Task.IsCompleted: true } && !s_dropPreparing
                                  && string.Equals(s_preparingRow.Key, source.Key, StringComparison.Ordinal);
                if (parked || registered) initUrl = s_preparedInitUrl;
            }
            if (PreparedHandOver(initUrl)) return true;
            return source.DrmDescriptor?.DefaultKid is { Length: > 0 } kid
                && ProtectedVideoRuntime.Shared.LicenseStateFor(kid) == LicenseCacheState.Usable;
        }

        /// <summary>The REAL rebuild: the first player of the process, or a faulted one that cannot re-open in place. F152:
        /// make-before-break. The new facade is built BEFORE the old one is touched, the old SESSION is closed before the
        /// new one opens (PlayReady's one shared native engine takes one attached session at a time), and ONE binding
        /// change swaps the player (never null-then-new, so the mounted stage swaps once and never shows a
        /// player-less frame in between). The old facade is disposed in the background (<see cref="s_toDispose"/>), off the
        /// critical path.</summary>
        static async Task BuildAndOpenAsync(LoadRequest req, long epoch, CancellationToken ct)
        {
            ResetLoadState();
            MediaPlayer built = BuildPlayer();
            // F185: the player's own signals are written on the UI thread only; the worker never touches them.
            await RunOnUiAsync(() =>
            {
                double volume;
                bool muted;
                lock (s_gate) { volume = s_volume; muted = s_muted || s_previewMuted || s_attachMuted; }
                built.SetVolume(volume);
                built.SetMuted(muted);
            }).ConfigureAwait(false);

            MediaPlayer? old;
            lock (s_gate) old = s_player;
            bool closed = old is null || await CloseBoundedAsync(old).ConfigureAwait(false);   // detach the old session; a clean facade stays bound until the swap

            if (IsStale(req, epoch))
            {
                s_toDispose.Enqueue(built);
                // A superseded rebuild leaves `old` bound. If its close did not finish it must not stay bound: the next load
                // would plan a Switch onto it and the late close would dispose or reset that newer session.
                if (old is not null && !closed) Retire(old);
                else DrainDisposals();
                return;
            }

            long generation;
            lock (s_gate)
            {
                s_player = built;
                generation = ++s_generation;
            }
            MediaOpenOptions options = ResetPerLoad(req);
            PublishBinding(built, generation);
            if (old is not null) s_toDispose.Enqueue(old);
            DrainDisposals();
            VideoSource published = req.Source;
            ToUi(() => Source.Value = published);

            PostBufferingUnlessHeld(req.Epoch);
            StartTicker();                         // BEFORE the open: an open that never completes must still tick
            long startedAt = FrameNowMs();
            try
            {
                await built.OpenAsync(BuildMediaSource(req.Source, forPrepare: false), options, ct).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMs), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn("video", "open timed out — the start watchdog owns it now");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log.Info("video", "open superseded — the newer load owns the player");
                ForgetAbandonedKey(req);
                return;
            }
            if (IsStale(req, epoch)) return;
            if (built.Error.Peek() is { } err) { ReportFault(req.Epoch, HostRules.MapError(err), err.Message, err); return; }
            Log.Info("video", OpenOkLine(req, startedAt, rebuild: true));
            ReleasePrepared(keepKey: null);        // the open took it (the dispose is then a no-op) or never will
            SettleIntent(built, req, epoch);
        }

        /// <summary>F215: the `open.ok` line. `openCallMs` is how long the open CALL took, which for a protected source is only the
        /// session being built and the attach being posted: the attach (SetSource), metadata, canplay and first frame all happen after it,
        /// and are on the engine's one `[video] switch.budget` line (the old `openMs=3` beside `setSourceMs=940` read as a fast open).
        /// `sinceSwitchMs` counts from switch.begin, so the resolve and the prepare wait before the open are on it.</summary>
        static string OpenOkLine(LoadRequest req, long startedAt, bool rebuild)
        {
            long now = FrameNowMs();
            return $"[video] open.ok key={Tail(req.Source.Key)} epoch={req.Epoch} openCallMs={now - startedAt} " +
                   $"sinceSwitchMs={now - Interlocked.Read(ref s_switchAtMs)} rebuild={(rebuild ? "true" : "false")}";
        }

        /// <summary>A different video on a HEALTHY player: re-open the same instance. No `PlayerChanged`, so the mounted
        /// element is never remounted. On a timeout the watchdog takes it. An open that REPORTS an error (the engine's
        /// open never throws for a backend failure) is a typed fault exactly as in <see cref="BuildAndOpenAsync"/>; the
        /// reducer's retry then re-opens a transient one in place again. Only a throw (a source that cannot be built)
        /// degrades ONCE to a full rebuild.</summary>
        static async Task SwitchInPlaceAsync(LoadRequest req, long epoch, MediaPlayer live, CancellationToken ct)
        {
            RetractLiveWindow();                  // must precede ResetPerLoad: it retracts the OLD window
            MediaOpenOptions options = ResetPerLoad(req);
            VideoSource published = req.Source;
            ToUi(() => Source.Value = published);
            PostBufferingUnlessHeld(req.Epoch);
            StartTicker();
            long startedAt = FrameNowMs();
            try
            {
                await live.OpenAsync(BuildMediaSource(req.Source, forPrepare: false), options, ct).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMs), ct).ConfigureAwait(false);
                // F155: a superseded open reports nothing and releases nothing — ReleasePrepared would dispose a prepare
                // for the newer row that landed meanwhile.
                if (IsStale(req, epoch)) return;
                if (live.Error.Peek() is { } err)
                {
                    ReportFault(req.Epoch, HostRules.MapError(err), err.Message, err);   // no open.ok line, no Play on a Failed session
                    return;
                }
                Log.Info("video", OpenOkLine(req, startedAt, rebuild: false));
                ReleasePrepared(keepKey: null);    // the open took it (the dispose is then a no-op) or never will
                SettleIntent(live, req, epoch);
            }
            catch (TimeoutException)
            {
                Log.Warn("video", "switch timed out — the start watchdog owns it now");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log.Info("video", "switch superseded — the newer load owns the player");
                ForgetAbandonedKey(req);
            }
            catch (Exception ex)
            {
                Log.Warn("video", "switch failed — degrading to a rebuild", ex);
                if (!IsStale(req, epoch)) await BuildAndOpenAsync(req, epoch, ct).ConfigureAwait(false);   // closes the failed player itself
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

        /// <summary>F185: called from the load worker, which must not write the player's signals — the intent is applied
        /// on the UI thread, where it is RE-READ at execution (a Pause that landed since the open finished wins) and where
        /// <see cref="Play"/>/<see cref="Pause"/> are serialised with it.</summary>
        static void SettleIntent(MediaPlayer p, LoadRequest req, long epoch) => ToUi(() => SettleIntentOnUi(p, req, epoch));

        static void SettleIntentOnUi(MediaPlayer p, LoadRequest req, long epoch)
        {
            bool play;
            float rate;
            lock (s_gate)
            {
                // Torn down or replaced while the hop was queued — or superseded: on a Switch the player is the SAME, and the
                // next load's open is disposing this one's session on the worker, so no transport call may race it.
                if (!ReferenceEquals(s_player, p)
                    || LoadSupersession.IsStale(s_pumpEpoch, epoch, req.Epoch, Volatile.Read(ref s_latestLoadEpoch))) return;
                play = s_playIntent;
                rate = s_desiredRate;
            }
            try
            {
                p.SetRate(rate);
                if (play) _ = p.PlayAsync();
                else _ = p.PauseAsync();
            }
            catch (Exception ex) { Log.Warn("video", "initial transport failed", ex); }
        }

        /// <summary>An open abandoned for a newer load never completed, so the key <see cref="ResetPerLoad"/> claimed is not
        /// live: a following request for the SAME key (a rapid A to B to A skip) must plan a re-open, not a seek on a
        /// player with no session.</summary>
        static void ForgetAbandonedKey(LoadRequest req)
        {
            lock (s_gate) if (string.Equals(s_key, req.Source.Key, StringComparison.Ordinal)) s_key = "";
        }

        /// <summary>The per-load state back to "nothing loaded": the key, the live source, the intent, the fault memory,
        /// the watchdog and the ticker, the live window and the published source. It does NOT touch <see cref="s_player"/>.</summary>
        static void ResetLoadState()
        {
            lock (s_gate)
            {
                s_key = "";
                s_live = null;
                s_playIntent = false;
                s_progressed = false;
                s_errorReported = false;
                s_firstFrameFired = false;
                Volatile.Write(ref s_firstFrameAtMs, 0);   // F225: the previous load's first frame is not this one's
                s_reportedDurMs = 0;
                s_lastState = PlaybackState.Idle;
                s_baseSession = null;
                s_baseError = null;
                s_faultRetryable = false;
                s_reopenedKey = null;
                s_watchdog.Disarm();
            }
            StopTicker();
            RetractLiveWindow();
            ToUi(s_clearSource);
        }

        /// <summary>Close the video session. F152: the ONE <see cref="MediaPlayer"/> of the process stays BOUND — the mounted
        /// stage keeps its element and its pump, there is no `PlayerChanged` and no remount — and only its session is
        /// released (<see cref="MediaPlayer.CloseAsync"/>: the surface binding, the clock and, for PlayReady, the secure
        /// decoder). The next video is then a plain <see cref="SwitchAction.Switch"/> on the same player, not a
        /// rebuild. At shutdown the player is unbound and disposed instead.</summary>
        static async Task TeardownAsync()
        {
            ResetLoadState();
            bool final = Volatile.Read(ref s_disposed);
            MediaPlayer? live;
            lock (s_gate)
            {
                live = s_player;
                if (final) s_player = null;
            }
            if (live is null) return;

            if (!final)
            {
                if (await CloseBoundedAsync(live).ConfigureAwait(false)) return;
                // The close did not finish: its reset can still land on the core after a NEWER session attaches, so this
                // facade must not be reused. Retire it (the next load plans a Rebuild), disposing it in the background.
                Retire(live);
                return;
            }
            MediaPlayer stopping = live;
            await RunOnUiAsync(() => stopping.Stop()).ConfigureAwait(false);   // F185: Stop writes core state, so it runs on the UI thread, before the dispose
            long generation;
            lock (s_gate) generation = ++s_generation;
            PublishBinding(null, generation);
            await DisposeBoundedAsync(live).ConfigureAwait(false);
            DrainDisposals();
        }

        /// <summary>Stop the player (a core write, so on the UI thread) and release its session, bounded so a wedged native
        /// close delays the worker rather than hanging it. True only when the close finished inside its budget without
        /// throwing: the facade is then clean and reusable. False means its close may still be pending, so the caller
        /// must not reuse the facade (a late close would reset the next session's core state).</summary>
        static async Task<bool> CloseBoundedAsync(MediaPlayer p)
        {
            await RunOnUiAsync(() => p.Stop()).ConfigureAwait(false);   // F185
            try
            {
                await p.CloseAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(TeardownTimeoutMs)).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) { Log.Warn("video", "player close exceeded its budget — retiring the player"); }
            catch (Exception ex) { Log.Warn("video", "player close failed — retiring the player", ex); }
            return false;
        }

        /// <summary>Unbind a facade whose close did not finish and dispose it in the background. It must never stay bound:
        /// the next load would reuse it and its late close would land on the newer session.</summary>
        static void Retire(MediaPlayer p)
        {
            long retired;
            lock (s_gate)
            {
                if (ReferenceEquals(s_player, p)) s_player = null;
                retired = ++s_generation;
            }
            PublishBinding(null, retired);
            s_toDispose.Enqueue(p);
            DrainDisposals();
        }

        static readonly Action s_clearSource = static () =>
        {
            Source.Value = null;
            Phase.SetIfChanged(SwitchPhase.Idle);
        };

        /// <summary>F185: run a player write on the UI thread and wait for it (bounded, so a wedged UI delays the worker
        /// rather than hanging it). Once the loop is gone (<c>Platform.UiThreadId</c> is cleared when Shell.Run returns, before <see cref="Shutdown"/>) the write runs inline.
        /// A throwing write is logged, never fatal: these are fail-soft transport calls.</summary>
        static async Task RunOnUiAsync(Action write)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action guarded = () =>
            {
                try { write(); }
                catch (Exception ex) { Log.Warn("video", "player write on the UI thread failed", ex); }
                finally { done.TrySetResult(); }
            };
            if (Volatile.Read(ref s_disposed) || Platform.UiThreadId is null) { guarded(); return; }
            ToUi(guarded);
            try { await done.Task.WaitAsync(TimeSpan.FromMilliseconds(TeardownTimeoutMs)).ConfigureAwait(false); }
            catch (TimeoutException) { Log.Warn("video", "the UI thread did not run a player write within its budget"); }
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

        /// <summary>Reset the per-load state and build the open's options: the position the open lands at, whether it
        /// opens paused (the reducer's LATEST word — a Pause issued after the Load counts), and this source's own relay.</summary>
        static MediaOpenOptions ResetPerLoad(LoadRequest req)
        {
            bool paused;
            lock (s_gate)
            {
                // F158: the player still holds the PREVIOUS load's session and error until the open replaces them.
                MediaPlayer? current = s_player;
                s_baseSession = current?.Session;
                s_baseError = current?.Error.Peek();
                s_faultRetryable = false;
                s_key = req.Source.Key;
                s_live = req.Source;
                s_epoch = req.Epoch;
                RestartAttachWindow();
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
        /// relay slot any more).
        /// <para>A1 (video stutter fix, 2026-09-22): ONE <see cref="AdaptiveBitrateController"/> for the process. A
        /// rebuilt player (pop-out reopen, a warm-runtime shed, a fault) must not forget what the link measured: that
        /// re-ran the whole climb, and every rung is a visible freeze. The cap is re-pushed on every build because
        /// ProtectedMediaSession snapshots MaxHeight as its policy cap at open and clamps it to the viewport afterwards —
        /// a previous session's small pop-out must not become the next session's ceiling. ApplyQualityCaps does the same
        /// on load.</para></summary>
        static MediaPlayer BuildPlayer()
        {
            int cap = HostRules.QualityCap(Platform.Settings.Get(Platform.Keys.VideoQuality), Platform.Network.EffectiveVideoMaxHeight());
            AdaptiveBitrateController abr;
            lock (s_gate)
            {
                // ONE controller for the process.
                abr = s_abr ??= new AdaptiveBitrateController();
                // The OPENING pick (A3) must see the cap the last session actually ran under: the engine element clamps
                // MaxHeight to the viewport on every pump, floored at 720, and a rebuild that re-pushed the bare policy
                // cap would open a measured link at 1080p in an 848 px pop-out, only for the new session's first pump to
                // clamp it and force a 720p downswitch — the very switch this plan removes. Carry a VIEWPORT clamp
                // across the rebuild; never a stale metered/pin cap.
                int effective = abr.MaxHeight;
                s_lastViewportCap = effective < s_policyCap ? effective : int.MaxValue;
                s_policyCap = cap;
                abr.MaxHeight = cap;
                abr.Selection = QualitySelection.Auto;   // a rebuild starts Auto, as a fresh controller did: a pin taken
                                                          // in the Settings tab lives on as the MaxHeight cap
                                                          // (HostRules.QualityCap), and a new session never re-issues
                                                          // SelectVideoRepresentation for a stale one
                if (abr.EstimateIsPrior) abr.SeedEstimate(LinkMemory.Load(Platform.Settings));
            }
            return MediaPlayer.Build()
                .WithBackend(MediaKind.MfVideoOrFile, ClearBackend)
                .WithAbr(abr)
                .WithDrm(License.ByKeyId)
                .Build();
        }

        /// <summary>Persist the link estimate when LinkMemory says so. Writes INLINE when there is no live UI thread
        /// (Shutdown runs from Main after Shell.Run returned, where ToUi would post to a loop that no longer pumps and
        /// the exit write would be lost every time) or when already on it; otherwise through ToUi, because Settings.Set
        /// asserts the UI thread in Debug. Reads of s_abr's EWMAs off the pump thread are lock-free gates only.</summary>
        static void PersistLink(bool force)
        {
            double k; bool measured;
            lock (s_gate) { if (s_abr is not { } abr) return; k = Math.Min(abr.EstimatedKbps, LinkMemory.MaxKbps); measured = !abr.EstimateIsPrior; }
            long now = Environment.TickCount64;
            lock (s_gate) if (!LinkMemory.ShouldPersist(measured, k, s_linkPersisted, now, s_linkWriteMs, force)) return;
            void Write()
            {
                lock (s_gate) { s_linkPersisted = k; s_linkWriteMs = now; }
                Platform.Settings.Set(Platform.Keys.VideoLinkKbps, Math.Round(k));
                Log.Info("video", $"[video] link.memory kbps={k:F0}{(force ? " why=shutdown" : "")}");   // always-on, one per write
            }
            if (Platform.UiThreadId is null || Platform.UiThreadId == Environment.CurrentManagedThreadId) Write(); else ToUi(Write);
        }

        /// <summary>The link state an OPEN reasons from: the live controller's, or — before the first player exists —
        /// what BuildPlayer is about to seed it with (the remembered estimate under the policy cap), so a prepare and the
        /// open that adopts it compute the same rung. The cap is the last session's effective (viewport-clamped) one —
        /// see BuildPlayer.</summary>
        static (double Kbps, bool Prior, int Cap) LinkState()
        {
            lock (s_gate)
                if (s_abr is { } abr) return (abr.EstimatedKbps, abr.EstimateIsPrior, Math.Min(abr.MaxHeight, s_lastViewportCap));
            return (LinkMemory.Load(Platform.Settings), true,
                    HostRules.QualityCap(Platform.Settings.Get(Platform.Keys.VideoQuality), Platform.Network.EffectiveVideoMaxHeight()));
        }

        /// <summary>A3: build the media source for an open or a prepare. <paramref name="forPrepare"/> distinguishes the
        /// two: a prepare always picks its own opening rung and remembers the descriptor it picked; an open ADOPTS that
        /// descriptor when it is for the exact same row AND the backend would still hand its prepared session over (so a
        /// first measurement landing between the prepare and the open cannot lift the prior ceiling and pick a different
        /// rung), and otherwise picks its own rung fresh.</summary>
        static MediaSource BuildMediaSource(VideoSource src, bool forPrepare)
        {
            if (src.FilePath is { } file) return MediaSource.FromFile(file);
            if (!src.IsDrm)
                return MediaSource.FromUri(src.ClearUrl ?? "")
                    .WithLiveness(src.IsLive ? SourceLiveness.Live : SourceLiveness.Auto);
            DashSourceDescriptor d = src.DrmDescriptor!;
            DashSourceDescriptor? adopt = null;
            string? adoptInitUrl = null;
            lock (s_gate)
                if (!forPrepare && string.Equals(s_preparedKey, src.Key, StringComparison.Ordinal)) { adopt = s_preparedDescriptor; adoptInitUrl = s_preparedInitUrl; }
            // The adoption exists only to hit the prepared session by its init url: drop it unless the backend would still
            // hand that session over (a failed, expired, taken or condemned prepare must not force its old rung or its
            // possibly lapsed signed urls onto the open). Asked with s_gate released — the backend takes its own lock.
            if (adopt is not null && !PreparedHandOver(adoptInitUrl)) adopt = null;
            d = adopt ?? Opening(d);
            if (forPrepare) lock (s_gate) { s_preparedDescriptor = d; s_preparedKey = src.Key; s_preparedInitUrl = d.InitUrl; }
            return MediaSource.FromUri(d.InitUrl).With(new DrmConfig(DrmSystem.PlayReady, src.LicenseServerUri) { SourceDescriptor = d });
        }

        /// <summary>The descriptor re-pointed at the opening rung (a `with` over the record; every other field, the
        /// catalog and the audio addressing included, is untouched). `Codecs` is left as the descriptor's default —
        /// nothing consumes it for video (the native open reads the codec from the init segment).</summary>
        static DashSourceDescriptor Opening(DashSourceDescriptor d)
        {
            var video = d.Catalog?.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Representations;
            if (video is not { Count: > 1 }) return d;
            var (kbps, prior, cap) = LinkState();
            if (prior && kbps <= ThroughputEstimator.DefaultSeedKbps) kbps = 0;   // the bare seed is not an estimate: cold rule
            Span<(int, int)> rungs = stackalloc (int, int)[video.Count];
            for (int i = 0; i < video.Count; i++) rungs[i] = (video[i].Quality.Resolution.Height, video[i].Quality.Bitrate);
            var rep = video[OpeningRung(rungs, kbps, prior, cap)];
            if (string.Equals(rep.InitUrl, d.InitUrl, StringComparison.Ordinal)) return d;
            Log.Info("video", $"[video] open.rung {rep.Quality.Resolution.Width}x{rep.Quality.Resolution.Height}@{rep.Quality.Bitrate} kbps={kbps:F0} why={(prior ? "prior" : "measured")} cap={cap}");
            return d with { InitUrl = rep.InitUrl, SegmentBaseUrl = rep.SegmentBaseUrl, SegmentPrefix = rep.SegmentPrefix, SegmentSuffix = rep.SegmentSuffix, RepresentationId = rep.Id };
        }

        static void PublishBinding(MediaPlayer? p, long generation)
            => ToUi(() => Player.Value = new Binding(p, generation));

        // ── 6. the 200 ms tick: position, duration, liveness, the watchdog ──────────────────────────────────────────

        /// <summary>The tick is a ONE-SHOT timer re-armed at the end of <see cref="Tick"/>: a slow tick can never be
        /// overlapped by the next one, and every field a tick reads or writes is under <see cref="s_gate"/>.</summary>
        static void StartTicker()
        {
            Timer ticker;
            lock (s_gate)
            {
                s_tickerOn = true;
                ticker = s_ticker ??= new Timer(static _ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            }
            try { ticker.Change(TickMs, Timeout.Infinite); } catch { }
        }

        static void StopTicker()
        {
            Timer? ticker;
            lock (s_gate) { s_tickerOn = false; ticker = s_ticker; }
            try { ticker?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        static void Tick()
        {
            if (Interlocked.Exchange(ref s_tickRunning, 1) != 0) return;   // a Start during a slow tick: the running one re-arms
            try { TickOnce(); }
            finally
            {
                Volatile.Write(ref s_tickRunning, 0);
                Timer? ticker;
                bool on;
                lock (s_gate) { ticker = s_ticker; on = s_tickerOn && !s_disposed; }
                if (on) { try { ticker?.Change(TickMs, Timeout.Infinite); } catch { } }
            }
        }

        static void TickOnce()
        {
            PersistLink(force: false);
            MediaPlayer? p;
            uint epoch, gen;
            bool intent, reported, drm;
            IMediaSession? baseSession;
            MediaError? baseError;
            lock (s_gate)
            {
                p = s_player; epoch = s_epoch; intent = s_playIntent; gen = s_videoSeekGen;
                reported = s_errorReported; drm = s_live is { IsDrm: true };
                baseSession = s_baseSession; baseError = s_baseError;
            }
            if (p is null || s_disposed) { StopTicker(); return; }

            // (a) the engine's own error is the richest diagnosis we will get; it wins over the watchdog. The previous
            // load's error object is not this load's (F158).
            if (!reported && p.Error.Peek() is { } err && HostRules.IsFreshError(err, baseError))
            {
                ReportFault(epoch, HostRules.MapError(err), err.Message, err);
                return;
            }

            // The session is read BEFORE the state and position: a tick that sees the new session then also sees at least
            // the state the new session's first hop published. Until the open assigns it, the player still reads as the
            // PREVIOUS video, so nothing below folds it (F158); the watchdog still runs, with no progress.
            IMediaSession? session = p.Session;
            bool owned = HostRules.OwnsTick(session, baseSession);
            PlaybackState state = PlaybackState.Idle;
            long pos = 0, durMs = 0;
            LiveWindow window = default;
            if (owned)
            {
                state = p.State.Peek();
                pos = Math.Max(0, (long)p.Position.Peek().TotalMilliseconds);
                durMs = (long)p.Duration.Peek().TotalMilliseconds;
                NoteStatePump(p, state, pos, session is ProtectedMediaSession);

                // (b) liveness, from the engine's timeline and NEVER from a finite duration.
                TimelineInfo tl = p.Timeline.Peek();
                window = new LiveWindow(tl.IsLive,
                    (long)tl.SeekableStart.TotalMilliseconds, (long)tl.SeekableEnd.TotalMilliseconds,
                    (long)tl.LiveEdge.TotalMilliseconds, pos, tl.IsAtLiveEdge);
            }

            // Every decision under the gate, every post after it: Post takes the reducer's own lock.
            bool relayLive = false, relayDur = false, fireWatchdog = false, logCut = false;
            long cutSongPos = 0, cutGap = 0;
            TickFold fold = TickFold.None;
            MediaSwitch.AttachVerdict attach = MediaSwitch.AttachVerdict.Wait;
            string key;
            lock (s_gate)
            {
                if (s_epoch != epoch || s_errorReported) return;      // a load began, or a fault landed, since the snapshot
                key = s_key;
                if (owned)
                {
                    if (LiveWindowMoved(in window))
                    {
                        s_reportedLive = window;
                        s_liveReported = true;
                        relayLive = true;
                    }

                    // (c) duration. Suppressed while live, and RE-relayed whenever it moves.
                    if (!window.IsLive && durMs > 0 && Math.Abs(durMs - s_reportedDurMs) > DurationRelayEpsilonMs)
                    {
                        s_reportedDurMs = durMs;
                        relayDur = true;
                    }
                }

                // (d) the watchdog. `progressed` is deliberately generous: anything that proves THIS session is alive — a
                // paused open presenting its first frame included.
                bool progressed = HostRules.Progressed(s_progressed, owned, pos, state);
                if (progressed) { s_progressed = true; s_reopenedKey = null; }   // alive again: the key's re-open budget is back
                if (s_watchdog.ShouldFault(FrameNowMs(), intent, progressed)) fireWatchdog = true;
                else if (owned)
                {
                    // (e) the state fold, edge-triggered on the last state except the position tick. F151: while the attach window
                    // holds the reports the song owns the deck, so no transport state is posted AND none is consumed — the
                    // release re-folds, so the first report after the cut is a Started. The end and a failure still go through.
                    fold = HostRules.Fold(state, s_lastState, s_firstFrameFired);
                    if (s_holdReports && fold is TickFold.Started or TickFold.Position or TickFold.Paused or TickFold.Buffering)
                        fold = TickFold.None;
                    else
                    {
                        if (fold == TickFold.Started) s_firstFrameFired = true;
                        s_lastState = state;
                    }
                }

                // (e2) the cut's gap probe (F151): the first tick after the release that finds the video PLAYING past the cut position.
                if (s_cutProbeArmed && !s_holdReports && owned && state == PlaybackState.Playing && pos > s_cutProbePcMs)
                {
                    s_cutProbeArmed = false;
                    logCut = true;
                    cutSongPos = s_cutProbeSongPosMs;
                    cutGap = AudioHandoff.CutGapMs(FrameNowMs() - s_cutProbeAtMs, pos - s_cutProbePcMs);
                }

                // (f) the attach window (F151): once per window, a presented frame asks for the cut, a stalled open is played muted
                // for a frame, and a window that never gets one is a video fault (the song keeps playing).
                if (s_holdReports && !s_attachReadyPosted && !fireWatchdog)
                {
                    bool presented = owned && (session is ProtectedMediaSession ? !p.VideoSurface.Peek().IsNone : session is MfMediaSession mfs ? mfs.FirstFrameEpoch != 0 : !p.NaturalSize.Peek().IsEmpty);
                    attach = MediaSwitch.JudgeAttach(FrameNowMs() - s_holdSinceMs, owned, presented, s_prerolled);
                    if (attach == MediaSwitch.AttachVerdict.Preroll) s_prerolled = true;
                    else if (attach is MediaSwitch.AttachVerdict.Cut or MediaSwitch.AttachVerdict.TimedOut) s_attachReadyPosted = true;
                }
            }

            if (relayLive) ReportLiveWindow(in window);
            if (relayDur) Post(Input.Duration((int)Math.Min(durMs, int.MaxValue), epoch));
            if (logCut) LogAudioCut(cutSongPos, pos, cutGap);
            if (fireWatchdog)
            {
                ProtectedVideoPhase phase = session is ProtectedMediaSession protectedSession ? protectedSession.Player.Phase : ProtectedVideoPhase.Resolving;
                Log.Warn("video", $"start watchdog fired after {s_watchdog.TimeoutMs}ms — state={state} pos={pos} key={Tail(key)}");
                ReportFault(epoch, HostRules.WatchdogFault(drm, phase), "the video session never started playing (no progress within the start budget)");
                return;
            }

            switch (attach)
            {
                case MediaSwitch.AttachVerdict.Preroll: ToUi(PrerollOnUi); break;
                case MediaSwitch.AttachVerdict.Cut: PostCut(epoch); break;
                case MediaSwitch.AttachVerdict.TimedOut:
                {
                    ProtectedVideoPhase phase = session is ProtectedMediaSession protectedSession ? protectedSession.Player.Phase : ProtectedVideoPhase.Resolving;
                    Log.Warn("video", $"attach window gave up after {MediaSwitch.AttachBudgetMs}ms with no presented frame — state={state} key={Tail(key)}");
                    ReportFault(epoch, HostRules.WatchdogFault(drm, phase), "the video never presented a frame while the song kept playing (attach window)");
                    return;
                }
            }

            switch (fold)
            {
                case TickFold.Started: PostSignal(epoch, AudioSignal.Started, pos); break;
                case TickFold.Position: PostSignal(epoch, AudioSignal.Position, pos, gen); break;   // V-PA2: stamped with the generation read above
                case TickFold.Paused: PostSignal(epoch, AudioSignal.Paused, pos); break;
                case TickFold.Buffering: PostSignal(epoch, AudioSignal.Buffering, pos); break;
                case TickFold.Ended:
                    StopTicker();
                    Post(Input.Ended(epoch, FrameNowMs()));
                    break;
                case TickFold.Failed: ReportFault(epoch, Fault.DecodeFailed, "video playback failed"); break;
            }
        }

        /// <summary>F179: the always-on heartbeat check. The player pumps its own state (F132); a PLAYING session whose pump has not
        /// run for <see cref="HostRules.StatePumpStalled"/>'s budget has a frozen playhead and no error to explain it, so say so —
        /// once per episode, and again only after a pump has been seen. Tick thread; a read of two counters, no allocation until it fires.</summary>
        static void NoteStatePump(MediaPlayer p, PlaybackState state, long posMs, bool protectedSession)
        {
            long idleMs = p.StatePumpAgeMs;
            if (!HostRules.StatePumpStalled(state, idleMs, protectedSession)) { s_statePumpStallWarned = false; return; }
            if (s_statePumpStallWarned) return;
            s_statePumpStallWarned = true;
            Log.Warn("video", $"[video] state pump stalled — no pump for {idleMs}ms while {state} (pos={posMs}ms pumps={p.StatePumpCount})");
        }

        /// <summary>F151: ask the UI thread for the cut. Its own method so the closure is allocated once per window, never by the tick.</summary>
        static void PostCut(uint epoch) => ToUi(() => ParkAndCut.Cut(epoch));

        /// <summary>F151: an attach that has sat open without a frame plays muted so it presents one (the cut still waits for
        /// that frame). UI thread; a no-op once the window is gone.</summary>
        static void PrerollOnUi()
        {
            lock (s_gate) { if (!s_holdReports) return; }
            Log.Info("video", "[video] attach.preroll — the paused open presented no frame, playing it muted");
            Play();
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
            lock (s_gate)
            {
                if (!s_liveReported) return;
                s_liveReported = false;
                s_reportedLive = default;
            }
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

        static void PostSignal(uint epoch, AudioSignal signal, long posMs = 0, uint gen = 0)
            => Post(Input.Audio(signal, epoch, FrameNowMs(), posMs, gen));

        /// <summary>The open's own Buffering. While the attach window holds the reports the song owns the deck under this very
        /// epoch, so a Buffering from the video would stall the bar, SMTC and the Connect wire while the song plays (and never
        /// clear after a paused cut, which starts no video). The tick's Buffering fold after the release still reports a real stall.</summary>
        static void PostBufferingUnlessHeld(uint epoch)
        {
            lock (s_gate) { if (s_holdReports) return; }
            PostSignal(epoch, AudioSignal.Buffering);
        }

        /// <summary>One typed fault per load. The error (when the engine gave one) decides what the reducer's retry
        /// does: a transient one is re-opened IN PLACE once (<see cref="HostRules.MayReopenInPlace"/>), and a DRM source
        /// is forgotten by the memo only when its signed urls may be what died or the plain re-open is spent
        /// (<see cref="HostRules.InvalidateManifest"/>). A dead LOCAL attachment is quarantined by the shell so the
        /// reducer's one retry resolves past it.</summary>
        static void ReportFault(uint epoch, Fault kind, string message, MediaError? err = null)
        {
            VideoSource? live;
            string key;
            bool reopenNow;
            lock (s_gate)
            {
                if (s_errorReported) return;
                s_errorReported = true;
                live = s_live;
                key = s_key;
                reopenNow = HostRules.MayReopenInPlace(HostRules.IsReopenable(err), key, s_reopenedKey);
                s_faultRetryable = reopenNow;
            }
            StopTicker();
            Log.Warn("video", $"[video] fault={kind} key={Tail(key)} reopen={reopenNow} — {message}");
            if (live is not null && HostRules.InvalidateManifest(live.IsDrm, err?.UnderlyingCode, reopenNow)) ManifestMemo.Invalidate(live.Key);
            if (live is { FilePath: not null, PlayableUri.Length: > 0 } local && OnOverrideFailed is { } quarantine)
            {
                string uri = local.PlayableUri, sourceKey = local.OverrideSourceKey ?? "";
                ToUi(() => quarantine(uri, sourceKey));
            }
            Post(Input.Audio(AudioSignal.Failed, epoch, FrameNowMs(), (long)kind));
        }

        /// <summary>The last 24 characters of a key. A module playable is 60+ characters of base64 that differs from its
        /// neighbours only in the tail, so a full log line is unreadable and a prefix is useless.</summary>
        static string Tail(string key) => key.Length <= 24 ? key : key[^24..];

        // ── 7. the UI-thread observation: phase, first frame, buffered, seek landed ─────────────────────────────────

        static IMediaSession? s_observedSession;
        static long s_observedFirstFrame;
        // F225: FrameNowMs the bound session's first frame was observed (0 = none yet for this session). The keeper reads it
        // off the UI thread to hold the next row's licence until the watched session is past its startup.
        static long s_firstFrameAtMs;
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
                Volatile.Write(ref s_firstFrameAtMs, 0);
                s_observedIndexEpoch = -1;
            }
            if (session is null)
            {
                // F152: the player outlives its session. With no source published it is CLOSED (idle), not attaching — only a
                // loaded source with no session yet is an open in the air.
                if (Source.Peek() is null)
                {
                    if (Phase.Peek() is not (SwitchPhase.Resolving or SwitchPhase.Buffering)) Phase.SetIfChanged(SwitchPhase.Idle);
                }
                else Phase.SetIfChanged(SwitchPhase.Attaching);
                return;
            }

            long firstFrame;
            long firstFrameQpc = 0;                // F215: the native FIRSTFRAMEREADY QPC (0 = this session has none)
            bool engineSeeking;
            if (session is ProtectedMediaSession ps)
            {
                IProtectedVideoPlayer pv = ps.Player;
                Phase.SetIfChanged(HostRules.PhaseOf(pv.Phase));
                engineSeeking = pv.IsSeeking;
                firstFrame = pv.FirstFrameEpoch;
                firstFrameQpc = pv.FirstFrameQpc;  // read AFTER the epoch: the engine stamps the QPC before it bumps the epoch
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
                // The MF session's own first-frame epoch, like the protected branch: NaturalSize is NOT a first-frame signal
                // (it is the metadata size, known before any frame of this source presents). Another clear backend that has
                // no epoch keeps the size heuristic.
                firstFrame = session is MfMediaSession mf ? mf.FirstFrameEpoch : !p.NaturalSize.Peek().IsEmpty ? 1 : 0;
                bool framed = firstFrame != 0;
                Phase.SetIfChanged(HostRules.PhaseOfClear(p.State.Peek(), framed));
                engineSeeking = p.Buffering.Peek().Reason == BufferingReason.Seeking;
            }
            SettleOwedSeek(p, engineSeeking);

            if (firstFrame == 0 || firstFrame == s_observedFirstFrame) return;
            s_observedFirstFrame = firstFrame;
            long observedAtMs = FrameNowMs();
            Volatile.Write(ref s_firstFrameAtMs, observedAtMs);
            FirstFrame.Value = FirstFrame.Peek() + 1;
            SizeI natural = p.NaturalSize.Peek();
            // F215: sinceSwitchMs is the NATIVE first-frame instant when the player has one (a fullscreen stage that unmounted the
            // pump used to make it 2.4 s late: sinceSwitchMs=4575 against a native sinceAttachMs=2121); observedLateMs is how long
            // after the frame this observation ran.
            LogLine(new VideoLog.FirstFrame(Tail(s_key), s_epoch,
                HostRules.FirstFrameSinceSwitchMs(Interlocked.Read(ref s_switchAtMs), Interlocked.Read(ref s_switchAtQpc), firstFrameQpc, observedAtMs), -1,
                (long)p.Position.Peek().TotalMilliseconds, natural.Width, natural.Height,
                HostRules.ObservedLateMs(firstFrameQpc, System.Diagnostics.Stopwatch.GetTimestamp())));
        }

        /// <summary>UI THREAD. Pay the <c>Seeked</c> the reducer is owed for the last seek it emitted (V-PA2): once
        /// <see cref="HostRules.SeekLanded"/> says it landed, post it with the landed position, the generation the seek was
        /// stamped with and the load epoch of the session that took it. The reducer then publishes
        /// <c>LastSeekLandedGen</c>, which releases the seek bar's drop-point hold (V-PA12). A newer seek replaced the debt (its
        /// own <c>Seeked</c> is the one that counts); a load or a stop cleared it. Idempotent: the debt is cleared before the post.</summary>
        static void SettleOwedSeek(MediaPlayer p, bool engineSeeking)
        {
            OwedSeek owed = s_owed;
            if (owed.Gen == 0) return;
            long pos = Math.Max(0, (long)p.Position.Peek().TotalMilliseconds);
            if (!HostRules.SeekLanded(engineSeeking, owed.Rode, pos, owed.TargetMs)) return;
            s_owed = default;
            Post(Input.Audio(AudioSignal.Seeked, owed.Epoch, FrameNowMs(), pos, owed.Gen));
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
