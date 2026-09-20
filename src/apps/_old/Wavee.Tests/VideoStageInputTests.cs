using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Which video surface may move its window by dragging the picture, and which hides the idle cursor while windowed
/// (<see cref="VideoStageInput"/>). The pop-out window is chromeless — it has no title bar to grab — so dragging the
/// picture is how it moves; every other surface lives inside the MAIN window and must never move it.
///
/// <para>Engine-free by construction: the rule is a pure function of the surface's <see cref="TransportOwner"/>
/// identity and its fullscreen bit (the <see cref="PlacementCore"/> / <see cref="DetachedFullscreenRule"/> pattern).</para>
/// </summary>
public class VideoStageInputTests
{
    [Fact]
    public void OnlyThePopOutMovesItsWindow()
    {
        Assert.True(VideoStageInput.DragMovesWindow(TransportOwner.PopOut, hostFullscreen: false));
        Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.Docked, false));      // would drag the MAIN window
        Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.Fullscreen, false));
        Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.GlobalBar, false));
    }

    [Fact]
    public void AFullscreenPopOutHasNowhereToGo()
        => Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.PopOut, hostFullscreen: true));

    [Fact]
    public void OnlyTheDedicatedWindowHidesTheCursorWindowed()
    {
        Assert.True(VideoStageInput.HidesCursorWindowed(TransportOwner.PopOut));        // mpv's windowed default
        Assert.False(VideoStageInput.HidesCursorWindowed(TransportOwner.Docked));       // inline: fullscreen only
        Assert.False(VideoStageInput.HidesCursorWindowed(TransportOwner.Fullscreen));   // hides by the fullscreen rule instead
        Assert.False(VideoStageInput.HidesCursorWindowed(TransportOwner.GlobalBar));
    }
}
