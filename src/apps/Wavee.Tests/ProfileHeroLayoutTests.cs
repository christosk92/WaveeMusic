// ── Wavee.Tests/ProfileHeroLayoutTests.cs — the round-avatar hero's sub-tiers → metrics (Classic Rules/Tiers.Profile.cs, core Profile.Rules.cs §3) ───
//
// Since TC6 the hero's per-tier numbers are Classic's records (`ClassicProfileTiers`); the tuned Standard ones are pinned here unchanged, and the core keeps
// the collapse and the wash (`ProfileHeroLayout`).

using Wavee;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public class ProfileHeroLayoutTests
{
    static ClassicProfileHeroForm Form(ArtistHeroTier tier) => ClassicProfileTiers.StandardForm(tier);
    static ProfileHeroMetrics At(ArtistHeroTier tier) => Form(tier).Metrics(36f);

    [Theory]
    [InlineData(ArtistHeroTier.Wide, 260f)]
    [InlineData(ArtistHeroTier.Medium, 220f)]
    [InlineData(ArtistHeroTier.Compact, 316f)]
    [InlineData(ArtistHeroTier.Narrow, 352f)]
    public void Heights_AreTheWorstCaseBudget(ArtistHeroTier tier, float expected) => Assert.Equal(expected, At(tier).Height);

    public static TheoryData<ArtistHeroTier> Tiers => new() { ArtistHeroTier.Wide, ArtistHeroTier.Medium, ArtistHeroTier.Compact, ArtistHeroTier.Narrow };

    [Theory, MemberData(nameof(Tiers))]
    public void Height_IsPadsPlusTheBody_AndContainsTheCopy(ArtistHeroTier tier)
    {
        var f = Form(tier);
        bool stacked = f.Arm != ClassicProfileHeroArm.Row;
        float body = stacked ? f.Avatar + f.AvatarGap + f.CopyHeight : MathF.Max(f.Avatar, f.CopyHeight);
        Assert.Equal(f.TopPad + body + f.BottomPad, f.Height);
        Assert.True(f.Height - f.TopPad - f.BottomPad >= (stacked ? f.CopyHeight + f.Avatar : f.CopyHeight));
    }

    [Theory, MemberData(nameof(Tiers))]
    public void Collapse_EndsOnTheBand(ArtistHeroTier tier)
    {
        var m = At(tier);
        Assert.True(ProfileHeroLayout.CollapseDistance(in m) > 0f);
        Assert.Equal(ArtistHeroLayout.CompactIdentityHeight, m.Height - ProfileHeroLayout.CollapseDistance(in m));
    }

    [Fact]
    public void Avatar_Shrinks_StackedBelowMedium_NarrowAloneTakesTwoStatRows()
    {
        var w = At(ArtistHeroTier.Wide);
        var md = At(ArtistHeroTier.Medium);
        var c = At(ArtistHeroTier.Compact);
        var n = At(ArtistHeroTier.Narrow);
        Assert.True(w.Avatar > md.Avatar && md.Avatar > c.Avatar && c.Avatar > n.Avatar);
        Assert.False(w.Stacked); Assert.False(md.Stacked); Assert.True(c.Stacked); Assert.True(n.Stacked);
        Assert.Equal(1, w.NameLines); Assert.Equal(1, md.NameLines); Assert.Equal(1, c.NameLines); Assert.Equal(1, n.NameLines);
        Assert.Equal(1, c.StatRows); Assert.Equal(2, n.StatRows);
    }

    [Theory, MemberData(nameof(Tiers))]
    public void Gutter_IsThePageGutter(ArtistHeroTier tier)
    {
        var facts = WindowTiers.Resolve(ClassicTiers.Policy, 1440f, 960f, 1120f);
        Assert.Equal(ClassicTiers.PageOf(facts).Gutter, ClassicProfileTiers.HeroMetrics(tier, facts, 1120f).Gutter);
        Assert.Equal(ClassicTiers.PageOf(facts).Gutter, ClassicArtistTiers.HeroMetrics(tier, facts, 1120f).Gutter);
    }

    [Theory, MemberData(nameof(Tiers))]
    public void Wash_CoversTheHeroPlusTheTail(ArtistHeroTier tier)
    {
        var m = At(tier);
        Assert.Equal(m.Height + ArtistHeroLayout.ContentBlendTail, ProfileHeroLayout.WashHeight(in m));
        float b = ProfileHeroLayout.WashBoundary(in m);
        Assert.True(b > 0f && b < 1f);
    }
}
