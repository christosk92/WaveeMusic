using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// A UI surface that hosts a <c>MediaPlayerElement</c> and can attach/detach
/// from a shared <see cref="MediaPlayer"/>. Consumers in the app are
/// <c>ExpandedNowPlayingLayout</c> (used by the popout window, the sidebar's
/// expanded layout, and the now-playing page through <c>ExpandedPlayerView</c>),
/// <c>SidebarPlayerWidget</c>, and <c>MiniVideoPlayer</c> (the bottom-right
/// floating widget). At most one consumer holds the binding at any moment;
/// <see cref="IActiveVideoSurfaceService"/> arbitrates the handoff.
/// </summary>
public interface IMediaSurfaceConsumer
{
    /// <summary>
    /// Acquisition priority for this consumer. Higher wins. When a lower-
    /// priority surface (e.g. the floating <c>MiniVideoPlayer</c>) tries to
    /// acquire while a higher-priority surface (e.g. the fullscreen
    /// <c>VideoPlayerPage</c>) is already the active owner, the arbiter
    /// drops the request rather than letting the mini steal the surface
    /// mid-navigation.
    ///
    /// Defaults to 0. Concrete consumers override with their own constant.
    /// Suggested values: VideoPlayerPage = 10, MiniVideoPlayer = 5, sidebar
    /// / expanded popout = 0.
    /// </summary>
    int OwnerPriority => 0;

    /// <summary>
    /// Bind <paramref name="player"/> to this consumer's
    /// <c>MediaPlayerElement</c> (typically <c>element.SetMediaPlayer(player)</c>).
    /// Called by <see cref="IActiveVideoSurfaceService"/> when this consumer
    /// has just acquired the surface.
    /// </summary>
    void AttachSurface(MediaPlayer player);

    /// <summary>
    /// Bind a non-MediaPlayer XAML surface, for example a WebView2 instance.
    /// </summary>
    void AttachElementSurface(FrameworkElement element) { }

    /// <summary>
    /// Unbind the player from this consumer (typically <c>element.SetMediaPlayer(null)</c>).
    /// Called when another consumer is taking over, or when the active provider
    /// went idle.
    /// </summary>
    void DetachSurface();
}
