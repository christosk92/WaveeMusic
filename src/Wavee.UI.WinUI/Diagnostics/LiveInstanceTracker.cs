using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Diagnostics;

/// <summary>
/// Diagnostics-only registry of in-flight ViewModel instances tracked via
/// <see cref="WeakReference"/>. Lets the memory panel show whether
/// <c>Deactivate()</c> is actually releasing a ViewModel — a positive count
/// long after navigating away is the signature of a leak.
///
/// Not thread-safe by-instance, but Snapshot is — read it from the UI
/// dispatcher only. Costs are negligible (one weak ref per VM construction).
/// </summary>
public static class LiveInstanceTracker
{
    private static readonly Dictionary<Type, List<WeakReference>> _refs = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Register a constructed instance for tracking. Call from VM constructors —
    /// the tracker keeps only a weak reference, so registering does not extend
    /// the instance's lifetime.
    /// </summary>
    public static void Register(object instance)
    {
        if (instance is null) return;
        var type = instance.GetType();
        lock (_lock)
        {
            if (!_refs.TryGetValue(type, out var list))
            {
                list = new List<WeakReference>(8);
                _refs[type] = list;
            }
            list.Add(new WeakReference(instance));
        }
    }

    /// <summary>
    /// Counts of live (uncollected) instances per type. Walks the registry,
    /// compacting dead refs as it goes — calling this opportunistically keeps
    /// memory usage of the tracker itself bounded.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Snapshot()
    {
        var result = new Dictionary<string, int>();
        lock (_lock)
        {
            foreach (var (type, list) in _refs)
            {
                int alive = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].IsAlive)
                    {
                        alive++;
                    }
                    else
                    {
                        list.RemoveAt(i);
                    }
                }
                if (alive > 0)
                    result[type.Name] = alive;
            }
        }
        return result;
    }
}
