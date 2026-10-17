// ── Wavee.Tests/PodcastLedgerRulesTests.cs — the played ledger's pure geometry, as a core rule file (C16, X46) ───────
//
// The part weights and the slide geometry are pinned fact by fact in WordsRailTests (`LedgerBarTests`); these facts add
// what the move to `PodcastLedgerRules` promises on top: the metrics are the ones the ledger always drew with, the
// arithmetic is deterministic for every input a model can hand it, and it allocates nothing (it runs inside a slide).

using Xunit;

namespace Wavee.Tests;

public class PodcastLedgerRulesTests
{
    [Fact]
    public void The_metrics_are_the_ones_the_ledger_always_drew_with()
    {
        Assert.Equal(4f, PodcastLedgerRules.Height);
        Assert.Equal(2f, PodcastLedgerRules.Gap);
        Assert.Equal(2f, PodcastLedgerRules.MinPart);
        Assert.Equal(0.45f, PodcastLedgerRules.ProgressTone);
        Assert.Equal(0.001f, PodcastLedgerRules.Floor);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(2f, -3f)]
    [InlineData(float.NaN, float.NaN)]
    [InlineData(float.PositiveInfinity, float.NegativeInfinity)]
    [InlineData(0.3f, 0.9f)]
    public void Every_weight_is_floored_and_the_three_never_exceed_the_whole_by_more_than_the_floors(float played, float progress)
    {
        var (a, b, c) = PodcastLedgerRules.PartsOf(played, progress);

        Assert.True(a >= PodcastLedgerRules.Floor && b >= PodcastLedgerRules.Floor && c >= PodcastLedgerRules.Floor);
        Assert.True(a + b + c <= 1f + 3f * PodcastLedgerRules.Floor);
    }

    [Fact]
    public void A_zero_width_bar_pins_every_part_at_its_minimum_and_never_goes_negative()
    {
        Span<float> x = stackalloc float[3];
        Span<float> w = stackalloc float[3];
        PodcastLedgerRules.Geometry(0f, 0.5f, 0.25f, 0.25f, x, w);

        for (int i = 0; i < 3; i++) Assert.True(w[i] >= 0f);
        Assert.Equal(PodcastLedgerRules.MinPart, w[0], 3);
        Assert.Equal(PodcastLedgerRules.MinPart, w[1], 3);
        Assert.Equal(PodcastLedgerRules.MinPart, w[2], 3);
    }

    [Fact]
    public void The_arithmetic_allocates_nothing()
    {
        Span<float> x = stackalloc float[3];
        Span<float> w = stackalloc float[3];
        for (int i = 0; i < 8; i++)                                    // warm the JIT before the measured run
        {
            _ = PodcastLedgerRules.PartsOf(0.4f, 0.2f);
            PodcastLedgerRules.Geometry(240f, 0.4f, 0.2f, 0.4f, x, w);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            var (a, b, c) = PodcastLedgerRules.PartsOf(0.4f, 0.2f);
            PodcastLedgerRules.Geometry(240f, a, b, c, x, w);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }
}
