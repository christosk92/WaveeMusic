// ── Wavee.Tests/DetailCoverStabilityTests.cs — the hero-artwork-flicker fix's pure half ─────────────────────────────
//
// Ported from _old/Wavee.Tests/DetailCoverStabilityTests.cs. The page-width estimate and the decode bucket stay on
// `Detail.Breakpoints` / `Detail.VerticalLayout`; the cover identity rules move from Wavee.Core's `ImageSource`
// (over an `Image` record) to `Detail.CoverLatch` (over url strings). Two rewrites of the 0.2.9 input, assertions kept:
//   · a MOSAIC is `CoverLatch.Reduce(coverUrl: "", leadTile)` — the same reduction `ImageSource.ReducedUrl` applied
//     before comparing, so every mosaic fact survives as a fact about the lead tile's url;
//   · `Image` records became urls, so `Assert.Same`/`.Url` compare the url strings.
// One assertion was rewritten: `PreferVisible_NoPreviewFallback…` used to keep the visible 300 forever when the
// same-art 640 landed (0.3 dropped 0.2.9's largest-rendition enrich because it assumed decodePx selected size).
// The stored URL IS the rendition, so PreferVisible must upgrade a larger same-art id and never downgrade.

using Wavee;
using Xunit;
using Breakpoints = Wavee.Detail.Breakpoints;
using CoverLatch = Wavee.Detail.CoverLatch;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailCoverStabilityTests
{
    // ── 2a: the page-width estimate the pre-measure mode seed uses instead of the raw window viewport ───────────────

    [Fact]
    public void EstimatePageWidthFromViewport_SubtractsTheShellChromeAllowance()
    {
        Assert.Equal(560f, Breakpoints.EstimatePageWidthFromViewport(560f + Breakpoints.ShellChromeAllowanceDip));
        Assert.Equal(0f, Breakpoints.EstimatePageWidthFromViewport(0f));
        Assert.Equal(0f, Breakpoints.EstimatePageWidthFromViewport(Breakpoints.ShellChromeAllowanceDip - 10f));
    }

    /// <summary>The allowance is the nav pane's own token (240), not a restated literal.</summary>
    [Fact]
    public void ShellChromeAllowance_IsTheNavPaneToken()
        => Assert.Equal(Design.Size.NavPaneW, Breakpoints.ShellChromeAllowanceDip);

    [Fact]
    public void InitialModeForViewport_WindowWidthAndPageWidthDisagree_StraddlingTheVerticalBreakpoint()
    {
        // A 799-DIP window seeds mode 1 from the raw viewport, but the page is ~240 narrower (559): below the 560
        // vertical threshold. Composing mode 1 there is the wrong-wide first frame that remounts the hero.
        const float windowWidth = 799f;
        float pageWidthEstimate = Breakpoints.EstimatePageWidthFromViewport(windowWidth);
        Assert.Equal(559f, pageWidthEstimate);

        int fromWindow = Breakpoints.InitialModeForViewport(windowWidth);
        int fromPage = Breakpoints.InitialModeForViewport(pageWidthEstimate);

        Assert.Equal(1, fromWindow);
        Assert.Equal(Breakpoints.VerticalMode, fromPage);
        Assert.NotEqual(fromWindow, fromPage);
    }

    [Fact]
    public void InitialModeForViewport_PageWidthEstimate_NeverPicksAWiderModeThanTheWindowReading()
    {
        for (float w = 300f; w <= 1400f; w += 4f)
        {
            int fromWindow = Breakpoints.InitialModeForViewport(w);
            int fromPage = Breakpoints.InitialModeForViewport(Breakpoints.EstimatePageWidthFromViewport(w));
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
        => Assert.Equal(256, VerticalLayout.ArtworkDecodePx(artSize, widthMeasured: false));

    [Theory]
    [InlineData(1f, 256)]
    [InlineData(128f, 256)]     // boundary: <= 128 → 256
    [InlineData(129f, 512)]
    [InlineData(288f, 512)]     // boundary: <= 288 → 512
    [InlineData(289f, 1024)]
    [InlineData(1024f, 1024)]
    public void ArtworkDecodePx_Measured_UsesTheUnchangedSizeLadder(float artSize, int expected)
    {
        Assert.Equal(expected, VerticalLayout.ArtworkDecodePx(artSize, widthMeasured: true));
        Assert.Equal(VerticalLayout.ArtworkDecodePx(artSize), VerticalLayout.ArtworkDecodePx(artSize, widthMeasured: true));
    }

    [Fact]
    public void PlaylistHeaderCoverDecode_IsAtLeastThePaintedSizeAndTheArtworkLadder()
    {
        Assert.Equal(512, Playlist.HeaderCoverDecodePx(232f, 1f));
        Assert.True(Playlist.HeaderCoverDecodePx(232f, 2f) >= 512);
        Assert.True(Playlist.HeaderCoverDecodePx(232f, 2f) >= Design.ImageDecodeScale.For(232f, 2f));
    }

    [Fact]
    public void PreferVisible_KeepsTheSharperId_WhenIdentityIsAlreadyKnown()
    {
        string? visible = CoverLatch.PreferVisible(Thumb64, null);
        Assert.Equal(Hero640, CoverLatch.PreferVisible(Hero640, visible));
        Assert.Equal(Hero640, CoverLatch.PreferVisible(Thumb64, Hero640));
    }

    // ── 2d.1: SameArt / PreferVisible over mosaics + the no-preview fallback latch ─────────────────────────────────

    // Real id shapes: 40 hex = 16-char size/kind marker + 24-char art identity.
    const string Art = "a149cc5f2c8074884fc06a80";
    const string Thumb64 = "https://i.scdn.co/image/ab67616d00004851" + Art;
    const string Card300 = "https://i.scdn.co/image/ab67616d00001e02" + Art;
    const string Hero640 = "https://i.scdn.co/image/ab67616d0000b273" + Art;
    const string OtherArt = "https://i.scdn.co/image/ab67616d0000b27392144c5952844a7c0086b141";

    [Fact]
    public void SameArt_MosaicToMosaic_MatchesTheIdenticalTileSet()
    {
        string[] tiles = [Card300, Hero640, "https://i.scdn.co/image/tile3", "https://i.scdn.co/image/tile4"];
        string[] copy = [.. tiles];   // a distinct array instance, same urls
        Assert.True(CoverLatch.SameArt(CoverLatch.Reduce("", tiles[0]), CoverLatch.Reduce("", copy[0])));
    }

    [Fact]
    public void SameArt_MosaicToMosaic_DifferentTileSetsDoNotMatch()
    {
        string[] a = [Card300, Hero640];
        string[] b = [OtherArt, Hero640];
        Assert.False(CoverLatch.SameArt(CoverLatch.Reduce("", a[0]), CoverLatch.Reduce("", b[0])));
    }

    [Fact]
    public void SameArt_MosaicReducesToItsLeadTile_MatchesASingleCoverOfThatTile()
    {
        string[] tiles = [Card300, Hero640];
        string? mosaic = CoverLatch.Reduce("", tiles[0]);
        const string single = Card300;
        Assert.True(CoverLatch.SameArt(mosaic, single));
        Assert.True(CoverLatch.SameArt(single, mosaic));   // symmetric
    }

    [Fact]
    public void SameArt_MosaicLeadTile_MatchesADifferentSizeRenditionOfThatTile()
    {
        string? mosaic = CoverLatch.Reduce("", Card300);
        Assert.True(CoverLatch.SameArt(mosaic, Hero640));
    }

    [Fact]
    public void SameArt_MosaicLeadTile_DoesNotMatchAnUnrelatedCover()
    {
        string? mosaic = CoverLatch.Reduce("", Card300);
        Assert.False(CoverLatch.SameArt(mosaic, OtherArt));
    }

    /// <summary>A cover's own url wins over its lead tile; a blank url falls to the tile.</summary>
    [Fact]
    public void Reduce_PrefersTheCoverUrlAndFallsToTheLeadTile()
    {
        Assert.Equal(OtherArt, CoverLatch.Reduce(OtherArt, Card300));
        Assert.Equal(Card300, CoverLatch.Reduce("", Card300));
        Assert.Equal(Card300, CoverLatch.Reduce("  ", Card300));
        Assert.Equal(Card300, CoverLatch.Reduce(null, Card300));
    }

    /// <summary>Null or blank on either side is never the same art; a <c>spotify:image:</c> token normalizes to its CDN url.</summary>
    [Fact]
    public void SameArt_BlankIsNeverTheSameArt_AndProviderTokensNormalize()
    {
        Assert.False(CoverLatch.SameArt(null, Card300));
        Assert.False(CoverLatch.SameArt("", ""));
        Assert.False(CoverLatch.SameArt(null, null));
        Assert.True(CoverLatch.SameArt("spotify:image:ab67616d00001e02" + Art, Hero640));
    }

    [Fact]
    public void PreferVisible_MosaicVsSingleTile_UpgradesToTheLargerLeadArt()
    {
        const string visibleSingle = Card300;
        string? incomingMosaic = CoverLatch.Reduce("", Hero640);

        string? chosen = CoverLatch.PreferVisible(incomingMosaic, visibleSingle);

        Assert.NotNull(chosen);
        Assert.Equal(incomingMosaic, chosen);   // same art, larger prefix → take the 640 mosaic lead
    }

    [Fact]
    public void PreferVisible_NoPreviewFallback_UpgradesTheLastPublishedCoverTo640()
    {
        // The no-preview cover latch (deep link / search hit): `previewCover ?? lastPublished`.
        string? preview = null;
        const string lastPublished = Card300;
        string? fallback = preview ?? lastPublished;
        const string loaded = Hero640;

        string? chosen = CoverLatch.PreferVisible(loaded, fallback);

        Assert.NotNull(chosen);
        Assert.Equal(loaded, chosen);   // same art, larger prefix → take the 640 incoming
    }

    [Fact]
    public void PreferVisible_ALater64_DoesNotReplaceAVisible640()
    {
        string? visible = CoverLatch.PreferVisible(Hero640, null);
        Assert.Equal(Hero640, visible);
        visible = CoverLatch.PreferVisible(Thumb64, visible);
        Assert.Equal(Hero640, visible);
    }

    [Fact]
    public void PreferVisible_NoPreviewAndNoLastCover_TakesTheLoadedCoverOutright()
    {
        string? preview = null;
        string? lastPublished = null;
        string? fallback = preview ?? lastPublished;
        string loaded = Card300;

        Assert.Same(loaded, CoverLatch.PreferVisible(loaded, fallback));
    }

    [Fact]
    public void PreferVisible_NoPreviewFallback_StillTakesIncoming_WhenItIsGenuinelyDifferentArt()
    {
        string? preview = null;
        const string lastPublished = Card300;
        string? fallback = preview ?? lastPublished;
        string loaded = OtherArt;

        Assert.Same(loaded, CoverLatch.PreferVisible(loaded, fallback));
    }

    /// <summary>Only one side usable ⇒ that side; an unresolved provider token is not usable.</summary>
    [Fact]
    public void PreferVisible_OnlyOneSideUsable_TakesThatSide()
    {
        Assert.Equal(Card300, CoverLatch.PreferVisible(null, Card300));
        Assert.Equal(Card300, CoverLatch.PreferVisible("spotify:image:unresolved", Card300));
        Assert.Equal(Hero640, CoverLatch.PreferVisible(Hero640, "  "));
        Assert.False(CoverLatch.IsUsable("spotify:image:" + Art));
        Assert.True(CoverLatch.IsUsable(Card300));
    }

    // ── 2e: the artist hero's avatar → header hand-off (ch 08 BUG E) ────────────────────────────────────────────────
    //
    // The card that launched the navigation carries the avatar (`Image`) first; once the artist's own overview lands,
    // `HeroImageId` (Header ?? Image) may swap to the HEADER. The page latches whichever url is on screen through
    // `Detail.CoverLatch.PreferVisible`, exactly like Playlist.Page.cs's `_visibleCover` — these pin the three outcomes
    // that latch must produce for the hero specifically.

    [Fact]
    public void ArtistHero_ImageThenHeader_SameArtIdentity_UpgradesToTheLargerRendition()
    {
        string? visible = CoverLatch.PreferVisible(Card300, null);   // the avatar (Image) mounts first
        Assert.Equal(Card300, visible);

        // The overview lands and offers Hero640 — the SAME 24-char identity, a larger size prefix — so the hero
        // must take the sharper hash rather than keep the 300 it decoded from the card.
        visible = CoverLatch.PreferVisible(Hero640, visible);
        Assert.Equal(Hero640, visible);
    }

    [Fact]
    public void ArtistHero_ImageThenHeader_GenuinelyDifferentIdentity_TakesTheIncomingHeader()
    {
        string? visible = CoverLatch.PreferVisible(Card300, null);
        Assert.Equal(Card300, visible);

        // The header is a real, different banner photo (a distinct 24-char identity) — the hero must update.
        visible = CoverLatch.PreferVisible(OtherArt, visible);
        Assert.Equal(OtherArt, visible);
    }

    [Fact]
    public void ArtistHero_EmptyIncoming_KeepsTheVisibleImage()
    {
        string? visible = CoverLatch.PreferVisible(Card300, null);
        Assert.Equal(Card300, visible);

        // The overview lands but this artist carries no header image (HeaderId empty ⇒ HeroImageId falls back to the
        // same Image again, or the field simply is not known yet) — an EMPTY incoming must never blank a photo that
        // is already on screen (the "unmount to a flat placeholder" half of the bug).
        visible = CoverLatch.PreferVisible(null, visible);
        Assert.Equal(Card300, visible);
    }
}
