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

using Wavee.Themes.Classic;
using Xunit;
using ReaderBlock = Wavee.Artist.ReaderBlock;
using RS = Wavee.Artist.ReaderShape;

namespace Wavee.Tests;

public class ArtistReaderHeadLayoutTests
{
    // ── 1. the two edges (the structure's reader ladder since TC6: `ClassicArtistTiers.Reader`) ─────────────────────────────

    /// <summary>The reader's arm after a resolve from <paramref name="previous"/> (0 narrow, 1 medium, 2 wide; -1 a fresh resolve).</summary>
    static int Arm(float w, int previous) => ClassicArtistTiers.Reader.Resolve(w, previous);

    [Fact]
    public void Narrow_enters_below_640_and_holds_until_664()
    {
        Assert.Equal(0, Arm(639f, 2));
        Assert.NotEqual(0, Arm(640f, 2));
        Assert.Equal(0, Arm(663f, 0));                          // the hysteresis band: once narrow, stay until 664
        Assert.NotEqual(0, Arm(664f, 0));
        Assert.NotEqual(0, Arm(640f, -1));                      // a fresh resolve uses the edge itself
    }

    [Fact]
    public void Medium_enters_below_748_and_holds_until_772()
    {
        Assert.Equal(748f, ClassicArtistTiers.ReaderMediumBelow);
        Assert.Equal(1, Arm(747f, 2));
        Assert.Equal(2, Arm(748f, 2));
        Assert.Equal(1, Arm(771f, 1));
        Assert.Equal(2, Arm(772f, 1));
        Assert.True(Arm(391f, 2) <= 1);                         // the owner's reader stacks its band, however it got there
    }

    [Fact]
    public void The_medium_edge_is_derived_from_the_wide_band_and_the_spine_beside_it()
    {
        // The wide band shares the lane with the 56-DIP spine (one arm drives both), so the narrowest reader that can
        // show it is everything in the row that never shrinks, plus the spine, plus the name's floor. The narrow-heads
        // plan's "484 + 232 = 716 ≤ 720" left the spine out: at 720 the name would have had 180 DIP.
        Assert.Equal(460f, RS.WideBandFixedW);                     // Y3: 40 pad + 2 × 16 + 72 avatar + the 316 verb row
        Assert.Equal(316f, RS.ActionsW);                           // Play all 120 + Follow 108 + 2 × 32 + 3 × 8
        Assert.Equal(56f, RS.SpineW);
        Assert.Equal(RS.WideBandFixedW + RS.SpineW + RS.NameMinW, RS.WideBandLaneW);
        Assert.Equal(RS.WideBandLaneW, ClassicArtistTiers.ReaderMediumBelow);
    }

    [Fact]
    public void The_narrow_arm_always_sits_inside_the_medium_one()
    {
        // One ladder for both edges: a narrow (stacked-head) reader is below the medium edge too, so it never has the spine beside it, and the narrow
        // arm's EXIT is under the medium arm's ENTRY.
        Assert.True(ClassicArtistTiers.ReaderNarrowBelow + ClassicArtistTiers.ReaderHold <= ClassicArtistTiers.ReaderMediumBelow);
        for (float w = 1f; w <= 1200f; w += 1f)
            for (int previous = -1; previous <= 2; previous++)
                Assert.InRange(Arm(w, previous), 0, 2);
    }

    // ── 2. the band ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_band_metrics_are_the_controls_own_numbers()
    {
        // The shape is engine-free, so it names these widths rather than asking the controls — pinned to them here.
        Assert.Equal(Controls.PrimaryMinWidth, RS.PlayButtonMinW);
        Assert.Equal(Controls.IconButtonSize, RS.IconActionW);
        Assert.Equal(Controls.ButtonHeight, RS.ActionH);           // Y3: one row of stock 32-tall controls in both arms
    }

    [Fact]
    public void The_stacked_verb_row_folds_shuffle_and_open_into_a_menu_before_it_would_clip()
    {
        // Y3 follow-up: the stacked row measures its lane (the band less 2 × 16) and folds wherever the whole row does not fit — so the 300-DIP
        // window floor (a ~268-DIP lane) is folded, a 391-DIP reader (359 lane) is not, and an unmeasured lane folds.
        Assert.True(ClassicLibraryTiers.FoldVerbs(0f));
        Assert.True(ClassicLibraryTiers.FoldVerbs(RS.ActionsW - 1f));
        Assert.False(ClassicLibraryTiers.FoldVerbs(RS.ActionsW));
        Assert.True(ClassicLibraryTiers.FoldVerbs(300f - 2f * RS.BandStackPadX));
        Assert.False(ClassicLibraryTiers.FoldVerbs(391f - 2f * RS.BandStackPadX));
        // Folded, what is left without Play all's label (Follow's slot, ⋯, two gaps) leaves Play all ≥ 100 DIP at the window floor.
        float rest = RS.FollowSlotW + RS.IconActionW + 2f * RS.ActionGap;
        Assert.True(300f - 2f * RS.BandStackPadX - rest >= 100f);
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
        Assert.Equal(164f, RS.BandStackedH);                        // 16 + 100 + 8 + 32 (the verb row) + 8
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
