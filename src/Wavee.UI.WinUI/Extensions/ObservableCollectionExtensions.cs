using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
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
        RaiseResetResilient(collection);
    }

    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    /// <summary>
    /// Raise the <see cref="NotifyCollectionChangedAction.Reset"/> that re-syncs
    /// bound controls, surviving a transient native rejection.
    ///
    /// <para>The notification re-enters subscribers synchronously. An
    /// <c>ItemsRepeater</c> that is mid-layout during a page cross-fade — or whose
    /// composition surfaces were dropped by the nav cache — can reject the Reset
    /// with a native <c>E_FAIL</c> (<see cref="COMException"/> 0x80004005), or, once
    /// it has been torn down, with an <see cref="ObjectDisposedException"/> from the
    /// CsWinRT projection. The backing list has already been updated by the time we
    /// get here, so on that specific transient failure we re-raise the Reset once on
    /// the next dispatcher tick (Low priority), by when the control has settled. Without this, the
    /// exception would unwind the caller's whole section-apply and strand the shelf
    /// in shimmer forever (e.g. album "More by artist" / artist "Fans also like").
    /// Both the native <c>E_FAIL</c> form (<see cref="COMException"/>) and the
    /// CsWinRT <see cref="ObjectDisposedException"/> form (a disposed
    /// <c>IObjectReference</c> for an already-torn-down control) are caught — same
    /// teardown condition surfaced two ways. Genuine managed binding/template
    /// errors still propagate so they aren't masked.</para>
    /// </summary>
    private static void RaiseResetResilient<T>(ObservableCollection<T> collection)
    {
        try
        {
            Accessors<T>.RaiseCollectionChanged(collection, ResetArgs);
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            // A bound control rejected the Reset because it is mid-teardown / still
            // transitioning. The runtime surfaces this two ways for the same cause:
            // COMException (E_FAIL) from a live-but-failing native call, or
            // ObjectDisposedException from CsWinRT's IObjectReference.ThrowIfDisposed
            // when the native control is already disposed (e.g. an ItemsRepeater
            // inside an x:Load shelf clearing realized elements during navigation).
            // Treat both identically: retry once next tick, then give up — the
            // backing list is already correct, so a later re-show / re-bind renders it.
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
                throw; // off the UI thread — not the transient-control case; surface it.

            System.Diagnostics.Debug.WriteLine(
                $"[ReplaceWith] bound control rejected Reset ({ex.GetType().Name} 0x{ex.HResult:X8}); retrying next tick. {ex.Message}");

            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    Accessors<T>.RaiseCollectionChanged(collection, ResetArgs);
                }
                catch (Exception retryEx) when (retryEx is COMException or ObjectDisposedException)
                {
                    // Second failure (control torn down / still transitioning): give up.
                    // The backing list is correct, so a later re-show / re-bind shows it.
                }
            });
        }
    }

    public static void ClearWithoutNotify<T>(this ObservableCollection<T> collection)
    {
        ref var backing = ref Accessors<T>.GetItems(collection);
        backing.Clear();
    }
}
