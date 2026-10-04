// ── Wavee.Tests/VisualizerCoversTests.cs — the Zune faces' pure rules (Shell/Visualizer.Covers.cs) ─────────────────────
//
// Pure: `Visualizer.Covers` takes plain ints and floats (image ids are raw `StringId.Value`s, 0 = none) and touches no
// engine, no signal and no table, so nothing here needs a scope. The faces that bind these (Visualizer.Zune.UI.cs) are
// covered by the `--fake` walk, not a unit test. Plan: viz-app-plan.md §2.15-§2.17, §3.6, §6.1.

using Wavee;
using Xunit;

using C = Wavee.Visualizer.Covers;

namespace Wavee.Tests;

public class VisualizerCoversTests
{
    // ── Collect ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Collect_orders_current_then_queue_then_recents()
    {
        var into = new int[C.Cap];
        int n = C.Collect(7, [3, 4], [9, 8], into);
        Assert.Equal(new[] { 7, 3, 4, 9, 8 }, into[..n]);
    }

    [Fact]
    public void Collect_dedupes_by_image_id_and_skips_missing_images()
    {
        var into = new int[C.Cap];
        int n = C.Collect(5, [0, 5, 6, 6, 0], [6, 7, 5, 0], into);
        Assert.Equal(new[] { 5, 6, 7 }, into[..n]);
    }

    [Fact]
    public void Collect_without_a_current_cover_starts_at_the_queue()
    {
        var into = new int[C.Cap];
        int n = C.Collect(0, [2], [1], into);
        Assert.Equal(new[] { 2, 1 }, into[..n]);
    }

    [Fact]
    public void Collect_caps_at_forty_and_at_the_buffer()
    {
        var queue = new int[60];
        for (int i = 0; i < queue.Length; i++) queue[i] = i + 1;
        var big = new int[100];
        Assert.Equal(C.Cap, C.Collect(1000, queue, [2000], big));
        Assert.Equal(1000, big[0]);
        Assert.DoesNotContain(2000, big[..C.Cap]);
        var small = new int[5];
        Assert.Equal(5, C.Collect(1000, queue, [], small));
    }

    [Fact]
    public void Ready_needs_twelve_distinct_covers()
    {
        Assert.False(C.Ready(0));
        Assert.False(C.Ready(C.MinPool - 1));
        Assert.True(C.Ready(C.MinPool));
        Assert.True(C.Ready(C.Cap));
    }

    // ── Tiles ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static int[] Coverage(C.Tile[] plan, int cols, int rows)
    {
        var hits = new int[cols * rows];
        foreach (var t in plan)
            for (int dr = 0; dr < t.Size; dr++)
                for (int dc = 0; dc < t.Size; dc++)
                {
                    int c = t.Col + dc, r = t.Row + dr;
                    Assert.InRange(c, 0, cols - 1);
                    Assert.InRange(r, 0, rows - 1);
                    hits[r * cols + c]++;
                }
        return hits;
    }

    [Theory]
    [InlineData(12, 7, 1u)]
    [InlineData(12, 7, 424242u)]
    [InlineData(12, 9, 77u)]
    [InlineData(6, 4, 3u)]
    [InlineData(1, 1, 9u)]
    public void Plan_covers_every_cell_exactly_once(int cols, int rows, uint seed)
    {
        var plan = C.Tiles.Plan(cols, rows, seed);
        Assert.All(Coverage(plan, cols, rows), h => Assert.Equal(1, h));
        Assert.All(plan, t => Assert.InRange(t.Size, 1, 2));
    }

    [Fact]
    public void Plan_is_deterministic_per_seed()
    {
        Assert.Equal(C.Tiles.Plan(12, 7, 99u), C.Tiles.Plan(12, 7, 99u));
        bool anyDiffers = false;
        for (uint s = 1; s < 20 && !anyDiffers; s++)
            anyDiffers = !C.Tiles.Plan(12, 7, s).AsSpan().SequenceEqual(C.Tiles.Plan(12, 7, s + 100u));
        Assert.True(anyDiffers);
    }

    [Fact]
    public void Plan_makes_some_big_tiles_and_none_when_forbidden()
    {
        int bigs = 0;
        for (uint s = 0; s < 20; s++) foreach (var t in C.Tiles.Plan(12, 7, s)) if (t.Size == 2) bigs++;
        Assert.InRange(bigs, 20, 400);                      // ~14 % of free slots across twenty walls
        var flat = C.Tiles.Plan(6, 4, 5u, big: false);
        Assert.Equal(24, flat.Length);
        Assert.All(flat, t => Assert.Equal(1, t.Size));
    }

    [Fact]
    public void Plan_of_an_empty_wall_is_empty()
    {
        Assert.Empty(C.Tiles.Plan(0, 7, 1u));
        Assert.Empty(C.Tiles.Plan(12, 0, 1u));
    }

    [Fact]
    public void RowsFor_fills_the_height_with_square_cells()
    {
        Assert.Equal(7, C.Tiles.RowsFor(1500f, 860f, 12, C.Tiles.MaxRows));   // cell 125 → 6.9 rows
        Assert.Equal(C.Tiles.MaxRows, C.Tiles.RowsFor(1000f, 4000f, 12, C.Tiles.MaxRows));
        Assert.Equal(1, C.Tiles.RowsFor(0f, 100f, 12, 9));
        Assert.Equal(3, C.Tiles.RowsFor(200f, 78f, 6, C.Tiles.PreviewMaxRows));
    }

    [Fact]
    public void Columns_map_low_to_high_bands_and_veils_clear_with_level()
    {
        Assert.Equal(0, C.Tiles.BandOf(0, 12));
        Assert.True(C.Tiles.BandOf(11, 12) > C.Tiles.BandOf(6, 12));
        Assert.InRange(C.Tiles.BandOf(11, 12), 0, C.Tiles.BandSpan - 1);
        Assert.Equal(0.55f, C.Tiles.VeilAlpha(0f), 4);
        Assert.Equal(0f, C.Tiles.VeilAlpha(1.2f));
        Assert.True(C.Tiles.VeilAlpha(0.5f) < C.Tiles.VeilAlpha(0.2f));
    }

    [Fact]
    public void DecodeFor_is_one_bounded_size_per_wall()
    {
        Assert.Equal(48, C.Tiles.DecodeFor(33f, 1, weak: false, preview: true));
        Assert.Equal(256, C.Tiles.DecodeFor(125f, 2, weak: false, preview: false));
        Assert.Equal(320, C.Tiles.DecodeFor(400f, 2, weak: false, preview: false));
        Assert.Equal(192, C.Tiles.DecodeFor(250f, 1, weak: true, preview: false));
        Assert.Equal(0, C.Tiles.DecodeFor(125f, 2, false, false) % 32);
    }

    [Fact]
    public void Assign_puts_the_current_cover_in_the_lead_tile_and_avoids_neighbour_repeats()
    {
        var plan = C.Tiles.Plan(12, 7, 11u);
        int lead = C.Tiles.LeadOf(plan);
        var covers = new int[plan.Length];
        C.Tiles.Assign(plan, 20, 11u, covers);
        Assert.Equal(0, covers[lead]);
        Assert.All(covers, c => Assert.InRange(c, 0, 19));
        for (int t = 1; t < covers.Length; t++)
            if (t != lead) Assert.NotEqual(covers[t - 1], covers[t]);
        var again = new int[plan.Length];
        C.Tiles.Assign(plan, 20, 11u, again);
        Assert.Equal(covers, again);
    }

    [Fact]
    public void Assign_with_no_pool_marks_every_tile_empty()
    {
        var plan = C.Tiles.Plan(6, 3, 1u, big: false);
        var covers = new int[plan.Length];
        C.Tiles.Assign(plan, 0, 1u, covers);
        Assert.All(covers, c => Assert.Equal(-1, c));
    }

    [Fact]
    public void LeadOf_is_the_first_big_tile_else_zero()
    {
        Assert.Equal(0, C.Tiles.LeadOf([new C.Tile(0, 0, 1), new C.Tile(1, 0, 1)]));
        Assert.Equal(1, C.Tiles.LeadOf([new C.Tile(0, 0, 1), new C.Tile(1, 0, 2), new C.Tile(3, 0, 2)]));
    }

    [Fact]
    public void FallbackSaturation_uses_three_rungs()
    {
        var seen = new HashSet<float>();
        for (int t = 0; t < 60; t++) seen.Add(C.Tiles.FallbackSaturation(t, 4u));
        Assert.True(seen.SetEquals([1f, 0.55f, 0.2f]));
    }

    // ── Flips ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pick_returns_two_distinct_tiles_never_the_previous_beats()
    {
        int lastA = -1, lastB = -1;
        for (int beat = 0; beat < 2000; beat++)
        {
            int got = C.Flips.Pick(beat, 70, lastA, lastB, 2, out int a, out int b);
            Assert.Equal(2, got);
            Assert.InRange(a, 0, 69);
            Assert.InRange(b, 0, 69);
            Assert.NotEqual(a, b);
            Assert.True(a != lastA && a != lastB && b != lastA && b != lastB, $"beat {beat} re-flipped a tile");
            lastA = a; lastB = b;
        }
    }

    [Fact]
    public void Pick_is_deterministic_and_honours_want()
    {
        C.Flips.Pick(17, 40, 3, 5, 2, out int a1, out int b1);
        C.Flips.Pick(17, 40, 3, 5, 2, out int a2, out int b2);
        Assert.Equal((a1, b1), (a2, b2));
        Assert.Equal(1, C.Flips.Pick(17, 40, -1, -1, 1, out int one, out int none));
        Assert.InRange(one, 0, 39);
        Assert.Equal(-1, none);
        Assert.Equal(0, C.Flips.Pick(17, 0, -1, -1, 2, out _, out _));
    }

    [Fact]
    public void Pick_on_a_tiny_wall_picks_what_it_can()
    {
        Assert.Equal(0, C.Flips.Pick(3, 2, 0, 1, 2, out _, out _));      // both tiles flipped last beat
        Assert.Equal(1, C.Flips.Pick(3, 3, 0, 1, 2, out int a, out _));
        Assert.Equal(2, a);
    }

    [Fact]
    public void NextCover_moves_to_a_different_cover_in_range()
    {
        for (int beat = 0; beat < 300; beat++)
            for (int cur = 0; cur < 12; cur++)
            {
                int next = C.Flips.NextCover(cur, beat, beat % 70, 12);
                Assert.InRange(next, 0, 11);
                Assert.NotEqual(cur, next);
            }
        Assert.Equal(0, C.Flips.NextCover(0, 5, 2, 1));
        Assert.Equal(1, C.Flips.NextCover(0, 5, 2, 2));
    }

    [Fact]
    public void A_flip_takes_about_a_second_and_calm_halves_the_rate()
    {
        float flip = 1f; int frames = 0;
        while (flip > -1f) { flip -= C.Flips.Step(1f / 60f, calm: false); frames++; }
        Assert.InRange(frames, 55, 60);                         // ≈ 0.95 s at 60 Hz
        Assert.Equal(C.Flips.Step(0.1f, false) * 0.5f, C.Flips.Step(0.1f, true), 5);
        Assert.Equal(0f, C.Flips.Step(-1f, false));
    }

    [Fact]
    public void ScaleOf_is_a_hairline_edge_on_never_zero()
    {
        Assert.Equal(1f, C.Flips.ScaleOf(1f));
        Assert.Equal(1f, C.Flips.ScaleOf(-1f));
        Assert.Equal(0.02f, C.Flips.ScaleOf(0f));
        Assert.Equal(0.5f, C.Flips.ScaleOf(-0.5f));
    }

    // ── Spotlight ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_photo_changes_only_on_an_eight_bar_downbeat()
    {
        int last = C.Spot.PhotoAt(C.Spot.CycleOf(0), 5);
        for (int bar = 1; bar < 200; bar++)
        {
            int photo = C.Spot.PhotoAt(C.Spot.CycleOf(bar), 5);
            if (photo != last) Assert.Equal(0, bar % C.Spot.BarsPerPhoto);
            if (bar % C.Spot.BarsPerPhoto == 0) Assert.NotEqual(last, photo);
            last = photo;
        }
    }

    [Fact]
    public void Cycles_wrap_the_gallery_and_survive_edge_input()
    {
        Assert.Equal(0, C.Spot.CycleOf(-4));
        Assert.Equal(0, C.Spot.CycleOf(7));
        Assert.Equal(1, C.Spot.CycleOf(8));
        Assert.Equal(-1, C.Spot.PhotoAt(3, 0));
        Assert.Equal(0, C.Spot.PhotoAt(9, 1));
        Assert.Equal(2, C.Spot.PhotoAt(5, 3));
        Assert.Equal(0, C.Spot.PhotoAt(-2, 3));
    }

    [Fact]
    public void SpanMs_is_eight_bars_of_the_tempo()
    {
        Assert.Equal(16_000f, C.Spot.SpanMs(1200), 1);         // 120 bpm: 32 beats × 500 ms
        Assert.Equal(C.Spot.DefaultSpanMs, C.Spot.SpanMs(0));
        Assert.Equal(60_000f, C.Spot.SpanMs(100));              // 10 bpm clamps
        Assert.Equal(8_000f, C.Spot.SpanMs(4000));              // 400 bpm clamps
        Assert.InRange(C.Spot.FadeMs(16_000f), 900f, 2_400f);
    }

    [Fact]
    public void Pan_alternates_direction_and_stays_in_frame()
    {
        for (int cycle = 0; cycle < 50; cycle++)
        {
            var p = C.Spot.Pan(cycle);
            Assert.Equal(p, C.Spot.Pan(cycle));
            Assert.True(p.ScaleTo > p.ScaleFrom && p.ScaleFrom >= 1f);
            // the pan never exceeds the overscan the scale buys: half of (scale − 1) on each side
            Assert.True(MathF.Abs(p.Dx) <= (p.ScaleFrom - 1f) * 0.5f + 0.011f);
            Assert.True(MathF.Abs(p.Dy) <= (p.ScaleFrom - 1f) * 0.5f);
            Assert.True(MathF.Sign(p.Dx) != MathF.Sign(C.Spot.Pan(cycle + 1).Dx));
        }
    }

    [Fact]
    public void ScrimAlpha_moves_a_tenth_either_way()
    {
        Assert.Equal(1f, C.Spot.ScrimAlpha(0f), 4);
        Assert.Equal(0.9f, C.Spot.ScrimAlpha(0.5f), 4);
        Assert.Equal(0.8f, C.Spot.ScrimAlpha(1f), 4);
        Assert.Equal(0.8f, C.Spot.ScrimAlpha(9f), 4);
    }

    [Fact]
    public void DecodeFor_caps_the_photo()
    {
        Assert.Equal(160, C.Spot.DecodeFor(200f, preview: true, weak: false));
        Assert.Equal(1508, C.Spot.DecodeFor(1508f, false, false));
        Assert.Equal(1920, C.Spot.DecodeFor(3840f, false, false));
        Assert.Equal(1280, C.Spot.DecodeFor(3840f, false, weak: true));
    }

    // ── Drift ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Wrap_folds_into_one_period()
    {
        Assert.Equal(10f, C.Drift.Wrap(110f, 100f), 3);
        Assert.Equal(90f, C.Drift.Wrap(-10f, 100f), 3);
        Assert.Equal(0f, C.Drift.Wrap(500f, 0f));
        Assert.Equal(0f, C.Drift.Wrap(float.NaN, 100f));
        for (float o = -1000f; o < 1000f; o += 37.3f)
        {
            float w = C.Drift.Wrap(o, 250f);
            Assert.True(w >= 0f && w < 250f);
        }
    }

    [Fact]
    public void Step_advances_and_stays_bounded_over_a_long_session()
    {
        float o = 0f;
        for (int i = 0; i < 100_000; i++) o = C.Drift.Step(o, C.Drift.Speed(1, 1f), 16.7f, 900f);
        Assert.True(o >= 0f && o < 900f);
        Assert.Equal(15f, C.Drift.Step(10f, 0.5f, 10f, 0f), 4);   // an unknown period does not wrap yet
    }

    [Fact]
    public void The_title_runs_against_the_other_rows_and_energy_speeds_them_up()
    {
        Assert.True(C.Drift.Speed(0, 0f) > 0f);
        Assert.True(C.Drift.Speed(1, 0f) < 0f);
        Assert.True(C.Drift.Speed(2, 0f) > 0f);
        for (int r = 0; r < C.Drift.Rows; r++)
            Assert.True(MathF.Abs(C.Drift.Speed(r, 1f)) > MathF.Abs(C.Drift.Speed(r, 0f)));
        Assert.Equal(0f, C.Drift.Drive(-1f, -1f));
        Assert.Equal(1f, C.Drift.Drive(3f, 3f));
    }

    [Fact]
    public void The_title_brightens_on_the_kick()
    {
        Assert.Equal(0.55f, C.Drift.TitleAlpha(0f, 0f), 4);
        Assert.True(C.Drift.TitleAlpha(1f, 0f) > C.Drift.TitleAlpha(0f, 0f));
        Assert.Equal(1f, C.Drift.TitleAlpha(1f, 1f));
        Assert.True(C.Drift.InkAlpha(0, dark: true) > C.Drift.InkAlpha(0, dark: false));
    }

    [Fact]
    public void Hash_is_stable_and_spreads()
    {
        Assert.Equal(C.Hash(1u, 2, 3), C.Hash(1u, 2, 3));
        Assert.NotEqual(C.Hash(1u, 2, 3), C.Hash(1u, 3, 2));
        float sum = 0f;
        for (int i = 0; i < 4000; i++) { float u = C.Unit(9u, i, 1); Assert.True(u >= 0f && u < 1f); sum += u; }
        Assert.InRange(sum / 4000f, 0.45f, 0.55f);
    }
}
