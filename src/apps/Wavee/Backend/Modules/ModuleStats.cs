using System;
using System.Threading;

namespace Wavee.Backend.Modules;

// ── PER-MODULE COUNTERS — what the diagnostics page reports about a module that is (or was) running ──────────────────
// Every host→module request is counted and timed here, so "the YouTube module is slow" / "it keeps crashing" is a
// number on a page rather than a hunch. Deliberately tiny: interlocked counters plus a fixed 128-sample latency ring
// (no allocation per request, no lock on the hot path).

/// <summary>An immutable read of a module's counters — what the diagnostics page renders.</summary>
/// <param name="Requests">Total host→module requests issued.</param>
/// <param name="Failures">How many of them ended in an error, a timeout or a broken pipe.</param>
/// <param name="Restarts">How many times the process was (re)started after the first launch.</param>
/// <param name="P50Ms">Median request latency over the retained samples (0 = no samples).</param>
/// <param name="P95Ms">95th-percentile request latency over the retained samples (0 = no samples).</param>
/// <param name="LastError">The most recent failure message, or null.</param>
/// <param name="LastErrorMethod">The method that produced <paramref name="LastError"/>, or null.</param>
public readonly record struct ModuleStatsSnapshot(
    long Requests, long Failures, long Restarts, int P50Ms, int P95Ms, string? LastError, string? LastErrorMethod);

/// <summary>Live counters for one module. Thread-safe; every member may be called from any thread.</summary>
public sealed class ModuleStats
{
    const int SampleCapacity = 128;

    readonly int[] _samples = new int[SampleCapacity];
    readonly Lock _sampleGate = new();
    int _sampleCount;
    int _sampleNext;

    long _requests;
    long _failures;
    long _restarts;
    string? _lastError;
    string? _lastErrorMethod;

    /// <summary>Record a completed request and its wall-clock duration.</summary>
    /// <param name="elapsedMs">How long the round trip took, in milliseconds.</param>
    public void NoteRequest(long elapsedMs)
    {
        Interlocked.Increment(ref _requests);
        int ms = (int)Math.Clamp(elapsedMs, 0, int.MaxValue);
        lock (_sampleGate)
        {
            _samples[_sampleNext] = ms;
            _sampleNext = (_sampleNext + 1) % SampleCapacity;
            if (_sampleCount < SampleCapacity) _sampleCount++;
        }
    }

    /// <summary>Record a failed request.</summary>
    /// <param name="method">The wire method that failed.</param>
    /// <param name="message">The failure message.</param>
    public void NoteFailure(string method, string message)
    {
        Interlocked.Increment(ref _failures);
        Volatile.Write(ref _lastError, message);
        Volatile.Write(ref _lastErrorMethod, method);
    }

    /// <summary>Record a process (re)start after the first launch.</summary>
    public void NoteRestart() => Interlocked.Increment(ref _restarts);

    /// <summary>Read every counter at once.</summary>
    public ModuleStatsSnapshot Snapshot()
    {
        int p50, p95;
        lock (_sampleGate)
        {
            if (_sampleCount == 0) { p50 = 0; p95 = 0; }
            else
            {
                var copy = new int[_sampleCount];
                Array.Copy(_samples, copy, _sampleCount);
                Array.Sort(copy);
                p50 = copy[(int)((copy.Length - 1) * 0.50)];
                p95 = copy[(int)((copy.Length - 1) * 0.95)];
            }
        }

        return new ModuleStatsSnapshot(
            Interlocked.Read(ref _requests), Interlocked.Read(ref _failures), Interlocked.Read(ref _restarts),
            p50, p95, Volatile.Read(ref _lastError), Volatile.Read(ref _lastErrorMethod));
    }
}
