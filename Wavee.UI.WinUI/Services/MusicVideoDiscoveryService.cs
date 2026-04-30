using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Coordinates music-video discovery for the currently-playing audio track
/// using a layered approach:
///
/// <list type="number">
/// <item>Cheap path: <see cref="IMusicVideoCatalogCache"/> populated by the
/// GraphQL response handlers Wavee already runs (artist top tracks, album
/// tracks, search). When the cache has a hit (positive or negative), no
/// extra HTTP fires.</item>
/// <item>Fallback path: when the cache returns null, run
/// <c>queryNpvArtist</c> in the background to determine availability AND
/// stash the audio→video URI mapping for later click-time resolution.</item>
/// </list>
///
/// Manifest_id resolution (the actual TrackV4 fetch on the video URI) is
/// always deferred to <see cref="ResolveManifestIdAsync"/> — called from
/// <c>PlaybackStateService.SwitchToVideoAsync</c> at click time.
/// </summary>
internal sealed class MusicVideoDiscoveryService
    : IMusicVideoDiscoveryService, IDisposable
{
    private const int VideoAssociationsExtensionKindValue = 99;

    private readonly IPathfinderClient _pathfinder;
    private readonly IExtendedMetadataClient _extendedMetadata;
    private readonly IMessenger _messenger;
    private readonly IMusicVideoCatalogCache _cache;
    private readonly ILogger<MusicVideoDiscoveryService>? _logger;

    private CancellationTokenSource? _activeDiscoveryCts;

    public MusicVideoDiscoveryService(
        IPathfinderClient pathfinder,
        IExtendedMetadataClient extendedMetadata,
        IMessenger messenger,
        IMusicVideoCatalogCache cache,
        ILogger<MusicVideoDiscoveryService>? logger = null)
    {
        _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
        _extendedMetadata = extendedMetadata ?? throw new ArgumentNullException(nameof(extendedMetadata));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
    }

    // PlaybackStateService is resolved lazily via Ioc.Default to break the
    // construction cycle (the state service consumes this service for click-
    // time resolution).
    private IPlaybackStateService? PlaybackState =>
        Ioc.Default.GetService<IPlaybackStateService>();

    // ── IMusicVideoDiscoveryService.BeginBackgroundDiscovery ──

    public void BeginBackgroundDiscovery(string audioTrackUri)
    {
        if (string.IsNullOrEmpty(audioTrackUri)) return;

        // Self-contained tracks already have the manifest published via
        // PlaybackStateService.CurrentTrackManifestId — skip everything.
        var ps = PlaybackState;
        if (!string.IsNullOrEmpty(ps?.CurrentTrackManifestId))
        {
            _logger?.LogDebug("[VideoDiscovery] {Track}: skipping — self-contained manifest already known", audioTrackUri);
            return;
        }

        // Already cached? Caller should've checked, but guard regardless.
        var cached = _cache.GetHasVideo(audioTrackUri);
        if (cached.HasValue)
        {
            _logger?.LogInformation("[VideoDiscovery] {Track}: cache hit hasVideo={HasVideo} — no NPV needed", audioTrackUri, cached.Value);
            Publish(audioTrackUri, cached.Value);
            if (!cached.Value || _cache.TryGetVideoUri(audioTrackUri, out _))
                return;

            _logger?.LogInformation("[VideoDiscovery] {Track}: positive hint without video URI; prefetching NPV mapping", audioTrackUri);
        }

        // Cancel any in-flight discovery from a previous track.
        _activeDiscoveryCts?.Cancel();
        _activeDiscoveryCts = new CancellationTokenSource();
        var ct = _activeDiscoveryCts.Token;

        _logger?.LogInformation("[VideoDiscovery] {Track}: cache miss → extended metadata / NPV", audioTrackUri);
        _ = DiscoverVideoMappingAsync(audioTrackUri, ct);
    }

    private async Task DiscoverVideoMappingAsync(string audioUri, CancellationToken ct)
    {
        try
        {
            await Task.Yield();

            var metadataVideoUri = await TryResolveVideoUriViaExtendedMetadataAsync(audioUri, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(metadataVideoUri))
            {
                _cache.NoteVideoUri(audioUri, metadataVideoUri);
                _logger?.LogInformation("[VideoDiscovery] extended metadata {Audio} → {Video}", audioUri, metadataVideoUri);
                Publish(audioUri, true);
                return;
            }

            var artistUri = PlaybackState?.CurrentArtistId;
            if (string.IsNullOrEmpty(artistUri))
            {
                _logger?.LogDebug("[VideoDiscovery] {Track}: no artist URI yet — skipping NPV", audioUri);
                return;
            }

            var npv = await _pathfinder.GetNpvArtistAsync(artistUri, audioUri, ct: ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            CacheAllNpvVideoMappings(npv);
            var videoUri = FindMatchingVideoUri(npv, audioUri);
            if (string.IsNullOrEmpty(videoUri))
            {
                _cache.NoteHasVideo(audioUri, false);
                _logger?.LogInformation("[VideoDiscovery] {Track}: no music video", audioUri);
                Publish(audioUri, false);
                return;
            }

            _cache.NoteVideoUri(audioUri, videoUri);
            _logger?.LogInformation("[VideoDiscovery] {Audio} → {Video}", audioUri, videoUri);
            Publish(audioUri, true);
        }
        catch (OperationCanceledException) { /* track changed */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[VideoDiscovery] discovery failed for {Track} (non-fatal)", audioUri);
        }
    }

    // ── IMusicVideoDiscoveryService ──

    public bool TryGetVideoUri(string audioTrackUri, out string videoTrackUri)
        => _cache.TryGetVideoUri(audioTrackUri, out videoTrackUri);

    public async Task<string?> ResolveManifestIdAsync(string audioTrackUri, CancellationToken cancellationToken = default)
    {
        // Fast path: manifest_id already cached.
        if (_cache.TryGetManifestId(audioTrackUri, out var cached))
        {
            _logger?.LogInformation("[VideoDiscovery] {Track}: cache hit manifestId={Manifest}", audioTrackUri, cached);
            return cached;
        }

        // Need the video URI. Use cache if available, otherwise use the exact
        // VIDEO_ASSOCIATIONS extension before falling back to NPV.
        if (!_cache.TryGetVideoUri(audioTrackUri, out var videoUri))
        {
            videoUri = await TryResolveVideoUriViaExtendedMetadataAsync(audioTrackUri, cancellationToken).ConfigureAwait(false)
                ?? string.Empty;

            if (!string.IsNullOrEmpty(videoUri))
            {
                _cache.NoteVideoUri(audioTrackUri, videoUri);
            }
            else
            {
                var artistUri = PlaybackState?.CurrentArtistId;
                if (string.IsNullOrEmpty(artistUri))
                {
                    _logger?.LogWarning("[VideoDiscovery] click-time NPV: no artist URI for {Track}", audioTrackUri);
                    return null;
                }

                try
                {
                    var npv = await _pathfinder.GetNpvArtistAsync(artistUri, audioTrackUri, ct: cancellationToken).ConfigureAwait(false);
                    CacheAllNpvVideoMappings(npv);
                    videoUri = FindMatchingVideoUri(npv, audioTrackUri) ?? string.Empty;
                    if (!string.IsNullOrEmpty(videoUri))
                        _cache.NoteVideoUri(audioTrackUri, videoUri);
                    else
                        _cache.NoteHasVideo(audioTrackUri, false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[VideoDiscovery] click-time NPV failed for {Track}", audioTrackUri);
                    return null;
                }

                if (string.IsNullOrEmpty(videoUri)) return null;
            }
        }

        // Fetch the video URI's TrackV4 to extract the manifest_id.
        try
        {
            var videoTrack = await _extendedMetadata.GetTrackAudioFilesAsync(videoUri, cancellationToken).ConfigureAwait(false);
            if (videoTrack is null || videoTrack.OriginalVideo.Count == 0) return null;
            var bytes = videoTrack.OriginalVideo[0].Gid;
            if (bytes is null || bytes.Length == 0) return null;

            var manifestId = Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();
            _cache.NoteManifestId(audioTrackUri, manifestId);

            _logger?.LogInformation("[VideoDiscovery] resolved manifest {Manifest} for {Track}",
                manifestId, audioTrackUri);
            return manifestId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[VideoDiscovery] manifest fetch failed for {Track}", audioTrackUri);
            return null;
        }
    }

    // ── Helpers ──

    private void Publish(string audioUri, bool hasVideo)
    {
        _messenger.Send(new MusicVideoAvailabilityMessage(audioUri, hasVideo));
    }

    private async Task<string?> TryResolveVideoUriViaExtendedMetadataAsync(string audioUri, CancellationToken ct)
    {
        try
        {
            var data = await _extendedMetadata
                .GetExtensionAsync(audioUri, (ExtensionKind)VideoAssociationsExtensionKindValue, ct)
                .ConfigureAwait(false);

            var videoUri = TryReadVideoAssociationUri(data);
            if (string.IsNullOrWhiteSpace(videoUri))
            {
                _logger?.LogDebug("[VideoDiscovery] {Track}: VIDEO_ASSOCIATIONS returned no video URI", audioUri);
                return null;
            }

            return videoUri;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[VideoDiscovery] VIDEO_ASSOCIATIONS failed for {Track} (non-fatal)", audioUri);
            return null;
        }
    }

    private static string? TryReadVideoAssociationUri(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;

        try
        {
            var assoc = Assoc.Parser.ParseFrom(data);
            var uri = assoc.PlainList?.EntityUri.FirstOrDefault(IsSpotifyTrackUri);
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        catch (InvalidProtocolBufferException)
        {
        }

        try
        {
            var plainList = PlainListAssoc.Parser.ParseFrom(data);
            var uri = plainList.EntityUri.FirstOrDefault(IsSpotifyTrackUri);
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        catch (InvalidProtocolBufferException)
        {
        }

        var text = Encoding.UTF8.GetString(data);
        var markerIndex = text.IndexOf("spotify:track:", StringComparison.Ordinal);
        if (markerIndex < 0) return null;

        var endIndex = markerIndex;
        while (endIndex < text.Length)
        {
            var ch = text[endIndex];
            if (!IsSpotifyUriChar(ch)) break;
            endIndex++;
        }

        var candidate = text[markerIndex..endIndex];
        return IsSpotifyTrackUri(candidate) ? candidate : null;
    }

    private static bool IsSpotifyTrackUri(string? uri)
        => !string.IsNullOrWhiteSpace(uri)
           && uri.StartsWith("spotify:track:", StringComparison.Ordinal);

    private static bool IsSpotifyUriChar(char ch)
        => (ch >= 'a' && ch <= 'z')
           || (ch >= 'A' && ch <= 'Z')
           || (ch >= '0' && ch <= '9')
           || ch == ':';

    private void CacheAllNpvVideoMappings(NpvArtistResponse? npv)
    {
        var related = npv?.Data?.TrackUnion?.RelatedVideos?.Items;
        if (related is null || related.Count == 0) return;

        foreach (var item in related)
        {
            var videoUri = item.TrackOfVideo?.Uri ?? item.TrackOfVideo?.Data?.Uri;
            if (string.IsNullOrWhiteSpace(videoUri)) continue;

            var audioUris = item.TrackOfVideo?.Data?.AssociationsV3?.AudioAssociations?.Items?
                .Select(association => association.TrackAudio?.Uri)
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .Select(uri => uri!)
                .Distinct(StringComparer.Ordinal);

            if (audioUris is null) continue;

            foreach (var audioUri in audioUris)
            {
                _cache.NoteVideoUri(audioUri, videoUri);
                _logger?.LogDebug("[VideoDiscovery] NPV prewarm {Audio} -> {Video}", audioUri, videoUri);
            }
        }
    }

    /// <summary>
    /// Resolves the music-video URI for the playing audio track from a
    /// <c>queryNpvArtist</c> response. Two complementary projections are
    /// checked, in order of precision:
    ///
    /// <list type="number">
    /// <item><c>TrackUnion.AssociationsV3.UnmappedVideoTrackAssociations
    /// .Items[].TrackVideo._uri</c> — the direct audio→video mapping for the
    /// playing track. Available when the audio URI has a "linked" music
    /// video on a separate URI (drunk text → 00cDl02L…).</item>
    /// <item><c>TrackUnion.RelatedVideos.Items[].TrackOfVideo</c> — the
    /// "Related videos" sidebar list. Used as a fallback by walking each
    /// item's reverse <c>audioAssociations.items[0].trackAudio._uri</c> and
    /// matching against the playing audio URI.</item>
    /// </list>
    /// </summary>
    private static string? FindMatchingVideoUri(NpvArtistResponse? npv, string audioUri)
    {
        var trackUnion = npv?.Data?.TrackUnion;
        if (trackUnion is null) return null;

        // Direct mapping — the audio track's own associationsV3 lists video
        // tracks linked to it (via associatedTrack._uri). This is the cheap,
        // exact match path. When multiple associated videos exist (e.g.,
        // multiple live performances), we pick the first.
        var direct = trackUnion.AssociationsV3?.UnmappedVideoTrackAssociations?.Items;

        // Fallback — walk RelatedVideos and reverse-match via the video
        // track's audioAssociations. Slower and only catches cases where the
        // pair shows up in the "Related videos" list.
        var related = trackUnion.RelatedVideos?.Items;
        if (related is not null)
        {
            foreach (var rv in related)
            {
                var ttv = rv.TrackOfVideo;
                if (ttv?.Uri is null) continue;
                var linkedAudio = ttv.Data?.AssociationsV3?.AudioAssociations?.Items?.FirstOrDefault()?.TrackAudio?.Uri;
                if (string.Equals(linkedAudio, audioUri, StringComparison.Ordinal))
                    return ttv.Uri;
            }
        }

        if (direct is { Count: > 0 })
        {
            var first = direct.Select(item => item?.AssociatedTrack?.Uri)
                .FirstOrDefault(uri => !string.IsNullOrEmpty(uri));
            if (!string.IsNullOrEmpty(first)) return first;
        }

        return null;
    }

    public void Dispose()
    {
        _activeDiscoveryCts?.Cancel();
        _activeDiscoveryCts?.Dispose();
    }
}
