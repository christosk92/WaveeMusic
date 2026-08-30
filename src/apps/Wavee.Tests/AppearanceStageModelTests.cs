using Xunit;

namespace Wavee.Tests;

// Appearance page 4's engine-free live-preview model (Features/Setup/AppearanceStageModel.cs) — drives the REAL
// Resolve() decision (source-included, never a copy), exactly like DetailVerticalLayoutTests/TrackRowStyleRulesTests
// drive the two production files it depends on.
public class AppearanceStageModelTests
{
    const float ThumbDip = 32f;
    const float ListHeight = 232f;

    static AppearanceStageModel.Inputs Baseline(
        int themeMode = 0, bool systemIsLight = false, string? paletteId = "neutral", bool baseMica = true,
        int trackRowStyle = 0, int density = 1, int detailPageLayout = 0, bool hideTrackArtwork = false,
        bool disableColorWashes = false, bool disableMarquee = false, bool lyricsAnimatedBackdrop = true,
        bool detailPageToneHeroOnly = false)
        => new(themeMode, systemIsLight, paletteId, baseMica, trackRowStyle, density, detailPageLayout,
            hideTrackArtwork, disableColorWashes, disableMarquee, lyricsAnimatedBackdrop, detailPageToneHeroOnly);

    // ── RowHeight ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void RowHeight_IsRowHeightForTimesScale(int density, bool classic)
    {
        var r = AppearanceStageModel.Resolve(Baseline(density: density, trackRowStyle: classic ? 1 : 0), ThumbDip, ListHeight);
        Assert.Equal(DetailTrackTableRules.RowHeightFor(density, classic) * AppearanceStageModel.Scale, r.RowHeight, precision: 4);
    }

    // ── ArtEdge ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArtEdge_IsZero_WhenArtworkHidden()
    {
        var r = AppearanceStageModel.Resolve(Baseline(hideTrackArtwork: true), ThumbDip, ListHeight);
        Assert.Equal(0f, r.ArtEdge);
    }

    [Fact]
    public void ArtEdge_IsZero_WhenClassic()
    {
        var r = AppearanceStageModel.Resolve(Baseline(trackRowStyle: 1), ThumbDip, ListHeight);
        Assert.Equal(0f, r.ArtEdge);
    }

    [Fact]
    public void ArtEdge_IsThumbScaled_WhenModernAndArtworkShown()
    {
        var r = AppearanceStageModel.Resolve(Baseline(trackRowStyle: 0, hideTrackArtwork: false), ThumbDip, ListHeight);
        Assert.Equal(ThumbDip * AppearanceStageModel.Scale, r.ArtEdge);
    }

    // ── TwoLineRows / Classic ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_HasSingleLineRows()
    {
        var r = AppearanceStageModel.Resolve(Baseline(trackRowStyle: 1), ThumbDip, ListHeight);
        Assert.True(r.Classic);
        Assert.False(r.TwoLineRows);
    }

    [Fact]
    public void Modern_HasTwoLineRows()
    {
        var r = AppearanceStageModel.Resolve(Baseline(trackRowStyle: 0), ThumbDip, ListHeight);
        Assert.False(r.Classic);
        Assert.True(r.TwoLineRows);
    }

    // ── NormalizePalette ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("warm")]
    [InlineData("slate")]
    [InlineData("neutral")]
    [InlineData("accent")]
    public void NormalizePalette_RoundTripsEveryKnownId(string id)
        => Assert.Equal(id, AppearanceStageModel.NormalizePalette(id));

    [Fact]
    public void NormalizePalette_FallsBackToNeutral_ForAnUnknownOrMissingId()
    {
        Assert.Equal("neutral", AppearanceStageModel.NormalizePalette("bogus"));
        Assert.Equal("neutral", AppearanceStageModel.NormalizePalette(null));
    }

    // ── IsDark ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsDark_System_FollowsTheOsFlag()
    {
        Assert.True(AppearanceStageModel.IsDark(0, systemIsLight: false));
        Assert.False(AppearanceStageModel.IsDark(0, systemIsLight: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsDark_LightMode_IsAlwaysLight_RegardlessOfSystem(bool systemIsLight)
        => Assert.False(AppearanceStageModel.IsDark(1, systemIsLight));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsDark_DarkMode_IsAlwaysDark_RegardlessOfSystem(bool systemIsLight)
        => Assert.True(AppearanceStageModel.IsDark(2, systemIsLight));

    // ── Tint ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DisableColorWashes_ZeroesTheTint()
    {
        var r = AppearanceStageModel.Resolve(Baseline(disableColorWashes: true), ThumbDip, ListHeight);
        Assert.Equal(0f, r.TintAlpha);
    }

    [Fact]
    public void BaseMicaFalse_IsMicaAlt_WithALargerTint()
    {
        var baseMica = AppearanceStageModel.Resolve(Baseline(baseMica: true), ThumbDip, ListHeight);
        var micaAlt = AppearanceStageModel.Resolve(Baseline(baseMica: false), ThumbDip, ListHeight);

        Assert.False(baseMica.MicaAlt);
        Assert.True(micaAlt.MicaAlt);
        Assert.True(micaAlt.TintAlpha > baseMica.TintAlpha);
    }

    // ── RowCount ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RowCount_IsNonIncreasing_AsDensityGrows()
    {
        int? previous = null;
        for (int density = 0; density <= 3; density++)
        {
            var r = AppearanceStageModel.Resolve(Baseline(density: density), ThumbDip, ListHeight);
            Assert.True(r.RowCount >= 1);
            if (previous is { } p) Assert.True(r.RowCount <= p);
            previous = r.RowCount;
        }
    }

    // ── HeroLayout ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HeroLayout_FollowsDetailPageLayout()
    {
        Assert.False(AppearanceStageModel.Resolve(Baseline(detailPageLayout: 0), ThumbDip, ListHeight).HeroLayout);
        Assert.True(AppearanceStageModel.Resolve(Baseline(detailPageLayout: 1), ThumbDip, ListHeight).HeroLayout);
    }
}
