namespace Wavee.UI.WinUI.Data.Models.Common;

/// <summary>
/// Represents an image with multiple size variants.
/// </summary>
public sealed record ImageModel
{
    /// <summary>
    /// Small image URL (~64px).
    /// </summary>
    public string? SmallUrl { get; init; }

    /// <summary>
    /// Medium image URL (~300px).
    /// </summary>
    public string? MediumUrl { get; init; }

    /// <summary>
    /// Large image URL (~640px).
    /// </summary>
    public string? LargeUrl { get; init; }

    /// <summary>
    /// Extra large/original image URL.
    /// </summary>
    public string? ExtraLargeUrl { get; init; }

    /// <summary>
    /// Gets the best available URL for a target size.
    /// </summary>
    /// <param name="targetSize">Target display size in pixels.</param>
    /// <returns>The most appropriate image URL, or null if none available.</returns>
    public string? GetBestUrl(int targetSize = 300) => targetSize switch
    {
        <= 64 => SmallUrl ?? MediumUrl ?? LargeUrl ?? ExtraLargeUrl,
        <= 300 => MediumUrl ?? LargeUrl ?? SmallUrl ?? ExtraLargeUrl,
        <= 640 => LargeUrl ?? ExtraLargeUrl ?? MediumUrl ?? SmallUrl,
        _ => ExtraLargeUrl ?? LargeUrl ?? MediumUrl ?? SmallUrl
    };

    /// <summary>
    /// Creates an ImageModel from a single URL (used for all sizes).
    /// </summary>
    public static ImageModel FromUrl(string? url) => new()
    {
        SmallUrl = url,
        MediumUrl = url,
        LargeUrl = url,
        ExtraLargeUrl = url
    };

    /// <summary>
    /// Empty image model instance.
    /// </summary>
    public static ImageModel Empty { get; } = new();
}
