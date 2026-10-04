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
// D15 ("runtime never idle while placement ≠ None, 30 s after off"), for the rows that need it. The keeper holds ONE
// `ProtectedVideoRuntime.TakeKeepAlive()` token while one of its targets resolved to a PROTECTED source (its manifest
// named a KID) or a protected session is live (F165, `WarmPolicy.HoldsRuntime`): the engine's warm-idle destroy is
// refcounted, so while the token is held the runtime — and the KID license cache that lives and dies with it — cannot
// shed. Without it (measured, sid=db4da393) a track that had been badge-prefetched 30 s earlier paid the whole challenge
// again at the switch (`license.acquire … cached=false`, 632 ms of the 966 ms to the first frame): the beat re-acquired
// keys into a cache the idle timer had just wiped. When both targets are clear (no row, or no protected video) the token
// is not taken, or is let go: a user who plays songs with the surface on never pays a second D3D11 device, a media
// engine, the CDM and the PMP process for nothing. `Shed` disposes the token too, and from then on the engine's own
// window is the only thing keeping the runtime alive.
//
// WARM ON INTENT (F218). A user who has asked for a video this session and listens to a row that carries one with the
// surface off is one click from the cold switch (runtime bring-up 450-675 ms + a cold challenge): the beat keeps the
// PLAYING row's key and the runtime warm for them too (`WarmPolicy.WarmsOnIntent`); the next row is left alone. The
// intent beat keeps the runtime reference and the KID memory, but a PREPARED session (a segment store and a runtime
// reference of its own) is still dropped once the surface has been off for `WarmPolicy.ShedMs`, as the shed always did.
//
// WHAT IT LEAVES ALONE (F225). While the video host owns the playing row its own switch acquired the key, so the
// keeper only re-acquires an EXPIRY for it (the KID is read off the live source, the one place it is known) and takes its
// protected/clear verdict from that source too; and the next row's challenge waits `WarmPolicy.NextAfterFirstFrameMs`
// past the first frame so its CDM round trips do not interleave with the session's startup on the native runtime thread.
// A manifest that is already being fetched is never joined (F167): the waiter would park an api worker.
//
// AUDIO ALWAYS WINS (§3.1.4). The beat never touches the api pool while a track is opening
// (`Playback.PhaseSignal == Loading`), and it queues at most ONE warm item at a time — a licence POST can never be
// what makes the music wait.
//
// THREADS. `KeepWarm` / `ApplyPrepareAhead` are UI thread; the beat runs on a `Timer` thread and does nothing but
// decide; every resolve and every POST is on an api thread (C9). `s_warmGate` is only ever taken on its own or
// AFTER `s_gate`, never before it, and NOTHING native is called while it is held (F161): `TakeKeepAlive` can wait on a
// runtime teardown, and the UI thread's `KeepWarm` takes the same gate. `s_warmEpoch` is what makes that safe against a
// Shed: a token taken with no lock held is published only if no Shed ran since the beat that wanted it began.

using System.Threading;

using FluentGpu.WindowsApi.Media.PlayReady;

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        /// <summary>What the keeper is asked to hold in hand: whether a video surface is wanted at all, whether an audio
        /// load is in flight RIGHT NOW (warming always yields to it), the playing row and the next queued one with the
        /// manifest ids their catalogue rows carry, and whether the video host already owns the playing row (F225).</summary>
        public readonly record struct WarmRequest(
            bool VideoOn, bool AudioBusy, EntityId Current, string CurrentManifestId, EntityId Next, string NextManifestId,
            bool HostOwns = false)
        {
            public static readonly WarmRequest Off = new(false, false, default, "", default, "");
        }

        static readonly object s_warmGate = new();
        static WarmRequest s_warm = WarmRequest.Off;
        static Timer? s_warmTimer;
        static long s_warmOffAtMs;
        static int s_warmBusy;
        // Bumped (under the gate) by every Shed. A beat or warm pass carries the epoch it started in; one that finishes after
        // a Shed must not record its row or publish a keep-alive into a keeper whose timer has stopped (F161, F165).
        static int s_warmEpoch;
        // 1 once the intent beat dropped the parked prepare for this surface-off period; reset at each surface edge, so a
        // prefetch that lands later (a new track) is not condemned again by every beat.
        static int s_warmPrepShed;
        // What the keeper already tried: each target's row and the KID its manifest named. A beat whose keys are all in
        // hand costs two dictionary probes and queues nothing at all.
        static EntityId s_warmCurrentId, s_warmNextId;
        static string s_warmCurrentKid = "", s_warmNextKid = "";
        // The runtime reference the beat holds (D15's first half). Taken by the first beat or warm pass that finds a
        // PROTECTED target (a KID-bearing row, or a protected session live), let go by `SyncRuntimeHold` once both targets
        // are clear and by Shed. Null while nothing protected is wanted, and while the native component is missing
        // (StartupError says why) — a beat with a protected target then keeps re-trying, which is also what recreates a
        // runtime the user's OS killed.
        static IDisposable? s_warmKeepAlive;
        // F218: the user has asked for a video (a surface wanted with a video row playing) at some point this session.
        static int s_videoUsed;

        /// <summary>UI THREAD (`Video.HostObserver`). Tell the keeper what to hold warm. Idempotent — a request equal to
        /// the live one leaves the beat exactly as it is, which matters because this runs off a signal effect.</summary>
        public static void KeepWarm(in WarmRequest request)
        {
            bool arm;
            lock (s_warmGate)
            {
                if (s_warm.Equals(request)) return;
                if (s_warm.VideoOn != request.VideoOn) Volatile.Write(ref s_warmPrepShed, 0);
                if (s_warm.VideoOn && !request.VideoOn) s_warmOffAtMs = FrameNowMs();
                else if (request.VideoOn) s_warmOffAtMs = 0;
                if (request.VideoOn && !request.Current.IsEmpty) Volatile.Write(ref s_videoUsed, 1);
                s_warm = request;
                // Nothing to keep warm and nothing left to let go of: no beat at all, so a session that never opens a
                // video surface never arms this timer once. A held runtime, or an intent warm for the playing row, does.
                arm = request.VideoOn || s_warmOffAtMs > 0 || s_warmKeepAlive is not null
                      || WarmPolicy.WarmsOnIntent(request.VideoOn, true, Volatile.Read(ref s_videoUsed) != 0, !request.Current.IsEmpty);
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

        /// <summary>The beat. Three outcomes and no fourth: keep the keys in hand while a surface is wanted (or, on intent,
        /// for the playing row), count quietly down while one is not, and LET GO <see cref="WarmPolicy.ShedMs"/> after
        /// it closed. The intent beat lets go of the parked prepare at that point and keeps the rest.</summary>
        static void WarmBeat()
        {
            if (s_disposed) { StopWarmTimer(); return; }
            WarmRequest r;
            long offAt;
            int epoch;
            EntityId knownCurrent, knownNext;
            string currentKid, nextKid;
            lock (s_warmGate)
            {
                r = s_warm;
                offAt = s_warmOffAtMs;
                epoch = s_warmEpoch;
                knownCurrent = s_warmCurrentId; currentKid = s_warmCurrentKid;
                knownNext = s_warmNextId; nextKid = s_warmNextKid;
            }
            bool prepareAhead = Platform.Settings.Get(PrepareAhead);
            bool beats = WarmPolicy.Beats(r.VideoOn, prepareAhead);
            bool intent = WarmPolicy.WarmsOnIntent(r.VideoOn, prepareAhead, Volatile.Read(ref s_videoUsed) != 0, !r.Current.IsEmpty);

            if (!beats && !intent)
            {
                // offAt == 0 is "there was never anything warm to let go of" — stop rather than tick for the process life.
                if (offAt == 0 || WarmPolicy.Sheds(r.VideoOn, prepareAhead, FrameNowMs() - offAt)) Shed(offAt > 0);
                return;
            }
            if (!beats && offAt > 0 && WarmPolicy.Sheds(r.VideoOn, prepareAhead, FrameNowMs() - offAt)
                && Interlocked.Exchange(ref s_warmPrepShed, 1) == 0)
            {
                // The intent beat never reaches Shed, so the parked prepare is let go here, once per surface-off period; the
                // runtime reference and the KID memory stay, which is the point of warming on intent.
                CondemnPreparing();
                ReleasePrepared(keepKey: null);
            }
            SyncRuntimeHold(in r, intent, epoch);
            if (Volatile.Read(ref s_warmBusy) != 0) return;            // one warm item in the api pool, ever
            // The row the video host owns was loaded by the host's own switch, so the keeper's memory has no KID for it: its
            // expiry is probed with the live source's.
            EntityId probeId = knownCurrent;
            string probeKid = currentKid;
            if (r.HostOwns) { LiveFacts(out _, out _, out probeKid); probeId = r.Current; }
            bool current = beats
                ? WarmPolicy.Acquires(r.VideoOn, prepareAhead, r.AudioBusy, !NeedsKey(r.Current, probeId, probeKid, expiryOnly: r.HostOwns))
                : WarmPolicy.AcquiresOnIntent(intent, r.AudioBusy, !NeedsKey(r.Current, probeId, probeKid, expiryOnly: r.HostOwns));
            // The next row is a surface-wanted target only (an intent beat warms the playing row alone), and while the
            // video host owns the playing row it waits out that session's startup (F225).
            bool next = beats && WarmPolicy.NextMayStart(r.HostOwns, SinceFirstFrameMs())
                && WarmPolicy.Acquires(r.VideoOn, prepareAhead, r.AudioBusy, !NeedsKey(r.Next, knownNext, nextKid, expiryOnly: false));
            if (!current && !next) return;
            if (Interlocked.Exchange(ref s_warmBusy, 1) != 0) return;
            if (!Spotify.Api.Run(() => WarmWork(r, current, next, intent, epoch))) Volatile.Write(ref s_warmBusy, 0);
        }

        /// <summary>Milliseconds since the bound session's first frame, -1 while it has none.</summary>
        static long SinceFirstFrameMs()
        {
            long at = Volatile.Read(ref s_firstFrameAtMs);
            return at <= 0 ? -1 : FrameNowMs() - at;
        }

        /// <summary>Is this row's content key still to fetch? A row with no id needs nothing, and a row nobody has tried
        /// yet always does. For a row already tried: no KID means it has no protected video (or its resolve failed) and
        /// the beat leaves it alone until the row itself changes; a key that is Usable or Pending is never asked for
        /// twice, because a second challenge for one KID is the one the CDM rejects; and a key that FAILED is not
        /// hammered either — only a cache the runtime lost (None, after it shed) or an expiry is re-acquired.
        /// <paramref name="expiryOnly"/>: the video host owns the row, only an expiry counts (F225); the caller passes the
        /// live source's KID as <paramref name="kid"/> for it, and the row as its own <paramref name="attempted"/>.</summary>
        static bool NeedsKey(EntityId id, EntityId attempted, string kid, bool expiryOnly)
        {
            if (id.IsEmpty) return false;
            bool tried = id.Equals(attempted);
            bool known = tried && kid.Length > 0;
            bool lost = false, expired = false;
            if (known)
            {
                LicenseCacheState state = ProtectedVideoRuntime.Shared.LicenseStateFor(kid);
                lost = state == LicenseCacheState.None;
                expired = state == LicenseCacheState.Expired;
            }
            return WarmPolicy.NeedsKey(expiryOnly, tried, known, lost, expired);
        }

        /// <summary>API THREAD, at most one at a time. The playing row first: that is the switch the user is one click
        /// away from.</summary>
        static void WarmWork(WarmRequest r, bool current, bool next, bool intent, int epoch)
        {
            try
            {
                // The row is recorded WHATEVER came back, including "" for a row with no protected video: the beat must
                // remember what it has already tried, or a row that answers nothing is re-resolved every ten seconds.
                // A row SKIPPED (null: its manifest is already in flight) is not recorded, so the next beat asks again, and
                // nothing is recorded or held for a pass that outlived a Shed: that keeper started over without it.
                if (current)
                {
                    string? kid = WarmRow(r.Current, r.CurrentManifestId, PrefetchReason.Current);
                    if (kid is not null) lock (s_warmGate) { if (s_warmEpoch == epoch) { s_warmCurrentId = r.Current; s_warmCurrentKid = kid; } }
                }
                if (next)
                {
                    string? kid = WarmRow(r.Next, r.NextManifestId, PrefetchReason.Next);
                    if (kid is not null) lock (s_warmGate) { if (s_warmEpoch == epoch) { s_warmNextId = r.Next; s_warmNextKid = kid; } }
                }
                SyncRuntimeHold(in r, intent, epoch);   // the KID just learnt is what says whether the runtime is worth pinning
            }
            catch (Exception ex) { Log.Warn("video", "warm pass failed", ex); }
            finally { Volatile.Write(ref s_warmBusy, 0); }
        }

        /// <summary>One row's content key, pre-acquired: the memoised resolve (a manifest already in hand costs no GET)
        /// and then the proactive acquisition. Answers the KID the manifest named, so the next beat can watch the key
        /// land without asking for it again; null when the row was skipped because its manifest is already being fetched
        /// (F167: joining that fetch would park this api worker on its task).</summary>
        static string? WarmRow(EntityId id, string manifestId, PrefetchReason why)
        {
            if (id.IsEmpty) return "";
            if (PrefetchRace.ShouldSkipInFlight(manifestId, ManifestMemo.IsInFlight(manifestId))) return null;
            VideoSource? source;
            try { source = ResolveCore(id, manifestId, CancellationToken.None, forPlayback: false); }
            catch (Exception ex) { Log.Warn("video", "warm resolve failed", ex); return ""; }
            if (source is not { IsDrm: true }) return "";
            LogLine(new VideoLog.PrefetchPlanned(Tail(id.Text), PrefetchLevel.ManifestAndLicense, why, Platform.Network.IsMetered));
            StartLicense(source);
            return source.DrmDescriptor!.DefaultKid ?? "";
        }

        /// <summary>F165: pin the native runtime only while a target is PROTECTED, let it go when both are clear. Beat
        /// thread and api thread; reads the keeper's memory under the gate and calls into the runtime with no lock held. A
        /// caller from before the last Shed (<paramref name="epoch"/> no longer current) changes nothing.</summary>
        static void SyncRuntimeHold(in WarmRequest r, bool intent, int epoch)
        {
            EntityId knownCurrent, knownNext;
            string currentKid, nextKid;
            bool held;
            lock (s_warmGate)
            {
                if (s_warmEpoch != epoch) return;
                knownCurrent = s_warmCurrentId; currentKid = s_warmCurrentKid;
                knownNext = s_warmNextId; nextKid = s_warmNextKid;
                held = s_warmKeepAlive is not null;
            }
            LiveFacts(out bool liveKnown, out bool protectedLive, out _);
            // The row the host owns is judged by the live source: the keeper never resolved it (see CurrentTargetOf).
            WarmTarget current = WarmPolicy.CurrentTargetOf(
                r.HostOwns, liveKnown, protectedLive, !r.Current.IsEmpty, r.Current.Equals(knownCurrent), currentKid.Length > 0);
            // An intent beat warms the playing row alone: the next row is no target of it.
            WarmTarget next = WarmPolicy.TargetOf(!intent && !r.Next.IsEmpty, r.Next.Equals(knownNext), nextKid.Length > 0);
            if (WarmPolicy.HoldsRuntime(current, next, protectedLive, held)) HoldRuntime(epoch);
            else ReleaseRuntime();
        }

        /// <summary>What the live source says: is there one yet, is it protected, and the KID it named ("" when none). Read
        /// under <c>s_gate</c>, so never call this while <c>s_warmGate</c> is held.</summary>
        static void LiveFacts(out bool known, out bool drm, out string kid)
        {
            lock (s_gate)
            {
                VideoSource? live = s_live;
                known = live is not null;
                drm = live is { IsDrm: true };
                kid = live?.DrmDescriptor?.DefaultKid ?? "";
            }
        }

        /// <summary>D15's first half: while the beat has a protected target, the keeper holds the native runtime's
        /// reference so the engine's warm-idle destroy — and the license-cache wipe that comes with it — cannot fire
        /// between beats. Idempotent; a failed native bring-up leaves it null and the next beat tries again.
        /// <para>F161: <c>TakeKeepAlive</c> runs with NO lock held — its slow path takes the runtime's lifecycle gate, which
        /// an idle teardown holds across a native join of up to ~2.5 s, and the UI thread's <see cref="KeepWarm"/> takes
        /// <c>s_warmGate</c>. The flag is read under the gate, the token taken outside it and published under it; a
        /// duplicate (another beat won) is disposed. A Shed can run completely between the take and the publish: the
        /// token is published only while <paramref name="epoch"/> is still the keeper's, else it is disposed too, so no
        /// keep-alive outlives a Shed.</para></summary>
        static void HoldRuntime(int epoch)
        {
            lock (s_warmGate) { if (s_warmKeepAlive is not null || s_warmEpoch != epoch) return; }
            IDisposable? token = ProtectedVideoRuntime.Shared.TakeKeepAlive();
            if (token is null) return;
            bool published;
            lock (s_warmGate)
            {
                published = s_warmKeepAlive is null && s_warmEpoch == epoch;
                if (published) s_warmKeepAlive = token;
            }
            if (published) Log.Info("video", "[video] warm.hold runtime keep-alive taken");
            else token.Dispose();
        }

        /// <summary>F165: both targets are clear — give the reference back; the runtime's own warm-idle window finishes the
        /// job. Disposed outside the gate (Release may run the idle timer's bookkeeping).</summary>
        static void ReleaseRuntime()
        {
            IDisposable? keepAlive;
            lock (s_warmGate) { keepAlive = s_warmKeepAlive; s_warmKeepAlive = null; }
            if (keepAlive is null) return;
            keepAlive.Dispose();
            Log.Info("video", "[video] warm.release runtime keep-alive let go: no protected target");
        }

        /// <summary>D15's second half. The beat stops, the prepared session the keeper may still be holding is disposed,
        /// the KID memory is dropped and the runtime reference is let go: from here the native runtime's own warm-idle
        /// window is the only thing keeping it alive, and nothing in the app is extending it.</summary>
        static void Shed(bool wasWarm)
        {
            StopWarmTimer();
            CondemnPreparing();
            ReleasePrepared(keepKey: null);
            IDisposable? keepAlive;
            lock (s_warmGate)
            {
                s_warmEpoch++;   // anything still in flight from before this Shed must not publish into the keeper after it
                s_warmOffAtMs = 0;
                s_warmCurrentId = default; s_warmCurrentKid = "";
                s_warmNextId = default; s_warmNextKid = "";
                keepAlive = s_warmKeepAlive; s_warmKeepAlive = null;
            }
            keepAlive?.Dispose();   // outside the gate: Release may run the idle timer's bookkeeping
            if (wasWarm) Log.Info("video", "[video] warm.shed afterMs=" + WarmPolicy.ShedMs);
        }
    }
}
