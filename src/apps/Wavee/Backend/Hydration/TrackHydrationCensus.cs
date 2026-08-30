using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

/// <summary>Pure scan of store Track rows after a hydration/trait batch: how many fields are still missing, and the
/// majority reason per gap type. Engine-free so tests table-drive it without I/O. One Info line is the caller's job —
/// this type only answers the numbers.
/// <para><see cref="OpenPredicate"/> is the reason for BOTH the IMAGE gap and the DURATION gap: a row already past
/// Identity (a real title landed) that simply has no usable image, or a <c>DurationMs</c> still at zero — as opposed
/// to <see cref="ThinSeed"/>, where the gap coincides with the row itself being unnamed (None).</para></summary>
public static class TrackHydrationCensus
{
    public const string ThinSeed = "thin_seed";
    public const string OpenPredicate = "open_predicate";
    /// <summary>A ladder's own internal identity/ref-repair sub-ask (<see cref="HydrationOptions.SubAsk"/>) never wants
    /// this trait — by design, not by omission.</summary>
    public const string TraitNotAsked = "trait_not_asked";
    /// <summary>A REAL top-level caller's own <see cref="TraitSurface"/> policy asked for nothing. Distinct from
    /// <see cref="TraitNotAsked"/> so a caller that forgot to attribute a surface (the bug this label exists to catch)
    /// reads differently in the log than a ladder's own sub-ask — see the `surface=` field logged alongside it.</summary>
    public const string TraitSurfaceEmpty = "surface_no_traits";
    public const string TraitUnanswered = "trait_unanswered";
    public const string TraitNegative = "trait_negative";
    public const string TraitNotResident = "trait_not_resident";
    public const string Exhausted = "exhausted";

    public const int SampleCap = 3;

    /// <summary>Per-kind trait outcomes for the batch that just projected. Zeroes mean "this kind did not run"
    /// (not asked, or the page planned nothing), which is distinct from unanswered.</summary>
    public readonly record struct TraitTallies(int Unanswered, int Negative, int NotResident);

    public readonly record struct Sample(string Uri, string Gap);

    public readonly record struct Report(
        int N,
        int None, int Identity, int Open, int Full,
        int Title, int Artists, int Album, int Image, int Duration,
        int Playcount, int Tempo, int TagsNull, int TagsEmpty, int Year,
        string TitleReason, string ImageReason, string PlaycountReason, string TempoReason, string DurationReason,
        IReadOnlyList<Sample> Samples)
    {
        public bool HasGaps => Title + Artists + Album + Image + Duration + Playcount + Tempo
            + TagsNull + TagsEmpty + Year > 0;
    }

    public static Report Scan(
        IReadOnlyList<Track?> rows,
        TraitSet asked,
        TraitTallies playcount = default,
        TraitTallies tempo = default,
        TraitTallies tags = default,
        int exhausted = 0,
        bool subAsk = false)
    {
        int n = rows?.Count ?? 0;
        int none = 0, identity = 0, open = 0, full = 0;
        int title = 0, artists = 0, album = 0, image = 0, duration = 0;
        int playcountN = 0, tempoN = 0, tagsNull = 0, tagsEmpty = 0, year = 0;
        int imageThin = 0, imageOpen = 0;
        int durationThin = 0, durationOpen = 0;
        var samples = new List<Sample>(SampleCap);

        if (rows is not null)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var t = rows[i];
                var level = HydrationLevels.Of(t);
                switch (level)
                {
                    case HydrationLevel.None: none++; break;
                    case HydrationLevel.Identity: identity++; break;
                    case HydrationLevel.Full: full++; break;
                    default: open++; break;   // Open ≡ Rich for a playable
                }

                if (t is null)
                {
                    title++;
                    TrySample(samples, "", "title");
                    continue;
                }

                bool thin = HydrationLevels.TitleMissing(t.Title, t.Uri);
                if (thin) { title++; TrySample(samples, t.Uri, "title"); }
                if (t.Artists.Count == 0 || t.Artists[0].Name.Length == 0)
                { artists++; TrySample(samples, t.Uri, "artists"); }
                if (t.Album.Name.Length == 0) { album++; TrySample(samples, t.Uri, "album"); }
                if (!ImageSource.IsUsable(t.Image))
                {
                    image++;
                    if (thin) imageThin++; else imageOpen++;
                    TrySample(samples, t.Uri, "image");
                }
                if (t.DurationMs <= 0)
                {
                    duration++;
                    if (thin) durationThin++; else durationOpen++;
                    TrySample(samples, t.Uri, "duration");
                }
                if (t.PlayCount == 0) { playcountN++; TrySample(samples, t.Uri, "playcount"); }
                if (t.TempoBpm is null) { tempoN++; TrySample(samples, t.Uri, "tempo"); }
                if (t.Tags is null) { tagsNull++; TrySample(samples, t.Uri, "tags"); }
                else if (t.Tags.Count == 0) { tagsEmpty++; TrySample(samples, t.Uri, "tags"); }
                if (t.Year <= 0) { year++; TrySample(samples, t.Uri, "year"); }
            }
        }

        return new Report(
            n, none, identity, open, full,
            title, artists, album, image, duration,
            playcountN, tempoN, tagsNull, tagsEmpty, year,
            title > 0 ? ThinSeed : "",
            GapReason(imageThin, imageOpen, exhausted, image),
            TraitReason(asked, TraitSet.PlayCount, playcount, exhausted, playcountN, subAsk),
            TraitReason(asked, TraitSet.AudioAttributes, tempo, exhausted, tempoN, subAsk),
            GapReason(durationThin, durationOpen, exhausted, duration),
            samples);
    }

    public static void LogIfNeeded(WaveeLogger log, in Report report, TraitSurface surface, HydrationLevel level)
    {
        if (!log.IsEnabled(WaveeLogLevel.Info) || !report.HasGaps) return;

        log.Event(WaveeLogLevel.Info, "hydration.tracks.gaps", "track hydration still has field gaps",
            fields:
            [
                WaveeLogField.Of("n", report.N),
                WaveeLogField.Of("surface", surface.ToString()),
                WaveeLogField.Of("level", level.ToString()),
                WaveeLogField.Of("rungs",
                    $"none={report.None} identity={report.Identity} open={report.Open} full={report.Full}"),
                WaveeLogField.Of("gaps",
                    $"title={report.Title} artists={report.Artists} album={report.Album} image={report.Image} duration={report.Duration} playcount={report.Playcount} tempo={report.Tempo} tags=null:{report.TagsNull},empty:{report.TagsEmpty} year={report.Year}"),
                WaveeLogField.Of("reasons",
                    $"title={report.TitleReason} image={report.ImageReason} duration={report.DurationReason} playcount={report.PlaycountReason} tempo={report.TempoReason}"),
                WaveeLogField.Of("sample", FormatSamples(report.Samples)),
            ]);
    }

    // Shared by ImageReason and DurationReason (both gap types split the same way: did the gap land on a still-unnamed
    // row, or on one that is already Identity-or-better?). `thin`/`open` are the counts of the SAME gap restricted to
    // each bucket — the caller tallies them alongside the raw gap count in the scan loop above.
    static string GapReason(int thin, int open, int exhausted, int gaps)
    {
        if (gaps <= 0) return "";
        if (exhausted > 0 && exhausted >= thin && exhausted >= open) return Exhausted;
        return thin >= open ? ThinSeed : OpenPredicate;
    }

    static string TraitReason(TraitSet asked, TraitSet flag, TraitTallies tallies, int exhausted, int gaps, bool subAsk)
    {
        if (gaps <= 0) return "";
        if ((asked & flag) == 0) return subAsk ? TraitNotAsked : TraitSurfaceEmpty;
        if (exhausted > tallies.Unanswered && exhausted > tallies.Negative && exhausted > tallies.NotResident)
            return Exhausted;
        return Majority(tallies.Unanswered, TraitUnanswered,
                        tallies.Negative, TraitNegative,
                        tallies.NotResident, TraitNotResident,
                        TraitUnanswered);
    }

    static string Majority(int a, string sa, int b, string sb, int c, string sc, string fallback)
    {
        int best = a;
        string s = sa;
        if (b > best) { best = b; s = sb; }
        if (c > best) { best = c; s = sc; }
        return best > 0 ? s : fallback;
    }

    static void TrySample(List<Sample> samples, string uri, string gap)
    {
        if (samples.Count >= SampleCap || string.IsNullOrEmpty(uri)) return;
        for (int i = 0; i < samples.Count; i++)
            if (string.Equals(samples[i].Uri, uri, StringComparison.Ordinal)) return;
        samples.Add(new Sample(uri, gap));
    }

    static string FormatSamples(IReadOnlyList<Sample> samples)
    {
        if (samples is null || samples.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < samples.Count; i++)
        {
            if (i > 0) sb.Append(" ; ");
            sb.Append(samples[i].Uri).Append(" (").Append(samples[i].Gap).Append(')');
        }
        return sb.ToString();
    }
}
