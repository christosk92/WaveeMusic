using Wavee.Core.Http.Pathfinder;

namespace Wavee.Core.Http;

/// <summary>
/// Service for extracting dominant colors from image URLs.
/// Handles caching (in-memory + SQLite) and resilience.
/// </summary>
public interface IColorService
{
    /// <summary>
    /// Gets the extracted color for an image URL.
    /// Checks hot cache → SQLite → API.
    /// </summary>
    Task<ExtractedColor?> GetColorAsync(string imageUrl, CancellationToken ct = default);

    /// <summary>
    /// Gets extracted colors for multiple image URLs in a single batch.
    /// </summary>
    Task<Dictionary<string, ExtractedColor>> GetColorsAsync(IReadOnlyList<string> imageUrls, CancellationToken ct = default);
}
