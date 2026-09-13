using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class DiscographyEraBandsTests
{
    static Album[] Albums(params int[] years) => years.Select((year, index) => new Album(
        "id:" + index, "spotify:album:" + index, "Album " + index, null,
        [], year, 1, ReleaseDate: year > 0 ? year + "-01-01" : null)).ToArray();

    static List<DiscographyYearRun> OnePerYear(int newest, int count)
    {
        var runs = new List<DiscographyYearRun>(count);
        for (int i = 0; i < count; i++) runs.Add(new DiscographyYearRun(newest - i, i, 1));
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
        var runs = new List<DiscographyYearRun>();
        int start = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            runs.Add(new DiscographyYearRun(2024 - i, start, counts[i]));
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
        Assert.Null(DiscographyEraBands.Plan([new(2024, 0, 40)], 40));
        Assert.Null(DiscographyEraBands.Plan(OnePerYear(2018, 3), 3));
        Assert.Null(DiscographyEraBands.Plan(OnePerYear(2024, 6), 6));
    }

    [Fact]
    public void LargeCatalogue_UsesAtMostEightCalendarAlignedBands()
    {
        var runs = new List<DiscographyYearRun>(40);
        int start = 0;
        for (int i = 0; i < 40; i++)
        {
            int count = i < 20 ? 8 : 7;
            runs.Add(new DiscographyYearRun(2024 - i, start, count));
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
        Assert.Null(DiscographyEraBands.Plan([new(0, 0, 30)], 30));
    }

    [Fact]
    public void ExactDecadesUseCompactLabels_AndProvisionalTailIsOpenEnded()
    {
        var runs = OnePerYear(2019, 30);
        var bands = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(runs, 30));
        Assert.Equal(["2010s", "2000s", "1990s"], [.. bands.Select(static b => b.Label)]);

        var provisional = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.Plan(runs, 30, provisional: true));
        Assert.EndsWith("and earlier", provisional[^1].Label);
        Assert.True(provisional[^1].Provisional);
    }

    [Fact]
    public void ResidentAlbums_ProduceHeaderOnlyRanges_ThatMapFlatIndices()
    {
        var albums = Albums(
            2026, 2026, 2026, 2025, 2025, 2025, 2024, 2024, 2024,
            2023, 2023, 2023, 2022, 2022, 2022, 2021, 2021, 2020, 2020);

        var eras = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.PlanAlbums(albums));
        Assert.True(eras.Length >= 2);
        Assert.Equal(albums.Length, eras.Sum(static era => era.Count));
        Assert.Equal(eras[0], DiscographyEraBands.AtIndex(eras, 0));
        Assert.Equal(eras[1], DiscographyEraBands.AtIndex(eras, eras[1].Start));
        Assert.Null(DiscographyEraBands.AtIndex(eras, albums.Length));
    }

    [Fact]
    public void ResidentAlbums_FallBackToReleaseDate_AndSmallCataloguesStayUnfaceted()
    {
        var dated = Albums(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        for (int i = 0; i < dated.Length; i++)
            dated[i] = dated[i] with { ReleaseDate = (2026 - i) + "-06-01" };

        Assert.NotNull(DiscographyEraBands.PlanAlbums(dated));
        Assert.Null(DiscographyEraBands.PlanAlbums(Albums(2026, 2025, 2024)));
    }
}
