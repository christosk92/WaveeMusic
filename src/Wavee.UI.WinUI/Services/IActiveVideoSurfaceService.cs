using System;
using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Aggregates all registered <see cref="IVideoSurfaceProvider"/>s and
/// arbitrates which UI <see cref="IMediaSurfaceConsumer"/> currently owns
/// the binding to the active provider's <see cref="MediaPlayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The service is the single broker between providers (engines) and consumers
/// (UI surfaces). Adding a new video source is "register a provider"; adding a
/// new UI surface is "implement <see cref="IMediaSurfaceConsumer"/> and call
/// AcquireSurface in Loaded / ReleaseSurface in Unloaded".
/// </para>
/// <para>
/// At most one consumer holds the binding at any moment — when a new consumer
/// acquires, the previous one is detached first. This keeps the
/// <see cref="MediaPlayer"/> rendering into exactly one element so handoff is
/// glitch-free.
/// </para>
/// </remarks>
public interface IActiveVideoSurfaceService
{
    /// <summary>The active provider, or null if no provider is currently active.</summary>
    IVideoSurfaceProvider? ActiveProvider { get; }

    /// <summary>The active provider's surface, or null when none is active.</summary>
    MediaPlayer? ActiveSurface { get; }

    /// <summary>The active provider's element surface, or null when none is active.</summary>
    FrameworkElement? ActiveElementSurface { get; }

    /// <summary>True when either a MediaPlayer or element surface is active.</summary>
    bool HasActiveSurface { get; }

    /// <summary>True while the active surface exists but has not rendered a first frame yet.</summary>
    bool IsActiveSurfaceLoading { get; }

    /// <summary>True after the active surface has rendered or opened its first video frame.</summary>
    bool HasActiveFirstFrame { get; }

    /// <summary>True after first frame when the active surface is buffering and may appear frozen.</summary>
    bool IsActiveSurfaceBuffering { get; }

    /// <summary>The UI surface currently bound to the active provider, if any.</summary>
    IMediaSurfaceConsumer? CurrentOwner { get; }

    /// <summary>The active provider's <see cref="IVideoSurfaceProvider.Kind"/>, or null.</summary>
    string? ActiveKind { get; }

    /// <summary>
    /// Fires whenever <see cref="ActiveSurface"/> changes — provider became
    /// active, provider went idle, or active-provider switched (e.g. local
    /// playback ended and a Spotify video started). Subscribed to by both
    /// the page and the mini-player to react to source changes.
    /// </summary>
    event EventHandler<MediaPlayer?>? ActiveSurfaceChanged;

    /// <summary>
    /// Fires whenever ownership of the active surface moves between UI hosts.
    /// </summary>
    event EventHandler? SurfaceOwnershipChanged;

    /// <summary>
    /// Register a video provider. Idempotent. Implementations call this
    /// from DI startup.
    /// </summary>
    void RegisterProvider(IVideoSurfaceProvider provider);

    /// <summary>
    /// Claim the active surface for this consumer. If another consumer
    /// currently owns it, that consumer is detached first. The new owner's
    /// <see cref="IMediaSurfaceConsumer.AttachSurface"/> is called immediately
    /// if a provider is active; otherwise the consumer is remembered and
    /// attached the moment a provider becomes active.
    /// </summary>
    void AcquireSurface(IMediaSurfaceConsumer consumer);

    /// <summary>
    /// Release the surface ownership held by this consumer. If this consumer
    /// is the current owner, it is detached and ownership becomes none.
    /// Calling on a non-owner is a no-op.
    /// </summary>
    void ReleaseSurface(IMediaSurfaceConsumer consumer);

    /// <summary>Returns true when <paramref name="consumer"/> owns the active surface.</summary>
    bool IsOwnedBy(IMediaSurfaceConsumer consumer);
}
