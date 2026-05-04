using System.Diagnostics;
using System.Reactive.Subjects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Library.Local;

/// <summary>
/// Walks watched folders, fingerprints + extracts new/changed files,
/// upserts entities, prunes orphan rows. Single-flight per folder.
/// </summary>
public sealed class LocalFolderScanner
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".m4b", ".mp4", ".mov", ".aac",
        ".ogg", ".opus", ".wma", ".aiff", ".aif",
        ".m4v", ".mkv", ".webm",
    };

    /// <summary>
    /// Subset of <see cref="AudioExtensions"/> that contain video frames.
    /// Files matching these go through the UI-side MediaPlayer (Windows.Media.Playback)
    /// so the user can both see and hear them, instead of the audio-only AudioHost
    /// path. .mp4/.mov/.m4v are technically containers that may be audio-only —
    /// flagging them as video is a false positive that just routes through
    /// MediaPlayer (still plays correctly), so the trade-off favours simplicity.
    /// </summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".webm",
    };

    public static bool IsVideoExtension(string path) =>
        VideoExtensions.Contains(System.IO.Path.GetExtension(path));

    private const int ProgressEveryN = 25;

    private readonly LocalMetadataExtractor _extractor;
    private readonly LocalArtworkCache _artwork;
    private readonly IVideoThumbnailExtractor? _videoThumbnail;
    private readonly ILogger? _logger;

    public LocalFolderScanner(
        LocalMetadataExtractor extractor,
        LocalArtworkCache artwork,
        IVideoThumbnailExtractor? videoThumbnail = null,
        ILogger? logger = null)
    {
        _extractor = extractor;
        _artwork = artwork;
        _videoThumbnail = videoThumbnail;
        _logger = logger;
    }

    public Task ScanFolderAsync(
        LocalLibraryFolder folder,
        string connectionString,
        Subject<LocalSyncProgress> progressSink,
        CancellationToken ct)
    {
        return Task.Run(() => ScanFolderCore(folder, connectionString, progressSink, ct), ct);
    }

    private void ScanFolderCore(
        LocalLibraryFolder folder,
        string connectionString,
        Subject<LocalSyncProgress> progressSink,
        CancellationToken ct)
    {
        if (!Directory.Exists(folder.Path))
        {
            using var conn = Open(connectionString);
            UpdateFolderStatus(conn, folder.Id, "error", "Folder no longer exists.", durationMs: 0, fileCount: folder.FileCount);
            return;
        }

        _logger?.LogInformation("Scanning local folder: {Path}", folder.Path);
        var stopwatch = Stopwatch.StartNew();
        var scanStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Notably do NOT skip FileAttributes.Offline. The Windows default
        // Music folder under a user profile is frequently OneDrive-redirected,
        // and OneDrive marks both the folder and any cloud-only files as
        // Offline. Skipping Offline = enumeration walks right past the entire
        // music library. Reading these files rehydrates them on demand, which
        // is the behaviour the user expects when they explicitly point Wavee
        // at a folder.
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = folder.IncludeSubfolders,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder.Path, "*", enumOpts)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f)));
        }
        catch (Exception ex)
        {
            using var conn = Open(connectionString);
            UpdateFolderStatus(conn, folder.Id, "error", ex.Message, stopwatch.ElapsedMilliseconds, folder.FileCount);
            return;
        }

        var processed = 0;
        var anyError = false;
        string? firstError = null;

        // Pre-count for progress UI; cheap-ish for typical libraries (~tens of thousands).
        var fileList = files.ToList();
        var total = fileList.Count;

        foreach (var raw in fileList)
        {
            if (ct.IsCancellationRequested) break;

            var path = LocalPath.Normalize(raw);
            try
            {
                using var conn = Open(connectionString);
                IndexFile(conn, folder.Id, path, scanStart);
            }
            catch (Exception ex)
            {
                anyError = true;
                firstError ??= ex.Message;
                _logger?.LogWarning(ex, "Indexing failed for {Path}", path);
            }

            processed++;
            if (processed % ProgressEveryN == 0 || processed == total)
            {
                progressSink.OnNext(new LocalSyncProgress(folder.Id, total, processed, path));
            }
        }

        // Prune rows we didn't see this scan.
        int prunedCount;
        using (var conn = Open(connectionString))
        {
            prunedCount = PruneStale(conn, folder.Id, scanStart);
            CleanOrphanEntities(conn);
            UpdateFolderStatus(conn, folder.Id,
                anyError ? "partial" : "ok",
                firstError,
                stopwatch.ElapsedMilliseconds,
                fileCount: total - prunedCount);
        }

        progressSink.OnNext(new LocalSyncProgress(folder.Id, total, total, null));
        _logger?.LogInformation("Scan complete: {Path} — {Processed} files in {Ms} ms (pruned {Pruned})",
            folder.Path, processed, stopwatch.ElapsedMilliseconds, prunedCount);
    }

    public Task RescanSingleFileAsync(string normalizedPath, string connectionString, CancellationToken ct)
    {
        if (!File.Exists(normalizedPath)) return Task.CompletedTask;

        // Look up the folder this path belongs to.
        using var conn = Open(connectionString);
        var folderId = LookupFolderId(conn, normalizedPath);
        if (folderId is null) return Task.CompletedTask;

        try
        {
            var scanStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            IndexFile(conn, folderId.Value, normalizedPath, scanStart);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Single-file rescan failed for {Path}", normalizedPath);
        }
        return Task.CompletedTask;
    }

    public Task RemoveSingleFileAsync(string normalizedPath, string connectionString, CancellationToken ct)
    {
        using var conn = Open(connectionString);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM local_files WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", normalizedPath);
            cmd.ExecuteNonQuery();
        }
        CleanOrphanEntities(conn);
        return Task.CompletedTask;
    }

    private void IndexFile(SqliteConnection conn, int folderId, string path, long scanStart)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;

        var size = info.Length;
        var mtime = info.LastWriteTimeUtc.Ticks;

        // Fast path: if the row exists with same (size, mtime), bump
        // last_seen_at and skip — UNLESS we know we want to re-process to
        // backfill late-added artwork sources. Specifically: a video file
        // scanned before IVideoThumbnailExtractor was wired up has no entry
        // in local_artwork_links; running through the full IndexFile path
        // will let _videoThumbnail kick in and fix it. Without this carve-
        // out, those rows would stay artwork-less forever (no mtime change).
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = """
                SELECT lf.file_size, lf.file_mtime_ticks, lf.track_uri,
                       (SELECT 1 FROM local_artwork_links la WHERE la.entity_uri = lf.track_uri AND la.role = 'cover' LIMIT 1)
                  FROM local_files lf WHERE lf.path = $p LIMIT 1;
                """;
            probe.Parameters.AddWithValue("$p", path);
            using var r = probe.ExecuteReader();
            if (r.Read())
            {
                var existingSize = r.GetInt64(0);
                var existingMtime = r.GetInt64(1);
                var hasArtwork = !r.IsDBNull(3);
                var canBackfillVideoArt = !hasArtwork && _videoThumbnail is not null && IsVideoExtension(path);
                if (existingSize == size && existingMtime == mtime && !canBackfillVideoArt)
                {
                    r.Close();
                    using var bump = conn.CreateCommand();
                    bump.CommandText = "UPDATE local_files SET last_seen_at = $now, scan_error = NULL WHERE path = $p;";
                    bump.Parameters.AddWithValue("$now", scanStart);
                    bump.Parameters.AddWithValue("$p", path);
                    bump.ExecuteNonQuery();
                    return;
                }
            }
        }

        // New or changed. Hash + extract.
        var hash = LocalTrackHasher.ComputeAsync(path).GetAwaiter().GetResult();
        var trackUri = LocalUri.BuildTrack(hash);

        var meta = _extractor.Extract(path);
        if (meta is null)
        {
            // Couldn't extract; record the file but mark with an error.
            using var err = conn.CreateCommand();
            err.CommandText = """
                INSERT INTO local_files (path, folder_id, track_uri, file_size, file_mtime_ticks, content_hash, last_indexed_at, last_seen_at, scan_error, is_video)
                VALUES ($p, $f, $u, $sz, $mt, $h, $now, $now, $err, $iv)
                ON CONFLICT(path) DO UPDATE SET folder_id=$f, track_uri=$u, file_size=$sz, file_mtime_ticks=$mt, content_hash=$h, last_indexed_at=$now, last_seen_at=$now, scan_error=$err, is_video=$iv;
                """;
            err.Parameters.AddWithValue("$p", path);
            err.Parameters.AddWithValue("$f", folderId);
            err.Parameters.AddWithValue("$u", trackUri);
            err.Parameters.AddWithValue("$sz", size);
            err.Parameters.AddWithValue("$mt", mtime);
            err.Parameters.AddWithValue("$h", hash);
            err.Parameters.AddWithValue("$now", scanStart);
            err.Parameters.AddWithValue("$err", "Tag extraction returned no metadata");
            err.Parameters.AddWithValue("$iv", IsVideoExtension(path) ? 1 : 0);
            err.ExecuteNonQuery();
            return;
        }

        var artistName  = LocalNormalize.Artist(meta.AlbumArtist, meta.Artist);
        var albumName   = LocalNormalize.Album(meta.Album);
        var albumUri    = LocalNormalize.AlbumUri(artistName, albumName, meta.Year);
        var artistUri   = LocalNormalize.ArtistUri(artistName);
        var displayArtist = !string.IsNullOrWhiteSpace(meta.AlbumArtist) ? meta.AlbumArtist
                          : !string.IsNullOrWhiteSpace(meta.Artist) ? meta.Artist
                          : "Unknown Artist";
        var displayAlbum  = !string.IsNullOrWhiteSpace(meta.Album) ? meta.Album : "Unknown Album";

        using var tx = conn.BeginTransaction();

        UpsertEntity(conn, tx, trackUri, EntityType.Track, meta.Title ?? Path.GetFileNameWithoutExtension(path),
            artistName: meta.Artist ?? displayArtist, albumName: displayAlbum, albumUri: albumUri,
            durationMs: (int)Math.Min(int.MaxValue, meta.DurationMs),
            trackNumber: meta.TrackNumber, discNumber: meta.DiscNumber, year: meta.Year,
            genre: meta.Genre, filePath: path);

        UpsertEntity(conn, tx, albumUri, EntityType.Album, displayAlbum,
            artistName: displayArtist, albumName: displayAlbum, albumUri: null,
            durationMs: null, trackNumber: null, discNumber: null, year: meta.Year,
            genre: null, filePath: null);

        UpsertEntity(conn, tx, artistUri, EntityType.Artist, displayArtist,
            artistName: displayArtist, albumName: null, albumUri: null,
            durationMs: null, trackNumber: null, discNumber: null, year: null,
            genre: null, filePath: null);

        // Artwork (cover-front), linked to album AND track.
        // Priority: tag-embedded cover art first (most accurate); for video
        // files with no embedded art, fall back to a Windows-shell frame
        // thumbnail via IVideoThumbnailExtractor (host-supplied, optional).
        // This means an .mp4 with no ID3 cover still gets a representative
        // image without the home shelf having to do lazy-load magic later.
        byte[]? artworkBytes = null;
        string? artworkMime = null;
        if (meta.CoverArtData is { Length: > 0 } coverBytes)
        {
            artworkBytes = coverBytes;
            artworkMime = meta.CoverArtMimeType;
        }
        else if (_videoThumbnail is not null && IsVideoExtension(path))
        {
            var thumb = _videoThumbnail.Extract(path);
            if (thumb is { Length: > 0 })
            {
                artworkBytes = thumb;
                // GetThumbnailAsync returns JPEG bytes by default.
                artworkMime = "image/jpeg";
            }
        }

        if (artworkBytes is { Length: > 0 })
        {
            try
            {
                var artworkUri = _artwork.StoreAndLink(conn, tx, albumUri, "cover", artworkBytes, artworkMime);
                _artwork.StoreAndLink(conn, tx, trackUri, "cover", artworkBytes, artworkMime);
                // Mirror the artwork URI on entities.image_url for compatibility with existing image-binding paths.
                using var setImg = conn.CreateCommand();
                setImg.Transaction = tx;
                setImg.CommandText = "UPDATE entities SET image_url = $img WHERE uri IN ($t, $a);";
                setImg.Parameters.AddWithValue("$img", artworkUri);
                setImg.Parameters.AddWithValue("$t", trackUri);
                setImg.Parameters.AddWithValue("$a", albumUri);
                setImg.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Artwork persist failed for {Path}", path);
            }
        }

        // Upsert the file row.
        var isVideo = IsVideoExtension(path) ? 1 : 0;
        using (var fileCmd = conn.CreateCommand())
        {
            fileCmd.Transaction = tx;
            fileCmd.CommandText = """
                INSERT INTO local_files (path, folder_id, track_uri, file_size, file_mtime_ticks, content_hash, last_indexed_at, last_seen_at, scan_error, is_video)
                VALUES ($p, $f, $u, $sz, $mt, $h, $now, $now, NULL, $iv)
                ON CONFLICT(path) DO UPDATE SET folder_id=$f, track_uri=$u, file_size=$sz, file_mtime_ticks=$mt, content_hash=$h, last_indexed_at=$now, last_seen_at=$now, scan_error=NULL, is_video=$iv;
                """;
            fileCmd.Parameters.AddWithValue("$p", path);
            fileCmd.Parameters.AddWithValue("$f", folderId);
            fileCmd.Parameters.AddWithValue("$u", trackUri);
            fileCmd.Parameters.AddWithValue("$sz", size);
            fileCmd.Parameters.AddWithValue("$mt", mtime);
            fileCmd.Parameters.AddWithValue("$h", hash);
            fileCmd.Parameters.AddWithValue("$now", scanStart);
            fileCmd.Parameters.AddWithValue("$iv", isVideo);
            fileCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void UpsertEntity(SqliteConnection conn, SqliteTransaction tx, string uri,
        EntityType entityType, string title, string? artistName, string? albumName, string? albumUri,
        int? durationMs, int? trackNumber, int? discNumber, int? year, string? genre, string? filePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO entities (
                uri, source_type, entity_type, title, artist_name, album_name, album_uri,
                duration_ms, track_number, disc_number, release_year, genre, file_path,
                created_at, updated_at)
            VALUES (
                $u, 1, $et, $title, $artist, $album, $aurl,
                $dur, $tn, $dn, $year, $genre, $path,
                $now, $now)
            ON CONFLICT(uri) DO UPDATE SET
                source_type   = 1,
                entity_type   = $et,
                title         = $title,
                artist_name   = $artist,
                album_name    = $album,
                album_uri     = $aurl,
                duration_ms   = $dur,
                track_number  = $tn,
                disc_number   = $dn,
                release_year  = $year,
                genre         = $genre,
                file_path     = $path,
                updated_at    = $now;
            """;
        cmd.Parameters.AddWithValue("$u", uri);
        cmd.Parameters.AddWithValue("$et", (int)entityType);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artist", (object?)artistName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$album", (object?)albumName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aurl", (object?)albumUri ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", (object?)durationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tn", (object?)trackNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dn", (object?)discNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$year", (object?)year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genre", (object?)genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)filePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    private static int PruneStale(SqliteConnection conn, int folderId, long scanStart)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_files WHERE folder_id = $f AND last_seen_at < $s;";
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$s", scanStart);
        return cmd.ExecuteNonQuery();
    }

    private static void CleanOrphanEntities(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM entities
            WHERE source_type = 1
              AND uri LIKE 'wavee:local:track:%'
              AND uri NOT IN (SELECT track_uri FROM local_files);
            """;
        cmd.ExecuteNonQuery();
    }

    private static int? LookupFolderId(SqliteConnection conn, string path)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM watched_folders WHERE $p LIKE path || '%' AND enabled = 1 ORDER BY length(path) DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", path);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static void UpdateFolderStatus(SqliteConnection conn, int folderId, string status, string? error, long durationMs, int fileCount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE watched_folders
            SET last_scan_at = $now,
                last_scan_status = $st,
                last_scan_error  = $err,
                last_scan_duration_ms = $dur,
                file_count = $fc
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", durationMs);
        cmd.Parameters.AddWithValue("$fc", fileCount);
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string connectionString)
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }
}
