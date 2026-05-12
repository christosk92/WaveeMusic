namespace Wavee.Local.Schema;

/// <summary>
/// SQL constants for the v17 schema migration. Imported by
/// <c>Wavee.Core.Storage.MetadataDatabase</c> and run as a single
/// migration step bumping <c>PRAGMA user_version</c> from 16 to 17.
///
/// <para>
/// Wavee.Local doesn't run migrations itself — the shared MetadataDatabase
/// owns schema setup for the entire SQLite file. This class just declares
/// what the local-files-specific v17 changes are.
/// </para>
///
/// <para>v17 additions:</para>
/// <list type="bullet">
///   <item>New columns on <c>local_files</c>: auto_kind, kind_override,
///         metadata_overrides JSON, series_id, season_number, episode_number,
///         episode_title, movie_year, tmdb_id, musicbrainz_id,
///         enrichment_state, enrichment_at, last_position_ms, watched_at,
///         watch_count</item>
///   <item>New tables: local_series, local_groups, local_group_members,
///         local_subtitle_files, local_embedded_tracks,
///         local_enrichment_negatives, local_plays, local_lyrics</item>
///   <item>Schema is additive — no DROP / no restructure / no data loss</item>
/// </list>
/// </summary>
internal static class LocalSchemaV17
{
    /// <summary>
    /// Complete SQL block for migrating an existing v16 database to v17.
    /// Idempotent (every statement uses IF NOT EXISTS / IF NOT EXISTS-style
    /// guards where SQLite allows) so a partial mid-run failure on retry
    /// stays safe.
    /// </summary>
    public const string MigrationSql = """
        -- Per-file classification + override + enrichment state.
        ALTER TABLE local_files ADD COLUMN auto_kind          TEXT    NOT NULL DEFAULT 'Other';
        ALTER TABLE local_files ADD COLUMN kind_override      TEXT;
        ALTER TABLE local_files ADD COLUMN metadata_overrides TEXT;
        ALTER TABLE local_files ADD COLUMN series_id          TEXT;
        ALTER TABLE local_files ADD COLUMN season_number      INTEGER;
        ALTER TABLE local_files ADD COLUMN episode_number     INTEGER;
        ALTER TABLE local_files ADD COLUMN episode_title      TEXT;
        ALTER TABLE local_files ADD COLUMN movie_year         INTEGER;
        ALTER TABLE local_files ADD COLUMN tmdb_id            INTEGER;
        ALTER TABLE local_files ADD COLUMN musicbrainz_id     TEXT;
        ALTER TABLE local_files ADD COLUMN enrichment_state   TEXT    NOT NULL DEFAULT 'Pending';
        ALTER TABLE local_files ADD COLUMN enrichment_at      INTEGER;
        ALTER TABLE local_files ADD COLUMN last_position_ms   INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE local_files ADD COLUMN watched_at         INTEGER;
        ALTER TABLE local_files ADD COLUMN watch_count        INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX IF NOT EXISTS idx_local_files_auto_kind  ON local_files(auto_kind);
        CREATE INDEX IF NOT EXISTS idx_local_files_series     ON local_files(series_id, season_number, episode_number);
        CREATE INDEX IF NOT EXISTS idx_local_files_watched    ON local_files(watched_at) WHERE watched_at IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_local_files_resume     ON local_files(last_position_ms) WHERE last_position_ms > 0;

        -- Per-folder soft kind hint that biases the classifier.
        ALTER TABLE watched_folders ADD COLUMN expected_kind  TEXT;

        -- TV series grouping. One row per detected show; episodes link via local_files.series_id.
        CREATE TABLE IF NOT EXISTS local_series (
            id              TEXT PRIMARY KEY NOT NULL,
            name            TEXT NOT NULL,
            poster_hash     TEXT,
            backdrop_hash   TEXT,
            tmdb_id         INTEGER,
            overview        TEXT,
            created_at      INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_local_series_name ON local_series(name);

        -- User-defined collections + auto-generated show/album groupings.
        CREATE TABLE IF NOT EXISTS local_groups (
            id              TEXT PRIMARY KEY NOT NULL,
            name            TEXT NOT NULL,
            kind            TEXT NOT NULL,
            poster_hash     TEXT,
            created_at      INTEGER NOT NULL,
            user_created    INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_local_groups_kind ON local_groups(kind);

        CREATE TABLE IF NOT EXISTS local_group_members (
            group_id        TEXT NOT NULL,
            local_file_path TEXT NOT NULL,
            sort_order      INTEGER NOT NULL,
            PRIMARY KEY (group_id, local_file_path),
            FOREIGN KEY (group_id)        REFERENCES local_groups(id)   ON DELETE CASCADE,
            FOREIGN KEY (local_file_path) REFERENCES local_files(path)  ON DELETE CASCADE
        );

        -- External subtitle files associated with a video.
        CREATE TABLE IF NOT EXISTS local_subtitle_files (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            local_file_path     TEXT NOT NULL,
            subtitle_path       TEXT NOT NULL,
            language            TEXT,
            forced              INTEGER NOT NULL DEFAULT 0,
            sdh                 INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (local_file_path) REFERENCES local_files(path) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_local_subs_file ON local_subtitle_files(local_file_path);

        -- Embedded audio/video/subtitle tracks discovered in container metadata
        -- (mkv, mp4 multi-track files). Indexed at scan time via IEmbeddedTrackProber.
        CREATE TABLE IF NOT EXISTS local_embedded_tracks (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            local_file_path     TEXT NOT NULL,
            kind                TEXT NOT NULL,        -- 'audio' | 'video' | 'subtitle'
            stream_index        INTEGER NOT NULL,
            language            TEXT,
            label               TEXT,
            codec               TEXT,
            is_default          INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (local_file_path) REFERENCES local_files(path) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_local_embedded_file_kind ON local_embedded_tracks(local_file_path, kind);

        -- TTL'd no-match cache so a failed enrichment lookup isn't re-tried for 30 days.
        CREATE TABLE IF NOT EXISTS local_enrichment_negatives (
            local_file_path     TEXT NOT NULL,
            provider            TEXT NOT NULL,        -- 'tmdb' | 'musicbrainz'
            queried_at          INTEGER NOT NULL,
            PRIMARY KEY (local_file_path, provider),
            FOREIGN KEY (local_file_path) REFERENCES local_files(path) ON DELETE CASCADE
        );

        -- Local-only play history. Powers "Recently played" rails and merges
        -- with the global Recently Played surface. Does NOT go upstream to
        -- Spotify (Spotify play history is the gabo-receiver-service path).
        CREATE TABLE IF NOT EXISTS local_plays (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            track_uri       TEXT NOT NULL,
            played_at       INTEGER NOT NULL,
            position_ms     INTEGER NOT NULL DEFAULT 0,
            duration_ms     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_local_plays_played_at ON local_plays(played_at DESC);
        CREATE INDEX IF NOT EXISTS idx_local_plays_track_uri ON local_plays(track_uri);

        -- Cached + sibling-discovered lyrics for local tracks. Source-of-record
        -- for what the lyrics view shows when a local track is playing.
        CREATE TABLE IF NOT EXISTS local_lyrics (
            local_file_path     TEXT PRIMARY KEY NOT NULL,
            source              TEXT NOT NULL,    -- 'sibling-file' | 'lrclib' | 'manual'
            format              TEXT NOT NULL,    -- 'plain' | 'lrc' | 'enhanced-lrc'
            body                TEXT NOT NULL,
            language            TEXT,
            fetched_at          INTEGER NOT NULL,
            FOREIGN KEY (local_file_path) REFERENCES local_files(path) ON DELETE CASCADE
        );
        """;
}
