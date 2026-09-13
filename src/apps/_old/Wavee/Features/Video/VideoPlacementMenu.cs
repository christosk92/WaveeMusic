using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee.Features.Video;

/// <summary>
/// The single definition of the video placement picker's rows — the full four-rung commitment ladder (Dock in rail ·
/// Mini player · Pop-out window · Full screen), each radio-checked against the one resolved placement, each disabled
/// with its reason in <see cref="MenuFlyoutItem.AcceleratorText"/> rather than hidden (a disabled row cannot carry a
/// tooltip — <c>IsEnabled = false</c> removes hit-testing), plus the conditional "Always on top" (Detached only) and
/// "Turn off video" (active only) rows.
///
/// THREE call sites share this so they cannot drift apart: the player-bar split button's own chevron menu, the
/// narrow-layout overflow's cascading <c>AppBarCommand.Flyout</c> (both <c>Features/Shell/PlayerBar.cs</c>), and the
/// video's own transport More (⋯) menu (<see cref="Controls.Media.MediaPlayerElement.MoreMenuItems"/>, wired from
/// <c>Features/Video/PopOutVideoWindow.cs</c>).
/// </summary>
public static class VideoPlacementMenu
{
    /// <summary>Build the rows for the current resolved placement. <paramref name="includeFullscreen"/> omits ONLY
    /// the Full screen row — used by a host that already offers its own Fullscreen affordance (the video element's
    /// built-in row), so the two rows never duplicate.
    ///
    /// <para><b>Verified:</b> that built-in row really does delegate to the app on EVERY Wavee surface. All four
    /// (docked card, mini player, detached window, fullscreen surface) now set
    /// <c>MediaPlayerElement.FullscreenRequested</c> — the docked card directly, the other three through
    /// <see cref="VideoStageHost.FullscreenRequested"/> — and <c>ToggleFullscreen</c> prefers that delegate over its own
    /// overlay path. Nothing in Wavee can reach the element's modal fullscreen popup any more, which is also what
    /// removes "Alt-Tab leaves fullscreen" (the popup was light-dismiss, and window blur closed it into
    /// <c>LeaveFullscreen</c>) and its unconditional <c>WindowSetFullscreen(false)</c>.</para>
    ///
    /// <para><b>Known label wart</b> on the fullscreen surface only: there the delegate EXITS, but the element still
    /// labels its row "Full screen" and draws the enter glyph, because <c>MediaPlayerElement.IsFullscreenPresentation</c>
    /// is <c>internal</c> to FluentGpu.Controls and Wavee is not an InternalsVisibleTo friend. It needs a public knob on
    /// the element (engine-side) to read "Exit full screen" / <c>Icons.BackToWindow</c>; the behaviour is already
    /// correct.</para></summary>
    public static List<MenuFlyoutItem> Items(PlaybackBridge b, IAppSettings? settings, bool includeFullscreen)
    {
        var state = b.VideoSurface.Value;
        var now = PlacementCore.Resolve(state);

        MenuFlyoutItem Placement(SurfacePlacement p, string labelKey, IconRef icon, string? accelWhenAllowed, string? reasonKey)
        {
            bool allowed = PlacementCore.Allows(state.Available, p);
            string? accel = allowed ? accelWhenAllowed : (reasonKey is null ? null : Loc.Get(reasonKey));
            return MenuFlyoutItem.RadioItem(Loc.Get(labelKey), now == p, () => b.ShowVideoAt(p), icon, allowed)
                with { AcceleratorText = accel };
        }

        var items = new List<MenuFlyoutItem>(7)
        {
            Placement(SurfacePlacement.Docked, Strings.Player.DockInRail, Icons.SplitView, null, Strings.Player.VideoNeedsWiderWindow),
            Placement(SurfacePlacement.Floating, Strings.Player.VideoMiniPlayer, Icons.BackToWindow, null, null),
            Placement(SurfacePlacement.Detached, Strings.Player.VideoInSeparateWindow, Icons.Movie, null, Strings.Player.VideoNoSecondWindow),
        };
        if (includeFullscreen)
            items.Add(Placement(SurfacePlacement.Fullscreen, Strings.Player.VideoFullScreen, Icons.FullScreen, "F11", Strings.Player.VideoNoFullscreen));

        // Always-on-top is a property of the SEPARATE WINDOW, so it is only offered when that is where the video
        // lives. A checkable item rather than a mode switch: it is a preference the user flips and forgets, and the
        // window applies it live (VideoPlacementHost) instead of at the next open.
        if (settings is { } vset && now == SurfacePlacement.Detached)
        {
            bool onTop = vset.Get(WaveeSettings.VideoWindowAlwaysOnTop);
            items.Add(MenuFlyoutItem.Separator);
            items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.VideoAlwaysOnTop), onTop,
                () => VideoWindowPrefs.SetAlwaysOnTop(vset, !onTop)));
        }
        if (PlacementCore.IsActive(state))   // "off" is only meaningful while something is on
        {
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new(Loc.Get(Strings.Player.TurnOffVideo), Icons.Cancel, true, b.TurnVideoOff));
        }
        return items;
    }
}
