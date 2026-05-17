using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.DescriptorExtension;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.Protocol.Resumption;
using Wavee.UI.WinUI.Data.Contexts.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Stores;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IPodcastEpisodeService"/>. Owns the Herodotus
/// resume-point cache + the SpClient / Pathfinder podcast read paths +
/// the (mostly stubbed) comment / reaction write paths.
/// </summary>
public sealed class PodcastEpisodeService : IPodcastEpisodeService
{
    private static readonly TimeSpan PodcastProgressCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PodcastProgressLookback = TimeSpan.FromDays(7);

    private readonly IMetadataDatabase _database;
    private readonly ISession _session;
    private readonly IExtendedMetadataClient _metadataClient;
    private readonly ExtendedMetadataStore? _extendedMetadataStore;
    private readonly ILogger<PodcastEpisodeService>? _logger;

    private readonly SemaphoreSlim _podcastProgressGate = new(1, 1);
    private IReadOnlyDictionary<string, PodcastEpisodeProgressDto> _cachedPodcastEpisodeProgress =
        new Dictionary<string, PodcastEpisodeProgressDto>(StringComparer.Ordinal);
    private DateTimeOffset _podcastProgressFetchedAt;
    private bool _podcastProgressFetchFailed;

    public event EventHandler<PodcastEpisodeProgressChangedEventArgs>? PodcastEpisodeProgressChanged;

    public PodcastEpisodeService(
        IMetadataDatabase database,
        ISession session,
        IExtendedMetadataClient metadataClient,
        ExtendedMetadataStore? extendedMetadataStore = null,
        ILogger<PodcastEpisodeService>? logger = null)
    {
        _database = database;
        _session = session;
        _metadataClient = metadataClient;
        _extendedMetadataStore = extendedMetadataStore;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LibraryEpisodeDto>> GetYourEpisodesAsync(CancellationToken ct = default)
    {
        var entities = await _database
            .GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.ListenLater, int.MaxValue, 0, ct)
            .ConfigureAwait(false);

        return entities
            .Where(static e => e.Uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            .Select((e, idx) => new LibraryEpisodeDto
            {
                Id = PlaylistUriHelpers.ExtractBareId(e.Uri, "spotify:episode:"),
                Uri = e.Uri,
                Title = e.Title ?? "Unknown episode",
                ArtistName = e.Publisher ?? e.ArtistName ?? e.AlbumName ?? "",
                ArtistId = "",
                AlbumName = e.AlbumName ?? e.ArtistName ?? "",
                AlbumId = e.AlbumUri ?? "",
                ImageUrl = e.ImageUrl,
                Description = e.Description,
                Duration = TimeSpan.FromMilliseconds(e.DurationMs ?? 0),
                AddedAt = e.AddedAt.HasValue ? e.AddedAt.Value.LocalDateTime : DateTime.Now,
                IsExplicit = false,
                IsPlayable = true,
                MediaTypes = ["AUDIO"],
                OriginalIndex = idx + 1,
                IsLiked = true
            })
            .ToList();
    }

    public async Task<IReadOnlyList<LibraryEpisodeDto>> GetRecentlyPlayedPodcastEpisodesAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        var progressByUri = await GetPodcastEpisodeProgressCacheAsync(ct).ConfigureAwait(false);
        if (_podcastProgressFetchFailed || progressByUri.Count == 0)
            return [];

        var savedEpisodes = await GetYourEpisodesAsync(ct).ConfigureAwait(false);
        var savedByUri = savedEpisodes
            .GroupBy(static episode => episode.Uri, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        var orderedProgress = progressByUri.Values
            .Where(static progress => progress.Uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            .OrderByDescending(GetProgressSortDate)
            .Take(limit)
            .ToList();

        var unsavedProgress = orderedProgress
            .Where(progress => !savedByUri.ContainsKey(progress.Uri))
            .ToList();
        var unsavedEpisodes = await FetchRecentPodcastEpisodesAsync(unsavedProgress, ct).ConfigureAwait(false);

        var results = new List<LibraryEpisodeDto>(orderedProgress.Count);
        var index = 1;
        foreach (var progress in orderedProgress)
        {
            ct.ThrowIfCancellationRequested();

            LibraryEpisodeDto? episode;
            if (savedByUri.TryGetValue(progress.Uri, out var savedEpisode))
                episode = CreateRecentSavedEpisode(savedEpisode, progress, index);
            else if (unsavedEpisodes.TryGetValue(NormalizeEpisodeUri(progress.Uri), out var metadataEpisode))
                episode = CreateRecentUnsavedEpisode(metadataEpisode, progress, index);
            else
                episode = null;

            if (episode is null)
                continue;

            results.Add(episode);
            index++;
        }

        return results;
    }

    public async Task<IReadOnlyList<LibraryPodcastShowDto>> GetPodcastShowsAsync(CancellationToken ct = default)
    {
        var showsTask = _database
            .GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Show, int.MaxValue, 0, ct);
        var episodesTask = GetYourEpisodesAsync(ct);

        await Task.WhenAll(showsTask, episodesTask).ConfigureAwait(false);

        var episodesByShow = (await episodesTask.ConfigureAwait(false))
            .GroupBy(BuildEpisodeShowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static g => g.Key,
                static g => g.OrderByDescending(e => e.AddedAt).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var results = new List<LibraryPodcastShowDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var show in await showsTask.ConfigureAwait(false))
        {
            var key = BuildShowKey(show.Uri, show.Title ?? show.AlbumName ?? show.Publisher);
            episodesByShow.TryGetValue(key, out var savedEpisodes);
            seen.Add(key);

            results.Add(new LibraryPodcastShowDto
            {
                Id = show.Uri,
                Name = show.Title ?? show.AlbumName ?? show.Publisher ?? "Unknown podcast",
                Publisher = show.Publisher ?? show.ArtistName,
                Description = show.Description,
                ImageUrl = show.ImageUrl,
                EpisodeCount = show.EpisodeCount ?? savedEpisodes?.Count ?? 0,
                SavedEpisodeCount = savedEpisodes?.Count ?? 0,
                AddedAt = show.AddedAt?.LocalDateTime ?? DateTime.Now,
                LastEpisodeAddedAt = savedEpisodes?.FirstOrDefault()?.AddedAt,
                IsFollowed = true
            });
        }

        foreach (var (key, savedEpisodes) in episodesByShow)
        {
            if (seen.Contains(key))
                continue;

            var first = savedEpisodes[0];
            results.Add(new LibraryPodcastShowDto
            {
                Id = GetEpisodeShowId(first),
                Name = GetEpisodeShowName(first),
                Publisher = first.ArtistName,
                ImageUrl = first.ImageUrl,
                EpisodeCount = savedEpisodes.Count,
                SavedEpisodeCount = savedEpisodes.Count,
                AddedAt = savedEpisodes.Min(static e => e.AddedAt),
                LastEpisodeAddedAt = first.AddedAt,
                IsFollowed = false
            });
        }

        return results
            .OrderByDescending(static s => s.SortDate)
            .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PodcastEpisodeCommentsPageDto?> GetPodcastEpisodeCommentsPageAsync(
        string episodeUri, string? pageToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        try
        {
            var response = await _session.Pathfinder.GetCommentsForEntityAsync(episodeUri, pageToken, ct);
            var page = response?.Data?.Comments?.FirstOrDefault();
            if (page is null) return null;

            return new PodcastEpisodeCommentsPageDto
            {
                Items = MapCommentItems(page.Items),
                NextPageToken = page.NextPageToken,
                TotalCount = page.TotalCount ?? 0
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch podcast comments page for {Uri}", episodeUri);
            return null;
        }
    }

    public async Task<PodcastCommentRepliesPageDto?> GetPodcastCommentRepliesAsync(
        string commentUri, string? pageToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);

        try
        {
            var response = await _session.Pathfinder.GetCommentRepliesAsync(commentUri, pageToken, ct);
            var page = response?.Data?.CommentReplies?.FirstOrDefault();
            if (page is null) return null;

            return new PodcastCommentRepliesPageDto
            {
                Items = MapCommentReplies(page.Items),
                NextPageToken = page.NextPageToken,
                TotalCount = page.TotalCount ?? 0
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch comment replies for {Uri}", commentUri);
            return null;
        }
    }

    public async Task<PodcastCommentReactionsPageDto?> GetPodcastCommentReactionsAsync(
        string uri,
        string? pageToken,
        string? reactionUnicode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        try
        {
            var response = await _session.Pathfinder.GetCommentReactionsAsync(uri, pageToken, reactionUnicode, ct);
            var page = response?.Data?.CommentReactions?.FirstOrDefault();
            if (page is null) return null;

            return new PodcastCommentReactionsPageDto
            {
                Items = MapCommentReactions(page.Items),
                ReactionCounts = MapCommentReactionCounts(page.ReactionCounts),
                NextPageToken = page.NextPageToken
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch comment reactions for {Uri}", uri);
            return null;
        }
    }

    public Task<PodcastEpisodeCommentReplyDto> CreatePodcastCommentReplyAsync(
        string commentUri,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ct.ThrowIfCancellationRequested();

        _logger?.LogInformation("Podcast comment reply create stub invoked for {CommentUri}", commentUri);
        return Task.FromResult(new PodcastEpisodeCommentReplyDto
        {
            Uri = $"wavee:local-reply:{Guid.NewGuid():N}",
            AuthorName = "You",
            Text = text.Trim(),
            CreatedAt = DateTimeOffset.Now
        });
    }

    public Task ReactToPodcastCommentAsync(
        string commentUri,
        string emoji,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        ct.ThrowIfCancellationRequested();

        _logger?.LogInformation(
            "Podcast comment reaction stub invoked for {CommentUri} with {Emoji}",
            commentUri,
            emoji);
        return Task.CompletedTask;
    }

    public Task ReactToPodcastCommentReplyAsync(
        string replyUri,
        string emoji,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        ct.ThrowIfCancellationRequested();

        _logger?.LogInformation(
            "Podcast comment reply reaction stub invoked for {ReplyUri} with {Emoji}",
            replyUri,
            emoji);
        return Task.CompletedTask;
    }

    public async Task<PodcastEpisodeDetailDto?> GetPodcastEpisodeDetailAsync(string episodeUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);
        var normalizedUri = NormalizeEpisodeUri(episodeUri);

        try
        {
            var episodeTask = _session.Pathfinder.GetEpisodeOrChapterAsync(normalizedUri, ct);
            var recommendationsTask = FetchOptionalAsync(
                () => _session.Pathfinder.GetSeoRecommendedEpisodesAsync(normalizedUri, ct),
                "episode recommendations");
            var commentsTask = FetchOptionalAsync(
                () => _session.Pathfinder.GetCommentsForEntityAsync(normalizedUri, null, ct),
                "episode comments");
            var progressTask = FetchOptionalAsync(
                () => GetPodcastEpisodeProgressAsync(normalizedUri, ct),
                "episode Herodotus progress");

            await Task.WhenAll(episodeTask, recommendationsTask, commentsTask, progressTask).ConfigureAwait(false);

            var episode = episodeTask.Result.Data?.EpisodeUnionV2;
            if (episode is null)
                return null;

            var detail = MapPodcastEpisodeDetail(
                episode,
                recommendationsTask.Result,
                commentsTask.Result);
            return ApplyPodcastProgress(detail, progressTask.Result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch podcast episode detail for {EpisodeUri}", episodeUri);
            return null;
        }
    }

    public async Task<PodcastEpisodeProgressDto?> GetPodcastEpisodeProgressAsync(
        string episodeUri,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);
        var normalizedUri = NormalizeEpisodeUri(episodeUri);

        var cached = await GetPodcastEpisodeProgressCacheAsync(ct).ConfigureAwait(false);
        if (cached.TryGetValue(normalizedUri, out var progress) ||
            cached.TryGetValue(episodeUri, out progress))
        {
            return progress;
        }

        return _podcastProgressFetchFailed
            ? CreatePodcastProgressError(normalizedUri)
            : CreatePodcastProgressNotStarted(normalizedUri);
    }

    public async Task SavePodcastEpisodeProgressAsync(
        string episodeUri,
        TimeSpan? resumePosition,
        bool completed,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        var normalizedUri = NormalizeEpisodeUri(episodeUri);
        var serverResumePosition = completed ? null : resumePosition;

        try
        {
            var response = await _session.SpClient
                .CreateResumePointRevisionAsync(normalizedUri, serverResumePosition, ct)
                .ConfigureAwait(false);

            var revision = response.Revision;
            var savedPosition = revision?.Value?.ResumePoint?.PositionSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
            var savedState = completed || revision?.Value?.ResumePoint is null
                ? "COMPLETED"
                : savedPosition > TimeSpan.Zero
                    ? "IN_PROGRESS"
                    : "NOT_STARTED";

            UpsertPodcastProgress(
                new PodcastEpisodeProgressDto
                {
                    Uri = normalizedUri,
                    PlayedPosition = savedPosition,
                    PlayedState = savedState,
                    CreatedAt = ToDateTimeOffset(revision?.CreateTime),
                    UpdatedAt = ToDateTimeOffset(revision?.UpdateTime)
                },
                episodeUri);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to save Herodotus podcast progress for {EpisodeUri}", normalizedUri);
            throw;
        }
    }

    public Task<PodcastEpisodeCommentDto> CreatePodcastEpisodeCommentAsync(
        string episodeUri,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ct.ThrowIfCancellationRequested();

        _logger?.LogInformation("Podcast comment create stub invoked for {EpisodeUri}", episodeUri);
        return Task.FromResult(new PodcastEpisodeCommentDto
        {
            Uri = $"wavee:local-comment:{Guid.NewGuid():N}",
            AuthorName = "You",
            Text = text.Trim(),
            CreatedAt = DateTimeOffset.Now
        });
    }

    // ── Private helpers ──

    private async Task<IReadOnlyDictionary<string, PodcastEpisodeProgressDto>> GetPodcastEpisodeProgressCacheAsync(
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (IsPodcastProgressCacheFresh(now))
        {
            return _cachedPodcastEpisodeProgress;
        }

        await _podcastProgressGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (IsPodcastProgressCacheFresh(now))
            {
                return _cachedPodcastEpisodeProgress;
            }

            try
            {
                var response = await _session.SpClient
                    .ListCurrentStatesAsync(now - PodcastProgressLookback, cancellationToken: ct)
                    .ConfigureAwait(false);

                _cachedPodcastEpisodeProgress = MapCurrentStateProgress(response);
                _podcastProgressFetchFailed = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to fetch Herodotus podcast progress; using empty progress cache");
                _cachedPodcastEpisodeProgress = new Dictionary<string, PodcastEpisodeProgressDto>(StringComparer.Ordinal);
                _podcastProgressFetchFailed = true;
            }

            _podcastProgressFetchedAt = now;
            return _cachedPodcastEpisodeProgress;
        }
        finally
        {
            _podcastProgressGate.Release();
        }
    }

    private bool IsPodcastProgressCacheFresh(DateTimeOffset now)
        => !_podcastProgressFetchFailed &&
           _podcastProgressFetchedAt != default &&
           now - _podcastProgressFetchedAt < PodcastProgressCacheTtl;

    private static PodcastEpisodeProgressDto CreatePodcastProgressError(string episodeUri) => new()
    {
        Uri = episodeUri,
        PlayedState = PodcastEpisodeProgressDto.ErrorState
    };

    private static PodcastEpisodeProgressDto CreatePodcastProgressNotStarted(string episodeUri) => new()
    {
        Uri = episodeUri,
        PlayedState = "NOT_STARTED",
        PlayedPosition = TimeSpan.Zero
    };

    private void UpsertPodcastProgress(PodcastEpisodeProgressDto progress, string? aliasUri = null)
    {
        var normalizedUri = NormalizeEpisodeUri(progress.Uri);
        var updated = new Dictionary<string, PodcastEpisodeProgressDto>(
            _cachedPodcastEpisodeProgress,
            StringComparer.Ordinal)
        {
            [progress.Uri] = progress,
            [normalizedUri] = progress
        };
        if (!string.IsNullOrWhiteSpace(aliasUri))
            updated[aliasUri] = progress;

        _cachedPodcastEpisodeProgress = updated;
        _podcastProgressFetchedAt = DateTimeOffset.UtcNow;
        _podcastProgressFetchFailed = false;
        PodcastEpisodeProgressChanged?.Invoke(this, new PodcastEpisodeProgressChangedEventArgs(progress, aliasUri));
    }

    private async Task<IReadOnlyDictionary<string, Episode>> FetchRecentPodcastEpisodesAsync(
        IReadOnlyList<PodcastEpisodeProgressDto> progresses,
        CancellationToken ct)
    {
        if (progresses.Count == 0)
            return new Dictionary<string, Episode>(StringComparer.Ordinal);

        var uris = progresses
            .Select(progress => NormalizeEpisodeUri(progress.Uri))
            .Where(static uri => uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (uris.Count == 0)
            return new Dictionary<string, Episode>(StringComparer.Ordinal);

        var episodes = new Dictionary<string, Episode>(uris.Count, StringComparer.Ordinal);

        if (_extendedMetadataStore is not null)
        {
            try
            {
                var response = await _extendedMetadataStore.GetManyAsync(
                    uris.Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.EpisodeV4 })),
                    ct).ConfigureAwait(false);

                foreach (var (key, data) in response)
                {
                    if (key.Kind != ExtensionKind.EpisodeV4 || data.Length == 0)
                        continue;

                    try
                    {
                        episodes[key.Uri] = Episode.Parser.ParseFrom(data);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to parse recent podcast EpisodeV4 metadata for {Uri}", key.Uri);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Recent podcast EpisodeV4 metadata store lookup failed for {Count} URIs", uris.Count);
            }
        }

        var missing = uris.Where(uri => !episodes.ContainsKey(uri)).ToList();
        const int batchSize = 500;
        for (var i = 0; i < missing.Count; i += batchSize)
        {
            var batch = missing.Skip(i).Take(batchSize).ToList();
            try
            {
                var response = await _metadataClient.GetBatchedExtensionsAsync(
                    batch.Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.EpisodeV4 })),
                    ct).ConfigureAwait(false);

                foreach (var entry in response.GetAllExtensionData(ExtensionKind.EpisodeV4))
                {
                    if (string.IsNullOrWhiteSpace(entry.EntityUri))
                        continue;

                    try
                    {
                        var episode = entry.UnpackAs<Episode>();
                        if (episode is not null)
                        {
                            episodes[entry.EntityUri] = episode;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to unpack recent podcast EpisodeV4 metadata for {Uri}", entry.EntityUri);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Recent podcast EpisodeV4 batch fetch failed at offset {Offset}", i);
            }
        }

        _logger?.LogDebug(
            "Hydrated {ResolvedCount}/{RequestedCount} recently played podcast episodes from EpisodeV4",
            episodes.Count,
            uris.Count);

        return episodes;
    }

    private static LibraryEpisodeDto CreateRecentSavedEpisode(
        LibraryEpisodeDto episode,
        PodcastEpisodeProgressDto progress,
        int index)
    {
        var recent = episode with
        {
            AddedAt = GetProgressSortDate(progress).LocalDateTime,
            OriginalIndex = index
        };
        recent.ApplyPlaybackProgress(progress.PlayedPosition, progress.PlayedState);
        return recent;
    }

    private static LibraryEpisodeDto CreateRecentUnsavedEpisode(
        Episode episode,
        PodcastEpisodeProgressDto progress,
        int index)
    {
        var uri = NormalizeEpisodeUri(progress.Uri);
        var show = episode.Show;
        var showName = show?.Name ?? "";

        var result = new LibraryEpisodeDto
        {
            Id = PlaylistUriHelpers.ExtractBareId(uri, "spotify:episode:"),
            Uri = uri,
            Title = string.IsNullOrWhiteSpace(episode.Name) ? "Unknown episode" : episode.Name,
            ArtistName = showName,
            ArtistId = "",
            AlbumName = showName,
            AlbumId = MetadataShowUri(show) ?? "",
            ImageUrl = MetadataImageUrl(episode.CoverImage) ?? MetadataImageUrl(show?.CoverImage),
            Description = episode.Description,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.Duration)),
            ReleaseDate = MetadataDate(episode.PublishTime),
            ShareUrl = null,
            PreviewUrl = null,
            MediaTypes = episode.Video.Count > 0 ? ["VIDEO"] : ["AUDIO"],
            AddedAt = GetProgressSortDate(progress).LocalDateTime,
            IsExplicit = episode.Explicit,
            IsPlayable = true,
            OriginalIndex = index,
            IsLiked = false
        };

        result.ApplyPlaybackProgress(progress.PlayedPosition, progress.PlayedState);
        return result;
    }

    private async Task<T?> FetchOptionalAsync<T>(Func<Task<T>> factory, string label) where T : class
    {
        try
        {
            return await factory().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Optional podcast detail fetch failed: {Label}", label);
            return null;
        }
    }

    private static PodcastEpisodeDetailDto MapPodcastEpisodeDetail(
        PathfinderEpisode episode,
        SeoRecommendedEpisodesResponse? recommendations,
        EntityCommentsResponse? comments)
    {
        var show = episode.PodcastV2?.Data;
        var commentsPage = comments?.Data?.Comments?.FirstOrDefault();

        return new PodcastEpisodeDetailDto
        {
            Uri = episode.Uri ?? "",
            Title = episode.Name ?? "Unknown episode",
            ShowUri = show?.Uri,
            ShowName = show?.Name,
            ImageUrl = BestImageUrl(episode.CoverArt?.Sources) ?? BestImageUrl(show?.CoverArt?.Sources),
            ShowImageUrl = BestImageUrl(show?.CoverArt?.Sources) ?? BestImageUrl(episode.CoverArt?.Sources),
            Description = episode.Description,
            HtmlDescription = episode.HtmlDescription,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.Duration?.TotalMilliseconds ?? 0)),
            ReleaseDate = ParseDate(episode.ReleaseDate?.IsoString),
            AddedAt = DateTime.Now,
            IsExplicit = string.Equals(episode.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase),
            IsPlayable = episode.Playability?.Playable ?? true,
            IsPaywalled = episode.Restrictions?.PaywallContent ?? false,
            ShareUrl = episode.SharingInfo?.ShareUrl,
            PreviewUrl = episode.PreviewPlayback?.AudioPreview?.CdnUrl,
            MediaTypes = DistinctNonEmpty(episode.MediaTypes),
            TranscriptLanguages = DistinctNonEmpty(episode.Transcripts?.Items?.Select(static t => t.Language)),
            Recommendations = MapEpisodeRecommendations(recommendations),
            Comments = MapEpisodeComments(comments),
            CommentsNextPageToken = commentsPage?.NextPageToken,
            CommentsTotalCount = commentsPage?.TotalCount ?? 0
        };
    }

    private static PodcastEpisodeDetailDto ApplyPodcastProgress(
        PodcastEpisodeDetailDto detail,
        PodcastEpisodeProgressDto? progress)
    {
        if (progress is null ||
            string.Equals(progress.PlayedState, PodcastEpisodeProgressDto.ErrorState, StringComparison.Ordinal))
        {
            return detail;
        }

        var playedState = string.IsNullOrWhiteSpace(progress.PlayedState)
            ? (progress.PlayedPosition > TimeSpan.Zero ? "IN_PROGRESS" : "NOT_STARTED")
            : progress.PlayedState;

        return detail with
        {
            PlayedState = playedState,
            PlayedPosition = progress.PlayedPosition < TimeSpan.Zero
                ? TimeSpan.Zero
                : progress.PlayedPosition
        };
    }

    private static IReadOnlyDictionary<string, PodcastEpisodeProgressDto> MapCurrentStateProgress(
        ListCurrentStatesResponse response)
    {
        var result = new Dictionary<string, PodcastEpisodeProgressDto>(StringComparer.Ordinal);
        foreach (var state in response.States)
        {
            var uri = !string.IsNullOrWhiteSpace(state.EntityUri)
                ? state.EntityUri
                : state.Revision?.Value?.EntityUri;

            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            {
                continue;
            }

            var revisionValue = state.Revision?.Value;
            var resumePoint = revisionValue?.ResumePoint;
            var positionSeconds = resumePoint?.PositionSeconds ?? 0;
            var playedPosition = TimeSpan.FromSeconds(positionSeconds);

            string playedState;
            if (revisionValue is not null && resumePoint is null)
                playedState = "COMPLETED";
            else if (positionSeconds > 0)
                playedState = "IN_PROGRESS";
            else
                playedState = "NOT_STARTED";

            result[uri] = new PodcastEpisodeProgressDto
            {
                Uri = uri,
                PlayedPosition = playedPosition,
                PlayedState = playedState,
                CreatedAt = ToDateTimeOffset(state.Revision?.CreateTime),
                UpdatedAt = ToDateTimeOffset(state.Revision?.UpdateTime)
            };
        }

        return result;
    }

    private static DateTimeOffset GetProgressSortDate(PodcastEpisodeProgressDto progress)
        => progress.UpdatedAt ?? progress.CreatedAt ?? DateTimeOffset.UtcNow;

    private static string NormalizeEpisodeUri(string episodeUri)
        => episodeUri.StartsWith("spotify:episode:", StringComparison.Ordinal)
            ? episodeUri
            : $"spotify:episode:{episodeUri}";

    private static DateTimeOffset? ToDateTimeOffset(Google.Protobuf.WellKnownTypes.Timestamp? timestamp)
    {
        if (timestamp is null)
            return null;

        try
        {
            return timestamp.ToDateTimeOffset();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PodcastEpisodeRecommendationDto> MapEpisodeRecommendations(
        SeoRecommendedEpisodesResponse? response)
    {
        var items = response?.Data?.SeoRecommendedEpisode?.Items;
        if (items is null || items.Count == 0)
            return [];

        return items
            .Select(static item => item.Data)
            .Where(static episode => episode is not null)
            .Select(static episode =>
            {
                var show = episode!.PodcastV2?.Data;
                return new PodcastEpisodeRecommendationDto
                {
                    Uri = episode.Uri ?? "",
                    Title = episode.Name ?? "Unknown episode",
                    ShowName = show?.Name,
                    ImageUrl = BestImageUrl(episode.CoverArt?.Sources) ?? BestImageUrl(show?.CoverArt?.Sources),
                    Duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.Duration?.TotalMilliseconds ?? 0)),
                    ReleaseDate = ParseDate(episode.ReleaseDate?.IsoString),
                    IsExplicit = string.Equals(episode.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase)
                };
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Uri))
            .Take(8)
            .ToList();
    }

    private static IReadOnlyList<PodcastEpisodeCommentDto> MapEpisodeComments(EntityCommentsResponse? response)
    {
        var page = response?.Data?.Comments?.FirstOrDefault();
        return MapCommentItems(page?.Items);
    }

    private static IReadOnlyList<PodcastEpisodeCommentDto> MapCommentItems(IReadOnlyList<EntityCommentItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        var result = new List<PodcastEpisodeCommentDto>(items.Count);
        foreach (var item in items)
        {
            if (item.IsSensitive || string.IsNullOrWhiteSpace(item.CommentString) || string.IsNullOrWhiteSpace(item.Uri))
                continue;

            var topReplyAuthors = item.TopRepliesAuthors is { Count: > 0 } authors
                ? authors
                    .Select(static a => a?.Data)
                    .Where(static a => a is not null)
                    .Select(static a => new PodcastCommentAvatarDto
                    {
                        Name = a!.Name ?? "",
                        ImageUrl = BestImageUrl(a.Avatar?.Sources)
                    })
                    .ToList()
                : (IReadOnlyList<PodcastCommentAvatarDto>)[];

            var topReactions = item.ReactionsMetadata?.TopReactionUnicode is { Count: > 0 } emoji
                ? emoji.Where(static s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList()
                : (IReadOnlyList<string>)[];

            result.Add(new PodcastEpisodeCommentDto
            {
                Uri = item.Uri!,
                Text = item.CommentString!,
                AuthorName = item.Author?.Data?.Name,
                AuthorImageUrl = BestImageUrl(item.Author?.Data?.Avatar?.Sources),
                CreatedAt = ParseDate(item.CreateDate?.IsoString),
                ReactionCount = item.ReactionsMetadata?.NumberOfReactions ?? 0,
                ReplyCount = item.NumberOfRepliesWithThreads,
                IsPinned = item.IsPinned,
                UserReactionEmoji = item.ReactionsMetadata?.UsersReactionUnicode,
                TopReactionEmoji = topReactions,
                TopReplyAuthors = topReplyAuthors
            });
        }

        return result;
    }

    private static IReadOnlyList<PodcastEpisodeCommentReplyDto> MapCommentReplies(IReadOnlyList<CommentReplyItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        var result = new List<PodcastEpisodeCommentReplyDto>(items.Count);
        foreach (var item in items)
        {
            if (item.IsSensitive || string.IsNullOrWhiteSpace(item.ReplyString) || string.IsNullOrWhiteSpace(item.Uri))
                continue;

            var topReactions = item.ReactionsMetadata?.TopReactionUnicode is { Count: > 0 } emoji
                ? emoji.Where(static s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList()
                : (IReadOnlyList<string>)[];

            result.Add(new PodcastEpisodeCommentReplyDto
            {
                Uri = item.Uri!,
                Text = item.ReplyString!,
                AuthorName = item.Author?.Data?.Name,
                AuthorImageUrl = BestImageUrl(item.Author?.Data?.Avatar?.Sources),
                CreatedAt = ParseDate(item.CreateDate?.IsoString),
                ReactionCount = item.ReactionsMetadata?.NumberOfReactions ?? 0,
                UserReactionEmoji = item.ReactionsMetadata?.UsersReactionUnicode,
                TopReactionEmoji = topReactions
            });
        }

        return result;
    }

    private static IReadOnlyList<PodcastCommentReactionDto> MapCommentReactions(IReadOnlyList<CommentReactionItem>? items)
    {
        if (items is null || items.Count == 0)
            return [];

        var result = new List<PodcastCommentReactionDto>(items.Count);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ReactionUnicode))
                continue;

            result.Add(new PodcastCommentReactionDto
            {
                AuthorName = item.Author?.Data?.Name,
                AuthorImageUrl = BestImageUrl(item.Author?.Data?.Avatar?.Sources),
                CreatedAt = ParseDate(item.CreateDate?.IsoString),
                ReactionUnicode = item.ReactionUnicode!
            });
        }

        return result;
    }

    private static IReadOnlyList<PodcastCommentReactionCountDto> MapCommentReactionCounts(
        IReadOnlyList<CommentReactionCount>? counts)
    {
        if (counts is null || counts.Count == 0)
            return [];

        return counts
            .Where(static count => !string.IsNullOrWhiteSpace(count.ReactionUnicode) && count.NumberOfReactions > 0)
            .Select(static count => new PodcastCommentReactionCountDto
            {
                ReactionUnicode = count.ReactionUnicode!,
                Count = count.NumberOfReactions
            })
            .ToList();
    }

    private static IReadOnlyList<string> DistinctNonEmpty(IEnumerable<string?>? values)
    {
        if (values is null)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static DateTimeOffset? MetadataDate(Wavee.Protocol.Metadata.Date? value)
    {
        if (value is null || value.Year <= 0)
            return null;

        try
        {
            return new DateTimeOffset(
                value.Year,
                Math.Max(1, value.Month),
                Math.Max(1, value.Day),
                0,
                0,
                0,
                TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static string? MetadataShowUri(Wavee.Protocol.Metadata.Show? show)
    {
        if (show?.Gid is not { Length: > 0 } gid)
            return null;

        try
        {
            return $"spotify:show:{SpotifyId.FromRaw(gid.Span, SpotifyIdType.Show).ToBase62()}";
        }
        catch
        {
            return null;
        }
    }

    private static string? MetadataImageUrl(ImageGroup? imageGroup)
    {
        if (imageGroup?.Image.Count is not > 0)
            return null;

        var image = imageGroup.Image
            .OrderByDescending(static img => img.Size == Image.Types.Size.Default ? 2 :
                                             img.Size == Image.Types.Size.Large ? 1 : 0)
            .FirstOrDefault();
        if (image?.FileId is not { Length: > 0 } fileId)
            return null;

        return $"https://i.scdn.co/image/{Convert.ToHexString(fileId.ToByteArray()).ToLowerInvariant()}";
    }

    private static string? BestImageUrl(IReadOnlyList<ArtistImageSource>? sources)
        => sources?
            .Where(static source => !string.IsNullOrWhiteSpace(source.Url))
            .OrderByDescending(static source => source.Width ?? source.MaxWidth ?? 0)
            .ThenByDescending(static source => source.Height ?? source.MaxHeight ?? 0)
            .Select(static source => source.Url)
            .FirstOrDefault();

    private static string BuildEpisodeShowKey(LibraryEpisodeDto episode)
        => BuildShowKey(GetEpisodeShowId(episode), GetEpisodeShowName(episode));

    private static string BuildShowKey(string? showUri, string? showName)
    {
        if (!string.IsNullOrWhiteSpace(showUri) &&
            showUri.StartsWith("spotify:show:", StringComparison.OrdinalIgnoreCase))
        {
            return showUri;
        }

        var name = string.IsNullOrWhiteSpace(showName) ? "Unknown podcast" : showName.Trim();
        return "podcast:show:" + Uri.EscapeDataString(name.ToLowerInvariant());
    }

    private static string GetEpisodeShowId(LibraryEpisodeDto episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.AlbumId) &&
            episode.AlbumId.StartsWith("spotify:show:", StringComparison.OrdinalIgnoreCase))
        {
            return episode.AlbumId;
        }

        return BuildShowKey(null, GetEpisodeShowName(episode));
    }

    private static string GetEpisodeShowName(LibraryEpisodeDto episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.AlbumName))
            return episode.AlbumName;

        if (!string.IsNullOrWhiteSpace(episode.ArtistName))
            return episode.ArtistName;

        return "Unknown podcast";
    }
}
