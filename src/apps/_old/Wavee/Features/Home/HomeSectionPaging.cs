using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>The cursor arithmetic behind the Home section page's "Show all". Pure over <see cref="HomeSection"/> — no
/// services, no elements — because the whole defect class here is arithmetic.
/// <para>The load-bearing distinction is RAW vs DEDUPED. <see cref="HomeSection.Cards"/> is deduplicated (the ledger
/// contract in <c>HomeFeed.cs</c>: <c>RawItemCount == Cards.Count + UnsupportedCount + DuplicateCount</c>), while the
/// server's cursor counts everything it sent. Paging by <c>Cards.Count</c> therefore walks the cursor BACKWARDS by
/// exactly the number of items we dropped, re-fetching what we just discarded; a page that is entirely already-seen
/// URIs does not advance <c>Cards.Count</c> at all, so the offset — and the whole request — repeats forever while
/// <c>TotalCount &gt; Cards.Count</c> keeps the button armed. Every quantity below is the RAW one.</para>
/// <para>The second defect class is TERMINATION, and it is why <see cref="CanAdvance"/> exists. Measured across the 31
/// sections of a captured Home: the returned item count disagreed with <c>totalCount</c> in 7 of them (8 items /
/// totalCount 9 / <c>nextOffset: null</c>), and a COMPLETE section can answer <c>nextOffset: 0</c> (6 items / totalCount
/// 6). So <c>totalCount</c> is an arming hint, never a terminator, and a cursor is only a cursor when it points PAST the
/// offset that produced it.</para></summary>
static class HomeSectionPaging
{
    /// <summary>The offset to request next: the raw number of items the endpoint has already handed us, duplicates and
    /// unsupported entries included. Floored at <c>Cards.Count</c> so a section whose source under-reported its raw
    /// count (or left it at zero) still asks for the page AFTER what it is showing rather than re-reading page one.
    /// </summary>
    public static int NextOffset(HomeSection section) => Math.Max(section.RawItemCount, section.Cards.Count);

    /// <summary>Whether the server still has items past our cursor, and therefore whether "Show all" stays armed.
    /// <para><paramref name="serverNextOffset"/> is the server's own <c>pagingInfo.nextOffset</c> from the last page we
    /// fetched, and it WINS when we have one: <c>totalCount</c> is under-reported often enough that trusting it here is
    /// what leaves a dead button on screen. It is compared against <see cref="NextOffset"/> rather than being required
    /// to exceed it, because a well-behaved cursor equals the raw position it left us at (offset 20 + 20 items →
    /// <c>nextOffset: 20</c> means "ask for 20 next", not "you are done").</para>
    /// <para>Null means we have NO cursor — a section seeded from Home's inline response, which carries a total and no
    /// paging info — and only then does the total serve as the arming hint. Compared against the RAW count so it agrees
    /// with <see cref="NextOffset"/>: measuring the server's total against our deduped count claims there is more to
    /// fetch for as long as we have dropped anything, which is what armed the no-progress loop.</para></summary>
    public static bool HasMore(HomeSection section, int? serverNextOffset = null) =>
        serverNextOffset is int next ? next >= NextOffset(section) : section.TotalCount > NextOffset(section);

    /// <summary>Whether a fetched page's server cursor can carry us forward from the offset that produced it.
    /// <para>Null → the section is complete, full stop. A value at or behind <paramref name="requestedOffset"/> says the
    /// same thing in a different dialect: a complete section answers <c>nextOffset: 0</c>, and honouring that as a
    /// cursor would re-request page one forever. This — not <c>loaded &lt; totalCount</c> — is the terminator.</para>
    /// </summary>
    public static bool CanAdvance(int requestedOffset, int? nextOffset) =>
        nextOffset is int next && next > requestedOffset;

    /// <summary>Fold a fetched page into the section: append the URIs we have not seen, and advance the raw cursor by
    /// the FULL page — duplicates included. That is the no-progress guard: a page that contributes zero new cards still
    /// moves the cursor, so the next click asks for the following page instead of re-issuing the same request. (A page
    /// with zero items cannot advance anything; the caller latches "exhausted" for that case, since the section carries
    /// no such flag and <see cref="HomeSection.TotalCount"/> is the server's number, not ours to rewrite.)
    /// <para>An ALL-duplicate page is the same dead end one step later: the cursor moved but the user sees nothing new,
    /// so <see cref="Progressed"/> is the caller's second terminator.</para></summary>
    public static HomeSection Append(HomeSection current, IReadOnlyList<HomeCard> pageCards, int pageTotal)
    {
        int raw = NextOffset(current);
        var seen = new HashSet<string>(current.Cards.Count + pageCards.Count, StringComparer.OrdinalIgnoreCase);
        var cards = new List<HomeCard>(current.Cards.Count + pageCards.Count);
        foreach (var card in current.Cards) { seen.Add(card.Uri); cards.Add(card); }
        int duplicates = 0;
        for (int i = 0; i < pageCards.Count; i++)
        {
            var card = pageCards[i];
            if (seen.Add(card.Uri)) cards.Add(card); else duplicates++;
        }
        return current with
        {
            Cards = cards,
            TotalCount = Math.Max(current.TotalCount, pageTotal),
            RawItemCount = raw + pageCards.Count,
            DuplicateCount = current.DuplicateCount + duplicates,
        };
    }

    /// <summary>How far along an eager walk is, as 0..1 — the Charts progress bar's value.
    /// <para>The load-bearing case is an UNTRUSTWORTHY total. <c>HomeBrowseCards.Section</c> floors
    /// <see cref="HomeSection.TotalCount"/> at the card count, and the walk exists precisely because that total
    /// under-reports, so a total at or below what we already hold says "unknown", never "finished": treating it as the
    /// denominator paints a FULL bar on a 20-card seed while three more pages are in flight, then falls back to ~54%
    /// when the next one lands. Such a total is worth <paramref name="pageAssumed"/> more items instead. The caller
    /// writes 1f explicitly on its terminal latch — this never reports done on its own.</para></summary>
    public static float WalkFraction(HomeSection section, int pageAssumed)
    {
        int have = section.Cards.Count;
        int total = section.TotalCount > have ? section.TotalCount : have + Math.Max(1, pageAssumed);
        return total <= 0 ? 0f : have / (float)total;
    }

    /// <summary>Did an <see cref="Append"/> put anything new on screen? Named rather than inlined because it is a
    /// TERMINATION signal, not a cosmetic check: a page the dedup ate entirely means clicking again can only produce the
    /// same nothing, however healthy the server's cursor and total look.</summary>
    public static bool Progressed(HomeSection before, HomeSection after) => after.Cards.Count > before.Cards.Count;

    /// <summary>BrowseSection has no server cursor — synthesize one from the offset we asked for plus how many cards
    /// came back, versus <c>totalCount</c>. Null means this page exhausted the total (or the total is unknown/zero).
    /// </summary>
    public static int? BrowseNextOffset(int requestedOffset, int pageCount, int total)
    {
        int loaded = requestedOffset + pageCount;
        return total > loaded ? loaded : null;
    }

    /// <summary>Resolve a fetched <see cref="BrowseSection"/> page's next cursor, preferring the server's own value
    /// over the synthesized one. <see cref="BrowseSection.NextOffset"/> is a tri-state (see its doc comment): a real
    /// offset passes straight through; <see cref="BrowseSection.PagingComplete"/> — an EXPLICIT server terminator —
    /// resolves to <c>null</c> outright, even when <c>page.Total</c> still claims more (never fall back to
    /// <see cref="BrowseNextOffset"/> in that case, or a section the server has already finished serving would stay
    /// armed forever off an unreliable total — the same trap <see cref="HasMore"/> exists to avoid for Home's own
    /// sections); a plain <c>null</c> — no <c>pagingInfo</c> came back at all — is the ONLY case that falls back to
    /// the synthesized offset+count-vs-total cursor.</summary>
    public static int? BrowseSectionNextOffset(int requestedOffset, BrowseSection page) => page.NextOffset switch
    {
        BrowseSection.PagingComplete => null,
        { } v => v,
        null => BrowseNextOffset(requestedOffset, page.Cards.Count, page.Total),
    };
}
