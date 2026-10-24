// ── Entities/Palette.Tone.cs — SHELL: a photograph's luminance map and subject, measured once, for the scrim under text on it and its crop ──
//
// The cover-colour plane answers "what colour is this image" from the provider; it cannot say how BRIGHT the photograph is where a
// surface sets its copy, which is what the on-media scrim needs (`MediaScrim`, Platform/MediaScrim.cs). So the plane gets a second,
// client-side column: `Tone(url)` returns the image's `MediaScrim.LumaMap`, or null until it is measured, and subscribes the caller.
//
// THE MEASUREMENT runs ONCE per url, on a pool thread: the encoded bytes come through the same fetcher the engine's image pipeline uses
// (`DefaultImageFetcher`, disk-first over the app's image cache directory, so a photograph already on screen is a disk hit and no
// second download), WIC decodes them to a small map (`ToneImage`), and the map is posted back through the plane's own UI poster
// (`Palette.Post`). A failure leaves the row null: the surface keeps its floor veil. The rows are bounded (`ToneRows`); past it the
// measured ones no surface reads any more are forgotten and measure again on their next ask.
//
// THE SUBJECT (lane B10) is read in the same pass from the same decode (`ToneImage.Read`: the OS's face detector over a grey copy):
// `Subject(url)` returns where the photograph's subject stands (`PhotoSubject`), or null until the measurement settles. It always settles: a
// photograph that cannot be fetched or decoded settles on the safe zone, so a surface that waits for it to crop never waits for ever.

using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Palette
{
    sealed class ToneRow
    {
        public readonly Signal<MediaScrim.LumaMap?> Map = new(null);
        public readonly Signal<PhotoSubject?> Subject = new(null);
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
    public static MediaScrim.LumaMap? Tone(string? url) => RowOf(url)?.Map.Value;

    /// <summary>Where the subject of the photograph at <paramref name="url"/> stands (faces, or the safe zone), or null until it is measured. Reading it
    /// subscribes the caller; the first read starts the measurement (the one <see cref="Tone"/> runs). It always settles. UI thread.</summary>
    public static PhotoSubject? Subject(string? url) => RowOf(url)?.Subject.Value;

    /// <summary>The row of <paramref name="url"/>, its measurement started.</summary>
    static ToneRow? RowOf(string? url)
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
        return row;
    }

    static void ForgetMeasuredTones()
    {
        var done = new List<string>();
        // A row a mounted surface still reads stays (its photograph's crop and scrim would otherwise drop and come back on its next render).
        foreach (var (key, row) in s_tones)
            if ((row.Map.Peek() is not null || row.Subject.Peek() is not null) && !row.Map.HasSubscribers && !row.Subject.HasSubscribers) done.Add(key);
        foreach (string key in done) s_tones.Remove(key);
    }

    /// <summary>The frame's images are the engine's fake decoder's (the headless inspector without `images=real`): a photograph paints as its
    /// per-source gradient (`FakeImageDecoder`), so its luminance map is measured from that gradient, not the file the gradient stands for, and a
    /// scrim solved from it agrees with the frame (audit B8: ar9's light band words read 3.5 : 1 over a dark fake gradient under a scrim solved for
    /// the bright file). The subject still comes from the file. Setting it forgets the measured rows. UI thread.</summary>
    public static bool FakePixels
    {
        get => s_fakePixels;
        set
        {
            if (s_fakePixels == value) return;
            s_fakePixels = value;
            s_tones.Clear();
        }
    }

    static bool s_fakePixels;

    /// <summary>The fake decoder's picture of <paramref name="url"/> as a map of <paramref name="width"/> x <paramref name="height"/>.</summary>
    static MediaScrim.LumaMap FakeMap(string url, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        FluentGpu.Scene.FakeImageDecoder.Paint(url, bgra, width, height);
        return MediaScrim.Measure(bgra, width, height);
    }

    static async Task MeasureTone(string url, ToneRow row)
    {
        bool fake = s_fakePixels;
        try
        {
            DefaultImageFetcher fetcher = s_toneFetcher.Value;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            FetchResult fetched = await fetcher.FetchAsync(url, cts.Token).ConfigureAwait(false);
            if (!fetched.Ok || fetched.Buffer is not { } buffer)
            {
                if (fake) { var painted = FakeMap(url, MediaScrim.MapEdge, MediaScrim.MapEdge); Post(() => row.Map.Value = painted); }
                SettleSafe(row);
                return;
            }
            ToneImage.Reading reading;
            try { reading = ToneImage.Read(buffer, fetched.Length); }
            finally { fetcher.ReturnBuffer(buffer); }
            var subject = reading.Subject;
            Log.Info("palette", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"photo subject {(subject.Faces ? "faces" : "safe zone")} at {subject.X:0.000}, {subject.Y:0.000}: {Path.GetFileName(url)}"));
            MediaScrim.LumaMap? measured = reading.Map;
            if (fake) measured = FakeMap(url, measured?.Width ?? MediaScrim.MapEdge, measured?.Height ?? MediaScrim.MapEdge);
            Post(() =>
            {
                if (measured is { } map) row.Map.Value = map;
                row.Subject.Value = reading.Subject;
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or InvalidOperationException) { SettleSafe(row); }
    }

    /// <summary>A photograph that could not be measured: its subject settles on the safe zone (the map stays null: the surface keeps its floor).</summary>
    static void SettleSafe(ToneRow row) => Post(() => row.Subject.Value = PhotoSubject.SafeZone);
}
