namespace Wavee.Core.Audio.Cache;

/// <summary>
/// Configuration for the audio cache.
/// </summary>
public sealed record AudioCacheConfig
{
    /// <summary>
    /// Directory where cached audio chunks are stored.
    /// Default: %LOCALAPPDATA%/Wavee/AudioCache
    /// </summary>
    public string CacheDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "AudioCache");

    /// <summary>
    /// Maximum cache size in bytes.
    /// Default: 1GB
    /// </summary>
    public long MaxCacheSizeBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether caching is enabled.
    /// Default: true
    /// </summary>
    public bool EnableCaching { get; init; } = true;

    /// <summary>
    /// Size of each chunk in bytes.
    /// Default: 128KB
    /// </summary>
    public int ChunkSize { get; init; } = 128 * 1024;

    /// <summary>
    /// How often to run cache pruning in the background.
    /// Default: 5 minutes
    /// </summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum free space to maintain when pruning.
    /// As percentage of MaxCacheSizeBytes.
    /// Default: 10%
    /// </summary>
    public double MinFreeSpacePercent { get; init; } = 0.10;

    /// <summary>
    /// Default configuration.
    /// </summary>
    public static AudioCacheConfig Default { get; } = new();
}
