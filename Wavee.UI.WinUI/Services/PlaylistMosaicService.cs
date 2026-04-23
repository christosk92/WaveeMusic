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
    // 88px = 2x the existing 44px sidebar icon for Hi-DPI; matches DecodePixelWidth=44
    // used today in ShellViewModel.CreatePlaylistIconSource.
    private const int MosaicSizePx = 88;
    private const int TileSizePx = MosaicSizePx / 2;
    private const string CacheFolderName = "playlist-mosaics";

    private readonly ILibraryDataService _libraryDataService;
    private readonly DispatcherQueue _uiDispatcher;
    private readonly ILogger<PlaylistMosaicService>? _logger;

    // Caps simultaneous mosaic builds so a fast scroll doesn't fan out N builds at once.
    private readonly SemaphoreSlim _buildThrottle = new(initialCount: 4, maxCount: 4);

    // In-flight de-dupe: if a row scrolls out and back in mid-build, the second call
    // awaits the first task instead of re-fetching tracks.
    private readonly ConcurrentDictionary<string, Task<IconSource?>> _inFlight = new();

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

        // Coalesce concurrent calls for the same playlist onto a single in-flight task.
        // The shared task is registered before any await so a re-entrant call sees it.
        return _inFlight.GetOrAdd(playlistId, id => BuildAndForgetAsync(id, mosaicHint, ct));
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
        var fileName = $"{SanitizeId(playlistId)}_{hash}.png";

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
            using var renderTarget = new CanvasRenderTarget(device, MosaicSizePx, MosaicSizePx, 96f);
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);

                // Row-major quadrants. If we have fewer than 4 tiles, cycle through what we have
                // so every quadrant is filled (matches Spotify's "fill" behaviour for sparse playlists).
                for (int i = 0; i < 4; i++)
                {
                    var tile = available[i % available.Length];
                    var destX = (i % 2) * TileSizePx;
                    var destY = (i / 2) * TileSizePx;
                    var destRect = new Windows.Foundation.Rect(destX, destY, TileSizePx, TileSizePx);
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
                var image = new BitmapImage { DecodePixelWidth = MosaicSizePx };
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
                var image = new BitmapImage { DecodePixelWidth = MosaicSizePx };
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

            // Drop any prior mosaic files for this playlist whose hash no longer matches —
            // these are stale (the playlist's first 4 albums changed since they were written).
            var prefix = playlistIdSafe + "_";
            foreach (var existing in Directory.EnumerateFiles(folderPath, prefix + "*"))
            {
                var existingName = Path.GetFileName(existing);
                if (!string.Equals(existingName, fileName, StringComparison.Ordinal))
                {
                    try { File.Delete(existing); }
                    catch { /* best-effort */ }
                }
            }
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
