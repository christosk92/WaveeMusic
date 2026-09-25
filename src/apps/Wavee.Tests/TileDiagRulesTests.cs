// ── Wavee.Tests/TileDiagRulesTests.cs — the Diagnostics "Tiles" card's pure decisions ──────────────────────────────
//
// MiB/percent formatting, the health verdict, the nonzero invalidation reasons, and the GPU pass timeline's per-pass
// rows and per-kind aggregate (Screens/Diagnostics.Tiles.cs).

using System.Linq;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class TileDiagRulesTests
{
    static TileCensus Census(
        int exposedMissing = 0, int degradedSlices = 0, int coverageClamps = 0,
        long residentBytes = 0, long budgetBytes = 0,
        int noTexture = 0, int content = 0, int primCount = 0, int validRectChanged = 0, int scaleChanged = 0,
        int sliceGeometry = 0, int backgroundOrTheme = 0, int evictedReason = 0, int degraded = 0)
        => new()
        {
            ExposedMissing = exposedMissing, DegradedSlices = degradedSlices, CoverageClamps = coverageClamps,
            ResidentBytes = residentBytes, BudgetBytes = budgetBytes,
            NoTexture = noTexture, Content = content, PrimCount = primCount, ValidRectChanged = validRectChanged,
            ScaleChanged = scaleChanged, SliceGeometry = sliceGeometry, BackgroundOrTheme = backgroundOrTheme,
            EvictedReason = evictedReason, Degraded = degraded,
        };

    // ── IsWarning ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_all_zero_census_within_budget_is_healthy()
        => Assert.False(TileDiagRules.IsWarning(Census(residentBytes: 10, budgetBytes: 100)));

    [Fact]
    public void An_exposed_missing_tile_is_a_warning()
        => Assert.True(TileDiagRules.IsWarning(Census(exposedMissing: 1)));

    [Fact]
    public void A_degraded_slice_is_a_warning()
        => Assert.True(TileDiagRules.IsWarning(Census(degradedSlices: 1)));

    [Fact]
    public void A_coverage_clamp_is_a_warning()
        => Assert.True(TileDiagRules.IsWarning(Census(coverageClamps: 1)));

    /// <summary>A stale tile (valid pixels the stream no longer describes — evidence-diagnostics §A.1) is a warning.</summary>
    [Fact]
    public void A_stale_tile_is_a_warning()
        => Assert.True(TileDiagRules.IsWarning(Census(residentBytes: 10, budgetBytes: 100) with { StaleTiles = 1 }));

    [Fact]
    public void Over_budget_residency_is_a_warning()
        => Assert.True(TileDiagRules.IsWarning(Census(residentBytes: 200, budgetBytes: 100)));

    [Fact]
    public void A_zero_budget_never_counts_as_over_budget()
        => Assert.False(TileDiagRules.OverBudget(Census(residentBytes: 200, budgetBytes: 0)));

    [Fact]
    public void Resident_bytes_exactly_at_budget_is_not_over_budget()
        => Assert.False(TileDiagRules.OverBudget(Census(residentBytes: 100, budgetBytes: 100)));

    // ── formatting ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, "0.0 MiB")]
    [InlineData(1024L * 1024L, "1.0 MiB")]
    [InlineData(1536L * 1024L, "1.5 MiB")]
    [InlineData(512L * 1024L, "0.5 MiB")]
    public void Bytes_format_as_MiB_with_one_decimal(long bytes, string expected)
        => Assert.Equal(expected, TileDiagRules.FormatMiB(bytes));

    [Theory]
    [InlineData(0L, 0L, 0)]
    [InlineData(50L, 100L, 50)]
    [InlineData(100L, 100L, 100)]
    [InlineData(200L, 100L, 100)]
    [InlineData(50L, 0L, 0)]
    [InlineData(50L, -1L, 0)]
    public void Budget_percent_clamps_to_0_100_and_a_missing_budget_reads_zero(long resident, long budget, int expected)
        => Assert.Equal(expected, TileDiagRules.BudgetPercentClamped(resident, budget));

    [Theory]
    [InlineData(0f, "0.00")]
    [InlineData(1.5f, "1.50")]
    [InlineData(12.34f, "12.34")]
    [InlineData(0.001f, "0.00")]
    public void Ms_formats_with_two_decimals(float ms, string expected)
        => Assert.Equal(expected, TileDiagRules.FormatMs(ms));

    // ── invalidation reasons ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_all_zero_census_has_no_reason_rows()
        => Assert.Empty(TileDiagRules.ReasonRows(Census()));

    [Fact]
    public void Only_nonzero_reasons_appear_in_the_census_field_order()
    {
        var c = Census(content: 3, scaleChanged: 1, degraded: 2);
        var rows = TileDiagRules.ReasonRows(c);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new TileDiagRules.ReasonRow(TileDiagRules.ReasonKind.Content, 3), rows[0]);
        Assert.Equal(new TileDiagRules.ReasonRow(TileDiagRules.ReasonKind.ScaleChanged, 1), rows[1]);
        Assert.Equal(new TileDiagRules.ReasonRow(TileDiagRules.ReasonKind.Degraded, 2), rows[2]);
    }

    [Fact]
    public void Every_reason_field_maps_to_its_own_reason_kind()
    {
        var c = Census(
            noTexture: 1, content: 1, primCount: 1, validRectChanged: 1, scaleChanged: 1,
            sliceGeometry: 1, backgroundOrTheme: 1, evictedReason: 1, degraded: 1);
        var rows = TileDiagRules.ReasonRows(c);
        Assert.Equal(9, rows.Count);
        TileDiagRules.ReasonKind[] expected =
        [
            TileDiagRules.ReasonKind.NoTexture, TileDiagRules.ReasonKind.Content, TileDiagRules.ReasonKind.PrimCount,
            TileDiagRules.ReasonKind.ValidRectChanged, TileDiagRules.ReasonKind.ScaleChanged, TileDiagRules.ReasonKind.SliceGeometry,
            TileDiagRules.ReasonKind.BackgroundOrTheme, TileDiagRules.ReasonKind.Evicted, TileDiagRules.ReasonKind.Degraded,
        ];
        Assert.Equal(expected, rows.Select(r => r.Reason).ToArray());
    }

    // ── GPU pass timeline: per-pass rows ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_passes_yields_no_rows()
        => Assert.Empty(TileDiagRules.PassRows(ReadOnlySpan<GpuPassTiming>.Empty));

    [Fact]
    public void Pass_rows_preserve_recorded_order_and_format_each_ms()
    {
        GpuPassTiming[] passes =
        [
            new(GpuPassKind.Clear, 1920, 1080, 0.5f),
            new(GpuPassKind.Scene, 1920, 1080, 3.14f),
        ];
        var rows = TileDiagRules.PassRows(passes);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new TileDiagRules.PassRow(GpuPassKind.Clear, 1920, 1080, "0.50"), rows[0]);
        Assert.Equal(new TileDiagRules.PassRow(GpuPassKind.Scene, 1920, 1080, "3.14"), rows[1]);
    }

    // ── GPU pass timeline: per-kind aggregate ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_passes_yields_no_aggregate_rows()
        => Assert.Empty(TileDiagRules.AggregatePasses(ReadOnlySpan<GpuPassTiming>.Empty));

    [Fact]
    public void Aggregate_sums_ms_and_counts_per_kind_heaviest_first()
    {
        GpuPassTiming[] passes =
        [
            new(GpuPassKind.TileRaster, 256, 256, 1.0f),
            new(GpuPassKind.TileRaster, 256, 256, 1.0f),
            new(GpuPassKind.Composite, 1920, 1080, 5.0f),
            new(GpuPassKind.Clear, 1920, 1080, 0.1f),
        ];
        var rows = TileDiagRules.AggregatePasses(passes);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new TileDiagRules.AggregateRow(GpuPassKind.Composite, 1, "5.00"), rows[0]);
        Assert.Equal(new TileDiagRules.AggregateRow(GpuPassKind.TileRaster, 2, "2.00"), rows[1]);
        Assert.Equal(new TileDiagRules.AggregateRow(GpuPassKind.Clear, 1, "0.10"), rows[2]);
    }

    [Fact]
    public void A_single_pass_kind_aggregates_to_one_row()
    {
        GpuPassTiming[] passes = [new(GpuPassKind.Uploads, 64, 64, 2.5f)];
        var rows = TileDiagRules.AggregatePasses(passes);
        Assert.Equal(new TileDiagRules.AggregateRow(GpuPassKind.Uploads, 1, "2.50"), Assert.Single(rows));
    }
}
