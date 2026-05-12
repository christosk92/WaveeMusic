using Wavee.Local.Models;

namespace Wavee.Local.Enrichment;

/// <summary>
/// Background enrichment for indexed local files.
/// Movies + TV → TMDB. Music + MusicVideo → MusicBrainz + CoverArtArchive.
///
/// <para>Implementations should:</para>
/// <list type="bullet">
///   <item>Run lookups on a serial worker per provider (TMDB up to ~40 req/sec,
///         MusicBrainz contractually 1 req/sec)</item>
///   <item>Batch by series for TV shows (1 search + 1 season fetch covers
///         all episodes)</item>
///   <item>Cache positive matches as tmdb_id / musicbrainz_id on local_files</item>
///   <item>Cache negative matches in local_enrichment_negatives with 30-day TTL</item>
///   <item>Write poster/backdrop bytes through LocalArtworkCache so the same
///         wavee-artwork://hash URIs work everywhere else</item>
///   <item>No-op gracefully when no TMDB token is configured
///         (<see cref="ITmdbTokenStore.HasToken"/> is false) or when the
///         host disables enrichment in Settings</item>
/// </list>
/// </summary>
public interface ILocalEnrichmentService
{
    /// <summary>Queue one file for enrichment. Idempotent.</summary>
    Task EnqueueAsync(string trackUri, CancellationToken ct = default);

    /// <summary>Queue every Pending row in the library — bulk "Run now"
    /// from Settings. Explicit user action only.</summary>
    Task EnqueueAllPendingAsync(CancellationToken ct = default);

    /// <summary>Queue every TV episode for enrichment. Drives the
    /// "Sync with TMDB" toolbar button on <c>LocalShowsPage</c>.</summary>
    Task EnqueueAllShowsAsync(bool forceResync, CancellationToken ct = default);

    /// <summary>Queue every episode of one series. Drives the per-show
    /// Sync button on <c>LocalShowDetailPage</c>.</summary>
    Task EnqueueShowAsync(string seriesId, bool forceResync, CancellationToken ct = default);

    /// <summary>Queue every movie. Drives the "Sync with TMDB" toolbar
    /// button on <c>LocalMoviesPage</c>.</summary>
    Task EnqueueAllMoviesAsync(bool forceResync, CancellationToken ct = default);

    /// <summary>Queue every music track. Drives the "Sync with Spotify"
    /// toolbar button on <c>LocalMusicPage</c>. Doesn't depend on the TMDB
    /// token — Spotify auth alone is enough.</summary>
    Task EnqueueAllMusicAsync(bool forceResync, CancellationToken ct = default);

    /// <summary>Force a re-enrich for one file (clears the negative cache for it).</summary>
    Task ForceRefreshAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// One-shot TMDB person fetch for <c>LocalPersonDetailPage</c>. Returns
    /// null when no token is configured or on any failure. Explicit-user-click
    /// call site only — never enqueued / batched.
    /// </summary>
    Task<LocalPersonInfo?> GetTmdbPersonAsync(int personId, CancellationToken ct = default);

    /// <summary>Live progress for the UI's "Enriching X of Y" ribbon.</summary>
    IObservable<EnrichmentProgress> Progress { get; }

    /// <summary>
    /// Fires whenever <see cref="ProcessOneAsync"/> finishes — pass-through
    /// for the track URI that just had its row touched (success or negative).
    /// Detail pages subscribe via the facade so they re-read fresh metadata
    /// without needing a manual back-and-forward navigation.
    /// </summary>
    IObservable<string> Matched { get; }

    /// <summary>Enabled at runtime. Default true unless secrets are stubs.</summary>
    bool IsEnabled { get; }

    /// <summary>Stop the background loop and drop the queue.</summary>
    Task PauseAsync();

    /// <summary>Resume background processing.</summary>
    Task ResumeAsync();

    /// <summary>Wipe all enrichment-related caches (TMDB / MB ids + negative cache).</summary>
    Task ClearCachedLookupsAsync();
}
