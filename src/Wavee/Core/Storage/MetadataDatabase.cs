using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Playlists;
using Wavee.Core.Storage.Abstractions;
using Wavee.Core.Storage.Entities;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Core.Storage;

/// <summary>
/// SQLite-backed metadata database for caching entity metadata and extension data.
/// Thread-safe with connection pooling and in-memory LRU cache for hot data.
/// </summary>
public sealed class MetadataDatabase : IMetadataDatabase
{
    private const string EntityColumns = """
        uri,
        source_type,
        entity_type,
        title,
        artist_name,
        album_name,
        album_uri,
        duration_ms,
        track_number,
        disc_number,
        release_year,
        image_url,
        genre,
        track_count,
        follower_count,
        publisher,
        episode_count,
        description,
        file_path,
        stream_url,
        expires_at,
        added_at,
        created_at,
        updated_at
        """;

    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedExtensionEntry> _hotCache = new();
    private readonly int _maxHotCacheSize;
    private readonly string _spotifyMetadataLocale;
    // Active write-batch scope for the current async-flow. When set, write
    // methods (UpsertEntityAsync, SetExtensionsBulkAsync, RefreshExtensionTtlBulkAsync)
    // share the scope's open connection + transaction instead of self-locking.
    private readonly AsyncLocal<WriteBatchScope?> _activeBatch = new();
    private bool _disposed;

    // Schema version for migrations - bump to force fresh start
    // v4: Added is_from_rootlist column to spotify_playlists
    // v5: Added color_cache table
    // v6: Added album_tracks_cache table
    // v7: Added library_outbox table
    // v8: Removed FK constraint from spotify_library (decoupled from entities)
    // v9: Added media_overrides table
    // v11: Added playlist/rootlist cache snapshots
    // v12: Added header_image_url + format_attributes_json on spotify_playlists
    // v13: Added available_signals_json on spotify_playlists
    // v14: Added cache_schema_version per playlist row
    // v15: Local-file library — local_files, local_artwork[+_links], playlist_overlay_items;
    //      entities.is_locally_liked; watched_folders gains include_subfolders + scan-status cols.
    // v16: local_files.is_video flag — flips routing from AudioHost (BASS) to UI MediaPlayer
    //      for files whose extension is in the video set (.mp4 .mov .m4v .mkv .webm).
    private const int CurrentSchemaVersion = 16;

    /// <summary>
    /// Creates a new MetadataDatabase.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="maxHotCacheSize">Maximum entries in the in-memory hot cache.</param>
    /// <param name="spotifyMetadataLocale">Optional 2-character Spotify metadata locale scope.</param>
    /// <param name="logger">Optional logger.</param>
    public MetadataDatabase(string databasePath, int maxHotCacheSize = 1000, string? spotifyMetadataLocale = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _maxHotCacheSize = maxHotCacheSize;
        _spotifyMetadataLocale = NormalizeSpotifyLocale(spotifyMetadataLocale);
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

    /// <summary>
    /// Incremental, non-destructive schema migrations. Each entry is run
    /// exactly once when an existing DB is opened at <c>FromVersion</c>.
    /// ALL migrations must be additive-only (ALTER TABLE ADD COLUMN,
    /// CREATE TABLE IF NOT EXISTS, CREATE INDEX IF NOT EXISTS). Never DROP
    /// or restructure — users lose zero cached data on upgrade.
    /// </summary>
    private static readonly IReadOnlyList<SchemaMigration> Migrations =
    [
        new SchemaMigration(
            FromVersion: 11,
            ToVersion: 12,
            Sql: """
                ALTER TABLE spotify_playlists ADD COLUMN header_image_url TEXT;
                ALTER TABLE spotify_playlists ADD COLUMN format_attributes_json TEXT;
                """),
        new SchemaMigration(
            FromVersion: 12,
            ToVersion: 13,
            Sql: """
                ALTER TABLE spotify_playlists ADD COLUMN available_signals_json TEXT;
                """),

        // v14: per-row cache schema version. Bumped via
        // PlaylistCacheService.CurrentCacheSchemaVersion whenever any of the
        // JSON blobs persisted on the playlist row (CapabilitiesJson,
        // OrderedItemsJson, FormatAttributesJson, AvailableSignalsJson) change
        // shape. The cache layer treats a row whose stored version is below
        // current as cache-miss, forcing a fresh fetch. Existing rows default
        // to 0, so the first version bump after upgrade re-fetches everything
        // exactly once.
        new SchemaMigration(
            FromVersion: 13,
            ToVersion: 14,
            Sql: """
                ALTER TABLE spotify_playlists ADD COLUMN cache_schema_version INTEGER NOT NULL DEFAULT 0;
                """),

        // v15: First-class local file library. Adds the indexer's per-file fingerprint
        // table, deduped artwork cache, playlist overlay rows, a local-likes column on
        // entities, and richer scan status on watched_folders.
        new SchemaMigration(
            FromVersion: 14,
            ToVersion: 15,
            Sql: """
                ALTER TABLE entities ADD COLUMN is_locally_liked INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE watched_folders ADD COLUMN include_subfolders INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE watched_folders ADD COLUMN last_scan_status TEXT;
                ALTER TABLE watched_folders ADD COLUMN last_scan_error TEXT;
                ALTER TABLE watched_folders ADD COLUMN last_scan_duration_ms INTEGER;

                CREATE TABLE IF NOT EXISTS local_files (
                    path                TEXT PRIMARY KEY NOT NULL,
                    folder_id           INTEGER NOT NULL,
                    track_uri           TEXT NOT NULL,
                    file_size           INTEGER NOT NULL,
                    file_mtime_ticks    INTEGER NOT NULL,
                    content_hash        TEXT NOT NULL,
                    last_indexed_at     INTEGER NOT NULL,
                    last_seen_at        INTEGER NOT NULL,
                    scan_error          TEXT,
                    FOREIGN KEY (folder_id) REFERENCES watched_folders(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_local_files_track_uri ON local_files(track_uri);
                CREATE INDEX IF NOT EXISTS idx_local_files_folder    ON local_files(folder_id);

                CREATE TABLE IF NOT EXISTS local_artwork (
                    image_hash    TEXT PRIMARY KEY NOT NULL,
                    cached_path   TEXT NOT NULL,
                    mime_type     TEXT,
                    width         INTEGER,
                    height        INTEGER,
                    created_at    INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS local_artwork_links (
                    entity_uri  TEXT NOT NULL,
                    role        TEXT NOT NULL,
                    image_hash  TEXT NOT NULL,
                    PRIMARY KEY (entity_uri, role),
                    FOREIGN KEY (image_hash) REFERENCES local_artwork(image_hash) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS playlist_overlay_items (
                    playlist_uri    TEXT NOT NULL,
                    item_uri        TEXT NOT NULL,
                    position        INTEGER NOT NULL,
                    added_at        INTEGER NOT NULL,
                    added_by        TEXT,
                    PRIMARY KEY (playlist_uri, item_uri, added_at)
                );
                CREATE INDEX IF NOT EXISTS idx_playlist_overlay_pl ON playlist_overlay_items(playlist_uri);
                """),

        // v16: local_files.is_video — drives PlaybackOrchestrator dispatch
        // (video files take the UI-process MediaPlayer path; audio stays on
        // AudioHost). Default 0 so existing rows stay audio; the next scan
        // re-evaluates each file by extension.
        new SchemaMigration(
            FromVersion: 15,
            ToVersion: 16,
            Sql: """
                ALTER TABLE local_files ADD COLUMN is_video INTEGER NOT NULL DEFAULT 0;
                """)
    ];

    private sealed record SchemaMigration(int FromVersion, int ToVersion, string Sql);

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

        var currentVersion = GetSchemaVersion(connection);

        if (currentVersion == 0)
        {
            // Fresh DB — create all tables at the latest schema.
            CreateTables(connection);
            SetSchemaVersion(connection, CurrentSchemaVersion);
            _logger?.LogInformation("Created fresh metadata DB at schema v{Version}", CurrentSchemaVersion);
        }
        else if (currentVersion < CurrentSchemaVersion)
        {
            try
            {
                RunMigrations(connection, fromVersion: currentVersion);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Metadata DB migration failed at v{From} → v{To}",
                    currentVersion, CurrentSchemaVersion);
                throw new MetadataMigrationException(
                    $"Failed migrating metadata DB from v{currentVersion} to v{CurrentSchemaVersion}: {ex.Message}",
                    currentVersion, CurrentSchemaVersion, ex)
                {
                    Reason = MetadataMigrationFailureReason.MigrationFailed
                };
            }
        }
        else if (currentVersion > CurrentSchemaVersion)
        {
            // DB was written by a newer Wavee version. We don't know what
            // those future migrations did, so we can't safely use it.
            _logger?.LogError(
                "Metadata DB is at v{Actual}, this build supports up to v{Supported}",
                currentVersion, CurrentSchemaVersion);
            throw new MetadataMigrationException(
                $"Database is at v{currentVersion}, but this build of Wavee supports up to v{CurrentSchemaVersion}. A newer Wavee version created this database.",
                currentVersion, CurrentSchemaVersion)
            {
                Reason = MetadataMigrationFailureReason.Downgrade
            };
        }
        // else: currentVersion == CurrentSchemaVersion, nothing to do.

        EnsureLocalizedMetadataTables(connection);
        EnsureAudioBlobTables(connection);
    }

    /// <summary>
    /// Applies all registered migrations whose <c>FromVersion &gt;= fromVersion</c>,
    /// in ascending order. Each step runs inside its own transaction and commits
    /// the new <c>user_version</c> atomically — a mid-run crash leaves the DB
    /// at the last successfully-committed intermediate version, so the next
    /// open resumes from there.
    /// </summary>
    private void RunMigrations(SqliteConnection connection, int fromVersion)
    {
        foreach (var step in Migrations
                     .Where(m => m.FromVersion >= fromVersion)
                     .OrderBy(m => m.FromVersion))
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = step.Sql;
                cmd.ExecuteNonQuery();
            }
            SetSchemaVersion(connection, step.ToVersion, tx);
            tx.Commit();
            _logger?.LogInformation(
                "Applied metadata DB migration v{From} → v{To}",
                step.FromVersion, step.ToVersion);
        }
    }

    /// <summary>
    /// Deletes the metadata DB file (and its WAL/SHM sidecars) from disk.
    /// Used by the startup gate when the user opts to rebuild the cache
    /// after a migration failure. Caller is responsible for disposing any
    /// open connections before calling this.
    /// </summary>
    public static void DeleteDatabaseFile(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // In WAL mode SQLite keeps two sidecar files alongside the main DB —
        // deleting only the .db leaves orphans that corrupt the next open.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (IOException) { /* best-effort; will surface again on next open */ }
            }
        }
    }

    /// <summary>
    /// Idempotently create the audio_keys and head_data tables on every DB open.
    /// These were added after the initial schema shipped, so DBs at the current
    /// schema version won't have them unless we create them additively — same
    /// pattern as <see cref="EnsureLocalizedMetadataTables"/>.
    /// </summary>
    private static void EnsureAudioBlobTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS audio_keys (
                file_id    TEXT PRIMARY KEY NOT NULL,
                track_uri  TEXT,
                key_bytes  BLOB NOT NULL,
                cached_at  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS head_data (
                file_id    TEXT PRIMARY KEY NOT NULL,
                data       BLOB NOT NULL,
                cached_at  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS playplay_obfuscated_keys (
                file_id        TEXT PRIMARY KEY NOT NULL,
                obf_key_bytes  BLOB NOT NULL,
                cached_at      INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void DropAllTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS extension_cache;
            DROP TABLE IF EXISTS entities;
            DROP TABLE IF EXISTS localized_extension_cache;
            DROP TABLE IF EXISTS localized_entities;
            DROP TABLE IF EXISTS spotify_library;
            DROP TABLE IF EXISTS play_history;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS watched_folders;
            DROP TABLE IF EXISTS podcast_shows;
            DROP TABLE IF EXISTS podcast_episodes;
            DROP TABLE IF EXISTS episode_progress;
            DROP TABLE IF EXISTS spotify_playlists;
            DROP TABLE IF EXISTS rootlist_cache;
            DROP TABLE IF EXISTS color_cache;
            DROP TABLE IF EXISTS album_tracks_cache;
            DROP TABLE IF EXISTS lyrics_cache;
            DROP TABLE IF EXISTS library_outbox;
            DROP TABLE IF EXISTS media_overrides;
            DROP TABLE IF EXISTS local_files;
            DROP TABLE IF EXISTS local_artwork;
            DROP TABLE IF EXISTS local_artwork_links;
            DROP TABLE IF EXISTS playlist_overlay_items;
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

    private static void SetSchemaVersion(SqliteConnection connection, int version, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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

                        -- Local-file likes (Spotify likes live in spotify_library)
                        is_locally_liked INTEGER NOT NULL DEFAULT 0,

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
                        synced_at   INTEGER NOT NULL
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
                        id                       INTEGER PRIMARY KEY AUTOINCREMENT,
                        path                     TEXT NOT NULL UNIQUE,
                        last_scan_at             INTEGER,
                        file_count               INTEGER NOT NULL DEFAULT 0,
                        enabled                  INTEGER NOT NULL DEFAULT 1,
                        include_subfolders       INTEGER NOT NULL DEFAULT 1,
                        last_scan_status         TEXT,
                        last_scan_error          TEXT,
                        last_scan_duration_ms    INTEGER
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Local file library (v15) — per-file fingerprint, artwork cache, playlist overlays.
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS local_files (
                        path                TEXT PRIMARY KEY NOT NULL,
                        folder_id           INTEGER NOT NULL,
                        track_uri           TEXT NOT NULL,
                        file_size           INTEGER NOT NULL,
                        file_mtime_ticks    INTEGER NOT NULL,
                        content_hash        TEXT NOT NULL,
                        last_indexed_at     INTEGER NOT NULL,
                        last_seen_at        INTEGER NOT NULL,
                        scan_error          TEXT,
                        is_video            INTEGER NOT NULL DEFAULT 0,
                        FOREIGN KEY (folder_id) REFERENCES watched_folders(id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS idx_local_files_track_uri ON local_files(track_uri);
                    CREATE INDEX IF NOT EXISTS idx_local_files_folder    ON local_files(folder_id);

                    CREATE TABLE IF NOT EXISTS local_artwork (
                        image_hash    TEXT PRIMARY KEY NOT NULL,
                        cached_path   TEXT NOT NULL,
                        mime_type     TEXT,
                        width         INTEGER,
                        height        INTEGER,
                        created_at    INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS local_artwork_links (
                        entity_uri  TEXT NOT NULL,
                        role        TEXT NOT NULL,
                        image_hash  TEXT NOT NULL,
                        PRIMARY KEY (entity_uri, role),
                        FOREIGN KEY (image_hash) REFERENCES local_artwork(image_hash) ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS playlist_overlay_items (
                        playlist_uri    TEXT NOT NULL,
                        item_uri        TEXT NOT NULL,
                        position        INTEGER NOT NULL,
                        added_at        INTEGER NOT NULL,
                        added_by        TEXT,
                        PRIMARY KEY (playlist_uri, item_uri, added_at)
                    );
                    CREATE INDEX IF NOT EXISTS idx_playlist_overlay_pl ON playlist_overlay_items(playlist_uri);
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
                        owner_name          TEXT,
                        description         TEXT,
                        image_url           TEXT,
                        header_image_url    TEXT,
                        track_count         INTEGER NOT NULL DEFAULT 0,
                        is_public           INTEGER NOT NULL DEFAULT 1,
                        is_collaborative    INTEGER NOT NULL DEFAULT 0,
                        is_owned            INTEGER NOT NULL DEFAULT 0,
                        synced_at           INTEGER NOT NULL,
                        revision            TEXT,
                        cache_revision      BLOB,
                        folder_path         TEXT,
                        is_from_rootlist    INTEGER NOT NULL DEFAULT 1,
                        ordered_items_json  TEXT,
                        has_contents_snapshot INTEGER NOT NULL DEFAULT 0,
                        base_permission     INTEGER NOT NULL DEFAULT 0,
                        capabilities_json   TEXT,
                        format_attributes_json TEXT,
                        available_signals_json TEXT,
                        deleted_by_owner    INTEGER NOT NULL DEFAULT 0,
                        abuse_reporting_enabled INTEGER NOT NULL DEFAULT 0,
                        last_accessed_at    INTEGER,
                        cache_schema_version INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS rootlist_cache (
                        id               TEXT PRIMARY KEY NOT NULL,
                        revision         BLOB,
                        json_data        TEXT NOT NULL,
                        cached_at        INTEGER NOT NULL,
                        last_accessed_at INTEGER
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Album tracks cache table
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS album_tracks_cache (
                        album_uri   TEXT PRIMARY KEY NOT NULL,
                        json_data   TEXT NOT NULL,
                        cached_at   INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Color cache table
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS color_cache (
                        image_url   TEXT PRIMARY KEY NOT NULL,
                        dark_hex    TEXT,
                        light_hex   TEXT,
                        raw_hex     TEXT,
                        cached_at   INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Lyrics cache table
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS lyrics_cache (
                        track_uri        TEXT PRIMARY KEY,
                        provider         TEXT,
                        json_data        TEXT NOT NULL,
                        has_syllable_sync INTEGER NOT NULL DEFAULT 0,
                        cached_at        INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Library outbox (pending API sync operations)
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS library_outbox (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        item_uri    TEXT NOT NULL,
                        item_type   INTEGER NOT NULL,
                        operation   INTEGER NOT NULL,
                        created_at  INTEGER NOT NULL,
                        retry_count INTEGER NOT NULL DEFAULT 0,
                        last_error  TEXT,
                        UNIQUE(item_uri)
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // AudioKey persistence — 16-byte AES keys, keyed by FileId (hex).
            // Keys never rotate for a given file, so caching them permanently is
            // safe; this lets playback start without a round-trip to the AP after
            // a restart. trackUri is informational (nullable) — cache lookup is
            // by file_id, matching how AudioKeyManager looks it up at runtime.
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS audio_keys (
                        file_id    TEXT PRIMARY KEY NOT NULL,
                        track_uri  TEXT,
                        key_bytes  BLOB NOT NULL,
                        cached_at  INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // Head data persistence — first ~128 KB of encrypted audio used for
            // instant-start playback. File IDs never change their contents on
            // Spotify's side, so this is also safe to keep across restarts.
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS head_data (
                        file_id    TEXT PRIMARY KEY NOT NULL,
                        data       BLOB NOT NULL,
                        cached_at  INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS media_overrides (
                        asset_type                INTEGER NOT NULL,
                        entity_key                TEXT NOT NULL,
                        effective_asset_url       TEXT,
                        effective_source          INTEGER NOT NULL DEFAULT 0,
                        last_seen_upstream_url    TEXT,
                        pending_asset_url         TEXT,
                        last_reviewed_upstream_url TEXT,
                        created_at                INTEGER NOT NULL,
                        updated_at                INTEGER NOT NULL,
                        PRIMARY KEY (asset_type, entity_key)
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

    private static void EnsureLocalizedMetadataTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS localized_entities (
                uri              TEXT NOT NULL,
                locale           TEXT NOT NULL,
                source_type      INTEGER NOT NULL DEFAULT 0,
                entity_type      INTEGER NOT NULL,
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
                track_count      INTEGER,
                follower_count   INTEGER,
                publisher        TEXT,
                episode_count    INTEGER,
                description      TEXT,
                file_path        TEXT,
                stream_url       TEXT,
                expires_at       INTEGER,
                added_at         INTEGER,
                created_at       INTEGER NOT NULL,
                updated_at       INTEGER NOT NULL,
                PRIMARY KEY (uri, locale)
            );
            CREATE TABLE IF NOT EXISTS localized_extension_cache (
                entity_uri       TEXT NOT NULL,
                locale           TEXT NOT NULL,
                extension_kind   INTEGER NOT NULL,
                data             BLOB NOT NULL,
                etag             TEXT,
                expires_at       INTEGER NOT NULL,
                created_at       INTEGER NOT NULL,
                PRIMARY KEY (entity_uri, locale, extension_kind)
            );
            CREATE INDEX IF NOT EXISTS idx_localized_entities_locale ON localized_entities(locale);
            CREATE INDEX IF NOT EXISTS idx_localized_entities_type ON localized_entities(entity_type);
            CREATE INDEX IF NOT EXISTS idx_localized_entities_artist ON localized_entities(artist_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_localized_entities_album ON localized_entities(album_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_localized_entities_title ON localized_entities(title COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_localized_entities_updated ON localized_entities(updated_at);
            CREATE INDEX IF NOT EXISTS idx_localized_extension_expires ON localized_extension_cache(expires_at);
            CREATE INDEX IF NOT EXISTS idx_localized_extension_entity ON localized_extension_cache(entity_uri, locale);
            """;
        cmd.ExecuteNonQuery();
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
        var useLocalizedTable = ShouldUseLocalizedSpotifyMetadata(sourceType);

        // If we're inside a BeginWriteBatchAsync scope, share its connection
        // and transaction. Otherwise self-acquire the write lock + connection.
        var batch = _activeBatch.Value;
        if (batch is not null)
        {
            await ExecuteUpsertEntityAsync(
                batch.Connection, batch.Transaction,
                uri, entityType, sourceType, title, artistName, albumName, albumUri,
                durationMs, trackNumber, discNumber, releaseYear, imageUrl, genre,
                trackCount, followerCount, publisher, episodeCount, description,
                filePath, streamUrl, expiresAt, addedAt,
                useLocalizedTable, now, cancellationToken);
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await ExecuteUpsertEntityAsync(
                connection, null,
                uri, entityType, sourceType, title, artistName, albumName, albumUri,
                durationMs, trackNumber, discNumber, releaseYear, imageUrl, genre,
                trackCount, followerCount, publisher, episodeCount, description,
                filePath, streamUrl, expiresAt, addedAt,
                useLocalizedTable, now, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ExecuteUpsertEntityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string uri,
        EntityType entityType,
        SourceType sourceType,
        string? title,
        string? artistName,
        string? albumName,
        string? albumUri,
        int? durationMs,
        int? trackNumber,
        int? discNumber,
        int? releaseYear,
        string? imageUrl,
        string? genre,
        int? trackCount,
        int? followerCount,
        string? publisher,
        int? episodeCount,
        string? description,
        string? filePath,
        string? streamUrl,
        long? expiresAt,
        long? addedAt,
        bool useLocalizedTable,
        long now,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        if (transaction is not null) cmd.Transaction = transaction;
        cmd.CommandText = useLocalizedTable
            ? """
                INSERT INTO localized_entities (
                    uri, locale, source_type, entity_type, title, artist_name, album_name, album_uri,
                    duration_ms, track_number, disc_number, release_year, image_url, genre,
                    track_count, follower_count, publisher, episode_count, description,
                    file_path, stream_url, expires_at, added_at, created_at, updated_at
                ) VALUES (
                    $uri, $locale, $source_type, $entity_type, $title, $artist_name, $album_name, $album_uri,
                    $duration_ms, $track_number, $disc_number, $release_year, $image_url, $genre,
                    $track_count, $follower_count, $publisher, $episode_count, $description,
                    $file_path, $stream_url, $expires_at, $added_at, $now, $now
                )
                ON CONFLICT(uri, locale) DO UPDATE SET
                    source_type = excluded.source_type,
                    entity_type = excluded.entity_type,
                    title = COALESCE(excluded.title, localized_entities.title),
                    artist_name = COALESCE(excluded.artist_name, localized_entities.artist_name),
                    album_name = COALESCE(excluded.album_name, localized_entities.album_name),
                    album_uri = COALESCE(excluded.album_uri, localized_entities.album_uri),
                    duration_ms = COALESCE(excluded.duration_ms, localized_entities.duration_ms),
                    track_number = COALESCE(excluded.track_number, localized_entities.track_number),
                    disc_number = COALESCE(excluded.disc_number, localized_entities.disc_number),
                    release_year = COALESCE(excluded.release_year, localized_entities.release_year),
                    image_url = COALESCE(excluded.image_url, localized_entities.image_url),
                    genre = COALESCE(excluded.genre, localized_entities.genre),
                    track_count = COALESCE(excluded.track_count, localized_entities.track_count),
                    follower_count = COALESCE(excluded.follower_count, localized_entities.follower_count),
                    publisher = COALESCE(excluded.publisher, localized_entities.publisher),
                    episode_count = COALESCE(excluded.episode_count, localized_entities.episode_count),
                    description = COALESCE(excluded.description, localized_entities.description),
                    file_path = COALESCE(excluded.file_path, localized_entities.file_path),
                    stream_url = COALESCE(excluded.stream_url, localized_entities.stream_url),
                    expires_at = COALESCE(excluded.expires_at, localized_entities.expires_at),
                    added_at = COALESCE(excluded.added_at, localized_entities.added_at),
                    updated_at = $now;
                """
            : """
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
        if (useLocalizedTable)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
        }
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

    /// <summary>
    /// Gets an entity by URI.
    /// </summary>
    public async Task<CachedEntity?> GetEntityAsync(string uri, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? $"""
                WITH candidates AS (
                    SELECT {EntityColumns}, 0 AS priority
                    FROM localized_entities
                    WHERE uri = $uri AND locale = $locale
                    UNION ALL
                    SELECT {EntityColumns}, 1 AS priority
                    FROM entities
                    WHERE uri = $uri
                    UNION ALL
                    SELECT {EntityColumns}, 2 AS priority
                    FROM localized_entities
                    WHERE uri = $uri AND locale <> $locale
                )
                SELECT {EntityColumns}
                FROM candidates
                ORDER BY priority, updated_at DESC
                LIMIT 1;
                """
            : "SELECT * FROM entities WHERE uri = $uri;";
        cmd.Parameters.AddWithValue("$uri", uri);
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
        }

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
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? $"""
                WITH candidates AS (
                    SELECT {EntityColumns}, 0 AS priority
                    FROM localized_entities
                    WHERE locale = $locale AND uri IN ({placeholders})
                    UNION ALL
                    SELECT {EntityColumns}, 1 AS priority
                    FROM entities
                    WHERE uri IN ({placeholders})
                    UNION ALL
                    SELECT {EntityColumns}, 2 AS priority
                    FROM localized_entities
                    WHERE locale <> $locale AND uri IN ({placeholders})
                ),
                ranked AS (
                    SELECT
                        {EntityColumns},
                        ROW_NUMBER() OVER (PARTITION BY uri ORDER BY priority, updated_at DESC) AS rn
                    FROM candidates
                )
                SELECT {EntityColumns}
                FROM ranked
                WHERE rn = 1;
                """
            : $"SELECT * FROM entities WHERE uri IN ({placeholders});";

        for (int i = 0; i < uriList.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$uri{i}", uriList[i]);
        }
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
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
            cmd.CommandText = """
                DELETE FROM entities WHERE uri = $uri;
                DELETE FROM localized_entities WHERE uri = $uri;
                """;
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
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? $"""
                WITH candidates AS (
                    SELECT {EntityColumns}, 0 AS priority FROM localized_entities
                    WHERE locale = $locale
                    UNION ALL
                    SELECT {EntityColumns}, 1 AS priority FROM entities
                    UNION ALL
                    SELECT {EntityColumns}, 2 AS priority FROM localized_entities
                    WHERE locale <> $locale
                ),
                ranked AS (
                    SELECT
                        {EntityColumns},
                        ROW_NUMBER() OVER (PARTITION BY uri ORDER BY priority, updated_at DESC) AS rn
                    FROM candidates
                    {whereClause}
                )
                SELECT {EntityColumns}
                FROM ranked
                WHERE rn = 1
                {orderClause}
                LIMIT $limit OFFSET $offset;
                """
            : $"SELECT * FROM entities {whereClause} {orderClause} LIMIT $limit OFFSET $offset;";

        foreach (var param in parameters)
        {
            cmd.Parameters.Add(param);
        }
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
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
        var cacheKey = GetExtensionCacheKey(entityUri, extensionKind, _spotifyMetadataLocale);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Check hot cache first
        if (_hotCache.TryGetValue(cacheKey, out var hotEntry))
        {
            if (hotEntry.ExpiresAt > now)
            {
                _logger?.LogTrace(
                    "Extension hot hit: uri={Uri} kind={Kind} locale={Locale} ttlRemaining={TtlRemaining}s bytes={Bytes}",
                    entityUri,
                    extensionKind,
                    _spotifyMetadataLocale ?? "<default>",
                    hotEntry.ExpiresAt - now,
                    hotEntry.Data.Length);
                return (hotEntry.Data, hotEntry.Etag);
            }
            else
            {
                // Expired, remove from hot cache
                _hotCache.TryRemove(cacheKey, out _);
                _logger?.LogDebug(
                    "Extension hot expired: uri={Uri} kind={Kind} locale={Locale} expiredBy={ExpiredBy}s bytes={Bytes}",
                    entityUri,
                    extensionKind,
                    _spotifyMetadataLocale ?? "<default>",
                    now - hotEntry.ExpiresAt,
                    hotEntry.Data.Length);
            }
        }

        // Check SQLite
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? """
                SELECT data, etag, expires_at FROM localized_extension_cache
                WHERE entity_uri = $entity_uri AND locale = $locale AND extension_kind = $extension_kind;
                """
            : """
                SELECT data, etag, expires_at FROM extension_cache
                WHERE entity_uri = $entity_uri AND extension_kind = $extension_kind;
                """;
        cmd.Parameters.AddWithValue("$entity_uri", entityUri);
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
        }
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

                _logger?.LogTrace(
                    "Extension SQLite hit: uri={Uri} kind={Kind} locale={Locale} ttlRemaining={TtlRemaining}s bytes={Bytes}",
                    entityUri,
                    extensionKind,
                    _spotifyMetadataLocale ?? "<default>",
                    expiresAt - now,
                    data.Length);
                return (data, etag);
            }

            _logger?.LogDebug(
                "Extension SQLite expired: uri={Uri} kind={Kind} locale={Locale} expiredBy={ExpiredBy}s",
                entityUri,
                extensionKind,
                _spotifyMetadataLocale ?? "<default>",
                now - expiresAt);
        }
        else
        {
            _logger?.LogDebug(
                "Extension SQLite miss: uri={Uri} kind={Kind} locale={Locale}",
                entityUri,
                extensionKind,
                _spotifyMetadataLocale ?? "<default>");
        }

        return null;
    }

    /// <summary>
    /// Bulk-reads cached extension data for many URIs in a single SQLite connection.
    /// Chunks into groups of 500 to stay under SQLite parameter limits.
    /// Includes zero-length blobs (negative-cache markers). Omits URIs that are missing or expired.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> GetExtensionsBulkAsync(
        IReadOnlyList<string> entityUris,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, byte[]>(entityUris.Count, StringComparer.Ordinal);
        if (entityUris.Count == 0) return result;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var kind = (int)extensionKind;

        // Hot-cache pass first — resolves fresh entries without touching SQLite.
        var missing = new List<string>(entityUris.Count);
        foreach (var uri in entityUris)
        {
            if (string.IsNullOrEmpty(uri)) continue;
            var cacheKey = GetExtensionCacheKey(uri, extensionKind, _spotifyMetadataLocale);
            if (_hotCache.TryGetValue(cacheKey, out var hot) && hot.ExpiresAt > now)
            {
                result[uri] = hot.Data;
                continue;
            }
            missing.Add(uri);
        }

        if (missing.Count == 0) return result;

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const int chunkSize = 500;
        var useLocalized = HasLocalizedSpotifyMetadata;

        for (int offset = 0; offset < missing.Count; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, missing.Count - offset);

            using var cmd = connection.CreateCommand();
            var sb = new System.Text.StringBuilder();
            sb.Append(useLocalized
                ? "SELECT entity_uri, data, etag, expires_at FROM localized_extension_cache WHERE locale = $locale AND extension_kind = $kind AND entity_uri IN ("
                : "SELECT entity_uri, data, etag, expires_at FROM extension_cache WHERE extension_kind = $kind AND entity_uri IN (");

            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(',');
                var name = $"$u{i}";
                sb.Append(name);
                cmd.Parameters.AddWithValue(name, missing[offset + i]);
            }
            sb.Append(");");

            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("$kind", kind);
            if (useLocalized)
            {
                cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
            }

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var expiresAt = reader.GetInt64(3);
                if (expiresAt <= now) continue; // stale row, skip

                var uri = reader.GetString(0);
                var data = (byte[])reader.GetValue(1);
                var etag = reader.IsDBNull(2) ? null : reader.GetString(2);

                result[uri] = data;

                var cacheKey = GetExtensionCacheKey(uri, extensionKind, _spotifyMetadataLocale);
                PromoteToHotCache(cacheKey, data, etag, expiresAt);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets cached extension data even when the row is expired.
    /// </summary>
    public async Task<(byte[] Data, string? Etag)?> GetStaleExtensionAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = GetExtensionCacheKey(entityUri, extensionKind, _spotifyMetadataLocale);

        if (_hotCache.TryGetValue(cacheKey, out var hotEntry))
        {
            return (hotEntry.Data, hotEntry.Etag);
        }

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? """
                SELECT data, etag, expires_at FROM localized_extension_cache
                WHERE entity_uri = $entity_uri AND locale = $locale AND extension_kind = $extension_kind;
                """
            : """
                SELECT data, etag, expires_at FROM extension_cache
                WHERE entity_uri = $entity_uri AND extension_kind = $extension_kind;
                """;
        cmd.Parameters.AddWithValue("$entity_uri", entityUri);
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
        }
        cmd.Parameters.AddWithValue("$extension_kind", (int)extensionKind);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            _logger?.LogDebug(
                "Stale extension miss: uri={Uri} kind={Kind} locale={Locale}",
                entityUri,
                extensionKind,
                _spotifyMetadataLocale ?? "<default>");
            return null;
        }

        var data = (byte[])reader.GetValue(0);
        var etag = reader.IsDBNull(1) ? null : reader.GetString(1);
        var expiresAt = reader.GetInt64(2);

        _logger?.LogDebug(
            "Stale extension hit: uri={Uri} kind={Kind} locale={Locale} expiredBy={ExpiredBy}s bytes={Bytes} hasEtag={HasEtag}",
            entityUri,
            extensionKind,
            _spotifyMetadataLocale ?? "<default>",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - expiresAt,
            data.Length,
            !string.IsNullOrEmpty(etag));

        return (data, etag);
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
        var cacheKey = GetExtensionCacheKey(entityUri, extensionKind, _spotifyMetadataLocale);

        // Check hot cache first
        if (_hotCache.TryGetValue(cacheKey, out var hotEntry))
        {
            return hotEntry.Etag;
        }

        // Check SQLite
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? """
                SELECT etag, expires_at FROM localized_extension_cache
                WHERE entity_uri = $entity_uri AND locale = $locale AND extension_kind = $extension_kind;
                """
            : """
                SELECT etag, expires_at FROM extension_cache
                WHERE entity_uri = $entity_uri AND extension_kind = $extension_kind;
                """;
        cmd.Parameters.AddWithValue("$entity_uri", entityUri);
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
        }
        cmd.Parameters.AddWithValue("$extension_kind", (int)extensionKind);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            _logger?.LogDebug(
                "Extension etag miss: uri={Uri} kind={Kind} locale={Locale}",
                entityUri,
                extensionKind,
                _spotifyMetadataLocale ?? "<default>");
            return null;
        }

        var etag = reader.IsDBNull(0) ? null : reader.GetString(0);
        var expiresAt = reader.GetInt64(1);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _logger?.LogDebug(
            "Extension etag found: uri={Uri} kind={Kind} locale={Locale} hasEtag={HasEtag} ttlRemaining={TtlRemaining}s",
            entityUri,
            extensionKind,
            _spotifyMetadataLocale ?? "<default>",
            !string.IsNullOrEmpty(etag),
            expiresAt - now);
        return etag;
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
        var cacheKey = GetExtensionCacheKey(entityUri, extensionKind, _spotifyMetadataLocale);

        // Update hot cache
        PromoteToHotCache(cacheKey, data, etag, expiresAt);

        // Update SQLite
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = HasLocalizedSpotifyMetadata
                ? """
                    INSERT INTO localized_extension_cache (entity_uri, locale, extension_kind, data, etag, expires_at, created_at)
                    VALUES ($entity_uri, $locale, $extension_kind, $data, $etag, $expires_at, $now)
                    ON CONFLICT(entity_uri, locale, extension_kind) DO UPDATE SET
                        data = excluded.data,
                        etag = excluded.etag,
                        expires_at = excluded.expires_at;
                    """
                : """
                    INSERT INTO extension_cache (entity_uri, extension_kind, data, etag, expires_at, created_at)
                    VALUES ($entity_uri, $extension_kind, $data, $etag, $expires_at, $now)
                    ON CONFLICT(entity_uri, extension_kind) DO UPDATE SET
                        data = excluded.data,
                        etag = excluded.etag,
                        expires_at = excluded.expires_at;
                    """;

            cmd.Parameters.AddWithValue("$entity_uri", entityUri);
            if (HasLocalizedSpotifyMetadata)
            {
                cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
            }
            cmd.Parameters.AddWithValue("$extension_kind", (int)extensionKind);
            cmd.Parameters.AddWithValue("$data", data);
            cmd.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$expires_at", expiresAt);
            cmd.Parameters.AddWithValue("$now", now);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogDebug(
                "Cached extension: uri={Uri} kind={Kind} locale={Locale} ttl={TTL}s expiresAt={ExpiresAt} bytes={Bytes} hasEtag={HasEtag}",
                entityUri,
                extensionKind,
                _spotifyMetadataLocale ?? "<default>",
                ttlSeconds,
                expiresAt,
                data.Length,
                !string.IsNullOrEmpty(etag));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, CachedExtensionLookup>> GetExtensionsBulkWithEtagAsync(
        IReadOnlyList<string> entityUris,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, CachedExtensionLookup>(entityUris.Count, StringComparer.Ordinal);
        if (entityUris.Count == 0) return result;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var kind = (int)extensionKind;

        // Hot-cache pass — resolves rows without touching SQLite. The hot
        // entry already carries data + etag + expiresAt, so we can classify
        // it without a SELECT.
        var pendingDb = new List<string>(entityUris.Count);
        foreach (var uri in entityUris)
        {
            if (string.IsNullOrEmpty(uri)) continue;
            var cacheKey = GetExtensionCacheKey(uri, extensionKind, _spotifyMetadataLocale);
            if (_hotCache.TryGetValue(cacheKey, out var hot))
            {
                result[uri] = new CachedExtensionLookup(uri, hot.Data, hot.Etag, hot.ExpiresAt > now);
                continue;
            }
            pendingDb.Add(uri);
        }

        if (pendingDb.Count == 0) return result;

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const int chunkSize = 500;
        var useLocalized = HasLocalizedSpotifyMetadata;

        for (int offset = 0; offset < pendingDb.Count; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, pendingDb.Count - offset);

            using var cmd = connection.CreateCommand();
            var sb = new System.Text.StringBuilder();
            sb.Append(useLocalized
                ? "SELECT entity_uri, data, etag, expires_at FROM localized_extension_cache WHERE locale = $locale AND extension_kind = $kind AND entity_uri IN ("
                : "SELECT entity_uri, data, etag, expires_at FROM extension_cache WHERE extension_kind = $kind AND entity_uri IN (");

            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(',');
                var name = $"$u{i}";
                sb.Append(name);
                cmd.Parameters.AddWithValue(name, pendingDb[offset + i]);
            }
            sb.Append(");");

            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("$kind", kind);
            if (useLocalized) cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var uri = reader.GetString(0);
                var data = (byte[])reader.GetValue(1);
                var etag = reader.IsDBNull(2) ? null : reader.GetString(2);
                var expiresAt = reader.GetInt64(3);
                var isFresh = expiresAt > now;

                result[uri] = new CachedExtensionLookup(uri, data, etag, isFresh);
                if (isFresh)
                {
                    var cacheKey = GetExtensionCacheKey(uri, extensionKind, _spotifyMetadataLocale);
                    PromoteToHotCache(cacheKey, data, etag, expiresAt);
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetExtensionsBulkAsync(
        IReadOnlyList<ExtensionWriteRecord> records,
        CancellationToken cancellationToken = default)
    {
        if (records.Count == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var batch = _activeBatch.Value;
        if (batch is not null)
        {
            await ExecuteSetExtensionsBulkAsync(batch.Connection, batch.Transaction, records, now, cancellationToken);
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteSetExtensionsBulkAsync(connection, tx, records, now, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ExecuteSetExtensionsBulkAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<ExtensionWriteRecord> records,
        long now,
        CancellationToken cancellationToken)
    {
        var useLocalized = HasLocalizedSpotifyMetadata;
        using var cmd = connection.CreateCommand();
        if (transaction is not null) cmd.Transaction = transaction;
        cmd.CommandText = useLocalized
            ? """
                INSERT INTO localized_extension_cache (entity_uri, locale, extension_kind, data, etag, expires_at, created_at)
                VALUES ($entity_uri, $locale, $extension_kind, $data, $etag, $expires_at, $now)
                ON CONFLICT(entity_uri, locale, extension_kind) DO UPDATE SET
                    data = excluded.data,
                    etag = excluded.etag,
                    expires_at = excluded.expires_at;
                """
            : """
                INSERT INTO extension_cache (entity_uri, extension_kind, data, etag, expires_at, created_at)
                VALUES ($entity_uri, $extension_kind, $data, $etag, $expires_at, $now)
                ON CONFLICT(entity_uri, extension_kind) DO UPDATE SET
                    data = excluded.data,
                    etag = excluded.etag,
                    expires_at = excluded.expires_at;
                """;

        // Bind once, reuse parameters for each row — avoids reparsing the
        // statement and re-allocating the parameter collection per row.
        var pUri = cmd.Parameters.Add("$entity_uri", SqliteType.Text);
        SqliteParameter? pLocale = null;
        if (useLocalized)
        {
            pLocale = cmd.Parameters.Add("$locale", SqliteType.Text);
            pLocale.Value = _spotifyMetadataLocale;
        }
        var pKind = cmd.Parameters.Add("$extension_kind", SqliteType.Integer);
        var pData = cmd.Parameters.Add("$data", SqliteType.Blob);
        var pEtag = cmd.Parameters.Add("$etag", SqliteType.Text);
        var pExpires = cmd.Parameters.Add("$expires_at", SqliteType.Integer);
        var pNow = cmd.Parameters.Add("$now", SqliteType.Integer);
        pNow.Value = now;

        foreach (var record in records)
        {
            var expiresAt = now + record.TtlSeconds;
            var cacheKey = GetExtensionCacheKey(record.EntityUri, record.Kind, _spotifyMetadataLocale);
            PromoteToHotCache(cacheKey, record.Data, record.Etag, expiresAt);

            pUri.Value = record.EntityUri;
            pKind.Value = (int)record.Kind;
            pData.Value = record.Data;
            pEtag.Value = (object?)record.Etag ?? DBNull.Value;
            pExpires.Value = expiresAt;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task RefreshExtensionTtlBulkAsync(
        IReadOnlyList<(string EntityUri, ExtensionKind Kind)> rows,
        long ttlSeconds,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var newExpiresAt = now + ttlSeconds;

        var batch = _activeBatch.Value;
        if (batch is not null)
        {
            await ExecuteRefreshExtensionTtlBulkAsync(batch.Connection, batch.Transaction, rows, newExpiresAt, cancellationToken);
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteRefreshExtensionTtlBulkAsync(connection, tx, rows, newExpiresAt, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ExecuteRefreshExtensionTtlBulkAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<(string EntityUri, ExtensionKind Kind)> rows,
        long newExpiresAt,
        CancellationToken cancellationToken)
    {
        var useLocalized = HasLocalizedSpotifyMetadata;
        using var cmd = connection.CreateCommand();
        if (transaction is not null) cmd.Transaction = transaction;
        cmd.CommandText = useLocalized
            ? "UPDATE localized_extension_cache SET expires_at = $expires_at WHERE entity_uri = $entity_uri AND locale = $locale AND extension_kind = $extension_kind;"
            : "UPDATE extension_cache SET expires_at = $expires_at WHERE entity_uri = $entity_uri AND extension_kind = $extension_kind;";

        var pUri = cmd.Parameters.Add("$entity_uri", SqliteType.Text);
        SqliteParameter? pLocale = null;
        if (useLocalized)
        {
            pLocale = cmd.Parameters.Add("$locale", SqliteType.Text);
            pLocale.Value = _spotifyMetadataLocale;
        }
        var pKind = cmd.Parameters.Add("$extension_kind", SqliteType.Integer);
        var pExpires = cmd.Parameters.Add("$expires_at", SqliteType.Integer);
        pExpires.Value = newExpiresAt;

        foreach (var (uri, kind) in rows)
        {
            pUri.Value = uri;
            pKind.Value = (int)kind;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Refresh hot cache entry's TTL too so future hot-cache hits are
            // classified as fresh.
            var cacheKey = GetExtensionCacheKey(uri, kind, _spotifyMetadataLocale);
            if (_hotCache.TryGetValue(cacheKey, out var hot))
            {
                _hotCache[cacheKey] = new CachedExtensionEntry(hot.Data, hot.Etag, newExpiresAt);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> BeginWriteBatchAsync(CancellationToken cancellationToken = default)
    {
        if (_activeBatch.Value is not null)
        {
            throw new InvalidOperationException("BeginWriteBatchAsync cannot be nested in the same async-flow.");
        }

        await _writeLock.WaitAsync(cancellationToken);
        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        try
        {
            connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var scope = new WriteBatchScope(this, connection, transaction);
            _activeBatch.Value = scope;
            return scope;
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            _writeLock.Release();
            throw;
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
            cmd.CommandText = """
                DELETE FROM extension_cache WHERE expires_at <= $now;
                DELETE FROM localized_extension_cache WHERE expires_at <= $now;
                """;
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
        var keysToRemove = _hotCache.Keys.Where(k => k.Contains($":{entityUri}:", StringComparison.Ordinal)).ToList();
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
            cmd.CommandText = """
                DELETE FROM extension_cache WHERE entity_uri = $entity_uri;
                DELETE FROM localized_extension_cache WHERE entity_uri = $entity_uri;
                """;
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
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? $"""
                WITH candidates AS (
                    SELECT
                        e.uri,
                        e.source_type,
                        e.entity_type,
                        e.title,
                        e.artist_name,
                        e.album_name,
                        e.album_uri,
                        e.duration_ms,
                        e.track_number,
                        e.disc_number,
                        e.release_year,
                        e.image_url,
                        e.genre,
                        e.track_count,
                        e.follower_count,
                        e.publisher,
                        e.episode_count,
                        e.description,
                        e.file_path,
                        e.stream_url,
                        e.expires_at,
                        e.added_at,
                        e.created_at,
                        e.updated_at,
                        sl.added_at AS library_added_at,
                        0 AS priority
                    FROM localized_entities e
                    INNER JOIN spotify_library sl ON e.uri = sl.item_uri
                    WHERE sl.item_type = $item_type AND e.locale = $locale
                    UNION ALL
                    SELECT
                        e.uri,
                        e.source_type,
                        e.entity_type,
                        e.title,
                        e.artist_name,
                        e.album_name,
                        e.album_uri,
                        e.duration_ms,
                        e.track_number,
                        e.disc_number,
                        e.release_year,
                        e.image_url,
                        e.genre,
                        e.track_count,
                        e.follower_count,
                        e.publisher,
                        e.episode_count,
                        e.description,
                        e.file_path,
                        e.stream_url,
                        e.expires_at,
                        e.added_at,
                        e.created_at,
                        e.updated_at,
                        sl.added_at AS library_added_at,
                        1 AS priority
                    FROM entities e
                    INNER JOIN spotify_library sl ON e.uri = sl.item_uri
                    WHERE sl.item_type = $item_type
                    UNION ALL
                    SELECT
                        e.uri,
                        e.source_type,
                        e.entity_type,
                        e.title,
                        e.artist_name,
                        e.album_name,
                        e.album_uri,
                        e.duration_ms,
                        e.track_number,
                        e.disc_number,
                        e.release_year,
                        e.image_url,
                        e.genre,
                        e.track_count,
                        e.follower_count,
                        e.publisher,
                        e.episode_count,
                        e.description,
                        e.file_path,
                        e.stream_url,
                        e.expires_at,
                        e.added_at,
                        e.created_at,
                        e.updated_at,
                        sl.added_at AS library_added_at,
                        2 AS priority
                    FROM localized_entities e
                    INNER JOIN spotify_library sl ON e.uri = sl.item_uri
                    WHERE sl.item_type = $item_type AND e.locale <> $locale
                ),
                ranked AS (
                    SELECT
                        uri,
                        source_type,
                        entity_type,
                        title,
                        artist_name,
                        album_name,
                        album_uri,
                        duration_ms,
                        track_number,
                        disc_number,
                        release_year,
                        image_url,
                        genre,
                        track_count,
                        follower_count,
                        publisher,
                        episode_count,
                        description,
                        file_path,
                        stream_url,
                        expires_at,
                        library_added_at AS added_at,
                        created_at,
                        updated_at,
                        ROW_NUMBER() OVER (PARTITION BY uri ORDER BY priority, updated_at DESC) AS rn
                    FROM candidates
                )
                SELECT
                    uri,
                    source_type,
                    entity_type,
                    title,
                    artist_name,
                    album_name,
                    album_uri,
                    duration_ms,
                    track_number,
                    disc_number,
                    release_year,
                    image_url,
                    genre,
                    track_count,
                    follower_count,
                    publisher,
                    episode_count,
                    description,
                    file_path,
                    stream_url,
                    expires_at,
                    added_at,
                    created_at,
                    updated_at
                FROM ranked
                WHERE rn = 1
                ORDER BY added_at DESC
                LIMIT $limit OFFSET $offset;
                """
            : """
                SELECT
                    e.uri,
                    e.source_type,
                    e.entity_type,
                    e.title,
                    e.artist_name,
                    e.album_name,
                    e.album_uri,
                    e.duration_ms,
                    e.track_number,
                    e.disc_number,
                    e.release_year,
                    e.image_url,
                    e.genre,
                    e.track_count,
                    e.follower_count,
                    e.publisher,
                    e.episode_count,
                    e.description,
                    e.file_path,
                    e.stream_url,
                    e.expires_at,
                    sl.added_at AS added_at,
                    e.created_at,
                    e.updated_at
                FROM entities e
                INNER JOIN spotify_library sl ON e.uri = sl.item_uri
                WHERE sl.item_type = $item_type
                ORDER BY sl.added_at DESC
                LIMIT $limit OFFSET $offset;
                """;

        cmd.Parameters.AddWithValue("$item_type", (int)itemType);
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
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
    /// Gets URIs in spotify_library that have no corresponding row in the entities table.
    /// </summary>
    public async Task<List<string>> GetLibraryUrisMissingMetadataAsync(
        SpotifyLibraryItemType itemType,
        CancellationToken cancellationToken = default)
    {
        var results = new List<string>();

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = HasLocalizedSpotifyMetadata
            ? """
                SELECT sl.item_uri FROM spotify_library sl
                LEFT JOIN localized_entities e ON e.uri = sl.item_uri AND e.locale = $locale
                WHERE sl.item_type = $item_type AND e.uri IS NULL;
                """
            : """
                SELECT sl.item_uri FROM spotify_library sl
                LEFT JOIN entities e ON e.uri = sl.item_uri
                WHERE sl.item_type = $item_type AND e.uri IS NULL;
                """;
        cmd.Parameters.AddWithValue("$item_type", (int)itemType);
        if (HasLocalizedSpotifyMetadata)
        {
            cmd.Parameters.AddWithValue("$locale", _spotifyMetadataLocale);
        }

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        return results;
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

    #region Library Outbox Operations

    public async Task EnqueueLibraryOpAsync(string itemUri, SpotifyLibraryItemType itemType, LibraryOutboxOperation operation, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO library_outbox (item_uri, item_type, operation, created_at)
                VALUES ($uri, $type, $op, $created)
                ON CONFLICT(item_uri) DO UPDATE SET
                    item_type = excluded.item_type,
                    operation = excluded.operation,
                    created_at = excluded.created_at,
                    retry_count = 0,
                    last_error = NULL;
                """;
            cmd.Parameters.AddWithValue("$uri", itemUri);
            cmd.Parameters.AddWithValue("$type", (int)itemType);
            cmd.Parameters.AddWithValue("$op", (int)operation);
            cmd.Parameters.AddWithValue("$created", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<LibraryOutboxEntry>> DequeueLibraryOpsAsync(int limit = 50, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, item_uri, item_type, operation, created_at, retry_count, last_error FROM library_outbox ORDER BY created_at LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<LibraryOutboxEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new LibraryOutboxEntry
            {
                Id = reader.GetInt64(0),
                ItemUri = reader.GetString(1),
                ItemType = (SpotifyLibraryItemType)reader.GetInt32(2),
                Operation = (LibraryOutboxOperation)reader.GetInt32(3),
                CreatedAt = reader.GetInt64(4),
                RetryCount = reader.GetInt32(5),
                LastError = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return results;
    }

    public async Task CompleteLibraryOpAsync(long id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM library_outbox WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task FailLibraryOpAsync(long id, string? error, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE library_outbox SET retry_count = retry_count + 1, last_error = $error WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
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

    public async Task ClearAllSyncStateAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_state;";
            var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogInformation("Cleared {Count} sync_state rows", affected);
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
                INSERT INTO spotify_playlists (id, name, owner_id, owner_name, description, image_url, track_count, is_public, is_collaborative, is_owned, synced_at, revision, folder_path, is_from_rootlist)
                VALUES ($id, $name, $owner_id, $owner_name, $description, $image_url, $track_count, $is_public, $is_collaborative, $is_owned, $synced_at, $revision, $folder_path, $is_from_rootlist)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    owner_id = excluded.owner_id,
                    owner_name = excluded.owner_name,
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
            cmd.Parameters.AddWithValue("$owner_name", (object?)playlist.OwnerName ?? DBNull.Value);
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
            SELECT id, name, owner_id, owner_name, description, image_url, track_count, is_public, is_collaborative, is_owned, synced_at, revision, folder_path, is_from_rootlist
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
            SELECT id, name, owner_id, owner_name, description, image_url, track_count, is_public, is_collaborative, is_owned, synced_at, revision, folder_path, is_from_rootlist
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

    public async Task UpsertPlaylistCacheEntryAsync(PlaylistCacheEntry playlist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO spotify_playlists (
                    id, name, owner_id, owner_name, description, image_url, header_image_url, track_count,
                    is_public, is_collaborative, is_owned, synced_at, cache_revision,
                    ordered_items_json, has_contents_snapshot, base_permission,
                    capabilities_json, format_attributes_json, available_signals_json,
                    deleted_by_owner, abuse_reporting_enabled,
                    last_accessed_at, is_from_rootlist, cache_schema_version
                )
                VALUES (
                    $id, $name, $owner_id, $owner_name, $description, $image_url, $header_image_url, $track_count,
                    $is_public, $is_collaborative, $is_owned, $synced_at, $cache_revision,
                    $ordered_items_json, $has_contents_snapshot, $base_permission,
                    $capabilities_json, $format_attributes_json, $available_signals_json,
                    $deleted_by_owner, $abuse_reporting_enabled,
                    $last_accessed_at, 1, $cache_schema_version
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    owner_id = excluded.owner_id,
                    owner_name = excluded.owner_name,
                    description = excluded.description,
                    image_url = excluded.image_url,
                    header_image_url = excluded.header_image_url,
                    track_count = excluded.track_count,
                    is_public = excluded.is_public,
                    is_collaborative = excluded.is_collaborative,
                    is_owned = excluded.is_owned,
                    synced_at = excluded.synced_at,
                    cache_revision = excluded.cache_revision,
                    ordered_items_json = COALESCE(excluded.ordered_items_json, spotify_playlists.ordered_items_json),
                    has_contents_snapshot = excluded.has_contents_snapshot,
                    base_permission = excluded.base_permission,
                    capabilities_json = excluded.capabilities_json,
                    format_attributes_json = excluded.format_attributes_json,
                    available_signals_json = excluded.available_signals_json,
                    deleted_by_owner = excluded.deleted_by_owner,
                    abuse_reporting_enabled = excluded.abuse_reporting_enabled,
                    last_accessed_at = excluded.last_accessed_at,
                    is_from_rootlist = 1,
                    cache_schema_version = excluded.cache_schema_version;
                """;

            cmd.Parameters.AddWithValue("$id", playlist.Uri);
            cmd.Parameters.AddWithValue("$name", playlist.Name ?? string.Empty);
            cmd.Parameters.AddWithValue("$owner_id", (object?)playlist.OwnerUri ?? (object?)playlist.OwnerUsername ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owner_name", (object?)playlist.OwnerName ?? (object?)playlist.OwnerUsername ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)playlist.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$image_url", (object?)playlist.ImageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$header_image_url", (object?)playlist.HeaderImageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$track_count", playlist.TrackCount ?? 0);
            cmd.Parameters.AddWithValue("$is_public", playlist.IsPublic ? 1 : 0);
            cmd.Parameters.AddWithValue("$is_collaborative", playlist.IsCollaborative ? 1 : 0);
            cmd.Parameters.AddWithValue("$is_owned", playlist.BasePermission == CachedPlaylistBasePermission.Owner ? 1 : 0);
            cmd.Parameters.AddWithValue("$synced_at", playlist.CachedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$cache_revision", (object?)playlist.Revision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ordered_items_json", (object?)playlist.OrderedItemsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$has_contents_snapshot", playlist.HasContentsSnapshot ? 1 : 0);
            cmd.Parameters.AddWithValue("$base_permission", (int)playlist.BasePermission);
            cmd.Parameters.AddWithValue("$capabilities_json", (object?)playlist.CapabilitiesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$format_attributes_json", (object?)playlist.FormatAttributesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$available_signals_json", (object?)playlist.AvailableSignalsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$deleted_by_owner", playlist.DeletedByOwner ? 1 : 0);
            cmd.Parameters.AddWithValue("$abuse_reporting_enabled", playlist.AbuseReportingEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_accessed_at", playlist.LastAccessedAt.HasValue
                ? playlist.LastAccessedAt.Value.ToUnixTimeSeconds()
                : DBNull.Value);
            cmd.Parameters.AddWithValue("$cache_schema_version", playlist.CacheSchemaVersion);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<PlaylistCacheEntry?> GetPlaylistCacheEntryAsync(
        string playlistUri,
        bool touchAccess = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, owner_id, owner_name, description, image_url, track_count,
                   is_public, is_collaborative, cache_revision, ordered_items_json,
                   has_contents_snapshot, base_permission, capabilities_json,
                   deleted_by_owner, abuse_reporting_enabled, synced_at, last_accessed_at,
                   header_image_url, format_attributes_json, available_signals_json,
                   cache_schema_version
            FROM spotify_playlists
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", playlistUri);

        PlaylistCacheEntry? entry;
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            entry = ReadPlaylistCacheEntry(reader);
        }

        if (!touchAccess)
            return entry;

        var touchedAt = DateTimeOffset.UtcNow;
        await TouchPlaylistCacheEntryAsync(playlistUri, touchedAt, cancellationToken);
        return entry with { LastAccessedAt = touchedAt };
    }

    public async Task<List<PlaylistCacheEntry>> GetRecentPlaylistCacheEntriesAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        var results = new List<PlaylistCacheEntry>();

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, owner_id, owner_name, description, image_url, track_count,
                   is_public, is_collaborative, cache_revision, ordered_items_json,
                   has_contents_snapshot, base_permission, capabilities_json,
                   deleted_by_owner, abuse_reporting_enabled, synced_at, last_accessed_at,
                   header_image_url, format_attributes_json, available_signals_json,
                   cache_schema_version
            FROM spotify_playlists
            ORDER BY COALESCE(last_accessed_at, synced_at, 0) DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadPlaylistCacheEntry(reader));
        }

        return results;
    }

    public async Task UpsertRootlistCacheEntryAsync(RootlistCacheEntry rootlist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootlist);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rootlist_cache (id, revision, json_data, cached_at, last_accessed_at)
                VALUES ($id, $revision, $json_data, $cached_at, $last_accessed_at)
                ON CONFLICT(id) DO UPDATE SET
                    revision = excluded.revision,
                    json_data = excluded.json_data,
                    cached_at = excluded.cached_at,
                    last_accessed_at = excluded.last_accessed_at;
                """;
            cmd.Parameters.AddWithValue("$id", rootlist.Uri);
            cmd.Parameters.AddWithValue("$revision", (object?)rootlist.Revision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$json_data", rootlist.JsonData);
            cmd.Parameters.AddWithValue("$cached_at", rootlist.CachedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$last_accessed_at", rootlist.LastAccessedAt.HasValue
                ? rootlist.LastAccessedAt.Value.ToUnixTimeSeconds()
                : DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<RootlistCacheEntry?> GetRootlistCacheEntryAsync(
        string rootlistUri,
        bool touchAccess = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootlistUri);

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, revision, json_data, cached_at, last_accessed_at
            FROM rootlist_cache
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", rootlistUri);

        RootlistCacheEntry? entry;
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            entry = new RootlistCacheEntry
            {
                Uri = reader.GetString(0),
                Revision = reader.IsDBNull(1) ? null : (byte[])reader[1],
                JsonData = reader.GetString(2),
                CachedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                LastAccessedAt = reader.IsDBNull(4)
                    ? null
                    : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))
            };
        }

        if (!touchAccess)
            return entry;

        var touchedAt = DateTimeOffset.UtcNow;
        await TouchRootlistCacheEntryAsync(rootlistUri, touchedAt, cancellationToken);
        return entry with { LastAccessedAt = touchedAt };
    }

    private static SpotifyPlaylist ReadPlaylist(SqliteDataReader reader)
    {
        return new SpotifyPlaylist
        {
            Uri = reader.GetString(0),
            Name = reader.GetString(1),
            OwnerId = reader.IsDBNull(2) ? null : reader.GetString(2),
            OwnerName = reader.IsDBNull(3) ? null : reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            ImageUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
            TrackCount = reader.GetInt32(6),
            IsPublic = reader.GetInt32(7) == 1,
            IsCollaborative = reader.GetInt32(8) == 1,
            IsOwned = reader.GetInt32(9) == 1,
            SyncedAt = reader.GetInt64(10),
            Revision = reader.IsDBNull(11) ? null : reader.GetString(11),
            FolderPath = reader.IsDBNull(12) ? null : reader.GetString(12),
            IsFromRootlist = reader.IsDBNull(13) || reader.GetInt32(13) == 1
        };
    }

    private static PlaylistCacheEntry ReadPlaylistCacheEntry(SqliteDataReader reader)
    {
        // Column 2 is the owner_id column, which stores the full `spotify:user:{id}` URI.
        // OwnerUsername should be the bare id — strip the prefix (repeatedly, so legacy
        // rows that were already corrupted as `spotify:user:spotify:user:{id}` heal on read).
        // Without this normalisation, downstream writes build `spotify:user:{OwnerUsername}`
        // and the prefix grows on every round-trip, eventually producing the long recursive
        // string visible in the UI.
        var ownerIdRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
        string? ownerUsername = ownerIdRaw;
        if (ownerUsername is not null)
        {
            const string prefix = "spotify:user:";
            while (ownerUsername.StartsWith(prefix, StringComparison.Ordinal))
                ownerUsername = ownerUsername[prefix.Length..];
        }

        return new PlaylistCacheEntry
        {
            Uri = reader.GetString(0),
            Name = reader.GetString(1),
            OwnerUri = ownerIdRaw,
            OwnerUsername = ownerUsername,
            OwnerName = reader.IsDBNull(3) ? null : reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            ImageUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
            TrackCount = reader.GetInt32(6),
            IsPublic = reader.GetInt32(7) == 1,
            IsCollaborative = reader.GetInt32(8) == 1,
            Revision = reader.IsDBNull(9) ? null : (byte[])reader[9],
            OrderedItemsJson = reader.IsDBNull(10) ? null : reader.GetString(10),
            HasContentsSnapshot = reader.GetInt32(11) == 1,
            BasePermission = (CachedPlaylistBasePermission)reader.GetInt32(12),
            CapabilitiesJson = reader.IsDBNull(13) ? null : reader.GetString(13),
            DeletedByOwner = reader.GetInt32(14) == 1,
            AbuseReportingEnabled = reader.GetInt32(15) == 1,
            CachedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(16)),
            LastAccessedAt = reader.IsDBNull(17)
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(17)),
            HeaderImageUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
            FormatAttributesJson = reader.IsDBNull(19) ? null : reader.GetString(19),
            AvailableSignalsJson = reader.IsDBNull(20) ? null : reader.GetString(20),
            CacheSchemaVersion = reader.IsDBNull(21) ? 0 : reader.GetInt32(21)
        };
    }

    public async Task TouchPlaylistCacheEntryAsync(
        string playlistUri,
        DateTimeOffset accessedAt,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE spotify_playlists
                SET last_accessed_at = $last_accessed_at
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", playlistUri);
            cmd.Parameters.AddWithValue("$last_accessed_at", accessedAt.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task TouchRootlistCacheEntryAsync(
        string rootlistUri,
        DateTimeOffset accessedAt,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE rootlist_cache
                SET last_accessed_at = $last_accessed_at
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", rootlistUri);
            cmd.Parameters.AddWithValue("$last_accessed_at", accessedAt.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(cancellationToken);
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
            cmd.CommandText = """
                SELECT COUNT(*) FROM (
                    SELECT uri FROM entities
                    UNION
                    SELECT uri FROM localized_entities
                );
                """;
            entityCount = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM extension_cache) + (SELECT COUNT(*) FROM localized_extension_cache),
                    (SELECT COALESCE(SUM(LENGTH(data)), 0) FROM extension_cache) + (SELECT COALESCE(SUM(LENGTH(data)), 0) FROM localized_extension_cache);
                """;
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
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM extension_cache WHERE expires_at <= $now) +
                    (SELECT COUNT(*) FROM localized_extension_cache WHERE expires_at <= $now);
                """;
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
                DELETE FROM localized_extension_cache;
                DELETE FROM entities;
                DELETE FROM localized_entities;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger?.LogInformation("Database cleared");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task WipeAllUserDataAsync(CancellationToken cancellationToken = default)
    {
        _hotCache.Clear();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM extension_cache;
                DELETE FROM localized_extension_cache;
                DELETE FROM entities;
                DELETE FROM localized_entities;
                DELETE FROM spotify_library;
                DELETE FROM sync_state;
                DELETE FROM spotify_playlists;
                DELETE FROM rootlist_cache;
                DELETE FROM library_outbox;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger?.LogInformation("Wiped all user-bound tables (entities, library, sync_state, playlists, rootlist, outbox)");
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

    private static string GetExtensionCacheKey(string entityUri, ExtensionKind extensionKind, string? locale)
    {
        return $"{locale ?? string.Empty}:{entityUri}:{(int)extensionKind}";
    }

    private bool HasLocalizedSpotifyMetadata => !string.IsNullOrEmpty(_spotifyMetadataLocale);

    private bool ShouldUseLocalizedSpotifyMetadata(SourceType sourceType) =>
        sourceType == SourceType.Spotify && HasLocalizedSpotifyMetadata;

    private static string NormalizeSpotifyLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return string.Empty;
        }

        var trimmed = locale.Trim();
        if (trimmed.Length != 2 || !char.IsLetter(trimmed[0]) || !char.IsLetter(trimmed[1]))
        {
            return string.Empty;
        }

        return trimmed.ToLowerInvariant();
    }

    private void PromoteToHotCache(string cacheKey, byte[] data, string? etag, long expiresAt)
    {
        // Simple size-based eviction if we're at capacity
        if (_hotCache.Count >= _maxHotCacheSize)
        {
            // Snapshot first — enumerating a ConcurrentDictionary while other
            // callers write can surface a KVP whose Value reads as null for
            // an instant (bucket in the middle of a transition). OrderBy would
            // then NRE on kvp.Value.ExpiresAt. ToArray() takes a stable view,
            // and the explicit null-guard survives any remaining races.
            var snapshot = _hotCache.ToArray();
            var keysToRemove = snapshot
                .Where(kvp => kvp.Value is not null)
                .OrderBy(kvp => kvp.Value.ExpiresAt)
                .Take(Math.Max(1, _maxHotCacheSize / 10))
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

    #region Album Tracks Cache Operations

    /// <inheritdoc />
    public async Task SetAlbumTracksCacheAsync(string albumUri, string jsonData, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO album_tracks_cache (album_uri, json_data, cached_at)
                VALUES (@uri, @json, @cached)
                """;
            cmd.Parameters.AddWithValue("@uri", albumUri);
            cmd.Parameters.AddWithValue("@json", jsonData);
            cmd.Parameters.AddWithValue("@cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetAlbumTracksCacheAsync(string albumUri, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM album_tracks_cache WHERE album_uri = @uri";
        cmd.Parameters.AddWithValue("@uri", albumUri);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return reader.GetString(0);

        return null;
    }

    #endregion

    #region Color Cache Operations

    /// <inheritdoc />
    public async Task SetColorCacheAsync(string imageUrl, string? darkHex, string? lightHex, string? rawHex, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO color_cache (image_url, dark_hex, light_hex, raw_hex, cached_at)
                VALUES (@url, @dark, @light, @raw, @cached)
                """;
            cmd.Parameters.AddWithValue("@url", imageUrl);
            cmd.Parameters.AddWithValue("@dark", (object?)darkHex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@light", (object?)lightHex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@raw", (object?)rawHex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<(string? DarkHex, string? LightHex, string? RawHex)?> GetColorCacheAsync(string imageUrl, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT dark_hex, light_hex, raw_hex FROM color_cache WHERE image_url = @url";
        cmd.Parameters.AddWithValue("@url", imageUrl);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)
            );
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetPersistedAudioKeyAsync(string fileIdHex, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key_bytes FROM audio_keys WHERE file_id = @file_id";
        cmd.Parameters.AddWithValue("@file_id", fileIdHex);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
        {
            return (byte[])reader.GetValue(0);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task SetPersistedAudioKeyAsync(string fileIdHex, string? trackUri, byte[] keyBytes, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO audio_keys (file_id, track_uri, key_bytes, cached_at)
                VALUES (@file_id, @track_uri, @key, @cached)
                """;
            cmd.Parameters.AddWithValue("@file_id", fileIdHex);
            cmd.Parameters.AddWithValue("@track_uri", (object?)trackUri ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@key", keyBytes);
            cmd.Parameters.AddWithValue("@cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetPersistedPlayPlayObfuscatedKeyAsync(string fileIdHex, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT obf_key_bytes FROM playplay_obfuscated_keys WHERE file_id = @file_id";
        cmd.Parameters.AddWithValue("@file_id", fileIdHex);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
            return (byte[])reader.GetValue(0);
        return null;
    }

    /// <inheritdoc />
    public async Task SetPersistedPlayPlayObfuscatedKeyAsync(string fileIdHex, byte[] obfuscatedKey, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO playplay_obfuscated_keys (file_id, obf_key_bytes, cached_at)
                VALUES (@file_id, @key, @cached)
                """;
            cmd.Parameters.AddWithValue("@file_id", fileIdHex);
            cmd.Parameters.AddWithValue("@key", obfuscatedKey);
            cmd.Parameters.AddWithValue("@cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetPersistedHeadDataAsync(string fileIdHex, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM head_data WHERE file_id = @file_id";
        cmd.Parameters.AddWithValue("@file_id", fileIdHex);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
        {
            return (byte[])reader.GetValue(0);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task SetPersistedHeadDataAsync(string fileIdHex, byte[] headData, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO head_data (file_id, data, cached_at)
                VALUES (@file_id, @data, @cached)
                """;
            cmd.Parameters.AddWithValue("@file_id", fileIdHex);
            cmd.Parameters.AddWithValue("@data", headData);
            cmd.Parameters.AddWithValue("@cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Lyrics Cache Operations

    /// <inheritdoc />
    public async Task<(string JsonData, string? Provider)?> GetLyricsCacheAsync(string trackUri, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json_data, provider FROM lyrics_cache WHERE track_uri = @uri";
        cmd.Parameters.AddWithValue("@uri", trackUri);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return (
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)
            );
        }

        return null;
    }

    /// <inheritdoc />
    public async Task SetLyricsCacheAsync(string trackUri, string? provider, string jsonData, bool hasSyllableSync, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO lyrics_cache (track_uri, provider, json_data, has_syllable_sync, cached_at)
                VALUES (@uri, @provider, @json, @sync, @cached)
                """;
            cmd.Parameters.AddWithValue("@uri", trackUri);
            cmd.Parameters.AddWithValue("@provider", (object?)provider ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@json", jsonData);
            cmd.Parameters.AddWithValue("@sync", hasSyllableSync ? 1 : 0);
            cmd.Parameters.AddWithValue("@cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteLyricsCacheAsync(string trackUri, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM lyrics_cache WHERE track_uri = @uri";
            cmd.Parameters.AddWithValue("@uri", trackUri);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Media Override Operations

    /// <inheritdoc />
    public async Task<MediaOverrideEntry?> GetMediaOverrideAsync(
        MediaOverrideAssetType assetType,
        string entityKey,
        CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT effective_asset_url,
                   effective_source,
                   last_seen_upstream_url,
                   pending_asset_url,
                   last_reviewed_upstream_url,
                   created_at,
                   updated_at
            FROM media_overrides
            WHERE asset_type = @assetType AND entity_key = @entityKey
            """;
        cmd.Parameters.AddWithValue("@assetType", (int)assetType);
        cmd.Parameters.AddWithValue("@entityKey", entityKey);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new MediaOverrideEntry
        {
            AssetType = assetType,
            EntityKey = entityKey,
            EffectiveAssetUrl = reader.IsDBNull(0) ? null : reader.GetString(0),
            EffectiveSource = (MediaOverrideSource)reader.GetInt32(1),
            LastSeenUpstreamUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
            PendingAssetUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
            LastReviewedUpstreamUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetInt64(5),
            UpdatedAt = reader.GetInt64(6),
        };
    }

    /// <inheritdoc />
    public async Task SetMediaOverrideAsync(MediaOverrideEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO media_overrides (
                    asset_type,
                    entity_key,
                    effective_asset_url,
                    effective_source,
                    last_seen_upstream_url,
                    pending_asset_url,
                    last_reviewed_upstream_url,
                    created_at,
                    updated_at)
                VALUES (
                    @assetType,
                    @entityKey,
                    @effectiveAssetUrl,
                    @effectiveSource,
                    @lastSeenUpstreamUrl,
                    @pendingAssetUrl,
                    @lastReviewedUpstreamUrl,
                    @createdAt,
                    @updatedAt)
                """;
            cmd.Parameters.AddWithValue("@assetType", (int)entry.AssetType);
            cmd.Parameters.AddWithValue("@entityKey", entry.EntityKey);
            cmd.Parameters.AddWithValue("@effectiveAssetUrl", (object?)entry.EffectiveAssetUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@effectiveSource", (int)entry.EffectiveSource);
            cmd.Parameters.AddWithValue("@lastSeenUpstreamUrl", (object?)entry.LastSeenUpstreamUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pendingAssetUrl", (object?)entry.PendingAssetUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lastReviewedUpstreamUrl", (object?)entry.LastReviewedUpstreamUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", entry.CreatedAt);
            cmd.Parameters.AddWithValue("@updatedAt", entry.UpdatedAt);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteMediaOverrideAsync(
        MediaOverrideAssetType assetType,
        string entityKey,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM media_overrides
                WHERE asset_type = @assetType AND entity_key = @entityKey
                """;
            cmd.Parameters.AddWithValue("@assetType", (int)assetType);
            cmd.Parameters.AddWithValue("@entityKey", entityKey);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
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

    /// <summary>
    /// Open write-batch scope: holds the write lock + a SQLite transaction
    /// for the lifetime of the using-block. Disposing commits.
    /// </summary>
    private sealed class WriteBatchScope : IAsyncDisposable
    {
        private readonly MetadataDatabase _owner;
        private bool _disposed;

        public SqliteConnection Connection { get; }
        public SqliteTransaction Transaction { get; }

        public WriteBatchScope(MetadataDatabase owner, SqliteConnection connection, SqliteTransaction transaction)
        {
            _owner = owner;
            Connection = connection;
            Transaction = transaction;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await Transaction.CommitAsync().ConfigureAwait(false);
            }
            finally
            {
                await Transaction.DisposeAsync().ConfigureAwait(false);
                await Connection.DisposeAsync().ConfigureAwait(false);
                _owner._activeBatch.Value = null;
                _owner._writeLock.Release();
            }
        }
    }
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
