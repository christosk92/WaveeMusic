// ── Wavee.Tests/DiscographyEraBandsTests.cs — the resize-stable era grouping (ch 08 §8) ─────────────────────────────
//
// Ported from 0.2.9 `Wavee.Tests/DiscographyEraBandsTests.cs` (8 facts). The one input change is the port's own: the
// per-album year read moved to the caller, so `PlanAlbums(albums)` is `PlanYears(years)` and a release date's year is
// the caller's column (Album.Year is filled from the release date at commit). Every assertion is the 0.2.9 one.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DiscographyEraBandsTests
{
    static DiscographyYearRun[] OnePerYear(int newest, int count)
    {
        var runs = new DiscographyYearRun[count];
        for (int i = 0; i < count; i++) runs[i] = new DiscographyYearRun(newest - i, i, 1);
        return runs;
    }

    [Fact]
    public void SparseAlbumCatalogue_StillGetsUsefulCalendarFacets()
    {
        var bands = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(OnePerYear(2024, 18), 18));
        Assert.True(bands.Length >= 2);
    }

    [Fact]
    public void DenseSingles_CoalesceShortYearsIntoOlderNeighbors()
    {
        int[] counts = [12, 8, 8, 7, 6, 5];
        var runs = new DiscographyYearRun[counts.Length];
        int start = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            runs[i] = new DiscographyYearRun(2024 - i, start, counts[i]);
            start += counts[i];
        }

        var bands = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(runs, start));
        Assert.Collection(bands,
            b => Assert.Equal(("2024", 0, 12), (b.Label, b.Start, b.Count)),
            b => Assert.Equal(("2023", 12, 8), (b.Label, b.Start, b.Count)),
            b => Assert.Equal(("2022", 20, 8), (b.Label, b.Start, b.Count)),
            b => Assert.Equal(("2021", 28, 7), (b.Label, b.Start, b.Count)),
            b => Assert.Equal(("2020", 35, 6), (b.Label, b.Start, b.Count)),
            b => Assert.Equal(("2019", 41, 5), (b.Label, b.Start, b.Count)));
    }

    [Fact]
    public void LowYearVariety_AndSmallFacets_StayFlat()
    {
        Assert.Null(DiscographyEraBands.Plan(new[] { new DiscographyYearRun(2024, 0, 40) }, 40));
        Assert.Null(DiscographyEraBands.Plan(OnePerYear(2018, 3), 3));
        Assert.Null(DiscographyEraBands.Plan(OnePerYear(2024, 6), 6));
    }

    [Fact]
    public void LargeCatalogue_UsesAtMostEightCalendarAlignedBands()
    {
        var runs = new DiscographyYearRun[40];
        int start = 0;
        for (int i = 0; i < 40; i++)
        {
            int count = i < 20 ? 8 : 7;
            runs[i] = new DiscographyYearRun(2024 - i, start, count);
            start += count;
        }

        var bands = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(runs, start));
        Assert.Equal(8, bands.Length);
        Assert.Equal("2024–2020", bands[0].Label);
        Assert.Equal(start, bands[^1].Start + bands[^1].Count);
    }

    [Fact]
    public void UndatedItemsJoinTheCurrentRun_AndAllUndatedIsFlat()
    {
        DiscographyYearRun[] mixed = [new(2024, 0, 6), new(0, 6, 4), new(2023, 10, 10), new(2022, 20, 10)];
        var bands = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(mixed, 30));
        Assert.Equal(10, bands[0].Count);
        Assert.Null(DiscographyEraBands.Plan(new[] { new DiscographyYearRun(0, 0, 30) }, 30));
    }

    [Fact]
    public void ExactDecadesUseCompactLabels_AndProvisionalTailIsOpenEnded()
    {
        var runs = OnePerYear(2019, 30);
        var bands = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(runs, 30));
        Assert.Equal(new[] { "2010s", "2000s", "1990s" }, Array.ConvertAll(bands, static b => b.Label));

        var provisional = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(runs, 30, provisional: true));
        Assert.EndsWith("and earlier", provisional[^1].Label);
        Assert.True(provisional[^1].Provisional);
    }

    [Fact]
    public void ResidentYears_ProduceHeaderOnlyRanges_ThatMapFlatIndices()
    {
        ushort[] years = [2026, 2026, 2026, 2025, 2025, 2025, 2024, 2024, 2024,
                          2023, 2023, 2023, 2022, 2022, 2022, 2021, 2021, 2020, 2020];

        var eras = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.PlanYears(years));
        Assert.True(eras.Length >= 2);
        int sum = 0;
        foreach (var era in eras) sum += era.Count;
        Assert.Equal(years.Length, sum);
        Assert.Equal(eras[0], DiscographyEraBands.AtIndex(eras, 0));
        Assert.Equal(eras[1], DiscographyEraBands.AtIndex(eras, eras[1].Start));
        Assert.Null(DiscographyEraBands.AtIndex(eras, years.Length));
    }

    [Fact]
    public void ResidentYears_FromReleaseDates_Plan_AndSmallCataloguesStayUnfaceted()
    {
        // 0.2.9 fell back to the ISO date's year when `Year` was 0; in 0.3 the album commit writes the year WITH the
        // release date, so the caller's year column already carries it — twelve consecutive years group.
        var dated = new ushort[12];
        for (int i = 0; i < dated.Length; i++) dated[i] = (ushort)(2026 - i);
        Assert.NotNull(DiscographyEraBands.PlanYears(dated));
        Assert.Null(DiscographyEraBands.PlanYears([2026, 2025, 2024]));
    }

    // ── the facet host's era gate (W3-A4): a RowFold over the facet's years, in facet order, replaces the raw
    //    Albums.Changed read. The fold is a function of the years and their order, and a year LANDING (0 → 2019 behind a
    //    placeholder) moves it — the memo behind the host notifies exactly then.
    static ulong YearFold(ReadOnlySpan<ushort> years)
    {
        ulong fold = RowFold.Seed;
        for (int i = 0; i < years.Length; i++) fold = RowFold.Add(fold, (uint)years[i]);
        return fold;
    }

    [Fact]
    public void YearFold_MovesOnlyWhenAYearLandsOrMoves()
    {
        ulong a = YearFold([2024, 2021, 0]);
        Assert.Equal(a, YearFold([2024, 2021, 0]));                 // the same years: the same fold (no re-render)
        Assert.NotEqual(a, YearFold([2024, 2021, 2019]));           // the placeholder's year landed
        Assert.NotEqual(a, YearFold([2021, 2024, 0]));              // a reorder paints different bands
        Assert.NotEqual(RowFold.Seed, YearFold([0]));               // one unanswered album is still a row
        Assert.Equal(RowFold.Seed, YearFold(ReadOnlySpan<ushort>.Empty));   // a complete facet with no albums: the seed, never 0
        Assert.NotEqual(0UL, RowFold.Seed);
    }
}
