using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Podcast / episode surface: episode reads, episode detail, comments + replies
/// + reactions, episode resume-point progress. Carved out of
/// <c>ILibraryDataService</c> in Phase 2. Most comment-side methods are stubs
/// today (Spotify's mutation endpoints aren't captured); the read paths are
/// fully wired.
/// </summary>
public interface IPodcastEpisodeService
{
    /// <summary>
    /// Returns the user's saved episodes (the "Your Episodes" surface).
    /// </summary>
    Task<IReadOnlyList<LibraryEpisodeDto>> GetYourEpisodesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns episodes ordered by most-recent listen activity (Herodotus
    /// resume-point cache + extended-metadata fallback). Limit defaults to 50.
    /// </summary>
    Task<IReadOnlyList<LibraryEpisodeDto>> GetRecentlyPlayedPodcastEpisodesAsync(
        int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Returns the user's followed podcast shows + counts of saved episodes
    /// per show.
    /// </summary>
    Task<IReadOnlyList<LibraryPodcastShowDto>> GetPodcastShowsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns one page of the comments thread for an episode.
    /// </summary>
    Task<PodcastEpisodeCommentsPageDto?> GetPodcastEpisodeCommentsPageAsync(
        string episodeUri, string? pageToken, CancellationToken ct = default);

    /// <summary>
    /// Returns one page of replies for a comment.
    /// </summary>
    Task<PodcastCommentRepliesPageDto?> GetPodcastCommentRepliesAsync(
        string commentUri, string? pageToken, CancellationToken ct = default);

    /// <summary>
    /// Returns reactions to a comment or reply, optionally filtered to one
    /// emoji code-point.
    /// </summary>
    Task<PodcastCommentReactionsPageDto?> GetPodcastCommentReactionsAsync(
        string uri, string? pageToken, string? reactionUnicode, CancellationToken ct = default);

    /// <summary>
    /// Stub: posts a reply to a comment. Returns a synthetic
    /// <c>wavee:local-reply:</c> placeholder until the Spotify wire format is captured.
    /// </summary>
    Task<PodcastEpisodeCommentReplyDto> CreatePodcastCommentReplyAsync(
        string commentUri, string text, CancellationToken ct = default);

    /// <summary>
    /// Stub: records an emoji reaction on a comment.
    /// </summary>
    Task ReactToPodcastCommentAsync(string commentUri, string emoji, CancellationToken ct = default);

    /// <summary>
    /// Stub: records an emoji reaction on a reply.
    /// </summary>
    Task ReactToPodcastCommentReplyAsync(string replyUri, string emoji, CancellationToken ct = default);

    /// <summary>
    /// Fetches the full episode detail (Pathfinder + recommendations + first
    /// comment page + resume progress in one shot).
    /// </summary>
    Task<PodcastEpisodeDetailDto?> GetPodcastEpisodeDetailAsync(
        string episodeUri, CancellationToken ct = default);

    /// <summary>
    /// Cached resume-point lookup for the supplied episode URI. Falls back to
    /// a NOT_STARTED placeholder when no record exists; returns an ERROR
    /// placeholder when the cache previously failed to populate.
    /// </summary>
    Task<PodcastEpisodeProgressDto?> GetPodcastEpisodeProgressAsync(
        string episodeUri, CancellationToken ct = default);

    /// <summary>
    /// Writes a new resume-point revision to the server and updates the local
    /// cache + fires <see cref="PodcastEpisodeProgressChanged"/>.
    /// </summary>
    Task SavePodcastEpisodeProgressAsync(
        string episodeUri, TimeSpan? resumePosition, bool completed, CancellationToken ct = default);

    /// <summary>
    /// Stub: posts a top-level comment on the episode.
    /// </summary>
    Task<PodcastEpisodeCommentDto> CreatePodcastEpisodeCommentAsync(
        string episodeUri, string text, CancellationToken ct = default);

    /// <summary>
    /// Raised after every successful <see cref="SavePodcastEpisodeProgressAsync"/>.
    /// </summary>
    event EventHandler<PodcastEpisodeProgressChangedEventArgs>? PodcastEpisodeProgressChanged;
}
