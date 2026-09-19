using System;
using System.Collections.Generic;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// LibraryNavOrder is the one comparator set behind every "Recents"/"Recently added"/"Alphabetical"/"Creator"/
// "Release date" list in Your Library (artists, albums, podcasts, discography). It is also the source of the
// navigator's remount key (OrderKey/FactsKey) — the #E fix depends on both being pure functions of the ROWS,
// never of selection or of how the upstream store happened to permute ties.
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
}
