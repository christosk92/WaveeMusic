using System;

namespace Wavee.Core.Catalog;

/// <summary>What a cache-maintenance tick does next.</summary>
public enum CatalogSweepAction
{
    /// <summary>Nothing is evictable. The sweep's whole-table scans would delete zero rows, so they are not run
    /// at all — a skipped sweep and a zero-row sweep produce the same <c>CatalogEviction</c>.</summary>
    Skip,
    /// <summary>Foreground work owns the commit owner, or a surface opened inside the reveal window: come back
    /// after <see cref="CatalogSweepDecision.Delay"/>.</summary>
    Defer,
    /// <summary>Run one bounded batch now.</summary>
    Run,
}

/// <summary>A tick's answer plus the reason the always-on <c>catalog.sweep</c> line prints.</summary>
public readonly record struct CatalogSweepDecision(CatalogSweepAction Action, TimeSpan Delay, string Reason)
{
    public static CatalogSweepDecision Go(string reason) => new(CatalogSweepAction.Run, TimeSpan.Zero, reason);
    public static CatalogSweepDecision Wait(TimeSpan delay, string reason) => new(CatalogSweepAction.Defer, delay, reason);
    public static CatalogSweepDecision Nothing(string reason) => new(CatalogSweepAction.Skip, TimeSpan.Zero, reason);
}

/// <summary>Everything a tick can know without touching the database or the UI.</summary>
/// <param name="Clear">A <c>ClearAsync</c> sweep. It must always run to completion and is never skipped or deferred.</param>
/// <param name="Evictable">The cheap index-only probe said a batch could delete something.</param>
/// <param name="PendingCommits">Commands already queued on the shared commit owner.</param>
/// <param name="OwnerIdleFor">How long since the last command finished on the commit owner.</param>
/// <param name="SinceSurfaceOpened">How long since a surface was recorded as opened — the reveal signal.</param>
/// <param name="Deferrals">How many times this sweep has already backed off.</param>
public readonly record struct CatalogSweepState(bool Clear, bool Evictable, int PendingCommits,
    TimeSpan OwnerIdleFor, TimeSpan SinceSurfaceOpened, int Deferrals);

/// <summary>When a catalog cache sweep may take the shared writer, and when it must get out of the way.
///
/// The measured failure this encodes (native ARM64 tour 2026-09-09, seq 195): one <c>RunCatalogGcBatch</c> ran
/// 3,429 ms as a single command on the shared commit owner and deleted nothing. Three navigation commands that
/// arrived while it ran reported <c>queueMs≈1405-1445</c> and the artist page revealed in 1,483 ms instead of the
/// 55-68 ms every other cold reveal in that session took. Two rules follow, and this class is both of them:
///
/// 1. A sweep that cannot evict anything is not run. The batch's cost is fixed whole-table scan cost, paid whether
///    it deletes 1,000 rows or none, so "can it delete anything?" has to be answered first and cheaply.
/// 2. A sweep that can evict something still waits for the owner to be quiet. Deferral is bounded
///    (<see cref="MaxDeferrals"/>): a permanently busy app must still get its cache trimmed.
///
/// Pure by construction — no engine, no clock, no I/O — so the policy is unit-testable without a database.</summary>
public static class CatalogSweepSchedule
{
    /// <summary>How long after a surface opens a sweep stays out of the way. The tour's worst reveal was 1,483 ms
    /// and its cold page load finished 1,427 ms after nav, so the window has to cover a slow cold reveal.</summary>
    public static readonly TimeSpan RevealWindow = TimeSpan.FromMilliseconds(1_500);

    /// <summary>Owner quiet time a sweep waits for. One frame at 120 Hz is 8.3 ms; a command burst lands well
    /// inside 120 ms, so this reads "the owner is not mid-burst" without a UI hook.</summary>
    public static readonly TimeSpan RequiredOwnerIdle = TimeSpan.FromMilliseconds(120);

    /// <summary>Backoff between deferrals.</summary>
    public static readonly TimeSpan DeferBackoff = TimeSpan.FromMilliseconds(250);

    /// <summary>Deferrals before the sweep runs regardless. 40 × 250 ms = 10 s of a busy owner, after which the
    /// cache budget wins over the frame budget — otherwise a permanently busy session would never bound its cache.</summary>
    public const int MaxDeferrals = 40;

    /// <summary>Rows one batch may evict. Matches the batch limit the cold store applies, and is what
    /// <see cref="AfterBatch"/> reads as "the batch filled up, there is more to do".</summary>
    public const int BatchRows = 1000;

    /// <summary>Days a facet's rows survive without being read. Mirrors the cold store's own CASE, and is what the
    /// cheap probe compares each facet's <c>MIN(last_access)</c> against: overviews churn (7), relation pages are
    /// re-fetched per page (14), identities and everything else keep the long tail (30).</summary>
    public static int TtlDays(FacetKind facet) => facet switch
    {
        FacetKind.ArtistOverview => 7,
        >= FacetKind.AlbumTracks and <= FacetKind.ArtistRelated => 14,
        _ => 30,
    };

    /// <summary>The decision for a tick that has not yet run a batch.</summary>
    public static CatalogSweepDecision Next(CatalogSweepState state)
    {
        // A clear is a user/teardown action, not maintenance: it always runs, and runs to the end.
        if (state.Clear) return CatalogSweepDecision.Go("clear");
        if (!state.Evictable) return CatalogSweepDecision.Nothing("nothing evictable");
        if (state.Deferrals >= MaxDeferrals) return CatalogSweepDecision.Go("defer budget spent");
        if (state.PendingCommits > 0)
            return CatalogSweepDecision.Wait(DeferBackoff, "owner busy pending=" + state.PendingCommits);
        if (state.SinceSurfaceOpened < RevealWindow)
            return CatalogSweepDecision.Wait(RevealWindow - state.SinceSurfaceOpened, "reveal window");
        if (state.OwnerIdleFor < RequiredOwnerIdle)
            return CatalogSweepDecision.Wait(RequiredOwnerIdle - state.OwnerIdleFor, "owner not idle");
        return CatalogSweepDecision.Go("idle");
    }

    /// <summary>The decision after a batch of <paramref name="rows"/> rows. A short batch drained the work; a full
    /// batch means more, and whether the next one starts now goes back through <see cref="Next"/> so a navigation
    /// that arrived during the batch is not made to wait behind a second one.</summary>
    public static CatalogSweepDecision AfterBatch(int rows, CatalogSweepState state)
        => rows < BatchRows
            ? CatalogSweepDecision.Nothing("drained rows=" + rows)
            : Next(state with { Evictable = true });
}
