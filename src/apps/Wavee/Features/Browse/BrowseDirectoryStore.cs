using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using Wavee.Core;

namespace Wavee.Features.Browse;

/// <summary>Session cache for the Browse directory's two loads (categories + the Charts deck). The shell's
/// <c>Flow.KeepAlive</c> holds only 8 page slots (ContentHost.cs); a long browsing session can still evict the
/// directory slot. Without this store, that eviction cold-remounts <see cref="BrowseDirectory"/>, its two
/// <c>UseResource</c> loads refetch from the network, and the page renders its skeleton (short content) — so the
/// ScrollView's keyed offset (<c>BrowseDirectoryPage</c>'s <c>ScrollKey = "browse"</c>) has nothing real to
/// restore against. This store makes a remount paint the last successful load INSTANTLY: full-height content right
/// away, so keyed scroll restoration lands on real layout instead of a skeleton.
///
/// Written from the <c>UseResource</c> loader — which runs off the UI thread (<c>ResourceCell.Launch</c>'s
/// <c>Task.Run</c>) — and from a detached background-refresh task (see <see cref="BrowseDirectory"/>'s stale path),
/// then read from the UI thread during Render. That is the one way this differs from its sibling
/// <see cref="HomeSectionPreviewStore"/> (writes only from UI-thread click handlers): a writer thread that isn't the
/// reader thread, so unlike that sibling this store takes a lock.</summary>
sealed class BrowseDirectoryStore
{
    public static readonly Context<BrowseDirectoryStore?> Slot = new(null);

    // Editorial browse content (categories, the Charts deck) moves on the hours scale, not the minute scale — this
    // TTL only keeps a remount from trusting a days-old cache after a long-idle session; it is not a freshness
    // source of truth (the service underneath already runs its own 6h TTL — see BrowseDirectory's load comment).
    const long TtlMs = 15 * 60_000;

    readonly object _gate = new();
    IReadOnlyList<BrowseCategory>? _categories;
    long _categoriesAtMs;
    IReadOnlyList<HomeSection>? _charts;
    long _chartsAtMs;

    public IReadOnlyList<BrowseCategory>? Categories { get { lock (_gate) return _categories; } }
    public long CategoriesAtMs { get { lock (_gate) return _categoriesAtMs; } }
    public IReadOnlyList<HomeSection>? Charts { get { lock (_gate) return _charts; } }
    public long ChartsAtMs { get { lock (_gate) return _chartsAtMs; } }

    public void SetCategories(IReadOnlyList<BrowseCategory> value)
    {
        lock (_gate) { _categories = value; _categoriesAtMs = Environment.TickCount64; }
    }

    public void SetCharts(IReadOnlyList<HomeSection> value)
    {
        lock (_gate) { _charts = value; _chartsAtMs = Environment.TickCount64; }
    }

    /// <summary>Whether a stamp minted by <see cref="Environment.TickCount64"/> is still inside the TTL. A zero stamp
    /// (nothing ever written) is never fresh.</summary>
    public static bool IsFresh(long stampMs) => stampMs != 0 && Environment.TickCount64 - stampMs < TtlMs;
}
