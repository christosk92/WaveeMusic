namespace Wavee.Local.Schema;

/// <summary>
/// SQL constants for the v21 schema migration. Mirrors v20 for the TV side —
/// surfaces full TMDB show details + principal cast on the Show Detail page.
///
/// <para>Powered by the existing <c>GetTvDetailsAsync</c> call which is
/// extended to <c>?append_to_response=credits</c> — one HTTP call per
/// show carries the new fields + cast.</para>
///
/// <para>Additive only.</para>
/// </summary>
internal static class LocalSchemaV21
{
    public const string MigrationSql = """
        ALTER TABLE local_series ADD COLUMN tagline        TEXT;
        ALTER TABLE local_series ADD COLUMN status         TEXT;
        ALTER TABLE local_series ADD COLUMN first_air_date TEXT;
        ALTER TABLE local_series ADD COLUMN last_air_date  TEXT;
        ALTER TABLE local_series ADD COLUMN genres         TEXT;
        ALTER TABLE local_series ADD COLUMN vote_average   REAL;
        ALTER TABLE local_series ADD COLUMN networks       TEXT;

        CREATE TABLE IF NOT EXISTS local_show_cast (
            series_id      TEXT NOT NULL,
            order_index    INTEGER NOT NULL,
            person_id      INTEGER,
            name           TEXT NOT NULL,
            character_name TEXT,
            profile_hash   TEXT,
            PRIMARY KEY (series_id, order_index)
        );
        CREATE INDEX IF NOT EXISTS idx_local_show_cast_series
            ON local_show_cast(series_id);
        """;
}
