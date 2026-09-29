// ── Home/Reveal.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// Home's facet load state and the shell-wash accent pick — two small pure rule sets Wave 1 owns alongside
// Home/LayoutFile.cs. Written FRESH: no reference to the old HomeRevealGate / HomeFeedReadiness / HomeWashSource
// (Entities/Home.Rules.cs, Entities/Home.Host.cs) — those were read only to learn the shape of the problem (accents
// are `uint` ARGB on HomeCard, `Shell.For` is the one route composer). Every type here is engine-free and pure: no
// hooks, no elements, no I/O.
//
// Fifth pass (docs/plans/wavee/home-rebuild-implementation.md, "Fifth pass — stock controls"): CardNav (open/play
// routing for a HomeCard) is DELETED — card open/play now goes through the app-wide `HomeCardNav`
// (Entities/Home.Rules.cs §12 + Entities/Browse.Cards.cs), the same one Browse/Search already ride.
//
// Role: CORE
// Owner: A5
// Wave: 1
// Spec: docs/plans/wavee/home-redesign-implementation.md — Workstream H, "Rebuilt from scratch" (ScreenLoad,
//   WashPick), and the Wave 1 row (A5 `Reveal.cs`+`LayoutFile.cs`).

using FluentGpu.Signals;   // LoadState

namespace Wavee.HomeUi;

// ══ 1. SCREEN LOAD — the facet document's Loadable state ═════════════════════════════════════════════════════════

/// <summary>Projects a facet's Home row onto its <c>Loadable&lt;ScreenModel&gt;</c> (the engine's <c>Skel.Region</c>
/// then owns shimmer, reveal and swap — no hand-rolled reveal clock). Rules: a document with zones is shown at once
/// (a cached answer is real content); zero zones stays Pending until the live attempt concludes, so a provisional
/// EMPTY page never flashes — nor does an unanswered row while the session is still coming up; a concluded attempt that never produced sections while online is Failed (offline, the
/// cached/empty answer stands). Uses the engine's own <see cref="LoadState"/> (Pending/Ready/Failed) rather than a
/// local twin (remediation F35).</summary>
public static class ScreenLoad
{
    /// <param name="sessionStarting">The session has not reached Online yet and has not failed (Offline..Minting).
    /// Offline is also the pre-login resting phase, so an unanswered row read there is "not asked YET", never an
    /// answered empty feed.</param>
    public static LoadState Of(int zoneCount, bool knowsSections, bool concluded, bool online, bool sessionStarting)
    {
        if (zoneCount > 0) return LoadState.Ready;
        if (!knowsSections && sessionStarting) return LoadState.Pending;
        if (!concluded) return LoadState.Pending;
        if (!knowsSections && online) return LoadState.Failed;
        return LoadState.Ready;   // a real, answered empty facet
    }
}

// ══ 2. WASH PICK — the shell wash accent source order ══════════════════════════════════════════════════════════════

/// <summary>Picks Home's shell wash colour: the daylist card's own accent first (the strongest, most "this page"
/// signal), then the first cover's graded accent, else the caller's fallback (the system accent, typically). Pure
/// over already-resolved `uint` ARGB values — the graded-cover lookup itself (image → <c>Scheme</c> → accent, the old
/// <c>HomeWashSource.Pick</c>'s tier 2) is an async/UI concern the caller resolves before calling this, exactly as
/// <c>HomeCard.Accent</c> is itself a plain <c>uint</c> column (Entities/Home.cs) with 0 meaning "no accent set" —
/// there is no tier 3 (old code's comment: "NOTHING third"); a missing accent falls through to the fallback.</summary>
public static class WashPick
{
    public static uint Pick(uint daylistAccent, uint firstCoverAccent, uint fallback)
    {
        if (daylistAccent != 0u) return daylistAccent;
        if (firstCoverAccent != 0u) return firstCoverAccent;
        return fallback;
    }
}

