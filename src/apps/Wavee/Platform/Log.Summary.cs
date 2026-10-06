// ── Platform/Log.Summary.cs ────────────────────────────────────────────────────────────────────────────────────────
// RoutineSummary: the steady-state log policy for per-request lines (wire.call, audio.range)
//
// Role: CORE (engine-free)
//
// A routine request used to write one Info line each: ~10 MB of log every few hours of ordinary listening, and the
// anomalies (a slow call, a refusal, a stall) buried in it. Now a routine request only COUNTS here; the caller still
// writes its own line for anything that is not routine (an error status, a fault, a slow call, the first lines of a
// stream), and this class writes ONE summary line per window. No timer: the window is checked on the next request and
// on `FlushAll` (the memory sampler's frame-driven tick calls it), so an idle app wakes nothing for logging.

using System.Globalization;

namespace Wavee;

public sealed class RoutineSummary
{
    public const long DefaultWindowMs = 60_000;

    static readonly List<RoutineSummary> s_all = new();

    readonly string _category, _line;
    readonly long _windowMs;
    readonly Action<string, string> _write;
    readonly Func<long> _now;
    readonly object _gate = new();
    long _windowStart;
    long _count, _bytes, _maxMs, _sumMs;

    RoutineSummary(string category, string line, long windowMs, Action<string, string>? write, Func<long>? now)
    {
        _category = category; _line = line; _windowMs = windowMs;
        _write = write ?? ((c, t) => Log.Info(c, t));
        _now = now ?? (() => Environment.TickCount64);
        _windowStart = _now();
    }

    /// <summary>An unregistered summary with an injected writer and clock (tests).</summary>
    public static RoutineSummary Standalone(string category, string line, long windowMs, Action<string, string> write, Func<long> now)
        => new(category, line, windowMs, write, now);

    /// <summary>One summary per window; registered so <see cref="FlushAll"/> reaches it.</summary>
    public static RoutineSummary Create(string category, string line, long windowMs = DefaultWindowMs)
    {
        var s = new RoutineSummary(category, line, windowMs, null, null);
        lock (s_all) s_all.Add(s);
        return s;
    }

    /// <summary>Count one routine request; writes the window's summary when it has elapsed.</summary>
    public void Note(long bytes, long ms)
    {
        string? text = null;
        lock (_gate)
        {
            _count++; _bytes += Math.Max(0, bytes); _sumMs += ms;
            if (ms > _maxMs) _maxMs = ms;
            long now = _now();
            if (now - _windowStart >= _windowMs) text = TakeLocked(now);
        }
        if (text is not null) _write(_category, text);
    }

    /// <summary>Write the pending window if it holds anything and has run its full length (or <paramref name="force"/>).</summary>
    public void Flush(bool force = false)
    {
        string? text = null;
        lock (_gate)
        {
            long now = _now();
            if (_count > 0 && (force || now - _windowStart >= _windowMs)) text = TakeLocked(now);
        }
        if (text is not null) _write(_category, text);
    }

    public static void FlushAll(bool force = false)
    {
        RoutineSummary[] all;
        lock (s_all) all = s_all.ToArray();
        foreach (var s in all) s.Flush(force);
    }

    string? TakeLocked(long now)
    {
        long windowSeconds = Math.Max(1, (now - _windowStart + 500) / 1000);
        string? text = _count == 0 ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"{_line} window={windowSeconds}s n={_count} bytes={_bytes} avgMs={_sumMs / _count} maxMs={_maxMs}");
        _windowStart = now; _count = _bytes = _maxMs = _sumMs = 0;
        return text;
    }
}
