using System.Collections.Generic;
using Wavee.Core;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The hero-artwork-flicker fix's PURE half: the page-width estimate a detail page's pre-measure mode/hero geometry
/// seeds from instead of the raw window viewport, the unmeasured-vs-measured hero decode bucket, and the
/// <see cref="ImageSource"/> identity rules the cover-handoff latch (<c>DetailPage</c>'s <c>PreferVisible</c> calls)
/// relies on. The engine-bound renderers that consume these (<c>DetailShell</c>, <c>DetailVerticalHero</c>,
/// <c>DetailPage</c>) are deliberately NOT included here — this file drives the same production arithmetic they call.
/// </summary>
public class DetailCoverStabilityTests
{
    // ── 2a: the page-width estimate the pre-measure mode seed uses instead of the raw window viewport ───────────────

    [Fact]
    public void EstimatePageWidthFromViewport_SubtractsTheShellChromeAllowance()
    {
        Assert.Equal(560f, DetailLayoutBreakpoints.EstimatePageWidthFromViewport(560f + DetailLayoutBreakpoints.ShellChromeAllowanceDip));
        // Never negative — a tiny/snapped window still yields a usable (zero-floor) estimate.
        Assert.Equal(0f, DetailLayoutBreakpoints.EstimatePageWidthFromViewport(0f));
        Assert.Equal(0f, DetailLayoutBreakpoints.EstimatePageWidthFromViewport(DetailLayoutBreakpoints.ShellChromeAllowanceDip - 10f));
    }

    [Fact]
    public void InitialModeForViewport_WindowWidthAndPageWidthDisagree_StraddlingTheVerticalBreakpoint()
    {
        // A window at 799 DIP: the RAW viewport reading seeds mode 1 (two-column, narrow variant — 799 is in the
        // [660,820) band) — but this page's own content column is ~240 DIP narrower (the shell's nav pane), landing
        // at 559: below the 560 vertical threshold. Composing mode 1 against a 559-DIP page is exactly the wrong-wide
        // first frame that remounts (and re-decodes) the hero the instant the real Measure callback corrects it.
        const float windowWidth = 799f;
        float pageWidthEstimate = DetailLayoutBreakpoints.EstimatePageWidthFromViewport(windowWidth);
        Assert.Equal(559f, pageWidthEstimate);

        int fromWindow = DetailLayoutBreakpoints.InitialModeForViewport(windowWidth);
        int fromPage = DetailLayoutBreakpoints.InitialModeForViewport(pageWidthEstimate);

        Assert.Equal(1, fromWindow);
        Assert.Equal(DetailLayoutBreakpoints.VerticalMode, fromPage);
        Assert.NotEqual(fromWindow, fromPage);   // the disagreement this estimate exists to resolve
    }

    [Fact]
    public void InitialModeForViewport_PageWidthEstimate_NeverPicksAWiderModeThanTheWindowReading()
    {
        // The allowance is a SUBTRACTION, so for every window width the page-derived seed is always at least as
        // narrow (numerically ≥) as the raw-viewport seed would have been — it can never accidentally compose a
        // WIDER arm than the naive (bug) reading did.
        for (float w = 300f; w <= 1400f; w += 4f)
        {
            int fromWindow = DetailLayoutBreakpoints.InitialModeForViewport(w);
            int fromPage = DetailLayoutBreakpoints.InitialModeForViewport(DetailLayoutBreakpoints.EstimatePageWidthFromViewport(w));
            Assert.True(fromPage >= fromWindow, $"w={w}: page-derived mode {fromPage} was WIDER than the window reading {fromWindow}");
        }
    }

    // ── 2b: the vertical hero's unmeasured decode bucket ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(96f)]
    [InlineData(200f)]
    [InlineData(240f)]
    [InlineData(280f)]
    public void ArtworkDecodePx_Unmeasured_AlwaysRequests256_RegardlessOfTheGuessedArtSize(float artSize)
    {
        // Unmeasured geometry is itself only a guess (FallbackW / a page-width estimate, never the page's real
        // bounds) — so the decode bucket must not chase it. 256 matches the grid tiles / DetailRail hero / the
        // Home shelf card, so first frame is a cache hit instead of a probably-wrong bucket.
        Assert.Equal(256, DetailVerticalLayout.ArtworkDecodePx(artSize, widthMeasured: false));
    }

    [Theory]
    [InlineData(1f, 256)]
    [InlineData(128f, 256)]     // boundary: <= 128 → 256
    [InlineData(129f, 512)]
    [InlineData(288f, 512)]     // boundary: <= 288 → 512
    [InlineData(289f, 1024)]
    [InlineData(1024f, 1024)]
    public void ArtworkDecodePx_Measured_UsesTheUnchangedSizeLadder(float artSize, int expected)
    {
        Assert.Equal(expected, DetailVerticalLayout.ArtworkDecodePx(artSize, widthMeasured: true));
        // The gated overload must never diverge from the plain one it wraps for the measured case.
        Assert.Equal(DetailVerticalLayout.ArtworkDecodePx(artSize), DetailVerticalLayout.ArtworkDecodePx(artSize, widthMeasured: true));
    }

    // ── 2d.1: ImageSource.SameArt / PreferVisible over mosaics + the no-preview fallback latch ─────────────────────

    // Real id shapes (mirrors ImageSourceTests): 40 hex = 16-char size/kind marker + 24-char art identity.
    const string Art = "a149cc5f2c8074884fc06a80";
    const string Card300 = "https://i.scdn.co/image/ab67616d00001e02" + Art;
    const string Hero640 = "https://i.scdn.co/image/ab67616d0000b273" + Art;
    const string OtherArt = "https://i.scdn.co/image/ab67616d0000b27392144c5952844a7c0086b141";

    [Fact]
    public void SameArt_MosaicToMosaic_MatchesTheIdenticalTileSet()
    {
        var tiles = new List<string> { Card300, Hero640, "https://i.scdn.co/image/tile3", "https://i.scdn.co/image/tile4" };
        var a = new Image("", MosaicTiles: tiles);
        var b = new Image("", MosaicTiles: new List<string>(tiles));   // a distinct list instance, same urls
        Assert.True(ImageSource.SameArt(a, b));
    }

    [Fact]
    public void SameArt_MosaicToMosaic_DifferentTileSetsDoNotMatch()
    {
        var a = new Image("", MosaicTiles: new List<string> { Card300, Hero640 });
        var b = new Image("", MosaicTiles: new List<string> { OtherArt, Hero640 });
        Assert.False(ImageSource.SameArt(a, b));
    }

    [Fact]
    public void SameArt_MosaicReducesToItsLeadTile_MatchesASingleCoverOfThatTile()
    {
        // Surfaces.Artwork renders a 1-3-tile mosaic as a single cover of tiles[0] (`image = new Image(tiles[0])`);
        // the identity comparison must agree, or a nav-preview card that already reduced to a single Url can never
        // latch against the full model's still-mosaic Image (or vice versa) — exactly the H-class flicker this file
        // is about, just on the mosaic path instead of the plain-cover one.
        var mosaic = new Image("", MosaicTiles: new List<string> { Card300, Hero640 });
        var single = new Image(Card300, 300, 300);
        Assert.True(ImageSource.SameArt(mosaic, single));
        Assert.True(ImageSource.SameArt(single, mosaic));   // symmetric
    }

    [Fact]
    public void SameArt_MosaicLeadTile_MatchesADifferentSizeRenditionOfThatTile()
    {
        // The single-cover side can also be a DIFFERENT size hash of the mosaic's lead tile — the same size-agnostic
        // asymmetry SameArt already grants two plain covers.
        var mosaic = new Image("", MosaicTiles: new List<string> { Card300 });
        var singleAtHeroSize = new Image(Hero640, 640, 640);
        Assert.True(ImageSource.SameArt(mosaic, singleAtHeroSize));
    }

    [Fact]
    public void SameArt_MosaicLeadTile_DoesNotMatchAnUnrelatedCover()
    {
        var mosaic = new Image("", MosaicTiles: new List<string> { Card300 });
        Assert.False(ImageSource.SameArt(mosaic, new Image(OtherArt)));
    }

    [Fact]
    public void PreferVisible_MosaicVsSingleTile_KeepsVisible_ForTheSameLeadArt()
    {
        var visibleSingle = new Image(Card300, 300, 300);
        var incomingMosaic = new Image("", MosaicTiles: new List<string> { Hero640 });

        Image? chosen = ImageSource.PreferVisible(incomingMosaic, visibleSingle);

        // Same art (the mosaic's lead tile is the visible cover at another size) → keep the already-shown rendition.
        Assert.NotNull(chosen);
        Assert.Equal(visibleSingle.Url, chosen!.Url);
    }

    [Fact]
    public void PreferVisible_NoPreviewFallback_LatchesAgainstTheLastPublishedCover()
    {
        // DetailPage's no-preview cover latch (deep link / search hit): `preview?.Cover ?? _lastCover`. With no
        // preview, the last cover THIS page instance actually published must still be honoured for the same-art
        // case, exactly like the live-refresh latch already does.
        Image? preview = null;
        var lastPublished = new Image(Card300, 300, 300);
        Image? fallback = preview ?? lastPublished;
        var loaded = new Image(Hero640, 640, 640);

        Image? chosen = ImageSource.PreferVisible(loaded, fallback);

        Assert.NotNull(chosen);
        Assert.Equal(lastPublished.Url, chosen!.Url);     // same art → keep the visible rendition, not the fresh hash
        Assert.Equal(loaded.Url, chosen.LargestUrl);       // still enriched with the incoming's largest known url
    }

    [Fact]
    public void PreferVisible_NoPreviewAndNoLastCover_TakesTheLoadedCoverOutright()
    {
        // First-ever load of a route (nothing published yet, no preview either): nothing to latch against, so the
        // freshly loaded cover simply wins — unchanged from before this fix.
        Image? preview = null;
        Image? lastPublished = null;
        Image? fallback = preview ?? lastPublished;
        var loaded = new Image(Card300, 300, 300);

        Assert.Same(loaded, ImageSource.PreferVisible(loaded, fallback));
    }

    [Fact]
    public void PreferVisible_NoPreviewFallback_StillTakesIncoming_WhenItIsGenuinelyDifferentArt()
    {
        // A genuinely new cover (an edit, a daylist rollover) must not be suppressed just because SOME cover was
        // published before — PreferVisible's same-art gate is what protects against over-latching.
        Image? preview = null;
        var lastPublished = new Image(Card300, 300, 300);
        Image? fallback = preview ?? lastPublished;
        var loaded = new Image(OtherArt, 640, 640);

        Assert.Same(loaded, ImageSource.PreferVisible(loaded, fallback));
    }
}
