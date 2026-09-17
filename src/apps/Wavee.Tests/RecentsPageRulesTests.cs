// ── Wavee.Tests/RecentsPageRulesTests.cs — the RecentsPage decisions ch 16 §8 marks "pure but untested" ─────────────────
//
// Role: TEST
// Owner: P
// Wave: 5
// Spec: ch 16 §8 rows 20-24 (month card height · sticky metrics + push · zoom anchor maps · density fill alpha ·
//   content-type pool id), §9.1 #1/#2/#11/#13 · contract §8 (the mount demand)
//
// NEW in Wave 5: 0.2.9 kept these inside `RecentsPage` (a component), so nothing pinned them. 0.3 lifts them into
// `RecentsLayout` (Entities/Recents.cs) and every fact here drives the production rule. `RecentsLayoutTests` is pure;
// `RecentsSnapshotCommitTests` stages a real answer through the drain (it writes `Entities` statics, so it joins
// EntitiesCollection) to pin the one payload change Wave 5 made: every edge carries its target's KIND.

using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Xunit;
using static Wavee.Tests.RecentsViewTests;

namespace Wavee.Tests;

public class RecentsLayoutTests
{
    static readonly DateTimeOffset Aug12 = new(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);

    static bool Near(float expected, float actual) => MathF.Abs(expected - actual) < 1e-4f;

    /// <summary>Today ×2 then yesterday ×1: flat [H0 · R · R · H1 · R], extents 48 · 64 · 64 · 48 · 64, so the offsets
    /// are 0 · 48 · 112 · 176 · 224 once the measured layout is primed.</summary>
    static (RecentsSections Sections, GroupedListVirtualLayout Layout) TwoDays()
    {
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(Aug12, 0)),
            Group(Al(2), playedAtMs: Ms(Aug12, 0, hour: 9)),
            Group(Pl(3), playedAtMs: Ms(Aug12, -1)));
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), Aug12, Inv, Localize);
        var layout = new GroupedListVirtualLayout(sections.HeaderIndices, RecentsLayout.DateHeaderHeight, RecentsLayout.RowHeight);
        layout.ContentExtent(sections.Items.Length, 0f);
        return (sections, layout);
    }

    /// <summary>Aug 12, Aug 11 and Jun 3 under an Aug 12 clock: months [Aug, Jul, Jun]; flat [H · R · H · R · H · R].</summary>
    static (RecentsSections Sections, RecentsCalendar Calendar) Summer()
    {
        var rows = Snap(
            Group(Pl(1), playedAtMs: Ms(Aug12, 0)),
            Group(Pl(2), playedAtMs: Ms(Aug12, -1)),
            Group(Al(3), playedAtMs: new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        var map = RecentsView.Filter(rows, null);
        return (RecentsView.BuildSections(rows, map, Aug12, Inv, Localize), RecentsView.DayDensity(rows, map, Aug12, Inv));
    }

    // ── the month card (§8 row 20) ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MonthCardHeight_IsFiftySixPlusThirtySixPerWeekRow_AndTheCardIsSevenCellsAndSixGuttersWide()
    {
        Assert.Equal(200f, RecentsLayout.MonthCardHeight(4));
        Assert.Equal(236f, RecentsLayout.MonthCardHeight(5));
        Assert.Equal(272f, RecentsLayout.MonthCardHeight(6));
        // The grid's min cell IS the card width, so a wider window adds a column instead of stretching a heatmap cell.
        Assert.Equal(290f, RecentsLayout.CalGridW);
        // The estimate the overview grid is seeded with is the TALLEST month the calendar actually carries.
        var (_, calendar) = Summer();
        Assert.Equal(RecentsLayout.MonthCardHeight(RecentsView.MaxWeeks(calendar)),
            RecentsLayout.MonthCardHeight(calendar.Months.Max(m => m.WeekCount)));
    }

    // ── the pinned band (§8 row 21) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StickyMetrics_PinsTheHeaderThatOwnsTheOffset_AndPushesOnlyWhileTheNextHeaderIsInsideTheBand()
    {
        var (sections, layout) = TwoDays();

        RecentsLayout.StickyMetrics(sections, layout, 0f, 800f, out int header, out float push);
        Assert.Equal(0, header);
        Assert.Equal(0f, push);                                  // Yesterday's header is 128 below the band: no push

        RecentsLayout.StickyMetrics(sections, layout, 130f, 800f, out header, out push);
        Assert.Equal(0, header);
        Assert.Equal(-2f, push);                                 // 176 − 130 − 48: the incoming header nudges Today up

        RecentsLayout.StickyMetrics(sections, layout, 150f, 800f, out header, out push);
        Assert.Equal(0, header);
        Assert.Equal(-22f, push);

        RecentsLayout.StickyMetrics(sections, layout, 177f, 800f, out header, out push);
        Assert.Equal(3, header);                                 // Yesterday now owns the band …
        Assert.Equal(0f, push);                                  // … and, the last day, is never pushed
    }

    [Fact]
    public void StickyMetrics_OfAProjectionWithNoHeaders_PinsNothing()
    {
        var empty = RecentsView.BuildSections(RecentsSnapshot.Empty, [], Aug12, Inv, Localize);
        var layout = new GroupedListVirtualLayout(empty.HeaderIndices, RecentsLayout.DateHeaderHeight, RecentsLayout.RowHeight);
        RecentsLayout.StickyMetrics(empty, layout, 300f, 800f, out int header, out float push);
        Assert.Equal(-1, header);
        Assert.Equal(0f, push);
    }

    [Fact]
    public void QuantizePush_SnapsToTwoDip_SoAScrollFrameNeverWritesASubPixelPush()
    {
        Assert.Equal(-22f, RecentsLayout.QuantizePush(-22f));
        Assert.Equal(-22f, RecentsLayout.QuantizePush(-21.2f));
        Assert.Equal(-48f, RecentsLayout.QuantizePush(-47.3f));
        Assert.Equal(0f, RecentsLayout.QuantizePush(-0.9f) + 0f);           // −0 + 0 = +0: a sub-quantum push is no push
        Assert.Equal(2f, RecentsLayout.PushQuantum);
    }

    [Fact]
    public void ProjectSticky_ChangesWithTheHeaderTheMeasuredVersionOrTheQuantizedPush_AndNothingElse()
    {
        long rest = RecentsLayout.ProjectSticky(0, 0, 0f);
        Assert.NotEqual(rest, RecentsLayout.ProjectSticky(3, 0, 0f));       // a new pinned day
        Assert.NotEqual(rest, RecentsLayout.ProjectSticky(-1, 0, 0f));      // nothing pinned is its own key
        Assert.NotEqual(rest, RecentsLayout.ProjectSticky(0, 1, 0f));       // a real extent delta (a drawer opened)
        Assert.NotEqual(rest, RecentsLayout.ProjectSticky(0, 0, -22f));     // the push moved a quantum
        // A sub-quantum wobble projects the SAME key, so the observer's action never runs for it (§9.1 #2).
        Assert.Equal(RecentsLayout.ProjectSticky(0, 0, -22f), RecentsLayout.ProjectSticky(0, 0, -21.2f));
        Assert.Equal(rest, RecentsLayout.ProjectSticky(0, 0, 0.4f));
    }

    // ── the semantic-zoom anchors (§8 row 22) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HeaderFlatFor_AndMonthFor_AnswerMinusOneForADayOrMonthTheViewDoesNotCarry()
    {
        var (sections, calendar) = Summer();
        Assert.Equal(0, RecentsLayout.HeaderFlatFor(sections, new DateOnly(2026, 8, 12)));
        Assert.Equal(2, RecentsLayout.HeaderFlatFor(sections, new DateOnly(2026, 8, 11)));
        Assert.Equal(4, RecentsLayout.HeaderFlatFor(sections, new DateOnly(2026, 6, 3)));
        Assert.Equal(-1, RecentsLayout.HeaderFlatFor(sections, new DateOnly(2026, 7, 1)));   // no dead "Jump to"

        Assert.Equal(["2026-8", "2026-7", "2026-6"], calendar.Months.Select(m => m.Year + "-" + m.Month));
        Assert.Equal(1, RecentsLayout.MonthFor(calendar, new DateOnly(2026, 7, 15)));         // an empty month still exists
        Assert.Equal(-1, RecentsLayout.MonthFor(calendar, new DateOnly(2025, 1, 1)));
    }

    [Fact]
    public void MapInToOut_TakesAFlatItemToTheMonthOfItsDay()
    {
        var (sections, calendar) = Summer();
        Assert.Equal(0, RecentsLayout.MapInToOut(sections, calendar, 0));    // Today's header
        Assert.Equal(0, RecentsLayout.MapInToOut(sections, calendar, 3));    // Yesterday's row — still August
        Assert.Equal(2, RecentsLayout.MapInToOut(sections, calendar, 5));    // June's row
        Assert.Equal(-1, RecentsLayout.MapInToOut(sections, calendar, 99));
        Assert.Equal(-1, RecentsLayout.MapInToOut(sections, calendar, -1));
    }

    [Fact]
    public void MapOutToIn_PrefersTheExactSelectedDay_ThenTheMonthsFirstHeader_ElseMinusOne()
    {
        var (sections, calendar) = Summer();
        // The hovered/clicked day has a header and sits in the month being zoomed into: land ON it.
        Assert.Equal(2, RecentsLayout.MapOutToIn(sections, calendar, new DateOnly(2026, 8, 11), 0));
        // The selection is in a DIFFERENT month than the one zoomed into: that month's first header wins.
        Assert.Equal(4, RecentsLayout.MapOutToIn(sections, calendar, new DateOnly(2026, 8, 11), 2));
        // A selected day with no rows falls back to its month's first header.
        Assert.Equal(0, RecentsLayout.MapOutToIn(sections, calendar, new DateOnly(2026, 8, 20), 0));
        // A month with no rows at all, or one outside the calendar, answers −1 (the zoom keeps its own anchor).
        Assert.Equal(-1, RecentsLayout.MapOutToIn(sections, calendar, new DateOnly(2026, 8, 20), 1));
        Assert.Equal(-1, RecentsLayout.MapOutToIn(sections, calendar, new DateOnly(2026, 8, 11), 99));
    }

    [Fact]
    public void CalendarDay_IsTheDayOfItsMonth_OrNullOutsideTheCalendar()
    {
        var (_, calendar) = Summer();
        var today = RecentsLayout.CalendarDay(calendar, new DateOnly(2026, 8, 12));
        Assert.NotNull(today);
        Assert.Equal(1, today.PlayCount);
        Assert.Equal(new DateOnly(2026, 8, 12), today.Date);
        var july = RecentsLayout.CalendarDay(calendar, new DateOnly(2026, 7, 4));
        Assert.NotNull(july);
        Assert.Equal(0, july.PlayCount);
        Assert.Equal(0, july.DensityLevel);
        Assert.Null(RecentsLayout.CalendarDay(calendar, new DateOnly(2025, 1, 1)));
    }

    // ── recycling (§8 row 24, §9.1 #13) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UsesTrackArm_OnlyForASinglePlayOfATrackOrAnEpisode()
    {
        var rows = Snap(
            Play(Tr(1)),
            Play(new EntityRef(EntityKind.Episode, 2)),
            Play(Sh(3)),                              // the seed's single show play renders as a card
            Play(Ar(4)),
            Group(Tr(5), childCount: 2));
        Assert.True(RecentsLayout.UsesTrackArm(rows, 0));
        Assert.True(RecentsLayout.UsesTrackArm(rows, 1));
        Assert.False(RecentsLayout.UsesTrackArm(rows, 2));
        Assert.False(RecentsLayout.UsesTrackArm(rows, 3));
        Assert.False(RecentsLayout.UsesTrackArm(rows, 4));
        Assert.False(RecentsLayout.UsesTrackArm(rows, 5));
    }

    [Fact]
    public void ContentTypeOf_HeadersShareOnePool_AndTheTrackArmAndTheCardNeverShareOne()
    {
        var rows = Snap(Play(Tr(1), Ms(Aug12, 0)), Group(Al(2), playedAtMs: Ms(Aug12, 0, hour: 9)));
        var sections = RecentsView.BuildSections(rows, RecentsView.Filter(rows, null), Aug12, Inv, Localize);
        Assert.Equal(RecentsFlatItemKind.DateHeader, sections.Items[0].Kind);

        int header = RecentsLayout.ContentTypeOf(sections, rows, 0);
        int single = RecentsLayout.ContentTypeOf(sections, rows, 1);
        int card = RecentsLayout.ContentTypeOf(sections, rows, 2);
        Assert.Equal(0, header);
        Assert.Equal(1 + (int)RecentsRowKind.Single, single);
        Assert.Equal(1 + (int)RecentsRowKind.Group, card);
        Assert.Equal(3, new[] { header, single, card }.Distinct().Count());
        Assert.Equal(0, RecentsLayout.ContentTypeOf(sections, rows, 42));   // a stale index recycles as a header
    }

    // ── the heat (§8 row 23, §4.3) ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DensityAlpha_RampsFromTheSubtleAlphaToTheInksAlpha_InFifths_AndLevelZeroIsTransparent()
    {
        const float subtle = 0.141f;
        Assert.Equal(0f, RecentsLayout.DensityAlpha(0, subtle, 1f));
        Assert.True(Near(0.3128f, RecentsLayout.DensityAlpha(1, subtle, 1f)));
        Assert.True(Near(0.4846f, RecentsLayout.DensityAlpha(2, subtle, 1f)));
        Assert.True(Near(0.6564f, RecentsLayout.DensityAlpha(3, subtle, 1f)));
        Assert.True(Near(0.8282f, RecentsLayout.DensityAlpha(4, subtle, 1f)));
        Assert.True(Near(1f, RecentsLayout.DensityAlpha(5, subtle, 1f)));
        Assert.True(Near(1f, RecentsLayout.DensityAlpha(9, subtle, 1f)));          // clamped at the busiest rung
        Assert.True(Near(0.8f, RecentsLayout.DensityAlpha(5, subtle, 0.8f)));      // the top rung IS the ink's alpha
        Assert.Equal(0f, RecentsLayout.DensityAlpha(-1, subtle, 1f));
    }

    [Fact]
    public void DensityFill_PaintsTheAccentInkAtTheRungsAlpha_AndLevelZeroPaintsNothing()
    {
        var ink = new ColorF(0.2f, 0.4f, 0.6f, 1f);
        Assert.Equal(ColorF.Transparent, RecentsLayout.DensityFill(0, ink, 0.141f));
        var three = RecentsLayout.DensityFill(3, ink, 0.141f);
        Assert.Equal((ink.R, ink.G, ink.B), (three.R, three.G, three.B));          // the hue is the ink's, never re-derived
        Assert.True(Near(RecentsLayout.DensityAlpha(3, 0.141f, ink.A), three.A));
        Assert.True(RecentsLayout.DensityFill(1, ink, 0.141f).A < RecentsLayout.DensityFill(2, ink, 0.141f).A);
    }

    // ── the clock ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RolloverDelayMs_IsTheNextLocalMidnightPlusASecond_AndNeverUnderASecond()
    {
        Assert.Equal(21_601_000f, RecentsLayout.RolloverDelayMs(Aug12));                                        // 18:00 → 6 h + 1 s
        Assert.Equal(1_500f, RecentsLayout.RolloverDelayMs(new DateTimeOffset(2026, 8, 12, 23, 59, 59, 500, TimeSpan.Zero)));
        Assert.Equal(3_601_000f, RecentsLayout.RolloverDelayMs(new DateTimeOffset(2026, 8, 12, 23, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal(86_401_000f, RecentsLayout.RolloverDelayMs(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(RecentsLayout.RolloverDelayMs(new DateTimeOffset(2026, 8, 12, 23, 59, 59, 999, TimeSpan.Zero)) >= 1000f);
    }

    // ── the mount demand (contract §8; replaces 0.2.9's CollectRange / CollectChildUris) ─────────────────────────────

    [Fact]
    public void DemandSlots_TakesEachRowsHydrationTargetOnce_PlusASavedRowsFirstTwoNamedMembers()
    {
        var rows = Snap(
            Group(Pl(5), childCount: 2, members: [(Tr(1), 2), (Tr(2), 1)]),              // a PLAYED group: members wait for the drawer
            Group(Pl(5)),                                                                   // the same playlist again: asked once
            Group(Pl(6), childCount: 4, members: [(Nothing, 4), (Tr(3), 3), (Tr(4), 2), (Tr(5), 1)], reason: RecentsReason.Saved),
            Group(Nothing, childCount: 1, members: [(Tr(7), 1)]),                           // a headless header renders from its member
            Play(new EntityRef(EntityKind.Episode, 8)));

        var into = new List<int> { 42 };
        var seen = new HashSet<int>();
        RecentsLayout.DemandSlots(rows, EntityKind.Track, into, seen);
        Assert.Equal([42, 3, 4, 7], into);                    // appended, never cleared; the stack's two covers, then Tr7

        into.Clear();
        RecentsLayout.DemandSlots(rows, EntityKind.Playlist, into, seen);   // `seen` is scratch: reset per call
        Assert.Equal([5, 6], into);

        into.Clear();
        RecentsLayout.DemandSlots(rows, EntityKind.Episode, into, seen);
        Assert.Equal([8], into);

        into.Clear();
        RecentsLayout.DemandSlots(rows, EntityKind.Artist, into, seen);
        Assert.Empty(into);
    }

    // ── the snapshot (§9.1 #1) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecentsSnapshot_SameAs_IsValueEquality_SoANoOpPublicationRebuildsNothing()
    {
        var a = Snap(Group(Pl(1), childCount: 2, members: [(Tr(1), 2), (Tr(2), 1)]), Play(Tr(3)));
        var b = Snap(Group(Pl(1), childCount: 2, members: [(Tr(1), 2), (Tr(2), 1)]), Play(Tr(3)));
        var memberMoved = Snap(Group(Pl(1), childCount: 2, members: [(Tr(1), 2), (Tr(2), 0)]), Play(Tr(3)));
        var targetMoved = Snap(Group(Pl(1), childCount: 2, members: [(Tr(1), 2), (Tr(2), 1)]), Play(Tr(4)));

        Assert.NotSame(a, b);
        Assert.True(a.SameAs(b));
        Assert.True(a.SameAs(a));
        Assert.False(a.SameAs(memberMoved));
        Assert.False(a.SameAs(targetMoved));
        Assert.False(a.SameAs(RecentsSnapshot.Empty));
        Assert.True(RecentsSnapshot.Empty.SameAs(Snap()));
    }

    [Fact]
    public void RecentsSnapshot_RefusesUnparallelTargets_AndClampsAMemberRangeThatRunsPastTheRun()
    {
        var row = new RecentsEdge(new StringId(1), T0, 2, (byte)RecentsReason.Played, 0, (byte)RecentsRowKind.Group, 0, 2);
        Assert.Throws<ArgumentException>(() => new RecentsSnapshot([row], [], [], []));
        Assert.Throws<ArgumentException>(() => new RecentsSnapshot([row], [Pl(1)], [new RecentsMemberEdge(new StringId(9), T0)], []));

        // A range from a superseded publication (2 members claimed, 1 present) answers nothing rather than throwing.
        var snapshot = new RecentsSnapshot([row], [Pl(1)], [new RecentsMemberEdge(new StringId(9), T0)], [Tr(1)]);
        Assert.True(snapshot.MembersOf(0).IsEmpty);
        Assert.True(snapshot.MemberTargetsOf(0).IsEmpty);
        Assert.True(snapshot.MembersOf(5).IsEmpty);
        Assert.False(RecentsView.CanExpand(snapshot, 0));
    }
}

/// <summary>The drain half: a staged answer commits each target's kind onto its edge, and a snapshot copied from the
/// live relation reads the Liked collection as a row-less <c>(Collection, 0)</c> that still names a destination.</summary>
[Collection(EntitiesCollection.Name)]
public class RecentsSnapshotCommitTests
{
    static EntityId Gid(EntityKind kind, byte seed)
    {
        Span<byte> gid = stackalloc byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return EntityId.ForGid(kind, gid);
    }

    [Fact]
    public void A_committed_snapshot_carries_each_targets_kind_and_Liked_Songs_as_a_rowless_collection()
    {
        TestScope.Fresh();
        var me = Entities.User(EntityUri.Parse("spotify:user:tester".AsSpan()));
        Entities.Current.MeSlot = me.Slot;
        var s = Staging.Rent();

        ref var page = ref s.RecentsPages.Add();
        page.Parent = s.Text("spotify:user:tester");
        page.Revision = s.Text("0a1b2c");

        ref var album = ref s.RecentsRows.Add();
        album.Id = Gid(EntityKind.Album, 90);
        album.ItemId = s.Text("a1");
        album.PlayedAtMs = T0;
        album.ChildCount = 5;
        album.Kind = (byte)RecentsRowKind.Group;
        album.Reason = (byte)RecentsReason.Played;
        album.MembersStart = 0;
        album.MembersLen = 2;

        ref var liked = ref s.RecentsRows.Add();
        liked.Id = s.Text(EntityUri.LikedCollection);
        liked.ItemId = s.Text("l1");
        liked.PlayedAtMs = T0 - 1;
        liked.Kind = (byte)RecentsRowKind.Single;
        liked.Reason = (byte)RecentsReason.Saved;

        ref var artist = ref s.RecentsRows.Add();
        artist.Id = Gid(EntityKind.Artist, 50);
        artist.ItemId = s.Text("r1");
        artist.PlayedAtMs = T0 - 2;
        artist.Kind = (byte)RecentsRowKind.Single;
        artist.Reason = (byte)RecentsReason.Played;

        for (byte i = 0; i < 2; i++)
        {
            ref var member = ref s.RecentsMembers.Add();
            member.Id = Gid(EntityKind.Track, (byte)(10 + i));
            member.ItemId = s.Text("m" + i);
            member.PlayedAtMs = T0 - i;
        }
        page.RowCount = 3;
        page.MemberCount = 2;
        TestScope.CommitAndPublish(s);

        var snapshot = RecentsSnapshot.Of(Recents.Me);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(new EntityRef(EntityKind.Album, Entities.Album(Gid(EntityKind.Album, 90)).Slot), snapshot.Targets[0]);
        Assert.Equal(EntityKind.Album, snapshot.Rows[0].Entity);
        Assert.Equal(new EntityRef(EntityKind.Collection, Table.None), snapshot.Targets[1]);
        Assert.True(RecentsView.Names(snapshot.Targets[1]));                   // no row, still a destination
        Assert.Equal(EntityKind.Collection, RecentsView.TargetKind(snapshot, 1));
        Assert.Equal(EntityKind.Artist, snapshot.Targets[2].Kind);
        Assert.True(RecentsView.PivotAvailable(snapshot, RecentsView.PivotArtists));

        Assert.Equal(
            [new EntityRef(EntityKind.Track, Entities.Track(Gid(EntityKind.Track, 10)).Slot),
             new EntityRef(EntityKind.Track, Entities.Track(Gid(EntityKind.Track, 11)).Slot)],
            snapshot.MemberTargetsOf(0).ToArray());
        Assert.All(snapshot.Members, m => Assert.Equal(EntityKind.Track, m.Entity));
        Assert.True(RecentsView.CanExpand(snapshot, 0));
        Assert.False(RecentsView.CanExpand(snapshot, 1));

        // Reading the same publication again is value-equal: the page adopts it as "nothing changed".
        Assert.True(snapshot.SameAs(RecentsSnapshot.Of(Recents.Me)));
    }
}
