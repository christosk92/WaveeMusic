// ── Home/DaylistSource.cs — pure, engine-free (owner G, wave 1) ────────────────────────────────────────────────────
//
// Role: CORE
// Owner: G
// Wave: 1
// Spec: docs/plans/wavee/home-rebuild-implementation.md §1 D6, §6.1
//
// D6: the live All feed has no `SectionKind.HomeSpotlight` band — the daylist card sits inside a Made-for-you
// (Personal-role) shelf like any other daily mix. `ZonePlanner.PlanAll` used to build the Daylist zone ONLY from a
// spotlight section (`ZonePlanner.cs:81`/`:306-311` pre-fix), so a feed with no spotlight band never showed a
// daylist card at all, even though the decoder already stages the daylist window/header for any daylist-format
// card (`Spotify.Decode.Home.cs:623-670`).
//
// `Find` is the pure lookup `ZonePlanner.PlanAll` calls BEFORE `PageDedupe` claims anything, so the hoisted card
// leaves its original shelf for free: `PageDedupe.Place`/the cover-shelf builder both gate on `placed.Add`, so
// claiming the card's `DedupeKey` here (in `ZonePlanner`, once `Find` has answered) removes it from wherever else
// it would otherwise have been folded.
//
// Order (the file banner's own words): the spotlight/daylist-role section's card first (matches the pre-fix
// behaviour when a spotlight band DOES exist — 06-facet-design.md's captured fixtures); else the first usable card
// anywhere whose raw format token is "daylist"; else null (no daylist zone at all).

using Wavee;

namespace Wavee.HomeUi;

/// <summary>Finds the one card the Daylist zone renders (D6). Engine-free and side-effect-free: it never mutates
/// <paramref name="sections"/> or claims a dedupe key itself — the caller (<see cref="ZonePlanner"/>) does that once
/// it has decided to use the answer.</summary>
public static class DaylistSource
{
    /// <summary>The daylist card plus the slot of the section it was found in (so the caller can carry over that
    /// section's server title/subtitle/total-count), or null when no section has one.</summary>
    public static (HomeCard Card, int SectionSlot)? Find(IReadOnlyList<SectionInput> sections)
    {
        // 1. A spotlight band's own card wins outright — this is the "daylist-role section" the pre-fix code
        //    already recognised (`SectionRoles.Of`: `HomeSpotlight` → `SectionRole.Daylist`), kept verbatim so a
        //    fixture that DOES ship a spotlight band behaves exactly as before.
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Kind != SectionKind.HomeSpotlight) continue;
            // Only a DAYLIST card: the live spotlight band also carries podcast episodes and editorial promos
            // (2026-09-27: "Is Anthropic Worth Two Trillion Dollars?"), which must never become the daylist card.
            for (int j = 0; j < s.Cards.Count; j++)
                if (SectionRoles.IsUsable(s.Cards[j]) && s.Cards[j].Format == "daylist") return (s.Cards[j], s.Slot);
        }

        // 2. No spotlight band: the first usable card anywhere (server order) whose format token is "daylist" —
        //    the card D6 needs to hoist out of whatever shelf (Made-for-you, a generic band, …) it landed in.
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            for (int j = 0; j < s.Cards.Count; j++)
            {
                var c = s.Cards[j];
                if (SectionRoles.IsUsable(c) && c.Format == "daylist") return (c, s.Slot);
            }
        }

        return null;
    }
}
