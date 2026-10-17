// ── Wavee.Tests/Theming/ZuneStageGeometryTests.cs — Zune's stage policy, tier records and arrangements (P5 Z4b) ───────────────────────
//
// Pure, against the boards: the full-bleed 112 plate the allocator budgets from (gutter 0 on the desktop tiers), the stage record per window
// tier (the frame's gutters, Compact's 40 targets, the 56 disc of the Standard board), the Card arrangement at 1440 x 900
// (Z-NowPlaying-Card: the 600 x 376 card at 48 / 476, the 220 cover, the aside at the bottom-right gutter), the Large art column
// (Z-NowPlaying-LargeArt: the 756 cover at 48 / 96, the 532 column at 860, the foot block from 484, the 120 title), and the fallbacks to
// the allocator's arrangement (beside an open gallery on a narrow stage, too short, another mode, the Compact class).

using FluentGpu.Foundation;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Xunit;

using L = Wavee.Stage.Layout;
using M = Wavee.Stage.Mode;
using Wavee.Theming.Contracts;

namespace Wavee.Tests.Theming;

public sealed class ZuneStageGeometryTests
{
    static ZuneStageLayout Standard => ZuneStageTiers.Stage(WidthTier.Standard);

    static L Seed(float w, float h) => L.Seed(w, h, ZuneStageTiers.Policy.Aspect, ZuneStageTiers.StandardMetrics);

    static Stage.Look CardLook => Stage.Look.Plain(M.Visualizer);

    static Stage.Look LargeArt(Stage.SpectrumStyle spectrum = Stage.SpectrumStyle.Off) => new(M.Visualizer, Stage.VizLayout.LargeArt, spectrum, false, false);

    [Fact]
    public void The_policy_budgets_the_full_bleed_plate()
    {
        var policy = ZuneStageTiers.Policy;
        Assert.Empty(policy.Validate());
        Assert.Equal(AspectThresholds.Default, policy.Aspect);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var m = policy.MetricsFor(tier);
            Assert.Equal(112f, m.TransportH);
            Assert.Equal(0f, m.TransportInset);
        }
        Assert.True(policy.MetricsFor(WidthTier.Compact).IdentityCard);
        var layout = Seed(1440f, 900f);
        Assert.Equal(900f - 112f, layout.TransportTop);   // the allocator clears the plate exactly
        Assert.Equal(1440f, layout.TransportW);
    }

    [Fact]
    public void The_stage_record_follows_the_frame_and_the_boards()
    {
        for (var tier = WidthTier.Compact; tier <= WidthTier.Huge4K; tier++)
        {
            var record = ZuneStageTiers.Stage(tier);
            Assert.Equal(tier, record.Tier);
            Assert.Equal(ZuneTiers.Frame(tier).Gutter, record.Gutter);
        }
        Assert.Equal(40f, ZuneStageTiers.Stage(WidthTier.Compact).TargetH);
        Assert.Equal(40f, ZuneStageTiers.Stage(WidthTier.Compact).GlyphBox);
        Assert.Equal(56f, Standard.Disc);
        Assert.Equal(20f, Standard.PivotSize);
        Assert.Equal(24f, Standard.PivotGap);
        Assert.Equal(Standard, ZuneStageTiers.StageOf(TierFacts.Unknown));
    }

    [Fact]
    public void The_plate_seats_three_clusters_on_the_board_and_stacks_when_narrow()
    {
        var wide = Seed(1440f, 900f);
        float disc = ZuneStageGeometry.DiscFor(Standard, wide.TransportH - ZuneStageGeometry.SeekRowH);
        Assert.Equal(56f, disc);
        Assert.Equal((false, true), ZuneStageGeometry.PlateFit(in wide, Standard, disc));
        var small = ZuneStageTiers.Stage(WidthTier.Small);
        var narrow = L.Seed(900f, 700f, ZuneStageTiers.Policy.Aspect, ZuneStageTiers.SmallMetrics);
        Assert.True(ZuneStageGeometry.PlateFit(in narrow, small, ZuneStageGeometry.DiscFor(small, narrow.TransportH - ZuneStageGeometry.SeekRowH)).Stacked);
    }

    [Fact]
    public void The_card_arrangement_is_the_boards()
    {
        var layout = Seed(1440f, 900f);
        var look = CardLook;
        var a = ZuneStageGeometry.Arrange(in layout, in look, Standard, galleryShown: false);
        Assert.Equal(ZuneStageForm.Card, a.Form);
        Assert.Equal(new ZuneRect(48f, 476f, 600f, 376f), a.Card);
        Assert.Equal(new ZuneRect(72f, 500f, 220f, 220f), a.Cover);
        Assert.Equal(316f, a.Titles.X);
        Assert.Equal(a.Cover.Bottom, a.Source.Bottom);
        Assert.Equal(new ZuneRect(72f, 736f, 552f, 92f), a.Controls);
        Assert.Equal(new ZuneRect(1072f, 816f, 320f, 36f), a.Aside);
        Assert.Equal(a.Card.Bottom, a.Controls.Bottom + ZuneStageGeometry.CardPad);

        var safe = ZuneStageGeometry.FaceSafe(in layout, in a, galleryShown: false, captionShown: false, backdrop: false);
        Assert.True(safe.Bottom <= a.Card.Y, "the face's subject stays above the card");
        Assert.Equal(900f - 476f + Stage.Layout.Pad, ZuneStageGeometry.CaptionBottom(in layout, in a, 0f));

        // the card stays beside an open gallery on the board, and gives way to the allocator's on a narrow stage
        Assert.Equal(ZuneStageForm.Card, ZuneStageGeometry.Arrange(in layout, in look, Standard, galleryShown: true).Form);
        var narrow = Seed(1008f, 760f);
        Assert.Equal(ZuneStageForm.Card, ZuneStageGeometry.Arrange(in narrow, in look, Standard, galleryShown: false).Form);
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in narrow, in look, Standard, galleryShown: true).Form);
    }

    [Fact]
    public void The_large_art_column_is_the_boards()
    {
        var layout = Seed(1440f, 900f);
        var look = LargeArt();
        var a = ZuneStageGeometry.Arrange(in layout, in look, Standard, galleryShown: false);
        Assert.Equal(ZuneStageForm.Column, a.Form);
        Assert.Equal(new ZuneRect(48f, 96f, 756f, 756f), a.Cover);
        Assert.Equal(860f, a.Titles.X);
        Assert.Equal(532f, a.Titles.W);
        Assert.Equal(484f, a.Titles.Y);
        Assert.Equal(120f, a.TitleSize);
        Assert.Equal(new ZuneRect(860f, 668f, 532f, 184f), a.Controls);
        Assert.Equal(new ZuneRect(860f, 96f, 532f, 18f), a.Source);
        Assert.True(a.Pane.H >= ZuneStageGeometry.ColumnPaneMinH, "Large art keeps its lyrics pane");
        Assert.True(a.Pane.Bottom <= a.Titles.Y, "the pane stops above the foot block");

        // the ring: the cover shrinks into its box; the bars: under the cover
        var ring = LargeArt(Stage.SpectrumStyle.Ring);
        var r = ZuneStageGeometry.Arrange(in layout, in ring, Standard, false);
        Assert.Equal(ZuneStageForm.Column, r.Form);
        Assert.True(r.Cover.W < r.Spectrum.W);
        var bars = LargeArt(Stage.SpectrumStyle.Bars);
        var b = ZuneStageGeometry.Arrange(in layout, in bars, Standard, false);
        Assert.Equal(b.Cover.W, b.Spectrum.W);
        Assert.True(b.Spectrum.Y >= b.Cover.Bottom);
        Assert.True(b.Spectrum.Bottom <= 900f - Standard.Gutter + 0.5f);
    }

    [Fact]
    public void Every_other_look_and_class_is_the_allocators()
    {
        var layout = Seed(1440f, 900f);
        foreach (var mode in new[] { M.Lyrics, M.Queue, M.Artist })
        {
            var plain = Stage.Look.Plain(mode);
            Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in layout, in plain, Standard, false).Form);
        }
        var centered = new Stage.Look(M.Visualizer, Stage.VizLayout.Centered, Stage.SpectrumStyle.Bars, false, false);
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in layout, in centered, Standard, false).Form);
        var shortStage = Seed(1440f, 600f);
        var large = LargeArt();
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in shortStage, in large, Standard, false).Form);
        var compact = Seed(1000f, 420f);
        Assert.Equal(Stage.Aspect.Compact, compact.Aspect);
        var card = CardLook;
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in compact, in card, Standard, false).Form);
    }
}
