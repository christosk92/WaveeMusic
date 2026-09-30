// ── Home/SectionScreen.Rules.cs ────────────────────────────────────────────────────────────────────────────────────
// The tiny pure rules `Home/SectionScreen.UI.cs` needs: the page's load state (`SectionScreenLoadRule`: pending / ready /
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
/// its WHOLE card list (<c>SectionFields.Identity | Whole</c>), so it reads Pending until the query layer's walk has landed
/// it — a feed's or a page's first page of cards is never shown as the drill (partial data behind no readiness gate).
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
    public static SectionScreenLoad Of(bool rowValid, bool whole, int cardCount, bool failed, bool inflight, bool asked,
                                       bool online)
    {
        if (!rowValid) return SectionScreenLoad.Failed;
        if (whole) return cardCount > 0 ? SectionScreenLoad.Ready : SectionScreenLoad.Empty;
        if (inflight) return SectionScreenLoad.Pending;
        if (failed) return SectionScreenLoad.Failed;
        if (asked && online) return SectionScreenLoad.Failed;   // sealed without an answer
        return SectionScreenLoad.Pending;
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
