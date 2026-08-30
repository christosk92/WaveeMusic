using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.SpotifyLive.Audio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHY THIS EXISTS — the video→video supersession wedge.
//
// The native PlayReady/CENC session behind a DRM video is a PROCESS-GLOBAL SINGLETON with a SESSION-LESS ABI:
// FgPlayReadyRunEx / FgPlayReadyStop / FgPlayReadyPlay / FgPlayReadyGetSnapshot take no session handle (see
// DesktopProtectedVideoPlayer + the ERROR_BUSY self-heal comment in its RunNative). So exactly ONE session may exist at a
// time, and a Stop issued for session A lands on whatever session currently holds the latch — including a SUCCESSOR that
// just took it.
//
// FluentVideoMediaHost.LoadVideo used to tear the previous player down FIRE-AND-FORGET (`_ = DisposePlayerAsync(old)`) and
// then immediately build+open the successor. Two threadpool work items therefore raced on one global native session:
//   • predecessor teardown:  player.Stop() → FgPlayReadyStop() → thread.Join(3s)
//   • successor open:        FgPlayReadyPlay() seed → new MTA thread → FgPlayReadyRunEx()
// When the successor won the latch first, the predecessor's global Stop shut the SUCCESSOR down. RunEx then returned a
// SUCCESS hr, so nothing reported an error; the snapshot settled on native state 4 (stopped) → ProtectedVideoState.Stopped
// → PlaybackState.Idle — a state the host's Tick switch has no case for. Result: no signal, ever. Silent wedge.
//
// This pump removes the race by construction: every load and every clear runs on ONE logical worker, the predecessor's
// teardown is AWAITED TO COMPLETION before the successor is built, and a request that is already superseded is never built
// at all (coalescing — only the LATEST wins). It is deliberately engine-free (System + BCL only) so the ordering contract
// is unit-tested against production code rather than a mock of it — the same discipline as PlacementCore/MediaSwitchLogic.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The serialized, coalescing load pump for the video-media host: a single logical worker that guarantees
/// <c>teardown(previous) → build(next)</c> ordering, never builds a request it already knows is stale, and stamps every
/// request with a monotonic <see cref="Epoch"/> so an in-flight build can abandon itself the moment it is superseded.
/// <para>No lock is ever held across an <c>await</c>, and the delegates run on a threadpool worker — never on a host-signal
/// callback — so the track-end "no locks in a signal callback / bounded joins only" discipline is preserved.</para>
/// </summary>
/// <typeparam name="TSource">The resolved video source (production: <c>PopOutVideoSource</c>).</typeparam>
public sealed class VideoLoadPump<TSource> where TSource : class
{
    readonly Func<long, Task> _teardownAsync;
    readonly Func<TSource, long, Task> _buildAsync;
    readonly Func<TSource, bool>? _isAlreadyLive;
    readonly WaveeLogger _log;

    readonly object _g = new();
    TSource? _pending;          // the coalescing slot — only the LATEST requested source survives here
    bool _pendingClear;
    bool _running;
    long _epoch;
    Task _worker = Task.CompletedTask;

    /// <summary>Create a pump over the host's teardown/build steps.</summary>
    /// <param name="teardownAsync">Tear the CURRENT session fully down (bounded — the caller owns its own timeout). The
    /// pump awaits this to completion before any successor is built, which is what stops a predecessor's process-global
    /// native Stop from landing on the successor.</param>
    /// <param name="buildAsync">Build + OPEN the successor. The pump awaits it, so the next teardown can never race a
    /// half-opened native session (the "mid-open teardown" half of the wedge).</param>
    /// <param name="isAlreadyLive">Optional idempotence predicate: a dequeued request the host is already playing is
    /// dropped without a teardown or a rebuild (re-entering the video path for the current track must never restart it
    /// from 0). Evaluated at DEQUEUE time, so it reflects the state after any preceding teardown.</param>
    public VideoLoadPump(Func<long, Task> teardownAsync, Func<TSource, long, Task> buildAsync,
                         Func<TSource, bool>? isAlreadyLive = null, WaveeLogger log = default)
    {
        _teardownAsync = teardownAsync ?? throw new ArgumentNullException(nameof(teardownAsync));
        _buildAsync = buildAsync ?? throw new ArgumentNullException(nameof(buildAsync));
        _isAlreadyLive = isAlreadyLive;
        _log = log;
    }

    /// <summary>The monotonic request stamp. Every <see cref="Request"/>/<see cref="RequestClear"/> bumps it, so a build
    /// that captured an older value knows it is stale (<see cref="IsStale"/>) and must abandon itself.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>True once a NEWER request has arrived — the caller (a build in flight) must stop and let the pump take
    /// the newest one instead.</summary>
    public bool IsStale(long epoch) => Interlocked.Read(ref _epoch) != epoch;

    /// <summary>True while the worker is draining (a load/clear is queued or in flight).</summary>
    public bool IsBusy { get { lock (_g) return _running; } }

    /// <summary>Queue a load. Non-blocking. If a load is already queued it is REPLACED (only the latest wins — an
    /// intermediate track the user already skipped past is never built).</summary>
    public long Request(TSource source)
    {
        lock (_g)
        {
            long e = ++_epoch;
            _pending = source;
            _pendingClear = false;
            EnsureWorker();
            return e;
        }
    }

    /// <summary>Queue a teardown with no successor (the host's Stop). Also invalidates any queued/in-flight load, so a
    /// stop can never be overtaken by a load that was already on its way.</summary>
    public long RequestClear()
    {
        lock (_g)
        {
            long e = ++_epoch;
            _pending = null;
            _pendingClear = true;
            EnsureWorker();
            return e;
        }
    }

    /// <summary>Await quiescence — the pump has torn down/built everything requested so far. Test + dispose helper; never
    /// called on the UI or a signal-callback thread.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task w;
            lock (_g)
            {
                if (!_running) return;
                w = _worker;
            }
            try { await w.ConfigureAwait(false); } catch { }
        }
    }

    // Caller holds _g. Task.Run cannot enter RunAsync's lock until we release, so the _worker assignment is safe.
    void EnsureWorker()
    {
        if (_running) return;
        _running = true;
        _worker = Task.Run(RunAsync);
    }

    async Task RunAsync()
    {
        while (true)
        {
            TSource? next;
            bool clear;
            long epoch;
            lock (_g)
            {
                next = _pending;
                clear = _pendingClear;
                _pending = null;
                _pendingClear = false;
                epoch = _epoch;
                if (next is null && !clear) { _running = false; return; }
            }

            // Idempotence: a redundant load of the source already playing is a no-op — no teardown, no rebuild, no
            // restart from 0 (a placement flip, a re-published source, a kind re-evaluation all land here).
            if (next is not null && _isAlreadyLive is { } live)
            {
                bool already;
                try { already = live(next); }
                catch (Exception ex) { already = false; _log.Info($"video load pump: liveness probe threw: {ex.GetType().Name}: {ex.Message}"); }
                if (already) continue;
            }

            // 1. The predecessor goes away FIRST, and completely. This is the serialization point: the process-global
            //    native session is released (bounded inside the delegate) before anything asks for it again.
            try { await _teardownAsync(epoch).ConfigureAwait(false); }
            catch (Exception ex) { _log.Info($"video load pump: teardown failed: {ex.GetType().Name}: {ex.Message}"); }

            if (next is null) continue;   // a clear — nothing to build

            // 2. Coalesce: a newer request landed while we were tearing down. Never build a session we already know is
            //    stale — that is exactly the "two opens 250ms apart" shape that wedged the native singleton.
            bool superseded;
            lock (_g) superseded = _pending is not null || _pendingClear || _epoch != epoch;
            if (superseded) { _log.Info("video load superseded during teardown — skipping its build (the newer source wins)"); continue; }

            try { await _buildAsync(next, epoch).ConfigureAwait(false); }
            catch (Exception ex) { _log.Info($"video load pump: build failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
