using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Extensions;

public static class ObservableCollectionExtensions
{
    public static void InsertRange<T>(this ObservableCollection<T> collection, int index, IEnumerable<T> items)
    {
        if (collection == null || items == null) return;
        if (index < 0 || index > collection.Count) return;

        foreach (var item in items)
        {
            collection.Insert(index++, item);
        }
    }

    public static void Sort<T>(this ObservableCollection<T> collection, Comparison<T> comparison)
    {
        using var _ = Services.UiOperationProfiler.Instance?.Profile("CollectionSort");
        var sorted = collection.ToList();
        sorted.Sort(comparison);
        // Bulk replace instead of O(n^2) individual Move() calls.
        // Fires 1 Reset + N Add events instead of up to N^2 Move events.
        collection.ReplaceWith(sorted);
    }

    public static void Sort<T, TKey>(this ObservableCollection<T> collection, Func<T, TKey> keySelector, bool descending = false)
    {
        Comparison<T> comparison = (x, y) =>
        {
            var keyX = keySelector(x);
            var keyY = keySelector(y);
            return descending
                ? Comparer<TKey>.Default.Compare(keyY, keyX)
                : Comparer<TKey>.Default.Compare(keyX, keyY);
        };

        collection.Sort(comparison);
    }

    /// <summary>
    /// Replaces the entire collection content efficiently.
    /// Clears and repopulates in one pass.
    /// </summary>
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }
}
