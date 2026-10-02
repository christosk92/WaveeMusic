// ── Wavee.Tests/ArtistReaderHeadLayoutTests.cs — the artist reader's two width arms (Entities/Artist.Reader.Shape.cs) ──
//
// #158: at a ~391-DIP reader the library's artist band printed the name as "Tro…" and the album heads printed their
// titles as "P…" / "Ta…" — every control in the one-row band and the one-row head was `Shrink 0`, and the text was the
// only flexible child. The fix is a geometry change (both rows STACK under an edge), and every number it rests on is a
// pure constant or function on `ReaderShape` that the tree DECLARES and the extent estimator READS. So the whole fix is
// facts here: the two edges and their hysteresis, the derivations that put the edges where they are, the width the name
// and the title get in each arm, the stated heights, and the extents the list lays out before a block renders.
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
        // `BodyWidth` and `TitleAvailW` take a narrow (stacked-head) reader to have no spine beside it. That holds for
        // every history only if the narrow arm's EXIT is under the medium arm's ENTRY.
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

    [Fact]
    public void The_stacked_band_controls_fit_the_window_floor()
    {
        // ▶ 36 · ⤨ 32 · Follow 108 · ↗ 32 · three 12-DIP gaps — the SAME verbs as the wide arm, on a row of their own.
        Assert.Equal(244f, RS.StackedControlsW);
        Assert.True(RS.StackedControlsW + 2f * RS.BandStackPadX <= RS.MinReaderW);    // 276 ≤ 300
    }

    [Fact]
    public void The_wide_band_leaves_the_name_its_floor_at_the_fold()
    {
        Assert.Equal(RS.NameMinW, RS.NameAvailW(RS.MediumBelow, stacked: false));                       // 232
        Assert.True(RS.NameAvailW(RS.MediumBelow + RS.BreakHysteresis, stacked: false) >= RS.NameMinW); // 256
    }

    [Fact]
    public void Name_width_table()
    {
        // Stacked: W − 16 − 56 avatar − 12 gap − 16. Wide: W − 56 spine − 484.
        Assert.Equal(200f, RS.NameAvailW(300f, stacked: true));
        Assert.Equal(260f, RS.NameAvailW(360f, stacked: true));
        Assert.Equal(291f, RS.NameAvailW(391f, stacked: true));    // the screenshot's reader: "Tro…" had 79
        Assert.Equal(330f, RS.NameAvailW(430f, stacked: true));
        Assert.Equal(420f, RS.NameAvailW(520f, stacked: true));
        Assert.Equal(260f, RS.NameAvailW(800f, stacked: false));
        Assert.Equal(329f, RS.NameAvailW(869f, stacked: false));   // a maximized 1080p window at 110 %
    }

    // ── 3. the album head ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_inline_title_keeps_its_floor_at_the_narrow_edge()
    {
        // The inline head first appears at 640 — under the medium edge, so with no spine — and there even the longest
        // common meta ("12 songs · 1 hr 2 min" ≈ 126 DIP) leaves the title text over its floor.
        Assert.Equal(202f, RS.TitleAvailW(RS.NarrowBelow, narrow: false, medium: true, metaW: 126f));
        Assert.True(RS.TitleAvailW(RS.NarrowBelow, narrow: false, medium: true, metaW: 126f) >= RS.TitleFloorW);
        // ...and where the spine appears beside it, the reader is already wide enough to pay for it.
        Assert.True(RS.TitleAvailW(RS.MediumBelow, narrow: false, medium: false, metaW: 126f) >= RS.TitleFloorW);
    }

    [Fact]
    public void The_stacked_title_at_the_window_floor_is_still_wider_than_the_inline_floor()
        => Assert.True(RS.TitleAvailW(RS.MinReaderW, narrow: true, medium: true, metaW: 0f) >= RS.TitleFloorW);   // 156

    [Fact]
    public void Title_width_table()
    {
        // Stacked: the whole body (W − 16 − 88 − 16 − 20) less the link's 4-DIP inset; the meta does not share its row.
        Assert.Equal(156f, RS.TitleAvailW(300f, narrow: true, medium: true, metaW: 102f));
        Assert.Equal(247f, RS.TitleAvailW(391f, narrow: true, medium: true, metaW: 102f));    // the screenshot: "P…" had 3
        Assert.Equal(286f, RS.TitleAvailW(430f, narrow: true, medium: true, metaW: 102f));
        // Inline, with the spine: W − 56 − 172, less the meta, the 116 of circles, two gaps and the inset.
        Assert.Equal(330f, RS.TitleAvailW(800f, narrow: false, medium: false, metaW: 102f));
        // The stacked meta shares its row with the circles: the body less 116 and one gap ("10 songs · 32 m…" at 360).
        Assert.Equal(94f, RS.StackedMetaAvailW(360f));
        Assert.Equal(125f, RS.StackedMetaAvailW(391f));
    }

    [Fact]
    public void No_reader_width_crushes_the_name_or_the_title()
    {
        // The defect, as one sweep from the window floor up: in whichever arm the rules pick, under every hysteresis
        // history, the WIDE band leaves the name its floor, the STACKED band gives it two lines that add up to more than
        // that floor, and every head leaves the title text its floor beside the longest common meta.
        for (float w = RS.MinReaderW; w <= 1600f; w += 1f)
            foreach (bool wasNarrow in new[] { false, true })
                foreach (bool wasMedium in new[] { false, true })
                {
                    bool narrow = RS.Narrow(w, wasNarrow), medium = RS.Medium(w, wasMedium);
                    if (medium)
                        Assert.True(RS.NameLines * RS.NameAvailW(w, stacked: true) >= RS.NameMinW, $"stacked name at {w}");
                    else
                        Assert.True(RS.NameAvailW(w, stacked: false) >= RS.NameMinW, $"wide name at {w}");
                    Assert.True(RS.TitleAvailW(w, narrow, medium, metaW: 126f) >= RS.TitleFloorW, $"title at {w}");
                }
    }

    // ── 4. the stated heights (what the tree declares and the estimator reads) ────────────────────────────────────

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

    // ── 5. what the list remounts on ───────────────────────────────────────────────────────────────────────────────

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
