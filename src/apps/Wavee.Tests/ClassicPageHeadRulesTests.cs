// ── Wavee.Tests/ClassicPageHeadRulesTests.cs — Classic's page head numbers (Classic tuning F5, breadcrumb B) ────────────────────────────
//
// The tuned canvas's head (`C-BrowseCategory`, `C-Concert`, `C-Discography`): a 28-DIP trail slot of 12/16/600 links 6 apart, 4 above the 40/52
// title; a page whose hero is its title keeps the trail's slot alone, 12 above the hero. The reserve under the overlay masthead is fixed per form.

using FluentGpu.Dsl;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ClassicPageHeadRulesTests
{
    [Fact]
    public void The_trail_is_a_28_slot_of_600_links_6_apart_4_above_the_title_and_12_above_a_hero()
    {
        Assert.Equal(28f, ClassicPageHeadRules.TrailSlotHeight);
        Assert.Equal(6f, ClassicPageHeadRules.TrailLinkGap);
        Assert.Equal(600, ClassicPageHeadRules.TrailWeight);
        Assert.Equal(4f, ClassicPageHeadRules.TrailTitleGap);
        Assert.Equal(12f, ClassicPageHeadRules.HeroGap);
        Assert.Equal(3, ClassicPageHeadRules.TrailMinDepth);   // depth <= 2 has no trail
    }

    [Theory]
    [InlineData(PageHeadForm.Title, 52f, 84f)]
    [InlineData(PageHeadForm.Drill, 84f, 116f)]
    [InlineData(PageHeadForm.Hero, 28f, 60f)]
    public void The_head_and_its_reserve_are_fixed_per_form(PageHeadForm form, float head, float reserve)
    {
        Assert.Equal(head, ClassicPageHeadRules.HeadHeight(form));
        Assert.Equal(reserve, ClassicPageHeadRules.HeadReserve(form));
    }

    [Fact]
    public void A_title_heads_reserve_is_the_browse_masthead_reserve()
        => Assert.Equal(BrowseMastheadMetrics.Reserve, ClassicPageHeadRules.HeadReserve(PageHeadForm.Title));

    [Fact]
    public void A_drill_in_body_clears_the_drill_reserve_then_the_16_gap_on_the_page_gutters()
    {
        var pad = ClassicPageHeadRules.DrillBodyPad(32f, Spacing.L);   // the gutter is the content tier's (TC1): any one of 16 / 32 / 36
        Assert.Equal(132f, pad.Top);
        Assert.Equal(32f, pad.Left);
        Assert.Equal(32f, pad.Right);
        Assert.Equal(Spacing.L, pad.Bottom);
    }
}
