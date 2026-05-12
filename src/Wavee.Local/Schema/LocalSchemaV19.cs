namespace Wavee.Local.Schema;

/// <summary>
/// SQL constants for the v19 schema migration. Imported by
/// <c>Wavee.Core.Storage.MetadataDatabase</c> and run as a single
/// migration step bumping <c>PRAGMA user_version</c> from 18 to 19.
///
/// <para>v19 caches TMDB's full season roster + show summary stats so
/// the Show Detail page can render missing-episode + missing-season
/// state without re-querying TMDB on every page open. The data flows
/// from <c>LocalEnrichmentService.EnrichTvEpisodeAsync</c>: after the
/// existing <c>GetSeasonAsync</c> call returns, every entry in
/// <c>season.Episodes</c> is upserted into <c>local_series_episodes</c>
/// (not just the one the user is matching). The new
/// <c>GetTvDetailsAsync</c> call additionally fills the per-show totals.</para>
///
/// <para>Additive only. No drops on prior columns.</para>
/// </summary>
internal static class LocalSchemaV19
{
    public const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS local_series_episodes (
            series_id     TEXT    NOT NULL,
            season        INTEGER NOT NULL,
            episode       INTEGER NOT NULL,
            tmdb_id       INTEGER,
            title         TEXT,
            overview      TEXT,
            runtime_min   INTEGER,
            air_date      TEXT,
            still_hash    TEXT,
            fetched_at    INTEGER NOT NULL,
            PRIMARY KEY (series_id, season, episode)
        );
        CREATE INDEX IF NOT EXISTS idx_series_episodes_series
            ON local_series_episodes(series_id, season);

        ALTER TABLE local_series ADD COLUMN total_seasons         INTEGER;
        ALTER TABLE local_series ADD COLUMN total_episodes_tmdb   INTEGER;
        ALTER TABLE local_series ADD COLUMN tmdb_last_fetched_at  INTEGER;
        """;
}
