// ── Wavee.Tests/Theming/StageTiersTests.cs — the stage family's tiers (themes plan 3.5; TC3) ──────────────────────────────────────────
//
// Pure: the stage's class ladders moved out of the allocator (`ClassicStageTiers`, plan 3.1's "stage aspect classes"), their holds at every
// edge, the boards' sizes in the boards' classes, the per-window-tier metrics the allocator budgets from (the transport card, the lyrics
// type), the 330-tall stage that keeps its art, title and controls apart, and the view records (the stage's, the panel contents'). The
// regions the tiers add: the gallery down to the foot with the card beside it (C-Stage-Huge), the Up next column and the bars under the
// cover (C-Stage-Huge4K, from the 3200 rung), the Compact window's identity card, face chip and gallery-on-request (C-Stage-Compact,
// C-Lyrics-Compact), the Huge transport card's padding and gaps, and the video title row's actions (C-Video-Huge).

using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

using A = Wavee.Stage.Aspect;
using L = Wavee.Stage.Layout;
using M = Wavee.Stage.Mode;

namespace Wavee.Tests.Theming;

public sealed class StageTiersTests
{
    static readonly AspectThresholds Classic = ClassicStageTiers.Aspect;

    // ── the ladders ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_ladders_are_the_structures_and_the_core_baseline_mirrors_them()
    {
        Assert.Same(ClassicStageTiers.Policy, ClassicPolicies.Stage);
        Assert.Equal(StagePolicy.Default, ClassicStageTiers.Policy);
        Assert.Equal(AspectThresholds.Default, Classic);
        Assert.Empty(ClassicStageTiers.Policy.Validate());
        Assert.Equal(1, Classic.Width.EdgeCount);
        Assert.Equal(1, Classic.Height.EdgeCount);
        Assert.Equal(2, Classic.Ratio.EdgeCount);
        Assert.Equal(600f, Classic.Width.EdgeAt(0).At);
        // the inclusive lower bands sit one float above the board value; their holds land exactly on the leave values
        var height = Classic.Height.EdgeAt(0);
        Assert.Equal(MathF.BitIncrement(460f), height.At);
        Assert.Equal(484f, height.At + height.WidenHold);
        var portrait = Classic.Ratio.EdgeAt(0);
        Assert.Equal(0.85f, portrait.At + portrait.WidenHold);
        var ultra = Classic.Ratio.EdgeAt(1);
        Assert.Equal(2.00f, ultra.At);
        Assert.Equal(MathF.BitIncrement(1.95f), ultra.At - ultra.NarrowHold);
    }

    [Fact]
    public void Each_edge_holds_the_way_the_stage_always_did()
    {
        // height: Compact at or below 460 (inclusive), held below 484 on the way up (W = 900 keeps the ratio under 2)
        Assert.Equal(A.Compact, L.ClassOf(900f, 460f, null, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(900f, 461f, null, Classic));
        Assert.Equal(A.Compact, L.ClassOf(900f, 483f, A.Compact, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(900f, 484f, A.Compact, Classic));
        // ratio: Portrait at or below 0.80, held below 0.85
        Assert.Equal(A.Portrait, L.ClassOf(800f, 1000f, null, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(801f, 1000f, null, Classic));
        Assert.Equal(A.Portrait, L.ClassOf(849f, 1000f, A.Portrait, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(850f, 1000f, A.Portrait, Classic));
        // ratio: Ultrawide from 2.00, held while above 1.95
        Assert.Equal(A.Ultrawide, L.ClassOf(2000f, 1000f, null, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(1999f, 1000f, A.Desktop, Classic));
        Assert.Equal(A.Ultrawide, L.ClassOf(1951f, 1000f, A.Ultrawide, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(1950f, 1000f, A.Ultrawide, Classic));
        // width: under 600 a landscape stage is Compact and any other Portrait, with no hold
        Assert.Equal(A.Compact, L.ClassOf(599f, 590f, null, Classic));
        Assert.Equal(A.Compact, L.ClassOf(599f, 590f, A.Desktop, Classic));
        Assert.Equal(A.Portrait, L.ClassOf(599f, 600f, null, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(600f, 600f, A.Compact, Classic));
    }

    [Fact]
    public void A_drag_across_each_edge_flips_once_per_crossing()
    {
        // H = 400 (every width a landscape-short Compact) is no use; H = 1000 sweeps Portrait | Desktop | Ultrawide, H = 700 crosses 600.
        foreach (float h in new[] { 700f, 1000f })
        {
            var cur = L.Seed(300f, h);
            int up = 0;
            for (float w = 300f; w <= 2600f; w += 1f) { var n = L.Resolve(w, h, cur); if (n.Aspect != cur.Aspect) up++; cur = n; }
            int down = 0;
            for (float w = 2600f; w >= 300f; w -= 1f) { var n = L.Resolve(w, h, cur); if (n.Aspect != cur.Aspect) down++; cur = n; }
            Assert.Equal(up, down);
            Assert.InRange(up, 2, 3);
        }
    }

    // ── the boards' sizes in the boards' classes ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 860f, A.Portrait)]    // C-Stage-Compact, C-Lyrics-Compact: a column with the pane
    [InlineData(300f, 640f, A.Portrait)]    // the floor
    [InlineData(300f, 330f, A.Portrait)]    // the floor at the minimum height: narrow and not landscape
    [InlineData(800f, 330f, A.Compact)]     // the short window
    [InlineData(800f, 600f, A.Desktop)]     // C-Stage-Small, C-Lyrics-Small
    [InlineData(1440f, 900f, A.Desktop)]    // C-Stage, C-Lyrics
    [InlineData(1440f, 960f, A.Desktop)]
    [InlineData(2560f, 1440f, A.Desktop)]   // C-Stage-Huge, C-Lyrics-Huge, C-StageFaces-Huge
    [InlineData(3840f, 2160f, A.Desktop)]   // C-Stage-Huge4K, C-StageFaces-Huge4K
    public void The_boards_sizes_land_in_the_boards_classes(float w, float h, A expected)
        => Assert.Equal(expected, L.ClassOf(w, h, null, Classic));

    // ── the tier metrics ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_window_tier_reads_its_metrics()
    {
        var p = ClassicStageTiers.Policy;
        Assert.Same(ClassicStageTiers.CompactMetrics, p.MetricsFor(WidthTier.Compact));
        Assert.Same(ClassicStageTiers.SmallMetrics, p.MetricsFor(WidthTier.Small));
        Assert.Same(ClassicStageTiers.StandardMetrics, p.MetricsFor(WidthTier.Standard));
        Assert.Same(ClassicStageTiers.HugeMetrics, p.MetricsFor(WidthTier.Huge));
        Assert.Same(ClassicStageTiers.HugeMetrics, p.MetricsFor(WidthTier.Huge4K));
        Assert.Same(p.Metrics, p.MetricsFor(TierFacts.Unknown));   // no frame resolved the tiers: Standard's
        Assert.Equal(StageTierMetrics.Default, ClassicStageTiers.StandardMetrics);   // Standard is the tuned stage, unchanged
    }

    [Theory]
    [InlineData(400f, 860f, WidthTier.Compact, 26f)]    // C-Lyrics-Compact 26/33
    [InlineData(800f, 600f, WidthTier.Small, 30f)]      // C-Lyrics-Small 30/38
    [InlineData(1440f, 900f, WidthTier.Standard, 36f)]  // C-Lyrics 36/46 (the tuned stage)
    [InlineData(1920f, 1080f, WidthTier.Huge, 36f)]     // Auto zoom's 4K-at-100 % layout: a 1084 pane reads the authored size
    [InlineData(2560f, 1440f, WidthTier.Huge, 48f)]     // C-Lyrics-Huge 48/60
    [InlineData(3840f, 2160f, WidthTier.Huge, 48f)]     // C-Stage-Huge4K 48/60 (Standard's 64 ceiling would read 64)
    public void The_lyrics_type_is_the_boards_at_each_tier(float w, float h, WidthTier tier, float size)
    {
        var l = L.Seed(w, h, Classic, ClassicStageTiers.Policy.MetricsFor(tier));
        Assert.Equal(size, l.LyricsTypeSize);
        Assert.Equal(size / L.LyricsTypeMin, l.LyricsTypeScale);   // the view scales the authored 36 rows by it
    }

    [Fact]
    public void The_transport_card_is_the_tiers()
    {
        var compact = L.Seed(400f, 860f, Classic, ClassicStageTiers.CompactMetrics);
        Assert.Equal(104f, compact.TransportH);
        Assert.Equal(16f, compact.TransportInset);
        Assert.Equal(16f, compact.TransportLeft);
        Assert.Equal(368f, compact.TransportW);              // C-Stage-Compact: the card at 16 either side
        Assert.True(compact.TransportStacked, "a 400-wide card stacks to heart · previous · play · next · more");
        Assert.True(compact.TransportW >= L.TransportStackedMinW);
        Assert.True(compact.ShowPane, "the 400 x 860 stage keeps its pane (C-Lyrics-Compact)");

        var small = L.Seed(800f, 600f, Classic, ClassicStageTiers.SmallMetrics);
        Assert.Equal(96f, small.TransportH);                 // C-Stage-Small: the 96 card
        Assert.Equal(96f + 16f + L.PaneAboveTransport, small.PaneBottom);
        Assert.Equal(small.H - 16f - 96f, small.TransportTop);

        var standard = L.Seed(1440f, 960f, Classic, ClassicStageTiers.StandardMetrics);
        Assert.Equal(L.TransportDesktopH, standard.TransportH);
        Assert.Equal(168f, standard.PaneBottom);             // the tuned pane foot, unchanged
        Assert.False(standard.TransportStacked);
        Assert.Equal(L.Seed(1440f, 960f), standard);         // Standard's metrics are the default resolve's
    }

    // ── the narrow and short stage: art, title and controls, never overlapping ──────────────────────────────────────────

    [Theory]
    [InlineData(300f, 330f, WidthTier.Compact)]
    [InlineData(400f, 330f, WidthTier.Compact)]
    [InlineData(300f, 640f, WidthTier.Compact)]
    [InlineData(400f, 860f, WidthTier.Compact)]
    [InlineData(527f, 760f, WidthTier.Compact)]
    [InlineData(800f, 330f, WidthTier.Small)]
    [InlineData(800f, 600f, WidthTier.Small)]
    [InlineData(1440f, 960f, WidthTier.Standard)]
    [InlineData(2560f, 1440f, WidthTier.Huge)]
    [InlineData(3840f, 2160f, WidthTier.Huge)]
    public void The_stage_keeps_its_art_title_and_controls_apart(float w, float h, WidthTier tier)
    {
        var l = L.Seed(w, h, Classic, ClassicStageTiers.Policy.MetricsFor(tier));
        var mode = M.Lyrics;
        var top = new RectF(0f, 0f, w, L.TopBarH);
        var art = new RectF(l.CoverX(mode), l.CoverY(mode), l.CoverSize(mode), l.CoverSize(mode));
        var title = new RectF(l.TitleX(mode), l.TitleY(mode), l.TitleW(mode), l.TitleBlockH(mode, l.TitleMaxLines(mode)));
        var transport = new RectF(l.TransportLeft, l.TransportTop, l.TransportW, l.TransportH);
        var regions = new List<(string, RectF)> { ("top bar", top), ("art", art), ("title", title), ("transport", transport) };
        if (l.ShowPane) regions.Add(("pane", new RectF(l.PaneX, l.PaneTop, l.PaneW, l.PaneH)));
        foreach (var (name, r) in regions)
        {
            Assert.True(r.W > 0f && r.H > 0f, $"{w}x{h}: the {name} is empty ({r})");
            Assert.True(r.X >= -0.5f && r.X + r.W <= w + 0.5f && r.Y >= -0.5f && r.Y + r.H <= h + 0.5f, $"{w}x{h}: the {name} leaves the window ({r})");
        }
        for (int i = 0; i < regions.Count; i++)
            for (int j = i + 1; j < regions.Count; j++)
            {
                var (an, a) = regions[i];
                var (bn, b) = regions[j];
                if ((an, bn) is ("art", "title")) continue;   // the stacked classes put the title under the art, the row classes beside it: checked below
                bool apart = a.X + a.W <= b.X + 0.5f || b.X + b.W <= a.X + 0.5f || a.Y + a.H <= b.Y + 0.5f || b.Y + b.H <= a.Y + 0.5f;
                Assert.True(apart, $"{w}x{h} ({l.Aspect}): the {an} {a} overlaps the {bn} {b}");
            }
        bool artTitleApart = art.X + art.W <= title.X + 0.5f || art.Y + art.H <= title.Y + 0.5f;
        Assert.True(artTitleApart, $"{w}x{h}: the title {title} overlaps the art {art}");
        if (!l.CaptionFits(mode)) Assert.True(l.IdentityIsRow, "only a row-identity stage drops the Lyrics caption");
    }

    [Fact]
    public void The_short_stage_parks_or_drops_what_does_not_fit()
    {
        var floor = L.Seed(300f, 330f, Classic, ClassicStageTiers.CompactMetrics);
        Assert.False(floor.ShowPane, "no room for a pane at 330 tall");
        Assert.False(floor.CaptionFits(M.Lyrics), "no room for the caption under the art at 330 tall");
        var narrow = L.Seed(400f, 330f, Classic, ClassicStageTiers.CompactMetrics);
        Assert.Equal(A.Compact, narrow.Aspect);
        Assert.True(narrow.TransportUnder, "a 400-wide short stage parks the card under the art");
        Assert.Equal(narrow.TransportInset, narrow.TransportLeft);
        Assert.False(narrow.CaptionFits(M.Lyrics));

        var shortWide = L.Seed(800f, 330f, Classic, ClassicStageTiers.SmallMetrics);
        Assert.False(shortWide.TransportUnder, "the 800-wide short stage keeps the card beside the art");
        Assert.True(shortWide.TransportStacked, "the strip beside the art is too narrow for the three clusters");
        Assert.True(L.Seed(1100f, 440f).CaptionFits(M.Lyrics), "the prototype's short stage keeps its caption beside the art");
        Assert.True(L.Seed(1920f, 1080f).CaptionFits(M.Lyrics));
    }

    // ── the view records ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_stage_and_panel_records_follow_the_window_tier()
    {
        Assert.Equal(40f, ClassicStageTiers.Stage(WidthTier.Compact).BarTargetH);
        Assert.Equal(48f, ClassicStageTiers.Stage(WidthTier.Small).PlayDiscMax);   // C-Stage-Small
        Assert.Equal(88f, ClassicStageTiers.Stage(WidthTier.Small).VolumeW);
        Assert.Equal(56f, ClassicStageTiers.Stage(WidthTier.Standard).PlayDiscMax);
        Assert.Same(ClassicStageTiers.Stage(WidthTier.Huge), ClassicStageTiers.Stage(WidthTier.Huge4K));
        Assert.Equal(WidthTier.Standard, ClassicStageTiers.StageOf(TierFacts.Unknown).Tier);
        foreach (WidthTier t in Enum.GetValues<WidthTier>())
        {
            Assert.Equal((WidthTier)Math.Min((int)t, (int)WidthTier.Huge), ClassicStageTiers.Stage(t).Tier);
            Assert.Equal((WidthTier)Math.Min((int)t, (int)WidthTier.Huge), ClassicStageTiers.Panel(t).Tier);
        }

        var compact = ClassicStageTiers.Panel(WidthTier.Compact);
        Assert.Equal(40f, compact.AtLeast(26f));
        Assert.True(compact.HeartBox >= 40f && compact.CloseBox >= 40f && compact.MediaGlyphBox >= 40f, "Compact targets are at least 40");
        Assert.True(compact.MediaChromeAtRest);
        var standard = ClassicStageTiers.Panel(WidthTier.Standard);
        Assert.Equal(26f, standard.AtLeast(26f));
        Assert.True(float.IsNaN(standard.MinTarget));
        Assert.False(standard.MediaChromeAtRest);

        Assert.Equal(ClassicQueueRules.HeaderExtent, ClassicQueueRules.HeaderExtentFor(standard.Target));
        Assert.Equal(ClassicQueueRules.HeaderSubExtent, ClassicQueueRules.HeaderSubExtentFor(standard.Target));
        Assert.Equal(ClassicQueueRules.HeaderExtent + 20f, ClassicQueueRules.HeaderExtentFor(compact.Target));   // the eyebrow row grows 20 to 40
    }

    [Fact]
    public void The_transport_card_and_the_video_title_row_follow_the_huge_boards()
    {
        var huge = ClassicStageTiers.Stage(WidthTier.Huge);
        Assert.Equal(new Edges4(24f, 14f, 24f, 0f), huge.TransportPadding);   // C-Stage-Huge: padding 14 24
        Assert.Equal(12f, huge.ClusterGap);                                    // 12 between the centred buttons
        Assert.Equal(8f, huge.RightGap);                                       // 8 in the right cluster
        var standard = ClassicStageTiers.Stage(WidthTier.Standard);
        Assert.Equal(new Edges4(20f, 10f, 20f, 0f), standard.TransportPadding);   // the tuned card, unchanged
        Assert.Null(standard.ClusterGap);
        Assert.Equal(6f, standard.RightGap);

        Assert.True(ClassicStageTiers.Panel(WidthTier.Huge).VideoTitleActions, "C-Video-Huge: the heart and the ... on the title row");
        Assert.False(ClassicStageTiers.Panel(WidthTier.Standard).VideoTitleActions);
        Assert.False(ClassicStageTiers.Panel(WidthTier.Small).VideoTitleActions);
        Assert.False(ClassicStageTiers.Panel(WidthTier.Compact).VideoTitleActions);
        Assert.Equal(32f, ClassicStageTiers.VideoTitleButton);
    }

    // ── Huge: the gallery runs to the foot, the card beside it (C-Stage-Huge) ────────────────────────────────────────────

    [Fact]
    public void The_huge_gallery_runs_to_the_foot_and_the_card_yields_beside_it()
    {
        var huge = L.Seed(2560f, 1440f, Classic, ClassicStageTiers.HugeMetrics);
        Assert.Equal(1440f - L.GalleryTop - 24f, huge.GalleryH);        // top 64, bottom 24 (the board's aside)
        Assert.Equal(24f, huge.GalleryBottomInset);
        Assert.Equal(516f, huge.TransportRight(galleryShown: true));     // the board's card stops 516 from the right edge
        Assert.Equal(24f, huge.TransportRight(galleryShown: false));     // the full-width card when the gallery is closed
        Assert.Equal(huge.W - huge.FaceRight(true), huge.W - huge.TransportRight(true) + 24f);   // the card and the face share the inset

        var standard = L.Seed(1440f, 960f, Classic, ClassicStageTiers.StandardMetrics);
        Assert.Equal(960f - L.GalleryTop - L.GalleryBottom, standard.GalleryH);   // the tuned column, clear of the full-width card
        Assert.Equal(L.GalleryBottom, standard.GalleryBottomInset);
        Assert.Equal(24f, standard.TransportRight(galleryShown: true));
    }

    // ── Huge 4K: the Up next column and the bars under the cover (C-Stage-Huge4K) ──────────────────────────────────────

    [Theory]
    [InlineData(2560f, false)]
    [InlineData(3199f, false)]
    [InlineData(3200f, true)]
    [InlineData(3840f, true)]
    public void A_huge_window_reads_the_4K_stage_metrics_from_3200(float width, bool wide)
    {
        var facts = WindowTiers.Resolve(ClassicTierFixture.Tiers, width, 2160f, width - 320f);
        Assert.True(facts.Window >= WidthTier.Huge);
        Assert.Same(wide ? ClassicStageTiers.HugeWideMetrics : ClassicStageTiers.HugeMetrics, ClassicStageTiers.Policy.MetricsFor(facts));
        Assert.Equal(wide, ClassicStageTiers.Policy.IsHugeWide(facts));
    }

    [Fact]
    public void The_4K_metrics_are_hugeplus_the_column_and_the_bars()
    {
        var wide = ClassicStageTiers.HugeWideMetrics;
        Assert.Equal(ClassicStageTiers.HugeMetrics with { UpNextW = 520f, ArtBarsH = 140f }, wide);
        Assert.Equal(ClassicDetailTiers.HugeWideRung, ClassicStageTiers.Policy.HugeWideRung);
        Assert.Empty(ClassicStageTiers.Policy.Validate());
        Assert.NotEmpty((ClassicStageTiers.Policy with { HugeWideRung = null }).Validate());   // a wide twin needs the rung it is read from
        Assert.Equal(0f, ClassicStageTiers.HugeMetrics.UpNextW);
        Assert.Equal(0f, ClassicStageTiers.HugeMetrics.ArtBarsH);
    }

    [Fact]
    public void The_4K_lyrics_stage_docks_up_next_beside_the_lyrics_and_bars_under_the_cover()
    {
        var l = L.Seed(3840f, 2160f, Classic, ClassicStageTiers.HugeWideMetrics);
        Assert.True(l.UpNextShown(M.Lyrics));
        Assert.False(l.UpNextShown(M.Queue), "the Up next mode's pane IS the queue");
        Assert.False(l.UpNextShown(M.Visualizer));
        Assert.False(l.UpNextShown(M.Artist));
        var up = l.UpNextRect;
        Assert.Equal(3840f - 24f - 520f, up.X);       // right 24, 520 wide
        Assert.Equal(64f, up.Y);                       // top 64
        Assert.Equal(520f, up.W);
        Assert.Equal(160f, 2160f - (up.Y + up.H));     // bottom 160: a gutter above the 112 card at 24

        var lyrics = l.PaneRect(Stage.Look.Plain(M.Lyrics));
        Assert.True(lyrics.X + lyrics.W <= up.X - L.UpNextGap + 0.5f, $"the lyrics {lyrics} run into the Up next column at {up.X}");
        Assert.True(lyrics.W > 0f);
        Assert.Equal(l.PaneW, l.PaneRect(Stage.Look.Plain(M.Queue)).W);   // the other panes keep the whole region
        Assert.Equal(48f, l.LyricsTypeSize);                                // 48/60 on the board

        var bars = l.ArtBarsRect(M.Lyrics);
        Assert.True(l.ArtBarsShown(M.Lyrics));
        Assert.False(l.ArtBarsShown(M.Queue));
        Assert.Equal(140f, bars.H);                    // the board's 140
        Assert.Equal(l.PadX, bars.X);                  // under the cover, the cover's width
        Assert.Equal(l.HeroArt, bars.W);
        Assert.True(bars.Y >= l.TitleY(M.Lyrics) + l.TitleBlockH(M.Lyrics, 2), "the bars sit under the two-line title budget");
        Assert.True(bars.Y + bars.H <= l.TransportTop - L.Pad, "the bars clear the transport card");
        Assert.True(bars.X + bars.W <= lyrics.X, "the bars stay in the cover's column");

        var huge = L.Seed(3840f, 2160f, Classic, ClassicStageTiers.HugeMetrics);   // under the rung: no column, no bars
        Assert.False(huge.UpNextShown(M.Lyrics));
        Assert.False(huge.ArtBarsShown(M.Lyrics));
        Assert.Equal(huge.PaneW, huge.PaneRect(Stage.Look.Plain(M.Lyrics)).W);
    }

    [Fact]
    public void The_bars_under_the_cover_raise_the_lyrics_stage_lease_to_the_spectrum()
    {
        Assert.Equal(Visualizer.Tier.Spectrum, Visualizer.Demand.WithArtBars(Visualizer.Tier.Level, artBars: true));
        Assert.Equal(Visualizer.Tier.Level, Visualizer.Demand.WithArtBars(Visualizer.Tier.Level, artBars: false));
        Assert.Equal(Visualizer.Tier.None, Visualizer.Demand.WithArtBars(Visualizer.Tier.None, artBars: true));   // the gates closed it
        Assert.Equal(Visualizer.Tier.Scope, Visualizer.Demand.WithArtBars(Visualizer.Tier.Scope, artBars: true));
    }

    // ── Compact: the identity card, the face chip, no gallery until asked (C-Stage-Compact, C-Lyrics-Compact) ──────────

    [Fact]
    public void The_compact_column_draws_the_identity_card_with_the_face_chip_under_it()
    {
        var l = L.Seed(400f, 860f, Classic, ClassicStageTiers.CompactMetrics);
        Assert.Equal(A.Portrait, l.Aspect);
        foreach (var mode in new[] { M.Lyrics, M.Visualizer, M.Queue, M.Artist })
        {
            Assert.True(l.IdentityCardOn(mode), $"{mode}: the Portrait column draws the card in every mode");
            Assert.Equal((16f, 56f, 368f, 72f), l.NowPlayingCardRect(mode));   // left 16, top 56, 368 x 72
            Assert.Equal(56f, l.CoverSize(mode));                               // the 56 cover at the card's 8 padding
            Assert.Equal(24f, l.CoverX(mode));
            Assert.Equal(64f, l.CoverY(mode));
            Assert.Equal(24f + 56f + 12f, l.TitleX(mode));                      // 12 from the cover
            Assert.Equal((16f, 22f), l.TitleFont(mode));                        // 16/22 over 13/18
            Assert.Equal((13f, 18f), l.MetaFont(mode));
            Assert.Equal(1, l.TitleMaxLines(mode));
            Assert.Equal(40f, l.TitleBlockH(mode, 1));
            Assert.Equal(72f, l.TitleY(mode));                                  // centred on the cover
            Assert.Equal(400f - 16f - 8f - 92f, l.TitleW(mode));                // to the card's padding
            Assert.False(l.ShowChipsFor(Stage.Look.Plain(mode)), "the card carries no chips");
            Assert.Equal(0f, l.TitleGapFor(Stage.Look.Plain(mode)));
        }
        Assert.True(l.FaceChipShown(M.Visualizer));
        Assert.False(l.FaceChipShown(M.Lyrics));
        Assert.Equal(140f, l.FaceChipY);                                         // the board's chip at top 140
        Assert.True(L.FaceChipH >= 40f, "the chip is a Compact target");
        Assert.Equal(136f, l.PaneTop);                                           // C-Lyrics-Compact: the lyrics from 136
        Assert.True(l.ShowPane);
        Assert.Equal(l.FaceChipY + L.FaceChipH + L.Pad, l.FaceSafe(galleryOpen: false, captionShown: false).Y);   // a face centres under the chip

        var narrowShort = L.Seed(500f, 400f, Classic, ClassicStageTiers.CompactMetrics);   // the landscape Compact class
        Assert.Equal(A.Compact, narrowShort.Aspect);
        Assert.True(narrowShort.IdentityCardOn(M.Visualizer), "the card stands where the 460 card would not fit");
        Assert.Equal(500f - 32f, narrowShort.NowPlayingCardRect(M.Visualizer).W);
        Assert.False(narrowShort.IdentityCardOn(M.Lyrics), "the Compact class keeps its art beside the card");

        var small = L.Seed(800f, 600f, Classic, ClassicStageTiers.SmallMetrics);
        Assert.False(small.IdentityCardOn(M.Visualizer));
        Assert.Equal((L.NowPlayingCardX, L.NowPlayingCardY, L.NowPlayingCardW, L.NowPlayingCardH), small.NowPlayingCardRect(M.Visualizer));
        Assert.False(small.FaceChipShown(M.Visualizer));
    }

    [Fact]
    public void The_compact_gallery_opens_only_when_asked()
    {
        var compact = L.Seed(400f, 860f, Classic, ClassicStageTiers.CompactMetrics);
        Assert.False(compact.GalleryOpenFor(stored: true, asked: false), "C-Stage-Compact shows no sheet by default");
        Assert.True(compact.GalleryOpenFor(stored: true, asked: true));
        Assert.False(compact.GalleryOpenFor(stored: false, asked: true));
        var standard = L.Seed(1440f, 960f, Classic, ClassicStageTiers.StandardMetrics);
        Assert.True(standard.GalleryOpenFor(stored: true, asked: false), "the stored flag stands on the wide tiers");
        Assert.False(standard.GalleryOpenFor(stored: false, asked: true));
    }
}
