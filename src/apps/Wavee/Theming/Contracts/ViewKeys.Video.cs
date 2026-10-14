// ── Theming/Contracts/ViewKeys.Video.cs — the video surfaces' published keys (themes plan 6.0, T7c) ───────────────────────────
//
// T7c publishes the six pictures of the video surfaces: the docked card (the rail's cap and the watch page's in-page stage), the
// in-window overlay (its frame and every chrome that sits over the picture: the docked hover strip, the mini player's drag strip, close and
// eight resize grips), the pop-out window's content (the title band), the fullscreen surface's exit chrome, the poster with the join
// notice, and the placement menu's flyout. The PLAYBACK and MEDIA wiring stays core: the one stay-mounted presenter and its engine
// `MediaPlayerElement` (its keys and handles, the idle machine, the chrome feed), the hollow reservation the overlay follows, the join
// fold, the host observer, the pop-out's lifecycle and caption regions, the fullscreen mode (OS window, focus scope, Escape and F), the
// placement and PiP rules (`Video.cs`, `PipGesture.cs`) and the PiP gestures. The core hosts mount these keys with a props record.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The docked video card: the rail's cap (its height, the hollow reservation the stay-mounted presenter follows, the Space key) and
    /// the watch page's in-page stage frame (the poster ground, the idle play disc, the live card). Mounted by the core hosts
    /// (<c>Video.DockedCap</c>, <c>Video.WatchStage</c>). Props: <see cref="VideoDockedProps"/>.</summary>
    public static readonly ViewKey<VideoDockedProps> VideoDocked = new("Video.Docked");

    /// <summary>The in-window overlay of the stay-mounted presenter: its frame (the mini player's corners, ring and shadow), the picture it
    /// places, and the chrome over it (the docked hover strip; the mini player's drag strip, close and eight resize zones). Mounted by the
    /// core <c>PipSurface</c>. Props: <see cref="VideoPipProps"/>.</summary>
    public static readonly ViewKey<VideoPipProps> VideoPip = new("Video.Pip");

    /// <summary>The pop-out window's content: the stage over the poster and the title band. Mounted by the core pop-out content, which keeps the
    /// caption regions, the move-loop hold and the band's fade. Props: <see cref="VideoPopOutProps"/>.</summary>
    public static readonly ViewKey<VideoPopOutProps> VideoPopOut = new("Video.PopOut");

    /// <summary>The fullscreen surface's chrome: the title and the way out. Mounted by the core fullscreen surface, which keeps the OS window
    /// mode, the focus scope and the Escape / F keys. Props: <see cref="VideoFullscreenProps"/>.</summary>
    public static readonly ViewKey<VideoFullscreenProps> VideoFullscreen = new("Video.Fullscreen");

    /// <summary>The "no picture yet" composition every surface shares: the live artwork over the letterbox and, only for the join states that
    /// have something to say, the loading ring or the fault line over it. Mounted by every video surface. Props: <see cref="VideoJoinPosterProps"/>.</summary>
    public static readonly ViewKey<VideoJoinPosterProps> VideoJoinPoster = new("Video.JoinPoster");

    /// <summary>The placement menu's flyout (where the video plays: the rows, their radio checks and reasons). Mounted by the player bar's
    /// video chevron. Props: <see cref="VideoPlacementMenuProps"/>.</summary>
    public static readonly ViewKey<VideoPlacementMenuProps> VideoPlacementMenu = new("Video.PlacementMenu");
}
