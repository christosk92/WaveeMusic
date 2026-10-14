// ── Wavee.Tests/ArtistReaderHeadLayoutTests.cs — the artist reader's two width arms (Entities/Artist.Reader.Shape.cs) ──
//
// #158: at a ~391-DIP reader the library's artist band printed the name as "Tro…" and the album heads printed their
// titles as "P…" / "Ta…" — every control in the one-row band and the one-row head was `Shrink 0`, and the text was the
// only flexible child. The fix is a geometry change (both rows STACK under an edge), and every number it rests on is a
// pure constant or function on `ReaderShape` that the tree DECLARES and the extent estimator READS. So the whole fix is
// facts here: the two edges and their hysteresis, the derivation that puts the medium edge where it is, the stated heights, and the extents the list lays out before a block renders.
//
// The edge facts mirror LibraryLayoutBreakpointTests. `TextFit` (the trimmed-text proxies the tooltips use) is tested in
// SurfaceRulesTests, beside the rule itself.

using Xunit;
using ReaderBlock = Wavee.Artist.ReaderBlock;
using RS = Wavee.Artist.ReaderShape;

namespace Wavee.Tests;

public class ArtistReaderHeadLayoutTests
{
    // ── 1. the two edges ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Narrow_enters_below_640_and_holds_until_664()
    {
        Assert.True(RS.Narrow(639f, false));
        Assert.False(RS.Narrow(640f, false));
        Assert.True(RS.Narrow(663f, true));                    // the hysteresis band: once narrow, stay until 664
        Assert.False(RS.Narrow(664f, true));
        Assert.True(RS.Narrow(0f, true));                      // an unmeasured width keeps the previous answer
        Assert.False(RS.Narrow(0f, false));
    }

    [Fact]
    public void Medium_enters_below_772_and_holds_until_796()
    {
        Assert.Equal(772f, RS.MediumBelow);
        Assert.True(RS.Medium(771f, false));
        Assert.False(RS.Medium(772f, false));
        Assert.True(RS.Medium(795f, true));
        Assert.False(RS.Medium(796f, true));
        Assert.True(RS.Medium(0f, true));
        Assert.False(RS.Medium(0f, false));
        Assert.True(RS.Medium(391f, false));                   // the owner's reader stacks its band, however it got there
    }

    [Fact]
    public void The_medium_edge_is_derived_from_the_wide_band_and_the_spine_beside_it()
    {
        // The wide band shares the lane with the 56-DIP spine (one signal drives both), so the narrowest reader that can
        // show it is everything in the row that never shrinks, plus the spine, plus the name's floor. The narrow-heads
        // plan's "484 + 232 = 716 ≤ 720" left the spine out: at 720 the name would have had 180 DIP.
        Assert.Equal(484f, RS.WideBandFixedW);
        Assert.Equal(56f, RS.SpineW);
        Assert.Equal(RS.WideBandFixedW + RS.SpineW + RS.NameMinW, RS.MediumBelow);
    }

    [Fact]
    public void The_narrow_arm_always_sits_inside_the_medium_one()
    {
        // A narrow (stacked-head) reader has no spine beside it. That holds for every history only if the narrow arm's
        // EXIT is under the medium arm's ENTRY.
        Assert.True(RS.NarrowBelow + RS.BreakHysteresis <= RS.MediumBelow);
        for (float w = 1f; w <= 1200f; w += 1f)
            foreach (bool wasNarrow in new[] { false, true })
                foreach (bool wasMedium in new[] { false, true })
                    if (RS.Narrow(w, wasNarrow))
                        Assert.True(RS.Medium(w, wasMedium), $"a narrow reader without the medium arm at {w} DIP");
    }

    // ── 2. the band ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_band_metrics_are_the_controls_own_numbers()
    {
        // The shape is engine-free, so it names these widths rather than asking the controls — pinned to them here.
        Assert.Equal(Controls.PrimaryMinWidth, RS.PlayButtonMinW);
        Assert.Equal(Controls.IconButtonSize, RS.IconActionW);
    }

    // ── 3. the stated heights (what the tree declares and the estimator reads) ────────────────────────────────────

    [Fact]
    public void Heights_are_the_sum_of_their_parts()
    {
        Assert.Equal(44f, RS.HeadH);
        Assert.Equal(30f, RS.HeadTitleRowH);                        // 2 + 26 + 2
        Assert.True(MathF.Max(RS.HeadTitleRowH, RS.HeadCircle) <= RS.HeadH);   // the inline row fits its box
        Assert.Equal(72f, RS.HeadStackedH);                         // 30 + 6 + 32 + 4
        Assert.Equal(2f * RS.RowH, RS.HeadStackedH);             // the stacked head costs exactly two track rows
        Assert.Equal(RS.HeadH, RS.HeadHeight(narrow: false));
        Assert.Equal(RS.HeadStackedH, RS.HeadHeight(narrow: true));

        Assert.Equal(100f, RS.BandH);                               // the wide band is unchanged: 20 + 72 + 8
        Assert.True(RS.NameLine + RS.NameSubGap + RS.SubLine <= RS.AvatarEdge);   // ...its one-line name fits under the avatar
        Assert.Equal(100f, RS.NameColumnH);                         // two 40-DIP lines + 2 + the 18-DIP subline
        Assert.True(RS.NameColumnH >= RS.AvatarEdgeStacked);     // the avatar centres in the name column's box
        Assert.Equal(168f, RS.BandStackedH);                        // 16 + 100 + 8 + 36 + 8
        Assert.Equal(RS.BandH, RS.BandHeight(stacked: false));
        Assert.Equal(RS.BandStackedH, RS.BandHeight(stacked: true));
    }

    [Fact]
    public void ExtentOf_narrow_uses_the_stacked_head()
    {
        static ReaderBlock Block(int rows) => new(AlbumSlot: 1, Saved: true, Rows: rows, Failed: false, LikedOnly: false);

        // pad 16 + max(cover + 8 + 16, head + rows × 36 + 8) + pad 8 + divider 1 — narrow: cover 88, head 72.
        Assert.Equal(137f, RS.ExtentOf(Block(0), narrow: true));    // the cover still wins: 112 over 80
        Assert.Equal(141f, RS.ExtentOf(Block(1), narrow: true));    // the body wins from the first row: 116
        Assert.Equal(177f, RS.ExtentOf(Block(2), narrow: true));
        Assert.Equal(249f, RS.ExtentOf(Block(4), narrow: true));
        Assert.Equal(537f, RS.ExtentOf(Block(12), narrow: true));

        // Wide, unchanged: cover 120, head 44.
        Assert.Equal(169f, RS.ExtentOf(Block(0), narrow: false));
        Assert.Equal(169f, RS.ExtentOf(Block(2), narrow: false));
        Assert.Equal(221f, RS.ExtentOf(Block(4), narrow: false));
        Assert.Equal(509f, RS.ExtentOf(Block(12), narrow: false));

        // Once the body wins in both arms, the narrow block is taller by exactly what stacking the head costs.
        Assert.Equal(RS.HeadStackedH - RS.HeadH, RS.ExtentOf(Block(12), narrow: true) - RS.ExtentOf(Block(12), narrow: false));
    }

    // ── 4. what the list remounts on ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MountKey_ignores_the_band_arm()
    {
        // The band's arm (MEDIUM) is not a mount input: the band is item 0, corrected in place, and a 24-DIP grip drag
        // across that edge must never remount the list. The key has exactly its four inputs — artist, scope, sort and the
        // NARROW arm, which does freeze the block layout — and nowhere to put a fifth.
        string key = ReaderMountPolicy.MountKey(1, 0, 0, true);
        Assert.Equal(key, ReaderMountPolicy.MountKey(1, 0, 0, true));
        Assert.Equal(4, key.Split(':').Length);
        Assert.NotEqual(key, ReaderMountPolicy.MountKey(1, 0, 0, false));
    }
}
