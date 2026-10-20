// ── Entities/Palette.Tone.cs — SHELL: a photograph's luminance map, measured once, for the scrim under text on it ─────────────────────
//
// The cover-colour plane answers "what colour is this image" from the provider; it cannot say how BRIGHT the photograph is where a
// surface sets its copy, which is what the on-media scrim needs (`MediaScrim`, Platform/MediaScrim.cs). So the plane gets a second,
// client-side column: `Tone(url)` returns the image's `MediaScrim.LumaMap`, or null until it is measured, and subscribes the caller.
//
// THE MEASUREMENT runs ONCE per url, on a pool thread: the encoded bytes come through the same fetcher the engine's image pipeline uses
// (`DefaultImageFetcher`, disk-first over the app's image cache directory, so a photograph already on screen is a disk hit and no
// second download), WIC decodes them to a small map (`ToneImage`), and the map is posted back through the plane's own UI poster
// (`Palette.Post`). A failure leaves the row null: the surface keeps its floor veil. The rows are bounded (`ToneRows`); past it the
// measured ones are forgotten and measure again on their next ask.

using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Palette
{
    sealed class ToneRow
    {
        public readonly Signal<MediaScrim.LumaMap?> Map = new(null);
        public bool Started;
    }

    /// <summary>The most photographs measured at once before the measured rows are forgotten.</summary>
    const int ToneRows = 64;

    static readonly Dictionary<string, ToneRow> s_tones = new(StringComparer.Ordinal);
    static readonly Lazy<DefaultImageFetcher> s_toneFetcher = new(static () => new DefaultImageFetcher(
        http: DefaultImageFetcher.CreateClient(static inner => Wire.Handler("images", inner, storms: false)),
        diskCache: new DiskImageCache(Settings.ImageCacheDirectory())));

    /// <summary>The luminance map of the photograph at <paramref name="url"/> (an https url or a local path), or null until it is measured
    /// (or when it cannot be). Reading it subscribes the caller; the first read starts the measurement. UI thread.</summary>
    public static MediaScrim.LumaMap? Tone(string? url)
    {
        if (url is not { Length: > 0 }) return null;
        if (!s_tones.TryGetValue(url, out ToneRow? row))
        {
            if (s_tones.Count >= ToneRows) ForgetMeasuredTones();
            s_tones[url] = row = new ToneRow();
        }
        if (!row.Started)
        {
            row.Started = true;
            ToneRow target = row;
            _ = Task.Run(() => MeasureTone(url, target));
        }
        return row.Map.Value;
    }

    static void ForgetMeasuredTones()
    {
        var done = new List<string>();
        foreach (var (key, row) in s_tones)
            if (row.Map.Peek() is not null) done.Add(key);
        foreach (string key in done) s_tones.Remove(key);
    }

    static async Task MeasureTone(string url, ToneRow row)
    {
        try
        {
            DefaultImageFetcher fetcher = s_toneFetcher.Value;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            FetchResult fetched = await fetcher.FetchAsync(url, cts.Token).ConfigureAwait(false);
            if (!fetched.Ok || fetched.Buffer is not { } buffer) return;
            MediaScrim.LumaMap? map;
            try { map = ToneImage.Measure(buffer, fetched.Length); }
            finally { fetcher.ReturnBuffer(buffer); }
            if (map is not null) Post(() => row.Map.Value = map);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or InvalidOperationException) { }
    }
}
