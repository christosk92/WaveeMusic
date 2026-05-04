using System.Security.Cryptography;

namespace Wavee.Core.Library;

/// <summary>
/// Interface for caching extracted album art to disk.
/// </summary>
public interface IAlbumArtCache
{
    /// <summary>
    /// Caches album art data and returns the file path to the cached image.
    /// Uses content-based hashing to avoid duplicates.
    /// </summary>
    /// <param name="artData">Raw image data.</param>
    /// <param name="mimeType">MIME type of the image (image/jpeg, image/png, etc.).</param>
    /// <returns>File path to the cached image, or null if no data provided.</returns>
    Task<string?> CacheArtAsync(byte[]? artData, string? mimeType);

    /// <summary>
    /// Gets the path to a cached image by its hash, if it exists.
    /// </summary>
    /// <param name="artHash">The hash of the image data.</param>
    /// <returns>File path if cached, null otherwise.</returns>
    string? GetCachedArtPath(string artHash);

    /// <summary>
    /// Clears old cached images that haven't been accessed recently.
    /// </summary>
    /// <param name="maxAge">Maximum age of cached files to keep.</param>
    /// <returns>Number of files deleted.</returns>
    Task<int> CleanupAsync(TimeSpan maxAge);
}

/// <summary>
/// Caches extracted album art to disk with content-based deduplication.
/// </summary>
public sealed class AlbumArtCache : IAlbumArtCache
{
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Creates a new album art cache.
    /// </summary>
    /// <param name="cacheDirectory">Base cache directory. Album art will be stored in an "album-art" subdirectory.</param>
    public AlbumArtCache(string cacheDirectory)
    {
        _cacheDir = Path.Combine(cacheDirectory, "album-art");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <inheritdoc/>
    public async Task<string?> CacheArtAsync(byte[]? artData, string? mimeType)
    {
        if (artData == null || artData.Length == 0)
            return null;

        // Hash the art data for filename (content-based deduplication)
        var hash = ComputeHash(artData);
        var extension = GetExtensionFromMimeType(mimeType);
        var cachedPath = Path.Combine(_cacheDir, $"{hash}{extension}");

        // Check if already cached
        if (File.Exists(cachedPath))
        {
            // Update last access time for cleanup tracking
            try
            {
                File.SetLastAccessTimeUtc(cachedPath, DateTime.UtcNow);
            }
            catch
            {
                // Ignore access time update failures
            }
            return cachedPath;
        }

        // Write to cache (with lock to prevent concurrent writes of same file)
        await _writeLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!File.Exists(cachedPath))
            {
                await File.WriteAllBytesAsync(cachedPath, artData);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        return cachedPath;
    }

    /// <inheritdoc/>
    public string? GetCachedArtPath(string artHash)
    {
        // Check for any extension
        foreach (var ext in new[] { ".jpg", ".png", ".gif", ".bmp" })
        {
            var path = Path.Combine(_cacheDir, $"{artHash}{ext}");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <inheritdoc/>
    public Task<int> CleanupAsync(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;

        try
        {
            var files = Directory.GetFiles(_cacheDir);
            foreach (var file in files)
            {
                try
                {
                    var lastAccess = File.GetLastAccessTimeUtc(file);
                    if (lastAccess < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch
                {
                    // Ignore individual file deletion failures
                }
            }
        }
        catch
        {
            // Ignore directory access failures
        }

        return Task.FromResult(deleted);
    }

    private static string ComputeHash(byte[] data)
    {
        var hashBytes = SHA256.HashData(data);
        // Use first 16 hex chars (8 bytes = 64 bits) for filename
        return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
    }

    private static string GetExtensionFromMimeType(string? mimeType)
    {
        return mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            _ => ".jpg"  // Default to JPEG for image/jpeg and unknown
        };
    }
}
