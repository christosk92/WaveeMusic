namespace Wavee.Playback.Contracts;

/// <summary>
/// Helpers for locating persistently cached audio files.
/// Both the UI process (to check before resolving CDN) and AudioHost (to write after download)
/// use these paths so they agree on where cached files live.
/// </summary>
public static class AudioFileCache
{
    private const string AudioSubDir = "audio";
    private const string CacheExtension = ".enc";

    /// <summary>
    /// Returns the full path of the persistent cache file for a given Spotify audio file ID.
    /// The file is stored encrypted (the same Spotify OGG encryption used on the CDN);
    /// the audio key is still required to decrypt it during playback.
    /// </summary>
    public static string GetCachedFilePath(string cacheDirectory, string fileIdHex)
        => Path.Combine(cacheDirectory, AudioSubDir, fileIdHex + CacheExtension);

    /// <summary>
    /// Returns true when the audio file for <paramref name="fileIdHex"/> is fully cached on disk.
    /// </summary>
    public static bool IsCached(string cacheDirectory, string fileIdHex)
    {
        if (string.IsNullOrEmpty(cacheDirectory) || string.IsNullOrEmpty(fileIdHex))
            return false;

        var path = GetCachedFilePath(cacheDirectory, fileIdHex);
        if (!File.Exists(path))
            return false;

        var info = new FileInfo(path);
        if (info.Length <= 0)
            return false;

        TryTouch(info);
        return true;
    }

    /// <summary>
    /// Returns the size (bytes) of the cached file, or 0 if it is not cached.
    /// </summary>
    public static long GetCachedFileSize(string cacheDirectory, string fileIdHex)
    {
        if (string.IsNullOrEmpty(cacheDirectory) || string.IsNullOrEmpty(fileIdHex))
            return 0;

        var path = GetCachedFilePath(cacheDirectory, fileIdHex);
        if (!File.Exists(path)) return 0;
        var info = new FileInfo(path);
        TryTouch(info);
        return info.Length;
    }

    /// <summary>
    /// Ensures the audio sub-directory exists under <paramref name="cacheDirectory"/>.
    /// </summary>
    public static void EnsureDirectoryExists(string cacheDirectory)
    {
        var dir = Path.Combine(cacheDirectory, AudioSubDir);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static void TryTouch(FileInfo info)
    {
        try
        {
            info.LastAccessTimeUtc = DateTime.UtcNow;
        }
        catch
        {
            // Cache access timestamps are best-effort metadata for LRU pruning.
        }
    }
}
