// ── Wavee.Tests/Theming/ZuneProfileRulesTests.cs — Zune's profile page and people list rules (P5 Z7) ───────────────────────────────────────
//
// The pure rules of `Themes\Zune\Structure\Rules\ZuneProfileRules.cs` against the boards (Z-Profile, -Compact, -Small, -Huge) and the plan (decision 2:
// pages cap at 2400; the frame's gutter is every page's): the tier records per content tier, the name's voice, the targets.

using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneProfileRulesTests
{
    static readonly WidthTier[] s_tiers = [WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K];

    [Fact]
    public void Every_tier_stands_on_the_frames_gutter()
    {
        foreach (var tier in s_tiers)
        {
            var rec = ZuneProfileTiers.For(tier);
            Assert.Equal(tier, rec.Tier);
            Assert.Equal(ZuneTiers.Frame(tier).Gutter, rec.Gutter);
        }
    }

    [Fact]
    public void Standard_is_the_boards_tuned_page()
    {
        var rec = ZuneProfileTiers.For(WidthTier.Standard);
        Assert.Equal(160f, rec.Avatar);
        Assert.Equal(ZuneProfileName.Hero, rec.Name);
        Assert.Equal(5, rec.TopCols);
        Assert.Equal(56f, rec.TopArt);
        Assert.Equal(28f, rec.SectionGap);
        // The band's row 2 holds "open account" and share at Standard: the hero does not repeat them.
        Assert.False(rec.OwnVerbsInHero);
    }

    [Fact]
    public void Huge_4K_draws_Huges_page_and_the_name_is_the_panorama()
    {
        var huge = ZuneProfileTiers.For(WidthTier.Huge);
        var k = ZuneProfileTiers.For(WidthTier.Huge4K);
        Assert.Equal(ZuneProfileName.Panorama, huge.Name);
        Assert.Equal(huge with { Tier = WidthTier.Huge4K, Gutter = k.Gutter }, k);
    }

    [Fact]
    public void Compact_targets_are_forty_and_the_columns_narrow()
    {
        var rec = ZuneProfileTiers.For(WidthTier.Compact);
        Assert.Equal(40f, rec.Target);
        Assert.Equal(40f, rec.ActionsH);
        Assert.Equal(40f, rec.StatH);
        Assert.Equal(1, rec.TopCols);
        Assert.True(rec.TopRowH >= 2f * 32f, "a ranked row is at least a two-line target tall");
    }

    [Fact]
    public void A_ranked_row_holds_its_art_and_the_name_never_outgrows_its_reserved_line()
    {
        foreach (var tier in s_tiers)
        {
            var rec = ZuneProfileTiers.For(tier);
            Assert.True(rec.TopRowH >= rec.TopArt, $"{tier}: the row holds its art");
            Assert.True(rec.RankW > 0f && rec.TopMinCol > 0f && rec.ListMinCell > 0f && rec.ListShimmerCols > 0);
            float line = rec.Name switch { ZuneProfileName.Panorama => 130f, ZuneProfileName.Hero => 72f, _ => 48f };
            Assert.Equal(line, rec.NameLine);
        }
    }

    [Fact]
    public void The_content_tier_resolves_standard_where_no_frame_exists()
        => Assert.Equal(WidthTier.Standard, ZuneProfileTiers.Of(default(TierFacts)).Tier);
}
