using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// THE ONE SIDEBAR DROP-SLOT RESOLVER.
//
// WHY IT EXISTS. Every rootlist drop used to be a whole-row `Drop.Target` whose MEANING was a hidden vertical hit test
// (`SidebarPane.RootlistPlacementFor`, deleted): 25/50/25 or 50/50 depending on the payload, three different outcomes
// behind ONE pixel-identical accent plate, no depth, no captions for before/after, and the cycle/no-op guards buried
// three layers down in `RootlistOps` where they could only fail SILENTLY. The user's verdict was "I cannot drag/drop
// reorder playlists properly; it's counter-intuitive", and every one of those was a cause.
//
// THE MODEL. ONE pure function turns (row facts, t, xInRow) into a `SidebarDropSlot` = (kind, depth, refusal). ONE
// signal publishes it. Rows render a LINE (Before/After/EndOfList, indented to the resolved depth) or a PLATE (Into) —
// never both, never neither-but-armed. Every drop CONSUMES the published slot instead of recomputing it, so the cue and
// the mutation cannot disagree. The `Refusal` arm is what lifts "folder into its own descendant" and "already there"
// from a silent `return false` up to the sentence the drag chip shows.
//
// ENGINE-FREE BY CONSTRUCTION (System + Wavee.Core only), exactly like its `Data/` siblings: `Wavee.Tests` source-
// includes this folder, so `RootlistSlotResolverTests` / `RootlistRefusalTests` drive the REAL rules rather than a copy.
// Nothing here may reference Signal<T>, Element, Icons, Loc or Tok.

/// <summary>What a rootlist drop at the resolved position MEANS. <see cref="None"/> is "nothing is armed here" — it is
/// also what a REFUSED slot reports, so a refused row draws neither a line nor a plate (pinned by
/// <c>SidebarDropCueTests</c>).</summary>
public enum SidebarDropKind : byte
{
    None = 0,
    /// <summary>An insertion line ABOVE the row, at <see cref="SidebarDropSlot.Depth"/>.</summary>
    Before = 1,
    /// <summary>An insertion line BELOW the row, at <see cref="SidebarDropSlot.Depth"/> (which may be SHALLOWER than the
    /// row's own depth — that is the "move out of this folder" gesture).</summary>
    After = 2,
    /// <summary>Into the row: a folder takes the item as a child, an editable playlist takes the payload's TRACKS. The
    /// plate — and the ONLY kind that draws one.</summary>
    Into = 3,
    /// <summary>The end of the whole tree, at depth 0 — the <c>TreeEnd</c> chrome row's only slot.</summary>
    EndOfList = 4,
}

/// <summary>Why a drop at this position is refused, in the order the table evaluates. Each value maps to exactly one
/// caption (<c>SidebarPane.WhyRefused</c>), because a refusing drop target is transparent to the engine and the caption
/// is the ONLY thing that reaches the user.</summary>
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
    /// <summary>The geometry is degenerate (no plan row, no viewport, no scene). Refuse with a reason rather than
    /// guessing a placement, which is what the old resolver did.</summary>
    Unavailable = 7,
}

/// <summary>Everything the resolver needs about ONE row, and nothing about the renderer. The one payload-dependent
/// member (<see cref="SourceIsSelf"/>) is computed at HOVER, because that is the first moment the payload exists; the
/// structural ones come from the plan.</summary>
/// <param name="NextVisibleDepth">Depth of the next VISIBLE tree row; 0 when this is the last tree row. Together with
/// <paramref name="Depth"/> it is the whole depth-ambiguity story: a slot is ambiguous iff the two differ downward.</param>
/// <param name="CenterAccepts">This row has a dead centre that DEPOSITS: a folder (always), or an editable playlist
/// whose centre takes the payload's tracks (the retained copy gesture).</param>
/// <param name="SourceIsSelf">This row IS the dragged item. The one payload fact the resolver still reads, because
/// "into MYSELF" and "before myself" need two different sentences and the marker-stream check
/// (<c>RootlistOps.CheckMove</c>) collapses both into <c>SameItem</c>. Every OTHER legality question — the cycle, the
/// no-op — is the marker stream's, never re-derived here (F1/F3).</param>
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

    /// <summary>Does this slot draw the INSERTION LINE? Exactly the three ordering kinds.
    /// <para>DELEGATES to <see cref="SidebarDropCue.DrawsLine"/> — the row binds that one, this is the same question
    /// asked of a whole slot, and two hand-written copies of "which kinds draw a line" is exactly the drift the one-rule
    /// invariant exists to prevent.</para></summary>
    public bool DrawsLine => SidebarDropCue.DrawsLine(Kind);

    /// <summary>Does this slot draw the accent PLATE? Exactly <see cref="SidebarDropKind.Into"/>. Delegates for the same
    /// reason as <see cref="DrawsLine"/>.</summary>
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
    /// <summary>Hysteresis around each depth boundary (DIP). Without it the line flickers between two depths while the
    /// hand holds still, which is the classic tree-drag jitter.</summary>
    public const float DepthHysteresis = 4f;

    /// <summary>The edge-band height for a row: <c>clamp(0.30·h, 10, 16)</c>. ONE band for every payload and every
    /// ownership state — the old geometry changed between 11 and 22 DIP depending on what you were dragging, so the
    /// same pointer position meant different things on two adjacent rows.</summary>
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
    /// <para><c>Max</c> is the row's own depth — "stay where you are". <c>Min</c> is the depth of the next visible row,
    /// clamped so it can never exceed Max (a folder header's next row is its own child, one level DEEPER, and there is
    /// no slot below a header that lands outside the header). <c>Min &lt; Max</c> is exactly "after the last visible
    /// child of a (possibly nested) folder" — the one place the tree is genuinely ambiguous, and the gesture D2 got
    /// silently wrong.</para></summary>
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
    /// <param name="xInRow">Pointer X relative to the row's own left edge — the DEPTH channel, which the old resolver
    /// never read at all.</param>
    /// <param name="rowHeight">The row's measured extent.</param>
    /// <param name="previous">The slot published on the previous move, for depth hysteresis. Pass
    /// <see cref="SidebarDropSlot.None"/> when there is none.</param>
    public static SidebarDropSlot Resolve(int planIndex, float t, float xInRow, float rowHeight,
                                          in SidebarRowFacts f, in SidebarDropSlot previous)
    {
        if (planIndex < 0 || !float.IsFinite(t) || !float.IsFinite(rowHeight) || rowHeight <= 0f)
            return new SidebarDropSlot(planIndex, SidebarDropKind.None, 0, SidebarDropRefusal.Unavailable);

        // ── the zone, first: two of the refusals below (IntoItself, SortedList) are about WHICH zone you are in ──────
        var (kind, depth) = Zone(t, xInRow, rowHeight, in f, in previous);

        // ── the refusal table (design §Refusal table), in its stated order ────────────────────────────────────────────
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
        // A non-custom SORT cannot show a positional insert, so an ORDERING is refused with the fix ("clear sorting");
        // a DEPOSIT into a folder or a playlist is unaffected by how the list happens to be sorted and stays legal.
        if (f.SortedNonCustom && kind is SidebarDropKind.Before or SidebarDropKind.After or SidebarDropKind.EndOfList)
            return SidebarDropRefusal.SortedList;
        return SidebarDropRefusal.None;
    }

    static (SidebarDropKind Kind, int Depth) Zone(float t, float xInRow, float rowHeight,
                                                  in SidebarRowFacts f, in SidebarDropSlot previous)
    {
        // The tree's END: one whole-row slot at the root level. No bands — there is nothing below it to be "after".
        if (f.IsListEnd) return (SidebarDropKind.EndOfList, 0);

        int depth = f.Depth < 0 ? 0 : f.Depth;
        float edge = EdgeFor(rowHeight);
        float top = edge / rowHeight;
        float bottom = 1f - top;

        if (f.IsFolder)
        {
            // Dropping ON a folder means INTO it — Explorer, Finder, VS Code and Spotify all agree, and the old build's
            // "the header appends last, and only the header" is why "into a folder" had exactly one anchor (D7).
            if (t < top) return (SidebarDropKind.Before, depth);
            if (t > bottom)
                // The bottom band of an EXPANDED header is the precise "first child" slot: the line indents one step and
                // the drop lands ahead of the folder's current first child.
                return f.FolderExpanded && f.FolderHasChildren
                    ? (SidebarDropKind.Before, depth + 1)
                    : (SidebarDropKind.After, PickDepth(xInRow, in f, in previous));
            return (SidebarDropKind.Into, depth);
        }

        if (f.CenterAccepts)
        {
            // The retained copy gesture: an editable playlist's centre deposits the payload's tracks. It survives only
            // because the PLATE now distinguishes it from the two edge bands, which draw a line (D4).
            if (t < top) return (SidebarDropKind.Before, depth);
            if (t > bottom) return (SidebarDropKind.After, PickDepth(xInRow, in f, in previous));
            return (SidebarDropKind.Into, depth);
        }

        // Everything else: two zones, no dead centre. A row that cannot take a deposit must not reserve half its height
        // for one.
        return t < 0.5f
            ? (SidebarDropKind.Before, depth)
            : (SidebarDropKind.After, PickDepth(xInRow, in f, in previous));
    }

    /// <summary>The DEPTH channel: only an <see cref="SidebarDropKind.After"/> slot spanning more than one depth reads
    /// the pointer's X at all. The default — the pointer over the row's LABEL, x well past the indent ladder — is
    /// <c>Max</c>, i.e. "stay at this row's depth"; travelling LEFT outdents one step per indent level.</summary>
    static int PickDepth(float xInRow, in SidebarRowFacts f, in SidebarDropSlot previous)
    {
        var (min, max) = DepthRange(in f);
        if (min >= max) return max;
        if (!float.IsFinite(xInRow)) return max;

        // THE LADDER IS THE ROW'S OWN (F2/F3). `TreeContentX(d)` is where a tree row at depth d actually starts drawing
        // — gutter + connector cells + the fixed disclosure cell — so travelling one cell left is exactly one outdent.
        // Reading `IndentFor` here instead put every boundary ~19 DIP too far left, which made depth 0 unreachable and
        // turned "move out of this folder" into "move INTO it".
        float steps = (xInRow - SidebarRowGeometry.TreeContentX(0)) / SidebarRowGeometry.TreeGuideStep;
        int picked = (int)MathF.Round(steps);
        if (picked < min) picked = min;
        if (picked > max) picked = max;

        // HYSTERESIS. Without it the boundary between two depths sits under a still hand and the line flickers. The
        // previous depth is held until the pointer is a clear 4 DIP past the boundary that would leave it.
        if (previous.Kind == SidebarDropKind.After && previous.Depth >= min && previous.Depth <= max
            && previous.Depth != picked)
        {
            float boundary = SidebarRowGeometry.TreeContentX(0)
                + (previous.Depth + (picked > previous.Depth ? 0.5f : -0.5f)) * SidebarRowGeometry.TreeGuideStep;
            float travelled = MathF.Abs(xInRow - boundary);
            if (travelled < DepthHysteresis) return previous.Depth;
        }
        return picked;
    }
}

/// <summary>The insertion cue's PURE geometry + its one invariant, in the engine-free layer so a test can drive the
/// real mapping rather than a copy of it.
///
/// <para><b>The invariant</b> (<c>SidebarDropCueTests</c>): <b>line ⟺ Before/After/EndOfList, plate ⟺ Into, never
/// both, never neither-while-armed.</b> That is the entire fix for D1 — three outcomes used to share one accent plate,
/// so the surface could not say which of them a drop meant.</para></summary>
public static class SidebarDropCue
{
    /// <summary>The caret's stroke (2 DIP).</summary>
    public const float LineThickness = 2f;
    /// <summary>Its corner radius — enough to round a 2-DIP bar's ends without reading as a pill.</summary>
    public const float LineCorner = 1f;
    /// <summary>The terminal dot at the left cap (6 DIP): what makes a hairline read as an insertion caret rather than
    /// as a divider.</summary>
    public const float DotSize = 6f;

    /// <summary>Does this slot draw the line? (The one predicate the row's Opacity bind reads.)</summary>
    public static bool DrawsLine(SidebarDropKind kind)
        => kind is SidebarDropKind.Before or SidebarDropKind.After or SidebarDropKind.EndOfList;

    /// <summary>Does this slot draw the accent plate? (The one predicate the row's Fill/Border binds read.)</summary>
    public static bool DrawsPlate(SidebarDropKind kind) => kind == SidebarDropKind.Into;

    /// <summary>The caret's width: the row's content lane from <see cref="SidebarRowGeometry.TreeContentX"/> — the x the
    /// row itself starts drawing at for that depth — to the row's trailing inset. ONE origin with the caret's transform
    /// and with <c>PickDepth</c>.</summary>
    public static float LineWidth(float contentWidth, int depth)
    {
        float w = contentWidth - SidebarRowGeometry.TreeContentX(depth) - SidebarRowGeometry.RowInsetRight;
        return w > 0f ? w : 0f;
    }

    /// <summary>The caret's Y inside its row: the TOP edge for Before (and for the tree's end marker, whose whole row is
    /// the slot), the bottom edge for After.</summary>
    public static float LineY(SidebarDropKind kind, float rowHeight)
        => kind is SidebarDropKind.Before or SidebarDropKind.EndOfList
            ? 0f
            : MathF.Max(0f, rowHeight - LineThickness);
}

/// <summary>Where a rootlist item CAME FROM, so a completed move can be offered back as Undo.
///
/// <para>Pure, over the SAME depth-first flattened tree the planner consumes (<c>SidebarProjectionInput.PlaylistTree</c>
/// — the full tree, not the expansion-filtered plan), so the anchor is the item's real pre-move sibling and not
/// whichever row happened to be visible. Expressed as an ordinary <c>(RootlistItemRef, RootlistDropPlacement)</c> pair,
/// which means the inverse rides the very same <c>MoveRootlistItemsAsync</c> seam the forward move did — no second
/// mutation path, and nothing to keep in sync.</para></summary>
public static class RootlistUndoAnchors
{
    /// <summary>Resolve the move that would put <paramref name="entryId"/> back exactly where it is now. The N=1 sugar
    /// over <see cref="TryResolveMany"/> — one item is a selection of one, and there is no second anchor rule.
    /// <para>False when there is nothing to anchor against (the item is the tree's only member, or it is not in the
    /// tree at all) — the caller then shows its confirmation toast WITHOUT an Undo action rather than offering one that
    /// would land somewhere else.</para></summary>
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

    /// <summary>Resolve the BATCH that would put a whole multi-selection back exactly where it is now, computed BEFORE
    /// the move (once the rootlist has shifted, where the items used to be is unknowable).
    ///
    /// <para>Each item anchors on its previous <b>UNSELECTED</b> sibling (After) → its next unselected sibling (Before)
    /// → its parent folder (Inside). Unselected siblings are the only rows guaranteed not to have moved, so every
    /// anchor still means the same place in the POST-move stream. Anchoring on a selected neighbour would chain the
    /// undo onto a row that is itself in flight.</para>
    ///
    /// <para>The emitted ORDER is <see cref="RootlistBatchOrder.For"/>'s, applied per run of items that share an anchor
    /// — a contiguous selected run collapses onto ONE unselected neighbour, and issuing those After-anchored moves
    /// forward would reverse the run. That is the same rule the forward batch obeys, reached through the same function
    /// rather than restated here.</para>
    ///
    /// <para>False if ANY item has no resolvable anchor (a selection containing the tree's only top-level item): a
    /// partial Undo would scatter the rest, so the toast simply carries no Undo — the existing single-item rule.</para></summary>
    public static bool TryResolveMany(IReadOnlyList<SidebarLibraryEntry>? tree, IReadOnlyList<string> ids,
                                      out IReadOnlyList<RootlistMove> undo)
    {
        undo = Array.Empty<RootlistMove>();
        if (tree is null || ids is null || ids.Count == 0) return false;

        // The selection as the MOVE sees it: tree order, descendants of a selected folder dropped. Anchoring a row that
        // is riding inside a selected folder would undo it against an index the parent's own op has already moved.
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

    // The rootlist reference one entry moves AS is ONE rule, and it lives in `RootlistTreeNav.RefOf`. This file used to
    // carry a byte-identical copy of it.
    static RootlistItemRef RefOf(in SidebarLibraryEntry entry) => RootlistTreeNav.RefOf(in entry);
}
