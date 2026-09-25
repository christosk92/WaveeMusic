// ── Wavee.Tests/WordsRailTests.cs — the word rail's pure decisions, and the ledger bar's (Controls.Words / Controls.Podcast)
//
// Podcast wave P1 (owner O). The rail and the ledger are engine-bound components, so — the house rule — the DECISIONS
// inside them are pure functions and it is those that are pinned here: which value a word writes, what a tap on the
// active word means, when the underline slides and from where, how the ledger splits a show into its three parts and
// where the flex puts them (the slide's start). No window, no loop, no element is rendered, and no source is read.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class WordsRailTests
{
    [Fact]
    public void A_word_without_a_code_writes_its_position()
        => Assert.Equal(3, Controls.Words.CodeOf(new Controls.Words.Word("oldest"), 3));

    [Fact]
    public void A_word_with_a_code_writes_the_code_not_its_position()
    {
        // The library navigator's sort codes are persisted and are not rail positions (albums: recents, a-z, artist, …).
        var word = new Controls.Words.Word("added", Code: (int)LibraryNavSort.RecentlyAdded);
        Assert.Equal((int)LibraryNavSort.RecentlyAdded, Controls.Words.CodeOf(word, 3));
    }

    [Fact]
    public void Tapping_the_active_word_is_a_reselect()
        => Assert.True(Controls.Words.IsReselect(selected: 2, tapped: 2));

    [Fact]
    public void Tapping_another_word_selects_it()
        => Assert.False(Controls.Words.IsReselect(selected: 2, tapped: 0));

    [Fact]
    public void The_underline_slides_from_the_old_word_to_the_new_one()
    {
        // Old word at x 0, 40 wide; new word at x 60, 20 wide: the new bar starts 60 to the left at twice its width.
        var s = Controls.Words.SlideFrom(0f, 40f, 60f, 20f);
        Assert.True(s.Animate);
        Assert.Equal(-60.0f, s.Dx, 3);
        Assert.Equal(2.0f, s.Scale, 3);
    }

    [Theory]
    [InlineData(0f, 0f, 60f, 20f)]      // the old word never laid out (the first frame)
    [InlineData(0f, 40f, 60f, 0f)]      // the new word is collapsed (a hidden tab)
    [InlineData(10f, 40f, 10f, 40f)]    // same rect: nothing to move
    [InlineData(float.NaN, 40f, 60f, 20f)]
    public void Nothing_slides_without_two_real_distinct_rects(float fromX, float fromW, float toX, float toW)
        => Assert.False(Controls.Words.SlideFrom(fromX, fromW, toX, toW).Animate);

    [Fact]
    public void The_rail_keeps_the_library_metrics()
    {
        // The library rework's W8 values — moving the rail into Controls.Words must not move a library pixel.
        Assert.Equal(13.5f, Controls.Words.Size);
        Assert.Equal(18f, Controls.Words.Line);
        Assert.Equal(14f, Controls.Words.Gap);
        Assert.Equal(32f, Controls.Words.Height);
    }
}

public class WordsRailRevealTests
{
    // A real rail's grain: five words 52 / 86 / 40 / 108 / 84 DIP wide, 14-DIP gaps — built once, never mutated.
    static readonly float[] W = [52f, 86f, 40f, 108f, 84f];
    static readonly float[] X = BuildX(W, 14f);
    static readonly float Content = X[^1] + W[^1];
    const float Band = Controls.Words.FadeBand;

    static float[] BuildX(float[] w, float gap)
    {
        var x = new float[w.Length];
        float cur = 0f;
        for (int i = 0; i < w.Length; i++) { x[i] = cur; cur += w[i] + gap; }
        return x;
    }

    [Fact]
    public void Every_word_lands_fully_visible_and_clear_of_its_live_feather()
    {
        for (float viewport = 60f; viewport <= 520f; viewport += 20f)
        {
            float maxOffset = MathF.Max(0f, Content - viewport);
            float[] starts = [0f, 40f, 120f, 250f, maxOffset];
            foreach (float start in starts)
            {
                for (int i = 0; i < W.Length; i++)
                {
                    float t = Controls.Words.RevealOffset(X[i], W[i], viewport, Content, start, Band);
                    float offset = float.IsNaN(t) ? start : t;
                    string ctx = $"viewport={viewport} start={start} word={i}";

                    // A word narrower than the viewport is fully inside the reveal's own destination window —
                    // never partially clipped by the reveal it produced.
                    if (W[i] < viewport)
                    {
                        Assert.True(X[i] >= offset - 0.01f, ctx);
                        Assert.True(X[i] + W[i] <= offset + viewport + 0.01f, ctx);
                    }

                    // And, when both bands fit beside it, clear of whichever feather is actually LIVE at the
                    // landing offset (a feather at an edge with nothing past it is not drawn, so it cannot clip).
                    if (W[i] + 2f * Band <= viewport)
                    {
                        bool leadingLive = offset > 0.5f;
                        bool trailingLive = offset < maxOffset - 0.5f;
                        if (leadingLive) Assert.True(X[i] >= offset + Band - 0.01f, ctx);
                        if (trailingLive) Assert.True(X[i] + W[i] <= offset + viewport - Band + 0.01f, ctx);
                    }
                }
            }
        }
    }

    [Fact]
    public void The_first_word_lands_at_the_leading_edge()
    {
        const float viewport = 200f;
        float start = MathF.Max(0f, Content - viewport);   // scrolled all the way to the tail
        Assert.Equal(0f, Controls.Words.RevealOffset(X[0], W[0], viewport, Content, start, Band), 2);
    }

    [Fact]
    public void The_last_word_lands_at_the_maximum_offset()
    {
        const float viewport = 200f;
        float max = Content - viewport;
        Assert.Equal(max, Controls.Words.RevealOffset(X[^1], W[^1], viewport, Content, 0f, Band), 2);
    }

    [Fact]
    public void An_already_visible_word_does_not_move()
        => Assert.True(float.IsNaN(Controls.Words.RevealOffset(X[0], W[0], 200f, Content, 0f, Band)));

    [Fact]
    public void A_word_wider_than_the_viewport_pins_its_leading_edge()
    {
        // Word 3 is 108 DIP; a 60-DIP viewport cannot fit it clear of the band on either side.
        float t = Controls.Words.RevealOffset(X[3], W[3], 60f, Content, 0f, Band);
        Assert.Equal(X[3], t, 2);
    }

    [Fact]
    public void A_collapsed_word_never_moves_the_rail()
        => Assert.True(float.IsNaN(Controls.Words.RevealOffset(100f, 0f, 200f, Content, 0f, Band)));

    [Fact]
    public void A_word_with_no_laid_out_x_never_moves_the_rail()
        => Assert.True(float.IsNaN(Controls.Words.RevealOffset(float.NaN, 50f, 200f, Content, 0f, Band)));
}

public class WordsRailRevealPolicyTests
{
    [Fact]
    public void The_first_frame_snaps()
        => Assert.Equal(Controls.Words.Reveal.Snap, Controls.Words.RevealFor(int.MinValue, 2, reducedMotion: false));

    [Fact]
    public void An_unchanged_selection_snaps()
        => Assert.Equal(Controls.Words.Reveal.Snap, Controls.Words.RevealFor(2, 2, reducedMotion: false));

    [Fact]
    public void Reduced_motion_always_snaps()
        => Assert.Equal(Controls.Words.Reveal.Snap, Controls.Words.RevealFor(0, 3, reducedMotion: true));

    [Fact]
    public void A_real_change_glides()
        => Assert.Equal(Controls.Words.Reveal.Glide, Controls.Words.RevealFor(0, 3, reducedMotion: false));
}

public class WordsRailGeometryKeyTests
{
    [Fact]
    public void A_sub_DIP_wobble_in_the_same_cell_keeps_the_same_key()
        => Assert.Equal(Controls.Words.GeometryKey(400f, 800f), Controls.Words.GeometryKey(400.6f, 800.6f));

    [Fact]
    public void A_viewport_change_changes_the_key()
        => Assert.NotEqual(Controls.Words.GeometryKey(400f, 800f), Controls.Words.GeometryKey(440f, 800f));

    [Fact]
    public void A_content_change_changes_the_key()
        => Assert.NotEqual(Controls.Words.GeometryKey(400f, 800f), Controls.Words.GeometryKey(400f, 840f));
}

public class LedgerBarTests
{
    [Fact]
    public void The_three_parts_split_the_whole()
    {
        var (played, progress, toGo) = Controls.LedgerPartsOf(0.5f, 0.25f);
        Assert.Equal(0.5f, played, 4);
        Assert.Equal(0.25f, progress, 4);
        Assert.Equal(0.25f, toGo, 4);
    }

    [Fact]
    public void Progress_never_overlaps_what_is_played()
    {
        var (played, progress, toGo) = Controls.LedgerPartsOf(0.9f, 0.5f);
        Assert.Equal(0.9f, played, 4);
        Assert.Equal(0.1f, progress, 4);
        Assert.Equal(Controls.LedgerFloor, toGo, 4);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(float.NaN, -1f)]
    public void An_empty_or_garbage_ledger_is_all_to_go_with_floored_stubs(float played, float progress)
    {
        var (p, g, t) = Controls.LedgerPartsOf(played, progress);
        Assert.Equal(Controls.LedgerFloor, p, 4);
        Assert.Equal(Controls.LedgerFloor, g, 4);
        Assert.Equal(1.0f, t, 4);
    }

    [Fact]
    public void The_geometry_fills_the_bar_with_its_two_gaps()
    {
        Span<float> x = stackalloc float[3];
        Span<float> w = stackalloc float[3];
        Controls.LedgerGeometry(204f, 0.5f, 0.25f, 0.25f, x, w);
        Assert.Equal(100.0f, w[0], 3);
        Assert.Equal(50.0f, w[1], 3);
        Assert.Equal(50.0f, w[2], 3);
        Assert.Equal(0.0f, x[0], 3);
        Assert.Equal(102.0f, x[1], 3);
        Assert.Equal(154.0f, x[2], 3);
        Assert.Equal(204.0f, x[2] + w[2], 3);
    }

    [Fact]
    public void A_sliver_part_keeps_its_minimum_and_the_rest_share_what_is_left()
    {
        Span<float> x = stackalloc float[3];
        Span<float> w = stackalloc float[3];
        Controls.LedgerGeometry(104f, Controls.LedgerFloor, Controls.LedgerFloor, 1f, x, w);
        Assert.Equal(Controls.LedgerMinPart, w[0], 3);
        Assert.Equal(Controls.LedgerMinPart, w[1], 3);
        Assert.Equal(104f - 2f * Controls.LedgerGap - 2f * Controls.LedgerMinPart, w[2], 3);
        Assert.Equal(104.0f, x[2] + w[2], 3);
    }
}
