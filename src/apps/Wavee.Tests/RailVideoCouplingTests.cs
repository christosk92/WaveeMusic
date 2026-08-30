using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The pure rail ↔ docked-video coupling rules (<see cref="RailVideoCoupling"/>) — the four edge cases connecting the
/// right rail's own open/closed + mode state to the video surface's placement (docked-video design §3.5, B1/B6-B8/B10-B14).
/// Reuses the <see cref="PlacementCoreTests"/> helpers' shape (<c>Off</c>/<c>At</c>) locally so this file stays
/// self-contained and, like its sibling, engine-free.
/// </summary>
public class RailVideoCouplingTests
{
    static readonly PlacementPolicy Policy = PlacementPolicy.Video;
    const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    static PlacementState Off(PlacementSet available = All)
        => PlacementState.Initial(Policy) with { Available = available };

    static PlacementState At(SurfacePlacement p, PlacementSet available = All)
        => PlacementCore.OpenAt(Off(available), p);

    // ── ModeOnDock — B1/B12: docking a closed rail opens Video mode; docking an already-open rail leaves it alone ─────

    [Fact]
    public void ModeOnDock_OpensVideoMode_OnlyWhenTheRailWasClosed()
    {
        Assert.Equal(RailMode.Video, RailVideoCoupling.ModeOnDock(false, RailMode.Lyrics, DockedVideoHost.Rail));
        Assert.Null(RailVideoCoupling.ModeOnDock(true, RailMode.Lyrics, DockedVideoHost.Rail));
        Assert.Null(RailVideoCoupling.ModeOnDock(true, RailMode.Video, DockedVideoHost.Rail));
    }

    /// <summary>Docking while the module watch page's IN-PAGE stage is what will host the surface must not open the
    /// rail into the video-first body: that body exists to wrap the docked card, and the card is mounted on the page.
    /// The user would get a rail sliding in over the very page they are watching, showing an empty takeover.</summary>
    [Fact]
    public void ModeOnDock_DoesNotOpenVideoMode_WhenTheStageWillHostIt()
    {
        Assert.Null(RailVideoCoupling.ModeOnDock(false, RailMode.Lyrics, DockedVideoHost.PageStage));
        Assert.Null(RailVideoCoupling.ModeOnDock(false, RailMode.Queue, DockedVideoHost.PageStage));
        Assert.Null(RailVideoCoupling.ModeOnDock(true, RailMode.Lyrics, DockedVideoHost.PageStage));
    }

    // ── OnRailClosed — B6: closing the rail demotes a DOCKED video; every other placement is untouched ────────────────

    [Fact]
    public void RailClosed_WhileFloating_IsInert()
    {
        Assert.Equal(SurfacePlacement.None, RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Floating), DockedVideoHost.Rail));
        Assert.Equal(SurfacePlacement.None, RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Detached), DockedVideoHost.Rail));
        Assert.Equal(SurfacePlacement.None, RailVideoCoupling.OnRailClosed(Off(), DockedVideoHost.Rail));   // no video at all
    }

    [Fact]
    public void RailClosed_WhileDocked_DemotesToFloating()
        => Assert.Equal(SurfacePlacement.Floating,
            RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Docked), DockedVideoHost.Rail));

    /// <summary>The rail is not where a page-hosted video lives, so closing the rail has nothing to say about it —
    /// exactly as it has nothing to say about a detached window. Without the host term this rule would demote the watch
    /// page's in-page stage to the floating mini player and yank the video off the page the user is reading.</summary>
    [Fact]
    public void RailClosed_WhileTheStageHosts_IsInert()
    {
        Assert.Equal(SurfacePlacement.None,
            RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Docked), DockedVideoHost.PageStage));
        Assert.Equal(SurfacePlacement.None,
            RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Floating), DockedVideoHost.PageStage));
    }

    // ── ReDockOnRailOpen — B7/B8: only when the rail itself is what took the video away ─────────────────────────────────

    [Fact]
    public void ReDock_OnlyWhenPreferredIsDocked()
    {
        // The rail took it away (Preferred still Docked, now sitting at the Floating fallback) → re-dock.
        var demoted = PlacementCore.Demote(At(SurfacePlacement.Docked), SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, demoted.Requested);
        Assert.True(RailVideoCoupling.ReDockOnRailOpen(demoted));

        // The user deliberately picked "Mini player" from the menu (Preferred = Floating) → never re-dock (B8).
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(At(SurfacePlacement.Floating)));

        // Video is off entirely → nothing to re-dock.
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(Off() with { Preferred = SurfacePlacement.Docked }));

        // Already docked → no-op, it never left.
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(At(SurfacePlacement.Docked)));

        // Fullscreen entered FROM Docked must not re-fire the moment the rail happens to still be open beneath it.
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Docked));
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(fs));
    }

    // ── CloseRailOnVideoLeft — B13/B14: only the Video-mode body has nothing left to show ────────────────────────────

    [Fact]
    public void CloseRailOnVideoLeft_OnlyInVideoMode()
    {
        Assert.True(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Video, true, DockedVideoHost.Rail));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Video, false, DockedVideoHost.Rail));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Lyrics, true, DockedVideoHost.Rail));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Queue, true, DockedVideoHost.Rail));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Details, true, DockedVideoHost.Rail));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Friends, true, DockedVideoHost.Rail));
    }

    /// <summary>The video that left the dock was never the rail's — it was on the watch page's stage — so the rail's
    /// own body is untouched and there is nothing to close. Keyed on the host BEFORE, because once the video has left
    /// the dock the derivation reads Rail unconditionally (nothing is docked, so the rail is the resting owner) and a
    /// current-host term would be true for every case.</summary>
    [Fact]
    public void CloseRailOnVideoLeft_IsInert_WhenTheStageWasHosting()
    {
        foreach (var mode in (RailMode[])System.Enum.GetValues(typeof(RailMode)))
            Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(mode, true, DockedVideoHost.PageStage));
    }

    // ── BodyModeFor — the rail YIELDS at render time, and only the two card-hosting bodies are substituted ───────────

    /// <summary>Video-first and Details are the only two bodies that HOST the docked card (the Cap face and the
    /// Art-tile hero); with the stage owning the one surface neither has anything to show, so both fall to Queue. Every
    /// other body is untouched, and NOTHING is touched while the stage is not hosting — the substitution is a render
    /// decision, never a write, so the user's chosen mode survives the whole round trip.</summary>
    [Fact]
    public void BodyModeFor_SubstitutesQueue_ForTheTwoCardBodiesOnly()
    {
        Assert.Equal(RailMode.Queue, RailVideoCoupling.BodyModeFor(RailMode.Video, stageHostsVideo: true));
        Assert.Equal(RailMode.Queue, RailVideoCoupling.BodyModeFor(RailMode.Details, stageHostsVideo: true));
        Assert.Equal(RailMode.Lyrics, RailVideoCoupling.BodyModeFor(RailMode.Lyrics, stageHostsVideo: true));
        Assert.Equal(RailMode.Friends, RailVideoCoupling.BodyModeFor(RailMode.Friends, stageHostsVideo: true));
        Assert.Equal(RailMode.Queue, RailVideoCoupling.BodyModeFor(RailMode.Queue, stageHostsVideo: true));

        // Not hosting → total identity. The rail is exactly where the user left it the instant the stage yields.
        foreach (var mode in (RailMode[])System.Enum.GetValues(typeof(RailMode)))
            Assert.Equal(mode, RailVideoCoupling.BodyModeFor(mode, stageHostsVideo: false));
    }
}
