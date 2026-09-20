// ── Wavee.Tests/HomeSectionPagingTests.cs — the "Show all" cursor arithmetic (Wave 5, owner P; 0.2.9 port) ──────────
//
// The two defect classes behind Home's "Show all", both pure arithmetic. OFFSETS: the cursor we hand the server is the
// RAW item count, never the deduped card count. TERMINATION: measured across the 31 sections of a captured Home,
// `items.Count != totalCount` in 7 of them and a COMPLETE section can answer `nextOffset: 0` — so the total is an arming
// hint, the cursor is the terminator, and a page the dedupe ate whole is a dead end whatever either says.
//
// Ported from 0.2.9 `HomeSectionPagingTests` onto `HomeSectionPaging` over `HomeSectionView`; the `int?` cursors are the
// `SectionPaging.NoCursor` / `SectionPaging.Complete` sentinels. Cards are real handles over committed playlist rows
// (HomeFixtures), so dedupe is by entity, not by uri text.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class HomeSectionPagingTests
{
    public HomeSectionPagingTests() => TestScope.Fresh();

    static HomeCard Card(string id) => HomeFixtures.Card(HomeFixtures.Playlist("spotify:playlist:paging-" + id, id));

    static HomeSectionView Section(int totalCount, int rawItemCount, params string[] ids)
    {
        var cards = new HomeCard[ids.Length];
        for (int i = 0; i < ids.Length; i++) cards[i] = Card(ids[i]);
        return new HomeSectionView(Table.None, "spotify:section:s", "Section", null, cards, totalCount, rawItemCount);
    }

    // ── offsets ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NextOffset_IsTheRawServerCursor_NotTheDedupedCardCount()
    {
        // 10 items came back; 2 were duplicates/unsupported and never became cards. Asking for 8 would re-fetch the
        // two we just dropped — forever, since dropping them again leaves the offset exactly where it was.
        Assert.Equal(10, HomeSectionPaging.NextOffset(Section(40, 10, "a", "b", "c", "d", "e", "f", "g", "h")));
    }

    [Fact]
    public void NextOffset_IsFlooredAtTheCardCount_WhenTheSourceUnderReportedItsRawCount()
        => Assert.Equal(3, HomeSectionPaging.NextOffset(Section(20, 0, "a", "b", "c")));

    // ── arming ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HasMore_WithoutACursor_UsesTheServerTotal_AgainstTheRawCount()
    {
        Assert.True(HomeSectionPaging.HasMore(Section(40, 10, "a", "b")));
        Assert.False(HomeSectionPaging.HasMore(Section(10, 10, "a", "b")));
        // Against RAW, not deduped: 10 of 10 raw items are in hand.
        Assert.False(HomeSectionPaging.HasMore(Section(10, 10, "a", "b", "c", "d", "e", "f", "g", "h")));
    }

    [Fact]
    public void HasMore_PrefersTheServerCursor_OverAnUnderReportedTotal()
    {
        var section = Section(20, 20, "a", "b");
        Assert.False(HomeSectionPaging.HasMore(section));
        Assert.True(HomeSectionPaging.HasMore(section, 20));
    }

    [Fact]
    public void HasMore_CursorBehindOurRawPosition_Disarms_EvenWithABigTotal()
        => Assert.False(HomeSectionPaging.HasMore(Section(500, 40, "a", "b"), 20));

    [Fact]
    public void HasMore_AnExplicitTerminator_Disarms_WhateverTheTotalSays()
    {
        // 0.3 can say what 0.2.9's `int?` could not: an explicit `Complete` is final.
        Assert.False(HomeSectionPaging.HasMore(Section(500, 2, "a", "b"), SectionPaging.Complete));
    }

    // ── termination ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CanAdvance_NoCursorOrTheTerminator_Stops()
    {
        Assert.False(HomeSectionPaging.CanAdvance(0, SectionPaging.NoCursor));
        Assert.False(HomeSectionPaging.CanAdvance(20, SectionPaging.NoCursor));
        Assert.False(HomeSectionPaging.CanAdvance(0, SectionPaging.Complete));
        Assert.False(HomeSectionPaging.CanAdvance(20, SectionPaging.Complete));
    }

    [Fact]
    public void CanAdvance_ZeroOnACompleteSection_Stops()
    {
        // Measured: 6 items / totalCount 6 / nextOffset 0. Zero is a legal cursor VALUE — honouring it as a cursor would
        // re-request page one forever.
        Assert.False(HomeSectionPaging.CanAdvance(0, 0));
        Assert.False(HomeSectionPaging.CanAdvance(20, 0));
    }

    [Fact]
    public void CanAdvance_CursorAtTheOffsetThatProducedIt_Stops()
    {
        Assert.False(HomeSectionPaging.CanAdvance(20, 20));
        Assert.False(HomeSectionPaging.CanAdvance(20, 19));
    }

    [Fact]
    public void CanAdvance_ForwardCursor_Advances()
    {
        Assert.True(HomeSectionPaging.CanAdvance(0, 20));
        Assert.True(HomeSectionPaging.CanAdvance(20, 40));
    }

    [Fact]
    public void ShortPageWithNoCursor_IsComplete_EvenThoughTheTotalDisagrees()
    {
        // Measured in 7 of 31 captured sections: 8 items, totalCount 9, nextOffset null.
        var section = Section(9, 8, "a", "b", "c", "d", "e", "f", "g", "h");
        Assert.True(HomeSectionPaging.HasMore(section));                              // the trap: the total says "one more"
        Assert.False(HomeSectionPaging.CanAdvance(0, SectionPaging.NoCursor));        // the cursor says "nothing to ask for"
    }

    // ── folding a page in ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Append_AdvancesTheRawCursorByTheWholePage_AndKeepsTheLedgerHonest()
    {
        // 3 raw items in hand, of which one was unsupported → 2 cards. The incoming page repeats one of them.
        HomeCard a = Card("a"), b = Card("b"), c = Card("c");
        var current = new HomeSectionView(Table.None, "spotify:section:s", "Section", null, [a, b],
            TotalCount: 40, RawItemCount: 3, UnsupportedCount: 1);

        var next = HomeSectionPaging.Append(current, [Card("b"), c], pageTotal: 41);

        Assert.Equal([a.DedupeKey, b.DedupeKey, c.DedupeKey], next.Cards.Select(x => x.DedupeKey));
        Assert.Equal(["a", "b", "c"], next.Cards.Select(x => x.Title));
        Assert.Equal(5, next.RawItemCount);          // 3 + the FULL page, duplicates included
        Assert.Equal(1, next.DuplicateCount);
        Assert.Equal(41, next.TotalCount);           // the server may revise its total upward
        Assert.Equal(next.RawItemCount, next.Cards.Count + next.UnsupportedCount + next.DuplicateCount);
        Assert.True(HomeSectionPaging.Progressed(current, next));
    }

    [Fact]
    public void Append_NeverLowersTheTotal()
    {
        var current = Section(40, 2, "a", "b");
        Assert.Equal(40, HomeSectionPaging.Append(current, [Card("c")], pageTotal: 3).TotalCount);
    }

    [Fact]
    public void Append_AnAllDuplicatePage_MovesTheCursorButMakesNoProgress()
    {
        var current = Section(40, 2, "a", "b");

        var next = HomeSectionPaging.Append(current, [Card("a"), Card("b")], pageTotal: 40);

        Assert.Equal(2, next.Cards.Count);
        Assert.Equal(4, next.RawItemCount);
        Assert.Equal(2, next.DuplicateCount);
        Assert.False(HomeSectionPaging.Progressed(current, next));
        Assert.True(HomeSectionPaging.HasMore(next));   // the total alone would keep it armed — the trap
    }

    [Fact]
    public void Append_TheSameEntityReadFromAnotherBand_IsADuplicate()
    {
        // 0.2.9 folded case-variant uri TEXT; 0.3 folds the ENTITY: the same playlist read out of two different bands
        // (two SectionSlots, one DedupeKey) is one card.
        var current = Section(40, 1, "a");
        var again = Card("a");
        Assert.NotEqual(current.Cards[0].SectionSlot, again.SectionSlot);
        Assert.Equal(1, HomeSectionPaging.Append(current, [again], pageTotal: 40).DuplicateCount);
    }

    // ── BrowseSection's synthesized cursor ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 10, 74, 10)]                          // 10 of 74 in hand: ask for the next 10.
    [InlineData(70, 4, 74, SectionPaging.Complete)]      // 74 of 74 in hand: the total is exhausted.
    [InlineData(0, 0, 0, SectionPaging.Complete)]        // nothing requested, nothing returned, nothing to page.
    [InlineData(0, 10, 10, SectionPaging.Complete)]      // a complete section returned whole on page one.
    public void BrowseNextOffset_SynthesizesFromOffsetPlusPageCount_VersusTotal(int requestedOffset, int pageCount, int total, int expected)
        => Assert.Equal(expected, HomeSectionPaging.BrowseNextOffset(requestedOffset, pageCount, total));

    [Fact]
    public void BrowseSectionNextOffset_PrefersTheServerCursor_OverTheSynthesizedOne()
        => Assert.Equal(40, HomeSectionPaging.BrowseSectionNextOffset(0, pageNextOffset: 40, pageCount: 20, pageTotal: 74));

    [Fact]
    public void BrowseSectionNextOffset_ExplicitTerminator_StopsEvenWhenTotalClaimsMore()
        => Assert.Equal(SectionPaging.Complete,
            HomeSectionPaging.BrowseSectionNextOffset(0, SectionPaging.Complete, pageCount: 20, pageTotal: 74));

    [Fact]
    public void BrowseSectionNextOffset_NoServerCursorAtAll_FallsBackToTheSynthesizedOne()
        => Assert.Equal(10, HomeSectionPaging.BrowseSectionNextOffset(0, SectionPaging.NoCursor, pageCount: 10, pageTotal: 74));

    [Fact]
    public void BrowseSectionNextOffset_NoServerCursor_SynthesizedAlsoTerminatesAtTotal()
        => Assert.Equal(SectionPaging.Complete,
            HomeSectionPaging.BrowseSectionNextOffset(60, SectionPaging.NoCursor, pageCount: 14, pageTotal: 74));
}
