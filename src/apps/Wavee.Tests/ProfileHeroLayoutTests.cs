// ── Wavee.Tests/ProfileHeroLayoutTests.cs — the round-avatar hero's tiers → metrics (Entities/Profile.Rules.cs §3) ───
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ProfileHeroLayoutTests
{
    [Theory]
    [InlineData(380f, ArtistHeroTier.Narrow)] [InlineData(504f, ArtistHeroTier.Narrow)] [InlineData(610f, ArtistHeroTier.Compact)]
    [InlineData(700f, ArtistHeroTier.Compact)] [InlineData(850f, ArtistHeroTier.Wide)] [InlineData(1016f, ArtistHeroTier.Wide)]
    [InlineData(1064f, ArtistHeroTier.Medium)] [InlineData(300f, ArtistHeroTier.Compact)] [InlineData(1440f, ArtistHeroTier.Narrow)]
    public void Tier_IsTheArtistHerosTier_HysteresisIncluded(float width, ArtistHeroTier previous)
        => Assert.Equal(ArtistHeroLayout.TierFor(width, previous), ProfileHeroLayout.For(width, previous).Tier);

    [Theory]
    [InlineData(1440f, ArtistHeroTier.Wide, 260f)]
    [InlineData(700f, ArtistHeroTier.Medium, 220f)]
    [InlineData(500f, ArtistHeroTier.Compact, 316f)]
    [InlineData(320f, ArtistHeroTier.Narrow, 352f)]
    public void Heights_AreTheWorstCaseBudget(float width, ArtistHeroTier previous, float expected)
        => Assert.Equal(expected, ProfileHeroLayout.For(width, previous).Height);

    public static TheoryData<float, ArtistHeroTier> Tiers => new()
    {
        { 1440f, ArtistHeroTier.Wide }, { 700f, ArtistHeroTier.Medium }, { 500f, ArtistHeroTier.Compact }, { 320f, ArtistHeroTier.Narrow },
    };

    [Theory, MemberData(nameof(Tiers))]
    public void Height_IsPadsPlusTheBody_AndContainsTheCopy(float width, ArtistHeroTier previous)
    {
        var m = ProfileHeroLayout.For(width, previous);
        float copy = ProfileHeroLayout.CopyHeight(in m);
        float body = m.Stacked ? m.Avatar + ProfileHeroLayout.StackedAvatarGap + copy : MathF.Max(m.Avatar, copy);
        Assert.Equal(m.TopPad + body + m.BottomPad, m.Height);
        Assert.True(m.Height - m.TopPad - m.BottomPad >= (m.Stacked ? copy + m.Avatar : copy));
    }

    [Theory, MemberData(nameof(Tiers))]
    public void Collapse_EndsOnTheBand(float width, ArtistHeroTier previous)
    {
        var m = ProfileHeroLayout.For(width, previous);
        Assert.True(ProfileHeroLayout.CollapseDistance(in m) > 0f);
        Assert.Equal(ArtistHeroLayout.CompactIdentityHeight, m.Height - ProfileHeroLayout.CollapseDistance(in m));
    }

    [Fact]
    public void Avatar_Shrinks_StackedBelowMedium_NarrowAloneTakesTwoStatRows()
    {
        var w = ProfileHeroLayout.For(1440f, ArtistHeroTier.Wide);
        var md = ProfileHeroLayout.For(700f, ArtistHeroTier.Medium);
        var c = ProfileHeroLayout.For(500f, ArtistHeroTier.Compact);
        var n = ProfileHeroLayout.For(320f, ArtistHeroTier.Narrow);
        Assert.True(w.Avatar > md.Avatar && md.Avatar > c.Avatar && c.Avatar > n.Avatar);
        Assert.False(w.Stacked); Assert.False(md.Stacked); Assert.True(c.Stacked); Assert.True(n.Stacked);
        Assert.Equal(1, w.NameLines); Assert.Equal(1, md.NameLines); Assert.Equal(1, c.NameLines); Assert.Equal(1, n.NameLines);
        Assert.Equal(1, c.StatRows); Assert.Equal(2, n.StatRows);
    }

    [Theory, MemberData(nameof(Tiers))]
    public void Gutter_IsThePageGutter(float width, ArtistHeroTier previous)
        => Assert.Equal(ArtistHeroLayout.PageGutterFor(width), ProfileHeroLayout.For(width, previous).Gutter);

    [Theory, MemberData(nameof(Tiers))]
    public void Wash_CoversTheHeroPlusTheTail(float width, ArtistHeroTier previous)
    {
        var m = ProfileHeroLayout.For(width, previous);
        Assert.Equal(m.Height + ArtistHeroLayout.ContentBlendTail, ProfileHeroLayout.WashHeight(in m));
        float b = ProfileHeroLayout.WashBoundary(in m);
        Assert.True(b > 0f && b < 1f);
    }

    // ── A2: the collapse floor ──

    [Theory, MemberData(nameof(Tiers))]
    public void CollapseDistance_RunsToTheFloor(float width, ArtistHeroTier previous)
    {
        var m = ProfileHeroLayout.For(width, previous);
        Assert.Equal(m.Height, ProfileHeroLayout.CollapseDistance(in m, 0f));
        Assert.Equal(m.Height - 56f, ProfileHeroLayout.CollapseDistance(in m, 56f));
        Assert.Equal(ProfileHeroLayout.CollapseDistance(in m, ArtistHeroLayout.CompactIdentityHeight), ProfileHeroLayout.CollapseDistance(in m));
    }
}
