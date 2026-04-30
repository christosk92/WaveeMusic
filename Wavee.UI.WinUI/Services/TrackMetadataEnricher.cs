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

        _ = EnrichTrackAsync(trackUri, ct);
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

            _logger?.LogDebug("Queue enrichment: fetching metadata for {Count} tracks", trackUris.Count);

            // Check cache first. Treat an entry with no ImageUrl as incomplete —
            // it was likely populated by a path that stored only title/artist
            // (bare playback, TrackReference) without running the TrackV4
            // extension fetch that carries album cover art. Without this, the
            // queue shows enriched titles but empty artwork placeholders forever,
            // because the enricher considers any entry "done" once it exists.
            var cached = await _cacheService.GetTracksAsync(trackUris, CancellationToken.None);
            var uncached = trackUris.Where(u =>
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

            // Build result
            var result = new Dictionary<string, QueueTrackMetadata>();
            foreach (var (uri, entry) in cached)
            {
                result[uri] = new QueueTrackMetadata(
                    Title: entry.Title ?? "",
                    ArtistName: entry.Artist ?? "",
                    AlbumArt: entry.ImageUrl,
                    DurationMs: entry.DurationMs ?? 0);
            }

            _logger?.LogDebug("Queue enrichment complete: {EnrichedCount}/{TotalCount} tracks enriched",
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
            // Mirror QueueEnrichmentRequestMessage's flow: cache lookup, then
            // batch-fetch any missing/incomplete entries via TrackV4 extension.
            var cached = await _cacheService.GetTracksAsync(trackUris, ct);
            var uncached = trackUris.Where(u =>
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

            foreach (var uri in trackUris)
                result[uri] = cached.TryGetValue(uri, out var entry) ? entry.ImageUrl : null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Track-image enrichment failed for {Count} URIs", trackUris.Count);
        }

        return result;
    }

    // ── Helpers ──

    private static string? ExtractTrackId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        const string prefix = "spotify:track:";
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    private static string? GetImageUrl(Album? album, Image.Types.Size preferredSize)
    {
        if (album?.CoverGroup?.Image.Count is not > 0) return null;
        var image = album.CoverGroup.Image.FirstOrDefault(i => i.Size == preferredSize)
                    ?? album.CoverGroup.Image.FirstOrDefault();
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
