using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Stores;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Viewport-driven playlist metadata prefetch — the playlist analogue of
/// <see cref="AlbumPrefetcher"/>. Card surfaces (ContentCard, PopularReleaseRow,
/// SpotlightReleaseCard, search hero / row cards) call
/// <see cref="EnqueuePlaylistPrefetch"/> when a playlist URI scrolls into reach;
/// the prefetcher batches enqueued URIs (100 ms debounce), requests
/// <c>LIST_METADATA_V2</c> extended-metadata in a single POST via
/// <see cref="ExtendedMetadataStore"/>, parses the resulting
/// <see cref="ListMetadataV2"/> protobuf into a partial
/// <see cref="PlaylistDetailDto"/> (name, description, cover, header banner,
/// primary color), and seeds <see cref="PlaylistStore"/> via
/// <see cref="PlaylistStore.HintPartial"/>. As a side effect it kicks the WinUI
/// BitmapImage cache for the cover + header URLs so the hero paints with
/// already-decoded bytes when <c>PlaylistPage</c> activates.
/// </summary>
public interface IPlaylistMetadataPrefetcher
{
    /// <summary>
    /// Enqueue a playlist URI for viewport-driven prefetch. No-ops for URIs
    /// that don't look like a playlist (only <c>spotify:playlist:…</c> is
    /// accepted) and for URIs that have already been prefetched this session.
    /// Returns immediately; the fetch happens on a background batching tick.
    /// </summary>
    void EnqueuePlaylistPrefetch(string? playlistUri);
}

public sealed class PlaylistMetadataPrefetcher : IPlaylistMetadataPrefetcher, IDisposable
{
    private const string PlaylistUriPrefix = "spotify:playlist:";
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(100);

    private readonly ExtendedMetadataStore _metadataStore;
    private readonly PlaylistStore _playlistStore;
    private readonly ILogger? _logger;

    // Single-fire guard: once a URI's prefetch has been kicked off this session,
    // re-enqueues are dropped. ExtendedMetadataClient's SQLite cache handles
    // longer-term dedup across restarts.
    private readonly ConcurrentDictionary<string, byte> _alreadyKicked = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private List<string>? _pending;
    private Task? _flushTask;
    private CancellationTokenSource? _disposeCts = new();

    public PlaylistMetadataPrefetcher(
        ExtendedMetadataStore metadataStore,
        PlaylistStore playlistStore,
        ILogger<PlaylistMetadataPrefetcher>? logger = null)
    {
        _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        _playlistStore = playlistStore ?? throw new ArgumentNullException(nameof(playlistStore));
        _logger = logger;
    }

    public void EnqueuePlaylistPrefetch(string? playlistUri)
    {
        if (_disposeCts is null) return;
        if (string.IsNullOrEmpty(playlistUri)) return;
        if (!playlistUri.StartsWith(PlaylistUriPrefix, StringComparison.Ordinal)) return;
        if (!_alreadyKicked.TryAdd(playlistUri, 0)) return;

        bool scheduleFlush;
        lock (_gate)
        {
            _pending ??= new List<string>();
            _pending.Add(playlistUri);
            scheduleFlush = _flushTask is null;
        }

        if (scheduleFlush)
        {
            var cts = _disposeCts;
            if (cts is null) return;
            var task = Task.Delay(FlushDelay, cts.Token)
                .ContinueWith(_ => FlushAsync(cts.Token), TaskScheduler.Default)
                .Unwrap();
            lock (_gate) _flushTask = task;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        List<string>? batch;
        lock (_gate)
        {
            batch = _pending;
            _pending = null;
            _flushTask = null;
        }
        if (batch is null || batch.Count == 0) return;
        if (ct.IsCancellationRequested) return;

        try
        {
            var requests = batch.Select(uri =>
                (uri, (IEnumerable<Wavee.Protocol.ExtendedMetadata.ExtensionKind>)
                    new[] { Wavee.Protocol.ExtendedMetadata.ExtensionKind.ListMetadataV2 }));
            var results = await _metadataStore.GetManyAsync(requests, ct).ConfigureAwait(false);

            foreach (var kv in results)
            {
                var uri = kv.Key.Uri;
                var bytes = kv.Value;
                if (bytes is null or { Length: 0 }) continue;

                try
                {
                    var meta = ListMetadataV2.Parser.ParseFrom(bytes);
                    var partial = PartialFromListMetadataV2(uri, meta);
                    _playlistStore.HintPartial(uri, partial);
                    WarmImageCache(partial.ImageUrl, partial.HeaderImageUrl);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "PlaylistMetadataPrefetcher: failed to parse LIST_METADATA_V2 for {Uri}", uri);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-flush — silent.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PlaylistMetadataPrefetcher: batch flush failed (size={Size})", batch.Count);
        }
    }

    // ── ListMetadataV2 protobuf → partial PlaylistDetailDto ─────────────────

    /// <summary>
    /// Project a decoded <see cref="ListMetadataV2"/> into a partial
    /// <see cref="PlaylistDetailDto"/> suitable for seeding
    /// <c>PlaylistStore.HintPartial</c>. Shared between this prefetcher's
    /// viewport-driven flush and <c>AlbumService.GetRecommendedPlaylistsAsync</c>
    /// (which hands the resulting partials to the AlbumPage rail and seeds the
    /// store via the prefetcher in one motion).
    /// </summary>
    internal static PlaylistDetailDto PartialFromListMetadataV2(string uri, ListMetadataV2 meta)
    {
        // PlaylistStore is keyed by the same value PlaylistViewModel.Activate
        // receives — the full URI (`spotify:playlist:…`). Set DTO.Id to match
        // so any code path that compares `dto.Id` against the observed key
        // doesn't see a mismatch when the partial is later replaced by the
        // canonical Pathfinder fetch.
        var id = uri;

        // Cover URL: prefer the "default" variant (300 px square — what the page
        // hero binds to today). Fall back to "large", then any present URL.
        string? coverUrl = null;
        if (meta.Images?.Variant != null && meta.Images.Variant.Count > 0)
        {
            coverUrl = meta.Images.Variant.FirstOrDefault(v => v.Format == "default")?.Url
                    ?? meta.Images.Variant.FirstOrDefault(v => v.Format == "large")?.Url
                    ?? meta.Images.Variant.FirstOrDefault(v => !string.IsNullOrEmpty(v.Url))?.Url;
        }

        string? headerUrl = null;
        string? primaryColor = null;
        if (meta.Attributes?.Attribute != null)
        {
            foreach (var attr in meta.Attributes.Attribute)
            {
                if (attr.Key == "header_image_url_desktop")
                    headerUrl = attr.Value;
                else if (attr.Key == "primary_color")
                    primaryColor = attr.Value;
            }
        }

        // PlaylistViewModel.ApplyDetail preserves prior values when the incoming
        // field is empty/whitespace, so leaving OwnerName empty is safe — the
        // canonical Pathfinder fetch fills it in. IsOwner is always false for
        // editorial playlists; user-owned playlists would briefly render the
        // "not yours" affordance for the millis before the full fetch lands —
        // acceptable trade-off for the instant-hero win.
        return new PlaylistDetailDto
        {
            Id = id,
            Name = meta.Name ?? string.Empty,
            Description = string.IsNullOrEmpty(meta.Description) ? null : meta.Description,
            ImageUrl = coverUrl,
            HeaderImageUrl = headerUrl,
            OwnerName = string.Empty,
            OwnerId = null,
            TrackCount = 0,
            FollowerCount = 0,
            IsOwner = false,
            IsCollaborative = false,
            IsPublic = true,
            IsPartial = true,
            // primaryColor is captured but PlaylistDetailDto has no slot for it
            // today — when the page palette pipeline grows a hex hint field,
            // wire it through here. For now the BitmapImage warm-up below is
            // the side benefit that matters.
        };
    }

    private void WarmImageCache(string? coverUrl, string? headerUrl)
    {
        // BitmapImage decode triggers WinUI's image cache to fetch + cache the
        // bytes. Has to run on the UI thread because BitmapImage is a XAML type
        // and the constructor / UriSource setter both touch DispatcherQueue.
        // Fire-and-forget — discard the BitmapImage; the cache is keyed by URI.
        var dq = MainWindow.Instance?.DispatcherQueue;
        if (dq is null) return;

        dq.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(coverUrl) && Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri))
                    _ = new BitmapImage(coverUri);
                if (!string.IsNullOrEmpty(headerUrl) && Uri.TryCreate(headerUrl, UriKind.Absolute, out var headerUri))
                    _ = new BitmapImage(headerUri);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "PlaylistMetadataPrefetcher: image cache warm-up failed");
            }
        });
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _disposeCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* already cancelled */ }
        cts.Dispose();
    }
}
