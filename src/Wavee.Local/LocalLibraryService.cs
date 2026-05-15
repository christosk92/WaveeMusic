using System.Reactive.Subjects;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Local.Classification;
using Wavee.Local.Models;

namespace Wavee.Local;

[JsonSerializable(typeof(MetadataPatch))]
internal partial class LocalLibraryJsonContext : JsonSerializerContext { }

/// <summary>
/// Watched-folder management + read API for the local file library. Indexing
/// itself is delegated to <see cref="LocalFolderScanner"/>; this service owns
/// state, scheduling, and SQLite I/O.
/// </summary>
public sealed class LocalLibraryService : ILocalLibraryService, IDisposable
{
    private readonly string _connectionString;
    private readonly LocalFolderScanner _scanner;
    private readonly Subject<LocalSyncProgress> _progress = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ILogger? _logger;
    private int _scanRunning;

    public LocalLibraryService(string databasePath, LocalFolderScanner scanner, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _connectionString = b.ConnectionString;
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _logger = logger;
    }

    public IObservable<LocalSyncProgress> SyncProgress => _progress;
    public bool IsScanning => Volatile.Read(ref _scanRunning) != 0;

    private SqliteConnection OpenConnection()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public Task<IReadOnlyList<LocalLibraryFolder>> GetWatchedFoldersAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, path, enabled, include_subfolders, last_scan_at, file_count,
                   last_scan_status, last_scan_error, last_scan_duration_ms
            FROM watched_folders
            ORDER BY id;
            """;
        var list = new List<LocalLibraryFolder>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LocalLibraryFolder(
                Id: r.GetInt32(0),
                Path: r.GetString(1),
                Enabled: r.GetInt32(2) != 0,
                IncludeSubfolders: r.GetInt32(3) != 0,
                LastScanAt: r.IsDBNull(4) ? null : r.GetInt64(4),
                FileCount: r.GetInt32(5),
                LastScanStatus: r.IsDBNull(6) ? null : r.GetString(6),
                LastScanError: r.IsDBNull(7) ? null : r.GetString(7),
                LastScanDurationMs: r.IsDBNull(8) ? null : r.GetInt64(8)));
        }
        return Task.FromResult<IReadOnlyList<LocalLibraryFolder>>(list);
    }

    public async Task<LocalLibraryFolder> AddWatchedFolderAsync(string path, bool includeSubfolders = true, CancellationToken ct = default)
    {
        var normalized = LocalPath.Normalize(path);
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException($"Folder does not exist: {normalized}");

        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO watched_folders (path, enabled, include_subfolders, file_count)
                VALUES ($p, 1, $s, 0)
                ON CONFLICT(path) DO UPDATE SET include_subfolders = $s, enabled = 1;
                """;
            cmd.Parameters.AddWithValue("$p", normalized);
            cmd.Parameters.AddWithValue("$s", includeSubfolders ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        var folders = await GetWatchedFoldersAsync(ct);
        var folder = folders.First(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase));
        // Kick off a scan of just this folder in the background.
        _ = Task.Run(() => RunScanAsync(folder.Id, ct: CancellationToken.None));
        return folder;
    }

    public Task RemoveWatchedFolderAsync(int folderId, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM watched_folders WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.ExecuteNonQuery();
        }

        // Cascade prunes local_files rows; orphan entity rows are cleaned in the next scan.
        return Task.CompletedTask;
    }

    public Task SetWatchedFolderEnabledAsync(int folderId, bool enabled, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE watched_folders SET enabled = $e WHERE id = $id;";
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public async Task TriggerRescanAsync(int? folderId = null, CancellationToken ct = default)
    {
        // User-initiated rescan: force re-classification of unchanged files so
        // classifier-rule changes or per-folder hint tweaks land immediately.
        // Background / app-open scans skip this to stay fast on large libraries.
        await RunScanAsync(folderId, forceReclassify: true, ct);
    }

    /// <summary>Internal scan entry point used by AddWatchedFolderAsync, TriggerRescanAsync, hosted service.</summary>
    public async Task RunScanAsync(int? folderId, CancellationToken ct)
        => await RunScanAsync(folderId, forceReclassify: false, ct);

    /// <summary>
    /// Scan worker. <paramref name="forceReclassify"/> only true for user-initiated
    /// rescans — gates the "re-classify unchanged file" fast-path inside the scanner
    /// so 10k-track libraries don't pay regex+classifier on every background tick.
    /// </summary>
    public async Task RunScanAsync(int? folderId, bool forceReclassify, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _scanRunning, 1) == 1)
        {
            _logger?.LogDebug("Scan already in progress; skipping new request");
            return;
        }

        await _scanLock.WaitAsync(ct);
        try
        {
            var folders = await GetWatchedFoldersAsync(ct);
            foreach (var folder in folders)
            {
                if (ct.IsCancellationRequested) break;
                if (!folder.Enabled) continue;
                if (folderId.HasValue && folder.Id != folderId.Value) continue;

                try
                {
                    await _scanner.ScanFolderAsync(folder, _connectionString, _progress, forceReclassify, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Folder scan failed: {Path}", folder.Path);
                }
            }
        }
        finally
        {
            _scanLock.Release();
            Interlocked.Exchange(ref _scanRunning, 0);
        }
    }

    public Task NotifyFileChangedAsync(string path, CancellationToken ct = default)
        => _scanner.RescanSingleFileAsync(LocalPath.Normalize(path), _connectionString, ct);

    public Task NotifyFileDeletedAsync(string path, CancellationToken ct = default)
        => _scanner.RemoveSingleFileAsync(LocalPath.Normalize(path), _connectionString, ct);

    public Task<IReadOnlyList<LocalTrackRow>> GetAllTracksAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        IReadOnlyList<LocalTrackRow> rows = ReadTracks(conn, where: null, parameters: null);
        return Task.FromResult(rows);
    }

    public Task<string?> GetFilePathForTrackAsync(string trackUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM local_files WHERE track_uri = $u LIMIT 1;";
        cmd.Parameters.AddWithValue("$u", trackUri);
        var result = cmd.ExecuteScalar();
        return Task.FromResult(result as string);
    }

    public Task<LocalTrackRow?> GetTrackAsync(string trackUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var rows = ReadTracks(conn, where: "e.uri = $u",
            parameters: cmd => cmd.Parameters.AddWithValue("$u", trackUri));
        return Task.FromResult(rows.Count > 0 ? rows[0] : null);
    }

    public Task<long> AddExternalSubtitleAsync(
        string videoFilePath,
        string subtitlePath,
        string? language,
        bool forced,
        bool sdh,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();

        // De-dupe — if the user re-drops the same file (or it was already
        // discovered by the scanner) just return the existing row id.
        using (var lookup = conn.CreateCommand())
        {
            lookup.CommandText = """
                SELECT id FROM local_subtitle_files
                 WHERE local_file_path = $p AND subtitle_path = $sp
                 LIMIT 1;
                """;
            lookup.Parameters.AddWithValue("$p", videoFilePath);
            lookup.Parameters.AddWithValue("$sp", subtitlePath);
            var existing = lookup.ExecuteScalar();
            if (existing is long l) return Task.FromResult(l);
            if (existing is int i) return Task.FromResult((long)i);
        }

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO local_subtitle_files (local_file_path, subtitle_path, language, forced, sdh)
            VALUES ($p, $sp, $lang, $f, $sdh);
            SELECT last_insert_rowid();
            """;
        ins.Parameters.AddWithValue("$p", videoFilePath);
        ins.Parameters.AddWithValue("$sp", subtitlePath);
        ins.Parameters.AddWithValue("$lang", (object?)language ?? DBNull.Value);
        ins.Parameters.AddWithValue("$f", forced ? 1 : 0);
        ins.Parameters.AddWithValue("$sdh", sdh ? 1 : 0);
        var id = (long)(ins.ExecuteScalar() ?? 0L);
        return Task.FromResult(id);
    }

    public Task<LocalPlaybackMetadata?> GetPlaybackMetadataAsync(string trackUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lf.track_uri,
                   COALESCE(lf.kind_override, lf.auto_kind) AS effective_kind,
                   e.title, e.artist_name, e.album_name,
                   COALESCE(la.image_hash, '') AS art_hash,
                   e.image_url,
                   lf.series_id, s.name AS series_name,
                   lf.season_number, lf.episode_number, lf.episode_title,
                   lf.movie_year, lf.tmdb_id,
                   lf.metadata_overrides
              FROM local_files lf
              INNER JOIN entities e ON e.uri = lf.track_uri
              LEFT JOIN local_series s ON s.id = lf.series_id
              LEFT JOIN local_artwork_links la
                ON la.entity_uri = e.uri AND la.role = 'cover'
             WHERE lf.track_uri = $u
             LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$u", trackUri);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<LocalPlaybackMetadata?>(null);

        var kind = Classification.LocalContentKindExtensions.ParseWireValue(r.GetString(1));
        var hash = r.GetString(5);
        var spotifyImageUrl = r.IsDBNull(6) ? null : r.GetString(6);
        var artworkUri = !string.IsNullOrEmpty(hash)
            ? LocalArtworkCache.UriScheme + hash
            : spotifyImageUrl;
        var overrides = ParseMetadataOverrides(r.IsDBNull(14) ? null : r.GetString(14));

        return Task.FromResult<LocalPlaybackMetadata?>(new LocalPlaybackMetadata(
            TrackUri: r.GetString(0),
            Kind: kind,
            RawTitle: OverlayString(overrides?.Title, r.IsDBNull(2) ? null : r.GetString(2)),
            RawArtist: OverlayString(overrides?.Artist, r.IsDBNull(3) ? null : r.GetString(3)),
            RawAlbum: OverlayString(overrides?.Album, r.IsDBNull(4) ? null : r.GetString(4)),
            ArtworkUri: artworkUri,
            SeriesId: r.IsDBNull(7) ? null : r.GetString(7),
            SeriesName: r.IsDBNull(8) ? null : r.GetString(8),
            SeasonNumber: r.IsDBNull(9) ? null : r.GetInt32(9),
            EpisodeNumber: r.IsDBNull(10) ? null : r.GetInt32(10),
            EpisodeTitle: OverlayString(overrides?.EpisodeTitle, r.IsDBNull(11) ? null : r.GetString(11)),
            MovieYear: r.IsDBNull(12) ? null : r.GetInt32(12),
            TmdbId: r.IsDBNull(13) ? null : r.GetInt32(13)));
    }

    public Task<LocalAlbumDetail?> GetAlbumAsync(string albumUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var tracks = ReadTracks(conn, where: "e.album_uri = $a",
            parameters: cmd => cmd.Parameters.AddWithValue("$a", albumUri));
        if (tracks.Count == 0) return Task.FromResult<LocalAlbumDetail?>(null);

        var first = tracks[0];
        var artworkUri = ResolveArtwork(conn, albumUri, "cover")
                         ?? first.ArtworkUri;
        var detail = new LocalAlbumDetail(
            AlbumUri: albumUri,
            Album: first.Album ?? "Unknown Album",
            AlbumArtist: first.AlbumArtist ?? first.Artist,
            ArtistUri: first.ArtistUri,
            Year: first.Year,
            ArtworkUri: artworkUri,
            Tracks: tracks
                .OrderBy(t => t.DiscNumber ?? 1)
                .ThenBy(t => t.TrackNumber ?? 0)
                .ToList());
        return Task.FromResult<LocalAlbumDetail?>(detail);
    }

    public Task<LocalArtistDetail?> GetArtistAsync(string artistUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();

        // Pull all tracks attributed to this artist (album_uri may differ; we project albums from tracks).
        var tracks = ReadTracks(conn, where: """
            EXISTS (SELECT 1 FROM entities a WHERE a.uri = e.album_uri AND a.entity_type = 2)
            AND (
              SELECT artist_name FROM entities WHERE uri = $a
            ) IS NOT NULL
            """, parameters: cmd => cmd.Parameters.AddWithValue("$a", artistUri));

        // The above is conservative — refine: tracks whose ArtistUri matches.
        tracks = tracks.Where(t => string.Equals(t.ArtistUri, artistUri, StringComparison.Ordinal)).ToList();

        if (tracks.Count == 0)
        {
            // Fall back: read the artist entity by URI; return empty discography if it exists.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT title FROM entities WHERE uri = $a AND entity_type = 3 LIMIT 1;";
            cmd.Parameters.AddWithValue("$a", artistUri);
            var name = cmd.ExecuteScalar() as string;
            if (name == null) return Task.FromResult<LocalArtistDetail?>(null);
            return Task.FromResult<LocalArtistDetail?>(new LocalArtistDetail(
                artistUri, name, ArtworkUri: null,
                Albums: Array.Empty<LocalAlbumSummary>(),
                AllTracks: Array.Empty<LocalTrackRow>()));
        }

        var albums = tracks
            .Where(t => t.AlbumUri != null)
            .GroupBy(t => t.AlbumUri!)
            .Select(g => new LocalAlbumSummary(
                AlbumUri: g.Key,
                Album: g.First().Album ?? "Unknown Album",
                Year: g.Max(x => x.Year),
                TrackCount: g.Count(),
                ArtworkUri: g.First().ArtworkUri))
            .OrderByDescending(a => a.Year ?? 0)
            .ThenBy(a => a.Album)
            .ToList();

        var artistName = tracks[0].AlbumArtist ?? tracks[0].Artist ?? "Unknown Artist";
        var artistArt = ResolveArtwork(conn, artistUri, "artist") ?? albums.FirstOrDefault()?.ArtworkUri;

        return Task.FromResult<LocalArtistDetail?>(new LocalArtistDetail(
            artistUri, artistName, artistArt, albums, tracks));
    }

    public Task<IReadOnlyList<LocalSearchResult>> SearchAsync(
        string query,
        int limit = 20,
        LocalSearchScope scope = LocalSearchScope.LocalFilesOnly,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<LocalSearchResult>>(Array.Empty<LocalSearchResult>());

        var like = "%" + query + "%";
        var results = new List<LocalSearchResult>(limit * 2);

        using var conn = OpenConnection();

        // Leg 1 — `entities` table. Holds local filesystem metadata + Spotify
        // entities when the user has NO metadata locale configured.
        var entityTypeFilter = scope == LocalSearchScope.AllCached
            ? "AND e.entity_type IN (1, 2, 3, 6)"
            : "AND e.entity_type IN (1, 2, 3)";

        var sourceFilter = scope == LocalSearchScope.AllCached
            ? string.Empty
            : "AND e.source_type = 1";

        QueryEntitiesTable(conn, "entities", like, entityTypeFilter, sourceFilter, limit, results, ct);

        // Leg 2 — `localized_entities` table. Spotify metadata gets written here
        // (keyed by locale) when MetadataDatabase.HasLocalizedSpotifyMetadata is
        // true — which is the default when a Spotify metadata language is set.
        // Skipped for LocalFilesOnly since local files don't live in this table.
        if (scope == LocalSearchScope.AllCached)
        {
            QueryEntitiesTable(conn, "localized_entities", like,
                "AND e.entity_type IN (1, 2, 3, 6)",
                sourceFilter: string.Empty,
                limit, results, ct);
        }

        // De-dup by URI (the same entity may appear in both `entities` and
        // `localized_entities` rows for different locales).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<LocalSearchResult>(limit);
        foreach (var item in results)
        {
            if (!seen.Add(item.Uri)) continue;
            unique.Add(item);
            if (unique.Count >= limit) break;
        }
        return Task.FromResult<IReadOnlyList<LocalSearchResult>>(unique);
    }

    private static void QueryEntitiesTable(
        SqliteConnection conn,
        string tableName,
        string like,
        string entityTypeFilter,
        string sourceFilter,
        int limit,
        List<LocalSearchResult> results,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.uri, e.entity_type, e.title, e.artist_name,
                   e.album_name, e.publisher, e.description,
                   COALESCE(la.image_hash, '') AS art_hash,
                   e.image_url
            FROM {tableName} e
            LEFT JOIN local_artwork_links la
              ON la.entity_uri = e.uri AND la.role = 'cover'
            WHERE 1=1
              {sourceFilter}
              {entityTypeFilter}
              AND (e.title LIKE $q OR e.artist_name LIKE $q OR e.album_name LIKE $q OR e.publisher LIKE $q)
            ORDER BY
              CASE e.entity_type WHEN 1 THEN 1 WHEN 6 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 4 ELSE 9 END,
              e.title
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$q", like);
        cmd.Parameters.AddWithValue("$lim", limit * 2);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var uri = r.GetString(0);
            var entityType = r.GetInt32(1);
            var title = r.IsDBNull(2) ? null : r.GetString(2);
            var artist = r.IsDBNull(3) ? null : r.GetString(3);
            var album = r.IsDBNull(4) ? null : r.GetString(4);
            var publisher = r.IsDBNull(5) ? null : r.GetString(5);
            var description = r.IsDBNull(6) ? null : r.GetString(6);
            var hash = r.GetString(7);
            var spotifyImageUrl = r.IsDBNull(8) ? null : r.GetString(8);

            var art = !string.IsNullOrEmpty(hash)
                ? LocalArtworkCache.UriScheme + hash
                : spotifyImageUrl;

            results.Add(MapEntityRow(entityType, uri, title, artist, album, publisher, description, art));
        }
    }

    private static LocalSearchResult MapEntityRow(
        int entityType,
        string uri,
        string? title,
        string? artist,
        string? album,
        string? publisher,
        string? description,
        string? art)
    {
        return entityType switch
        {
            1 => new LocalSearchResult(LocalSearchEntityType.Track, uri, title ?? uri, artist, art),
            2 => new LocalSearchResult(LocalSearchEntityType.Album, uri, album ?? title ?? uri, artist, art),
            3 => new LocalSearchResult(LocalSearchEntityType.Artist, uri, artist ?? title ?? uri, null, art),
            6 => new LocalSearchResult(
                    LocalSearchEntityType.Playlist,
                    uri,
                    title ?? uri,
                    !string.IsNullOrEmpty(publisher) ? publisher
                        : !string.IsNullOrEmpty(artist) ? artist
                        : !string.IsNullOrEmpty(description) ? description
                        : "Playlist",
                    art),
            _ => new LocalSearchResult(LocalSearchEntityType.Track, uri, title ?? uri, artist, art),
        };
    }

    private List<LocalTrackRow> ReadTracks(SqliteConnection conn, string? where, Action<SqliteCommand>? parameters,
        string? orderByOverride = null, int? limit = null)
    {
        using var cmd = conn.CreateCommand();
        var whereClause = where != null ? $"AND ({where})" : string.Empty;
        var orderClause = orderByOverride ?? "e.artist_name, e.album_name, e.disc_number, e.track_number, e.title";
        var limitClause = limit is { } l ? $" LIMIT {l}" : string.Empty;
        cmd.CommandText = $"""
            SELECT e.uri, lf.path, e.title, e.artist_name, e.album_name, e.album_uri,
                   e.duration_ms, e.track_number, e.disc_number, e.release_year,
                   COALESCE(la.image_hash, '') AS art_hash,
                   (SELECT artist_name FROM entities a WHERE a.uri = e.uri) AS album_artist,
                   (SELECT uri FROM entities x WHERE x.entity_type = 3 AND x.title = e.artist_name AND x.source_type = 1 LIMIT 1) AS artist_uri,
                   lf.is_video,
                   lf.last_position_ms,
                   lf.spotify_track_uri, lf.spotify_album_uri, lf.spotify_artist_uri, lf.spotify_cover_url,
                   lf.metadata_overrides
            FROM entities e
            INNER JOIN local_files lf ON lf.track_uri = e.uri
            LEFT JOIN local_artwork_links la
              ON la.entity_uri = e.uri AND la.role = 'cover'
            WHERE e.source_type = 1 AND e.entity_type = 1
            {whereClause}
            ORDER BY {orderClause}{limitClause};
            """;
        parameters?.Invoke(cmd);

        var list = new List<LocalTrackRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var uri = r.GetString(0);
            var path = r.GetString(1);
            var rawTitle = r.IsDBNull(2) ? null : r.GetString(2);
            var rawArtist = r.IsDBNull(3) ? null : r.GetString(3);
            var rawAlbum = r.IsDBNull(4) ? null : r.GetString(4);
            var albumUri = r.IsDBNull(5) ? null : r.GetString(5);
            var duration = r.IsDBNull(6) ? 0L : r.GetInt64(6);
            var rawTrackNo = r.IsDBNull(7) ? (int?)null : r.GetInt32(7);
            var rawDiscNo = r.IsDBNull(8) ? (int?)null : r.GetInt32(8);
            var rawYear = r.IsDBNull(9) ? (int?)null : r.GetInt32(9);
            var artHash = r.GetString(10);
            var artworkUri = string.IsNullOrEmpty(artHash) ? null : LocalArtworkCache.UriScheme + artHash;
            var rawAlbumArtist = r.IsDBNull(11) ? null : r.GetString(11);
            var artistUri = r.IsDBNull(12) ? null : r.GetString(12);
            var isVideo = !r.IsDBNull(13) && r.GetInt32(13) != 0;
            var lastPositionMs = r.IsDBNull(14) ? 0L : r.GetInt64(14);
            var spotifyTrackUri = r.IsDBNull(15) ? null : r.GetString(15);
            var spotifyAlbumUri = r.IsDBNull(16) ? null : r.GetString(16);
            var spotifyArtistUri = r.IsDBNull(17) ? null : r.GetString(17);
            var spotifyCoverUrl = r.IsDBNull(18) ? null : r.GetString(18);
            var overrides = ParseMetadataOverrides(r.IsDBNull(19) ? null : r.GetString(19));

            var title = OverlayString(overrides?.Title, rawTitle);
            var artist = OverlayString(overrides?.Artist, rawArtist);
            var album = OverlayString(overrides?.Album, rawAlbum);
            var albumArtist = OverlayString(overrides?.AlbumArtist, rawAlbumArtist);
            var year = OverlayValue(overrides?.Year, rawYear);
            var trackNo = OverlayValue(overrides?.TrackNumber, rawTrackNo);
            var discNo = OverlayValue(overrides?.DiscNumber, rawDiscNo);

            list.Add(new LocalTrackRow(uri, path, title, artist, albumArtist, album, albumUri, artistUri,
                duration, trackNo, discNo, year, artworkUri, isVideo, lastPositionMs)
            {
                SpotifyTrackUri = spotifyTrackUri,
                SpotifyAlbumUri = spotifyAlbumUri,
                SpotifyArtistUri = spotifyArtistUri,
                SpotifyCoverUrl = spotifyCoverUrl,
            });
        }
        return list;
    }

    private static string? ResolveArtwork(SqliteConnection conn, string entityUri, string role)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT image_hash FROM local_artwork_links WHERE entity_uri = $u AND role = $r LIMIT 1;";
        cmd.Parameters.AddWithValue("$u", entityUri);
        cmd.Parameters.AddWithValue("$r", role);
        var hash = cmd.ExecuteScalar() as string;
        return string.IsNullOrEmpty(hash) ? null : LocalArtworkCache.UriScheme + hash;
    }

    // ============================================================================
    // v17 query API — classification-aware reads. Used by ILocalLibraryFacade.
    // ============================================================================

    /// <summary>Logs the kind-distribution after each scan or query.
    /// Cheap COUNT(*) GROUP BY auto_kind — useful for debugging why a rail is empty.</summary>
    public void LogKindDistribution()
    {
        if (_logger is null) return;
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(kind_override, auto_kind) AS k, COUNT(*) AS n
                FROM local_files
                GROUP BY k
                ORDER BY n DESC;
                """;
            var sb = new System.Text.StringBuilder("[lib] kind distribution:");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                sb.Append(' ').Append(r.GetString(0)).Append('=').Append(r.GetInt32(1));
            _logger.LogInformation("{Line}", sb.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LogKindDistribution failed");
        }
    }

    /// <summary>Lists all local TV shows with rolled-up episode/season counts.</summary>
    public Task<IReadOnlyList<Models.LocalShow>> GetShowsAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.name, s.tmdb_id, s.overview,
                   s.poster_hash, s.backdrop_hash,
                   COUNT(DISTINCT lf.season_number) AS season_count,
                   COUNT(lf.path)                    AS ep_count,
                   SUM(CASE WHEN lf.watched_at IS NULL THEN 1 ELSE 0 END) AS unwatched,
                   MAX(lf.watched_at)               AS last_watched
            FROM local_series s
            LEFT JOIN local_files lf ON lf.series_id = s.id
            GROUP BY s.id
            HAVING ep_count > 0
            ORDER BY s.name;
            """;
        var list = new List<Models.LocalShow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var posterHash = r.IsDBNull(4) ? null : r.GetString(4);
            var backdropHash = r.IsDBNull(5) ? null : r.GetString(5);
            list.Add(new Models.LocalShow(
                Id: r.GetString(0),
                Name: r.GetString(1),
                TmdbId: r.IsDBNull(2) ? null : r.GetInt32(2),
                Overview: r.IsDBNull(3) ? null : r.GetString(3),
                PosterArtworkUri: HashToArtUri(posterHash),
                BackdropArtworkUri: HashToArtUri(backdropHash),
                SeasonCount: r.GetInt32(6),
                EpisodeCount: r.GetInt32(7),
                UnwatchedCount: r.GetInt32(8),
                LastWatchedAt: r.IsDBNull(9) ? null : r.GetInt64(9)));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalShow>>(list);
    }

    /// <summary>Returns the full show detail: all seasons + episodes.</summary>
    public Task<Models.LocalShow?> GetShowAsync(string showId, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.name, s.tmdb_id, s.overview, s.poster_hash, s.backdrop_hash,
                   COUNT(DISTINCT lf.season_number),
                   COUNT(lf.path),
                   SUM(CASE WHEN lf.watched_at IS NULL THEN 1 ELSE 0 END),
                   MAX(lf.watched_at),
                   s.total_seasons,
                   s.total_episodes_tmdb,
                   s.tagline, s.status, s.first_air_date, s.last_air_date,
                   s.genres, s.vote_average, s.networks
            FROM local_series s
            LEFT JOIN local_files lf ON lf.series_id = s.id
            WHERE s.id = $id
            GROUP BY s.id;
            """;
        cmd.Parameters.AddWithValue("$id", showId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<Models.LocalShow?>(null);
        var posterHash = r.IsDBNull(4) ? null : r.GetString(4);
        var backdropHash = r.IsDBNull(5) ? null : r.GetString(5);
        var genresCsv = r.IsDBNull(16) ? null : r.GetString(16);
        var networksCsv = r.IsDBNull(18) ? null : r.GetString(18);
        return Task.FromResult<Models.LocalShow?>(new Models.LocalShow(
            Id: r.GetString(0),
            Name: r.GetString(1),
            TmdbId: r.IsDBNull(2) ? null : r.GetInt32(2),
            Overview: r.IsDBNull(3) ? null : r.GetString(3),
            PosterArtworkUri: HashToArtUri(posterHash),
            BackdropArtworkUri: HashToArtUri(backdropHash),
            SeasonCount: r.GetInt32(6),
            EpisodeCount: r.GetInt32(7),
            UnwatchedCount: r.GetInt32(8),
            LastWatchedAt: r.IsDBNull(9) ? null : r.GetInt64(9))
        {
            TotalSeasonsExpected = r.IsDBNull(10) ? null : r.GetInt32(10),
            TotalEpisodesExpected = r.IsDBNull(11) ? null : r.GetInt32(11),
            Tagline = r.IsDBNull(12) ? null : r.GetString(12),
            Status = r.IsDBNull(13) ? null : r.GetString(13),
            FirstAirDate = r.IsDBNull(14) ? null : r.GetString(14),
            LastAirDate = r.IsDBNull(15) ? null : r.GetString(15),
            Genres = string.IsNullOrEmpty(genresCsv)
                ? null
                : genresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            VoteAverage = r.IsDBNull(17) ? null : r.GetDouble(17),
            Networks = string.IsNullOrEmpty(networksCsv)
                ? null
                : networksCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        });
    }

    /// <summary>
    /// Returns episodes of a show grouped by season. After Continuation 8
    /// the roster (<c>local_series_episodes</c>) is the spine — every
    /// episode TMDB knows about gets a row, with <c>local_files</c>
    /// LEFT JOINed by <c>(series_id, season, episode)</c> to fill on-disk
    /// presence. <c>LocalEpisode.IsOnDisk</c> is true iff the matching
    /// file exists; missing-from-disk entries render as gray placeholders.
    ///
    /// <para>Falls back to local-files-only mode for shows that haven't
    /// been TMDB-enriched yet (no roster rows) so first-time users still
    /// see something before they hit Sync.</para>
    /// </summary>
    public Task<IReadOnlyList<Models.LocalSeason>> GetShowSeasonsAsync(string showId, CancellationToken ct = default)
    {
        using var conn = OpenConnection();

        // Is the roster populated for this show? If not, fall back to the
        // on-disk-only path (matches pre-Continuation-8 behaviour for shows
        // that have never been enriched).
        long rosterCount;
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM local_series_episodes WHERE series_id = $id;";
            probe.Parameters.AddWithValue("$id", showId);
            rosterCount = (long)(probe.ExecuteScalar() ?? 0L);
        }
        if (rosterCount == 0)
            return GetShowSeasonsFromFilesOnlyAsync(conn, showId, ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rse.season, rse.episode,
                   COALESCE(lf.episode_title, rse.title) AS title,
                   rse.overview,
                   lf.path, lf.track_uri,
                   COALESCE(e.duration_ms, rse.runtime_min * 60000) AS duration_ms,
                   COALESCE(lf.last_position_ms, 0) AS last_position_ms,
                   lf.watched_at,
                   COALESCE(la.image_hash, rse.still_hash, '') AS art_hash,
                   CASE WHEN lf.path IS NOT NULL THEN
                        (SELECT COUNT(*) FROM local_subtitle_files WHERE local_file_path = lf.path)
                      + (SELECT COUNT(*) FROM local_embedded_tracks WHERE local_file_path = lf.path AND kind = 'subtitle')
                   ELSE 0 END AS sub_count,
                   CASE WHEN lf.path IS NOT NULL THEN
                        (SELECT COUNT(*) FROM local_embedded_tracks WHERE local_file_path = lf.path AND kind = 'audio')
                   ELSE 0 END AS audio_count,
                   COALESCE(lf.tmdb_id, rse.tmdb_id) AS tmdb_id,
                   (lf.path IS NOT NULL) AS is_on_disk,
                   lf.metadata_overrides
            FROM local_series_episodes rse
            LEFT JOIN local_files lf
                ON lf.series_id = rse.series_id
               AND lf.season_number = rse.season
               AND lf.episode_number = rse.episode
            LEFT JOIN entities e ON e.uri = lf.track_uri
            LEFT JOIN local_artwork_links la ON la.entity_uri = lf.track_uri AND la.role = 'cover'
            WHERE rse.series_id = $id
            ORDER BY rse.season, rse.episode;
            """;
        cmd.Parameters.AddWithValue("$id", showId);
        var seasons = new Dictionary<int, List<Models.LocalEpisode>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var season = r.GetInt32(0);
            var artHash = r.GetString(9);
            var isOnDisk = r.GetInt32(13) != 0;
            var overrides = ParseMetadataOverrides(r.IsDBNull(14) ? null : r.GetString(14));
            var rawTitle = r.IsDBNull(2) ? null : r.GetString(2);
            var ep = new Models.LocalEpisode(
                TrackUri: r.IsDBNull(5) ? null : r.GetString(5),
                FilePath: r.IsDBNull(4) ? null : r.GetString(4),
                ShowId: showId,
                Season: season,
                Episode: r.GetInt32(1),
                Title: OverlayString(overrides?.EpisodeTitle, rawTitle),
                DurationMs: r.IsDBNull(6) ? 0L : r.GetInt64(6),
                LastPositionMs: r.IsDBNull(7) ? 0L : r.GetInt64(7),
                WatchedAt: r.IsDBNull(8) ? null : r.GetInt64(8),
                StillImageUri: HashToArtUri(artHash),
                SubtitleCount: r.IsDBNull(10) ? 0 : r.GetInt32(10),
                AudioTrackCount: r.IsDBNull(11) ? 0 : r.GetInt32(11),
                TmdbId: r.IsDBNull(12) ? null : r.GetInt32(12))
            {
                Overview = r.IsDBNull(3) ? null : r.GetString(3),
                IsOnDisk = isOnDisk,
            };
            if (!seasons.TryGetValue(season, out var list)) seasons[season] = list = new();
            list.Add(ep);
        }
        var result = seasons
            .OrderBy(kv => kv.Key)
            .Select(kv => new Models.LocalSeason(showId, kv.Key, kv.Value.Count, kv.Value))
            .ToList();
        return Task.FromResult<IReadOnlyList<Models.LocalSeason>>(result);
    }

    /// <summary>
    /// Pre-Continuation-8 fallback: builds the season tree purely from
    /// <c>local_files</c> when no roster rows exist for the show yet.
    /// Every returned episode has <c>IsOnDisk=true</c>.
    /// </summary>
    private Task<IReadOnlyList<Models.LocalSeason>> GetShowSeasonsFromFilesOnlyAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn, string showId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lf.season_number, lf.episode_number, lf.episode_title, lf.path,
                   lf.track_uri, e.duration_ms, lf.last_position_ms, lf.watched_at,
                   COALESCE(la.image_hash, '') AS art_hash,
                   (SELECT COUNT(*) FROM local_subtitle_files WHERE local_file_path = lf.path)
                 + (SELECT COUNT(*) FROM local_embedded_tracks WHERE local_file_path = lf.path AND kind = 'subtitle') AS sub_count,
                   (SELECT COUNT(*) FROM local_embedded_tracks WHERE local_file_path = lf.path AND kind = 'audio') AS audio_count,
                   lf.tmdb_id,
                   lf.metadata_overrides
            FROM local_files lf
            INNER JOIN entities e ON e.uri = lf.track_uri
            LEFT JOIN local_artwork_links la ON la.entity_uri = lf.track_uri AND la.role = 'cover'
            WHERE lf.series_id = $id
            ORDER BY lf.season_number, lf.episode_number;
            """;
        cmd.Parameters.AddWithValue("$id", showId);
        var seasons = new Dictionary<int, List<Models.LocalEpisode>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var season = r.IsDBNull(0) ? 1 : r.GetInt32(0);
            var artHash = r.GetString(8);
            var overrides = ParseMetadataOverrides(r.IsDBNull(12) ? null : r.GetString(12));
            var rawTitle = r.IsDBNull(2) ? null : r.GetString(2);
            var ep = new Models.LocalEpisode(
                TrackUri: r.GetString(4),
                FilePath: r.GetString(3),
                ShowId: showId,
                Season: season,
                Episode: r.IsDBNull(1) ? 0 : r.GetInt32(1),
                Title: OverlayString(overrides?.EpisodeTitle, rawTitle),
                DurationMs: r.IsDBNull(5) ? 0L : r.GetInt64(5),
                LastPositionMs: r.GetInt64(6),
                WatchedAt: r.IsDBNull(7) ? null : r.GetInt64(7),
                StillImageUri: HashToArtUri(artHash),
                SubtitleCount: r.GetInt32(9),
                AudioTrackCount: r.GetInt32(10),
                TmdbId: r.IsDBNull(11) ? null : r.GetInt32(11))
            {
                IsOnDisk = true,
            };
            if (!seasons.TryGetValue(season, out var list)) seasons[season] = list = new();
            list.Add(ep);
        }
        var result = seasons
            .OrderBy(kv => kv.Key)
            .Select(kv => new Models.LocalSeason(showId, kv.Key, kv.Value.Count, kv.Value))
            .ToList();
        return Task.FromResult<IReadOnlyList<Models.LocalSeason>>(result);
    }

    /// <summary>Lists all local movies, newest-watched first.</summary>
    public Task<IReadOnlyList<Models.LocalMovie>> GetMoviesAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var rows = ReadMovies(conn, where: null, parameters: null);
        return Task.FromResult(rows);
    }

    /// <summary>Detail of one movie by URI.</summary>
    public Task<Models.LocalMovie?> GetMovieAsync(string trackUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var rows = ReadMovies(conn, where: "lf.track_uri = $u",
            parameters: cmd => cmd.Parameters.AddWithValue("$u", trackUri));
        return Task.FromResult(rows.Count > 0 ? rows[0] : null);
    }

    /// <summary>Lists all local music videos.</summary>
    public Task<IReadOnlyList<Models.LocalMusicVideo>> GetMusicVideosAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = MusicVideoSelectSql + """
            WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'MusicVideo'
            ORDER BY lf.last_indexed_at DESC;
            """;
        var list = new List<Models.LocalMusicVideo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadMusicVideo(r));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalMusicVideo>>(list);
    }

    public Task<Models.LocalMusicVideo?> GetMusicVideoAsync(string trackUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = MusicVideoSelectSql + """
            WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'MusicVideo'
              AND lf.track_uri = $u
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$u", trackUri);
        using var r = cmd.ExecuteReader();
        return Task.FromResult(r.Read() ? ReadMusicVideo(r) : null);
    }

    public Task<Models.LocalMusicVideo?> GetLinkedMusicVideoForSpotifyTrackAsync(
        string spotifyTrackUri,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = MusicVideoSelectSql + """
            WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'MusicVideo'
              AND lf.spotify_track_uri = $u
            ORDER BY lf.last_indexed_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$u", spotifyTrackUri);
        using var r = cmd.ExecuteReader();
        return Task.FromResult(r.Read() ? ReadMusicVideo(r) : null);
    }

    public Task<IReadOnlyDictionary<string, string>> GetLinkedMusicVideoUrisForSpotifyTracksAsync(
        IEnumerable<string> spotifyTrackUris,
        CancellationToken ct = default)
    {
        var uris = spotifyTrackUris
            .Where(IsSpotifyTrackUri)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uris.Length == 0)
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.Ordinal));

        using var conn = OpenConnection();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var batch in uris.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = conn.CreateCommand();
            var parameterNames = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                var name = "$u" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameterNames[i] = name;
                cmd.Parameters.AddWithValue(name, batch[i]);
            }

            cmd.CommandText = $"""
                SELECT lf.spotify_track_uri, lf.track_uri
                  FROM local_files lf
                 WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'MusicVideo'
                   AND lf.spotify_track_uri IN ({string.Join(", ", parameterNames)})
                 ORDER BY lf.last_indexed_at DESC;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var spotifyUri = r.GetString(0);
                if (result.ContainsKey(spotifyUri)) continue;
                result[spotifyUri] = r.GetString(1);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    public Task LinkMusicVideoToSpotifyTrackAsync(
        string localMusicVideoTrackUri,
        string spotifyTrackUri,
        CancellationToken ct = default)
    {
        if (!LocalUri.IsTrack(localMusicVideoTrackUri))
            throw new ArgumentException("Expected a wavee:local:track:* music-video URI.", nameof(localMusicVideoTrackUri));
        if (!IsSpotifyTrackUri(spotifyTrackUri))
            throw new ArgumentException("Expected a spotify:track:* URI.", nameof(spotifyTrackUri));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_files
               SET spotify_track_uri = $spotify,
                   enrichment_state = 'Matched',
                   enrichment_at = $now
             WHERE track_uri = $local
               AND COALESCE(kind_override, auto_kind) = 'MusicVideo';
            """;
        cmd.Parameters.AddWithValue("$spotify", spotifyTrackUri);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$local", localMusicVideoTrackUri);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            throw new InvalidOperationException("The selected local item is not an indexed music video.");
        return Task.CompletedTask;
    }

    public Task UnlinkMusicVideoFromSpotifyTrackAsync(string localMusicVideoTrackUri, CancellationToken ct = default)
    {
        if (!LocalUri.IsTrack(localMusicVideoTrackUri))
            throw new ArgumentException("Expected a wavee:local:track:* music-video URI.", nameof(localMusicVideoTrackUri));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_files
               SET spotify_track_uri = NULL,
                   enrichment_at = $now
             WHERE track_uri = $local
               AND COALESCE(kind_override, auto_kind) = 'MusicVideo';
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$local", localMusicVideoTrackUri);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private const string MusicVideoSelectSql = """
        SELECT lf.track_uri, lf.path, e.title, e.artist_name, e.release_year,
               e.duration_ms,
               COALESCE(la.image_hash, '') AS art_hash,
               lf.spotify_track_uri,
               lf.metadata_overrides
          FROM local_files lf
          INNER JOIN entities e ON e.uri = lf.track_uri
          LEFT JOIN local_artwork_links la ON la.entity_uri = lf.track_uri AND la.role = 'cover'
        """;

    private static Models.LocalMusicVideo ReadMusicVideo(SqliteDataReader r)
    {
        var path = r.GetString(1);
        var artHash = r.GetString(6);
        var overrides = ParseMetadataOverrides(r.IsDBNull(8) ? null : r.GetString(8));

        var baseTitle = r.IsDBNull(2) ? Path.GetFileNameWithoutExtension(path) : r.GetString(2);
        var baseArtist = r.IsDBNull(3) ? null : r.GetString(3);
        var baseYear = r.IsDBNull(4) ? (int?)null : r.GetInt32(4);

        return new Models.LocalMusicVideo(
            TrackUri: r.GetString(0),
            FilePath: path,
            Title: OverlayString(overrides?.Title, baseTitle) ?? baseTitle,
            Artist: OverlayString(overrides?.Artist, baseArtist),
            Year: OverlayValue(overrides?.Year, baseYear),
            DurationMs: r.IsDBNull(5) ? 0L : r.GetInt64(5),
            ThumbnailUri: HashToArtUri(artHash))
        {
            LinkedSpotifyTrackUri = r.IsDBNull(7) ? null : r.GetString(7)
        };
    }

    private static Models.MetadataPatch? ParseMetadataOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(json, LocalLibraryJsonContext.Default.MetadataPatch);
        }
        catch
        {
            // Malformed JSON shouldn't blow up the whole music-video query.
            return null;
        }
    }

    // Field-by-field "non-null override wins" merge used by every row builder
    // that consumes a MetadataPatch. Lifted out so we don't replicate the
    // ternaries across ReadTracks / ReadMovies / ReadMusicVideo / episode /
    // continue-watching / playback-metadata.
    private static string? OverlayString(string? @override, string? baseValue)
        => string.IsNullOrWhiteSpace(@override) ? baseValue : @override;

    private static T? OverlayValue<T>(T? @override, T? baseValue) where T : struct
        => @override.HasValue ? @override : baseValue;

    private static bool IsSpotifyTrackUri(string? uri)
        => !string.IsNullOrWhiteSpace(uri)
           && uri.StartsWith("spotify:track:", StringComparison.Ordinal)
           && uri.Length > "spotify:track:".Length;

    /// <summary>Lists unclassified ("Other") items.</summary>
    public Task<IReadOnlyList<Models.LocalOtherItem>> GetOthersAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lf.track_uri, lf.path, e.title, e.duration_ms, lf.file_size,
                   lf.auto_kind, lf.kind_override, lf.metadata_overrides
            FROM local_files lf
            INNER JOIN entities e ON e.uri = lf.track_uri
            WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'Other'
            ORDER BY lf.last_indexed_at DESC;
            """;
        var list = new List<Models.LocalOtherItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var path = r.GetString(1);
            var overrides = ParseMetadataOverrides(r.IsDBNull(7) ? null : r.GetString(7));
            var rawTitle = r.IsDBNull(2) ? Path.GetFileNameWithoutExtension(path) : r.GetString(2);
            list.Add(new Models.LocalOtherItem(
                TrackUri: r.GetString(0),
                FilePath: path,
                DisplayName: OverlayString(overrides?.Title, rawTitle) ?? rawTitle,
                DurationMs: r.IsDBNull(3) ? 0L : r.GetInt64(3),
                FileSize: r.GetInt64(4),
                Extension: Path.GetExtension(path),
                AutoKind: Classification.LocalContentKindExtensions.ParseWireValue(r.GetString(5)),
                KindOverride: r.IsDBNull(6) ? null : Classification.LocalContentKindExtensions.ParseWireValue(r.GetString(6))));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalOtherItem>>(list);
    }

    /// <summary>Rows with 0 &lt; last_position_ms &lt; 0.9 × duration_ms — paused playback.</summary>
    public Task<IReadOnlyList<Models.LocalContinueItem>> GetContinueWatchingAsync(int limit = 20, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lf.track_uri, lf.path, COALESCE(e.title, ''), e.duration_ms,
                   lf.last_position_ms, lf.last_indexed_at,
                   COALESCE(la.image_hash, '') AS art_hash,
                   COALESCE(lf.kind_override, lf.auto_kind) AS effective_kind,
                   lf.metadata_overrides
            FROM local_files lf
            INNER JOIN entities e ON e.uri = lf.track_uri
            LEFT JOIN local_artwork_links la ON la.entity_uri = lf.track_uri AND la.role = 'cover'
            WHERE lf.last_position_ms > 0
              AND (e.duration_ms = 0 OR lf.last_position_ms < (e.duration_ms * 9 / 10))
              AND lf.watched_at IS NULL
            ORDER BY lf.last_indexed_at DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<Models.LocalContinueItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var artHash = r.GetString(6);
            var path = r.GetString(1);
            var overrides = ParseMetadataOverrides(r.IsDBNull(8) ? null : r.GetString(8));
            var rawTitle = r.GetString(2);
            var baseName = string.IsNullOrEmpty(rawTitle) ? Path.GetFileNameWithoutExtension(path) : rawTitle;
            list.Add(new Models.LocalContinueItem(
                TrackUri: r.GetString(0),
                FilePath: path,
                DisplayName: OverlayString(overrides?.Title, baseName) ?? baseName,
                DurationMs: r.IsDBNull(3) ? 0L : r.GetInt64(3),
                LastPositionMs: r.GetInt64(4),
                PlayedAt: r.GetInt64(5),
                ArtworkUri: HashToArtUri(artHash),
                Kind: Classification.LocalContentKindExtensions.ParseWireValue(r.GetString(7))));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalContinueItem>>(list);
    }

    /// <summary>Recently-added (by indexing time). Limit kept conservative for rail use.</summary>
    public Task<IReadOnlyList<LocalTrackRow>> GetRecentlyAddedAsync(int limit = 30, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var tracks = ReadTracks(conn, where: null, parameters: null,
            orderByOverride: "lf.last_indexed_at DESC", limit: limit);
        return Task.FromResult<IReadOnlyList<LocalTrackRow>>(tracks);
    }

    /// <summary>
    /// Music-only tracks (effective kind = Music). Drives the Music drill-in
    /// page; recently-indexed first.
    /// </summary>
    public Task<IReadOnlyList<LocalTrackRow>> GetMusicTracksAsync(int limit = 500, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var tracks = ReadTracks(conn,
            where: "COALESCE(lf.kind_override, lf.auto_kind) = 'Music'",
            parameters: null,
            orderByOverride: "lf.last_indexed_at DESC",
            limit: limit);
        _logger?.LogDebug("[lib] GetMusicTracksAsync → {Count} rows", tracks.Count);
        return Task.FromResult<IReadOnlyList<LocalTrackRow>>(tracks);
    }

    /// <summary>Recently-played local tracks, dedup-by-URI, newest first.</summary>
    public Task<IReadOnlyList<LocalTrackRow>> GetRecentlyPlayedAsync(int limit = 30, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT track_uri FROM (
                SELECT track_uri, MAX(played_at) AS pa
                FROM local_plays
                GROUP BY track_uri
            )
            ORDER BY pa DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var uris = new List<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) uris.Add(r.GetString(0));

        var list = new List<LocalTrackRow>(uris.Count);
        foreach (var uri in uris)
        {
            var rows = ReadTracks(conn, where: "e.uri = $u",
                parameters: pcmd => pcmd.Parameters.AddWithValue("$u", uri));
            if (rows.Count > 0) list.Add(rows[0]);
        }
        return Task.FromResult<IReadOnlyList<LocalTrackRow>>(list);
    }

    /// <summary>All liked local tracks (entities.is_locally_liked = 1).</summary>
    public Task<IReadOnlyList<LocalTrackRow>> GetLikedTracksAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var rows = ReadTracks(conn, where: "e.is_locally_liked = 1", parameters: null);
        return Task.FromResult<IReadOnlyList<LocalTrackRow>>(rows);
    }

    /// <summary>Subtitles + embedded subtitle tracks for one video, merged.</summary>
    public Task<IReadOnlyList<Models.LocalSubtitle>> GetSubtitlesForAsync(string filePath, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var list = new List<Models.LocalSubtitle>();
        // External
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, subtitle_path, language, forced, sdh FROM local_subtitle_files WHERE local_file_path = $p ORDER BY language;";
            cmd.Parameters.AddWithValue("$p", filePath);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Models.LocalSubtitle(
                    Id: r.GetInt64(0),
                    Path: r.GetString(1),
                    Language: r.IsDBNull(2) ? null : r.GetString(2),
                    Forced: r.GetInt32(3) != 0,
                    Sdh: r.GetInt32(4) != 0,
                    Embedded: false));
        }
        // Embedded
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, language, label FROM local_embedded_tracks WHERE local_file_path = $p AND kind = 'subtitle';";
            cmd.Parameters.AddWithValue("$p", filePath);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Models.LocalSubtitle(
                    Id: r.GetInt64(0),
                    Path: r.IsDBNull(2) ? "" : r.GetString(2),
                    Language: r.IsDBNull(1) ? null : r.GetString(1),
                    Forced: false,
                    Sdh: false,
                    Embedded: true));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalSubtitle>>(list);
    }

    /// <summary>Embedded audio tracks for one video (for the audio-track picker).</summary>
    public Task<IReadOnlyList<Models.LocalEmbeddedTrack>> GetAudioTracksForAsync(string filePath, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, stream_index, language, label, codec, is_default FROM local_embedded_tracks WHERE local_file_path = $p AND kind = 'audio' ORDER BY stream_index;";
        cmd.Parameters.AddWithValue("$p", filePath);
        var list = new List<Models.LocalEmbeddedTrack>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Models.LocalEmbeddedTrack(
                Id: r.GetInt64(0),
                Kind: r.GetString(1),
                StreamIndex: r.GetInt32(2),
                Language: r.IsDBNull(3) ? null : r.GetString(3),
                Label: r.IsDBNull(4) ? null : r.GetString(4),
                Codec: r.IsDBNull(5) ? null : r.GetString(5),
                IsDefault: r.GetInt32(6) != 0));
        return Task.FromResult<IReadOnlyList<Models.LocalEmbeddedTrack>>(list);
    }

    // ============================================================================
    // v17 write API — overrides, watched state, resume, recording plays, etc.
    // ============================================================================

    public Task SetKindOverrideAsync(string filePath, Classification.LocalContentKind? kind, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_files SET kind_override = $k WHERE path = $p;";
        cmd.Parameters.AddWithValue("$k", kind is { } k ? (object)k.ToWireValue() : DBNull.Value);
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task PatchMetadataOverridesAsync(string filePath, string metadataOverridesJson, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_files SET metadata_overrides = $m WHERE path = $p;";
        cmd.Parameters.AddWithValue("$m", (object?)metadataOverridesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task SetLastPositionAsync(string trackUri, long positionMs, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_files SET last_position_ms = $pos WHERE track_uri = $u;";
        cmd.Parameters.AddWithValue("$pos", positionMs);
        cmd.Parameters.AddWithValue("$u", trackUri);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task MarkWatchedAsync(string trackUri, bool watched, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        if (watched)
        {
            cmd.CommandText = "UPDATE local_files SET watched_at = $now, watch_count = watch_count + 1, last_position_ms = 0 WHERE track_uri = $u;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        else
        {
            cmd.CommandText = "UPDATE local_files SET watched_at = NULL WHERE track_uri = $u;";
        }
        cmd.Parameters.AddWithValue("$u", trackUri);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RecordPlayAsync(string trackUri, long positionMs, long durationMs, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_plays (track_uri, played_at, position_ms, duration_ms)
            VALUES ($u, $now, $pos, $dur);
            """;
        cmd.Parameters.AddWithValue("$u", trackUri);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$pos", positionMs);
        cmd.Parameters.AddWithValue("$dur", durationMs);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteFileFromDiskAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Delete-from-disk failed for {Path}", filePath);
            return Task.FromResult(false);
        }
        using var conn = OpenConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM local_files WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", filePath);
            cmd.ExecuteNonQuery();
        }
        return Task.FromResult(true);
    }

    public Task RemoveFromLibraryAsync(string filePath, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_files WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task SetWatchedFolderExpectedKindAsync(int folderId, Classification.LocalContentKind? expected, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE watched_folders SET expected_kind = $k WHERE id = $id;";
        cmd.Parameters.AddWithValue("$k", expected is { } k ? (object)k.ToWireValue() : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the outcome of a successful enrichment lookup onto a local_files
    /// row: provider id (TMDB or MusicBrainz), updated metadata_overrides JSON,
    /// optional poster artwork-cache hash. Marks <c>enrichment_state='Matched'</c>
    /// + sets <c>enrichment_at</c> to now.
    /// </summary>
    public Task UpsertEnrichmentResultAsync(
        string trackUri,
        int? tmdbId,
        string? musicBrainzId,
        string? metadataOverridesJson,
        string? posterArtworkHash,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_files
            SET tmdb_id          = COALESCE($tmdb, tmdb_id),
                musicbrainz_id   = COALESCE($mb, musicbrainz_id),
                metadata_overrides = COALESCE($meta, metadata_overrides),
                enrichment_state = 'Matched',
                enrichment_at    = $now
            WHERE track_uri = $u;
            """;
        cmd.Parameters.AddWithValue("$tmdb", (object?)tmdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mb", (object?)musicBrainzId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meta", (object?)metadataOverridesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$u", trackUri);
        cmd.ExecuteNonQuery();

        if (!string.IsNullOrEmpty(posterArtworkHash))
        {
            // Link the entity URI to the poster as 'cover' role.
            using var link = conn.CreateCommand();
            link.CommandText = """
                INSERT OR REPLACE INTO local_artwork_links (entity_uri, role, image_hash)
                VALUES ($u, 'cover', $h);
                """;
            link.Parameters.AddWithValue("$u", trackUri);
            link.Parameters.AddWithValue("$h", posterArtworkHash);
            link.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Overwrites <c>local_files.episode_title</c> directly with the TMDB
    /// match's canonical episode title. The read path
    /// (<c>GetShowSeasonsAsync</c>) selects this column, not the
    /// <c>metadata_overrides</c> JSON — so without this write the episode
    /// row keeps showing whatever the filename parser scraped from the
    /// <c>SxxExx</c> file name (typically nothing).
    /// </summary>
    public Task UpdateEpisodeTitleAsync(string trackUri, string episodeTitle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(episodeTitle)) return Task.CompletedTask;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_files SET episode_title = $t WHERE track_uri = $u;";
        cmd.Parameters.AddWithValue("$t", episodeTitle);
        cmd.Parameters.AddWithValue("$u", trackUri);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Overwrites the entity's <c>title</c> (and <c>local_files.movie_year</c>)
    /// with the canonical TMDB result. Same rationale as
    /// <see cref="UpdateEpisodeTitleAsync"/> — read paths for movies select
    /// <c>e.title</c> directly, so the metadata_overrides JSON alone doesn't
    /// surface in the UI.
    /// </summary>
    public Task UpdateMovieMetadataAsync(string trackUri, string title, int? year, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        if (!string.IsNullOrWhiteSpace(title))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE entities SET title = $t WHERE uri = $u;";
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$u", trackUri);
            cmd.ExecuteNonQuery();
        }
        if (year is { } y)
        {
            using var cmd2 = conn.CreateCommand();
            cmd2.Transaction = tx;
            cmd2.CommandText = "UPDATE local_files SET movie_year = $y WHERE track_uri = $u;";
            cmd2.Parameters.AddWithValue("$y", y);
            cmd2.Parameters.AddWithValue("$u", trackUri);
            cmd2.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// v20: writes the rich movie-detail columns onto <c>local_files</c> for
    /// one matched movie. All parameters are nullable so the caller can pass
    /// only what it has — COALESCE preserves existing column values.
    /// </summary>
    public Task UpdateMovieDetailsAsync(
        string trackUri,
        string? overview,
        string? tagline,
        int? runtimeMinutes,
        string? genresCsv,
        double? voteAverage,
        string? backdropHash,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_files
               SET movie_overview      = COALESCE($overview, movie_overview),
                   movie_tagline       = COALESCE($tagline, movie_tagline),
                   movie_runtime_min   = COALESCE($runtime, movie_runtime_min),
                   movie_genres        = COALESCE($genres, movie_genres),
                   movie_vote_average  = COALESCE($vote, movie_vote_average),
                   movie_backdrop_hash = COALESCE($backdrop, movie_backdrop_hash)
             WHERE track_uri = $u;
            """;
        cmd.Parameters.AddWithValue("$overview", (object?)overview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tagline", (object?)tagline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runtime", (object?)runtimeMinutes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genres", (object?)genresCsv ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vote", (object?)voteAverage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$backdrop", (object?)backdropHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", trackUri);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>Lightweight DTO for the cast upsert call site.</summary>
    public sealed record MovieCastInput(
        int? PersonId,
        string Name,
        string? Character,
        string? ProfileHash,
        int Order);

    /// <summary>
    /// v20: wipe-and-replace cast list for one movie. Single transaction so
    /// re-sync clicks don't transiently render half-old / half-new.
    /// </summary>
    public Task UpsertMovieCastAsync(string trackUri, IReadOnlyList<MovieCastInput> cast, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM local_movie_cast WHERE track_uri = $u;";
            del.Parameters.AddWithValue("$u", trackUri);
            del.ExecuteNonQuery();
        }
        foreach (var c in cast)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO local_movie_cast (track_uri, order_index, person_id, name, character_name, profile_hash)
                VALUES ($u, $idx, $pid, $name, $char, $prof);
                """;
            ins.Parameters.AddWithValue("$u", trackUri);
            ins.Parameters.AddWithValue("$idx", c.Order);
            ins.Parameters.AddWithValue("$pid", (object?)c.PersonId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$name", c.Name);
            ins.Parameters.AddWithValue("$char", (object?)c.Character ?? DBNull.Value);
            ins.Parameters.AddWithValue("$prof", (object?)c.ProfileHash ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// v20: reads the principal cast for one movie, ordered by TMDB billing
    /// order. Returns empty list when the movie hasn't been enriched yet.
    /// </summary>
    public Task<IReadOnlyList<Models.LocalCastMember>> GetMovieCastAsync(string trackUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT person_id, name, character_name, profile_hash, order_index
              FROM local_movie_cast
             WHERE track_uri = $u
             ORDER BY order_index;
            """;
        cmd.Parameters.AddWithValue("$u", trackUri);
        var list = new List<Models.LocalCastMember>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var profHash = r.IsDBNull(3) ? null : r.GetString(3);
            list.Add(new Models.LocalCastMember(
                PersonId: r.IsDBNull(0) ? null : r.GetInt32(0),
                Name: r.GetString(1),
                Character: r.IsDBNull(2) ? null : r.GetString(2),
                ProfileImageUri: HashToArtUri(profHash),
                Order: r.GetInt32(4)));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalCastMember>>(list);
    }

    /// <summary>
    /// Writes the four <c>spotify_*</c> columns for one local track after a
    /// successful Spotify search match. The cover URL is the resolved HTTPS
    /// CDN URL (i.scdn.co/image/&lt;hash&gt;) so UI bindings don't re-resolve.
    /// Marks <c>enrichment_state='Matched'</c>.
    /// </summary>
    public Task UpsertSpotifyMatchAsync(
        string trackUri,
        string spotifyTrackUri,
        string? spotifyAlbumUri,
        string? spotifyArtistUri,
        string? spotifyCoverUrl,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_files
               SET spotify_track_uri  = $st,
                   spotify_album_uri  = $sa,
                   spotify_artist_uri = $sar,
                   spotify_cover_url  = $sc,
                   enrichment_state   = 'Matched',
                   enrichment_at      = $now
             WHERE track_uri = $u;
            """;
        cmd.Parameters.AddWithValue("$st", spotifyTrackUri);
        cmd.Parameters.AddWithValue("$sa", (object?)spotifyAlbumUri ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sar", (object?)spotifyArtistUri ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sc", (object?)spotifyCoverUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$u", trackUri);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns track URIs of every locally-indexed music file. When
    /// <paramref name="includeAlreadyMatched"/> is false, rows with a
    /// non-null <c>spotify_track_uri</c> are excluded so re-syncing is
    /// cheap. Powers the LocalMusicPage "Sync with Spotify" toolbar.
    /// </summary>
    public Task<IReadOnlyList<string>> GetMusicTracksForEnrichmentAsync(bool includeAlreadyMatched, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT lf.track_uri
              FROM local_files lf
             WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'Music'
               {(includeAlreadyMatched ? "" : "AND lf.spotify_track_uri IS NULL")};
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    /// <summary>
    /// Writes a TMDB poster/backdrop hash onto a local_series row.
    /// Called once per series after the first successful show-batch match.
    /// </summary>
    public Task UpsertSeriesEnrichmentAsync(
        string seriesId,
        int? tmdbId,
        string? overview,
        string? posterArtworkHash,
        string? backdropArtworkHash,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_series
            SET tmdb_id       = COALESCE($tmdb, tmdb_id),
                overview      = COALESCE($overview, overview),
                poster_hash   = COALESCE($poster, poster_hash),
                backdrop_hash = COALESCE($backdrop, backdrop_hash)
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$tmdb", (object?)tmdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$overview", (object?)overview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poster", (object?)posterArtworkHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$backdrop", (object?)backdropArtworkHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", seriesId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Upserts one episode row in the cached TMDB roster
    /// (<c>local_series_episodes</c>). Called from
    /// <c>EnrichTvEpisodeAsync</c> for every entry in the season response
    /// (not just the matched one) so the Show Detail page can render
    /// missing-from-disk episodes as gray placeholders.
    /// </summary>
    public Task UpsertSeriesEpisodeAsync(
        string seriesId,
        int season,
        int episode,
        int? tmdbId,
        string? title,
        string? overview,
        int? runtimeMinutes,
        string? airDate,
        string? stillHash,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_series_episodes
                (series_id, season, episode, tmdb_id, title, overview, runtime_min, air_date, still_hash, fetched_at)
            VALUES ($sid, $s, $e, $tmdb, $title, $ov, $rt, $air, $still, $now)
            ON CONFLICT(series_id, season, episode) DO UPDATE SET
                tmdb_id     = COALESCE($tmdb, tmdb_id),
                title       = COALESCE($title, title),
                overview    = COALESCE($ov, overview),
                runtime_min = COALESCE($rt, runtime_min),
                air_date    = COALESCE($air, air_date),
                still_hash  = COALESCE($still, still_hash),
                fetched_at  = $now;
            """;
        cmd.Parameters.AddWithValue("$sid", seriesId);
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$e", episode);
        cmd.Parameters.AddWithValue("$tmdb", (object?)tmdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ov", (object?)overview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rt", (object?)runtimeMinutes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$air", (object?)airDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$still", (object?)stillHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes show-level totals from TMDB <c>/tv/{id}</c> onto <c>local_series</c>.
    /// Called once per show via the new <c>GetTvDetailsAsync</c> call in
    /// the enrichment pipeline.
    /// </summary>
    public Task UpsertSeriesSummaryAsync(
        string seriesId,
        int? totalSeasons,
        int? totalEpisodes,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_series
               SET total_seasons        = COALESCE($ts, total_seasons),
                   total_episodes_tmdb  = COALESCE($te, total_episodes_tmdb),
                   tmdb_last_fetched_at = $now
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$ts", (object?)totalSeasons ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$te", (object?)totalEpisodes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", seriesId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// v21: writes the rich show details (tagline / status / dates /
    /// genres CSV / vote / networks CSV) onto <c>local_series</c>. All
    /// parameters nullable; COALESCE preserves existing values.
    /// </summary>
    public Task UpdateSeriesDetailsAsync(
        string seriesId,
        string? tagline,
        string? status,
        string? firstAirDate,
        string? lastAirDate,
        string? genresCsv,
        double? voteAverage,
        string? networksCsv,
        CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_series
               SET tagline        = COALESCE($tag, tagline),
                   status         = COALESCE($status, status),
                   first_air_date = COALESCE($fad, first_air_date),
                   last_air_date  = COALESCE($lad, last_air_date),
                   genres         = COALESCE($genres, genres),
                   vote_average   = COALESCE($vote, vote_average),
                   networks       = COALESCE($nets, networks)
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$tag", (object?)tagline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fad", (object?)firstAirDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lad", (object?)lastAirDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genres", (object?)genresCsv ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vote", (object?)voteAverage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nets", (object?)networksCsv ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", seriesId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// v21: wipe-and-replace show cast list. Mirrors <c>UpsertMovieCastAsync</c>.
    /// </summary>
    public Task UpsertShowCastAsync(string seriesId, IReadOnlyList<MovieCastInput> cast, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM local_show_cast WHERE series_id = $sid;";
            del.Parameters.AddWithValue("$sid", seriesId);
            del.ExecuteNonQuery();
        }
        foreach (var c in cast)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO local_show_cast (series_id, order_index, person_id, name, character_name, profile_hash)
                VALUES ($sid, $idx, $pid, $name, $char, $prof);
                """;
            ins.Parameters.AddWithValue("$sid", seriesId);
            ins.Parameters.AddWithValue("$idx", c.Order);
            ins.Parameters.AddWithValue("$pid", (object?)c.PersonId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$name", c.Name);
            ins.Parameters.AddWithValue("$char", (object?)c.Character ?? DBNull.Value);
            ins.Parameters.AddWithValue("$prof", (object?)c.ProfileHash ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>v21: reads principal cast for one show, ordered by TMDB billing.</summary>
    public Task<IReadOnlyList<Models.LocalCastMember>> GetShowCastAsync(string seriesId, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT person_id, name, character_name, profile_hash, order_index
              FROM local_show_cast
             WHERE series_id = $sid
             ORDER BY order_index;
            """;
        cmd.Parameters.AddWithValue("$sid", seriesId);
        var list = new List<Models.LocalCastMember>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var profHash = r.IsDBNull(3) ? null : r.GetString(3);
            list.Add(new Models.LocalCastMember(
                PersonId: r.IsDBNull(0) ? null : r.GetInt32(0),
                Name: r.GetString(1),
                Character: r.IsDBNull(2) ? null : r.GetString(2),
                ProfileImageUri: HashToArtUri(profHash),
                Order: r.GetInt32(4)));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalCastMember>>(list);
    }

    /// <summary>
    /// All TV shows in the user's library that feature the given TMDB person
    /// in their cast list. Powers <c>LocalPersonDetailPage</c>'s "in your
    /// library" shelf. Same shape as <see cref="GetShowsAsync"/>, just
    /// inner-joined to <c>local_show_cast</c> filtered by person id.
    /// </summary>
    public Task<IReadOnlyList<Models.LocalShow>> GetShowsByPersonIdAsync(int personId, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.name, s.tmdb_id, s.overview,
                   s.poster_hash, s.backdrop_hash,
                   COUNT(DISTINCT lf.season_number) AS season_count,
                   COUNT(lf.path)                    AS ep_count,
                   SUM(CASE WHEN lf.watched_at IS NULL THEN 1 ELSE 0 END) AS unwatched,
                   MAX(lf.watched_at)               AS last_watched
            FROM local_series s
            INNER JOIN local_show_cast c ON c.series_id = s.id AND c.person_id = $pid
            LEFT JOIN local_files lf ON lf.series_id = s.id
            GROUP BY s.id
            HAVING ep_count > 0
            ORDER BY s.name;
            """;
        cmd.Parameters.AddWithValue("$pid", personId);
        var list = new List<Models.LocalShow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var posterHash = r.IsDBNull(4) ? null : r.GetString(4);
            var backdropHash = r.IsDBNull(5) ? null : r.GetString(5);
            list.Add(new Models.LocalShow(
                Id: r.GetString(0),
                Name: r.GetString(1),
                TmdbId: r.IsDBNull(2) ? null : r.GetInt32(2),
                Overview: r.IsDBNull(3) ? null : r.GetString(3),
                PosterArtworkUri: HashToArtUri(posterHash),
                BackdropArtworkUri: HashToArtUri(backdropHash),
                SeasonCount: r.GetInt32(6),
                EpisodeCount: r.GetInt32(7),
                UnwatchedCount: r.GetInt32(8),
                LastWatchedAt: r.IsDBNull(9) ? null : r.GetInt64(9)));
        }
        return Task.FromResult<IReadOnlyList<Models.LocalShow>>(list);
    }

    /// <summary>
    /// All movies in the user's library that feature the given TMDB person
    /// in their cast list. Reuses the shared <see cref="ReadMovies"/> helper
    /// with an EXISTS subquery on <c>local_movie_cast</c>.
    /// </summary>
    public Task<IReadOnlyList<Models.LocalMovie>> GetMoviesByPersonIdAsync(int personId, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var rows = ReadMovies(
            conn,
            where: "EXISTS (SELECT 1 FROM local_movie_cast c WHERE c.track_uri = lf.track_uri AND c.person_id = $pid)",
            parameters: cmd => cmd.Parameters.AddWithValue("$pid", personId));
        return Task.FromResult<IReadOnlyList<Models.LocalMovie>>(rows);
    }

    /// <summary>
    /// Records a no-match for an enrichment lookup so we don't keep hammering
    /// the provider for a file we've already shown is unmatchable.
    /// </summary>
    public Task RecordEnrichmentNegativeAsync(string filePath, string provider, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_enrichment_negatives (local_file_path, provider, queried_at)
            VALUES ($p, $prov, $now)
            ON CONFLICT(local_file_path, provider) DO UPDATE SET queried_at = $now;
            """;
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.Parameters.AddWithValue("$prov", provider);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();

        using var mark = conn.CreateCommand();
        mark.CommandText = "UPDATE local_files SET enrichment_state = 'NoMatch', enrichment_at = $now WHERE path = $p;";
        mark.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        mark.Parameters.AddWithValue("$p", filePath);
        mark.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Single-row read used by the enrichment pipeline. Joins
    /// <c>local_files</c> + <c>entities</c> + <c>local_series</c> and projects
    /// just the columns an adapter needs to drive the lookup.
    /// </summary>
    public Task<Enrichment.EnrichmentRow?> GetEnrichmentRowAsync(string trackUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lf.track_uri, lf.path,
                   COALESCE(lf.kind_override, lf.auto_kind) AS effective_kind,
                   e.title, e.artist_name, e.album_name,
                   lf.movie_year, lf.series_id,
                   s.name AS series_name,
                   lf.season_number, lf.episode_number,
                   lf.tmdb_id, lf.musicbrainz_id, lf.enrichment_state,
                   e.duration_ms
            FROM local_files lf
            INNER JOIN entities e ON e.uri = lf.track_uri
            LEFT JOIN local_series s ON s.id = lf.series_id
            WHERE lf.track_uri = $u
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$u", trackUri);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<Enrichment.EnrichmentRow?>(null);
        return Task.FromResult<Enrichment.EnrichmentRow?>(new Enrichment.EnrichmentRow(
            TrackUri: r.GetString(0),
            FilePath: r.GetString(1),
            AutoKind: Classification.LocalContentKindExtensions.ParseWireValue(r.GetString(2)),
            Title: r.IsDBNull(3) ? null : r.GetString(3),
            Artist: r.IsDBNull(4) ? null : r.GetString(4),
            Album: r.IsDBNull(5) ? null : r.GetString(5),
            MovieYear: r.IsDBNull(6) ? null : r.GetInt32(6),
            SeriesId: r.IsDBNull(7) ? null : r.GetString(7),
            SeriesName: r.IsDBNull(8) ? null : r.GetString(8),
            SeasonNumber: r.IsDBNull(9) ? null : r.GetInt32(9),
            EpisodeNumber: r.IsDBNull(10) ? null : r.GetInt32(10),
            TmdbId: r.IsDBNull(11) ? null : r.GetInt32(11),
            MusicBrainzId: r.IsDBNull(12) ? null : r.GetString(12),
            EnrichmentState: r.GetString(13),
            DurationMs: r.IsDBNull(14) ? 0L : r.GetInt64(14)));
    }

    /// <summary>
    /// True if we already have a recent no-match for this (file, provider)
    /// pair — caller skips the lookup. 30-day TTL.
    /// </summary>
    public Task<bool> HasRecentNegativeMatchAsync(string filePath, string provider, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM local_enrichment_negatives WHERE local_file_path = $p AND provider = $prov AND queried_at >= $cutoff LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.Parameters.AddWithValue("$prov", provider);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return Task.FromResult(cmd.ExecuteScalar() is not null);
    }

    /// <summary>
    /// Returns track URIs of every locally-indexed TV episode. When
    /// <paramref name="includeAlreadyMatched"/> is false, rows that already
    /// have a non-null <c>tmdb_id</c> are excluded — that's the cheap path
    /// for the Shows page Sync button so re-clicking doesn't re-query TMDB
    /// for items that are already enriched.
    /// </summary>
    public Task<IReadOnlyList<string>> GetShowEpisodesForEnrichmentAsync(bool includeAlreadyMatched, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT lf.track_uri
              FROM local_files lf
             WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'TvEpisode'
               {(includeAlreadyMatched ? "" : "AND lf.tmdb_id IS NULL")};
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    /// <summary>
    /// Returns track URIs of every episode belonging to one series. Powers
    /// the per-show Sync button.
    /// </summary>
    public Task<IReadOnlyList<string>> GetEpisodesForSeriesAsync(string seriesId, bool includeAlreadyMatched, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT lf.track_uri
              FROM local_files lf
             WHERE lf.series_id = $sid
               {(includeAlreadyMatched ? "" : "AND lf.tmdb_id IS NULL")};
            """;
        cmd.Parameters.AddWithValue("$sid", seriesId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    /// <summary>
    /// Returns track URIs of every locally-indexed movie. Same
    /// <paramref name="includeAlreadyMatched"/> behaviour as the show variant.
    /// </summary>
    public Task<IReadOnlyList<string>> GetMoviesForEnrichmentAsync(bool includeAlreadyMatched, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT lf.track_uri
              FROM local_files lf
             WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'Movie'
               {(includeAlreadyMatched ? "" : "AND lf.tmdb_id IS NULL")};
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    /// <summary>
    /// Drops every recorded enrichment result: clears tmdb_id / musicbrainz_id /
    /// enrichment_state on local_files, drops the negative-match TTL table, and
    /// resets series-level enrichment fields. Next Sync click re-queries TMDB
    /// from scratch. Wired to the "Clear cached lookups" Settings button.
    /// </summary>
    public Task ClearAllEnrichmentResultsAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE local_files
                   SET tmdb_id = NULL,
                       musicbrainz_id = NULL,
                       enrichment_state = 'Pending',
                       enrichment_at = NULL;
                DELETE FROM local_enrichment_negatives;
                UPDATE local_series
                   SET tmdb_id = NULL,
                       poster_hash = NULL,
                       backdrop_hash = NULL,
                       overview = NULL;
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Replaces the cover artwork for one entity URI with user-supplied bytes
    /// (e.g. a JPG / PNG dropped onto the detail flyout). Bytes are hashed +
    /// stored in the local-artwork cache, then linked to the entity as role
    /// 'cover' so every surface that already binds against
    /// <c>wavee-artwork://&lt;hash&gt;</c> picks the override up next read.
    /// </summary>
    public Task<string> SetArtworkOverrideAsync(string entityUri, byte[] bytes, string? mimeType, CancellationToken ct = default)
    {
        if (bytes is null || bytes.Length == 0) throw new ArgumentException("Empty artwork bytes.", nameof(bytes));
        var hash = StoreArtworkBytes(bytes, mimeType);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO local_artwork_links (entity_uri, role, image_hash)
            VALUES ($u, 'cover', $h);
            """;
        cmd.Parameters.AddWithValue("$u", entityUri);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.ExecuteNonQuery();
        return Task.FromResult(LocalArtworkCache.UriScheme + hash);
    }

    /// <summary>
    /// Removes the cover-role link for an entity so any previous artwork override
    /// stops applying. The hashed blob in <c>local_artwork</c> stays — it may be
    /// referenced by other entities and is cheap to keep on disk.
    /// </summary>
    public Task ClearArtworkOverrideAsync(string entityUri, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_artwork_links WHERE entity_uri = $u AND role = 'cover';";
        cmd.Parameters.AddWithValue("$u", entityUri);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stores raw bytes as a local-artwork blob (hashed) and returns the
    /// <c>wavee-artwork://{hash}</c> URI plus the hash. Caller can then link
    /// it to an entity via UpsertEnrichmentResultAsync (poster) or write the
    /// hash onto a local_series row.
    /// </summary>
    public string StoreArtworkBytes(ReadOnlySpan<byte> bytes, string? mimeType)
    {
        // Reuses LocalArtworkCache's hashing + deduped path layout; we just
        // also need the resulting hash, so call a slim helper that returns it.
        using var conn = OpenConnection();
        var hashBytes = System.Security.Cryptography.SHA1.HashData(bytes);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        // Pseudo-entity URI prefix so StoreAndLink writes the file + a stub
        // local_artwork row. The link row is overwritten later via
        // UpsertEnrichmentResultAsync / UpsertSeriesEnrichmentAsync.
        _ = _scanner; // ensure compile-time visibility
        // Inline the disk write since LocalArtworkCache.StoreAndLink requires
        // an entity-uri; for enrichment we just need the hash + file on disk.
        // Reuse the cache root path resolved via reflection-free static helper:
        var root = LocalArtworkCachePathFor(_connectionString);
        var dirPath = Path.Combine(root, hash.Substring(0, 2));
        Directory.CreateDirectory(dirPath);
        var ext = mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg",
        };
        var fullPath = Path.Combine(dirPath, hash + ext);
        if (!File.Exists(fullPath))
            File.WriteAllBytes(fullPath, bytes.ToArray());

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT OR IGNORE INTO local_artwork (image_hash, cached_path, mime_type, created_at)
            VALUES ($hash, $path, $mime, $now);
            """;
        ins.Parameters.AddWithValue("$hash", hash);
        ins.Parameters.AddWithValue("$path", Path.GetFileName(fullPath));
        ins.Parameters.AddWithValue("$mime", (object?)mimeType ?? DBNull.Value);
        ins.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        ins.ExecuteNonQuery();
        return hash;
    }

    /// <summary>
    /// Resolves the local-artwork cache root from the connection string.
    /// Same convention as LocalArtworkCache: alongside the metadata DB file in
    /// a <c>local-artwork</c> sibling directory.
    /// </summary>
    private static string LocalArtworkCachePathFor(string connectionString)
    {
        var b = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var dbDir = Path.GetDirectoryName(b.DataSource) ?? Path.GetTempPath();
        var root = Path.Combine(dbDir, "local-artwork");
        Directory.CreateDirectory(root);
        return root;
    }

    public Task<Models.LocalLyrics?> GetLyricsAsync(string filePath, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT local_file_path, source, format, body, language, fetched_at FROM local_lyrics WHERE local_file_path = $p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", filePath);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<Models.LocalLyrics?>(null);
        return Task.FromResult<Models.LocalLyrics?>(new Models.LocalLyrics(
            FilePath: r.GetString(0),
            Source: r.GetString(1),
            Format: r.GetString(2),
            Body: r.GetString(3),
            Language: r.IsDBNull(4) ? null : r.GetString(4),
            FetchedAt: r.GetInt64(5)));
    }

    public Task UpsertLyricsAsync(string filePath, string source, string format, string body, string? language, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_lyrics (local_file_path, source, format, body, language, fetched_at)
            VALUES ($p, $s, $f, $b, $l, $now)
            ON CONFLICT(local_file_path) DO UPDATE SET source=$s, format=$f, body=$b, language=$l, fetched_at=$now;
            """;
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$f", format);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$l", (object?)language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shared movies query — used by GetMoviesAsync + GetMovieAsync. Pulls
    /// from local_files filtered by effective kind = Movie.
    /// </summary>
    private IReadOnlyList<Models.LocalMovie> ReadMovies(SqliteConnection conn, string? where, Action<SqliteCommand>? parameters)
    {
        using var cmd = conn.CreateCommand();
        var whereClause = where != null ? $"AND ({where})" : string.Empty;
        cmd.CommandText = $"""
            SELECT lf.track_uri, lf.path, COALESCE(e.title, ''), lf.movie_year, e.duration_ms,
                   lf.last_position_ms, lf.watched_at, lf.watch_count,
                   COALESCE(la_p.image_hash, '') AS poster_hash,
                   COALESCE(lf.movie_backdrop_hash, '') AS backdrop_hash,
                   lf.movie_overview, lf.tmdb_id,
                   (SELECT COUNT(*) FROM local_subtitle_files WHERE local_file_path = lf.path)
                 + (SELECT COUNT(*) FROM local_embedded_tracks WHERE local_file_path = lf.path AND kind = 'subtitle') AS sub_count,
                   (SELECT COUNT(*) FROM local_embedded_tracks WHERE local_file_path = lf.path AND kind = 'audio') AS audio_count,
                   lf.movie_tagline, lf.movie_runtime_min, lf.movie_genres, lf.movie_vote_average,
                   lf.metadata_overrides
            FROM local_files lf
            INNER JOIN entities e ON e.uri = lf.track_uri
            LEFT JOIN local_artwork_links la_p ON la_p.entity_uri = lf.track_uri AND la_p.role = 'cover'
            WHERE COALESCE(lf.kind_override, lf.auto_kind) = 'Movie'
            {whereClause}
            ORDER BY lf.last_indexed_at DESC;
            """;
        parameters?.Invoke(cmd);
        var list = new List<Models.LocalMovie>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var path = r.GetString(1);
            var titleFromDb = r.GetString(2);
            var rawTitle = string.IsNullOrEmpty(titleFromDb) ? Path.GetFileNameWithoutExtension(path) : titleFromDb;
            var rawYear = r.IsDBNull(3) ? (int?)null : r.GetInt32(3);
            var posterHash = r.GetString(8);
            var backdropHash = r.GetString(9);
            var overview = r.IsDBNull(10) ? null : r.GetString(10);
            var tagline = r.IsDBNull(14) ? null : r.GetString(14);
            var runtimeMin = r.IsDBNull(15) ? (int?)null : r.GetInt32(15);
            var genresCsv = r.IsDBNull(16) ? null : r.GetString(16);
            var voteAvg = r.IsDBNull(17) ? (double?)null : r.GetDouble(17);
            var overrides = ParseMetadataOverrides(r.IsDBNull(18) ? null : r.GetString(18));
            list.Add(new Models.LocalMovie(
                TrackUri: r.GetString(0),
                FilePath: path,
                Title: OverlayString(overrides?.Title, rawTitle) ?? rawTitle,
                Year: OverlayValue(overrides?.Year, rawYear),
                DurationMs: r.IsDBNull(4) ? 0L : r.GetInt64(4),
                LastPositionMs: r.GetInt64(5),
                WatchedAt: r.IsDBNull(6) ? null : r.GetInt64(6),
                WatchCount: r.GetInt32(7),
                PosterUri: HashToArtUri(posterHash),
                BackdropUri: HashToArtUri(backdropHash),
                Overview: overview,
                TmdbId: r.IsDBNull(11) ? null : r.GetInt32(11),
                SubtitleCount: r.GetInt32(12),
                AudioTrackCount: r.GetInt32(13))
            {
                Tagline = tagline,
                RuntimeMinutes = runtimeMin,
                Genres = string.IsNullOrEmpty(genresCsv)
                    ? null
                    : genresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                VoteAverage = voteAvg,
            });
        }
        return list;
    }

    private static string? HashToArtUri(string? hash) =>
        string.IsNullOrEmpty(hash) ? null : LocalArtworkCache.UriScheme + hash;

    public void Dispose()
    {
        _progress.Dispose();
        _scanLock.Dispose();
    }
}
