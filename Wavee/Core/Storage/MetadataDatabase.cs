using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Core.Storage;

/// <summary>
/// SQLite-backed metadata database for caching entity metadata and extension data.
/// Thread-safe with connection pooling and in-memory LRU cache for hot data.
/// </summary>
public sealed class MetadataDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedExtensionEntry> _hotCache = new();
    private readonly int _maxHotCacheSize;
    private bool _disposed;

    // Schema version for migrations
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Creates a new MetadataDatabase.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="maxHotCacheSize">Maximum entries in the in-memory hot cache.</param>
    /// <param name="logger">Optional logger.</param>
    public MetadataDatabase(string databasePath, int maxHotCacheSize = 1000, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _maxHotCacheSize = maxHotCacheSize;
        _logger = logger;

        // Ensure directory exists
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Build connection string with WAL mode for better concurrency
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ConnectionString;

        // Initialize schema
        InitializeSchema();

        _logger?.LogInformation("MetadataDatabase initialized at {Path}", databasePath);
    }

    #region Schema Initialization

    private void InitializeSchema()
    {
        using var connection = CreateConnection();
        connection.Open();

        // Enable WAL mode for better concurrent read/write performance
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        // Check schema version
        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion < CurrentSchemaVersion)
        {
            CreateTables(connection);
            SetSchemaVersion(connection, CurrentSchemaVersion);
        }
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    private static void SetSchemaVersion(SqliteConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private void CreateTables(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        try
        {
            // Entities table - stores queryable metadata properties
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS entities (
                        uri              TEXT PRIMARY KEY NOT NULL,
                        entity_type      INTEGER NOT NULL,

                        -- Common properties (nullable based on entity type)
                        title            TEXT,
                        artist_name      TEXT,
                        album_name       TEXT,
                        album_uri        TEXT,
                        duration_ms      INTEGER,
                        track_number     INTEGER,
                        disc_number      INTEGER,
                        release_year     INTEGER,
                        image_url        TEXT,

                        -- Album-specific
                        track_count      INTEGER,

                        -- Artist-specific
                        follower_count   INTEGER,

                        -- Show/Podcast-specific
                        publisher        TEXT,
                        episode_count    INTEGER,
                        description      TEXT,

                        -- Timestamps (stored as Unix seconds UTC)
                        created_at       INTEGER NOT NULL,
                        updated_at       INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Extension cache table - stores raw protobuf blobs
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS extension_cache (
                        entity_uri       TEXT NOT NULL,
                        extension_kind   INTEGER NOT NULL,
                        data             BLOB NOT NULL,
                        etag             TEXT,
                        expires_at       INTEGER NOT NULL,
                        created_at       INTEGER NOT NULL,
                        PRIMARY KEY (entity_uri, extension_kind)
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Indexes for common query patterns
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS idx_entities_type ON entities(entity_type);
                    CREATE INDEX IF NOT EXISTS idx_entities_artist ON entities(artist_name COLLATE NOCASE);
                    CREATE INDEX IF NOT EXISTS idx_entities_album ON entities(album_name COLLATE NOCASE);
                    CREATE INDEX IF NOT EXISTS idx_entities_title ON entities(title COLLATE NOCASE);
                    CREATE INDEX IF NOT EXISTS idx_entities_duration ON entities(duration_ms);
                    CREATE INDEX IF NOT EXISTS idx_entities_year ON entities(release_year);
                    CREATE INDEX IF NOT EXISTS idx_entities_updated ON entities(updated_at);
                    CREATE INDEX IF NOT EXISTS idx_extension_expires ON extension_cache(expires_at);
                    CREATE INDEX IF NOT EXISTS idx_extension_entity ON extension_cache(entity_uri);
                    """;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            _logger?.LogDebug("Database schema created successfully");
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    #endregion

    #region Entity Operations

    /// <summary>
    /// Upserts an entity with its metadata properties.
    /// </summary>
    public async Task UpsertEntityAsync(
        string uri,
        EntityType entityType,
        string? title = null,
        string? artistName = null,
        string? albumName = null,
        string? albumUri = null,
        int? durationMs = null,
        int? trackNumber = null,
        int? discNumber = null,
        int? releaseYear = null,
        string? imageUrl = null,
        int? trackCount = null,
        int? followerCount = null,
        string? publisher = null,
        int? episodeCount = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entities (
                    uri, entity_type, title, artist_name, album_name, album_uri,
                    duration_ms, track_number, disc_number, release_year, image_url,
                    track_count, follower_count, publisher, episode_count, description,
                    created_at, updated_at
                ) VALUES (
                    $uri, $entity_type, $title, $artist_name, $album_name, $album_uri,
                    $duration_ms, $track_number, $disc_number, $release_year, $image_url,
                    $track_count, $follower_count, $publisher, $episode_count, $description,
                    $now, $now
                )
                ON CONFLICT(uri) DO UPDATE SET
                    entity_type = excluded.entity_type,
                    title = COALESCE(excluded.title, entities.title),
                    artist_name = COALESCE(excluded.artist_name, entities.artist_name),
                    album_name = COALESCE(excluded.album_name, entities.album_name),
                    album_uri = COALESCE(excluded.album_uri, entities.album_uri),
                    duration_ms = COALESCE(excluded.duration_ms, entities.duration_ms),
                    track_number = COALESCE(excluded.track_number, entities.track_number),
                    disc_number = COALESCE(excluded.disc_number, entities.disc_number),
                    release_year = COALESCE(excluded.release_year, entities.release_year),
                    image_url = COALESCE(excluded.image_url, entities.image_url),
                    track_count = COALESCE(excluded.track_count, entities.track_count),
                    follower_count = COALESCE(excluded.follower_count, entities.follower_count),
                    publisher = COALESCE(excluded.publisher, entities.publisher),
                    episode_count = COALESCE(excluded.episode_count, entities.episode_count),
                    description = COALESCE(excluded.description, entities.description),
                    updated_at = $now;
                """;

            cmd.Parameters.AddWithValue("$uri", uri);
            cmd.Parameters.AddWithValue("$entity_type", (int)entityType);
            cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$artist_name", (object?)artistName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$album_name", (object?)albumName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$album_uri", (object?)albumUri ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$duration_ms", (object?)durationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$track_number", (object?)trackNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$disc_number", (object?)discNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$release_year", (object?)releaseYear ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$image_url", (object?)imageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$track_count", (object?)trackCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$follower_count", (object?)followerCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$publisher", (object?)publisher ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$episode_count", (object?)episodeCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Upserted entity {Uri}", uri);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets an entity by URI.
    /// </summary>
    public async Task<CachedEntity?> GetEntityAsync(string uri, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM entities WHERE uri = $uri;";
        cmd.Parameters.AddWithValue("$uri", uri);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadEntity(reader);
        }

        return null;
    }

    /// <summary>
    /// Queries entities with optional filters.
    /// </summary>
    public async Task<List<CachedEntity>> QueryEntitiesAsync(
        EntityType? entityType = null,
        string? artistNameContains = null,
        string? albumNameContains = null,
        string? titleContains = null,
        int? minDurationMs = null,
        int? maxDurationMs = null,
        int? minYear = null,
        int? maxYear = null,
        string? orderBy = null,
        bool descending = false,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CachedEntity>();
        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (entityType.HasValue)
        {
            conditions.Add("entity_type = $entity_type");
            parameters.Add(new SqliteParameter("$entity_type", (int)entityType.Value));
        }

        if (!string.IsNullOrEmpty(artistNameContains))
        {
            conditions.Add("artist_name LIKE $artist_name COLLATE NOCASE");
            parameters.Add(new SqliteParameter("$artist_name", $"%{artistNameContains}%"));
        }

        if (!string.IsNullOrEmpty(albumNameContains))
        {
            conditions.Add("album_name LIKE $album_name COLLATE NOCASE");
            parameters.Add(new SqliteParameter("$album_name", $"%{albumNameContains}%"));
        }

        if (!string.IsNullOrEmpty(titleContains))
        {
            conditions.Add("title LIKE $title COLLATE NOCASE");
            parameters.Add(new SqliteParameter("$title", $"%{titleContains}%"));
        }

        if (minDurationMs.HasValue)
        {
            conditions.Add("duration_ms >= $min_duration");
            parameters.Add(new SqliteParameter("$min_duration", minDurationMs.Value));
        }

        if (maxDurationMs.HasValue)
        {
            conditions.Add("duration_ms <= $max_duration");
            parameters.Add(new SqliteParameter("$max_duration", maxDurationMs.Value));
        }

        if (minYear.HasValue)
        {
            conditions.Add("release_year >= $min_year");
            parameters.Add(new SqliteParameter("$min_year", minYear.Value));
        }

        if (maxYear.HasValue)
        {
            conditions.Add("release_year <= $max_year");
            parameters.Add(new SqliteParameter("$max_year", maxYear.Value));
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        // Validate orderBy to prevent SQL injection
        var validOrderColumns = new HashSet<string>
        {
            "title", "artist_name", "album_name", "duration_ms", "release_year",
            "track_number", "created_at", "updated_at"
        };
        var orderClause = "";
        if (!string.IsNullOrEmpty(orderBy) && validOrderColumns.Contains(orderBy.ToLowerInvariant()))
        {
            orderClause = $"ORDER BY {orderBy} {(descending ? "DESC" : "ASC")}";
        }

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM entities {whereClause} {orderClause} LIMIT $limit OFFSET $offset;";

        foreach (var param in parameters)
        {
            cmd.Parameters.Add(param);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntity(reader));
        }

        return results;
    }

    private static CachedEntity ReadEntity(SqliteDataReader reader)
    {
        return new CachedEntity
        {
            Uri = reader.GetString(reader.GetOrdinal("uri")),
            EntityType = (EntityType)reader.GetInt32(reader.GetOrdinal("entity_type")),
            Title = reader.IsDBNull(reader.GetOrdinal("title")) ? null : reader.GetString(reader.GetOrdinal("title")),
            ArtistName = reader.IsDBNull(reader.GetOrdinal("artist_name")) ? null : reader.GetString(reader.GetOrdinal("artist_name")),
            AlbumName = reader.IsDBNull(reader.GetOrdinal("album_name")) ? null : reader.GetString(reader.GetOrdinal("album_name")),
            AlbumUri = reader.IsDBNull(reader.GetOrdinal("album_uri")) ? null : reader.GetString(reader.GetOrdinal("album_uri")),
            DurationMs = reader.IsDBNull(reader.GetOrdinal("duration_ms")) ? null : reader.GetInt32(reader.GetOrdinal("duration_ms")),
            TrackNumber = reader.IsDBNull(reader.GetOrdinal("track_number")) ? null : reader.GetInt32(reader.GetOrdinal("track_number")),
            DiscNumber = reader.IsDBNull(reader.GetOrdinal("disc_number")) ? null : reader.GetInt32(reader.GetOrdinal("disc_number")),
            ReleaseYear = reader.IsDBNull(reader.GetOrdinal("release_year")) ? null : reader.GetInt32(reader.GetOrdinal("release_year")),
            ImageUrl = reader.IsDBNull(reader.GetOrdinal("image_url")) ? null : reader.GetString(reader.GetOrdinal("image_url")),
            TrackCount = reader.IsDBNull(reader.GetOrdinal("track_count")) ? null : reader.GetInt32(reader.GetOrdinal("track_count")),
            FollowerCount = reader.IsDBNull(reader.GetOrdinal("follower_count")) ? null : reader.GetInt32(reader.GetOrdinal("follower_count")),
            Publisher = reader.IsDBNull(reader.GetOrdinal("publisher")) ? null : reader.GetString(reader.GetOrdinal("publisher")),
            EpisodeCount = reader.IsDBNull(reader.GetOrdinal("episode_count")) ? null : reader.GetInt32(reader.GetOrdinal("episode_count")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("updated_at")))
        };
    }

    #endregion

    #region Extension Cache Operations

    /// <summary>
    /// Gets cached extension data, checking hot cache first then SQLite.
    /// </summary>
    /// <param name="entityUri">The entity URI.</param>
    /// <param name="extensionKind">The extension kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached data and etag, or null if not found or expired.</returns>
    public async Task<(byte[] Data, string? Etag)?> GetExtensionAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = GetExtensionCacheKey(entityUri, extensionKind);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Check hot cache first
        if (_hotCache.TryGetValue(cacheKey, out var hotEntry))
        {
            if (hotEntry.ExpiresAt > now)
            {
                _logger?.LogTrace("Hot cache hit for {Uri}:{Kind}", entityUri, extensionKind);
                return (hotEntry.Data, hotEntry.Etag);
            }
            else
            {
                // Expired, remove from hot cache
                _hotCache.TryRemove(cacheKey, out _);
            }
        }

        // Check SQLite
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT data, etag, expires_at FROM extension_cache
            WHERE entity_uri = $entity_uri AND extension_kind = $extension_kind;
            """;
        cmd.Parameters.AddWithValue("$entity_uri", entityUri);
        cmd.Parameters.AddWithValue("$extension_kind", (int)extensionKind);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var expiresAt = reader.GetInt64(2);
            if (expiresAt > now)
            {
                var data = (byte[])reader.GetValue(0);
                var etag = reader.IsDBNull(1) ? null : reader.GetString(1);

                // Promote to hot cache
                PromoteToHotCache(cacheKey, data, etag, expiresAt);

                _logger?.LogTrace("SQLite cache hit for {Uri}:{Kind}", entityUri, extensionKind);
                return (data, etag);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the etag for cached extension data (for conditional requests).
    /// Returns etag even if data is expired.
    /// </summary>
    public async Task<string?> GetExtensionEtagAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = GetExtensionCacheKey(entityUri, extensionKind);

        // Check hot cache first
        if (_hotCache.TryGetValue(cacheKey, out var hotEntry))
        {
            return hotEntry.Etag;
        }

        // Check SQLite
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT etag FROM extension_cache
            WHERE entity_uri = $entity_uri AND extension_kind = $extension_kind;
            """;
        cmd.Parameters.AddWithValue("$entity_uri", entityUri);
        cmd.Parameters.AddWithValue("$extension_kind", (int)extensionKind);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    /// <summary>
    /// Stores extension data in both hot cache and SQLite.
    /// </summary>
    public async Task SetExtensionAsync(
        string entityUri,
        ExtensionKind extensionKind,
        byte[] data,
        string? etag,
        long ttlSeconds,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = now + ttlSeconds;
        var cacheKey = GetExtensionCacheKey(entityUri, extensionKind);

        // Update hot cache
        PromoteToHotCache(cacheKey, data, etag, expiresAt);

        // Update SQLite
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO extension_cache (entity_uri, extension_kind, data, etag, expires_at, created_at)
                VALUES ($entity_uri, $extension_kind, $data, $etag, $expires_at, $now)
                ON CONFLICT(entity_uri, extension_kind) DO UPDATE SET
                    data = excluded.data,
                    etag = excluded.etag,
                    expires_at = excluded.expires_at;
                """;

            cmd.Parameters.AddWithValue("$entity_uri", entityUri);
            cmd.Parameters.AddWithValue("$extension_kind", (int)extensionKind);
            cmd.Parameters.AddWithValue("$data", data);
            cmd.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$expires_at", expiresAt);
            cmd.Parameters.AddWithValue("$now", now);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Cached extension {Uri}:{Kind} with TTL={TTL}s", entityUri, extensionKind, ttlSeconds);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Removes expired extension cache entries.
    /// </summary>
    public async Task<int> CleanupExpiredExtensionsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Clean hot cache
        var expiredKeys = _hotCache
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _hotCache.TryRemove(key, out _);
        }

        // Clean SQLite
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM extension_cache WHERE expires_at <= $now;";
            cmd.Parameters.AddWithValue("$now", now);

            var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger?.LogDebug("Cleaned up {Count} expired extension cache entries", deleted);
            }

            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Invalidates all cached data for an entity.
    /// </summary>
    public async Task InvalidateEntityAsync(string entityUri, CancellationToken cancellationToken = default)
    {
        // Remove from hot cache
        var keysToRemove = _hotCache.Keys.Where(k => k.StartsWith(entityUri + ":")).ToList();
        foreach (var key in keysToRemove)
        {
            _hotCache.TryRemove(key, out _);
        }

        // Remove from SQLite
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM extension_cache WHERE entity_uri = $entity_uri;";
            cmd.Parameters.AddWithValue("$entity_uri", entityUri);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogDebug("Invalidated cache for {EntityUri}", entityUri);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Database Maintenance

    /// <summary>
    /// Gets database statistics.
    /// </summary>
    public async Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        long entityCount = 0;
        long extensionCacheCount = 0;
        long extensionCacheBytes = 0;
        long expiredCount = 0;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM entities;";
            entityCount = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(data)), 0) FROM extension_cache;";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                extensionCacheCount = reader.GetInt64(0);
                extensionCacheBytes = reader.GetInt64(1);
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM extension_cache WHERE expires_at <= $now;";
            cmd.Parameters.AddWithValue("$now", now);
            expiredCount = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        return new DatabaseStatistics
        {
            EntityCount = entityCount,
            ExtensionCacheCount = extensionCacheCount,
            ExtensionCacheSizeBytes = extensionCacheBytes,
            ExpiredExtensionCount = expiredCount,
            HotCacheCount = _hotCache.Count
        };
    }

    /// <summary>
    /// Compacts the database file (VACUUM).
    /// </summary>
    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogInformation("Database vacuumed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Clears all data from the database.
    /// </summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        _hotCache.Clear();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM extension_cache;
                DELETE FROM entities;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogInformation("Database cleared");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Private Helpers

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private static string GetExtensionCacheKey(string entityUri, ExtensionKind extensionKind)
    {
        return $"{entityUri}:{(int)extensionKind}";
    }

    private void PromoteToHotCache(string cacheKey, byte[] data, string? etag, long expiresAt)
    {
        // Simple size-based eviction if we're at capacity
        if (_hotCache.Count >= _maxHotCacheSize)
        {
            // Remove ~10% of oldest entries
            var keysToRemove = _hotCache
                .OrderBy(kvp => kvp.Value.ExpiresAt)
                .Take(_maxHotCacheSize / 10)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _hotCache.TryRemove(key, out _);
            }
        }

        _hotCache[cacheKey] = new CachedExtensionEntry(data, etag, expiresAt);
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _hotCache.Clear();
        _writeLock.Dispose();

        _logger?.LogDebug("MetadataDatabase disposed");

        await Task.CompletedTask;
    }

    #endregion

    /// <summary>
    /// In-memory cache entry for hot extension data.
    /// </summary>
    private sealed record CachedExtensionEntry(byte[] Data, string? Etag, long ExpiresAt);
}

/// <summary>
/// Cached entity with queryable metadata.
/// </summary>
public sealed class CachedEntity
{
    public required string Uri { get; init; }
    public required EntityType EntityType { get; init; }

    // Common properties
    public string? Title { get; init; }
    public string? ArtistName { get; init; }
    public string? AlbumName { get; init; }
    public string? AlbumUri { get; init; }
    public int? DurationMs { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int? ReleaseYear { get; init; }
    public string? ImageUrl { get; init; }

    // Album-specific
    public int? TrackCount { get; init; }

    // Artist-specific
    public int? FollowerCount { get; init; }

    // Show/Podcast-specific
    public string? Publisher { get; init; }
    public int? EpisodeCount { get; init; }
    public string? Description { get; init; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Database statistics for monitoring.
/// </summary>
public sealed record DatabaseStatistics
{
    public long EntityCount { get; init; }
    public long ExtensionCacheCount { get; init; }
    public long ExtensionCacheSizeBytes { get; init; }
    public long ExpiredExtensionCount { get; init; }
    public int HotCacheCount { get; init; }

    public double ExtensionCacheSizeMB => ExtensionCacheSizeBytes / (1024.0 * 1024.0);
}
