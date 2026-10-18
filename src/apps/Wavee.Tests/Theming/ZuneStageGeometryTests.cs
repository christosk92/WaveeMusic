// ── Wavee.Tests/Theming/ZuneStageGeometryTests.cs — Zune's stage policy, tier records and arrangements (P5 Z4b, tiers ZT3) ─────────────
//
// Pure, against the boards: the full-bleed plate the allocator budgets from at every tier (152 / 96 / 112 / 120 / 280, Huge 4K from the
// stage's own rung), the stage record per window tier (the boards' gutters and heads, Compact's 40 targets, the discs), the plate's form
// per tier (Full, HeartLead, the Compact Stack, the stacked fallback), the Card arrangement at 1440 x 900 (Z-NowPlaying-Card) and at Huge
// and Huge 4K (Z-NowPlaying-Huge: the 860 card at 64 / 816 with the display title at 52 / 96), the Large art column at Standard and Small
// (Z-NowPlaying-LargeArt, Z-NowPlaying-Small), the Centered arrangement (Z-NowPlaying-Centered: the 360 cover, the 76 foot), the Compact
// stack (Z-NowPlaying-Compact: the 304 cover) and the fallbacks to the allocator's arrangement.

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

    static L SeedAt(float w, float h, StageTierMetrics metrics) => L.Seed(w, h, ZuneStageTiers.Policy.Aspect, metrics);

    static Stage.Look CardLook => Stage.Look.Plain(M.Visualizer);

    static Stage.Look LargeArt(Stage.SpectrumStyle spectrum = Stage.SpectrumStyle.Off) => new(M.Visualizer, Stage.VizLayout.LargeArt, spectrum, false, false);

    static Stage.Look Centered(Stage.SpectrumStyle spectrum = Stage.SpectrumStyle.Off) => new(M.Visualizer, Stage.VizLayout.Centered, spectrum, false, false);

    [Fact]
    public void The_policy_budgets_the_full_bleed_plate_per_tier()
    {
        var policy = ZuneStageTiers.Policy;
        Assert.Empty(policy.Validate());
        Assert.Equal(AspectThresholds.Default, policy.Aspect);
        Assert.Equal(152f, policy.MetricsFor(WidthTier.Compact).TransportH);
        Assert.Equal(96f, policy.MetricsFor(WidthTier.Small).TransportH);
        Assert.Equal(112f, policy.MetricsFor(WidthTier.Standard).TransportH);
        Assert.Equal(120f, policy.MetricsFor(WidthTier.Huge).TransportH);
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
            Assert.Equal(0f, policy.MetricsFor(tier).TransportInset);
        Assert.True(policy.MetricsFor(WidthTier.Compact).IdentityCard);
        Assert.Equal(440f, policy.MetricsFor(WidthTier.Huge).UpNextW);

        // Huge 4K: the 280 plate from the stage's own rung (3200, held 24), through the core's HugeWide
        Assert.Equal(ZuneTiers.StageHuge4KRung, policy.HugeWideRung);
        Assert.True(ZuneTiers.Policy.IndexOfRung(ZuneTiers.StageHuge4KRung) >= 0);
        var at4K = WindowTiers.Resolve(ZuneTiers.Policy, 3840f, 2160f, 3840f);
        Assert.Equal(WidthTier.Huge4K, at4K.Window);
        Assert.Equal(280f, policy.MetricsFor(in at4K).TransportH);
        var atHuge = WindowTiers.Resolve(ZuneTiers.Policy, 2560f, 1440f, 2560f);
        Assert.Equal(120f, policy.MetricsFor(in atHuge).TransportH);
        var atEdge = WindowTiers.Resolve(ZuneTiers.Policy, 3200f, 1440f, 3200f);
        Assert.Equal(280f, policy.MetricsFor(in atEdge).TransportH);
        var below = WindowTiers.Resolve(ZuneTiers.Policy, 3199f, 1440f, 3199f);
        Assert.Equal(120f, policy.MetricsFor(in below).TransportH);

        var layout = Seed(1440f, 900f);
        Assert.Equal(900f - 112f, layout.TransportTop);   // the allocator clears the plate exactly
        Assert.Equal(1440f, layout.TransportW);
    }

    [Fact]
    public void The_stage_record_follows_the_boards()
    {
        float[] gutters = [16f, 24f, 48f, 64f, 96f];
        float[] heads = [96f, 48f, 48f, 56f, 72f];
        float[] discs = [56f, 48f, 56f, 64f, 96f];
        for (var tier = WidthTier.Compact; tier <= WidthTier.Huge4K; tier++)
        {
            var record = ZuneStageTiers.Stage(tier);
            Assert.Equal(tier, record.Tier);
            Assert.Equal(gutters[(int)tier], record.Gutter);
            Assert.Equal(heads[(int)tier], record.HeadH);
            Assert.Equal(discs[(int)tier], record.Disc);
            Assert.True(record.TargetH >= 36f);
        }
        var compact = ZuneStageTiers.Stage(WidthTier.Compact);
        Assert.Equal(40f, compact.TargetH);
        Assert.True(compact.GlyphBox >= 40f && compact.DeviceH >= 40f && compact.PivotRowH >= 40f);
        Assert.Equal(ZunePlateKind.Stack, compact.Plate);
        Assert.NotNull(compact.Stack);
        Assert.Equal(ZunePlateKind.HeartLead, ZuneStageTiers.Stage(WidthTier.Small).Plate);
        Assert.True(ZuneStageTiers.Stage(WidthTier.Small).PivotsLead);
        Assert.Equal(0f, Standard.Drop);
        Assert.Equal(20f, Standard.PivotSize);
        Assert.Equal(24f, Standard.PivotGap);
        Assert.Equal(48f, Standard.HeadH);
        Assert.Equal(160f, ZuneStageTiers.Stage(WidthTier.Huge4K).PlateRowH);
        Assert.Equal(Standard, ZuneStageTiers.StageOf(TierFacts.Unknown));
    }

    [Fact]
    public void The_plate_takes_its_tiers_form_and_stacks_where_it_does_not_fit()
    {
        var wide = Seed(1440f, 900f);
        Assert.Equal(56f, ZuneStageGeometry.DiscFor(Standard, ZuneStageGeometry.PlateRowFor(in wide, Standard, ZunePlateForm.Full)));
        Assert.Equal((ZunePlateForm.Full, true), ZuneStageGeometry.PlateFit(in wide, Standard));

        var small = ZuneStageTiers.Stage(WidthTier.Small);
        var s = SeedAt(800f, 600f, ZuneStageTiers.SmallMetrics);
        Assert.Equal(ZunePlateForm.HeartLead, ZuneStageGeometry.PlateFit(in s, small).Form);

        var compact = ZuneStageTiers.Stage(WidthTier.Compact);
        var tall = SeedAt(400f, 860f, ZuneStageTiers.CompactMetrics);
        Assert.Equal(Stage.Aspect.Portrait, tall.Aspect);
        Assert.Equal(ZunePlateForm.Stack, ZuneStageGeometry.PlateFit(in tall, compact).Form);
        var shortCompact = SeedAt(600f, 400f, ZuneStageTiers.CompactMetrics);
        Assert.Equal(Stage.Aspect.Compact, shortCompact.Aspect);
        Assert.Equal(ZunePlateForm.Stacked, ZuneStageGeometry.PlateFit(in shortCompact, compact).Form);

        var k4 = ZuneStageTiers.Stage(WidthTier.Huge4K);
        var big = SeedAt(3840f, 2160f, ZuneStageTiers.Huge4KMetrics);
        Assert.Equal(280f, big.TransportH);
        Assert.Equal((ZunePlateForm.Full, true), ZuneStageGeometry.PlateFit(in big, k4));
        Assert.Equal(160f, ZuneStageGeometry.PlateRowFor(in big, k4, ZunePlateForm.Full));
        Assert.Equal(96f, ZuneStageGeometry.DiscFor(k4, 160f));
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
        Assert.True(a.Display.IsEmpty, "no display title at Standard");

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
    public void The_huge_card_is_the_boards_with_its_display_title()
    {
        var huge = ZuneStageTiers.Stage(WidthTier.Huge);
        var layout = SeedAt(2560f, 1440f, ZuneStageTiers.HugeMetrics);
        var look = CardLook;
        var a = ZuneStageGeometry.Arrange(in layout, in look, huge, galleryShown: false);
        Assert.Equal(ZuneStageForm.Card, a.Form);
        Assert.Equal(new ZuneRect(64f, 816f, 860f, 560f), a.Card);          // Z-NowPlaying-Huge: left 64, bottom 64, 860 wide
        Assert.Equal(new ZuneRect(96f, 848f, 360f, 360f), a.Cover);
        Assert.Equal(64f, a.TitleSize);
        Assert.Equal(72f, a.Voice!.Disc);
        Assert.Equal(52f, a.Display.X);                                      // the display title at 52 / 96
        Assert.Equal(96f, a.Display.Y);
        Assert.Equal(150f, a.DisplaySize);
        Assert.True(a.Display.Bottom <= a.Card.Y, "the display title clears the card");
        Assert.Equal(a.Card.Bottom, a.Controls.Bottom + 32f);

        // a short Huge stage drops the display title, then the Huge card for Standard's, before the plate
        var shortHuge = SeedAt(2560f, 900f, ZuneStageTiers.HugeMetrics);
        var s = ZuneStageGeometry.Arrange(in shortHuge, in look, huge, galleryShown: false);
        Assert.Equal(ZuneStageForm.Card, s.Form);
        Assert.True(s.Display.IsEmpty || s.Display.Bottom <= s.Card.Y);

        var k4 = ZuneStageTiers.Stage(WidthTier.Huge4K);
        var big = SeedAt(3840f, 2160f, ZuneStageTiers.Huge4KMetrics);
        var b = ZuneStageGeometry.Arrange(in big, in look, k4, galleryShown: false);
        Assert.Equal(ZuneStageForm.Card, b.Form);
        Assert.Equal(96f, b.Card.X);
        Assert.Equal(2160f - 96f, b.Card.Bottom);
        Assert.Equal(96f, b.Voice!.Disc);
        Assert.Equal(480f, b.Cover.W);
        Assert.False(b.Display.IsEmpty);
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
        Assert.True(a.Aside.IsEmpty, "the Standard column carries its volume row");

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
    public void The_small_column_is_the_boards()
    {
        var small = ZuneStageTiers.Stage(WidthTier.Small);
        var layout = SeedAt(800f, 600f, ZuneStageTiers.SmallMetrics);
        var look = LargeArt();
        var a = ZuneStageGeometry.Arrange(in layout, in look, small, galleryShown: false);
        Assert.Equal(ZuneStageForm.Column, a.Form);
        Assert.Equal(new ZuneRect(24f, 64f, 360f, 360f), a.Cover);        // Z-NowPlaying-Small: the 360 cover at 24 / 64
        Assert.Equal(416f, a.Titles.X);                                      // the column at 416, 360 wide
        Assert.Equal(360f, a.Titles.W);
        Assert.Equal(64f, a.TitleSize);
        Assert.False(a.Aside.IsEmpty, "Small's mute · volume · device sit under the block");
        Assert.True(a.Controls.Bottom <= a.Aside.Y, "the aside never overlaps the controls");
        Assert.True(a.Aside.Bottom <= 600f - small.Gutter + 0.5f);
        Assert.True(a.Pane.H > 0f && a.Pane.Bottom <= a.Titles.Y);
    }

    [Fact]
    public void The_centered_arrangement_is_the_boards()
    {
        var layout = Seed(1440f, 900f);
        var look = Centered();
        var a = ZuneStageGeometry.Arrange(in layout, in look, Standard, galleryShown: false);
        Assert.Equal(ZuneStageForm.Centered, a.Form);
        Assert.Equal(360f, a.Cover.W);                                       // Z-NowPlaying-Centered: the 360 cover, centred
        Assert.Equal((1440f - 360f) * 0.5f, a.Cover.X);
        Assert.Equal(new ZuneRect(0f, 900f - 76f, 1440f, 76f), a.Foot);     // the 76 gradient foot
        Assert.True(a.Cover.Y >= Standard.HeadH, "the cover clears the bar");
        Assert.True(a.Titles.Y >= a.Cover.Bottom);
        Assert.True(a.Controls.Y >= a.Titles.Bottom);
        Assert.True(a.Controls.Bottom <= a.Foot.Y, "the controls stay above the foot");
        Assert.Equal(640f, a.Controls.W);

        var ring = Centered(Stage.SpectrumStyle.Ring);
        var r = ZuneStageGeometry.Arrange(in layout, in ring, Standard, false);
        Assert.Equal(ZuneStageForm.Centered, r.Form);
        Assert.True(r.Spectrum.W > r.Cover.W, "the ring's box wraps the cover");
        Assert.True(r.Controls.Bottom <= r.Foot.Y);

        // Huge: the larger cover and voice
        var huge = ZuneStageTiers.Stage(WidthTier.Huge);
        var hugeLayout = SeedAt(2560f, 1440f, ZuneStageTiers.HugeMetrics);
        var h = ZuneStageGeometry.Arrange(in hugeLayout, in look, huge, false);
        Assert.Equal(ZuneStageForm.Centered, h.Form);
        Assert.True(h.Cover.W > a.Cover.W);
        Assert.Equal(96f, h.Foot.H);
    }

    [Fact]
    public void The_compact_stack_is_the_boards()
    {
        var compact = ZuneStageTiers.Stage(WidthTier.Compact);
        var layout = SeedAt(400f, 860f, ZuneStageTiers.CompactMetrics);
        var look = CardLook;
        var a = ZuneStageGeometry.Arrange(in layout, in look, compact, galleryShown: false);
        Assert.Equal(ZuneStageForm.Stack, a.Form);
        Assert.Equal(new ZuneRect(16f, 108f, 304f, 304f), a.Cover);       // Z-NowPlaying-Compact: the 304 cover under the 96 head
        Assert.Equal(16f, a.Titles.X);
        Assert.True(a.Titles.Y >= a.Cover.Bottom);
        Assert.True(a.Source.Bottom <= a.Controls.Y, "the titles never run into the controls");
        Assert.Equal(860f - 16f, a.Controls.Bottom);
        Assert.Equal(64f, a.Voice!.Disc);
        Assert.True(a.Voice.Box >= 40f && a.Voice.AsideH >= 40f, "Compact targets are at least 40");
        Assert.False(ZuneStageGeometry.CaptionAllowed(in a));

        // too short for the stack: the allocator's identity card and the plate
        var floor = SeedAt(300f, 330f, ZuneStageTiers.CompactMetrics);
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in floor, in look, compact, false).Form);
        // another mode or layout on a Compact window: the plate
        var lyrics = Stage.Look.Plain(M.Lyrics);
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in layout, in lyrics, compact, false).Form);
        var large = LargeArt();
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in layout, in large, compact, false).Form);
    }

    [Fact]
    public void The_compact_head_never_overlaps_the_plate_at_the_floor()
    {
        var compact = ZuneStageTiers.Stage(WidthTier.Compact);
        var floor = SeedAt(300f, 330f, ZuneStageTiers.CompactMetrics);
        Assert.True(compact.HeadH <= floor.TransportTop, "the head and the plate never overlap at 300 x 330");
        var huge4K = ZuneStageTiers.Stage(WidthTier.Huge4K);
        var big = SeedAt(3840f, 2160f, ZuneStageTiers.Huge4KMetrics);
        Assert.True(huge4K.HeadH <= big.TransportTop);
        Assert.Equal(24f, huge4K.Drop);
        Assert.Equal(48f, compact.Drop);
        // a rect the allocator hangs under its bar moves down with the head; one in the stage's middle does not
        Assert.Equal(48f, ZuneStageGeometry.DropFor(Stage.Layout.TopBarH + 8f, compact));
        Assert.Equal(0f, ZuneStageGeometry.DropFor(400f, compact));
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
        var artist = new Stage.Look(M.Visualizer, Stage.VizLayout.Artist, Stage.SpectrumStyle.Bars, false, false);
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in layout, in artist, Standard, false).Form);
        var shortStage = Seed(1440f, 600f);
        var large = LargeArt();
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in shortStage, in large, Standard, false).Form);
        var compact = Seed(1000f, 420f);
        Assert.Equal(Stage.Aspect.Compact, compact.Aspect);
        var card = CardLook;
        Assert.Equal(ZuneStageForm.Plate, ZuneStageGeometry.Arrange(in compact, in card, Standard, false).Form);
    }

    // ── the plate stage on the tier's gutter (T4): the identity, the card and the pane ───────────────────────────────────

    static StageTierMetrics MetricsOf(WidthTier tier) => tier switch
    {
        WidthTier.Compact => ZuneStageTiers.CompactMetrics,
        WidthTier.Small => ZuneStageTiers.SmallMetrics,
        WidthTier.Standard => ZuneStageTiers.StandardMetrics,
        WidthTier.Huge => ZuneStageTiers.HugeMetrics,
        _ => ZuneStageTiers.Huge4KMetrics,
    };

    /// <summary>A window of each tier whose stage is a Desktop class (the hero cover left, the pane beside it).</summary>
    public static TheoryData<WidthTier, float, float> DesktopStages() => new()
    {
        { WidthTier.Small, 800f, 600f }, { WidthTier.Standard, 1440f, 900f }, { WidthTier.Huge, 2560f, 1440f }, { WidthTier.Huge4K, 3840f, 2160f },
    };

    /// <summary>A window of each tier whose stage is Portrait (the identity over the pane): the Compact board's 400 x 860 and twice the width tall.</summary>
    public static TheoryData<WidthTier, float, float> PortraitStages() => new()
    {
        { WidthTier.Compact, 400f, 860f }, { WidthTier.Small, 800f, 1600f }, { WidthTier.Standard, 1440f, 2880f }, { WidthTier.Huge, 2560f, 5120f },
        { WidthTier.Huge4K, 3840f, 7680f },
    };

    [Theory, MemberData(nameof(DesktopStages))]
    public void A_desktop_plate_puts_the_art_and_the_titles_on_the_tier_gutter_and_the_pane_keeps_its_gap(WidthTier tier, float w, float h)
    {
        var record = ZuneStageTiers.Stage(tier);
        var layout = SeedAt(w, h, MetricsOf(tier));
        Assert.Equal(Stage.Aspect.Desktop, layout.Aspect);
        var look = Stage.Look.Plain(M.Lyrics);

        // the allocator's 5.8 % pad (44 / 80 / 148 at 800 / 1440 / 2560) is not the art's x: the record's gutter is
        float shift = ZuneStageGeometry.EdgeShift(in layout, in look, record);
        Assert.Equal(record.Gutter, layout.CoverX(in look) + shift, 0.01f);
        Assert.Equal(record.Gutter, layout.TitleX(in look) + shift, 0.01f);
        Assert.Equal(layout.TitleW(in look), ZuneStageGeometry.PlateTitleWidth(in layout, in look, record, shift), 0.01f);

        // the pane moves with the art: the gap between them is the allocator's, its right edge is the lyrics column's room
        var pane = ZuneStageGeometry.PlatePane(in layout, in look, record, Stage.PaneKind.Lyrics);
        float gap = layout.PaneX - (layout.CoverX(in look) + layout.HeroArt);
        Assert.Equal(record.Gutter + layout.HeroArt + gap, pane.X, 0.01f);
        Assert.True(pane.Right <= layout.PaneX + layout.PaneW + 0.01f, "the pane never runs past the allocator's right edge");
        var queue = ZuneStageGeometry.PlatePane(in layout, Stage.Look.Plain(M.Queue), record, Stage.PaneKind.Queue);
        Assert.Equal(pane.X, queue.X, 0.01f);   // the queue and the artist pane stand where the lyrics do: no pane slides on a mode switch
    }

    [Theory, MemberData(nameof(PortraitStages))]
    public void A_portrait_plate_puts_the_identity_and_the_lyrics_lead_on_the_tier_gutter(WidthTier tier, float w, float h)
    {
        var record = ZuneStageTiers.Stage(tier);
        var layout = SeedAt(w, h, MetricsOf(tier));
        Assert.Equal(Stage.Aspect.Portrait, layout.Aspect);
        var look = Stage.Look.Plain(M.Lyrics);

        // the cover (Compact: the identity strip's cover, the allocator's 8) and its titles
        float shift = ZuneStageGeometry.EdgeShift(in layout, in look, record);
        Assert.Equal(record.Gutter, layout.CoverX(in look) + shift, 0.01f);
        float titleX = layout.TitleX(in look) + shift;
        float titleW = ZuneStageGeometry.PlateTitleWidth(in layout, in look, record, shift);
        Assert.True(titleX > record.Gutter && titleW > 0f, "the titles sit right of the cover");
        Assert.True(titleX + titleW <= w + 0.01f, "the titles stay inside the stage");
        if (layout.IdentityCardOn(M.Lyrics)) Assert.Equal(w - record.Gutter, titleX + titleW, 0.01f);   // the strip's titles run to the gutter at the right

        // the lyrics' TEXT starts on the gutter (Compact: 16, not 28 + 22 = 50); the queue's and the artist pane's edge is the gutter itself
        var lyrics = ZuneStageGeometry.PlatePane(in layout, in look, record, Stage.PaneKind.Lyrics);
        Assert.Equal(record.Gutter, lyrics.X + ZuneStageGeometry.LyricsLead(in layout), 0.01f);
        Assert.Equal(record.LyricsPaneMaxW > 0f ? MathF.Min(layout.PaneX + layout.PaneW, lyrics.X + record.LyricsPaneMaxW) : layout.PaneX + layout.PaneW, lyrics.Right, 0.01f);   // the allocator's right edge, or the tier's reading cap (Huge: 900)
        var queue = ZuneStageGeometry.PlatePane(in layout, Stage.Look.Plain(M.Queue), record, Stage.PaneKind.Queue);
        Assert.Equal(record.Gutter, queue.X, 0.01f);
        Assert.Equal(lyrics.Y, queue.Y, 0.01f);
        Assert.Equal(record.Drop, lyrics.Y - layout.PaneTop, 0.01f);   // under the tier's head
    }

    [Fact]
    public void The_compact_strip_and_the_lyrics_both_start_at_16()
    {
        var compact = ZuneStageTiers.Stage(WidthTier.Compact);
        var layout = SeedAt(400f, 860f, ZuneStageTiers.CompactMetrics);
        var look = Stage.Look.Plain(M.Lyrics);
        Assert.Equal(8f, layout.CoverX(in look));   // before: the strip's cover at 8
        Assert.Equal(16f, layout.CoverX(in look) + ZuneStageGeometry.EdgeShift(in layout, in look, compact));
        Assert.Equal(22f, ZuneStageGeometry.LyricsLead(in layout));   // the rail's gutter at the Compact stage's 26 type
        Assert.Equal(28f + 22f, layout.PaneX + ZuneStageGeometry.LyricsLead(in layout));   // before: the lyrics at 50
        var pane = ZuneStageGeometry.PlatePane(in layout, in look, compact, Stage.PaneKind.Lyrics);
        Assert.Equal(16f, pane.X + ZuneStageGeometry.LyricsLead(in layout));
    }

    [Fact]
    public void The_460_card_stands_on_the_tier_gutter_and_a_layout_look_is_the_allocators()
    {
        var layout = Seed(1440f, 900f);
        var card = CardLook;
        Assert.Equal(Standard.Gutter, Stage.Layout.NowPlayingCardX + ZuneStageGeometry.EdgeShift(in layout, in card, Standard));
        var huge = ZuneStageTiers.Stage(WidthTier.Huge);
        Assert.Equal(huge.Gutter, Stage.Layout.NowPlayingCardX + ZuneStageGeometry.EdgeShift(in layout, in card, huge));

        var large = LargeArt();
        Assert.Equal(0f, ZuneStageGeometry.EdgeShift(in layout, in large, Standard));
        var pane = ZuneStageGeometry.PlatePane(in layout, in large, Standard, Stage.PaneKind.Lyrics);
        var (x, y, pw, ph) = layout.PaneRect(in large);
        Assert.Equal(new ZuneRect(x, y + Standard.Drop, pw, ph - Standard.Drop), pane);
    }

    [Fact]
    public void Lyrics_mode_seats_the_identity_column_beside_the_lines_at_each_tier()
    {
        var look = Stage.Look.Plain(M.Lyrics);

        // Z-Lyrics (1440 x 900): the 300 cover at (48, 192), the lines' text 80 right of it, running to the gutter
        var std = SeedAt(1440f, 900f, ZuneStageTiers.StandardMetrics);
        var s = ZuneStageGeometry.LyricsSide(in std, in look, Standard)!.Value;
        Assert.Equal(new ZuneRect(48f, 192f, 300f, 300f), s.Cover);
        Assert.Equal(48f + 300f + 80f, s.Pane.X + ZuneStageGeometry.LyricsLead(in std), 0.01f);
        Assert.Equal(std.W - Standard.Gutter, s.Pane.Right, 0.01f);
        Assert.Equal(56f, std.LyricsTypeSize);   // the board's 56/68 at 300

        // Z-Lyrics-Huge (2560 x 1440): the 440 cover under the 56 head, the pane stops the tier's gap short of the Up next column, uncapped
        var huge = ZuneStageTiers.Stage(WidthTier.Huge);
        var hl = SeedAt(2560f, 1440f, ZuneStageTiers.HugeMetrics);
        var h = ZuneStageGeometry.LyricsSide(in hl, in look, huge)!.Value;
        Assert.Equal(new ZuneRect(64f, 96f, 440f, 440f), h.Cover);
        Assert.True(hl.UpNextShown(M.Lyrics));
        Assert.Equal(ZuneStageGeometry.UpNextPaneX(in hl, huge) - huge.UpNextGap, h.Pane.Right, 0.01f);
        Assert.Equal(56f, hl.LyricsTypeSize);

        // Z-Lyrics-Small (800 x 600): the 180 cover centred between the head and the plate
        var small = ZuneStageTiers.Stage(WidthTier.Small);
        var sl = SeedAt(800f, 600f, ZuneStageTiers.SmallMetrics);
        var m = ZuneStageGeometry.LyricsSide(in sl, in look, small)!.Value;
        Assert.Equal(180f, m.Cover.W);
        Assert.Equal(24f, m.Cover.X);
        Assert.Equal(36f, sl.LyricsTypeSize);

        // another mode, and Compact's identity card, keep the allocator's rects
        Assert.Null(ZuneStageGeometry.LyricsSide(in std, Stage.Look.Plain(M.Queue), Standard));
        Assert.Null(ZuneStageGeometry.LyricsSide(SeedAt(400f, 860f, ZuneStageTiers.CompactMetrics), in look, ZuneStageTiers.Stage(WidthTier.Compact)));
    }
}
