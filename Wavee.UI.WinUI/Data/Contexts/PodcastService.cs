using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Stores;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IPodcastService"/> implementation backed by Spotify's
/// Pathfinder GraphQL (for show metadata + recommendations), playlist-v2
/// (for the authoritative show episode list), and the extended-metadata
/// batch API (for episode payloads).
/// </summary>
public sealed class PodcastService : IPodcastService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly IExtendedMetadataClient _extendedMetadata;
    private readonly ISpClient _spClient;
    private readonly ExtendedMetadataStore? _metadataStore;
    private readonly ILibraryDataService? _libraryData;
    private readonly ILogger? _logger;
    private readonly object _showEpisodeCacheGate = new();
    private readonly Dictionary<string, Episode> _showEpisodeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Conservative fallback for direct extended-metadata calls when the shared
    /// store is unavailable. Normal show loads go through ExtendedMetadataStore,
    /// which coalesces callers and uses the same 500-key ceiling.
    /// </summary>
    private const int EpisodeBatchChunkSize = 500;

    public PodcastService(
        IPathfinderClient pathfinder,
        IExtendedMetadataClient extendedMetadata,
        ISpClient spClient,
        ExtendedMetadataStore? metadataStore = null,
        ILibraryDataService? libraryData = null,
        ILogger<PodcastService>? logger = null)
    {
        _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
        _extendedMetadata = extendedMetadata ?? throw new ArgumentNullException(nameof(extendedMetadata));
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _metadataStore = metadataStore;
        _libraryData = libraryData;
        _logger = logger;
    }

    public async Task<ShowDetailDto?> GetShowDetailAsync(string showUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showUri);

        try
        {
            var response = await _pathfinder.GetShowMetadataAsync(showUri, ct).ConfigureAwait(false);
            var podcast = response?.Data?.PodcastUnionV2;
            if (podcast is null)
            {
                _logger?.LogDebug("No podcastUnionV2 in show metadata response for {Uri}", showUri);
                return null;
            }

            var allEpisodeUris = await TryLoadShowEpisodeUrisAsync(podcast.Uri ?? showUri, ct).ConfigureAwait(false);
            return MapShowDetail(podcast, showUri, allEpisodeUris);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch show metadata for {Uri}", showUri);
            return null;
        }
    }

    public async Task<IReadOnlyList<ShowEpisodeDto>> GetEpisodesAsync(
        IReadOnlyList<string> episodeUris, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(episodeUris);
        if (episodeUris.Count == 0) return Array.Empty<ShowEpisodeDto>();

        // Resolve each URI to its Episode protobuf via show metadata first, then
        // fall back to the cached batch endpoint for any missing/stale entries.
        var episodes = new Dictionary<string, Episode>(StringComparer.Ordinal);
        var missingUris = new List<string>();
        lock (_showEpisodeCacheGate)
        {
            foreach (var uri in episodeUris)
            {
                if (_showEpisodeCache.TryGetValue(uri, out var cached) && HasUsableEpisodeMetadata(cached))
                    episodes[uri] = cached;
                else
                    missingUris.Add(uri);
            }
        }

        if (_metadataStore is not null)
        {
            try
            {
                var requests = missingUris.Select(uri =>
                    (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.EpisodeV4 }));
                var response = await _metadataStore.GetManyAsync(requests, ct).ConfigureAwait(false);

                foreach (var (key, data) in response)
                {
                    if (key.Kind != ExtensionKind.EpisodeV4 || data.Length == 0)
                        continue;

                    if (!TryParseEpisode(data, key.Uri, out var ep))
                        continue;

                    episodes[key.Uri] = ep;
                    lock (_showEpisodeCacheGate)
                        _showEpisodeCache[key.Uri] = ep;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Episode metadata store fetch failed ({Count} URIs)", missingUris.Count);
            }
        }

        var stillMissing = missingUris
            .Where(uri => !episodes.ContainsKey(uri))
            .ToList();

        foreach (var chunk in Chunk(stillMissing, EpisodeBatchChunkSize))
        {
            try
            {
                var requests = chunk.Select(uri =>
                    (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.EpisodeV4 }));
                var response = await _extendedMetadata.GetBatchedExtensionsAsync(requests, ct).ConfigureAwait(false);

                foreach (var entry in response.GetAllExtensionData(ExtensionKind.EpisodeV4))
                {
                    if (string.IsNullOrEmpty(entry.EntityUri))
                        continue;

                    Episode? ep;
                    try
                    {
                        ep = entry.UnpackAs<Episode>();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to parse episode metadata for {Uri}", entry.EntityUri);
                        continue;
                    }

                    if (ep != null)
                    {
                        episodes[entry.EntityUri] = ep;
                        lock (_showEpisodeCacheGate)
                            _showEpisodeCache[entry.EntityUri] = ep;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Episode batch fetch failed (chunk size {Size})", chunk.Count);
            }
        }

        // Pull podcast playback progress in one round-trip via the existing
        // library service cache (Herodotus). Misses fall back to NOT_STARTED.
        var progressByUri = await TryLoadProgressAsync(episodeUris, ct).ConfigureAwait(false);

        var result = new List<ShowEpisodeDto>(episodeUris.Count);
        foreach (var uri in episodeUris)
        {
            if (!episodes.TryGetValue(uri, out var ep))
                continue;

            progressByUri.TryGetValue(uri, out var progress);
            result.Add(MapEpisode(uri, ep, progress));
        }

        if (result.Count == 0)
        {
            _logger?.LogWarning(
                "Resolved 0 episode metadata rows for {RequestedCount} requested episode URIs",
                episodeUris.Count);
        }
        else if (result.Count < episodeUris.Count)
        {
            _logger?.LogDebug(
                "Resolved {ResolvedCount} of {RequestedCount} requested episode metadata rows",
                result.Count,
                episodeUris.Count);
        }

        return result;
    }

    public async Task<IReadOnlyList<ShowRecommendationDto>> GetRecommendedShowsAsync(
        string showUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showUri);

        try
        {
            var response = await _pathfinder.GetSeoRecommendedShowsAsync(showUri, ct).ConfigureAwait(false);
            var items = response?.Data?.SeoRecommendedPodcast?.Items;
            if (items is null || items.Count == 0)
                return Array.Empty<ShowRecommendationDto>();

            var list = new List<ShowRecommendationDto>(items.Count);
            foreach (var item in items)
            {
                var data = item?.Data;
                if (data == null || string.IsNullOrEmpty(data.Uri)) continue;

                list.Add(new ShowRecommendationDto
                {
                    Uri = data.Uri,
                    Name = data.Name ?? "",
                    PublisherName = data.Publisher?.Name,
                    CoverArtUrl = PickBestImage(data.CoverArt?.Sources, preferredHeight: 300),
                });
            }
            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch recommended shows for {Uri}", showUri);
            return Array.Empty<ShowRecommendationDto>();
        }
    }

    public async Task<IReadOnlyList<ViewModels.EpisodeChapterVm>> GetEpisodeChaptersAsync(
        string episodeUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);

        try
        {
            var response = await _pathfinder.GetEpisodeChaptersAsync(episodeUri, offset: 0, limit: 50, ct).ConfigureAwait(false);
            var items = response?.Data?.EpisodeUnionV2?.DisplaySegments?.DisplaySegments?.Items;
            if (items is null || items.Count == 0)
                return Array.Empty<ViewModels.EpisodeChapterVm>();

            var list = new List<ViewModels.EpisodeChapterVm>(items.Count);
            foreach (var segment in items)
            {
                if (segment is null || string.IsNullOrWhiteSpace(segment.Title)) continue;

                list.Add(new ViewModels.EpisodeChapterVm
                {
                    Number = list.Count + 1,
                    Title = segment.Title ?? "",
                    Subtitle = segment.Subtitle,
                    StartMilliseconds = Math.Max(0, segment.SeekStart?.Milliseconds ?? 0),
                    StopMilliseconds = Math.Max(0, segment.SeekStop?.Milliseconds ?? 0),
                });
            }

            for (var i = 0; i < list.Count; i++)
            {
                list[i].IsFirst = i == 0;
                list[i].IsLast = i == list.Count - 1;
            }

            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch episode chapters for {Uri}", episodeUri);
            return Array.Empty<ViewModels.EpisodeChapterVm>();
        }
    }

    // Mapping helpers

    private async Task<IReadOnlyList<string>> TryLoadShowEpisodeUrisAsync(string showUri, CancellationToken ct)
    {
        try
        {
            var playlistUris = await TryLoadShowPlaylistEpisodeUrisAsync(showUri, ct).ConfigureAwait(false);
            if (playlistUris.Count > 0)
                return playlistUris;

            var assocUris = await TryLoadShowEpisodeAssocUrisAsync(showUri, ct).ConfigureAwait(false);
            if (assocUris.Count > 0)
                return assocUris;

            var data = await _extendedMetadata.GetExtensionAsync(showUri, ExtensionKind.ShowV4, ct).ConfigureAwait(false);
            if (data is null || data.Length == 0)
                return Array.Empty<string>();

            var show = Show.Parser.ParseFrom(data);
            if (show.Episode.Count == 0)
                return Array.Empty<string>();

            var uris = new List<string>(show.Episode.Count);
            lock (_showEpisodeCacheGate)
            {
                foreach (var episode in show.Episode)
                {
                    var uri = EpisodeUriFromGid(episode.Gid);
                    if (string.IsNullOrEmpty(uri))
                        continue;

                    uris.Add(uri);
                    if (HasUsableEpisodeMetadata(episode))
                        _showEpisodeCache[uri] = episode;
                }
            }

            return DeduplicateEpisodeUris(uris);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch full show metadata for {Uri}", showUri);
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> TryLoadShowPlaylistEpisodeUrisAsync(string showUri, CancellationToken ct)
    {
        try
        {
            const int pageSize = 500;
            var allUris = new List<string>();

            var content = await _spClient.GetPlaylistAsync(showUri, cancellationToken: ct).ConfigureAwait(false);
            AddPlaylistEpisodeUris(content, allUris);

            var expectedCount = Math.Max(content.Length, allUris.Count);
            while (allUris.Count < expectedCount && content.Contents?.Truncated == true)
            {
                var before = allUris.Count;
                content = await _spClient.GetPlaylistAsync(
                    showUri,
                    start: allUris.Count,
                    length: pageSize,
                    cancellationToken: ct).ConfigureAwait(false);
                AddPlaylistEpisodeUris(content, allUris);

                if (allUris.Count == before)
                    break;
            }

            if (allUris.Count > 0)
            {
                _logger?.LogDebug(
                    "Fetched {Count} show episode URIs from playlist-v2 for {Uri}",
                    allUris.Count,
                    showUri);
            }

            return DeduplicateEpisodeUris(allUris);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to fetch show episode playlist for {Uri}", showUri);
            return Array.Empty<string>();
        }
    }

    private static void AddPlaylistEpisodeUris(
        Wavee.Protocol.Playlist.SelectedListContent content,
        ICollection<string> target)
    {
        foreach (var item in content.Contents?.Items ?? [])
        {
            var uri = item.Uri;
            if (uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
                target.Add(uri);
        }
    }

    private async Task<IReadOnlyList<string>> TryLoadShowEpisodeAssocUrisAsync(string showUri, CancellationToken ct)
    {
        try
        {
            var data = await _extendedMetadata.GetExtensionAsync(
                showUri,
                ExtensionKind.ShowV4EpisodesAssoc,
                ct).ConfigureAwait(false);
            if (data is null || data.Length == 0)
                return Array.Empty<string>();

            var assoc = Assoc.Parser.ParseFrom(data);
            var uris = assoc.PlainList?.EntityUri;
            if (uris is null || uris.Count == 0)
                return Array.Empty<string>();

            return DeduplicateEpisodeUris(uris.Where(static uri =>
                uri.StartsWith("spotify:episode:", StringComparison.Ordinal)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to fetch show episode assoc for {Uri}", showUri);
            return Array.Empty<string>();
        }
    }

    private static ShowDetailDto MapShowDetail(
        PathfinderShow podcast,
        string showUri,
        IReadOnlyList<string>? allEpisodeUris)
    {
        var topics = podcast.Topics?.Items?
            .Where(t => !string.IsNullOrEmpty(t?.Title))
            .Select(t => new ShowTopicDto { Title = t!.Title!, Uri = t.Uri })
            .ToList() ?? new List<ShowTopicDto>();

        var firstPageEpisodeUris = podcast.EpisodesV2?.Items?
            .Select(i => i?.Entity?.Data?.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToList() ?? new List<string>();
        var episodeUris = MergeEpisodeUris(firstPageEpisodeUris, allEpisodeUris);
        var totalEpisodes = Math.Max(podcast.EpisodesV2?.TotalCount ?? 0, episodeUris.Count);

        var labels = podcast.ContentRatingV2?.Labels;
        var isExplicit = labels != null && labels.Any(l =>
            string.Equals(l, "EXPLICIT", StringComparison.OrdinalIgnoreCase));

        var showTypes = podcast.ShowTypes;
        var isExclusive = showTypes != null && showTypes.Any(t =>
            t != null && t.IndexOf("EXCLUSIVE", StringComparison.OrdinalIgnoreCase) >= 0);

        var rating = podcast.Rating?.AverageRating;

        return new ShowDetailDto
        {
            Uri = podcast.Uri ?? showUri,
            Id = podcast.Id ?? ExtractIdFromUri(podcast.Uri ?? showUri),
            Name = podcast.Name ?? "",
            PublisherName = podcast.Publisher?.Name,
            PlainDescription = StripHtml(podcast.HtmlDescription) ?? podcast.Description,
            CoverArtUrl = PickBestImage(podcast.CoverArt?.Sources, preferredHeight: 640),
            MediaType = podcast.MediaType,
            IsExplicit = isExplicit,
            IsExclusive = isExclusive,
            IsPlayable = podcast.Playability?.Playable ?? true,
            IsSavedOnServer = podcast.Saved,
            ConsumptionOrder = podcast.ConsumptionOrderV2,
            ShareUrl = podcast.SharingInfo?.ShareUrl,
            TrailerUri = podcast.TrailerV2?.Data?.Uri,
            AverageRating = rating?.Average ?? 0,
            ShowAverageRating = rating?.ShowAverage ?? false,
            TotalRatings = rating?.TotalRatings ?? 0,
            Topics = topics,
            EpisodeUris = episodeUris,
            TotalEpisodes = totalEpisodes,
            Palette = MapPalette(podcast.VisualIdentity?.SquareCoverImage?.ExtractedColorSet),
        };
    }

    private static ShowEpisodeDto MapEpisode(string uri, Episode episode, EpisodeProgressSnapshot progress)
    {
        var durationMs = (long)Math.Max(0, episode.Duration);
        var publish = episode.PublishTime;
        DateTimeOffset? release = null;
        if (publish != null && publish.Year > 0)
        {
            try
            {
                release = new DateTimeOffset(
                    publish.Year, Math.Max(1, publish.Month), Math.Max(1, publish.Day),
                    0, 0, 0, TimeSpan.Zero);
            }
            catch { /* malformed date - leave null */ }
        }

        var coverUrl = ImageUrlFromCoverGroup(episode.CoverImage)
            ?? ImageUrlFromCoverGroup(episode.Show?.CoverImage);
        var showImageUrl = ImageUrlFromCoverGroup(episode.Show?.CoverImage);

        var isVideo = episode.Video.Count > 0;
        var isExplicit = episode.Explicit;

        var dateText = release.HasValue ? release.Value.ToString("MMM d, yyyy", CultureInfo.CurrentCulture) : "";
        var durationText = FormatDuration(durationMs);
        var descriptionHtml = episode.Description;
        var fullDescription = StripHtml(descriptionHtml) ?? descriptionHtml;
        var description = TrimToFirstParagraph(fullDescription);

        var playedState = progress.State ?? "NOT_STARTED";
        var playedPos = Math.Max(0, progress.PlayedPositionMs);

        // "Near end" rule: when the user listens through the last ~30 s of an
        // episode, Spotify treats it as COMPLETED on the server but the
        // resumePoint can land within seconds of the end. Without this the row
        // would show "12 s left · In progress" indefinitely. 30 000 ms matches
        // Spotify desktop's threshold (verified against the gabo plays gate).
        const long NearEndThresholdMs = 90_000;
        if (playedState == "IN_PROGRESS" && durationMs > 0 && playedPos > 0
            && durationMs - playedPos <= NearEndThresholdMs)
        {
            playedState = "COMPLETED";
        }

        var durationOrRemaining = playedState switch
        {
            "COMPLETED" => "Played",
            "IN_PROGRESS" when durationMs > 0 && playedPos > 0
                => $"{FormatDuration(Math.Max(0, durationMs - playedPos))} left",
            _ => durationText,
        };

        var metaLine = string.Join(" | ", new[] { dateText, durationOrRemaining }.Where(s => !string.IsNullOrEmpty(s)));

        return new ShowEpisodeDto
        {
            Uri = uri,
            Title = episode.Name ?? "",
            DescriptionPreview = description,
            CoverArtUrl = coverUrl,
            DurationMs = durationMs,
            ReleaseDate = release,
            PlayedState = playedState,
            PlayedPositionMs = playedPos,
            IsExplicit = isExplicit,
            IsVideo = isVideo,
            IsPlayable = true,
            DateText = dateText,
            DurationOrRemainingText = durationOrRemaining,
            MetaLine = metaLine,
            ShowUri = ShowUriFromShow(episode.Show),
            ShowName = episode.Show?.Name,
            ShowImageUrl = showImageUrl,
            FullDescription = fullDescription,
            DescriptionHtml = descriptionHtml,
        };
    }

    private static string? ShowUriFromShow(Wavee.Protocol.Metadata.Show? show)
    {
        if (show?.Gid is not { Length: > 0 } gid)
            return null;
        try
        {
            return $"spotify:show:{Wavee.Core.Audio.SpotifyId.FromRaw(gid.Span, Wavee.Core.Audio.SpotifyIdType.Show).ToBase62()}";
        }
        catch
        {
            return null;
        }
    }

    private bool TryParseEpisode(byte[] data, string uri, out Episode episode)
    {
        try
        {
            episode = Episode.Parser.ParseFrom(data);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to parse cached episode metadata for {Uri}", uri);
            episode = new Episode();
            return false;
        }
    }

    private static ShowPaletteDto? MapPalette(PathfinderShowExtractedColorSet? set)
    {
        if (set is null) return null;
        return new ShowPaletteDto
        {
            HighContrast = MapTier(set.HighContrast),
            HigherContrast = MapTier(set.HigherContrast),
            MinContrast = MapTier(set.MinContrast),
        };
    }

    private static ShowPaletteTier? MapTier(PathfinderShowColorTier? tier)
    {
        if (tier?.BackgroundBase is null) return null;
        var bg = tier.BackgroundBase!;
        var bgTint = tier.BackgroundTintedBase ?? bg;
        var accent = tier.TextBrightAccent ?? tier.TextBase ?? bg;
        return new ShowPaletteTier
        {
            BackgroundR = bg.Red, BackgroundG = bg.Green, BackgroundB = bg.Blue,
            BackgroundTintedR = bgTint.Red, BackgroundTintedG = bgTint.Green, BackgroundTintedB = bgTint.Blue,
            TextAccentR = accent.Red, TextAccentG = accent.Green, TextAccentB = accent.Blue,
        };
    }

    private async Task<Dictionary<string, EpisodeProgressSnapshot>> TryLoadProgressAsync(
        IReadOnlyList<string> uris, CancellationToken ct)
    {
        var map = new Dictionary<string, EpisodeProgressSnapshot>(StringComparer.Ordinal);
        if (_libraryData is null) return map;

        // GetPodcastEpisodeProgressAsync hydrates a single shared cache on first
        // call and then serves every subsequent lookup from memory, so this loop
        // only fires one Herodotus request per page load.
        foreach (var uri in uris)
        {
            try
            {
                var dto = await _libraryData.GetPodcastEpisodeProgressAsync(uri, ct).ConfigureAwait(false);
                if (dto is null) continue;
                map[uri] = new EpisodeProgressSnapshot(
                    dto.PlayedState ?? "NOT_STARTED",
                    (long)Math.Max(0, dto.PlayedPosition.TotalMilliseconds));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Per-episode failures are non-fatal - fall through to NOT_STARTED.
            }
        }

        return map;
    }

    // Format helpers

    private static string? PickBestImage(IList<PathfinderShowImageSource>? sources, int preferredHeight)
    {
        if (sources is null || sources.Count == 0) return null;

        // Prefer the source closest to (but not smaller than) the requested
        // height - drops the 64px thumbnail that would render blurry as a hero.
        var sorted = sources
            .Where(s => !string.IsNullOrEmpty(s?.Url))
            .OrderBy(s => Math.Abs((s!.Height ?? 0) - preferredHeight))
            .ToList();
        return sorted.FirstOrDefault()?.Url;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        // Crude tag strip is fine here - show descriptions only ever contain
        // <p>, <br>, <a>, <strong> per Spotify's editorial guidelines. We don't
        // want a full HTML renderer for a sidebar paragraph.
        var noTags = Regex.Replace(html, "<[^>]+>", " ");
        var collapsed = Regex.Replace(noTags, @"\s+", " ").Trim();
        return collapsed.Length == 0 ? null : System.Net.WebUtility.HtmlDecode(collapsed);
    }

    private static string? TrimToFirstParagraph(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Keep things scannable - only the first sentence-ish chunk shows on the row.
        var idx = text.IndexOf('\n');
        if (idx > 0 && idx < text.Length) text = text[..idx];
        return text.Length > 280 ? text[..280].TrimEnd() + "..." : text;
    }

    private static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0) return "";
        var ts = TimeSpan.FromMilliseconds(durationMs);
        if (ts.TotalHours >= 1)
        {
            var hr = (int)ts.TotalHours;
            var min = ts.Minutes;
            return min > 0 ? $"{hr} hr {min} min" : $"{hr} hr";
        }
        var totalMin = Math.Max(1, (int)Math.Round(ts.TotalMinutes));
        return $"{totalMin} min";
    }

    private static string? ImageUrlFromCoverGroup(ImageGroup? group)
    {
        if (group is null || group.Image.Count == 0) return null;
        // Prefer Default > Large > Small. Same ordering ExtendedMetadataClient
        // uses when it stores entity rows, so the URL we mint matches the cache
        // and we benefit from any in-memory image cache the SquareImage control
        // already holds.
        var image = group.Image
            .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2
                                   : i.Size == Image.Types.Size.Large ? 1 : 0)
            .FirstOrDefault();
        if (image?.FileId is null || image.FileId.Length == 0) return null;
        var hex = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
        return $"https://i.scdn.co/image/{hex}";
    }

    private static IReadOnlyList<string> MergeEpisodeUris(
        IReadOnlyList<string> firstPageUris,
        IReadOnlyList<string>? allEpisodeUris)
    {
        if (allEpisodeUris is null || allEpisodeUris.Count == 0)
            return DeduplicateEpisodeUris(firstPageUris);

        var merged = new List<string>(Math.Max(firstPageUris.Count, allEpisodeUris.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var uri in allEpisodeUris)
        {
            if (!string.IsNullOrEmpty(uri) && seen.Add(uri))
                merged.Add(uri);
        }

        foreach (var uri in firstPageUris)
        {
            if (!string.IsNullOrEmpty(uri) && seen.Add(uri))
                merged.Add(uri);
        }

        return merged;
    }

    private static IReadOnlyList<string> DeduplicateEpisodeUris(IEnumerable<string> episodeUris)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var uri in episodeUris)
        {
            if (!string.IsNullOrEmpty(uri) && seen.Add(uri))
                result.Add(uri);
        }

        return result;
    }

    private static bool HasUsableEpisodeMetadata(Episode episode)
        => !string.IsNullOrWhiteSpace(episode.Name)
           || episode.Duration > 0
           || episode.PublishTime is not null;

    private static string? EpisodeUriFromGid(ByteString? gid)
    {
        if (gid is not { Length: > 0 })
            return null;

        try
        {
            return $"spotify:episode:{SpotifyId.FromRaw(gid.Span, SpotifyIdType.Episode).ToBase62()}";
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractIdFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        var idx = uri.LastIndexOf(':');
        return idx >= 0 && idx < uri.Length - 1 ? uri[(idx + 1)..] : uri;
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }

    private readonly record struct EpisodeProgressSnapshot(string? State, long PlayedPositionMs);
}
