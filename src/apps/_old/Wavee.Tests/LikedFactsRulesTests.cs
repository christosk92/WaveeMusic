using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The Liked Songs rail facts (<c>Features/Detail/LikedFactsRules.cs</c>, source-included because it is
/// engine-free). Every function here takes its clock as a parameter, which is the only reason the week bucketing, the
/// DST and year boundaries, and the future-stamp clamp can be pinned at all — a rule that read
/// <c>DateTimeOffset.UtcNow</c> internally would be testable only on the day it happened to be written.
///
/// <para>The other standing rule is honesty about missing data: a track with no usable <c>AddedAt</c> is EXCLUDED from
/// every fact rather than counted at a guessed date, and a statistic without enough evidence behind it is not returned
/// at all so the caller mounts no card.</para></summary>
public class LikedFactsRulesTests
{
    static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    static Track T(DateTimeOffset? addedAt, string id = "t", IReadOnlyList<ArtistRef>? artists = null,
                   IReadOnlyList<string>? tags = null, int year = 0, double? bpm = null, string? camelot = null,
                   uint? color = null)
        => new(id, "spotify:track:" + id, "Title " + id,
            artists ?? Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
            180_000, false, null, AddedAt: addedAt, Tags: tags, Year: year,
            TempoBpm: bpm, CamelotCode: camelot, CamelotColor: color);

    static ArtistRef A(string name) => new(name, "spotify:artist:" + name, name);

    static IReadOnlyList<Track> Repeat(int n, Func<int, Track> make)
    {
        var list = new List<Track>(n);
        for (int i = 0; i < n; i++) list.Add(make(i));
        return list;
    }

    /// <summary>The bucket (in the returned oldest-first order) that a single like lands in, or -1 when it is excluded
    /// altogether. Asserts that exactly one bucket claims it — a like counted twice would inflate the sparkline.</summary>
    static int SoleBucket(DateTimeOffset? addedAt, DateTimeOffset now, int weeks = 12)
    {
        var buckets = LikedFactsRules.LikesPerWeek([T(addedAt)], now, weeks);
        Assert.Equal(weeks, buckets.Count);

        int found = -1, total = 0;
        for (int i = 0; i < buckets.Count; i++)
        {
            total += buckets[i].Count;
            if (buckets[i].Count > 0) found = i;
        }
        Assert.InRange(total, 0, 1);
        return found;
    }

    // ── LikesPerWeek: the rolling sparkline ─────────────────────────────────────────────────────────────────────────

    /// <summary>Always exactly <c>weeks</c> buckets, oldest first, each window start exactly seven days after the
    /// last. An empty week is a zero-height bar, never a missing one — a sparkline that omitted quiet weeks would
    /// compress time and read as a busier library than it is.</summary>
    [Fact]
    public void TheWindowIsTwelveContiguousSevenDayBucketsOldestFirst()
    {
        var buckets = LikedFactsRules.LikesPerWeek(Array.Empty<Track>(), Now);

        Assert.Equal(12, buckets.Count);
        Assert.Equal(Now - TimeSpan.FromDays(84), buckets[0].WindowStart);
        Assert.Equal(Now - TimeSpan.FromDays(7), buckets[11].WindowStart);
        for (int i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(TimeSpan.FromDays(7), buckets[i].WindowStart - buckets[i - 1].WindowStart);
            Assert.Equal(0, buckets[i].Count);
        }
    }

    /// <summary>Bucket k is the HALF-OPEN window <c>(now - 7(k+1)d, now - 7k d]</c>: a like exactly seven days old
    /// belongs to LAST week, not this one. "This week" is the last bucket by construction, whatever weekday it is.</summary>
    [Theory]
    [InlineData(0.0, 11)]        // right now
    [InlineData(0.5, 11)]
    [InlineData(6.99, 11)]
    [InlineData(7.0, 10)]        // the rung: exactly one week old is last week's bucket
    [InlineData(13.99, 10)]
    [InlineData(14.0, 9)]
    [InlineData(83.99, 0)]       // the oldest bucket still on the chart
    [InlineData(84.0, -1)]       // one tick past the window: off the chart entirely, not squashed into the last bar
    [InlineData(400.0, -1)]
    public void LikesLandInTheirRollingWeek(double daysAgo, int expectedBucket)
        => Assert.Equal(expectedBucket, SoleBucket(Now - TimeSpan.FromDays(daysAgo), Now));

    /// <summary>The rung is exact to the tick, not to the day.</summary>
    [Fact]
    public void TheWeekRungIsExact()
    {
        var oneWeek = TimeSpan.FromDays(7);
        Assert.Equal(11, SoleBucket(Now - oneWeek + TimeSpan.FromTicks(1), Now));
        Assert.Equal(10, SoleBucket(Now - oneWeek, Now));
    }

    /// <summary>A stamp from the future — clock skew on whichever device wrote it — is clamped to now and counted in
    /// THIS week rather than silently falling off the right edge of the chart (E12).</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(45.0)]
    [InlineData(4000.0)]
    public void FutureStampsClampIntoTheNewestBucket(double daysAhead)
        => Assert.Equal(11, SoleBucket(Now + TimeSpan.FromDays(daysAhead), Now));

    /// <summary>The windows are absolute instants, so an hour appearing or disappearing from the wall clock inside the
    /// span changes nothing: the bars stay exactly seven days wide across a DST transition.</summary>
    [Fact]
    public void DaylightSavingDoesNotMoveTheBuckets()
    {
        // Europe ends summer time on 2025-10-26; this window straddles it.
        var now = new DateTimeOffset(2025, 11, 2, 12, 0, 0, TimeSpan.FromHours(1));
        var buckets = LikedFactsRules.LikesPerWeek(
        [
            T(now - TimeSpan.FromDays(3)),     // this week
            T(now - TimeSpan.FromDays(9)),     // last week — across the transition
            T(now - TimeSpan.FromDays(7)),     // exactly a week: last week too
        ], now);

        for (int i = 1; i < buckets.Count; i++)
            Assert.Equal(TimeSpan.FromDays(7), buckets[i].WindowStart - buckets[i - 1].WindowStart);
        Assert.Equal(1, buckets[11].Count);
        Assert.Equal(2, buckets[10].Count);
    }

    /// <summary>And a year boundary inside the window is a non-event, because these are not calendar weeks.</summary>
    [Fact]
    public void AYearBoundaryDoesNotMoveTheBuckets()
    {
        var now = new DateTimeOffset(2026, 1, 3, 9, 30, 0, TimeSpan.Zero);
        var buckets = LikedFactsRules.LikesPerWeek(
        [
            T(new DateTimeOffset(2025, 12, 30, 9, 30, 0, TimeSpan.Zero)),   // 4 days back
            T(new DateTimeOffset(2025, 12, 20, 9, 30, 0, TimeSpan.Zero)),   // 14 days back
        ], now);

        Assert.Equal(1, buckets[11].Count);
        Assert.Equal(1, buckets[9].Count);
        Assert.Equal(0, buckets[10].Count);
    }

    [Fact]
    public void AskingForNoWeeksAsksForNothing()
    {
        Assert.Empty(LikedFactsRules.LikesPerWeek([T(Now)], Now, 0));
        Assert.Empty(LikedFactsRules.LikesPerWeek([T(Now)], Now, -4));
        Assert.Equal(4, LikedFactsRules.LikesPerWeek([T(Now)], Now, 4).Count);
    }

    // ── E12: the AddedAt anomaly table ──────────────────────────────────────────────────────────────────────────────

    public static TheoryData<DateTimeOffset?> UnusableStamps() => new()
    {
        (DateTimeOffset?)null,                 // never stamped — a curated/editorial row, or a thin write
        DateTimeOffset.UnixEpoch,              // a zero timestamp deserialised into a real type
        default(DateTimeOffset),               // an unpopulated struct (== DateTimeOffset.MinValue)
        DateTimeOffset.UnixEpoch - TimeSpan.FromDays(1),
        DateTimeOffset.UnixEpoch - TimeSpan.FromDays(3650),
    };

    /// <summary>A like we cannot date is excluded from TIME facts — not defaulted to today, not to 1970. Artist and
    /// blend still count the row: a bulk-republished editorial list has no usable spread but still has credits.</summary>
    [Theory]
    [MemberData(nameof(UnusableStamps))]
    public void AnUndatableLikeIsExcludedFromTimeFacts(DateTimeOffset? addedAt)
    {
        IReadOnlyList<Track> tracks = [T(addedAt, "x", [A("Aphex")], ["Ambient"])];

        Assert.Equal(-1, SoleBucket(addedAt, Now));
        Assert.Empty(LikedFactsRules.LikedInWindow(tracks, Now - TimeSpan.FromDays(4000), Now + TimeSpan.FromDays(1)));
        Assert.Null(LikedFactsRules.LikingSince(tracks));
        Assert.Null(LikedFactsRules.OldestLike(tracks));
        Assert.Null(LikedFactsRules.DominantDecade(tracks));
        Assert.False(LikedFactsRules.TryStamp(tracks[0], out _));
        Assert.False(LikedFactsRules.StampsSpread(tracks));

        var top = LikedFactsRules.TopArtists(tracks);
        Assert.Single(top);
        Assert.Equal("Aphex", top[0].Artist.Name);
        // One tagged row is below the blend evidence floor — empty is the floor, not the stamp skip.
        Assert.Empty(LikedFactsRules.BlendShares(tracks));
    }

    /// <summary>The epoch floor is a floor, not a year filter: a like genuinely saved the day after the epoch is still
    /// a like.</summary>
    [Fact]
    public void OnlyTheEpochSentinelItselfIsRejected()
    {
        var justAfter = DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(1);
        Assert.True(LikedFactsRules.TryStamp(T(justAfter), out var at));
        Assert.Equal(justAfter, at);
    }

    /// <summary>An undatable like does not poison the ones around it — the facts are computed from whatever IS
    /// datable.</summary>
    [Fact]
    public void UndatableLikesDoNotSuppressTheDatableOnes()
    {
        IReadOnlyList<Track> tracks =
        [
            T(null, "a", [A("Aphex")], ["Ambient"]),
            T(Now - TimeSpan.FromDays(2), "b", [A("Boards")], ["Ambient"]),
            T(DateTimeOffset.UnixEpoch, "c", [A("Clark")], ["Ambient"]),
        ];

        Assert.Equal("b", LikedFactsRules.OldestLike(tracks)!.Id);
        var top = LikedFactsRules.TopArtists(tracks);
        Assert.Equal(3, top.Count);
        Assert.Equal(["Aphex", "Boards", "Clark"], Names(top));
    }

    // ── This week, last year ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whole weeks back, not a calendar year: the window covers the same seven weekdays the user is living
    /// through now, which is what makes the fact read as "this week, last year".</summary>
    [Theory]
    [InlineData("2026-03-15T12:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("2024-02-29T18:45:00Z")]   // a leap day, so the arithmetic cannot be a naive year subtraction
    public void TheLastYearWindowIsSevenSameWeekdayDays(string nowIso)
    {
        var now = DateTimeOffset.Parse(nowIso, System.Globalization.CultureInfo.InvariantCulture);
        var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(now);

        Assert.Equal(now - TimeSpan.FromDays(371), start);
        Assert.Equal(now - TimeSpan.FromDays(364), end);
        Assert.Equal(TimeSpan.FromDays(7), end - start);
        Assert.Equal(now.DayOfWeek, start.DayOfWeek);
        Assert.Equal(now.DayOfWeek, end.DayOfWeek);
    }

    /// <summary>Half-open <c>[start, end)</c>, so the window's own end instant belongs to the NEXT window and a like
    /// can never be counted by two adjacent windows.</summary>
    [Fact]
    public void TheWindowIsHalfOpenAndKeepsInputOrder()
    {
        var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(Now);
        IReadOnlyList<Track> tracks =
        [
            T(start - TimeSpan.FromTicks(1), "before"),
            T(start, "atStart"),
            T(start + TimeSpan.FromDays(3), "inside"),
            T(end - TimeSpan.FromTicks(1), "justInside"),
            T(end, "atEnd"),
        ];

        var hits = LikedFactsRules.LikedInWindow(tracks, start, end);
        Assert.Equal(["atStart", "inside", "justInside"], Ids(hits));
    }

    [Fact]
    public void AnEmptyOrInvertedWindowSelectsNothing()
    {
        IReadOnlyList<Track> tracks = [T(Now)];
        Assert.Empty(LikedFactsRules.LikedInWindow(tracks, Now, Now));
        Assert.Empty(LikedFactsRules.LikedInWindow(tracks, Now, Now - TimeSpan.FromDays(1)));
        Assert.Empty(LikedFactsRules.LikedInWindow(Array.Empty<Track>(), Now - TimeSpan.FromDays(1), Now));
    }

    static string[] Ids(IReadOnlyList<Track> tracks)
    {
        var ids = new string[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) ids[i] = tracks[i].Id;
        return ids;
    }

    // ── Most liked artists ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every credited artist counts, not just the billed one: a feature credit is a real reason the track is
    /// in the library, and collapsing to the primary would hide exactly the collaborations this fact exists to
    /// surface.</summary>
    [Fact]
    public void EveryCreditCountsNotJustTheFirst()
    {
        IReadOnlyList<Track> tracks =
        [
            T(Now, "1", [A("Solo"), A("Guest")]),
            T(Now, "2", [A("Solo")]),
            T(Now, "3", [A("Guest")]),
            T(Now, "4", [A("Guest")]),
        ];

        var top = LikedFactsRules.TopArtists(tracks);
        Assert.Equal(["Guest", "Solo"], Names(top));
        Assert.Equal(3, top[0].Count);
        Assert.Equal(2, top[1].Count);
    }

    /// <summary>A tie is broken by name, so a refresh that changes nothing cannot reorder the face pile.</summary>
    [Fact]
    public void TiesAreBrokenByNameSoThePileNeverTwitches()
    {
        IReadOnlyList<Track> tracks = [T(Now, "1", [A("Zeta"), A("Alpha"), A("Mid")])];
        Assert.Equal(["Alpha", "Mid", "Zeta"], Names(LikedFactsRules.TopArtists(tracks)));
    }

    [Fact]
    public void TopArtistsIsCappedAndSurvivesEmptyInput()
    {
        var tracks = Repeat(20, i => T(Now, "t" + i, [A("A" + i.ToString("00"))]));
        Assert.Equal(5, LikedFactsRules.TopArtists(tracks).Count);
        Assert.Equal(3, LikedFactsRules.TopArtists(tracks, 3).Count);
        Assert.Empty(LikedFactsRules.TopArtists(tracks, 0));
        Assert.Empty(LikedFactsRules.TopArtists(Array.Empty<Track>()));
        Assert.Empty(LikedFactsRules.TopArtists([T(Now, "x")]));
    }

    static string[] Names(IReadOnlyList<LikedFactsRules.ArtistCount> counts)
    {
        var names = new string[counts.Count];
        for (int i = 0; i < counts.Count; i++) names[i] = counts[i].Artist.Name;
        return names;
    }

    // ── Your blend ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Only the PRIMARY descriptor counts, so the slices partition the tagged likes and can be stacked in one
    /// bar. Counting every tag would count a three-descriptor track three times and push the bar past 100%.</summary>
    [Fact]
    public void OnlyThePrimaryTagContributesSoTheBarPartitions()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(5, i => T(Now, "p" + i, tags: ["Pop", "Chill"])));
        tracks.AddRange(Repeat(4, i => T(Now, "c" + i, tags: ["Chill"])));
        tracks.AddRange(Repeat(3, i => T(Now, "j" + i, tags: ["Jazz"])));

        var shares = LikedFactsRules.BlendShares(tracks);
        Assert.Equal(["Pop", "Chill", "Jazz"], Titles(shares));
        Assert.Equal([5, 4, 3], Counts(shares));

        // 12 tagged likes, all of them represented: the slices sum to exactly one bar.
        Assert.InRange(Sum(shares), 0.9999f, 1.0001f);
        Assert.InRange(shares[0].Fraction, 5f / 12f - 0.0001f, 5f / 12f + 0.0001f);
    }

    /// <summary>Capping the legend leaves a remainder, which is the caller's "Other" — the slices must therefore never
    /// sum past 1.0 whatever the take.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(50)]
    public void SlicesNeverSumPastOneBar(int take)
    {
        var tracks = new List<Track>();
        for (int t = 0; t < 8; t++)
            tracks.AddRange(Repeat(3 + t, i => T(Now, $"t{t}_{i}", tags: ["tag" + t])));
        tracks.AddRange(Repeat(4, i => T(Now, "untagged" + i)));

        var shares = LikedFactsRules.BlendShares(tracks, take);
        Assert.Equal(Math.Min(take, 8), shares.Count);
        Assert.InRange(Sum(shares), 0f, 1.0001f);
        foreach (var s in shares) Assert.InRange(s.Fraction, 0f, 1.0001f);
    }

    /// <summary>E14: below the evidence floor there is no card at all. "60% Ambient" derived from two tracks is a made
    /// up statistic, and the floor is the SAME one the content-filter chips use so the two cannot disagree.</summary>
    [Fact]
    public void BelowTheEvidenceFloorThereIsNoBlend()
    {
        Assert.Equal(3, ContentFilterTags.MinTrackCount);

        var justUnder = Repeat(ContentFilterTags.MinTrackCount - 1, i => T(Now, "t" + i, tags: ["Ambient"]));
        Assert.Empty(LikedFactsRules.BlendShares(justUnder));

        var atTheFloor = Repeat(ContentFilterTags.MinTrackCount, i => T(Now, "t" + i, tags: ["Ambient"]));
        var shares = LikedFactsRules.BlendShares(atTheFloor);
        Assert.Single(shares);
        Assert.Equal("Ambient", shares[0].Title);
        Assert.InRange(shares[0].Fraction, 0.9999f, 1.0001f);
    }

    /// <summary>Null Tags means "descriptor enrichment has not landed", empty means "this track genuinely has none".
    /// Neither is a blend, and neither may be presented as one.</summary>
    [Fact]
    public void UnenrichedAndUntaggedLikesAreNotABlend()
    {
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i))));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i, tags: Array.Empty<string>()))));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i, tags: ["  "]))));
        Assert.Empty(LikedFactsRules.BlendShares(Array.Empty<Track>()));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i, tags: ["Pop"])), 0));
    }

    /// <summary>Casing variants are one concept, exactly as they are for the chips — a descriptor arriving without a
    /// display name comes through as its lowercase wire token.</summary>
    [Fact]
    public void CasingVariantsCollapseToOneSlice()
    {
        IReadOnlyList<Track> tracks =
        [
            T(Now, "a", tags: ["K-Pop"]), T(Now, "b", tags: ["k-pop"]), T(Now, "c", tags: ["K-POP"]),
        ];

        var shares = LikedFactsRules.BlendShares(tracks);
        Assert.Single(shares);
        Assert.Equal(3, shares[0].Count);
    }

    // ── Your blend: what "Other" pools ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Eight descriptors above the floor (3…10 carriers), two below it, four untagged likes. 52 + 3 = 55
    /// tagged likes; the untagged four are outside the partition entirely, exactly as the bar draws it.</summary>
    static IReadOnlyList<Track> BlendLibrary()
    {
        var tracks = new List<Track>();
        for (int t = 0; t < 8; t++)
            tracks.AddRange(Repeat(3 + t, i => T(Now, $"t{t}_{i}", tags: ["tag" + t])));
        tracks.AddRange(Repeat(2, i => T(Now, "rare" + i, tags: ["Rare"])));      // below ContentFilterTags.MinTrackCount
        tracks.Add(T(Now, "rarest", tags: ["Rarest"]));                            // …and so is this one
        tracks.AddRange(Repeat(4, i => T(Now, "untagged" + i)));
        return tracks;
    }

    /// <summary>The tail is a strict CONTINUATION of the bar, not a second statistic: same ranking, same denominator.
    /// A tooltip that re-derived its percentages over the pooled likes alone would name shares that visibly contradict
    /// the bar the pointer is resting on.</summary>
    [Fact]
    public void TheOtherTailContinuesTheSameRankingAndDenominator()
    {
        var tracks = BlendLibrary();
        var bar = LikedFactsRules.BlendShares(tracks, 5);
        var tail = LikedFactsRules.BlendOther(tracks, 5, 3);

        // The bar names the five biggest; the tail picks up at the sixth.
        Assert.Equal(["tag7", "tag6", "tag5", "tag4", "tag3"], Titles(bar));
        Assert.Equal(["tag2", "tag1", "tag0"], Titles(tail.Named));
        Assert.Equal([5, 4, 3], Counts(tail.Named));

        // 55 tagged likes, 40 of them named by the bar → 15 pooled, and every fraction is over the same 55.
        Assert.Equal(15, tail.Count);
        Assert.InRange(tail.Fraction, 15f / 55f - 0.0001f, 15f / 55f + 0.0001f);
        Assert.InRange(tail.Named[0].Fraction, 5f / 55f - 0.0001f, 5f / 55f + 0.0001f);
        Assert.InRange(bar[0].Fraction, 10f / 55f - 0.0001f, 10f / 55f + 0.0001f);

        // …and the two together are the whole bar.
        Assert.InRange(Sum(bar) + tail.Fraction, 0.9999f, 1.0001f);
    }

    /// <summary>"and N more" counts the descriptors nothing named — INCLUDING the ones below the evidence floor. They
    /// are exactly what the remainder pools, so omitting them from the count would understate the tail while the bar
    /// keeps drawing their likes.</summary>
    [Theory]
    [InlineData(5, 3, 3, 2)]    // bar names 5, a 3-deep tail names 3 → 2 left, and both are below-floor
    [InlineData(5, 0, 0, 5)]    // no detail at all → the 3 ranked leftovers plus the 2 below-floor
    [InlineData(0, 2, 2, 8)]    // nothing named by the bar → the whole partition is the tail
    [InlineData(1, 3, 3, 6)]    // the tail always picks up where the bar stopped, whatever the bar named
    public void TheMoreCountCoversEveryDescriptorNothingNames(int shown, int detail, int named, int more)
    {
        var tail = LikedFactsRules.BlendOther(BlendLibrary(), shown, detail);
        Assert.Equal(named, tail.Named.Count);
        Assert.Equal(more, tail.MoreTags);
    }

    /// <summary>An UNBOUNDED tail enumerates the remainder completely — every descriptor named, nothing left over —
    /// which is what lets the card draw one tick per descriptor with no pooled slab behind them.
    ///
    /// <para>Below-evidence-floor descriptors are named HERE and nowhere else. The floor exists to stop the bar
    /// INFERRING ("60 % Ambient" off three tracks); listing "Rarest · 1 song" inside a region explicitly labelled "the
    /// other N" infers nothing — it is an enumeration with an exact count. <see cref="LikedFactsRules.BlendShares"/> is
    /// the surface that makes claims, and it still refuses to name them (the row below re-asserts that).</para></summary>
    [Fact]
    public void AnUnboundedTailNamesEveryRemainingDescriptor()
    {
        var tracks = BlendLibrary();
        var bar = LikedFactsRules.BlendShares(tracks, 5);
        var tail = LikedFactsRules.BlendOther(tracks, bar.Count, int.MaxValue);

        // Five named by the bar, ten distinct descriptors in all → five in the tail, and NOTHING unnamed after them.
        Assert.Equal(["tag2", "tag1", "tag0", "Rare", "Rarest"], Titles(tail.Named));
        Assert.Equal([5, 4, 3, 2, 1], Counts(tail.Named));
        Assert.Equal(0, tail.MoreTags);

        // The tail's own counts add up to the pooled count exactly — that identity is what makes a tick strip drawn
        // from these widths a true partition of the remainder rather than an approximation of it.
        int summed = 0;
        for (int i = 0; i < tail.Named.Count; i++) summed += tail.Named[i].Count;
        Assert.Equal(tail.Count, summed);

        // …and the evidence floor still governs the BAR: "Rare" and "Rarest" can never become slices of it.
        Assert.DoesNotContain("Rare", Titles(LikedFactsRules.BlendShares(tracks, 50)));
        Assert.DoesNotContain("Rarest", Titles(LikedFactsRules.BlendShares(tracks, 50)));
        Assert.Equal(8, LikedFactsRules.BlendShares(tracks, 50).Count);
    }

    /// <summary>The tail legend's cut: at or above the floor is a ROW, below it is a NUMBER. Exactly on the floor is
    /// named — a descriptor sitting on 1 % is at 1 %, not under it — and the rows keep the tail's own rank order.</summary>
    [Fact]
    public void TailSplitNamesAtOrAboveTheFloorAndCountsTheRest()
    {
        IReadOnlyList<LikedFactsRules.TagShare> tail =
        [
            new("EDM", 9, 0.07f), new("R&B", 8, 0.02f), new("Chill", 3, 0.01f),   // 0.01 is ON the floor → named
            new("Trap", 2, 0.009f), new("Ska", 1, 0.004f),
        ];

        var (named, under) = LikedFactsRules.TailSplit(tail);
        Assert.Equal(["EDM", "R&B", "Chill"], Titles(named));
        Assert.Equal(2, under);

        // The floor is a parameter, not a constant: raising it moves rows into the count, never loses them.
        var (fewer, moreUnder) = LikedFactsRules.TailSplit(tail, 0.05f);
        Assert.Equal(["EDM"], Titles(fewer));
        Assert.Equal(4, moreUnder);

        // Everything above the floor ⇒ the input list comes straight back (no copy, no reordering).
        var (all, none) = LikedFactsRules.TailSplit(tail, 0f);
        Assert.Same(tail, all);
        Assert.Equal(0, none);
    }

    /// <summary>No tail, no split — and never a throw: an empty tail is the normal state of a bar that named
    /// everything, and a tail entirely under the floor is a legend of one caption.</summary>
    [Fact]
    public void TailSplitOfNothingIsNothing()
    {
        Assert.Empty(LikedFactsRules.TailSplit(Array.Empty<LikedFactsRules.TagShare>()).Named);
        Assert.Equal(0, LikedFactsRules.TailSplit(Array.Empty<LikedFactsRules.TagShare>()).UnderFloor);
        Assert.Empty(LikedFactsRules.TailSplit(null!).Named);

        IReadOnlyList<LikedFactsRules.TagShare> tiny = [new("Ska", 1, 0.004f), new("Emo", 1, 0.004f)];
        var (named, under) = LikedFactsRules.TailSplit(tiny);
        Assert.Empty(named);
        Assert.Equal(2, under);
    }

    /// <summary>Nothing pooled means no answer: when the named slices already cover every tagged like there is no
    /// "Other" segment for the tooltip to open up, and a zero-count bubble would be worse than none.</summary>
    [Fact]
    public void AFullyNamedBarHasNoTail()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(6, i => T(Now, "a" + i, tags: ["Pop"])));
        tracks.AddRange(Repeat(4, i => T(Now, "b" + i, tags: ["Jazz"])));
        tracks.AddRange(Repeat(3, i => T(Now, "c" + i)));            // untagged — outside the partition, not the tail

        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(tracks, 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(Array.Empty<Track>(), 5, 3));
        // The 0.2.0.1 crash: the card dereferenced `Named` on the default answer. A default tail is EMPTY, never null.
        Assert.Empty(default(LikedFactsRules.BlendTail).Named);
        Assert.Empty(LikedFactsRules.BlendOther(tracks, 5, 3).Named);
        Assert.Empty(LikedFactsRules.BlendOther(Array.Empty<Track>(), 5, 3).Named);
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(null!, 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(Repeat(20, i => T(Now, "t" + i)), 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(tracks, -1, 3));
    }

    /// <summary>The tagged population recovered from any one slice. The share is a float, so the division is
    /// reconstructive: it must ROUND, not truncate — 5/(5/12f) lands a hair under 12 in single precision and a cast
    /// would print the card's header as "11 songs" over a bar drawn from twelve.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(55)]
    [InlineData(3)]
    [InlineData(9999)]
    public void TheTaggedTotalIsRecoveredFromAnySlice(int tagged)
    {
        var tracks = new List<Track>();
        int rest = tagged;
        for (int t = 0; t < 3 && rest > 4; t++, rest -= 4)
            tracks.AddRange(Repeat(4, i => T(Now, $"t{t}_{i}", tags: ["tag" + t])));
        int carry = rest;
        tracks.AddRange(Repeat(carry, i => T(Now, "z" + i, tags: ["ZZ"])));

        var shares = LikedFactsRules.BlendShares(tracks, 50);
        Assert.Equal(tagged, LikedFactsRules.TaggedTotal(shares));
    }

    /// <summary>No slices, no population — and never a throw: the card is not mounted in that case, but the helper is
    /// public and must answer rather than fault.</summary>
    [Fact]
    public void TheTaggedTotalOfNothingIsZero()
    {
        Assert.Equal(0, LikedFactsRules.TaggedTotal(Array.Empty<LikedFactsRules.TagShare>()));
        Assert.Equal(0, LikedFactsRules.TaggedTotal(null!));
        Assert.Equal(0, LikedFactsRules.TaggedTotal([new LikedFactsRules.TagShare("Pop", 4, 0f)]));
    }

    static string[] Titles(IReadOnlyList<LikedFactsRules.TagShare> shares)
    {
        var titles = new string[shares.Count];
        for (int i = 0; i < shares.Count; i++) titles[i] = shares[i].Title;
        return titles;
    }

    static int[] Counts(IReadOnlyList<LikedFactsRules.TagShare> shares)
    {
        var counts = new int[shares.Count];
        for (int i = 0; i < shares.Count; i++) counts[i] = shares[i].Count;
        return counts;
    }

    static float Sum(IReadOnlyList<LikedFactsRules.TagShare> shares)
    {
        float sum = 0f;
        for (int i = 0; i < shares.Count; i++) sum += shares[i].Fraction;
        return sum;
    }

    // ── The since-line ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSinceLineIsTheOldestDatableLike()
    {
        var oldest = new DateTimeOffset(2019, 4, 2, 8, 0, 0, TimeSpan.Zero);
        IReadOnlyList<Track> tracks =
        [
            T(Now, "new"),
            T(DateTimeOffset.UnixEpoch, "bogus"),
            T(oldest, "oldest"),
            T(null, "unstamped"),
        ];

        Assert.Equal(oldest, LikedFactsRules.LikingSince(tracks));
        Assert.Equal("oldest", LikedFactsRules.OldestLike(tracks)!.Id);
    }

    [Fact]
    public void NothingDatableMeansNoSinceLine()
    {
        Assert.Null(LikedFactsRules.LikingSince(Array.Empty<Track>()));
        Assert.Null(LikedFactsRules.OldestLike(Array.Empty<Track>()));
        Assert.Null(LikedFactsRules.LikingSince(null!));
        Assert.Null(LikedFactsRules.OldestLike(null!));
    }

    // ── DominantDecade ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The mode of the decades likes were SAVED in — the only decade the data can honestly speak to, since
    /// Track carries no release year.</summary>
    [Fact]
    public void TheDominantDecadeIsTheModeOfTheSaveDates()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(6, i => T(new DateTimeOffset(2021, 5, 1, 0, 0, 0, TimeSpan.Zero), "a" + i)));
        tracks.AddRange(Repeat(4, i => T(new DateTimeOffset(2015, 5, 1, 0, 0, 0, TimeSpan.Zero), "b" + i)));

        Assert.Equal(2020, LikedFactsRules.DominantDecade(tracks));
    }

    /// <summary>A dead heat goes to the more recent decade: a library split evenly is better described by the one it
    /// is still growing into.</summary>
    [Fact]
    public void ATieGoesToTheMoreRecentDecade()
    {
        var tracks = new List<Track>();
        tracks.AddRange(Repeat(5, i => T(new DateTimeOffset(2016, 5, 1, 0, 0, 0, TimeSpan.Zero), "a" + i)));
        tracks.AddRange(Repeat(5, i => T(new DateTimeOffset(2022, 5, 1, 0, 0, 0, TimeSpan.Zero), "b" + i)));

        Assert.Equal(2020, LikedFactsRules.DominantDecade(tracks));
    }

    /// <summary>The mode of a handful is trivia, not a pattern, so under the evidence floor there is no answer and the
    /// clause is simply not rendered.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(40, true)]
    public void TheDecadeNeedsEnoughStampedLikes(int stamped, bool answered)
    {
        Assert.Equal(10, LikedFactsRules.MinDecadeEvidence);

        var tracks = Repeat(stamped, i => T(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), "t" + i));
        Assert.Equal(answered ? 2020 : (int?)null, LikedFactsRules.DominantDecade(tracks));
    }

    /// <summary>And undatable likes do not count toward the floor — twenty unstamped rows are not evidence of
    /// anything.</summary>
    [Fact]
    public void UndatableLikesDoNotCountTowardTheDecadeFloor()
        => Assert.Null(LikedFactsRules.DominantDecade(Repeat(20, i => T(null, "t" + i))));

    // ── The facts as LENSES over the track list ────────────────────────────────────────────────────────────────────

    /// <summary>THE invariant behind the sparkline lens: clicking bar k returns exactly the likes bar k counted.
    /// Asserted against the real filter predicate, so the histogram and the filter cannot drift apart — they are two
    /// readings of one interval and this is the test that keeps them one.</summary>
    [Fact]
    public void EachBarsWindowSelectsExactlyTheLikesThatBarCounted()
    {
        // Twenty-eight likes spread one per six hours back from `Now`, so several bars are non-empty and one like sits
        // exactly on a bucket seam.
        var tracks = Repeat(28, i => T(Now.AddHours(-6 * i), "t" + i));
        var buckets = LikedFactsRules.LikesPerWeek(tracks, Now);

        int selectedTotal = 0;
        for (int b = 0; b < buckets.Count; b++)
        {
            var (after, before) = LikedFactsRules.WeekWindowMs(buckets[b]);
            var lens = TrackFilterState.Default.WithAddedWindow(after, before);

            int selected = 0;
            foreach (var t in tracks)
                if (TrackFilterModel.Matches(t, "", lens, false, false, Now)) selected++;

            Assert.Equal(buckets[b].Count, selected);
            selectedTotal += selected;
        }
        Assert.Equal(28, selectedTotal);   // and between them the twelve lenses partition the stamped likes
    }

    /// <summary>Consecutive bars ABUT: bucket k's upper bound is bucket k+1's lower bound to the millisecond. That is
    /// what makes the half-open <c>(after, before]</c> rule a partition rather than an approximation.</summary>
    [Fact]
    public void ConsecutiveBarWindowsAbutExactly()
    {
        var buckets = LikedFactsRules.LikesPerWeek([], Now);
        for (int i = 1; i < buckets.Count; i++)
        {
            var (_, prevEnd) = LikedFactsRules.WeekWindowMs(buckets[i - 1]);
            var (nextStart, _) = LikedFactsRules.WeekWindowMs(buckets[i]);
            Assert.Equal(prevEnd, nextStart);
        }
    }

    /// <summary>The lit bar is identified by its WINDOW, never by its index: the histogram re-rolls on every clock
    /// read, so bar 7 an hour from now is a different week.</summary>
    [Fact]
    public void OnlyTheLensedBarReadsAsLit()
    {
        var buckets = LikedFactsRules.LikesPerWeek([], Now);
        var (after, before) = LikedFactsRules.WeekWindowMs(buckets[4]);
        var lens = TrackFilterState.Default.WithAddedWindow(after, before);

        for (int i = 0; i < buckets.Count; i++)
            Assert.Equal(i == 4, LikedFactsRules.IsWeekLens(lens, buckets[i]));

        // The same twelve bars, an hour later: every window has slid, so none of them is the one that is on.
        var later = LikedFactsRules.LikesPerWeek([], Now.AddHours(1));
        for (int i = 0; i < later.Count; i++) Assert.False(LikedFactsRules.IsWeekLens(lens, later[i]));
    }

    /// <summary>Why the panel floors its clock: two renders inside the same hour must produce the SAME twelve windows,
    /// or the bar that was clicked stops matching the window the lens stored and never reads as lit.</summary>
    [Fact]
    public void ABarsWindowIsStableAcrossRendersWithinTheHour()
    {
        var early = LikedFactsRules.BucketClock(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));
        var late = LikedFactsRules.BucketClock(new DateTimeOffset(2026, 3, 15, 12, 59, 59, TimeSpan.Zero));
        Assert.Equal(early, late);

        var drawn = LikedFactsRules.LikesPerWeek([], early);
        var redrawn = LikedFactsRules.LikesPerWeek([], late);
        var (after, before) = LikedFactsRules.WeekWindowMs(drawn[4]);
        var lens = TrackFilterState.Default.WithAddedWindow(after, before);

        Assert.True(LikedFactsRules.IsWeekLens(lens, redrawn[4]));
    }

    /// <summary>And nothing is lost at the near end: a like saved during the current PARTIAL hour is stamped after the
    /// floored clock, and the future-stamp clamp puts it in the newest bar — which is the bar it belongs to.</summary>
    [Fact]
    public void ALikeSavedSinceTheFlooredHourStillLandsInTheNewestBar()
    {
        var wall = new DateTimeOffset(2026, 3, 15, 12, 40, 0, TimeSpan.Zero);
        var buckets = LikedFactsRules.LikesPerWeek([T(wall)], LikedFactsRules.BucketClock(wall));

        Assert.Equal(1, buckets[buckets.Count - 1].Count);
    }

    /// <summary>The identity ladder the pile ranks by and the filter matches by — uri, else id, else name, else
    /// nothing at all (a credit that cannot be a lens).</summary>
    [Theory]
    [InlineData("i", "u", "n", "u")]
    [InlineData("i", "", "n", "i")]
    [InlineData("", "", "n", "n")]
    [InlineData("", "", "", "")]
    public void ArtistKeyPrefersUriThenIdThenName(string id, string uri, string name, string expected)
        => Assert.Equal(expected, LikedFactsRules.ArtistKey(new ArtistRef(id, uri, name)));

    [Fact]
    public void ArtistKeyOfNothingIsEmpty() => Assert.Equal("", LikedFactsRules.ArtistKey(null));

    [Fact]
    public void TheArtistLensIsRecognisedByTheSameKeyItWasSetFrom()
    {
        var artist = A("vaultboy");
        var lens = TrackFilterState.Default.WithArtist(LikedFactsRules.ArtistKey(artist), artist.Name);

        Assert.True(LikedFactsRules.IsArtistLens(lens, artist));
        Assert.False(LikedFactsRules.IsArtistLens(lens, A("Henry Moodie")));
        Assert.False(LikedFactsRules.IsArtistLens(TrackFilterState.Default, artist));
    }

    /// <summary>Case-insensitive, like the chip bar and the filter: a descriptor with no display name arrives as its
    /// lowercase wire token, and "K-Pop"/"k-pop" are one concept.</summary>
    [Theory]
    [InlineData("K-Pop", "K-Pop", true)]
    [InlineData("k-pop", "K-Pop", true)]
    [InlineData("Pop", "K-Pop", false)]
    [InlineData("K-Pop", "", false)]
    public void TheTagLensMatchesTheChipsCaseInsensitively(string active, string title, bool expected)
        => Assert.Equal(expected, LikedFactsRules.IsTagLens(TrackFilterState.Default with { Tag = active }, title));

    /// <summary>The lenses are independent facets that combine, so the header has to be able to name each of them.</summary>
    [Fact]
    public void ActiveLensesReportsEveryRailFacetThatIsOn()
    {
        Assert.Equal(LikedFactsRules.LikedLens.None, LikedFactsRules.ActiveLenses(TrackFilterState.Default));

        var all = TrackFilterState.Default
            .WithAddedWindow(1_000L, 2_000L)
            .WithArtist("spotify:artist:a", "vaultboy") with { Tag = "Pop" };

        Assert.Equal(LikedFactsRules.LikedLens.Week | LikedFactsRules.LikedLens.Artist | LikedFactsRules.LikedLens.Tag,
                     LikedFactsRules.ActiveLenses(all));

        // The flyout's own facets are the flyout's to describe — the header must not claim a lens the rail never offered.
        Assert.Equal(LikedFactsRules.LikedLens.None,
                     LikedFactsRules.ActiveLenses(TrackFilterState.Default with { Duration = TrackDurationRange.OverFiveMinutes }));
    }

    /// <summary>The header's clear is a PER-FACET undo, not a reset: dropping the week from "this week, by vaultboy"
    /// means "vaultboy, all time" — not "start over".</summary>
    [Fact]
    public void ClearingOneLensLeavesTheOthersStanding()
    {
        var all = TrackFilterState.Default
            .WithAddedWindow(1_000L, 2_000L)
            .WithArtist("spotify:artist:a", "vaultboy") with { Tag = "Pop", Duration = TrackDurationRange.OverFiveMinutes };

        var noWeek = LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Week);
        Assert.Equal(0L, noWeek.AddedAfterMs);
        Assert.Equal("spotify:artist:a", noWeek.ArtistId);
        Assert.Equal("Pop", noWeek.Tag);
        Assert.Equal(TrackDurationRange.OverFiveMinutes, noWeek.Duration);   // and the flyout's facets are untouched

        var noArtist = LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Artist);
        Assert.Null(noArtist.ArtistId);
        Assert.Null(noArtist.ArtistName);
        Assert.Equal(1_000L, noArtist.AddedAfterMs);

        Assert.Null(LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Tag).Tag);
        Assert.Equal(0, LikedFactsRules.ClearLens(all.WithReleaseYear(2010, 2014), LikedFactsRules.LikedLens.Year).ReleaseYearMin);
        Assert.Equal(all, LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.None));
    }

    [Fact]
    public void UnstampedTracksStillRankInArtistsAndBlend()
    {
        var tracks = Repeat(ContentFilterTags.MinTrackCount, i => T(null, "t" + i, [A("Aphex")], ["Ambient"]));
        var top = LikedFactsRules.TopArtists(tracks);
        Assert.Single(top);
        Assert.Equal("Aphex", top[0].Artist.Name);
        Assert.Equal(ContentFilterTags.MinTrackCount, top[0].Count);

        var shares = LikedFactsRules.BlendShares(tracks);
        Assert.Single(shares);
        Assert.Equal("Ambient", shares[0].Title);
    }

    [Fact]
    public void OneSharedTimestampIsNotStampSpread()
    {
        var at = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var tracks = Repeat(20, i => T(at, "t" + i));
        Assert.False(LikedFactsRules.StampsSpread(tracks));
        Assert.False(LikedFactsRules.StampsSpread([T(at, "a"), T(at, "b")]));
    }

    [Fact]
    public void TwoDistinctUtcDaysAreStampSpread()
    {
        var a = new DateTimeOffset(2026, 3, 15, 23, 0, 0, TimeSpan.Zero);
        var b = new DateTimeOffset(2026, 3, 16, 1, 0, 0, TimeSpan.Zero);
        Assert.True(LikedFactsRules.StampsSpread([T(a, "a"), T(b, "b")]));
        // Same UTC calendar day is not spread, even an hour apart.
        Assert.False(LikedFactsRules.StampsSpread([T(a, "a"), T(a.AddHours(-1), "b")]));
    }

    [Fact]
    public void YearHistogramIsConsecutiveWhenTheSpanFitsTwelveBars()
    {
        var tracks = new List<Track>();
        for (int y = 2010; y <= 2021; y++)
            tracks.Add(T(null, "t" + y, year: y));

        var bars = LikedFactsRules.YearHistogram(tracks);
        Assert.Equal(12, bars.Count);
        Assert.Equal(2010, bars[0].YearMin);
        Assert.Equal(2010, bars[0].YearMax);
        Assert.Equal(2021, bars[11].YearMin);
        Assert.Equal(2021, bars[11].YearMax);
        for (int i = 0; i < bars.Count; i++)
        {
            Assert.Equal(1, bars[i].Count);
            Assert.Equal(bars[i].YearMin, bars[i].YearMax);
        }
    }

    [Fact]
    public void YearHistogramBinsAWideSpanRatherThanPickingTwelveRandomYears()
    {
        var tracks = new List<Track>();
        for (int y = 1960; y <= 2020; y++)
            tracks.Add(T(null, "t" + y, year: y));

        var bars = LikedFactsRules.YearHistogram(tracks);
        Assert.Equal(12, bars.Count);
        Assert.Equal(1960, bars[0].YearMin);
        Assert.Equal(2020, bars[11].YearMax);
        Assert.True(bars[0].YearMax > bars[0].YearMin, "a 61-year span must bin, not lie as 12 consecutive years");
        int covered = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            Assert.True(bars[i].YearMax >= bars[i].YearMin);
            covered += bars[i].Count;
            if (i > 0) Assert.Equal(bars[i - 1].YearMax + 1, bars[i].YearMin);
        }
        Assert.Equal(61, covered);
    }

    [Fact]
    public void DominantReleaseDecadeIgnoresAddedAt()
    {
        var added = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var tracks = Repeat(10, i => T(added, "t" + i, year: 2011));
        Assert.Equal(2010, LikedFactsRules.DominantReleaseDecade(tracks));
        Assert.Equal(2020, LikedFactsRules.DominantDecade(tracks)); // saved in the 2020s
        Assert.Equal("t0", LikedFactsRules.OldestRelease(tracks)!.Id);
    }

    [Fact]
    public void HasReleaseYearsUsesTheDecadeEvidenceFloor()
    {
        Assert.False(LikedFactsRules.HasReleaseYears(Repeat(9, i => T(null, "t" + i, year: 2014))));
        Assert.True(LikedFactsRules.HasReleaseYears(Repeat(10, i => T(null, "t" + i, year: 2014))));
    }

    [Fact]
    public void YearLensIsAnInclusiveWindow()
    {
        var filter = TrackFilterState.Default.WithReleaseYear(2010, 2014);
        Assert.Equal(LikedFactsRules.LikedLens.Year, LikedFactsRules.ActiveLenses(filter));
        Assert.True(LikedFactsRules.IsYearLens(filter, new LikedFactsRules.YearBucket(2010, 2014, 3)));
        Assert.False(LikedFactsRules.IsYearLens(filter, new LikedFactsRules.YearBucket(2010, 2010, 1)));
        Assert.Equal(1, filter.ActiveCount);
        Assert.True(TrackFilterModel.Matches(
            T(null, "in", year: 2012), "", filter, false, false, Now));
        Assert.False(TrackFilterModel.Matches(
            T(null, "out", year: 2009), "", filter, false, false, Now));
        Assert.False(TrackFilterModel.Matches(
            T(null, "unknown", year: 0), "", filter, false, false, Now));
    }

    // ── Which facts earn a card ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(9, 10, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Absent)]     // under the evidence floor
    [InlineData(10, 20, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Absent)]    // 50 % coverage < 60 %
    [InlineData(12, 20, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Label)]     // 60 % coverage but < 20 known
    [InlineData(19, 20, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Label)]
    [InlineData(20, 20, 2, 0.3f, 0.5f, LikedFactsRules.FactShape.Label)]     // too few categories
    [InlineData(20, 20, 3, 0.5f, 0.5f, LikedFactsRules.FactShape.Label)]     // exactly at the cap → label
    [InlineData(20, 20, 3, 0.49f, 0.5f, LikedFactsRules.FactShape.Graph)]
    public void ShapeBoundaryTable(int known, int total, int cats, float top, float cap, LikedFactsRules.FactShape expected)
        => Assert.Equal(expected, LikedFactsRules.Shape(known, total, cats, top, cap));

    [Fact]
    public void YearsDominanceMeasuresHistogramBins()
    {
        // 1965–2024 → twelve five-year bins; 45 of 64 tracks in the last bin. Measured per YEAR this would look spread
        // (the single-year top share is small); measured per BIN — what the chart draws — it is dominated.
        var tracks = new List<Track>();
        int[] early = [1965, 1968, 1971, 1972, 1974, 1975, 1977, 1980, 1984, 1995, 1997, 2000, 2005, 2012, 2015, 2016, 2017, 2018, 2019];
        for (int i = 0; i < early.Length; i++) tracks.Add(T(null, "e" + i, year: early[i]));
        for (int i = 0; i < 45; i++) tracks.Add(T(null, "l" + i, year: 2020 + i % 5));
        var buckets = LikedFactsRules.YearHistogram(tracks);
        var d = LikedFactsRules.YearsDominance(buckets);
        Assert.Equal(64, d.Known);
        Assert.Equal(buckets.Count - 1, d.TopIndex);
        Assert.InRange(d.TopShare, 0.69f, 0.71f);
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.YearsShape(tracks, buckets));

        // 2010–2021, two per year: spread across twelve one-year bars → a real shape.
        var spread = Repeat(24, i => T(null, "s" + i, year: 2010 + i / 2));
        var sb = LikedFactsRules.YearHistogram(spread);
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.YearsShape(spread, sb));

        // 41 of 50 in one year (the screenshot) → label.
        var one = Repeat(50, i => T(null, "o" + i, year: i < 41 ? 2024 : 2012 + i % 7));
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.YearsShape(one, LikedFactsRules.YearHistogram(one)));
    }

    [Theory]
    [InlineData(89.9, TrackTempoBand.Under90)]
    [InlineData(90.0, TrackTempoBand.From90To119)]
    [InlineData(119.9, TrackTempoBand.From90To119)]
    [InlineData(120.0, TrackTempoBand.From120To139)]
    [InlineData(139.9, TrackTempoBand.From120To139)]
    [InlineData(140.0, TrackTempoBand.From140AndUp)]
    [InlineData(0.0, TrackTempoBand.Any)]
    public void TempoBandOfIsHalfOpen(double bpm, TrackTempoBand expected)
        => Assert.Equal(expected, TrackFilterModel.BandOf(bpm));

    [Fact]
    public void TempoBandCountsUseTheFilterBoundaries()
    {
        var tracks = new List<Track> { T(null, "a", bpm: 89.9), T(null, "b", bpm: 90), T(null, "c", bpm: 139.9), T(null, "d", bpm: 140), T(null, "e"), T(null, "f", bpm: 0) };
        var counts = new int[LikedFactsRules.TempoBandCount];
        int known = LikedFactsRules.TempoBandCounts(tracks, counts);
        Assert.Equal(4, known);
        Assert.Equal(new[] { 1, 1, 1, 1 }, counts);
        // The lens the pill applies filters exactly those rows.
        var filter = TrackFilterState.Default with { Tempo = TrackTempoBand.From120To139 };
        Assert.True(TrackFilterModel.Matches(tracks[2], "", filter, false, false, Now));
        Assert.False(TrackFilterModel.Matches(tracks[3], "", filter, false, false, Now));
    }

    [Fact]
    public void TempoShapeIsAbsentUnderCoverage()
    {
        // 22 of 40 (55 %) carry a tempo, spread over the bands → still absent: the fact cannot be claimed for the list.
        var thin = Repeat(40, i => T(null, "t" + i, bpm: i < 22 ? 80 + (i * 9) % 100 : null));
        Assert.Equal(LikedFactsRules.FactShape.Absent, LikedFactsRules.TempoShape(thin));
        // 30 of 40 (75 %) → eligible; spread across three bands, no band over the cap → graph.
        var ok = Repeat(40, i => T(null, "t" + i, bpm: i < 30 ? 80 + (i * 9) % 100 : null));
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.TempoShape(ok));
    }

    [Fact]
    public void HalfTimeSplitIsALabelAndBimodalIsAGraph()
    {
        // Spotify reports a fifth of a drum-and-bass list at half time: 32 @ 174, 8 @ 87 → 80 % in one band → a pill
        // that names "140 bpm and up", never "steady".
        var dnb = Repeat(40, i => T(null, "d" + i, bpm: i < 32 ? 174 : 87));
        var d = LikedFactsRules.TempoDominance(dnb);
        Assert.Equal(3, d.TopIndex);
        Assert.InRange(d.TopShare, 0.79f, 0.81f);
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.TempoShape(dnb));

        // A 50/50 house / rollers set is two humps — an IQR rule would call it "tight"; the band share says 50 % → graph.
        var split = Repeat(40, i => T(null, "s" + i, bpm: i < 20 ? 124 : 174));
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.TempoShape(split));
    }

    [Fact]
    public void TempoStatisticsUseTheLowerMedianAndSkipUnknown()
    {
        var tracks = new List<Track> { T(null, "a", bpm: 170), T(null, "b", bpm: 100), T(null, "c"), T(null, "d", bpm: 130), T(null, "e", bpm: 120, color: 0xFF56D9F8u) };
        var s = LikedFactsRules.TempoStatistics(tracks);
        Assert.Equal(4, s.Known);
        Assert.Equal(5, s.Total);
        Assert.Equal(120d, s.Median);      // lower middle of 100,120,130,170 — a real track's tempo
        Assert.Equal(100d, s.Min);
        Assert.Equal(170d, s.Max);

        var bpm = new float[4]; var argb = new uint[4];
        Assert.Equal(4, LikedFactsRules.TempoValues(tracks, bpm, argb));
        Assert.Equal(new[] { 170f, 100f, 130f, 120f }, bpm);        // list order, the unknown row skipped
        Assert.Equal(0xFF56D9F8u, argb[3]);
        Assert.Equal(0u, argb[0]);
    }

    [Fact]
    public void BlendShapeCollapsesDominantAndFlatBlends()
    {
        var kpop = Repeat(50, i => T(null, "k" + i, tags: [i < 49 ? "K-Pop" : "Pop"]));
        var d = LikedFactsRules.BlendsDominance(kpop);
        Assert.Equal("K-Pop", d.TopTitle);
        Assert.InRange(d.TopShare, 0.97f, 0.99f);
        Assert.False(d.Flat);
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.BlendShape(kpop));

        var flat = Repeat(60, i => T(null, "f" + i, tags: ["Style " + i % 15]));   // 15 styles × 4 tracks, none over 7 %
        var fd = LikedFactsRules.BlendsDominance(flat);
        Assert.True(fd.Flat);
        Assert.Equal(15, fd.Styles);
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.BlendShape(flat));

        var rock = Repeat(60, i => T(null, "r" + i, tags: [i < 40 ? "Classic Rock" : i < 52 ? "Hard Rock" : "Arena Rock"]));   // 67 %
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.BlendShape(rock));

        var thin = Repeat(4, i => T(null, "n" + i, tags: ["Tag " + i]));        // every descriptor under the floor
        Assert.Equal(LikedFactsRules.FactShape.Absent, LikedFactsRules.BlendShape(thin));
        Assert.Equal(LikedFactsRules.FactShape.Absent, LikedFactsRules.BlendShape(Repeat(5, i => T(null, "u" + i))));   // not fetched
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LikedFactsRules.FactShape.Absent)]   // "+0" — nothing to show
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, LikedFactsRules.FactShape.Label)]    // one lonely add
    [InlineData(new[] { 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0 }, LikedFactsRules.FactShape.Label)]    // one bucket, however tall
    [InlineData(new[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1 }, LikedFactsRules.FactShape.Label)]    // two adds < MinWeekEvidence
    [InlineData(new[] { 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 1 }, LikedFactsRules.FactShape.Graph)]    // 3 adds over 2 buckets
    public void WeekShapeTable(int[] counts, LikedFactsRules.FactShape expected)
    {
        var weeks = new LikedFactsRules.WeekBucket[counts.Length];
        for (int i = 0; i < counts.Length; i++) weeks[i] = new LikedFactsRules.WeekBucket(Now.AddDays(-7 * (counts.Length - i)), counts[i]);
        Assert.Equal(expected, LikedFactsRules.WeekShape(weeks));
    }

    [Fact]
    public void LatestStampIsTheNewestUsableStamp()
    {
        var tracks = new List<Track> { T(Now.AddDays(-40)), T(Now.AddDays(-3), "new"), T(null, "unstamped"), T(DateTimeOffset.UnixEpoch, "epoch") };
        Assert.Equal(Now.AddDays(-3), LikedFactsRules.LatestStamp(tracks));
        Assert.Null(LikedFactsRules.LatestStamp(new List<Track> { T(null), T(DateTimeOffset.UnixEpoch) }));
    }

    [Fact]
    public void TempoFingerprintIsContentNotIdentity()
    {
        var a = new List<Track> { T(null, "a", bpm: 128), T(null, "b", bpm: 96.4), T(null, "c", bpm: 172) };
        var b = new List<Track> { T(null, "c", bpm: 172), T(null, "a", bpm: 128), T(null, "b", bpm: 96.4), T(null, "d") };   // new instance, reordered, one unknown
        var c = new List<Track> { T(null, "a", bpm: 128), T(null, "b", bpm: 96.4), T(null, "c", bpm: 173) };                // one tempo changed
        Assert.Equal(LikedFactsRules.FingerprintTempo(a), LikedFactsRules.FingerprintTempo(b));
        Assert.NotEqual(LikedFactsRules.FingerprintTempo(a), LikedFactsRules.FingerprintTempo(c));
        Assert.Equal(3, LikedFactsRules.FingerprintTempo(a).Known);
    }

    [Fact]
    public void TempoStatisticsMatchTheSortedLowerMedian()
    {
        var rng = new Random(7);
        var tracks = Repeat(101, i => T(null, "t" + i, bpm: 60 + rng.Next(0, 140)));
        var sorted = new List<double>();
        foreach (var t in tracks) sorted.Add(t.TempoBpm!.Value);
        sorted.Sort();
        var s = LikedFactsRules.TempoStatistics(tracks);
        Assert.Equal(sorted[(sorted.Count - 1) / 2], s.Median);
        Assert.Equal(sorted[0], s.Min);
        Assert.Equal(sorted[^1], s.Max);
        Assert.Equal(101, s.Known);
    }

    [Fact]
    public void SummarizeAgreesWithTheStandaloneRules()
    {
        var tracks = Repeat(60, i => T(null, "t" + i, year: 2010 + i % 12, bpm: i % 3 == 0 ? 96 : i % 3 == 1 ? 128 : 172,
                                        tags: [i % 5 == 0 ? "Dance" : i % 5 == 1 ? "Pop" : "House"],
                                        artists: [A("Artist " + i % 7)]));
        var s = LikedFactsRules.Summarize(tracks);
        Assert.Equal(LikedFactsRules.YearHistogram(tracks), s.YearBuckets);
        Assert.Equal(LikedFactsRules.YearsShape(tracks, s.YearBuckets), s.YearsShape);
        Assert.Equal(LikedFactsRules.TempoShape(tracks), s.Tempo.Shape);
        Assert.Equal(LikedFactsRules.TempoStatistics(tracks), s.Tempo.Stats);
        Assert.Equal(LikedFactsRules.BlendShares(tracks), s.BlendShares);
        Assert.Equal(LikedFactsRules.BlendsDominance(tracks), s.BlendDominance);
        Assert.Equal(LikedFactsRules.BlendShape(tracks), s.BlendShape);
        Assert.Equal(LikedFactsRules.TopArtists(tracks, 40).Count, s.Artists.Count);
        Assert.True(LikedFactsRules.AnyArtistCredit(tracks));
        Assert.False(LikedFactsRules.AnyArtistCredit(Repeat(3, i => T(null, "n" + i))));
        var counts = new int[4];
        LikedFactsRules.TempoBandCounts(tracks, counts);
        Assert.Equal(counts, new[] { s.Tempo.Under90, s.Tempo.From90To119, s.Tempo.From120To139, s.Tempo.From140AndUp });
    }

    [Fact]
    public void LatchOnlyUpgrades()
    {
        var a = LikedFactsRules.FactShape.Absent; var l = LikedFactsRules.FactShape.Label; var g = LikedFactsRules.FactShape.Graph;
        Assert.Equal(l, LikedFactsRules.Latch(a, l));
        Assert.Equal(g, LikedFactsRules.Latch(l, g));
        Assert.Equal(g, LikedFactsRules.Latch(g, l));      // a straggler cannot fold a card back into a pill
        Assert.Equal(l, LikedFactsRules.Latch(l, a));
        Assert.Equal(a, LikedFactsRules.Latch(a, a));
    }

    [Fact]
    public void TracksEquivalentIsRowEqualityNotListIdentity()
    {
        var row = T(null, "a", bpm: 128);
        var a = new List<Track> { row, T(null, "b", year: 2020) };
        var sameRows = new List<Track> { row, T(null, "b", year: 2020) };          // new list, a new-but-equal record for row 2
        var changed = new List<Track> { row, T(null, "b", year: 2020, bpm: 96) };  // a tempo landed
        var shorter = new List<Track> { row };
        Assert.True(LikedFactsRules.TracksEquivalent(a, a));
        Assert.True(LikedFactsRules.TracksEquivalent(a, sameRows));
        Assert.False(LikedFactsRules.TracksEquivalent(a, changed));
        Assert.False(LikedFactsRules.TracksEquivalent(a, shorter));
        Assert.False(LikedFactsRules.TracksEquivalent(a, null));
        Assert.True(LikedFactsRules.TracksEquivalent(null, null));
    }

    [Fact]
    public void TempoLensRoundTrips()
    {
        var filter = TrackFilterState.Default with { Tempo = TrackTempoBand.From120To139 };
        Assert.Equal(LikedFactsRules.LikedLens.Tempo, LikedFactsRules.ActiveLenses(filter));
        Assert.True(LikedFactsRules.IsTempoLens(filter, TrackTempoBand.From120To139));
        Assert.False(LikedFactsRules.IsTempoLens(filter, TrackTempoBand.Under90));
        Assert.False(LikedFactsRules.IsTempoLens(TrackFilterState.Default, TrackTempoBand.Any));
        Assert.Equal(1, filter.ActiveCount);
        var cleared = LikedFactsRules.ClearLens(filter, LikedFactsRules.LikedLens.Tempo);
        Assert.Equal(TrackTempoBand.Any, cleared.Tempo);
        Assert.Equal(LikedFactsRules.LikedLens.None, LikedFactsRules.ActiveLenses(cleared));
    }
}
