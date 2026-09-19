// ── Wavee.Tests/StageTests.cs — the immersive stage's allocator and its pane choice ────────────────────────────────
//
// Ported from `StageLayoutTests` (585 lines, 29 facts): the width ladder, the height ladder, the sizes, the fold set and
// the scrim ladder — every boundary DERIVED from the constants rather than written down, so retuning a threshold
// retunes the test with it. The INK half of that file (`TheStageInkDarkArm_IsWaveeOnMediaVerbatim`, the light arm's
// mirror and contrast facts) drives `Design.StageArm`, which is owner L's `Platform/Design.cs` — those facts port with
// that file, not this one. The one ink fact this file keeps is the seam between the two: the scrim plateau and the
// accent ground are ONE number.

using Wavee;
using Xunit;

using L = Wavee.Stage.Layout;
using C = Wavee.Stage.Control;

namespace Wavee.Tests;

public class StageLayoutTests
{
    const float SweepMax = 2600f;

    /// <summary>A column height at which the HEIGHT ladder is inert, DERIVED from the ladder so it cannot drift away from
    /// the thing it neutralises — the width tests then test exactly the width ladder.</summary>
    static readonly float TallH = L.ColumnChromeH(C.None, L.WidePlayBoxW) + L.WideArtW;

    /// <summary>The app's default window (1180 × 760) less the caption band, the player bar and the stage's top band.</summary>
    const float DefaultColumnAvailH = 760f - 48f - 72f - 88f;

    static L SeedTall(float w) => L.Seed(w, TallH);
    static L StepTall(float w, L prev) => L.Resolve(w, TallH, prev);

    // ── the width ladder ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_wide_threshold_is_the_declared_one()
    {
        float first = -1f;
        for (float w = 0f; w <= SweepMax; w += 1f) if (SeedTall(w).Wide) { first = w; break; }
        Assert.Equal(L.WideEnterW, first);
        Assert.False(SeedTall(first - 1f).Wide);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-100f)]
    [InlineData(1f)]
    public void A_degenerate_width_is_compact(float w) => Assert.False(SeedTall(w).Wide);

    [Fact]
    public void An_upward_sweep_flips_exactly_once()
    {
        var cur = SeedTall(0f);
        int flips = 0;
        for (float w = 0f; w <= SweepMax; w += 1f)
        {
            var next = StepTall(w, cur);
            if (next.Wide != cur.Wide) flips++;
            cur = next;
        }
        Assert.Equal(1, flips);
        Assert.True(cur.Wide);
    }

    [Fact]
    public void A_downward_sweep_flips_exactly_once()
    {
        var cur = SeedTall(SweepMax);
        int flips = 0;
        for (float w = SweepMax; w >= 0f; w -= 1f)
        {
            var next = StepTall(w, cur);
            if (next.Wide != cur.Wide) flips++;
            cur = next;
        }
        Assert.Equal(1, flips);
        Assert.False(cur.Wide);
    }

    [Fact]
    public void Promotion_costs_the_reserve_and_demotion_is_free()
    {
        float inBand = L.WideEnterW + L.PromotionHysteresisW * 0.5f;
        Assert.False(StepTall(inBand, L.CompactStage).Wide);
        Assert.True(StepTall(inBand, L.WideStage).Wide);
        Assert.True(StepTall(L.WideEnterW + L.PromotionHysteresisW, L.CompactStage).Wide);
        Assert.False(StepTall(L.WideEnterW - 1f, L.WideStage).Wide);
    }

    [Fact]
    public void Narrowing_never_adds()
    {
        int prev = int.MinValue;
        for (float w = 0f; w <= SweepMax; w += 1f)
        {
            int r = SeedTall(w).Richness;
            Assert.True(r >= prev, $"richness went DOWN as the window widened, at {w}");
            prev = r;
        }
    }

    [Fact]
    public void The_width_ladder_has_exactly_two_shapes()
    {
        var seen = new HashSet<L>();
        for (float w = 0f; w <= SweepMax; w += 1f) seen.Add(SeedTall(w));
        Assert.Equal(2, seen.Count);
        Assert.Contains(L.WideStage, seen);
        Assert.Contains(L.CompactStage, seen);
    }

    // ── the height ladder ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void At_the_default_window_no_control_is_lost()
    {
        // THE reported defect, pinned: the fixed column overflowed its band and the output-device line fell off the
        // bottom. The ladder spends the shortfall on the COVER instead.
        var l = L.Seed(1180f, DefaultColumnAvailH);
        Assert.True(l.Wide);
        Assert.True(l.ShowDeviceLine, "the output-device line was the control this defect ate");
        Assert.True(l.ShowVolume);
        Assert.True(l.ShowSatellites);
        Assert.True(L.ColumnChromeH(l.Folded, l.PlayBox) + l.ArtSize <= DefaultColumnAvailH);
        Assert.True(l.ArtSize < L.WideArtW, "the cover is what absorbed the shortfall");
        Assert.True(l.ArtSize >= L.MinArtW);
    }

    [Fact]
    public void The_column_never_exceeds_its_band()
    {
        for (float h = 0f; h <= 1400f; h += 1f)
        {
            var l = L.Seed(1180f, h);
            if (!l.Wide) continue;
            Assert.True(L.ColumnChromeH(l.Folded, l.PlayBox) + l.ArtSize <= h + 0.001f,
                $"the wide column wants more than the {h} DIP it was given");
        }
    }

    [Fact]
    public void A_control_only_folds_when_keeping_it_would_break_the_cover_floor()
    {
        const float box = L.WidePlayBoxW;
        for (float h = 0f; h <= 1400f; h += 1f)
        {
            var l = L.Seed(1180f, h);
            if (!l.Wide) continue;
            Assert.True(l.ArtSize >= L.MinArtW);
            if (!l.ShowDeviceLine)
                Assert.True(h - L.ColumnChromeH(C.None, box) < L.MinArtW, $"the device line folded early at h={h}");
            if (!l.ShowVolume)
                Assert.True(h - L.ColumnChromeH(C.OutputDevice, box) < L.MinArtW, $"the volume row folded early at h={h}");
        }
    }

    [Fact]
    public void The_cover_is_quantised_so_a_resize_pixel_is_not_a_rerender()
    {
        var seen = new HashSet<L>();
        for (float h = 0f; h <= 1400f; h += 1f)
        {
            var l = L.Seed(1180f, h);
            if (l.Wide) Assert.Equal(0f, l.ArtSize % L.ArtQuantum);
            seen.Add(l);
        }
        Assert.True(seen.Count <= 1400f / L.ArtQuantum + 8f, $"{seen.Count} distinct layouts across a 1400 DIP sweep");
    }

    [Fact]
    public void A_vertical_sweep_folds_each_rung_at_most_once()
    {
        foreach (var rung in new[] { C.OutputDevice, C.Volume })
        {
            var cur = L.Seed(1180f, 1400f);
            int flips = 0;
            for (float h = 1400f; h >= 0f; h -= 1f)
            {
                var next = L.Resolve(1180f, h, cur);
                if (next.Wide && cur.Wide && next.Shows(rung) != cur.Shows(rung)) flips++;
                cur = next;
            }
            Assert.True(flips <= 1, $"{rung} folded/unfolded {flips} times on one downward sweep");
        }
    }

    [Fact]
    public void Unfolding_costs_the_height_reserve()
    {
        // Find the height at which the device line folds on the way DOWN, then prove it does not unfold one DIP later.
        var cur = L.Seed(1180f, 1400f);
        float foldAt = -1f;
        for (float h = 1400f; h >= 0f; h -= 1f)
        {
            var next = L.Resolve(1180f, h, cur);
            if (cur.Wide && next.Wide && cur.ShowDeviceLine && !next.ShowDeviceLine) { foldAt = h; cur = next; break; }
            cur = next;
        }
        Assert.True(foldAt > 0f, "the device line never folded");
        Assert.False(L.Resolve(1180f, foldAt + 1f, cur).ShowDeviceLine);
        Assert.True(L.Resolve(1180f, foldAt + L.FoldHysteresisH + 1f, cur).ShowDeviceLine);
    }

    [Fact]
    public void Growing_either_axis_never_takes_something_away()
    {
        for (float h = 120f; h <= 1200f; h += 20f)
        {
            int prev = int.MinValue;
            for (float w = 0f; w <= SweepMax; w += 20f)
            {
                int r = L.Seed(w, h).Richness;
                Assert.True(r >= prev, $"richness dropped as the window WIDENED at w={w}, h={h}");
                prev = r;
            }
        }
        for (float w = 620f; w <= SweepMax; w += 40f)
        {
            int prev = int.MinValue;
            for (float h = 0f; h <= 1200f; h += 20f)
            {
                int r = L.Seed(w, h).Richness;
                Assert.True(r >= prev, $"richness dropped as the window grew TALLER at w={w}, h={h}");
                prev = r;
            }
        }
    }

    [Fact]
    public void The_height_threshold_is_derived_from_the_ladder()
    {
        Assert.Equal(L.ColumnChromeH(L.HeightFoldable, L.WidePlayBoxW) + L.MinArtW, L.WideEnterH);
        Assert.False(L.Seed(1180f, L.WideEnterH - 1f).Wide);
        Assert.True(L.Seed(1180f, L.WideEnterH).Wide);
    }

    [Fact]
    public void The_satellites_are_not_a_height_rung()
    {
        Assert.Equal(C.None, L.HeightFoldable & C.Shuffle);
        Assert.Equal(C.None, L.HeightFoldable & C.Repeat);
        Assert.Equal(L.ColumnChromeH(C.None, L.WidePlayBoxW), L.ColumnChromeH(C.Shuffle | C.Repeat, L.WidePlayBoxW));
    }

    [Fact]
    public void The_non_art_chrome_totals_exactly_320_with_everything_shown()
        => Assert.Equal(320f, L.ColumnChromeH(C.None, L.WidePlayBoxW));

    // ── the sizes and the fold ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_wide_stage_is_the_authored_geometry()
    {
        var w = L.WideStage;
        Assert.True(w.Wide);
        Assert.Equal(352f, w.ColumnWidth);
        Assert.Equal(300f, w.ArtSize);
        Assert.Equal(56f, w.PlayBox);
        Assert.Equal(40f, w.StepBox);
        Assert.Equal(32f, w.SatelliteBox);
        Assert.True(w.PlayBox > w.StepBox && w.StepBox > w.SatelliteBox);
    }

    [Fact]
    public void The_compact_stage_is_the_same_ladder_one_rung_down()
    {
        var c = L.CompactStage;
        var w = L.WideStage;
        Assert.False(c.Wide);
        Assert.Equal(64f, c.ArtSize);
        Assert.Equal(40f, c.PlayBox);
        Assert.Equal(32f, c.StepBox);
        Assert.True(c.ArtSize < w.ArtSize && c.PlayBox < w.PlayBox && c.StepBox < w.StepBox);
    }

    [Fact]
    public void The_column_box_is_the_designed_column_and_the_gap_is_the_bands()
    {
        Assert.Equal(L.WideColumnW, L.WideStage.LayoutWidth);
        Assert.Equal(L.WideColumnW - 2f * L.ColumnPadX, L.ColumnContentW);
        Assert.True(L.RegionGapW > 0f);
        Assert.Equal(0f, L.CompactStage.LayoutWidth);
    }

    [Fact]
    public void Folded_means_moved_address_never_lost()
    {
        Assert.Equal(C.None, L.WideStage.Folded);
        Assert.Equal(C.Shuffle | C.Repeat | C.Volume | C.OutputDevice, L.CompactStage.Folded);
        Assert.True(L.CompactStage.ShowOverflow);
        Assert.False(L.WideStage.ShowOverflow);
        foreach (var c in new[] { C.Shuffle, C.Repeat, C.Volume, C.OutputDevice })
        {
            Assert.True(L.WideStage.Shows(c));
            Assert.False(L.CompactStage.Shows(c));
        }
    }

    // ── the scrim ladder ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_scrim_is_one_continuous_system_with_a_plateau()
    {
        Assert.True(L.ScrimTopA > L.ScrimBaseA);
        Assert.True(L.ScrimBottomA > L.ScrimBaseA);
        Assert.True(L.ScrimBaseA > 0f && L.ScrimTopA < 1f);
        Assert.True(L.ScrimTopStop > 0f && L.ScrimTopStop < L.ScrimBottomStop && L.ScrimBottomStop < 1f);
        Assert.True(L.ScrimTopStop >= 0.2f, "the top feather is too short to be edgeless");
        Assert.True(1f - L.ScrimBottomStop >= 0.2f, "the bottom feather is too short to be edgeless");
        Assert.True(L.ScrimBottomStop - L.ScrimTopStop >= 0.3f, "a real flat middle, not two ramps meeting");
    }

    [Fact]
    public void The_column_shade_overhangs_the_box_and_feathers_to_zero_along_a_curve()
    {
        Assert.Equal(L.WideColumnW + L.ColumnShadeFalloffW, L.ColumnShadeW);
        Assert.True(L.ColumnShadeW > L.WideStage.LayoutWidth, "a shade that stops at the box edge IS the edge");
        Assert.True(L.ColumnShadeFalloffW >= 2f * L.RegionGapW);
        Assert.True(L.ColumnShadeFalloffW >= 240f, "a short ramp to zero still reads as a smear");
        Assert.Equal(L.WideColumnW / L.ColumnShadeW, L.ColumnShadeHoldStop, 4);
        Assert.True(L.ColumnShadeMidStop > L.ColumnShadeHoldStop && L.ColumnShadeMidStop < 1f);
        Assert.True(L.ColumnShadeMidFrac > 0f && L.ColumnShadeMidFrac < 1f);
    }

    [Fact]
    public void The_pane_shade_feathers_up_from_zero_on_its_leading_edge()
    {
        Assert.True(L.PaneShadeA > 0f && L.PaneShadeA < L.ScrimTopA);
        Assert.True(L.PaneShadeFeatherStop > 0f && L.PaneShadeFeatherStop < 0.5f);
    }

    [Fact]
    public void The_scrim_plateau_and_the_accent_ground_are_one_number()
        => Assert.Equal(Design.StageArm.ScrimBaseA, L.ScrimBaseA);
}

public class StagePaneTests
{
    [Fact]
    public void The_pane_choice_is_last_pane_wins_and_toggles_between_the_two()
    {
        int before = Stage.Pane.Current.Peek();
        try
        {
            Stage.Pane.Current.Value = Stage.Pane.Lyrics;
            Stage.Pane.Toggle();
            Assert.Equal(Stage.Pane.Queue, Stage.Pane.Current.Peek());
            Stage.Pane.Toggle();
            Assert.Equal(Stage.Pane.Lyrics, Stage.Pane.Current.Peek());
        }
        finally { Stage.Pane.Current.Value = before; }
    }
}
