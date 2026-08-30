using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;

namespace Wavee;

public readonly record struct DiscographyYearRun(int Year, int Start, int Count);
public readonly record struct DiscographyEraBand(string Label, int Start, int Count, bool Provisional = false);

/// <summary>Resize-stable discography grouping. Sparse calendar buckets coalesce until every header earns roughly one
/// nominal five-card row; tiny or low-variety catalogues stay flat.</summary>
public static class DiscographyEraBands
{
    public const int NominalColumns = 5;
    public const int MinBandItems = NominalColumns;
    public const int MaxBands = 8;

    static readonly int[] Widths = [1, 2, 5, 10, 20, 50, 100];

    /// <summary>Derive display-only era metadata from a complete resident discography snapshot. The result never owns
    /// grid geometry: callers use it to label the visible flat-grid range without inserting headers or restarting rows.</summary>
    public static DiscographyEraBand[]? PlanAlbums(IReadOnlyList<Album> albums)
    {
        if (albums.Count == 0) return null;
        var runs = new List<DiscographyYearRun>();
        int openYear = 0, openStart = 0, openCount = 0;
        for (int i = 0; i < albums.Count; i++)
        {
            var album = albums[i];
            int year = album.Year;
            if (year <= 0 && album.ReleaseDate is { Length: >= 4 } date)
                int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year);

            if (openCount == 0)
            {
                openYear = year;
                openStart = i;
                openCount = 1;
            }
            else if (year <= 0 || openYear <= 0 || year == openYear)
            {
                if (openYear <= 0 && year > 0) openYear = year;
                openCount++;
            }
            else
            {
                runs.Add(new DiscographyYearRun(openYear, openStart, openCount));
                openYear = year;
                openStart = i;
                openCount = 1;
            }
        }
        if (openCount > 0) runs.Add(new DiscographyYearRun(openYear, openStart, openCount));
        return Plan(runs, albums.Count);
    }

    /// <summary>The display era containing a flat-grid item index, or <c>null</c> when the catalogue stays ungrouped.</summary>
    public static DiscographyEraBand? AtIndex(IReadOnlyList<DiscographyEraBand>? eras, int index)
    {
        if (eras is null || index < 0) return null;
        for (int i = 0; i < eras.Count; i++)
        {
            var era = eras[i];
            if (index >= era.Start && index < era.Start + era.Count) return era;
        }
        return null;
    }

    readonly record struct DatedRun(int Year, int Start, int Count);
    readonly record struct WorkBand(int Newest, int Oldest, int Start, int Count)
    {
        public WorkBand Merge(in WorkBand older) =>
            new(Math.Max(Newest, older.Newest), Math.Min(Oldest, older.Oldest),
                Math.Min(Start, older.Start), Count + older.Count);
    }

    public static DiscographyEraBand[]? Plan(IReadOnlyList<DiscographyYearRun> runs, int itemCount,
                                              bool provisional = false)
    {
        if (runs.Count == 0 || itemCount <= MinBandItems) return null;
        var dated = Normalize(runs);
        if (dated.Count == 0) return null;

        int distinct = 0, previous = int.MinValue;
        int newest = int.MinValue, oldest = int.MaxValue;
        int covered = 0;
        for (int i = 0; i < dated.Count; i++)
        {
            var run = dated[i];
            if (run.Year != previous) { distinct++; previous = run.Year; }
            newest = Math.Max(newest, run.Year);
            oldest = Math.Min(oldest, run.Year);
            covered += run.Count;
        }
        if (distinct < 3 || covered <= MinBandItems) return null;

        int span = newest - oldest + 1;
        int target = Math.Clamp((int)MathF.Round(MathF.Sqrt(Math.Max(1, itemCount))), 3, MaxBands);
        int widthIndex = WidthIndexFor((float)span / target);
        List<WorkBand> planned;
        while (true)
        {
            planned = Bucket(dated, Widths[widthIndex]);
            CoalesceSparse(planned);
            if (planned.Count <= MaxBands || widthIndex == Widths.Length - 1) break;
            widthIndex++;
        }
        if (planned.Count < 2) return null;

        var result = new DiscographyEraBand[planned.Count];
        for (int i = 0; i < planned.Count; i++)
        {
            var band = planned[i];
            bool open = provisional && i == planned.Count - 1;
            result[i] = new DiscographyEraBand(Label(band.Newest, band.Oldest, open),
                                                band.Start, band.Count, open);
        }
        return result;
    }

    static List<DatedRun> Normalize(IReadOnlyList<DiscographyYearRun> runs)
    {
        var result = new List<DatedRun>(runs.Count);
        int leadingStart = 0, leadingCount = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.Count <= 0) continue;
            if (run.Year <= 0)
            {
                if (result.Count == 0)
                {
                    if (leadingCount == 0) leadingStart = run.Start;
                    leadingCount += run.Count;
                }
                else
                {
                    var current = result[^1];
                    result[^1] = current with { Count = current.Count + run.Count };
                }
                continue;
            }

            int start = leadingCount > 0 ? leadingStart : run.Start;
            int count = run.Count + leadingCount;
            leadingCount = 0;
            result.Add(new DatedRun(run.Year, start, count));
        }
        if (leadingCount > 0 && result.Count > 0)
        {
            var current = result[^1];
            result[^1] = current with { Count = current.Count + leadingCount };
        }
        return result;
    }

    static int WidthIndexFor(float desired)
    {
        for (int i = 0; i < Widths.Length; i++)
            if (Widths[i] >= desired) return i;
        return Widths.Length - 1;
    }

    static List<WorkBand> Bucket(List<DatedRun> runs, int width)
    {
        var result = new List<WorkBand>();
        int key = int.MinValue;
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            int bucket = (run.Year / width) * width;
            if (bucket == key)
            {
                var current = result[^1];
                result[^1] = current with
                {
                    Newest = Math.Max(current.Newest, run.Year),
                    Oldest = Math.Min(current.Oldest, run.Year),
                    Count = current.Count + run.Count,
                };
                continue;
            }
            key = bucket;
            result.Add(new WorkBand(run.Year, run.Year, run.Start, run.Count));
        }
        return result;
    }

    static void CoalesceSparse(List<WorkBand> bands)
    {
        int i = 0;
        while (i < bands.Count - 1)
        {
            if (bands[i].Count >= MinBandItems) { i++; continue; }
            bands[i] = bands[i].Merge(bands[i + 1]);
            bands.RemoveAt(i + 1);
        }
        if (bands.Count > 1 && bands[^1].Count < MinBandItems)
        {
            bands[^2] = bands[^2].Merge(bands[^1]);
            bands.RemoveAt(bands.Count - 1);
        }
    }

    static string Label(int newest, int oldest, bool openEnded)
    {
        if (openEnded)
            return ((newest / 10) * 10).ToString(CultureInfo.InvariantCulture) + "s and earlier";
        if (newest == oldest) return newest.ToString(CultureInfo.InvariantCulture);
        if (newest % 10 == 9 && oldest % 10 == 0 && newest - oldest == 9)
            return oldest.ToString(CultureInfo.InvariantCulture) + "s";
        if (newest % 10 == 9 && oldest % 10 == 0 && newest - oldest > 9)
            return ((newest / 10) * 10).ToString(CultureInfo.InvariantCulture) + "s–"
                   + ((oldest / 10) * 10).ToString(CultureInfo.InvariantCulture) + "s";
        return newest.ToString(CultureInfo.InvariantCulture) + "–" + oldest.ToString(CultureInfo.InvariantCulture);
    }
}
