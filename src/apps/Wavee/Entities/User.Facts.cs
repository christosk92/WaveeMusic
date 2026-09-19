// ── Entities/User.Facts.cs ─────────────────────────────────────────────────────────────────────────────────────────
// LikedFactsRules, verbatim (every member ch 07 §8 lists), over `ReadOnlySpan<LikedRow>`; plus the two pure decisions the
// 0.2.9 panel kept in its component body (the card plan and the chip-set source), lifted here so they are tested
//
// Role: CORE
// Owner: O
// Wave: 5
// Budget: 1000 lines
// Spec: ch 07 §8 (the LikedFactsRules rows), §7 (the edge-payload AddedAt), §9 ("the facts settle before they speak")
//
// ── WHAT CHANGED FROM 0.2.9, AND WHAT DID NOT ────────────────────────────────────────────────────────────────────────
//
// The ARITHMETIC is 0.2.9's `Features/Detail/LikedFactsRules.cs`, constant for constant and tie-break for tie-break. The
// INPUT changed shape, exactly as ch 07 §1.2 asks:
//   · a row is `LikedRow(Track, AddedAtUnixSec)` — the save stamp is the EDGE's payload (`LibraryEdge.AddedAt`,
//     `PlaylistTrackEdge.AddedAt`), never a track column: one recording in Liked and in a playlist has two stamps;
//   · stamps are unix SECONDS in an int (0 = none), so the epoch sentinel is `≤ 0` and the resolution is one second;
//   · a credit is an artist SLOT (`Edges.TrackArtists`), so `ArtistKey` is the slot and the lens is
//     `Track.FilterState.ArtistSlot` — two artists sharing a display name can never collapse into one face;
//   · a descriptor is an interned DISPLAY name (`Edges.TrackTags` payload, [0] = primary), "not fetched" is
//     `!Knows(TrackFields.Tags)` (0.2.9's null), and "none" is a known empty run;
//   · a tempo is `Tracks.Tempo` ×10 and exists only once `Knows(TrackFields.Audio)` (0.2.9's null double).
// `TracksEquivalent` is kept as a row-identity compare (track + stamp): a handle is stable and its enrichment is a Version
// bump, so "a tempo landed" is no longer a different row.
//
// Nothing here reads a clock: every rule that needs "now" takes it. Nothing here touches an engine type.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>One row a facts rule reads: the track handle and the save stamp its MEMBERSHIP carries (unix seconds, 0 =
/// none). A liked row's stamp is <c>LibraryEdge.AddedAt</c>; a playlist row's is <c>PlaylistTrackEdge.AddedAt</c>.</summary>
public readonly record struct LikedRow(Track Track, int AddedAtUnixSec);

/// <summary>The PURE facts behind the Liked Songs (and playlist) rail panel: how often you like, who you like, and what
/// you like. Two deliberate constraints, both honesty rather than convenience: time facts derive from the row's stamp
/// ONLY (artist and blend facts count every row); and every function that needs a clock TAKES one.</summary>
public static class LikedFactsRules
{
    /// <summary>A stamp at or before the Unix epoch is UNKNOWN, not "liked in 1970".</summary>
    public static readonly DateTimeOffset UnknownStampFloor = DateTimeOffset.UnixEpoch;

    /// <summary>True when this row carries a stamp we can reason about (present, and past the epoch sentinel). A row that
    /// fails this is excluded from TIME facts; artist and blend facts do not use this gate.</summary>
    public static bool TryStamp(in LikedRow row, out DateTimeOffset addedAt)
    {
        addedAt = default;
        if (row.AddedAtUnixSec <= 0) return false;
        addedAt = DateTimeOffset.FromUnixTimeSeconds(row.AddedAtUnixSec);
        return true;
    }

    // ── the per-row readers (0.3: the handle's columns, gated on their Known bits) ──────────────────────────────────

    /// <summary>The row's tempo in BPM, or 0 when kind 222 has not answered (0.2.9's null <c>TempoBpm</c>).</summary>
    public static double TempoBpm(Track t) => t.IsValid && t.Knows(TrackFields.Audio) && t.Tempo > 0 ? t.Tempo / 10d : 0d;

    /// <summary>The Camelot swatch ARGB, 0 = none.</summary>
    public static uint CamelotColor(Track t) => t.IsValid && t.Knows(TrackFields.Audio) ? t.CamelotColor : 0u;

    /// <summary>The row's descriptors when kind 6 has answered (false = 0.2.9's null "not fetched"; true with an empty
    /// span = "genuinely none").</summary>
    public static bool TryTags(Track t, out ReadOnlySpan<StringId> tags)
    {
        if (!t.IsValid || !t.Knows(TrackFields.Tags)) { tags = default; return false; }
        tags = t.Tags;
        return true;
    }

    /// <summary>Does this track carry at least one keyed credit? The per-row half of <see cref="AnyArtistCredit"/>, for a
    /// caller scanning a membership without building a row span (the rail's <c>FactsHas</c>).</summary>
    public static bool HasKeyedCredit(Track t)
    {
        if (!t.IsValid) return false;
        var artists = t.ArtistSlots;
        for (int a = 0; a < artists.Length; a++) if (artists[a] > Table.None) return true;
        return false;
    }

    // ── This week / the sparkline ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One rolling 7-day window and how many likes landed inside it.</summary>
    public readonly record struct WeekBucket(DateTimeOffset WindowStart, int Count);

    /// <summary>Rolling 7-day windows ending at <paramref name="now"/>: bucket k covers <c>(now - 7(k+1)d, now - 7k d]</c>.
    /// Returned OLDEST-FIRST, always exactly <paramref name="weeks"/> entries. Deliberately NOT calendar weeks. A
    /// future-dated stamp is CLAMPED to <paramref name="now"/> and counted in the newest bucket.</summary>
    public static IReadOnlyList<WeekBucket> LikesPerWeek(ReadOnlySpan<LikedRow> rows, DateTimeOffset now, int weeks = 12)
    {
        if (weeks <= 0) return Array.Empty<WeekBucket>();

        const long WeekTicks = TimeSpan.TicksPerDay * 7L;
        var counts = new int[weeks];
        for (int i = 0; i < rows.Length; i++)
        {
            if (!TryStamp(rows[i], out var at)) continue;
            long age = at >= now ? 0L : (now - at).Ticks;      // future stamps clamp into the newest bucket
            long k = age / WeekTicks;
            if (k < weeks) counts[(int)k]++;
        }

        var buckets = new WeekBucket[weeks];
        for (int i = 0; i < weeks; i++)
        {
            int k = weeks - 1 - i;                                  // oldest-first output, newest bucket is k = 0
            buckets[i] = new WeekBucket(now - TimeSpan.FromTicks(WeekTicks * (k + 1)), counts[k]);
        }
        return buckets;
    }

    /// <summary>The clock the panel buckets from: the caller's instant floored to the HOUR, so the twelve windows are
    /// STABLE for the hour they were drawn in and the lens a bar stored keeps matching that bar.</summary>
    public static DateTimeOffset BucketClock(DateTimeOffset now)
        => new(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);

    /// <summary>The half-open <c>(after, before]</c> Unix-ms window one sparkline bar stands for — what
    /// <c>Track.FilterState.WithAddedWindow</c> takes when that bar is clicked. Derived from the bucket itself.</summary>
    public static (long AfterMs, long BeforeMs) WeekWindowMs(in WeekBucket week)
        => (week.WindowStart.ToUnixTimeMilliseconds(),
            week.WindowStart.Add(TimeSpan.FromDays(7)).ToUnixTimeMilliseconds());

    /// <summary>The bar's interval as LOCAL instants — the one place the "Jul 27 – Aug 3" wording is derived from.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) WeekRange(in WeekBucket week)
    {
        var start = week.WindowStart.ToLocalTime();
        return (start, start.Add(TimeSpan.FromDays(7)));
    }

    /// <summary>Is THIS bar the one currently lensing the list? Compared on the window itself, never an index.</summary>
    public static bool IsWeekLens(in Track.FilterState filter, in WeekBucket week)
    {
        var (after, before) = WeekWindowMs(week);
        return filter.AddedAfterMs == after && filter.AddedBeforeMs == before;
    }

    // ── This week, last year ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same seven weekdays exactly 52 weeks back: <c>[now - 371d, now - 364d)</c>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) ThisWeekLastYearWindow(DateTimeOffset now)
        => (now - TimeSpan.FromDays(371), now - TimeSpan.FromDays(364));

    /// <summary>The rows whose stamp falls in <c>[start, end)</c> — the "Play them" set. Input order is kept.</summary>
    public static IReadOnlyList<LikedRow> LikedInWindow(ReadOnlySpan<LikedRow> rows, DateTimeOffset start, DateTimeOffset end)
    {
        if (rows.Length == 0 || end <= start) return Array.Empty<LikedRow>();

        List<LikedRow>? hits = null;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!TryStamp(rows[i], out var at)) continue;
            if (at >= start && at < end) (hits ??= new List<LikedRow>()).Add(rows[i]);
        }
        return (IReadOnlyList<LikedRow>?)hits ?? Array.Empty<LikedRow>();
    }

    // ── Most liked artists ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An artist and how many of your likes credit them.</summary>
    public readonly record struct ArtistCount(Artist Artist, int Count);

    /// <summary>The identity an artist is counted, ranked and LENSED by: its SLOT, and 0 when the credit resolves to no
    /// row — the one case an artist cannot be a lens. Shared on purpose: <see cref="TopArtists"/> ranks by it and
    /// <c>Track.FilterModel</c> matches <c>FilterState.ArtistSlot</c> by it.</summary>
    public static int ArtistKey(Artist artist) => artist.IsValid ? artist.Slot : 0;

    /// <summary>Is THIS artist the one currently lensing the list?</summary>
    public static bool IsArtistLens(in Track.FilterState filter, Artist artist)
        => filter.ArtistSlot != 0 && filter.ArtistSlot == ArtistKey(artist);

    /// <summary>Is THIS descriptor the one currently lensing the list? Case-insensitive, like the chip bar.</summary>
    public static bool IsTagLens(in Track.FilterState filter, string? title)
        => filter.Tag is { Length: > 0 } tag && !string.IsNullOrEmpty(title)
           && string.Equals(tag, title, StringComparison.OrdinalIgnoreCase);

    /// <summary>Which rail facts are currently lensing the list — independent facets that DO combine.</summary>
    [Flags]
    public enum LikedLens : byte { None = 0, Week = 1, Artist = 2, Tag = 4, Year = 8, Tempo = 16 }

    /// <summary>The lenses active in <paramref name="filter"/>. Only the rail's own facets: the flyout's are the
    /// flyout's to describe.</summary>
    public static LikedLens ActiveLenses(in Track.FilterState filter)
    {
        var lenses = LikedLens.None;
        if (filter.AddedAfterMs != 0L || filter.AddedBeforeMs != 0L) lenses |= LikedLens.Week;
        if (filter.ArtistSlot != 0) lenses |= LikedLens.Artist;
        if (!string.IsNullOrEmpty(filter.Tag)) lenses |= LikedLens.Tag;
        if (filter.ReleaseYearMin != 0 || filter.ReleaseYearMax != 0) lenses |= LikedLens.Year;
        if (filter.Tempo != Track.TempoBand.Any) lenses |= LikedLens.Tempo;
        return lenses;
    }

    /// <summary>Retire ONE lens, leaving every other facet exactly as it was — a per-facet undo, not a reset.</summary>
    public static Track.FilterState ClearLens(in Track.FilterState filter, LikedLens lens) => lens switch
    {
        LikedLens.Week => filter.WithAddedWindow(0L, 0L),
        LikedLens.Artist => filter.WithArtist(0),
        LikedLens.Tag => filter with { Tag = null },
        LikedLens.Year => filter.WithReleaseYear(0, 0),
        LikedLens.Tempo => filter with { Tempo = Track.TempoBand.Any },
        _ => filter,
    };

    /// <summary>Is THIS tempo band the one currently lensing the list? <c>Any</c> is never a lens.</summary>
    public static bool IsTempoLens(in Track.FilterState filter, Track.TempoBand band)
        => band != Track.TempoBand.Any && filter.Tempo == band;

    /// <summary>Is THIS year bar the one currently lensing the list? Compared on the inclusive range itself.</summary>
    public static bool IsYearLens(in Track.FilterState filter, in YearBucket bucket)
        => filter.ReleaseYearMin == bucket.YearMin && filter.ReleaseYearMax == bucket.YearMax;

    /// <summary>Distinct credited artists by liked-row count descending, ties broken by name (a tie that reshuffles on
    /// every refresh makes the face pile twitch). EVERY credit on a row counts, not just the first.</summary>
    public static IReadOnlyList<ArtistCount> TopArtists(ReadOnlySpan<LikedRow> rows, int take = 5)
    {
        if (rows.Length == 0 || take <= 0) return Array.Empty<ArtistCount>();

        var counts = new Dictionary<int, int>();
        for (int i = 0; i < rows.Length; i++)
        {
            var t = rows[i].Track;
            if (!t.IsValid) continue;
            var artists = t.ArtistSlots;
            for (int a = 0; a < artists.Length; a++)
            {
                int key = ArtistKey(new Artist(artists[a]));   // THE shared identity — the lens matches on the same one
                if (key == 0) continue;
                counts.TryGetValue(key, out int seen);
                counts[key] = seen + 1;
            }
        }
        if (counts.Count == 0) return Array.Empty<ArtistCount>();

        var ordered = new List<(Artist Artist, int Count, string Name)>(counts.Count);
        foreach (var kv in counts)
        {
            var artist = new Artist(kv.Key);
            ordered.Add((artist, kv.Value, artist.Name));
        }
        ordered.Sort(static (x, y) =>
        {
            int c = y.Count.CompareTo(x.Count);
            return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        int n = Math.Min(take, ordered.Count);
        var top = new ArtistCount[n];
        for (int i = 0; i < n; i++) top[i] = new ArtistCount(ordered[i].Artist, ordered[i].Count);
        return top;
    }

    // ── Your blend ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One slice of the blend bar: a descriptor, how many likes lead with it, and its share of the tagged likes.</summary>
    public readonly record struct TagShare(string Title, int Count, float Fraction);

    /// <summary>The blend bar. Each row contributes its PRIMARY tag only (<c>Tags[0]</c>), so the shares PARTITION the
    /// tagged likes. A descriptor needs <see cref="ContentFilterTags.MinTrackCount"/> carriers to appear; below it the
    /// result is EMPTY and the caller mounts no card.</summary>
    public static IReadOnlyList<TagShare> BlendShares(ReadOnlySpan<LikedRow> rows, int take = 5)
    {
        if (rows.Length == 0 || take <= 0) return Array.Empty<TagShare>();

        var (ranked, tagged, aboveFloor) = Partition(rows);
        if (aboveFloor == 0) return Array.Empty<TagShare>();

        int slices = Math.Min(take, aboveFloor);
        var shares = new TagShare[slices];
        for (int i = 0; i < slices; i++)
            shares[i] = new TagShare(ranked[i].Key, ranked[i].Value, ranked[i].Value / (float)tagged);
        return shares;
    }

    /// <summary>What the bar's "Other" segment is made of.</summary>
    /// <param name="Count">Tagged likes pooled into the remainder.</param>
    /// <param name="Fraction">That remainder as a share of the tagged likes.</param>
    /// <param name="Named">The next ranked descriptors after the bar's own slices, same denominator, same order.</param>
    /// <param name="MoreTags">Distinct primary descriptors in the remainder that <see cref="Named"/> does NOT name.</param>
    public readonly record struct BlendTail(int Count, float Fraction, IReadOnlyList<TagShare> Named, int MoreTags)
    {
        // `default(BlendTail)` is a LEGITIMATE answer and a default struct carries a null list — which is exactly what
        // took 0.2.0.1 down. The list is never null from here on; equality still runs over the backing field.
        readonly IReadOnlyList<TagShare>? _named = Named;
        public IReadOnlyList<TagShare> Named => _named ?? Array.Empty<TagShare>();
    }

    /// <summary>Open up the "Other" segment: a strict continuation of <see cref="BlendShares"/> over the SAME partition and
    /// denominator. THE EVIDENCE FLOOR APPLIES TO THE BAR, NOT TO THE TAIL: at <c>detail = int.MaxValue</c> every remaining
    /// descriptor is named and <see cref="BlendTail.MoreTags"/> is 0.</summary>
    public static BlendTail BlendOther(ReadOnlySpan<LikedRow> rows, int shown, int detail = 3)
    {
        if (rows.Length == 0 || shown < 0) return default;

        var (ranked, tagged, aboveFloor) = Partition(rows);
        if (aboveFloor == 0) return default;

        int named = Math.Min(shown, aboveFloor);
        int pooled = tagged;
        for (int i = 0; i < named; i++) pooled -= ranked[i].Value;
        if (pooled <= 0) return default;

        int tail = detail <= 0 ? 0 : Math.Min(detail, ranked.Count - named);
        var next = tail > 0 ? new TagShare[tail] : Array.Empty<TagShare>();
        for (int i = 0; i < tail; i++)
        {
            var kv = ranked[named + i];
            next[i] = new TagShare(kv.Key, kv.Value, kv.Value / (float)tagged);
        }
        int more = ranked.Count - named - tail;
        return new BlendTail(pooled, pooled / (float)tagged, next, more > 0 ? more : 0);
    }

    /// <summary>Split a tail into the descriptors big enough to earn a legend ROW and a COUNT of the ones that are not.
    /// At or above the floor is NAMED; input order is preserved.</summary>
    public static (IReadOnlyList<TagShare> Named, int UnderFloor) TailSplit(IReadOnlyList<TagShare> tail, float floor = 0.01f)
    {
        if (tail is null || tail.Count == 0) return (Array.Empty<TagShare>(), 0);

        int named = 0;
        for (int i = 0; i < tail.Count; i++) if (tail[i].Fraction >= floor) named++;
        if (named == tail.Count) return (tail, 0);
        if (named == 0) return (Array.Empty<TagShare>(), tail.Count);

        var rows = new TagShare[named];
        int n = 0;
        for (int i = 0; i < tail.Count; i++) if (tail[i].Fraction >= floor) rows[n++] = tail[i];
        return (rows, tail.Count - named);
    }

    /// <summary>How many likes a set of shares is describing, recovered from any slice by one exact division — ROUNDED,
    /// not truncated (5/(5/12f) lands a hair under 12 in single precision).</summary>
    public static int TaggedTotal(IReadOnlyList<TagShare> shares)
    {
        if (shares is null) return 0;
        for (int i = 0; i < shares.Count; i++)
            if (shares[i].Fraction > 0f) return (int)MathF.Round(shares[i].Count / shares[i].Fraction);
        return 0;
    }

    /// <summary>The ONE primary-descriptor pass both blend answers are built on: every distinct descriptor ranked
    /// descending (ties by name), the tagged population, and the above-floor PREFIX length. Case-insensitive.</summary>
    static (List<KeyValuePair<string, int>> Ranked, int Tagged, int AboveFloor) Partition(ReadOnlySpan<LikedRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int tagged = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!TryTags(rows[i].Track, out var tags) || tags.Length == 0) continue;   // not fetched / genuinely none
            string primary = Entities.Strings.Resolve(tags[0]);
            if (string.IsNullOrWhiteSpace(primary)) continue;
            counts.TryGetValue(primary, out int n);
            counts[primary] = n + 1;
            tagged++;
        }

        var ranked = new List<KeyValuePair<string, int>>(counts.Count);
        int aboveFloor = 0;
        if (tagged > 0)
        {
            foreach (var kv in counts)
            {
                ranked.Add(kv);
                if (kv.Value >= ContentFilterTags.MinTrackCount) aboveFloor++;
            }
            ranked.Sort(static (a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
            });
        }
        return (ranked, tagged, aboveFloor);
    }

    // ── The since-line ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>When the oldest surviving like was saved, or null when nothing carries a usable stamp.</summary>
    public static DateTimeOffset? LikingSince(ReadOnlySpan<LikedRow> rows)
        => OldestLike(rows) is { } row && TryStamp(row, out var at) ? at : null;

    /// <summary>The row behind <see cref="LikingSince"/> — the oldest usably-stamped like.</summary>
    public static LikedRow? OldestLike(ReadOnlySpan<LikedRow> rows)
    {
        LikedRow? oldest = null;
        DateTimeOffset best = default;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!TryStamp(rows[i], out var at)) continue;
            if (oldest is null || at < best) { oldest = rows[i]; best = at; }
        }
        return oldest;
    }

    /// <summary>Fewer stamped likes than this and the decade fact is trivia, not a pattern.</summary>
    public const int MinDecadeEvidence = 10;

    /// <summary>The decade most likes were SAVED in, or null under <see cref="MinDecadeEvidence"/> stamped likes. Ties go
    /// to the more recent decade.</summary>
    public static int? DominantDecade(ReadOnlySpan<LikedRow> rows)
    {
        if (rows.Length == 0) return null;

        var counts = new Dictionary<int, int>();
        int stamped = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!TryStamp(rows[i], out var at)) continue;
            stamped++;
            int decade = at.Year / 10 * 10;
            counts.TryGetValue(decade, out int n);
            counts[decade] = n + 1;
        }
        if (stamped < MinDecadeEvidence) return null;

        int bestDecade = 0, bestCount = 0;
        foreach (var kv in counts)
            if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key > bestDecade))
                (bestDecade, bestCount) = (kv.Key, kv.Value);
        return bestCount > 0 ? bestDecade : null;
    }

    // ── Stamp spread / release years ────────────────────────────────────────────────────────────────────────────────

    /// <summary>True when usable stamps land on at least two distinct UTC calendar days — a single republish instant is
    /// not activity and must not light a week sparkline.</summary>
    public static bool StampsSpread(ReadOnlySpan<LikedRow> rows)
    {
        DateOnly first = default;
        bool have = false;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!TryStamp(rows[i], out var at)) continue;
            var day = DateOnly.FromDateTime(at.UtcDateTime);
            if (!have) { first = day; have = true; }
            else if (day != first) return true;
        }
        return false;
    }

    /// <summary>One bar of the release-year sparkline: an inclusive year range and how many rows fall inside it.</summary>
    public readonly record struct YearBucket(int YearMin, int YearMax, int Count);

    static int YearOf(Track t) => t.IsValid ? t.Year : 0;

    /// <summary>Twelve bars over the dated rows' release years. Span ≤ 12: consecutive years, padded and centred. Span
    /// &gt; 12: twelve equal-width bins that partition <c>[min, max]</c>. Empty when nothing carries <c>Year &gt; 0</c>.</summary>
    public static IReadOnlyList<YearBucket> YearHistogram(ReadOnlySpan<LikedRow> rows, int bars = 12)
    {
        if (rows.Length == 0 || bars <= 0) return Array.Empty<YearBucket>();

        int minY = 0, maxY = 0, dated = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int y = YearOf(rows[i].Track);
            if (y <= 0) continue;
            if (dated == 0) minY = maxY = y;
            else { if (y < minY) minY = y; if (y > maxY) maxY = y; }
            dated++;
        }
        if (dated == 0) return Array.Empty<YearBucket>();

        var buckets = new YearBucket[bars];
        int span = maxY - minY + 1;
        if (span <= bars)
        {
            int extra = bars - span;
            int start = Math.Max(1, minY - extra / 2);
            for (int i = 0; i < bars; i++) buckets[i] = new YearBucket(start + i, start + i, 0);
        }
        else
        {
            for (int i = 0; i < bars; i++)
            {
                int lo = minY + (int)((long)i * span / bars);
                int hi = minY + (int)((long)(i + 1) * span / bars) - 1;
                buckets[i] = new YearBucket(lo, hi, 0);
            }
        }

        for (int i = 0; i < rows.Length; i++)
        {
            int y = YearOf(rows[i].Track);
            if (y <= 0) continue;
            int idx = IndexOfYear(buckets, y);
            if (idx < 0) continue;
            var b = buckets[idx];
            buckets[idx] = b with { Count = b.Count + 1 };
        }
        return buckets;
    }

    static int IndexOfYear(YearBucket[] buckets, int year)
    {
        for (int i = 0; i < buckets.Length; i++)
            if (year >= buckets[i].YearMin && year <= buckets[i].YearMax) return i;
        return -1;
    }

    /// <summary>The year the big numeral names for <paramref name="bucket"/>: the bar itself when it is one year,
    /// otherwise the modal year inside the bin (tie → more recent).</summary>
    public static int PeakYear(ReadOnlySpan<LikedRow> rows, in YearBucket bucket)
    {
        if (bucket.YearMin == bucket.YearMax) return bucket.YearMin;
        if (rows.Length == 0) return bucket.YearMax;

        int bestYear = bucket.YearMax, bestCount = 0;
        for (int y = bucket.YearMin; y <= bucket.YearMax; y++)
        {
            int n = 0;
            for (int i = 0; i < rows.Length; i++) if (YearOf(rows[i].Track) == y) n++;
            if (n > bestCount || (n == bestCount && y > bestYear)) { bestYear = y; bestCount = n; }
        }
        return bestYear;
    }

    /// <summary>True when at least <see cref="MinDecadeEvidence"/> rows carry a known release year.</summary>
    public static bool HasReleaseYears(ReadOnlySpan<LikedRow> rows)
    {
        int n = 0;
        for (int i = 0; i < rows.Length; i++)
            if (YearOf(rows[i].Track) > 0 && ++n >= MinDecadeEvidence) return true;
        return false;
    }

    /// <summary>The decade most dated rows were RELEASED in, or null under <see cref="MinDecadeEvidence"/>. Ignores the
    /// stamp. Ties go to the more recent decade.</summary>
    public static int? DominantReleaseDecade(ReadOnlySpan<LikedRow> rows)
    {
        if (rows.Length == 0) return null;

        var counts = new Dictionary<int, int>();
        int dated = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int y = YearOf(rows[i].Track);
            if (y <= 0) continue;
            dated++;
            int decade = y / 10 * 10;
            counts.TryGetValue(decade, out int n);
            counts[decade] = n + 1;
        }
        if (dated < MinDecadeEvidence) return null;

        int bestDecade = 0, bestCount = 0;
        foreach (var kv in counts)
            if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key > bestDecade))
                (bestDecade, bestCount) = (kv.Key, kv.Value);
        return bestCount > 0 ? bestDecade : null;
    }

    /// <summary>The dated row with the oldest release year, or null. Ties keep the first such row.</summary>
    public static LikedRow? OldestRelease(ReadOnlySpan<LikedRow> rows)
    {
        LikedRow? oldest = null;
        int best = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int y = YearOf(rows[i].Track);
            if (y <= 0) continue;
            if (oldest is null || y < best) { oldest = rows[i]; best = y; }
        }
        return oldest;
    }

    // ── Which facts EARN a card ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How a distribution fact renders: ABSENT, a one-line LABEL pill, or the full GRAPH card.</summary>
    public enum FactShape : byte { Absent = 0, Label = 1, Graph = 2 }

    /// <summary>Fewer known values than this and twelve bars are noise: 10–19 known is LABEL at most.</summary>
    public const int MinGraphEvidence = 20;
    /// <summary>A graph needs at least this many non-empty categories (years); tempo has four bands and asks for two.</summary>
    public const int MinGraphCategories = 3;
    public const int MinTempoGraphCategories = 2;
    /// <summary>Share of the rows that must carry the value (tempo, years) for the fact to exist at all.</summary>
    public const float MinCoverage = 0.60f;
    /// <summary>Top-category share at or above which the fact collapses to a pill.</summary>
    public const float YearsCap = 0.50f;
    public const float TempoCap = 0.70f;
    public const float BlendCap = 0.85f;
    /// <summary>Under this top share no style leads at all.</summary>
    public const float BlendFlat = 0.15f;

    /// <summary>THE resolver.</summary>
    public static FactShape Shape(int known, int total, int categories, float topShare, float cap, int minCategories = MinGraphCategories)
    {
        if (known < MinDecadeEvidence || total <= 0) return FactShape.Absent;
        if (known / (float)total < MinCoverage) return FactShape.Absent;
        if (known < MinGraphEvidence || categories < minCategories || topShare >= cap) return FactShape.Label;
        return FactShape.Graph;
    }

    /// <summary>How concentrated a distribution is: values known, non-empty categories, the largest category (ties → the
    /// LATER index) and its share.</summary>
    public readonly record struct Dominance(int Known, int Categories, int TopIndex, float TopShare);

    /// <summary>Years dominance measured over the histogram's OWN buckets.</summary>
    public static Dominance YearsDominance(IReadOnlyList<YearBucket> buckets)
    {
        if (buckets is null || buckets.Count == 0) return default;
        int known = 0, cats = 0, top = -1, topCount = -1;
        for (int i = 0; i < buckets.Count; i++)
        {
            int c = buckets[i].Count;
            known += c;
            if (c > 0) cats++;
            if (c >= topCount) { topCount = c; top = i; }
        }
        return new Dominance(known, cats, top, known > 0 ? topCount / (float)known : 0f);
    }

    public static FactShape YearsShape(ReadOnlySpan<LikedRow> rows, IReadOnlyList<YearBucket> buckets)
    {
        var d = YearsDominance(buckets);
        return Shape(d.Known, rows.Length, d.Categories, d.TopShare, YearsCap);
    }

    /// <summary>The four filter bands in <c>Track.TempoBand</c> order (Under90, 90–119, 120–139, 140+).</summary>
    public const int TempoBandCount = 4;

    /// <summary>Rows per tempo band using the filter's own boundary table. Returns how many rows carry a tempo.</summary>
    public static int TempoBandCounts(ReadOnlySpan<LikedRow> rows, Span<int> counts)
    {
        counts.Clear();
        int known = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            double bpm = TempoBpm(rows[i].Track);
            if (bpm <= 0d) continue;
            var band = Track.FilterModel.BandOf(bpm);
            if (band == Track.TempoBand.Any) continue;
            counts[(int)band - 1]++;
            known++;
        }
        return known;
    }

    public static Dominance TempoDominance(ReadOnlySpan<LikedRow> rows) => TempoSummarize(rows).Dominance;

    public static FactShape TempoShape(ReadOnlySpan<LikedRow> rows) => TempoSummarize(rows).Shape;

    /// <summary>Tempo summary: known / total, the LOWER median (a real row's tempo, never an average), and the range.</summary>
    public readonly record struct TempoStats(int Known, int Total, double Median, double Min, double Max);

    public static TempoStats TempoStatistics(ReadOnlySpan<LikedRow> rows) => TempoSummarize(rows).Stats;

    /// <summary>A content fingerprint of the tempos — <see cref="Known"/> plus an order-independent hash — so a plot keys
    /// its geometry on the tempos themselves, never on a list instance.</summary>
    public readonly record struct TempoFingerprint(int Known, long Hash);

    /// <summary>Everything the rail wants to know about tempo, from ONE pass.</summary>
    public readonly record struct TempoSummary(TempoStats Stats, int Under90, int From90To119, int From120To139, int From140AndUp,
                                               Dominance Dominance, FactShape Shape, TempoFingerprint Fingerprint)
    {
        /// <summary>The band count by <c>(int)Track.TempoBand - 1</c> index.</summary>
        public int Count(int bandIndex) => bandIndex switch { 0 => Under90, 1 => From90To119, 2 => From120To139, 3 => From140AndUp, _ => 0 };
    }

    /// <summary>The histogram's top bin (bpm), a clamp — never a drop.</summary>
    public const int MaxBpmBin = 400;

    public static TempoSummary TempoSummarize(ReadOnlySpan<LikedRow> rows)
    {
        int total = rows.Length;
        if (total == 0) return new TempoSummary(default, 0, 0, 0, 0, new Dominance(0, 0, -1, 0f), FactShape.Absent, default);

        Span<int> bins = stackalloc int[MaxBpmBin + 1];
        Span<int> bands = stackalloc int[TempoBandCount];
        int known = 0;
        double min = double.MaxValue, max = double.MinValue;
        long hash = 0;
        for (int i = 0; i < total; i++)
        {
            double bpm = TempoBpm(rows[i].Track);
            if (bpm <= 0d || double.IsNaN(bpm)) continue;
            var band = Track.FilterModel.BandOf(bpm);
            if (band == Track.TempoBand.Any) continue;
            bands[(int)band - 1]++;
            bins[Math.Clamp((int)Math.Round(bpm), 0, MaxBpmBin)]++;
            if (bpm < min) min = bpm;
            if (bpm > max) max = bpm;
            hash += Mix((long)Math.Round(bpm * 10d));
            known++;
        }

        int cats = 0, top = -1, topCount = -1;
        for (int i = 0; i < bands.Length; i++)
        {
            if (bands[i] > 0) cats++;
            if (bands[i] > topCount) { topCount = bands[i]; top = i; }   // first wins a tie: the slower band
        }
        var dominance = new Dominance(known, cats, top, known > 0 ? topCount / (float)known : 0f);
        var shape = Shape(known, total, cats, dominance.TopShare, TempoCap, MinTempoGraphCategories);
        var fingerprint = new TempoFingerprint(known, hash);
        if (known == 0)
            return new TempoSummary(new TempoStats(0, total, 0d, 0d, 0d), 0, 0, 0, 0, dominance, shape, fingerprint);

        // Lower median off the histogram: the first bin whose cumulative count reaches the lower-middle rank.
        int rank = (known + 1) / 2, seen = 0, median = 0;
        for (int b = 0; b <= MaxBpmBin; b++) { seen += bins[b]; if (seen >= rank) { median = b; break; } }
        var stats = new TempoStats(known, total, median, min, max);
        return new TempoSummary(stats, bands[0], bands[1], bands[2], bands[3], dominance, shape, fingerprint);
    }

    public static TempoFingerprint FingerprintTempo(ReadOnlySpan<LikedRow> rows) => TempoSummarize(rows).Fingerprint;

    static long Mix(long v)
    {
        ulong x = (ulong)v * 0x9E3779B97F4A7C15UL;
        x ^= x >> 29; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 32;
        return (long)x;
    }

    /// <summary>The plot's inputs, in list order: every known tempo and its Camelot colour (ARGB, 0 = none).</summary>
    public static int TempoValues(ReadOnlySpan<LikedRow> rows, Span<float> bpm, Span<uint> argb)
    {
        int n = 0;
        for (int i = 0; i < rows.Length && n < bpm.Length && n < argb.Length; i++)
        {
            double t = TempoBpm(rows[i].Track);
            if (t <= 0d) continue;
            bpm[n] = (float)t;
            argb[n] = CamelotColor(rows[i].Track);
            n++;
        }
        return n;
    }

    /// <summary>The blend's concentration over primary descriptors. <see cref="Flat"/> = no style leads.</summary>
    public readonly record struct BlendDominance(int Tagged, int AboveFloor, int Styles, string? TopTitle, int TopCount, float TopShare, bool Flat);

    public static BlendDominance BlendsDominance(ReadOnlySpan<LikedRow> rows)
    {
        if (rows.Length == 0) return default;
        var (ranked, tagged, aboveFloor) = Partition(rows);
        return DominanceOf(ranked, tagged, aboveFloor);
    }

    static BlendDominance DominanceOf(List<KeyValuePair<string, int>> ranked, int tagged, int aboveFloor)
    {
        if (aboveFloor == 0 || tagged == 0) return new BlendDominance(tagged, aboveFloor, ranked.Count, null, 0, 0f, false);
        float share = ranked[0].Value / (float)tagged;
        return new BlendDominance(tagged, aboveFloor, ranked.Count, ranked[0].Key, ranked[0].Value, share, share < BlendFlat);
    }

    /// <summary>One descriptor at or above <see cref="BlendCap"/> — or none reaching <see cref="BlendFlat"/> — collapses
    /// the bar to a pill.</summary>
    public static FactShape BlendShape(ReadOnlySpan<LikedRow> rows) => ShapeOf(BlendsDominance(rows));

    static FactShape ShapeOf(in BlendDominance d)
    {
        if (d.AboveFloor == 0) return FactShape.Absent;
        return d.TopShare >= BlendCap || d.Flat ? FactShape.Label : FactShape.Graph;
    }

    /// <summary>The per-page shape latch: a fact may upgrade while a page is open, never downgrade.</summary>
    public static FactShape Latch(FactShape previous, FactShape current) => current > previous ? current : previous;

    /// <summary>True when two row sets are the same rows: the same length with every track and stamp equal. (A handle's
    /// enrichment is a Version bump, not a different row — the 0.3 reading of 0.2.9's record equality.)</summary>
    public static bool TracksEquivalent(ReadOnlySpan<LikedRow> a, ReadOnlySpan<LikedRow> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Track != b[i].Track || a[i].AddedAtUnixSec != b[i].AddedAtUnixSec) return false;
        return true;
    }

    // ── The week card earns its card ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fewer adds than this in the whole window and the strip is a flat line with one blip.</summary>
    public const int MinWeekEvidence = 3;

    /// <summary>ABSENT when nothing was added, LABEL when the adds are too few or all in one bucket, GRAPH otherwise.</summary>
    public static FactShape WeekShape(IReadOnlyList<WeekBucket> weeks)
    {
        if (weeks is null || weeks.Count == 0) return FactShape.Absent;
        int adds = 0, active = 0;
        for (int i = 0; i < weeks.Count; i++)
        {
            int c = weeks[i].Count;
            adds += c;
            if (c > 0) active++;
        }
        if (adds == 0) return FactShape.Absent;
        return adds < MinWeekEvidence || active < 2 ? FactShape.Label : FactShape.Graph;
    }

    /// <summary>The newest usable stamp — "last like Jul 12" when the week card has no shape to show.</summary>
    public static DateTimeOffset? LatestStamp(ReadOnlySpan<LikedRow> rows)
    {
        DateTimeOffset? latest = null;
        for (int i = 0; i < rows.Length; i++)
            if (TryStamp(rows[i], out var at) && (latest is null || at > latest)) latest = at;
        return latest;
    }

    /// <summary>True when any row carries a keyed credit — the allocation-free "would the artists card mount" question.</summary>
    public static bool AnyArtistCredit(ReadOnlySpan<LikedRow> rows)
    {
        for (int i = 0; i < rows.Length; i++) if (HasKeyedCredit(rows[i].Track)) return true;
        return false;
    }

    // ── One pass per list ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every list-level fact the rail draws, computed ONCE per (membership version × track publication).</summary>
    public sealed class FactsSummary
    {
        public required IReadOnlyList<YearBucket> YearBuckets { get; init; }
        public required Dominance YearsDominance { get; init; }
        public required FactShape YearsShape { get; init; }
        public required TempoSummary Tempo { get; init; }
        public required IReadOnlyList<TagShare> BlendShares { get; init; }
        public required BlendDominance BlendDominance { get; init; }
        public required FactShape BlendShape { get; init; }
        public required IReadOnlyList<ArtistCount> Artists { get; init; }
        public required bool AnyStamped { get; init; }
        public required bool StampsSpread { get; init; }
    }

    public static FactsSummary Summarize(ReadOnlySpan<LikedRow> rows, int yearBars = 12, int blendSlices = 5, int artistCap = 40)
    {
        var buckets = YearHistogram(rows, yearBars);
        var yd = YearsDominance(buckets);

        IReadOnlyList<TagShare> shares = Array.Empty<TagShare>();
        BlendDominance bd = default;
        if (rows.Length > 0)
        {
            var (ranked, tagged, aboveFloor) = Partition(rows);
            bd = DominanceOf(ranked, tagged, aboveFloor);
            if (aboveFloor > 0 && blendSlices > 0)
            {
                int slices = Math.Min(blendSlices, aboveFloor);
                var s = new TagShare[slices];
                for (int i = 0; i < slices; i++) s[i] = new TagShare(ranked[i].Key, ranked[i].Value, ranked[i].Value / (float)tagged);
                shares = s;
            }
        }

        return new FactsSummary
        {
            YearBuckets = buckets,
            YearsDominance = yd,
            YearsShape = Shape(yd.Known, rows.Length, yd.Categories, yd.TopShare, YearsCap),
            Tempo = TempoSummarize(rows),
            BlendShares = shares,
            BlendDominance = bd,
            BlendShape = ShapeOf(bd),
            Artists = TopArtists(rows, artistCap),
            AnyStamped = AnyStampedRow(rows),
            StampsSpread = StampsSpread(rows),
        };
    }

    static bool AnyStampedRow(ReadOnlySpan<LikedRow> rows)
    {
        for (int i = 0; i < rows.Length; i++) if (TryStamp(rows[i], out _)) return true;
        return false;
    }

    // ══ 0.3: THE TWO DECISIONS 0.2.9 KEPT INSIDE A COMPONENT BODY ═══════════════════════════════════════════════════

    /// <summary>The per-page shape memory the panel owns (0.2.9 <c>LikedFactsPanel.ShapeLatch</c>): a fact may upgrade
    /// while the page is open, never fold back.</summary>
    public sealed class ShapeLatch
    {
        public FactShape Week, Years, Tempo, Blend;
    }

    /// <summary>Which cards and pills mount, decided from ONE settled summary (0.2.9 <c>LikedFactsPanel.Cards</c>,
    /// :172-233). The week card and the years card share ONE time slot; years that lost the slot fall to a pill; a week
    /// with a little activity but no shape becomes a since-line clause.</summary>
    public readonly record struct FactsPlan(
        FactShape Week, FactShape Years, FactShape Tempo, FactShape Blend,
        bool WeekCard, bool YearsCard, bool YearsPill, bool TempoCard, bool TempoPill,
        bool ArtistsCard, bool BlendCard, bool BlendPill, bool LastActivityClause, bool Stamped);

    /// <summary>The card plan. <paramref name="liked"/> is the Liked collection (stamps always mean activity); a playlist's
    /// time slot needs its stamps to SPREAD. <paramref name="weeks"/> is <see cref="LikesPerWeek"/> when stamped, else
    /// empty. Writes the upgraded shapes back into <paramref name="latch"/>.</summary>
    public static FactsPlan Plan(FactsSummary s, IReadOnlyList<WeekBucket> weeks, bool liked, ShapeLatch latch)
    {
        bool stamped = liked ? s.AnyStamped : s.StampsSpread;
        var week = latch.Week = Latch(latch.Week, stamped ? WeekShape(weeks) : FactShape.Absent);
        var years = latch.Years = Latch(latch.Years, s.YearsShape);
        var tempo = latch.Tempo = Latch(latch.Tempo, s.Tempo.Shape);
        var blend = latch.Blend = Latch(latch.Blend, s.BlendShape);
        bool weekCard = week == FactShape.Graph;
        bool tempoKnown = s.Tempo.Stats.Known > 0;
        return new FactsPlan(
            week, years, tempo, blend,
            WeekCard: weekCard,
            YearsCard: !weekCard && years == FactShape.Graph,
            YearsPill: years == FactShape.Label || (weekCard && years != FactShape.Absent),
            TempoCard: tempo == FactShape.Graph && tempoKnown,
            TempoPill: tempo == FactShape.Label && tempoKnown,
            ArtistsCard: s.Artists.Count > 0,
            BlendCard: s.BlendShares.Count > 0 && blend == FactShape.Graph,
            BlendPill: blend == FactShape.Label,
            LastActivityClause: week == FactShape.Label,
            Stamped: stamped);
    }

    /// <summary>The Liked chip rail's SET (ch 07 §2.4, 0.2.9 <c>DetailTracks.ContentFilterBar</c>): the curated set is
    /// authoritative once the account answered with chips, ordered evidenced-first; a set that is unknown or known-empty
    /// hands the bar to the descriptor-derived fallback, every chip of which is evidenced by construction.</summary>
    public static ContentFilterChipSet ChipSet(bool curatedKnown, IReadOnlyList<ContentFilterChip> curated, ReadOnlySpan<Track> tracks)
    {
        if (curatedKnown && curated is { Count: > 0 }) return ContentFilterTags.OrderByEvidence(curated, tracks);
        var derived = ContentFilterTags.Derive(tracks);
        return derived.Count == 0 ? ContentFilterChipSet.Empty : new ContentFilterChipSet(derived, derived.Count);
    }
}

public readonly partial struct User
{
}
