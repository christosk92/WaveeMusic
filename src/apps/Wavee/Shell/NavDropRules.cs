// ── Shell/NavDropRules.cs ──────────────────────────────────────────────────────────────────────────────────────────
// every rule a sidebar drop is decided by: the drop-slot resolver (kind, depth, refusal), the refusal sentences, the undo
// anchors, the slot → destination mapper and its legality, the "+" create destinations, and the row-level table that says
// whether a row accepts a payload, is merely crossed by it, or refuses it (and why)
//
// Role: CORE · Plan: technical-plan.md §5.1 row C12 (BP X30) · design ch 25 §0.8, W4, W16 (the five tree cues), §9
//
// ENGINE-FREE and UI-thread-agnostic: a drop is decided from (row facts, pointer-relative t, payload, the marker stream), and
// nothing here knows a node, a pointer or a pixel. Turning a pointer position into a row-relative `t` and an x is geometry,
// and so are the cues: both stay in the Classic view (`Sidebar.UI.Drop.cs`). What a drop DOES — the rootlist move, the track
// deposit, the pin, the refusal toast — is `NavModel.Drop` (NavModel.Drop.cs). The block between the two rules banners
// below is the unchanged 0.3 drop authority, moved here from Sidebar.cs and Sidebar.Host.cs (the types keep their names).

using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

// ── DROP, TREE NAV, FOLDER FLYOUT AND MULTI-SELECTION ─────────────────────────────────────────────────────────────
//
// The rootlist drag-and-drop authority, and the non-mouse verbs that share it. ONE pure resolver turns a pointer
// position over a row into a (kind, depth, refusal) slot; ONE decision layer maps an armed slot onto the real
// marker-stream destination and checks it against the SAME legality the keyboard/menu verbs and the folder picker
// ask; ONE selection model and ONE flyout stack carry the rest of non-mouse tree organisation. Nothing outside this
// block computes a placement, a refusal reason, or "which folders may this be filed into" — every surface (row,
// TreeEnd gutter, rail tile, folder picker) publishes and commits exactly what these types decide.

// ── the drop-slot resolver ─────────────────────────────────────────────────────────────────────────────────────────
//
// ONE pure function turns (row facts, t, xInRow) into a SidebarDropSlot = (kind, depth, refusal). A row renders a
// LINE (Before/After/EndOfList, indented to the resolved depth) or a PLATE (Into) — never both, never
// neither-while-armed. Refusal lifts "folder into its own descendant" / "already there" from a silent `false` up to
// the sentence the drag chip shows.

/// <summary>What a rootlist drop at the resolved position MEANS. <see cref="None"/> is "nothing is armed here" — also
/// what a REFUSED slot reports, so a refused row draws neither a line nor a plate.</summary>
public enum SidebarDropKind : byte
{
    None = 0,
    /// <summary>An insertion line ABOVE the row, at <see cref="SidebarDropSlot.Depth"/>.</summary>
    Before = 1,
    /// <summary>An insertion line BELOW the row, at <see cref="SidebarDropSlot.Depth"/> — may be SHALLOWER than the
    /// row's own depth, which is the "move out of this folder" gesture.</summary>
    After = 2,
    /// <summary>Into the row: a folder takes the item as a child, an editable playlist takes the payload's TRACKS.
    /// The plate — and the ONLY kind that draws one.</summary>
    Into = 3,
    /// <summary>The end of the whole tree, at depth 0 — the <c>TreeEnd</c> chrome row's only slot.</summary>
    EndOfList = 4,
}

/// <summary>Why a drop at this position is refused, in the order the table evaluates. Each value maps to exactly one
/// caption, because a refusing drop target is transparent to the engine and the caption is the ONLY thing that
/// reaches the user.</summary>
public enum SidebarDropRefusal : byte
{
    None = 0,
    /// <summary>The row IS the dragged item.</summary>
    Self = 1,
    /// <summary>Into the dragged FOLDER itself.</summary>
    IntoItself = 2,
    /// <summary>The row lives inside the dragged folder — filing a folder into its own subtree.</summary>
    IntoDescendant = 3,
    /// <summary>The resolved destination is where the item already is.</summary>
    NoOp = 4,
    /// <summary>The list is showing a non-custom SORT, so a positional insert cannot be honoured.</summary>
    SortedList = 5,
    /// <summary>The rootlist has not arrived, so there is no order to write into.</summary>
    NotLoaded = 6,
    /// <summary>The geometry is degenerate (no plan row, no viewport, no scene) — refuse with a reason rather than
    /// guess a placement.</summary>
    Unavailable = 7,
    /// <summary>The library write this gesture needs has no seam installed (<c>Sidebar.LibraryWrites</c> or the one
    /// member of it) — the "never a silent no-op" arm of every drop, "+" and menu verb that writes the library.</summary>
    WritesUnavailable = 8,
}

/// <summary>One localized sentence per refusal, keyed by the RAW dotted loc key (verified present in
/// <c>assets/loc/en-US.json</c> under <c>drag.*</c>). Engine-free by construction: the Pane layer wraps this with its
/// own <c>Loc.Get</c>/<c>Strings</c> lookup — the table itself, and the rule that every non-<see cref="SidebarDropRefusal.None"/>
/// value has ONE entry, lives here so a refusal can never reach the UI as a silent <c>return false</c>.</summary>
public static class SidebarDropRefusalText
{
    /// <summary>The dotted loc key for a refusal, or "" for <see cref="SidebarDropRefusal.None"/> (nothing to say).</summary>
    public static string LocKey(SidebarDropRefusal refusal) => refusal switch
    {
        SidebarDropRefusal.Self => "drag.cantMoveHere",
        SidebarDropRefusal.Unavailable => "drag.cantMoveHere",
        SidebarDropRefusal.IntoItself => "drag.cantMoveIntoItself",
        SidebarDropRefusal.IntoDescendant => "drag.cantMoveIntoItself",
        SidebarDropRefusal.NoOp => "drag.alreadyThere",
        SidebarDropRefusal.SortedList => "drag.clearSortingToReorder",
        SidebarDropRefusal.NotLoaded => "drag.stillLoading",
        SidebarDropRefusal.WritesUnavailable => "drag.libraryUnavailable",
        _ => "",
    };
}

/// <summary>Everything the resolver needs about ONE row, and nothing about the renderer. The one payload-dependent
/// member (<see cref="SourceIsSelf"/>) is computed at HOVER, because that is the first moment the payload exists; the
/// structural ones come from the plan.</summary>
/// <param name="NextVisibleDepth">Depth of the next VISIBLE tree row; 0 when this is the last tree row. Together with
/// <paramref name="Depth"/> it is the whole depth-ambiguity story: a slot is ambiguous iff the two differ downward.</param>
/// <param name="CenterAccepts">This row has a dead centre that DEPOSITS: a folder (always), or an editable playlist
/// whose centre takes the payload's tracks.</param>
/// <param name="SourceIsSelf">This row IS the dragged item. Every OTHER legality question — cycle, no-op — is the
/// marker stream's, never re-derived here.</param>
/// <param name="IsListEnd">The synthetic <c>TreeEnd</c> row: the whole row is one <see cref="SidebarDropKind.EndOfList"/>
/// slot at depth 0, with no bands at all.</param>
public readonly record struct SidebarRowFacts(
    bool IsFolder,
    bool FolderExpanded,
    bool FolderHasChildren,
    int Depth,
    int NextVisibleDepth,
    bool CenterAccepts,
    bool SourceIsSelf,
    bool SortedNonCustom,
    bool RootlistLoaded)
{
    public bool IsListEnd { get; init; }
}

/// <summary>The published slot: WHERE the drop lands, at WHAT depth, or WHY it will not.
/// <para><b>Invariant:</b> a slot with a <see cref="Refusal"/> always carries <see cref="SidebarDropKind.None"/>. The
/// cue therefore has one rule — line ⟺ Before/After/EndOfList, plate ⟺ Into — and a refusal draws neither.</para></summary>
public readonly record struct SidebarDropSlot(int PlanIndex, SidebarDropKind Kind, int Depth, SidebarDropRefusal Refusal)
{
    public static readonly SidebarDropSlot None = new(-1, SidebarDropKind.None, 0, SidebarDropRefusal.None);

    /// <summary>Is anything actually armed here (a line or a plate)?</summary>
    public bool IsArmed => Kind != SidebarDropKind.None;

    /// <summary>Delegates to <see cref="SidebarDropCue.DrawsLine"/> — two hand-written copies of "which kinds draw a
    /// line" is exactly the drift the one-rule invariant exists to prevent.</summary>
    public bool DrawsLine => SidebarDropCue.DrawsLine(Kind);

    /// <summary>Delegates to <see cref="SidebarDropCue.DrawsPlate"/> for the same reason as <see cref="DrawsLine"/>.</summary>
    public bool DrawsPlate => SidebarDropCue.DrawsPlate(Kind);
}

/// <summary>The pure geometry + legality rules behind every sidebar rootlist drop.</summary>
public static class RootlistSlotResolver
{
    /// <summary>Fraction of the row height each edge band claims, before the clamp.</summary>
    public const float EdgeFraction = 0.30f;
    /// <summary>Minimum edge-band height. Below this the band is smaller than the pointer's own precision.</summary>
    public const float MinEdge = 10f;
    /// <summary>Maximum edge-band height. Above this a 48-DIP comfortable row loses its centre entirely.</summary>
    public const float MaxEdge = 16f;
    /// <summary>Hysteresis around each depth boundary (DIP). Without it the line flickers between two depths while
    /// the hand holds still.</summary>
    public const float DepthHysteresis = 4f;

    /// <summary>The edge-band height for a row: <c>clamp(0.30·h, 10, 16)</c>, capped at half the row. ONE band for
    /// every payload and ownership state.</summary>
    public static float EdgeFor(float rowHeight)
    {
        if (!float.IsFinite(rowHeight) || rowHeight <= 0f) return MinEdge;
        float edge = rowHeight * EdgeFraction;
        if (edge < MinEdge) edge = MinEdge;
        if (edge > MaxEdge) edge = MaxEdge;
        // A degenerate (very short) row cannot carry two bands and a centre; half the row is the honest cap.
        float half = rowHeight * 0.5f;
        return edge > half ? half : edge;
    }

    /// <summary>The depths an <see cref="SidebarDropKind.After"/> slot on this row may address.
    /// <para><c>Max</c> is the row's own depth. <c>Min</c> is the next visible row's depth, clamped so it never
    /// exceeds <c>Max</c>. <c>Min &lt; Max</c> is exactly "after the last visible child of a (possibly nested)
    /// folder" — the one genuinely ambiguous place.</para></summary>
    public static (int Min, int Max) DepthRange(in SidebarRowFacts f)
    {
        int max = f.Depth < 0 ? 0 : f.Depth;
        int min = f.NextVisibleDepth < 0 ? 0 : f.NextVisibleDepth;
        if (min > max) min = max;
        return (min, max);
    }

    /// <summary>Resolve one pointer position over one row into the slot it means.</summary>
    /// <param name="planIndex">The row's plan index; negative = degenerate ⇒ <see cref="SidebarDropRefusal.Unavailable"/>.</param>
    /// <param name="t">Normalized vertical position inside the row, 0 = top edge, 1 = bottom edge.</param>
    /// <param name="xInRow">Pointer X relative to the row's own left edge — the DEPTH channel.</param>
    /// <param name="rowHeight">The row's measured extent.</param>
    /// <param name="previous">The slot published on the previous move, for depth hysteresis. Pass
    /// <see cref="SidebarDropSlot.None"/> when there is none.</param>
    /// <param name="ladder">The structure's tree ladder (<see cref="NavRowKit.TreeLadder"/>): where a depth-0 row's content
    /// starts and how far one nesting level moves it. The depth pick reads exactly what the rows and the caret are drawn on.</param>
    public static SidebarDropSlot Resolve(int planIndex, float t, float xInRow, float rowHeight,
                                          in SidebarRowFacts f, in SidebarDropSlot previous, in NavTreeLadder ladder)
    {
        if (planIndex < 0 || !float.IsFinite(t) || !float.IsFinite(rowHeight) || rowHeight <= 0f)
            return new SidebarDropSlot(planIndex, SidebarDropKind.None, 0, SidebarDropRefusal.Unavailable);

        // The zone first: two refusals below (IntoItself, SortedList) depend on WHICH zone was picked.
        var (kind, depth) = Zone(t, xInRow, rowHeight, in f, in previous, in ladder);

        var refusal = Refuse(kind, in f);
        return refusal == SidebarDropRefusal.None
            ? new SidebarDropSlot(planIndex, kind, depth, SidebarDropRefusal.None)
            : new SidebarDropSlot(planIndex, SidebarDropKind.None, depth, refusal);
    }

    static SidebarDropRefusal Refuse(SidebarDropKind kind, in SidebarRowFacts f)
    {
        if (!f.RootlistLoaded) return SidebarDropRefusal.NotLoaded;
        // Into the dragged folder itself gets its OWN sentence: "can't move a folder into itself" says the thing the
        // user tried, where the generic "can't move here" would leave them guessing which rule they hit.
        if (f.SourceIsSelf) return kind == SidebarDropKind.Into ? SidebarDropRefusal.IntoItself : SidebarDropRefusal.Self;
        // A non-custom SORT cannot show a positional insert; a DEPOSIT into a folder/playlist is unaffected by sort.
        if (f.SortedNonCustom && kind is SidebarDropKind.Before or SidebarDropKind.After or SidebarDropKind.EndOfList)
            return SidebarDropRefusal.SortedList;
        return SidebarDropRefusal.None;
    }

    static (SidebarDropKind Kind, int Depth) Zone(float t, float xInRow, float rowHeight,
                                                  in SidebarRowFacts f, in SidebarDropSlot previous, in NavTreeLadder ladder)
    {
        // The tree's END: one whole-row slot at the root level. No bands — there is nothing below it to be "after".
        if (f.IsListEnd) return (SidebarDropKind.EndOfList, 0);

        int depth = f.Depth < 0 ? 0 : f.Depth;
        float edge = EdgeFor(rowHeight);
        float top = edge / rowHeight;
        float bottom = 1f - top;

        if (f.IsFolder)
        {
            // Dropping ON a folder means INTO it (Explorer/Finder/VS Code/Spotify all agree).
            if (t < top) return (SidebarDropKind.Before, depth);
            if (t > bottom)
                // The bottom band of an EXPANDED header is the precise "first child" slot: the line indents one step
                // and the drop lands ahead of the folder's current first child.
                return f.FolderExpanded && f.FolderHasChildren
                    ? (SidebarDropKind.Before, depth + 1)
                    : (SidebarDropKind.After, PickDepth(xInRow, in f, in previous, in ladder));
            return (SidebarDropKind.Into, depth);
        }

        if (f.CenterAccepts)
        {
            // The retained copy gesture: an editable playlist's centre deposits the payload's tracks. Survives only
            // because the PLATE now distinguishes it from the two edge bands, which draw a line.
            if (t < top) return (SidebarDropKind.Before, depth);
            if (t > bottom) return (SidebarDropKind.After, PickDepth(xInRow, in f, in previous, in ladder));
            return (SidebarDropKind.Into, depth);
        }

        // Everything else: two zones, no dead centre. A row that cannot take a deposit must not reserve half its
        // height for one.
        return t < 0.5f
            ? (SidebarDropKind.Before, depth)
            : (SidebarDropKind.After, PickDepth(xInRow, in f, in previous, in ladder));
    }

    /// <summary>The DEPTH channel: only an <see cref="SidebarDropKind.After"/> slot spanning more than one depth
    /// reads the pointer's X at all. The default — pointer over the row's LABEL — is <c>Max</c> ("stay at this row's
    /// depth"); travelling LEFT outdents one step per indent level.</summary>
    static int PickDepth(float xInRow, in SidebarRowFacts f, in SidebarDropSlot previous, in NavTreeLadder ladder)
    {
        var (min, max) = DepthRange(in f);
        if (min >= max) return max;
        if (!float.IsFinite(xInRow)) return max;

        // THE LADDER IS THE ROW'S OWN, and the structure's (`NavRowKit.TreeLadder`). Its origin plus one step per depth is where a
        // tree row at depth d starts drawing (Classic: 31 per level from the row origin, the pill's x) — so one step left is one
        // outdent. There is no reserved disclosure cell (the folder chevron is trailing).
        float steps = (xInRow - ladder.OriginX) / ladder.IndentStep;
        int picked = (int)MathF.Round(steps);
        if (picked < min) picked = min;
        if (picked > max) picked = max;

        // HYSTERESIS. Without it the boundary between two depths sits under a still hand and the line flickers. The
        // previous depth is held until the pointer is a clear 4 DIP past the boundary that would leave it.
        if (previous.Kind == SidebarDropKind.After && previous.Depth >= min && previous.Depth <= max
            && previous.Depth != picked)
        {
            float boundary = ladder.OriginX
                + (previous.Depth + (picked > previous.Depth ? 0.5f : -0.5f)) * ladder.IndentStep;
            float travelled = MathF.Abs(xInRow - boundary);
            if (travelled < DepthHysteresis) return previous.Depth;
        }
        return picked;
    }
}

/// <summary>The insertion cue's FACTS: which slot kinds draw the line and which the plate, in the engine-free layer so a test
/// can drive the real mapping. The pictures (the caret's stroke, dot, width and Y; the plate) are the structure's
/// (Classic's <c>SidebarGeometry</c> and <c>ClassicNavRow</c>).
/// <para><b>The invariant:</b> <b>line ⟺ Before/After/EndOfList, plate ⟺ Into, never both, never
/// neither-while-armed.</b> Three outcomes used to share one accent plate, so the surface could not say which of them
/// a drop meant.</para></summary>
public static class SidebarDropCue
{
    /// <summary>Does this slot draw the line? (The one predicate the row's Opacity bind reads.)</summary>
    public static bool DrawsLine(SidebarDropKind kind)
        => kind is SidebarDropKind.Before or SidebarDropKind.After or SidebarDropKind.EndOfList;

    /// <summary>Does this slot draw the accent plate? (The one predicate the row's Fill/Border binds read.)</summary>
    public static bool DrawsPlate(SidebarDropKind kind) => kind == SidebarDropKind.Into;
}

/// <summary>Where a rootlist item CAME FROM, so a completed move can be offered back as Undo.
/// <para>Pure, over the SAME depth-first flattened tree the planner consumes (the FULL tree, not the
/// expansion-filtered plan), so the anchor is the item's real pre-move sibling and not whichever row happened to be
/// visible. Expressed as an ordinary <c>(RootlistItemRef, RootlistDropPlacement)</c> pair, so the inverse rides the
/// very same move seam the forward move did.</para></summary>
public static class RootlistUndoAnchors
{
    /// <summary>Resolve the move that would put <paramref name="entryId"/> back exactly where it is now. The N=1
    /// sugar over <see cref="TryResolveMany"/>.
    /// <para>False when there is nothing to anchor against (the item is the tree's only member, or not in the tree at
    /// all) — the caller then shows its toast WITHOUT an Undo action.</para></summary>
    public static bool TryResolve(IReadOnlyList<SidebarLibraryEntry>? tree, string entryId,
                                  out RootlistItemRef anchor, out RootlistDropPlacement placement)
    {
        anchor = default;
        placement = RootlistDropPlacement.After;
        if (!TryResolveMany(tree, [entryId], out var undo) || undo.Count != 1) return false;
        anchor = undo[0].Target;
        placement = undo[0].Placement;
        return true;
    }

    /// <summary>Resolve the BATCH that would put a whole multi-selection back exactly where it is now, computed
    /// BEFORE the move (once the rootlist has shifted, where the items used to be is unknowable). Each item anchors
    /// on its previous UNSELECTED sibling (After) → its next unselected sibling (Before) → its parent folder
    /// (Inside) — unselected siblings are the only rows guaranteed not to have moved. The emitted ORDER is
    /// <see cref="RootlistBatchOrder.For"/>'s, applied per run of items sharing an anchor. False if ANY item has no
    /// resolvable anchor: a partial Undo would scatter the rest, so the toast carries none.</summary>
    public static bool TryResolveMany(IReadOnlyList<SidebarLibraryEntry>? tree, IReadOnlyList<string> ids,
                                      out IReadOnlyList<RootlistMove> undo)
    {
        undo = Array.Empty<RootlistMove>();
        if (tree is null || ids is null || ids.Count == 0) return false;

        // The selection as the MOVE sees it: tree order, descendants of a selected folder dropped.
        var selected = RootlistSelection.Normalize(tree, ids);
        if (selected.Count == 0) return false;
        var chosen = new HashSet<string>(selected.Count, StringComparer.Ordinal);
        for (int i = 0; i < selected.Count; i++) chosen.Add(selected[i].Id);

        var sources = new List<RootlistItemRef>(selected.Count);
        var anchors = new List<(RootlistItemRef Target, RootlistDropPlacement Placement)>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            var self = selected[i];
            var source = RefOf(self);
            if (source.Key.Length == 0) return false;
            if (!TryAnchor(tree, in self, chosen, out var target, out var placement)) return false;
            sources.Add(source);
            anchors.Add((target, placement));
        }

        var moves = new List<RootlistMove>(sources.Count);
        for (int start = 0; start < sources.Count;)
        {
            int end = start + 1;
            while (end < sources.Count && anchors[end] == anchors[start]) end++;
            var run = sources.GetRange(start, end - start);
            moves.AddRange(RootlistBatchOrder.For(run, anchors[start].Target, anchors[start].Placement));
            start = end;
        }
        if (moves.Count == 0) return false;
        undo = moves;
        return true;
    }

    /// <summary>Where ONE item came from, expressed against a row the batch is NOT moving.</summary>
    static bool TryAnchor(IReadOnlyList<SidebarLibraryEntry> tree, in SidebarLibraryEntry self,
                          HashSet<string> selected, out RootlistItemRef anchor, out RootlistDropPlacement placement)
    {
        anchor = default;
        placement = RootlistDropPlacement.After;

        int at = -1;
        for (int i = 0; i < tree.Count; i++)
            if (string.Equals(tree[i].Id, self.Id, StringComparison.Ordinal)) { at = i; break; }
        if (at < 0) return false;
        int depth = self.Depth;

        // The PREVIOUS unselected sibling — the exact anchor whenever one exists.
        for (int i = at - 1; i >= 0; i--)
        {
            int d = tree[i].Depth;
            if (d > depth) continue;                 // a descendant of an earlier sibling
            if (d < depth) break;                    // the parent: this item is the first (unselected-wise) child
            if (selected.Contains(tree[i].Id)) continue;
            anchor = RefOf(tree[i]);
            placement = RootlistDropPlacement.After;
            return anchor.Key.Length > 0;
        }

        // First among its unselected siblings: anchor on the next one instead.
        for (int i = at + 1; i < tree.Count; i++)
        {
            int d = tree[i].Depth;
            if (d > depth) continue;                 // this item's own subtree, or a later sibling's
            if (d < depth) break;                    // out of the folder: there is no next sibling
            if (selected.Contains(tree[i].Id)) continue;
            anchor = RefOf(tree[i]);
            placement = RootlistDropPlacement.Before;
            return anchor.Key.Length > 0;
        }

        // The only UNSELECTED-adjacent child of its folder: append back into that folder.
        if (self.ParentFolderId is { Length: > 0 } parent)
        {
            anchor = new RootlistItemRef(parent, IsFolder: true);
            placement = RootlistDropPlacement.Inside;
            return true;
        }
        return false;   // the only top-level item — there is no move to undo
    }

    // The rootlist reference one entry moves AS is ONE rule and lives in RootlistTreeNav.RefOf.
    static RootlistItemRef RefOf(in SidebarLibraryEntry entry) => RootlistTreeNav.RefOf(in entry);
}

// ── the drop decision: MAP then CHECK ─────────────────────────────────────────────────────────────────────────────
//
// THE ONE ANSWER TO "where does this drop land, and may it?". MAP turns the geometry cue plus the FULL projection
// tree into a destination; CHECK asks the store's own marker stream via RootlistOps — the same authority the
// keyboard/menu verbs and the folder picker use, so nothing here can disagree with them. A cue that cannot be mapped
// is never armed; it publishes Unavailable rather than a placement that would silently drop nothing.

/// <summary>A resolved rootlist destination: the tree entry it is expressed against, the seam ref + placement the
/// mutation rides, and the folder the item will END UP IN.
/// <para><see cref="DestinationName"/> is the toast's subject, computed from the MAPPED TARGET (never from whichever
/// row the pointer happened to be over). Empty = the top level.</para>
/// <para><see cref="Deposit"/> marks the one slot that is not a rootlist move at all — dropping a playlist on an
/// editable playlist's centre to copy its songs.</para></summary>
/// <param name="AnchorName">The name of the entry the placement is expressed AGAINST — for an outdent, the folder
/// the item is leaving. Distinct from <paramref name="DestinationName"/>: they used to be answered by the same
/// (wrong) field.</param>
public readonly record struct RootlistSlotTarget(string EntryId, RootlistItemRef Ref, RootlistDropPlacement Placement,
                                          string DestinationName, string AnchorName, bool Deposit);

/// <summary>Cue + tree → destination. GEOMETRY IS NOT LEGALITY: nothing here refuses a move, and nothing here reads
/// the visible (expansion-filtered) plan.</summary>
public static class RootlistSlotMapper
{
    /// <summary>Map one armed cue onto the rootlist destination it means. <paramref name="rowEntryId"/> is the entry
    /// behind the hovered plan row ("" for the synthetic <c>TreeEnd</c> row). <paramref name="tree"/> must be the
    /// FULL flattened tree (collapsed subtrees included) so "first child" and the outdent walk still resolve inside
    /// a collapsed folder. False = no destination; the caller must publish <c>Unavailable</c>, never an armed slot.</summary>
    public static bool TryMap(in SidebarDropSlot cue, string rowEntryId,
                              IReadOnlyList<SidebarLibraryEntry>? tree, out RootlistSlotTarget target)
    {
        target = default;
        if (tree is null || tree.Count == 0) return false;

        // The tree's END marker is CHROME: it stands for no entity, so it resolves before the row lookup. Its anchor
        // is the last TOP-LEVEL entry, whose exclusive range end lands after a trailing folder's whole subtree.
        if (cue.Kind == SidebarDropKind.EndOfList)
        {
            if (!TryLastTopLevel(tree, out var last)) return false;
            target = new RootlistSlotTarget(last.Id, RootlistTreeNav.RefOf(in last),
                                            RootlistDropPlacement.After, "", last.Name, false);
            return true;
        }

        if (!RootlistTreeNav.TryEntry(tree, rowEntryId, out var entry)) return false;

        switch (cue.Kind)
        {
            case SidebarDropKind.Into:
                // A PLAYLIST's centre takes the payload's TRACKS — not a rootlist move at all. A FOLDER's centre
                // takes the item as a child.
                target = entry.IsFolder
                    ? new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                             RootlistDropPlacement.Inside, entry.Name, entry.Name, false)
                    : new RootlistSlotTarget(entry.Id, default, RootlistDropPlacement.Inside, entry.Name, entry.Name, true);
                return true;

            case SidebarDropKind.Before when cue.Depth > entry.Depth && entry.IsFolder:
                // The bottom band of an EXPANDED folder header: the precise "first child" slot. An EMPTY folder has
                // no child to land before, so "inside it" is the same place.
                if (TryFirstChild(tree, in entry, out var firstChild))
                {
                    target = new RootlistSlotTarget(firstChild.Id, RootlistTreeNav.RefOf(in firstChild),
                                                    RootlistDropPlacement.Before, entry.Name, firstChild.Name, false);
                    return true;
                }
                target = new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                                RootlistDropPlacement.Inside, entry.Name, entry.Name, false);
                return true;

            case SidebarDropKind.Before:
                target = new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                                RootlistDropPlacement.Before, entry.ParentFolderName, entry.Name, false);
                return true;

            case SidebarDropKind.After when cue.Depth < entry.Depth:
                // THE OUTDENT: "after the last child of a folder", aimed left, means AFTER THE FOLDER. Expressing it
                // against the CHILD (what the shifted depth pick forced) lands it straight back inside.
                if (!TryAncestorFolder(tree, in entry, entry.Depth - cue.Depth, out var ancestor)) return false;
                target = new RootlistSlotTarget(ancestor.Id, RootlistTreeNav.RefOf(in ancestor),
                                                RootlistDropPlacement.After, ancestor.ParentFolderName, ancestor.Name, false);
                return true;

            case SidebarDropKind.After:
                target = new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                                RootlistDropPlacement.After, entry.ParentFolderName, entry.Name, false);
                return true;

            default:
                return false;
        }
    }

    /// <summary>The folder's FIRST child in the full tree — the entry directly after it, one level deeper.</summary>
    static bool TryFirstChild(IReadOnlyList<SidebarLibraryEntry> tree, in SidebarLibraryEntry folder,
                              out SidebarLibraryEntry child)
    {
        child = default;
        for (int i = 0; i < tree.Count; i++)
        {
            if (!string.Equals(tree[i].Id, folder.Id, StringComparison.Ordinal)) continue;
            if (i + 1 >= tree.Count) return false;
            var next = tree[i + 1];
            if (next.Depth != folder.Depth + 1) return false;      // the folder is empty (or collapsed out of the tree)
            child = next;
            return true;
        }
        return false;
    }

    /// <summary>Walk <paramref name="levels"/> containing folders up from <paramref name="entry"/>, over the FULL tree.</summary>
    static bool TryAncestorFolder(IReadOnlyList<SidebarLibraryEntry> tree, in SidebarLibraryEntry entry, int levels,
                                  out SidebarLibraryEntry folder)
    {
        folder = entry;
        if (levels <= 0) return false;
        for (int i = 0; i < levels; i++)
        {
            if (folder.ParentFolderId is not { Length: > 0 } parent
                || !RootlistTreeNav.TryFolder(tree, parent, out folder)) return false;
        }
        return folder.IsFolder;
    }

    /// <summary>The last TOP-LEVEL tree entry — the anchor "move to the end" files against.</summary>
    static bool TryLastTopLevel(IReadOnlyList<SidebarLibraryEntry> tree, out SidebarLibraryEntry entry)
    {
        entry = default;
        for (int i = tree.Count - 1; i >= 0; i--)
            if (tree[i].Depth == 0) { entry = tree[i]; return true; }
        return false;
    }
}

/// <summary>THE published-slot decision: map, then check against the marker stream, then refuse or arm. Every
/// rootlist drop surface publishes what this returns and commits the target it hands back, so a cue and its mutation
/// cannot describe different destinations.</summary>
public static class RootlistDropDecision
{
    /// <summary>The ONE <see cref="RootlistMoveCheck"/> → <see cref="SidebarDropRefusal"/> table. Every one of these
    /// used to be a silent <c>false</c> three layers below the pointer.</summary>
    public static SidebarDropRefusal RefusalFor(RootlistMoveCheck check) => check switch
    {
        RootlistMoveCheck.Ok => SidebarDropRefusal.None,
        RootlistMoveCheck.NoOp => SidebarDropRefusal.NoOp,
        RootlistMoveCheck.Cycle => SidebarDropRefusal.IntoDescendant,
        RootlistMoveCheck.SameItem => SidebarDropRefusal.Self,
        // Missing (source/target not in the stream) and Invalid (a placement the stream cannot express) are both
        // "this position has no meaning" — the honest refusal, never a guessed placement.
        _ => SidebarDropRefusal.Unavailable,
    };

    /// <summary>Refine a GEOMETRY cue into the slot the surface publishes, and hand back the destination it commits.
    /// An unarmed cue passes through untouched; an armed one with no mapping is DISARMED with
    /// <see cref="SidebarDropRefusal.Unavailable"/> rather than left publishing a slot nothing could map. A mapped
    /// ordering is then checked against <paramref name="markers"/>.</summary>
    /// <param name="sources">The dragged items as the rootlist addresses them, in TREE ORDER (one item = a list of
    /// one). A source that IS the hovered target does NOT refuse the batch — it drops as a legal GATHER; hovering one
    /// of your OWN rows still says <see cref="SidebarDropRefusal.Self"/>, from the resolver's <c>SourceIsSelf</c>
    /// fact, which refuses the cue BEFORE it reaches here.</param>
    /// <param name="markers">The store's marker stream. Null/empty refuses every ordering
    /// <see cref="SidebarDropRefusal.Unavailable"/> rather than arming against indices nobody has.</param>
    public static SidebarDropSlot Refine(in SidebarDropSlot cue, string rowEntryId,
                                         IReadOnlyList<SidebarLibraryEntry>? tree,
                                         IReadOnlyList<RootlistEntry>? markers,
                                         IReadOnlyList<RootlistItemRef> sources, out RootlistSlotTarget target)
    {
        target = default;
        if (!cue.IsArmed) return cue;
        if (!RootlistSlotMapper.TryMap(in cue, rowEntryId, tree, out target))
            return new SidebarDropSlot(cue.PlanIndex, SidebarDropKind.None, cue.Depth, SidebarDropRefusal.Unavailable);
        if (target.Deposit) return cue;                       // a track copy is not a rootlist ORDERING at all

        var refusal = RefusalFor(Check(markers, sources, target.Ref, target.Placement,
                                       cue.Kind == SidebarDropKind.EndOfList));
        return refusal == SidebarDropRefusal.None
            ? cue
            : new SidebarDropSlot(cue.PlanIndex, SidebarDropKind.None, cue.Depth, refusal);
    }

    /// <summary>The legality question, asked of the ONE authority. Every caller — hover, commit, the rail tile's
    /// accept predicate, the folder picker, the bridge — goes through this.
    /// <para>The batch it asks about is <see cref="RootlistBatchOrder.For"/>'s — the SAME ordered move list the
    /// commit issues, so the cue cannot answer a different question from the write.</para></summary>
    public static RootlistMoveCheck Check(IReadOnlyList<RootlistEntry>? markers,
                                          IReadOnlyList<RootlistItemRef>? sources,
                                          RootlistItemRef target, RootlistDropPlacement placement,
                                          bool endOfList = false)
    {
        if (markers is null || markers.Count == 0) return RootlistMoveCheck.Missing;
        if (sources is null || sources.Count == 0 || target.Key.Length == 0) return RootlistMoveCheck.Missing;
        for (int i = 0; i < sources.Count; i++)
            if (sources[i].Key.Length == 0) return RootlistMoveCheck.Missing;
        return RootlistOps.CheckMoves(markers, RootlistBatchOrder.For(sources, target, placement, endOfList));
    }

    /// <summary>The N=1 sugar. One item is a batch of one, so it answers through the very same builder.</summary>
    public static RootlistMoveCheck Check(IReadOnlyList<RootlistEntry>? markers, RootlistItemRef source,
                                          RootlistItemRef target, RootlistDropPlacement placement)
        => Check(markers, [source], target, placement);
}

// ── the "+" create destinations ────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What a drop on a "+" create affordance means (G-170).</summary>
public enum SidebarCreateDrop : byte
{
    /// <summary>None of this target's business — the drag is crossing on its way somewhere else. No cue at all.</summary>
    Transparent = 0,
    /// <summary>A new folder holding the dropped rootlist items.</summary>
    NewFolder = 1,
    /// <summary>A new playlist seeded from the dropped tracks.</summary>
    NewPlaylist = 2,
    /// <summary>The drop means something, but the library write it needs has no seam: refuse, with the sentence.</summary>
    Refused = 3,
}

/// <summary>THE "+" DROP RULE (G-170): one decision per create destination, so the cue, the refusal caption and the
/// commit all ask the same question — and a missing seam member REFUSES (a caption while hovering, a toast on a drop)
/// instead of arming a cue for a drop that does nothing. Allocation-free: it runs per frame while a drag is live.</summary>
public static class SidebarCreateDropRules
{
    /// <summary>A rootlist filing gesture: a playlist or folder dragged AS a rootlist item.</summary>
    public static bool IsFiling(DragPayload payload) => payload.RootlistItem && payload.Kind is DragKind.Playlist or DragKind.Folder;

    /// <summary>The PlaylistTree header "+": rootlist items ⇒ a new top-level folder holding them; a track set that is
    /// not itself a rootlist item ⇒ a new playlist from it; anything else crosses.</summary>
    public static SidebarCreateDrop Header(DragPayload payload, SidebarLibraryWrites? writes)
    {
        if (IsFiling(payload)) return writes?.NewFolderWith is null ? SidebarCreateDrop.Refused : SidebarCreateDrop.NewFolder;
        if (payload.CanCopyTracks && !payload.RootlistItem)
            return writes?.CreatePlaylistWith is null ? SidebarCreateDrop.Refused : SidebarCreateDrop.NewPlaylist;
        return SidebarCreateDrop.Transparent;
    }

    /// <summary>A folder row's "+": rootlist items ⇒ a new sub-folder inside it; a track set crosses (it is on its way
    /// to a playlist row).</summary>
    public static SidebarCreateDrop Folder(DragPayload payload, SidebarLibraryWrites? writes)
        => !IsFiling(payload) ? SidebarCreateDrop.Transparent
            : writes?.NewFolderWith is null ? SidebarCreateDrop.Refused : SidebarCreateDrop.NewFolder;

    /// <summary>A click verb that writes the library (a "+" click, "New playlist in this folder", "Rename folder"):
    /// the refusal to say when its seam member is absent, else <see cref="SidebarDropRefusal.None"/>.</summary>
    public static SidebarDropRefusal Verb(bool seamPresent)
        => seamPresent ? SidebarDropRefusal.None : SidebarDropRefusal.WritesUnavailable;
}

// ── the row-level decisions (themes plan §5.1 row C12, BP X30) ───────────────────────────────────────────────────────────
//
// What a row's drop target ANSWERS for a payload: does it accept it, is it merely crossing (transparent), or was it aimed
// at and refused (and the sentence). They were closures inside the Classic pane's `ResourceDropSpec`; the facts they read
// (is the rootlist loaded, is this row the dragged one) now come in as arguments, so the whole table is a pure function of
// (row, payload, loaded) and `NavDropRulesTests` pins it. The POINTER half stays in the view — turning a position into a
// row-relative slot is geometry — and so does everything that draws a cue.

/// <summary>The structural facts of ONE row as a drop target. <see cref="RootTarget"/> is the row as a rootlist item (null:
/// not a rootlist row, so a rootlist filing is none of its business). <see cref="PinSlot"/> is its slot in the Pinned band
/// (negative: not a pinned row). <see cref="PlaylistUri"/> is set only for an EDITABLE playlist; <see cref="IsPlaylistRow"/>
/// says the row is a playlist at all — the refusal-versus-transparent split. <see cref="HasRailCue"/>: it is a rail tile.</summary>
public readonly record struct NavDropRow(DragPayload? RootTarget, int PinSlot, string? PlaylistUri, bool IsPlaylistRow, bool HasRailCue)
{
    /// <summary>The row is a playlist, editable or not.</summary>
    public bool IsPlaylist => IsPlaylistRow || PlaylistUri is { Length: > 0 };

    /// <summary>A track set dropped here can be copied INTO the playlist.</summary>
    public bool CanDeposit => PlaylistUri is { Length: > 0 };
}

/// <summary>Which arm of a row's drop owns the commit.</summary>
public enum NavDropArm : byte
{
    /// <summary>Nothing to do (an artist dropped on a plain row).</summary>
    None = 0,
    /// <summary>A same-band reorder: the band's own Reorderable commits it.</summary>
    OwnReorder = 1,
    /// <summary>The rootlist arm: the published slot decides.</summary>
    Rootlist = 2,
    /// <summary>A track deposit into an editable playlist.</summary>
    Deposit = 3,
    /// <summary>A foreign entity onto the pinned band: pin it there.</summary>
    Pin = 4,
}

/// <summary>How far a rail-folder drop gets before the marker stream is asked.</summary>
public enum NavRailFolderGate : byte
{
    /// <summary>Not a rootlist filing: a track drag is only crossing.</summary>
    Transparent = 0,
    /// <summary>The rootlist has not arrived.</summary>
    NotLoaded = 1,
    /// <summary>The folder is the dragged item.</summary>
    IntoItself = 2,
    /// <summary>Ask the marker stream.</summary>
    Open = 3,
}

public static class NavDropRules
{
    /// <summary>A rootlist filing gesture: a playlist or folder dragged AS a rootlist item.</summary>
    public static bool IsFiling(DragPayload payload) => SidebarCreateDropRules.IsFiling(payload);

    /// <summary>The seam refs of a rootlist payload (the root refs, retyped).</summary>
    public static IReadOnlyList<RootlistItemRef> RefsOf(DragPayload payload)
    {
        var roots = payload.RootRefs();
        var refs = new RootlistItemRef[roots.Length];
        for (int i = 0; i < roots.Length; i++) refs[i] = new RootlistItemRef(roots[i].Key, roots[i].IsFolder);
        return refs;
    }

    /// <summary>The projection-entry ids a rootlist payload carries (undo anchors resolve against the TREE, the seam moves
    /// refs — one conversion, here). A ref the tree does not hold keeps its key.</summary>
    public static IReadOnlyList<string> SourceIds(IReadOnlyList<SidebarLibraryEntry>? tree, DragPayload source)
    {
        var refs = source.RootRefs();
        var ids = new List<string>(refs.Length);
        for (int i = 0; i < refs.Length; i++)
        {
            var r = refs[i];
            if (r.Key.Length == 0) continue;
            if (tree is null) { ids.Add(r.Key); continue; }
            for (int j = 0; j < tree.Count; j++)
            {
                var e = tree[j];
                if (!string.Equals(RootlistTreeNav.RefOf(in e).Key, r.Key, StringComparison.Ordinal)) continue;
                ids.Add(e.Id);
                break;
            }
        }
        return ids;
    }

    /// <summary>ONE sentence per refusal, shared by the accept-time reason, the positional caption and the commit's toast.</summary>
    public static string? Sentence(SidebarDropRefusal refusal)
        => SidebarDropRefusalText.LocKey(refusal) is { Length: > 0 } key ? Loc.Get(key) : null;

    // ── the table ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>This drop is a rootlist filing aimed at a rootlist row.</summary>
    public static bool Filing(in NavDropRow row, DragPayload source) => row.RootTarget is not null && IsFiling(source);

    /// <summary>The payload-only half of the refusal table — it can drive <c>accepts</c> (and so the not-allowed cue). ONE owner
    /// of "is this row one of the items the drag carries" (a multi-select carries N refs); every other legality question
    /// (cycle, no-op) is the marker stream's, per resolved destination.</summary>
    public static SidebarDropRefusal PayloadRefusal(in NavDropRow row, DragPayload source, bool rootlistLoaded)
    {
        if (row.RootTarget is not { } root || !Filing(in row, source)) return SidebarDropRefusal.None;
        if (!rootlistLoaded) return SidebarDropRefusal.NotLoaded;
        if (Drag.IsSource(source, root.Id, root.Uri))
            return root.Kind == DragKind.Folder ? SidebarDropRefusal.IntoItself : SidebarDropRefusal.Self;
        return SidebarDropRefusal.None;
    }

    /// <summary>The pinned region refuses what can never be a pin, WITH the reason (design corner case). A pinned PLAYLIST row
    /// keeps its own track answer: it deposits, or refuses with "can't edit this playlist", which names the real reason better
    /// than "Tracks can't be pinned". Null = no pin refusal.</summary>
    public static string? PinRefusalKey(in NavDropRow row, DragPayload source)
        => row.PinSlot >= 0 && !(row.IsPlaylist && source.CanCopyTracks)
            ? SidebarPinRules.RefusalKeyOf(source.Kind, source.Id, source.Uri)
            : null;

    /// <summary>Does the row accept this payload (a filing, a track deposit or a pin)?</summary>
    public static bool Compatible(in NavDropRow row, DragPayload source, bool rootlistLoaded)
    {
        if (Filing(in row, source)) return PayloadRefusal(in row, source, rootlistLoaded) == SidebarDropRefusal.None;
        if (PinRefusalKey(in row, source) is not null) return false;
        if (row.CanDeposit && source.CanCopyTracks) return true;
        return row.PinSlot >= 0 && source.CanPin;
    }

    /// <summary>The row's structural facts with the payload-dependent half folded in — at hover, the first moment the payload
    /// exists: whether the centre takes it, whether the row IS the dragged item, and the live rootlist-loaded gate.</summary>
    public static SidebarRowFacts FoldFacts(in NavDropRow row, in SidebarRowFacts rootFacts, DragPayload source, bool rootlistLoaded)
        => rootFacts with
        {
            CenterAccepts = rootFacts.IsFolder || (row.CanDeposit && source.CanCopyTracks),
            SourceIsSelf = PayloadRefusal(in row, source, rootlistLoaded) is SidebarDropRefusal.Self or SidebarDropRefusal.IntoItself,
            RootlistLoaded = rootlistLoaded,
        };

    /// <summary>TRANSPARENT = none of my business (an album / artist / show / route row a track drag is merely crossing).</summary>
    public static bool Transparent(in NavDropRow row, DragPayload source)
    {
        if (PinRefusalKey(in row, source) is not null) return false;
        if (row.HasRailCue && Drag.RailTileTransparent(source.RootlistItem, source.CanCopyTracks)) return true;
        if (row.PinSlot >= 0 && source.CanPin) return false;
        if (Filing(in row, source)) return false;
        return source.CanCopyTracks && !row.IsPlaylist;
    }

    /// <summary>REFUSED = you aimed here, and here is why (the sentence), or null when there is nothing to say.</summary>
    public static string? WhyRefused(in NavDropRow row, DragPayload source, bool rootlistLoaded)
    {
        if (PinRefusalKey(in row, source) is { } pinKey) return Loc.Get(pinKey);
        var refusal = PayloadRefusal(in row, source, rootlistLoaded);
        if (refusal != SidebarDropRefusal.None) return Sentence(refusal);
        if (row.IsPlaylist && !row.CanDeposit && source.CanCopyTracks) return Loc.Get("drag.cantEditPlaylist");
        // An artist has no single obvious track set: refuse rather than guess.
        return row.CanDeposit && !source.CanCopyTracks
            ? Loc.Get(source.Kind == DragKind.Artist ? "drag.cantAddArtist" : "drag.nothingToAdd")
            : null;
    }

    /// <summary>Which arm commits this drop. <paramref name="ownReorder"/>: the payload is this band's own reorder (hoisted:
    /// below the deposit arm it once copied a playlist's songs into it on the way past, D9). <paramref name="rootPlanIndex"/>
    /// is the row's plan index when it is a rootlist row.</summary>
    public static NavDropArm CommitArm(in NavDropRow row, DragPayload source, bool ownReorder, int rootPlanIndex)
    {
        if (row.PinSlot >= 0 && ownReorder) return NavDropArm.OwnReorder;
        if (Filing(in row, source) && rootPlanIndex >= 0) return NavDropArm.Rootlist;
        if (row.CanDeposit && source.CanCopyTracks) return NavDropArm.Deposit;
        return row.PinSlot >= 0 ? NavDropArm.Pin : NavDropArm.None;
    }

    /// <summary>The rootlist arm owns this drop. A cue published by a NEIGHBOUR (or one that is not armed) is a refusal with a
    /// logged reason, never a fallback placement — guessing is what made "after the last child" land inside the folder.
    /// <see cref="SidebarDropRefusal.None"/> means the cue is this row's and armed: commit it.</summary>
    public static SidebarDropRefusal RootlistCueRefusal(in SidebarDropSlot cue, int rootPlanIndex)
    {
        if (cue.PlanIndex == rootPlanIndex && cue.IsArmed) return SidebarDropRefusal.None;
        return cue.PlanIndex == rootPlanIndex && cue.Refusal != SidebarDropRefusal.None ? cue.Refusal : SidebarDropRefusal.Unavailable;
    }

    // ── the compact folder flyout's folder row: Into, and only Into ───────────────────────────────────────────────────

    /// <summary>The rail-folder drop's gate, before the marker stream is asked (<paramref name="row"/> is the folder row's own payload).</summary>
    public static NavRailFolderGate RailFolderGate(DragPayload source, DragPayload row, bool rootlistLoaded)
    {
        if (!IsFiling(source)) return NavRailFolderGate.Transparent;
        if (!rootlistLoaded) return NavRailFolderGate.NotLoaded;
        return Drag.IsSource(source, row.Id, row.Uri) ? NavRailFolderGate.IntoItself : NavRailFolderGate.Open;
    }

    // ── tree row facts ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The rootlist rows: the Playlists section, and Your Library while it is the Playlists · Custom order list — the
    /// rootlist itself (§P3.12). A sorted Library is a view of it, so its rows only drop INTO folders.</summary>
    public static bool IsRootlistSection(SidebarSectionKind kind, bool treeSortedNonCustom)
        => kind == SidebarSectionKind.Playlists || (kind == SidebarSectionKind.Library && !treeSortedNonCustom);

    /// <summary>The row's STRUCTURAL drop facts, straight off the published plan. <c>NextVisibleDepth</c> is the whole
    /// depth-ambiguity story (a slot is ambiguous exactly when it is shallower than this row). The payload-dependent half is
    /// folded in at hover (<see cref="FoldFacts"/>). <paramref name="folderExpanded"/> is the folder's expansion (ignored for a
    /// non-folder).</summary>
    public static SidebarRowFacts TreeRowFacts(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries, int index,
                                               in SidebarLibraryEntry entry, bool folderExpanded, bool sortedNonCustom)
    {
        int nextDepth = 0;
        bool hasChild = false;
        if ((uint)index < (uint)rows.Count && index + 1 < rows.Count)
        {
            var next = rows[index + 1];
            if (next.Kind is SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader
                && string.Equals(next.SectionId, rows[index].SectionId, StringComparison.Ordinal)
                && (uint)next.EntryIndex < (uint)entries.Count)
            {
                nextDepth = entries[next.EntryIndex].Depth;
                hasChild = nextDepth > entry.Depth;
            }
        }
        return new SidebarRowFacts(
            IsFolder: entry.IsFolder,
            FolderExpanded: entry.IsFolder && folderExpanded,
            FolderHasChildren: hasChild,
            Depth: entry.Depth,
            NextVisibleDepth: nextDepth,
            CenterAccepts: entry.IsFolder,
            SourceIsSelf: false,
            SortedNonCustom: sortedNonCustom,
            RootlistLoaded: true);
    }

    /// <summary>The tree's closing gutter: ONE slot, "top level, at the end" (the chip says "Move to end").</summary>
    public static SidebarRowFacts TreeEndFacts(bool sortedNonCustom)
        => new SidebarRowFacts(
            IsFolder: false, FolderExpanded: false, FolderHasChildren: false,
            Depth: 0, NextVisibleDepth: 0, CenterAccepts: false, SourceIsSelf: false,
            SortedNonCustom: sortedNonCustom, RootlistLoaded: true) { IsListEnd = true };
}
