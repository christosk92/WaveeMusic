using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;

namespace Wavee.UI.Services.Tracks;

/// <summary>
/// Resolves preview URLs from the <c>Track</c> protobuf <c>preview</c> field
/// (<c>repeated AudioFile</c>, field 15) → <c>https://p.scdn.co/mp3-preview/{file_id}</c>.
/// <para>
/// Reuses the app's caches: track metadata comes from
/// <see cref="IExtendedMetadataClient.GetTrackAsync"/> (the 1-hour SQLite-cached TrackV4 tier the
/// playlist deck already populated when it loaded), so a deck track's preview resolves with no extra
/// network call. Resolved URLs are also memoised in-process by URI (null = "no preview, don't
/// refetch"); <see cref="PrefetchAsync"/> warms the next few cards with bounded concurrency.
/// </para>
/// </summary>
public sealed class PreviewUrlResolver : IPreviewUrlResolver
{
    private readonly IExtendedMetadataClient _metadata;
    private readonly ILogger<PreviewUrlResolver>? _logger;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(4);

    public PreviewUrlResolver(IExtendedMetadataClient metadata, ILogger<PreviewUrlResolver>? logger = null)
    {
        _metadata = metadata;
        _logger = logger;
    }

    public async Task<string?> ResolveAsync(string trackUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri)) return null;
        if (_cache.TryGetValue(trackUri, out var cached)) return cached;

        string? url = null;
        try
        {
            // SQLite-cached (1h) TrackV4 — already warmed for deck tracks by the playlist load.
            var track = await _metadata.GetTrackAsync(trackUri, ct).ConfigureAwait(false);
            var preview = track?.Preview.FirstOrDefault(f => f.FileId is { Length: > 0 });
            if (preview is not null)
                url = PreviewUrl.Build(preview.FileId.Span);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Preview resolve failed for {Uri}", trackUri);
        }

        _cache[trackUri] = url;     // cache null too — a missing preview won't refetch
        return url;
    }

    public async Task PrefetchAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        var tasks = trackUris
            .Where(u => !string.IsNullOrEmpty(u) && !_cache.ContainsKey(u))
            .Select(async u =>
            {
                try
                {
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try { await ResolveAsync(u, ct).ConfigureAwait(false); }
                    finally { _gate.Release(); }
                }
                catch { /* never throw from prefetch */ }
            });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
