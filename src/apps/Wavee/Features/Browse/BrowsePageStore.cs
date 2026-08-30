using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using Wavee.Core;

namespace Wavee.Features.Browse;

/// <summary>Session cache for browse CATEGORY pages (<see cref="BrowsePage"/>), keyed by <c>pageUri</c> — the same
/// eviction problem <see cref="BrowseDirectoryStore"/> solves for the directory, one level deeper. The shell's
/// <c>Flow.KeepAlive</c> holds only 8 slots (ContentHost.cs); revisiting a category the user drilled into earlier in
/// the session, after enough OTHER navigation evicted its slot, cold-remounts <see cref="BrowsePage"/> — its
/// <c>UseResource</c> load refetches from the network and the page renders its skeleton (short content) for both the
/// outgoing and incoming half of the page swap. This store makes that remount — and the incoming half of the swap —
/// paint the last successful load INSTANTLY: full content right away instead of a shimmer.
///
/// <para>Bounded FIFO at 16 slots (twice <see cref="HomeSectionPreviewStore"/>'s cap — a browse session drills into
/// more distinct category pages per visit than it drills into home sections), same queue-of-keys + dict eviction
/// idiom as that store.</para>
///
/// <para>Written from the <c>UseResource</c> loader — which runs off the UI thread (<c>ResourceCell.Launch</c>'s
/// <c>Task.Run</c>) — and from a detached background-refresh task (see <see cref="BrowsePage"/>'s stale path), then
/// read from the UI thread during Render. Same reason as <see cref="BrowseDirectoryStore"/> (a writer thread that
/// isn't the reader thread): this store takes a lock, unlike write-from-UI-thread-only siblings like
/// <see cref="HomeSectionPreviewStore"/>.</para></summary>
sealed class BrowsePageStore
{
    public static readonly Context<BrowsePageStore?> Slot = new(null);

    const int Capacity = 16;

    readonly object _gate = new();
    readonly Dictionary<string, (BrowsePageModel Page, long AtMs)> _map = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    /// <summary>True when a cached page exists for <paramref name="pageUri"/>; <paramref name="fresh"/> reports
    /// whether it is still inside <see cref="BrowseDirectoryStore"/>'s shared TTL (the one owner of that value — see
    /// its own doc comment) — the caller decides FRESH/STALE policy off that flag, mirroring
    /// <see cref="BrowseDirectory"/>'s <c>LoadCategoriesAsync</c>.</summary>
    public bool TryGet(string pageUri, out BrowsePageModel page, out bool fresh)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(pageUri, out var entry))
            {
                page = entry.Page;
                fresh = BrowseDirectoryStore.IsFresh(entry.AtMs);
                return true;
            }
        }
        page = null!;
        fresh = false;
        return false;
    }

    public void Set(string pageUri, BrowsePageModel page)
    {
        lock (_gate)
        {
            if (!_map.ContainsKey(pageUri)) _order.Enqueue(pageUri);
            _map[pageUri] = (page, Environment.TickCount64);
            while (_map.Count > Capacity && _order.TryDequeue(out var old)) _map.Remove(old);
        }
    }
}
