using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>The PURE facts behind the Liked Songs rail panel: how often you like, who you like, and what you like.
/// Engine-free (System + Wavee.Core) so <c>LikedFactsRulesTests</c> pins every rule without a page or a store.
///
/// <para>Two deliberate constraints, both honesty rather than convenience:</para>
/// <list type="bullet">
/// <item><description>Time facts (the week spark, the since-line, the save decade) are derived from
/// <c>Track.AddedAt</c> only. Artist and blend facts count every track — a bulk-republished editorial list still has
/// artists and tags. Release-year facts key on <c>Track.Year</c>. There is no play-recency fact —
/// <c>PlayLogStore</c> is a 200-entry FIFO ring, so "you have not played this in a year" cannot be answered truthfully
/// and is therefore not asked. The rediscover angle is served by <see cref="ThisWeekLastYearWindow"/>, which is a
/// pure <c>AddedAt</c> question.</description></item>
/// <item><description>Every function that needs a clock TAKES one. Nothing here reads
/// <c>DateTimeOffset.Now</c>/<c>UtcNow</c>; the component is the single clock reader, which is what makes the week
/// bucketing, the DST/leap-year boundaries and the future-stamp clamp testable at all.</description></item>
/// </list></summary>
public static class LikedFactsRules
{
    /// <summary>A stamp at or before the Unix epoch is UNKNOWN, not "liked in 1970": zero is what a missing timestamp
    /// deserialises to across half the wire formats involved, and <c>default(DateTimeOffset)</c> is what an
    /// unpopulated struct reads as. Treating either as a real like would put the since-line at 1970 and stretch the
    /// oldest-like fact over a lie.</summary>
    public static readonly DateTimeOffset UnknownStampFloor = DateTimeOffset.UnixEpoch;

    /// <summary>True when this track carries an <c>AddedAt</c> we can actually reason about (present, and past the
    /// epoch sentinel). A track that fails this is excluded from TIME facts (week, since, save decade) rather than
    /// being counted with a guessed date. Artist and blend facts do not use this gate — every credit and tag still
    /// ranks.</summary>
    public static bool TryStamp(Track track, out DateTimeOffset addedAt)
    {
        addedAt = default;
        if (track?.AddedAt is not { } at || at <= UnknownStampFloor) return false;
        addedAt = at;
        return true;
    }

    // ── This week / the sparkline ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One rolling 7-day window and how many likes landed inside it.</summary>
    public readonly record struct WeekBucket(DateTimeOffset WindowStart, int Count);

    /// <summary>Rolling 7-day windows ending at <paramref name="now"/>: bucket k covers
    /// <c>(now - 7(k+1)d, now - 7k d]</c>. Returned OLDEST-FIRST so the sparkline reads left to right, and always
    /// exactly <paramref name="weeks"/> entries — an empty week is a zero-height bar, never a missing one.
    ///
    /// <para>Deliberately NOT calendar weeks: those depend on the locale's first day, wobble across a year boundary,
    /// and would make "this week" mean something different on a Sunday than on a Monday. Rolling windows make the last
    /// bucket "this week" BY CONSTRUCTION. The arithmetic is <c>DateTimeOffset</c> (absolute instants), so a DST
    /// transition or a leap day inside the window changes nothing.</para>
    ///
    /// <para>A future-dated stamp — a clock skew on the device that wrote it — is CLAMPED to <paramref name="now"/>
    /// and counted in the newest bucket rather than silently vanishing off the right edge of the chart.</para></summary>
    public static IReadOnlyList<WeekBucket> LikesPerWeek(IReadOnlyList<Track> tracks, DateTimeOffset now, int weeks = 12)
    {
        if (weeks <= 0) return Array.Empty<WeekBucket>();

        const long WeekTicks = TimeSpan.TicksPerDay * 7L;
        var counts = new int[weeks];
        if (tracks is not null)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                if (!TryStamp(tracks[i], out var at)) continue;
                long age = at >= now ? 0L : (now - at).Ticks;      // future stamps clamp into the newest bucket
                long k = age / WeekTicks;
                if (k < weeks) counts[(int)k]++;
            }
        }

        var buckets = new WeekBucket[weeks];
        for (int i = 0; i < weeks; i++)
        {
            int k = weeks - 1 - i;                                  // oldest-first output, newest bucket is k = 0
            buckets[i] = new WeekBucket(now - TimeSpan.FromTicks(WeekTicks * (k + 1)), counts[k]);
        }
        return buckets;
    }

    /// <summary>The clock the panel buckets from: the caller's instant floored to the HOUR.
    ///
    /// <para>The buckets are ROLLING windows anchored on "now", so a raw clock read makes every bar a few milliseconds
    /// different on every render. That is invisible in the chart and fatal to the LENS: a bar's window is stored in the
    /// filter as two absolute instants, and the very next render would compute a window that no longer equals it, so
    /// the bar the user clicked would never read as lit. Flooring to the hour makes the twelve windows STABLE for the
    /// hour they were drawn in, which is the granularity a 7-day bucket deserves anyway.</para>
    ///
    /// <para>Nothing is lost at the near end: a like saved during the current partial hour is stamped AFTER this clock,
    /// and <see cref="LikesPerWeek"/> clamps a future stamp into the newest bucket — which is the bucket it belongs to.
    /// (When the hour does roll over under an active lens, the stored window stops matching any bar and the highlight
    /// goes out; the list stays filtered and the list's own lens header remains the authority on what it is showing.)</para></summary>
    public static DateTimeOffset BucketClock(DateTimeOffset now)
        => new(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);

    /// <summary>The half-open <c>(after, before]</c> Unix-ms window one sparkline bar stands for — what
    /// <c>TrackFilterState.WithAddedWindow</c> takes when that bar is clicked.
    ///
    /// <para>Derived from the bucket itself rather than recomputed from a clock, so the lens can only ever select the
    /// interval the bar was DRAWN from: a second clock read (the panel re-renders, a minute passes) would slide a
    /// rolling window off the bar the user actually pointed at.</para>
    ///
    /// <para>Half-open in that direction because the buckets are laid end to end — bucket k ends exactly where k+1
    /// begins — so a like on the seam belongs to exactly one bar and the twelve lenses partition the twelve bars.</para></summary>
    public static (long AfterMs, long BeforeMs) WeekWindowMs(in WeekBucket week)
        => (week.WindowStart.ToUnixTimeMilliseconds(),
            week.WindowStart.Add(TimeSpan.FromDays(7)).ToUnixTimeMilliseconds());

    /// <summary>The bar's interval as LOCAL instants — the one place the "Jul 27 – Aug 3" wording is derived from, so
    /// the bar's tooltip and the list's lens header cannot name two different weeks. Local, like the since-line: the
    /// week a listener is living through is the local one.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) WeekRange(in WeekBucket week)
    {
        var start = week.WindowStart.ToLocalTime();
        return (start, start.Add(TimeSpan.FromDays(7)));
    }

    /// <summary>Is THIS bar the one currently lensing the list? Compared on the window itself, not on an index: the
    /// histogram re-rolls every time the panel reads its clock, so bar 7 an hour from now is a different week.</summary>
    public static bool IsWeekLens(in TrackFilterState filter, in WeekBucket week)
    {
        var (after, before) = WeekWindowMs(week);
        return filter.AddedAfterMs == after && filter.AddedBeforeMs == before;
    }

    // ── This week, last year ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same seven weekdays exactly 52 weeks back: <c>[now - 371d, now - 364d)</c>. Whole weeks rather
    /// than a calendar year so the window lands on the same days of the week the user is living through now — which is
    /// what makes "this week last year" read as the same week rather than as an arbitrary seven days.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) ThisWeekLastYearWindow(DateTimeOffset now)
        => (now - TimeSpan.FromDays(371), now - TimeSpan.FromDays(364));

    /// <summary>The tracks whose <c>AddedAt</c> falls in <c>[start, end)</c> — the "Play them" set. Input order is
    /// kept, so the set plays back in the same order the liked list shows.</summary>
    public static IReadOnlyList<Track> LikedInWindow(IReadOnlyList<Track> tracks, DateTimeOffset start, DateTimeOffset end)
    {
        if (tracks is null || tracks.Count == 0 || end <= start) return Array.Empty<Track>();

        List<Track>? hits = null;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (!TryStamp(tracks[i], out var at)) continue;
            if (at >= start && at < end) (hits ??= new List<Track>()).Add(tracks[i]);
        }
        return (IReadOnlyList<Track>?)hits ?? Array.Empty<Track>();
    }

    // ── Most liked artists ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An artist and how many of your likes credit them.</summary>
    public readonly record struct ArtistCount(ArtistRef Artist, int Count);

    /// <summary>The identity an artist is counted, ranked and LENSED by: the uri, else the id, else the display name —
    /// and empty when the credit carries none of the three, which is the one case an artist cannot be a lens at all.
    ///
    /// <para>Public and shared on purpose: <see cref="TopArtists"/> ranks by this key and
    /// <c>TrackFilterModel.HasArtist</c> matches by it. Two spellings of "who is this" is how the face you clicked and
    /// the rows you got end up being different artists.</para></summary>
    public static string ArtistKey(ArtistRef? artist)
    {
        if (artist is null) return "";
        if (!string.IsNullOrEmpty(artist.Uri)) return artist.Uri;
        if (!string.IsNullOrEmpty(artist.Id)) return artist.Id;
        return artist.Name ?? "";
    }

    /// <summary>Is THIS artist the one currently lensing the list?</summary>
    public static bool IsArtistLens(in TrackFilterState filter, ArtistRef? artist)
        => filter.ArtistId is { Length: > 0 } id && string.Equals(id, ArtistKey(artist), StringComparison.Ordinal);

    /// <summary>Is THIS descriptor the one currently lensing the list? Case-insensitive, like the chip bar and
    /// <c>TrackFilterModel.HasTag</c> — a descriptor without a display name arrives as its lowercase wire token.</summary>
    public static bool IsTagLens(in TrackFilterState filter, string? title)
        => filter.Tag is { Length: > 0 } tag && !string.IsNullOrEmpty(title)
           && string.Equals(tag, title, StringComparison.OrdinalIgnoreCase);

    /// <summary>Which rail facts are currently lensing the list. A flags enum, not a single answer: the lenses are
    /// independent facets and DO combine (a week and an artist is a perfectly sensible question), so the list header
    /// has to be able to describe — and clear — each of them on its own.</summary>
    [Flags]
    public enum LikedLens : byte { None = 0, Week = 1, Artist = 2, Tag = 4, Year = 8, Tempo = 16 }

    /// <summary>The lenses active in <paramref name="filter"/>. Only the four the rail can set: the flyout's own
    /// facets are the flyout's to describe, and folding them in here would make the header claim a lens the rail never
    /// offered.</summary>
    public static LikedLens ActiveLenses(in TrackFilterState filter)
    {
        var lenses = LikedLens.None;
        if (filter.AddedAfterMs != 0L || filter.AddedBeforeMs != 0L) lenses |= LikedLens.Week;
        if (!string.IsNullOrEmpty(filter.ArtistId)) lenses |= LikedLens.Artist;
        if (!string.IsNullOrEmpty(filter.Tag)) lenses |= LikedLens.Tag;
        if (filter.ReleaseYearMin != 0 || filter.ReleaseYearMax != 0) lenses |= LikedLens.Year;
        if (filter.Tempo != TrackTempoBand.Any) lenses |= LikedLens.Tempo;
        return lenses;
    }

    /// <summary>Retire ONE lens, leaving every other facet — including the other lenses — exactly as it was. The
    /// header's "×" is a per-facet undo, not a reset: a user who narrowed to one artist inside one week and then drops
    /// the week means "the artist, all time", not "start over".</summary>
    public static TrackFilterState ClearLens(in TrackFilterState filter, LikedLens lens) => lens switch
    {
        LikedLens.Week => filter.WithAddedWindow(0L, 0L),
        LikedLens.Artist => filter.WithArtist(null),
        LikedLens.Tag => filter with { Tag = null },
        LikedLens.Year => filter.WithReleaseYear(0, 0),
        LikedLens.Tempo => filter with { Tempo = TrackTempoBand.Any },
        _ => filter,
    };

    /// <summary>Is THIS tempo band the one currently lensing the list? <see cref="TrackTempoBand.Any"/> is never a lens.</summary>
    public static bool IsTempoLens(in TrackFilterState filter, TrackTempoBand band)
        => band != TrackTempoBand.Any && filter.Tempo == band;

    /// <summary>Is THIS year bar the one currently lensing the list? Compared on the inclusive range itself, so a
    /// one-year bar and a wide bin cannot light each other.</summary>
    public static bool IsYearLens(in TrackFilterState filter, in YearBucket bucket)
        => filter.ReleaseYearMin == bucket.YearMin && filter.ReleaseYearMax == bucket.YearMax;

    /// <summary>Distinct credited artists by liked-track count descending, ties broken by name (the
    /// <c>ContentFilterTags</c> stable-order rule — a tie that reshuffles on every refresh makes the face pile
    /// visibly twitch).
    ///
    /// <para>EVERY credited artist on a track counts, not just the first: a feature credit is a real reason the track
    /// is in your library, and collapsing to the primary artist would hide exactly the collaborations a "most liked"
    /// fact is interesting about. Artists are keyed by uri (falling back to id, then name).</para></summary>
    public static IReadOnlyList<ArtistCount> TopArtists(IReadOnlyList<Track> tracks, int take = 5)
    {
        if (tracks is null || tracks.Count == 0 || take <= 0) return Array.Empty<ArtistCount>();

        // Counts and the representative ArtistRef are kept apart so the FIRST credit seen for a key wins the display
        // name — later rows carrying a differently-cased or abbreviated spelling must not rename an artist mid-list.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var reps = new Dictionary<string, ArtistRef>(StringComparer.Ordinal);
        for (int i = 0; i < tracks.Count; i++)
        {
            var artists = tracks[i].Artists;
            if (artists is null) continue;
            for (int a = 0; a < artists.Count; a++)
            {
                var artist = artists[a];
                if (artist is null) continue;
                string key = ArtistKey(artist);   // THE shared identity — see ArtistKey; the lens matches on the same one
                if (key.Length == 0) continue;
                counts.TryGetValue(key, out int seen);
                counts[key] = seen + 1;
                if (seen == 0) reps[key] = artist;
            }
        }
        if (counts.Count == 0) return Array.Empty<ArtistCount>();

        var ordered = new List<(ArtistRef Artist, int Count)>(counts.Count);
        foreach (var kv in counts) ordered.Add((reps[kv.Key], kv.Value));
        ordered.Sort(static (x, y) =>
        {
            int c = y.Count.CompareTo(x.Count);
            return c != 0 ? c : string.Compare(x.Artist.Name, y.Artist.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        int n = Math.Min(take, ordered.Count);
        var top = new ArtistCount[n];
        for (int i = 0; i < n; i++) top[i] = new ArtistCount(ordered[i].Artist, ordered[i].Count);
        return top;
    }

    // ── Your blend ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One slice of the blend bar: a descriptor, how many likes lead with it, and its share of the tagged
    /// likes.</summary>
    public readonly record struct TagShare(string Title, int Count, float Fraction);

    /// <summary>The blend bar. Each track contributes its PRIMARY tag only (<c>Tags[0]</c> — the server's own
    /// descending-weight order), so the shares PARTITION the tagged likes and can be stacked in one bar. Raw tag
    /// counts cannot: a track carrying three descriptors would be counted three times and the bar would sum past 100%.
    ///
    /// <para><paramref name="take"/> caps the legend, so the returned fractions sum to at most 1.0 — the remainder is
    /// the implicit "Other" the caller draws (the tag tail plus, visually, the untagged likes).</para>
    ///
    /// <para>Evidence floor: a descriptor needs <see cref="ContentFilterTags.MinTrackCount"/> carriers to appear, the
    /// same discipline the content-filter chips use. Below it the result is EMPTY and the caller mounts no card —
    /// "60% Ambient" off three tracks is a made-up statistic, and null <c>Tags</c> (not fetched) must never be
    /// presented as an answer.</para></summary>
    public static IReadOnlyList<TagShare> BlendShares(IReadOnlyList<Track> tracks, int take = 5)
    {
        if (tracks is null || tracks.Count == 0 || take <= 0) return Array.Empty<TagShare>();

        var (ranked, tagged, aboveFloor) = Partition(tracks);
        if (aboveFloor == 0) return Array.Empty<TagShare>();

        int slices = Math.Min(take, aboveFloor);
        var shares = new TagShare[slices];
        for (int i = 0; i < slices; i++)
            shares[i] = new TagShare(ranked[i].Key, ranked[i].Value, ranked[i].Value / (float)tagged);
        return shares;
    }

    /// <summary>What the bar's "Other" segment is actually made of: how big the pooled remainder is, the next few
    /// ranked descriptors inside it, and how many descriptors it still hides after those.</summary>
    /// <param name="Count">Tagged likes pooled into the remainder — <c>tagged - sum(the named slices)</c>.</param>
    /// <param name="Fraction">That remainder as a share of the tagged likes; exactly the caller's <c>1 - listed</c>.</param>
    /// <param name="Named">The next ranked descriptors after the bar's own slices, same denominator, same order.</param>
    /// <param name="MoreTags">Distinct primary descriptors in the remainder that <see cref="Named"/> does NOT name.
    /// Zero when <c>detail</c> is unbounded — an unbounded tail ENUMERATES the remainder, below-evidence-floor
    /// descriptors included (see <see cref="BlendOther"/>).</param>
    public readonly record struct BlendTail(int Count, float Fraction, IReadOnlyList<TagShare> Named, int MoreTags);

    /// <summary>Open up the "Other" segment. <paramref name="shown"/> is how many slices the bar itself names (its own
    /// <c>take</c>) and <paramref name="detail"/> how many of the tail to name back — so the answer is a strict
    /// continuation of <see cref="BlendShares"/> over the SAME partition and the SAME denominator, never a second,
    /// differently-scoped statistic that could contradict the bar it explains.
    ///
    /// <para>A default (all-zero) result means there is nothing pooled: every tagged like is already named by the bar.
    /// The caller draws no "Other" segment in that case, so it has nothing to explain either.</para>
    ///
    /// <para>THE EVIDENCE FLOOR APPLIES TO THE BAR, NOT TO THE TAIL. <paramref name="shown"/> counts slices the bar
    /// named, and the bar may only name descriptors at or above <see cref="ContentFilterTags.MinTrackCount"/> — so the
    /// tail always picks up at the (shown+1)-th ABOVE-FLOOR descriptor. But the tail itself continues down the whole
    /// ranking, below-floor descriptors included, because naming one inside a region explicitly labelled "the other N"
    /// is an ENUMERATION with an exact count ("Trance · 1 song · 0.4%"), not the inferred claim the floor exists to
    /// prevent ("60% Ambient" off three tracks, which only <see cref="BlendShares"/> could ever make). That is what lets
    /// a caller draw the tail as one tick per descriptor with no pooled slab left over: at
    /// <c>detail = int.MaxValue</c> every remaining descriptor is named and <see cref="BlendTail.MoreTags"/> is 0.</para></summary>
    public static BlendTail BlendOther(IReadOnlyList<Track> tracks, int shown, int detail = 3)
    {
        if (tracks is null || tracks.Count == 0 || shown < 0) return default;

        var (ranked, tagged, aboveFloor) = Partition(tracks);
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
        // Every distinct primary descriptor that nothing names — the ranking beyond `detail`, whether it cleared the
        // evidence floor or not. Unbounded detail leaves nothing, which is the whole point of an unbounded tail.
        int more = ranked.Count - named - tail;
        return new BlendTail(pooled, pooled / (float)tagged, next, more > 0 ? more : 0);
    }

    /// <summary>Split a tail into the descriptors big enough to earn a legend ROW and a COUNT of the ones that are not.
    ///
    /// <para>The long tail of a real library is mostly ones and twos: a legend that listed all forty would be a wall of
    /// 0% rows, and one that silently truncated at ten would leave the reader unable to tell whether it stopped because
    /// the tail ended or because the list did. So the cut is a stated THRESHOLD (<paramref name="floor"/>, a share of
    /// the same tagged denominator every other blend number uses) and the remainder is reported as a number — "… and 34
    /// under 1 %" is data, not prose.</para>
    ///
    /// <para>At or above the floor is NAMED (a descriptor sitting exactly on 1% is at 1%, not under it). Input order is
    /// preserved, so the rows stay in the tail's own rank order.</para></summary>
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

    /// <summary>How many likes a set of shares is describing — the tagged population, recovered from any slice by one
    /// exact division. Lives here rather than at the call site because it is arithmetic over this file's own output,
    /// and because the rounding deserves a table: a share is a float, so the division is reconstructive and the result
    /// is rounded, not truncated.</summary>
    public static int TaggedTotal(IReadOnlyList<TagShare> shares)
    {
        if (shares is null) return 0;
        for (int i = 0; i < shares.Count; i++)
            if (shares[i].Fraction > 0f) return (int)MathF.Round(shares[i].Count / shares[i].Fraction);
        return 0;
    }

    /// <summary>The ONE primary-descriptor pass both blend answers are built on: EVERY distinct descriptor ranked
    /// descending (ties by name — the <c>ContentFilterTags</c> stable-order rule), how many likes carried a primary
    /// descriptor at all (the denominator), and how many of the leading entries clear
    /// <see cref="ContentFilterTags.MinTrackCount"/>.
    ///
    /// <para>The evidence floor is returned as a PREFIX LENGTH rather than applied as a filter, because the two callers
    /// want opposite things from it: <see cref="BlendShares"/> may only name above-floor descriptors (it makes a claim
    /// about each one), while <see cref="BlendOther"/> enumerates the whole ranking (it makes a claim about none of
    /// them). The list is sorted by count descending, so "above the floor" is exactly its first
    /// <c>AboveFloor</c> entries and neither caller needs a second pass.</para>
    ///
    /// <para>Case-insensitive for the same reason <c>Derive</c> is: a descriptor without a display_name arrives as its
    /// lowercase wire token, and "K-Pop"/"k-pop" are one concept.</para></summary>
    static (List<KeyValuePair<string, int>> Ranked, int Tagged, int AboveFloor) Partition(IReadOnlyList<Track> tracks)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int tagged = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var tags = tracks[i].Tags;
            if (tags is not { Count: > 0 }) continue;               // null = not fetched, empty = genuinely none
            string primary = tags[0];
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

    /// <summary>When the oldest surviving like was saved, or null when nothing in the list carries a usable stamp —
    /// in which case the caller renders no since-clause rather than an invented date.</summary>
    public static DateTimeOffset? LikingSince(IReadOnlyList<Track> tracks)
        => OldestLike(tracks) is { } t && TryStamp(t, out var at) ? at : null;

    /// <summary>The track behind <see cref="LikingSince"/> — the oldest usably-stamped like.</summary>
    public static Track? OldestLike(IReadOnlyList<Track> tracks)
    {
        if (tracks is null || tracks.Count == 0) return null;

        Track? oldest = null;
        DateTimeOffset best = default;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (!TryStamp(tracks[i], out var at)) continue;
            if (oldest is null || at < best) { oldest = tracks[i]; best = at; }
        }
        return oldest;
    }

    /// <summary>Fewer stamped likes than this and the decade fact is trivia, not a pattern.</summary>
    public const int MinDecadeEvidence = 10;

    /// <summary>The decade most of your likes were SAVED in (2010 reads as "the 2010s"), or null under
    /// <see cref="MinDecadeEvidence"/> stamped likes.
    ///
    /// <para>Saved, not released: <see cref="DominantReleaseDecade"/> is the catalogue-year analogue. Ties go to the
    /// more recent decade — a library split evenly across two decades is better described by the one it is still
    /// growing into.</para></summary>
    public static int? DominantDecade(IReadOnlyList<Track> tracks)
    {
        if (tracks is null || tracks.Count == 0) return null;

        var counts = new Dictionary<int, int>();
        int stamped = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (!TryStamp(tracks[i], out var at)) continue;
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

    /// <summary>True when usable <c>AddedAt</c> stamps land on at least two distinct UTC calendar days. A single
    /// republish or copy instant is one timestamp on every row — that is not activity, and must not light a week
    /// sparkline.</summary>
    public static bool StampsSpread(IReadOnlyList<Track> tracks)
    {
        if (tracks is null || tracks.Count == 0) return false;

        DateOnly first = default;
        bool have = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (!TryStamp(tracks[i], out var at)) continue;
            var day = DateOnly.FromDateTime(at.UtcDateTime);
            if (!have) { first = day; have = true; }
            else if (day != first) return true;
        }
        return false;
    }

    /// <summary>One bar of the release-year sparkline: an inclusive year range and how many tracks fall inside it.
    /// A one-year bar has <c>YearMin == YearMax</c>; a wide bin (span &gt; 12) covers several years.</summary>
    public readonly record struct YearBucket(int YearMin, int YearMax, int Count);

    /// <summary>Twelve bars over the dated tracks' release years. Span ≤ 12: consecutive years covering
    /// <c>[min, max]</c>, padded to twelve and centred (<c>start = Max(1, min - extra/2)</c>). Span &gt; 12: twelve
    /// equal-width bins that partition <c>[min, max]</c>. Empty when nothing carries <c>Year &gt; 0</c>.
    ///
    /// <para>The peak bar is the mode — the bucket with the most tracks, ties going to the more recent bucket — so the
    /// big numeral and the tallest bar cannot disagree.</para></summary>
    public static IReadOnlyList<YearBucket> YearHistogram(IReadOnlyList<Track> tracks, int bars = 12)
    {
        if (tracks is null || tracks.Count == 0 || bars <= 0) return Array.Empty<YearBucket>();

        int minY = 0, maxY = 0, dated = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            int y = tracks[i].Year;
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
            for (int i = 0; i < bars; i++)
            {
                int year = start + i;
                buckets[i] = new YearBucket(year, year, 0);
            }
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

        for (int i = 0; i < tracks.Count; i++)
        {
            int y = tracks[i].Year;
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

    /// <summary>The year the big numeral should name for <paramref name="bucket"/>: the bar itself when it is one
    /// year, otherwise the modal year inside the bin (tie → more recent).</summary>
    public static int PeakYear(IReadOnlyList<Track> tracks, in YearBucket bucket)
    {
        if (bucket.YearMin == bucket.YearMax) return bucket.YearMin;
        if (tracks is null || tracks.Count == 0) return bucket.YearMax;

        int bestYear = bucket.YearMax, bestCount = 0;
        for (int y = bucket.YearMin; y <= bucket.YearMax; y++)
        {
            int n = 0;
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].Year == y) n++;
            if (n > bestCount || (n == bestCount && y > bestYear)) { bestYear = y; bestCount = n; }
        }
        return bestYear;
    }

    /// <summary>True when at least <see cref="MinDecadeEvidence"/> tracks carry a known release year. Below that the
    /// year sparkline is trivia, not a pattern — the same floor the save-decade fact uses.</summary>
    public static bool HasReleaseYears(IReadOnlyList<Track> tracks)
    {
        if (tracks is null) return false;
        int n = 0;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Year > 0 && ++n >= MinDecadeEvidence) return true;
        return false;
    }

    /// <summary>The decade most of the dated tracks were RELEASED in (2010 reads as "the 2010s"), or null under
    /// <see cref="MinDecadeEvidence"/> tracks with <c>Year &gt; 0</c>. Ignores <c>AddedAt</c> — a 2024 add of a 2011
    /// track is a 2010s release. Ties go to the more recent decade.</summary>
    public static int? DominantReleaseDecade(IReadOnlyList<Track> tracks)
    {
        if (tracks is null || tracks.Count == 0) return null;

        var counts = new Dictionary<int, int>();
        int dated = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            int y = tracks[i].Year;
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

    /// <summary>The dated track with the oldest release year, or null when nothing carries <c>Year &gt; 0</c>. Ties
    /// keep the first such row — input order is the list's own.</summary>
    public static Track? OldestRelease(IReadOnlyList<Track> tracks)
    {
        if (tracks is null || tracks.Count == 0) return null;

        Track? oldest = null;
        int best = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            int y = tracks[i].Year;
            if (y <= 0) continue;
            if (oldest is null || y < best) { oldest = tracks[i]; best = y; }
        }
        return oldest;
    }

    // ── Which facts EARN a card ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How a distribution fact renders: <see cref="Absent"/> (it cannot be answered honestly), a one-line
    /// <see cref="Label"/> pill (one value dominates, or the values are too few for a shape), or the full
    /// <see cref="Graph"/> card. ONE rule for every distribution, measured over the categories the card's own lens
    /// offers — the twelve year buckets, the four tempo bands, the primary descriptors — so a label always names exactly
    /// the filter that clicking it applies.</summary>
    public enum FactShape : byte { Absent = 0, Label = 1, Graph = 2 }

    /// <summary>Fewer known values than this and twelve bars are noise: 10–19 known is LABEL at most.</summary>
    public const int MinGraphEvidence = 20;
    /// <summary>A graph needs at least this many non-empty categories (the years card; the tempo card has only four
    /// bands and asks for two — a bimodal 124/174 list IS a shape).</summary>
    public const int MinGraphCategories = 3;
    public const int MinTempoGraphCategories = 2;
    /// <summary>Share of the tracks that must carry the value (tempo, years) for the fact to exist at all.</summary>
    public const float MinCoverage = 0.60f;
    /// <summary>Top-category share at or above which the fact collapses to a pill.</summary>
    public const float YearsCap = 0.50f;
    /// <summary>Tempo bands are 20–30 bpm wide, so a band has to hold more before the shape stops mattering.</summary>
    public const float TempoCap = 0.70f;
    public const float BlendCap = 0.85f;
    /// <summary>Under this top share no style leads at all — "12 styles, none over 15 %" is a label, not a bar that is
    /// 65 % "Other".</summary>
    public const float BlendFlat = 0.15f;

    /// <summary>THE resolver. <paramref name="known"/> = values present, <paramref name="total"/> = tracks,
    /// <paramref name="categories"/> = non-empty lens categories, <paramref name="topShare"/> = the largest category's
    /// share of the known values, <paramref name="cap"/> = the fact's dominance cap.</summary>
    public static FactShape Shape(int known, int total, int categories, float topShare, float cap, int minCategories = MinGraphCategories)
    {
        if (known < MinDecadeEvidence || total <= 0) return FactShape.Absent;
        if (known / (float)total < MinCoverage) return FactShape.Absent;
        if (known < MinGraphEvidence || categories < minCategories || topShare >= cap) return FactShape.Label;
        return FactShape.Graph;
    }

    /// <summary>How concentrated a distribution is over its categories: values known, non-empty categories, the
    /// largest category (ties → the LATER index, so a year tie goes to the more recent bucket like the card's peak
    /// rule) and its share of the known values.</summary>
    public readonly record struct Dominance(int Known, int Categories, int TopIndex, float TopShare);

    /// <summary>Years dominance measured over the histogram's OWN buckets — so a 1965–2024 list binned into twelve
    /// five-year bars with most tracks in the last bar reads as dominated, exactly as its chart would look.</summary>
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

    public static FactShape YearsShape(IReadOnlyList<Track> tracks, IReadOnlyList<YearBucket> buckets)
    {
        var d = YearsDominance(buckets);
        return Shape(d.Known, tracks?.Count ?? 0, d.Categories, d.TopShare, YearsCap);
    }

    /// <summary>The four filter bands in <c>TrackTempoBand</c> order (Under90, 90–119, 120–139, 140+).</summary>
    public const int TempoBandCount = 4;

    /// <summary>Tracks per tempo band (<paramref name="counts"/> needs <see cref="TempoBandCount"/> slots), using the
    /// filter's own boundary table (<c>TrackFilterModel.BandOf</c>). Returns how many tracks carry a tempo.</summary>
    public static int TempoBandCounts(IReadOnlyList<Track> tracks, Span<int> counts)
    {
        counts.Clear();
        int known = 0;
        if (tracks is null) return 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].TempoBpm is not { } bpm || bpm <= 0d) continue;
            var band = TrackFilterModel.BandOf(bpm);
            if (band == TrackTempoBand.Any) continue;
            counts[(int)band - 1]++;
            known++;
        }
        return known;
    }

    public static Dominance TempoDominance(IReadOnlyList<Track> tracks) => TempoSummarize(tracks).Dominance;

    public static FactShape TempoShape(IReadOnlyList<Track> tracks) => TempoSummarize(tracks).Shape;

    /// <summary>Tempo summary: known / total counts, the LOWER median (an even count picks the lower middle — a real
    /// track's tempo, never an average of two), and the range.</summary>
    public readonly record struct TempoStats(int Known, int Total, double Median, double Min, double Max);

    public static TempoStats TempoStatistics(IReadOnlyList<Track> tracks) => TempoSummarize(tracks).Stats;

    /// <summary>A content fingerprint of the tempos in a list — <see cref="Known"/> plus an order-independent hash of
    /// the rounded tempos. Two lists with the same tempos fingerprint equal even when they are different list
    /// INSTANCES, which is what lets a card key its geometry on the tempos themselves: the detail page rebuilds its
    /// track list on every hydration pass (descriptors, play counts, identity…), and a plot keyed on the list would
    /// re-mint its paths every time although no tempo changed.</summary>
    public readonly record struct TempoFingerprint(int Known, long Hash);

    /// <summary>Everything the rail wants to know about tempo, from ONE pass over the list: the statistics (median
    /// from an integer-bpm histogram — no sort, no allocation), the four band counts, the dominance/shape decision and
    /// the content fingerprint.</summary>
    public readonly record struct TempoSummary(TempoStats Stats, int Under90, int From90To119, int From120To139, int From140AndUp,
                                               Dominance Dominance, FactShape Shape, TempoFingerprint Fingerprint)
    {
        /// <summary>The band count by <c>(int)TrackTempoBand - 1</c> index.</summary>
        public int Count(int bandIndex) => bandIndex switch { 0 => Under90, 1 => From90To119, 2 => From120To139, 3 => From140AndUp, _ => 0 };
    }

    const int MaxBpmBin = 400;

    public static TempoSummary TempoSummarize(IReadOnlyList<Track> tracks)
    {
        int total = tracks?.Count ?? 0;
        if (total == 0) return new TempoSummary(default, 0, 0, 0, 0, new Dominance(0, 0, -1, 0f), FactShape.Absent, default);

        Span<int> bins = stackalloc int[MaxBpmBin + 1];
        Span<int> bands = stackalloc int[TempoBandCount];
        int known = 0;
        double min = double.MaxValue, max = double.MinValue;
        long hash = 0;
        for (int i = 0; i < total; i++)
        {
            if (tracks![i].TempoBpm is not { } bpm || bpm <= 0d || double.IsNaN(bpm)) continue;
            var band = TrackFilterModel.BandOf(bpm);
            if (band == TrackTempoBand.Any) continue;
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

    public static TempoFingerprint FingerprintTempo(IReadOnlyList<Track> tracks) => TempoSummarize(tracks).Fingerprint;

    static long Mix(long v)
    {
        ulong x = (ulong)v * 0x9E3779B97F4A7C15UL;
        x ^= x >> 29; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 32;
        return (long)x;
    }

    /// <summary>The plot's inputs, in list order: every known tempo into <paramref name="bpm"/> and its Camelot colour
    /// (ARGB, 0 = none) into <paramref name="argb"/>. Returns the count written (≤ both spans' lengths).</summary>
    public static int TempoValues(IReadOnlyList<Track> tracks, Span<float> bpm, Span<uint> argb)
    {
        int n = 0;
        if (tracks is null) return 0;
        for (int i = 0; i < tracks.Count && n < bpm.Length && n < argb.Length; i++)
        {
            if (tracks[i].TempoBpm is not { } t || t <= 0d) continue;
            bpm[n] = (float)t;
            argb[n] = tracks[i].CamelotColor ?? 0u;
            n++;
        }
        return n;
    }

    /// <summary>The blend's concentration over primary descriptors: how many tracks are tagged, how many descriptors
    /// clear the evidence floor, how many distinct descriptors there are, and the leading one with its share.
    /// <see cref="Flat"/> = no style leads (top share under <see cref="BlendFlat"/>).</summary>
    public readonly record struct BlendDominance(int Tagged, int AboveFloor, int Styles, string? TopTitle, int TopCount, float TopShare, bool Flat);

    public static BlendDominance BlendsDominance(IReadOnlyList<Track> tracks)
    {
        if (tracks is null || tracks.Count == 0) return default;
        var (ranked, tagged, aboveFloor) = Partition(tracks);
        return DominanceOf(ranked, tagged, aboveFloor);
    }

    static BlendDominance DominanceOf(List<KeyValuePair<string, int>> ranked, int tagged, int aboveFloor)
    {
        if (aboveFloor == 0 || tagged == 0) return new BlendDominance(tagged, aboveFloor, ranked.Count, null, 0, 0f, false);
        float share = ranked[0].Value / (float)tagged;
        return new BlendDominance(tagged, aboveFloor, ranked.Count, ranked[0].Key, ranked[0].Value, share, share < BlendFlat);
    }

    /// <summary>The blend keeps its existing evidence floor (a descriptor needs <c>ContentFilterTags.MinTrackCount</c>
    /// carriers to be named at all); on top of it, one descriptor at or above <see cref="BlendCap"/> — or none reaching
    /// <see cref="BlendFlat"/> — collapses the bar to a pill.</summary>
    public static FactShape BlendShape(IReadOnlyList<Track> tracks) => ShapeOf(BlendsDominance(tracks));

    static FactShape ShapeOf(in BlendDominance d)
    {
        if (d.AboveFloor == 0) return FactShape.Absent;
        return d.TopShare >= BlendCap || d.Flat ? FactShape.Label : FactShape.Graph;
    }

    /// <summary>The per-page shape latch: a fact may upgrade (Absent → Label → Graph) while a page is open, never
    /// downgrade — a straggler hydration batch cannot fold a card back into a pill.</summary>
    public static FactShape Latch(FactShape previous, FactShape current) => current > previous ? current : previous;

    /// <summary>True when two track lists are the same rows — the same instance, or the same count with every row
    /// reference-equal or value-equal (<c>Track</c> is a record over reused store rows). The detail page uses it to
    /// republish the PREVIOUS list instance after a refresh pass that landed nothing for this list, so every consumer
    /// keyed on the list reference skips the pass.</summary>
    public static bool TracksEquivalent(IReadOnlyList<Track>? a, IReadOnlyList<Track>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (ReferenceEquals(x, y)) continue;
            if (x is null || y is null || !x.Equals(y)) return false;
        }
        return true;
    }

    // ── The week card earns its card ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fewer adds than this in the whole 12-week window and the strip is a flat line with one blip.</summary>
    public const int MinWeekEvidence = 3;

    /// <summary>ABSENT when nothing was added in the window (the strip would be all baseline and the numeral "+0"),
    /// LABEL when the adds are too few or all in one bucket to make a shape, GRAPH otherwise.</summary>
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

    /// <summary>The newest usable stamp in the list — "last add Jul 12" when the week card has no shape to show.</summary>
    public static DateTimeOffset? LatestStamp(IReadOnlyList<Track> tracks)
    {
        if (tracks is null) return null;
        DateTimeOffset? latest = null;
        for (int i = 0; i < tracks.Count; i++)
            if (TryStamp(tracks[i], out var at) && (latest is null || at > latest)) latest = at;
        return latest;
    }

    /// <summary>True when any track carries a keyed artist credit — the allocation-free "would the artists card mount"
    /// question (<see cref="TopArtists"/> answers it too, with two dictionaries and a sort).</summary>
    public static bool AnyArtistCredit(IReadOnlyList<Track> tracks)
    {
        if (tracks is null) return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var artists = tracks[i].Artists;
            if (artists is null) continue;
            for (int a = 0; a < artists.Count; a++)
                if (ArtistKey(artists[a]).Length > 0) return true;
        }
        return false;
    }

    // ── One pass per list ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every list-level fact the rail draws, computed ONCE per track-list instance (the panel memoises it on
    /// the list reference): the year histogram and its shape, the tempo summary, the blend partition (shares, dominance,
    /// shape — one <c>Partition</c>, not three) and the ranked artists. The standalone rule functions stay for the
    /// tests and for callers that want one answer; this is the rail's bulk path.</summary>
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

    public static FactsSummary Summarize(IReadOnlyList<Track> tracks, int yearBars = 12, int blendSlices = 5, int artistCap = 40)
    {
        tracks ??= Array.Empty<Track>();
        var buckets = YearHistogram(tracks, yearBars);
        var yd = YearsDominance(buckets);

        IReadOnlyList<TagShare> shares = Array.Empty<TagShare>();
        BlendDominance bd = default;
        if (tracks.Count > 0)
        {
            var (ranked, tagged, aboveFloor) = Partition(tracks);
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
            YearsShape = Shape(yd.Known, tracks.Count, yd.Categories, yd.TopShare, YearsCap),
            Tempo = TempoSummarize(tracks),
            BlendShares = shares,
            BlendDominance = bd,
            BlendShape = ShapeOf(bd),
            Artists = TopArtists(tracks, artistCap),
            AnyStamped = AnyStampedTrack(tracks),
            StampsSpread = StampsSpread(tracks),
        };
    }

    static bool AnyStampedTrack(IReadOnlyList<Track> tracks)
    {
        for (int i = 0; i < tracks.Count; i++) if (TryStamp(tracks[i], out _)) return true;
        return false;
    }
}
