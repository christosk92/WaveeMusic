namespace Wavee.Local.Schema;

/// <summary>
/// SQL constants for the v20 schema migration. Imported by
/// <c>Wavee.Core.Storage.MetadataDatabase</c> and run as a single
/// migration step bumping <c>PRAGMA user_version</c> from 19 to 20.
///
/// <para>v20 surfaces full TMDB movie details on the Movie Detail page —
/// overview, tagline, runtime, genres, vote average, backdrop — plus the
/// principal cast list. The data ships in TMDB's
/// <c>/3/movie/{id}?append_to_response=credits</c> response (one HTTP call
/// per movie); the new columns are the read-side projection target so
/// <c>ReadMovies</c> doesn't have to parse <c>metadata_overrides</c> JSON.</para>
///
/// <para>Additive only — no DROP / no rewrite.</para>
/// </summary>
internal static class LocalSchemaV20
{
    public const string MigrationSql = """
        ALTER TABLE local_files ADD COLUMN movie_overview      TEXT;
        ALTER TABLE local_files ADD COLUMN movie_tagline       TEXT;
        ALTER TABLE local_files ADD COLUMN movie_runtime_min   INTEGER;
        ALTER TABLE local_files ADD COLUMN movie_genres        TEXT;
        ALTER TABLE local_files ADD COLUMN movie_vote_average  REAL;
        ALTER TABLE local_files ADD COLUMN movie_backdrop_hash TEXT;

        CREATE TABLE IF NOT EXISTS local_movie_cast (
            track_uri      TEXT NOT NULL,
            order_index    INTEGER NOT NULL,
            person_id      INTEGER,
            name           TEXT NOT NULL,
            character_name TEXT,
            profile_hash   TEXT,
            PRIMARY KEY (track_uri, order_index)
        );
        CREATE INDEX IF NOT EXISTS idx_local_movie_cast_track
            ON local_movie_cast(track_uri);
        """;
}
