// ── Entities/User.NavMount.cs — the navigator's ONE mount identity (plan docs/plans/wavee/library-stabilization-plan.md
// §3.3, step A0) ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
// RC1/RC2/RC3 (the plan's root-cause section) trace the view-switcher bug to FIVE places that had to agree on what a
// navigator remount needs and did not share a type: the key string, the layout pick, the template pick, the wrapper
// padding, and the letters/strip decision. This file is the fix's foundation: ONE pure function
// (`LibraryNavMount.For`) that decides every one of those from (view, size, alphabetical, lettersKey) — nothing else,
// and in particular never `OrderKey`/`FactsKey`/`RowsKey` (a bound list refreshes those fields in place; see
// `LibraryNavOrder` and `FillRowVersions` in User.cs). `User.Page.Library.cs` (owned by another agent, coded against
// this file's public shape per §3.3) is the only caller; this file never touches the engine and is exercised entirely
// by `Wavee.Tests` without a live scope (no source-text tests: the decision is a value, not a grep target).
//
// `LibraryNavReadiness.IsPending` (step A4) is the twin decision for the navigator's shimmer boundary: it extends
// "nothing has answered" with "the a–z projection has been built over rows whose titles have not all landed" — sorting
// and grouping by a title that has not arrived yet is wrong data, not a smaller version of the right data (project
// rule: partial data without a readiness gate is a regression).
//
// Rules: engine-free (no FluentGpu types), no LINQ, no allocation beyond the two key strings a mount change requires.

using System.Globalization;

namespace Wavee;

// ── the navigator's mount identity ──────────────────────────────────────────────────────────────────────────────────

/// <summary>Which row/card shape a mount renders. Frozen at mount — see <see cref="LibraryNavMount"/>.</summary>
public enum LibraryNavTemplate : byte { Row, RowCompact, Card, CardCompact }

/// <summary>Whether the mount's layout is a measured-extent list or a fitted grid. Frozen at mount.</summary>
public enum LibraryNavLayoutKind : byte { Extents, GridFit }

/// <summary>Everything the page freezes when it mounts a fresh <c>ItemsView</c> for the navigator — the ONE owner
/// §3.3 calls for. <see cref="Key"/> is the only string the page keys the list's child slot on
/// (<c>ReconcileChildren</c> honors a key there, never on a single-child slot's root — RC1); everything else here is
/// a plain function of that same key's inputs, resolved once per key by the page's <c>NavMountCache</c>.
/// <para>What is deliberately NOT here: <c>OrderKey</c>, <c>FactsKey</c>, <c>RowsKey</c>, selection. A reorder, a
/// landed title/cover/count, or a selection move never needs a fresh mount — the bound source re-resolves them in
/// place (RC3). <c>LettersKey</c> is the one data-shaped exception, and only while the flat a–z projection is live:
/// the per-index extent SEED (<see cref="LibraryLetters"/>'s header/row offsets) is baked into the layout at mount, so
/// a header moving — a row added, removed or renamed under a–z — has to remount to reseed it.</para></summary>
/// <param name="Key">The ONLY remount key: identical across renders ⇒ the mounted `ItemsView` is reused (refresh);
/// different ⇒ `ReconcileChildren` removes the old list and mounts a fresh one.</param>
/// <param name="Template">Which row/card the bound list's slots build.</param>
/// <param name="Layout">Measured extents (list) or a fitted grid.</param>
/// <param name="RowExtent">Extents layouts only: the row's OUTER extent (plate + the bound list chrome's margin —
/// <see cref="User.NavRowExtent"/> / <see cref="User.NavRowCompactExtent"/>), the same number
/// <see cref="LibraryLetters"/> sums its offsets at.</param>
/// <param name="CellMin">GridFit layouts only: the cell's minimum main-axis extent — the S/M/L ladder
/// (<see cref="CardCellMinCompact"/>/<see cref="CardCellMinFull"/> plus one <see cref="CardCellStepCompact"/>/
/// <see cref="CardCellStepFull"/> step per size).</param>
/// <param name="Gap">The grid's fixed gap between cells (<see cref="GridGap"/>).</param>
/// <param name="Lettered">The flat a–z projection (headers interleaved with rows) is live: a LIST view under
/// alphabetical order. False in a grid — a grid has no headers, only the jump strip's first-card index.</param>
/// <param name="Strip">The A–Z jump strip is present: any view under alphabetical order, grid included.</param>
/// <param name="GridPadding">The wrapper padding a grid needs around its cells that a list does not
/// (`(8,8,8,0)` vs none) — the exact 8-DIP twitch RC1 traced to this bit changing while the list itself did not.</param>
/// <param name="ScrollKey">Restored scroll offset, keyed per LAYOUT FAMILY (list / letters / grid) rather than per
/// page-kind alone (RC4/F7): a list's offset is meaningless for a grid of the same entity, and reusing one `ScrollKey`
/// across families is exactly what put the selected row at an arbitrary edge after a sort change (D7).</param>
public readonly record struct LibraryNavMount(
    string Key,
    LibraryNavTemplate Template,
    LibraryNavLayoutKind Layout,
    float RowExtent,
    float CellMin,
    float Gap,
    bool Lettered,
    bool Strip,
    bool GridPadding,
    string ScrollKey)
{
    // The grid cell ladder (LayoutFor in User.Page.Library.cs today: `(compact ? 88f : 116f) + size * (compact ? 16f :
    // 24f)`) has no public const anywhere in the app — it was four inline literals at one call site. Canonical here
    // because this file is now the mount's one owner; User.Page.Library.cs's `LayoutFor`/`LayoutOf` should read these
    // instead of repeating the literals (reported to the page owner; not changed here — that file is owned elsewhere).
    /// <summary>Compact grid (view 0/2) cell minimum at size S (0): 88 DIP.</summary>
    public const float CardCellMinCompact = 88f;
    /// <summary>Full grid (view 1/3) cell minimum at size S (0): 116 DIP.</summary>
    public const float CardCellMinFull = 116f;
    /// <summary>Per-size-step increment, compact grid: S/M/L = 88/104/120.</summary>
    public const float CardCellStepCompact = 16f;
    /// <summary>Per-size-step increment, full grid: S/M/L = 116/140/164.</summary>
    public const float CardCellStepFull = 24f;
    /// <summary>The grid's fixed gap between cells, every size and density.</summary>
    public const float GridGap = 8f;

    /// <summary>The one mint. <paramref name="kind"/> is the page's route word ("artists"/"albums"/"podcasts" — the
    /// <c>ScrollKey</c>'s namespace); <paramref name="view"/>/<paramref name="size"/> are the persisted view-switcher
    /// signals (clamped defensively — a corrupt settings value must never index out of the ladder);
    /// <paramref name="alphabetical"/> is <c>sort == LibraryNavSort.Alphabetical</c>; <paramref name="lettersKey"/> is
    /// <see cref="LibraryLetters.Key"/>() from the CURRENT build, 0 when a–z is not the sort (its own folding: it never
    /// enters the key unless <paramref name="alphabetical"/> AND the view is not a grid).</summary>
    public static LibraryNavMount For(string kind, int view, int size, bool alphabetical, ulong lettersKey)
    {
        view = Math.Clamp(view, 0, 3); size = Math.Clamp(size, 0, 2);
        bool grid = User.IsGridView(view), compact = User.IsCompactView(view), lettered = alphabetical && !grid;
        // Order / facts / rows are NOT here: a bound list refreshes them in place. LettersKey is here ONLY while the
        // flat projection is live, because header positions are baked into the per-index extent seed.
        string key = "nav:" + view + ":" + (grid ? size : 0) + (lettered ? ":L" + lettersKey.ToString("x16", CultureInfo.InvariantCulture) : ":F");
        string family = grid ? "grid" : lettered ? "letters" : "list";
        return new(key,
            grid ? (compact ? LibraryNavTemplate.CardCompact : LibraryNavTemplate.Card)
                 : (compact ? LibraryNavTemplate.RowCompact : LibraryNavTemplate.Row),
            grid ? LibraryNavLayoutKind.GridFit : LibraryNavLayoutKind.Extents,
            compact ? User.NavRowCompactExtent : User.NavRowExtent,
            (compact ? CardCellMinCompact : CardCellMinFull) + size * (compact ? CardCellStepCompact : CardCellStepFull),
            GridGap,
            lettered, alphabetical, grid,
            "lib:nav:" + kind + ":" + family);
    }
}

// ── the navigator's shimmer gate ────────────────────────────────────────────────────────────────────────────────────

/// <summary>The navigator's loading-boundary rule (step A4), pure over the facts <c>SyncNavLoad</c> already has to
/// hand. Extracted the same way <c>Screens/Setup.cs</c>'s <c>Gating</c> class is (CLAUDE.md's "SetupGating" pattern):
/// a UI-thread effect calls one function and flips a <c>Loadable</c> from its answer, and the truth table is a test
/// rather than a per-render probe.</summary>
public static class LibraryNavReadiness
{
    /// <summary>Pending (shimmer stays up) while EITHER:
    /// <list type="bullet">
    /// <item>the relation itself has not answered — today's rule, unchanged: nothing shown, no filter typed, the edge
    /// still <c>EdgeState.Unknown</c>;</item>
    /// <item>OR the sort is alphabetical and not every listed row's title has landed yet, and no row's demand has
    /// FAILED. Sorting/grouping by a–z over an unknown title is wrong data (it would file under the wrong letter, or
    /// under '#', and then jump bands the moment the real title lands — exactly the remount storm A2 closes for
    /// `OrderKey`, reopened through the grouping instead) — project rule: partial data without a readiness gate is a
    /// regression.</item>
    /// </list>
    /// A failed row demand outranks the alphabetical gate on purpose (mirrors <see cref="AlbumPaneReadiness"/>): a
    /// title that will never land must not hold the whole navigator in a shimmer forever, so the list opens sorted by
    /// whatever letters DID land — the visible bug (a row briefly under '#') is smaller than the invisible one (a
    /// permanently blank navigator).
    /// <para><paramref name="count"/>/<paramref name="titlesKnown"/> are both vacuously satisfied by an empty (or
    /// fully filtered-out) row set, so an answered, empty library or a filter with no matches never gets stuck on the
    /// alphabetical arm — <c>count == 0</c> already means "every one of the zero rows knows its title".</para></summary>
    public static bool IsPending(int count, bool hasFilter, bool answered, bool alphabetical, bool titlesKnown, bool anyFailed)
        => (count == 0 && !hasFilter && !answered)
        || (alphabetical && !titlesKnown && !anyFailed);
}
