// ── Wavee.Tests/Theming/WinUiStageTests.cs — WinUI's stage, pure (P6 W4b) ───────────────────────────────────────────────────────────────
//
// What needs no mounted tree: the metrics the core allocator budgets from (the 196 / 104 / 112 / 128 transport at 0 / 16 / 24 / 32, the lyrics type),
// the layouts each window tier allows (W-Tiers: Compact = Card, Small = Card | Large art, Standard and Huge every layout), every tier's record
// valid and visible at rest (bar, card), and Compact the sheet. The mounted facts are the stage renders under `C:\WAVEE\scratch\w4b`.

using Wavee.Themes.WinUi;
using Wavee.Theming.Contracts;
using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class WinUiStageTests
{
    [Fact]
    public void The_policy_budgets_the_boards_transport_per_tier()
    {
        var p = WinUiStageTiers.Policy;
        Assert.Equal(196f, p.Compact!.TransportH);
        Assert.Equal(104f, p.Small!.TransportH);
        Assert.Equal(112f, p.Metrics.TransportH);
        Assert.Equal(128f, p.Huge!.TransportH);
        Assert.Equal(new[] { 0f, 16f, 24f, 32f }, new[] { p.Compact.TransportInset, p.Small.TransportInset, p.Metrics.TransportInset, p.Huge.TransportInset });
        Assert.All(new[] { p.Compact, p.Small, p.Metrics, p.Huge }, m => Assert.Empty(m.Validate("WinUi")));
    }

    [Fact]
    public void Each_window_tier_allows_the_layouts_the_tiers_board_names()
    {
        var p = WinUiStageTiers.Policy;
        Assert.Equal(StageLayouts.Card, p.Compact!.Layouts);
        Assert.Equal(StageLayouts.Card | StageLayouts.LargeArt, p.Small!.Layouts);
        Assert.Equal(StageLayouts.All, p.Metrics.Layouts);
        Assert.Equal(StageLayouts.All, p.Huge!.Layouts);
    }

    [Fact]
    public void The_gallery_opens_only_on_request_at_every_tier()
    {
        var p = WinUiStageTiers.Policy;
        Assert.All(new[] { p.Compact!, p.Small!, p.Metrics, p.Huge! }, m => Assert.True(m.GalleryOnDemand));
    }

    [Fact]
    public void Huge_docks_the_up_next_column_and_grows_the_lyrics()
    {
        var h = WinUiStageTiers.Policy.Huge!;
        Assert.Equal(WinUiStageTiers.UpNextW, h.UpNextW);
        Assert.True(h.LyricsTypeMax > h.LyricsTypeMin);
        Assert.Equal(0f, WinUiStageTiers.Policy.Small!.UpNextW);
    }

    [Theory]
    [InlineData(WidthTier.Compact, true)]
    [InlineData(WidthTier.Small, false)]
    [InlineData(WidthTier.Standard, false)]
    [InlineData(WidthTier.Huge, false)]
    public void Only_Compact_is_the_sheet(WidthTier tier, bool sheet) => Assert.Equal(sheet, WinUiStageTiers.Stage(tier).Sheet);

    [Fact]
    public void Compact_targets_are_at_least_40_and_the_pill_is_glyph_only()
    {
        var c = WinUiStageTiers.Stage(WidthTier.Compact);
        Assert.True(c.PillIcons);
        Assert.True(c.PillItemH >= 40f && c.BarButtonW >= 40f && c.BarButtonH >= 40f && c.ButtonBox >= 40f && c.DeviceH >= 40f);
        Assert.True(c.PillItemH >= 40f);
    }

    [Fact]
    public void Wider_than_Huge_reads_Huge_and_a_missing_tier_reads_Standard()
    {
        Assert.Same(WinUiStageTiers.Stage(WidthTier.Huge), WinUiStageTiers.Stage((WidthTier)99));
        Assert.Equal(WidthTier.Standard, WinUiStageTiers.StageOf(default).Tier);
    }
}
