using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Wavee.Local.Classification;
using Wavee.Local.Models;

namespace Wavee.Local.Enrichment;

/// <summary>
/// Default <see cref="ILocalEnrichmentService"/> implementation. Owns a
/// single serial background worker; sources lookups from
/// <see cref="LocalLibraryService"/> and writes results back through it.
///
/// <para>The worker only runs while <see cref="ITmdbTokenStore.HasToken"/>
/// is true. When the user clears their token, the worker drains and stops;
/// when they paste a new one and verify it, the worker spins back up. The
/// service never auto-enqueues — every sync is an explicit user action
/// (Sync buttons on Shows / Movies / detail pages, "Run now" in Settings).</para>
/// </summary>
public sealed class LocalEnrichmentService : ILocalEnrichmentService, IDisposable
{
    private readonly LocalLibraryService _library;
    private readonly ITmdbTokenStore _tokenStore;
    private readonly ISpotifyTrackSearcher? _spotifySearcher;
    private TmdbAdapter? _tmdb;
    private readonly HttpClient _http;
    private readonly Subject<EnrichmentProgress> _progress = new();
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private CancellationTokenSource _workerCts = new();
    private readonly IDisposable? _tokenSub;
    private readonly ILogger? _logger;
    private readonly object _workerGate = new();
    private Task? _worker;
    private bool _paused;
    private int _pending, _matched, _noMatch, _failed;

    // Show-batch optimisation: one TMDB lookup + one season fetch per series
    // covers every episode that shares the same parsed series name. Dict is
    // keyed by series_id; value is (tmdbId, episodes-by-S0xE0x) or null on
    // no-match. Cleared on each scoped enqueue cycle and on token-clear.
    private readonly Dictionary<string, ShowBatchEntry?> _showBatch = new(StringComparer.Ordinal);
    private sealed record ShowBatchEntry(int TmdbId, Dictionary<(int Season, int Episode), TmdbEpisode> Episodes);

    public LocalEnrichmentService(
        LocalLibraryService library,
        ITmdbTokenStore tokenStore,
        ISpotifyTrackSearcher? spotifySearcher = null,
        HttpClient? httpClient = null,
        ILogger<LocalEnrichmentService>? logger = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _spotifySearcher = spotifySearcher;
        _http = httpClient ?? new HttpClient();
        _logger = logger;

        // Worker runs whenever any adapter is available. The TMDB adapter is
        // built on-demand from the token store (HasTokenChanged toggles it);
        // the Spotify searcher is injected once and stays. Music enqueues can
        // process without a TMDB token, and vice-versa for movies / TV.
        if (_tokenStore.HasToken || _spotifySearcher is not null)
            _ = EnsureWorkerAsync();
        _tokenSub = _tokenStore.HasTokenChanged.Subscribe(present =>
        {
            if (present) _ = EnsureWorkerAsync();
            else _ = OnTokenClearedAsync();
        });
    }

    public IObservable<EnrichmentProgress> Progress => _progress;
    public IObservable<string> Matched => _matchedSubject;
    private readonly Subject<string> _matchedSubject = new();
    public bool IsEnabled => _tokenStore.HasToken && !_paused;

    public Task EnqueueAsync(string trackUri, CancellationToken ct = default)
    {
        if (!IsEnabled) return Task.CompletedTask;
        lock (_queued)
        {
            if (!_queued.Add(trackUri)) return Task.CompletedTask;
        }
        _queue.Enqueue(trackUri);
        Interlocked.Increment(ref _pending);
        EmitProgress(trackUri);
        try { _queueSignal.Release(); } catch (SemaphoreFullException) { /* signal already raised */ }
        return Task.CompletedTask;
    }

    public async Task EnqueueAllPendingAsync(CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        lock (_showBatch) _showBatch.Clear();
        var all = await _library.GetAllTracksAsync(ct);
        foreach (var row in all)
        {
            if (ct.IsCancellationRequested) return;
            await EnqueueAsync(row.TrackUri, ct);
        }
    }

    /// <summary>
    /// Drives the "Sync with TMDB" toolbar button on
    /// <c>LocalShowsPage</c>. Enqueues every episode of every series; when
    /// <paramref name="forceResync"/> is false, items with a <c>tmdb_id</c>
    /// already populated are skipped (so re-clicking Sync is cheap).
    /// </summary>
    public async Task EnqueueAllShowsAsync(bool forceResync, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        lock (_showBatch) _showBatch.Clear();
        var rows = await _library.GetShowEpisodesForEnrichmentAsync(forceResync, ct);
        foreach (var trackUri in rows)
        {
            if (ct.IsCancellationRequested) return;
            await EnqueueAsync(trackUri, ct);
        }
    }

    /// <summary>
    /// Drives the per-show "Sync" button on <c>LocalShowDetailPage</c>.
    /// </summary>
    public async Task EnqueueShowAsync(string seriesId, bool forceResync, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        lock (_showBatch)
        {
            // Force-resync clears any cached series mapping so TMDB gets a
            // fresh search even if a previous run mapped it.
            if (forceResync) _showBatch.Remove(seriesId);
        }
        var rows = await _library.GetEpisodesForSeriesAsync(seriesId, forceResync, ct);
        foreach (var trackUri in rows)
        {
            if (ct.IsCancellationRequested) return;
            await EnqueueAsync(trackUri, ct);
        }
    }

    /// <summary>
    /// Drives the "Sync with TMDB" toolbar button on
    /// <c>LocalMoviesPage</c>.
    /// </summary>
    public async Task EnqueueAllMoviesAsync(bool forceResync, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        var rows = await _library.GetMoviesForEnrichmentAsync(forceResync, ct);
        foreach (var trackUri in rows)
        {
            if (ct.IsCancellationRequested) return;
            await EnqueueAsync(trackUri, ct);
        }
    }

    /// <summary>
    /// Drives the "Sync with Spotify" toolbar button on <c>LocalMusicPage</c>.
    /// Enqueues every Music row; when <paramref name="forceResync"/> is false,
    /// items with a non-null <c>spotify_track_uri</c> are skipped so re-clicking
    /// Sync stays cheap. Doesn't require a TMDB token — Spotify auth is enough.
    /// </summary>
    public async Task EnqueueAllMusicAsync(bool forceResync, CancellationToken ct = default)
    {
        // Music enrichment doesn't require a TMDB token, so IsEnabled
        // (which gates on the TMDB token) isn't the right guard — check the
        // Spotify searcher directly.
        if (_spotifySearcher is null) return;
        var rows = await _library.GetMusicTracksForEnrichmentAsync(forceResync, ct);
        foreach (var trackUri in rows)
        {
            if (ct.IsCancellationRequested) return;
            // Bypass IsEnabled in EnqueueAsync — it would block when there's
            // no TMDB token. Use the raw queue path so music can sync solo.
            lock (_queued)
            {
                if (!_queued.Add(trackUri)) continue;
            }
            _queue.Enqueue(trackUri);
            Interlocked.Increment(ref _pending);
            EmitProgress(trackUri);
            try { _queueSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>
    /// One-shot TMDB person fetch for <c>LocalPersonDetailPage</c>. Returns
    /// null when no token is configured, when TMDB can't find the person,
    /// or on any network failure (UI degrades gracefully). Counts as an
    /// explicit-user-click TMDB hit — never auto-fired from a timer / scan.
    /// </summary>
    public async Task<LocalPersonInfo?> GetTmdbPersonAsync(int personId, CancellationToken ct = default)
    {
        if (!_tokenStore.HasToken) return null;
        await EnsureWorkerAsync();
        var adapter = _tmdb;
        if (adapter is null) return null;
        var resp = await adapter.GetPersonAsync(personId, ct);
        if (resp is null) return null;
        var imageUrl = string.IsNullOrEmpty(resp.ProfilePath)
            ? null
            : TmdbAdapter.BuildImageUrl("w500", resp.ProfilePath!);
        return new LocalPersonInfo(
            Id: resp.Id != 0 ? resp.Id : personId,
            Name: resp.Name ?? string.Empty,
            Biography: resp.Biography,
            ProfileImageUrl: imageUrl,
            KnownForDepartment: resp.KnownForDepartment,
            Birthday: resp.Birthday,
            Deathday: resp.Deathday,
            PlaceOfBirth: resp.PlaceOfBirth);
    }

    public Task ForceRefreshAsync(string trackUri, CancellationToken ct = default) =>
        EnqueueAsync(trackUri, ct);

    public Task PauseAsync() { _paused = true; return Task.CompletedTask; }
    public Task ResumeAsync() { _paused = false; return Task.CompletedTask; }

    public async Task ClearCachedLookupsAsync()
    {
        // Real DB scrub: drop negative-match TTL rows and reset every file's
        // tmdb_id / musicbrainz_id / enrichment_state. Next Sync click on any
        // surface re-queries from scratch.
        await _library.ClearAllEnrichmentResultsAsync();
        Interlocked.Exchange(ref _matched, 0);
        Interlocked.Exchange(ref _noMatch, 0);
        Interlocked.Exchange(ref _failed, 0);
        lock (_showBatch) _showBatch.Clear();
        EmitProgress(null);
        _logger?.LogInformation("[enrich] cleared cached lookups");
    }

    /// <summary>
    /// Spins the worker task up if it isn't already running, and builds the
    /// TMDB adapter when a token is present. Idempotent — safe to call from
    /// the token-changed subscription and the constructor.
    /// </summary>
    private async Task EnsureWorkerAsync()
    {
        var token = await _tokenStore.GetTokenAsync();

        lock (_workerGate)
        {
            if (!string.IsNullOrWhiteSpace(token))
                _tmdb = new TmdbAdapter(_http, token!, _logger);

            if (_worker is { IsCompleted: false }) return; // already running

            // Start the worker if any adapter is available (TMDB token OR
            // Spotify searcher). Music can enrich without TMDB and vice-versa.
            if (_tmdb is null && _spotifySearcher is null) return;

            _workerCts.Dispose();
            _workerCts = new CancellationTokenSource();
            _worker = Task.Run(() => RunWorkerAsync(_workerCts.Token));
        }
    }

    /// <summary>
    /// When the user clears their TMDB token, drop the TMDB adapter so future
    /// movie / TV enqueues no-match cleanly. Keep the worker alive — music
    /// enrichment still works via Spotify (no TMDB token required).
    /// </summary>
    private Task OnTokenClearedAsync()
    {
        lock (_workerGate) { _tmdb = null; }
        // Drain pending TMDB items (movie / TV) from the queue. Music URIs
        // stay queued — they don't depend on TMDB. Cheap O(queue-depth) walk.
        var keep = new System.Collections.Generic.List<string>();
        while (_queue.TryDequeue(out var uri)) keep.Add(uri);
        foreach (var uri in keep)
        {
            // No way to peek at kind without a DB hit, so re-enqueue all and
            // let ProcessOneAsync's "no _tmdb adapter" check skip TMDB items.
            _queue.Enqueue(uri);
        }
        lock (_showBatch) _showBatch.Clear();
        EmitProgress(null);
        return Task.CompletedTask;
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(ct);
                if (_paused)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    try { _queueSignal.Release(); } catch (SemaphoreFullException) { }
                    continue;
                }

                if (!_queue.TryDequeue(out var trackUri)) continue;
                lock (_queued) _queued.Remove(trackUri);

                EmitProgress(trackUri);
                try
                {
                    await ProcessOneAsync(trackUri, ct);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    _logger?.LogWarning(ex, "Enrichment failed for {Uri}", trackUri);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                    EmitProgress(null);
                    // Notify subscribers (e.g. the facade) that this row has
                    // had its metadata touched — fires on both match + no-match
                    // so the UI re-reads the row's fresh state either way.
                    try { _matchedSubject.OnNext(trackUri); } catch { }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Enrichment worker loop fault");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// Processes a single track URI:
    ///   - Reads its row + extended fields (auto_kind, series_id, season, episode, year)
    ///   - Dispatches to TmdbAdapter (TvEpisode / Movie) or MusicBrainzAdapter (Music / MusicVideo)
    ///   - Writes results back through LocalLibraryService.UpsertEnrichmentResultAsync
    /// </summary>
    private async Task ProcessOneAsync(string trackUri, CancellationToken ct)
    {
        var detail = await _library.GetEnrichmentRowAsync(trackUri, ct);
        if (detail is null)
        {
            _logger?.LogWarning("[enrich] no DB row for {Uri} — skipping", trackUri);
            Interlocked.Increment(ref _noMatch);
            return;
        }

        // Note: we DO NOT short-circuit on the negative-match TTL anymore.
        // Every enrichment cycle is now an explicit user-initiated Sync
        // (per the explicit-sync model — no background auto-enrichment).
        // Honoring the TTL would silently no-op a deliberate "try again"
        // click, leaving the user staring at gray placeholders with no log
        // explaining why. The provider's own rate limits + the show-batch
        // optimisation keep total HTTP work modest even on rage-clicks.
        _logger?.LogInformation("[enrich] processing {Kind} for {Uri} (title='{Title}', artist='{Artist}')",
            detail.AutoKind, trackUri, detail.Title, detail.Artist);

        try
        {
            switch (detail.AutoKind)
            {
                case LocalContentKind.Movie:
                    await EnrichMovieAsync(detail, ct);
                    break;
                case LocalContentKind.TvEpisode:
                    await EnrichTvEpisodeAsync(detail, ct);
                    break;
                case LocalContentKind.Music:
                case LocalContentKind.MusicVideo:
                    await EnrichMusicAsync(detail, ct);
                    break;
                default:
                    Interlocked.Increment(ref _noMatch);
                    break;
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            _logger?.LogDebug(ex, "Enrichment failed for {Uri}", trackUri);
        }
    }

    private async Task EnrichMovieAsync(EnrichmentRow row, CancellationToken ct)
    {
        if (_tmdb is null)
        {
            Interlocked.Increment(ref _noMatch);
            return;
        }

        // `row.Title` from the entities table is typically the raw filename
        // ("Once We Were Us 2025 1080p Korean WEB-DL HEVC x265 BONE.mkv")
        // because ATL doesn't extract titles from .mkv. Run the filename
        // parser to derive a clean title + year before hitting TMDB —
        // searching for the raw filename never matches.
        var fileName = System.IO.Path.GetFileName(row.FilePath);
        string searchTitle;
        int? searchYear;
        if (Classification.LocalFilenameParser.TryParseMovie(fileName, out var parsedTitle, out var parsedYear))
        {
            searchTitle = parsedTitle;
            searchYear = parsedYear ?? row.MovieYear;
        }
        else
        {
            searchTitle = row.Title ?? System.IO.Path.GetFileNameWithoutExtension(row.FilePath);
            searchYear = row.MovieYear;
        }

        if (string.IsNullOrWhiteSpace(searchTitle))
        {
            Interlocked.Increment(ref _noMatch);
            return;
        }

        _logger?.LogDebug("[enrich] movie query: '{Title}' ({Year}) (file='{File}')",
            searchTitle, searchYear, fileName);
        var match = await _tmdb.FindMovieAsync(searchTitle, searchYear, ct);
        if (match is null)
        {
            await _library.RecordEnrichmentNegativeAsync(row.FilePath, "tmdb", ct);
            Interlocked.Increment(ref _noMatch);
            return;
        }

        string? posterHash = null;
        if (!string.IsNullOrEmpty(match.PosterPath))
        {
            var url = TmdbAdapter.BuildImageUrl("w500", match.PosterPath!);
            posterHash = await DownloadAndStoreAsync(url, ct);
        }

        // AOT-safe JSON construction — anonymous-type Serialize trips
        // IL2026/IL3050 because reflection can't enumerate the synthetic type.
        var overrides = new JsonObject
        {
            ["Overview"] = match.Overview,
            ["Year"] = ParseYear(match.ReleaseDate) ?? row.MovieYear,
            ["Title"] = match.Title,
            ["VoteAverage"] = match.VoteAverage,
        }.ToJsonString();

        await _library.UpsertEnrichmentResultAsync(
            row.TrackUri, tmdbId: match.Id, musicBrainzId: null,
            metadataOverridesJson: overrides, posterArtworkHash: posterHash, ct);
        // Read paths select `entities.title` + `local_files.movie_year`
        // directly — push the canonical TMDB title there so the UI shows
        // "Once We Were Us" instead of the raw filename.
        if (!string.IsNullOrWhiteSpace(match.Title))
            await _library.UpdateMovieMetadataAsync(row.TrackUri, match.Title!, ParseYear(match.ReleaseDate) ?? row.MovieYear, ct);

        // v20: fetch full movie details + credits in one call. Powers the
        // hero meta line (runtime / genres / ★ rating), the tagline / overview
        // blocks, and the cast strip on the Movie Detail page.
        var details = await _tmdb.GetMovieDetailsAsync(match.Id, ct);
        if (details is not null)
        {
            string? backdropHash = !string.IsNullOrEmpty(details.BackdropPath)
                ? await DownloadAndStoreAsync(TmdbAdapter.BuildImageUrl("w1280", details.BackdropPath!), ct)
                : null;
            var genresCsv = details.Genres is { Count: > 0 }
                ? string.Join(", ", details.Genres.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)))
                : null;
            await _library.UpdateMovieDetailsAsync(
                row.TrackUri,
                overview: !string.IsNullOrWhiteSpace(details.Overview) ? details.Overview : match.Overview,
                tagline: !string.IsNullOrWhiteSpace(details.Tagline) ? details.Tagline : null,
                runtimeMinutes: details.Runtime > 0 ? details.Runtime : null,
                genresCsv: string.IsNullOrEmpty(genresCsv) ? null : genresCsv,
                voteAverage: details.VoteAverage > 0 ? details.VoteAverage : null,
                backdropHash: backdropHash,
                ct);

            // Cast — top 10 in TMDB billing order. Download profile images
            // sequentially via the existing artwork cache so they're served
            // from disk on the detail page without re-hitting i.tmdb on every
            // render.
            if (details.Credits?.Cast is { Count: > 0 } cast)
            {
                const int CastLimit = 10;
                var ordered = cast.OrderBy(c => c.Order).Take(CastLimit).ToList();
                var castInputs = new List<LocalLibraryService.MovieCastInput>(ordered.Count);
                foreach (var c in ordered)
                {
                    string? profileHash = !string.IsNullOrEmpty(c.ProfilePath)
                        ? await DownloadAndStoreAsync(TmdbAdapter.BuildImageUrl("w185", c.ProfilePath!), ct)
                        : null;
                    castInputs.Add(new LocalLibraryService.MovieCastInput(
                        PersonId: c.Id > 0 ? c.Id : null,
                        Name: c.Name ?? c.OriginalName ?? string.Empty,
                        Character: c.Character,
                        ProfileHash: profileHash,
                        Order: c.Order));
                }
                if (castInputs.Count > 0)
                {
                    await _library.UpsertMovieCastAsync(row.TrackUri, castInputs, ct);
                    _logger?.LogInformation("[enrich] cast upserted: '{Title}' — {Count} principals",
                        match.Title, castInputs.Count);
                }
            }
        }

        _logger?.LogInformation("[enrich] movie matched: '{Title}' ({Year}) → TMDB {TmdbId}",
            match.Title, match.ReleaseDate, match.Id);
        Interlocked.Increment(ref _matched);
    }

    private async Task EnrichTvEpisodeAsync(EnrichmentRow row, CancellationToken ct)
    {
        if (_tmdb is null || string.IsNullOrEmpty(row.SeriesId)
            || row.SeasonNumber is null || row.EpisodeNumber is null
            || string.IsNullOrWhiteSpace(row.SeriesName))
        {
            Interlocked.Increment(ref _noMatch);
            return;
        }

        ShowBatchEntry? batch;
        bool fetchSeason;
        lock (_showBatch)
        {
            if (!_showBatch.TryGetValue(row.SeriesId!, out batch))
            {
                fetchSeason = true;
                _showBatch[row.SeriesId!] = null; // tentatively reserve
            }
            else
            {
                fetchSeason = false;
            }
        }

        if (fetchSeason)
        {
            var show = await _tmdb.FindTvShowAsync(row.SeriesName!, ct);
            if (show is null)
            {
                lock (_showBatch) _showBatch[row.SeriesId!] = null;
                await _library.RecordEnrichmentNegativeAsync(row.FilePath, "tmdb", ct);
                Interlocked.Increment(ref _noMatch);
                return;
            }

            var season = await _tmdb.GetSeasonAsync(show.Id, row.SeasonNumber!.Value, ct);
            var dict = new Dictionary<(int, int), TmdbEpisode>();
            if (season?.Episodes is not null)
            {
                foreach (var ep in season.Episodes)
                    dict[(ep.SeasonNumber, ep.EpisodeNumber)] = ep;
            }
            batch = new ShowBatchEntry(show.Id, dict);
            lock (_showBatch) _showBatch[row.SeriesId!] = batch;

            // Persist series-level enrichment (poster, backdrop, overview) once.
            string? seriesPosterHash = !string.IsNullOrEmpty(show.PosterPath)
                ? await DownloadAndStoreAsync(TmdbAdapter.BuildImageUrl("w500", show.PosterPath!), ct)
                : null;
            string? seriesBackdropHash = !string.IsNullOrEmpty(show.BackdropPath)
                ? await DownloadAndStoreAsync(TmdbAdapter.BuildImageUrl("w1280", show.BackdropPath!), ct)
                : null;
            await _library.UpsertSeriesEnrichmentAsync(row.SeriesId!, show.Id, show.Overview,
                seriesPosterHash, seriesBackdropHash, ct);

            // v19 — persist the FULL season roster, not just the matched
            // episode. TMDB's season-details response (already in-hand from
            // GetSeasonAsync above) carries name + overview + runtime +
            // air_date for every episode in the season. The Show Detail
            // page joins against this so missing-from-disk episodes still
            // render with their canonical metadata.
            if (season?.Episodes is { Count: > 0 })
            {
                foreach (var ep in season.Episodes)
                {
                    await _library.UpsertSeriesEpisodeAsync(
                        seriesId: row.SeriesId!,
                        season: ep.SeasonNumber,
                        episode: ep.EpisodeNumber,
                        tmdbId: ep.Id,
                        title: ep.Name,
                        overview: ep.Overview,
                        runtimeMinutes: ep.Runtime,
                        airDate: ep.AirDate,
                        stillHash: null,        // updated per-file below when matched
                        ct);
                }
                _logger?.LogInformation("[enrich] roster upserted: {Series} S{Season} — {Count} episodes",
                    row.SeriesName, row.SeasonNumber, season.Episodes.Count);
            }

            // v19/v21 — fetch the show's totals + rich details (tagline,
            // status, dates, genres, rating, networks) + principal cast in
            // one HTTP call via ?append_to_response=credits. Powers the
            // hero meta strip + cast strip on the Show Detail page.
            // Best-effort; null result just leaves the fields blank.
            var details = await _tmdb.GetTvDetailsAsync(show.Id, ct);
            if (details is not null)
            {
                await _library.UpsertSeriesSummaryAsync(
                    row.SeriesId!,
                    totalSeasons: details.NumberOfSeasons > 0 ? details.NumberOfSeasons : null,
                    totalEpisodes: details.NumberOfEpisodes > 0 ? details.NumberOfEpisodes : null,
                    ct);
                _logger?.LogInformation("[enrich] show summary: {Series} — {Seasons} seasons / {Episodes} episodes",
                    row.SeriesName, details.NumberOfSeasons, details.NumberOfEpisodes);

                // v21 — persist tagline/status/dates/genres/vote/networks
                var genresCsv = details.Genres is { Count: > 0 }
                    ? string.Join(", ", details.Genres.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)))
                    : null;
                var networksCsv = details.Networks is { Count: > 0 }
                    ? string.Join(", ", details.Networks.Select(n => n.Name).Where(n => !string.IsNullOrEmpty(n)))
                    : null;
                await _library.UpdateSeriesDetailsAsync(
                    row.SeriesId!,
                    tagline: !string.IsNullOrWhiteSpace(details.Tagline) ? details.Tagline : null,
                    status: !string.IsNullOrWhiteSpace(details.Status) ? details.Status : null,
                    firstAirDate: !string.IsNullOrWhiteSpace(details.FirstAirDate) ? details.FirstAirDate : null,
                    lastAirDate: !string.IsNullOrWhiteSpace(details.LastAirDate) ? details.LastAirDate : null,
                    genresCsv: string.IsNullOrEmpty(genresCsv) ? null : genresCsv,
                    voteAverage: details.VoteAverage > 0 ? details.VoteAverage : null,
                    networksCsv: string.IsNullOrEmpty(networksCsv) ? null : networksCsv,
                    ct);

                // v21 — top-10 cast in TMDB billing order. Mirrors the movie
                // path exactly: download w185 profile images via the shared
                // artwork cache, wipe-and-replace the show's cast list.
                if (details.Credits?.Cast is { Count: > 0 } cast)
                {
                    const int CastLimit = 10;
                    var ordered = cast.OrderBy(c => c.Order).Take(CastLimit).ToList();
                    var castInputs = new List<LocalLibraryService.MovieCastInput>(ordered.Count);
                    foreach (var c in ordered)
                    {
                        string? profileHash = !string.IsNullOrEmpty(c.ProfilePath)
                            ? await DownloadAndStoreAsync(TmdbAdapter.BuildImageUrl("w185", c.ProfilePath!), ct)
                            : null;
                        castInputs.Add(new LocalLibraryService.MovieCastInput(
                            PersonId: c.Id > 0 ? c.Id : null,
                            Name: c.Name ?? c.OriginalName ?? string.Empty,
                            Character: c.Character,
                            ProfileHash: profileHash,
                            Order: c.Order));
                    }
                    if (castInputs.Count > 0)
                    {
                        await _library.UpsertShowCastAsync(row.SeriesId!, castInputs, ct);
                        _logger?.LogInformation("[enrich] show cast upserted: '{Series}' — {Count} principals",
                            row.SeriesName, castInputs.Count);
                    }
                }
            }
        }

        if (batch is null
            || !batch.Episodes.TryGetValue((row.SeasonNumber!.Value, row.EpisodeNumber!.Value), out var episode))
        {
            await _library.RecordEnrichmentNegativeAsync(row.FilePath, "tmdb", ct);
            Interlocked.Increment(ref _noMatch);
            return;
        }

        string? stillHash = !string.IsNullOrEmpty(episode.StillPath)
            ? await DownloadAndStoreAsync(TmdbAdapter.BuildImageUrl("w300", episode.StillPath!), ct)
            : null;

        var overrides = new JsonObject
        {
            ["EpisodeTitle"] = episode.Name,
            ["Overview"] = episode.Overview,
            ["AirDate"] = episode.AirDate,
            ["Runtime"] = episode.Runtime,
        }.ToJsonString();
        await _library.UpsertEnrichmentResultAsync(
            row.TrackUri, tmdbId: episode.Id, musicBrainzId: null,
            metadataOverridesJson: overrides, posterArtworkHash: stillHash, ct);
        // Push the canonical episode title into local_files.episode_title —
        // GetShowSeasonsAsync reads that column directly, so without this
        // the row keeps showing the raw filename.
        if (!string.IsNullOrWhiteSpace(episode.Name))
            await _library.UpdateEpisodeTitleAsync(row.TrackUri, episode.Name!, ct);
        // v19 — also write the downloaded still hash onto the roster row so
        // the show-detail JOIN can serve the still for this episode regardless
        // of whether the local_files cover link is present.
        if (!string.IsNullOrEmpty(stillHash))
        {
            await _library.UpsertSeriesEpisodeAsync(
                seriesId: row.SeriesId!,
                season: episode.SeasonNumber,
                episode: episode.EpisodeNumber,
                tmdbId: episode.Id,
                title: episode.Name,
                overview: episode.Overview,
                runtimeMinutes: episode.Runtime,
                airDate: episode.AirDate,
                stillHash: stillHash,
                ct);
        }
        _logger?.LogInformation("[enrich] episode matched: '{Series}' S{Season}E{Episode} '{EpisodeTitle}' → TMDB {TmdbId}",
            row.SeriesName, row.SeasonNumber, row.EpisodeNumber, episode.Name, episode.Id);
        Interlocked.Increment(ref _matched);
    }

    // Match thresholds for Spotify track linking. Tuned to be strict
    // enough that "Artist X — Remix vs Original" doesn't get the wrong
    // URI, but lenient enough that diacritics / "(feat.)" suffixes /
    // case differences pass.
    private const double MinTitleSimilarity  = 0.85;
    private const double MinArtistSimilarity = 0.70;
    private const long   MaxDurationSkewMs   = 5_000;

    private async Task EnrichMusicAsync(EnrichmentRow row, CancellationToken ct)
    {
        if (_spotifySearcher is null
            || string.IsNullOrWhiteSpace(row.Artist) || string.IsNullOrWhiteSpace(row.Title))
        {
            Interlocked.Increment(ref _noMatch);
            return;
        }

        var query = $"track:\"{row.Title}\" artist:\"{row.Artist}\"";
        IReadOnlyList<SpotifyTrackMatch> hits;
        try
        {
            hits = await _spotifySearcher.SearchTracksAsync(query, limit: 5, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Spotify search failed for '{Artist}' — '{Title}'", row.Artist, row.Title);
            Interlocked.Increment(ref _failed);
            return;
        }

        if (hits.Count == 0)
        {
            await _library.RecordEnrichmentNegativeAsync(row.FilePath, "spotify", ct);
            Interlocked.Increment(ref _noMatch);
            return;
        }

        // Score each hit; pick the best above thresholds.
        SpotifyTrackMatch? best = null;
        double bestScore = 0;
        foreach (var hit in hits)
        {
            var titleSim = StringSimilarity.Ratio(
                NormaliseForMatch(row.Title!), NormaliseForMatch(hit.Title));
            var artistSim = string.IsNullOrEmpty(hit.FirstArtistName)
                ? 0
                : StringSimilarity.Ratio(
                    NormaliseForMatch(row.Artist!), NormaliseForMatch(hit.FirstArtistName));
            var durationOk = row.DurationMs <= 0
                          || hit.DurationMs <= 0
                          || Math.Abs(row.DurationMs - hit.DurationMs) <= MaxDurationSkewMs;

            if (titleSim < MinTitleSimilarity || artistSim < MinArtistSimilarity || !durationOk)
                continue;

            var combined = titleSim * 0.5 + artistSim * 0.5;
            if (combined > bestScore) { best = hit; bestScore = combined; }
        }

        if (best is null)
        {
            await _library.RecordEnrichmentNegativeAsync(row.FilePath, "spotify", ct);
            Interlocked.Increment(ref _noMatch);
            return;
        }

        // CoverImageUri is already resolved to an HTTPS i.scdn.co URL by the
        // PathfinderSpotifyTrackSearcher impl (the Pathfinder layer owns the
        // spotify:image:… → https:// conversion since Wavee.Local stays
        // Spotify-agnostic).
        await _library.UpsertSpotifyMatchAsync(
            row.TrackUri,
            spotifyTrackUri: best.TrackUri,
            spotifyAlbumUri: best.AlbumUri,
            spotifyArtistUri: best.FirstArtistUri,
            spotifyCoverUrl: best.CoverImageUri,
            ct);

        _logger?.LogInformation("[enrich] music matched: '{Artist}' — '{Title}' → {Uri} (score {Score:F2})",
            row.Artist, row.Title, best.TrackUri, bestScore);
        Interlocked.Increment(ref _matched);
    }

    /// <summary>
    /// Lower-cases, ASCII-folds, strips parenthesised "feat./remix/etc." groups
    /// for fuzzy comparison. Cheap — operates on small strings (track titles).
    /// </summary>
    private static string NormaliseForMatch(string s)
    {
        var span = s.AsSpan();
        var sb = new System.Text.StringBuilder(span.Length);
        var inParen = 0;
        foreach (var c in span)
        {
            if (c == '(' || c == '[') { inParen++; continue; }
            if (c == ')' || c == ']') { if (inParen > 0) inParen--; continue; }
            if (inParen > 0) continue;
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c)) { sb.Append(' '); continue; }
            sb.Append(char.ToLowerInvariant(c));
        }
        // Collapse double spaces, trim.
        var result = sb.ToString();
        while (result.Contains("  ")) result = result.Replace("  ", " ");
        return result.Trim();
    }

    /// <summary>Fetches an image URL with HttpClient, persists to the artwork
    /// cache, and returns the SHA-1 hash. Returns null on any failure — and
    /// LOUDLY logs every failure mode at Warning so users / devs can see why
    /// posters didn't land.</summary>
    private async Task<string?> DownloadAndStoreAsync(string url, CancellationToken ct)
    {
        try
        {
            // Build an explicit request so we DON'T inherit any default
            // headers that might have leaked from TmdbAdapter (e.g. an
            // Authorization: Bearer that image.tmdb.org won't accept).
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("[enrich] image fetch returned HTTP {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
            {
                _logger?.LogWarning("[enrich] image fetch returned 0 bytes for {Url}", url);
                return null;
            }
            var mime = resp.Content.Headers.ContentType?.MediaType;
            var hash = _library.StoreArtworkBytes(bytes, mime);
            _logger?.LogInformation("[enrich] stored artwork {Hash} ({Bytes} B, {Mime}) from {Url}",
                hash, bytes.Length, mime, url);
            return hash;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[enrich] image fetch failed for {Url}", url);
            return null;
        }
    }

    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrEmpty(releaseDate)) return null;
        if (releaseDate.Length >= 4 && int.TryParse(releaseDate.AsSpan(0, 4), out var y)) return y;
        return null;
    }

    private void EmitProgress(string? currentlyProcessing)
    {
        try
        {
            _progress.OnNext(new EnrichmentProgress(
                Pending: _pending,
                Matched: _matched,
                NoMatch: _noMatch,
                Failed: _failed,
                CurrentlyProcessing: currentlyProcessing));
        }
        catch { /* sink errors swallowed — progress is best-effort */ }
    }

    public void Dispose()
    {
        try { _tokenSub?.Dispose(); } catch { }
        try { _workerCts.Cancel(); } catch { }
        try { _worker?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _workerCts.Dispose();
        _queueSignal.Dispose();
        _progress.Dispose();
        _matchedSubject.Dispose();
    }
}
