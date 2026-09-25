// ── Wavee.Tests/PlacementMenuTests.cs — the placement menu's OWN truth, pinned without an engine ───────────────────
//
// `Video.PlacementMenuRules` answers "why is this row disabled" for the player bar's chevron and every surface's ⋯.
// It is deliberately separate from `Video.UpgradeGate`'s `Available` fold: that value is a SNAPSHOT (seeded
// `PlacementSet.None`, re-stamped only at a track boundary or an explicit click), so reading it straight would make
// the menu lie about "no video on this track" until the next fold ran. `ReasonKey` takes the live facts instead —
// `hasVideo` and the host's own capability — and answers at OPEN time.
//
// Pure values only: two bools/sets and a placement in, a `PlacementSet` or a loc key (or null) out.

using Wavee;
using Xunit;

using static Wavee.Video;

namespace Wavee.Tests;

public class PlacementMenuRulesTests
{
    static readonly SurfacePlacement[] AllRows =
        [SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Detached, SurfacePlacement.Fullscreen];

    const PlacementSet FullHostCapability =
        PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    [Fact]
    public void No_video_disables_every_row_with_the_same_no_video_reason()
    {
        // The track fact beats every host fact: a track with no video is not "waiting on a wider window".
        Assert.Equal(PlacementSet.None, PlacementMenuRules.Available(hasVideo: false, FullHostCapability));

        foreach (var row in AllRows)
            Assert.Equal(Strings.Player.NoVideoForThisSong,
                PlacementMenuRules.ReasonKey(hasVideo: false, FullHostCapability, row));
    }

    [Fact]
    public void Video_with_a_host_missing_docked_disables_only_that_row_with_the_wider_window_reason()
    {
        PlacementSet hostCapable = FullHostCapability & ~PlacementSet.Docked;

        Assert.Equal(Strings.Player.VideoNeedsWiderWindow,
            PlacementMenuRules.ReasonKey(hasVideo: true, hostCapable, SurfacePlacement.Docked));
        Assert.Null(PlacementMenuRules.ReasonKey(hasVideo: true, hostCapable, SurfacePlacement.Floating));
        Assert.Null(PlacementMenuRules.ReasonKey(hasVideo: true, hostCapable, SurfacePlacement.Detached));
        Assert.Null(PlacementMenuRules.ReasonKey(hasVideo: true, hostCapable, SurfacePlacement.Fullscreen));
    }

    [Fact]
    public void Video_with_a_fully_capable_host_disables_nothing()
    {
        Assert.Equal(FullHostCapability, PlacementMenuRules.Available(hasVideo: true, FullHostCapability));
        foreach (var row in AllRows)
            Assert.Null(PlacementMenuRules.ReasonKey(hasVideo: true, FullHostCapability, row));
    }

    [Theory]
    [InlineData(PlacementSet.Docked)]      // only Docked capable: Detached is the one missing
    [InlineData(PlacementSet.None)]        // headless / a detached child: nothing is capable
    public void Detached_missing_reads_not_available_from_this_window(PlacementSet hostCapable)
        => Assert.Equal(Strings.Player.VideoNoSecondWindow,
            PlacementMenuRules.ReasonKey(hasVideo: true, hostCapable, SurfacePlacement.Detached));

    [Fact]
    public void Fullscreen_missing_reads_its_own_reason()
        => Assert.Equal(Strings.Player.VideoNoFullscreen,
            PlacementMenuRules.ReasonKey(hasVideo: true, PlacementSet.None, SurfacePlacement.Fullscreen));

    [Fact]
    public void Floating_is_never_null_reasoned_while_disabled()
    {
        // The bug this item fixes: the menu's Floating row passed (null, null) for its reason, so a disabled mini
        // player looked enabled — a radio row with no accelerator text reads as a live choice, not a greyed-out one.
        foreach (PlacementSet hostCapable in new[] { PlacementSet.None, PlacementSet.Docked, PlacementSet.Detached, PlacementSet.Fullscreen })
        {
            bool allowed = PlacementCore.Allows(PlacementMenuRules.Available(hasVideo: true, hostCapable), SurfacePlacement.Floating);
            Assert.False(allowed);
            Assert.Equal(Strings.Player.VideoMiniPlayerUnavailable,
                PlacementMenuRules.ReasonKey(hasVideo: true, hostCapable, SurfacePlacement.Floating));
        }

        // …and never reasoned at all while actually allowed.
        Assert.Null(PlacementMenuRules.ReasonKey(hasVideo: true, PlacementSet.Floating, SurfacePlacement.Floating));
    }

    [Fact]
    public void Available_agrees_with_UpgradeGate_AvailabilityFor()
    {
        // The menu asks a simpler question than the upgrade gate, but the law must be the SAME law — a row the fold
        // would mount can never show as the menu's "disabled".
        foreach (bool hasVideo in new[] { true, false })
        foreach (PlacementSet hostCapable in new[] { PlacementSet.None, PlacementSet.Docked, FullHostCapability })
            Assert.Equal(UpgradeGate.AvailabilityFor(hasVideo, hostCapable), PlacementMenuRules.Available(hasVideo, hostCapable));
    }
}
