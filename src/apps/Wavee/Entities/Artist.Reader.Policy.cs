// ── Entities/Artist.Reader.Policy.cs — the reader's mount identity + catalogue gate (plan
// docs/plans/wavee/library-stabilization-plan.md §5 wave 2, step R0) ────────────────────────────────────────────────
//
// RC6/RC7 trace the reader's flashing to the list's key carrying `_generation`, a counter bumped on every data-driven
// reshape that is not a pure append (a facet landing out of date order, a re-sort when the sort key changes under an
// already-listed album). That conflates "the user changed what they are looking at" (a fresh artist, scope or sort —
// genuinely needs a fresh mount: different rows, different measured-extent table) with "more data arrived for the
// same list" (never needs one: the keyed-child diff inside an unchanged `ItemsView` already swaps exactly the blocks
// whose album changed — R2's job in `Artist.Reader.cs`). `MountKey` is what "the user changed what they are looking
// at" reduces to once `_generation`/`_order` are gone: the four inputs that are frozen at mount, nothing else.
//
// `CatalogueReady` is R3's gate: scope 1 ("all releases") unions three facets (albums/singles/compilations) sorted
// together, so showing the group before every facet has answered means it starts sorted by whichever facet's page
// landed first and gets progressively re-sorted as the other two arrive — R3 holds it back to library-only rows until
// every facet has EITHER answered or its own ask failed, then it lands once, as an append below the library group.
//
// Rules: engine-free (no FluentGpu types); `EdgeState` lives in the app assembly (Entities/Edges.cs) and is used here
// directly, so no bool-triple fallback is needed. Pure, no allocation, no I/O.

namespace Wavee;

/// <summary>The reader's mount identity and its catalogue readiness gate — the two decisions <c>Artist.Reader.cs</c>
/// (owned by another agent, wave 2) reads instead of re-deriving them inline.</summary>
public static class ReaderMountPolicy
{
    /// <summary>The list remounts for what the USER changed and what the layout freezes — never for data landing.
    /// <paramref name="artist"/>/<paramref name="scope"/>/<paramref name="sort"/> select which rows exist and in what
    /// order they are asked for; <paramref name="narrow"/> selects the block layout (narrow vs wide column). A facet
    /// landing, a re-sort of already-listed rows, or a block's own data changing must never move this key — those are
    /// in-place reshapes of the SAME mount, exactly like a library navigator reorder (RC3's sibling rule).</summary>
    public static string MountKey(int artist, int scope, int sort, bool narrow)
        => artist + ":" + scope + ":" + sort + ":" + (narrow ? "n" : "w");

    /// <summary>Scope 1 ("all releases") shows the catalogue group only once all three facets' FIRST pages have
    /// answered (or failed): before that, `Compute` (R3) withholds the catalogue span and the list is library rows
    /// only, so the catalogue lands ONCE, as an append below them, never interleaved facet-by-facet. A facet whose ask
    /// FAILED counts as settled here (not <c>Unknown</c>) — a permanently-failing facet must not hold the other two,
    /// already-answered facets off the screen forever; the gate is "everyone has said something", not "everyone
    /// succeeded".</summary>
    public static bool CatalogueReady(EdgeState albums, EdgeState singles, EdgeState compilations,
                                      bool albumsFailed, bool singlesFailed, bool compilationsFailed)
        => (albums != EdgeState.Unknown || albumsFailed) && (singles != EdgeState.Unknown || singlesFailed)
        && (compilations != EdgeState.Unknown || compilationsFailed);
}
