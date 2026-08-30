using Wavee;
using Xunit;

namespace Wavee.Tests;

// The progressive-reveal ramp progression (DetailRevealRamp) — the cold shimmer→content swap reveals REAL rows a chunk
// at a time instead of the whole visible band in one ~80ms frame. Pure math, so it is locked here in isolation.
public class DetailRevealRampTests
{
    [Fact]
    public void Next_RampsOneChunkPerStep_ThenSnapsToDoneAtTheRealizedBand()
    {
        // A tall list: target = min(visible, Cap) = 44. Each step adds exactly one Chunk until a chunk reaches the band.
        int r = DetailRevealRamp.Chunk;                 // the swap frame already shows the first chunk real (12)
        r = DetailRevealRamp.Next(r, 44);
        Assert.Equal(24, r);
        r = DetailRevealRamp.Next(r, 44);
        Assert.Equal(36, r);
        r = DetailRevealRamp.Next(r, 44);               // 36 + 12 = 48 >= 44 → done
        Assert.Equal(DetailRevealRamp.Done, r);
    }

    [Fact]
    public void Next_CapsTheBand_SoAHugeListStillFinishesInAFewSteps()
    {
        // visible = 10_000 must NOT ramp forever: the target is capped at Cap, so it snaps to Done within Cap/Chunk steps.
        int r = DetailRevealRamp.Chunk;
        int steps = 0;
        while (r != DetailRevealRamp.Done)
        {
            r = DetailRevealRamp.Next(r, 10_000);
            steps++;
            Assert.True(steps <= DetailRevealRamp.Cap / DetailRevealRamp.Chunk + 1, "ramp must terminate near Cap, never walk the whole list");
        }
        Assert.Equal(DetailRevealRamp.Done, r);
    }

    [Fact]
    public void Next_SmallList_FinishesImmediately()
    {
        // Fewer tracks than a chunk: the very first advance already covers the whole (tiny) band → Done, no visible ramp.
        Assert.Equal(DetailRevealRamp.Done, DetailRevealRamp.Next(DetailRevealRamp.Chunk, 3));
    }

    [Fact]
    public void Revealed_GatesRowsBelowTheCount_AndDoneRevealsEverything()
    {
        Assert.True(DetailRevealRamp.Revealed(0, DetailRevealRamp.Chunk));    // row 0 is within the first chunk
        Assert.True(DetailRevealRamp.Revealed(11, DetailRevealRamp.Chunk));   // last row of the first chunk
        Assert.False(DetailRevealRamp.Revealed(12, DetailRevealRamp.Chunk));  // row 12 still shimmer until the next tick
        Assert.False(DetailRevealRamp.Revealed(43, DetailRevealRamp.Chunk));  // deep row: shimmer while cold
        Assert.True(DetailRevealRamp.Revealed(9_999, DetailRevealRamp.Done)); // steady state (Done) → every row real
    }
}
