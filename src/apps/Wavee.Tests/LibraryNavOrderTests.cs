// ── Wavee.Tests/LibraryNavOrderTests.cs — the ONE library ordering rule (Entities/User.cs §9) ────────────────────────
//
// A VERBATIM port of 0.2.9's LibraryNavOrderTests. LibraryNavOrder is the one comparator set behind every
// "Recents"/"Recently added"/"Alphabetical"/"Creator"/"Release date" list in Your Library (artists, albums, podcasts,
// discography), and the source of the navigator's remount key (OrderKey/FactsKey) — both must be pure functions of the
// ROWS, never of selection or of how the upstream store happened to permute ties. The 0.3 page sorts SLOTS through the
// same comparator (`LibraryNavSorter<LibraryRows>`); `LibraryRecencyTests` pins that the two paths agree.
//
// Added for 0.3: the persisted sort / view codes (shared with the sidebar's Library V3 — never renumber).
//
// Added by the 2026-09-17 library rework: the `Albums` arm (code 5, the artists rail's "albums" word) — saved-album count
// desc, then title. The count arrives through `ILibraryNavRows.CountOf`, precomputed by the caller, so the comparator
// itself never walks the library relations; `LibraryWordRailTests` pins which kinds offer the word.

using Xunit;

namespace Wavee.Tests;

public class LibraryNavOrderTests
{
    static LibraryNavFacts F(string uri, string title, string subtitle = "", int year = 0, string? cover = null)
        => new(uri, title, subtitle, year, cover);

    static readonly IReadOnlyDictionary<string, long> NoPlays = new Dictionary<string, long>();

    [Fact]
    public void Recents_PlayedNewestFirst_ThenNeverPlayedInSourceOrder()
    {
        // Source order: A, B, C, D, E. Played: A@100, C@300, E@200. Never played: B, D.
        var rows = new[]
        {
            F("a", "A"), F("b", "B"), F("c", "C"), F("d", "D"), F("e", "E"),
        };
        var recency = new Dictionary<string, long> { ["a"] = 100, ["c"] = 300, ["e"] = 200 };

        var order = LibraryNavOrder.Order(rows, LibraryNavSort.Recents, desc: false, recency);

        // Played block newest-first (C 300, E 200, A 100), then the never-played block in source order (B, D).
        Assert.Equal(new[] { 2, 4, 0, 1, 3 }, order);
    }

    [Fact]
    public void Recents_Desc_ReversesInsideBlocks_NeverPlayedStaysBelow()
    {
        var rows = new[]
        {
            F("a", "A"), F("b", "B"), F("c", "C"), F("d", "D"), F("e", "E"),
        };
        var recency = new Dictionary<string, long> { ["a"] = 100, ["c"] = 300, ["e"] = 200 };

        var order = LibraryNavOrder.Order(rows, LibraryNavSort.Recents, desc: true, recency);

        // desc flips the direction INSIDE each block (played: oldest-first; never-played: reverse source order) —
        // but the played block still sits above the never-played block, whatever desc says (the sidebar rule).
        Assert.Equal(new[] { 0, 4, 2, 3, 1 }, order);
    }

    [Fact]
    public void Recents_TieOnStamp_BreaksByTitleThenUri()
    {
        var rows = new[]
        {
            F("c1", "Cherry"), F("a1", "apple"), F("b1", "Banana"),
        };
        var recency = new Dictionary<string, long> { ["c1"] = 500, ["a1"] = 500, ["b1"] = 500 };

        var order = LibraryNavOrder.Order(rows, LibraryNavSort.Recents, desc: false, recency);

        // Equal stamps: the block comparator ties, so title (case-insensitive) breaks it — never the source index.
        Assert.Equal(new[] { 1, 2, 0 }, order);
    }

    [Fact]
    public void RecentlyAdded_IsSourceOrder_DescReverses()
    {
        var rows = new[] { F("a", "A"), F("b", "B"), F("c", "C"), F("d", "D") };

        Assert.Equal(new[] { 0, 1, 2, 3 }, LibraryNavOrder.Order(rows, LibraryNavSort.RecentlyAdded, desc: false, NoPlays));
        Assert.Equal(new[] { 3, 2, 1, 0 }, LibraryNavOrder.Order(rows, LibraryNavSort.RecentlyAdded, desc: true, NoPlays));
    }

    [Fact]
    public void Alphabetical_IsCaseInsensitive_TieBreaksByUri()
    {
        var rows = new[]
        {
            F("b", "Same"), F("a", "same"), F("z", "Apple"),
        };

        var order = LibraryNavOrder.Order(rows, LibraryNavSort.Alphabetical, desc: false, NoPlays);

        // "Apple" sorts first; "Same"/"same" tie case-insensitively, so uri ("a" < "b") breaks the tie.
        Assert.Equal(new[] { 2, 1, 0 }, order);
    }

    [Fact]
    public void Alphabetical_OrdersByTheGroupingLetterFirst_SoTheBandsAreContiguous()
    {
        // The a–z grouping files "The Beatles" under B, skips a leading quote, and puts an accented / CJK / digit initial
        // under '#'. A plain ordinal title order disagrees with all three ("Ölüm" and "宇多田" sort after Z, "The …" under
        // T), and rows whose letter flips back and forth are what overran LibraryLetters.Build (the 2026-09-18 crash).
        var rows = new[]
        {
            F("t", "Thriller"), F("o", "Ölüm"), F("b", "The Beatles"), F("a", "Abbey Road"),
            F("q", "\"Bad\""), F("n", "1999"), F("u", "宇多田ヒカル"),
        };

        var order = LibraryNavOrder.Order(rows, LibraryNavSort.Alphabetical, desc: false, NoPlays);

        int last = -1;
        foreach (int i in order)
        {
            int letter = LibraryLetters.Of(rows[i].Title);
            Assert.True(letter >= last, "the grouping letter must never go backwards under a–z");
            last = letter;
        }
        Assert.Equal(0, LibraryLetters.Of(rows[order[0]].Title));               // the '#' band leads
        Assert.Equal("Thriller", rows[order[^1]].Title);                        // … and T closes it
    }

    [Fact]
    public void Creator_BySubtitleThenTitle()
    {
        var rows = new[]
        {
            F("beta", "Beta", subtitle: "Zz"),
            F("alpha", "Alpha", subtitle: "Aa"),
            F("zulu", "Zulu", subtitle: "Aa"),
        };

        var order = LibraryNavOrder.Order(rows, LibraryNavSort.Creator, desc: false, NoPlays);

        Assert.Equal(new[] { 1, 2, 0 }, order);
    }

    [Fact]
    public void ReleaseDate_NewestFirst_UnknownYearSinks()
    {
        var rows = new[]
        {
            F("a", "X", year: 2020),
            F("b", "M", year: 0),
            F("c", "Y", year: 2023),
            F("d", "A", year: 0),
        };

        var order = LibraryNavOrder.Order(rows, LibraryNavSort.ReleaseDate, desc: false, NoPlays);

        // Known years newest-first (2023, 2020); unknown years sink as a block, tie-broken by title ("A" < "M").
        Assert.Equal(new[] { 2, 0, 3, 1 }, order);
    }

    [Fact]
    public void Albums_MostSavedAlbumsFirst_ThenTitle()
    {
        // The artists rail's third word. Counts: A 1, B 3, C 3, D 0 → B/C tie on 3 and break by title, and the artist
        // with nothing saved sinks to the bottom without a block split (it is still an artist, not an unknown year).
        var rows = new[] { F("a", "Adele"), F("b", "Blur"), F("c", "Bowie"), F("d", "Dio") };
        int[] counts = [1, 3, 3, 0];

        Assert.Equal(new[] { 1, 2, 0, 3 }, AlbumsOrder(rows, counts, desc: false));
        // desc flips the whole comparison, tie-breaks included: fewest first, and the tie reads Bowie before Blur.
        Assert.Equal(new[] { 3, 0, 2, 1 }, AlbumsOrder(rows, counts, desc: true));
    }

    [Fact]
    public void Albums_WithNoCountsAtAll_IsPureTitleOrder()
    {
        // Every kind but Artist counts 0 (the word is not on their rails), so the arm degrades to alphabetical rather
        // than to source order — a list that looks arbitrary is worse than one that looks sorted.
        var rows = new[] { F("z", "Zulu"), F("a", "Alpha"), F("m", "Mike") };
        Assert.Equal(new[] { 1, 2, 0 }, AlbumsOrder(rows, [0, 0, 0], desc: false));
    }

    static int[] AlbumsOrder(LibraryNavFacts[] rows, int[] counts, bool desc)
    {
        var order = new int[rows.Length];
        new LibraryNavSorter<LibraryFactsRows>().Order(new LibraryFactsRows(rows, null, counts), LibraryNavSort.Albums, desc, order);
        return order;
    }

    [Fact]
    public void Order_SingleRow_And_Empty()
    {
        Assert.Empty(LibraryNavOrder.Order(Array.Empty<LibraryNavFacts>(), LibraryNavSort.Recents, false, NoPlays));

        var single = new[] { F("a", "A") };
        Assert.Equal(new[] { 0 }, LibraryNavOrder.Order(single, LibraryNavSort.Alphabetical, true, NoPlays));
    }

    [Fact]
    public void OrderKey_SameSequence_SameKey_DifferentOrder_DifferentKey()
    {
        var f1 = F("a", "A"); var f2 = F("b", "B"); var f3 = F("c", "C");
        var sameOrder = new[] { f1, f2, f3 };
        var sameOrderAgain = new[] { f1, f2, f3 };
        var reordered = new[] { f2, f1, f3 };

        Assert.Equal(LibraryNavOrder.OrderKey(sameOrder), LibraryNavOrder.OrderKey(sameOrderAgain));
        Assert.NotEqual(LibraryNavOrder.OrderKey(sameOrder), LibraryNavOrder.OrderKey(reordered));
    }

    [Fact]
    public void FactsKey_ChangesWhenTitleOrCoverChanges_NotWhenSelectionWould()
    {
        var f1 = F("a", "A", cover: "cover-a");
        var f2 = F("b", "B", cover: "cover-b");
        var baseRows = new[] { f1, f2 };
        var sameRows = new[] { f1, f2 };

        // LibraryNavFacts carries no selection field — recomputing from the same facts (e.g. after a click changed
        // only which row is selected) MUST yield the same key, or the navigator would remount on every selection.
        Assert.Equal(LibraryNavOrder.FactsKey(baseRows), LibraryNavOrder.FactsKey(sameRows));

        var titleChanged = new[] { f1 with { Title = "A2" }, f2 };
        Assert.NotEqual(LibraryNavOrder.FactsKey(baseRows), LibraryNavOrder.FactsKey(titleChanged));

        var coverChanged = new[] { f1 with { CoverUrl = "cover-a2" }, f2 };
        Assert.NotEqual(LibraryNavOrder.FactsKey(baseRows), LibraryNavOrder.FactsKey(coverChanged));
    }

    [Fact]
    public void Order_IsDeterministic_ForTiedStamps()
    {
        // 50 rows, all played at the exact same instant (a realistic tie: a bulk import or a session replay).
        // Titles are unique and already alphabetical (Artist00..Artist49), so the tie always resolves through
        // ByTitle — never through source position. Feeding the SAME rows in a different array order must still
        // produce the same final VALUE sequence: the property the remount key (#E) depends on is that a same-set
        // republish (which can permute the join's output) never changes what OrderKey sees.
        const int n = 50;
        var canonical = new LibraryNavFacts[n];
        var recency = new Dictionary<string, long>();
        for (int i = 0; i < n; i++)
        {
            string uri = $"spotify:artist:{i:D2}";
            canonical[i] = F(uri, $"Artist{i:D2}");
            recency[uri] = 1_000_000; // identical stamp for every row
        }

        // A fixed permutation (not identity, not a simple reversal) standing in for "however the upstream join
        // happened to order this republish".
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = (i * 17 + 3) % n;
        var permuted = new LibraryNavFacts[n];
        for (int i = 0; i < n; i++) permuted[i] = canonical[perm[i]];

        var orderCanonical = LibraryNavOrder.Order(canonical, LibraryNavSort.Recents, desc: false, recency);
        var orderPermuted = LibraryNavOrder.Order(permuted, LibraryNavSort.Recents, desc: false, recency);

        string[] urisFromCanonical = new string[n];
        for (int i = 0; i < n; i++) urisFromCanonical[i] = canonical[orderCanonical[i]].Uri;
        string[] urisFromPermuted = new string[n];
        for (int i = 0; i < n; i++) urisFromPermuted[i] = permuted[orderPermuted[i]].Uri;

        Assert.Equal(urisFromCanonical, urisFromPermuted);
        // And since the titles were already alphabetical, the canonical result is simply source order 0..n-1.
        for (int i = 0; i < n; i++) Assert.Equal(i, orderCanonical[i]);
    }

    // ── 0.3: the persisted codes the pill writes and the sidebar reads ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, LibraryNavSort.Recents)]
    [InlineData(1, LibraryNavSort.RecentlyAdded)]
    [InlineData(2, LibraryNavSort.Alphabetical)]
    [InlineData(3, LibraryNavSort.Creator)]
    [InlineData(4, LibraryNavSort.ReleaseDate)]
    [InlineData(5, LibraryNavSort.Albums)]
    public void TheSortCodesAreTheWire(int code, LibraryNavSort sort) => Assert.Equal(code, (int)sort);

    [Fact]
    public void EverySortCodeHasItsOwnLabelKey_AndAnUnknownCodeReadsAsRecents()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int code = 0; code <= 4; code++)
        {
            string key = User.SortLabelKey(code);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(keys.Add(key), $"sort code {code} reuses the loc key {key}");
        }
        // An out-of-range code reads as Recents. (5 is a real sort since the rework — the artists rail's "albums" —
        // and its RAIL word is `LibraryWordRail.WordKey`; this pill's label table is the sidebar's, codes 0-4.)
        Assert.Equal(User.SortLabelKey(0), User.SortLabelKey(6));
        Assert.Equal(User.SortLabelKey(0), User.SortLabelKey(-1));
    }

    [Fact]
    public void TheViewCodes_TwoAndThreeAreGrids_ZeroAndTwoAreCompact()
    {
        Assert.False(User.IsGridView(0));
        Assert.False(User.IsGridView(1));
        Assert.True(User.IsGridView(2));
        Assert.True(User.IsGridView(3));
        Assert.True(User.IsCompactView(0));
        Assert.False(User.IsCompactView(1));
        Assert.True(User.IsCompactView(2));
        Assert.False(User.IsCompactView(3));
        // The pill's trailing glyph follows the grid/list split and nothing else.
        Assert.Equal(User.ViewGlyph(0), User.ViewGlyph(1));
        Assert.Equal(User.ViewGlyph(2), User.ViewGlyph(3));
        Assert.NotEqual(User.ViewGlyph(1), User.ViewGlyph(2));
    }
}
