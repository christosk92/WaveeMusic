// ── Wavee.Tests/LibraryNavMountTests.cs — the navigator's one mount identity (Entities/User.NavMount.cs, plan
// docs/plans/wavee/library-stabilization-plan.md §3.3/§5 step A0) ──────────────────────────────────────────────────
//
// `LibraryNavMount.For` is the fix for RC1/RC2/RC3: ONE function that decides the remount key, the template, the
// layout, the cell ladder and the scroll-key family from (view, size, alphabetical, lettersKey) — nothing else. What
// is pinned here is exactly that boundary: which inputs move the key (view, size-in-a-grid, letters-while-lettered)
// and which do NOT (size in a list, letters in a grid, and — because the type has no such parameter at all —
// order/facts/rows), plus the concrete numbers every other surface (the grid ladder, the row extents) has to agree
// with.

using Xunit;

namespace Wavee.Tests;

public class LibraryNavMountTests
{
    const string Kind = "artists";

    // ── the key ignores everything a bound list refreshes in place ─────────────────────────────────────────────────

    [Fact]
    public void Key_IgnoresOrderAndFacts_BecauseTheTypeHasNoSuchInput()
    {
        // There is no OrderKey/FactsKey/RowsKey parameter to pass at all — the strongest form of "does not move the
        // key" a signature can make. Size and lettersKey are folded away too, in a plain (non-alphabetical) list view.
        Assert.Equal(LibraryNavMount.For(Kind, 1, 0, false, 0).Key, LibraryNavMount.For(Kind, 1, 2, false, 123).Key);
    }

    // ── the key changes with view ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)]
    [InlineData(1, 0)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 3)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void Key_ChangesWithView_ForEveryOrderedPairOfViews(int a, int b)
    {
        // Fixed size/alphabetical/lettersKey so the view is the only thing moving — all 12 ordered pairs among the
        // four views (0 compact list, 1 list, 2 compact grid, 3 grid) must disagree.
        Assert.NotEqual(LibraryNavMount.For(Kind, a, 0, false, 0).Key, LibraryNavMount.For(Kind, b, 0, false, 0).Key);
    }

    // ── size only moves the key in a grid ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]   // compact grid
    [InlineData(3)]   // full grid
    public void Key_ChangesWithSize_InAGridView(int view)
    {
        string s = LibraryNavMount.For(Kind, view, 0, false, 0).Key;
        string m = LibraryNavMount.For(Kind, view, 1, false, 0).Key;
        string l = LibraryNavMount.For(Kind, view, 2, false, 0).Key;
        Assert.NotEqual(s, m); Assert.NotEqual(m, l); Assert.NotEqual(s, l);
    }

    [Theory]
    [InlineData(0)]   // compact list
    [InlineData(1)]   // list
    public void Key_IsUnchangedBySize_InAListView(int view)
    {
        // S/M/L is a no-op in a list — there is no cell to resize, so a remount here would be pure churn (D13).
        string s = LibraryNavMount.For(Kind, view, 0, false, 0).Key;
        string m = LibraryNavMount.For(Kind, view, 1, false, 0).Key;
        string l = LibraryNavMount.For(Kind, view, 2, false, 0).Key;
        Assert.Equal(s, m); Assert.Equal(m, l);
    }

    // ── letters only enter the key while the flat projection is live ───────────────────────────────────────────────

    [Theory]
    [InlineData(0)]   // compact list
    [InlineData(1)]   // list
    public void Key_CarriesLettersKey_WhenAlphabeticalInAList(int view)
    {
        // A list under a–z IS the flat header/row projection — a grouping change (a header moved) has to reseed the
        // per-index extent table, which only a remount does.
        string a = LibraryNavMount.For(Kind, view, 0, true, 0x1111UL).Key;
        string b = LibraryNavMount.For(Kind, view, 0, true, 0x2222UL).Key;
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(2)]   // compact grid
    [InlineData(3)]   // full grid
    public void Key_IgnoresLettersKey_WhenAlphabeticalInAGrid(int view)
    {
        // A grid never builds the flat projection (no headers to interleave), so the grouping's identity is not part
        // of what the grid mount freezes — only the strip (a separate, always-keyed sibling) reads it.
        string a = LibraryNavMount.For(Kind, view, 0, true, 0x1111UL).Key;
        string b = LibraryNavMount.For(Kind, view, 0, true, 0x2222UL).Key;
        Assert.Equal(a, b);
    }

    [Fact]
    public void Key_IgnoresLettersKey_WhenNotAlphabetical()
    {
        // Any other sort never groups by letter, in list or grid, so lettersKey (whatever `LibraryLetters` last
        // built) must not leak into a key the recents/albums sort mints.
        Assert.Equal(LibraryNavMount.For(Kind, 1, 0, false, 0x1111UL).Key, LibraryNavMount.For(Kind, 1, 0, false, 0x2222UL).Key);
        Assert.Equal(LibraryNavMount.For(Kind, 3, 0, false, 0x1111UL).Key, LibraryNavMount.For(Kind, 3, 0, false, 0x2222UL).Key);
    }

    // ── the cell ladder and the row extents ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 0, 88f)] [InlineData(2, 1, 104f)] [InlineData(2, 2, 120f)]     // compact grid, S/M/L
    [InlineData(3, 0, 116f)] [InlineData(3, 1, 140f)] [InlineData(3, 2, 164f)]    // full grid, S/M/L
    public void CellLadder_StepsSMLAtTheSpecNumbers(int view, int size, float expected)
        => Assert.Equal(expected, LibraryNavMount.For(Kind, view, size, false, 0).CellMin);

    [Fact]
    public void CellLadder_MatchesItsOwnPublishedConstants()
    {
        // The consts are the canonical numbers now (User.Page.Library.cs's LayoutFor still has them inline — this
        // pins that they agree, and is what that call site should switch to reading).
        Assert.Equal(LibraryNavMount.CardCellMinCompact, LibraryNavMount.For(Kind, 2, 0, false, 0).CellMin);
        Assert.Equal(LibraryNavMount.CardCellMinCompact + LibraryNavMount.CardCellStepCompact, LibraryNavMount.For(Kind, 2, 1, false, 0).CellMin);
        Assert.Equal(LibraryNavMount.CardCellMinFull, LibraryNavMount.For(Kind, 3, 0, false, 0).CellMin);
        Assert.Equal(LibraryNavMount.CardCellMinFull + LibraryNavMount.CardCellStepFull, LibraryNavMount.For(Kind, 3, 1, false, 0).CellMin);
        Assert.Equal(LibraryNavMount.GridGap, LibraryNavMount.For(Kind, 2, 0, false, 0).Gap);
        Assert.Equal(LibraryNavMount.GridGap, LibraryNavMount.For(Kind, 3, 2, false, 0).Gap);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]   // compact: list and compact grid share the compact row extent
    public void RowExtent_IsTheCompactPlateOuterExtent_ForCompactViews(int view)
    {
        Assert.Equal(44f, LibraryNavMount.For(Kind, view, 0, false, 0).RowExtent);
        Assert.Equal(User.NavRowCompactExtent, LibraryNavMount.For(Kind, view, 0, false, 0).RowExtent);
    }

    [Theory]
    [InlineData(1)] [InlineData(3)]
    public void RowExtent_IsTheFullPlateOuterExtent_ForFullViews(int view)
    {
        Assert.Equal(60f, LibraryNavMount.For(Kind, view, 0, false, 0).RowExtent);
        Assert.Equal(User.NavRowExtent, LibraryNavMount.For(Kind, view, 0, false, 0).RowExtent);
    }

    // ── ScrollKey is per layout FAMILY, not per view ────────────────────────────────────────────────────────────────

    [Fact]
    public void ScrollKey_IsPerFamily_ListLettersGrid()
    {
        string list = LibraryNavMount.For(Kind, 1, 0, false, 0).ScrollKey;
        string letters = LibraryNavMount.For(Kind, 1, 0, true, 1).ScrollKey;
        string grid = LibraryNavMount.For(Kind, 3, 0, false, 0).ScrollKey;

        Assert.NotEqual(list, letters);
        Assert.NotEqual(letters, grid);
        Assert.NotEqual(list, grid);
        Assert.Equal("lib:nav:" + Kind + ":list", list);
        Assert.Equal("lib:nav:" + Kind + ":letters", letters);
        Assert.Equal("lib:nav:" + Kind + ":grid", grid);
    }

    [Fact]
    public void ScrollKey_IsTheSame_ForBothDensitiesOfOneFamily()
    {
        // Compact and full share the restored offset within a family (a family is about SHAPE, not size/density).
        Assert.Equal(LibraryNavMount.For(Kind, 0, 0, false, 0).ScrollKey, LibraryNavMount.For(Kind, 1, 0, false, 0).ScrollKey);
        Assert.Equal(LibraryNavMount.For(Kind, 2, 0, false, 0).ScrollKey, LibraryNavMount.For(Kind, 3, 0, false, 0).ScrollKey);
    }

    [Fact]
    public void ScrollKey_CarriesTheRouteKind_SoTwoPagesNeverShareAnOffset()
    {
        Assert.NotEqual(LibraryNavMount.For("artists", 1, 0, false, 0).ScrollKey, LibraryNavMount.For("albums", 1, 0, false, 0).ScrollKey);
    }

    // ── Strip vs Lettered: the strip is wider than the flat projection ─────────────────────────────────────────────

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public void StripWithoutLetters_InAGrid(int view)
    {
        // A grid under a–z gets the jump strip (it has a first-card index per letter) but never the flat header/row
        // projection (a grid has no header row to interleave).
        var mount = LibraryNavMount.For(Kind, view, 0, true, 0);
        Assert.True(mount.Strip);
        Assert.False(mount.Lettered);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void StripAndLetters_BothLive_InAListUnderAlphabetical(int view)
    {
        var mount = LibraryNavMount.For(Kind, view, 0, true, 0);
        Assert.True(mount.Strip);
        Assert.True(mount.Lettered);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void NeitherStripNorLetters_WhenNotAlphabetical(int view)
    {
        var mount = LibraryNavMount.For(Kind, view, 0, false, 0);
        Assert.False(mount.Strip);
        Assert.False(mount.Lettered);
    }

    // ── template / layout / padding: the rest of what a mount freezes ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, LibraryNavTemplate.RowCompact, LibraryNavLayoutKind.Extents, false)]
    [InlineData(1, LibraryNavTemplate.Row, LibraryNavLayoutKind.Extents, false)]
    [InlineData(2, LibraryNavTemplate.CardCompact, LibraryNavLayoutKind.GridFit, true)]
    [InlineData(3, LibraryNavTemplate.Card, LibraryNavLayoutKind.GridFit, true)]
    public void TemplateLayoutAndPadding_FollowTheViewCode(int view, LibraryNavTemplate template, LibraryNavLayoutKind layout, bool gridPadding)
    {
        var mount = LibraryNavMount.For(Kind, view, 1, false, 0);
        Assert.Equal(template, mount.Template);
        Assert.Equal(layout, mount.Layout);
        Assert.Equal(gridPadding, mount.GridPadding);
    }

    // ── defensive clamping: a corrupt persisted value must never index out of the ladder ───────────────────────────

    [Theory]
    [InlineData(-5, 0)] [InlineData(4, 0)] [InlineData(int.MaxValue, 0)] [InlineData(int.MinValue, 0)]
    public void View_IsClamped_ToTheFourValidCodes(int wildView, int size)
    {
        var mount = LibraryNavMount.For(Kind, wildView, size, false, 0);
        Assert.Equal(LibraryNavMount.For(Kind, Math.Clamp(wildView, 0, 3), size, false, 0), mount);
    }

    [Theory]
    [InlineData(-1)] [InlineData(3)] [InlineData(int.MaxValue)] [InlineData(int.MinValue)]
    public void Size_IsClamped_ToTheThreeValidSteps(int wildSize)
    {
        var mount = LibraryNavMount.For(Kind, 3, wildSize, false, 0);
        Assert.Equal(LibraryNavMount.For(Kind, 3, Math.Clamp(wildSize, 0, 2), false, 0), mount);
    }
}
