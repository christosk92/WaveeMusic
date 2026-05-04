using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// View-facing service for the Show detail page. Wraps the underlying
/// PathfinderClient + ExtendedMetadataClient so the page never has to reach
/// into the network layer directly.
/// </summary>
public interface IPodcastService
{
    /// <summary>
    /// Hero metadata + first-page episode URIs + palette. Drives everything
    /// in the sticky left panel and seeds the episode list with URIs.
    /// </summary>
    Task<ShowDetailDto?> GetShowDetailAsync(string showUri, CancellationToken ct = default);

    /// <summary>
    /// Resolve a batch of episode URIs to fully populated <see cref="ShowEpisodeDto"/>s.
    /// Uses the shared extended-metadata store/client (protobuf, cached) under
    /// the hood and merges in played-state from local episode-progress rows.
    /// </summary>
    Task<IReadOnlyList<ShowEpisodeDto>> GetEpisodesAsync(
        IReadOnlyList<string> episodeUris, CancellationToken ct = default);

    /// <summary>"More podcasts you might like" — bound to the footer shelf.</summary>
    Task<IReadOnlyList<ShowRecommendationDto>> GetRecommendedShowsAsync(
        string showUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches a podcast browse page: root podcasts, a category page, or another
    /// browse page returned from Spotify's category cards.
    /// </summary>
    Task<PodcastBrowsePageDto?> GetPodcastBrowsePageAsync(
        string uri,
        int pageOffset = 0,
        int pageLimit = 10,
        int sectionOffset = 0,
        int sectionLimit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches one paged podcast browse section, usually a show-producing shelf.
    /// </summary>
    Task<PodcastBrowseSectionDto?> GetPodcastBrowseSectionAsync(
        string uri,
        int offset = 0,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>
    /// Chapter / display-segment list for a podcast episode — used by the
    /// player position bar to render discrete segments. Returns an empty list
    /// when the episode has no chapters or the call fails (callers shouldn't
    /// branch on null). Uses the dedicated paginated <c>queryNpvEpisodeChapters</c>
    /// endpoint (full chapter list); the older <c>queryNpvEpisode</c> path used
    /// by <c>TrackDetailsViewModel</c> caps at 10 entries.
    /// </summary>
    Task<IReadOnlyList<EpisodeChapterVm>> GetEpisodeChaptersAsync(
        string episodeUri, CancellationToken ct = default);
}
