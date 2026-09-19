using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee;

// THE ONE ANSWER TO "WHERE DOES THIS DROP LAND, AND MAY IT?" — the whole of it, in one pure place.
//
// WHAT WENT WRONG. The sidebar used to decide those two questions in EIGHT places over THREE different tree models: a
// flattened `SidebarLibraryEntry` list with no end-group markers (`RootlistTreeMoves`, deleted), the expansion-filtered
// VISIBLE plan (`SidebarPane.TryMapSlot`/`TryFolderEntry`/`SourceContainsRow`, rewritten to run here over the FULL
// tree), and the real rootlist marker stream (`RootlistOps`, three layers below the pointer). They disagreed, and every
// user-visible failure followed from that: a legal "into this folder" refused as "Already there" (the flattened model
// maps Inside to the folder's END, which reads as a no-op), a caret that armed a slot nothing could map, an outdent
// that filed the item INTO the folder it was leaving, and a success toast for a move that never happened.
//
// THE MODEL, in two steps and no more:
//   1. MAP — the geometry cue (`SidebarDropSlot`: kind + depth) plus the FULL projection tree (collapsed subtrees
//      included) become ONE `RootlistSlotTarget`: the entry the move is expressed against, its seam ref, the placement,
//      and the name of the folder the item will END UP IN (the toast's destination — never the hovered row's parent).
//      A cue that cannot be mapped is never armed; it publishes `Unavailable`.
//   2. CHECK — legality is `RootlistOps.CheckMove` over the STORE'S OWN marker stream (`store.Rootlist()`, kind-2
//      end markers and all), the same index math `TryBuildMove` uses to build the op. There is no second rule, no
//      second tree, and no UI-side cycle/no-op guard anywhere.
//
// ENGINE-FREE (System + Wavee.Core + the pure Backend rootlist rules), like the rest of `Data/`: `Wavee.Tests`
// source-includes both, so `RootlistDropScenarioTests` drives the REAL decision the pane publishes and commits.

/// <summary>A resolved rootlist destination: the tree entry it is expressed against, the seam ref + placement the
/// mutation rides, and the folder the item will END UP IN.
/// <para><see cref="DestinationName"/> is the confirmation toast's subject: it is computed from the MAPPED TARGET (for
/// an outdent — <c>(ancestorFolder, After)</c> — that is the ancestor's PARENT, so a move to the top level says "Your
/// Library"), never from whichever row the pointer happened to be over. Empty = the top level.</para>
/// <para><see cref="Deposit"/> marks the one slot that is not a rootlist move at all — the retained "drop a playlist on
/// an editable playlist's centre = copy its songs" gesture.</para></summary>
/// <param name="AnchorName">The name of the entry the placement is expressed AGAINST — for an outdent, the folder the
/// item is leaving. The drag chip's "Move out of {name}" reads this; the toast reads
/// <paramref name="DestinationName"/>. They are different questions and used to be answered by the same (wrong) field.</param>
readonly record struct RootlistSlotTarget(string EntryId, RootlistItemRef Ref, RootlistDropPlacement Placement,
                                          string DestinationName, string AnchorName, bool Deposit);

/// <summary>Cue + tree → destination. GEOMETRY IS NOT LEGALITY: nothing here refuses a move, and nothing here reads the
/// visible plan.</summary>
static class RootlistSlotMapper
{
    /// <summary>Map one armed cue onto the rootlist destination it means.
    /// <para><paramref name="rowEntryId"/> is the entry behind the hovered plan row ("" for the synthetic
    /// <c>TreeEnd</c> row, which stands for no entity). <paramref name="tree"/> must be the FULL depth-first flattened
    /// projection tree (<c>SidebarProjectionInput.PlaylistTree</c>) — collapsed subtrees included. Reading the VISIBLE
    /// plan here is what made "first child" and the outdent ancestor walk fail whenever the row they needed was inside
    /// a collapsed folder, and a failed map used to arm a cue that then dropped nothing (F2).</para>
    /// <para>False = there is no destination. The caller must publish <c>Unavailable</c>, never an armed slot.</para></summary>
    public static bool TryMap(in SidebarDropSlot cue, string rowEntryId,
                              IReadOnlyList<SidebarLibraryEntry>? tree, out RootlistSlotTarget target)
    {
        target = default;
        if (tree is null || tree.Count == 0) return false;

        // The tree's END marker is CHROME: it stands for no entity, so it resolves before the row lookup. Its anchor is
        // the last TOP-LEVEL entry, whose exclusive range end lands after a trailing folder's whole subtree — which is
        // exactly what makes "below everything, at the root level" reachable at all.
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
                // A PLAYLIST's centre takes the payload's TRACKS — not a rootlist move at all. A FOLDER's centre takes
                // the item as a child.
                target = entry.IsFolder
                    ? new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                             RootlistDropPlacement.Inside, entry.Name, entry.Name, false)
                    : new RootlistSlotTarget(entry.Id, default, RootlistDropPlacement.Inside, entry.Name, entry.Name, true);
                return true;

            case SidebarDropKind.Before when cue.Depth > entry.Depth && entry.IsFolder:
                // The bottom band of an EXPANDED folder header: the precise "first child" slot, resolved from the FULL
                // tree so a folder whose first child is itself a collapsed folder still maps. An EMPTY folder has no
                // child to land before, and "inside it" is the same place — so that is what it maps to, rather than
                // arming a cue with no destination.
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
                // THE OUTDENT. "After the last child of a folder", aimed left, means AFTER THE FOLDER — the same shape
                // `FolderActions.MoveOut` builds. Expressing it against the CHILD (which is what the shifted depth pick
                // forced) lands it straight back inside.
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

/// <summary>THE published-slot decision: map, then check against the marker stream, then refuse or arm. Every rootlist
/// drop surface — the tree row, the <c>TreeEnd</c> gutter, the rail's folder tile, the rail folder flyout — publishes
/// what this returns and commits the target it hands back, so a cue and its mutation cannot describe different
/// destinations.</summary>
static class RootlistDropDecision
{
    /// <summary>The ONE <see cref="RootlistMoveCheck"/> → <see cref="SidebarDropRefusal"/> table. It exists once
    /// because every one of these refusals used to be a silent <c>false</c> three layers below the pointer, and the
    /// second copy of the table was the thing that answered "Already there" to a perfectly legal move.</summary>
    public static SidebarDropRefusal RefusalFor(RootlistMoveCheck check) => check switch
    {
        RootlistMoveCheck.Ok => SidebarDropRefusal.None,
        RootlistMoveCheck.NoOp => SidebarDropRefusal.NoOp,
        RootlistMoveCheck.Cycle => SidebarDropRefusal.IntoDescendant,
        RootlistMoveCheck.SameItem => SidebarDropRefusal.Self,
        // Missing (the source or the target is not in the stream) and Invalid (a placement the stream cannot express)
        // are both "this position has no meaning" — the honest refusal, never a guessed placement.
        _ => SidebarDropRefusal.Unavailable,
    };

    /// <summary>Refine a GEOMETRY cue into the slot the surface publishes, and hand back the destination it commits.
    ///
    /// <para>An unarmed cue passes through untouched (it already carries the resolver's own refusal). An armed one is
    /// mapped; a cue with no mapping is DISARMED with <see cref="SidebarDropRefusal.Unavailable"/> — publishing an
    /// armed slot nothing could map is what made a drop silently do nothing. A mapped ordering is then checked against
    /// <paramref name="markers"/>, the store's live rootlist stream, and refused with the one table above.</para></summary>
    /// <param name="sources">The dragged items as the rootlist addresses them (a folder by group id, a playlist by uri),
    /// in TREE ORDER. A single-item drag is a list of one — there is no second decision path for a batch.
    /// <para>A source that IS the hovered target does NOT refuse the batch: <c>RootlistOps.TryBuildMoves</c> drops that
    /// self-pair as a legal GATHER (the rest of the selection closes up around the member you aimed at). Hovering one of
    /// your OWN rows still says <see cref="SidebarDropRefusal.Self"/> — that comes from the resolver's
    /// <c>SourceIsSelf</c> fact (fed by <c>WaveeResourceDrop.IsSource</c>), which refuses the cue BEFORE it reaches
    /// here, and which is the only place that can tell "into myself" from "before myself".</para></param>
    /// <param name="markers">The STORE's marker stream (<c>IStore.Rootlist()</c>). Null/empty = nothing to decide
    /// against, and every ordering refuses <see cref="SidebarDropRefusal.Unavailable"/> rather than being armed against
    /// indices nobody has.</param>
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

    /// <summary>The legality question, asked of the ONE authority. Every caller — hover, commit, the rail tile's accept
    /// predicate, the folder picker, the bridge — goes through this.
    ///
    /// <para>The batch it asks about is <see cref="RootlistBatchOrder.For"/>'s — the SAME ordered move list the commit
    /// issues — so the cue cannot be answering a different question from the write. Allocating a list per hover is fine:
    /// this runs at pointer-move on N ≤ the selection size, outside the frame's 0-alloc region.</para></summary>
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
