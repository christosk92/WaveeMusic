using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentGpu.Localization;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The Recents page's PURE half (<c>Features/Recents/RecentsView.cs</c>) — the decisions a reviewer wants
/// pinned: which chips exist, what a chip filters, which URIs a realized window owes the network, which rows may claim
/// the shared-element tag, and how a play instant reads. All engine-free, so these drive the REAL production rules
/// rather than a copy of them.</summary>
public class RecentsViewTests
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static RecentsRow Group(string id, string? uri, string? contentType = "music", int childCount = 1,
                            long playedAtMs = 1_700_000_000_000, IReadOnlyList<string>? children = null)
        => new(RecentsRowKind.Group, id, uri ?? "", uri, null, null, null, childCount, playedAtMs,
            uri is null ? RecentsEntityKind.Unknown : RecentsList.EntityKindOf(uri),
            RecentsReason.Played, contentType, children);

    static string Localize(string key) => key == Strings.Detail.Today ? "Today"
        : key == Strings.Detail.Yesterday ? "Yesterday" : key;

    static long Ms(DateTimeOffset now, int daysAgo, int hour = 12)
    {
        var local = now.AddDays(daysAgo).Date.AddHours(hour);
        return new DateTimeOffset(local, now.Offset).ToUnixTimeMilliseconds();
    }

    static void AssertRowDatesAlignWithHeaders(IReadOnlyList<RecentsRow> rows, RecentsSections sections,
                                               DateTimeOffset now)
    {
        foreach (var item in sections.Items)
        {
            if (item.Kind != RecentsFlatItemKind.Row) continue;
            Assert.Equal(
                RecentsView.DateOf(rows[item.OriginalRowIndex].PlayedAtMs, now.Offset),
                sections.HeaderDates[item.DayIndex]);
        }
    }

    /// <summary>The label BuildSections actually stamps for a header date (DateOnly midnight → DayBucketLabel).</summary>
    static string SectionLabel(DateOnly date, DateTimeOffset now)
        => date == DateOnly.MinValue ? ""
            : RecentsView.DayBucketLabel(date.ToDateTime(TimeOnly.MinValue), now, Inv, Localize);

    // ── chips ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentTypes_AreDerivedFromTheRows_InWireOrder_WithoutDuplicates()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:show:2", "podcasts"),
            Group("c", "spotify:album:3"),
            Group("d", "spotify:show:4", "PODCASTS"),   // the set is case-insensitive; first spelling wins
        ];
        Assert.Equal(["music", "podcasts"], RecentsView.ContentTypes(rows));
    }

    [Fact]
    public void ContentTypes_OfAMusicOnlyList_OffersNoPodcastChipToFilterToNothing()
    {
        RecentsRow[] rows = [Group("a", "spotify:playlist:1"), Group("b", "spotify:album:2")];
        Assert.Equal(["music"], RecentsView.ContentTypes(rows));
    }

    [Fact]
    public void ContentTypes_IgnoresRowsCarryingNone_AndIsEmptyForAnUntypedList()
    {
        RecentsRow[] rows = [Group("a", "spotify:playlist:1", contentType: null), Group("b", "spotify:album:2", "")];
        Assert.Empty(RecentsView.ContentTypes(rows));
    }

    // ── the filter predicate ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllMatchesEverything_IncludingRowsTheServerGaveNoContentType()
    {
        Assert.True(RecentsView.Matches(Group("a", "spotify:playlist:1", contentType: null), null));
        Assert.True(RecentsView.Matches(Group("b", "spotify:album:2"), null));
    }

    [Fact]
    public void ASelectedChipMatchesOnlyItsOwnToken_AndNeverAnUntypedRow()
    {
        var music = Group("a", "spotify:playlist:1");
        var podcast = Group("b", "spotify:show:2", "podcasts");
        var untyped = Group("c", "spotify:album:3", contentType: null);
        Assert.True(RecentsView.Matches(music, "music"));
        Assert.False(RecentsView.Matches(podcast, "music"));
        Assert.False(RecentsView.Matches(untyped, "music"));
    }

    [Fact]
    public void Filter_ProducesADisplayMapIntoTheUnfilteredRows_InWireOrder()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:show:2", "podcasts"),
            Group("c", "spotify:album:3"),
        ];
        Assert.Equal([0, 1, 2], RecentsView.Filter(rows, null));
        Assert.Equal([0, 2], RecentsView.Filter(rows, RecentsView.PivotMusic));
        Assert.Equal([1], RecentsView.Filter(rows, RecentsView.PivotPodcasts));
    }

    [Fact]
    public void PivotAvailable_UsesStoredContentTypeSuffix_NotTheRawWireKey()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:show:2", RecentsView.PivotPodcasts),
            Group("c", "spotify:artist:3", contentType: null),
        ];
        Assert.True(RecentsView.PivotAvailable(rows, RecentsView.PivotMusic));
        Assert.True(RecentsView.PivotAvailable(rows, RecentsView.PivotPodcasts));
        Assert.True(RecentsView.PivotAvailable(rows, RecentsView.PivotArtists));
        Assert.False(RecentsView.PivotAvailable(rows, "content_type_music"));
        Assert.False(RecentsView.PivotAvailable(rows, "content_type_podcasts"));
        Assert.False(RecentsView.PivotAvailable(Array.Empty<RecentsRow>(), RecentsView.PivotMusic));
    }

    [Fact]
    public void Matches_KindArtistToken_IsDecidedFromTheHydrationUri_NotContentType()
    {
        var artistGroup = Group("a", "spotify:artist:1", contentType: null);
        var musicPlaylist = Group("b", "spotify:playlist:2");
        // A headless header (no uri of its own) resolves "kind:artist" the same way HydrationUri does — from the
        // first decoded child, exactly like a header with no uri resolves ITS EntityKind at grouping time.
        var headlessArtist = new RecentsRow(RecentsRowKind.Group, "c", "", null, null, null, null, 2, 1,
            RecentsEntityKind.Artist, RecentsReason.Played, null, ["spotify:artist:9"]);

        Assert.True(RecentsView.Matches(artistGroup, RecentsView.PivotArtists));
        Assert.False(RecentsView.Matches(musicPlaylist, RecentsView.PivotArtists));
        Assert.True(RecentsView.Matches(headlessArtist, RecentsView.PivotArtists));

        // content_type_* tokens are unaffected by the new token.
        Assert.True(RecentsView.Matches(musicPlaylist, "music"));
        Assert.False(RecentsView.Matches(artistGroup, "music"));
    }

    [Fact]
    public void Filter_KindArtistToken_KeepsOnlyArtistRows_RegardlessOfContentType()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:artist:1"),
            Group("b", "spotify:playlist:2"),
            Group("c", "spotify:artist:3", "podcasts"),
        ];
        Assert.Equal([0, 2], RecentsView.Filter(rows, RecentsView.PivotArtists));
        // content-type filtering on the same list is untouched by the new token.
        Assert.Equal([0, 1], RecentsView.Filter(rows, "music"));
        Assert.Equal([2], RecentsView.Filter(rows, "podcasts"));
    }

    [Fact]
    public void DayBucketLabel_CoversTodayYesterdayWeekdayAndMonthDayAcrossAYearBoundary()
    {
        var now = new DateTimeOffset(2027, 1, 2, 18, 0, 0, TimeSpan.FromHours(2));
        string Localize(string key) => key == Strings.Detail.Today ? "Today"
            : key == Strings.Detail.Yesterday ? "Yesterday" : key;

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
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:a", "music", playedAtMs: now.ToUnixTimeMilliseconds()),
            Group("b", "spotify:show:b", "podcasts", playedAtMs: now.AddDays(-1).ToUnixTimeMilliseconds()),
            Group("c", "spotify:album:c", "music", playedAtMs: now.AddDays(-2).ToUnixTimeMilliseconds()),
        ];

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
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: Ms(now, 0)),
            Group("today-b", "spotify:album:b", playedAtMs: Ms(now, 0, hour: 9)),
            Group("yesterday", "spotify:playlist:c", playedAtMs: Ms(now, -1)),
            Group("weekday", "spotify:album:d", playedAtMs: Ms(now, -3)),
            Group("may-a", "spotify:playlist:e", playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group("may-b", "spotify:album:f", playedAtMs: new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        ];

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
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: Ms(now, 0)),
            Group("today-b", "spotify:album:b", playedAtMs: Ms(now, 0, hour: 9)),
            Group("yesterday", "spotify:playlist:c", playedAtMs: Ms(now, -1)),
            Group("weekday", "spotify:album:d", playedAtMs: Ms(now, -3)),
            Group("may", "spotify:playlist:e", playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        ];

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
    // The shell clips the content region above the docked player bar (WaveeShell: "its bottom edge IS the player
    // bar's top"), so an in-scroller reserve item could never scroll under the transport — it only parked a permanent
    // dead band at the end of the scroll while the rail annotated that unreachable range. The projection is content
    // only; these facts pin that decision so the spacer refactor does not come back.

    [Fact]
    public void BuildSections_IsContentOnly_TheLastFlatItemIsARealRow()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:a", "music", playedAtMs: Ms(now, 0)),
            Group("b", "spotify:show:b", "podcasts", playedAtMs: Ms(now, -1)),
        ];

        var all = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal(RecentsFlatItemKind.Row, all.Items[^1].Kind);
        Assert.All(all.Items, i => Assert.True(i.Kind is RecentsFlatItemKind.DateHeader or RecentsFlatItemKind.Row));
        // The parallel flat maps stay index-aligned with Items — nothing synthetic pads any of them.
        Assert.Equal(all.Items.Length, all.FlatToRow.Length);
        Assert.Equal(all.Items.Length, all.FlatToDay.Length);
        Assert.Equal(all.Items.Length, all.FlatToMonth.Length);

        // An empty cut renders the empty state instead of the scroller — no items at all.
        var empty = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "nothing-matches"), now, Inv, Localize);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public void BuildSections_HeaderIndicesAndRowMapsAreExact()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: Ms(now, 0)),
            Group("today-b", "spotify:album:b", playedAtMs: Ms(now, 0, hour: 9)),
            Group("yesterday", "spotify:playlist:c", playedAtMs: Ms(now, -1)),
        ];

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        // Header positions are the anchor every consumer shares (the virtual layout, the rail, sticky, the calendar
        // jump); every header is followed by at least one row.
        Assert.Equal([0, 3], sections.HeaderIndices);
        Assert.All(sections.HeaderIndices, i => Assert.True(i < sections.Items.Length - 1));
        Assert.Equal([1, 2, 4], sections.RowToFlat);
        AssertRowDatesAlignWithHeaders(rows, sections, now);
    }

    [Fact]
    public void CountForDay_CountsRowsBetweenHeaders_AndTheLastDayToTheEnd()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: Ms(now, 0)),
            Group("today-b", "spotify:album:b", playedAtMs: Ms(now, 0, hour: 9)),
            Group("yesterday", "spotify:playlist:c", playedAtMs: Ms(now, -1)),
        ];

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal(2, RecentsView.CountForDay(sections, 0));   // an interior day is bounded by the next header
        Assert.Equal(1, RecentsView.CountForDay(sections, 1));   // the last day runs to the end of the flat list
        // Out of range answers 0 rather than throwing — a header index from a superseded shape must not crash a render.
        Assert.Equal(0, RecentsView.CountForDay(sections, 2));
        Assert.Equal(0, RecentsView.CountForDay(sections, -1));
    }

    [Fact]
    public void BuildSections_DoesNotSort_NonContiguousSameDayRowsGetInterleavedHeaders()
    {
        // Product-level newest-first ordering lives on the wire / page. BuildSections walks the display map
        // as given and opens a new header whenever the calendar day changes — it must not silently sort.
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        long today = now.ToUnixTimeMilliseconds();
        long july = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: today),
            Group("july", "spotify:album:b", playedAtMs: july),
            Group("today-b", "spotify:playlist:c", playedAtMs: today),
        ];

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        // Date buckets are Today / 6 Jul / Today — interleaved, not merged. Labels follow those
        // dates (via DateOnly midnight) and must not collapse the two Today runs.
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
        RecentsRow[] rows =
        [
            Group("today", "spotify:playlist:a", "music", playedAtMs: Ms(now, 0)),
            Group("yesterday", "spotify:show:b", "podcasts", playedAtMs: Ms(now, -1)),
            Group("may", "spotify:album:c", "music",
                playedAtMs: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group("july", "spotify:show:d", "podcasts",
                playedAtMs: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        ];

        var music = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "music"), now, Inv, Localize);
        var podcasts = RecentsView.BuildSections(rows, RecentsView.Filter(rows, "podcasts"), now, Inv, Localize);

        Assert.Equal(2, RecentsView.Filter(rows, "music").Length);
        Assert.Equal(2, RecentsView.Filter(rows, "podcasts").Length);
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.Date),
            new DateOnly(2026, 5, 4),
        ], music.HeaderDates);
        Assert.Equal(
        [
            DateOnly.FromDateTime(now.AddDays(-1).Date),
            new DateOnly(2026, 7, 6),
        ], podcasts.HeaderDates);
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
        // Mirrors RecentsPage.CreatePendingSeed: 8 rows stamped minutes apart. Frozen clock so Today is Aug 12.
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var rows = new RecentsRow[8];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new RecentsRow(
                RecentsRowKind.Group, "pending:" + i, "", null, null, null, null, 1,
                now.AddMinutes(-i).ToUnixTimeMilliseconds(), RecentsEntityKind.Unknown,
                RecentsReason.Played);

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
        // 23:30 local at UTC−8: the OLD `DateTimeOffset.UtcNow` seed reads as tomorrow once translated through the
        // grouping pass's LOCAL offset, splitting the skeleton into two day buckets. The pure seed takes the
        // CALLER's own clock (23:30 local, offset intact) so all 8 rows land in a single day bucket.
        var now = new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.FromHours(-8));
        var rows = RecentsView.PendingSeedRows(now);
        Assert.Equal(8, rows.Length);
        Assert.All(rows, r => Assert.Equal(RecentsRowKind.Group, r.Kind));
        Assert.All(rows, r => Assert.Equal(RecentsReason.Played, r.Reason));

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
        RecentsRow[] rows =
        [
            Group("today", "spotify:playlist:a", playedAtMs: before.ToUnixTimeMilliseconds()),
            Group("prior", "spotify:album:b", playedAtMs: before.AddDays(-1).ToUnixTimeMilliseconds()),
        ];

        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), before, Inv, Localize);
        Assert.Equal(
            [SectionLabel(sections.HeaderDates[0], before), SectionLabel(sections.HeaderDates[1], before)],
            sections.HeaderLabels);

        var relabeled = RecentsView.Relabel(sections, after, Inv, Localize);
        // Dates (and every other index/row mapping) are untouched — only the label array is rebuilt, against `after`.
        Assert.Same(sections.HeaderDates, relabeled.HeaderDates);
        Assert.Same(sections.Items, relabeled.Items);
        Assert.Same(sections.HeaderIndices, relabeled.HeaderIndices);
        Assert.Same(sections.RowToFlat, relabeled.RowToFlat);
        Assert.Same(sections.RowToDay, relabeled.RowToDay);
        Assert.Equal(
            [SectionLabel(sections.HeaderDates[0], after), SectionLabel(sections.HeaderDates[1], after)],
            relabeled.HeaderLabels);
        // The label actually advanced a day — that is the whole point of a midnight-rollover relabel.
        Assert.NotEqual(sections.HeaderLabels[0], relabeled.HeaderLabels[0]);
    }

    [Fact]
    public void ZeroTimestamp_IsMinValue_EmptyLabel_AndDoesNotBecomeToday()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(DateOnly.MinValue, RecentsView.DateOf(0, now.Offset));
        Assert.Equal("", RecentsView.DayBucketLabel(0L, now, Inv, Localize));

        RecentsRow[] rows =
        [
            Group("today", "spotify:playlist:a", playedAtMs: now.ToUnixTimeMilliseconds()),
            Group("unknown", "spotify:album:b", playedAtMs: 0),
        ];
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal("", sections.HeaderLabels[1]);
        Assert.Equal([DateOnly.FromDateTime(now.Date), DateOnly.MinValue], sections.HeaderDates);
        Assert.NotEqual(DateOnly.FromDateTime(now.Date), RecentsView.DateOf(rows[1].PlayedAtMs, now.Offset));
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
        Assert.Equal("May 04", RecentsView.DayBucketLabel(
            new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero), now, Inv, Localize));
        Assert.Equal("Dec 15", RecentsView.DayBucketLabel(
            new DateTimeOffset(2025, 12, 15, 12, 0, 0, TimeSpan.Zero), now, Inv, Localize));
    }

    // ── hydration targets ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HydrationUri_PrefersTheContextUri_ThenTheFirstChild_ThenTheRowsOwnUri()
    {
        Assert.Equal("spotify:playlist:1", RecentsView.HydrationUri(Group("a", "spotify:playlist:1")));

        // A single-context group header carries no uri of its own — those rows render from group_metadata's children.
        var headless = new RecentsRow(RecentsRowKind.Group, "b", "", null, null, null, null, 3, 1,
            RecentsEntityKind.Track, RecentsReason.Played, "music", ["", "spotify:track:9", "spotify:track:8"]);
        Assert.Equal("spotify:track:9", RecentsView.HydrationUri(headless));

        var single = new RecentsRow(RecentsRowKind.Single, "c", "spotify:track:7", null, null, null, null, 0, 1,
            RecentsEntityKind.Track);
        Assert.Equal("spotify:track:7", RecentsView.HydrationUri(single));

        var nothing = new RecentsRow(RecentsRowKind.Group, "d", "", null, null, null, null, 0, 1,
            RecentsEntityKind.Unknown);
        Assert.Null(RecentsView.HydrationUri(nothing));
    }

    [Fact]
    public void CollectRange_TakesOnlyTheRealizedWindow_WithAnExclusiveEnd()
    {
        var rows = new RecentsRow[6];
        for (int i = 0; i < rows.Length; i++) rows[i] = Group("id" + i, "spotify:playlist:" + i);
        int[] map = RecentsView.Filter(rows, null);

        var into = new List<string>();
        RecentsView.CollectRange(rows, map, 1, 4, static _ => true, into);
        Assert.Equal(["spotify:playlist:1", "spotify:playlist:2", "spotify:playlist:3"], into);
    }

    [Fact]
    public void CollectRange_RequestsMissesOnly_AndCollapsesTheRepeatedUrisARecentsListIsFullOf()
    {
        // ~1,388 uris repeat across a real recents list: play one playlist on three days and it heads three groups.
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:playlist:1"),
            Group("c", "spotify:album:2"),
            Group("d", "spotify:playlist:3"),
        ];
        int[] map = RecentsView.Filter(rows, null);
        var have = new HashSet<string> { "spotify:album:2" };

        var into = new List<string>();
        int added = RecentsView.CollectRange(rows, map, 0, 4, u => !have.Contains(u), into);
        Assert.Equal(2, added);
        Assert.Equal(["spotify:playlist:1", "spotify:playlist:3"], into);
    }

    [Fact]
    public void CollectRange_ClampsAStaleRange_AndHonoursTheCap()
    {
        RecentsRow[] rows = [Group("a", "spotify:playlist:1"), Group("b", "spotify:playlist:2")];
        int[] map = RecentsView.Filter(rows, null);

        var into = new List<string>();
        // A range from a list that has since shrunk must clamp, never throw.
        Assert.Equal(2, RecentsView.CollectRange(rows, map, -5, 900, static _ => true, into));

        into.Clear();
        Assert.Equal(1, RecentsView.CollectRange(rows, map, 0, 2, static _ => true, into, cap: 1));
        Assert.Equal(["spotify:playlist:1"], into);
    }

    [Fact]
    public void CollectRange_FollowsTheFilterMap_NotTheRawRowOrder()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:show:2", "podcasts"),
            Group("c", "spotify:album:3"),
        ];
        int[] podcasts = RecentsView.Filter(rows, "podcasts");
        var into = new List<string>();
        RecentsView.CollectRange(rows, podcasts, 0, podcasts.Length, static _ => true, into);
        Assert.Equal(["spotify:show:2"], into);
    }

    [Fact]
    public void CollectChildUris_UsesAvailableDecodedChildren_NotTheAuthoritativeChildCount()
    {
        var row = Group("a", null, childCount: 11,
            children: ["spotify:track:1", "", "spotify:track:1", "spotify:track:2"]);
        var into = new List<string>();
        Assert.Equal(2, RecentsView.CollectChildUris(row, static _ => true, into));
        Assert.Equal(["spotify:track:1", "spotify:track:2"], into);
    }

    // ── the expand drawer: members first, the header's own list as the fallback, nothing invented ───────────────────
    static RecentsMember M(string id, string uri, long at) => new(id, uri, at);

    [Fact]
    public void CollectChildUris_AsksForTheMembers_WhenTheWireCarriesThem_NotTheTruncatedChildUris()
    {
        // The header's child_uri list is the server-truncated one (3 of 11); the members are the full run.
        var row = Group("a", "spotify:playlist:p", childCount: 4, children: ["spotify:track:1"]) with
        {
            Members = [M("m1", "spotify:track:4", 4), M("m2", "", 3), M("m3", "spotify:track:4", 2), M("m4", "spotify:track:3", 1)],
        };
        var into = new List<string>();
        Assert.Equal(2, RecentsView.CollectChildUris(row, static _ => true, into));
        Assert.Equal(["spotify:track:4", "spotify:track:3"], into);   // wire order, empties and repeats collapsed

        into.Clear();
        Assert.Equal(1, RecentsView.CollectChildUris(row, static _ => true, into, cap: 1));
        Assert.Equal(["spotify:track:4"], into);
    }

    [Fact]
    public void CanExpand_NeedsAPlayCount_AndSomethingToList_FromEitherSource()
    {
        Assert.True(RecentsView.CanExpand(Group("a", "spotify:playlist:p", childCount: 3) with
            { Members = [M("m1", "spotify:track:1", 1)] }));
        Assert.True(RecentsView.CanExpand(Group("b", "spotify:playlist:p", childCount: 3, children: ["spotify:track:1"])));
        // "Played 3" with nothing listable: the artist / "Henry Moodie Mix" rows — no chevron, still the count.
        Assert.False(RecentsView.CanExpand(Group("c", "spotify:artist:x", childCount: 3)));
        Assert.False(RecentsView.CanExpand(Group("d", "spotify:artist:x", childCount: 3, children: [""]) with
            { Members = [M("m1", "", 1)] }));
        // A count of zero never expands, however many uris the header names.
        Assert.False(RecentsView.CanExpand(Group("e", "spotify:playlist:p", childCount: 0, children: ["spotify:track:1"]) with
            { Members = [M("m1", "spotify:track:1", 1)] }));
    }

    [Fact]
    public void MissingMembers_IsTheCountWithoutAList_TheCaseThePageLogsOnce()
    {
        Assert.True(RecentsView.MissingMembers(Group("a", "spotify:artist:x", childCount: 3)));
        Assert.False(RecentsView.MissingMembers(Group("b", "spotify:playlist:p", childCount: 3, children: ["spotify:track:1"])));
        Assert.False(RecentsView.MissingMembers(Group("c", "spotify:playlist:p", childCount: 0)));   // nothing to list, nothing claimed
    }

    [Fact]
    public void DrawerEntries_ListsTheMembersAsSent_ElseTheChildUrisWithNoInstant()
    {
        RecentsMember[] members = [M("m1", "spotify:track:2", 20), M("m2", "", 0), M("m3", "spotify:track:1", 10)];
        var withMembers = Group("a", "spotify:playlist:p", childCount: 3, children: ["spotify:track:9"]) with { Members = members };
        Assert.Same(members, RecentsView.DrawerEntries(withMembers));       // handed back in place — empties and all

        var folded = Group("b", "spotify:playlist:p", childCount: 2, children: ["spotify:track:1", "spotify:track:2"]);
        var entries = RecentsView.DrawerEntries(folded);
        Assert.Equal(["spotify:track:1", "spotify:track:2"], entries.Select(e => e.Uri));
        Assert.All(entries, e => Assert.Equal(0L, e.PlayedAtMs));            // no instant → PlayedAt renders ""
        Assert.All(entries, e => Assert.Equal("", e.ItemId));

        Assert.Empty(RecentsView.DrawerEntries(Group("c", "spotify:artist:x", childCount: 3)));
    }

    [Fact]
    public void MetaFor_BranchesPlayedSavedAndUnknown_WithSavedAtLeastOne()
    {
        var played = Group("p", "spotify:playlist:p", childCount: 3);
        var saved = played with { ItemId = "s", Reason = RecentsReason.Saved, ChildCount = 0 };
        var unknown = played with { ItemId = "u", Reason = RecentsReason.Unknown };

        Assert.Equal(new RecentsMeta(RecentsMetaKind.PlayedCount, 3), RecentsView.MetaFor(played));
        Assert.Equal(new RecentsMeta(RecentsMetaKind.SavedCount, 1), RecentsView.MetaFor(saved));
        Assert.Equal(new RecentsMeta(RecentsMetaKind.PlayedAt, 0), RecentsView.MetaFor(unknown));
    }

    [Fact]
    public void DayDensity_CountsPlayedOnly_UsesLogLevels_AndKeepsFirstTopItemOnATie()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var first = Group("first", "spotify:playlist:first", childCount: 3,
            playedAtMs: now.ToUnixTimeMilliseconds());
        var tied = Group("tied", "spotify:playlist:tied", childCount: 3,
            playedAtMs: now.AddHours(-1).ToUnixTimeMilliseconds());
        var saved = Group("saved", "spotify:playlist:saved", childCount: 99,
            playedAtMs: now.AddHours(-2).ToUnixTimeMilliseconds()) with { Reason = RecentsReason.Saved };
        var single = new RecentsRow(RecentsRowKind.Single, "single", "spotify:track:1", null, null, null, null,
            0, now.AddDays(-1).ToUnixTimeMilliseconds(), RecentsEntityKind.Track, RecentsReason.Played, "music");
        RecentsRow[] rows = [first, tied, saved, single];

        var calendar = RecentsView.DayDensity(rows, RecentsView.Filter(rows, null), now, Inv);
        var month = Assert.Single(calendar.Months);
        var today = month.Days[now.Day - 1];
        var yesterday = month.Days[now.Day - 2];
        Assert.Equal(6, today.PlayCount);
        Assert.Equal(5, today.DensityLevel);
        Assert.Equal("first", today.TopItem?.ItemId);
        Assert.Equal(1, yesterday.PlayCount);
        Assert.InRange(yesterday.DensityLevel, 1, 4);
        Assert.Equal(7, month.TotalPlays);
        Assert.Equal(DateOnly.FromDateTime(now.Date), month.BusiestDay);
    }

    // ── calendar grid geometry (what the overview's month cards are actually laid out from) ───────────────────────────

    /// <summary>A calendar spanning May 2026 back to February 2026 — the four consecutive months that between them
    /// produce every week-row count a 7-column grid can need (4, 5 and 6), so one fixture pins all three.</summary>
    static RecentsCalendar SpringCalendar(CultureInfo culture)
    {
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("may", "spotify:playlist:may", playedAtMs: now.ToUnixTimeMilliseconds()),
            Group("feb", "spotify:album:feb",
                playedAtMs: new DateTimeOffset(2026, 2, 3, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        ];
        return RecentsView.DayDensity(rows, RecentsView.Filter(rows, null), now, culture);
    }

    /// <summary>One month by identity rather than by ordinal — the months are newest-first, and a test that indexed
    /// into them would break for the wrong reason the day the covered range changes.</summary>
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

        // February 2026 starts ON a Sunday and has 28 days: four rows with no leading and no trailing blank at all.
        // The old card drew six regardless, and the two dead rows are what read as a clipped card.
        var february = MonthOf(calendar, 2026, 2);
        Assert.Equal(0, february.FirstDayOffset);
        Assert.Equal(4, february.WeekCount);

        // April starts on a Wednesday (offset 3) over 30 days → 33 cells → five rows.
        var april = MonthOf(calendar, 2026, 4);
        Assert.Equal(3, april.FirstDayOffset);
        Assert.Equal(5, april.WeekCount);

        // May starts on a Friday (offset 5) over 31 days → 36 cells → the six-row worst case the estimate is sized on.
        var may = MonthOf(calendar, 2026, 5);
        Assert.Equal(5, may.FirstDayOffset);
        Assert.Equal(6, may.WeekCount);
    }

    /// <summary>A Monday-first culture — what nl-NL (and most of Europe) is. Built by CLONING the invariant culture and
    /// moving its first weekday rather than by naming a real one: the whole repo builds with
    /// <c>InvariantGlobalization</c> (src/Directory.Build.props), so <c>GetCultureInfo("nl-NL")</c> throws here. The
    /// rule under test is the ROTATION, and this exercises exactly the <c>FirstDayOfWeek</c> value nl-NL supplies.</summary>
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
        Assert.Equal(DayOfWeek.Monday, dutch.DateTimeFormat.FirstDayOfWeek);   // the assumption this test is about
        var calendar = SpringCalendar(dutch);

        // The SAME 28 days the invariant grid fits in four rows: a Sunday 1st is the LAST column of a Monday-first
        // week, so February pushes one day into a fifth row.
        var february = MonthOf(calendar, 2026, 2);
        Assert.Equal(6, february.FirstDayOffset);
        Assert.Equal(5, february.WeekCount);

        // And the rotation cuts the other way too — May's six invariant rows become five.
        var may = MonthOf(calendar, 2026, 5);
        Assert.Equal(4, may.FirstDayOffset);
        Assert.Equal(5, may.WeekCount);
    }

    [Fact]
    public void MaxWeeks_TakesTheTallestMonth_AndFloorsAtOneForAnEmptyCalendar()
    {
        // March and May are the six-row months of the invariant fixture; under nl-NL March alone still is.
        Assert.Equal(6, RecentsView.MaxWeeks(SpringCalendar(Inv)));
        Assert.Equal(6, RecentsView.MaxWeeks(SpringCalendar(MondayFirst())));
        // No months at all (the pre-snapshot state) must still yield a POSITIVE row height for the grid estimate.
        Assert.Equal(1, RecentsView.MaxWeeks(new RecentsCalendar([], 0)));
    }

    // ── shared-element eligibility ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstOccurrence_TagsOnlyTheMostRecentRowOfEachUri_SoNoTwoLiveNodesShareAMorphId()
    {
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1"),
            Group("b", "spotify:album:2"),
            Group("c", "spotify:playlist:1"),   // same entity, played again later — must NOT be tagged
            Group("d", null, children: null),   // names nothing → never tagged
        ];
        Assert.Equal([true, true, false, false], RecentsView.FirstOccurrence(rows));
    }

    // ── played-at formatting (culture tables only — the page authors no copy) ─────────────────────────────────────────

    [Fact]
    public void PlayedAt_TodayIsATime_ThisWeekIsAWeekday_ThisYearIsAnAbbreviatedMonthDay_OlderIsAShortDate()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);

        Assert.Equal(now.AddHours(-3).ToString("t", Inv),
            RecentsView.PlayedAt(now.AddHours(-3), now, Inv));

        Assert.Equal(Inv.DateTimeFormat.GetAbbreviatedDayName(now.AddDays(-3).DayOfWeek),
            RecentsView.PlayedAt(now.AddDays(-3), now, Inv));

        // Invariant MonthDayPattern is "MMMM dd"; the abbreviated cut keeps the culture's ORDER and narrows the month.
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
        long Ms(int month, int day) => new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1", playedAtMs: Ms(8, 1)),
            Group("b", "spotify:album:2", playedAtMs: Ms(6, 3)),
        ];
        Assert.Equal("2 · Jun 03 – Aug 01", RecentsView.Summary(rows, now, Inv));
    }

    [Fact]
    public void Summary_OfAnEmptyList_IsEmpty_AndATimestampLessListStillStatesItsCount()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);
        Assert.Equal("", RecentsView.Summary(Array.Empty<RecentsRow>(), now, Inv));
        Assert.Equal("1", RecentsView.Summary([Group("a", "spotify:playlist:1", playedAtMs: 0)], now, Inv));
    }

    [Fact]
    public void Summary_TodayEndpoint_RendersDayWord()
    {
        // Regression for the "May 14 – 18:26" bug: the newest endpoint is TODAY and must read as the day word, never
        // the clock time a PlayedAt-style formatter would render it as.
        var now = new DateTimeOffset(2026, 8, 12, 18, 26, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1",
                playedAtMs: new DateTimeOffset(2026, 5, 14, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            Group("b", "spotify:album:2", playedAtMs: now.ToUnixTimeMilliseconds()),
        ];
        Assert.Equal("2 · May 14 – Today", RecentsView.Summary(rows, now, Inv, localize: Localize));
    }

    [Fact]
    public void Summary_SingleDay_Collapses()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 26, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1", playedAtMs: now.AddHours(-3).ToUnixTimeMilliseconds()),
            Group("b", "spotify:album:2", playedAtMs: now.AddHours(-1).ToUnixTimeMilliseconds()),
        ];
        Assert.Equal("2 · Today", RecentsView.Summary(rows, now, Inv, localize: Localize));
    }

    [Fact]
    public void Summary_GroupedFrom_AppendsTheSumOfChildCounts_WhenItExceedsTheRowCount()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1", childCount: 5, playedAtMs: now.ToUnixTimeMilliseconds()),
            Group("b", "spotify:album:2", childCount: 3, playedAtMs: now.ToUnixTimeMilliseconds()),
        ];
        string summary = RecentsView.Summary(rows, now, Inv,
            groupedPhrase: static n => "grouped from " + n + " plays", localize: Localize);
        Assert.Equal("2 · Today · grouped from 8 plays", summary);
    }

    [Fact]
    public void Summary_GroupedFrom_OmittedWhenEveryRowContributesExactlyOnePlay()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("a", "spotify:playlist:1", childCount: 1, playedAtMs: now.ToUnixTimeMilliseconds()),
            Group("b", "spotify:album:2", childCount: 0, playedAtMs: now.ToUnixTimeMilliseconds()),   // Max(1, 0)
        ];
        string summary = RecentsView.Summary(rows, now, Inv,
            groupedPhrase: static n => "grouped from " + n + " plays", localize: Localize);
        Assert.Equal("2 · Today", summary);
    }

    [Fact]
    public void Summary_GroupedFrom_NeverInvokedOnAnEmptyList()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal("", RecentsView.Summary(Array.Empty<RecentsRow>(), now, Inv,
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
        // The store parroting the bare id back as "the name" must hide, not render base62.
        Assert.Null(RecentsView.OwnerSubtitle(RawOwnerId, RawOwnerId, null));
        Assert.Null(RecentsView.OwnerSubtitle(UserProfileIds.Prefix + RawOwnerId, RawOwnerId, null));
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

    // ── viewport-derived accent (the pure day-bucket selector) ───────────────────────────────────────────────────────────

    [Fact]
    public void AccentSourceRow_ReturnsTheFirstRowOfTheStickyBucket()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        RecentsRow[] rows =
        [
            Group("today-a", "spotify:playlist:a", playedAtMs: Ms(now, 0)),
            Group("today-b", "spotify:album:b", playedAtMs: Ms(now, 0, hour: 9)),
            Group("yesterday", "spotify:playlist:c", playedAtMs: Ms(now, -1)),
        ];
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);

        int todayHeader = sections.HeaderIndices[0];
        Assert.Equal(0, RecentsView.AccentSourceRow(sections, rows, todayHeader));
        // Starting from inside the bucket (its own first row) finds the same row.
        Assert.Equal(0, RecentsView.AccentSourceRow(sections, rows, todayHeader + 1));

        int yesterdayHeader = sections.HeaderIndices[1];
        Assert.Equal(2, RecentsView.AccentSourceRow(sections, rows, yesterdayHeader));
    }

    [Fact]
    public void AccentSourceRow_BoundsTheForwardWalkToEightItems()
    {
        // A bucket whose first Row-kind item sits at offset 8 — one step beyond the walk's bound.
        RecentsFlatItem[] items = new RecentsFlatItem[10];
        for (int i = 0; i < 8; i++) items[i] = new RecentsFlatItem(RecentsFlatItemKind.DateHeader, -1, 0, 0);
        items[8] = new RecentsFlatItem(RecentsFlatItemKind.Row, 5, 0, 0);
        items[9] = new RecentsFlatItem(RecentsFlatItemKind.Row, 6, 0, 0);
        var sections = new RecentsSections(items, [0], [""], [DateOnly.MinValue],
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
        var rows = new RecentsRow[7];
        for (int i = 0; i < rows.Length; i++) rows[i] = Group("r" + i, "spotify:playlist:" + i);

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
        var sections = new RecentsSections(items, [0, 1], ["", ""], [DateOnly.MinValue, DateOnly.MinValue],
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
        RecentsRow[] rows = [Group("r", "spotify:playlist:1")];

        Assert.Equal(-1, RecentsView.AccentSourceRow(sections, rows, 0));   // day 0's own bucket has no Row
        Assert.Equal(0, RecentsView.AccentSourceRow(sections, rows, 1));    // day 1's header finds its own row
    }

    [Fact]
    public void AccentSourceRow_OutOfRangeOrEmpty_FallsBackToMinusOne()
    {
        RecentsRow[] rows = [Group("r", "spotify:playlist:1")];
        var empty = new RecentsSections(Array.Empty<RecentsFlatItem>(), Array.Empty<int>(), Array.Empty<string>(),
            Array.Empty<DateOnly>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(),
            Array.Empty<int>());
        Assert.Equal(-1, RecentsView.AccentSourceRow(empty, rows, 0));
        Assert.Equal(-1, RecentsView.AccentSourceRow(empty, rows, -1));

        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), now, Inv, Localize);
        Assert.Equal(-1, RecentsView.AccentSourceRow(sections, rows, 999));
    }
}
