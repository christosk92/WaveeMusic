// ── Wavee.Tests/PageGeometryTests.cs — the page's frame numbers (Platform/Page.Rules.cs) ──────────────────────────────
//
// THE GUTTER IS THE ARTIST HERO'S TIER RULE. `PageGeometry.GutterFor(width, previous)` restates `ArtistHeroLayout.TierFor`
// in gutter terms, so the artist page (which reads its tier) and every other page (which reads `Shell.Ui.PageGutter`)
// can never sit 4 DIP apart in steady state. The equivalence test below proves it at every width and every previous tier.
//
// THE RHYTHM IS MEASURED. The views bar's 48 DIP is the SelectorBar's 40-DIP item plus the engine's own 0,4,0,4 container
// padding; the gaps the app adds absorb that padding so the PLATES sit where the design says.

using Xunit;

namespace Wavee.Tests;

public class PageGeometryGutterTests
{
    [Theory]
    [InlineData(320f, 16f)]
    [InlineData(599f, 16f)]
    [InlineData(600f, 32f)]
    [InlineData(879f, 32f)]
    [InlineData(880f, 36f)]
    [InlineData(1200f, 36f)]
    public void The_memoryless_gutter_steps_at_600_and_880(float width, float expected)
        => Assert.Equal(expected, PageGeometry.GutterFor(width));

    [Theory]
    [InlineData(860f, 36f, 36f)]   // Wide holds down to 856
    [InlineData(850f, 36f, 32f)]   // ... and gives way below it
    [InlineData(900f, 32f, 32f)]   // Medium holds up to 904
    [InlineData(905f, 32f, 36f)]   // ... and steps up beyond it
    [InlineData(580f, 32f, 32f)]   // Medium holds down to 576
    [InlineData(570f, 32f, 16f)]   // ... and gives way below it
    [InlineData(620f, 16f, 16f)]   // Narrow/Compact holds up to 624
    [InlineData(625f, 16f, 32f)]   // ... and steps up beyond it
    [InlineData(float.NaN, 32f, 32f)]
    public void The_hysteretic_gutter_holds_its_previous_value_inside_the_recovery_band(float width, float previous, float expected)
        => Assert.Equal(expected, PageGeometry.GutterFor(width, previous));

    [Fact]
    public void A_resize_jittering_around_a_threshold_changes_the_gutter_at_most_once()
    {
        // From a settled gutter, a width alternating between w and w +/- 20 (a drag jittering over a breakpoint) may step
        // the gutter ONCE, never back and forth.
        for (int w = 540; w <= 940; w++)
        foreach (int delta in new[] { +20, -20 })
        {
            float gutter = PageGeometry.GutterFor(w);
            int changes = 0;
            for (int i = 0; i < 8; i++)
            {
                float next = PageGeometry.GutterFor(i % 2 == 0 ? w + delta : w, gutter);
                if (next != gutter) changes++;
                gutter = next;
            }
            Assert.True(changes <= 1, $"w={w} delta={delta}: the gutter changed {changes} times");
        }
    }

    [Fact]
    public void The_page_gutter_and_the_artist_hero_tier_agree_at_every_width_and_every_previous_tier()
    {
        // A representative width for each tier, settled: the tier a previous frame would have been in.
        static float Rep(ArtistHeroTier t) => t switch
        {
            ArtistHeroTier.Narrow => 100f,
            ArtistHeroTier.Compact => 500f,
            ArtistHeroTier.Medium => 700f,
            _ => 1000f,
        };

        foreach (var tier in Enum.GetValues<ArtistHeroTier>())
        {
            float previousGutter = ArtistHeroLayout.For(Rep(tier), tier).Gutter;
            for (int w = 0; w <= 1400; w++)
            {
                Assert.Equal(ArtistHeroLayout.For(w, tier).Gutter, PageGeometry.GutterFor(w, previousGutter));
                Assert.Equal(ArtistHeroLayout.PageGutterFor(w), PageGeometry.GutterFor(w));
            }
        }
    }
}

public class PageGeometryRhythmTests
{
    [Fact]
    public void The_views_bar_absorbs_the_engine_container_padding_into_its_gaps()
    {
        Assert.Equal(12f, PageGeometry.HeadToViewsGap + PageGeometry.ViewsBarPadY);
        Assert.Equal(16f, PageGeometry.ViewsToBodyGap + PageGeometry.ViewsBarPadY);
        Assert.Equal(48f, PageGeometry.ViewsBarH);
        Assert.Equal(12f, -PageGeometry.ViewsLeadingInset);
    }

    [Fact]
    public void A_hoisted_strip_is_one_views_bar_between_two_equal_insets()
        => Assert.Equal(72f, PageGeometry.HoistedTop + PageGeometry.ViewsBarH + PageGeometry.HoistedTop);

    [Fact]
    public void The_bottom_reserve_clears_the_dock_and_the_pane_inset_is_16()
    {
        Assert.Equal(Design.Dock.Reserve + 32f, PageGeometry.BottomReserve);
        Assert.Equal(16f, PageGeometry.PaneInset);
    }

    [Fact]
    public void The_show_reader_uses_the_one_bottom_reserve()
        => Assert.Equal(PageGeometry.BottomReserve, Show.BottomReserve);

    [Fact]
    public void The_reserved_lines_are_the_type_roles_own_line_heights()
    {
        Assert.Equal(PageGeometry.TitleLine, Design.Type.PageTitle("x").LineHeight);
        Assert.Equal(PageGeometry.MetaLine, Design.Type.PageMeta("x").LineHeight);
        Assert.Equal(36f, Design.Type.PaneTitle("x").LineHeight);
        Assert.Equal(28f, Design.Type.PaneTitle("x").Size);
    }

    [Fact]
    public void The_page_views_style_is_the_stock_bar_plus_the_leading_inset()
    {
        var style = Design.PageViewsStyle;
        Assert.Equal(14f, style.LabelSize);
        Assert.Equal(20f, style.LineHeight);
        Assert.Equal(PageGeometry.ViewsLeadingInset, style.LeadingInset);
        Assert.True(style.ShowPill);
        Assert.Null(style.ItemPadding);
        Assert.True(float.IsNaN(style.ItemHeight));
    }

    [Fact]
    public void The_head_roles_are_one_line_so_a_long_name_cannot_grow_the_head()
    {
        foreach (var el in new[] { Design.Type.PageTitle("x"), Design.Type.PageMeta("x"), Design.Type.PaneTitle("x") })
        {
            Assert.Equal(1, el.MaxLines);
            Assert.Equal(FluentGpu.Foundation.TextWrap.NoWrap, el.Wrap);
            Assert.Equal(FluentGpu.Foundation.TextTrim.CharacterEllipsis, el.Trim);
        }
    }

    [Fact]
    public void The_track_table_leads_with_the_one_pane_inset()
    {
        Assert.Equal(PageGeometry.PaneInset, Track.RowMetrics.PadX);
        Assert.Equal(PageGeometry.PaneInset, Track.RowMetrics.PadXFor(0));
        Assert.True(PageGeometry.PaneInset - Track.RowMetrics.RowInset >= 0f);
    }

    [Fact]
    public void The_detail_rail_leads_with_the_one_pane_inset()
    {
        Assert.Equal(PageGeometry.PaneInset, Detail.RailPolicy.SidePadL);
        Assert.Equal(PageGeometry.PaneInset, Detail.RailLayout.PadTop);
        Assert.Equal(Shell.FrameRules.FrameGap, Detail.RailPolicy.SidePadR);
    }
}
