using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// The one cover-colour plane. These cover the properties the whole feature rests on: a cover's colour is keyed by its
// IMAGE (so every pre-sized URL and every entity showing that cover share one entry), it survives restarts for ~half a
// year, a colourless cover is remembered for a shorter window instead of being re-asked forever, light theme never
// serves a dark-only (kind 179) grading, and a render-path miss is what triggers the fetch.
public class CoverColorPlaneTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "wavee-colors-" + Guid.NewGuid().ToString("N") + ".json");

    // The REAL wire shape, not a plausible-looking one: `textBrightAccent` is the contrast-graded INK — pure white in the
    // dark half, pure black in the light half — and the cover's chroma lives in the two background roles. See
    // CoverAccentDerivationTests for why fabricating a hue-bearing accent here was actively harmful.
    static CoverColorPlane.Scheme Dark => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);
    static CoverColorPlane.Scheme Light => new(0xFFF2F2F2u, 0xFFE0E0E0u, 0xFF101010u, 0xFF505050u, 0xFF000000u);

    // The SAME artwork at two sizes. Spotify puts the size in the id's first 16 chars and the artwork identity in the
    // trailing 24 — 64px `…00004851` vs 640px `…0000b273`, both ending `e86f30ec6f14a30f1cf9bb9d`.
    const string Small = "https://i.scdn.co/image/ab67616d00004851e86f30ec6f14a30f1cf9bb9d";
    const string Large = "https://i.scdn.co/image/ab67616d0000b273e86f30ec6f14a30f1cf9bb9d";
    const string Other = "https://i.scdn.co/image/ab67616d0000b273ffffffffffffffffffffffff";

    [Fact]
    public void KeyForUrl_IsTheSizeIndependentArtworkIdentity()
    {
        // Two sizes of one cover MUST collapse to one entry, or a row thumbnail and its grid card would each pay for
        // their own grading and the tint would land twice, late.
        Assert.Equal("e86f30ec6f14a30f1cf9bb9d", CoverColorPlane.KeyForUrl(Small));
        Assert.Equal(CoverColorPlane.KeyForUrl(Small), CoverColorPlane.KeyForUrl(Large));
        Assert.NotEqual(CoverColorPlane.KeyForUrl(Large), CoverColorPlane.KeyForUrl(Other));
        // The FULL id is still what the wire wants (`spotify:image:<full id>`), so it stays available separately.
        Assert.Equal("ab67616d0000b273e86f30ec6f14a30f1cf9bb9d", CoverColorPlane.IdSpan(Large).ToString());
        // A query string is not part of the identity, and a non-Spotify id keys on itself.
        Assert.Equal("cover.jpg", CoverColorPlane.KeyForUrl("https://mosaic.scdn.co/640/cover.jpg?x=1"));
        Assert.Equal("", CoverColorPlane.KeyForUrl(null));
        // The RAW provider token is the same artwork. A caller holding an un-normalized `Image.Url` must land on the very
        // entry the CDN url grades, or page chrome silently keys a second (never-filled) row — and the fetch would ask
        // for `spotify:image:spotify:image:<id>`.
        Assert.Equal(CoverColorPlane.KeyForUrl(Large),
                     CoverColorPlane.KeyForUrl("spotify:image:ab67616d0000b273e86f30ec6f14a30f1cf9bb9d"));
        Assert.Equal("ab67616d0000b273e86f30ec6f14a30f1cf9bb9d",
                     CoverColorPlane.IdSpan("spotify:image:ab67616d0000b273e86f30ec6f14a30f1cf9bb9d").ToString());
        Assert.True(CoverColorPlane.CanGrade(Large));
        Assert.True(CoverColorPlane.CanGrade("spotify:image:ab67616d0000b273e86f30ec6f14a30f1cf9bb9d"));
        Assert.False(CoverColorPlane.CanGrade("https://mosaic.scdn.co/640/cover.jpg"));
        Assert.False(CoverColorPlane.CanGrade("spotify:image:not-a-provider-image-id"));
    }

    [Fact]
    public void ImageUri_IsTheSpotifyImageForm_NotAnHttpsUrl()
        => Assert.Equal("spotify:image:abc", CoverColorPlane.ImageUri("abc"));

    [Fact]
    public void DarkGrading_ServesDarkTheme_ButNeverLightTheme()
    {
        var plane = new CoverColorPlane(TempFile());
        plane.SetDark(Large, Dark);

        Assert.True(plane.TryGetTint(Large, lightTheme: false, out uint dark));
        Assert.Equal(0xFF101040u, dark);
        // Kind 179 only ever ships dark treatments (its three schemes are elevation levels). Serving one on a pale page
        // would drop a near-black slab into it, so light theme keeps the neutral tile until the filler grades it.
        Assert.False(plane.TryGetTint(Large, lightTheme: true, out _));
    }

    [Fact]
    public void GradedEntry_ServesBothThemes()
    {
        var plane = new CoverColorPlane(TempFile());
        plane.SetGraded(CoverColorPlane.KeyForUrl(Large), new CoverColorPlane.GradedColors(Dark, Light, BestFitIsLight: true));

        Assert.True(plane.TryGetTint(Large, lightTheme: false, out uint d));
        Assert.True(plane.TryGetTint(Large, lightTheme: true, out uint l));
        Assert.Equal(0xFF101040u, d);
        Assert.Equal(0xFFF2F2F2u, l);
    }

    [Fact]
    public void OneImage_TintsEverySizeOfTheSameCover()
    {
        var plane = new CoverColorPlane(TempFile());
        plane.SetGraded(CoverColorPlane.KeyForUrl(Small), new CoverColorPlane.GradedColors(Dark, Light, false));

        // Written from the 64px URL, read from the 640px one: the row thumbnail and the grid card are the same entry.
        Assert.True(plane.TryGetTint(Large, lightTheme: false, out uint argb));
        Assert.Equal(0xFF101040u, argb);
    }

    [Fact]
    public void RoundTripsThroughDisk_AndExpiresAfterTheHitTtl()
    {
        var path = TempFile();
        try
        {
            long now = 1_000_000;
            var a = new CoverColorPlane(path, () => now);
            a.SetGraded(CoverColorPlane.KeyForUrl(Large), new CoverColorPlane.GradedColors(Dark, Light, false));
            a.Flush();

            var b = new CoverColorPlane(path, () => now);   // a fresh process reads the persisted table
            Assert.True(b.TryGetTint(Large, lightTheme: true, out uint l));
            Assert.Equal(0xFFF2F2F2u, l);

            long later = now + (long)TimeSpan.FromDays(181).TotalSeconds;
            var c = new CoverColorPlane(path, () => later);
            Assert.False(c.TryGetTint(Large, lightTheme: false, out _));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task ColourlessCover_IsNotReAskedInsideTheWindow_ButIsAfterIt()
    {
        long now = 5_000;
        var path = TempFile();
        try
        {
            int asks = 0;
            Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<CoverColorPlane.GradedColors?>>> filler =
                (keys, _) =>
                {
                    asks++;
                    return Task.FromResult<IReadOnlyList<CoverColorPlane.GradedColors?>>(
                        new CoverColorPlane.GradedColors?[keys.Count]);   // the server has no colours for these
                };

            var plane = new CoverColorPlane(path, () => now) { Filler = filler };
            Assert.False(plane.TryGetTint(Large, lightTheme: false, out _));
            for (int i = 0; i < 100 && asks == 0; i++) await Task.Delay(25);
            Assert.Equal(1, asks);

            // Inside the window the answer is remembered: rendering that art again must NOT re-ask.
            Assert.False(plane.TryGetTint(Large, lightTheme: false, out _));
            await Task.Delay(250);
            Assert.Equal(1, asks);
            plane.Flush();

            // Past it, the negative expires and the next render tries again — a colourless verdict is not permanent.
            long later = now + (long)TimeSpan.FromDays(8).TotalSeconds;
            int revivedAsks = 0;
            var revived = new CoverColorPlane(path, () => later)
            {
                Filler = (keys, _) =>
                {
                    revivedAsks++;
                    return Task.FromResult<IReadOnlyList<CoverColorPlane.GradedColors?>>(
                        new CoverColorPlane.GradedColors?[] { new CoverColorPlane.GradedColors(Dark, Light, false) });
                },
            };
            Assert.False(revived.TryGetTint(Large, lightTheme: false, out _));
            for (int i = 0; i < 100 && revivedAsks == 0; i++) await Task.Delay(25);
            Assert.Equal(1, revivedAsks);
            Assert.True(revived.TryGetTint(Large, lightTheme: false, out _));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task RenderMiss_IsTheRequest_AndBatchesBeforeAsking()
    {
        var plane = new CoverColorPlane(TempFile());
        var asked = new List<IReadOnlyList<string>>();
        plane.Filler = (keys, _) =>
        {
            asked.Add(keys.ToArray());
            var result = new CoverColorPlane.GradedColors?[keys.Count];
            for (int i = 0; i < keys.Count; i++) result[i] = new CoverColorPlane.GradedColors(Dark, Light, false);
            return Task.FromResult<IReadOnlyList<CoverColorPlane.GradedColors?>>(result);
        };

        // Four art slots render and miss — exactly what a grid realize does. Two of them are the same artwork at
        // different sizes (a row thumb + its card), which must cost ONE slot in the request, not two.
        Assert.False(plane.TryGetTint(Large, false, out _));
        Assert.False(plane.TryGetTint(Small, false, out _));
        Assert.False(plane.TryGetTint("https://i.scdn.co/image/ab67616d0000b273cafe00000000000000cafe01", false, out _));
        Assert.False(plane.TryGetTint("https://i.scdn.co/image/ab67616d0000b273cafe00000000000000cafe02", false, out _));

        for (int i = 0; i < 100 && asked.Count == 0; i++) await Task.Delay(25);

        Assert.Single(asked);                 // ONE request, not one per slot
        Assert.Equal(3, asked[0].Count);      // …and three artworks, not four URLs
        Assert.All(asked[0], id => Assert.Equal(40, id.Length));   // full image ids — spotify:image: takes nothing less
        Assert.True(plane.TryGetTint(Large, false, out uint argb));
        Assert.Equal(0xFF101040u, argb);
    }

    [Fact]
    public async Task FailedBatch_IsRetriedByTheNextRender()
    {
        var plane = new CoverColorPlane(TempFile());
        int calls = 0;
        plane.Filler = (keys, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromException<IReadOnlyList<CoverColorPlane.GradedColors?>>(new InvalidOperationException("boom"))
                : Task.FromResult<IReadOnlyList<CoverColorPlane.GradedColors?>>(
                    new CoverColorPlane.GradedColors?[] { new CoverColorPlane.GradedColors(Dark, Light, false) });
        };

        Assert.False(plane.TryGetTint(Large, false, out _));
        for (int i = 0; i < 100 && calls == 0; i++) await Task.Delay(25);

        // A transport failure must not poison the image: the slot asks again on its next render.
        Assert.False(plane.TryGetTint(Large, false, out _));
        for (int i = 0; i < 100 && calls < 2; i++) await Task.Delay(25);
        Assert.Equal(2, calls);
        Assert.True(plane.TryGetTint(Large, false, out _));
    }

    [Fact]
    public async Task WatchingOneCover_WakesOnlyThatWatcher()
    {
        var plane = new CoverColorPlane(TempFile());
        plane.Filler = (keys, _) =>
        {
            var result = new CoverColorPlane.GradedColors?[keys.Count];
            for (int i = 0; i < keys.Count; i++) result[i] = new CoverColorPlane.GradedColors(Dark, Light, false);
            return Task.FromResult<IReadOnlyList<CoverColorPlane.GradedColors?>>(result);
        };

        var mine = plane.Watch(Large);
        var other = plane.Watch("https://i.scdn.co/image/ab67616d0000b273cafe00000000000000cafe03");
        int mineAt = mine.Peek(), otherAt = other.Peek();

        Assert.False(plane.TryGetTint(Large, false, out _));
        for (int i = 0; i < 100 && mine.Peek() == mineAt; i++) await Task.Delay(25);

        Assert.True(mine.Peek() > mineAt);        // the page whose cover landed re-renders…
        Assert.Equal(otherAt, other.Peek());      // …and a page watching a different cover does not
    }

    [Fact]
    public async Task LandedBatch_BumpsOnlyWatchersForKeysInThatBatch()
    {
        // Pins the paint-only topology: a graded batch must wake per-key Watch signals for the keys it filled —
        // never a silent global fan-out to every Watcher (CoverShimmer / page leaves subscribe per key).
        var plane = new CoverColorPlane(TempFile());
        const string a = Large;
        const string b = "https://i.scdn.co/image/ab67616d0000b273cafe00000000000000cafe0a";
        const string c = "https://i.scdn.co/image/ab67616d0000b273cafe00000000000000cafe0b";
        plane.Filler = (keys, _) =>
        {
            var result = new CoverColorPlane.GradedColors?[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                // Grade only the first two request slots; leave the rest null so their watchers stay quiet.
                if (i < 2) result[i] = new CoverColorPlane.GradedColors(Dark, Light, false);
            }
            return Task.FromResult<IReadOnlyList<CoverColorPlane.GradedColors?>>(result);
        };

        var watchA = plane.Watch(a);
        var watchB = plane.Watch(b);
        var watchC = plane.Watch(c);
        int a0 = watchA.Peek(), b0 = watchB.Peek(), c0 = watchC.Peek();

        Assert.False(plane.TryGetTint(a, false, out _));
        Assert.False(plane.TryGetTint(b, false, out _));
        Assert.False(plane.TryGetTint(c, false, out _));
        for (int i = 0; i < 100 && watchA.Peek() == a0; i++) await Task.Delay(25);

        Assert.True(watchA.Peek() > a0);
        Assert.True(watchB.Peek() > b0);
        Assert.Equal(c0, watchC.Peek());   // in the batch request but ungraded ⇒ its Watch stays put
    }

    [Fact]
    public void NoFiller_ServesTheCacheAndNeverThrows()
    {
        var plane = new CoverColorPlane(TempFile());   // logged out / offline / headless
        Assert.False(plane.TryGetTint(Large, false, out _));
        plane.SetDark(Large, Dark);
        Assert.True(plane.TryGetTint(Large, false, out _));
    }
}

// The universal feed's decoder. The response is POSITIONAL — nothing in it echoes the requested uri — so index
// alignment is the contract that matters most; after that, picking the standard contrast tier and reading both themes.
//
// The fixture below is the REAL payload shape, which is load-bearing beyond decoding: `textBrightAccent` is pure white in
// the dark half and pure black in the light half — always, in all 9,316 cached gradings. It is a contrast-graded INK, and
// a fixture that fabricates a hue there teaches every reader the wrong model (it is what let the chrome-accent bug ship).
public class CoverColorFillerTests
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    const string TwoImages = """
    {
      "data": { "dynamicColors": [
        {
          "bestFit": "light",
          "dark":  { "highContrast": {
                        "backgroundBase":       { "red": 16,  "green": 16,  "blue": 64,  "alpha": 255 },
                        "backgroundTintedBase": { "red": 60,  "green": 68,  "blue": 120, "alpha": 255 },
                        "textBase":             { "red": 255, "green": 255, "blue": 255, "alpha": 255 },
                        "textSubdued":          { "red": 179, "green": 179, "blue": 179, "alpha": 255 },
                        "textBrightAccent":     { "red": 255, "green": 255, "blue": 255, "alpha": 255 } } },
          "light": { "highContrast": {
                        "backgroundBase":       { "red": 242, "green": 242, "blue": 242, "alpha": 255 },
                        "backgroundTintedBase": { "red": 224, "green": 224, "blue": 224, "alpha": 255 },
                        "textBase":             { "red": 16,  "green": 16,  "blue": 16,  "alpha": 255 },
                        "textSubdued":          { "red": 80,  "green": 80,  "blue": 80,  "alpha": 255 },
                        "textBrightAccent":     { "red": 0,   "green": 0,   "blue": 0,   "alpha": 255 } } }
        },
        { "bestFit": "dark" }
      ] }
    }
    """;

    [Fact]
    public void Decodes_BothThemes_AndKeepsRequestOrder()
    {
        var got = CoverColorFiller.Parse(Json(TwoImages), expected: 2);

        Assert.Equal(2, got.Count);
        Assert.NotNull(got[0]);
        var first = got[0]!.Value;
        Assert.Equal(0xFF101040u, first.Dark.BackgroundBase);
        Assert.Equal(0xFF3C4478u, first.Dark.BackgroundTintedBase);
        // The ink, decoded verbatim: white on the dark half, black on the light one. Nothing here amplifies or
        // reinterprets it — WaveePalette decides what is a HUE (CoverAccentDerivationTests), the decoder does not.
        Assert.Equal(0xFFFFFFFFu, first.Dark.TextBrightAccent);
        Assert.NotNull(first.Light);
        Assert.Equal(0xFF000000u, first.Light!.Value.TextBrightAccent);
        Assert.Equal(0xFFF2F2F2u, first.Light!.Value.BackgroundBase);
        Assert.True(first.BestFitIsLight);

        // Second image carried no grading at all → a null in ITS slot, not a shift of the first one's colours.
        Assert.Null(got[1]);
    }

    [Fact]
    public void ShortResponse_LeavesTheMissingSlotsNull()
    {
        var got = CoverColorFiller.Parse(Json(TwoImages), expected: 4);
        Assert.Equal(4, got.Count);
        Assert.NotNull(got[0]);
        Assert.Null(got[2]);
        Assert.Null(got[3]);
    }

    [Fact]
    public void FallsBackToHigherContrast_WhenTheStandardTierIsAbsent()
    {
        var got = CoverColorFiller.Parse(Json("""
        {
          "data": { "dynamicColors": [ { "dark": { "higherContrast": {
              "backgroundBase": { "red": 1, "green": 2, "blue": 3, "alpha": 255 } } } } ] }
        }
        """), expected: 1);

        Assert.NotNull(got[0]);
        Assert.Equal(0xFF010203u, got[0]!.Value.Dark.BackgroundBase);
    }

    [Fact]
    public void MalformedOrEmptyResponse_IsAllNulls_NotAThrow()
    {
        Assert.All(CoverColorFiller.Parse(Json("""{"data":{}}"""), expected: 2), Assert.Null);
        Assert.All(CoverColorFiller.Parse(Json("""{"errors":[{"message":"nope"}]}"""), expected: 1), Assert.Null);
    }
}

// The step AFTER decoding: which graded role is the cover's HUE. This is the regression guard for the bug where every
// artwork-derived accent in the app read `textBrightAccent` — a role whose name promises a hue but which the feed grades
// as pure white (dark) / pure black (light) in 100% of payloads. Its saturation is therefore always 0, so
// WaveePalette.ChromeAccent's greyscale guard fired on EVERY cover and all five Play/CTA sites rendered the semantic
// accent instead of the artwork colour. The fix reads the most-saturated role, which puts the hue back in the background
// roles where the feed actually keeps it — WITHOUT losing the fallback for art that really is monochrome.
public class CoverAccentDerivationTests
{
    // The same two halves CoverColorFillerTests decodes, as Schemes: a blue-ish cover whose only chroma is in the
    // background roles, plus the white/black ink the feed grades for text on it.
    static CoverColorPlane.Scheme WireDark => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);
    static CoverColorPlane.Scheme WireLight => new(0xFFF2F2F2u, 0xFFE0E0E0u, 0xFF101010u, 0xFF505050u, 0xFF000000u);

    [Fact]
    public void Accent_IsTheMostSaturatedRole_NotTheWhiteInk()
    {
        // backgroundBase (16,16,64) is HSV S=0.75 and backgroundTintedBase (60,68,120) S=0.50, so the dominant tone wins;
        // the ink (255,255,255) is S=0 and can never win unless a future feed actually grades it chromatically.
        Assert.Equal(WaveePalette.ToColor(0xFF101040u), WaveePalette.Accent(WireDark));
    }

    [Fact]
    public void ChromeAccent_KeepsTheCoverHue_WhenTextBrightAccentIsWhite()
    {
        // THE regression: with the ink read as the accent this returned Tok.AccentDefault for every cover ever graded,
        // i.e. a system-blue Play button on a warm album for 100% of the catalogue.
        var chrome = WaveePalette.ChromeAccent(WireDark);
        Assert.NotEqual(Tok.AccentDefault, chrome);
        var (_, sat, _) = chrome.ToHsv();
        Assert.True(sat > WaveePalette.NeutralS, "the cover's hue must survive into the chrome accent");
    }

    [Fact]
    public void GreyscaleArt_StillFallsBackToTheSemanticAccent()
    {
        // Every role of this light half is a neutral grey — which is what genuinely black-and-white art grades to. There
        // is no hue to amplify, and inventing one from rounding noise is worse than the system accent. Same for the
        // no-grading-yet fallback scheme.
        Assert.Equal(Tok.AccentDefault, WaveePalette.ChromeAccent(WireLight));
        Assert.Equal(Tok.AccentDefault, WaveePalette.ChromeAccent(WaveePalette.Neutral));
    }

    [Theory]
    [InlineData(217, 63, 49)]
    [InlineData(58, 92, 180)]
    [InlineData(230, 180, 42)]
    public void Hairline_IsQuietButLegible_InBothThemes(byte r, byte g, byte b)
    {
        var seed = ColorF.FromRgba(r, g, b);
        foreach (var palette in Tok.Presets)
        foreach (var theme in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            var tokens = theme == ThemeKind.Dark ? palette.Dark : palette.Light;
            var shell = theme == ThemeKind.Dark ? palette.DarkShell : palette.LightShell;
            var mica = theme == ThemeKind.Dark ? MicaRef.DarkDefault : MicaRef.LightDefault;
            var pane = ColorContrast.Flatten(shell.FileArea, ColorContrast.Flatten(shell.Toolbar, mica));
            var card = ColorContrast.Flatten(tokens.FillCardDefault, pane);
            var line = WaveePalette.Hairline(seed, theme, card);

            Assert.InRange(ColorContrast.Ratio(line, card), 3.24f, 3.26f);
            Assert.True(line.ToHsv().S <= 0.5001f);
        }
    }

    [Theory]
    [InlineData(230, 180, 42)]   // peach/gold — the daylist wash that made Lift-as-ink unreadable
    [InlineData(217, 63, 49)]
    [InlineData(58, 92, 180)]
    public void TextInk_MeetsAaOnTheCard_UnlikeTheLiftedFill(byte r, byte g, byte b)
    {
        var seed = ColorF.FromRgba(r, g, b);
        var lifted = WaveePalette.Lift(seed);
        foreach (var palette in Tok.Presets)
        foreach (var theme in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            var tokens = theme == ThemeKind.Dark ? palette.Dark : palette.Light;
            var shell = theme == ThemeKind.Dark ? palette.DarkShell : palette.LightShell;
            var mica = theme == ThemeKind.Dark ? MicaRef.DarkDefault : MicaRef.LightDefault;
            var pane = ColorContrast.Flatten(shell.FileArea, ColorContrast.Flatten(shell.Toolbar, mica));
            var card = ColorContrast.Flatten(tokens.FillCardDefault, pane);
            var ink = WaveePalette.TextInk(seed, theme, card);

            Assert.True(ColorContrast.MeetsAaText(ink, card),
                $"TextInk must be AA on the card (theme={theme}, ratio={ColorContrast.Ratio(ink, card):0.00})");
            Assert.False(ColorContrast.MeetsAaText(lifted, card) && ink.Equals(lifted),
                "TextInk must not be the raw Lift fill when that fill fails AA (the Home countdown bug)");
        }
    }

    [Fact]
    public void ChromeFromPayload_LiftsTheHomeCardAccent_AndTreatsZeroAsTheSemanticDefault()
    {
        uint peach = 0xFFE6B42Au;   // the daylist colorDark shape: a warm extracted tone
        Assert.Equal(WaveePalette.Lift(WaveePalette.ToColor(peach)), WaveePalette.ChromeFromPayload(peach));
        Assert.Equal(Tok.AccentDefault, WaveePalette.ChromeFromPayload(0));
    }
}
