using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Wavee.Core.Sidebar;

namespace Wavee;

// The Curated sidebar's RENDER CONTRACT: a SidebarCustomLayout plus the live projection, planned into ONE flat row list.
//
// WHY FLAT. The pane is a single scroll surface. A PlaylistTree or EntityList over a 10k library cannot virtualize as a
// Grow=1 child inside an outer ScrollView, so the document is planned into one flat SidebarRow[] (headers, dividers,
// rows, grid strips, cards, prompts, placeholders, empty/skeleton rows) and rendered by ONE ItemsView.CreateBound with
// RepeatLayout.VariableList — which virtualizes the whole pane end-to-end regardless of section count or library size.
//
// WHY APP-SIDE. The spec places the planner in Wavee.Core; it cannot live there, because it consumes SidebarLibraryEntry
// (Features/Sidebar/Data/SidebarLibraryEntry.cs) and Wavee.Core may not reference the app assembly. The planner is still
// PURE — no engine type, no service, no signal, no clock — so Wavee.Tests drives it by source-including this one file.
//
// ALLOCATION. No string is allocated during planning: a row's Key is always an EXISTING string (the entry's Id, the
// item's Key, or the section's Id), never a concatenation. Row/entry storage is a caller-owned SidebarPlanBuffers, so a
// re-plan reuses capacity and a warm re-plan of a 10k library allocates nothing.

/// <summary>The row vocabulary the Curated renderer switches on. Also the ItemsView's <c>ContentType</c>, so each kind
/// gets its own recycling pool.</summary>
public enum SidebarRowKind : byte
{
    SectionHeader = 0,   // clickable, toggles Collapsed
    HeaderLabel   = 1,   // Kind == Header (no chevron)
    Divider       = 2,
    IconRow       = 3,   // glyph + label (+ optional count badge)
    EntityRow     = 4,   // artwork + label (+ optional subtitle)
    FolderHeader  = 5,   // a PlaylistTree folder; indent-aware, clickable
    GridStrip     = 6,   // one row of a grid section: [EntryIndex, ItemCount] into the plan's entries
    Placeholder   = 7,   // a missing entity (fallback title/art, dimmed)
    Empty         = 8,   // a section resolved to zero rows
    Skeleton      = 9,   // the section's source is still pending
    // 10 was CreateAction — the "+" ROW a PlaylistTree section used to own at its end. DELETED: the affordance is now
    // the section HEADER's "+" (SidebarPaneConfig.HeaderCreate) plus the rail's, which is where Library V3 already put
    // it. The VALUE is left unused rather than reclaimed: the kind is the ItemsView's ContentType (one recycle pool per
    // kind) and the planner tests pin the row SEQUENCE, so renumbering would silently re-pool every existing row.
    EntityCard    = 11,  // the EntityEmbed hero card: taller row, cover-left, play affordance
    PromptRow     = 12,  // an actionable degraded state (e.g. Concerts' "Set your location" row)
    // PHASE 2 / Decision B — EDIT MODE ONLY. One uniform-height header card standing in for a whole section while the
    // pane is the customize canvas: grip · kind glyph · title · count · eye · "…". APPENDED, never inserted: the kind
    // is the ItemsView's ContentType (one recycle pool per kind) and the tests pin the row SEQUENCE, so renumbering
    // would silently re-pool every existing row. `ItemCount` carries the card's honest count (-1 = none — see
    // SidebarEditPlan.CardCount); `EntryIndex` stays -1, like every other chrome row.
    SectionCard   = 13,
    // The PlaylistTree's closing gutter (24 DIP), planned directly after the last tree row and BEFORE the create row.
    // It exists for exactly one reason: "top level, at the end" had no target at all. The create row occupied that spot
    // and ACCEPTED rootlist payloads, so dragging a playlist below everything duplicated it into a new playlist instead
    // of moving it (D3). This row owns that slot as a whole-row `EndOfList`, and the create row is now transparent to
    // rootlist payloads. APPENDED, never inserted: the kind is the ItemsView's ContentType (one recycle pool per kind).
    TreeEnd       = 14,
}

/// <summary>POD. No strings are allocated during planning: labels resolve at render time from the referenced
/// section/item/entry, never copied into the plan.
///
/// <para><b>How a row joins back to its data.</b> PROJECTED rows (Pinned, JumpBackIn, EntityList, PlaylistTree,
/// NewReleases, Concerts) carry <c>EntryIndex &gt;= 0</c> into <see cref="SidebarRowPlan.Entries"/> and
/// <c>Key == entry.Id</c>. HAND-PLACED rows (CollectionShortcuts / StaticLinks / CustomGroup items, the EntityEmbed
/// card) carry <c>Key == item.Key</c> — the route name or uri, unique within its section — and <c>EntryIndex</c> is the
/// resolved entry when the projection knows the entity, or -1 when it does not (the missing-entity retention path:
/// render from the item's FallbackTitle/FallbackImageUrl, dimmed). Chrome rows (headers, dividers, empty, skeleton,
/// create, prompt) carry <c>Key == section.Id</c> and <c>EntryIndex == -1</c>.</para></summary>
public readonly record struct SidebarRow(
    SidebarRowKind Kind,
    string SectionId,
    byte Depth,          // 0 top-level, 1 inside a CustomGroup / rootlist folder, 2 deeper folder nesting…
    int EntryIndex,      // index into the plan's entry list; -1 when not applicable
    int ItemCount,       // GridStrip only: how many entries this strip draws
    string Key);         // the stable reconciler/selection key

/// <summary>The planner's output. <paramref name="Entries"/> is the flattened, per-section-ordered entry slices the rows
/// index into; <paramref name="Revision"/> is echoed from the input so the ItemsView can key its DepKey on it.</summary>
public readonly record struct SidebarRowPlan(
    IReadOnlyList<SidebarRow> Rows,
    IReadOnlyList<SidebarLibraryEntry> Entries,
    int Revision);

/// <summary>A source's readiness. Ordered so <c>default</c> is <c>Ready</c> — a <c>default(SidebarProjectionInput)</c>
/// must plan real (empty) content, not a screenful of skeletons.</summary>
public enum SidebarSourceState : byte { Ready = 0, Pending = 1, Error = 2 }

/// <summary>One <c>SidebarSectionKind.Extension</c> section's resolved rows: a WINDOW into
/// <see cref="SidebarProjectionInput.ExtensionEntries"/> plus the health/availability the binder observed.
///
/// <para>This is the whole planner-side extension contract, and it keeps the planner PURE: the binder resolves the
/// contribution id through the registry, fills the shared entry pool and records this struct; the planner only reads it.
/// The planner therefore never sees an extension id and never <c>switch</c>es on one (the M3 forward-compat
/// guardrail).</para></summary>
/// <param name="NeedsPrompt">The source's degraded state is ACTIONABLE (Concerts with no location) — the section plans one
/// <c>PromptRow</c> instead of an empty caption, even though the source itself is Ready.</param>
public readonly record struct SidebarSectionSlice(
    int Start,
    int Count,
    SidebarSourceState State = SidebarSourceState.Ready,
    SidebarContributionAvailability Availability = SidebarContributionAvailability.Live,
    bool NeedsPrompt = false);

/// <summary>sectionId → its resolved extension slice. An interface (not a dictionary) so the binder's reusable table can
/// back it without materialising anything per rebuild — <c>SidebarExtensionSlices</c> is the implementation.</summary>
public interface ISidebarSectionSlices
{
    bool TryGet(string sectionId, out SidebarSectionSlice slice);
}

/// <summary>Everything outside the document the plan depends on. Every slice is nullable so a headless test, the fake
/// backend, or a live-only adapter that is not registered can simply omit it.</summary>
public readonly record struct SidebarProjectionInput(
    // The unified library projection (playlists/albums/artists/shows), in source order — EntityList's input.
    IReadOnlyList<SidebarLibraryEntry>? Library = null,
    // The rootlist tree, DEPTH-FIRST FLATTENED with Depth stamped and folders carried as SidebarEntryKind.Folder
    // entries. Flattened rather than a PlaylistNode tree so planning a 10k rootlist is one linear pass with no
    // recursion and no per-node allocation.
    IReadOnlyList<SidebarLibraryEntry>? PlaylistTree = null,
    // The shared pin store, resolved, in pin order.
    IReadOnlyList<SidebarLibraryEntry>? Pins = null,
    // Navigation recency (HistoryStore), newest first, deduped by uri.
    IReadOnlyList<SidebarLibraryEntry>? Visited = null,
    // Playback recency (PlayLogStore), context-first, newest first, deduped.
    IReadOnlyList<SidebarLibraryEntry>? Played = null,
    // New releases from followed artists, newest first.
    IReadOnlyList<SidebarLibraryEntry>? NewReleases = null,
    // Upcoming concerts, soonest first. Name = event title, Creator = venue, SortStamp = the event's epoch-ms.
    IReadOnlyList<SidebarLibraryEntry>? Concerts = null,
    // Resolves a hand-placed item's Key (a spotify uri) to its projected entry. A miss is the missing-entity path,
    // never a dropped row.
    IReadOnlyDictionary<string, SidebarLibraryEntry>? ByUri = null,
    // The pinned entry ids — pins sort first inside every EntityList sort mode.
    IReadOnlySet<string>? PinnedIds = null,
    // Expanded rootlist folder ids. null means "everything expanded" (the headless default).
    IReadOnlySet<string>? ExpandedFolders = null,
    // The library-only search text. Filters EntityList and PlaylistTree; never shortcuts or links.
    string? Search = null,
    SidebarSourceState LibraryState = SidebarSourceState.Ready,
    SidebarSourceState TreeState = SidebarSourceState.Ready,
    SidebarSourceState RecentsState = SidebarSourceState.Ready,
    SidebarSourceState NewReleasesState = SidebarSourceState.Ready,
    SidebarSourceState ConcertsState = SidebarSourceState.Ready,
    // True when the user has no location yet — Concerts then plans one actionable PromptRow.
    bool ConcertsLocationUnset = false,
    // The caller's composite revision (document + projection + pins + search + culture epoch). Echoed into the plan so
    // Build stays deterministic — the planner never carries hidden counter state.
    int Revision = 0,
    // ── extension contributions (M1) ─────────────────────────────────────────────────────────────────────────────────
    // ONE shared pool holding every Extension section's rows back to back, and the sectionId → window table over it.
    // Appended AFTER Revision deliberately: every existing caller's positional/named construction is untouched.
    IReadOnlyList<SidebarLibraryEntry>? ExtensionEntries = null,
    ISidebarSectionSlices? ExtensionSlices = null,
    // ── rail options (R3.0/R3.1) ──────────────────────────────────────────────────────────────────────────────────────
    // How many PlaylistTree tiles the 56-DIP RAIL may draw. 0 = unbounded (the landed behaviour, and the default every
    // existing caller and test keeps). The whole rail is still bounded by RailTileCap, but a tree is the only UNBOUNDED
    // source in the rail: a 200-playlist rootlist would consume the global 40-tile budget and silently push every LATER
    // section's tiles (the utility links a mode places after its playlists) out of the rail entirely. A per-tree ceiling
    // is the input option that keeps document order from becoming a race for tiles.
    // Appended AFTER ExtensionSlices deliberately: every existing positional/named construction is untouched.
    // (`SuppressTreeCreateRow` lived here. It is GONE with the row it suppressed: the create affordance is the
    // section HEADER's "+" now — chrome the RENDERER owns per `SidebarPaneConfig.HeaderCreate` — so the planner has
    // nothing to emit and therefore nothing to suppress.)
    int RailTreeCap = 0);

/// <summary>Caller-owned row/entry storage. Hand the SAME instance to every <c>Build</c> for a given pane and a warm
/// re-plan reuses its capacity (the 10k-library alloc bound). The returned plan's lists ALIAS these buffers, so a plan
/// is only valid until the next Build on the same buffers — exactly the UseMemo lifetime it is built for.</summary>
public sealed class SidebarPlanBuffers
{
    internal readonly List<SidebarRow> Rows = new(256);
    internal readonly List<SidebarLibraryEntry> Entries = new(256);
    internal readonly List<int> TreeParents = new(256);
    internal readonly List<byte> TreeVisible = new(256);
    internal readonly List<int> TreeLeaves = new(256);
    internal readonly List<int> TreeCursors = new(256);
    internal readonly List<int> TreeAncestors = new(16);
}

public static class SidebarRowPlanner
{
    /// <summary>Rail tiles are capped (the rail stays scrollable) — beyond this a rail is noise.</summary>
    public const int RailTileCap = 40;

    /// <summary>The guard on a HAND-AUTHORED item list (StaticLinks / CustomGroup / Pinned overrides). Unreachable in
    /// practice: <see cref="SidebarLayoutReducer.MaxItemsPerSection"/> already caps those at 500.</summary>
    public const int SectionRowCap = 5000;

    /// <summary>The guard on a PROJECTED section (EntityList / PlaylistTree / Pinned / JumpBackIn / feeds).
    /// <para>DEVIATION, deliberate: §C1.7 gives one <c>SectionRowCap = 5000</c> "a single section never plans more than
    /// this many rows", while §C8.5 requires a 10 000-entry EntityList to plan IN FULL and the driving app is sized for
    /// 10k+ libraries. Truncating a real library at 5 000 rows would silently hide half of it — a correctness bug, not
    /// a guard. So the 5 000 constant keeps its name and its job for authored lists, and projected sections get this
    /// (still finite) ceiling.</para></summary>
    public const int DynamicSectionRowCap = 20_000;

    public const int RailPinnedCap = 8;
    public const int RailJumpBackInCap = 4;
    public const int RailEntityListCap = 20;

    const int SkeletonRows = 3;

    /// <summary>Deterministic: same inputs → identical plan. Called from a UseMemo keyed on a DepKey of
    /// (documentRevision, projectionRevision, pinRevision, searchText, cultureEpoch).</summary>
    public static SidebarRowPlan Build(SidebarCustomLayout layout, in SidebarProjectionInput input,
        SidebarPlanBuffers? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var st = Begin(buffers);

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++) PlanSection(sections[i], 0, in input, ref st);

        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    /// <summary>The 56-DIP rail plan: tiles only, from sections with <c>ShowInRail</c>.</summary>
    public static SidebarRowPlan BuildRail(SidebarCustomLayout layout, in SidebarProjectionInput input,
        SidebarPlanBuffers? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var st = Begin(buffers);
        int tiles = 0;

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Hidden || !SidebarSectionKinds.IsKnown(s.Kind)) continue;
            // ShowInRail is the ONE option Header/Divider honour, so it gates them too.
            if (!s.Opts.ShowInRail) continue;

            // A rail has no headings: a Header collapses into the same compact divider a Divider draws.
            if (s.Kind is SidebarSectionKind.Divider or SidebarSectionKind.Header)
            {
                st.DividerPending = true;
                st.DividerSectionId = s.Id;
                st.DividerDepth = 0;
                continue;
            }
            RailSection(s, in input, ref st, ref tiles);
        }

        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    /// <summary>PHASE 2 / Decision B — THE EDIT PROJECTION of the same document.
    ///
    /// <para><b>Why a second entry point and not a flag inside <see cref="Build"/>.</b> Iron rule 3 says the document is
    /// never rendered section-by-section: one flat <c>SidebarRow[]</c> through one <c>ItemsView.CreateBound</c>. Edit
    /// mode must obey that too — a pane-level "substitute a hand-built card column for the list" would have re-created
    /// the nested-scroller shape the unification deleted, and would have cost the cards virtualization, the pane's
    /// reorder-band machinery and the section rhythm. So edit mode is a PLAN: every top-level section becomes ONE
    /// <see cref="SidebarRowKind.SectionCard"/> row, and the one expanded section (or every section, under
    /// <c>ShowContents</c>) has its ordinary body planned right underneath by the very same per-kind planners the live
    /// pane uses — which is what makes the canvas the real artifact rather than a preview of it (P1).</para>
    ///
    /// <para>It is a SEPARATE method rather than a branch so the normal path stays byte-identical, and so the pure
    /// planner keeps exactly one reason to know an edit session exists.</para>
    ///
    /// <para>Three deliberate differences from <see cref="Build"/>:</para>
    /// <list type="bullet">
    /// <item>a HIDDEN section still gets its card (P2 — it is dimmed with an eye-off badge, never removed from view)
    /// but never a body: its rows are not in the live sidebar, and drawing them would be the editor lying;</item>
    /// <item>no <c>SectionHeader</c> row is emitted — the card IS the header, and two of them would be one artifact
    /// drawn twice;</item>
    /// <item>an unknown (future) section kind plans no card, exactly as it renders no rows. It stays in the document
    /// and round-trips untouched; <c>SidebarEditPlan.ToMoveSection</c> bridges the resulting gap in the band.</item>
    /// </list>
    /// <para>One further, DELIBERATE divergence from the live pane: a revealed body ignores the section's persisted
    /// <c>Collapsed</c> bit. Expanding a card is the EDITOR's reveal — you cannot reorder or relabel items you cannot
    /// see — while the document's own collapse state stays exactly what it was and remains editable from the section's
    /// options ("Collapse section"). Honouring it here would make a collapsed section the one thing in the canvas that
    /// cannot be edited.</para></summary>
    public static SidebarRowPlan BuildEdit(SidebarCustomLayout layout, in SidebarProjectionInput input,
        in SidebarEditState edit, SidebarPlanBuffers? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var st = Begin(buffers);

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (!SidebarSectionKinds.IsKnown(s.Kind)) continue;
            // The card is a chrome row like any other: Key == section.Id, EntryIndex == -1, no string allocated.
            Add(ref st, new SidebarRow(SidebarRowKind.SectionCard, s.Id, 0, -1,
                                       SidebarEditPlan.CardCount(s), s.Id));
            if (SidebarEditPlan.ShowsBody(in edit, s)) PlanBody(s, 0, in input, ref st);
        }

        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    // ── expanded plan ────────────────────────────────────────────────────────────────────────────────────────────────

    static void PlanSection(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        // An authored-off section contributes no rows, no rail tiles and no projection work. An unknown (future) kind
        // renders as nothing — it stays in the document and round-trips untouched.
        if (s.Hidden || !SidebarSectionKinds.IsKnown(s.Kind)) return;

        if (s.Kind == SidebarSectionKind.Divider)
        {
            st.DividerPending = true;      // flushed by the next row: leading/trailing drop, consecutive collapse
            st.DividerSectionId = s.Id;
            st.DividerDepth = depth;
            return;
        }

        if (s.Kind == SidebarSectionKind.Header)
        {
            Add(ref st, new SidebarRow(SidebarRowKind.HeaderLabel, s.Id, depth, -1, 0, s.Id));
            return;
        }

        if (s.Title is not null || s.TitleLocKey is not null)
            Add(ref st, new SidebarRow(SidebarRowKind.SectionHeader, s.Id, depth, -1, 0, s.Id));

        if (s.Collapsed) return;

        PlanBody(s, depth, in input, ref st);
    }

    /// <summary>A section's BODY rows — everything after its header. Split out of <see cref="PlanSection"/> so the edit
    /// projection (<see cref="BuildEdit"/>) can plan a real body under a card without also planning a second header;
    /// <see cref="PlanSection"/> is unchanged in behaviour (header, collapse gate, then this).</summary>
    static void PlanBody(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        switch (s.Kind)
        {
            case SidebarSectionKind.Pinned: PlanPinned(s, depth, in input, ref st); break;
            case SidebarSectionKind.JumpBackIn: PlanJumpBackIn(s, depth, in input, ref st); break;
            case SidebarSectionKind.CollectionShortcuts:
            case SidebarSectionKind.StaticLinks: PlanItems(s, depth, in input, ref st, iconRows: true); break;
            case SidebarSectionKind.PlaylistTree: PlanPlaylistTree(s, depth, in input, ref st); break;
            case SidebarSectionKind.EntityList: PlanEntityList(s, depth, in input, ref st); break;
            case SidebarSectionKind.CustomGroup: PlanGroup(s, depth, in input, ref st); break;
            case SidebarSectionKind.EntityEmbed: PlanEmbed(s, depth, in input, ref st); break;
            case SidebarSectionKind.NewReleases:
                PlanFeed(s, depth, input.NewReleases, input.NewReleasesState, ref st);
                break;
            case SidebarSectionKind.Concerts: PlanConcerts(s, depth, in input, ref st); break;
            case SidebarSectionKind.Extension: PlanExtension(s, depth, in input, ref st); break;
        }
    }

    /// <summary>An extension contribution: the binder already resolved it into a window over
    /// <see cref="SidebarProjectionInput.ExtensionEntries"/>. The section KEEPS its spec in every degraded case — a
    /// missing / disabled / schema-incompatible contribution plans exactly ONE actionable <c>PromptRow</c> ("Manage
    /// extension"), never a silent disappearance and never a removal from the document.</summary>
    static void PlanExtension(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var slices = input.ExtensionSlices;
        if (slices is null || !slices.TryGet(s.Id, out var slice)
            || slice.Availability is SidebarContributionAvailability.Missing
                                  or SidebarContributionAvailability.Disabled
                                  or SidebarContributionAvailability.Incompatible)
        {
            Add(ref st, Chrome(SidebarRowKind.PromptRow, s, depth));
            return;
        }

        // Clamp the window defensively: the pool and the table are published together, but a stale table must degrade to
        // "empty", never index out of range.
        var pool = input.ExtensionEntries;
        int available = pool?.Count ?? 0;
        int start = slice.Start, count = slice.Count;
        if (start < 0 || count <= 0 || start >= available) count = 0;
        else if (start + count > available) count = available - start;

        if (count == 0)
        {
            // An actionable degraded state beats both a skeleton and an empty caption (the Concerts "Set your location" row).
            if (slice.NeedsPrompt) Add(ref st, Chrome(SidebarRowKind.PromptRow, s, depth));
            else if (slice.State == SidebarSourceState.Pending) EmitSkeletons(s, depth, ref st);
            else Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        int cap = Cap(s, DynamicSectionRowCap);
        if (count > cap) count = cap;
        int at = st.Entries.Count;
        for (int i = 0; i < count; i++) st.Entries.Add(pool![start + i]);
        EmitProjected(s, depth, at, count, ref st);
    }

    static void PlanPinned(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var pins = input.Pins;
        int start = st.Entries.Count;
        int cap = Cap(s, DynamicSectionRowCap);
        bool grid = s.Opts.Presentation == SidebarPresentation.Grid;
        if (pins is not null)
            for (int i = 0; i < pins.Count && st.Entries.Count - start < cap; i++)
            {
                var pin = pins[i];
                if (IsHiddenOverride(s, pin)) continue;

                int at = st.Entries.Count;
                st.Entries.Add(pin);
                if (!grid)
                    Add(ref st, new SidebarRow(pin.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow,
                        s.Id, depth, at, 0, pin.Id));

                if (pin.IsFolder && IsExpanded(input, FolderId(in pin)))
                    AppendPinnedFolderChildren(s, depth, in pin, start, cap, grid, in input, ref st);
            }

        int count = st.Entries.Count - start;
        // Empty Pinned is the real DropZone row ("Drop items here to pin"), not a caption.
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }
        if (grid) EmitProjected(s, depth, start, count, ref st);
    }

    /// <summary>Expand one pinned folder against the canonical flattened rootlist. The pinned folder itself is a root row
    /// in this section; descendants keep only their depth RELATIVE to that root. Nested disclosures obey the same shared
    /// expansion set as PlaylistTree, and the section's item cap bounds roots plus descendants together.</summary>
    static void AppendPinnedFolderChildren(SidebarSectionSpec s, byte depth, in SidebarLibraryEntry pin,
        int sectionStart, int cap, bool grid, in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (tree is null || tree.Count == 0) return;

        string folderId = FolderId(in pin);
        int root = -1;
        for (int i = 0; i < tree.Count; i++)
        {
            var candidate = tree[i];
            if (!candidate.IsFolder) continue;
            if (string.Equals(candidate.Id, pin.Id, StringComparison.Ordinal)
                || string.Equals(FolderId(in candidate), folderId, StringComparison.Ordinal))
            {
                root = i;
                break;
            }
        }
        if (root < 0) return;

        int rootDepth = tree[root].Depth;
        for (int i = root + 1; i < tree.Count && st.Entries.Count - sectionStart < cap; i++)
        {
            var child = tree[i];
            if (child.Depth <= rootDepth) break;

            int at = st.Entries.Count;
            st.Entries.Add(child);
            if (!grid)
            {
                int relativeDepth = Math.Max(1, child.Depth - rootDepth);
                byte rowDepth = (byte)Math.Min(depth + relativeDepth, byte.MaxValue);
                Add(ref st, new SidebarRow(child.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow,
                    s.Id, rowDepth, at, 0, child.Id));
            }

            if (child.IsFolder && !IsExpanded(input, FolderId(in child)))
            {
                int collapsedDepth = child.Depth;
                while (i + 1 < tree.Count && tree[i + 1].Depth > collapsedDepth) i++;
            }
        }
    }

    static void PlanJumpBackIn(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var src = s.Opts.Recents == SidebarRecentsSource.Played ? input.Played : input.Visited;
        if (input.RecentsState == SidebarSourceState.Pending && (src is null || src.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }
        PlanTopN(s, depth, src, ref st);
    }

    static void PlanFeed(SidebarSectionSpec s, byte depth, IReadOnlyList<SidebarLibraryEntry>? src,
        SidebarSourceState state, ref PlanState st)
    {
        if (state == SidebarSourceState.Pending && (src is null || src.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }
        PlanTopN(s, depth, src, ref st);
    }

    static void PlanTopN(SidebarSectionSpec s, byte depth, IReadOnlyList<SidebarLibraryEntry>? src, ref PlanState st)
    {
        int start = st.Entries.Count;
        int cap = Cap(s, DynamicSectionRowCap);
        if (src is not null)
            for (int i = 0; i < src.Count && st.Entries.Count - start < cap; i++) st.Entries.Add(src[i]);

        int count = st.Entries.Count - start;
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }
        EmitProjected(s, depth, start, count, ref st);
    }

    static void PlanConcerts(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        // Location unset is an ACTIONABLE degraded state, not an empty list.
        if (input.ConcertsLocationUnset) { Add(ref st, Chrome(SidebarRowKind.PromptRow, s, depth)); return; }
        PlanFeed(s, depth, input.Concerts, input.ConcertsState, ref st);
    }

    static void PlanPlaylistTree(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (input.TreeState == SidebarSourceState.Pending && (tree is null || tree.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }
        if (tree is null || tree.Count == 0)
        {
            // The kind's ordinary Empty hint, and nothing else. It used to be followed by the create ROW, which is why
            // an empty tree planned two rows; the affordance is the header's "+" now and the hint names it.
            Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        string? search = Search(input);
        if (search is not null || s.Opts.Presentation == SidebarPresentation.Grid)
            PlanFlatPlaylistTree(s, depth, tree, search, in input, ref st);
        else if (s.Query is null)
            PlanSourcePlaylistTree(s, depth, tree, in input, ref st);
        else
            PlanQueriedPlaylistTree(s, depth, tree, s.Query, in input, ref st);

        // The closing gutter, and only where there is a tree to close: an empty section's placeholder is not something
        // you can drop AFTER, and a skeleton has no order yet. It is now the section's LAST row outright — the create
        // row that used to sit under it (and squat this very slot, D3) is gone.
        if (EmittedTreeRows(in st)) Add(ref st, Chrome(SidebarRowKind.TreeEnd, s, depth));
    }

    /// <summary>Did the tree body just emit a real, orderable row? A trailing Empty/Skeleton means it did not.</summary>
    static bool EmittedTreeRows(in PlanState st)
    {
        var rows = st.Rows;
        if (rows.Count == 0) return false;
        return rows[rows.Count - 1].Kind is SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader
                                          or SidebarRowKind.GridStrip;
    }

    static void PlanSourcePlaylistTree(SidebarSectionSpec s, byte depth,
        IReadOnlyList<SidebarLibraryEntry> tree, in SidebarProjectionInput input, ref PlanState st)
    {
        int emitted = 0;
        for (int i = 0; i < tree.Count && emitted < DynamicSectionRowCap; i++)
        {
            var e = tree[i];
            byte d = (byte)Math.Min(depth + e.Depth, byte.MaxValue);
            int at = st.Entries.Count;
            st.Entries.Add(e);
            if (e.Kind == SidebarEntryKind.Folder)
            {
                Add(ref st, new SidebarRow(SidebarRowKind.FolderHeader, s.Id, d, at, 0, e.Id));
                emitted++;
                if (!IsExpanded(input, e.FolderId))
                {
                    int myDepth = e.Depth;
                    while (i + 1 < tree.Count && tree[i + 1].Depth > myDepth) i++;
                }
                continue;
            }
            Add(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, d, at, 0, e.Id));
            emitted++;
        }
        if (emitted == 0) Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
    }

    static void PlanFlatPlaylistTree(SidebarSectionSpec s, byte depth,
        IReadOnlyList<SidebarLibraryEntry> tree, string? search, in SidebarProjectionInput input, ref PlanState st)
    {
        var q = SidebarSectionKinds.EffectiveQuery(SidebarSectionKind.PlaylistTree, s.Query);
        int start = st.Entries.Count;
        for (int i = 0; i < tree.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
        {
            var e = tree[i];
            if (e.Kind == SidebarEntryKind.Folder || !TreeLeafMatches(q, in e, search)) continue;
            st.Entries.Add(e);
        }

        int count = st.Entries.Count - start;
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }
        if (s.Query is not null && count > 1)
            CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));
        EmitProjected(s, depth, start, count, ref st);
    }

    /// <summary>Filter a flattened preorder without breaking its tree: leaf slots sort only against other leaf slots
    /// under the same immediate parent; folders keep their structural source positions and survive iff a descendant leaf
    /// survives. Scratch lists belong to SidebarPlanBuffers, so a warm re-plan allocates nothing.</summary>
    static void PlanQueriedPlaylistTree(SidebarSectionSpec s, byte depth,
        IReadOnlyList<SidebarLibraryEntry> tree, SidebarEntityQuery q,
        in SidebarProjectionInput input, ref PlanState st)
    {
        var parents = st.TreeParents;
        var visible = st.TreeVisible;
        var leaves = st.TreeLeaves;
        var cursors = st.TreeCursors;
        if (!PrepareQueriedPlaylistTree(tree, q, in input, ref st))
        {
            Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        int emitted = 0;
        for (int i = 0; i < tree.Count && emitted < DynamicSectionRowCap; i++)
        {
            if (visible[i] == 0) continue;
            var source = tree[i];
            byte d = (byte)Math.Min(depth + source.Depth, byte.MaxValue);
            if (source.Kind == SidebarEntryKind.Folder)
            {
                int at = st.Entries.Count;
                st.Entries.Add(source);
                Add(ref st, new SidebarRow(SidebarRowKind.FolderHeader, s.Id, d, at, 0, source.Id));
                emitted++;
                if (!IsExpanded(input, source.FolderId))
                {
                    int myDepth = source.Depth;
                    while (i + 1 < tree.Count && tree[i + 1].Depth > myDepth) i++;
                }
                continue;
            }

            int parentSlot = parents[i] + 1;
            int sortedSource = leaves[cursors[parentSlot]++];
            var leaf = tree[sortedSource];
            int entryAt = st.Entries.Count;
            st.Entries.Add(leaf);
            Add(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, d, entryAt, 0, leaf.Id));
            emitted++;
        }
    }

    static bool PrepareQueriedPlaylistTree(IReadOnlyList<SidebarLibraryEntry> tree, SidebarEntityQuery q,
        in SidebarProjectionInput input, ref PlanState st)
    {
        var parents = st.TreeParents;
        var visible = st.TreeVisible;
        var leaves = st.TreeLeaves;
        var cursors = st.TreeCursors;
        var ancestors = st.TreeAncestors;
        parents.Clear();
        visible.Clear();
        leaves.Clear();
        cursors.Clear();
        ancestors.Clear();

        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            while (ancestors.Count > 0 && tree[ancestors[^1]].Depth >= e.Depth)
                ancestors.RemoveAt(ancestors.Count - 1);

            int parent = ancestors.Count == 0 ? -1 : ancestors[^1];
            parents.Add(parent);
            visible.Add(0);
            if (e.Kind == SidebarEntryKind.Folder)
            {
                ancestors.Add(i);
                continue;
            }
            if (!TreeLeafMatches(q, in e, search: null)) continue;

            visible[i] = 1;
            leaves.Add(i);
            for (int a = 0; a < ancestors.Count; a++) visible[ancestors[a]] = 1;
        }

        if (leaves.Count == 0) return false;
        CollectionsMarshal.AsSpan(leaves).Sort(
            new TreeLeafOrder(tree, parents, new EntryOrder(q.Sort, q.Descending, input.PinnedIds)));

        for (int i = 0; i <= tree.Count; i++) cursors.Add(-1);
        for (int i = 0; i < leaves.Count; i++)
        {
            int slot = parents[leaves[i]] + 1;
            if (cursors[slot] < 0) cursors[slot] = i;
        }
        return true;
    }

    static bool TreeLeafMatches(SidebarEntityQuery q, in SidebarLibraryEntry e, string? search)
    {
        if (!KindMatches(q.Kinds, e.Kind)) return false;
        if (q.Qualifier != SidebarPlaylistQualifier.Any &&
            (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier((byte)q.Qualifier))) return false;
        if (!UriMatches(q, in e)) return false;
        return search is null || SidebarSearch.Matches(in e, search);
    }

    static void PlanEntityList(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var lib = input.Library;
        if (input.LibraryState == SidebarSourceState.Pending && (lib is null || lib.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }

        var q = s.Query ?? SidebarEntityQuery.Default;
        string? search = Search(input);
        int start = st.Entries.Count;

        if (lib is not null)
            for (int i = 0; i < lib.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var e = lib[i];
                if (!KindMatches(q.Kinds, e.Kind)) continue;
                if (q.Qualifier != SidebarPlaylistQualifier.Any &&
                    (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier((byte)q.Qualifier))) continue;
                if (!UriMatches(q, in e)) continue;
                if (search is not null && !SidebarSearch.Matches(in e, search)) continue;
                st.Entries.Add(e);
            }

        int count = st.Entries.Count - start;
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }

        // Sort the slice in place — no temporary list, no boxed comparer (struct comparer, generic Span.Sort).
        if (count > 1)
            CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));

        int cap = Cap(s, DynamicSectionRowCap);
        if (count > cap)
        {
            // MaxItems truncates the PLAN, never the document.
            st.Entries.RemoveRange(start + cap, count - cap);
            count = cap;
        }

        EmitProjected(s, depth, start, count, ref st);
    }

    static void PlanGroup(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        int before = st.Rows.Count;
        byte inner = (byte)Math.Min(depth + 1, byte.MaxValue);
        PlanItems(s, inner, in input, ref st, iconRows: true, emitEmpty: false);

        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++) PlanSection(kids[i], inner, in input, ref st);

        if (st.Rows.Count == before) Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
    }

    static void PlanEmbed(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var items = s.ItemList;
        if (items.Count == 0 || items[0].Hidden)
        {
            Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        var item = items[0];
        int idx = Resolve(in input, item.Key, ref st);
        // A missing entity is STILL a card (dimmed, from FallbackTitle/FallbackImageUrl, play affordance hidden) —
        // EntryIndex == -1 is the signal, and the item is never auto-removed.
        Add(ref st, new SidebarRow(SidebarRowKind.EntityCard, s.Id, depth, idx, 0, item.Key));
    }

    static void PlanItems(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st,
        bool iconRows, bool emitEmpty = true)
    {
        var items = s.ItemList;
        int emitted = 0;
        for (int i = 0; i < items.Count && emitted < SectionRowCap; i++)
        {
            var item = items[i];
            if (item.Hidden) continue;

            if (item.Target == SidebarItemTarget.Route)
            {
                // Routes are glyph rows — a hand-picked page has no artwork and never resolves against the projection.
                Add(ref st, new SidebarRow(iconRows ? SidebarRowKind.IconRow : SidebarRowKind.EntityRow,
                    s.Id, depth, -1, 0, item.Key));
                emitted++;
                continue;
            }

            int idx = Resolve(in input, item.Key, ref st);
            if (item.Target == SidebarItemTarget.Track)
            {
                // A track has no detail route, and a HAND-PLACED track is not part of the library projection either (only
                // a feed source — queue / now playing / artist top tracks — emits SidebarEntryKind.Track rows): the row
                // renders from the item spec and PLAYS on click.
                Add(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, depth, idx, 0, item.Key));
                emitted++;
                continue;
            }

            Add(ref st, new SidebarRow(idx >= 0 ? SidebarRowKind.EntityRow : SidebarRowKind.Placeholder,
                s.Id, depth, idx, 0, item.Key));
            emitted++;
        }

        if (emitted == 0 && emitEmpty) Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
    }

    // ── rail plan ────────────────────────────────────────────────────────────────────────────────────────────────────

    static void RailSection(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        switch (s.Kind)
        {
            case SidebarSectionKind.Pinned:
                RailFrom(s, input.Pins, Cap(s, RailPinnedCap), skipHidden: true, ref st, ref tiles);
                break;

            case SidebarSectionKind.JumpBackIn:
                RailFrom(s, s.Opts.Recents == SidebarRecentsSource.Played ? input.Played : input.Visited,
                    Cap(s, RailJumpBackInCap), skipHidden: false, ref st, ref tiles);
                break;

            case SidebarSectionKind.CollectionShortcuts:
            case SidebarSectionKind.StaticLinks:
                RailItems(s, in input, ref st, ref tiles);
                break;

            case SidebarSectionKind.PlaylistTree:
                RailTree(s, in input, ref st, ref tiles);
                break;

            case SidebarSectionKind.EntityList:
                RailEntityList(s, in input, ref st, ref tiles);
                break;

            case SidebarSectionKind.CustomGroup:
                RailItems(s, in input, ref st, ref tiles);
                var kids = s.ChildList;
                for (int i = 0; i < kids.Count; i++)
                {
                    var k = kids[i];
                    if (k.Hidden || !k.Opts.ShowInRail || !SidebarSectionKinds.IsKnown(k.Kind)) continue;
                    if (k.Kind is SidebarSectionKind.Divider or SidebarSectionKind.Header) continue;
                    RailSection(k, in input, ref st, ref tiles);   // children's tiles, flattened
                }
                break;

            case SidebarSectionKind.EntityEmbed:
            {
                var items = s.ItemList;
                if (items.Count == 0 || items[0].Hidden) break;
                int idx = Resolve(in input, items[0].Key, ref st);
                if (idx < 0) break;                                 // placeholder items contribute no tile
                AddTile(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, idx, 0, items[0].Key), ref tiles);
                break;
            }

            case SidebarSectionKind.Concerts:
                // One glyph tile navigating to the hub (a feed has no single cover).
                AddTile(ref st, new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, s.Id), ref tiles);
                break;

            case SidebarSectionKind.Extension:
                // A contribution gets ONE glyph tile in the rail: a 56-DIP strip cannot express a third-party list, and
                // an unresolved contribution must not be able to fill the rail with prompts either. Tapping it expands.
                AddTile(ref st, new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, s.Id), ref tiles);
                break;

            // NewReleases: ShowInRail is forced off for this kind — a releases feed has no meaningful single tile.
            case SidebarSectionKind.NewReleases:
            default:
                break;
        }
    }

    static void RailFrom(SidebarSectionSpec s, IReadOnlyList<SidebarLibraryEntry>? src, int cap, bool skipHidden,
        ref PlanState st, ref int tiles)
    {
        if (src is null) return;
        int n = 0;
        for (int i = 0; i < src.Count && n < cap; i++)
        {
            if (skipHidden && IsHiddenOverride(s, src[i])) continue;
            int idx = st.Entries.Count;
            st.Entries.Add(src[i]);
            if (!AddTile(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, idx, 0, src[i].Id), ref tiles))
            {
                st.Entries.RemoveAt(idx);   // the cap swallowed the tile — do not leak an orphan entry
                return;
            }
            n++;
        }
    }

    static void RailItems(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Hidden) continue;

            if (item.Target == SidebarItemTarget.Route)
            {
                if (!AddTile(ref st, new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, item.Key), ref tiles))
                    return;
                continue;
            }
            if (item.Target == SidebarItemTarget.Track) continue;   // a track tile in a text-less rail is unreadable

            int idx = Resolve(in input, item.Key, ref st);
            if (idx < 0) continue;                                   // placeholder items are skipped
            if (!AddTile(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, idx, 0, item.Key), ref tiles))
            {
                st.Entries.RemoveAt(idx);
                return;
            }
        }
    }

    static void RailTree(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        var tree = input.PlaylistTree;
        if (tree is null || tree.Count == 0) return;
        // The caller's per-tree ceiling (0 = unbounded, the landed behaviour). See SidebarProjectionInput.RailTreeCap.
        int cap = input.RailTreeCap > 0 ? Math.Min(input.RailTreeCap, RailTileCap) : RailTileCap;
        string? search = Search(input);

        // A grid (and a search result) has no folder chrome: it is the same flatten-to-entities projection the expanded
        // pane uses. A null query preserves source order; an authored query applies its sort/qualifier/kind constraints.
        //
        // DELIBERATELY EXEMPT from the top-level-only rule the LIST arm below applies. This arm draws no folder tiles at
        // all, so there is no disclosure to reach a nested playlist through — filtering by depth here would make a
        // nested playlist unreachable from the rail rather than merely un-tiled, and a search that could not find one
        // would be a search that lies.
        if (search is not null || s.Opts.Presentation == SidebarPresentation.Grid)
        {
            var q = SidebarSectionKinds.EffectiveQuery(SidebarSectionKind.PlaylistTree, s.Query);
            int start = st.Entries.Count;
            for (int i = 0; i < tree.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var entry = tree[i];
                if (entry.Kind == SidebarEntryKind.Folder || !TreeLeafMatches(q, in entry, search)) continue;
                st.Entries.Add(entry);
            }

            int count = st.Entries.Count - start;
            if (s.Query is not null && count > 1)
                CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                    .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));
            if (count > cap)
            {
                st.Entries.RemoveRange(start + cap, count - cap);
                count = cap;
            }
            for (int i = 0; i < count; i++)
            {
                var entry = st.Entries[start + i];
                if (AddTile(ref st,
                    new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, start + i, 0, entry.Id), ref tiles)) continue;
                st.Entries.RemoveRange(start + i, count - i);
                return;
            }
            return;
        }

        // With no query, the persisted rootlist order remains byte-for-byte today's rail. With a query, prune folders
        // whose descendants no longer match and sort leaf slots only within their immediate folder.
        if (s.Query is not null && !PrepareQueriedPlaylistTree(tree, s.Query, in input, ref st)) return;
        int drawn = 0;
        for (int i = 0; i < tree.Count && drawn < cap; i++)
        {
            if (s.Query is not null && st.TreeVisible[i] == 0) continue;
            var source = tree[i];
            // TOP LEVEL ONLY. A 56-DIP strip has no indent lane and no disclosure, so a nested playlist tile was
            // indistinguishable from a top-level one: the rail read as a flat pile whose order nobody could explain, and
            // an expanded folder silently doubled the strip's length. A folder's CONTENTS are reachable from its tile —
            // it opens the side flyout (`SidebarRailFolderFlyout`) — so nothing is lost by keeping the tiles to depth 0.
            // Skipped BEFORE the sibling cursor below, which is per-parent: a nested slot we never draw must not consume
            // its folder's cursor.
            if (source.Depth > 0) continue;
            SidebarLibraryEntry e;
            if (s.Query is null || source.Kind == SidebarEntryKind.Folder) e = source;
            else
            {
                int parentSlot = st.TreeParents[i] + 1;
                e = tree[st.TreeLeaves[st.TreeCursors[parentSlot]++]];
            }
            int idx = st.Entries.Count;
            st.Entries.Add(e);
            var kind = e.Kind == SidebarEntryKind.Folder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow;
            if (!AddTile(ref st, new SidebarRow(kind, s.Id, 0, idx, 0, e.Id), ref tiles))
            {
                st.Entries.RemoveAt(idx);
                return;
            }
            drawn++;
        }
    }

    static void RailEntityList(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        var lib = input.Library;
        if (lib is null) return;

        var q = s.Query ?? SidebarEntityQuery.Default;
        int cap = s.Opts.MaxItems > 0 ? Math.Min(s.Opts.MaxItems, RailEntityListCap) : RailEntityListCap;
        int start = st.Entries.Count;

        for (int i = 0; i < lib.Count; i++)
        {
            var e = lib[i];
            if (!KindMatches(q.Kinds, e.Kind)) continue;
            if (q.Qualifier != SidebarPlaylistQualifier.Any &&
                (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier((byte)q.Qualifier))) continue;
            if (!UriMatches(q, in e)) continue;
            st.Entries.Add(e);
        }

        int count = st.Entries.Count - start;
        if (count == 0) return;
        if (count > 1)
            CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));
        if (count > cap) { st.Entries.RemoveRange(start + cap, count - cap); count = cap; }

        for (int i = 0; i < count; i++)
            if (!AddTile(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, start + i, 0,
                    st.Entries[start + i].Id), ref tiles))
                return;
    }

    // ── shared plumbing ──────────────────────────────────────────────────────────────────────────────────────────────

    struct PlanState
    {
        public List<SidebarRow> Rows;
        public List<SidebarLibraryEntry> Entries;
        public List<int> TreeParents;
        public List<byte> TreeVisible;
        public List<int> TreeLeaves;
        public List<int> TreeCursors;
        public List<int> TreeAncestors;
        public bool DividerPending;
        public string? DividerSectionId;
        public byte DividerDepth;
    }

    static PlanState Begin(SidebarPlanBuffers? buffers)
    {
        var rows = buffers?.Rows ?? new List<SidebarRow>(64);
        var entries = buffers?.Entries ?? new List<SidebarLibraryEntry>(64);
        var treeParents = buffers?.TreeParents ?? new List<int>(64);
        var treeVisible = buffers?.TreeVisible ?? new List<byte>(64);
        var treeLeaves = buffers?.TreeLeaves ?? new List<int>(64);
        var treeCursors = buffers?.TreeCursors ?? new List<int>(64);
        var treeAncestors = buffers?.TreeAncestors ?? new List<int>(8);
        rows.Clear();
        entries.Clear();
        treeParents.Clear();
        treeVisible.Clear();
        treeLeaves.Clear();
        treeCursors.Clear();
        treeAncestors.Clear();
        return new PlanState
        {
            Rows = rows,
            Entries = entries,
            TreeParents = treeParents,
            TreeVisible = treeVisible,
            TreeLeaves = treeLeaves,
            TreeCursors = treeCursors,
            TreeAncestors = treeAncestors,
        };
    }

    /// <summary>The one row sink. A pending divider resolves HERE, which is what makes leading/trailing dividers vanish
    /// and consecutive dividers collapse — no post-pass, no second walk:
    ///   * TRAILING — a divider that is never followed by a row is simply never flushed;
    ///   * LEADING  — a divider flushed before any row exists has nothing to separate, so it is dropped;
    ///   * CONSECUTIVE — a run of dividers keeps overwriting the pending slot, so only the last one draws.
    /// A hidden or empty-and-invisible section in between therefore cannot strand a rule either.</summary>
    static void Add(ref PlanState st, in SidebarRow row)
    {
        if (st.DividerPending)
        {
            st.DividerPending = false;
            var id = st.DividerSectionId!;
            byte depth = st.DividerDepth;
            st.DividerSectionId = null;
            st.DividerDepth = 0;
            if (st.Rows.Count > 0) st.Rows.Add(new SidebarRow(SidebarRowKind.Divider, id, depth, -1, 0, id));
        }
        st.Rows.Add(row);
    }

    static bool AddTile(ref PlanState st, in SidebarRow row, ref int tiles)
    {
        if (tiles >= RailTileCap) return false;
        Add(ref st, row);
        tiles++;
        return true;
    }

    static SidebarRow Chrome(SidebarRowKind kind, SidebarSectionSpec s, byte depth)
        => new(kind, s.Id, depth, -1, 0, s.Id);

    static void EmitSkeletons(SidebarSectionSpec s, byte depth, ref PlanState st)
    {
        for (int i = 0; i < SkeletonRows; i++) Add(ref st, Chrome(SidebarRowKind.Skeleton, s, depth));
    }

    /// <summary>Turns an already-appended entry slice into rows: one EntityRow each, or GridColumns-wide GridStrips.</summary>
    static void EmitProjected(SidebarSectionSpec s, byte depth, int start, int count, ref PlanState st)
    {
        if (s.Opts.Presentation == SidebarPresentation.Grid)
        {
            int cols = Math.Clamp(s.Opts.GridColumns, 2, 4);
            for (int i = 0; i < count; i += cols)
                Add(ref st, new SidebarRow(SidebarRowKind.GridStrip, s.Id, depth, start + i,
                    Math.Min(cols, count - i), s.Id));
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var e = st.Entries[start + i];
            // A folder can reach a projected section two ways: pinned (locked decision 4 lists playlist folders as
            // pinnable) or via a Playlists-kinded EntityList (SidebarEntryKinds.From maps Playlists -> the whole tree).
            Add(ref st, new SidebarRow(e.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow,
                s.Id, depth, start + i, 0, e.Id));
        }
    }

    static int Resolve(in SidebarProjectionInput input, string key, ref PlanState st)
    {
        if (input.ByUri is null || key.Length == 0) return -1;
        if (!input.ByUri.TryGetValue(key, out var e)) return -1;
        int idx = st.Entries.Count;
        st.Entries.Add(e);
        return idx;
    }

    static int Cap(SidebarSectionSpec s, int hard)
    {
        int m = s.Opts.MaxItems;
        return m > 0 ? Math.Min(m, hard) : hard;
    }

    static bool IsHiddenOverride(SidebarSectionSpec s, in SidebarLibraryEntry e)
    {
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].Hidden) continue;
            var k = items[i].Key;
            if (string.Equals(k, e.Uri, StringComparison.Ordinal) ||
                string.Equals(k, e.Id, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static bool IsExpanded(in SidebarProjectionInput input, string folderId)
        => input.ExpandedFolders is null || input.ExpandedFolders.Contains(folderId);

    static string FolderId(in SidebarLibraryEntry entry)
        => entry.FolderId.Length > 0 ? entry.FolderId : SidebarPinId.FolderIdOf(entry.Id);

    /// <summary>The trimmed library-only query, or null when the pane is not searching.</summary>
    static string? Search(in SidebarProjectionInput input)
    {
        var q = SidebarSearch.Normalize(input.Search);
        return q.Length == 0 ? null : q;
    }

    static bool KindMatches(SidebarEntityKinds kinds, SidebarEntryKind kind)
        => SidebarEntryKinds.Has(SidebarEntryKinds.From(kinds), kind);

    /// <summary>The query's include/exclude uri sets — "only these artists" without turning the section into a manually
    /// maintained item list. <c>Include</c> is a WHITELIST (null/empty = everything passes) and <c>Exclude</c> ALWAYS wins,
    /// so a uri named in both is excluded. Each key is matched against the entry's uri OR its id, because an authored list
    /// may legitimately be written in either vocabulary (a picker yields uris; a pin/route yields ids).</summary>
    static bool UriMatches(SidebarEntityQuery q, in SidebarLibraryEntry e)
    {
        var exclude = q.ExcludeUris;
        if (exclude is { Count: > 0 })
            for (int i = 0; i < exclude.Count; i++)
                if (SameEntity(exclude[i], in e)) return false;

        var include = q.IncludeUris;
        if (include is not { Count: > 0 }) return true;
        for (int i = 0; i < include.Count; i++)
            if (SameEntity(include[i], in e)) return true;
        return false;
    }

    static bool SameEntity(string? key, in SidebarLibraryEntry e)
        => key is { Length: > 0 }
           && (string.Equals(key, e.Uri, StringComparison.Ordinal) || string.Equals(key, e.Id, StringComparison.Ordinal));

    /// <summary>An empty rank map: <c>SidebarSortMode.CustomOrder</c> is Mode B's LOCAL overlay, which a Curated
    /// EntityList has no access to — SidebarSort.Custom with no ranks is exactly the documented degradation
    /// (pure SourceOrder + the ordinal Id tiebreak), so the order is still total and deterministic.</summary>
    static readonly Dictionary<string, int> NoRanks = new(0, StringComparer.Ordinal);

    /// <summary>Total order over the projection: pins first (locked decision 10), then the section's sort mode.
    /// The per-mode comparison is SidebarSort's — the one owner of sidebar collation — so a Curated EntityList and the
    /// V3 list can never drift apart. A struct comparer + the generic Span.Sort overload means no boxing and no
    /// per-plan delegate.</summary>
    readonly struct EntryOrder : IComparer<SidebarLibraryEntry>
    {
        readonly SidebarSortMode _mode;
        readonly bool _desc;
        readonly IReadOnlySet<string>? _pins;

        public EntryOrder(SidebarSortMode mode, bool descending, IReadOnlySet<string>? pins)
        {
            _mode = mode;
            // DIRECTION RECONCILIATION. SidebarSort's `desc` means "REVERSE this comparator's natural direction", and its
            // recency comparators are naturally newest-first; the Core query's `Descending` means "descending" literally.
            // Map per mode so SidebarEntityQuery.Default (Recents, Descending: true) really is newest-first and
            // PlaylistsAlphabetical (Descending: false) really is A→Z.
            _desc = mode is SidebarSortMode.Recents or SidebarSortMode.RecentlyAdded ? !descending : descending;
            _pins = pins;
        }

        public int Compare(SidebarLibraryEntry a, SidebarLibraryEntry b)
        {
            // Pins sort first in EVERY sort mode. The caller's explicit set wins; otherwise the projection's own
            // IsPinned stamp (SidebarProjection.PinsFirst) is the authority.
            bool pa = _pins?.Contains(a.Id) ?? a.IsPinned;
            bool pb = _pins?.Contains(b.Id) ?? b.IsPinned;
            if (pa != pb) return pa ? -1 : 1;

            return _mode switch
            {
                SidebarSortMode.RecentlyAdded => SidebarSort.RecentlyAdded(in a, in b, _desc),
                SidebarSortMode.Alphabetical => SidebarSort.Alphabetical(in a, in b, _desc),
                SidebarSortMode.Creator => SidebarSort.Creator(in a, in b, _desc),
                SidebarSortMode.CustomOrder => SidebarSort.Custom(in a, in b, NoRanks),
                _ => SidebarSort.Recents(in a, in b, _desc),
            };
        }
    }

    /// <summary>Sort leaf source indices into parent bands, then by the shared entry order inside each band.</summary>
    readonly struct TreeLeafOrder : IComparer<int>
    {
        readonly IReadOnlyList<SidebarLibraryEntry> _tree;
        readonly IReadOnlyList<int> _parents;
        readonly EntryOrder _entries;

        public TreeLeafOrder(IReadOnlyList<SidebarLibraryEntry> tree, IReadOnlyList<int> parents, EntryOrder entries)
        {
            _tree = tree;
            _parents = parents;
            _entries = entries;
        }

        public int Compare(int a, int b)
        {
            int parent = _parents[a].CompareTo(_parents[b]);
            return parent != 0 ? parent : _entries.Compare(_tree[a], _tree[b]);
        }
    }
}
