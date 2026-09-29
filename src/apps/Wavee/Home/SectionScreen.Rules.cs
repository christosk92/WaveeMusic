// ── Home/SectionScreen.Rules.cs ────────────────────────────────────────────────────────────────────────────────────
// The tiny pure rules `Home/SectionScreen.UI.cs` needs: the charts eager-walk fraction and the "close enough to the
// tail to page" / "can auto-page right now" predicates. Written FRESH — no reference to the old
// `HomeSectionPaging.WalkFraction` / `HomeSectionPageView` (Entities/Home.Rules.cs, Entities/Home.Page.cs); those
// were read only to learn the shape of the problem (RAW cursor vs. deduped count, a total that under-reports). The
// actual cursor arithmetic (`SectionPaging`, the `Section` handle) is DATA layer and stays — this file adds nothing
// that duplicates it, only the walk-progress estimate and the two small booleans the UI branches on.
//
// Role: CORE (pure; no engine, no tables, no I/O)
// Owner: B5
// Wave: 3
// Spec: docs/plans/wavee/home-redesign-implementation.md — Workstream H, New files table row `SectionScreen.UI.cs`
//   ("the charts walk bar"); "Pure rules" section (test files live under Wavee.Tests/HomeUi/, no source-text tests).

using System;

namespace Wavee.HomeUi;

/// <summary>How far a Charts drill's eager walk (Home/SectionScreen.UI.cs's <c>Walk</c>) is along, 0..1 — purely an
/// ESTIMATE for the determinate walk bar, never a termination signal (the walk stops on the section's own cursor
/// arithmetic, <c>SectionPaging.CanAdvance</c>, not on this fraction reaching 1).</summary>
public static class SectionWalk
{
    /// <summary>A total that already under-reports (at or below what's on screen) is worth one assumed page more, so
    /// the bar never reads "done" while a request is still about to land. <paramref name="assumedPageSize"/> is
    /// clamped to at least 1 so a caller passing 0 can't divide the bar into an all-zero denominator.</summary>
    public static float Fraction(int haveCount, int total, int assumedPageSize)
    {
        int denom = total > haveCount ? total : haveCount + Math.Max(1, assumedPageSize);
        if (denom <= 0) return 0f;
        float f = haveCount / (float)denom;
        return f < 0f ? 0f : f > 1f ? 1f : f;
    }
}

/// <summary>The grid's silent append-on-scroll rule (non-chart sections only — Charts self-walks, see
/// <see cref="SectionWalk"/>): when the realized window is close enough to the end that a fetch should already be in
/// flight, and whether one may actually be started right now.</summary>
public static class SectionScreenRules
{
    /// <summary>Default how-close-to-the-end trigger, in items: the same "a screen or so of runway" idea a PagedShelf
    /// preloader uses, expressed as a plain item count so the grid doesn't need to know its own row height.</summary>
    public const int DefaultNearTailThreshold = 8;

    /// <summary>True once the realized window's tail is within <paramref name="threshold"/> items of the end of the
    /// list. <paramref name="lastRealizedExclusive"/> is the EXCLUSIVE upper bound `ItemsView`/`VirtualListEl` report
    /// (the same "last, exclusive" contract as <c>ListOptions.OnVisibleRange</c>); an empty or invalid window (0 or
    /// negative <paramref name="itemCount"/>) never triggers.</summary>
    public static bool NearTail(int lastRealizedExclusive, int itemCount, int threshold = DefaultNearTailThreshold)
        => itemCount > 0 && lastRealizedExclusive >= itemCount - Math.Max(0, threshold);

    /// <summary>May the silent auto-page fire right now? Charts NEVER auto-pages this way — its own eager
    /// <c>Walk</c> owns every request — and a request already in flight or a section already latched exhausted both
    /// refuse a second one.</summary>
    public static bool CanAutoPage(bool isChart, bool hasMore, bool loading, bool exhausted)
        => !isChart && hasMore && !loading && !exhausted;
}
