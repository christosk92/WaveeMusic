using System;
using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Source-agnostic contract for any engine that produces a renderable video
/// stream. Implementations expose a <see cref="Windows.Media.Playback.MediaPlayer"/>
/// surface that the UI binds via <c>MediaPlayerElement.SetMediaPlayer(...)</c> —
/// the same call regardless of whether the bytes come from a local file, a
/// Spotify music-video CDN, or a podcast-video episode.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>Wavee.UI.WinUI</c> rather than <c>Wavee.Core</c> because the
/// interface inherently couples to a Windows type (<c>MediaPlayer</c>); the
/// core project targets <c>net10.0</c> (non-Windows TFM) and can't reference
/// <c>Windows.Media.Playback</c>. Engines that need to render via a
/// <c>MediaPlayer</c> implement this from a Windows-targeted assembly.
/// </para>
/// <para>
/// The orchestrator (in core) keeps using <see cref="Wavee.Audio.ILocalMediaPlayer"/>
/// for playback routing — that interface stays surface-free. This interface is
/// purely for the UI side: a registry (<see cref="IActiveVideoSurfaceService"/>)
/// aggregates registered providers and tells the page / mini-player which
/// surface is currently live.
/// </para>
/// </remarks>
public interface IVideoSurfaceProvider
{
    /// <summary>
    /// The renderable surface, or <c>null</c> when the engine is idle.
    /// Setting <c>MediaPlayerElement.SetMediaPlayer(provider.Surface)</c>
    /// attaches it to a UI surface; passing null detaches.
    /// </summary>
    MediaPlayer? Surface { get; }

    /// <summary>
    /// Optional XAML element surface for renderers that are not backed by
    /// <see cref="MediaPlayer"/>, such as WebView2/EME DRM playback.
    /// </summary>
    FrameworkElement? ElementSurface => null;

    /// <summary>True while the engine has loaded media (regardless of play/pause state).</summary>
    bool IsActive { get; }

    /// <summary>True while the active surface exists but has not rendered a first frame yet.</summary>
    bool IsSurfaceLoading => false;

    /// <summary>True after the active surface has rendered or opened its first video frame.</summary>
    bool HasFirstFrame => Surface is not null || ElementSurface is not null;

    /// <summary>
    /// Discriminator used by the UI for source-conditional UI bits (e.g.
    /// "Open folder" for local, "Open in Spotify" for Spotify). Free-form
    /// string; conventional values: <c>"local"</c>, <c>"spotify-music-video"</c>,
    /// <c>"spotify-podcast-video"</c>.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Pushes whenever <see cref="Surface"/> goes from null → non-null
    /// (engine became active) or non-null → null (engine stopped). Lets
    /// <see cref="IActiveVideoSurfaceService"/> arbitrate which UI surface
    /// owns the binding without polling.
    /// </summary>
    IObservable<VideoSurfaceChange> SurfaceChanges { get; }
}

/// <summary>
/// Notification payload for <see cref="IVideoSurfaceProvider.SurfaceChanges"/>.
/// </summary>
/// <param name="Surface">The new surface (null on stop).</param>
/// <param name="Kind">The provider's <see cref="IVideoSurfaceProvider.Kind"/>.</param>
public readonly record struct VideoSurfaceChange(MediaPlayer? Surface, string Kind);
