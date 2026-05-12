using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Wavee.Local.Enrichment;

/// <summary>
/// Source-generated JSON context for TMDB responses. Top-level + internal so
/// the System.Text.Json source generator emits a partial implementation with
/// the typed accessor properties used as <c>TmdbJsonContext.Default.TmdbMovieSearchResponse</c>.
/// (Nested private partial classes silently fail the generator.)
/// </summary>
[JsonSerializable(typeof(TmdbMovieSearchResponse))]
[JsonSerializable(typeof(TmdbTvSearchResponse))]
[JsonSerializable(typeof(TmdbSeasonResponse))]
[JsonSerializable(typeof(TmdbTvDetailsResponse))]
[JsonSerializable(typeof(TmdbMovieDetailsResponse))]
[JsonSerializable(typeof(TmdbPersonResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class TmdbJsonContext : JsonSerializerContext { }

/// <summary>
/// TMDB (themoviedb.org) v4 client for movie + TV-series + season lookups.
/// Designed for serial use behind a single worker — internal token-bucket
/// pacing keeps us under the 40 req/sec hard cap with comfortable headroom.
///
/// <para>API surface kept minimal for v1: search-movie, search-tv, get-season.
/// Image URLs are constructed against TMDB's public image CDN; the caller
/// downloads the bytes and feeds them through <see cref="LocalArtworkCache"/>.</para>
/// </summary>
internal sealed class TmdbAdapter
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p";

    private readonly HttpClient _http;
    private readonly string _bearerToken;
    private readonly ILogger? _logger;
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;
    private static readonly TimeSpan Slot = TimeSpan.FromMilliseconds(30); // ≈33 req/sec

    public TmdbAdapter(HttpClient http, string bearerToken, ILogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("Bearer token required.", nameof(bearerToken));
        _bearerToken = bearerToken;
        _logger = logger;
        // Authorization is set per-request via HttpRequestMessage rather than
        // on _http.DefaultRequestHeaders — the underlying HttpClient is shared
        // with MusicBrainzAdapter (different host, no auth header) so mutating
        // shared defaults would leak the token across hosts.
    }

    /// <summary>
    /// Verifies a candidate token against TMDB's <c>/authentication</c>
    /// endpoint. Returns <c>(true, null)</c> on a 200 with <c>success=true</c>,
    /// <c>(false, message)</c> otherwise. Builds its own transient request,
    /// safe to call before any token is stored.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> VerifyAsync(string bearerToken, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return (false, "Token is empty.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/authentication");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (false, "Token rejected by TMDB (HTTP 401). Check that you copied the v4 read-access token, not the v3 API key.");
            if (!resp.IsSuccessStatusCode)
                return (false, $"TMDB returned HTTP {(int)resp.StatusCode}.");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<TmdbMovieResult?> FindMovieAsync(string title, int? year, CancellationToken ct)
    {
        await WaitForSlot(ct);
        var url = $"{BaseUrl}/search/movie?query={Uri.EscapeDataString(title)}"
                + (year is { } y ? $"&year={y}" : "");
        return await GetAsync(url, TmdbJsonContext.Default.TmdbMovieSearchResponse, ct) is { } parsed
            ? parsed.Results?.FirstOrDefault()
            : null;
    }

    public async Task<TmdbTvResult?> FindTvShowAsync(string seriesName, CancellationToken ct)
    {
        await WaitForSlot(ct);
        var url = $"{BaseUrl}/search/tv?query={Uri.EscapeDataString(seriesName)}";
        return await GetAsync(url, TmdbJsonContext.Default.TmdbTvSearchResponse, ct) is { } parsed
            ? parsed.Results?.FirstOrDefault()
            : null;
    }

    public async Task<TmdbSeasonResponse?> GetSeasonAsync(int tvId, int seasonNumber, CancellationToken ct)
    {
        await WaitForSlot(ct);
        var url = $"{BaseUrl}/tv/{tvId}/season/{seasonNumber}";
        return await GetAsync(url, TmdbJsonContext.Default.TmdbSeasonResponse, ct);
    }

    /// <summary>
    /// Fetches show-level summary stats (<c>number_of_seasons</c>,
    /// <c>number_of_episodes</c>, per-season summaries). Drives the Show
    /// Detail page's "X of Y episodes / S of T seasons" hero meta.
    /// Called once per show during enrichment; the result is cached on
    /// <c>local_series.tmdb_last_fetched_at</c>.
    /// </summary>
    public async Task<TmdbTvDetailsResponse?> GetTvDetailsAsync(int tvId, CancellationToken ct)
    {
        await WaitForSlot(ct);
        // v21: append_to_response=credits returns details + principal cast
        // in one HTTP call. Same pattern used by GetMovieDetailsAsync.
        var url = $"{BaseUrl}/tv/{tvId}?append_to_response=credits";
        return await GetAsync(url, TmdbJsonContext.Default.TmdbTvDetailsResponse, ct);
    }

    /// <summary>
    /// Fetches the rich movie payload — overview, tagline, runtime,
    /// genres, vote average, backdrop — plus the principal cast list in
    /// one HTTP call via <c>append_to_response=credits</c>. Powers the
    /// Movie Detail page's hero meta + cast strip.
    /// </summary>
    public async Task<TmdbMovieDetailsResponse?> GetMovieDetailsAsync(int movieId, CancellationToken ct)
    {
        await WaitForSlot(ct);
        var url = $"{BaseUrl}/movie/{movieId}?append_to_response=credits";
        return await GetAsync(url, TmdbJsonContext.Default.TmdbMovieDetailsResponse, ct);
    }

    /// <summary>
    /// Fetches one person's biography + profile image + headline metadata.
    /// Powers <c>LocalPersonDetailPage</c> when the user clicks a cast portrait.
    /// Single explicit-user-click call site — no batching, no background queue.
    /// </summary>
    public async Task<TmdbPersonResponse?> GetPersonAsync(int personId, CancellationToken ct)
    {
        await WaitForSlot(ct);
        var url = $"{BaseUrl}/person/{personId}";
        return await GetAsync(url, TmdbJsonContext.Default.TmdbPersonResponse, ct);
    }

    /// <summary>Builds a TMDB image URL at the requested size, e.g. "w500".</summary>
    public static string BuildImageUrl(string size, string path) =>
        path is { Length: > 0 } ? $"{ImageBase}/{size}{(path.StartsWith('/') ? "" : "/")}{path}" : string.Empty;

    private async Task<T?> GetAsync<T>(string url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bearerToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var json = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync(json, typeInfo, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "TMDB GET failed: {Url}", url);
            return null;
        }
    }

    private async Task WaitForSlot(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextSlot)
        {
            var wait = _nextSlot - now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }
        _nextSlot = DateTimeOffset.UtcNow + Slot;
    }
}

#pragma warning disable CS8618 // JSON deserialization populates non-nullable members

internal sealed class TmdbMovieSearchResponse
{
    public List<TmdbMovieResult>? Results { get; set; }
}
internal sealed class TmdbMovieResult
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? ReleaseDate { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public double VoteAverage { get; set; }
}
internal sealed class TmdbTvSearchResponse
{
    public List<TmdbTvResult>? Results { get; set; }
}
internal sealed class TmdbTvResult
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? FirstAirDate { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
}
internal sealed class TmdbSeasonResponse
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public List<TmdbEpisode>? Episodes { get; set; }
}
internal sealed class TmdbEpisode
{
    public int Id { get; set; }
    public int EpisodeNumber { get; set; }
    public int SeasonNumber { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? StillPath { get; set; }
    public string? AirDate { get; set; }
    public int? Runtime { get; set; }
}

internal sealed class TmdbTvDetailsResponse
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public int NumberOfSeasons { get; set; }
    public int NumberOfEpisodes { get; set; }
    public List<TmdbSeasonSummary>? Seasons { get; set; }
    // v21 — extras carried when ?append_to_response=credits is requested.
    public string? Tagline { get; set; }
    public string? Status { get; set; }
    public string? FirstAirDate { get; set; }
    public string? LastAirDate { get; set; }
    public double VoteAverage { get; set; }
    public List<TmdbGenre>? Genres { get; set; }
    public List<TmdbNetwork>? Networks { get; set; }
    public TmdbMovieCredits? Credits { get; set; }   // same shape as movies — cast list
}
internal sealed class TmdbNetwork
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? LogoPath { get; set; }
    public string? OriginCountry { get; set; }
}
internal sealed class TmdbSeasonSummary
{
    public int Id { get; set; }
    public int SeasonNumber { get; set; }
    public string? Name { get; set; }
    public int EpisodeCount { get; set; }
    public string? PosterPath { get; set; }
}

internal sealed class TmdbMovieDetailsResponse
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public string? ReleaseDate { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public int? Runtime { get; set; }
    public double VoteAverage { get; set; }
    public List<TmdbGenre>? Genres { get; set; }
    public TmdbMovieCredits? Credits { get; set; }
}
internal sealed class TmdbGenre
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
internal sealed class TmdbMovieCredits
{
    public List<TmdbCastMember>? Cast { get; set; }
}
internal sealed class TmdbCastMember
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? OriginalName { get; set; }
    public string? Character { get; set; }
    public int Order { get; set; }
    public string? ProfilePath { get; set; }
}

internal sealed class TmdbPersonResponse
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Biography { get; set; }
    public string? Birthday { get; set; }
    public string? Deathday { get; set; }
    public string? PlaceOfBirth { get; set; }
    public string? KnownForDepartment { get; set; }
    public string? ProfilePath { get; set; }
    public double Popularity { get; set; }
}

#pragma warning restore CS8618
