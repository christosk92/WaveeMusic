// ── Playback/Playback.Video.Warm.cs ────────────────────────────────────────────────────────────────────────────────
// the warm keeper: what the app holds in hand BEFORE the user asks for a video. NAMED PARTIAL of
// Playback/Playback.Video.cs (the 30 % rule: the host is at its budget with the pump, the watchdog and the prefetch
// slot already in it)
//
// Role: SHELL
// Owner: H
// Wave: 3 (gap batch B7)
// Budget: 200 lines
// Spec: docs/plans/wavee/wavee-0.3-video-engine-implementation.md §3.1.5, §3.5; D15; the pure half is
//       `WarmPolicy` in Playback/Playback.Video.Rules.cs
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHY THIS EXISTS, in numbers (live log, session sid=515080bf). A cold song→video switch measured 2 477 ms to the
// first frame. Of that, `license.start` → `license.ok` was 2 010 ms (cached=false) and `runtime.create` →
// `runtime.ready` 521 ms; the manifest resolved in ~50 ms and was already memoised. So the licence IS the switch, and
// the one thing that makes a switch feel instant is having the content key before the user clicks.
//
// WHAT IT DOES. While a video surface is wanted (`PlacementPost.WantsVideo`), a 10 s beat keeps the content keys of
// the row that is PLAYING and of the next queued one in the runtime's KID cache. Pre-acquiring a key is also what
// keeps the native runtime alive: `ProtectedVideoRuntime.EnsureLicense` brings the runtime up when it is down and
// hands its reference straight back, so the engine's own 30 s warm-idle window is what finally sheds it.
//
// D15 ("runtime never idle while placement ≠ None, 30 s after off"), honestly. There is no PUBLIC way for the app to
// hold a reference on the native runtime — `Acquire`/`Release` are the engine's own — so the first half is kept as
// "never idle for longer than one beat": a runtime that shed is recreated by the next `WarmPolicy.HeartbeatMs` tick,
// on an api thread and off the user's switch. The second half is exact: when the surface closes the beat stops, the
// keeper drops the prepared session and the KIDs it was holding, and nothing in the app extends the engine's window.
//
// AUDIO ALWAYS WINS (§3.1.4). The beat never touches the api pool while a track is opening
// (`Playback.PhaseSignal == Loading`), and it queues at most ONE warm item at a time — a licence POST can never be
// what makes the music wait.
//
// THREADS. `KeepWarm` / `ApplyPrepareAhead` are UI thread; the beat runs on a `Timer` thread and does nothing but
// decide; every resolve and every POST is on an api thread (C9). `s_warmGate` is only ever taken on its own or
// AFTER `s_gate`, never before it.

using System.Threading;

using FluentGpu.WindowsApi.Media.PlayReady;

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        /// <summary>What the keeper is asked to hold in hand: whether a video surface is wanted at all, whether an audio
        /// load is in flight RIGHT NOW (warming always yields to it), and the playing row and the next queued one with
        /// the manifest ids their catalogue rows carry.</summary>
        public readonly record struct WarmRequest(
            bool VideoOn, bool AudioBusy, EntityId Current, string CurrentManifestId, EntityId Next, string NextManifestId)
        {
            public static readonly WarmRequest Off = new(false, false, default, "", default, "");
        }

        static readonly object s_warmGate = new();
        static WarmRequest s_warm = WarmRequest.Off;
        static Timer? s_warmTimer;
        static long s_warmOffAtMs;
        static int s_warmBusy;
        // What the keeper already tried: each target's row and the KID its manifest named. A beat whose keys are all in
        // hand costs two dictionary probes and queues nothing at all.
        static EntityId s_warmCurrentId, s_warmNextId;
        static string s_warmCurrentKid = "", s_warmNextKid = "";

        /// <summary>UI THREAD (`Video.HostObserver`). Tell the keeper what to hold warm. Idempotent — a request equal to
        /// the live one leaves the beat exactly as it is, which matters because this runs off a signal effect.</summary>
        public static void KeepWarm(in WarmRequest request)
        {
            bool arm;
            lock (s_warmGate)
            {
                if (s_warm.Equals(request)) return;
                if (s_warm.VideoOn && !request.VideoOn) s_warmOffAtMs = FrameNowMs();
                else if (request.VideoOn) s_warmOffAtMs = 0;
                s_warm = request;
                // Nothing to keep warm and nothing left to let go of: no beat at all, so a session that never opens a
                // video surface never arms this timer once.
                arm = request.VideoOn || s_warmOffAtMs > 0;
            }
            if (!arm) return;
            if (request.VideoOn) Boot();
            ArmWarmTimer();
        }

        /// <summary>The Playback tab's writer: pre-acquisition was turned on or off, so re-decide the beat NOW instead of
        /// at the next track. UI thread.</summary>
        public static void ApplyPrepareAhead() => ArmWarmTimer();

        static void ArmWarmTimer()
        {
            if (s_disposed) return;
            s_warmTimer ??= new Timer(static _ => WarmBeat(), null, Timeout.Infinite, Timeout.Infinite);
            try { s_warmTimer.Change(0, WarmPolicy.HeartbeatMs); } catch { }
        }

        static void StopWarmTimer()
        {
            try { s_warmTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        static void DisposeWarmTimer()
        {
            StopWarmTimer();
            try { s_warmTimer?.Dispose(); } catch { }
            s_warmTimer = null;
        }

        /// <summary>The beat. Three outcomes and no fourth: keep the two keys in hand while a surface is wanted, count
        /// quietly down while one is not, and LET GO <see cref="WarmPolicy.ShedMs"/> after it closed.</summary>
        static void WarmBeat()
        {
            if (s_disposed) { StopWarmTimer(); return; }
            WarmRequest r;
            long offAt;
            EntityId knownCurrent, knownNext;
            string currentKid, nextKid;
            lock (s_warmGate)
            {
                r = s_warm;
                offAt = s_warmOffAtMs;
                knownCurrent = s_warmCurrentId; currentKid = s_warmCurrentKid;
                knownNext = s_warmNextId; nextKid = s_warmNextKid;
            }
            bool prepareAhead = Platform.Settings.Get(PrepareAhead);

            if (!WarmPolicy.Beats(r.VideoOn, prepareAhead))
            {
                // offAt == 0 is "there was never anything warm to let go of" — stop rather than tick for the process life.
                if (offAt == 0 || WarmPolicy.Sheds(r.VideoOn, prepareAhead, FrameNowMs() - offAt)) Shed(offAt > 0);
                return;
            }
            if (Volatile.Read(ref s_warmBusy) != 0) return;            // one warm item in the api pool, ever
            bool current = WarmPolicy.Acquires(r.VideoOn, prepareAhead, r.AudioBusy, !NeedsKey(r.Current, knownCurrent, currentKid));
            bool next = WarmPolicy.Acquires(r.VideoOn, prepareAhead, r.AudioBusy, !NeedsKey(r.Next, knownNext, nextKid));
            if (!current && !next) return;
            if (Interlocked.Exchange(ref s_warmBusy, 1) != 0) return;
            if (!Spotify.Api.Run(() => WarmWork(r, current, next))) Volatile.Write(ref s_warmBusy, 0);
        }

        /// <summary>Is this row's content key still to fetch? A row with no id needs nothing, and a row nobody has tried
        /// yet always does. For a row already tried: no KID means it has no protected video (or its resolve failed) and
        /// the beat leaves it alone until the row itself changes; a key that is Usable or Pending is never asked for
        /// twice, because a second challenge for one KID is the one the CDM rejects; and a key that FAILED is not
        /// hammered either — only a cache the runtime lost (None, after it shed) or an expiry is re-acquired.</summary>
        static bool NeedsKey(EntityId id, EntityId attempted, string kid)
        {
            if (id.IsEmpty) return false;
            if (!id.Equals(attempted)) return true;
            if (kid.Length == 0) return false;
            return ProtectedVideoRuntime.Shared.LicenseStateFor(kid) is LicenseCacheState.None or LicenseCacheState.Expired;
        }

        /// <summary>API THREAD, at most one at a time. The playing row first: that is the switch the user is one click
        /// away from.</summary>
        static void WarmWork(WarmRequest r, bool current, bool next)
        {
            try
            {
                // The row is recorded WHATEVER came back, including "" for a row with no protected video: the beat must
                // remember what it has already tried, or a row that answers nothing is re-resolved every ten seconds.
                if (current)
                {
                    string kid = WarmRow(r.Current, r.CurrentManifestId, PrefetchReason.Current);
                    lock (s_warmGate) { s_warmCurrentId = r.Current; s_warmCurrentKid = kid; }
                }
                if (next)
                {
                    string kid = WarmRow(r.Next, r.NextManifestId, PrefetchReason.Next);
                    lock (s_warmGate) { s_warmNextId = r.Next; s_warmNextKid = kid; }
                }
            }
            catch (Exception ex) { Log.Warn("video", "warm pass failed", ex); }
            finally { Volatile.Write(ref s_warmBusy, 0); }
        }

        /// <summary>One row's content key, pre-acquired: the memoised resolve (a manifest already in hand costs no GET)
        /// and then the proactive acquisition. Answers the KID the manifest named, so the next beat can watch the key
        /// land without asking for it again.</summary>
        static string WarmRow(EntityId id, string manifestId, PrefetchReason why)
        {
            if (id.IsEmpty) return "";
            VideoSource? source;
            try { source = ResolveCore(id, manifestId, CancellationToken.None, forPlayback: false); }
            catch (Exception ex) { Log.Warn("video", "warm resolve failed", ex); return ""; }
            if (source is not { IsDrm: true }) return "";
            LogLine(new VideoLog.PrefetchPlanned(Tail(id.Text), PrefetchLevel.ManifestAndLicense, why, Platform.Network.IsMetered));
            StartLicense(source);
            return source.DrmDescriptor!.DefaultKid ?? "";
        }

        /// <summary>D15's second half. The beat stops, the prepared session the keeper may still be holding is disposed
        /// and the KID memory is dropped: from here the native runtime's own warm-idle window is the only thing keeping
        /// it alive, and nothing in the app is extending it.</summary>
        static void Shed(bool wasWarm)
        {
            StopWarmTimer();
            CondemnPreparing();
            ReleasePrepared(keepKey: null);
            lock (s_warmGate)
            {
                s_warmOffAtMs = 0;
                s_warmCurrentId = default; s_warmCurrentKid = "";
                s_warmNextId = default; s_warmNextKid = "";
            }
            if (wasWarm) Log.Info("video", "[video] warm.shed afterMs=" + WarmPolicy.ShedMs);
        }
    }
}
