using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Audio.Cache;

/// <summary>
/// Metadata for a cached audio file.
/// </summary>
public sealed class CacheEntry
{
    /// <summary>
    /// File ID (hex string).
    /// </summary>
    [JsonPropertyName("fileId")]
    public string FileId { get; init; } = string.Empty;

    /// <summary>
    /// Total file size in bytes.
    /// </summary>
    [JsonPropertyName("fileSize")]
    public long FileSize { get; init; }

    /// <summary>
    /// Audio file format.
    /// </summary>
    [JsonPropertyName("format")]
    public AudioFileFormat Format { get; init; }

    /// <summary>
    /// Chunk size used for this file.
    /// </summary>
    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; init; }

    /// <summary>
    /// Total number of chunks.
    /// </summary>
    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }

    /// <summary>
    /// Set of cached chunk indices.
    /// </summary>
    [JsonPropertyName("cachedChunks")]
    public HashSet<int> CachedChunks { get; init; } = new();

    /// <summary>
    /// When the entry was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the entry was last accessed.
    /// </summary>
    [JsonPropertyName("lastAccessed")]
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the total cached size in bytes.
    /// </summary>
    [JsonIgnore]
    public long CachedBytes => (long)CachedChunks.Count * ChunkSize;

    /// <summary>
    /// Checks if a specific chunk is cached.
    /// </summary>
    public bool HasChunk(int chunkIndex) => CachedChunks.Contains(chunkIndex);

    /// <summary>
    /// Checks if all chunks are cached (file is complete).
    /// </summary>
    [JsonIgnore]
    public bool IsComplete => CachedChunks.Count >= ChunkCount;

    /// <summary>
    /// Gets the percentage of the file that is cached.
    /// </summary>
    [JsonIgnore]
    public double CachePercentage => ChunkCount > 0
        ? (double)CachedChunks.Count / ChunkCount * 100
        : 0;

    /// <summary>
    /// Creates a new cache entry for a file.
    /// </summary>
    public static CacheEntry Create(FileId fileId, long fileSize, AudioFileFormat format, int chunkSize)
    {
        var chunkCount = (int)Math.Ceiling((double)fileSize / chunkSize);
        return new CacheEntry
        {
            FileId = fileId.ToBase16(),
            FileSize = fileSize,
            Format = format,
            ChunkSize = chunkSize,
            ChunkCount = chunkCount
        };
    }

    /// <summary>
    /// Serializes entry to JSON.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, CacheEntryJsonContext.Default.CacheEntry);

    /// <summary>
    /// Deserializes entry from JSON.
    /// </summary>
    public static CacheEntry? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CacheEntryJsonContext.Default.CacheEntry);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// AOT-compatible JSON serialization context for CacheEntry.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(CacheEntry))]
internal partial class CacheEntryJsonContext : JsonSerializerContext
{
}
