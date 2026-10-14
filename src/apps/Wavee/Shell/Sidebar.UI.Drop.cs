// ── Shell/Sidebar.UI.Drop.cs ───────────────────────────────────────────────────────────────────────────────────────
// NAMED PARTIAL of Sidebar.UI.cs (J1): every drop target the pane declares, the rootlist slot resolve → publish →
// commit, the refusal sentences, drop-to-pin / drop-to-create, and the rail's drag peek
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 600 lines (part of Sidebar.UI.cs's 7,500)
// Spec: ch 25 §0.8, W4, W16 (the five tree cues), §6 drag-and-drop table + refusal table, §9 "every no is two answers"
//
// ONE RESOLVER, ONE PUBLISHED SLOT, ONE COMMIT (dnd skill recipe 6). A row's hover resolves the pointer into a
// `SidebarDropSlot` (pure `RootlistSlotResolver`), refines it against the marker stream (`RootlistDropDecision`) and
// publishes it once; the row's line and plate BIND to that slot, and the drop COMMITS the slot it published — a cue and
// a mutation computed twice are a cue and a mutation that can disagree. Every "no" is one of two answers: TRANSPARENT
// (the drag is merely crossing — no cue at all) or REFUSED (you aimed here, and here is the sentence). A drop that
// cannot be honoured always says so; nothing on this path fails by returning.
//
// The MUTATIONS are the library seam's (`Sidebar.LibraryWrites`, Sidebar.Host.cs) — this file decides, it never writes
// the rootlist itself.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Sidebar
{
    internal sealed partial class PaneView
    {
        // Caption cache: a live drag re-runs Hover on every Over, so the (allocating) sentence is composed only when the
        // published slot or the payload changes — never per pointer move.
        SidebarDropSlot _captionSlot = SidebarDropSlot.None;
        DragPayload? _captionPayload;
        string? _caption;

        /// <summary>The spotlight scrim is suppressed for an intra-sidebar organisation drag: dimming the app you are
        /// reordering inside promises something false (rule 6 / D15).</summary>
        static bool SpotlightFor(DragSession s) => Drag.Unwrap(s.Payload) is not { RootlistItem: true };

        /// <summary>One resource destination that can be a rootlist filing slot, a playlist track deposit and a pinned-
        /// band insertion at once. <paramref name="playlistUri"/> is set only for an EDITABLE playlist;
        /// <paramref name="isPlaylistRow"/> says the row is a playlist at all — the refusal-versus-transparent split.
        /// <paramref name="rootFacts"/> carries the row's structural facts; the payload-dependent ones are folded in at
        /// hover, the first moment the payload exists.</summary>
        internal DropTargetSpec ResourceDropSpec(string sectionId, int slot, string? playlistUri, string? playlistName,
            DragPayload? rootTarget = null, int rootPlanIndex = -1, Action? onSpringLoad = null, string? railCueUri = null,
            bool isPlaylistRow = false, SidebarRowFacts rootFacts = default)
        {
            // The row's structural facts. Every decision below is NavDropRules': a pure table over (row, payload, loaded);
            // what is left here is the pointer geometry, the cue and the caption cache.
            var row = new NavDropRow(rootTarget, slot, playlistUri, isPlaylistRow, railCueUri is { Length: > 0 });

            bool Compatible(DragPayload source) => NavDropRules.Compatible(in row, source, RootlistLoaded);

            SidebarDropSlot SlotFor(DragPayload p, DragSession s)
            {
                if (!Compatible(p)) return SidebarDropSlot.None;
                if (NavDropRules.Filing(in row, p) && rootPlanIndex >= 0)
                    return RootlistSlotFor(rootPlanIndex, NavDropRules.FoldFacts(in row, in rootFacts, p, RootlistLoaded), p, s.Position);
                // A pin insertion, a track deposit, a rail tile: a whole-row INTO — the plate.
                return rootPlanIndex >= 0
                    ? new SidebarDropSlot(rootPlanIndex, SidebarDropKind.Into, 0, SidebarDropRefusal.None)
                    : SidebarDropSlot.None;
            }

            void Hover(DragPayload p, DragSession s)
            {
                var cue = SlotFor(p, s);
                _dropSlot.SetIfChanged(cue);
                if (railCueUri is { Length: > 0 }) _railDropUri.SetIfChanged(Compatible(p) ? railCueUri : null);
                if (!cue.Equals(_captionSlot) || !ReferenceEquals(p, _captionPayload))
                {
                    _captionSlot = cue;
                    _captionPayload = p;
                    _caption = CaptionFor(p, s, in cue);
                }
                s.Caption = _caption;
            }

            string? CaptionFor(DragPayload p, DragSession s, in SidebarDropSlot cue)
            {
                // A same-band reorder is this section's own gesture: its feedback is the displacement.
                if (s.Payload is ReorderPayload own && ReferenceEquals(own.Owner, ReorderFor(sectionId))) return null;
                if (cue.Refusal != SidebarDropRefusal.None) return NavDropRules.Sentence(cue.Refusal);
                if (rootTarget is { } root && NavDropRules.Filing(in row, p)) return RootlistCaption(in cue, root, p, playlistName);
                if (row.CanDeposit && p.CanCopyTracks) return Drag.AddTo(playlistName ?? "");
                if (slot >= 0 && p.CanPin) return Drag.Pin(p.Name);
                return null;
            }

            void Leave(DragSession _)
            {
                if (rootPlanIndex >= 0 && _dropSlot.Peek().PlanIndex == rootPlanIndex) _dropSlot.Value = SidebarDropSlot.None;
                if (railCueUri is { Length: > 0 } && string.Equals(_railDropUri.Peek(), railCueUri, StringComparison.Ordinal))
                    _railDropUri.Value = null;
            }

            void CommitDrop(DragPayload source, DragSession s)
            {
                var cue = _dropSlot.Peek();
                Leave(s);
                // HOISTED: a same-band reorder commits through its own Reorderable. Below the deposit arm it copied a
                // playlist's songs into it on the way past (D9).
                bool ownReorder = slot >= 0 && s.Payload is ReorderPayload rp && ReferenceEquals(rp.Owner, ReorderFor(sectionId));
                switch (NavDropRules.CommitArm(in row, source, ownReorder, rootPlanIndex))
                {
                    case NavDropArm.Rootlist:
                    {
                        // THE ROOTLIST ARM OWNS THIS DROP. A cue published by a NEIGHBOUR is a refusal with a logged reason,
                        // never a fallback placement — guessing is what made "after the last child" land inside the folder.
                        var refusal = NavDropRules.RootlistCueRefusal(in cue, rootPlanIndex);
                        if (refusal != SidebarDropRefusal.None)
                        {
                            RefuseDrop(refusal, "cue=" + cue.Kind + "/" + cue.PlanIndex + " row=" + rootPlanIndex);
                            return;
                        }
                        CommitRootlistSlot(source, in cue, playlistUri, playlistName);
                        return;
                    }
                    case NavDropArm.Deposit:
                        Model.Drop(source, NavDropTarget.Playlist(playlistUri!, playlistName ?? ""));
                        return;
                    case NavDropArm.Pin:
                        AcceptForeign(sectionId, s.Payload, slot);
                        return;
                }
            }

            // TRANSPARENT = none of my business (an album/artist/show/route row a track drag is merely crossing).
            bool Transparent(DragPayload source) => NavDropRules.Transparent(in row, source);

            // REFUSED = you aimed here, and here is why.
            string? WhyRefused(DragPayload source) => NavDropRules.WhyRefused(in row, source, RootlistLoaded);

            return Drop.Target<DragPayload>(Drag.Resource,
                accepts: Compatible, onDrop: CommitDrop, onEnter: Hover, onOver: Hover, onLeave: Leave,
                transparent: Transparent,
                visualPolicy: Kit.DropLook.TargetPolicy,
                refusalCaption: WhyRefused,
                spotlightWhen: SpotlightFor,
                // A COLLAPSED folder spring-loads open even when it refuses the payload: opening is navigation.
                springLoadMs: onSpringLoad is null ? 0f : Drag.SpringLoadMs,
                onSpringLoad: onSpringLoad is null ? null : (_, _) => onSpringLoad());
        }

        /// <summary>The compact folder flyout's FOLDER row: Into, and only Into — a flyout row files, it never reorders.</summary>
        internal DropTargetSpec RailFolderDropSpec(in SidebarLibraryEntry folder, DragPayload target)
        {
            string folderId = folder.FolderId;
            // The cue key is the ENTRY id — what both readers ask `IsRailDropActive` with (the rail's folder tile and the
            // flyout's folder row). It was the tile's "rail:"-prefixed NODE key, which neither reader used, so an armed
            // folder destination never lit.
            string cueKey = folder.Id;
            string name = folder.Name;
            // The destination. Accepting, refusing and moving are the model's (NavModel.Drop.cs).
            var dest = NavDropTarget.Folder(folderId, name, target);

            bool Accepts(DragPayload source) => Model.RailFolderAccepts(source, in dest);

            string? Why(DragPayload source) => Model.RailFolderWhy(source, in dest);

            void Commit(DragPayload source, DragSession s)
            {
                _railDropUri.Value = null;
                Model.Drop(source, in dest, Config.SortedListRefusalAction);
            }

            return Drop.Target<DragPayload>(Drag.Resource,
                accepts: Accepts,
                transparent: static p => !p.RootlistItem,
                caption: _ => Drag.MoveInto(name),
                refusalCaption: Why,
                onEnter: (p, _) => _railDropUri.SetIfChanged(Accepts(p) ? cueKey : null),
                onOver: (p, _) => _railDropUri.SetIfChanged(Accepts(p) ? cueKey : null),
                onLeave: _ => { if (string.Equals(_railDropUri.Peek(), cueKey, StringComparison.Ordinal)) _railDropUri.Value = null; },
                onDrop: Commit,
                visualPolicy: Kit.DropLook.TargetPolicy,
                spotlightWhen: SpotlightFor);
        }

        /// <summary>Pointer + row facts → the published slot: the viewport math that turns the pointer into a row-relative
        /// <c>t</c> and depth channel, the pure resolver, then <see cref="RootlistDropDecision.Refine"/> (an unmappable
        /// slot is never armed).</summary>
        SidebarDropSlot RootlistSlotFor(int planIndex, SidebarRowFacts facts, DragPayload source, Point2 pointer)
        {
            var viewport = _listController.Viewport;
            var scene = Context.Scene;
            if (planIndex < 0 || scene is null || viewport.IsNull || !scene.IsLive(viewport))
                return new SidebarDropSlot(planIndex, SidebarDropKind.None, 0, SidebarDropRefusal.Unavailable);

            var rect = scene.AbsoluteRect(viewport);
            float contentY = pointer.Y - rect.Y + _listController.ScrollOffset;
            float top = RowTopOf(planIndex);
            float extent = MathF.Max(1f, RowExtentOf(planIndex));
            float t = Math.Clamp((contentY - top) / extent, 0f, 1f);
            // The list is the padded box's only child, so the viewport's left edge IS the row's left edge.
            float xInRow = pointer.X - rect.X;
            var cue = RootlistSlotResolver.Resolve(planIndex, t, xInRow, extent, in facts, _dropSlot.Peek());
            TryDecide(in cue, source, out _, out var refined);
            return refined;
        }

        /// <summary>The content-space top of a plan row — a closure-free prefix sum over the live extents (this runs on
        /// every Over while a drag is live, so it must not allocate).</summary>
        float RowTopOf(int planIndex)
        {
            int stop = Math.Min(planIndex, Plan.Rows.Count);
            float y = 0f;
            for (int i = 0; i < stop; i++)
            {
                float e = RowExtentOf(i);
                if (float.IsFinite(e) && e > 0f) y += e;
            }
            return y;
        }

        /// <summary>THE cue → (published slot, destination) decision. Deterministic over (cue, tree, marker stream), so the
        /// hover and the commit cannot describe different destinations — and re-running it at drop time is what makes a
        /// rootlist that moved mid-gesture refuse rather than land against moved indices.</summary>
        bool TryDecide(in SidebarDropSlot cue, DragPayload source, out RootlistSlotTarget target, out SidebarDropSlot refined)
        {
            string rowId = cue.Kind == SidebarDropKind.EndOfList ? ""
                         : TryRowEntry(cue.PlanIndex, out var entry) ? entry.Id
                         : "";
            refined = RootlistDropDecision.Refine(in cue, rowId, RootlistTree, RootlistMarkers, NavDropRules.RefsOf(source), out target);
            return refined.IsArmed;
        }

        /// <summary>Commit the PUBLISHED slot, re-decided; a slot that no longer resolves logs and shows the refusal the
        /// user would have seen one frame earlier.</summary>
        void CommitRootlistSlot(DragPayload source, in SidebarDropSlot cue, string? playlistUri, string? playlistName)
        {
            if (!TryDecide(in cue, source, out var target, out var refined))
            {
                RefuseDrop(refined.Refusal == SidebarDropRefusal.None ? SidebarDropRefusal.Unavailable : refined.Refusal,
                           "cue=" + cue.Kind + "@" + cue.Depth + "/" + cue.PlanIndex);
                return;
            }
            if (target.Deposit)
            {
                // The retained centre gesture: a WRITABLE playlist under the pointer takes the payload's tracks.
                if (playlistUri is not { Length: > 0 } uri)
                    RefuseDrop(SidebarDropRefusal.Unavailable, "deposit without a writable playlist");
                else
                    Model.DepositTracks(uri, playlistName ?? "", source);
                return;
            }
            // This gesture's own re-projection must not be parked by the freeze.
            _publishThroughFreeze = true;
            // The model captures the undo anchors BEFORE the mutation, EXACTLY ONE mutation per drop.
            Model.MoveRootlist(source, target.Ref, target.Placement, target.DestinationName, Config.SortedListRefusalAction);
        }

        /// <summary>A drop that cannot be honoured says so (the model's <see cref="NavModel.RefuseDrop"/>), offering the mode's one
        /// fix for "clear sorting to reorder" (#85 H3).</summary>
        void RefuseDrop(SidebarDropRefusal refusal, string why)
            => NavModel.RefuseDrop(refusal, why, Config.SortedListRefusalAction);

        /// <summary>What an ARMED rootlist slot says. Before/After at the row's own depth say nothing (the line is under
        /// the pointer); only the outdent and the end of the list carry a sentence, and the outdent names the folder the
        /// item is LEAVING from the mapped anchor.</summary>
        string? RootlistCaption(in SidebarDropSlot cue, DragPayload root, DragPayload source, string? playlistName)
        {
            switch (cue.Kind)
            {
                case SidebarDropKind.Into:
                    return root.Kind == DragKind.Folder ? Drag.MoveInto(root.Name) : Drag.AddTo(playlistName ?? root.Name);
                case SidebarDropKind.EndOfList:
                    return Loc.Get("drag.moveToEnd");
                case SidebarDropKind.After when TryRowEntry(cue.PlanIndex, out var entry) && cue.Depth < entry.Depth:
                    string anchor = TryDecide(in cue, source, out var target, out _) && target.AnchorName.Length > 0
                        ? target.AnchorName
                        : entry.ParentFolderName.Length > 0 ? entry.ParentFolderName : Loc.Get("sidebar.yourLibrary");
                    return Loc.Format("drag.moveOutOf", ("name", anchor));
                default:
                    return null;
            }
        }

        bool TryRowEntry(int planIndex, out SidebarLibraryEntry entry)
        {
            entry = default;
            var rows = Plan.Rows;
            var entries = Plan.Entries;
            if ((uint)planIndex >= (uint)rows.Count) return false;
            int at = rows[planIndex].EntryIndex;
            if ((uint)at >= (uint)entries.Count) return false;
            entry = entries[at];
            return true;
        }

        // ── drag peek + the two create destinations ────────────────────────────────────────────────────────────────

        /// <summary>The rail's spring-load band: a pure WAYPOINT (accepts nothing), armed only for a payload that could
        /// actually land somewhere in the sidebar — motion that promises a destination it lacks is the bug class. Built
        /// ONCE (W3-A2): its delegates read only <c>this</c>, so the pane's render hands the compact layer the same spec
        /// every time instead of a fresh spec + two closures per render.</summary>
        DropTargetSpec RailPeekDropSpec() => _railPeekDrop ??= Drop.Target<DragPayload>(Drag.Resource,
            accepts: static _ => false,
            springLoadOnly: true,
            springLoadMs: DragPeekMs,
            onSpringLoad: (p, _) => { if (p.CanCopyTracks || p.CanPin) SetDragPeek(true); });

        DropTargetSpec? _railPeekDrop;

        /// <summary>THE HEADER "+" AS A DROP DESTINATION: a rootlist payload ⇒ a new top-level folder holding it; a track
        /// set that is not a rootlist item ⇒ a new playlist from it; anything else is transparent. Every answer is
        /// <see cref="SidebarCreateDropRules.Header"/>'s: a missing seam member REFUSES with its sentence instead of arming
        /// a cue for a drop that would do nothing (G-170).</summary>
        internal DropTargetSpec HeaderCreateDropSpec()
            => Drop.Target<DragPayload>(Drag.Resource,
                accepts: static p => SidebarCreateDropRules.Header(p, LibraryWrites)
                    is SidebarCreateDrop.NewFolder or SidebarCreateDrop.NewPlaylist,
                transparent: static p => SidebarCreateDropRules.Header(p, LibraryWrites) == SidebarCreateDrop.Transparent,
                caption: static p => SidebarCreateDropRules.IsFiling(p)
                    ? Loc.Format("drag.newFolderFromThis", ("count", p.RootlistCount))
                    : Loc.Get("drag.newPlaylistFromThis"),
                refusalCaption: static _ => NavDropRules.Sentence(SidebarDropRefusal.WritesUnavailable),
                onEnter: (p, _) => HeaderCreateDropActive.SetIfChanged(CreateArmed(SidebarCreateDropRules.Header(p, LibraryWrites))),
                onOver: (p, _) => HeaderCreateDropActive.SetIfChanged(CreateArmed(SidebarCreateDropRules.Header(p, LibraryWrites))),
                onLeave: _ => HeaderCreateDropActive.Value = false,
                onDrop: (p, _) =>
                {
                    HeaderCreateDropActive.Value = false;
                    var writes = LibraryWrites;
                    switch (SidebarCreateDropRules.Header(p, writes))
                    {
                        case SidebarCreateDrop.NewFolder: writes!.NewFolderWith!(null, NavDropRules.RefsOf(p)); break;
                        case SidebarCreateDrop.NewPlaylist: writes!.CreatePlaylistWith!(p); break;
                        case SidebarCreateDrop.Refused: NavModel.RefuseWrite("header create drop"); break;
                    }
                },
                visualPolicy: Kit.DropLook.TargetPolicy,
                spotlightWhen: SpotlightFor);

        /// <summary>Playlists dropped on a folder row's "+" ⇒ a new SUB-folder inside it. A track set is transparent: it
        /// is crossing on its way to a playlist row. <see cref="SidebarCreateDropRules.Folder"/> decides, seam included.</summary>
        internal DropTargetSpec FolderCreateDropSpec(string folderId, string folderName, Signal<bool> active)
            => Drop.Target<DragPayload>(Drag.Resource,
                accepts: static p => SidebarCreateDropRules.Folder(p, LibraryWrites) == SidebarCreateDrop.NewFolder,
                transparent: static p => SidebarCreateDropRules.Folder(p, LibraryWrites) == SidebarCreateDrop.Transparent,
                caption: _ => Loc.Format("drag.newFolderInside", ("name", folderName)),
                refusalCaption: static _ => NavDropRules.Sentence(SidebarDropRefusal.WritesUnavailable),
                onEnter: (p, _) => active.SetIfChanged(CreateArmed(SidebarCreateDropRules.Folder(p, LibraryWrites))),
                onOver: (p, _) => active.SetIfChanged(CreateArmed(SidebarCreateDropRules.Folder(p, LibraryWrites))),
                onLeave: _ => active.Value = false,
                onDrop: (p, _) =>
                {
                    active.Value = false;
                    var writes = LibraryWrites;
                    if (SidebarCreateDropRules.Folder(p, writes) == SidebarCreateDrop.NewFolder)
                        writes!.NewFolderWith!(folderId, NavDropRules.RefsOf(p));
                    else NavModel.RefuseWrite("folder create drop");
                },
                visualPolicy: Kit.DropLook.TargetPolicy,
                spotlightWhen: SpotlightFor);

        /// <summary>The "+" plate lights only for a drop that will actually create something.</summary>
        static bool CreateArmed(SidebarCreateDrop outcome) => outcome is SidebarCreateDrop.NewFolder or SidebarCreateDrop.NewPlaylist;

        // The pin and unpin verbs (recorded in the undo ring, §P4.4) are the nav model's: Model.Pin / Model.Unpin.
    }
}
