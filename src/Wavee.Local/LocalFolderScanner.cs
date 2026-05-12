using System.Diagnostics;
using System.Reactive.Subjects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Local.Classification;
using Wavee.Local.Subtitles;
// EntityType moved to LocalEntityKind (Wavee.Local-private). See LocalEntityKind.cs


namespace Wavee.Local;

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
    private readonly IEmbeddedTrackProber? _embeddedTrackProber;
    private readonly ILogger? _logger;

    public LocalFolderScanner(
        LocalMetadataExtractor extractor,
        LocalArtworkCache artwork,
        IVideoThumbnailExtractor? videoThumbnail = null,
        IEmbeddedTrackProber? embeddedTrackProber = null,
        ILogger? logger = null)
    {
        _extractor = extractor;
        _artwork = artwork;
        _videoThumbnail = videoThumbnail;
        _embeddedTrackProber = embeddedTrackProber;
        _logger = logger;
    }

    public Task ScanFolderAsync(
        LocalLibraryFolder folder,
        string connectionString,
        Subject<LocalSyncProgress> progressSink,
        CancellationToken ct)
        => ScanFolderAsync(folder, connectionString, progressSink, forceReclassify: false, ct);

    /// <param name="forceReclassify">
    /// True only for user-initiated rescans. When true, files whose
    /// (size, mtime) are unchanged still re-run the classifier from their
    /// filename + folder hint. Background scans pass false to stay fast on
    /// large libraries.
    /// </param>
    public Task ScanFolderAsync(
        LocalLibraryFolder folder,
        string connectionString,
        Subject<LocalSyncProgress> progressSink,
        bool forceReclassify,
        CancellationToken ct)
    {
        return Task.Run(() => ScanFolderCore(folder, connectionString, progressSink, forceReclassify, ct), ct);
    }

    private void ScanFolderCore(
        LocalLibraryFolder folder,
        string connectionString,
        Subject<LocalSyncProgress> progressSink,
        bool forceReclassify,
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
                IndexFile(conn, folder.Id, path, scanStart, forceReclassify);
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

        // Dump the current effective kind distribution so the user can see
        // immediately why a particular rail is empty (e.g. "all 35 → TvEpisode").
        try
        {
            using var dist = Open(connectionString);
            using var cmd = dist.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(kind_override, auto_kind) AS k, COUNT(*) AS n
                FROM local_files
                GROUP BY k
                ORDER BY n DESC;
                """;
            var sb = new System.Text.StringBuilder("[scan] kind distribution after scan:");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                sb.Append(' ').Append(r.GetString(0)).Append('=').Append(r.GetInt32(1));
            _logger?.LogInformation("{Line}", sb.ToString());
        }
        catch { /* diagnostic best-effort */ }
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

    private void IndexFile(SqliteConnection conn, int folderId, string path, long scanStart, bool forceReclassify = false)
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
                       (SELECT 1 FROM local_artwork_links la WHERE la.entity_uri = lf.track_uri AND la.role = 'cover' LIMIT 1),
                       lf.auto_kind
                  FROM local_files lf WHERE lf.path = $p LIMIT 1;
                """;
            probe.Parameters.AddWithValue("$p", path);
            using var r = probe.ExecuteReader();
            if (r.Read())
            {
                var existingSize = r.GetInt64(0);
                var existingMtime = r.GetInt64(1);
                var hasArtwork = !r.IsDBNull(3);
                var existingAutoKind = r.IsDBNull(4) ? "Other" : r.GetString(4);
                var canBackfillVideoArt = !hasArtwork && _videoThumbnail is not null && IsVideoExtension(path);
                if (existingSize == size && existingMtime == mtime && !canBackfillVideoArt)
                {
                    r.Close();

                    // Re-classify "unchanged" files only on user-initiated rescans.
                    // On background / app-open scans (forceReclassify=false), skip
                    // straight to the bare last_seen_at bump — a 10k-track library
                    // can't afford regex+classifier per file every 6 hours when the
                    // rules haven't changed.
                    if (!forceReclassify)
                    {
                        using var bumpFast = conn.CreateCommand();
                        bumpFast.CommandText = "UPDATE local_files SET last_seen_at = $now, scan_error = NULL WHERE path = $p;";
                        bumpFast.Parameters.AddWithValue("$now", scanStart);
                        bumpFast.Parameters.AddWithValue("$p", path);
                        bumpFast.ExecuteNonQuery();
                        return;
                    }

                    // Manual rescan: re-run the classifier from filename + folder
                    // hint (no tag/duration data — that's fine for the strongest
                    // signals: SxxExx, release markers, MV markers). If the kind
                    // shifts, update the row + parse episode/movie hints.
                    var fastExpectedKind = ReadFolderExpectedKind(conn, folderId);
                    var fastKind = LocalContentClassifier.Classify(
                        filePath: path,
                        hasArtistTag: false, hasAlbumTag: false, durationMs: 0,
                        expectedKind: fastExpectedKind, logger: _logger);

                    if (fastKind.ToWireValue() != existingAutoKind)
                    {
                        _logger?.LogInformation("[scan] re-classifying unchanged file {File}: {Old} → {New}",
                            System.IO.Path.GetFileName(path), existingAutoKind, fastKind);

                        string? newSeriesId = null;
                        int? newSeason = null, newEpisode = null, newMovieYear = null;
                        string? newEpisodeTitle = null;
                        using var fastTx = conn.BeginTransaction();
                        if (fastKind == LocalContentKind.TvEpisode &&
                            LocalFilenameParser.TryParseEpisode(path, out var seriesNameFast, out var sFast, out var eFast, out var epTitleFast))
                        {
                            newSeason = sFast;
                            newEpisode = eFast;
                            newEpisodeTitle = epTitleFast;
                            newSeriesId = FindOrCreateSeries(conn, fastTx, seriesNameFast, scanStart);
                        }
                        else if (fastKind == LocalContentKind.Movie &&
                                 LocalFilenameParser.TryParseMovie(path, out _, out var movieYFast))
                        {
                            newMovieYear = movieYFast;
                        }
                        using (var upd = conn.CreateCommand())
                        {
                            upd.Transaction = fastTx;
                            upd.CommandText = """
                                UPDATE local_files
                                   SET auto_kind = $kind,
                                       series_id = $sid,
                                       season_number = $sn,
                                       episode_number = $en,
                                       episode_title = $et,
                                       movie_year = $my,
                                       last_seen_at = $now,
                                       scan_error = NULL
                                 WHERE path = $p;
                                """;
                            upd.Parameters.AddWithValue("$kind", fastKind.ToWireValue());
                            upd.Parameters.AddWithValue("$sid", (object?)newSeriesId ?? DBNull.Value);
                            upd.Parameters.AddWithValue("$sn", (object?)newSeason ?? DBNull.Value);
                            upd.Parameters.AddWithValue("$en", (object?)newEpisode ?? DBNull.Value);
                            upd.Parameters.AddWithValue("$et", (object?)newEpisodeTitle ?? DBNull.Value);
                            upd.Parameters.AddWithValue("$my", (object?)newMovieYear ?? DBNull.Value);
                            upd.Parameters.AddWithValue("$now", scanStart);
                            upd.Parameters.AddWithValue("$p", path);
                            upd.ExecuteNonQuery();
                        }
                        fastTx.Commit();
                    }

                    // Manual rescan also re-probes video embedded tracks +
                    // sibling subtitles. Existing scans of pre-Continuation-7
                    // .mkv files only got subtitles indexed; audio tracks were
                    // never enumerated. Re-probe on the same fast-path so a
                    // single "Rescan" click in Settings backfills them.
                    if (IsVideoExtension(path) && fastKind.IsVideo())
                    {
                        using var probeTx = conn.BeginTransaction();
                        UpsertSubtitles(conn, probeTx, path);
                        UpsertEmbeddedTracks(conn, probeTx, path);
                        probeTx.Commit();
                    }

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

        UpsertEntity(conn, tx, trackUri, LocalEntityKind.Track, meta.Title ?? Path.GetFileNameWithoutExtension(path),
            artistName: meta.Artist ?? displayArtist, albumName: displayAlbum, albumUri: albumUri,
            durationMs: (int)Math.Min(int.MaxValue, meta.DurationMs),
            trackNumber: meta.TrackNumber, discNumber: meta.DiscNumber, year: meta.Year,
            genre: meta.Genre, filePath: path);

        UpsertEntity(conn, tx, albumUri, LocalEntityKind.Album, displayAlbum,
            artistName: displayArtist, albumName: displayAlbum, albumUri: null,
            durationMs: null, trackNumber: null, discNumber: null, year: meta.Year,
            genre: null, filePath: null);

        UpsertEntity(conn, tx, artistUri, LocalEntityKind.Artist, displayArtist,
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

        // Classify the file based on extension + tag signals + filename + duration.
        // The per-folder kind hint is fetched once below and passed as a soft tiebreaker.
        // The classifier emits a structured debug log line; see [classify] entries.
        var expectedKind = ReadFolderExpectedKind(conn, folderId);
        var contentKind = LocalContentClassifier.Classify(
            filePath: path,
            hasArtistTag: !string.IsNullOrWhiteSpace(meta.Artist) || !string.IsNullOrWhiteSpace(meta.AlbumArtist),
            hasAlbumTag: !string.IsNullOrWhiteSpace(meta.Album),
            durationMs: meta.DurationMs,
            expectedKind: expectedKind,
            logger: _logger);

        // TV-episode-specific hint extraction (used by the v17 series-grouping pass below).
        string? seriesId = null;
        int? seasonNumber = null;
        int? episodeNumber = null;
        string? episodeTitle = null;
        int? movieYear = null;

        if (contentKind == LocalContentKind.TvEpisode &&
            LocalFilenameParser.TryParseEpisode(path, out var seriesName, out var s, out var e, out var epTitle))
        {
            seasonNumber = s;
            episodeNumber = e;
            episodeTitle = epTitle;
            seriesId = FindOrCreateSeries(conn, tx, seriesName, scanStart);
            _logger?.LogDebug("[scan] episode parsed: series='{Series}' S{Season}E{Episode} title='{Title}' seriesId={SeriesId}",
                seriesName, s, e, epTitle, seriesId);
        }
        else if (contentKind == LocalContentKind.Movie &&
                 LocalFilenameParser.TryParseMovie(path, out var movieTitle, out var movieY))
        {
            movieYear = movieY;
            _logger?.LogDebug("[scan] movie parsed: title='{Title}' year={Year}", movieTitle, movieY);
        }

        // Upsert the file row, including all v17 columns. KIND_OVERRIDE +
        // metadata_overrides + last_position_ms + watched_at + watch_count are
        // preserved across re-scans because the ON CONFLICT clause only touches
        // auto-derived columns.
        var isVideo = IsVideoExtension(path) ? 1 : 0;
        using (var fileCmd = conn.CreateCommand())
        {
            fileCmd.Transaction = tx;
            fileCmd.CommandText = """
                INSERT INTO local_files (
                    path, folder_id, track_uri, file_size, file_mtime_ticks, content_hash,
                    last_indexed_at, last_seen_at, scan_error, is_video,
                    auto_kind, series_id, season_number, episode_number, episode_title, movie_year,
                    enrichment_state
                )
                VALUES (
                    $p, $f, $u, $sz, $mt, $h,
                    $now, $now, NULL, $iv,
                    $kind, $sid, $sn, $en, $et, $my,
                    'Pending'
                )
                ON CONFLICT(path) DO UPDATE SET
                    folder_id        = $f,
                    track_uri        = $u,
                    file_size        = $sz,
                    file_mtime_ticks = $mt,
                    content_hash     = $h,
                    last_indexed_at  = $now,
                    last_seen_at     = $now,
                    scan_error       = NULL,
                    is_video         = $iv,
                    auto_kind        = $kind,
                    series_id        = $sid,
                    season_number    = $sn,
                    episode_number   = $en,
                    episode_title    = $et,
                    movie_year       = $my,
                    -- enrichment_state reset to Pending only if classifier changed kind
                    enrichment_state = CASE WHEN local_files.auto_kind <> $kind THEN 'Pending' ELSE local_files.enrichment_state END;
                """;
            fileCmd.Parameters.AddWithValue("$p", path);
            fileCmd.Parameters.AddWithValue("$f", folderId);
            fileCmd.Parameters.AddWithValue("$u", trackUri);
            fileCmd.Parameters.AddWithValue("$sz", size);
            fileCmd.Parameters.AddWithValue("$mt", mtime);
            fileCmd.Parameters.AddWithValue("$h", hash);
            fileCmd.Parameters.AddWithValue("$now", scanStart);
            fileCmd.Parameters.AddWithValue("$iv", isVideo);
            fileCmd.Parameters.AddWithValue("$kind", contentKind.ToWireValue());
            fileCmd.Parameters.AddWithValue("$sid", (object?)seriesId ?? DBNull.Value);
            fileCmd.Parameters.AddWithValue("$sn", (object?)seasonNumber ?? DBNull.Value);
            fileCmd.Parameters.AddWithValue("$en", (object?)episodeNumber ?? DBNull.Value);
            fileCmd.Parameters.AddWithValue("$et", (object?)episodeTitle ?? DBNull.Value);
            fileCmd.Parameters.AddWithValue("$my", (object?)movieYear ?? DBNull.Value);
            fileCmd.ExecuteNonQuery();
        }

        // Subtitle discovery + embedded-track probe — only for video kinds.
        if (contentKind.IsVideo())
        {
            UpsertSubtitles(conn, tx, path);
            UpsertEmbeddedTracks(conn, tx, path);
        }

        tx.Commit();
    }

    /// <summary>Reads the per-folder expected_kind hint set by Settings UI.</summary>
    private static LocalContentKind? ReadFolderExpectedKind(SqliteConnection conn, int folderId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT expected_kind FROM watched_folders WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", folderId);
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return null;
        var s = result as string;
        if (string.IsNullOrWhiteSpace(s)) return null;
        return LocalContentKindExtensions.ParseWireValue(s);
    }

    /// <summary>
    /// Finds an existing local_series row by normalized name, or creates one.
    /// Returns the series id. Series name is normalized via LocalNormalize.Compact
    /// to coalesce variants ("Person of Interest" / "Person.of.Interest" / etc.).
    /// </summary>
    private static string FindOrCreateSeries(SqliteConnection conn, SqliteTransaction tx, string seriesName, long scanStart)
    {
        var normalized = LocalNormalize.Compact(seriesName);
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT id FROM local_series WHERE LOWER(name) = $n LIMIT 1;";
            probe.Parameters.AddWithValue("$n", normalized);
            var existing = probe.ExecuteScalar();
            if (existing is string id) return id;
        }

        var newId = Guid.NewGuid().ToString("N");
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO local_series (id, name, created_at)
            VALUES ($id, $n, $now);
            """;
        ins.Parameters.AddWithValue("$id", newId);
        ins.Parameters.AddWithValue("$n", normalized);
        ins.Parameters.AddWithValue("$now", scanStart);
        ins.ExecuteNonQuery();
        return newId;
    }

    /// <summary>
    /// Discovers external subtitle files for a video and upserts them into
    /// local_subtitle_files. Existing rows for this video are cleared first
    /// so re-scans see deleted/renamed sub files.
    /// </summary>
    private void UpsertSubtitles(SqliteConnection conn, SqliteTransaction tx, string videoPath)
    {
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM local_subtitle_files WHERE local_file_path = $p;";
            del.Parameters.AddWithValue("$p", videoPath);
            del.ExecuteNonQuery();
        }

        var subs = LocalSubtitleDiscoverer.Discover(videoPath);
        if (subs.Count > 0)
        {
            _logger?.LogDebug("[scan] subtitles discovered for {File}: {Count} ({Langs})",
                System.IO.Path.GetFileName(videoPath), subs.Count,
                string.Join(",", subs.Select(s => s.Language ?? "?")));
        }
        foreach (var sub in subs)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO local_subtitle_files (local_file_path, subtitle_path, language, forced, sdh)
                VALUES ($p, $sp, $lang, $f, $sdh);
                """;
            ins.Parameters.AddWithValue("$p", videoPath);
            ins.Parameters.AddWithValue("$sp", sub.Path);
            ins.Parameters.AddWithValue("$lang", (object?)sub.Language ?? DBNull.Value);
            ins.Parameters.AddWithValue("$f", sub.Forced ? 1 : 0);
            ins.Parameters.AddWithValue("$sdh", sub.Sdh ? 1 : 0);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Probes a video for embedded audio/video/subtitle tracks (only when a
    /// host-supplied IEmbeddedTrackProber is registered) and upserts results
    /// into local_embedded_tracks. Cleared first so re-scans don't accumulate
    /// stale rows when a file is replaced.
    /// </summary>
    private void UpsertEmbeddedTracks(SqliteConnection conn, SqliteTransaction tx, string videoPath)
    {
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM local_embedded_tracks WHERE local_file_path = $p;";
            del.Parameters.AddWithValue("$p", videoPath);
            del.ExecuteNonQuery();
        }

        if (_embeddedTrackProber is null) return;

        IReadOnlyList<EmbeddedTrackInfo> tracks;
        try { tracks = _embeddedTrackProber.Probe(videoPath); }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Embedded track probe failed for {Path}", videoPath);
            return;
        }

        if (tracks.Count > 0)
        {
            _logger?.LogDebug("[scan] embedded tracks for {File}: audio={Audio} video={Video} sub={Sub}",
                System.IO.Path.GetFileName(videoPath),
                tracks.Count(t => t.Kind == EmbeddedTrackKind.Audio),
                tracks.Count(t => t.Kind == EmbeddedTrackKind.Video),
                tracks.Count(t => t.Kind == EmbeddedTrackKind.Subtitle));
        }
        foreach (var t in tracks)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO local_embedded_tracks (local_file_path, kind, stream_index, language, label, codec, is_default)
                VALUES ($p, $k, $idx, $lang, $lbl, $codec, $def);
                """;
            ins.Parameters.AddWithValue("$p", videoPath);
            ins.Parameters.AddWithValue("$k", t.Kind.ToString().ToLowerInvariant());
            ins.Parameters.AddWithValue("$idx", t.StreamIndex);
            ins.Parameters.AddWithValue("$lang", (object?)t.Language ?? DBNull.Value);
            ins.Parameters.AddWithValue("$lbl", (object?)t.Label ?? DBNull.Value);
            ins.Parameters.AddWithValue("$codec", (object?)t.Codec ?? DBNull.Value);
            ins.Parameters.AddWithValue("$def", t.IsDefault ? 1 : 0);
            ins.ExecuteNonQuery();
        }
    }

    private static void UpsertEntity(SqliteConnection conn, SqliteTransaction tx, string uri,
        LocalEntityKind entityType, string title, string? artistName, string? albumName, string? albumUri,
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
