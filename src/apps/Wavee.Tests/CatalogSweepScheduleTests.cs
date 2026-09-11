using System;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>The retention scheduler, on its own: no database, no clock, no queue. Encodes the native ARM64 tour
/// of 2026-09-09, where one maintenance batch held the shared commit owner for 3,429 ms, deleted nothing, and cost
/// the artist page 1,483 ms of reveal against 55-68 ms for every other cold navigation in the session.</summary>
public sealed class CatalogSweepScheduleTests
{
    static CatalogSweepState Idle(bool evictable = true, bool clear = false) => new(clear, evictable,
        PendingCommits: 0, OwnerIdleFor: TimeSpan.FromSeconds(5),
        SinceSurfaceOpened: TimeSpan.FromMinutes(2), Deferrals: 0);

    [Fact]
    public void NothingEvictable_SkipsTheWholeSweep()
    {
        // The batch's cost is fixed whole-table scan cost — it pays the same seconds to delete 1,000 rows as to
        // delete none — so "can it delete anything?" has to gate it, not merely bound it.
        var decision = CatalogSweepSchedule.Next(Idle(evictable: false));
        Assert.Equal(CatalogSweepAction.Skip, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
    }

    [Fact]
    public void QuietOwnerAndNoRecentSurface_RunsImmediately()
        => Assert.Equal(CatalogSweepAction.Run, CatalogSweepSchedule.Next(Idle()).Action);

    [Fact]
    public void QueuedForegroundCommands_DeferTheSweep()
    {
        var decision = CatalogSweepSchedule.Next(Idle() with { PendingCommits = 3 });
        Assert.Equal(CatalogSweepAction.Defer, decision.Action);
        Assert.Equal(CatalogSweepSchedule.DeferBackoff, decision.Delay);
        Assert.Contains("pending=3", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASurfaceInsideTheRevealWindow_DefersForTheRestOfIt()
    {
        var since = TimeSpan.FromMilliseconds(400);
        var decision = CatalogSweepSchedule.Next(Idle() with { SinceSurfaceOpened = since });
        Assert.Equal(CatalogSweepAction.Defer, decision.Action);
        Assert.Equal(CatalogSweepSchedule.RevealWindow - since, decision.Delay);
    }

    [Fact]
    public void ASurfaceOlderThanTheRevealWindow_NoLongerDefers()
        => Assert.Equal(CatalogSweepAction.Run, CatalogSweepSchedule.Next(
            Idle() with { SinceSurfaceOpened = CatalogSweepSchedule.RevealWindow }).Action);

    [Fact]
    public void AnOwnerMidBurst_DefersForTheRestOfTheRequiredIdle()
    {
        var idleFor = TimeSpan.FromMilliseconds(20);
        var decision = CatalogSweepSchedule.Next(Idle() with { OwnerIdleFor = idleFor });
        Assert.Equal(CatalogSweepAction.Defer, decision.Action);
        Assert.Equal(CatalogSweepSchedule.RequiredOwnerIdle - idleFor, decision.Delay);
    }

    [Fact]
    public void DeferralIsBounded_SoAPermanentlyBusyOwnerStillGetsItsCacheTrimmed()
    {
        var busy = Idle() with { PendingCommits = 9, OwnerIdleFor = TimeSpan.Zero, SinceSurfaceOpened = TimeSpan.Zero };
        Assert.Equal(CatalogSweepAction.Defer, CatalogSweepSchedule.Next(busy with { Deferrals = CatalogSweepSchedule.MaxDeferrals - 1 }).Action);
        Assert.Equal(CatalogSweepAction.Run, CatalogSweepSchedule.Next(busy with { Deferrals = CatalogSweepSchedule.MaxDeferrals }).Action);
    }

    [Fact]
    public void AClearIsNeverSkippedAndNeverDeferred()
    {
        // ClearAsync is a user action (Settings → clear metadata cache) and a teardown path: it must empty the
        // cache, so neither the probe's answer nor a busy owner may stop it.
        var hostile = new CatalogSweepState(Clear: true, Evictable: false, PendingCommits: 12,
            OwnerIdleFor: TimeSpan.Zero, SinceSurfaceOpened: TimeSpan.Zero, Deferrals: 0);
        Assert.Equal(CatalogSweepAction.Run, CatalogSweepSchedule.Next(hostile).Action);
        // …and it keeps running while batches come back full, so a cache larger than one batch still empties.
        Assert.Equal(CatalogSweepAction.Run, CatalogSweepSchedule.AfterBatch(CatalogSweepSchedule.BatchRows, hostile).Action);
    }

    [Fact]
    public void AShortBatchEndsTheSweep_AFullBatchGoesBackThroughTheSameGate()
    {
        Assert.Equal(CatalogSweepAction.Skip, CatalogSweepSchedule.AfterBatch(CatalogSweepSchedule.BatchRows - 1, Idle()).Action);
        Assert.Equal(CatalogSweepAction.Run, CatalogSweepSchedule.AfterBatch(CatalogSweepSchedule.BatchRows, Idle()).Action);
        // The regression itself: a navigation that arrived DURING a batch must not be made to wait behind a second one.
        Assert.Equal(CatalogSweepAction.Defer, CatalogSweepSchedule.AfterBatch(
            CatalogSweepSchedule.BatchRows, Idle() with { SinceSurfaceOpened = TimeSpan.Zero }).Action);
    }

    [Fact]
    public void AFullBatchResumesEvenWhenTheProbeHadSaidNothing()
        // A batch that filled up proves there is work regardless of what the pre-sweep probe answered, so the
        // continuation must not inherit a stale `Evictable: false` and stop half-drained.
        => Assert.Equal(CatalogSweepAction.Run, CatalogSweepSchedule.AfterBatch(
            CatalogSweepSchedule.BatchRows, Idle(evictable: false)).Action);

    [Theory]
    [InlineData(FacetKind.ArtistOverview, 7)]
    [InlineData(FacetKind.AlbumTracks, 14)]
    [InlineData(FacetKind.ArtistDiscography, 14)]
    [InlineData(FacetKind.ArtistRelated, 14)]
    [InlineData(FacetKind.TrackIdentity, 30)]
    [InlineData(FacetKind.ExtensionDocument, 30)]
    [InlineData(FacetKind.AlbumDetail, 30)]
    public void TtlDays_MatchesTheRetentionTiersTheColdStoreEvictsBy(FacetKind facet, int days)
        // The cheap probe compares each facet's MIN(last_access) against this, so a value looser than the store's
        // own CASE would make the probe under-report and silently stop evicting that facet.
        => Assert.Equal(days, CatalogSweepSchedule.TtlDays(facet));
}
