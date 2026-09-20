namespace Wavee.Backend;

/// <summary>Leading+trailing coalescer for fire-and-forget side effects. Post() runs the action
/// immediately if the channel has been quiet for the window; otherwise it schedules ONE trailing
/// run for after the window, always executing the latest posted action. Thread-safe; Dispose
/// cancels any pending trailing run.</summary>
public sealed class TrailingCoalescer : IDisposable
{
    readonly int _windowMs;
    readonly Func<long> _now;
    readonly Func<int, CancellationToken, Task> _delay;
    readonly object _gate = new();
    long _lastRunMs;
    Action? _pending;
    CancellationTokenSource? _cts;

    public TrailingCoalescer(int windowMs, Func<long>? now = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _windowMs = windowMs;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
        _lastRunMs = -windowMs;
    }

    public void Post(Action action)
    {
        lock (_gate)
        {
            long t = _now();
            if (_cts is null && t - _lastRunMs >= _windowMs)
                _lastRunMs = t;
            else
            {
                _pending = action;
                ArmTrailing();
                return;
            }
        }
        action();
    }

    void ArmTrailing()
    {
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = RunTrailingAsync(cts);
    }

    async Task RunTrailingAsync(CancellationTokenSource cts)
    {
        try { await _delay(_windowMs, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        Action? run;
        lock (_gate)
        {
            run = _pending;
            _pending = null;
            _cts = null;
            _lastRunMs = _now();
            cts.Dispose();
        }
        run?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _pending = null;
        }
    }
}
