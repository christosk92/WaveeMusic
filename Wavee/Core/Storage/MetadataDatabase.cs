using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Core.Storage;

/// <summary>
/// SQLite-backed metadata database for caching entity metadata and extension data.
/// Thread-safe with connection pooling and in-memory LRU cache for hot data.
/// </summary>
public sealed class MetadataDatabase : IMetadataDatabase
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedExtensionEntry> _hotCache = new();
    private readonly int _maxHotCacheSize;
    private bool _disposed;

    // Schema version for migrations - bump to force fresh start
    // v4: Added is_from_rootlist column to spotify_playlists
    private const int CurrentSchemaVersion = 4;

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

        // Check schema version - fresh start on version mismatch
        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion != CurrentSchemaVersion)
        {
            if (currentVersion > 0)
            {
                _logger?.LogInformation("Schema version changed from {Old} to {New}, dropping all tables for fresh start",
                    currentVersion, CurrentSchemaVersion);
                DropAllTables(connection);
            }
            CreateTables(connection);
            SetSchemaVersion(connection, CurrentSchemaVersion);
        }
    }

    private static void DropAllTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS extension_cache;
            DROP TABLE IF EXISTS entities;
            DROP TABLE IF EXISTS spotify_library;
            DROP TABLE IF EXISTS play_history;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS watched_folders;
            DROP TABLE IF EXISTS podcast_shows;
            DROP TABLE IF EXISTS podcast_episodes;
            DROP TABLE IF EXISTS episode_progress;
            DROP TABLE IF EXISTS spotify_playlists;
            """;
        cmd.ExecuteNonQuery();
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
            // Entities table - unified metadata for ALL sources (Spotify, local files, streams)
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS entities (
                        uri              TEXT PRIMARY KEY NOT NULL,
                        source_type      INTEGER NOT NULL DEFAULT 0,  -- 0=Spotify, 1=Local, 2=Stream
                        entity_type      INTEGER NOT NULL,            -- Track=1, Album=2, Artist=3, etc.

                        -- Common queryable metadata
                        title            TEXT,
                        artist_name      TEXT,
                        album_name       TEXT,
                        album_uri        TEXT,
                        duration_ms      INTEGER,
                        track_number     INTEGER,
                        disc_number      INTEGER,
                        release_year     INTEGER,
                        image_url        TEXT,
                        genre            TEXT,

                        -- Album-specific
                        track_count      INTEGER,

                        -- Artist-specific
                        follower_count   INTEGER,

                        -- Show/Podcast-specific
                        publisher        TEXT,
                        episode_count    INTEGER,
                        description      TEXT,

                        -- Local file specific
                        file_path        TEXT,

                        -- Stream specific
                        stream_url       TEXT,

                        -- Spotify cache TTL (NULL for local files = permanent)
                        expires_at       INTEGER,

                        -- Timestamps (Unix seconds UTC)
                        added_at         INTEGER,
                        created_at       INTEGER NOT NULL,
                        updated_at       INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Extension cache table - stores raw protobuf blobs (Spotify only)
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

            // Spotify library - tracks which Spotify items are "liked/saved"
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS spotify_library (
                        item_uri    TEXT PRIMARY KEY NOT NULL,
                        item_type   INTEGER NOT NULL,
                        added_at    INTEGER NOT NULL,
                        synced_at   INTEGER NOT NULL,
                        FOREIGN KEY (item_uri) REFERENCES entities(uri) ON DELETE CASCADE
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Play history - play events for all sources
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS play_history (
                        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                        item_uri            TEXT NOT NULL,
                        played_at           INTEGER NOT NULL,
                        duration_played_ms  INTEGER NOT NULL,
                        completed           INTEGER NOT NULL DEFAULT 0,
                        source_context      TEXT,
                        FOREIGN KEY (item_uri) REFERENCES entities(uri) ON DELETE CASCADE
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Sync state - revision tracking for incremental sync
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS sync_state (
                        collection_type  TEXT PRIMARY KEY NOT NULL,
                        revision         TEXT,
                        last_sync_at     INTEGER NOT NULL,
                        item_count       INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Watched folders - local file scanning config
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS watched_folders (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        path            TEXT NOT NULL UNIQUE,
                        last_scan_at    INTEGER,
                        file_count      INTEGER NOT NULL DEFAULT 0,
                        enabled         INTEGER NOT NULL DEFAULT 1
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Podcast shows - subscriptions
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS podcast_shows (
                        id                  TEXT PRIMARY KEY NOT NULL,
                        source_type         INTEGER NOT NULL DEFAULT 0,
                        title               TEXT NOT NULL,
                        publisher           TEXT,
                        description         TEXT,
                        image_url           TEXT,
                        feed_url            TEXT,
                        spotify_uri         TEXT,
                        episode_count       INTEGER NOT NULL DEFAULT 0,
                        subscribed_at       INTEGER NOT NULL,
                        last_refreshed_at   INTEGER,
                        last_etag           TEXT,
                        language            TEXT,
                        categories_json     TEXT
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Podcast episodes
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS podcast_episodes (
                        id                      TEXT PRIMARY KEY NOT NULL,
                        show_id                 TEXT NOT NULL,
                        source_type             INTEGER NOT NULL DEFAULT 0,
                        title                   TEXT NOT NULL,
                        description             TEXT,
                        image_url               TEXT,
                        duration_ms             INTEGER,
                        published_at            INTEGER,
                        playback_position_ms    INTEGER NOT NULL DEFAULT 0,
                        is_played               INTEGER NOT NULL DEFAULT 0,
                        download_state          INTEGER NOT NULL DEFAULT 0,
                        local_file_path         TEXT,
                        file_size_bytes         INTEGER,
                        downloaded_bytes        INTEGER,
                        FOREIGN KEY (show_id) REFERENCES podcast_shows(id) ON DELETE CASCADE
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Episode progress - cross-device sync
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS episode_progress (
                        episode_id      TEXT PRIMARY KEY NOT NULL,
                        position_ms     INTEGER NOT NULL DEFAULT 0,
                        is_played       INTEGER NOT NULL DEFAULT 0,
                        updated_at      INTEGER NOT NULL,
                        is_synced       INTEGER NOT NULL DEFAULT 0,
                        FOREIGN KEY (episode_id) REFERENCES podcast_episodes(id) ON DELETE CASCADE
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Spotify playlists
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS spotify_playlists (
                        id                  TEXT PRIMARY KEY NOT NULL,
                        name                TEXT NOT NULL,
                        owner_id            TEXT,
                        description         TEXT,
                        image_url           TEXT,
                        track_count         INTEGER NOT NULL DEFAULT 0,
                        is_public           INTEGER NOT NULL DEFAULT 1,
                        is_collaborative    INTEGER NOT NULL DEFAULT 0,
                        is_owned            INTEGER NOT NULL DEFAULT 0,
                        synced_at           INTEGER NOT NULL,
                        revision            TEXT,
                        folder_path         TEXT,
                        is_from_rootlist    INTEGER NOT NULL DEFAULT 1
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Indexes for common query patterns
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS idx_entities_source ON entities(source_type);
                    CREATE INDEX IF NOT EXISTS idx_entities_type ON entities(entity_type);
                    CREATE INDEX IF NOT EXISTS idx_entities_artist ON entities(artist_name COLLATE NOCASE);
                    CREATE INDEX IF NOT EXISTS idx_entities_album ON entities(album_name COLLATE NOCASE);
                    CREATE INDEX IF NOT EXISTS idx_entities_title ON entities(title COLLATE NOCASE);
                    CREATE INDEX IF NOT EXISTS idx_entities_duration ON entities(duration_ms);
                    CREATE INDEX IF NOT EXISTS idx_entities_year ON entities(release_year);
                    CREATE INDEX IF NOT EXISTS idx_entities_updated ON entities(updated_at);
                    CREATE INDEX IF NOT EXISTS idx_entities_expires ON entities(expires_at);
                    CREATE INDEX IF NOT EXISTS idx_extension_expires ON extension_cache(expires_at);
                    CREATE INDEX IF NOT EXISTS idx_extension_entity ON extension_cache(entity_uri);
                    CREATE INDEX IF NOT EXISTS idx_spotify_library_type ON spotify_library(item_type);
                    CREATE INDEX IF NOT EXISTS idx_play_history_uri ON play_history(item_uri);
                    CREATE INDEX IF NOT EXISTS idx_play_history_played ON play_history(played_at);
                    CREATE INDEX IF NOT EXISTS idx_podcast_episodes_show ON podcast_episodes(show_id);
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
        SourceType sourceType = SourceType.Spotify,
        string? title = null,
        string? artistName = null,
        string? albumName = null,
        string? albumUri = null,
        int? durationMs = null,
        int? trackNumber = null,
        int? discNumber = null,
        int? releaseYear = null,
        string? imageUrl = null,
        string? genre = null,
        int? trackCount = null,
        int? followerCount = null,
        string? publisher = null,
        int? episodeCount = null,
        string? description = null,
        string? filePath = null,
        string? streamUrl = null,
        long? expiresAt = null,
        long? addedAt = null,
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
                    uri, source_type, entity_type, title, artist_name, album_name, album_uri,
                    duration_ms, track_number, disc_number, release_year, image_url, genre,
                    track_count, follower_count, publisher, episode_count, description,
                    file_path, stream_url, expires_at, added_at, created_at, updated_at
                ) VALUES (
                    $uri, $source_type, $entity_type, $title, $artist_name, $album_name, $album_uri,
                    $duration_ms, $track_number, $disc_number, $release_year, $image_url, $genre,
                    $track_count, $follower_count, $publisher, $episode_count, $description,
                    $file_path, $stream_url, $expires_at, $added_at, $now, $now
                )
                ON CONFLICT(uri) DO UPDATE SET
                    source_type = excluded.source_type,
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
                    genre = COALESCE(excluded.genre, entities.genre),
                    track_count = COALESCE(excluded.track_count, entities.track_count),
                    follower_count = COALESCE(excluded.follower_count, entities.follower_count),
                    publisher = COALESCE(excluded.publisher, entities.publisher),
                    episode_count = COALESCE(excluded.episode_count, entities.episode_count),
                    description = COALESCE(excluded.description, entities.description),
                    file_path = COALESCE(excluded.file_path, entities.file_path),
                    stream_url = COALESCE(excluded.stream_url, entities.stream_url),
                    expires_at = COALESCE(excluded.expires_at, entities.expires_at),
                    added_at = COALESCE(excluded.added_at, entities.added_at),
                    updated_at = $now;
                """;

            cmd.Parameters.AddWithValue("$uri", uri);
            cmd.Parameters.AddWithValue("$source_type", (int)sourceType);
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
            cmd.Parameters.AddWithValue("$genre", (object?)genre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$track_count", (object?)trackCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$follower_count", (object?)followerCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$publisher", (object?)publisher ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$episode_count", (object?)episodeCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$file_path", (object?)filePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stream_url", (object?)streamUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$expires_at", (object?)expiresAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$added_at", (object?)addedAt ?? DBNull.Value);
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
    /// Gets multiple entities by URIs.
    /// </summary>
    public async Task<List<CachedEntity>> GetEntitiesAsync(IEnumerable<string> uris, CancellationToken cancellationToken = default)
    {
        var uriList = uris.ToList();
        if (uriList.Count == 0)
            return new List<CachedEntity>();

        var results = new List<CachedEntity>();

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // Build parameterized query for batch fetch
        var placeholders = string.Join(", ", uriList.Select((_, i) => $"$uri{i}"));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM entities WHERE uri IN ({placeholders});";

        for (int i = 0; i < uriList.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$uri{i}", uriList[i]);
        }

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntity(reader));
        }

        return results;
    }

    /// <summary>
    /// Deletes an entity by URI.
    /// </summary>
    public async Task DeleteEntityAsync(string uri, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM entities WHERE uri = $uri;";
            cmd.Parameters.AddWithValue("$uri", uri);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Deleted entity {Uri}", uri);
        }
        finally
        {
            _writeLock.Release();
        }
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
        var expiresAtOrdinal = reader.GetOrdinal("expires_at");
        var addedAtOrdinal = reader.GetOrdinal("added_at");

        return new CachedEntity
        {
            Uri = reader.GetString(reader.GetOrdinal("uri")),
            SourceType = (SourceType)reader.GetInt32(reader.GetOrdinal("source_type")),
            EntityType = (EntityType)reader.GetInt32(reader.GetOrdinal("entity_type")),
            Title = reader.IsDBNull(reader.GetOrdinal("title")) ? null : reader.GetString(reader.GetOrdinal("title")),
            ArtistName = reader.IsDBNull(reader.GetOrdinal("artist_name")) ? null : reader.GetString(reader.GetOrdinal("artist_name")),
            AlbumName = reader.IsDBNull(reader.GetOrdinal("album_name")) ? null : reader.GetString(reader.GetOrdinal("album_name")),
            AlbumUri = reader.IsDBNull(reader.GetOrdinal("album_uri")) ? null : reader.GetString(reader.GetOrdinal("album_uri")),
            DurationMs = reader.IsDBNull(reader.GetOrdinal("duration_ms")) ? null : reader.GetInt32(reader.GetOrdinal("duration_ms")),
            TrackNumber = reader.IsDBNull(reader.GetOrdinal("track_number")) ? null : reader.GetInt32(reader.GetOrdinal("track_number")),
            DiscNumber = reader.IsDBNull(reader.GetOrdinal("disc_number")) ? null : reader.GetInt32(reader.GetOrdinal("disc_number")),
            ReleaseYear = reader.IsDBNull(reader.GetOrdinal("release_year")) ? null : reader.GetInt32(reader.GetOrdinal("release_year")),
            Genre = reader.IsDBNull(reader.GetOrdinal("genre")) ? null : reader.GetString(reader.GetOrdinal("genre")),
            ImageUrl = reader.IsDBNull(reader.GetOrdinal("image_url")) ? null : reader.GetString(reader.GetOrdinal("image_url")),
            TrackCount = reader.IsDBNull(reader.GetOrdinal("track_count")) ? null : reader.GetInt32(reader.GetOrdinal("track_count")),
            FollowerCount = reader.IsDBNull(reader.GetOrdinal("follower_count")) ? null : reader.GetInt32(reader.GetOrdinal("follower_count")),
            Publisher = reader.IsDBNull(reader.GetOrdinal("publisher")) ? null : reader.GetString(reader.GetOrdinal("publisher")),
            EpisodeCount = reader.IsDBNull(reader.GetOrdinal("episode_count")) ? null : reader.GetInt32(reader.GetOrdinal("episode_count")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            FilePath = reader.IsDBNull(reader.GetOrdinal("file_path")) ? null : reader.GetString(reader.GetOrdinal("file_path")),
            StreamUrl = reader.IsDBNull(reader.GetOrdinal("stream_url")) ? null : reader.GetString(reader.GetOrdinal("stream_url")),
            ExpiresAt = reader.IsDBNull(expiresAtOrdinal) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(expiresAtOrdinal)),
            AddedAt = reader.IsDBNull(addedAtOrdinal) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(addedAtOrdinal)),
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

    #region Spotify Library Operations

    /// <summary>
    /// Adds or updates an item in the Spotify library.
    /// </summary>
    public async Task AddToSpotifyLibraryAsync(
        string itemUri,
        SpotifyLibraryItemType itemType,
        long addedAt,
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
                INSERT INTO spotify_library (item_uri, item_type, added_at, synced_at)
                VALUES ($item_uri, $item_type, $added_at, $synced_at)
                ON CONFLICT(item_uri) DO UPDATE SET
                    item_type = excluded.item_type,
                    added_at = excluded.added_at,
                    synced_at = excluded.synced_at;
                """;

            cmd.Parameters.AddWithValue("$item_uri", itemUri);
            cmd.Parameters.AddWithValue("$item_type", (int)itemType);
            cmd.Parameters.AddWithValue("$added_at", addedAt);
            cmd.Parameters.AddWithValue("$synced_at", now);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Added {Uri} to Spotify library as {Type}", itemUri, itemType);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Removes an item from the Spotify library.
    /// </summary>
    public async Task RemoveFromSpotifyLibraryAsync(string itemUri, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM spotify_library WHERE item_uri = $item_uri;";
            cmd.Parameters.AddWithValue("$item_uri", itemUri);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Removed {Uri} from Spotify library", itemUri);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Checks if an item is in the Spotify library.
    /// </summary>
    public async Task<bool> IsInSpotifyLibraryAsync(string itemUri, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM spotify_library WHERE item_uri = $item_uri LIMIT 1;";
        cmd.Parameters.AddWithValue("$item_uri", itemUri);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    /// <summary>
    /// Gets Spotify library items with their entity metadata.
    /// </summary>
    public async Task<List<CachedEntity>> GetSpotifyLibraryItemsAsync(
        SpotifyLibraryItemType itemType,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CachedEntity>();

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.* FROM entities e
            INNER JOIN spotify_library sl ON e.uri = sl.item_uri
            WHERE sl.item_type = $item_type
            ORDER BY sl.added_at DESC
            LIMIT $limit OFFSET $offset;
            """;

        cmd.Parameters.AddWithValue("$item_type", (int)itemType);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntity(reader));
        }

        return results;
    }

    /// <summary>
    /// Gets the count of items in the Spotify library.
    /// </summary>
    public async Task<int> GetSpotifyLibraryCountAsync(SpotifyLibraryItemType itemType, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM spotify_library WHERE item_type = $item_type;";
        cmd.Parameters.AddWithValue("$item_type", (int)itemType);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Clears Spotify library items.
    /// </summary>
    public async Task ClearSpotifyLibraryAsync(SpotifyLibraryItemType? itemType = null, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            if (itemType.HasValue)
            {
                cmd.CommandText = "DELETE FROM spotify_library WHERE item_type = $item_type;";
                cmd.Parameters.AddWithValue("$item_type", (int)itemType.Value);
            }
            else
            {
                cmd.CommandText = "DELETE FROM spotify_library;";
            }

            var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger?.LogDebug("Cleared {Count} items from Spotify library", deleted);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Sync State Operations

    /// <summary>
    /// Gets the sync state for a collection type.
    /// </summary>
    public async Task<SyncStateEntry?> GetSyncStateAsync(string collectionType, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT collection_type, revision, last_sync_at, item_count FROM sync_state WHERE collection_type = $collection_type;";
        cmd.Parameters.AddWithValue("$collection_type", collectionType);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new SyncStateEntry(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                reader.GetInt32(3));
        }

        return null;
    }

    /// <summary>
    /// Updates the sync state for a collection type.
    /// </summary>
    public async Task SetSyncStateAsync(
        string collectionType,
        string? revision,
        int itemCount,
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
                INSERT INTO sync_state (collection_type, revision, last_sync_at, item_count)
                VALUES ($collection_type, $revision, $last_sync_at, $item_count)
                ON CONFLICT(collection_type) DO UPDATE SET
                    revision = excluded.revision,
                    last_sync_at = excluded.last_sync_at,
                    item_count = excluded.item_count;
                """;

            cmd.Parameters.AddWithValue("$collection_type", collectionType);
            cmd.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$last_sync_at", now);
            cmd.Parameters.AddWithValue("$item_count", itemCount);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogDebug("Updated sync state for {Collection}: revision={Revision}, count={Count}",
                collectionType, revision, itemCount);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Play History Operations

    /// <summary>
    /// Records a play event.
    /// </summary>
    public async Task RecordPlayAsync(
        string itemUri,
        int durationPlayedMs,
        bool completed,
        string? sourceContext = null,
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
                INSERT INTO play_history (item_uri, played_at, duration_played_ms, completed, source_context)
                VALUES ($item_uri, $played_at, $duration_played_ms, $completed, $source_context);
                """;

            cmd.Parameters.AddWithValue("$item_uri", itemUri);
            cmd.Parameters.AddWithValue("$played_at", now);
            cmd.Parameters.AddWithValue("$duration_played_ms", durationPlayedMs);
            cmd.Parameters.AddWithValue("$completed", completed ? 1 : 0);
            cmd.Parameters.AddWithValue("$source_context", (object?)sourceContext ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Recorded play for {Uri}, duration={Duration}ms, completed={Completed}",
                itemUri, durationPlayedMs, completed);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets play history with entity metadata.
    /// </summary>
    public async Task<List<PlayHistoryEntry>> GetPlayHistoryAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PlayHistoryEntry>();

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // First get play history entries
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ph.id, ph.item_uri, ph.played_at, ph.duration_played_ms, ph.completed, ph.source_context
            FROM play_history ph
            ORDER BY ph.played_at DESC
            LIMIT $limit OFFSET $offset;
            """;

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var entries = new List<(long Id, string ItemUri, long PlayedAt, int Duration, bool Completed, string? Context)>();

        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4) == 1,
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        if (entries.Count == 0)
            return results;

        // Fetch entities for all URIs in one query
        var uris = entries.Select(e => e.ItemUri).Distinct().ToList();
        var entities = await GetEntitiesAsync(uris, cancellationToken);
        var entityLookup = entities.ToDictionary(e => e.Uri);

        // Build results
        foreach (var entry in entries)
        {
            entityLookup.TryGetValue(entry.ItemUri, out var entity);
            results.Add(new PlayHistoryEntry(
                entry.Id,
                entry.ItemUri,
                DateTimeOffset.FromUnixTimeSeconds(entry.PlayedAt),
                entry.Duration,
                entry.Completed,
                entry.Context,
                entity));
        }

        return results;
    }

    /// <summary>
    /// Gets play count for an item.
    /// </summary>
    public async Task<int> GetPlayCountAsync(string itemUri, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM play_history WHERE item_uri = $item_uri;";
        cmd.Parameters.AddWithValue("$item_uri", itemUri);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Gets total play time for an item.
    /// </summary>
    public async Task<long> GetTotalPlayTimeAsync(string itemUri, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(duration_played_ms), 0) FROM play_history WHERE item_uri = $item_uri;";
        cmd.Parameters.AddWithValue("$item_uri", itemUri);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    #endregion

    #region Spotify Playlist Operations

    /// <summary>
    /// Upserts a playlist (inserts or updates if exists).
    /// </summary>
    public async Task UpsertPlaylistAsync(SpotifyPlaylist playlist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO spotify_playlists (id, name, owner_id, description, image_url, track_count, is_public, is_collaborative, is_owned, synced_at, revision, folder_path, is_from_rootlist)
                VALUES ($id, $name, $owner_id, $description, $image_url, $track_count, $is_public, $is_collaborative, $is_owned, $synced_at, $revision, $folder_path, $is_from_rootlist)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    owner_id = excluded.owner_id,
                    description = excluded.description,
                    image_url = excluded.image_url,
                    track_count = excluded.track_count,
                    is_public = excluded.is_public,
                    is_collaborative = excluded.is_collaborative,
                    is_owned = excluded.is_owned,
                    synced_at = excluded.synced_at,
                    revision = excluded.revision,
                    folder_path = excluded.folder_path,
                    is_from_rootlist = excluded.is_from_rootlist;
                """;

            cmd.Parameters.AddWithValue("$id", playlist.Uri);
            cmd.Parameters.AddWithValue("$name", playlist.Name);
            cmd.Parameters.AddWithValue("$owner_id", (object?)playlist.OwnerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)playlist.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$image_url", (object?)playlist.ImageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$track_count", playlist.TrackCount);
            cmd.Parameters.AddWithValue("$is_public", playlist.IsPublic ? 1 : 0);
            cmd.Parameters.AddWithValue("$is_collaborative", playlist.IsCollaborative ? 1 : 0);
            cmd.Parameters.AddWithValue("$is_owned", playlist.IsOwned ? 1 : 0);
            cmd.Parameters.AddWithValue("$synced_at", playlist.SyncedAt);
            cmd.Parameters.AddWithValue("$revision", (object?)playlist.Revision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$folder_path", (object?)playlist.FolderPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$is_from_rootlist", playlist.IsFromRootlist ? 1 : 0);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Upserted playlist: {Uri} - {Name} (folder: {Folder})",
                playlist.Uri, playlist.Name, playlist.FolderPath ?? "(root)");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets a playlist by URI.
    /// </summary>
    public async Task<SpotifyPlaylist?> GetPlaylistAsync(string playlistUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, owner_id, description, image_url, track_count, is_public, is_collaborative, is_owned, synced_at, revision, folder_path, is_from_rootlist
            FROM spotify_playlists
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", playlistUri);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadPlaylist(reader);
        }

        return null;
    }

    /// <summary>
    /// Gets all playlists.
    /// </summary>
    public async Task<List<SpotifyPlaylist>> GetAllPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<SpotifyPlaylist>();

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, owner_id, description, image_url, track_count, is_public, is_collaborative, is_owned, synced_at, revision, folder_path, is_from_rootlist
            FROM spotify_playlists
            ORDER BY folder_path, name COLLATE NOCASE;
            """;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadPlaylist(reader));
        }

        return results;
    }

    /// <summary>
    /// Deletes a playlist by URI.
    /// </summary>
    public async Task DeletePlaylistAsync(string playlistUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM spotify_playlists WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", playlistUri);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogTrace("Deleted playlist: {Uri}", playlistUri);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Clears all playlists from the database.
    /// </summary>
    public async Task ClearAllPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM spotify_playlists;";

            var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger?.LogDebug("Cleared {Count} playlists from database", deleted);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static SpotifyPlaylist ReadPlaylist(SqliteDataReader reader)
    {
        return new SpotifyPlaylist
        {
            Uri = reader.GetString(0),
            Name = reader.GetString(1),
            OwnerId = reader.IsDBNull(2) ? null : reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            ImageUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
            TrackCount = reader.GetInt32(5),
            IsPublic = reader.GetInt32(6) == 1,
            IsCollaborative = reader.GetInt32(7) == 1,
            IsOwned = reader.GetInt32(8) == 1,
            SyncedAt = reader.GetInt64(9),
            Revision = reader.IsDBNull(10) ? null : reader.GetString(10),
            FolderPath = reader.IsDBNull(11) ? null : reader.GetString(11),
            IsFromRootlist = reader.IsDBNull(12) || reader.GetInt32(12) == 1
        };
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
    public required SourceType SourceType { get; init; }
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
    public string? Genre { get; init; }
    public string? ImageUrl { get; init; }

    // Album-specific
    public int? TrackCount { get; init; }

    // Artist-specific
    public int? FollowerCount { get; init; }

    // Show/Podcast-specific
    public string? Publisher { get; init; }
    public int? EpisodeCount { get; init; }
    public string? Description { get; init; }

    // Local file specific
    public string? FilePath { get; init; }

    // Stream specific
    public string? StreamUrl { get; init; }

    // Cache TTL (NULL for local files = permanent)
    public DateTimeOffset? ExpiresAt { get; init; }

    // Timestamps
    public DateTimeOffset? AddedAt { get; init; }
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
