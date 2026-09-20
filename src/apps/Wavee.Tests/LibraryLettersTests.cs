// ── Wavee.Tests/LibraryLettersTests.cs — the navigator's letter groups (Entities/User.cs §9b, LibraryLetters) ──────────
//
// The alphabetical navigator is not a flat list any more: it is 27 possible letter bands ("#", A–Z) interleaved with the
// rows in ONE flat index space, and every derived fact the page needs — where a band starts, how tall the list is, which
// letter the sticky overlay shows at a scroll offset, which cells of the A–Z jump strip are live, and the grouping's
// identity for the remount key — is a pure function of the sorted titles (derived facts live on the model; the page
// renders the answer and never probes the rows for it).
//
// What is pinned here is the RULE, over a fixture row view rather than a live scope: the letter of a title (the article
// and leading-punctuation skips, and the "#" bucket for everything OrdinalIgnoreCase cannot fold), the flat layout and
// its prefix-sum arithmetic at rowExtent 56 / HeaderExtent 28, the sticky letter at exact band boundaries, the Present
// bitmask, and Key's stability across identical builds.
//
// …and, since the seed/measure drift of 2026-09-18, the ROW EXTENT the navigator hands Build: the offsets here are only
// truthful if that number is what a row MEASURES (the plate plus the bound list chrome's own margin), so the two halves
// of that contract are asserted against each other rather than being two 56s that happened to match.

using Xunit;

namespace Wavee.Tests;

public class LibraryLettersTests
{
    const float RowExtent = 56f;
    const float Header = LibraryLetters.HeaderExtent;   // 28

    /// <summary>The smallest honest <see cref="ILibraryNavRows"/>: titles in the order the alphabetical comparator would
    /// have produced. Nothing here reads a scope, so the rule is testable without an entity table.</summary>
    readonly struct TitleRows(string[] titles) : ILibraryNavRows
    {
        public int Count => titles.Length;
        public long PlayedAt(int row) => 0;
        public int Year(int row) => 0;
        public string Title(int row) => titles[row];
        public string Subtitle(int row) => "";
        public ReadOnlySpan<char> Uri(int row, Span<char> scratch) => titles[row];
        public string Cover(int row) => "";
        public int CountOf(int row) => 0;
    }

    /// <summary>#, #, A, A, B, B, T — four bands over seven rows, in alphabetical order.</summary>
    static readonly string[] Seven =
    [
        "1999", "Ölüm", "Abbey Road", "Achtung Baby", "Bad", "the Beatles", "Thriller",
    ];

    static LibraryLetters Built(string[] titles, float rowExtent = RowExtent)
    {
        var letters = new LibraryLetters();
        letters.Build(new TitleRows(titles), rowExtent);
        return letters;
    }

    static int L(char c) => c - 'A' + 1;

    // ── Of: which band a title files under ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("the Beatles", 2)]          // the article is skipped: B, not T
    [InlineData("The Beatles", 2)]          // …case-insensitively
    [InlineData("THE BEATLES", 2)]
    [InlineData("…But Seriously", 2)]       // leading punctuation is skipped, and "But" is not an article
    [InlineData("[05]", 0)]                 // brackets skipped, a digit is '#'
    [InlineData("Ölüm", 0)]                 // OrdinalIgnoreCase folds A–Z and nothing else → '#'
    [InlineData("", 0)]                     // an unnamed row still files somewhere
    [InlineData("   ", 0)]
    [InlineData("The", 20)]                 // a BARE "The" is a title, not an article: T
    [InlineData("The ", 20)]                // …and so is "The " with nothing behind it
    [InlineData("Then", 20)]                // no space after "the" → not an article
    [InlineData("the x", 24)]               // the shortest real article case: X
    [InlineData("thriller", 20)]
    [InlineData("Zooropa", 26)]
    [InlineData("A", 1)]
    [InlineData("1999", 0)]
    [InlineData("東京", 0)]
    public void Of_FilesATitleUnderItsLetter(string title, int expected) => Assert.Equal(expected, LibraryLetters.Of(title));

    // ── Build: the flat layout ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_InterleavesOneHeaderPerBand_AndTheRowsKeepTheirOrder()
    {
        var letters = Built(Seven);

        // 4 headers + 7 rows.
        Assert.Equal(11, letters.FlatCount);
        Assert.Equal(7, letters.Rows);

        int[] expectedRow = [-1, 0, 1, -1, 2, 3, -1, 4, 5, -1, 6];
        int[] expectedLetter = [0, 0, 0, L('A'), L('A'), L('A'), L('B'), L('B'), L('B'), L('T'), L('T')];
        for (int flat = 0; flat < letters.FlatCount; flat++)
        {
            Assert.Equal(expectedRow[flat], letters.RowOf(flat));
            Assert.Equal(expectedRow[flat] < 0, letters.IsHeader(flat));
            Assert.Equal(expectedLetter[flat], letters.LetterOf(flat));
        }

        // Out of range answers "nothing", so a recycled/stale flat index can never index another band.
        Assert.False(letters.IsHeader(-1));
        Assert.False(letters.IsHeader(letters.FlatCount));
        Assert.Equal(-1, letters.RowOf(letters.FlatCount));
        Assert.Equal(-1, letters.LetterOf(-1));
    }

    [Fact]
    public void Build_HeaderFlatAndPresent_NameExactlyTheBandsThatHaveRows()
    {
        var letters = Built(Seven);

        Assert.Equal(0, letters.HeaderFlat(0));         // '#'
        Assert.Equal(3, letters.HeaderFlat(L('A')));
        Assert.Equal(6, letters.HeaderFlat(L('B')));
        Assert.Equal(9, letters.HeaderFlat(L('T')));

        // The jump strip's whole input: four live cells, twenty-three inert ones.
        uint expected = 1u | (1u << L('A')) | (1u << L('B')) | (1u << L('T'));
        Assert.Equal(expected, letters.Present);
        for (int l = 0; l < LibraryLetters.Count; l++)
        {
            bool has = (expected & (1u << l)) != 0;
            Assert.Equal(has, letters.Has(l));
            Assert.Equal(has, letters.HeaderFlat(l) >= 0);
        }
        Assert.False(letters.Has(-1));
        Assert.False(letters.Has(LibraryLetters.Count));
        Assert.Equal(-1, letters.HeaderFlat(LibraryLetters.Count));
    }

    [Fact]
    public void Build_OffsetsAreThePrefixSum_AndTotalExtentIsTheirEnd()
    {
        var letters = Built(Seven);

        // header 28, row 56: 4 × 28 + 7 × 56 = 504.
        float[] expected = [0f, 28f, 84f, 140f, 168f, 224f, 280f, 308f, 364f, 420f, 448f];
        for (int flat = 0; flat < expected.Length; flat++) Assert.Equal(expected[flat], letters.OffsetOf(flat));
        Assert.Equal(504f, letters.TotalExtent);
        Assert.Equal(4 * Header + 7 * RowExtent, letters.TotalExtent);

        // OffsetOf clamps rather than throwing: the end is the total, and a negative index is the start.
        Assert.Equal(letters.TotalExtent, letters.OffsetOf(letters.FlatCount));
        Assert.Equal(letters.TotalExtent, letters.OffsetOf(9_999));
        Assert.Equal(0f, letters.OffsetOf(-7));

        // ExtentOf is the same two numbers the offsets were built from (the list's analytic extents seed).
        Assert.Equal(Header, letters.ExtentOf(0, RowExtent));
        Assert.Equal(RowExtent, letters.ExtentOf(1, RowExtent));
    }

    [Fact]
    public void Build_AtADifferentRowExtent_ScalesOnlyTheRows()
    {
        var letters = Built(Seven, rowExtent: 40f);
        Assert.Equal(4 * Header + 7 * 40f, letters.TotalExtent);
        Assert.Equal(Header, letters.OffsetOf(1));                    // the first row still sits under its header
        Assert.Equal(Header + 40f + 40f, letters.OffsetOf(3));         // …and the A header after the two '#' rows
    }

    // ── the extent the navigator actually hands Build ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheNavigatorRowExtent_IsThePlatePlusTheChromeMargin_OnBothDensities()
    {
        // THE seed/measure contract. `LibraryLetters` sums offsets at the extent the page passes, the list seeds its
        // scroll extent from the same number, and the layout then MEASURES each row — so the number has to be what a row
        // measures, which is the selected plate PLUS the bound list chrome's 2-DIP margin above and below it. The row
        // was authored at the plate (56 / 40) and seeded at the plate, and the missing 4 DIP a row was the drifting
        // sticky letter, the wrong jump target and the jumping scrollbar thumb.
        Assert.Equal(User.NavRowPlate + 2f * User.NavRowMarginY, User.NavRowExtent);
        Assert.Equal(User.NavRowCompactPlate + 2f * User.NavRowMarginY, User.NavRowCompactExtent);
        Assert.Equal(60f, User.NavRowExtent);
        Assert.Equal(44f, User.NavRowCompactExtent);

        // …and the grouping arithmetic at those two extents, which is what the page builds every time the sort is a–z.
        Assert.Equal(4 * Header + 7 * User.NavRowExtent, Built(Seven, User.NavRowExtent).TotalExtent);
        Assert.Equal(4 * Header + 7 * User.NavRowCompactExtent, Built(Seven, User.NavRowCompactExtent).TotalExtent);

        // A band boundary lands on the seed, not on the plate: the A header's top after the two '#' rows.
        Assert.Equal(Header + 2 * User.NavRowExtent, Built(Seven, User.NavRowExtent).OffsetOf(3));
        Assert.Equal(1, Built(Seven, User.NavRowExtent).StickyLetterAt(Header + 2 * User.NavRowExtent));
    }

    // ── StickyLetterAt ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1f, -1)]        // above the first header there is nothing to stick
    [InlineData(0f, 0)]          // the '#' header's own top
    [InlineData(27.99f, 0)]
    [InlineData(28f, 0)]         // the first '#' row
    [InlineData(139.99f, 0)]
    [InlineData(140f, 1)]        // exactly the A header's top
    [InlineData(279.99f, 1)]
    [InlineData(280f, 2)]        // exactly the B header's top
    [InlineData(419.99f, 2)]
    [InlineData(420f, 20)]       // exactly the T header's top
    [InlineData(503.99f, 20)]
    [InlineData(10_000f, 20)]    // past the end (overscroll): the last band, never -1
    public void StickyLetterAt_IsTheBandContainingTheOffset(float offset, int expected)
        => Assert.Equal(expected, Built(Seven).StickyLetterAt(offset));

    [Fact]
    public void StickyLetterAt_OnAnEmptyBuild_IsNothing()
    {
        var letters = Built([]);
        Assert.Equal(0, letters.FlatCount);
        Assert.Equal(0f, letters.TotalExtent);
        Assert.Equal(0u, letters.Present);
        Assert.Equal(-1, letters.StickyLetterAt(0f));
        Assert.Equal(-1, letters.StickyLetterAt(500f));
    }

    // ── Key: the remount key's grouping part ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Key_IsStableAcrossTwoIdenticalBuilds_AndMovesWhenTheGroupingDoes()
    {
        ulong a = Built(Seven).Key();
        ulong b = Built(Seven).Key();
        Assert.Equal(a, b);

        // The SAME instance rebuilt with the same rows agrees too (the arrays are reused, the answer is not).
        var reused = new LibraryLetters();
        reused.Build(new TitleRows(Seven), RowExtent);
        ulong first = reused.Key();
        reused.Build(new TitleRows(Seven), RowExtent);
        Assert.Equal(first, reused.Key());

        // A filter that drops the T band regroups everything after it.
        string[] noT = ["1999", "Ölüm", "Abbey Road", "Achtung Baby", "Bad", "the Beatles"];
        Assert.NotEqual(a, Built(noT).Key());

        // Same letters, different band SIZES → different header positions → a different key.
        string[] shifted = ["1999", "Abbey Road", "Achtung Baby", "Adore", "Bad", "the Beatles", "Thriller"];
        Assert.NotEqual(a, Built(shifted).Key());

        // Two empty builds agree (the navigator must not remount while a filter matches nothing).
        Assert.Equal(Built([]).Key(), Built([]).Key());
    }

    [Fact]
    public void Key_DoesNotChangeWhenOnlyATitleInsideABandChanges()
    {
        // The grouping — and only the grouping — is in the key: the row TEXT is FactsKey's business, so a title edit
        // inside the same band must not cost the navigator a remount.
        string[] renamed = ["1999", "Ölüm", "Abbey Road", "Achtung Baby!", "Bad", "the Beatles", "Thriller"];
        Assert.Equal(Built(Seven).Key(), Built(renamed).Key());
    }

    // ── reuse ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABigBuildThenASmallOne_AnswersForTheSmallOne()
    {
        // The arrays grow and never shrink (one instance per page, rebuilt on every filter keystroke): a stale cell from
        // the longer build must never be readable, which is what FlatCount and the offsets are asserted on here.
        var titles = new string[200];
        for (int i = 0; i < titles.Length; i++) titles[i] = ((char)('A' + i % 26)) + i.ToString("D3");
        Array.Sort(titles, StringComparer.OrdinalIgnoreCase);

        var letters = new LibraryLetters();
        letters.Build(new TitleRows(titles), RowExtent);
        Assert.Equal(200 + 26, letters.FlatCount);                     // every letter has rows, '#' has none
        Assert.False(letters.Has(0));
        Assert.Equal(26 * Header + 200 * RowExtent, letters.TotalExtent);

        letters.Build(new TitleRows(["Bad", "Thriller"]), RowExtent);
        Assert.Equal(4, letters.FlatCount);
        Assert.Equal(2, letters.Rows);
        Assert.Equal(2 * Header + 2 * RowExtent, letters.TotalExtent);
        Assert.Equal((1u << L('B')) | (1u << L('T')), letters.Present);
        Assert.Equal(-1, letters.HeaderFlat(L('A')));
        Assert.Equal(L('T'), letters.StickyLetterAt(letters.TotalExtent - 1f));
    }

    [Fact]
    public void RowsThatAreNotGroupedByLetter_NeverOverrunTheBuffers()
    {
        // The 2026-09-18 crash: hundreds of rows whose letter flips on every row (an ordinal title order over "The …",
        // accents and CJK). Every flip opens a band, so the worst case is one header PER ROW — far past `rows + 27`.
        var titles = new string[400];
        for (int i = 0; i < titles.Length; i++) titles[i] = (i & 1) == 0 ? "Abba " + i : "Zebra " + i;

        var letters = Built(titles);

        Assert.Equal(titles.Length, letters.Rows);
        Assert.Equal(titles.Length * 2, letters.FlatCount);                     // a header before every row
        Assert.Equal(titles.Length * (Header + RowExtent), letters.TotalExtent);
    }

    [Fact]
    public void OneBandOnly_IsStillAHeaderPlusItsRows()
    {
        var letters = Built(["Bad", "Beat It", "Billie Jean"]);
        Assert.Equal(4, letters.FlatCount);
        Assert.Equal(0, letters.HeaderFlat(L('B')));
        Assert.Equal(1u << L('B'), letters.Present);
        Assert.Equal(L('B'), letters.StickyLetterAt(0f));
        Assert.Equal(Header + 3 * RowExtent, letters.TotalExtent);
    }
}
