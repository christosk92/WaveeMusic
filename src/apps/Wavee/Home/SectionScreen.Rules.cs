// ── Home/SectionScreen.Rules.cs ────────────────────────────────────────────────────────────────────────────────────
// The tiny pure rules the section drill page (`SectionScreen.Host.cs`) needs: the page's load state (`SectionScreenLoadRule`: pending / ready /
// empty / failed from the section row's facts, so the walk landing — or a seal — reaches the region reactively, RCA
// 2026-09-30) and its hero title (`SectionScreenTitle`: the route arg first).
//
// There is no paging rule here, and there must never be one again: the page demands its section WHOLE
// (`SectionFields.Whole`) and the QUERY layer walks the section to its end (Spotify/Spotify.Api.Browse.cs, `BrowseWalk`)
// — the "no page-side fetch windows" rule. The near-tail / auto-page / walk-fraction rules that lived here went with the
// page-side pager.
//
// Role: CORE (pure; no engine, no tables, no I/O)
// Owner: B5
// Wave: 3
// Spec: docs/plans/wavee/home-redesign-implementation.md — Workstream H, New files table row `SectionScreen.UI.cs`;
//   "Pure rules" section (test files live under Wavee.Tests/HomeUi/, no source-text tests).

namespace Wavee.HomeUi;

/// <summary>The drill page's load state — what its skeleton region shows: the shimmer (<see cref="Pending"/>), the
/// grid (<see cref="Ready"/>), the empty card (<see cref="Empty"/>: the section answered with no cards) or the error card
/// (<see cref="Failed"/>).</summary>
public enum SectionScreenLoad : byte { Pending, Ready, Empty, Failed }

/// <summary>Decides <see cref="SectionScreenLoad"/> from the section row's facts. The page demands the row's identity AND
/// its WHOLE card list (<c>SectionFields.Identity | Whole</c>). Until the query layer's walk has landed it, the page shows the cards the
/// row ALREADY holds (the ones the section showed on Home) with a blank held for every card still to come (<paramref name="held"/>): the
/// drill opens as the section the user clicked, grown, never as an empty page that fills in (owner, 2026-11-18). A first page is never
/// shown AS the whole list: the blanks say what is missing, and the row's total counts them.
/// Derived, never latched: a re-ask (the error card's Retry, or a band a feed refresh took back) reads Pending again, and
/// its answer reads Ready. Precedence: no row → Failed; the whole list → Ready / Empty; a request on the wire → Pending;
/// the ask terminally failed, or was sealed with no answer while Online → Failed; otherwise Pending.</summary>
public static class SectionScreenLoadRule
{
    /// <param name="rowValid">The route names a section row at all (a blank uri never will).</param>
    /// <param name="whole">The row knows its identity AND its whole card list (the walk landed).</param>
    /// <param name="cardCount">The cards the whole list holds (the page's grid source).</param>
    /// <param name="failed">The row's last ask for the demanded groups terminally failed (<c>Table.Failed</c>).</param>
    /// <param name="inflight">A request for the row is on the wire.</param>
    /// <param name="asked">The demanded groups have been asked this scope.</param>
    /// <param name="online">The session is Online — an unanswered ask only means "failed" when one could have answered.</param>
    /// <param name="held">Cards the page already shows from the row's first page while the walk runs (0: none): it reads Ready on them.</param>
    public static SectionScreenLoad Of(bool rowValid, bool whole, int cardCount, bool failed, bool inflight, bool asked,
                                       bool online, int held = 0)
    {
        if (!rowValid) return SectionScreenLoad.Failed;
        if (whole) return cardCount > 0 ? SectionScreenLoad.Ready : SectionScreenLoad.Empty;
        if (held > 0) return SectionScreenLoad.Ready;   // the section as it stood on Home; the rest arrives (or does not) under it
        if (inflight) return SectionScreenLoad.Pending;
        if (failed) return SectionScreenLoad.Failed;
        if (asked && online) return SectionScreenLoad.Failed;   // sealed without an answer
        return SectionScreenLoad.Pending;
    }
}

/// <summary>The cards a drill page shows while its whole list is on its way: the row's own first cards, then a blank for each card the
/// row's total says is still to come (none once the walk has failed: the page keeps what it has). The blanks take the kind of the last
/// known card, so a placeholder is shaped like its neighbours.</summary>
public static class SectionScreenHeld
{
    /// <summary>The most blanks a page holds: a section's total is the wire's word and a grid of thousands of placeholders helps nobody.</summary>
    public const int MaxBlanks = 60;

    public static HomeCard[] Of(IReadOnlyList<HomeCard> known, int total, bool stillComing)
    {
        int have = known.Count;
        int blanks = stillComing ? Math.Clamp(total - have, 0, MaxBlanks) : 0;
        var cards = new HomeCard[have + blanks];
        for (int i = 0; i < have; i++) cards[i] = known[i];
        HomeCardKind kind = have > 0 ? known[have - 1].Kind : HomeCardKind.Playlist;
        for (int i = 0; i < blanks; i++) cards[have + i] = HomeCard.Blank(kind, have + i);
        return cards;
    }
}

/// <summary>The drill page's hero title: the route's arg (what the user clicked, trimmed) when present, else the row's
/// own live title, else empty.</summary>
public static class SectionScreenTitle
{
    public static string Of(string? routeArg, string? liveTitle)
    {
        if (!string.IsNullOrWhiteSpace(routeArg)) return routeArg.Trim();
        if (!string.IsNullOrWhiteSpace(liveTitle)) return liveTitle.Trim();
        return "";
    }
}
