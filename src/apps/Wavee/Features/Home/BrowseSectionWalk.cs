using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>One browseSection page folded onto a <see cref="HomeSection"/> — the same arithmetic
/// <see cref="HomeSectionPage"/> and the captured Weekly Song Charts walk use. Engine-free (Wavee.Core + the
/// <see cref="HomeSectionPaging"/> / <see cref="HomeBrowseCards"/> mappers) so tests drive the real fold.</summary>
static class BrowseSectionWalk
{
    public readonly record struct Step(HomeSection Section, int? NextOffset, bool Exhausted);

    /// <summary>How a Charts walk begins given an optional preview seed. Engine-free so tests pin the Weekly
    /// handoff: a seed with cards must be published before the next offset is asked; an empty HasMore seed must
    /// not be published (fetch offset 0 instead). A non-empty seed is never treated as complete here —
    /// <see cref="HomeSection"/> drops the browse cursor, and <c>totalCount</c> under-reports often enough that
    /// <see cref="HomeSectionPaging.HasMore"/> would freeze Weekly at the first 20 cards.
    /// Featured (4 of 4) still publishes, then asks offset 4 once; Fold / empty / PagingComplete stop the walk.</summary>
    public readonly record struct Start(HomeSection? Current, int Offset, bool Exhausted, bool Publish, bool FetchFirst);

    /// <summary>Decide the first published section and the first offset to ask, without touching the network.
    /// <see cref="Start.Publish"/> is the UI handoff: the page must <c>SetReady</c> that <see cref="Start.Current"/>
    /// before the walk awaits the next page, or a 74-item Weekly section stays blank until offset 20 returns.</summary>
    public static Start Begin(HomeSection? seed)
    {
        if (seed is null)
            return new Start(null, 0, false, false, true);
        // Metadata-only preview (a Charts-page shelf header with totalCount but no inline cards): do not paint an
        // empty Ready grid — fetch page 0 like an unseeded walk.
        if (seed.Cards.Count == 0)
            return HomeSectionPaging.HasMore(seed)
                ? new Start(null, 0, false, false, true)
                : new Start(seed, 0, true, true, false);
        return new Start(seed, HomeSectionPaging.NextOffset(seed), false, true, false);
    }

    /// <summary>Map <paramref name="page"/> onto <paramref name="current"/> at the offset we asked for. Empty pages
    /// and the three HomeSectionPaging terminators (no cursor, cursor cannot advance, no new cards) all latch
    /// <see cref="Step.Exhausted"/>.</summary>
    public static Step Fold(HomeSection current, int requestedOffset, BrowseSection page)
    {
        if (page.Cards.Count == 0)
            return new Step(current, null, true);

        var mapped = new HomeCard[page.Cards.Count];
        for (int i = 0; i < mapped.Length; i++)
            mapped[i] = HomeBrowseCards.Card(page.Cards[i]);
        var next = HomeSectionPaging.Append(current, mapped, page.Total);
        int? nextOffset = HomeSectionPaging.BrowseSectionNextOffset(requestedOffset, page);
        bool exhausted = !HomeSectionPaging.CanAdvance(requestedOffset, nextOffset)
                         || !HomeSectionPaging.Progressed(current, next);
        return new Step(next, exhausted ? null : nextOffset, exhausted);
    }
}

/// <summary>Title filter for a Charts grid. Ordinal-ignore-case substring; the span is what the library
/// highlight pill paints.</summary>
static class ChartTitleMatch
{
    public static bool TryFind(string? title, string? query, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (title is not { Length: > 0 }) return false;
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        int i = title.IndexOf(q, System.StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        start = i;
        length = q.Length;
        return true;
    }

    public static IReadOnlyList<HomeCard> Filter(IReadOnlyList<HomeCard> cards, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return cards;
        var hits = new List<HomeCard>(cards.Count);
        for (int i = 0; i < cards.Count; i++)
            if (TryFind(cards[i].Title, query, out _, out _)) hits.Add(cards[i]);
        return hits;
    }
}
