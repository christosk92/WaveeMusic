using System;
using System.Collections.Generic;
using System.Threading;

namespace Wavee.Core.Diagnostics;

/// <summary>Shared, engine-independent attribution used by the UI and the backend.</summary>
public static class PerformanceDiagnostics
{
    public static NavigationDiagnosticClock Navigation { get; } = new(TimeProvider.System);
    public static DiagnosticOwnerRegistry Owners { get; } = new();
}

/// <summary>A monotonic navigation anchor. UI route identity/de-duplication stays with navigation.</summary>
public sealed class NavigationDiagnosticClock(TimeProvider timeProvider)
{
    readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    long _startedAt;
    int _hasNavigation;

    public void MarkNavigation()
    {
        Volatile.Write(ref _startedAt, _time.GetTimestamp());
        Volatile.Write(ref _hasNavigation, 1);
    }

    /// <summary>NaN until a route has actually been reported; independent of wall-clock corrections.</summary>
    public double SinceNavigationMs
    {
        get
        {
            if (Volatile.Read(ref _hasNavigation) == 0) return double.NaN;
            long startedAt = Volatile.Read(ref _startedAt);
            return _time.GetElapsedTime(startedAt, _time.GetTimestamp()).TotalMilliseconds;
        }
    }
}

public readonly record struct DiagnosticOwnerReport(string Name, long RegistrationId, string Value);

/// <summary>Each registration has its own lifetime, even when several live runtimes use the same owner names.</summary>
public sealed class DiagnosticOwnerRegistry
{
    readonly object _gate = new();
    readonly List<Entry> _entries = new();
    long _nextId;
    readonly record struct Entry(string Name, long Id, Func<string> Report);

    public IDisposable Register(string name, Func<string> report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            long id = checked(++_nextId);
            _entries.Add(new(name, id, report));
            return new Registration(this, id);
        }
    }

    /// <summary>Callbacks run outside the registry lock. A sample already in progress may include a just-disposed
    /// owner; callback failures are reported individually so one unavailable owner cannot hide the others.</summary>
    public DiagnosticOwnerReport[] Sample()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        var result = new DiagnosticOwnerReport[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            string value;
            try { value = entry.Report(); }
            catch (Exception ex) { value = "error=" + ex.GetType().Name; }
            result[i] = new(entry.Name, entry.Id, value);
        }
        return result;
    }

    void Unregister(long id)
    {
        lock (_gate)
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Id == id)
                {
                    _entries.RemoveAt(i);
                    return;
                }
    }

    sealed class Registration(DiagnosticOwnerRegistry owner, long id) : IDisposable
    {
        DiagnosticOwnerRegistry? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unregister(id);
    }
}
