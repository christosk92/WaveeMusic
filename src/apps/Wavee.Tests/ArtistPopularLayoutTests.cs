// ── Wavee.Tests/ArtistPopularLayoutTests.cs — the chart row's hysteretic geometry tier ────────────────────────────────
//
// Ported VERBATIM from _old/Wavee.Tests/ArtistPopularLayoutTests.cs onto `Wavee.ArtistPopularLayout`
// (Entities/Artist.UI.cs, ch 08 §8). S2 audit finding #8: the tier is HYSTERETIC (narrow immediately, widen only once
// clear of the breakpoint), and the play-count format is not part of it — or width-derived — at all.

using Xunit;

namespace Wavee.Tests;

public class ArtistPopularLayoutTests
{
    [Fact]
    public void NominalFor_Modern_TiersOnArtDurationAndStack()
    {
        // The three breakpoints nest (200 < 220 < 340), so a width below the ART breakpoint is also below the STACK one.
        var wide = ArtistPopularLayout.NominalFor(400f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(44f, true, false), wide);

        var stacked = ArtistPopularLayout.NominalFor(339f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(44f, true, true), stacked);

        var narrowArt = ArtistPopularLayout.NominalFor(219f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(40f, true, true), narrowArt);

        var noDuration = ArtistPopularLayout.NominalFor(199f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(40f, false, true), noDuration);
    }

    [Fact]
    public void NominalFor_BoundarySanity()
    {
        Assert.Equal(44f, ArtistPopularLayout.NominalFor(220f, classic: false).Art);
        Assert.Equal(40f, ArtistPopularLayout.NominalFor(219.99f, classic: false).Art);
        Assert.True(ArtistPopularLayout.NominalFor(200f, classic: false).ShowDuration);
        Assert.False(ArtistPopularLayout.NominalFor(199.99f, classic: false).ShowDuration);
        Assert.False(ArtistPopularLayout.NominalFor(340f, classic: false).StackSub);
        Assert.True(ArtistPopularLayout.NominalFor(339.99f, classic: false).StackSub);
    }

    [Fact]
    public void NominalFor_Classic_ArtPinnedAt40AndNeverStacks()
    {
        var wide = ArtistPopularLayout.NominalFor(1000f, classic: true);
        Assert.Equal(40f, wide.Art);
        Assert.False(wide.StackSub);
        Assert.True(wide.ShowDuration);

        // Classic still drops the duration cell below 200.
        var narrow = ArtistPopularLayout.NominalFor(150f, classic: true);
        Assert.Equal(40f, narrow.Art);
        Assert.False(narrow.StackSub);
        Assert.False(narrow.ShowDuration);
    }

    [Fact]
    public void Decide_FirstDecision_TakesNominalOutright()
    {
        var t = ArtistPopularLayout.Decide(250f, classic: false, previous: null);
        Assert.Equal(ArtistPopularLayout.NominalFor(250f, false), t);
    }

    [Fact]
    public void Decide_Art_NarrowsImmediately_WidensOnlyAfterHysteresis()
    {
        var t = ArtistPopularLayout.Decide(300f, false, null);
        Assert.Equal(44f, t.Art);

        t = ArtistPopularLayout.Decide(219f, false, t);
        Assert.Equal(40f, t.Art);

        t = ArtistPopularLayout.Decide(220f, false, t);
        Assert.Equal(40f, t.Art);
        t = ArtistPopularLayout.Decide(243f, false, t);   // 220 + 24 - 1
        Assert.Equal(40f, t.Art);

        t = ArtistPopularLayout.Decide(244f, false, t);
        Assert.Equal(44f, t.Art);
    }

    [Fact]
    public void Decide_ShowDuration_NarrowsImmediately_WidensOnlyAfterHysteresis()
    {
        var t = ArtistPopularLayout.Decide(250f, false, null);
        Assert.True(t.ShowDuration);

        t = ArtistPopularLayout.Decide(199f, false, t);
        Assert.False(t.ShowDuration);

        t = ArtistPopularLayout.Decide(223f, false, t);   // 200 + 24 - 1
        Assert.False(t.ShowDuration);
        t = ArtistPopularLayout.Decide(224f, false, t);
        Assert.True(t.ShowDuration);
    }

    [Fact]
    public void Decide_StackSub_StacksImmediately_UnstacksOnlyAfterHysteresis()
    {
        var t = ArtistPopularLayout.Decide(400f, false, null);
        Assert.False(t.StackSub);

        t = ArtistPopularLayout.Decide(339f, false, t);
        Assert.True(t.StackSub);

        t = ArtistPopularLayout.Decide(363f, false, t);   // 340 + 24 - 1
        Assert.True(t.StackSub);
        t = ArtistPopularLayout.Decide(364f, false, t);
        Assert.False(t.StackSub);
    }

    [Fact]
    public void Decide_JitterAroundABreakpoint_NeverChatters()
    {
        // The audit's bug shape: cellW wobbles a few px either side of 340. The first dip stacks; nothing in the wobble
        // reaches 340 + 24, so the tier must hold STACKED throughout.
        var t = ArtistPopularLayout.Decide(350f, false, null);
        Assert.False(t.StackSub);

        float[] wobble = [338f, 341f, 336f, 342f, 335f, 339f];
        foreach (float w in wobble)
        {
            t = ArtistPopularLayout.Decide(w, false, t);
            Assert.True(t.StackSub);
        }
    }

    [Fact]
    public void Decide_LargeJump_ResolvesInOneCall()
    {
        var narrow = ArtistPopularLayout.Decide(260f, false, null);
        Assert.Equal(new ArtistPopularLayout.Tier(44f, true, true), narrow);

        var wide = ArtistPopularLayout.Decide(700f, false, narrow);
        Assert.Equal(new ArtistPopularLayout.Tier(44f, true, false), wide);

        var backToNarrow = ArtistPopularLayout.Decide(260f, false, wide);
        Assert.Equal(new ArtistPopularLayout.Tier(44f, true, true), backToNarrow);
    }

    [Fact]
    public void Decide_Classic_IgnoresArtAndStackHysteresis_OnlyDurationReacts()
    {
        var t = ArtistPopularLayout.Decide(150f, true, null);
        Assert.Equal(ArtistPopularLayout.Tier.Classic(false), t);

        t = ArtistPopularLayout.Decide(1000f, true, t);
        Assert.Equal(40f, t.Art);
        Assert.False(t.StackSub);
        Assert.True(t.ShowDuration);
    }
}
