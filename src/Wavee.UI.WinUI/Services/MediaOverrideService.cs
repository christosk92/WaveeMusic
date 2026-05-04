using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Helpers.Application;

namespace Wavee.UI.WinUI.Services;

public interface IMediaOverrideService
{
    Task<ResolvedMediaOverrideResult> ResolveTrackCanvasAsync(
        string trackUri,
        string? upstreamCanvasUrl,
        CancellationToken ct = default);

    Task<ResolvedMediaOverrideResult> AcceptPendingTrackCanvasUpdateAsync(
        string trackUri,
        CancellationToken ct = default);

    Task<ResolvedMediaOverrideResult> RejectPendingTrackCanvasUpdateAsync(
        string trackUri,
        CancellationToken ct = default);

    Task<ResolvedMediaOverrideResult> SetManualTrackCanvasUrlAsync(
        string trackUri,
        string assetUrl,
        CancellationToken ct = default);

    Task<ResolvedMediaOverrideResult> ImportManualTrackCanvasFileAsync(
        string trackUri,
        string sourceFilePath,
        CancellationToken ct = default);

    Task<ResolvedMediaOverrideResult> ResetTrackCanvasToUpstreamAsync(
        string trackUri,
        CancellationToken ct = default);
}

public sealed record ResolvedMediaOverrideResult
{
    public string? EffectiveAssetUrl { get; init; }
    public string? UpstreamAssetUrl { get; init; }
    public string? PendingAssetUrl { get; init; }
    public bool HasPendingUpdate { get; init; }
    public bool IsUsingLocalSnapshot { get; init; }
    public bool IsManualOverride { get; init; }
}

public sealed class MediaOverrideService : IMediaOverrideService
{
    private static readonly MediaOverrideAssetType CanvasAssetType = MediaOverrideAssetType.DetailsCanvas;
    private static readonly string ManagedCanvasRoot = Path.Combine(AppPaths.AppDataDirectory, "media", "canvas");
    private static readonly string[] SupportedVideoExtensions = [".mp4", ".webm", ".mov", ".m4v"];

    private readonly IMetadataDatabase _db;
    private readonly ILogger? _logger;

    public MediaOverrideService(
        IMetadataDatabase db,
        ILogger<MediaOverrideService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ResolvedMediaOverrideResult> ResolveTrackCanvasAsync(
        string trackUri,
        string? upstreamCanvasUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
            return BuildResult(null, NormalizeUrl(upstreamCanvasUrl), null, MediaOverrideSource.None);

        var normalizedUpstream = NormalizeUrl(upstreamCanvasUrl);
        var entry = await _db.GetMediaOverrideAsync(CanvasAssetType, trackUri, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (entry == null)
        {
            if (string.IsNullOrWhiteSpace(normalizedUpstream))
                return BuildResult(null, null, null, MediaOverrideSource.None);

            entry = new MediaOverrideEntry
            {
                AssetType = CanvasAssetType,
                EntityKey = trackUri,
                EffectiveAssetUrl = normalizedUpstream,
                EffectiveSource = MediaOverrideSource.UpstreamSnapshot,
                LastSeenUpstreamUrl = normalizedUpstream,
                PendingAssetUrl = null,
                LastReviewedUpstreamUrl = normalizedUpstream,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _db.SetMediaOverrideAsync(entry, ct).ConfigureAwait(false);
            return BuildResult(entry.EffectiveAssetUrl, normalizedUpstream, null, entry.EffectiveSource);
        }

        var updated = entry with
        {
            LastSeenUpstreamUrl = normalizedUpstream,
            UpdatedAt = now,
        };

        var mutated = !UrlEquals(entry.LastSeenUpstreamUrl, normalizedUpstream);

        if (string.IsNullOrWhiteSpace(updated.EffectiveAssetUrl) && !string.IsNullOrWhiteSpace(normalizedUpstream))
        {
            updated = updated with
            {
                EffectiveAssetUrl = normalizedUpstream,
                EffectiveSource = MediaOverrideSource.UpstreamSnapshot,
            };
            mutated = true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedUpstream)
            && !UrlEquals(normalizedUpstream, updated.LastReviewedUpstreamUrl))
        {
            if (updated.EffectiveSource != MediaOverrideSource.ManualOverride)
            {
                updated = updated with
                {
                    EffectiveAssetUrl = normalizedUpstream,
                    EffectiveSource = MediaOverrideSource.UpstreamSnapshot,
                    LastReviewedUpstreamUrl = normalizedUpstream,
                    PendingAssetUrl = null,
                };
                mutated = true;
            }
            else if (!UrlEquals(updated.PendingAssetUrl, normalizedUpstream))
            {
                updated = updated with { PendingAssetUrl = normalizedUpstream };
                mutated = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(updated.PendingAssetUrl))
        {
            updated = updated with { PendingAssetUrl = null };
            mutated = true;
        }

        if (mutated)
            await _db.SetMediaOverrideAsync(updated, ct).ConfigureAwait(false);

        return BuildResult(
            updated.EffectiveAssetUrl,
            normalizedUpstream,
            updated.PendingAssetUrl,
            updated.EffectiveSource);
    }

    public async Task<ResolvedMediaOverrideResult> AcceptPendingTrackCanvasUpdateAsync(
        string trackUri,
        CancellationToken ct = default)
    {
        var entry = await _db.GetMediaOverrideAsync(CanvasAssetType, trackUri, ct).ConfigureAwait(false);
        if (entry == null)
            return BuildResult(null, null, null, MediaOverrideSource.None);

        var pending = NormalizeUrl(entry.PendingAssetUrl);
        if (string.IsNullOrWhiteSpace(pending))
            return BuildResult(entry.EffectiveAssetUrl, entry.LastSeenUpstreamUrl, null, entry.EffectiveSource);

        var updated = entry with
        {
            EffectiveAssetUrl = pending,
            EffectiveSource = MediaOverrideSource.UpstreamSnapshot,
            LastSeenUpstreamUrl = pending,
            LastReviewedUpstreamUrl = pending,
            PendingAssetUrl = null,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        await CleanupManagedCanvasIfOwnedAsync(entry.EffectiveAssetUrl, ct).ConfigureAwait(false);
        await _db.SetMediaOverrideAsync(updated, ct).ConfigureAwait(false);
        _logger?.LogDebug("Accepted pending canvas override for {TrackUri}", trackUri);

        return BuildResult(updated.EffectiveAssetUrl, updated.LastSeenUpstreamUrl, null, updated.EffectiveSource);
    }

    public async Task<ResolvedMediaOverrideResult> RejectPendingTrackCanvasUpdateAsync(
        string trackUri,
        CancellationToken ct = default)
    {
        var entry = await _db.GetMediaOverrideAsync(CanvasAssetType, trackUri, ct).ConfigureAwait(false);
        if (entry == null)
            return BuildResult(null, null, null, MediaOverrideSource.None);

        var pending = NormalizeUrl(entry.PendingAssetUrl);
        if (string.IsNullOrWhiteSpace(pending))
            return BuildResult(entry.EffectiveAssetUrl, entry.LastSeenUpstreamUrl, null, entry.EffectiveSource);

        var updated = entry with
        {
            LastReviewedUpstreamUrl = pending,
            EffectiveSource = MediaOverrideSource.ManualOverride,
            PendingAssetUrl = null,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        await _db.SetMediaOverrideAsync(updated, ct).ConfigureAwait(false);
        _logger?.LogDebug("Rejected pending canvas override for {TrackUri}", trackUri);

        return BuildResult(updated.EffectiveAssetUrl, updated.LastSeenUpstreamUrl, null, updated.EffectiveSource);
    }

    public async Task<ResolvedMediaOverrideResult> SetManualTrackCanvasUrlAsync(
        string trackUri,
        string assetUrl,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);
        var normalizedAssetUrl = NormalizeAbsoluteUrl(assetUrl);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = await _db.GetMediaOverrideAsync(CanvasAssetType, trackUri, ct).ConfigureAwait(false);

        if (entry != null
            && entry.EffectiveSource == MediaOverrideSource.ManualOverride
            && UrlEquals(entry.EffectiveAssetUrl, normalizedAssetUrl))
        {
            return BuildResult(entry.EffectiveAssetUrl, entry.LastSeenUpstreamUrl, entry.PendingAssetUrl, entry.EffectiveSource);
        }

        if (entry != null)
            await CleanupManagedCanvasIfOwnedAsync(entry.EffectiveAssetUrl, ct).ConfigureAwait(false);

        var updated = new MediaOverrideEntry
        {
            AssetType = CanvasAssetType,
            EntityKey = trackUri,
            EffectiveAssetUrl = normalizedAssetUrl,
            EffectiveSource = MediaOverrideSource.ManualOverride,
            LastSeenUpstreamUrl = entry?.LastSeenUpstreamUrl,
            PendingAssetUrl = entry?.PendingAssetUrl,
            LastReviewedUpstreamUrl = entry?.LastReviewedUpstreamUrl,
            CreatedAt = entry?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        await _db.SetMediaOverrideAsync(updated, ct).ConfigureAwait(false);
        _logger?.LogDebug("Set manual canvas URL override for {TrackUri}", trackUri);

        return BuildResult(updated.EffectiveAssetUrl, updated.LastSeenUpstreamUrl, updated.PendingAssetUrl, updated.EffectiveSource);
    }

    public async Task<ResolvedMediaOverrideResult> ImportManualTrackCanvasFileAsync(
        string trackUri,
        string sourceFilePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = await _db.GetMediaOverrideAsync(CanvasAssetType, trackUri, ct).ConfigureAwait(false);

        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Canvas file not found.", sourceFilePath);

        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (Array.IndexOf(SupportedVideoExtensions, extension) < 0)
            throw new InvalidOperationException("Unsupported canvas file type.");

        Directory.CreateDirectory(ManagedCanvasRoot);

        var targetDirectory = GetManagedCanvasDirectory(trackUri);
        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, recursive: true);
        Directory.CreateDirectory(targetDirectory);

        var targetPath = Path.Combine(targetDirectory, $"manual{extension}");
        await using (var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await sourceStream.CopyToAsync(targetStream, ct).ConfigureAwait(false);
        }

        var updated = new MediaOverrideEntry
        {
            AssetType = CanvasAssetType,
            EntityKey = trackUri,
            EffectiveAssetUrl = new Uri(targetPath).AbsoluteUri,
            EffectiveSource = MediaOverrideSource.ManualOverride,
            LastSeenUpstreamUrl = entry?.LastSeenUpstreamUrl,
            PendingAssetUrl = entry?.PendingAssetUrl,
            LastReviewedUpstreamUrl = entry?.LastReviewedUpstreamUrl,
            CreatedAt = entry?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        await _db.SetMediaOverrideAsync(updated, ct).ConfigureAwait(false);
        _logger?.LogDebug("Imported manual canvas file for {TrackUri}", trackUri);

        return BuildResult(updated.EffectiveAssetUrl, updated.LastSeenUpstreamUrl, updated.PendingAssetUrl, updated.EffectiveSource);
    }

    public async Task<ResolvedMediaOverrideResult> ResetTrackCanvasToUpstreamAsync(
        string trackUri,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);
        var entry = await _db.GetMediaOverrideAsync(CanvasAssetType, trackUri, ct).ConfigureAwait(false);
        if (entry == null)
            return BuildResult(null, null, null, MediaOverrideSource.None);

        await CleanupManagedCanvasIfOwnedAsync(entry.EffectiveAssetUrl, ct).ConfigureAwait(false);

        var upstream = NormalizeUrl(entry.LastSeenUpstreamUrl);
        var updated = entry with
        {
            EffectiveAssetUrl = upstream,
            EffectiveSource = string.IsNullOrWhiteSpace(upstream)
                ? MediaOverrideSource.None
                : MediaOverrideSource.UpstreamSnapshot,
            LastReviewedUpstreamUrl = upstream,
            PendingAssetUrl = null,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        await _db.SetMediaOverrideAsync(updated, ct).ConfigureAwait(false);
        _logger?.LogDebug("Reset canvas override to upstream for {TrackUri}", trackUri);

        return BuildResult(updated.EffectiveAssetUrl, updated.LastSeenUpstreamUrl, updated.PendingAssetUrl, updated.EffectiveSource);
    }

    private static ResolvedMediaOverrideResult BuildResult(
        string? effectiveAssetUrl,
        string? upstreamAssetUrl,
        string? pendingAssetUrl,
        MediaOverrideSource source)
    {
        return new ResolvedMediaOverrideResult
        {
            EffectiveAssetUrl = NormalizeUrl(effectiveAssetUrl),
            UpstreamAssetUrl = NormalizeUrl(upstreamAssetUrl),
            PendingAssetUrl = NormalizeUrl(pendingAssetUrl),
            HasPendingUpdate = !string.IsNullOrWhiteSpace(pendingAssetUrl),
            IsUsingLocalSnapshot = source == MediaOverrideSource.ManualOverride
                                  || !UrlEquals(effectiveAssetUrl, upstreamAssetUrl),
            IsManualOverride = source == MediaOverrideSource.ManualOverride,
        };
    }

    private static string NormalizeAbsoluteUrl(string url)
    {
        var normalized = NormalizeUrl(url)
            ?? throw new InvalidOperationException("Canvas URL cannot be empty.");

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Canvas URL must be absolute.");

        if (uri.Scheme != Uri.UriSchemeHttp
            && uri.Scheme != Uri.UriSchemeHttps
            && uri.Scheme != Uri.UriSchemeFile)
        {
            throw new InvalidOperationException("Canvas URL must use http, https, or file.");
        }

        return uri.AbsoluteUri;
    }

    private static string GetManagedCanvasDirectory(string trackUri)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(trackUri));
        return Path.Combine(ManagedCanvasRoot, Convert.ToHexString(bytes));
    }

    private static string? TryGetManagedCanvasPath(string? url)
    {
        var normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || !uri.IsFile)
            return null;

        var localPath = Path.GetFullPath(uri.LocalPath);
        var rootPath = Path.GetFullPath(ManagedCanvasRoot);
        return localPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
            ? localPath
            : null;
    }

    private static Task CleanupManagedCanvasIfOwnedAsync(string? url, CancellationToken ct)
    {
        var localPath = TryGetManagedCanvasPath(url);
        if (string.IsNullOrWhiteSpace(localPath))
            return Task.CompletedTask;

        var directory = Path.GetDirectoryName(localPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Task.CompletedTask;

        ct.ThrowIfCancellationRequested();
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static bool UrlEquals(string? left, string? right)
        => string.Equals(NormalizeUrl(left), NormalizeUrl(right), StringComparison.Ordinal);

    private static string? NormalizeUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
