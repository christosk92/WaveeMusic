using System;
using System.Linq;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.Helpers;

/// <summary>
/// Helper class for working with Spotify images
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Selects the best image from an array based on the desired size.
    /// Prefers images closest to the target size.
    /// </summary>
    /// <param name="images">Array of images to choose from</param>
    /// <param name="preferredSize">Preferred size (e.g., 300 for 300x300)</param>
    /// <returns>The best matching image URL, or null if no images available</returns>
    public static string? SelectBestImage(ImageDto[]? images, int preferredSize = 300)
    {
        if (images == null || images.Length == 0)
            return null;

        // If only one image, return it
        if (images.Length == 1)
            return images[0].Url;

        // Find the image closest to preferred size
        var bestImage = images
            .Where(img => img.Width.HasValue)
            .OrderBy(img => Math.Abs(img.Width!.Value - preferredSize))
            .FirstOrDefault();

        return bestImage?.Url ?? images[0].Url;
    }

    /// <summary>
    /// Creates a BitmapImage from a URL
    /// </summary>
    /// <param name="url">Image URL</param>
    /// <returns>BitmapImage instance</returns>
    public static BitmapImage? CreateBitmapImage(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        try
        {
            return new BitmapImage(new Uri(url));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets a BitmapImage from CoverArt (used by Albums, Artists, Shows, Episodes)
    /// </summary>
    public static BitmapImage? GetBitmapFromCoverArt(CoverArtDto? coverArt, int preferredSize = 300)
    {
        var url = SelectBestImage(coverArt?.Sources, preferredSize);
        return CreateBitmapImage(url);
    }

    /// <summary>
    /// Gets a BitmapImage from playlist images.items structure
    /// </summary>
    public static BitmapImage? GetBitmapFromPlaylistImages(ImageItemsContainerDto? images, int preferredSize = 300)
    {
        var firstItem = images?.Items?.FirstOrDefault();
        var url = SelectBestImage(firstItem?.Sources, preferredSize);
        return CreateBitmapImage(url);
    }

    /// <summary>
    /// Gets extracted colors from CoverArt
    /// </summary>
    public static ExtractedColorsDto? GetColorsFromCoverArt(CoverArtDto? coverArt)
    {
        var result = coverArt?.ExtractedColors;
        System.Diagnostics.Debug.WriteLine($"[ImageHelper] GetColorsFromCoverArt: coverArt={coverArt != null}, ExtractedColors={result != null}");
        return result;
    }

    /// <summary>
    /// Gets extracted colors from playlist images.items structure
    /// </summary>
    public static ExtractedColorsDto? GetColorsFromPlaylistImages(ImageItemsContainerDto? images)
    {
        var result = images?.Items?.FirstOrDefault()?.ExtractedColors;
        System.Diagnostics.Debug.WriteLine($"[ImageHelper] GetColorsFromPlaylistImages: images={images != null}, Items={images?.Items?.Length ?? 0}, ExtractedColors={result != null}");
        return result;
    }
}
