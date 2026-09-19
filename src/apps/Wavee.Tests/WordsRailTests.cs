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
