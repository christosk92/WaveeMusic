using System.Reactive.Subjects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Library.Local;

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
        await RunScanAsync(folderId, ct);
    }

    /// <summary>Internal scan entry point used by AddWatchedFolderAsync, TriggerRescanAsync, hosted service.</summary>
    public async Task RunScanAsync(int? folderId, CancellationToken ct)
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
                    await _scanner.ScanFolderAsync(folder, _connectionString, _progress, ct);
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

    private List<LocalTrackRow> ReadTracks(SqliteConnection conn, string? where, Action<SqliteCommand>? parameters)
    {
        using var cmd = conn.CreateCommand();
        var whereClause = where != null ? $"AND ({where})" : string.Empty;
        cmd.CommandText = $"""
            SELECT e.uri, lf.path, e.title, e.artist_name, e.album_name, e.album_uri,
                   e.duration_ms, e.track_number, e.disc_number, e.release_year,
                   COALESCE(la.image_hash, '') AS art_hash,
                   (SELECT artist_name FROM entities a WHERE a.uri = e.uri) AS album_artist,
                   (SELECT uri FROM entities x WHERE x.entity_type = 3 AND x.title = e.artist_name AND x.source_type = 1 LIMIT 1) AS artist_uri,
                   lf.is_video
            FROM entities e
            INNER JOIN local_files lf ON lf.track_uri = e.uri
            LEFT JOIN local_artwork_links la
              ON la.entity_uri = e.uri AND la.role = 'cover'
            WHERE e.source_type = 1 AND e.entity_type = 1
            {whereClause}
            ORDER BY e.artist_name, e.album_name, e.disc_number, e.track_number, e.title;
            """;
        parameters?.Invoke(cmd);

        var list = new List<LocalTrackRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var uri = r.GetString(0);
            var path = r.GetString(1);
            var title = r.IsDBNull(2) ? null : r.GetString(2);
            var artist = r.IsDBNull(3) ? null : r.GetString(3);
            var album = r.IsDBNull(4) ? null : r.GetString(4);
            var albumUri = r.IsDBNull(5) ? null : r.GetString(5);
            var duration = r.IsDBNull(6) ? 0L : r.GetInt64(6);
            var trackNo = r.IsDBNull(7) ? (int?)null : r.GetInt32(7);
            var discNo = r.IsDBNull(8) ? (int?)null : r.GetInt32(8);
            var year = r.IsDBNull(9) ? (int?)null : r.GetInt32(9);
            var artHash = r.GetString(10);
            var artworkUri = string.IsNullOrEmpty(artHash) ? null : LocalArtworkCache.UriScheme + artHash;
            var albumArtist = r.IsDBNull(11) ? null : r.GetString(11);
            var artistUri = r.IsDBNull(12) ? null : r.GetString(12);
            var isVideo = !r.IsDBNull(13) && r.GetInt32(13) != 0;

            list.Add(new LocalTrackRow(uri, path, title, artist, albumArtist, album, albumUri, artistUri,
                duration, trackNo, discNo, year, artworkUri, isVideo));
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

    public void Dispose()
    {
        _progress.Dispose();
        _scanLock.Dispose();
    }
}
