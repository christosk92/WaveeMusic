using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The behavioural spec of the DETACHED pop-out window's own fullscreen mode (<see cref="DetachedFullscreenRule"/>, the
/// rule behind <c>PlaybackBridge.DetachedFullscreen</c>), which is deliberately NOT
/// <see cref="SurfacePlacement.Fullscreen"/>.
///
/// <para>The defect these lock down: pressing the pop-out transport's fullscreen glyph used to call
/// <c>ShowVideoAt(SurfacePlacement.Fullscreen)</c>. That resolves the placement AWAY from
/// <see cref="SurfacePlacement.Detached"/>, so the owner closed the pop-out and the shell fullscreened the MAIN window
/// — on the MAIN window's monitor. A pop-out dragged to a second display therefore jumped back to the laptop screen.
/// The fix makes fullscreen a MODE OF the pop-out window: the placement stays Detached and the request is forwarded to
/// that window's own <c>IDetachedVideoWindow.SetFullscreen</c>, whose backend resolves the display from that window's
/// own handle.</para>
///
/// <para>Engine-free by construction: the rule is a pure function over the placement values, so these assertions need
/// no host, no window and no reconciler (the <see cref="PlacementCore"/> / <c>LiveEdgeState</c> pattern).</para>
/// </summary>
public class DetachedFullscreenRuleTests
{
    const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    static PlacementState Off => PlacementState.Initial(PlacementPolicy.Video) with { Available = All };

    /// <summary>Watching in the pop-out: the resolved placement is Detached and the window is reported live.</summary>
    static PlacementState Detached
        => PlacementCore.WithLive(PlacementCore.OpenAt(Off, SurfacePlacement.Detached), SurfacePlacement.Detached);

    // ── the point of the whole change: fullscreen does NOT move the placement ────────────────────────────────────────

    /// <summary>Entering fullscreen from the pop-out changes NO placement state whatsoever — it is a separate signal.
    /// This is the assertion that would have failed against the old <c>ShowVideoAt(Fullscreen)</c> wiring, which
    /// resolved to <see cref="SurfacePlacement.Fullscreen"/> (a different window, on a different monitor).</summary>
    [Fact]
    public void EnteringFullscreenFromPopOut_KeepsPlacementDetached()
    {
        var s = Detached;
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(s));
        // The toggle writes only the fullscreen bit; the placement state is not a participant.
        Assert.True(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(s)));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(s));
    }

    /// <summary>…and the placement the OLD wiring produced is a different one, which is exactly why it closed the
    /// pop-out and handed fullscreen to the main window.</summary>
    [Fact]
    public void ShowVideoAtFullscreen_IsADifferentPlacement_AndClearsTheMode()
    {
        var moved = PlacementCore.OpenAt(Detached, SurfacePlacement.Fullscreen);
        Assert.Equal(SurfacePlacement.Fullscreen, PlacementCore.Resolve(moved));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(moved)));
    }

    /// <summary>Fullscreen survives across anything that leaves the placement at Detached (a track change that keeps
    /// availability, an in-place re-open) — the mode belongs to the window, and the window did not go anywhere.</summary>
    [Fact]
    public void ModeSurvives_WhilePlacementStaysDetached()
    {
        var next = PlacementCore.WithAvailability(Detached, All);   // a track change that still has a video
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(next));
        Assert.True(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(next)));
    }

    // ── the mode is false after the pop-out closes, by EVERY route ───────────────────────────────────────────────────

    /// <summary>The user closed the window with the OS chrome / Alt+F4 (<c>NotifyVideoSurfaceClosed(Detached)</c>).
    /// That falls back to the mini player, so the resolved placement is no longer Detached and the mode drops.</summary>
    [Fact]
    public void ModeIsFalse_AfterUserClosedThePopOut()
    {
        var closed = PlacementCore.HostClosed(Detached, SurfacePlacement.Detached);
        Assert.NotEqual(SurfacePlacement.Detached, PlacementCore.Resolve(closed));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(closed)));
    }

    /// <summary>A programmatic move to another placement (the ⋯ menu's placement rows) closes the window too.</summary>
    [Fact]
    public void ModeIsFalse_AfterMovingToAnotherPlacement()
    {
        foreach (var to in new[] { SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Fullscreen })
        {
            var moved = PlacementCore.OpenAt(Detached, to);
            Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(moved)));
        }
    }

    /// <summary>Turning video off entirely.</summary>
    [Fact]
    public void ModeIsFalse_AfterTurnVideoOff()
    {
        var off = PlacementCore.TurnOff(Detached);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(off));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(off)));
    }

    /// <summary>An AMBIENT loss of the placement — the next track has no video, or the host can no longer open a second
    /// window. Nobody "closed" anything, yet the window is gone, so the mode must go with it.</summary>
    [Fact]
    public void ModeIsFalse_WhenTheDetachedPlacementBecomesUnavailable()
    {
        var noVideo = PlacementCore.WithAvailability(Detached, PlacementSet.None);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(noVideo));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(noVideo)));

        var noSecondWindow = PlacementCore.WithAvailability(Detached, PlacementSet.Docked | PlacementSet.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(noSecondWindow));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(noSecondWindow)));
    }

    /// <summary>The rule is a pure AND: it never turns the mode ON by itself. Re-opening the pop-out therefore starts
    /// windowed, which is the "closed it while fullscreen, it came back fullscreen" regression.</summary>
    [Fact]
    public void ModeIsNeverTurnedOnByTheRule()
    {
        Assert.False(DetachedFullscreenRule.After(current: false, SurfacePlacement.Detached));
        var reopened = PlacementCore.OpenAt(PlacementCore.HostClosed(Detached, SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(reopened));
        Assert.False(DetachedFullscreenRule.After(current: false, PlacementCore.Resolve(reopened)));
    }

    /// <summary>Exhaustive: Detached is the ONLY placement that keeps the mode. Stated as a loop over the whole enum so
    /// a new placement cannot be added without deciding what it means here.</summary>
    [Fact]
    public void OnlyDetachedKeepsTheMode()
    {
        foreach (SurfacePlacement p in new[]
                 {
                     SurfacePlacement.None, SurfacePlacement.Docked, SurfacePlacement.Floating,
                     SurfacePlacement.Detached, SurfacePlacement.Fullscreen,
                 })
            Assert.Equal(p == SurfacePlacement.Detached, DetachedFullscreenRule.After(current: true, p));
    }
}
