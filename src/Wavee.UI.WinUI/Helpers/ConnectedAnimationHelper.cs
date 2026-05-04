using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Helper class for managing connected animations between pages.
/// Connected animations animate a shared element (like an album cover) from source to destination page.
/// </summary>
public static class ConnectedAnimationHelper
{
    // Animation keys for different element types
    public const string AlbumArt = "albumArt";
    public const string ArtistImage = "artistImage";
    public const string PlaylistArt = "playlistArt";
    public const string PodcastArt = "podcastArt";
    public const string PodcastEpisodeArt = "podcastEpisodeArt";
    private static readonly HashSet<string> PendingKeys = [];
    private static readonly TimeSpan ConnectedAnimationDuration = TimeSpan.FromMilliseconds(300);
    private static readonly Vector2 ConnectedAnimationEaseControlPoint1 = new(0.37f, 0.0f);
    private static readonly Vector2 ConnectedAnimationEaseControlPoint2 = new(0.63f, 1.0f);

    /// <summary>
    /// Prepare a connected animation from the source element.
    /// Call this before navigating to the destination page.
    /// </summary>
    /// <param name="key">Animation key (use constants like AlbumArt, ArtistImage)</param>
    /// <param name="source">The source UI element to animate from</param>
    public static void PrepareAnimation(string key, UIElement source)
    {
        var service = ConnectedAnimationService.GetForCurrentView();
        ConfigureConnectedAnimation(service, source);
        service.PrepareToAnimate(key, source);
        PendingKeys.Add(key);
    }

    public static bool HasPendingAnimation(string key)
    {
        return PendingKeys.Contains(key);
    }

    /// <summary>
    /// Try to start a connected animation on the destination element.
    /// Call this in the destination page's OnNavigatedTo.
    /// </summary>
    /// <param name="key">Animation key (use constants like AlbumArt, ArtistImage)</param>
    /// <param name="destination">The destination UI element to animate to</param>
    /// <returns>True if animation started successfully, false if no animation was prepared</returns>
    public static bool TryStartAnimation(string key, UIElement destination)
    {
        var service = ConnectedAnimationService.GetForCurrentView();
        var animation = service.GetAnimation(key);

        if (animation != null)
        {
            ConfigureConnectedAnimation(service, destination);
            animation.Configuration = new GravityConnectedAnimationConfiguration();
            var started = animation.TryStart(destination);
            PendingKeys.Remove(key);
            return started;
        }

        PendingKeys.Remove(key);
        return false;
    }

    /// <summary>
    /// Try to start a connected animation with coordinated elements.
    /// Use this when you want secondary elements to animate alongside the main element.
    /// </summary>
    /// <param name="key">Animation key</param>
    /// <param name="destination">The main destination element</param>
    /// <param name="coordinatedElements">Secondary elements to animate with the main element</param>
    /// <returns>True if animation started successfully</returns>
    public static bool TryStartAnimationWithCoordinatedElements(
        string key,
        UIElement destination,
        params UIElement[] coordinatedElements)
    {
        var service = ConnectedAnimationService.GetForCurrentView();
        var animation = service.GetAnimation(key);

        if (animation != null)
        {
            ConfigureConnectedAnimation(service, destination);
            animation.Configuration = new BasicConnectedAnimationConfiguration();
            var started = animation.TryStart(destination, coordinatedElements);
            PendingKeys.Remove(key);
            return started;
        }

        PendingKeys.Remove(key);
        return false;
    }

    /// <summary>
    /// Cancel any pending connected animations to prevent E_ABORT during navigation
    /// when the source element is no longer in the visual tree.
    /// </summary>
    public static void CancelPending()
    {
        var service = ConnectedAnimationService.GetForCurrentView();
        // Cancel all known animation keys
        service.GetAnimation(AlbumArt)?.Cancel();
        service.GetAnimation(ArtistImage)?.Cancel();
        service.GetAnimation(PlaylistArt)?.Cancel();
        service.GetAnimation(PodcastArt)?.Cancel();
        service.GetAnimation(PodcastEpisodeArt)?.Cancel();
        PendingKeys.Remove(AlbumArt);
        PendingKeys.Remove(ArtistImage);
        PendingKeys.Remove(PlaylistArt);
        PendingKeys.Remove(PodcastArt);
        PendingKeys.Remove(PodcastEpisodeArt);
    }

    /// <summary>
    /// Prepare a connected animation for back navigation.
    /// Call this in the destination page when preparing to go back.
    /// </summary>
    /// <param name="key">Animation key</param>
    /// <param name="source">The element to animate back from</param>
    public static void PrepareBackAnimation(string key, UIElement source)
    {
        var service = ConnectedAnimationService.GetForCurrentView();
        ConfigureConnectedAnimation(service, source);
        var animation = service.PrepareToAnimate(key, source);
        animation.Configuration = new BasicConnectedAnimationConfiguration();
        PendingKeys.Add(key);
    }

    private static void ConfigureConnectedAnimation(ConnectedAnimationService service, UIElement anchor)
    {
        service.DefaultDuration = ConnectedAnimationDuration;

        var compositor = ElementCompositionPreview.GetElementVisual(anchor).Compositor;
        service.DefaultEasingFunction = compositor.CreateCubicBezierEasingFunction(
            ConnectedAnimationEaseControlPoint1,
            ConnectedAnimationEaseControlPoint2);
    }
}
