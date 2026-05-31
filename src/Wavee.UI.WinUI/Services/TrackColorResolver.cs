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
using Windows.Storage.Streams;

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

    // The immersive background only needs two dominant colours. Decode the art straight to a tiny
    // 48px BGRA buffer (a ~9 KB gen0 allocation — no full-res LOH block, no Gen2 churn) and pick the
    // two most prominent vibrant buckets directly. This replaced a ColorThief median-cut on a
    // re-encoded thumbnail that threw on every call (leaving the palette null → a static background).
    private const uint PaletteSampleEdge = 48;

    private static async Task<TrackPalette?> ToPaletteAsync(BitmapDecoder source)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1u, Math.Min(PaletteSampleEdge, source.PixelWidth)),
            ScaledHeight = Math.Max(1u, Math.Min(PaletteSampleEdge, source.PixelHeight)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var provider = await source.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var px = provider.DetachPixelData();   // BGRA, edge*edge*4 bytes

        // Quantise to 4 bits/channel; score buckets by saturation so vibrant colours win over greys.
        // [0]=count [1..3]=ΣR,G,B [4]=Σ(score*1000).
        var buckets = new Dictionary<int, long[]>();
        void Accumulate(bool vibrantOnly)
        {
            buckets.Clear();
            for (var i = 0; i + 4 <= px.Length; i += 4)
            {
                int b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a < 16) continue;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx < 24 || mn > 236) continue;                       // skip near-black / near-white
                var sat = mx == 0 ? 0d : (mx - mn) / (double)mx;
                if (vibrantOnly && sat < 0.18) continue;
                var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                if (!buckets.TryGetValue(key, out var e)) { e = new long[5]; buckets[key] = e; }
                e[0]++; e[1] += r; e[2] += g; e[3] += b; e[4] += (long)((sat + 0.15) * 1000);
            }
        }

        Accumulate(vibrantOnly: true);
        if (buckets.Count == 0) Accumulate(vibrantOnly: false);   // greyscale art → fall back to any colour
        if (buckets.Count == 0) return null;

        var ranked = buckets.Values.OrderByDescending(e => e[4]).ToList();
        var primary = BucketColor(ranked[0]);
        var accent = ranked.Count > 1 ? BucketColor(ranked[1]) : Lighten(primary);
        return new TrackPalette(Hex(primary), Hex(accent));
    }

    private static Vector3 BucketColor(long[] e) => new(e[1] / (float)e[0], e[2] / (float)e[0], e[3] / (float)e[0]);

    private static Vector3 Lighten(Vector3 c) => new(
        Math.Clamp(c.X * 1.25f + 28f, 0, 255),
        Math.Clamp(c.Y * 1.25f + 28f, 0, 255),
        Math.Clamp(c.Z * 1.25f + 28f, 0, 255));

    private static string Hex(Vector3 c) => string.Format(
        CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
        (byte)Math.Clamp(c.X, 0, 255), (byte)Math.Clamp(c.Y, 0, 255), (byte)Math.Clamp(c.Z, 0, 255));
}
