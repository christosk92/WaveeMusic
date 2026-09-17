// ── Wavee.Tests/LikedFactsRulesTests.cs — the Liked Songs rail facts (Entities/User.Facts.cs) ─────────────────────────
//
// A VERBATIM port of 0.2.9's LikedFactsRulesTests (1,191 lines, ~70 facts). The decisions are unchanged; the call shape
// is 0.3's, exactly as `LikedFactsRules` changed it:
//   · a row is `LikedRow(Track, AddedAtUnixSec)`, built here through a real `TestScope` commit (the rules read the
//     handle's columns and edges), so a stamp is whole SECONDS — the two "one tick" rungs are one second;
//   · a credit is an artist SLOT: 0.2.9's uri → id → name `ArtistKey` ladder has no 0.3 shape (every credit is a row),
//     its surviving half — the lens is recognised by the same key it was set from — is kept, plus the slot identity;
//   · "not fetched" is `!Knows(Tags)` / `!Knows(Audio)` (0.2.9's nulls); the list inputs are spans, so a null list is an
//     empty span;
//   · `TracksEquivalent` compares row identity (track + stamp): a tempo landing is a version bump, not a new row.
// Every function still takes its clock as a parameter, which is what makes the week bucketing pinnable at all.

using System.Text;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LikedFactsRulesTests
{
    static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
    static long NowSec => Now.ToUnixTimeSeconds();

    int _serial;

    public LikedFactsRulesTests() => TestScope.Fresh();

    static int Sec(DateTimeOffset? at)
        => at is { } v ? (int)Math.Clamp(v.ToUnixTimeSeconds(), int.MinValue, int.MaxValue) : 0;

    /// <summary>0.2.9's <c>T(...)</c>: one committed track row (a fresh uri per call, titled with <paramref name="id"/>),
    /// its credits and descriptors as edge runs, and the membership stamp on the row.</summary>
    LikedRow T(DateTimeOffset? addedAt, string id = "t", string[]? artists = null, string[]? tags = null, int year = 0,
               double? bpm = null, uint? color = null)
    {
        string uri = "spotify:track:" + id + "-" + (++_serial).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text(uri);
        row.Title = s.Text(id);
        row.DurationMs = 180_000;
        row.Year = (ushort)Math.Max(0, year);
        uint known = (uint)(TrackFields.Identity | TrackFields.Year);
        if (bpm is not null || color is not null)
        {
            row.Tempo = (ushort)Math.Round((bpm ?? 0d) * 10d);
            row.CamelotColor = color ?? 0u;
            known |= (uint)TrackFields.Audio;
        }
        if (tags is not null) known |= (uint)TrackFields.Tags;
        row.Known = known;
        row.Authority = Authority.Full;
        if (artists is not null)
            foreach (var name in artists)
            {
                ref var a = ref s.Artists.Add();
                a.Id = s.Text("spotify:artist:" + name);
                a.Name = s.Text(name);
                a.Known = (uint)ArtistFields.Identity;
                a.Authority = Authority.Full;
            }
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse(uri));
        if (artists is not null)
        {
            var slots = new int[artists.Length];
            for (int i = 0; i < slots.Length; i++) slots[i] = Entities.Artist(EntityUri.Parse("spotify:artist:" + artists[i])).Slot;
            Entities.Current.Edges.TrackArtists.ReplaceRun(track.Slot, slots, default);
        }
        if (tags is not null)
        {
            var ids = new StringId[tags.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = Entities.Intern(Encoding.UTF8.GetBytes(tags[i]));
            Entities.Current.Edges.TrackTags.Replace(track.Slot, new int[tags.Length], ids, EdgeState.Complete, tags.Length);
        }
        return new LikedRow(track, Sec(addedAt));
    }

    LikedRow[] Repeat(int n, Func<int, LikedRow> make)
    {
        var list = new LikedRow[n];
        for (int i = 0; i < n; i++) list[i] = make(i);
        return list;
    }

    static Artist ArtistNamed(string name) => Entities.Artist(EntityUri.Parse("spotify:artist:" + name));

    /// <summary>The bucket (oldest-first) a single like lands in, or -1 when it is excluded. Asserts exactly one bucket
    /// claims it.</summary>
    int SoleBucket(DateTimeOffset? addedAt, DateTimeOffset now, int weeks = 12)
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

    static Track.FilterRow RowOf(LikedRow row, long now) => Track.FilterRow.Of(row.Track, row.AddedAtUnixSec, saved: false, now);

    // ── LikesPerWeek: the rolling sparkline ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWindowIsTwelveContiguousSevenDayBucketsOldestFirst()
    {
        var buckets = LikedFactsRules.LikesPerWeek(Array.Empty<LikedRow>(), Now);

        Assert.Equal(12, buckets.Count);
        Assert.Equal(Now - TimeSpan.FromDays(84), buckets[0].WindowStart);
        Assert.Equal(Now - TimeSpan.FromDays(7), buckets[11].WindowStart);
        for (int i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(TimeSpan.FromDays(7), buckets[i].WindowStart - buckets[i - 1].WindowStart);
            Assert.Equal(0, buckets[i].Count);
        }
    }

    [Theory]
    [InlineData(0.0, 11)]
    [InlineData(0.5, 11)]
    [InlineData(6.99, 11)]
    [InlineData(7.0, 10)]
    [InlineData(13.99, 10)]
    [InlineData(14.0, 9)]
    [InlineData(83.99, 0)]
    [InlineData(84.0, -1)]
    [InlineData(400.0, -1)]
    public void LikesLandInTheirRollingWeek(double daysAgo, int expectedBucket)
        => Assert.Equal(expectedBucket, SoleBucket(Now - TimeSpan.FromDays(daysAgo), Now));

    /// <summary>The rung is exact to the resolution a stamp has — one second in 0.3 (0.2.9: one tick).</summary>
    [Fact]
    public void TheWeekRungIsExact()
    {
        var oneWeek = TimeSpan.FromDays(7);
        Assert.Equal(11, SoleBucket(Now - oneWeek + TimeSpan.FromSeconds(1), Now));
        Assert.Equal(10, SoleBucket(Now - oneWeek, Now));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(45.0)]
    [InlineData(4000.0)]
    public void FutureStampsClampIntoTheNewestBucket(double daysAhead)
        => Assert.Equal(11, SoleBucket(Now + TimeSpan.FromDays(daysAhead), Now));

    [Fact]
    public void DaylightSavingDoesNotMoveTheBuckets()
    {
        var now = new DateTimeOffset(2025, 11, 2, 12, 0, 0, TimeSpan.FromHours(1));
        var buckets = LikedFactsRules.LikesPerWeek(
        [
            T(now - TimeSpan.FromDays(3)),
            T(now - TimeSpan.FromDays(9)),
            T(now - TimeSpan.FromDays(7)),
        ], now);

        for (int i = 1; i < buckets.Count; i++)
            Assert.Equal(TimeSpan.FromDays(7), buckets[i].WindowStart - buckets[i - 1].WindowStart);
        Assert.Equal(1, buckets[11].Count);
        Assert.Equal(2, buckets[10].Count);
    }

    [Fact]
    public void AYearBoundaryDoesNotMoveTheBuckets()
    {
        var now = new DateTimeOffset(2026, 1, 3, 9, 30, 0, TimeSpan.Zero);
        var buckets = LikedFactsRules.LikesPerWeek(
        [
            T(new DateTimeOffset(2025, 12, 30, 9, 30, 0, TimeSpan.Zero)),
            T(new DateTimeOffset(2025, 12, 20, 9, 30, 0, TimeSpan.Zero)),
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
        (DateTimeOffset?)null,
        DateTimeOffset.UnixEpoch,
        default(DateTimeOffset),
        DateTimeOffset.UnixEpoch - TimeSpan.FromDays(1),
        DateTimeOffset.UnixEpoch - TimeSpan.FromDays(3650),
    };

    [Theory]
    [MemberData(nameof(UnusableStamps))]
    public void AnUndatableLikeIsExcludedFromTimeFacts(DateTimeOffset? addedAt)
    {
        LikedRow[] rows = [T(addedAt, "x", ["Aphex"], ["Ambient"])];

        Assert.Equal(-1, SoleBucket(addedAt, Now));
        Assert.Empty(LikedFactsRules.LikedInWindow(rows, Now - TimeSpan.FromDays(4000), Now + TimeSpan.FromDays(1)));
        Assert.Null(LikedFactsRules.LikingSince(rows));
        Assert.Null(LikedFactsRules.OldestLike(rows));
        Assert.Null(LikedFactsRules.DominantDecade(rows));
        Assert.False(LikedFactsRules.TryStamp(rows[0], out _));
        Assert.False(LikedFactsRules.StampsSpread(rows));

        var top = LikedFactsRules.TopArtists(rows);
        Assert.Single(top);
        Assert.Equal("Aphex", top[0].Artist.Name);
        Assert.Empty(LikedFactsRules.BlendShares(rows));
    }

    /// <summary>The epoch floor is a floor, not a year filter: a like saved one second after the epoch is still a like.</summary>
    [Fact]
    public void OnlyTheEpochSentinelItselfIsRejected()
    {
        var justAfter = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(1);
        Assert.True(LikedFactsRules.TryStamp(T(justAfter), out var at));
        Assert.Equal(justAfter, at);
    }

    [Fact]
    public void UndatableLikesDoNotSuppressTheDatableOnes()
    {
        LikedRow[] rows =
        [
            T(null, "a", ["Aphex"], ["Ambient"]),
            T(Now - TimeSpan.FromDays(2), "b", ["Boards"], ["Ambient"]),
            T(DateTimeOffset.UnixEpoch, "c", ["Clark"], ["Ambient"]),
        ];

        Assert.Equal("b", LikedFactsRules.OldestLike(rows)!.Value.Track.Title);
        var top = LikedFactsRules.TopArtists(rows);
        Assert.Equal(3, top.Count);
        Assert.Equal(["Aphex", "Boards", "Clark"], Names(top));
    }

    // ── This week, last year ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-03-15T12:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("2024-02-29T18:45:00Z")]
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

    [Fact]
    public void TheWindowIsHalfOpenAndKeepsInputOrder()
    {
        var (start, end) = LikedFactsRules.ThisWeekLastYearWindow(Now);
        LikedRow[] rows =
        [
            T(start - TimeSpan.FromSeconds(1), "before"),
            T(start, "atStart"),
            T(start + TimeSpan.FromDays(3), "inside"),
            T(end - TimeSpan.FromSeconds(1), "justInside"),
            T(end, "atEnd"),
        ];

        var hits = LikedFactsRules.LikedInWindow(rows, start, end);
        Assert.Equal(["atStart", "inside", "justInside"], Ids(hits));
    }

    [Fact]
    public void AnEmptyOrInvertedWindowSelectsNothing()
    {
        LikedRow[] rows = [T(Now)];
        Assert.Empty(LikedFactsRules.LikedInWindow(rows, Now, Now));
        Assert.Empty(LikedFactsRules.LikedInWindow(rows, Now, Now - TimeSpan.FromDays(1)));
        Assert.Empty(LikedFactsRules.LikedInWindow(Array.Empty<LikedRow>(), Now - TimeSpan.FromDays(1), Now));
    }

    static string[] Ids(IReadOnlyList<LikedRow> rows)
    {
        var ids = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) ids[i] = rows[i].Track.Title;
        return ids;
    }

    // ── Most liked artists ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryCreditCountsNotJustTheFirst()
    {
        LikedRow[] rows =
        [
            T(Now, "1", ["Solo", "Guest"]),
            T(Now, "2", ["Solo"]),
            T(Now, "3", ["Guest"]),
            T(Now, "4", ["Guest"]),
        ];

        var top = LikedFactsRules.TopArtists(rows);
        Assert.Equal(["Guest", "Solo"], Names(top));
        Assert.Equal(3, top[0].Count);
        Assert.Equal(2, top[1].Count);
    }

    [Fact]
    public void TiesAreBrokenByNameSoThePileNeverTwitches()
    {
        LikedRow[] rows = [T(Now, "1", ["Zeta", "Alpha", "Mid"])];
        Assert.Equal(["Alpha", "Mid", "Zeta"], Names(LikedFactsRules.TopArtists(rows)));
    }

    [Fact]
    public void TopArtistsIsCappedAndSurvivesEmptyInput()
    {
        var rows = Repeat(20, i => T(Now, "t" + i, ["A" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)]));
        Assert.Equal(5, LikedFactsRules.TopArtists(rows).Count);
        Assert.Equal(3, LikedFactsRules.TopArtists(rows, 3).Count);
        Assert.Empty(LikedFactsRules.TopArtists(rows, 0));
        Assert.Empty(LikedFactsRules.TopArtists(Array.Empty<LikedRow>()));
        Assert.Empty(LikedFactsRules.TopArtists([T(Now, "x")]));
    }

    static string[] Names(IReadOnlyList<LikedFactsRules.ArtistCount> counts)
    {
        var names = new string[counts.Count];
        for (int i = 0; i < counts.Count; i++) names[i] = counts[i].Artist.Name;
        return names;
    }

    // ── Your blend ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyThePrimaryTagContributesSoTheBarPartitions()
    {
        var rows = new List<LikedRow>();
        rows.AddRange(Repeat(5, i => T(Now, "p" + i, tags: ["Pop", "Chill"])));
        rows.AddRange(Repeat(4, i => T(Now, "c" + i, tags: ["Chill"])));
        rows.AddRange(Repeat(3, i => T(Now, "j" + i, tags: ["Jazz"])));

        var shares = LikedFactsRules.BlendShares(rows.ToArray());
        Assert.Equal(["Pop", "Chill", "Jazz"], Titles(shares));
        Assert.Equal([5, 4, 3], Counts(shares));
        Assert.InRange(Sum(shares), 0.9999f, 1.0001f);
        Assert.InRange(shares[0].Fraction, 5f / 12f - 0.0001f, 5f / 12f + 0.0001f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(50)]
    public void SlicesNeverSumPastOneBar(int take)
    {
        var rows = new List<LikedRow>();
        for (int t = 0; t < 8; t++)
        {
            int tag = t;
            rows.AddRange(Repeat(3 + t, i => T(Now, $"t{tag}_{i}", tags: ["tag" + tag])));
        }
        rows.AddRange(Repeat(4, i => T(Now, "untagged" + i)));

        var shares = LikedFactsRules.BlendShares(rows.ToArray(), take);
        Assert.Equal(Math.Min(take, 8), shares.Count);
        Assert.InRange(Sum(shares), 0f, 1.0001f);
        foreach (var s in shares) Assert.InRange(s.Fraction, 0f, 1.0001f);
    }

    [Fact]
    public void BelowTheEvidenceFloorThereIsNoBlend()
    {
        Assert.Equal(3, ContentFilterTags.MinTrackCount);

        var justUnder = Repeat(ContentFilterTags.MinTrackCount - 1, i => T(Now, "t" + i, tags: ["Ambient"]));
        Assert.Empty(LikedFactsRules.BlendShares(justUnder));

        var atTheFloor = Repeat(ContentFilterTags.MinTrackCount, i => T(Now, "u" + i, tags: ["Ambient"]));
        var shares = LikedFactsRules.BlendShares(atTheFloor);
        Assert.Single(shares);
        Assert.Equal("Ambient", shares[0].Title);
        Assert.InRange(shares[0].Fraction, 0.9999f, 1.0001f);
    }

    /// <summary>Not fetched (no Tags bit — 0.2.9's null) and genuinely none (a known empty run) are both "no blend".</summary>
    [Fact]
    public void UnenrichedAndUntaggedLikesAreNotABlend()
    {
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "t" + i))));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "e" + i, tags: Array.Empty<string>()))));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "w" + i, tags: ["  "]))));
        Assert.Empty(LikedFactsRules.BlendShares(Array.Empty<LikedRow>()));
        Assert.Empty(LikedFactsRules.BlendShares(Repeat(20, i => T(Now, "p" + i, tags: ["Pop"])), 0));
    }

    [Fact]
    public void CasingVariantsCollapseToOneSlice()
    {
        LikedRow[] rows = [T(Now, "a", tags: ["K-Pop"]), T(Now, "b", tags: ["k-pop"]), T(Now, "c", tags: ["K-POP"])];

        var shares = LikedFactsRules.BlendShares(rows);
        Assert.Single(shares);
        Assert.Equal(3, shares[0].Count);
    }

    // ── Your blend: what "Other" pools ──────────────────────────────────────────────────────────────────────────────

    LikedRow[] BlendLibrary()
    {
        var rows = new List<LikedRow>();
        for (int t = 0; t < 8; t++)
        {
            int tag = t;
            rows.AddRange(Repeat(3 + t, i => T(Now, $"t{tag}_{i}", tags: ["tag" + tag])));
        }
        rows.AddRange(Repeat(2, i => T(Now, "rare" + i, tags: ["Rare"])));
        rows.Add(T(Now, "rarest", tags: ["Rarest"]));
        rows.AddRange(Repeat(4, i => T(Now, "untagged" + i)));
        return rows.ToArray();
    }

    [Fact]
    public void TheOtherTailContinuesTheSameRankingAndDenominator()
    {
        var rows = BlendLibrary();
        var bar = LikedFactsRules.BlendShares(rows, 5);
        var tail = LikedFactsRules.BlendOther(rows, 5, 3);

        Assert.Equal(["tag7", "tag6", "tag5", "tag4", "tag3"], Titles(bar));
        Assert.Equal(["tag2", "tag1", "tag0"], Titles(tail.Named));
        Assert.Equal([5, 4, 3], Counts(tail.Named));

        Assert.Equal(15, tail.Count);
        Assert.InRange(tail.Fraction, 15f / 55f - 0.0001f, 15f / 55f + 0.0001f);
        Assert.InRange(tail.Named[0].Fraction, 5f / 55f - 0.0001f, 5f / 55f + 0.0001f);
        Assert.InRange(bar[0].Fraction, 10f / 55f - 0.0001f, 10f / 55f + 0.0001f);
        Assert.InRange(Sum(bar) + tail.Fraction, 0.9999f, 1.0001f);
    }

    [Theory]
    [InlineData(5, 3, 3, 2)]
    [InlineData(5, 0, 0, 5)]
    [InlineData(0, 2, 2, 8)]
    [InlineData(1, 3, 3, 6)]
    public void TheMoreCountCoversEveryDescriptorNothingNames(int shown, int detail, int named, int more)
    {
        var tail = LikedFactsRules.BlendOther(BlendLibrary(), shown, detail);
        Assert.Equal(named, tail.Named.Count);
        Assert.Equal(more, tail.MoreTags);
    }

    [Fact]
    public void AnUnboundedTailNamesEveryRemainingDescriptor()
    {
        var rows = BlendLibrary();
        var bar = LikedFactsRules.BlendShares(rows, 5);
        var tail = LikedFactsRules.BlendOther(rows, bar.Count, int.MaxValue);

        Assert.Equal(["tag2", "tag1", "tag0", "Rare", "Rarest"], Titles(tail.Named));
        Assert.Equal([5, 4, 3, 2, 1], Counts(tail.Named));
        Assert.Equal(0, tail.MoreTags);

        int summed = 0;
        for (int i = 0; i < tail.Named.Count; i++) summed += tail.Named[i].Count;
        Assert.Equal(tail.Count, summed);

        Assert.DoesNotContain("Rare", Titles(LikedFactsRules.BlendShares(rows, 50)));
        Assert.DoesNotContain("Rarest", Titles(LikedFactsRules.BlendShares(rows, 50)));
        Assert.Equal(8, LikedFactsRules.BlendShares(rows, 50).Count);
    }

    [Fact]
    public void TailSplitNamesAtOrAboveTheFloorAndCountsTheRest()
    {
        IReadOnlyList<LikedFactsRules.TagShare> tail =
        [
            new("EDM", 9, 0.07f), new("R&B", 8, 0.02f), new("Chill", 3, 0.01f),
            new("Trap", 2, 0.009f), new("Ska", 1, 0.004f),
        ];

        var (named, under) = LikedFactsRules.TailSplit(tail);
        Assert.Equal(["EDM", "R&B", "Chill"], Titles(named));
        Assert.Equal(2, under);

        var (fewer, moreUnder) = LikedFactsRules.TailSplit(tail, 0.05f);
        Assert.Equal(["EDM"], Titles(fewer));
        Assert.Equal(4, moreUnder);

        var (all, none) = LikedFactsRules.TailSplit(tail, 0f);
        Assert.Same(tail, all);
        Assert.Equal(0, none);
    }

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

    [Fact]
    public void AFullyNamedBarHasNoTail()
    {
        var rows = new List<LikedRow>();
        rows.AddRange(Repeat(6, i => T(Now, "a" + i, tags: ["Pop"])));
        rows.AddRange(Repeat(4, i => T(Now, "b" + i, tags: ["Jazz"])));
        rows.AddRange(Repeat(3, i => T(Now, "c" + i)));
        var array = rows.ToArray();

        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(array, 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(Array.Empty<LikedRow>(), 5, 3));
        // The 0.2.0.1 crash: the card dereferenced `Named` on the default answer. A default tail is EMPTY, never null.
        Assert.Empty(default(LikedFactsRules.BlendTail).Named);
        Assert.Empty(LikedFactsRules.BlendOther(array, 5, 3).Named);
        Assert.Empty(LikedFactsRules.BlendOther(Array.Empty<LikedRow>(), 5, 3).Named);
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(default, 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(Repeat(20, i => T(Now, "t" + i)), 5, 3));
        Assert.Equal(default(LikedFactsRules.BlendTail), LikedFactsRules.BlendOther(array, -1, 3));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(55)]
    [InlineData(3)]
    [InlineData(9999)]
    public void TheTaggedTotalIsRecoveredFromAnySlice(int tagged)
    {
        var rows = new List<LikedRow>();
        int rest = tagged;
        for (int t = 0; t < 3 && rest > 4; t++, rest -= 4)
        {
            int tag = t;
            rows.AddRange(Repeat(4, i => T(Now, $"t{tag}_{i}", tags: ["tag" + tag])));
        }
        // One committed "ZZ" track carries the tail (the partition counts ROWS): 9,999 commits would test the store.
        var zz = T(Now, "zz", tags: ["ZZ"]);
        rows.AddRange(Repeat(rest, _ => zz));

        var shares = LikedFactsRules.BlendShares(rows.ToArray(), 50);
        Assert.Equal(tagged, LikedFactsRules.TaggedTotal(shares));
    }

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
        LikedRow[] rows = [T(Now, "new"), T(DateTimeOffset.UnixEpoch, "bogus"), T(oldest, "oldest"), T(null, "unstamped")];

        Assert.Equal(oldest, LikedFactsRules.LikingSince(rows));
        Assert.Equal("oldest", LikedFactsRules.OldestLike(rows)!.Value.Track.Title);
    }

    [Fact]
    public void NothingDatableMeansNoSinceLine()
    {
        Assert.Null(LikedFactsRules.LikingSince(Array.Empty<LikedRow>()));
        Assert.Null(LikedFactsRules.OldestLike(Array.Empty<LikedRow>()));
        Assert.Null(LikedFactsRules.LikingSince(default));
        Assert.Null(LikedFactsRules.OldestLike(default));
    }

    // ── DominantDecade ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDominantDecadeIsTheModeOfTheSaveDates()
    {
        var rows = new List<LikedRow>();
        rows.AddRange(Repeat(6, i => T(new DateTimeOffset(2021, 5, 1, 0, 0, 0, TimeSpan.Zero), "a" + i)));
        rows.AddRange(Repeat(4, i => T(new DateTimeOffset(2015, 5, 1, 0, 0, 0, TimeSpan.Zero), "b" + i)));

        Assert.Equal(2020, LikedFactsRules.DominantDecade(rows.ToArray()));
    }

    [Fact]
    public void ATieGoesToTheMoreRecentDecade()
    {
        var rows = new List<LikedRow>();
        rows.AddRange(Repeat(5, i => T(new DateTimeOffset(2016, 5, 1, 0, 0, 0, TimeSpan.Zero), "a" + i)));
        rows.AddRange(Repeat(5, i => T(new DateTimeOffset(2022, 5, 1, 0, 0, 0, TimeSpan.Zero), "b" + i)));

        Assert.Equal(2020, LikedFactsRules.DominantDecade(rows.ToArray()));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(40, true)]
    public void TheDecadeNeedsEnoughStampedLikes(int stamped, bool answered)
    {
        Assert.Equal(10, LikedFactsRules.MinDecadeEvidence);

        var rows = Repeat(stamped, i => T(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), "t" + i));
        Assert.Equal(answered ? 2020 : (int?)null, LikedFactsRules.DominantDecade(rows));
    }

    [Fact]
    public void UndatableLikesDoNotCountTowardTheDecadeFloor()
        => Assert.Null(LikedFactsRules.DominantDecade(Repeat(20, i => T(null, "t" + i))));

    // ── The facts as LENSES over the track list ────────────────────────────────────────────────────────────────────

    /// <summary>THE invariant behind the sparkline lens: clicking bar k returns exactly the likes bar k counted — asserted
    /// against the real filter predicate over the real row.</summary>
    [Fact]
    public void EachBarsWindowSelectsExactlyTheLikesThatBarCounted()
    {
        var rows = Repeat(28, i => T(Now.AddHours(-6 * i), "t" + i));
        var buckets = LikedFactsRules.LikesPerWeek(rows, Now);

        int selectedTotal = 0;
        for (int b = 0; b < buckets.Count; b++)
        {
            var (after, before) = LikedFactsRules.WeekWindowMs(buckets[b]);
            var lens = Track.FilterState.Default.WithAddedWindow(after, before);

            int selected = 0;
            foreach (var row in rows)
                if (Track.FilterModel.Matches(RowOf(row, NowSec), "", lens, NowSec)) selected++;

            Assert.Equal(buckets[b].Count, selected);
            selectedTotal += selected;
        }
        Assert.Equal(28, selectedTotal);
    }

    [Fact]
    public void ConsecutiveBarWindowsAbutExactly()
    {
        var buckets = LikedFactsRules.LikesPerWeek(Array.Empty<LikedRow>(), Now);
        for (int i = 1; i < buckets.Count; i++)
        {
            var (_, prevEnd) = LikedFactsRules.WeekWindowMs(buckets[i - 1]);
            var (nextStart, _) = LikedFactsRules.WeekWindowMs(buckets[i]);
            Assert.Equal(prevEnd, nextStart);
        }
    }

    [Fact]
    public void OnlyTheLensedBarReadsAsLit()
    {
        var buckets = LikedFactsRules.LikesPerWeek(Array.Empty<LikedRow>(), Now);
        var (after, before) = LikedFactsRules.WeekWindowMs(buckets[4]);
        var lens = Track.FilterState.Default.WithAddedWindow(after, before);

        for (int i = 0; i < buckets.Count; i++)
            Assert.Equal(i == 4, LikedFactsRules.IsWeekLens(lens, buckets[i]));

        var later = LikedFactsRules.LikesPerWeek(Array.Empty<LikedRow>(), Now.AddHours(1));
        for (int i = 0; i < later.Count; i++) Assert.False(LikedFactsRules.IsWeekLens(lens, later[i]));
    }

    [Fact]
    public void ABarsWindowIsStableAcrossRendersWithinTheHour()
    {
        var early = LikedFactsRules.BucketClock(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));
        var late = LikedFactsRules.BucketClock(new DateTimeOffset(2026, 3, 15, 12, 59, 59, TimeSpan.Zero));
        Assert.Equal(early, late);

        var drawn = LikedFactsRules.LikesPerWeek(Array.Empty<LikedRow>(), early);
        var redrawn = LikedFactsRules.LikesPerWeek(Array.Empty<LikedRow>(), late);
        var (after, before) = LikedFactsRules.WeekWindowMs(drawn[4]);
        var lens = Track.FilterState.Default.WithAddedWindow(after, before);

        Assert.True(LikedFactsRules.IsWeekLens(lens, redrawn[4]));
    }

    [Fact]
    public void ALikeSavedSinceTheFlooredHourStillLandsInTheNewestBar()
    {
        var wall = new DateTimeOffset(2026, 3, 15, 12, 40, 0, TimeSpan.Zero);
        var buckets = LikedFactsRules.LikesPerWeek([T(wall)], LikedFactsRules.BucketClock(wall));

        Assert.Equal(1, buckets[buckets.Count - 1].Count);
    }

    /// <summary>0.3: the artist identity is the SLOT — 0.2.9's uri → id → name ladder has no shape here (every credit is
    /// a row), and two credits sharing a display name are still two lenses.</summary>
    [Fact]
    public void ArtistKeyIsTheSlotAndNothingWithoutARow()
    {
        var a = ArtistNamed("same-name-1");
        var b = ArtistNamed("same-name-2");
        Assert.Equal(a.Slot, LikedFactsRules.ArtistKey(a));
        Assert.NotEqual(LikedFactsRules.ArtistKey(a), LikedFactsRules.ArtistKey(b));
        Assert.Equal(0, LikedFactsRules.ArtistKey(default));
    }

    [Fact]
    public void TheArtistLensIsRecognisedByTheSameKeyItWasSetFrom()
    {
        T(Now, "v", ["vaultboy", "Henry Moodie"]);
        var artist = ArtistNamed("vaultboy");
        var lens = Track.FilterState.Default.WithArtist(LikedFactsRules.ArtistKey(artist), artist.Name);

        Assert.True(LikedFactsRules.IsArtistLens(lens, artist));
        Assert.False(LikedFactsRules.IsArtistLens(lens, ArtistNamed("Henry Moodie")));
        Assert.False(LikedFactsRules.IsArtistLens(Track.FilterState.Default, artist));
    }

    [Theory]
    [InlineData("K-Pop", "K-Pop", true)]
    [InlineData("k-pop", "K-Pop", true)]
    [InlineData("Pop", "K-Pop", false)]
    [InlineData("K-Pop", "", false)]
    public void TheTagLensMatchesTheChipsCaseInsensitively(string active, string title, bool expected)
        => Assert.Equal(expected, LikedFactsRules.IsTagLens(Track.FilterState.Default with { Tag = active }, title));

    [Fact]
    public void ActiveLensesReportsEveryRailFacetThatIsOn()
    {
        Assert.Equal(LikedFactsRules.LikedLens.None, LikedFactsRules.ActiveLenses(Track.FilterState.Default));

        var all = Track.FilterState.Default.WithAddedWindow(1_000L, 2_000L).WithArtist(7, "vaultboy") with { Tag = "Pop" };

        Assert.Equal(LikedFactsRules.LikedLens.Week | LikedFactsRules.LikedLens.Artist | LikedFactsRules.LikedLens.Tag,
                     LikedFactsRules.ActiveLenses(all));

        Assert.Equal(LikedFactsRules.LikedLens.None,
                     LikedFactsRules.ActiveLenses(Track.FilterState.Default with { Duration = Track.DurationRange.OverFiveMinutes }));
    }

    [Fact]
    public void ClearingOneLensLeavesTheOthersStanding()
    {
        var all = Track.FilterState.Default.WithAddedWindow(1_000L, 2_000L).WithArtist(7, "vaultboy")
            with { Tag = "Pop", Duration = Track.DurationRange.OverFiveMinutes };

        var noWeek = LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Week);
        Assert.Equal(0L, noWeek.AddedAfterMs);
        Assert.Equal(7, noWeek.ArtistSlot);
        Assert.Equal("Pop", noWeek.Tag);
        Assert.Equal(Track.DurationRange.OverFiveMinutes, noWeek.Duration);

        var noArtist = LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Artist);
        Assert.Equal(0, noArtist.ArtistSlot);
        Assert.Null(noArtist.ArtistName);
        Assert.Equal(1_000L, noArtist.AddedAfterMs);

        Assert.Null(LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.Tag).Tag);
        Assert.Equal(0, LikedFactsRules.ClearLens(all.WithReleaseYear(2010, 2014), LikedFactsRules.LikedLens.Year).ReleaseYearMin);
        Assert.Equal(all, LikedFactsRules.ClearLens(all, LikedFactsRules.LikedLens.None));
    }

    [Fact]
    public void UnstampedTracksStillRankInArtistsAndBlend()
    {
        var rows = Repeat(ContentFilterTags.MinTrackCount, i => T(null, "t" + i, ["Aphex"], ["Ambient"]));
        var top = LikedFactsRules.TopArtists(rows);
        Assert.Single(top);
        Assert.Equal("Aphex", top[0].Artist.Name);
        Assert.Equal(ContentFilterTags.MinTrackCount, top[0].Count);

        var shares = LikedFactsRules.BlendShares(rows);
        Assert.Single(shares);
        Assert.Equal("Ambient", shares[0].Title);
    }

    [Fact]
    public void OneSharedTimestampIsNotStampSpread()
    {
        var at = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.False(LikedFactsRules.StampsSpread(Repeat(20, i => T(at, "t" + i))));
        Assert.False(LikedFactsRules.StampsSpread([T(at, "a"), T(at, "b")]));
    }

    [Fact]
    public void TwoDistinctUtcDaysAreStampSpread()
    {
        var a = new DateTimeOffset(2026, 3, 15, 23, 0, 0, TimeSpan.Zero);
        var b = new DateTimeOffset(2026, 3, 16, 1, 0, 0, TimeSpan.Zero);
        Assert.True(LikedFactsRules.StampsSpread([T(a, "a"), T(b, "b")]));
        Assert.False(LikedFactsRules.StampsSpread([T(a, "a"), T(a.AddHours(-1), "b")]));
    }

    [Fact]
    public void YearHistogramIsConsecutiveWhenTheSpanFitsTwelveBars()
    {
        var rows = new List<LikedRow>();
        for (int y = 2010; y <= 2021; y++) rows.Add(T(null, "t" + y, year: y));

        var bars = LikedFactsRules.YearHistogram(rows.ToArray());
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
        var rows = new List<LikedRow>();
        for (int y = 1960; y <= 2020; y++) rows.Add(T(null, "t" + y, year: y));

        var bars = LikedFactsRules.YearHistogram(rows.ToArray());
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
        var rows = Repeat(10, i => T(added, "t" + i, year: 2011));
        Assert.Equal(2010, LikedFactsRules.DominantReleaseDecade(rows));
        Assert.Equal(2020, LikedFactsRules.DominantDecade(rows));
        Assert.Equal("t0", LikedFactsRules.OldestRelease(rows)!.Value.Track.Title);
    }

    [Fact]
    public void HasReleaseYearsUsesTheDecadeEvidenceFloor()
    {
        Assert.False(LikedFactsRules.HasReleaseYears(Repeat(9, i => T(null, "t" + i, year: 2014))));
        Assert.True(LikedFactsRules.HasReleaseYears(Repeat(10, i => T(null, "u" + i, year: 2014))));
    }

    [Fact]
    public void YearLensIsAnInclusiveWindow()
    {
        var filter = Track.FilterState.Default.WithReleaseYear(2010, 2014);
        Assert.Equal(LikedFactsRules.LikedLens.Year, LikedFactsRules.ActiveLenses(filter));
        Assert.True(LikedFactsRules.IsYearLens(filter, new LikedFactsRules.YearBucket(2010, 2014, 3)));
        Assert.False(LikedFactsRules.IsYearLens(filter, new LikedFactsRules.YearBucket(2010, 2010, 1)));
        Assert.Equal(1, filter.ActiveCount);
        Assert.True(Track.FilterModel.Matches(RowOf(T(null, "in", year: 2012), NowSec), "", filter, NowSec));
        Assert.False(Track.FilterModel.Matches(RowOf(T(null, "out", year: 2009), NowSec), "", filter, NowSec));
        Assert.False(Track.FilterModel.Matches(RowOf(T(null, "unknown", year: 0), NowSec), "", filter, NowSec));
    }

    // ── Which facts earn a card ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(9, 10, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Absent)]
    [InlineData(10, 20, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Absent)]
    [InlineData(12, 20, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Label)]
    [InlineData(19, 20, 5, 0.3f, 0.5f, LikedFactsRules.FactShape.Label)]
    [InlineData(20, 20, 2, 0.3f, 0.5f, LikedFactsRules.FactShape.Label)]
    [InlineData(20, 20, 3, 0.5f, 0.5f, LikedFactsRules.FactShape.Label)]
    [InlineData(20, 20, 3, 0.49f, 0.5f, LikedFactsRules.FactShape.Graph)]
    public void ShapeBoundaryTable(int known, int total, int cats, float top, float cap, LikedFactsRules.FactShape expected)
        => Assert.Equal(expected, LikedFactsRules.Shape(known, total, cats, top, cap));

    [Fact]
    public void YearsDominanceMeasuresHistogramBins()
    {
        var rows = new List<LikedRow>();
        int[] early = [1965, 1968, 1971, 1972, 1974, 1975, 1977, 1980, 1984, 1995, 1997, 2000, 2005, 2012, 2015, 2016, 2017, 2018, 2019];
        for (int i = 0; i < early.Length; i++) rows.Add(T(null, "e" + i, year: early[i]));
        for (int i = 0; i < 45; i++) rows.Add(T(null, "l" + i, year: 2020 + i % 5));
        var array = rows.ToArray();
        var buckets = LikedFactsRules.YearHistogram(array);
        var d = LikedFactsRules.YearsDominance(buckets);
        Assert.Equal(64, d.Known);
        Assert.Equal(buckets.Count - 1, d.TopIndex);
        Assert.InRange(d.TopShare, 0.69f, 0.71f);
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.YearsShape(array, buckets));

        var spread = Repeat(24, i => T(null, "s" + i, year: 2010 + i / 2));
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.YearsShape(spread, LikedFactsRules.YearHistogram(spread)));

        var one = Repeat(50, i => T(null, "o" + i, year: i < 41 ? 2024 : 2012 + i % 7));
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.YearsShape(one, LikedFactsRules.YearHistogram(one)));
    }

    [Theory]
    [InlineData(89.9, Track.TempoBand.Under90)]
    [InlineData(90.0, Track.TempoBand.From90To119)]
    [InlineData(119.9, Track.TempoBand.From90To119)]
    [InlineData(120.0, Track.TempoBand.From120To139)]
    [InlineData(139.9, Track.TempoBand.From120To139)]
    [InlineData(140.0, Track.TempoBand.From140AndUp)]
    [InlineData(0.0, Track.TempoBand.Any)]
    public void TempoBandOfIsHalfOpen(double bpm, Track.TempoBand expected)
        => Assert.Equal(expected, Track.FilterModel.BandOf(bpm));

    [Fact]
    public void TempoBandCountsUseTheFilterBoundaries()
    {
        LikedRow[] rows = [T(null, "a", bpm: 89.9), T(null, "b", bpm: 90), T(null, "c", bpm: 139.9), T(null, "d", bpm: 140), T(null, "e"), T(null, "f", bpm: 0)];
        var counts = new int[LikedFactsRules.TempoBandCount];
        int known = LikedFactsRules.TempoBandCounts(rows, counts);
        Assert.Equal(4, known);
        Assert.Equal(new[] { 1, 1, 1, 1 }, counts);
        var filter = Track.FilterState.Default with { Tempo = Track.TempoBand.From120To139 };
        Assert.True(Track.FilterModel.Matches(RowOf(rows[2], NowSec), "", filter, NowSec));
        Assert.False(Track.FilterModel.Matches(RowOf(rows[3], NowSec), "", filter, NowSec));
    }

    [Fact]
    public void TempoShapeIsAbsentUnderCoverage()
    {
        var thin = Repeat(40, i => T(null, "t" + i, bpm: i < 22 ? 80 + (i * 9) % 100 : null));
        Assert.Equal(LikedFactsRules.FactShape.Absent, LikedFactsRules.TempoShape(thin));
        var ok = Repeat(40, i => T(null, "u" + i, bpm: i < 30 ? 80 + (i * 9) % 100 : null));
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.TempoShape(ok));
    }

    [Fact]
    public void HalfTimeSplitIsALabelAndBimodalIsAGraph()
    {
        var dnb = Repeat(40, i => T(null, "d" + i, bpm: i < 32 ? 174 : 87));
        var d = LikedFactsRules.TempoDominance(dnb);
        Assert.Equal(3, d.TopIndex);
        Assert.InRange(d.TopShare, 0.79f, 0.81f);
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.TempoShape(dnb));

        var split = Repeat(40, i => T(null, "s" + i, bpm: i < 20 ? 124 : 174));
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.TempoShape(split));
    }

    [Fact]
    public void TempoStatisticsUseTheLowerMedianAndSkipUnknown()
    {
        LikedRow[] rows = [T(null, "a", bpm: 170), T(null, "b", bpm: 100), T(null, "c"), T(null, "d", bpm: 130), T(null, "e", bpm: 120, color: 0xFF56D9F8u)];
        var s = LikedFactsRules.TempoStatistics(rows);
        Assert.Equal(4, s.Known);
        Assert.Equal(5, s.Total);
        Assert.Equal(120d, s.Median);
        Assert.Equal(100d, s.Min);
        Assert.Equal(170d, s.Max);

        var bpm = new float[4]; var argb = new uint[4];
        Assert.Equal(4, LikedFactsRules.TempoValues(rows, bpm, argb));
        Assert.Equal(new[] { 170f, 100f, 130f, 120f }, bpm);
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

        var flat = Repeat(60, i => T(null, "f" + i, tags: ["Style " + i % 15]));
        var fd = LikedFactsRules.BlendsDominance(flat);
        Assert.True(fd.Flat);
        Assert.Equal(15, fd.Styles);
        Assert.Equal(LikedFactsRules.FactShape.Label, LikedFactsRules.BlendShape(flat));

        var rock = Repeat(60, i => T(null, "r" + i, tags: [i < 40 ? "Classic Rock" : i < 52 ? "Hard Rock" : "Arena Rock"]));
        Assert.Equal(LikedFactsRules.FactShape.Graph, LikedFactsRules.BlendShape(rock));

        var thin = Repeat(4, i => T(null, "n" + i, tags: ["Tag " + i]));
        Assert.Equal(LikedFactsRules.FactShape.Absent, LikedFactsRules.BlendShape(thin));
        Assert.Equal(LikedFactsRules.FactShape.Absent, LikedFactsRules.BlendShape(Repeat(5, i => T(null, "u" + i))));
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LikedFactsRules.FactShape.Absent)]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, LikedFactsRules.FactShape.Label)]
    [InlineData(new[] { 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0 }, LikedFactsRules.FactShape.Label)]
    [InlineData(new[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1 }, LikedFactsRules.FactShape.Label)]
    [InlineData(new[] { 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 1 }, LikedFactsRules.FactShape.Graph)]
    public void WeekShapeTable(int[] counts, LikedFactsRules.FactShape expected)
    {
        var weeks = new LikedFactsRules.WeekBucket[counts.Length];
        for (int i = 0; i < counts.Length; i++) weeks[i] = new LikedFactsRules.WeekBucket(Now.AddDays(-7 * (counts.Length - i)), counts[i]);
        Assert.Equal(expected, LikedFactsRules.WeekShape(weeks));
    }

    [Fact]
    public void LatestStampIsTheNewestUsableStamp()
    {
        LikedRow[] rows = [T(Now.AddDays(-40)), T(Now.AddDays(-3), "new"), T(null, "unstamped"), T(DateTimeOffset.UnixEpoch, "epoch")];
        Assert.Equal(Now.AddDays(-3), LikedFactsRules.LatestStamp(rows));
        Assert.Null(LikedFactsRules.LatestStamp([T(null), T(DateTimeOffset.UnixEpoch)]));
    }

    [Fact]
    public void TempoFingerprintIsContentNotIdentity()
    {
        LikedRow[] a = [T(null, "a", bpm: 128), T(null, "b", bpm: 96.4), T(null, "c", bpm: 172)];
        LikedRow[] b = [T(null, "c", bpm: 172), T(null, "a", bpm: 128), T(null, "b", bpm: 96.4), T(null, "d")];
        LikedRow[] c = [T(null, "a", bpm: 128), T(null, "b", bpm: 96.4), T(null, "c", bpm: 173)];
        Assert.Equal(LikedFactsRules.FingerprintTempo(a), LikedFactsRules.FingerprintTempo(b));
        Assert.NotEqual(LikedFactsRules.FingerprintTempo(a), LikedFactsRules.FingerprintTempo(c));
        Assert.Equal(3, LikedFactsRules.FingerprintTempo(a).Known);
    }

    [Fact]
    public void TempoStatisticsMatchTheSortedLowerMedian()
    {
        var rng = new Random(7);
        var rows = Repeat(101, i => T(null, "t" + i, bpm: 60 + rng.Next(0, 140)));
        var sorted = new List<double>();
        foreach (var t in rows) sorted.Add(LikedFactsRules.TempoBpm(t.Track));
        sorted.Sort();
        var s = LikedFactsRules.TempoStatistics(rows);
        Assert.Equal(sorted[(sorted.Count - 1) / 2], s.Median);
        Assert.Equal(sorted[0], s.Min);
        Assert.Equal(sorted[^1], s.Max);
        Assert.Equal(101, s.Known);
    }

    [Fact]
    public void SummarizeAgreesWithTheStandaloneRules()
    {
        var rows = Repeat(60, i => T(null, "t" + i, year: 2010 + i % 12, bpm: i % 3 == 0 ? 96 : i % 3 == 1 ? 128 : 172,
                                     tags: [i % 5 == 0 ? "Dance" : i % 5 == 1 ? "Pop" : "House"],
                                     artists: ["Artist " + i % 7]));
        var s = LikedFactsRules.Summarize(rows);
        Assert.Equal(LikedFactsRules.YearHistogram(rows), s.YearBuckets);
        Assert.Equal(LikedFactsRules.YearsShape(rows, s.YearBuckets), s.YearsShape);
        Assert.Equal(LikedFactsRules.TempoShape(rows), s.Tempo.Shape);
        Assert.Equal(LikedFactsRules.TempoStatistics(rows), s.Tempo.Stats);
        Assert.Equal(LikedFactsRules.BlendShares(rows), s.BlendShares);
        Assert.Equal(LikedFactsRules.BlendsDominance(rows), s.BlendDominance);
        Assert.Equal(LikedFactsRules.BlendShape(rows), s.BlendShape);
        Assert.Equal(LikedFactsRules.TopArtists(rows, 40).Count, s.Artists.Count);
        Assert.True(LikedFactsRules.AnyArtistCredit(rows));
        Assert.False(LikedFactsRules.AnyArtistCredit(Repeat(3, i => T(null, "n" + i))));
        var counts = new int[4];
        LikedFactsRules.TempoBandCounts(rows, counts);
        Assert.Equal(counts, new[] { s.Tempo.Under90, s.Tempo.From90To119, s.Tempo.From120To139, s.Tempo.From140AndUp });
    }

    [Fact]
    public void LatchOnlyUpgrades()
    {
        var a = LikedFactsRules.FactShape.Absent; var l = LikedFactsRules.FactShape.Label; var g = LikedFactsRules.FactShape.Graph;
        Assert.Equal(l, LikedFactsRules.Latch(a, l));
        Assert.Equal(g, LikedFactsRules.Latch(l, g));
        Assert.Equal(g, LikedFactsRules.Latch(g, l));
        Assert.Equal(l, LikedFactsRules.Latch(l, a));
        Assert.Equal(a, LikedFactsRules.Latch(a, a));
    }

    /// <summary>0.3: equivalence is row IDENTITY (the track handle + the membership stamp). A tempo landing is a version
    /// bump on the same handle, not a different row; a different stamp or a different length is.</summary>
    [Fact]
    public void TracksEquivalentIsRowIdentityNotListIdentity()
    {
        var row = T(null, "a", bpm: 128);
        var other = T(Now, "b", year: 2020);
        LikedRow[] a = [row, other];
        LikedRow[] sameRows = [row, other];
        LikedRow[] restamped = [row, other with { AddedAtUnixSec = other.AddedAtUnixSec - 60 }];
        LikedRow[] shorter = [row];
        Assert.True(LikedFactsRules.TracksEquivalent(a, a));
        Assert.True(LikedFactsRules.TracksEquivalent(a, sameRows));
        Assert.False(LikedFactsRules.TracksEquivalent(a, restamped));
        Assert.False(LikedFactsRules.TracksEquivalent(a, shorter));
        Assert.False(LikedFactsRules.TracksEquivalent(a, default));
        Assert.True(LikedFactsRules.TracksEquivalent(default, default));
    }

    [Fact]
    public void TempoLensRoundTrips()
    {
        var filter = Track.FilterState.Default with { Tempo = Track.TempoBand.From120To139 };
        Assert.Equal(LikedFactsRules.LikedLens.Tempo, LikedFactsRules.ActiveLenses(filter));
        Assert.True(LikedFactsRules.IsTempoLens(filter, Track.TempoBand.From120To139));
        Assert.False(LikedFactsRules.IsTempoLens(filter, Track.TempoBand.Under90));
        Assert.False(LikedFactsRules.IsTempoLens(Track.FilterState.Default, Track.TempoBand.Any));
        Assert.Equal(1, filter.ActiveCount);
        var cleared = LikedFactsRules.ClearLens(filter, LikedFactsRules.LikedLens.Tempo);
        Assert.Equal(Track.TempoBand.Any, cleared.Tempo);
        Assert.Equal(LikedFactsRules.LikedLens.None, LikedFactsRules.ActiveLenses(cleared));
    }

    // ── 0.3: the per-row readers (the nulls became Known bits) ──────────────────────────────────────────────────────

    [Fact]
    public void TheRowReadersTellNotFetchedFromNone()
    {
        var cold = T(Now, "cold");
        var none = T(Now, "none", tags: Array.Empty<string>());
        var tagged = T(Now, "tagged", tags: ["Pop", "Chill"], bpm: 128.4, color: 0xFF112233u);

        Assert.False(LikedFactsRules.TryTags(cold.Track, out _));
        Assert.True(LikedFactsRules.TryTags(none.Track, out var empty));
        Assert.Equal(0, empty.Length);
        Assert.True(LikedFactsRules.TryTags(tagged.Track, out var two));
        Assert.Equal(2, two.Length);

        Assert.Equal(0d, LikedFactsRules.TempoBpm(cold.Track));
        Assert.Equal(0u, LikedFactsRules.CamelotColor(cold.Track));
        Assert.Equal(128.4, LikedFactsRules.TempoBpm(tagged.Track), 3);
        Assert.Equal(0xFF112233u, LikedFactsRules.CamelotColor(tagged.Track));
        Assert.False(LikedFactsRules.HasKeyedCredit(cold.Track));
        Assert.True(LikedFactsRules.HasKeyedCredit(T(Now, "credited", ["Aphex"]).Track));
    }
}
