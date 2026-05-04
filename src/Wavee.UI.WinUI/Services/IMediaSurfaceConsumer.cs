using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// A UI surface that hosts a <c>MediaPlayerElement</c> and can attach/detach
/// from a shared <see cref="MediaPlayer"/>. The two consumers in the app are
/// <c>VideoPlayerPage</c> (the full page) and <c>MiniVideoPlayer</c> (the
/// bottom-right floating widget). At most one consumer holds the binding at
/// any moment; <see cref="IActiveVideoSurfaceService"/> arbitrates the handoff.
/// </summary>
public interface IMediaSurfaceConsumer
{
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
