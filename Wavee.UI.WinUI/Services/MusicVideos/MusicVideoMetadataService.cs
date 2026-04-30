using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.UI.WinUI.Data.Stores;

namespace Wavee.UI.WinUI.Services;

internal sealed class MusicVideoMetadataService : IMusicVideoMetadataService
{
    private const int BatchSize = 200;
    private const long NegativeCacheTtlSeconds = 24 * 60 * 60;

    private readonly ExtendedMetadataStore _extendedMetadataStore;
    private readonly IExtendedMetadataClient _extendedMetadataClient;
    private readonly IMetadataDatabase _database;
    private readonly IMusicVideoCatalogCache _catalogCache;
    private readonly ILogger<MusicVideoMetadataService>? _logger;

    public MusicVideoMetadataService(
        ExtendedMetadataStore extendedMetadataStore,
        IExtendedMetadataClient extendedMetadataClient,
        IMetadataDatabase database,
        IMusicVideoCatalogCache catalogCache,
        ILogger<MusicVideoMetadataService>? logger = null)
    {
        _extendedMetadataStore = extendedMetadataStore;
        _extendedMetadataClient = extendedMetadataClient;
        _database = database;
        _catalogCache = catalogCache;
        _logger = logger;
    }

    public bool? GetKnownAvailability(string audioTrackUri)
        => _catalogCache.GetHasVideo(audioTrackUri);

    public async Task<IReadOnlyDictionary<string, bool>> GetCachedAvailabilityAsync(
        IEnumerable<string> trackUris,
        CancellationToken cancellationToken = default)
    {
        var unique = NormalizeTrackUris(trackUris);
        var result = new Dictionary<string, bool>(unique.Length, StringComparer.Ordinal);
        if (unique.Length == 0) return result;

        var diskCandidates = new List<string>(unique.Length);
        foreach (var uri in unique)
        {
            var hot = _catalogCache.GetHasVideo(uri);
            if (hot.HasValue)
            {
                result[uri] = hot.Value;
            }
            else
            {
                diskCandidates.Add(uri);
            }
        }

        if (diskCandidates.Count == 0) return result;

        var cached = await _database
            .GetExtensionsBulkAsync(diskCandidates, ExtensionKind.VideoAssociations, cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in cached)
        {
            ApplyAvailability(entry.Key, entry.Value, result);
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, bool>> EnsureAvailabilityAsync(
        IEnumerable<string> trackUris,
        CancellationToken cancellationToken = default)
    {
        var unique = NormalizeTrackUris(trackUris);
        var result = new Dictionary<string, bool>(
            await GetCachedAvailabilityAsync(unique, cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);

        var missing = unique.Where(uri => !result.ContainsKey(uri)).ToArray();
        if (missing.Length == 0) return result;

        foreach (var batch in missing.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requests = batch.Select(uri =>
                (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.VideoAssociations }));

            var resolved = await _extendedMetadataStore
                .GetManyAsync(requests, cancellationToken)
                .ConfigureAwait(false);

            var returnedUris = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in resolved)
            {
                var key = entry.Key;
                if (key.Kind != ExtensionKind.VideoAssociations) continue;
                returnedUris.Add(key.Uri);
                ApplyAvailability(key.Uri, entry.Value, result);
            }

            foreach (var uri in batch)
            {
                if (returnedUris.Contains(uri)) continue;

                result[uri] = false;
                _catalogCache.NoteHasVideo(uri, false);
                await WriteNegativeCacheAsync(uri, cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    public bool TryGetVideoUri(string audioTrackUri, out string videoTrackUri)
        => _catalogCache.TryGetVideoUri(audioTrackUri, out videoTrackUri);

    public bool TryGetAudioUri(string videoTrackUri, out string audioTrackUri)
        => _catalogCache.TryGetAudioUri(videoTrackUri, out audioTrackUri);

    public async Task<string?> TryResolveVideoUriViaExtendedMetadataAsync(
        string audioTrackUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioTrackUri)) return null;

        var data = await _extendedMetadataClient
            .GetExtensionAsync(audioTrackUri, ExtensionKind.VideoAssociations, cancellationToken)
            .ConfigureAwait(false);

        var videoUri = MusicVideoAssociationParser.TryReadVideoAssociationUri(data);
        if (string.IsNullOrWhiteSpace(videoUri))
        {
            _catalogCache.NoteHasVideo(audioTrackUri, false);
            return null;
        }

        _catalogCache.NoteVideoUri(audioTrackUri, videoUri);
        return videoUri;
    }

    public async Task<string?> TryResolveAudioUriViaExtendedMetadataAsync(
        string videoTrackUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoTrackUri)) return null;
        if (!videoTrackUri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        if (_catalogCache.TryGetAudioUri(videoTrackUri, out var cachedAudioUri)) return cachedAudioUri;

        var data = await _extendedMetadataStore
            .GetOnceAsync(videoTrackUri, ExtensionKind.AudioAssociations, cancellationToken)
            .ConfigureAwait(false);

        var audioUri = MusicVideoAssociationParser.TryReadAudioAssociationUri(data);
        if (string.IsNullOrWhiteSpace(audioUri))
        {
            return null;
        }

        _catalogCache.NoteVideoUri(audioUri, videoTrackUri);
        return audioUri;
    }

    public async Task<string?> ResolveManifestIdAsync(
        string audioTrackUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioTrackUri)) return null;
        if (_catalogCache.TryGetManifestId(audioTrackUri, out var cached)) return cached;

        if (!_catalogCache.TryGetVideoUri(audioTrackUri, out var videoTrackUri))
        {
            videoTrackUri = await TryResolveVideoUriViaExtendedMetadataAsync(audioTrackUri, cancellationToken)
                .ConfigureAwait(false) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(videoTrackUri)) return null;

        var videoTrack = await _extendedMetadataClient
            .GetTrackAudioFilesAsync(videoTrackUri, cancellationToken)
            .ConfigureAwait(false);
        if (videoTrack is null || videoTrack.OriginalVideo.Count == 0) return null;

        var bytes = videoTrack.OriginalVideo[0].Gid;
        if (bytes is null || bytes.Length == 0) return null;

        var manifestId = Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();
        _catalogCache.NoteManifestId(audioTrackUri, manifestId);
        return manifestId;
    }

    public void NoteHasVideo(string audioTrackUri, bool hasVideo)
        => _catalogCache.NoteHasVideo(audioTrackUri, hasVideo);

    public void NoteVideoUri(string audioTrackUri, string videoTrackUri)
        => _catalogCache.NoteVideoUri(audioTrackUri, videoTrackUri);

    public void NoteManifestId(string audioTrackUri, string manifestId)
        => _catalogCache.NoteManifestId(audioTrackUri, manifestId);

    private void ApplyAvailability(string audioUri, byte[]? associationBytes, Dictionary<string, bool> result)
    {
        var videoUri = MusicVideoAssociationParser.TryReadVideoAssociationUri(associationBytes);
        if (!string.IsNullOrWhiteSpace(videoUri))
        {
            result[audioUri] = true;
            _catalogCache.NoteVideoUri(audioUri, videoUri);
            return;
        }

        result[audioUri] = false;
        _catalogCache.NoteHasVideo(audioUri, false);
    }

    private async Task WriteNegativeCacheAsync(string uri, CancellationToken cancellationToken)
    {
        try
        {
            await _database
                .SetExtensionAsync(
                    uri,
                    ExtensionKind.VideoAssociations,
                    Array.Empty<byte>(),
                    etag: null,
                    NegativeCacheTtlSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to write negative video-association cache for {Uri}", uri);
        }
    }

    private static string[] NormalizeTrackUris(IEnumerable<string> trackUris)
        => trackUris
            .Where(uri => !string.IsNullOrWhiteSpace(uri)
                          && uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
