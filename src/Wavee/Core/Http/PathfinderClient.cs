using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Wavee.Core;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;

namespace Wavee.Core.Http;

/// <summary>
/// Client for Spotify's Pathfinder GraphQL API (search, browse, etc).
/// </summary>
/// <remarks>
/// Uses the api-partner.spotify.com endpoint for GraphQL queries.
/// Requires access tokens from login5.
/// </remarks>
public sealed class PathfinderClient : IPathfinderClient
{
    private const int MaxRetries = 3;
    private const string DefaultBaseUrl = "https://api-partner.spotify.com";
    private const string WebPlayerUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";
    private const string XpuiDesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.7680.179 Spotify/1.2.88.483 Safari/537.36";
    private const string XpuiDesktopAppVersion = "896000000";

    private readonly ISession _session;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger? _logger;
    private readonly ClientTokenManager? _clientTokenManager;

    /// <summary>
    /// Creates a new PathfinderClient.
    /// </summary>
    /// <param name="session">Active Spotify session for obtaining access tokens.</param>
    /// <param name="httpClient">HTTP client for making requests.</param>
    /// <param name="baseUrl">Base URL for the Pathfinder API (default: https://api-partner.spotify.com).</param>
    /// <param name="clientTokenManager">Optional client token manager for the client-token header.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    internal PathfinderClient(
        ISession session,
        HttpClient httpClient,
        string? baseUrl = null,
        ClientTokenManager? clientTokenManager = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(httpClient);

        _session = session;
        _httpClient = httpClient;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        _clientTokenManager = clientTokenManager;
        _logger = logger;
    }

    /// <summary>
    /// Executes a generic Pathfinder GraphQL query and deserializes the response.
    /// </summary>
    /// <typeparam name="T">The response type to deserialize into.</typeparam>
    /// <param name="variables">Variables to include in the GraphQL request.</param>
    /// <param name="operationName">The GraphQL operation name.</param>
    /// <param name="sha256Hash">The persisted query SHA-256 hash.</param>
    /// <param name="jsonTypeInfo">The JSON type info for AOT-compatible deserialization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    public async Task<T> QueryAsync<T>(
        object variables,
        string operationName,
        string sha256Hash,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken ct = default)
    {
        // Build JSON request body using Utf8JsonWriter for AOT compatibility
        var jsonBody = BuildRequestJson(variables, operationName, sha256Hash);

        _logger?.LogDebug("Pathfinder query: {Operation}", operationName);

        // Get access token
        var accessToken = await _session.GetAccessTokenAsync(ct);

        // Build HTTP request
        var url = $"{_baseUrl}/pathfinder/v2/query";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var locale = GetEffectiveLocale();
        if (!string.IsNullOrEmpty(locale))
        {
            httpRequest.Headers.AcceptLanguage.ParseAdd(locale);
        }
        var useXpuiDesktop = operationName is PathfinderOperations.QueryArtistOverview;

        // Web-player-only operations need browser-ish headers. Artist overview
        // is intentionally excluded: desktop XPUI returns the music-video
        // mapping pages (relatedMusicVideos/unmappedMusicVideos), while the
        // WebPlayer profile only gives us the top-track has-video hint.
        var useWebPlayer = operationName is PathfinderOperations.QueryNpvArtist
            or PathfinderOperations.QueryNpvEpisode
            or PathfinderOperations.QueryTrackCreditsModal
            or PathfinderOperations.Home
            or PathfinderOperations.FeedBaselineLookup
            or PathfinderOperations.FetchEntitiesForRecentlyPlayed
            or PathfinderOperations.UserLocation
            or PathfinderOperations.ConcertLocationsByLatLon
            or PathfinderOperations.SaveLocation
            or PathfinderOperations.SearchConcertLocations
            or PathfinderOperations.Concert
            or PathfinderOperations.InternalLinkRecommenderTrack
            or PathfinderOperations.GetTrack
            or PathfinderOperations.GetEpisodeOrChapter
            or PathfinderOperations.InternalLinkRecommenderEpisode
            or PathfinderOperations.QueryShowMetadataV2
            or PathfinderOperations.BrowsePage
            or PathfinderOperations.BrowseSection
            or PathfinderOperations.BrowseAll
            or PathfinderOperations.InternalLinkRecommenderShow
            or PathfinderOperations.GetCommentsForEntity
            or PathfinderOperations.GetReplies
            or PathfinderOperations.GetReactions;
        httpRequest.Headers.TryAddWithoutValidation("app-platform", useWebPlayer ? "WebPlayer" : "Win32_x86_64");
        if (useXpuiDesktop)
        {
            if (string.IsNullOrEmpty(locale))
                httpRequest.Headers.AcceptLanguage.ParseAdd("en");
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", XpuiDesktopUserAgent);
            httpRequest.Headers.TryAddWithoutValidation("spotify-app-version", XpuiDesktopAppVersion);
            httpRequest.Headers.TryAddWithoutValidation("origin", "https://xpui.app.spotify.com");
            httpRequest.Headers.TryAddWithoutValidation("referer", "https://xpui.app.spotify.com/");
            httpRequest.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not-A.Brand\";v=\"24\", \"Chromium\";v=\"146\"");
            httpRequest.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            httpRequest.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }
        if (useWebPlayer)
        {
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", WebPlayerUserAgent);
            httpRequest.Headers.TryAddWithoutValidation("spotify-app-version", SpotifyClientIdentity.AppVersionHeader);
            httpRequest.Headers.TryAddWithoutValidation("origin", "https://open.spotify.com");
            httpRequest.Headers.TryAddWithoutValidation("referer", "https://open.spotify.com/");
        }
        // Add client-token header (required by Spotify's Pathfinder API)
        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(ct);
                if (!string.IsNullOrEmpty(clientToken))
                    httpRequest.Headers.Add("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get client token for Pathfinder, continuing without");
            }
        }
        httpRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        // Send with retry
        var response = await SendWithRetryAsync(httpRequest, ct);

        // Handle errors
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(
                    SpClientFailureReason.Unauthorized,
                    "Access token invalid or expired");
            case HttpStatusCode.BadRequest:
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning("Pathfinder request failed: {Error}", errorContent);
                throw new SpClientException(
                    SpClientFailureReason.RequestFailed,
                    $"Invalid request: {errorContent}");
            case HttpStatusCode.TooManyRequests:
                throw new SpClientException(
                    SpClientFailureReason.RateLimited,
                    "Rate limit exceeded");
            case HttpStatusCode.PreconditionFailed:
                throw new SpClientException(
                    SpClientFailureReason.RequestFailed,
                    "Precondition failed — the request could not be completed");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        // Parse response using typed context
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize(responseJson, jsonTypeInfo);

        if (result == null)
        {
            throw new SpClientException(
                SpClientFailureReason.RequestFailed,
                $"Failed to parse {operationName} response");
        }

        if (result is HomeResponse homeResponse)
            homeResponse.RawJson = responseJson;

        return result;
    }

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(
        string query,
        SearchScope scope = SearchScope.All,
        int limit = 10,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        _logger?.LogDebug("Searching for: {Query} (limit={Limit}, offset={Offset})", query, limit, offset);

        PathfinderSearchResponse searchResponse;

        if (scope == SearchScope.Artists)
        {
            var variables = new FilteredSearchVariables
            {
                SearchTerm = query,
                Limit = Math.Max(limit, 30),
                Offset = offset,
                NumberOfTopResults = 20,
                IncludeAudiobooks = true,
                IncludeAuthors = true,
                IncludePreReleases = false
            };

            searchResponse = await QueryAsync(
                variables,
                PathfinderOperations.SearchArtists,
                PathfinderOperations.SearchArtistsHash,
                PathfinderJsonContext.Default.PathfinderSearchResponse,
                cancellationToken);
        }
        else
        {
            var variables = new SearchVariables
            {
                Query = query,
                Limit = limit,
                Offset = offset,
                NumberOfTopResults = limit,
                IncludeAudiobooks = true,
                IncludeArtistHasConcertsField = false,
                IncludePreReleases = true,
                IncludeAuthors = true,
                SectionFilters = ["GENERIC", "VIDEO_CONTENT"]
            };

            searchResponse = await QueryAsync(
                variables,
                PathfinderOperations.SearchTopResultsList,
                PathfinderOperations.SearchTopResultsListHash,
                PathfinderJsonContext.Default.PathfinderSearchResponse,
                cancellationToken);
        }

        var result = SearchResult.FromResponse(searchResponse);

        _logger?.LogDebug(
            "Search completed: {Query} - found {Tracks} tracks, {Artists} artists, {Albums} albums, {Playlists} playlists",
            query,
            result.TotalTracks,
            result.TotalArtists,
            result.TotalAlbums,
            result.TotalPlaylists);

        return result;
    }

    /// <inheritdoc />
    public async Task<UserTopContentResponse> GetUserTopContentAsync(
        int artistLimit = 10, int trackLimit = 10, CancellationToken ct = default)
    {
        var variables = new UserTopContentVariables
        {
            IncludeTopArtists = true,
            TopArtistsInput = new TopContentInput { Offset = 0, Limit = artistLimit, SortBy = "AFFINITY", TimeRange = "SHORT_TERM" },
            IncludeTopTracks = true,
            TopTracksInput = new TopContentInput { Offset = 0, Limit = trackLimit, SortBy = "AFFINITY", TimeRange = "SHORT_TERM" }
        };

        return await QueryAsync(
            variables,
            PathfinderOperations.UserTopContent,
            PathfinderOperations.UserTopContentHash,
            UserTopContentJsonContext.Default.UserTopContentResponse,
            ct);
    }

    /// <summary>
    /// Fetches extracted colors (dark, light, raw) for a list of image URLs.
    /// </summary>
    /// <param name="imageUrls">The image URLs to extract colors from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The extracted colors response.</returns>
    public async Task<ExtractedColorsResponse> GetExtractedColorsAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default)
    {
        var variables = new ExtractedColorsVariables
        {
            ImageUris = imageUrls
        };

        return await QueryAsync(
            variables,
            PathfinderOperations.FetchExtractedColors,
            PathfinderOperations.FetchExtractedColorsHash,
            ExtractedColorsJsonContext.Default.ExtractedColorsResponse,
            ct);
    }

    /// <summary>
    /// Fetches the user's personalized home feed.
    /// </summary>
    public async Task<HomeResponse> GetHomeAsync(
        int sectionItemsLimit = 10, string? facet = null, CancellationToken ct = default)
    {
        var variables = new HomeVariables
        {
            SectionItemsLimit = sectionItemsLimit,
            Facet = facet ?? ""
        };

        var hash = PathfinderOperations.HomeWithFacetHash;

        return await QueryAsync(
            variables,
            PathfinderOperations.Home,
            hash,
            HomeJsonContext.Default.HomeResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<BrowseAllResponse> GetBrowseAllAsync(CancellationToken ct = default)
    {
        var variables = new BrowseAllVariables();
        return await QueryAsync(
            variables,
            PathfinderOperations.BrowseAll,
            PathfinderOperations.BrowseAllHash,
            BrowseAllJsonContext.Default.BrowseAllResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<FeedBaselineLookupResponse> GetFeedBaselineLookupAsync(
        IReadOnlyList<string> uris, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uris);

        var variables = new FeedBaselineLookupVariables
        {
            Uris = uris.Where(uri => !string.IsNullOrWhiteSpace(uri)).Distinct(StringComparer.Ordinal).ToList()
        };

        return await QueryAsync(
            variables,
            PathfinderOperations.FeedBaselineLookup,
            PathfinderOperations.FeedBaselineLookupHash,
            FeedBaselineLookupJsonContext.Default.FeedBaselineLookupResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<ArtistOverviewResponse> GetArtistOverviewAsync(
        string artistUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artistUri);

        var variables = new ArtistOverviewVariables(artistUri, GetEffectiveLocale() ?? "");

        return await QueryAsync(
            variables,
            PathfinderOperations.QueryArtistOverview,
            PathfinderOperations.QueryArtistOverviewHash,
            ArtistOverviewJsonContext.Default.ArtistOverviewResponse,
            ct);
    }

    /// <inheritdoc />
    public Task<ArtistDiscographyResponse> GetArtistDiscographyAllAsync(
        string artistUri, int offset = 0, int limit = 50, CancellationToken ct = default)
        => GetArtistDiscographyInternalAsync(artistUri,
            PathfinderOperations.QueryArtistDiscographyAll,
            PathfinderOperations.QueryArtistDiscographyAllHash,
            offset, limit, ct);

    /// <inheritdoc />
    public Task<ArtistDiscographyResponse> GetArtistDiscographyAlbumsAsync(
        string artistUri, int offset = 0, int limit = 20, CancellationToken ct = default)
        => GetArtistDiscographyInternalAsync(artistUri,
            PathfinderOperations.QueryArtistDiscographyAlbums,
            PathfinderOperations.QueryArtistDiscographyAlbumsHash,
            offset, limit, ct);

    /// <inheritdoc />
    public Task<ArtistDiscographyResponse> GetArtistDiscographySinglesAsync(
        string artistUri, int offset = 0, int limit = 20, CancellationToken ct = default)
        => GetArtistDiscographyInternalAsync(artistUri,
            PathfinderOperations.QueryArtistDiscographySingles,
            PathfinderOperations.QueryArtistDiscographySinglesHash,
            offset, limit, ct);

    /// <inheritdoc />
    public Task<ArtistDiscographyResponse> GetArtistDiscographyCompilationsAsync(
        string artistUri, int offset = 0, int limit = 20, CancellationToken ct = default)
        => GetArtistDiscographyInternalAsync(artistUri,
            PathfinderOperations.QueryArtistDiscographyCompilations,
            PathfinderOperations.QueryArtistDiscographyCompilationsHash,
            offset, limit, ct);

    /// <inheritdoc />
    public async Task<AlbumTracksResponse> GetAlbumTracksAsync(
        string albumUri, int offset = 0, int limit = 300, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumUri);

        var variables = new AlbumTracksVariables
        {
            Uri = albumUri,
            Offset = offset,
            Limit = limit
        };

        return await QueryAsync(
            variables,
            PathfinderOperations.QueryAlbumTracks,
            PathfinderOperations.QueryAlbumTracksHash,
            AlbumTracksJsonContext.Default.AlbumTracksResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<GetAlbumResponse> GetAlbumAsync(
        string albumUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumUri);

        var variables = new GetAlbumVariables { Uri = albumUri };

        return await QueryAsync(
            variables,
            PathfinderOperations.GetAlbum,
            PathfinderOperations.GetAlbumHash,
            GetAlbumJsonContext.Default.GetAlbumResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<FetchPlaylistResponse> FetchPlaylistAsync(
        string playlistUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        var variables = new FetchPlaylistVariables { Uri = playlistUri };

        return await QueryAsync(
            variables,
            PathfinderOperations.FetchPlaylist,
            PathfinderOperations.FetchPlaylistHash,
            FetchPlaylistJsonContext.Default.FetchPlaylistResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<AlbumMerchResponse> GetAlbumMerchAsync(
        string albumUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumUri);

        return await QueryAsync(
            new AlbumMerchVariables { Uri = albumUri },
            PathfinderOperations.QueryAlbumMerch,
            PathfinderOperations.QueryAlbumMerchHash,
            AlbumMerchJsonContext.Default.AlbumMerchResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<NpvArtistResponse> GetNpvArtistAsync(
        string artistUri, string trackUri,
        int contributorsLimit = 10, int contributorsOffset = 0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artistUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);

        var variables = new NpvArtistVariables(artistUri, trackUri, contributorsLimit, contributorsOffset);

        return await QueryAsync(
            variables,
            PathfinderOperations.QueryNpvArtist,
            PathfinderOperations.QueryNpvArtistHash,
            NpvArtistJsonContext.Default.NpvArtistResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<GetEpisodeOrChapterResponse> GetNpvEpisodeAsync(
        string episodeUri,
        int numberOfChapters = 10,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        var variables = new QueryNpvEpisodeVariables(episodeUri, numberOfChapters);

        return await QueryAsync(
            variables,
            PathfinderOperations.QueryNpvEpisode,
            PathfinderOperations.QueryNpvEpisodeHash,
            GetEpisodeOrChapterJsonContext.Default.GetEpisodeOrChapterResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<SeoRecommendedTracksResponse> GetSeoRecommendedTracksAsync(
        string trackUri, int limit = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);

        var variables = new SeoRecommendedTracksVariables(trackUri, limit);

        return await QueryAsync(
            variables,
            PathfinderOperations.InternalLinkRecommenderTrack,
            PathfinderOperations.InternalLinkRecommenderTrackHash,
            SeoRecommendedTracksJsonContext.Default.SeoRecommendedTracksResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<GetTrackResponse> GetTrackAsync(
        string trackUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);

        var variables = new GetTrackVariables(trackUri);

        return await QueryAsync(
            variables,
            PathfinderOperations.GetTrack,
            PathfinderOperations.GetTrackHash,
            GetTrackJsonContext.Default.GetTrackResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<GetEpisodeOrChapterResponse> GetEpisodeOrChapterAsync(
        string episodeUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        var variables = new GetEpisodeOrChapterVariables(episodeUri);

        return await QueryAsync(
            variables,
            PathfinderOperations.GetEpisodeOrChapter,
            PathfinderOperations.GetEpisodeOrChapterHash,
            GetEpisodeOrChapterJsonContext.Default.GetEpisodeOrChapterResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<SeoRecommendedEpisodesResponse> GetSeoRecommendedEpisodesAsync(
        string episodeUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        var variables = new SeoRecommendedEpisodesVariables(episodeUri);

        return await QueryAsync(
            variables,
            PathfinderOperations.InternalLinkRecommenderEpisode,
            PathfinderOperations.InternalLinkRecommenderEpisodeHash,
            SeoRecommendedEpisodesJsonContext.Default.SeoRecommendedEpisodesResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<QueryShowMetadataV2Response> GetShowMetadataAsync(
        string showUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showUri);

        var variables = new QueryShowMetadataV2Variables(showUri);

        return await QueryAsync(
            variables,
            PathfinderOperations.QueryShowMetadataV2,
            PathfinderOperations.QueryShowMetadataV2Hash,
            QueryShowMetadataV2JsonContext.Default.QueryShowMetadataV2Response,
            ct);
    }

    /// <inheritdoc />
    public async Task<BrowsePageResponse> GetBrowsePageAsync(
        string uri,
        int pageOffset = 0,
        int pageLimit = 10,
        int sectionOffset = 0,
        int sectionLimit = 10,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        var variables = new BrowsePageVariables
        {
            Uri = uri,
            PagePagination = new BrowsePagination
            {
                Offset = Math.Max(0, pageOffset),
                Limit = Math.Max(1, pageLimit)
            },
            SectionPagination = new BrowsePagination
            {
                Offset = Math.Max(0, sectionOffset),
                Limit = Math.Max(1, sectionLimit)
            }
        };

        return await QueryAsync(
            variables,
            PathfinderOperations.BrowsePage,
            PathfinderOperations.BrowsePageHash,
            BrowsePageJsonContext.Default.BrowsePageResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<BrowseSectionResponse> GetBrowseSectionAsync(
        string uri,
        int offset = 0,
        int limit = 20,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        var variables = new BrowseSectionVariables
        {
            Uri = uri,
            Pagination = new BrowsePagination
            {
                Offset = Math.Max(0, offset),
                Limit = Math.Max(1, limit)
            }
        };

        return await QueryAsync(
            variables,
            PathfinderOperations.BrowseSection,
            PathfinderOperations.BrowseSectionHash,
            BrowseSectionJsonContext.Default.BrowseSectionResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<QueryNpvEpisodeChaptersResponse> GetEpisodeChaptersAsync(
        string episodeUri, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        var variables = new QueryNpvEpisodeChaptersVariables(episodeUri, offset, limit);

        return await QueryAsync(
            variables,
            PathfinderOperations.QueryNpvEpisodeChapters,
            PathfinderOperations.QueryNpvEpisodeChaptersHash,
            QueryNpvEpisodeChaptersJsonContext.Default.QueryNpvEpisodeChaptersResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<InternalLinkRecommenderShowResponse> GetSeoRecommendedShowsAsync(
        string showUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showUri);

        var variables = new InternalLinkRecommenderShowVariables(showUri);

        return await QueryAsync(
            variables,
            PathfinderOperations.InternalLinkRecommenderShow,
            PathfinderOperations.InternalLinkRecommenderShowHash,
            InternalLinkRecommenderShowJsonContext.Default.InternalLinkRecommenderShowResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<EntityCommentsResponse> GetCommentsForEntityAsync(
        string entityUri, string? token = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityUri);

        var variables = new EntityCommentsVariables(entityUri, token);

        return await QueryAsync(
            variables,
            PathfinderOperations.GetCommentsForEntity,
            PathfinderOperations.GetCommentsForEntityHash,
            EntityCommentsJsonContext.Default.EntityCommentsResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<CommentRepliesResponse> GetCommentRepliesAsync(
        string commentUri, string? pageToken = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);

        var variables = new CommentRepliesVariables(commentUri, pageToken);

        return await QueryAsync(
            variables,
            PathfinderOperations.GetReplies,
            PathfinderOperations.GetRepliesHash,
            CommentRepliesJsonContext.Default.CommentRepliesResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<CommentReactionsResponse> GetCommentReactionsAsync(
        string uri, string? token = null, string? reactionUnicode = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        var variables = new CommentReactionsVariables(uri, token, reactionUnicode);

        return await QueryAsync(
            variables,
            PathfinderOperations.GetReactions,
            PathfinderOperations.GetReactionsHash,
            CommentReactionsJsonContext.Default.CommentReactionsResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<TrackCreditsResponse> GetTrackCreditsAsync(
        string trackUri, int contributorsLimit = 100, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);

        var variables = new TrackCreditsVariables(trackUri, contributorsLimit);

        return await QueryAsync(
            variables,
            PathfinderOperations.QueryTrackCreditsModal,
            PathfinderOperations.QueryTrackCreditsModalHash,
            TrackCreditsJsonContext.Default.TrackCreditsResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<UserLocationResponse> GetUserLocationAsync(CancellationToken ct = default)
    {
        return await QueryAsync(
            new EmptyVariables(),
            PathfinderOperations.UserLocation,
            PathfinderOperations.UserLocationHash,
            UserLocationJsonContext.Default.UserLocationResponse,
            ct);
    }

    private string? GetEffectiveLocale()
    {
        var sessionLocale = _session.GetPreferredLocale();
        if (!string.IsNullOrEmpty(sessionLocale))
        {
            return sessionLocale;
        }

        var userData = _session.GetUserData();
        return userData?.PreferredLocale;
    }

    /// <inheritdoc />
    public async Task<ConcertLocationsResponse> GetConcertLocationByLatLonAsync(
        double lat, double lon, CancellationToken ct = default)
    {
        return await QueryAsync(
            new ConcertLocationsByLatLonVariables { Lat = lat, Lon = lon },
            PathfinderOperations.ConcertLocationsByLatLon,
            PathfinderOperations.ConcertLocationsByLatLonHash,
            ConcertLocationsJsonContext.Default.ConcertLocationsResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<SaveLocationResponse> SaveLocationAsync(string geonameId, CancellationToken ct = default)
    {
        return await QueryAsync(
            new SaveLocationVariables { GeonameId = geonameId },
            PathfinderOperations.SaveLocation,
            PathfinderOperations.SaveLocationHash,
            SaveLocationJsonContext.Default.SaveLocationResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<ConcertDetailResponse> GetConcertAsync(string concertUri, CancellationToken ct = default)
    {
        return await QueryAsync(
            new ConcertVariables { Uri = concertUri },
            PathfinderOperations.Concert,
            PathfinderOperations.ConcertHash,
            ConcertDetailJsonContext.Default.ConcertDetailResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<ConcertLocationsResponse> SearchConcertLocationsAsync(
        string query, CancellationToken ct = default)
    {
        return await QueryAsync(
            new SearchConcertLocationsVariables { Query = query },
            PathfinderOperations.SearchConcertLocations,
            PathfinderOperations.SearchConcertLocationsHash,
            ConcertLocationsJsonContext.Default.ConcertLocationsResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<RecentSearchesResponse> GetRecentSearchesAsync(int limit = 50, CancellationToken ct = default)
    {
        return await QueryAsync(
            new RecentSearchesVariables { Limit = limit },
            PathfinderOperations.RecentSearches,
            PathfinderOperations.RecentSearchesHash,
            PathfinderJsonContext.Default.RecentSearchesResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<SearchSuggestionsResponse> GetSearchSuggestionsAsync(
        string query, int limit = 30, CancellationToken ct = default)
    {
        return await QueryAsync(
            new SearchSuggestionsVariables { Query = query, Limit = limit, NumberOfTopResults = limit },
            PathfinderOperations.SearchSuggestions,
            PathfinderOperations.SearchSuggestionsHash,
            PathfinderJsonContext.Default.SearchSuggestionsResponse,
            ct);
    }

    /// <inheritdoc />
    public async Task<RecentlyPlayedEntitiesResponse> FetchEntitiesForRecentlyPlayedAsync(
        IReadOnlyList<string> uris, CancellationToken ct = default)
    {
        var variables = new RecentlyPlayedEntitiesVariables
        {
            Uris = new List<string>(uris)
        };

        return await QueryAsync(
            variables,
            PathfinderOperations.FetchEntitiesForRecentlyPlayed,
            PathfinderOperations.FetchEntitiesForRecentlyPlayedHash,
            RecentlyPlayedEntitiesJsonContext.Default.RecentlyPlayedEntitiesResponse,
            ct);
    }

    private async Task<ArtistDiscographyResponse> GetArtistDiscographyInternalAsync(
        string artistUri, string operationName, string sha256Hash,
        int offset, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artistUri);

        var variables = new ArtistDiscographyVariables
        {
            Uri = artistUri,
            Offset = offset,
            Limit = limit
        };

        return await QueryAsync(
            variables,
            operationName,
            sha256Hash,
            ArtistDiscographyJsonContext.Default.ArtistDiscographyResponse,
            ct);
    }

    /// <summary>
    /// Builds the JSON request body for a Pathfinder GraphQL query.
    /// Uses Utf8JsonWriter for AOT compatibility instead of reflection-based serialization.
    /// </summary>
    private static string BuildRequestJson(object variables, string operationName, string sha256Hash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            // Write variables - serialize using the known type's context or raw element
            writer.WritePropertyName("variables");
            var variablesJson = SerializeVariables(variables);
            variablesJson.WriteTo(writer);

            writer.WriteString("operationName", operationName);

            writer.WriteStartObject("extensions");
            writer.WriteStartObject("persistedQuery");
            writer.WriteNumber("version", 1);
            writer.WriteString("sha256Hash", sha256Hash);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Serializes variables to a JsonElement using the appropriate source-generated context.
    /// </summary>
    private static JsonElement SerializeVariables(object variables)
    {
        byte[] json;

        if (variables is SearchVariables sv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(sv, PathfinderJsonContext.Default.SearchVariables);
        }
        else if (variables is FilteredSearchVariables fsv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(fsv, PathfinderJsonContext.Default.FilteredSearchVariables);
        }
        else if (variables is UserTopContentVariables utc)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(utc, PathfinderVariablesJsonContext.Default.UserTopContentVariables);
        }
        else if (variables is ExtractedColorsVariables ecv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(ecv, PathfinderVariablesJsonContext.Default.ExtractedColorsVariables);
        }
        else if (variables is HomeVariables hv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(hv, HomeVariablesJsonContext.Default.HomeVariables);
        }
        else if (variables is FeedBaselineLookupVariables fbl)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(fbl, FeedBaselineLookupVariablesJsonContext.Default.FeedBaselineLookupVariables);
        }
        else if (variables is ArtistOverviewVariables aov)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(aov, ArtistOverviewVariablesJsonContext.Default.ArtistOverviewVariables);
        }
        else if (variables is ArtistDiscographyVariables adv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(adv, ArtistDiscographyVariablesJsonContext.Default.ArtistDiscographyVariables);
        }
        else if (variables is AlbumTracksVariables atv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(atv, AlbumTracksVariablesJsonContext.Default.AlbumTracksVariables);
        }
        else if (variables is GetAlbumVariables gav)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(gav, GetAlbumVariablesJsonContext.Default.GetAlbumVariables);
        }
        else if (variables is FetchPlaylistVariables fpv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(fpv, FetchPlaylistVariablesJsonContext.Default.FetchPlaylistVariables);
        }
        else if (variables is EmptyVariables ev)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(ev, EmptyVariablesJsonContext.Default.EmptyVariables);
        }
        else if (variables is ConcertLocationsByLatLonVariables clv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(clv, ConcertLocationsByLatLonVariablesJsonContext.Default.ConcertLocationsByLatLonVariables);
        }
        else if (variables is SaveLocationVariables slv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(slv, SaveLocationVariablesJsonContext.Default.SaveLocationVariables);
        }
        else if (variables is SearchConcertLocationsVariables sclv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(sclv, SearchConcertLocationsVariablesJsonContext.Default.SearchConcertLocationsVariables);
        }
        else if (variables is ConcertVariables cv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(cv, ConcertVariablesJsonContext.Default.ConcertVariables);
        }
        else if (variables is RecentSearchesVariables rsv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(rsv, RecentSearchesVariablesJsonContext.Default.RecentSearchesVariables);
        }
        else if (variables is SearchSuggestionsVariables ssv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(ssv, SearchSuggestionsVariablesJsonContext.Default.SearchSuggestionsVariables);
        }
        else if (variables is RecentlyPlayedEntitiesVariables rpev)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(rpev, RecentlyPlayedEntitiesVariablesJsonContext.Default.RecentlyPlayedEntitiesVariables);
        }
        else if (variables is AlbumMerchVariables amv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(amv, AlbumMerchVariablesJsonContext.Default.AlbumMerchVariables);
        }
        else if (variables is NpvArtistVariables npv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(npv, NpvArtistVariablesJsonContext.Default.NpvArtistVariables);
        }
        else if (variables is QueryNpvEpisodeVariables qnev)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(qnev, QueryNpvEpisodeVariablesJsonContext.Default.QueryNpvEpisodeVariables);
        }
        else if (variables is SeoRecommendedTracksVariables srtv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(srtv, SeoRecommendedTracksVariablesJsonContext.Default.SeoRecommendedTracksVariables);
        }
        else if (variables is GetTrackVariables gtv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(gtv, GetTrackVariablesJsonContext.Default.GetTrackVariables);
        }
        else if (variables is GetEpisodeOrChapterVariables geocv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(geocv, GetEpisodeOrChapterVariablesJsonContext.Default.GetEpisodeOrChapterVariables);
        }
        else if (variables is SeoRecommendedEpisodesVariables srev)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(srev, SeoRecommendedEpisodesVariablesJsonContext.Default.SeoRecommendedEpisodesVariables);
        }
        else if (variables is EntityCommentsVariables ecv2)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(ecv2, EntityCommentsVariablesJsonContext.Default.EntityCommentsVariables);
        }
        else if (variables is CommentRepliesVariables crv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(crv, CommentRepliesVariablesJsonContext.Default.CommentRepliesVariables);
        }
        else if (variables is CommentReactionsVariables crv2)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(crv2, CommentReactionsVariablesJsonContext.Default.CommentReactionsVariables);
        }
        else if (variables is TrackCreditsVariables tcv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(tcv, TrackCreditsVariablesJsonContext.Default.TrackCreditsVariables);
        }
        else if (variables is QueryShowMetadataV2Variables qsmv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(qsmv, QueryShowMetadataV2VariablesJsonContext.Default.QueryShowMetadataV2Variables);
        }
        else if (variables is BrowsePageVariables bpv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(bpv, BrowsePageVariablesJsonContext.Default.BrowsePageVariables);
        }
        else if (variables is BrowseSectionVariables bsv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(bsv, BrowseSectionVariablesJsonContext.Default.BrowseSectionVariables);
        }
        else if (variables is BrowseAllVariables bav)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(bav, BrowseAllJsonContext.Default.BrowseAllVariables);
        }
        else if (variables is QueryNpvEpisodeChaptersVariables qnecv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(qnecv, QueryNpvEpisodeChaptersVariablesJsonContext.Default.QueryNpvEpisodeChaptersVariables);
        }
        else if (variables is InternalLinkRecommenderShowVariables ilrsv)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(ilrsv, InternalLinkRecommenderShowVariablesJsonContext.Default.InternalLinkRecommenderShowVariables);
        }
        else
        {
            throw new ArgumentException($"Unknown variables type: {variables.GetType().Name}. Register it in SerializeVariables.");
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Sends an HTTP request with retry logic for transient failures.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                // Clone the request for retries (request can only be sent once)
                HttpRequestMessage requestToSend;
                if (attempt > 0)
                {
                    requestToSend = await CloneRequestAsync(request);
                }
                else
                {
                    requestToSend = request;
                }

                var response = await _httpClient.SendAsync(requestToSend, cancellationToken);

                // Check for retryable status codes
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.PreconditionFailed)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    _logger?.LogWarning(
                        "Pathfinder request failed with {StatusCode}, retrying in {Delay}s (attempt {Attempt}/{MaxRetries})",
                        response.StatusCode,
                        delay.TotalSeconds,
                        attempt + 1,
                        MaxRetries);

                    if (attempt < MaxRetries - 1)
                    {
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                }

                return response;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger?.LogWarning(ex,
                    "Pathfinder HTTP request failed (attempt {Attempt}/{MaxRetries})",
                    attempt + 1,
                    MaxRetries);

                if (attempt < MaxRetries - 1)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
            }
        }

        throw new SpClientException(
            SpClientFailureReason.RequestFailed,
            $"Pathfinder request failed after {MaxRetries} retries",
            lastException);
    }

    /// <summary>
    /// Clones an HTTP request for retry (original request cannot be reused after sending).
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        // Copy headers
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content
        if (request.Content != null)
        {
            var content = await request.Content.ReadAsStringAsync();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }
}
