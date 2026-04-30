using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
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
    private readonly IPathfinderClient _pathfinder;
    private readonly IMessenger _messenger;
    private readonly IMusicVideoMetadataService _metadata;
    private readonly ILogger<MusicVideoDiscoveryService>? _logger;

    private CancellationTokenSource? _activeDiscoveryCts;

    public MusicVideoDiscoveryService(
        IPathfinderClient pathfinder,
        IMessenger messenger,
        IMusicVideoMetadataService metadata,
        ILogger<MusicVideoDiscoveryService>? logger = null)
    {
        _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
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
        var cached = _metadata.GetKnownAvailability(audioTrackUri);
        if (cached.HasValue)
        {
            _logger?.LogInformation("[VideoDiscovery] {Track}: cache hit hasVideo={HasVideo} — no NPV needed", audioTrackUri, cached.Value);
            Publish(audioTrackUri, cached.Value);
            if (!cached.Value || _metadata.TryGetVideoUri(audioTrackUri, out _))
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

            var metadataVideoUri = await _metadata.TryResolveVideoUriViaExtendedMetadataAsync(audioUri, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(metadataVideoUri))
            {
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
                _metadata.NoteHasVideo(audioUri, false);
                _logger?.LogInformation("[VideoDiscovery] {Track}: no music video", audioUri);
                Publish(audioUri, false);
                return;
            }

            _metadata.NoteVideoUri(audioUri, videoUri);
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
        => _metadata.TryGetVideoUri(audioTrackUri, out videoTrackUri);

    public async Task<string?> ResolveManifestIdAsync(string audioTrackUri, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifestId = await _metadata.ResolveManifestIdAsync(audioTrackUri, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(manifestId))
            {
                _logger?.LogInformation("[VideoDiscovery] resolved manifest {Manifest} for {Track}",
                    manifestId, audioTrackUri);
                return manifestId;
            }

            var artistUri = PlaybackState?.CurrentArtistId;
            if (string.IsNullOrEmpty(artistUri))
            {
                _logger?.LogWarning("[VideoDiscovery] click-time NPV: no artist URI for {Track}", audioTrackUri);
                return null;
            }

            var npv = await _pathfinder.GetNpvArtistAsync(artistUri, audioTrackUri, ct: cancellationToken).ConfigureAwait(false);
            CacheAllNpvVideoMappings(npv);
            var videoUri = FindMatchingVideoUri(npv, audioTrackUri);
            if (string.IsNullOrEmpty(videoUri))
            {
                _metadata.NoteHasVideo(audioTrackUri, false);
                return null;
            }

            _metadata.NoteVideoUri(audioTrackUri, videoUri);
            return await _metadata.ResolveManifestIdAsync(audioTrackUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[VideoDiscovery] manifest/NPV resolution failed for {Track}", audioTrackUri);
            return null;
        }
    }

    // ── Helpers ──

    private void Publish(string audioUri, bool hasVideo)
    {
        _messenger.Send(new MusicVideoAvailabilityMessage(audioUri, hasVideo));
    }

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
                _metadata.NoteVideoUri(audioUri, videoUri);
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
