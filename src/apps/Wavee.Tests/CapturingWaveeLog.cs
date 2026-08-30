using System;
using System.Collections.Generic;
using Wavee;

namespace Wavee.Tests;

// A thread-safe IWaveeLog test double: records every Log/Event with its level/category/message, and drives IsEnabled off
// a settable MinLevel so WaveeLogger's level gating can be exercised. Paired with CountingFormattable to prove that a
// filtered interpolated message is never built.
public sealed class CapturingWaveeLog : IWaveeLog
{
    readonly object _gate = new();
    public readonly record struct Entry(WaveeLogLevel Level, string Category, string Message, string? EventId, Exception? Ex);

    readonly List<Entry> _entries = new();
    public WaveeLogLevel MinLevel { get; set; } = WaveeLogLevel.Trace;

    public IReadOnlyList<Entry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public Entry Last { get { lock (_gate) return _entries[^1]; } }

    public bool IsEnabled(WaveeLogLevel level) => level >= MinLevel;

    public void Log(WaveeLogLevel level, string category, string message, Exception? ex = null)
    {
        lock (_gate) _entries.Add(new Entry(level, category, message, null, ex));
    }

    public void Event(WaveeLogLevel level, string category, string eventId, string message,
        string? operationId = null, long elapsedMs = -1, Exception? ex = null, params WaveeLogField[] fields)
    {
        lock (_gate) _entries.Add(new Entry(level, category, message, eventId, ex));
    }
}

// ToString increments a shared counter — a filtered log call must leave the count at 0 (no message build).
public sealed class CountingFormattable
{
    public int Count;
    public override string ToString() { Count++; return "built"; }
}
