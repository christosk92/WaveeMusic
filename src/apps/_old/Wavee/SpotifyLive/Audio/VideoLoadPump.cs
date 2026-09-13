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
// The FIRST fix removed the race by construction, at the cost of a mandatory `teardown(previous) → build(next)` on
// EVERY load — safe, but a guaranteed native session rebuild (CDM re-key, MF engine restart) on every video→video track
// skip. The video-smooth-switching rework moves that ordering decision OUT of the pump and INTO the host
// (`FluentVideoMediaHost.ApplyAsync` + `VideoSwitchPolicy`): a warm, long-lived player now switches sources IN PLACE
// (`SwitchInPlaceAsync`, no teardown, no unmount) whenever the previous session is not the one about to change, and only
// a first load, a faulted session, or a `RequestClear()` (the host's Stop/Dispose) pays for a `teardown` at all. What the
// pump still guarantees, unconditionally:
//   • every clear and every apply runs on ONE logical worker — never two overlapping (the process-global session can
//     still only ever be touched by one in-flight operation at a time);
//   • an apply that is already superseded before it is DEQUEUED is never started at all (latest-wins coalescing);
//   • a `RequestClear()` overtakes any load already queued behind it, so a Stop can never be raced by a load already on
//     its way.
// It is deliberately engine-free (System + BCL only) so the ordering contract is unit-tested against production code
// rather than a mock of it — the same discipline as PlacementCore/MediaSwitchLogic.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The serialized, coalescing load pump for the video-media host: a single logical worker that runs every clear and
/// every apply one at a time, never builds a request it already knows is stale, and stamps every request with a
/// monotonic <see cref="Epoch"/> so an in-flight apply can abandon itself the moment it is superseded.
/// <para>The pump itself no longer decides WHETHER a load tears the previous session down — that decision (no-op / seek
/// / switch-in-place / full rebuild) is the host's, made inside <c>applyAsync</c> via <c>VideoSwitchPolicy</c>. The pump's
/// only job is ordering: a clear always runs alone, an apply always runs alone, and only the latest of either survives
/// the coalescing slot.</para>
/// <para>No lock is ever held across an <c>await</c>, and the delegates run on a threadpool worker — never on a host-signal
/// callback — so the track-end "no locks in a signal callback / bounded joins only" discipline is preserved.</para>
/// </summary>
/// <typeparam name="TSource">The resolved video source (production: <c>VideoLoadRequest</c>, itself wrapping
/// <c>PopOutVideoSource</c>).</typeparam>
public sealed class VideoLoadPump<TSource> where TSource : class
{
    readonly Func<long, Task> _clearAsync;
    readonly Func<TSource, long, Task> _applyAsync;
    readonly WaveeLogger _log;

    readonly object _g = new();
    TSource? _pending;          // the coalescing slot — only the LATEST requested source survives here
    bool _pendingClear;
    bool _running;
    long _epoch;
    Task _worker = Task.CompletedTask;

    /// <summary>Create a pump over the host's clear/apply steps.</summary>
    /// <param name="clearAsync">Tear the CURRENT session fully down with no successor (bounded — the caller owns its own
    /// timeout). Runs ONLY for a <see cref="RequestClear"/> (the host's Stop/Dispose) — a load never triggers this on its
    /// own; a load that itself needs a teardown-then-rebuild (a first-ever load, a faulted session) calls the host's
    /// teardown step directly from inside <paramref name="applyAsync"/>, on this same worker.</param>
    /// <param name="applyAsync">Apply one load: the host inspects its own state (via <c>VideoSwitchPolicy</c>) and does
    /// whichever of none / seek / switch-in-place / rebuild is appropriate. The pump awaits it to completion before
    /// dequeuing anything else, so a later clear can never race a still-in-flight apply.</param>
    /// <param name="log">Optional diagnostics sink (defaults to the no-op logger).</param>
    public VideoLoadPump(Func<long, Task> clearAsync, Func<TSource, long, Task> applyAsync, WaveeLogger log = default)
    {
        _clearAsync = clearAsync ?? throw new ArgumentNullException(nameof(clearAsync));
        _applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        _log = log;
    }

    /// <summary>The monotonic request stamp. Every <see cref="Request"/>/<see cref="RequestClear"/> bumps it, so an apply
    /// that captured an older value knows it is stale (<see cref="IsStale"/>) and must abandon itself.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>True once a NEWER request has arrived — the caller (an apply in flight) must stop and let the pump take
    /// the newest one instead.</summary>
    public bool IsStale(long epoch) => Interlocked.Read(ref _epoch) != epoch;

    /// <summary>True while the worker is draining (a load/clear is queued or in flight).</summary>
    public bool IsBusy { get { lock (_g) return _running; } }

    /// <summary>Queue a load. Non-blocking. If a load is already queued it is REPLACED (only the latest wins — an
    /// intermediate track the user already skipped past is never applied).</summary>
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

    /// <summary>Await quiescence — the pump has cleared/applied everything requested so far. Test + dispose helper; never
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

            if (clear)
            {
                // A clear never has a successor in the same dequeue (Request/RequestClear are mutually exclusive in the
                // coalescing slot) — run it alone and go back for whatever arrives next.
                try { await _clearAsync(epoch).ConfigureAwait(false); }
                catch (Exception ex) { _log.Info($"video load pump: clear failed: {ex.GetType().Name}: {ex.Message}"); }
                continue;
            }

            // next is guaranteed non-null here: the early-return above already handled the "nothing pending" case.
            try { await _applyAsync(next!, epoch).ConfigureAwait(false); }
            catch (Exception ex) { _log.Info($"video load pump: apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
