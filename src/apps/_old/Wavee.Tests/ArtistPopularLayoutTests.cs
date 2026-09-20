using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>S2 audit finding #8 (evidence t_cputh_tt/tt2, t_klaas_a/thumbs): ArtistPopular's chart rows used to fold
/// a bare `cellW >= threshold` straight into the mounted row's KEY, so a cosmetic width change (the extended-tracks
/// fetch landing, a 1↔2 column crossing, a page-count flip) destroyed and rebuilt every row with a different
/// play-count string and a squeezed artist name. These tests cover the two-part fix in isolation from the engine:
/// the geometry tier is now HYSTERETIC (narrow immediately, widen only once clear of the breakpoint), and the
/// play-count format is no longer part of the tier — or width-derived — at all.</summary>
public class ArtistPopularLayoutTests
{
    [Fact]
    public void NominalFor_Modern_TiersOnArtDurationAndStack()
    {
        // The three breakpoints nest (200 < 220 < 340), so a width below the ART breakpoint is also below the
        // (higher) STACK one — there is no width that narrows art alone while leaving the subtitle unstacked.

        // Wide: full artwork, duration shown, subtitle unstacked.
        var wide = ArtistPopularLayout.NominalFor(400f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(44f, true, false), wide);

        // Below the stack breakpoint only (< 340, still ≥ 220 and ≥ 200): full artwork + duration, but stacked.
        var stacked = ArtistPopularLayout.NominalFor(339f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(44f, true, true), stacked);

        // Below the art breakpoint too (< 220, still ≥ 200): smaller artwork, duration shown, stacked.
        var narrowArt = ArtistPopularLayout.NominalFor(219f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(40f, true, true), narrowArt);

        // Below the duration breakpoint too (< 200): duration cell dropped as well.
        var noDuration = ArtistPopularLayout.NominalFor(199f, classic: false);
        Assert.Equal(new ArtistPopularLayout.Tier(40f, false, true), noDuration);
    }

    [Fact]
    public void NominalFor_BoundarySanity()
    {
        Assert.Equal(44f, ArtistPopularLayout.NominalFor(220f, classic: false).Art);   // ≥ break → wide
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

        // Classic still drops the duration cell below 200 — that threshold was never gated on `classic` pre-fix.
        var narrow = ArtistPopularLayout.NominalFor(150f, classic: true);
        Assert.Equal(40f, narrow.Art);
        Assert.False(narrow.StackSub);
        Assert.False(narrow.ShowDuration);
    }

    [Fact]
    public void Decide_FirstDecision_TakesNominalOutright()
    {
        // previous: null is the mount case — "the width known at mount" — no hysteresis applies yet.
        var t = ArtistPopularLayout.Decide(250f, classic: false, previous: null);
        Assert.Equal(ArtistPopularLayout.NominalFor(250f, false), t);
    }

    [Fact]
    public void Decide_Art_NarrowsImmediately_WidensOnlyAfterHysteresis()
    {
        var t = ArtistPopularLayout.Decide(300f, false, null);
        Assert.Equal(44f, t.Art);

        // A single px below the bare breakpoint narrows immediately — no dead zone on the way down.
        t = ArtistPopularLayout.Decide(219f, false, t);
        Assert.Equal(40f, t.Art);

        // Clearing the breakpoint again is NOT enough to widen — must clear it by the full hysteresis dip.
        t = ArtistPopularLayout.Decide(220f, false, t);
        Assert.Equal(40f, t.Art);
        t = ArtistPopularLayout.Decide(243f, false, t);   // 220 + 24 - 1
        Assert.Equal(40f, t.Art);

        // 220 + 24 = 244 clears it.
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

        // Dropping a single px below 340 stacks immediately (the safe direction: more room per line).
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
        // This is the exact shape of the audit's bug: cellW wobbles a few px either side of the 340 breakpoint
        // (shelf remeasurement noise). The FIRST dip below 340 stacks the subtitle; every wobble value below is
        // deliberately mixed above and below 340 itself (338/341/336/342/335/339) to prove Decide is reading its
        // OWN hysteresis memory, not just re-deriving the nominal reading each call — none of them reaches
        // 340 + HysteresisDip (364), so the tier must hold STACKED through the entire wobble.
        var t = ArtistPopularLayout.Decide(350f, false, null);
        Assert.False(t.StackSub);

        float[] wobble = [338f, 341f, 336f, 342f, 335f, 339f];
        foreach (float w in wobble)
        {
            t = ArtistPopularLayout.Decide(w, false, t);
            Assert.True(t.StackSub);   // held stacked — no flapping, even though 341/342 alone would read "unstacked"
        }
    }

    [Fact]
    public void Decide_LargeJump_ResolvesInOneCall()
    {
        // A genuine 1↔2 column crossing moves cellW by 100s of px, comfortably clearing any hysteresis margin —
        // the tier must not lag behind it for several frames.
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
