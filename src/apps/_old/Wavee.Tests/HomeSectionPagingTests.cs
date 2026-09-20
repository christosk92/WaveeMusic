using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The two defect classes behind Home's "Show all", both pure arithmetic and both previously untested.
/// <para>OFFSETS: the cursor we hand the server is the RAW item count, never the deduped card count.</para>
/// <para>TERMINATION: measured across the 31 sections of a captured Home, <c>items.Count != totalCount</c> in 7 of them
/// and a COMPLETE section can answer <c>nextOffset: 0</c>. So the total is an arming hint, the cursor is the terminator,
/// and a page the dedup ate whole is a dead end whatever either of them says.</para></summary>
public sealed class HomeSectionPagingTests
{
    static HomeCard Card(string id) => new("spotify:playlist:" + id, id, null, null, HomeCardKind.Playlist);

    static HomeSection Section(int totalCount, int rawItemCount, params string[] ids) =>
        new("spotify:section:s", "Section", null, ids.Select(Card).ToArray(),
            totalCount, rawItemCount, UnsupportedCount: 0, DuplicateCount: 0);

    // ── offsets ───────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void NextOffset_IsTheRawServerCursor_NotTheDedupedCardCount()
    {
        // 10 items came back; 2 were duplicates/unsupported and never became cards. Asking for 8 would re-fetch the
        // two we just dropped — forever, since dropping them again leaves the offset exactly where it was.
        Assert.Equal(10, HomeSectionPaging.NextOffset(Section(40, 10, "a", "b", "c", "d", "e", "f", "g", "h")));
    }

    [Fact]
    public void NextOffset_IsFlooredAtTheCardCount_WhenTheSourceUnderReportedItsRawCount()
    {
        // A seed whose provenance left RawItemCount at 0 still asks for the page AFTER what it is showing.
        Assert.Equal(3, HomeSectionPaging.NextOffset(Section(20, 0, "a", "b", "c")));
    }

    // ── arming ────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void HasMore_WithoutACursor_UsesTheServerTotal_AgainstTheRawCount()
    {
        // A section still showing Home's inline seed: a total, and no paging info to go with it.
        Assert.True(HomeSectionPaging.HasMore(Section(40, 10, "a", "b")));
        Assert.False(HomeSectionPaging.HasMore(Section(10, 10, "a", "b")));
        // Against RAW, not deduped: 10 of 10 raw items are in hand, so the fact that only 8 survived dedup is not a
        // reason to claim the server is still holding two.
        Assert.False(HomeSectionPaging.HasMore(Section(10, 10, "a", "b", "c", "d", "e", "f", "g", "h")));
    }

    [Fact]
    public void HasMore_PrefersTheServerCursor_OverAnUnderReportedTotal()
    {
        // 20 in hand, totalCount says 20 — and the server's own cursor still points at 20 ("ask me for 20 next").
        // The total loses: it is under-reported often enough that trusting it here leaves a dead page.
        var section = Section(20, 20, "a", "b");
        Assert.False(HomeSectionPaging.HasMore(section));
        Assert.True(HomeSectionPaging.HasMore(section, 20));
    }

    [Fact]
    public void HasMore_CursorBehindOurRawPosition_Disarms_EvenWithABigTotal()
    {
        // The section claims 500 more, but the cursor we were last handed sits behind where we already are: clicking
        // again can only re-read a window we have.
        Assert.False(HomeSectionPaging.HasMore(Section(500, 40, "a", "b"), 20));
    }

    // ── termination (B-3) ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void CanAdvance_NullCursor_Stops()
    {
        // The section is complete. This is the ONLY unambiguous "done" the server sends.
        Assert.False(HomeSectionPaging.CanAdvance(0, null));
        Assert.False(HomeSectionPaging.CanAdvance(20, null));
    }

    [Fact]
    public void CanAdvance_ZeroOnACompleteSection_Stops()
    {
        // Measured: 6 items / totalCount 6 / nextOffset 0. Zero is a legal cursor VALUE, which is why it cannot be
        // flattened into "absent" — and why honouring it as a cursor would re-request page one forever.
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
    public void ShortPageWithANullCursor_IsComplete_EvenThoughTheTotalDisagrees()
    {
        // Measured in 7 of 31 captured sections: 8 items, totalCount 9, nextOffset null. The total alone keeps the
        // button armed forever on a section the server has already finished serving — hence "never terminate on
        // loaded < totalCount".
        var section = Section(9, 8, "a", "b", "c", "d", "e", "f", "g", "h");
        Assert.True(HomeSectionPaging.HasMore(section));            // the trap: the total says "one more"
        Assert.False(HomeSectionPaging.CanAdvance(0, null));        // the cursor says "there is nothing to ask for"
    }

    // ── folding a page in ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Append_AdvancesTheRawCursorByTheWholePage_AndKeepsTheLedgerHonest()
    {
        // 3 raw items in hand, of which one was unsupported → 2 cards. The incoming page repeats one of them.
        var current = new HomeSection("spotify:section:s", "Section", null, [Card("a"), Card("b")],
            TotalCount: 40, RawItemCount: 3, UnsupportedCount: 1);

        var next = HomeSectionPaging.Append(current, [Card("b"), Card("c")], pageTotal: 41);

        Assert.Equal(["spotify:playlist:a", "spotify:playlist:b", "spotify:playlist:c"], next.Cards.Select(c => c.Uri));
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
        // The third termination rule. The cursor advancing is what stops the SAME request repeating; Progressed being
        // false is what stops the user clicking a button that can only ever produce the same nothing.
        var current = Section(40, 2, "a", "b");

        var next = HomeSectionPaging.Append(current, [Card("a"), Card("b")], pageTotal: 40);

        Assert.Equal(2, next.Cards.Count);
        Assert.Equal(4, next.RawItemCount);
        Assert.Equal(2, next.DuplicateCount);
        Assert.False(HomeSectionPaging.Progressed(current, next));
        // …and the trap it used to fall into: the total still claims 36 more, so the total alone would keep it armed.
        Assert.True(HomeSectionPaging.HasMore(next));
    }

    [Fact]
    public void Append_IsCaseInsensitiveOnUri_LikeTheComposersOwnDedup()
    {
        var current = Section(40, 1, "a");
        Assert.Equal(1, HomeSectionPaging.Append(current, [Card("A")], pageTotal: 40).DuplicateCount);
    }

    // ── BrowseSection's synthesized cursor ────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0, 10, 74, 10)]     // 10 of 74 in hand: ask for the next 10.
    [InlineData(70, 4, 74, null)]   // 74 of 74 in hand: the total is exhausted.
    [InlineData(0, 0, 0, null)]     // nothing requested, nothing returned, nothing to page.
    [InlineData(0, 10, 10, null)]   // a complete section returned whole on page one.
    public void BrowseNextOffset_SynthesizesFromOffsetPlusPageCount_VersusTotal(
        int requestedOffset, int pageCount, int total, int? expected)
    {
        Assert.Equal(expected, HomeSectionPaging.BrowseNextOffset(requestedOffset, pageCount, total));
    }

    // ── BrowseSectionNextOffset: server cursor first, explicit terminator wins, synthesized cursor is the last resort ──
    static BrowseSection MakeBrowseSection(int total, int cardCount, int? nextOffset) =>
        new("spotify:section:s", "Section", BrowseSectionKind.Shelf,
            Enumerable.Range(0, cardCount).Select(i => new BrowseCard("spotify:playlist:" + i, "t" + i, null, null)).ToArray(),
            [], total, nextOffset);

    [Fact]
    public void BrowseSectionNextOffset_PrefersTheServerCursor_OverTheSynthesizedOne()
    {
        // 20 of 74 in hand; the server's own cursor says 40 (skipping straight to the next window) — the
        // synthesized offset+count-vs-total arithmetic would also say 20+20=40 here, but a case where they disagree
        // must still take the server's word: BrowseSectionNextOffset never even computes the synthesized value when
        // a real server cursor is present.
        var page = MakeBrowseSection(total: 74, cardCount: 20, nextOffset: 40);
        Assert.Equal(40, HomeSectionPaging.BrowseSectionNextOffset(0, page));
    }

    [Fact]
    public void BrowseSectionNextOffset_ExplicitTerminator_StopsEvenWhenTotalClaimsMore()
    {
        // The trap this exists to avoid: PagingComplete means the server is DONE with this section, even though
        // Total (74) still claims 54 more past what this page (20 cards) holds. Falling back to the synthesized
        // cursor here (as a naive `page.NextOffset ?? BrowseNextOffset(...)` would) re-arms a section the server has
        // already finished serving — the same total-vs-cursor disagreement HomeSectionPaging.cs documents for Home.
        var page = MakeBrowseSection(total: 74, cardCount: 20, nextOffset: BrowseSection.PagingComplete);
        Assert.Null(HomeSectionPaging.BrowseSectionNextOffset(0, page));
    }

    [Fact]
    public void BrowseSectionNextOffset_NoServerCursorAtAll_FallsBackToTheSynthesizedOne()
    {
        // Plain null — no pagingInfo came back — is the ONLY case that falls back to offset+count-vs-total.
        var page = MakeBrowseSection(total: 74, cardCount: 10, nextOffset: null);
        Assert.Equal(10, HomeSectionPaging.BrowseSectionNextOffset(0, page));
    }

    [Fact]
    public void BrowseSectionNextOffset_NoServerCursor_SynthesizedAlsoTerminatesAtTotal()
    {
        var page = MakeBrowseSection(total: 74, cardCount: 14, nextOffset: null);
        Assert.Null(HomeSectionPaging.BrowseSectionNextOffset(60, page));
    }
}
