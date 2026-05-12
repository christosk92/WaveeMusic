namespace Wavee.Local.Schema;

/// <summary>
/// SQL constants for the v18 schema migration. Imported by
/// <c>Wavee.Core.Storage.MetadataDatabase</c> and run as a single
/// migration step bumping <c>PRAGMA user_version</c> from 17 to 18.
///
/// <para>v18 swaps the MusicBrainz-based music enrichment path for
/// Spotify search. The new columns cache the search match's URIs +
/// canonical cover URL so local-music UI surfaces can borrow Spotify's
/// rich metadata (high-res covers, album/artist click-through) without
/// re-querying every time.</para>
///
/// <para>v18 additions:</para>
/// <list type="bullet">
///   <item><c>local_files.spotify_track_uri</c> — Spotify URI of the matched track</item>
///   <item><c>local_files.spotify_album_uri</c> — canonical Spotify album URI</item>
///   <item><c>local_files.spotify_artist_uri</c> — canonical Spotify artist URI</item>
///   <item><c>local_files.spotify_cover_url</c> — HTTPS URL to the album cover (i.scdn.co)</item>
///   <item>Index on <c>spotify_track_uri</c> for de-dup queries</item>
/// </list>
///
/// <para>Additive only. The dormant <c>musicbrainz_id</c> column from v17
/// stays — schema migrations never drop columns.</para>
/// </summary>
internal static class LocalSchemaV18
{
    public const string MigrationSql = """
        ALTER TABLE local_files ADD COLUMN spotify_track_uri  TEXT NULL;
        ALTER TABLE local_files ADD COLUMN spotify_album_uri  TEXT NULL;
        ALTER TABLE local_files ADD COLUMN spotify_artist_uri TEXT NULL;
        ALTER TABLE local_files ADD COLUMN spotify_cover_url  TEXT NULL;
        CREATE INDEX IF NOT EXISTS idx_local_files_spotify_track
            ON local_files(spotify_track_uri);
        """;
}
