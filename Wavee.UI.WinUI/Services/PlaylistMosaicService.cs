using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Lazily composes a 2x2 album-cover mosaic for Spotify "custom" playlists that don't
/// expose a single cover image (Spotify's UI shows a generated mosaic of the first 4
/// unique album covers). Result is cached on disk so app restart skips the network +
/// composition cost.
/// </summary>
public sealed class PlaylistMosaicService
{
    // Compose at ~1.4× the largest display surface (playlist-page hero is
    // 280 logical px; 400 px source gives crisp output on Hi-DPI without
    // ballooning compose peak). Sidebar consumers decode the same PNG at
    // their smaller size via BitmapImage.DecodePixelWidth — WinUI's image
    // pipeline downsamples on demand, so one cached asset per playlist serves
    // both surfaces. Step 8 lowered this from 600 → 400 to halve the
    // CanvasRenderTarget GPU allocation and the decoded-bitmap peak.
    private const int MosaicSizePx = 400;
    // Sidebar BitmapImage decode target — 88px = 2x the 44px rendered size for
    // Hi-DPI sharpness. Applied in CreateIconFromPathAsync below; it does NOT
    // affect the on-disk PNG, only how large a decoded bitmap the sidebar
    // materialises from it.
    private const int SidebarDecodePixelWidth = 88;
    private const string CacheFolderName = "playlist-mosaics";

    private readonly ILibraryDataService _libraryDataService;
    private readonly DispatcherQueue _uiDispatcher;
    private readonly ILogger<PlaylistMosaicService>? _logger;

    // Caps simultaneous mosaic builds so a fast scroll doesn't fan out N builds at once.
    private readonly SemaphoreSlim _buildThrottle = new(initialCount: 4, maxCount: 4);

    // In-flight de-dupe: if a row scrolls out and back in mid-build, the second call
    // awaits the first task instead of re-fetching tracks.
    //
    // Lazy<Task<T>> (NOT raw Task<T>): ConcurrentDictionary.GetOrAdd can invoke the
    // factory more than once under concurrent misses; only one value wins the slot,
    // but the loser's factory already ran — with a raw Task that means the loser's
    // Task is running but never awaited. If it throws (e.g. SpClientException for
    // a 404 playlist) the exception is unobserved and surfaces via the finalizer
    // thread as an UnobservedTaskException (seen in the 2026-04-23 session log).
    // Lazy<Task> defers the async method invocation until .Value is accessed on
    // the winner, so the loser's Lazy is discarded before any Task is ever created.
    private readonly ConcurrentDictionary<string, Lazy<Task<IconSource?>>> _inFlight = new();

    // Permanent (process-lifetime) negative cache for playlists whose backing
    // resource returned NotFound. Without this, every sidebar row recycle and
    // every dealer push fires another GetPlaylistTracksAsync that re-404s for
    // dead URIs — observed in user logs as endless "Mosaic build skipped …
    // NotFound" warnings. NotFound is structural; a 404 today won't become a
    // 200 mid-session. Process restarts get a fresh chance.
    private readonly ConcurrentDictionary<string, byte> _deadPlaylistIds =
        new(StringComparer.Ordinal);

    private StorageFolder? _cacheFolder;
    private readonly SemaphoreSlim _cacheFolderInit = new(1, 1);

    public PlaylistMosaicService(
        ILibraryDataService libraryDataService,
        DispatcherQueue uiDispatcher,
        ILogger<PlaylistMosaicService>? logger = null)
    {
        _libraryDataService = libraryDataService;
        _uiDispatcher = uiDispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Builds (or loads from disk) a 2×2 mosaic icon for a playlist.
    /// </summary>
    /// <param name="playlistId">Playlist URI / ID — used as the disk-cache key.</param>
    /// <param name="mosaicHint">Optional <c>spotify:mosaic:id1:id2:id3:id4</c> URI from the
    /// playlist metadata. When supplied, the 4 tile URLs are parsed directly and the playlist
    /// track fetch is skipped (fast path). Pass <c>null</c> to derive tiles by fetching the
    /// playlist's tracks and selecting the first 4 unique album covers (slow fallback).</param>
    public Task<IconSource?> BuildMosaicAsync(string playlistId, string? mosaicHint, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(playlistId))
            return Task.FromResult<IconSource?>(null);

        // Short-circuit for known-dead playlists. See _deadPlaylistIds for context;
        // without this guard the sidebar bangs on 404 URIs every time a row is
        // realized.
        if (_deadPlaylistIds.ContainsKey(playlistId))
            return Task.FromResult<IconSource?>(null);

        // Coalesce concurrent calls for the same playlist onto a single in-flight task.
        // Lazy<Task<T>> (see _inFlight comment) defers async method invocation to
        // the winning slot only — so concurrent GetOrAdd callers can't orphan a
        // running Task whose exception would later fire as UnobservedTaskException.
        //
        // The cached build runs under CancellationToken.None — its result is per-playlist
        // and reused across surfaces, so cancelling the build because one specific caller
        // (e.g. a sidebar row that scrolled out) went away would poison the task for every
        // other waiter. Each caller still observes its own ct via WaitAsync below.
        var lazy = _inFlight.GetOrAdd(
            playlistId,
            id => new Lazy<Task<IconSource?>>(
                () => BuildAndForgetAsync(id, mosaicHint, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(ct);
    }

    /// <summary>
    /// Ensures a cached PNG exists for the playlist and returns its absolute
    /// path. Same on-disk asset the sidebar uses; WinUI's image decoder
    /// downsamples via <c>BitmapImage.DecodePixelWidth</c> at each display
    /// surface, so the playlist-page hero (~280px) and sidebar (~88px) both
    /// render sharp from a single <see cref="MosaicSizePx"/> source PNG.
    /// </summary>
    public async Task<string?> GetMosaicFilePathAsync(string playlistId, string? mosaicHint, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(playlistId))
            return null;

        try
        {
            // Piggyback on the existing build pipeline. BuildMosaicAsync writes
            // the PNG to disk before returning the IconSource, so by the time
            // the IconSource materialises the file is already there. We then
            // reconstruct the path from the same hash the builder used.
            var icon = await BuildMosaicAsync(playlistId, mosaicHint, ct).ConfigureAwait(false);
            if (icon is null)
                return null;

            var tileUrls = await ResolveTileUrlsAsync(playlistId, mosaicHint, ct).ConfigureAwait(false);
            if (tileUrls.Count == 0)
                return null;

            var folder = await GetCacheFolderAsync().ConfigureAwait(false);
            if (folder is null)
                return null;

            var fileName = BuildMosaicFileName(playlistId, ComputeHash(tileUrls));
            var path = Path.Combine(folder.Path, fileName);
            return File.Exists(path) ? path : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GetMosaicFilePathAsync failed for {PlaylistId}", playlistId);
            return null;
        }
    }

    // Bump this whenever the on-disk asset format changes in a way that
    // requires a fresh compose:
    //   v1 (removed): 88 px sidebar-only.
    //   v2 (Step 7): 600 px single-source, shared between sidebar + page.
    //   v3 (Step 8): 400 px — same topology, smaller. Existing v2 PNGs miss
    //   on the next visit and get recomposed at the new size. v2 files are
    //   left on disk (stale-sweep removed in Step 7b); they'll get swept by
    //   the optional startup pass or overwritten for playlists that refresh.
    private const string MosaicCacheVersion = "v3";
    private static string BuildMosaicFileName(string playlistId, string hash) =>
        $"{SanitizeId(playlistId)}_{hash}_{MosaicCacheVersion}.png";

    /// <summary>
    /// Invalidates a playlist's cached mosaic so the next <see cref="BuildMosaicAsync"/>
    /// call recomposes from scratch. Call when the playlist's underlying tracks
    /// change (e.g. after <see cref="PlaylistDiffApplier"/> applies a Mercury push)
    /// — without this, sidebar rows keep displaying the stale composite forever
    /// because the per-model <c>IconSource</c> is captured permanently after first
    /// load. Removes the in-flight cache entry AND best-effort sweeps stale PNGs
    /// from the disk cache so the cache folder doesn't grow over time.
    /// </summary>
    public void Invalidate(string playlistId)
    {
        if (string.IsNullOrEmpty(playlistId)) return;

        _inFlight.TryRemove(playlistId, out _);

        if (_cacheFolder is null) return;
        try
        {
            var prefix = SanitizeId(playlistId) + "_";
            var pattern = prefix + "*_" + MosaicCacheVersion + ".png";
            foreach (var path in System.IO.Directory.EnumerateFiles(_cacheFolder.Path, pattern))
            {
                try { System.IO.File.Delete(path); } catch { /* best-effort sweep */ }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Mosaic disk sweep failed for {PlaylistId}", playlistId);
        }
    }

    private async Task<IconSource?> BuildAndForgetAsync(string playlistId, string? mosaicHint, CancellationToken ct)
    {
        try
        {
            return await BuildCoreAsync(playlistId, mosaicHint, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled tasks are evicted so a future realization can retry from scratch.
            throw;
        }
        catch (Wavee.Core.Http.SpClientException ex)
        {
            // Playlist returned a real error (404 for pre-release / region-locked
            // / deleted URIs, 401 for expired auth). Log + swallow so the caller
            // in SidebarItem sees "no mosaic" instead of a retry-worthy exception
            // — retry won't help and the outer 3-attempt retry loop would just
            // re-hit the negative cache. Returning null also ensures the Task
            // completes normally, so no UnobservedTaskException can fire even if
            // some caller forgets to await.
            _logger?.LogDebug(ex, "Mosaic build skipped for {PlaylistId}: {Reason}", playlistId, ex.Reason);
            // Latch known-NotFound so future BuildMosaicAsync calls short-circuit.
            // 4xx other than NotFound (e.g. 401 from expired auth) might recover
            // mid-session, so we only persist-cache the structural NotFound case.
            if (ex.Reason == Wavee.Core.Http.SpClientFailureReason.NotFound)
                _deadPlaylistIds.TryAdd(playlistId, 0);
            return null;
        }
        catch (Exception ex)
        {
            // Any other failure (network, GPU, etc.) — same story. Don't leak as
            // unobserved and don't spam retries.
            _logger?.LogDebug(ex, "Mosaic build failed for {PlaylistId}", playlistId);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(playlistId, out _);
        }
    }

    private async Task<IconSource?> BuildCoreAsync(string playlistId, string? mosaicHint, CancellationToken ct)
    {
        var tileUrls = await ResolveTileUrlsAsync(playlistId, mosaicHint, ct).ConfigureAwait(false);
        if (tileUrls.Count == 0)
            return null;

        var hash = ComputeHash(tileUrls);
        var fileName = BuildMosaicFileName(playlistId, hash);

        await _buildThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            var folder = await GetCacheFolderAsync().ConfigureAwait(false);
            if (folder != null)
            {
                var cachedPath = Path.Combine(folder.Path, fileName);
                if (File.Exists(cachedPath))
                    return await CreateIconFromPathAsync(cachedPath, ct).ConfigureAwait(false);
            }

            var pngBytes = await ComposeMosaicPngAsync(tileUrls, ct).ConfigureAwait(false);
            if (pngBytes == null)
                return null;

            if (folder != null)
            {
                // Await the write so the file exists before we hand its path to BitmapImage.
                // UriSource-based decode is strictly off-thread (WinUI's image pipeline
                // handles it) — vastly cheaper than SetSourceAsync-from-stream, which has
                // a synchronous init portion that runs on the dispatcher and backs the
                // queue up under rapid sidebar scroll.
                var composedPath = Path.Combine(folder.Path, fileName);
                await WriteToDiskAsync(folder, fileName, pngBytes, SanitizeId(playlistId)).ConfigureAwait(false);
                return await CreateIconFromPathAsync(composedPath, ct).ConfigureAwait(false);
            }

            // No cache folder available (shouldn't happen in prod) — fall back to the
            // in-memory stream path so the mosaic still renders this session.
            var memStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(memStream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            memStream.Seek(0);
            return await CreateIconFromStreamAsync(memStream, ct).ConfigureAwait(false);
        }
        finally
        {
            _buildThrottle.Release();
        }
    }

    /// <summary>
    /// Fast path when the playlist's metadata already exposes a <c>spotify:mosaic:</c> URI:
    /// parse the 4 image ids directly. Otherwise fall back to fetching the playlist's tracks
    /// and selecting the first 4 unique album covers.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveTileUrlsAsync(string playlistId, string? mosaicHint, CancellationToken ct)
    {
        if (SpotifyImageHelper.TryParseMosaicTileUrls(mosaicHint, out var hinted))
            return hinted;

        var tracks = await _libraryDataService.GetPlaylistTracksAsync(playlistId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var urls = new List<string>(4);
        var seenAlbums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (urls.Count == 4) break;
            if (string.IsNullOrEmpty(track.AlbumId)) continue;
            if (!seenAlbums.Add(track.AlbumId)) continue;

            var url = SpotifyImageHelper.ToHttpsUrl(track.ImageUrl);
            if (string.IsNullOrEmpty(url)) continue;

            urls.Add(url);
        }
        return urls;
    }

    private async Task<byte[]?> ComposeMosaicPngAsync(IReadOnlyList<string> tileUrls, CancellationToken ct)
    {
        var device = CanvasDevice.GetSharedDevice();
        const int sizePx = MosaicSizePx;
        const int tileSizePx = sizePx / 2;

        // Load tiles in parallel. Skip any that fail rather than aborting the whole mosaic.
        var loadTasks = tileUrls
            .Select(url => LoadCanvasBitmapSafeAsync(device, url, ct))
            .ToArray();
        var loaded = await Task.WhenAll(loadTasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var available = loaded.Where(b => b != null).Cast<CanvasBitmap>().ToArray();
        if (available.Length == 0)
        {
            foreach (var b in loaded) b?.Dispose();
            return null;
        }

        try
        {
            using var renderTarget = new CanvasRenderTarget(device, sizePx, sizePx, 96f);
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);

                if (available.Length >= 4)
                {
                    // Full 2×2 mosaic — 4 unique covers in row-major order.
                    for (int i = 0; i < 4; i++)
                    {
                        var tile = available[i];
                        var destX = (i % 2) * tileSizePx;
                        var destY = (i / 2) * tileSizePx;
                        var destRect = new Windows.Foundation.Rect(destX, destY, tileSizePx, tileSizePx);
                        var srcRect = CenterSquareSourceRect(tile);
                        ds.DrawImage(tile, destRect, srcRect);
                    }
                }
                else
                {
                    // Fewer than 4 unique albums — draw the first cover at full
                    // size instead of tiling duplicates (previously we cycled
                    // `available[i % available.Length]` which rendered the same
                    // cover in multiple quadrants). Matches the playlist-page
                    // hero fallback ("1 playlist = 1 cover art" when there
                    // isn't enough variety to justify a mosaic).
                    var tile = available[0];
                    var destRect = new Windows.Foundation.Rect(0, 0, sizePx, sizePx);
                    var srcRect = CenterSquareSourceRect(tile);
                    ds.DrawImage(tile, destRect, srcRect);
                }
            }

            using var stream = new InMemoryRandomAccessStream();
            await renderTarget.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size).AsTask(ct).ConfigureAwait(false);
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            foreach (var bitmap in available) bitmap.Dispose();
        }
    }

    private static Windows.Foundation.Rect CenterSquareSourceRect(CanvasBitmap bitmap)
    {
        var w = bitmap.SizeInPixels.Width;
        var h = bitmap.SizeInPixels.Height;
        var side = Math.Min(w, h);
        var x = (w - side) / 2.0;
        var y = (h - side) / 2.0;
        return new Windows.Foundation.Rect(x, y, side, side);
    }

    private async Task<CanvasBitmap?> LoadCanvasBitmapSafeAsync(CanvasDevice device, string url, CancellationToken ct)
    {
        // RPC_S_SERVER_UNAVAILABLE (0x80010012): the shared CanvasDevice's
        // underlying D3D device is transiently unavailable — typically during
        // a GPU reset or driver timeout. GetSharedDevice returns a self-healing
        // proxy, so re-acquiring after a short delay and retrying once is
        // usually enough to recover without a permanent device-lost handler.
        const int DeviceUnavailableHResult = unchecked((int)0x80010012);

        try
        {
            return await CanvasBitmap.LoadAsync(device, new Uri(url)).AsTask(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.HResult == DeviceUnavailableHResult)
        {
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                var freshDevice = CanvasDevice.GetSharedDevice();
                return await CanvasBitmap.LoadAsync(freshDevice, new Uri(url)).AsTask(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception retryEx)
            {
                _logger?.LogDebug(retryEx, "Mosaic tile failed to load after device-unavailable retry: {Url}", url);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Mosaic tile failed to load: {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Preferred loader: dispatch a single O(1) callback to the UI thread that
    /// assigns <see cref="BitmapImage.UriSource"/>. WinUI's image pipeline handles
    /// the actual PNG decode on its internal thread — no PNG work runs on the
    /// dispatcher. Compared with <see cref="CreateIconFromStreamAsync"/>
    /// (which does a synchronous stream-init before its first await on the UI
    /// thread), this keeps the sidebar responsive when many items realize at once.
    /// </summary>
    private Task<IconSource?> CreateIconFromPathAsync(string absolutePath, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IconSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var posted = _uiDispatcher.TryEnqueue(() =>
        {
            try
            {
                // Sidebar decodes the shared large PNG down to 88px. The image
                // on disk is larger (MosaicSizePx); WinUI's decoder downsamples
                // on demand so both the sidebar and the playlist-page hero
                // reuse the same cached asset.
                var image = new BitmapImage { DecodePixelWidth = SidebarDecodePixelWidth };
                image.UriSource = new Uri(absolutePath);
                tcs.TrySetResult(new ImageIconSource { ImageSource = image });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!posted)
            tcs.TrySetResult(null);

        return tcs.Task;
    }

    private async Task<IconSource?> CreateIconFromStreamAsync(IRandomAccessStream stream, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IconSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var posted = _uiDispatcher.TryEnqueue(async () =>
        {
            try
            {
                stream.Seek(0);
                var image = new BitmapImage { DecodePixelWidth = SidebarDecodePixelWidth };
                await image.SetSourceAsync(stream);
                tcs.TrySetResult(new ImageIconSource { ImageSource = image });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                stream.Dispose();
            }
        });

        if (!posted)
        {
            stream.Dispose();
            tcs.TrySetResult(null);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task WriteToDiskAsync(StorageFolder folder, string fileName, byte[] pngBytes, string playlistIdSafe)
    {
        // Use System.IO rather than WinRT StorageFolder/FileIO: WinRT objects are
        // apartment-bound and this runs from arbitrary ThreadPool workers during
        // mosaic composition. System.IO has no apartment affinity. Cached reads
        // go through File.Exists + BitmapImage.UriSource in BuildCoreAsync for
        // the same reason.
        try
        {
            var folderPath = folder.Path;
            var targetPath = Path.Combine(folderPath, fileName);
            await using (var fs = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await fs.WriteAsync(pngBytes).ConfigureAwait(false);
            }

            // Intentionally NO stale-sweep. Previously this deleted all files
            // matching {playlistIdSafe}_* that weren't the exact new filename —
            // meant to clean up "old hash" PNGs when the playlist's first 4
            // albums changed. Problem: the UI's BitmapImage may still be
            // decoding from the old path when the delete lands, causing the
            // sidebar to flash back to the placeholder. Playlist covers are a
            // high-hit cache (the user sees them every session), so pinning
            // them on disk is worth a bit of extra storage — few hundred PNGs
            // × <100 KB = under 30 MB total even for a large library. If disk
            // ever becomes an issue, a targeted sweep at app startup that
            // removes files whose playlist id is no longer in the rootlist is
            // safer than a write-time sweep that races the image pipeline.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Mosaic disk write failed for {File}", fileName);
        }
    }

    private async Task<StorageFolder?> GetCacheFolderAsync()
    {
        if (_cacheFolder != null) return _cacheFolder;

        await _cacheFolderInit.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheFolder != null) return _cacheFolder;
            _cacheFolder = await ApplicationData.Current.LocalCacheFolder
                .CreateFolderAsync(CacheFolderName, CreationCollisionOption.OpenIfExists);
            return _cacheFolder;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Mosaic cache folder init failed");
            return null;
        }
        finally
        {
            _cacheFolderInit.Release();
        }
    }

    private static string ComputeHash(IReadOnlyList<string> urls)
    {
        // Hash the ordered tile URLs (which encode album identity for our purposes).
        // 12 hex chars = 48 bits — more than enough collision resistance for a per-playlist key.
        var joined = string.Join("|", urls);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        var sb = new StringBuilder(12);
        for (int i = 0; i < 6; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static string SanitizeId(string playlistId)
    {
        // Spotify playlist IDs are base62 (URL-safe), but be defensive against any colon-prefixed
        // forms that might leak in (e.g. "spotify:playlist:xyz") so we don't end up with paths
        // containing reserved characters.
        var span = playlistId.AsSpan();
        var lastColon = playlistId.LastIndexOf(':');
        if (lastColon >= 0) span = span[(lastColon + 1)..];

        var sb = new StringBuilder(span.Length);
        foreach (var c in span)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }
}
