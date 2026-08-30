using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee;

/// <summary>
/// The open detail page's live-refresh driver: immediate leading pass, single-flight execution, and one coalesced
/// trailing pass after a mandatory cooldown. New store signals never cancel a pass that is already publishing.
/// </summary>
sealed class DetailLiveRefresh : IDisposable
{
    /// <summary>The cooldown after every pass. It bounds sustained traffic at twenty passes per second.</summary>
    public const int SettleMs = 50;
    public const int StormWindowMs = 10_000;
    public const int StormPasses = 40;

    readonly Func<CancellationToken, Task> _pass;
    readonly Func<int, CancellationToken, Task> _delay;
    readonly Func<long> _nowMs;
    readonly Action<long>? _onStorm;
    // Deliberately never Dispose()d: passes hold this token and a disposed source turns later registrations into
    // ObjectDisposedException. Cancellation is the teardown signal and is sufficient here.
    readonly CancellationTokenSource _life = new();
    readonly object _gate = new();
    bool _running, _dirty;
    long _passes;
    long _windowStartMs;
    int _windowPasses;
    bool _windowStarted, _stormReported;

    /// <param name="pass">One cache-only refresh load plus publish.</param>
    /// <param name="delay">Test seam for the post-pass cooldown.</param>
    /// <param name="nowMs">Monotonic-millisecond test seam for the storm tripwire.</param>
    /// <param name="onStorm">Called at most once per ten-second window when more than forty passes start.</param>
    public DetailLiveRefresh(Func<CancellationToken, Task> pass, Func<int, CancellationToken, Task>? delay = null,
                             Func<long>? nowMs = null, Action<long>? onStorm = null)
    {
        _pass = pass ?? throw new ArgumentNullException(nameof(pass));
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _onStorm = onStorm;
    }

    /// <summary>How many passes have started.</summary>
    public long Passes => Interlocked.Read(ref _passes);

    /// <summary>True while a pass or its post-pass cooldown owns the single-flight slot.</summary>
    public bool Busy { get { lock (_gate) return _running; } }

    /// <summary>Run immediately when idle; otherwise fold this request into one trailing pass.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_life.IsCancellationRequested) return;
            if (_running) { _dirty = true; return; }
            _running = true;
        }
        _ = RunAsync();
    }

    async Task RunAsync()
    {
        var ct = _life.Token;
        while (true)
        {
            CountPass();
            try { await _pass(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* a failed background refresh keeps the current content */ }

            try { await _delay(SettleMs, ct).ConfigureAwait(false); }
            catch
            {
                lock (_gate) _running = false;
                return;
            }

            lock (_gate)
            {
                if (!_dirty || ct.IsCancellationRequested) { _running = false; return; }
                _dirty = false;
            }
        }
    }

    void CountPass()
    {
        Interlocked.Increment(ref _passes);
        long now = _nowMs();
        if (!_windowStarted || now - _windowStartMs >= StormWindowMs || now < _windowStartMs)
        {
            _windowStarted = true;
            _windowStartMs = now;
            _windowPasses = 0;
            _stormReported = false;
        }
        _windowPasses++;
        if (_windowPasses <= StormPasses || _stormReported) return;
        _stormReported = true;
        try { _onStorm?.Invoke(_windowPasses); }
        catch { /* diagnostics must never break the refresh pump */ }
    }

    public void Dispose()
    {
        try { _life.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
