// ── Wavee.Tests/RecentsViewTests.cs — the Recents page's PURE half, ported from 0.2.9 ─────────────────────────────────
//
// Role: TEST
// Owner: P
// Wave: 5
// Spec: ch 16 §8 (the rule set, verbatim), §9.1 (what must not be simplified)
//
// 0.2.9's `Wavee.Tests/RecentsViewTests.cs`, fact for fact. Only the CALL SHAPES changed: 0.2.9 fed each rule a list of
// `RecentsRow` objects holding uri strings; 0.3 feeds a `RecentsSnapshot` (the edge payload + a parallel `EntityRef`
// target per row and per member). A "uri" in a 0.2.9 fact is an `EntityRef` of the same kind here, the content-type
// string is the stored axis byte, and the header's `child_uri` list is the member run (0.3 decodes no second source).
//
// DROPPED (6), with the reason: the four `CollectRange_*` facts and the two `CollectChildUris_*` facts. ch 16 §8 marks
// both rules "port, then retire — 0.3 demands the whole model": the page no longer fetches per realized window, so the
// window collector and the drawer's uri collector have no caller. Their replacement — the one mount demand — is
// `RecentsLayout.DemandSlots`, pinned in RecentsPageRulesTests.cs. Two facts lose ONE half each for the same reason the
// decoder changed (the child-uri fallback source no longer exists): `CanExpand_…` and `DrawerEntries_…` keep the member
// half. Everything here is engine-free and touches no `Entities` static, so the class runs outside EntitiesCollection.

using System.Globalization;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class RecentsViewTests
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── the snapshot builder (the call-shape change) ─────────────────────────────────────────────────────────────────

    /// <summary>One row to build: its target, its axis, its declared count, its instant, its reason, its shape and its
    /// member run (each member's target + instant).</summary>
    internal sealed record R(EntityRef Target, RecentsContentType Axis, int ChildCount, long PlayedAtMs, RecentsReason Reason,
                    RecentsRowKind Kind, (EntityRef Target, long At)[]? Members);

    internal const long T0 = 1_700_000_000_000;

    internal static EntityRef Pl(int n) => new(EntityKind.Playlist, n);
    internal static EntityRef Al(int n) => new(EntityKind.Album, n);
    internal static EntityRef Ar(int n) => new(EntityKind.Artist, n);
    internal static EntityRef Sh(int n) => new(EntityKind.Show, n);
    internal static EntityRef Tr(int n) => new(EntityKind.Track, n);
    internal static readonly EntityRef Nothing = default;

    internal static R Group(EntityRef target, RecentsContentType axis = RecentsContentType.Music, int childCount = 1,
                   long playedAtMs = T0, (EntityRef Target, long At)[]? members = null,
                   RecentsReason reason = RecentsReason.Played)
        => new(target, axis, childCount, playedAtMs, reason, RecentsRowKind.Group, members);

    internal static R Play(EntityRef target, long playedAtMs = T0, RecentsContentType axis = RecentsContentType.Music)
        => new(target, axis, 0, playedAtMs, RecentsReason.Played, RecentsRowKind.Single, null);

    /// <summary>Row i gets item id i + 1 (the accordion's identity); member j of the whole run gets 1000 + j.</summary>
    internal static RecentsSnapshot Snap(params R[] rows)
    {
        var edges = new RecentsEdge[rows.Length];
        var targets = new EntityRef[rows.Length];
        var members = new List<RecentsMemberEdge>();
        var memberTargets = new List<EntityRef>();
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            int start = members.Count;
            foreach (var (target, at) in r.Members ?? [])
            {
                members.Add(new RecentsMemberEdge(new StringId(1000 + members.Count), at, (byte)target.Kind));
                memberTargets.Add(target);
            }
            int length = members.Count - start;
            edges[i] = new RecentsEdge(new StringId(i + 1), r.PlayedAtMs, r.ChildCount, (byte)r.Reason, (byte)r.Axis,
                (byte)r.Kind, length > 0 ? start : 0, length, (byte)r.Target.Kind);
            targets[i] = r.Target;
        }
        return new RecentsSnapshot(edges, targets, members.ToArray(), memberTargets.ToArray());
    }

    internal static string Localize(string key) => key == Strings.Detail.Today ? "Today"
        : key == Strings.Detail.Yesterday ? "Yesterday" : key;

    internal static long Ms(DateTimeOffset now, int daysAgo, int hour = 12)
    {
        var local = now.AddDays(daysAgo).Date.AddHours(hour);
        return new DateTimeOffset(local, now.Offset).ToUnixTimeMilliseconds();
    }

    static void AssertRowDatesAlignWithHeaders(RecentsSnapshot rows, RecentsSections sections, DateTimeOffset now)
    {
        foreach (var item in sections.Items)
        {
            if (item.Kind != RecentsFlatItemKind.Row) continue;
            Assert.Equal(RecentsView.DateOf(rows.Rows[item.OriginalRowIndex].PlayedAtMs, now.Offset),
                sections.HeaderDates[item.DayIndex]);
        }
    }

    /// <summary>The label BuildSections actually stamps for a header date (DateOnly midnight → DayBucketLabel).</summary>
    static string SectionLabel(DateOnly date, DateTimeOffset now)
        => date == DateOnly.MinValue ? "" : RecentsView.DayBucketLabel(date.ToDateTime(TimeOnly.MinValue), now, Inv, Localize);

    // ── chips ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentTypes_AreDerivedFromTheRows_InWireOrder_WithoutDuplicates()
    {
        var rows = Snap(
            Group(Pl(1)),
            Group(Sh(2), RecentsContentType.Podcasts),
            Group(Al(3)),
            Group(Sh(4), RecentsContentType.Podcasts));   // the stored axis is one byte: a repeat is the same token
        Assert.Equal(["music", "podcasts"], RecentsView.ContentTypes(rows));
    }

    [Fact]
    public void ContentTypes_OfAMusicOnlyList_OffersNoPodcastChipToFilterToNothing()
    {
        var rows = Snap(Group(Pl(1)), Group(Al(2)));
        Assert.Equal(["music"], RecentsView.ContentTypes(rows));
    }

    [Fact]
    public void ContentTypes_IgnoresRowsCarryingNone_AndIsEmptyForAnUntypedList()
    {
        var rows = Snap(Group(Pl(1), RecentsContentType.None), Group(Al(2), RecentsContentType.None));
        Assert.Empty(RecentsView.ContentTypes(rows));
    }

    // ── the filter predicate ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllMatchesEverything_IncludingRowsTheServerGaveNoContentType()
    {
        var rows = Snap(Group(Pl(1), RecentsContentType.None), Group(Al(2)));
        Assert.True(RecentsView.Matches(rows, 0, null));
        Assert.True(RecentsView.Matches(rows, 1, null));
    }

    [Fact]
    public void ASelectedChipMatchesOnlyItsOwnToken_AndNeverAnUntypedRow()
    {
        var rows = Snap(Group(Pl(1)), Group(Sh(2), RecentsContentType.Podcasts), Group(Al(3), RecentsContentType.None));
        Assert.True(RecentsView.Matches(rows, 0, "music"));
        Assert.False(RecentsView.Matches(rows, 1, "music"));
        Assert.False(RecentsView.Matches(rows, 2, "music"));
    }

    [Fact]
    public void Filter_ProducesADisplayMapIntoTheUnfilteredRows_InWireOrder()
    {
        var rows = Snap(Group(Pl(1)), Group(Sh(2), RecentsContentType.Podcasts), Group(Al(3)));
        Assert.Equal([0, 1, 2], RecentsView.Filter(rows, null));
        Assert.Equal([0, 2], RecentsView.Filter(rows, RecentsView.PivotMusic));
        Assert.Equal([1], RecentsView.Filter(rows, RecentsView.PivotPodcasts));
    }

    [Fact]
    public void PivotAvailable_UsesStoredContentTypeSuffix_NotTheRawWireKey()
    {
        var rows = Snap(Group(Pl(1)), Group(Sh(2), RecentsContentType.Podcasts), Group(Ar(3), RecentsContentType.None));
        Assert.True(RecentsView.PivotAvailable(rows, RecentsView.PivotMusic));
        Assert.True(RecentsView.PivotAvailable(rows, RecentsView.PivotPodcasts));
        Assert.True(RecentsView.PivotAvailable(rows, RecentsView.PivotArtists));
        Assert.False(RecentsView.PivotAvailable(rows, "content_type_music"));
        Assert.False(RecentsView.PivotAvailable(rows, "content_type_podcasts"));
        Assert.False(RecentsView.PivotAvailable(RecentsSnapshot.Empty, RecentsView.PivotMusic));
    }

    [Fact]
    public void Matches_KindArtistToken_IsDecidedFromTheHydrationUri_NotContentType()
    {
        // A headless header (no target of its own) resolves "kind:artist" the same way EntitySlotOf does — from the
        // first named member.
        var rows = Snap(
            Group(Ar(1), RecentsContentType.None),
            Group(Pl(2)),
            Group(Nothing, RecentsContentType.None, childCount: 2, members: [(Ar(9), 1)]));

        Assert.True(RecentsView.Matches(rows, 0, RecentsView.PivotArtists));
        Assert.False(RecentsView.Matches(rows, 1, RecentsView.PivotArtists));
        Assert.True(RecentsView.Matches(rows, 2, RecentsView.PivotArtists));

        // content_type_* tokens are unaffected by the kind token.
        Assert.True(RecentsView.Matches(rows, 1, "music"));
        Assert.False(RecentsView.Matches(rows, 0, "music"));
    }

    [Fact]
    public void Filter_KindArtistToken_KeepsOnlyArtistRows_RegardlessOfContentType()
    {
        var rows = Snap(Group(Ar(1)), Group(Pl(2)), Group(Ar(3), RecentsContentType.Podcasts));
        Assert.Equal([0, 2], RecentsView.Filter(rows, RecentsView.PivotArtists));
        Assert.Equal([0, 1], RecentsView.Filter(rows, "music"));
        Assert.Equal([2], RecentsView.Filter(rows, "podcasts"));
    }

    [Fact]
    public void DayBucketLabel_CoversTodayYesterdayWeekdayAndMonthDayAcrossAYearBoundary()
    {
        var now = new DateTimeOffset(2027, 1, 2, 18, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal("Today", RecentsView.DayBucketLabel(now.AddHours(-2), now, Inv, Localize));
        Assert.Equal("Yesterday", RecentsView.DayBucketLabel(now.AddDays(-1), now, Inv, Localize));
        Assert.Equal(Inv.DateTimeFormat.GetDayName(now.AddDays(-2).DayOfWeek),
            RecentsView.DayBucketLabel(now.AddDays(-2), now, Inv, Localize));
        Assert.Equal("Dec 25", RecentsView.DayBucketLabel(now.AddDays(-8), now, Inv, Localize));
    }

    [Fact]
    public void BuildSections_InsertsHeadersAfterFiltering_AndDropsAnEmptiedDayBucket()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: now.ToUnixTimeMilliseconds()),
            Group(Sh(2), RecentsContentType.Podcasts, playedAtMs: now.AddDays(-1).ToUnixTimeMilliseconds()),
            Group(Al(3), playedAtMs: now.AddDays(-2).ToUnixTimeMilliseconds()));

        var all = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, static k => k);
        Assert.Equal([0, 2, 4], all.HeaderIndices);
        Assert.Equal(6, all.Items.Length);   // 3 headers + 3 rows — content only, no synthetic trailing item

        var music = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "music"), now, Inv, static k => k);
        Assert.Equal([0, 2], music.HeaderIndices);
        Assert.Equal([1, -1, 3], music.RowToFlat);
        Assert.Equal([0, -1, 1], music.RowToDay);
        Assert.DoesNotContain(DateOnly.FromDateTime(now.AddDays(-1).Date), music.HeaderDates);
    }

    [Fact]
    public void BuildSections_EveryRowDateMatchesItsHeaderDate_AcrossTodayYesterdayWeekdayAndMonthDay()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(now, 0)),
            Group(Al(2), playedAtMs: Ms(now, 0, hour: 9)),
            Group(Pl(3), playedAtMs: Ms(now, -1)),
            Group(Al(4), playedAtMs: Ms(now, -3)),
            Group(Pl(5), playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group(Al(6), playedAtMs: new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        AssertRowDatesAlignWithHeaders(rows, sections, now);
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.Date),
            DateOnly.FromDateTime(now.AddDays(-1).Date),
            DateOnly.FromDateTime(now.AddDays(-3).Date),
            new DateOnly(2026, 5, 4),
        ], sections.HeaderDates);
    }

    [Fact]
    public void BuildSections_RowsBetweenTwoHeaders_AllCarryThePrecedingHeaderDayIndex()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(now, 0)),
            Group(Al(2), playedAtMs: Ms(now, 0, hour: 9)),
            Group(Pl(3), playedAtMs: Ms(now, -1)),
            Group(Al(4), playedAtMs: Ms(now, -3)),
            Group(Pl(5), playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        for (int h = 0; h < sections.HeaderIndices.Length; h++)
        {
            int start = sections.HeaderIndices[h] + 1;
            int end = h + 1 < sections.HeaderIndices.Length ? sections.HeaderIndices[h + 1] : sections.Items.Length;
            for (int i = start; i < end; i++)
            {
                Assert.Equal(RecentsFlatItemKind.Row, sections.Items[i].Kind);
                Assert.Equal(h, sections.Items[i].DayIndex);
            }
        }
    }

    // ── content-only projection (NO trailing dock spacer) ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildSections_IsContentOnly_TheLastFlatItemIsARealRow()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(now, 0)),
            Group(Sh(2), RecentsContentType.Podcasts, playedAtMs: Ms(now, -1)));

        var all = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal(RecentsFlatItemKind.Row, all.Items[^1].Kind);
        Assert.All(all.Items, i => Assert.True(i.Kind is RecentsFlatItemKind.DateHeader or RecentsFlatItemKind.Row));
        Assert.Equal(all.Items.Length, all.FlatToRow.Length);
        Assert.Equal(all.Items.Length, all.FlatToDay.Length);
        Assert.Equal(all.Items.Length, all.FlatToMonth.Length);

        var empty = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "nothing-matches"), now, Inv, Localize);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public void BuildSections_HeaderIndicesAndRowMapsAreExact()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(now, 0)),
            Group(Al(2), playedAtMs: Ms(now, 0, hour: 9)),
            Group(Pl(3), playedAtMs: Ms(now, -1)));

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal([0, 3], sections.HeaderIndices);
        Assert.All(sections.HeaderIndices, i => Assert.True(i < sections.Items.Length - 1));
        Assert.Equal([1, 2, 4], sections.RowToFlat);
        AssertRowDatesAlignWithHeaders(rows, sections, now);
    }

    [Fact]
    public void CountForDay_CountsRowsBetweenHeaders_AndTheLastDayToTheEnd()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(now, 0)),
            Group(Al(2), playedAtMs: Ms(now, 0, hour: 9)),
            Group(Pl(3), playedAtMs: Ms(now, -1)));

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal(2, RecentsView.CountForDay(sections, 0));
        Assert.Equal(1, RecentsView.CountForDay(sections, 1));
        Assert.Equal(0, RecentsView.CountForDay(sections, 2));
        Assert.Equal(0, RecentsView.CountForDay(sections, -1));
    }

    [Fact]
    public void BuildSections_DoesNotSort_NonContiguousSameDayRowsGetInterleavedHeaders()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        long today = now.ToUnixTimeMilliseconds();
        long july = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var rows = Snap(Group(Pl(1), playedAtMs: today), Group(Al(2), playedAtMs: july), Group(Pl(3), playedAtMs: today));

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.Date),
            new DateOnly(2026, 7, 6),
            DateOnly.FromDateTime(now.Date),
        ], sections.HeaderDates);
        Assert.Equal(3, sections.HeaderLabels.Length);
        Assert.Equal(sections.HeaderLabels[0], sections.HeaderLabels[2]);
        Assert.NotEqual(sections.HeaderLabels[0], sections.HeaderLabels[1]);
        Assert.Equal([0, 2, 4], sections.HeaderIndices);
        AssertRowDatesAlignWithHeaders(rows, sections, now);
    }

    [Fact]
    public void BuildSections_SameCountRegroup_ReplacesHeaderDatesAndLabels()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(now, 0)),
            Group(Sh(2), RecentsContentType.Podcasts, playedAtMs: Ms(now, -1)),
            Group(Al(3), playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group(Sh(4), RecentsContentType.Podcasts,
                playedAtMs: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));

        var music = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "music"), now, Inv, Localize);
        var podcasts = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "podcasts"), now, Inv, Localize);

        Assert.Equal(2, RecentsView.Filter(rows, "music").Length);
        Assert.Equal(2, RecentsView.Filter(rows, "podcasts").Length);
        Assert.Equal([DateOnly.FromDateTime(now.Date), new DateOnly(2026, 5, 4)], music.HeaderDates);
        Assert.Equal([DateOnly.FromDateTime(now.AddDays(-1).Date), new DateOnly(2026, 7, 6)], podcasts.HeaderDates);
        Assert.NotEqual(music.HeaderDates, podcasts.HeaderDates);
        Assert.NotEqual(music.HeaderLabels, podcasts.HeaderLabels);
        Assert.Equal(SectionLabel(music.HeaderDates[0], now), music.HeaderLabels[0]);
        Assert.Equal(SectionLabel(music.HeaderDates[1], now), music.HeaderLabels[1]);
        Assert.Equal(SectionLabel(podcasts.HeaderDates[0], now), podcasts.HeaderLabels[0]);
        Assert.Equal(SectionLabel(podcasts.HeaderDates[1], now), podcasts.HeaderLabels[1]);
    }

    [Fact]
    public void BuildSections_PendingSeedShape_IsASingleTodayHeader_NeverJuly()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var seed = new R[8];
        for (int i = 0; i < seed.Length; i++) seed[i] = Group(Nothing, RecentsContentType.None, playedAtMs: now.AddMinutes(-i).ToUnixTimeMilliseconds());
        var rows = Snap(seed);

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        DateOnly today = DateOnly.FromDateTime(now.Date);
        Assert.Equal([today], sections.HeaderDates);
        Assert.All(sections.HeaderDates, d => Assert.True(d == DateOnly.MinValue || d == today));
        Assert.DoesNotContain(sections.HeaderDates, static d => d.Month == 7);
        Assert.DoesNotContain(sections.HeaderLabels, static l => l.Contains("Jul", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingSeedRows_OneHeader_LocalToday()
    {
        // 23:30 local at UTC−8: a UtcNow-stamped seed would read as tomorrow through the grouping pass's LOCAL offset.
        var now = new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.FromHours(-8));
        var rows = RecentsView.PendingSeedRows(now);
        Assert.Equal(8, rows.Count);
        Assert.All(rows.Rows, r => Assert.Equal(RecentsRowKind.Group, r.Shape));
        Assert.All(rows.Rows, r => Assert.Equal(RecentsReason.Played, r.Why));

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        DateOnly today = DateOnly.FromDateTime(now.Date);
        Assert.Equal([0], sections.HeaderIndices);
        Assert.Equal([today], sections.HeaderDates);
        Assert.Equal([SectionLabel(today, now)], sections.HeaderLabels);
    }

    [Fact]
    public void Relabel_AdvancesDayWords_DatesStable()
    {
        var before = new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 8, 13, 0, 5, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: before.ToUnixTimeMilliseconds()),
            Group(Al(2), playedAtMs: before.AddDays(-1).ToUnixTimeMilliseconds()));

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), before, Inv, Localize);
        Assert.Equal([SectionLabel(sections.HeaderDates[0], before), SectionLabel(sections.HeaderDates[1], before)],
            sections.HeaderLabels);

        var relabeled = RecentsView.Relabel(sections, after, Inv, Localize);
        Assert.Same(sections.HeaderDates, relabeled.HeaderDates);
        Assert.Same(sections.Items, relabeled.Items);
        Assert.Same(sections.HeaderIndices, relabeled.HeaderIndices);
        Assert.Same(sections.RowToFlat, relabeled.RowToFlat);
        Assert.Same(sections.RowToDay, relabeled.RowToDay);
        Assert.Equal([SectionLabel(sections.HeaderDates[0], after), SectionLabel(sections.HeaderDates[1], after)],
            relabeled.HeaderLabels);
        Assert.NotEqual(sections.HeaderLabels[0], relabeled.HeaderLabels[0]);
    }

    [Fact]
    public void ZeroTimestamp_IsMinValue_EmptyLabel_AndDoesNotBecomeToday()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(DateOnly.MinValue, RecentsView.DateOf(0, now.Offset));
        Assert.Equal("", RecentsView.DayBucketLabel(0L, now, Inv, Localize));

        var rows = Snap(Group(Pl(1), playedAtMs: now.ToUnixTimeMilliseconds()), Group(Al(2), playedAtMs: 0));
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal("", sections.HeaderLabels[1]);
        Assert.Equal([DateOnly.FromDateTime(now.Date), DateOnly.MinValue], sections.HeaderDates);
        Assert.NotEqual(DateOnly.FromDateTime(now.Date), RecentsView.DateOf(rows.Rows[1].PlayedAtMs, now.Offset));
        AssertRowDatesAlignWithHeaders(rows, sections, now);
    }

    [Fact]
    public void DayBucketLabel_FourTiersAndPreviousYear_FromTheAugustClock()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal("Today", RecentsView.DayBucketLabel(now.AddHours(-2), now, Inv, Localize));
        Assert.Equal("Yesterday", RecentsView.DayBucketLabel(now.AddDays(-1), now, Inv, Localize));
        Assert.Equal(Inv.DateTimeFormat.GetDayName(now.AddDays(-3).DayOfWeek),
            RecentsView.DayBucketLabel(now.AddDays(-3), now, Inv, Localize));
        Assert.Equal("May 04", RecentsView.DayBucketLabel(new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero), now, Inv, Localize));
        Assert.Equal("Dec 15", RecentsView.DayBucketLabel(new DateTimeOffset(2025, 12, 15, 12, 0, 0, TimeSpan.Zero), now, Inv, Localize));
    }

    // ── hydration targets ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HydrationUri_PrefersTheContextUri_ThenTheFirstChild_ThenTheRowsOwnUri()
    {
        // 0.2.9 `HydrationUri` is `EntitySlotOf` (ch 16 §8's rename): the row's own target IS the context.
        var rows = Snap(
            Group(Pl(1)),
            // A single-context group header carries no target of its own — it renders from its members.
            Group(Nothing, childCount: 3, members: [(Nothing, 3), (Tr(9), 2), (Tr(8), 1)]),
            Play(Tr(7)),
            Group(Nothing, childCount: 0));

        Assert.Equal(Pl(1), RecentsView.EntitySlotOf(rows, 0));
        Assert.Equal(Tr(9), RecentsView.EntitySlotOf(rows, 1));
        Assert.Equal(Tr(7), RecentsView.EntitySlotOf(rows, 2));
        Assert.True(RecentsView.EntitySlotOf(rows, 3).IsNone);
        Assert.False(RecentsView.Names(RecentsView.EntitySlotOf(rows, 3)));
        Assert.True(RecentsView.EntitySlotOf(rows, 99).IsNone);   // a row index from a superseded shape answers nothing
    }

    // ── the expand drawer: the member run, nothing invented ─────────────────────────────────────────────────────────

    [Fact]
    public void CanExpand_NeedsAPlayCount_AndSomethingToList_FromEitherSource()
    {
        var rows = Snap(
            Group(Pl(1), childCount: 3, members: [(Tr(1), 1)]),
            Group(Pl(1), childCount: 3, members: [(Nothing, 2), (Tr(1), 1)]),   // a named member after an unnamed one
            Group(Ar(5), childCount: 3),                                         // "Played 3" with nothing listable
            Group(Ar(5), childCount: 3, members: [(Nothing, 1)]),
            Group(Pl(1), childCount: 0, members: [(Tr(1), 1)]));                  // a zero count never expands
        Assert.True(RecentsView.CanExpand(rows, 0));
        Assert.True(RecentsView.CanExpand(rows, 1));
        Assert.False(RecentsView.CanExpand(rows, 2));
        Assert.False(RecentsView.CanExpand(rows, 3));
        Assert.False(RecentsView.CanExpand(rows, 4));
    }

    [Fact]
    public void MissingMembers_IsTheCountWithoutAList_TheCaseThePageLogsOnce()
    {
        var rows = Snap(
            Group(Ar(5), childCount: 3),
            Group(Pl(1), childCount: 3, members: [(Tr(1), 1)]),
            Group(Pl(1), childCount: 0));
        Assert.True(RecentsView.MissingMembers(rows, 0));
        Assert.False(RecentsView.MissingMembers(rows, 1));
        Assert.False(RecentsView.MissingMembers(rows, 2));
    }

    [Fact]
    public void DrawerEntries_ListsTheMembersAsSent_ElseTheChildUrisWithNoInstant()
    {
        var rows = Snap(
            Group(Pl(1), childCount: 3, members: [(Tr(2), 20), (Nothing, 0), (Tr(1), 10)]),
            Group(Ar(5), childCount: 3));

        var entries = RecentsView.DrawerEntries(rows, 0);
        var targets = RecentsView.DrawerTargets(rows, 0);
        // Handed back in place — empties and all, in wire order, each with its OWN instant.
        Assert.Equal(3, entries.Length);
        Assert.Equal([20L, 0L, 10L], entries.ToArray().Select(e => e.PlayedAtMs));
        Assert.Equal([Tr(2), Nothing, Tr(1)], targets.ToArray());
        Assert.True(entries.Overlaps(rows.Members));   // a view over the snapshot, not a copy

        Assert.True(RecentsView.DrawerEntries(rows, 1).IsEmpty);
        Assert.True(RecentsView.DrawerEntries(rows, 7).IsEmpty);
    }

    [Fact]
    public void MetaFor_BranchesPlayedSavedAndUnknown_WithSavedAtLeastOne()
    {
        var played = new RecentsEdge(new StringId(1), T0, 3, (byte)RecentsReason.Played, (byte)RecentsContentType.Music,
            (byte)RecentsRowKind.Group, 0, 0);
        var saved = played with { ItemId = new StringId(2), Reason = (byte)RecentsReason.Saved, ChildCount = 0 };
        var unknown = played with { ItemId = new StringId(3), Reason = (byte)RecentsReason.Unknown };

        Assert.Equal(new RecentsMeta(RecentsMetaKind.PlayedCount, 3), RecentsView.MetaFor(in played));
        Assert.Equal(new RecentsMeta(RecentsMetaKind.SavedCount, 1), RecentsView.MetaFor(in saved));
        Assert.Equal(new RecentsMeta(RecentsMetaKind.PlayedAt, 0), RecentsView.MetaFor(in unknown));
    }

    [Fact]
    public void DayDensity_CountsPlayedOnly_UsesLogLevels_AndKeepsFirstTopItemOnATie()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), childCount: 3, playedAtMs: now.ToUnixTimeMilliseconds()),
            Group(Pl(2), childCount: 3, playedAtMs: now.AddHours(-1).ToUnixTimeMilliseconds()),
            Group(Pl(3), childCount: 99, playedAtMs: now.AddHours(-2).ToUnixTimeMilliseconds(), reason: RecentsReason.Saved),
            Play(Tr(1), now.AddDays(-1).ToUnixTimeMilliseconds()));

        var calendar = RecentsView.DayDensity(rows, RecentsView.Filter(rows, null), now, Inv);
        var month = Assert.Single(calendar.Months);
        var today = month.Days[now.Day - 1];
        var yesterday = month.Days[now.Day - 2];
        Assert.Equal(6, today.PlayCount);
        Assert.Equal(5, today.DensityLevel);
        Assert.Equal(new StringId(1), today.TopItem?.ItemId);   // row 0's item id: the FIRST of the tied pair
        Assert.Equal(0, today.TopItem?.OriginalRowIndex);
        Assert.Equal(1, yesterday.PlayCount);
        Assert.InRange(yesterday.DensityLevel, 1, 4);
        Assert.Equal(7, month.TotalPlays);
        Assert.Equal(DateOnly.FromDateTime(now.Date), month.BusiestDay);
    }

    // ── calendar grid geometry ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>May 2026 back to February 2026 — four months that between them produce 4, 5 and 6 week rows.</summary>
    static RecentsCalendar SpringCalendar(CultureInfo culture)
    {
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: now.ToUnixTimeMilliseconds()),
            Group(Al(2), playedAtMs: new DateTimeOffset(2026, 2, 3, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        return RecentsView.DayDensity(rows, RecentsView.Filter(rows, null), now, culture);
    }

    static RecentsCalendarMonth MonthOf(RecentsCalendar calendar, int year, int month)
    {
        foreach (var candidate in calendar.Months)
            if (candidate.Year == year && candidate.Month == month) return candidate;
        Assert.Fail($"the calendar carries no {year}-{month:00} month");
        return null!;
    }

    [Fact]
    public void WeekCount_IsTheRowsTheGridActuallyNeeds_ForFourFiveAndSixWeekMonths()
    {
        var calendar = SpringCalendar(Inv);   // invariant = a Sunday-first grid

        var february = MonthOf(calendar, 2026, 2);
        Assert.Equal(0, february.FirstDayOffset);
        Assert.Equal(4, february.WeekCount);

        var april = MonthOf(calendar, 2026, 4);
        Assert.Equal(3, april.FirstDayOffset);
        Assert.Equal(5, april.WeekCount);

        var may = MonthOf(calendar, 2026, 5);
        Assert.Equal(5, may.FirstDayOffset);
        Assert.Equal(6, may.WeekCount);
    }

    /// <summary>A Monday-first culture cloned from invariant (the repo builds with InvariantGlobalization).</summary>
    static CultureInfo MondayFirst()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Monday;
        return culture;
    }

    [Fact]
    public void WeekCount_FollowsTheCultureFirstDayOfWeek_SoAMondayFirstGridCanNeedAnExtraRow()
    {
        var dutch = MondayFirst();
        Assert.Equal(DayOfWeek.Monday, dutch.DateTimeFormat.FirstDayOfWeek);
        var calendar = SpringCalendar(dutch);

        var february = MonthOf(calendar, 2026, 2);
        Assert.Equal(6, february.FirstDayOffset);
        Assert.Equal(5, february.WeekCount);

        var may = MonthOf(calendar, 2026, 5);
        Assert.Equal(4, may.FirstDayOffset);
        Assert.Equal(5, may.WeekCount);
    }

    [Fact]
    public void MaxWeeks_TakesTheTallestMonth_AndFloorsAtOneForAnEmptyCalendar()
    {
        Assert.Equal(6, RecentsView.MaxWeeks(SpringCalendar(Inv)));
        Assert.Equal(6, RecentsView.MaxWeeks(SpringCalendar(MondayFirst())));
        Assert.Equal(1, RecentsView.MaxWeeks(new RecentsCalendar([], 0)));
    }

    // ── shared-element eligibility ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstOccurrence_TagsOnlyTheMostRecentRowOfEachUri_SoNoTwoLiveNodesShareAMorphId()
    {
        var rows = Snap(Group(Pl(1)), Group(Al(2)), Group(Pl(1)), Group(Nothing));
        Assert.Equal([true, true, false, false], RecentsView.FirstOccurrence(rows));
    }

    // ── played-at formatting ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlayedAt_TodayIsATime_ThisWeekIsAWeekday_ThisYearIsAnAbbreviatedMonthDay_OlderIsAShortDate()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        Assert.Equal(now.AddHours(-3).ToString("t", Inv), RecentsView.PlayedAt(now.AddHours(-3), now, Inv));
        Assert.Equal(Inv.DateTimeFormat.GetAbbreviatedDayName(now.AddDays(-3).DayOfWeek), RecentsView.PlayedAt(now.AddDays(-3), now, Inv));
        Assert.Equal("Jun 12", RecentsView.PlayedAt(new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero), now, Inv));
        var older = new DateTimeOffset(2024, 3, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(older.ToString("d", Inv), RecentsView.PlayedAt(older, now, Inv));
    }

    [Fact]
    public void PlayedAt_OfAnUnknownTimestamp_IsEmpty_NotAnEpochDate()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        Assert.Equal("", RecentsView.PlayedAt(0L, now, Inv));
        Assert.Equal("", RecentsView.PlayedAt(-1L, now, Inv));
    }

    [Fact]
    public void Summary_StatesTheCount_AndTheWindowThePlaysSpan()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        static long At(int month, int day) => new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var rows = Snap(Group(Pl(1), playedAtMs: At(8, 1)), Group(Al(2), playedAtMs: At(6, 3)));
        Assert.Equal("2 · Jun 03 – Aug 01", RecentsView.Summary(rows, now, Inv));
    }

    [Fact]
    public void Summary_OfAnEmptyList_IsEmpty_AndATimestampLessListStillStatesItsCount()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        Assert.Equal("", RecentsView.Summary(RecentsSnapshot.Empty, now, Inv));
        Assert.Equal("1", RecentsView.Summary(Snap(Group(Pl(1), playedAtMs: 0)), now, Inv));
    }

    [Fact]
    public void Summary_TodayEndpoint_RendersDayWord()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 26, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: new DateTimeOffset(2026, 5, 14, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group(Al(2), playedAtMs: now.ToUnixTimeMilliseconds()));
        Assert.Equal("2 · May 14 – Today", RecentsView.Summary(rows, now, Inv, localize: Localize));
    }

    [Fact]
    public void Summary_SingleDay_Collapses()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 26, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: now.AddHours(-3).ToUnixTimeMilliseconds()),
            Group(Al(2), playedAtMs: now.AddHours(-1).ToUnixTimeMilliseconds()));
        Assert.Equal("2 · Today", RecentsView.Summary(rows, now, Inv, localize: Localize));
    }

    [Fact]
    public void Summary_GroupedFrom_AppendsTheSumOfChildCounts_WhenItExceedsTheRowCount()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), childCount: 5, playedAtMs: now.ToUnixTimeMilliseconds()),
            Group(Al(2), childCount: 3, playedAtMs: now.ToUnixTimeMilliseconds()));
        string summary = RecentsView.Summary(rows, now, Inv, groupedPhrase: static n => "grouped from " + n + " plays", localize: Localize);
        Assert.Equal("2 · Today · grouped from 8 plays", summary);
    }

    [Fact]
    public void Summary_GroupedFrom_OmittedWhenEveryRowContributesExactlyOnePlay()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), childCount: 1, playedAtMs: now.ToUnixTimeMilliseconds()),
            Group(Al(2), childCount: 0, playedAtMs: now.ToUnixTimeMilliseconds()));   // Max(1, 0)
        string summary = RecentsView.Summary(rows, now, Inv, groupedPhrase: static n => "grouped from " + n + " plays", localize: Localize);
        Assert.Equal("2 · Today", summary);
    }

    [Fact]
    public void Summary_GroupedFrom_NeverInvokedOnAnEmptyList()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal("", RecentsView.Summary(RecentsSnapshot.Empty, now, Inv,
            groupedPhrase: static _ => throw new InvalidOperationException("must not be called for an empty list")));
    }

    [Fact]
    public void ChipLabel_IsTheWireTokenItself_SoAContentTypeAddedTomorrowStaysRenderable()
    {
        Assert.Equal("Music", RecentsView.ChipLabel("music", Inv));
        Assert.Equal("Audiobooks", RecentsView.ChipLabel("audiobooks", Inv));
        Assert.Equal("", RecentsView.ChipLabel("", Inv));
    }

    // ── owner display names ───────────────────────────────────────────────────────────────────────────────────────────

    const string RawOwnerId = "31testuser000000000000000000";

    [Fact]
    public void OwnerSubtitle_AResolvedProfileNameAlwaysWins()
    {
        Assert.Equal("Jamie", RecentsView.OwnerSubtitle("Some Store Name", RawOwnerId, "Jamie"));
        Assert.Equal("Jamie", RecentsView.OwnerSubtitle(null, null, "Jamie"));
    }

    [Fact]
    public void OwnerSubtitle_StoreNameShownOnlyWhenItDiffersFromTheRawId_EitherSpelling()
    {
        Assert.Equal("Jamie's playlist", RecentsView.OwnerSubtitle("Jamie's playlist", RawOwnerId, null));
        Assert.Null(RecentsView.OwnerSubtitle(RawOwnerId, RawOwnerId, null));
        Assert.Null(RecentsView.OwnerSubtitle(RecentsView.UserUriPrefix + RawOwnerId, RawOwnerId, null));
    }

    [Fact]
    public void OwnerSubtitle_NoNameAnywhere_IsNull_NeverAnEmptyLine()
    {
        Assert.Null(RecentsView.OwnerSubtitle(null, null, null));
        Assert.Null(RecentsView.OwnerSubtitle("", RawOwnerId, null));
    }

    [Fact]
    public void OwnerSubtitle_StoreNameWithNoRawIdToCompareAgainst_StillShows()
    {
        Assert.Equal("Jamie's playlist", RecentsView.OwnerSubtitle("Jamie's playlist", null, null));
    }

    // ── viewport-derived accent (the pure day-bucket selector) ───────────────────────────────────────────────────────

    [Fact]
    public void AccentSourceRow_ReturnsTheFirstRowOfTheStickyBucket()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(now, 0)),
            Group(Al(2), playedAtMs: Ms(now, 0, hour: 9)),
            Group(Pl(3), playedAtMs: Ms(now, -1)));
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);

        int todayHeader = sections.HeaderIndices[0];
        Assert.Equal(0, RecentsView.AccentSourceRow(sections, rows, todayHeader));
        Assert.Equal(0, RecentsView.AccentSourceRow(sections, rows, todayHeader + 1));
        Assert.Equal(2, RecentsView.AccentSourceRow(sections, rows, sections.HeaderIndices[1]));
    }

    [Fact]
    public void AccentSourceRow_BoundsTheForwardWalkToEightItems()
    {
        var items = new RecentsFlatItem[10];
        for (int i = 0; i < 8; i++) items[i] = new RecentsFlatItem(RecentsFlatItemKind.DateHeader, -1, 0, 0);
        items[8] = new RecentsFlatItem(RecentsFlatItemKind.Row, 5, 0, 0);
        items[9] = new RecentsFlatItem(RecentsFlatItemKind.Row, 6, 0, 0);
        var sections = new RecentsSections(items, [0], [""], [DateOnly.MinValue], [], [], [], [], []);
        var seven = new R[7];
        for (int i = 0; i < seven.Length; i++) seven[i] = Group(Pl(i + 1));
        var rows = Snap(seven);

        Assert.Equal(-1, RecentsView.AccentSourceRow(sections, rows, 0));   // the Row at offset 8 is out of bound
        Assert.Equal(5, RecentsView.AccentSourceRow(sections, rows, 1));    // one step in, offset 8 is now in bound
    }

    [Fact]
    public void AccentSourceRow_StopsAtTheBucketBoundary_NeverBorrowsTheNextDay()
    {
        RecentsFlatItem[] items =
        [
            new(RecentsFlatItemKind.DateHeader, -1, 0, 0),
            new(RecentsFlatItemKind.DateHeader, -1, 1, 0),
            new(RecentsFlatItemKind.Row, 0, 1, 0),
        ];
        var sections = new RecentsSections(items, [0, 1], ["", ""], [DateOnly.MinValue, DateOnly.MinValue], [], [], [], [], []);
        var rows = Snap(Group(Pl(1)));

        Assert.Equal(-1, RecentsView.AccentSourceRow(sections, rows, 0));
        Assert.Equal(0, RecentsView.AccentSourceRow(sections, rows, 1));
    }

    [Fact]
    public void AccentSourceRow_OutOfRangeOrEmpty_FallsBackToMinusOne()
    {
        var rows = Snap(Group(Pl(1)));
        var empty = new RecentsSections([], [], [], [], [], [], [], [], []);
        Assert.Equal(-1, RecentsView.AccentSourceRow(empty, rows, 0));
        Assert.Equal(-1, RecentsView.AccentSourceRow(empty, rows, -1));

        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal(-1, RecentsView.AccentSourceRow(sections, rows, 999));
    }
}
