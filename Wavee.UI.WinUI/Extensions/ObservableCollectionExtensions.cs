using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Extensions;

public static class ObservableCollectionExtensions
{
    // Cached per-T reflection handles. Populated on first call for each generic instantiation.
    // Both handles are on base types of ObservableCollection<T> so they exist in every
    // runtime we build for; if reflection ever returns null we fall back to Clear+Add.
    private static class CollectionReflection<T>
    {
        public static readonly PropertyInfo? ItemsProperty =
            typeof(Collection<T>).GetProperty("Items",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static readonly MethodInfo? OnCollectionChanged =
            typeof(ObservableCollection<T>).GetMethod("OnCollectionChanged",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(NotifyCollectionChangedEventArgs) },
                modifiers: null);

        public static readonly MethodInfo? OnPropertyChanged =
            typeof(ObservableCollection<T>).GetMethod("OnPropertyChanged",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(PropertyChangedEventArgs) },
                modifiers: null);
    }

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
    /// Replaces the entire collection content and emits ONE Reset notification
    /// instead of N Add notifications.
    ///
    /// WinUI's ListView (and any other INotifyCollectionChanged consumer)
    /// processes each Add event individually on the UI thread. For a 3000-item
    /// playlist the naive Clear+Add loop produces 3000 CollectionChanged events
    /// on the dispatcher → visible hang on load/sort/filter. A single Reset
    /// lets the ListView rebuild its item tracking in one pass.
    ///
    /// Implementation: reach past ObservableCollection and mutate the protected
    /// backing <see cref="Collection{T}.Items"/> list directly (no events), then
    /// manually raise Count / Item[] / Reset via reflection. Same contract as
    /// ObservableCollection itself fires on Clear(). Falls back to the plain
    /// Clear+Add path if reflection ever fails.
    /// </summary>
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var backing = CollectionReflection<T>.ItemsProperty?.GetValue(collection) as IList<T>;
        var onChanged = CollectionReflection<T>.OnCollectionChanged;
        var onPropChanged = CollectionReflection<T>.OnPropertyChanged;

        if (backing is null || onChanged is null || onPropChanged is null)
        {
            collection.Clear();
            foreach (var item in items)
                collection.Add(item);
            return;
        }

        backing.Clear();
        foreach (var item in items)
            backing.Add(item);

        onPropChanged.Invoke(collection, new object[] { new PropertyChangedEventArgs(nameof(Collection<T>.Count)) });
        onPropChanged.Invoke(collection, new object[] { new PropertyChangedEventArgs("Item[]") });
        onChanged.Invoke(collection, new object[] { new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset) });
    }
}
