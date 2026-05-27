using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Extensions;

public static class ObservableCollectionExtensions
{
    // ── [UnsafeAccessor] accessors (correct generic pattern) ───────────
    //
    // [UnsafeAccessor] with generic types only resolves when the type
    // parameter sits on a CONTAINER CLASS, not on the accessor method
    // itself. Method-level type parameters trigger MissingFieldException /
    // MissingMethodException at runtime under both JIT and AOT — see
    // dotnet/runtime#104268, #109890, discussion #110964. The fix is to
    // declare a generic static container class with T on it and the extern
    // method as a non-generic member inside that class.
    //
    // GetItems reaches the private `items` field on Collection<T> directly
    // (the field name has been stable across every .NET release, see the
    // explicit "Do not rename (binary serialization)" comment in the
    // runtime source). Raise{Collection,Property}Changed call the protected
    // virtual methods on ObservableCollection<T>.

    private static class Accessors<T>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "items")]
        public static extern ref IList<T> GetItems(Collection<T> collection);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnCollectionChanged")]
        public static extern void RaiseCollectionChanged(
            ObservableCollection<T> collection,
            NotifyCollectionChangedEventArgs args);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnPropertyChanged")]
        public static extern void RaisePropertyChanged(
            ObservableCollection<T> collection,
            PropertyChangedEventArgs args);
    }

    // ── Public surface (unchanged signatures) ──────────────────────────

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
        using var _ = UiOperationProfiler.Instance?.Profile("CollectionSort");
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
    /// Implementation: reach the private `items` IList&lt;T&gt; field on
    /// Collection&lt;T&gt; via <see cref="Accessors{T}.GetItems"/>, mutate
    /// it directly (no events), then manually raise Count / Item[] / Reset
    /// via UnsafeAccessor-bound shims on ObservableCollection&lt;T&gt;.
    /// Same contract as ObservableCollection itself fires on Clear().
    /// </summary>
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        ref var backing = ref Accessors<T>.GetItems(collection);
        backing.Clear();
        foreach (var item in items)
            backing.Add(item);

        Accessors<T>.RaisePropertyChanged(collection, new PropertyChangedEventArgs(nameof(Collection<T>.Count)));
        Accessors<T>.RaisePropertyChanged(collection, new PropertyChangedEventArgs("Item[]"));
        Accessors<T>.RaiseCollectionChanged(collection, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public static void ClearWithoutNotify<T>(this ObservableCollection<T> collection)
    {
        ref var backing = ref Accessors<T>.GetItems(collection);
        backing.Clear();
    }
}
