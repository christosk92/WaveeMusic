using System;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The pure blocking-vs-background table a page open reads (design §2.1). This file pins the playlist branch's SWR
// shape after S2 #6 (the stale-daylist-open fix): a baseline's freshness — not merely its presence — decides whether
// the open blocks. OpenPolicy.For is engine-free and store-free, so every case here is a plain value assertion.
public class OpenPolicyTests
{
    [Fact]
    public void Playlist_NoBaseline_BlocksUnbounded()
    {
        // Nothing to paint either way, so `needsRevalidation` is moot — the no-baseline branch never even looks at it.
        var plan = OpenPolicy.For(EntityKind.Playlist, hasBaseline: false, needsRevalidation: true);

        Assert.Equal(HydrationLevel.Open, plan.Blocking);
        Assert.Equal(HydrationLevel.None, plan.Background);
        Assert.Null(plan.BlockingDeadline);
    }

    [Fact]
    public void Playlist_FreshBaseline_PaintsNowAndRevalidatesInTheBackground()
    {
        var plan = OpenPolicy.For(EntityKind.Playlist, hasBaseline: true, needsRevalidation: false);

        Assert.Equal(HydrationLevel.None, plan.Blocking);
        Assert.Equal(HydrationLevel.Open, plan.Background);
        Assert.True(plan.Revalidate);
        Assert.Null(plan.BlockingDeadline);   // nothing ever blocks on it, so there is nothing to bound
    }

    [Fact]
    public void Playlist_StaleBaseline_BlocksOnRevalidation_BoundedByTheDeadline()
    {
        var plan = OpenPolicy.For(EntityKind.Playlist, hasBaseline: true, needsRevalidation: true);

        Assert.Equal(HydrationLevel.Open, plan.Blocking);
        Assert.Equal(HydrationLevel.None, plan.Background);
        Assert.True(plan.Revalidate);
        Assert.Equal(OpenPolicy.PlaylistRevalidateDeadline, plan.BlockingDeadline);
        // Structural: the bound exists so a slow /diff cannot hang the page indefinitely — it must be a real, short
        // wait, not an open-ended one mislabelled as "bounded".
        Assert.True(plan.BlockingDeadline > TimeSpan.Zero);
        Assert.True(plan.BlockingDeadline < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Playlist_NeedsRevalidationDefaultsFalse_SoAnUnawareCallerNeverBlocks()
    {
        // A caller that does not (or cannot — offline, a fake registry) ask LibrarySync's freshness question gets the
        // pre-S2-#6 shape for free: paint the baseline, revalidate quietly, never wait.
        var plan = OpenPolicy.For(EntityKind.Playlist, hasBaseline: true);

        Assert.Equal(HydrationLevel.None, plan.Blocking);
        Assert.Equal(HydrationLevel.Open, plan.Background);
    }

    [Fact]
    public void Album_Unaffected_StillAwaitsRichAlone()
    {
        // The playlist-only parameters must not leak into an unrelated kind's shape.
        var plan = OpenPolicy.For(EntityKind.Album, needsRevalidation: true);

        Assert.Equal(HydrationLevel.Rich, plan.Blocking);
        Assert.Equal(HydrationLevel.None, plan.Background);
        Assert.Null(plan.BlockingDeadline);
    }
}
