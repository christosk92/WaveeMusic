using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Controls.Lyrics.Helper;
using Wavee.UI.Helpers;
using Wavee.UI.Services.Tracks;
using Windows.Graphics.Imaging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Extracts a primary + accent colour from a track's album art for the immersive refresh
/// background, reusing the app's median-cut <see cref="PaletteHelper"/>. Caches results in-process by
/// track URI (so a swipe back is instant and art is decoded at most once), and the underlying art
/// bytes ride the platform HTTP cache. <see cref="PrefetchAsync"/> warms upcoming cards.
/// </summary>
public sealed class TrackColorResolver : ITrackColorResolver
{
    private readonly IHttpClientFactory? _httpFactory;
    private readonly ILogger<TrackColorResolver>? _logger;
    private readonly ConcurrentDictionary<string, TrackPalette?> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(3);

    public TrackColorResolver(IHttpClientFactory? httpFactory = null, ILogger<TrackColorResolver>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<TrackPalette?> ResolveAsync(string trackUri, string? imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri)) return null;
        if (_cache.TryGetValue(trackUri, out var cached)) return cached;

        TrackPalette? palette = null;
        try { palette = await ExtractAsync(imageUrl, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Palette resolve failed for {Uri}", trackUri); }

        _cache[trackUri] = palette;     // cache null too — don't re-extract a failed/no-art track
        return palette;
    }

    public async Task PrefetchAsync(IReadOnlyList<(string Uri, string? ImageUrl)> tracks, CancellationToken ct = default)
    {
        var tasks = tracks
            .Where(t => !string.IsNullOrEmpty(t.Uri) && !_cache.ContainsKey(t.Uri))
            .Select(async t =>
            {
                try
                {
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try { await ResolveAsync(t.Uri, t.ImageUrl, ct).ConfigureAwait(false); }
                    finally { _gate.Release(); }
                }
                catch { /* never throw from prefetch */ }
            });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<TrackPalette?> ExtractAsync(string? imageUrl, CancellationToken ct)
    {
        var url = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(url)) return null;

        BitmapDecoder decoder;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(new Uri(url).LocalPath);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            decoder = await BitmapDecoder.CreateAsync(stream);
            return await ToPaletteAsync(decoder);
        }

        var http = _httpFactory?.CreateClient() ?? new HttpClient();
        var bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        using var ms = new MemoryStream(bytes);
        decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        return await ToPaletteAsync(decoder);
    }

    private static async Task<TrackPalette?> ToPaletteAsync(BitmapDecoder decoder)
    {
        var result = await PaletteHelper.MedianCutGetAccentColorsFromByteAsync(decoder, 3, isDark: true);
        if (result?.Palette is not { Count: > 0 } palette) return null;
        var primary = palette[0];
        var accent = palette.Count > 1 ? palette[1] : Lighten(primary);
        return new TrackPalette(Hex(primary), Hex(accent));
    }

    private static Vector3 Lighten(Vector3 c) => new(
        Math.Clamp(c.X * 1.25f + 28f, 0, 255),
        Math.Clamp(c.Y * 1.25f + 28f, 0, 255),
        Math.Clamp(c.Z * 1.25f + 28f, 0, 255));

    private static string Hex(Vector3 c) => string.Format(
        CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
        (byte)Math.Clamp(c.X, 0, 255), (byte)Math.Clamp(c.Y, 0, 255), (byte)Math.Clamp(c.Z, 0, 255));
}
