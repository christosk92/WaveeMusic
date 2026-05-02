using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Entities;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Enriches track metadata by fetching from the Spotify API after the session connects.
/// Created in InitializePlaybackEngine, communicates via <see cref="IMessenger"/>.
/// </summary>
internal sealed class TrackMetadataEnricher : IRecipient<TrackEnrichmentRequestMessage>,
    IRecipient<ExtendedTopTracksRequest>,
    IRecipient<QueueEnrichmentRequestMessage>,
    IRecipient<TrackImagesEnrichmentRequest>,
    IDisposable
{
    private readonly IExtendedMetadataClient _metadataClient;
    private readonly ICacheService _cacheService;
    private readonly ISpClient _spClient;
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;
    private CancellationTokenSource? _enrichmentCts;

    public TrackMetadataEnricher(
        IExtendedMetadataClient metadataClient,
        ICacheService cacheService,
        ISpClient spClient,
        IMessenger messenger,
        ILogger? logger = null)
    {
        _metadataClient = metadataClient;
        _cacheService = cacheService;
        _spClient = spClient;
        _messenger = messenger;
        _logger = logger;

        messenger.Register<TrackEnrichmentRequestMessage>(this);
        messenger.Register<ExtendedTopTracksRequest>(this);
        messenger.Register<QueueEnrichmentRequestMessage>(this);
        messenger.Register<TrackImagesEnrichmentRequest>(this);
    }

    // ── Track enrichment (from PlaybackStateService) ──

    public void Receive(TrackEnrichmentRequestMessage message)
    {
        var trackUri = message.Value;
        if (string.IsNullOrEmpty(trackUri)) return;

        _enrichmentCts?.Cancel();
        _enrichmentCts?.Dispose();
        _enrichmentCts = new CancellationTokenSource();
        var ct = _enrichmentCts.Token;

        _ = EnrichPlayableAsync(trackUri, ct);
    }

    private async Task EnrichPlayableAsync(string uri, CancellationToken ct)
    {
        if (IsSpotifyEpisodeUri(uri))
        {
            await EnrichEpisodeAsync(uri, ct).ConfigureAwait(false);
            return;
        }

        if (IsSpotifyTrackUri(uri))
        {
            await EnrichTrackAsync(uri, ct).ConfigureAwait(false);
        }
    }

    private async Task EnrichTrackAsync(string trackUri, CancellationToken ct)
    {
        try
        {
            // Yield immediately so the calling dispatcher frame (OnRemoteStateChanged)
            // can finish and render before we do cache lookups + protobuf deserialization.
            await Task.Yield();

            var track = await _metadataClient.GetTrackAsync(trackUri, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || track == null) return;

            var trackId = ExtractTrackId(trackUri);

            var title = track.Name;
            var artist = track.Artist.Count > 0
                ? string.Join(", ", track.Artist.Select(a => a.Name))
                : null;

            // Build per-artist credits (name + URI) for MetadataControl
            var artistCredits = new List<ArtistCredit>();
            foreach (var a in track.Artist)
            {
                string? uri = null;
                if (a.Gid is { Length: > 0 } aGid)
                    uri = $"spotify:artist:{SpotifyId.FromRaw(aGid.Span, SpotifyIdType.Artist).ToBase62()}";
                artistCredits.Add(new ArtistCredit(a.Name, uri));
            }

            var imageDefault = GetImageUrl(track.Album, Image.Types.Size.Default);
            var imageLarge = GetImageUrl(track.Album, Image.Types.Size.Large);
            var imageXLarge = GetImageUrl(track.Album, Image.Types.Size.Xlarge);

            string? artistUri = null;
            if (track.Artist.Count > 0 && track.Artist[0].Gid is { Length: > 0 } gid)
                artistUri = $"spotify:artist:{SpotifyId.FromRaw(gid.Span, SpotifyIdType.Artist).ToBase62()}";

            string? albumUri = null;
            if (track.Album?.Gid is { Length: > 0 } albumGid)
                albumUri = $"spotify:album:{SpotifyId.FromRaw(albumGid.Span, SpotifyIdType.Album).ToBase62()}";

            _logger?.LogInformation("Track metadata enriched: \"{Title}\" by \"{Artist}\" (uri={Uri})",
                title, artist, trackUri);

            _messenger.Send(new TrackMetadataEnrichedMessage
            {
                TrackId = trackId ?? trackUri,
                TrackUri = trackUri,
                Title = title,
                ArtistName = artist,
                AlbumArt = imageDefault,
                AlbumArtLarge = imageLarge ?? imageXLarge ?? imageDefault,
                ArtistId = artistUri,
                AlbumId = albumUri,
                Artists = artistCredits,
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich track metadata for {Uri}", trackUri);
        }
    }

    // ── Extended top tracks (from ArtistService) ──

    public void Receive(ExtendedTopTracksRequest message)
    {
        message.Reply(GetExtendedTopTracksAsync(message.ArtistUri, message.CancellationToken));
    }

    private async Task<List<ArtistTopTrackResult>> GetExtendedTopTracksAsync(
        string artistUri, CancellationToken ct)
    {
        try
        {
            // 1. Get track URIs from artistplaycontext endpoint
            var uris = await _spClient.GetArtistTopTrackExtensionsAsync(artistUri, ct);
            if (uris.Count == 0) return [];

            _logger?.LogDebug("Got {Count} extended top track URIs for {Artist}", uris.Count, artistUri);

            // 2. Check cache first
            var cached = await _cacheService.GetTracksAsync(uris, ct);
            var uncached = uris.Where(u => !cached.ContainsKey(u)).ToList();

            // 3. Fetch uncached via extended-metadata (batches of 500)
            if (uncached.Count > 0)
            {
                _logger?.LogDebug("Fetching metadata for {Count} uncached tracks", uncached.Count);
                const int batchSize = 500;
                for (int i = 0; i < uncached.Count; i += batchSize)
                {
                    var batch = uncached.Skip(i).Take(batchSize)
                        .Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackV4 }));
                    try
                    {
                        await _metadataClient.GetBatchedExtensionsAsync(batch, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to fetch metadata batch at offset {Offset}", i);
                    }
                }

                // Re-read from cache (now populated by the metadata client)
                var newCached = await _cacheService.GetTracksAsync(uncached, ct);
                foreach (var (uri, entry) in newCached)
                    cached[uri] = entry;
            }

            // 4. Map to ArtistTopTrackResult (preserving order)
            var results = new List<ArtistTopTrackResult>();
            foreach (var uri in uris)
            {
                if (cached.TryGetValue(uri, out var track))
                {
                    var id = uri.Replace("spotify:track:", "");
                    results.Add(new ArtistTopTrackResult
                    {
                        Id = id,
                        Title = track.Title,
                        Uri = uri,
                        AlbumImageUrl = track.ImageUrl,
                        AlbumUri = track.AlbumUri,
                        AlbumName = track.Album,
                        Duration = TimeSpan.FromMilliseconds(track.DurationMs ?? 0),
                        PlayCount = 0,
                        ArtistNames = track.Artist,
                        IsExplicit = track.IsExplicit,
                        IsPlayable = track.IsPlayable,
                        HasVideo = false
                    });
                }
            }

            _logger?.LogDebug("Enriched {Count}/{Total} extended top tracks for {Artist}",
                results.Count, uris.Count, artistUri);
            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get extended top tracks for {Artist}", artistUri);
            return [];
        }
    }

    private async Task EnrichEpisodeAsync(string episodeUri, CancellationToken ct)
    {
        try
        {
            await Task.Yield();

            var episodeId = SpotifyId.FromUri(episodeUri).ToBase62();
            var bytes = await _spClient.GetEpisodeMetadataAsync(episodeId, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || bytes.Length == 0) return;

            var episode = Episode.Parser.ParseFrom(bytes);
            var showName = episode.Show?.Name;
            var showUri = GetShowUri(episode.Show);
            var imageDefault = GetEpisodeImageUrl(episode, Image.Types.Size.Default);
            var imageLarge = GetEpisodeImageUrl(episode, Image.Types.Size.Large);
            var imageXLarge = GetEpisodeImageUrl(episode, Image.Types.Size.Xlarge);

            _logger?.LogInformation("Episode metadata enriched: \"{Title}\" from \"{Show}\" (uri={Uri})",
                episode.Name, showName, episodeUri);

            _messenger.Send(new TrackMetadataEnrichedMessage
            {
                TrackId = episodeUri,
                TrackUri = episodeUri,
                Title = episode.Name,
                ArtistName = showName,
                AlbumArt = imageDefault ?? imageLarge ?? imageXLarge,
                AlbumArtLarge = imageLarge ?? imageXLarge ?? imageDefault,
                ArtistId = null,
                AlbumId = showUri,
                Artists = null,
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich episode metadata for {Uri}", episodeUri);
        }
    }

    // ── Queue batch enrichment ──

    public void Receive(QueueEnrichmentRequestMessage message)
    {
        var uris = message.Value;
        if (uris == null || uris.Count == 0) return;
        _ = EnrichQueueTracksAsync(uris);
    }

    private async Task EnrichQueueTracksAsync(IReadOnlyList<string> trackUris)
    {
        try
        {
            await Task.Yield();

            _logger?.LogDebug("Queue enrichment: fetching metadata for {Count} playable items", trackUris.Count);

            var trackOnlyUris = trackUris.Where(IsSpotifyTrackUri).Distinct().ToList();
            var episodeUris = trackUris.Where(IsSpotifyEpisodeUri).Distinct().ToList();
            var result = new Dictionary<string, QueueTrackMetadata>();

            if (trackOnlyUris.Count > 0)
            {
                // Check cache first. Treat an entry with no ImageUrl as incomplete —
                // it was likely populated by a path that stored only title/artist
                // (bare playback, TrackReference) without running the TrackV4
                // extension fetch that carries album cover art. Without this, the
                // queue shows enriched titles but empty artwork placeholders forever,
                // because the enricher considers any entry "done" once it exists.
                var cached = await _cacheService.GetTracksAsync(trackOnlyUris, CancellationToken.None);
                var uncached = trackOnlyUris.Where(u =>
                    !cached.TryGetValue(u, out var entry) || string.IsNullOrEmpty(entry.ImageUrl)).ToList();

                // Batch-fetch uncached (or incomplete)
                if (uncached.Count > 0)
                {
                    _logger?.LogDebug("Queue enrichment: {CachedCount} cached, {UncachedCount} need fetch (missing or incomplete)",
                        cached.Count, uncached.Count);

                    const int batchSize = 500;
                    for (int i = 0; i < uncached.Count; i += batchSize)
                    {
                        var batch = uncached.Skip(i).Take(batchSize)
                            .Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackV4 }));
                        try
                        {
                            await _metadataClient.GetBatchedExtensionsAsync(batch, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Queue enrichment batch failed at offset {Offset}", i);
                        }
                    }

                    // Re-read from cache
                    var newCached = await _cacheService.GetTracksAsync(uncached, CancellationToken.None);
                    foreach (var (uri, entry) in newCached)
                        cached[uri] = entry;
                }

                foreach (var (uri, entry) in cached)
                {
                    result[uri] = new QueueTrackMetadata(
                        Title: entry.Title ?? "",
                        ArtistName: entry.Artist ?? "",
                        AlbumArt: entry.ImageUrl,
                        DurationMs: entry.DurationMs ?? 0);
                }
            }

            foreach (var episodeUri in episodeUris)
            {
                var metadata = await GetEpisodeQueueMetadataAsync(episodeUri, CancellationToken.None)
                    .ConfigureAwait(false);
                if (metadata != null)
                    result[episodeUri] = metadata;
            }

            _logger?.LogDebug("Queue enrichment complete: {EnrichedCount}/{TotalCount} playable items enriched",
                result.Count, trackUris.Count);

            if (result.Count > 0)
                _messenger.Send(new QueueMetadataEnrichedMessage { Tracks = result });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Queue enrichment failed");
        }
    }

    // ── Track image enrichment (cover-art-only, used by ArtistViewModel top tracks) ──

    public void Receive(TrackImagesEnrichmentRequest message)
    {
        message.Reply(GetTrackImagesAsync(message.TrackUris, message.CancellationToken));
    }

    private async Task<IReadOnlyDictionary<string, string?>> GetTrackImagesAsync(
        IReadOnlyList<string> trackUris, CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(trackUris.Count);
        if (trackUris.Count == 0) return result;

        try
        {
            var trackOnlyUris = trackUris.Where(IsSpotifyTrackUri).Distinct().ToList();
            var episodeUris = trackUris.Where(IsSpotifyEpisodeUri).Distinct().ToList();

            // Mirror QueueEnrichmentRequestMessage's flow: cache lookup, then
            // batch-fetch any missing/incomplete entries via TrackV4 extension.
            if (trackOnlyUris.Count > 0)
            {
                var cached = await _cacheService.GetTracksAsync(trackOnlyUris, ct);
                var uncached = trackOnlyUris.Where(u =>
                    !cached.TryGetValue(u, out var entry) || string.IsNullOrEmpty(entry.ImageUrl)).ToList();

                if (uncached.Count > 0)
                {
                    const int batchSize = 500;
                    for (int i = 0; i < uncached.Count; i += batchSize)
                    {
                        var batch = uncached.Skip(i).Take(batchSize)
                            .Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.TrackV4 }));
                        try
                        {
                            await _metadataClient.GetBatchedExtensionsAsync(batch, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Track-image enrichment batch failed at offset {Offset}", i);
                        }
                    }

                    var newCached = await _cacheService.GetTracksAsync(uncached, ct);
                    foreach (var (uri, entry) in newCached)
                        cached[uri] = entry;
                }

                foreach (var uri in trackOnlyUris)
                    result[uri] = cached.TryGetValue(uri, out var entry) ? entry.ImageUrl : null;
            }

            // Episodes — go through the EpisodeV4 batched extension. The
            // per-URI _spClient.GetEpisodeMetadataAsync path (kept as
            // GetEpisodeImageAsync below for the queue-enrichment caller) hits
            // /metadata/4/episode/{id}?market=from_token, which 404s for
            // user-only "Your Episodes" entries. The extended-metadata endpoint
            // populates the rest of the app's episode UI and handles those
            // entries correctly.
            //
            // GetBatchedExtensionsAsync already de-dupes against the SQLite
            // extension cache and only fetches uncached entries — no outer
            // batching loop needed here. We still pre-check via GetEpisodesAsync
            // so cache hits skip the call entirely; misses are forwarded.
            if (episodeUris.Count > 0)
            {
                var cached = await _cacheService.GetEpisodesAsync(episodeUris, ct);
                var uncached = episodeUris.Where(u =>
                    !cached.TryGetValue(u, out var entry) || string.IsNullOrEmpty(entry.ImageUrl)).ToList();

                if (uncached.Count > 0)
                {
                    try
                    {
                        await _metadataClient.GetBatchedExtensionsAsync(
                            uncached.Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.EpisodeV4 })),
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Episode-image enrichment failed for {Count} URIs", uncached.Count);
                    }

                    var newCached = await _cacheService.GetEpisodesAsync(uncached, ct);
                    foreach (var (uri, entry) in newCached)
                        cached[uri] = entry;
                }

                foreach (var uri in episodeUris)
                    result[uri] = cached.TryGetValue(uri, out var entry) ? entry.ImageUrl : null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Track-image enrichment failed for {Count} URIs", trackUris.Count);
        }

        return result;
    }

    // ── Helpers ──

    private static bool IsSpotifyTrackUri(string? uri)
        => uri?.StartsWith("spotify:track:", StringComparison.Ordinal) == true;

    private static bool IsSpotifyEpisodeUri(string? uri)
        => uri?.StartsWith("spotify:episode:", StringComparison.Ordinal) == true;

    private static string? ExtractTrackId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        const string prefix = "spotify:track:";
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    private static string? ExtractEpisodeId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        const string prefix = "spotify:episode:";
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    private async Task<QueueTrackMetadata?> GetEpisodeQueueMetadataAsync(string episodeUri, CancellationToken ct)
    {
        try
        {
            var episode = await GetEpisodeAsync(episodeUri, ct).ConfigureAwait(false);
            if (episode is null) return null;

            return new QueueTrackMetadata(
                Title: episode.Name ?? "",
                ArtistName: episode.Show?.Name ?? "",
                AlbumArt: GetEpisodeImageUrl(episode, Image.Types.Size.Default)
                          ?? GetEpisodeImageUrl(episode, Image.Types.Size.Large)
                          ?? GetEpisodeImageUrl(episode, Image.Types.Size.Xlarge),
                DurationMs: episode.Duration);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve episode queue metadata for {Uri}", episodeUri);
            return null;
        }
    }

    private async Task<string?> GetEpisodeImageAsync(string episodeUri, CancellationToken ct)
    {
        try
        {
            var episode = await GetEpisodeAsync(episodeUri, ct).ConfigureAwait(false);
            if (episode is null) return null;

            return GetEpisodeImageUrl(episode, Image.Types.Size.Default)
                   ?? GetEpisodeImageUrl(episode, Image.Types.Size.Large)
                   ?? GetEpisodeImageUrl(episode, Image.Types.Size.Xlarge);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve episode image for {Uri}", episodeUri);
            return null;
        }
    }

    private async Task<Episode?> GetEpisodeAsync(string episodeUri, CancellationToken ct)
    {
        if (!IsSpotifyEpisodeUri(episodeUri)) return null;

        var episodeId = SpotifyId.FromUri(episodeUri).ToBase62();
        var bytes = await _spClient.GetEpisodeMetadataAsync(episodeId, ct).ConfigureAwait(false);
        return bytes.Length == 0 ? null : Episode.Parser.ParseFrom(bytes);
    }

    private static string? GetShowUri(Show? show)
    {
        if (show?.Gid is not { Length: > 0 } gid)
            return null;

        return $"spotify:show:{SpotifyId.FromRaw(gid.Span, SpotifyIdType.Show).ToBase62()}";
    }

    private static string? GetImageUrl(Album? album, Image.Types.Size preferredSize)
    {
        return GetImageUrl(album?.CoverGroup, preferredSize);
    }

    private static string? GetEpisodeImageUrl(Episode? episode, Image.Types.Size preferredSize)
    {
        var image = GetImageUrl(episode?.CoverImage, preferredSize);
        if (!string.IsNullOrEmpty(image)) return image;

        return GetImageUrl(episode?.Show?.CoverImage, preferredSize);
    }

    private static string? GetImageUrl(ImageGroup? imageGroup, Image.Types.Size preferredSize)
    {
        if (imageGroup?.Image.Count is not > 0) return null;
        var image = imageGroup.Image.FirstOrDefault(i => i.Size == preferredSize)
                    ?? imageGroup.Image.FirstOrDefault();
        if (image == null) return null;
        return $"spotify:image:{Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant()}";
    }

    public void Dispose()
    {
        _enrichmentCts?.Cancel();
        _enrichmentCts?.Dispose();
        _enrichmentCts = null;
        _messenger.UnregisterAll(this);
    }
}
