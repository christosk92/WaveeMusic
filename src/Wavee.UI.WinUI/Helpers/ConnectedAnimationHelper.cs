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

    // 250 ms — Microsoft's canonical Fluent "Normal" duration
    // (ControlNormalAnimationDuration in WinUI 3, used throughout the OS).
    // Long enough to land cleanly above the 215-230 ms human visual reaction
    // threshold (UX research consensus), short enough to feel responsive on
    // desktop. The previous Direct-config 150 ms was below that perception
    // threshold — animations finished before the eye could register them,
    // which read as "cheap" rather than "fast". Material 3 sits at 300 ms,
    // iOS HIG at 200-500 ms; 250 ms is the consensus sweet spot for
    // shared-element hero morphs on a Windows desktop client.
    //
    // Applies to Basic config (forward + coordinated paths below). Direct
    // config (used by back nav per Microsoft's explicit recommendation)
    // IGNORES this and hardcodes 150 ms — appropriate for "return to
    // previous state as quickly as possible" semantics.
    private static readonly TimeSpan ConnectedAnimationDuration = TimeSpan.FromMilliseconds(250);

    // cubic-bezier(0, 0, 0, 1) — Microsoft's official Fluent "Fast Out, Slow
    // In" curve, the platform's canonical decelerate easing. Sharp
    // acceleration at the start, long gentle landing. Premium feel that
    // tracks Material 3 emphasized-decelerate and iOS spring-decelerate.
    // Reference: Microsoft Learn "Timing and easing - Windows apps".
    private static readonly Vector2 ConnectedAnimationEaseControlPoint1 = new(0.0f, 0.0f);
    private static readonly Vector2 ConnectedAnimationEaseControlPoint2 = new(0.0f, 1.0f);

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
            // Basic config: respects ConnectedAnimationService.DefaultDuration
            // (250 ms) + DefaultEasingFunction (Fast Out, Slow In). Gravity
            // (old default) added a parabolic arc that felt gimmicky on
            // short transitions; Direct hardcodes 150 ms which is below the
            // human visual-reaction threshold (~215 ms) and reads as a
            // teleport rather than a morph. Basic gives the canonical
            // Fluent 250 ms + aggressive-decelerate combination that
            // matches Windows 11 native motion.
            animation.Configuration = new BasicConnectedAnimationConfiguration();
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
            // Basic for coordinated entries too — keeps the primary morph
            // and the coordinated fade-ins on the same 250 ms timeline +
            // same decelerate curve. Direct would split them visually
            // (primary at hardcoded 150 ms / coordinated at service default).
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
        // Microsoft Learn ("Connected animation" → Recommendations):
        // "Use DirectConnectedAnimationConfiguration for back navigation."
        // Direct's hardcoded 150 ms + decelerate easing is the canonical
        // back-nav feel — quicker and more direct than Basic, returning the
        // user to their previous state as fast as possible.
        animation.Configuration = new DirectConnectedAnimationConfiguration();
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
