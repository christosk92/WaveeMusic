using Microsoft.UI.Xaml;
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

    /// <summary>
    /// Prepare a connected animation from the source element.
    /// Call this before navigating to the destination page.
    /// </summary>
    /// <param name="key">Animation key (use constants like AlbumArt, ArtistImage)</param>
    /// <param name="source">The source UI element to animate from</param>
    public static void PrepareAnimation(string key, UIElement source)
    {
        var service = ConnectedAnimationService.GetForCurrentView();
        service.PrepareToAnimate(key, source);
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
            // Configure animation for smooth appearance
            animation.Configuration = new DirectConnectedAnimationConfiguration();
            return animation.TryStart(destination);
        }

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
            animation.Configuration = new DirectConnectedAnimationConfiguration();
            return animation.TryStart(destination, coordinatedElements);
        }

        return false;
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
        var animation = service.PrepareToAnimate(key, source);
        animation.Configuration = new DirectConnectedAnimationConfiguration();
    }
}
