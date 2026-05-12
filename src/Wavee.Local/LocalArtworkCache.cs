using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Wavee.Local;

/// <summary>
/// Deduped local-artwork cache. Bytes are SHA-1 hashed; the first 2 hex chars
/// fan out into shard subdirectories under <c>%LOCALAPPDATA%/Wavee/local-artwork/</c>.
/// Returns the <c>wavee-artwork://{hash}</c> URI that callers store on entities
/// and feed into the image pipeline.
/// </summary>
public sealed class LocalArtworkCache
{
    public const string UriScheme = "wavee-artwork://";

    private readonly string _rootDir;
    private readonly ILogger? _logger;

    public LocalArtworkCache(string rootDir, ILogger? logger = null)
    {
        _rootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
        _logger = logger;
        Directory.CreateDirectory(_rootDir);
    }

    public string RootDirectory => _rootDir;

    /// <summary>Resolves a <c>wavee-artwork://{hash}</c> URI to a cached file path, or null.</summary>
    public string? ResolvePath(string artworkUri)
    {
        if (string.IsNullOrEmpty(artworkUri) || !artworkUri.StartsWith(UriScheme, StringComparison.Ordinal))
            return null;
        var hash = artworkUri.Substring(UriScheme.Length);
        return BuildPath(hash);
    }

    /// <summary>
    /// Stores artwork bytes if not already cached; links the entity URI to the artwork
    /// in <c>local_artwork_links</c>. Returns the <c>wavee-artwork://{hash}</c> URI.
    /// </summary>
    public string StoreAndLink(SqliteConnection connection, SqliteTransaction? tx,
        string entityUri, string role, ReadOnlySpan<byte> bytes, string? mimeType)
    {
        if (bytes.IsEmpty) throw new ArgumentException("Artwork bytes are empty.", nameof(bytes));

        var hashBytes = SHA1.HashData(bytes);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var path = BuildPath(hash);
        var ext = MimeToExtension(mimeType);
        var fullPath = path + ext;

        if (!File.Exists(fullPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllBytes(fullPath, bytes.ToArray());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write local artwork to {Path}", fullPath);
            }
        }

        // Upsert local_artwork
        using (var cmd = connection.CreateCommand())
        {
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO local_artwork (image_hash, cached_path, mime_type, created_at)
                VALUES ($hash, $path, $mime, $now);
                """;
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$path", Path.GetFileName(fullPath));
            cmd.Parameters.AddWithValue("$mime", (object?)mimeType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }

        // Link entity → artwork
        using (var cmd = connection.CreateCommand())
        {
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO local_artwork_links (entity_uri, role, image_hash)
                VALUES ($uri, $role, $hash);
                """;
            cmd.Parameters.AddWithValue("$uri", entityUri);
            cmd.Parameters.AddWithValue("$role", role);
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.ExecuteNonQuery();
        }

        return UriScheme + hash;
    }

    private string BuildPath(string hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length < 2) return string.Empty;
        return Path.Combine(_rootDir, hash.Substring(0, 2), hash);
    }

    private static string MimeToExtension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".jpg",
    };
}
