using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Library;

/// <summary>
/// SQLite-backed database for unified media library and play history.
/// </summary>
/// <remarks>
/// Tables:
/// - library_items: All playable content (Spotify, local files, streams, podcasts)
/// - play_history: Every play event for statistics and "recently played"
/// - watched_folders: Folders to scan for local files
/// - sync_state: Revision tracking for incremental sync
/// </remarks>
public sealed class LibraryDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    private const int CurrentSchemaVersion = 1;

    public LibraryDatabase(string databasePath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _logger = logger;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ConnectionString;

        InitializeSchema();
        _logger?.LogInformation("LibraryDatabase initialized at {Path}", databasePath);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    #region Schema

    private void InitializeSchema()
    {
        using var connection = CreateConnection();
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

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
        return Convert.ToInt32(cmd.ExecuteScalar());
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
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;

            // Unified library items
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS library_items (
                    id TEXT PRIMARY KEY,
                    source_type INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    artist TEXT,
                    album TEXT,
                    duration_ms INTEGER DEFAULT 0,
                    year INTEGER,
                    genre TEXT,
                    image_url TEXT,
                    file_path TEXT,
                    stream_url TEXT,
                    added_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL,
                    metadata_json TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_library_source ON library_items(source_type);
                CREATE INDEX IF NOT EXISTS idx_library_title ON library_items(title COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_library_artist ON library_items(artist COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_library_album ON library_items(album COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_library_added ON library_items(added_at DESC);
                """;
            cmd.ExecuteNonQuery();

            // Play history
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS play_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    item_id TEXT NOT NULL,
                    played_at INTEGER NOT NULL,
                    duration_played_ms INTEGER DEFAULT 0,
                    completed INTEGER DEFAULT 0,
                    source_context TEXT,
                    FOREIGN KEY (item_id) REFERENCES library_items(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_history_item ON play_history(item_id);
                CREATE INDEX IF NOT EXISTS idx_history_played ON play_history(played_at DESC);
                """;
            cmd.ExecuteNonQuery();

            // Watched folders for local files
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS watched_folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT UNIQUE NOT NULL,
                    last_scan_at INTEGER,
                    file_count INTEGER DEFAULT 0,
                    enabled INTEGER DEFAULT 1
                );
                """;
            cmd.ExecuteNonQuery();

            // Sync state for Spotify library sync
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sync_state (
                    collection_type TEXT PRIMARY KEY,
                    revision TEXT,
                    last_sync_at INTEGER,
                    item_count INTEGER DEFAULT 0
                );
                """;
            cmd.ExecuteNonQuery();

            // Spotify library (items in user's Spotify library)
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS spotify_library (
                    item_id TEXT PRIMARY KEY,
                    item_type INTEGER NOT NULL,
                    added_at INTEGER NOT NULL,
                    synced_at INTEGER NOT NULL,
                    FOREIGN KEY (item_id) REFERENCES library_items(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_spotify_type ON spotify_library(item_type);
                CREATE INDEX IF NOT EXISTS idx_spotify_added ON spotify_library(added_at DESC);
                """;
            cmd.ExecuteNonQuery();

            // Spotify playlists (user's playlists - owned and followed)
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS spotify_playlists (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    owner_id TEXT,
                    owner_name TEXT,
                    description TEXT,
                    image_url TEXT,
                    track_count INTEGER DEFAULT 0,
                    is_public INTEGER DEFAULT 0,
                    is_collaborative INTEGER DEFAULT 0,
                    is_owned INTEGER DEFAULT 0,
                    synced_at INTEGER NOT NULL,
                    revision TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_playlists_owned ON spotify_playlists(is_owned);
                """;
            cmd.ExecuteNonQuery();

            transaction.Commit();
            _logger?.LogInformation("Library database schema created (version {Version})", CurrentSchemaVersion);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    #endregion

    #region Library Items

    public async Task<LibraryItem?> GetItemAsync(string id, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT l.*,
                   COUNT(h.id) as play_count,
                   MAX(h.played_at) as last_played_at
            FROM library_items l
            LEFT JOIN play_history h ON h.item_id = l.id
            WHERE l.id = @id
            GROUP BY l.id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadLibraryItem(reader);
        }
        return null;
    }

    public async Task<LibraryItem> UpsertItemAsync(LibraryItem item, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO library_items (id, source_type, title, artist, album, duration_ms, year, genre, image_url, file_path, stream_url, added_at, updated_at, metadata_json)
                VALUES (@id, @source_type, @title, @artist, @album, @duration_ms, @year, @genre, @image_url, @file_path, @stream_url, @added_at, @updated_at, @metadata_json)
                ON CONFLICT(id) DO UPDATE SET
                    title = COALESCE(@title, title),
                    artist = COALESCE(@artist, artist),
                    album = COALESCE(@album, album),
                    duration_ms = COALESCE(@duration_ms, duration_ms),
                    year = COALESCE(@year, year),
                    genre = COALESCE(@genre, genre),
                    image_url = COALESCE(@image_url, image_url),
                    file_path = COALESCE(@file_path, file_path),
                    stream_url = COALESCE(@stream_url, stream_url),
                    updated_at = @updated_at,
                    metadata_json = COALESCE(@metadata_json, metadata_json)
                """;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.Parameters.AddWithValue("@id", item.Id);
            cmd.Parameters.AddWithValue("@source_type", (int)item.SourceType);
            cmd.Parameters.AddWithValue("@title", item.Title);
            cmd.Parameters.AddWithValue("@artist", (object?)item.Artist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@album", (object?)item.Album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@duration_ms", item.DurationMs);
            cmd.Parameters.AddWithValue("@year", (object?)item.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@genre", (object?)item.Genre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@image_url", (object?)item.ImageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@file_path", (object?)item.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@stream_url", (object?)item.StreamUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@added_at", item.AddedAt > 0 ? item.AddedAt : now);
            cmd.Parameters.AddWithValue("@updated_at", now);
            cmd.Parameters.AddWithValue("@metadata_json", (object?)item.MetadataJson ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);

            return item with { UpdatedAt = now };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<LibraryItem>> SearchAsync(LibrarySearchQuery query, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = new StringBuilder();
        sql.AppendLine("""
            SELECT l.*,
                   COUNT(h.id) as play_count,
                   MAX(h.played_at) as last_played_at
            FROM library_items l
            LEFT JOIN play_history h ON h.item_id = l.id
            WHERE 1=1
            """);

        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrEmpty(query.SearchText))
        {
            sql.AppendLine("AND (l.title LIKE @search OR l.artist LIKE @search OR l.album LIKE @search)");
            parameters.Add(new SqliteParameter("@search", $"%{query.SearchText}%"));
        }

        if (query.SourceType.HasValue)
        {
            sql.AppendLine("AND l.source_type = @source_type");
            parameters.Add(new SqliteParameter("@source_type", (int)query.SourceType.Value));
        }

        if (!string.IsNullOrEmpty(query.Artist))
        {
            sql.AppendLine("AND l.artist LIKE @artist");
            parameters.Add(new SqliteParameter("@artist", $"%{query.Artist}%"));
        }

        if (!string.IsNullOrEmpty(query.Album))
        {
            sql.AppendLine("AND l.album LIKE @album");
            parameters.Add(new SqliteParameter("@album", $"%{query.Album}%"));
        }

        if (!string.IsNullOrEmpty(query.Genre))
        {
            sql.AppendLine("AND l.genre LIKE @genre");
            parameters.Add(new SqliteParameter("@genre", $"%{query.Genre}%"));
        }

        if (query.MinDurationMs.HasValue)
        {
            sql.AppendLine("AND l.duration_ms >= @min_duration");
            parameters.Add(new SqliteParameter("@min_duration", query.MinDurationMs.Value));
        }

        if (query.MaxDurationMs.HasValue)
        {
            sql.AppendLine("AND l.duration_ms <= @max_duration");
            parameters.Add(new SqliteParameter("@max_duration", query.MaxDurationMs.Value));
        }

        if (query.MinYear.HasValue)
        {
            sql.AppendLine("AND l.year >= @min_year");
            parameters.Add(new SqliteParameter("@min_year", query.MinYear.Value));
        }

        if (query.MaxYear.HasValue)
        {
            sql.AppendLine("AND l.year <= @max_year");
            parameters.Add(new SqliteParameter("@max_year", query.MaxYear.Value));
        }

        sql.AppendLine("GROUP BY l.id");

        // Order by
        sql.Append("ORDER BY ");
        sql.AppendLine(query.SortOrder switch
        {
            LibrarySortOrder.RecentlyAdded => "l.added_at DESC",
            LibrarySortOrder.RecentlyPlayed => "last_played_at DESC NULLS LAST",
            LibrarySortOrder.MostPlayed => "play_count DESC",
            LibrarySortOrder.Title => "l.title COLLATE NOCASE ASC",
            LibrarySortOrder.Artist => "l.artist COLLATE NOCASE ASC",
            LibrarySortOrder.Album => "l.album COLLATE NOCASE ASC",
            LibrarySortOrder.Duration => "l.duration_ms ASC",
            LibrarySortOrder.Year => "l.year DESC NULLS LAST",
            _ => "l.added_at DESC"
        });

        sql.AppendLine("LIMIT @limit OFFSET @offset");
        parameters.Add(new SqliteParameter("@limit", query.Limit));
        parameters.Add(new SqliteParameter("@offset", query.Offset));

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddRange(parameters.ToArray());

        var results = new List<LibraryItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadLibraryItem(reader));
        }
        return results;
    }

    public async Task<bool> DeleteItemAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM library_items WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);

            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static LibraryItem ReadLibraryItem(SqliteDataReader reader)
    {
        return new LibraryItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            SourceType = (SourceType)reader.GetInt32(reader.GetOrdinal("source_type")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Artist = reader.IsDBNull(reader.GetOrdinal("artist")) ? null : reader.GetString(reader.GetOrdinal("artist")),
            Album = reader.IsDBNull(reader.GetOrdinal("album")) ? null : reader.GetString(reader.GetOrdinal("album")),
            DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
            Year = reader.IsDBNull(reader.GetOrdinal("year")) ? null : reader.GetInt32(reader.GetOrdinal("year")),
            Genre = reader.IsDBNull(reader.GetOrdinal("genre")) ? null : reader.GetString(reader.GetOrdinal("genre")),
            ImageUrl = reader.IsDBNull(reader.GetOrdinal("image_url")) ? null : reader.GetString(reader.GetOrdinal("image_url")),
            FilePath = reader.IsDBNull(reader.GetOrdinal("file_path")) ? null : reader.GetString(reader.GetOrdinal("file_path")),
            StreamUrl = reader.IsDBNull(reader.GetOrdinal("stream_url")) ? null : reader.GetString(reader.GetOrdinal("stream_url")),
            AddedAt = reader.GetInt64(reader.GetOrdinal("added_at")),
            UpdatedAt = reader.GetInt64(reader.GetOrdinal("updated_at")),
            MetadataJson = reader.IsDBNull(reader.GetOrdinal("metadata_json")) ? null : reader.GetString(reader.GetOrdinal("metadata_json")),
            PlayCount = reader.GetInt32(reader.GetOrdinal("play_count")),
            LastPlayedAt = reader.IsDBNull(reader.GetOrdinal("last_played_at")) ? null : reader.GetInt64(reader.GetOrdinal("last_played_at"))
        };
    }

    #endregion

    #region Play History

    public async Task RecordPlayAsync(string itemId, long durationPlayedMs, bool completed, string? sourceContext = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO play_history (item_id, played_at, duration_played_ms, completed, source_context)
                VALUES (@item_id, @played_at, @duration_played_ms, @completed, @source_context)
                """;
            cmd.Parameters.AddWithValue("@item_id", itemId);
            cmd.Parameters.AddWithValue("@played_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@duration_played_ms", durationPlayedMs);
            cmd.Parameters.AddWithValue("@completed", completed ? 1 : 0);
            cmd.Parameters.AddWithValue("@source_context", (object?)sourceContext ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<PlayHistoryEntry>> GetRecentPlaysAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM play_history
            ORDER BY played_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<PlayHistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PlayHistoryEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ItemId = reader.GetString(reader.GetOrdinal("item_id")),
                PlayedAt = reader.GetInt64(reader.GetOrdinal("played_at")),
                DurationPlayedMs = reader.GetInt64(reader.GetOrdinal("duration_played_ms")),
                Completed = reader.GetInt32(reader.GetOrdinal("completed")) == 1,
                SourceContext = reader.IsDBNull(reader.GetOrdinal("source_context")) ? null : reader.GetString(reader.GetOrdinal("source_context"))
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<LibraryItem>> GetRecentlyPlayedItemsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT l.*,
                   COUNT(h.id) as play_count,
                   MAX(h.played_at) as last_played_at
            FROM library_items l
            INNER JOIN play_history h ON h.item_id = l.id
            GROUP BY l.id
            ORDER BY last_played_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<LibraryItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadLibraryItem(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<LibraryItem>> GetMostPlayedItemsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT l.*,
                   COUNT(h.id) as play_count,
                   MAX(h.played_at) as last_played_at
            FROM library_items l
            INNER JOIN play_history h ON h.item_id = l.id
            GROUP BY l.id
            ORDER BY play_count DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<LibraryItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadLibraryItem(reader));
        }
        return results;
    }

    #endregion

    #region Statistics

    public async Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN source_type = 0 THEN 1 ELSE 0 END) as spotify,
                SUM(CASE WHEN source_type = 1 THEN 1 ELSE 0 END) as local,
                SUM(CASE WHEN source_type = 2 THEN 1 ELSE 0 END) as streams,
                SUM(CASE WHEN source_type = 3 THEN 1 ELSE 0 END) as podcasts
            FROM library_items
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var totalItems = reader.GetInt32(0);
        var spotify = reader.GetInt32(1);
        var local = reader.GetInt32(2);
        var streams = reader.GetInt32(3);
        var podcasts = reader.GetInt32(4);

        await reader.CloseAsync();

        cmd.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(duration_played_ms), 0)
            FROM play_history
            """;
        await using var reader2 = await cmd.ExecuteReaderAsync(ct);
        await reader2.ReadAsync(ct);

        var totalPlays = reader2.GetInt32(0);
        var totalListeningTime = reader2.GetInt64(1);

        return new LibraryStats
        {
            TotalItems = totalItems,
            SpotifyTracks = spotify,
            LocalFiles = local,
            Streams = streams,
            PodcastEpisodes = podcasts,
            TotalPlays = totalPlays,
            TotalListeningTimeMs = totalListeningTime
        };
    }

    #endregion

    #region Sync State

    public async Task<string?> GetSyncRevisionAsync(string collectionType, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT revision FROM sync_state WHERE collection_type = @type";
        cmd.Parameters.AddWithValue("@type", collectionType);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SetSyncRevisionAsync(string collectionType, string? revision, int itemCount = 0, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_state (collection_type, revision, last_sync_at, item_count)
                VALUES (@type, @revision, @last_sync, @count)
                ON CONFLICT(collection_type) DO UPDATE SET
                    revision = @revision,
                    last_sync_at = @last_sync,
                    item_count = @count
                """;
            cmd.Parameters.AddWithValue("@type", collectionType);
            cmd.Parameters.AddWithValue("@revision", (object?)revision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@last_sync", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@count", itemCount);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}
