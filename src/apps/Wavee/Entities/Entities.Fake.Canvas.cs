// ── Entities/Entities.Fake.Canvas.cs — the fake catalog's Canvas (a named partial of Entities.Fake.cs) ────────────────────────────────────
//
// Every fake track gets its kind-16 answer at boot, so `--fake` never sends for one: most have NONE (the page is what it is without
// Canvas), and the tracks in `WithCanvas` loop the one local canvas (assets/canvas: a 4 s 288x512 H.264 gradient, no audio, ~33 KB, made
// with ffmpeg's `gradients` source) with its own .thmb still as the poster. tr3 is the inspector's `play` track and tr20 the launch's
// now playing; tr4 and tr5 (no canvas) are the without-canvas pair.

namespace Wavee;

public static partial class Entities
{
    /// <summary>The fake tracks that carry a canvas (by their <c>trN</c> index).</summary>
    public static readonly int[] FakeCanvasTracks = [3, 20, 27, 41, 55, 62];

    static partial void SeedCanvasSurfaces(long now0)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "assets", "canvas");
        var canvas = new CanvasText(
            Url: Path.Combine(dir, "fake-canvas.cnvs.mp4"),
            Poster: Path.Combine(dir, "fake-canvas.thmb.288x512.jpg"),
            CanvasUri: "spotify:canvas:fakecanvas0000000000001", Artist: "", ArtistUri: "", UploadedBy: "artist", Etag: "fake",
            Kind: CanvasKind.VideoLoopingRandom, PosterW: 288, PosterH: 512,
            FetchedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), CacheTtl: int.MaxValue / 2, OfflineTtl: int.MaxValue / 2);
        for (int i = 0; i < TrackCount; i++)
        {
            int slot = ResolveSeedSlot(EntityKind.Track, TrackUri(i));
            LandCanvas(slot, Array.IndexOf(FakeCanvasTracks, i) >= 0 ? canvas : null);
        }
    }
}
