// ── Shell/NavModel.Drop.cs ─────────────────────────────────────────────────────────────────────────────────────────
// what a drop DOES: the rootlist move, the track deposit, the pin, and the refusal that says why it did not
//
// Role: CORE · Plan: technical-plan.md §5.1 row C12 (BP X30) · design ch 25 §0.8, §9 "every no is two answers"
//
// The decisions (does a row accept this payload, which arm commits it) are `NavDropRules`'; the pointer → slot geometry and
// the cues are the Classic view's. This is the layer that touches the library: the MUTATIONS are the library seam's
// (`Sidebar.LibraryWrites`), and a missing seam member REFUSES with a sentence and a log line — nothing on this path fails by
// returning. Pinning goes through <see cref="Pin"/>, so the signed-out guard ("Sign in to pin") and the already-pinned
// no-op are the same for a menu, a key and a drop.

using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>What a drop is aimed at. A pointer-based rootlist slot (a row's Before / After / Into) is NOT one of these: it is
/// resolved against the pane's plan and committed with <see cref="NavModel.MoveRootlist"/>.</summary>
public enum NavDropTargetKind : byte
{
    /// <summary>The compact folder flyout's folder row: Into, and only Into.</summary>
    Folder = 0,
    /// <summary>An editable playlist: it takes the payload's tracks.</summary>
    Playlist = 1,
    /// <summary>A slot of the Pinned band: the payload is pinned AT that position (or moved there if already pinned).</summary>
    PinIndex = 2,
    /// <summary>A whole section (the Pinned band's reorder surface receiving a foreign entity at a slot).</summary>
    Section = 3,
}

/// <summary>A drop destination. Build one with the factory that names its kind.</summary>
/// <param name="Id">The folder's group id (<see cref="NavDropTargetKind.Folder"/>) or the section's id (<see cref="NavDropTargetKind.Section"/>).</param>
/// <param name="Name">The destination's display name, for the toast ("Moved to …", "Added to …").</param>
/// <param name="Uri">The playlist's uri (<see cref="NavDropTargetKind.Playlist"/>).</param>
/// <param name="Slot">The pin slot (<see cref="NavDropTargetKind.PinIndex"/> and <see cref="NavDropTargetKind.Section"/>).</param>
/// <param name="Row">The folder row's own payload (what the self check asks), for a folder target.</param>
public readonly record struct NavDropTarget(NavDropTargetKind Kind, string Id = "", string Name = "", string? Uri = null,
                                            int Slot = -1, DragPayload? Row = null)
{
    public static NavDropTarget Folder(string folderId, string name, DragPayload row)
        => new(NavDropTargetKind.Folder, folderId, name, null, -1, row);

    public static NavDropTarget Playlist(string uri, string name) => new(NavDropTargetKind.Playlist, "", name, uri);

    public static NavDropTarget PinIndex(int slot) => new(NavDropTargetKind.PinIndex, Slot: slot);

    public static NavDropTarget Section(string sectionId, int slot) => new(NavDropTargetKind.Section, sectionId, Slot: slot);
}

/// <summary>What a committed drop did.</summary>
public enum NavDropResult : byte
{
    /// <summary>Nothing happened (not for this target, or refused — a refusal has already said so).</summary>
    None = 0,
    /// <summary>The rootlist items were sent to the move seam.</summary>
    Moved = 1,
    /// <summary>The tracks were sent to the deposit seam.</summary>
    Deposited = 2,
    /// <summary>The payload was pinned.</summary>
    Pinned = 3,
    /// <summary>An already-pinned payload was moved to the slot.</summary>
    PinMoved = 4,
}

public sealed partial class NavModel
{
    /// <summary>Commit a drop of <paramref name="payload"/> on <paramref name="target"/>. <paramref name="sortedFix"/> is the
    /// action a "clear sorting to reorder" refusal offers (the mode's).</summary>
    public NavDropResult Drop(DragPayload payload, in NavDropTarget target, Action? sortedFix = null)
    {
        switch (target.Kind)
        {
            case NavDropTargetKind.Folder:
                return RailFolderAccepts(payload, in target)
                    ? MoveRootlist(payload, new RootlistItemRef(target.Id, IsFolder: true), RootlistDropPlacement.Inside, target.Name, sortedFix)
                        ? NavDropResult.Moved : NavDropResult.None
                    : NavDropResult.None;
            case NavDropTargetKind.Playlist:
                return payload.CanCopyTracks && target.Uri is { Length: > 0 } uri && DepositTracks(uri, target.Name, payload)
                    ? NavDropResult.Deposited
                    : NavDropResult.None;
            case NavDropTargetKind.PinIndex:
                return DropPin(payload, target.Slot);
            case NavDropTargetKind.Section:
                // Only the Pinned band takes a foreign entity.
                return SidebarCatalogue.TryKindOf(target.Id, out var kind) && kind == SidebarSectionKind.Pinned
                    ? DropPin(payload, target.Slot)
                    : NavDropResult.None;
            default:
                return NavDropResult.None;
        }
    }

    /// <summary>Drop-to-pin (the band or the empty drop zone). An already-pinned payload is a MOVE, never a duplicate; a pin
    /// goes through <see cref="Pin"/>, so a signed-out drop says so and pins nothing.</summary>
    NavDropResult DropPin(DragPayload payload, int slot)
    {
        if (!payload.CanPin) return NavDropResult.None;
        string? pinId = SidebarPinId.Canonical(payload.Id) ?? SidebarPinId.Canonical(payload.Uri);
        if (pinId is null) return NavDropResult.None;
        int at = Pins.IndexOf(pinId);
        if (at >= 0)
        {
            MovePin(at, slot > at ? slot - 1 : slot);   // remove-then-insert shifts later indices down by one
            return NavDropResult.PinMoved;
        }
        return Pin(pinId, SidebarPinId.KindOf(pinId), SidebarPinId.UriOf(pinId), payload.Name, slot) ? NavDropResult.Pinned : NavDropResult.None;
    }

    // ── the rail folder row ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Does the flyout's folder row accept this payload: a rootlist filing, the rootlist loaded, a real folder, not the
    /// dragged item itself, and the marker stream says Into is legal.</summary>
    public bool RailFolderAccepts(DragPayload payload, in NavDropTarget target)
        => target.Row is { } row
           && NavDropRules.RailFolderGate(payload, row, RootlistLoaded) == NavRailFolderGate.Open
           && target.Id.Length > 0
           && RailFolderCheck(payload, target.Id) == RootlistMoveCheck.Ok;

    /// <summary>The sentence a refused rail-folder drop shows (the drop target's refusal caption), or null when the folder is
    /// merely being crossed (a track drag).</summary>
    public string? RailFolderWhy(DragPayload payload, in NavDropTarget target)
    {
        if (target.Row is not { } row) return null;
        switch (NavDropRules.RailFolderGate(payload, row, RootlistLoaded))
        {
            case NavRailFolderGate.Transparent: return null;
            case NavRailFolderGate.NotLoaded: return Loc.Get("drag.stillLoading");
            case NavRailFolderGate.IntoItself: return Loc.Get("drag.cantMoveIntoItself");
            default: return NavDropRules.Sentence(RootlistDropDecision.RefusalFor(RailFolderCheck(payload, target.Id)));
        }
    }

    RootlistMoveCheck RailFolderCheck(DragPayload payload, string folderId)
        => RootlistDropDecision.Check(RootlistMarkers, NavDropRules.RefsOf(payload),
                                      new RootlistItemRef(folderId, IsFolder: true), RootlistDropPlacement.Inside);

    // ── the mutations ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>EXACTLY ONE mutation per drop, whatever the selection size — through the library seam, which awaits it and only
    /// then announces, toasts and offers Undo (with <c>""</c> meaning "Your Library"). The undo anchors are captured BEFORE the
    /// mutation (once the rootlist moved, where things were is unknowable); a batch resolves all or none. A missing seam
    /// refuses out loud. Returns whether the move was sent.</summary>
    public bool MoveRootlist(DragPayload source, RootlistItemRef target, RootlistDropPlacement placement, string destinationName,
                             Action? sortedFix = null)
    {
        var tree = Rootlist;
        RootlistUndoAnchors.TryResolveMany(tree, NavDropRules.SourceIds(tree, source), out var undo);
        if (Sidebar.LibraryWrites?.MoveRootlist is not { } move)
        {
            RefuseDrop(SidebarDropRefusal.WritesUnavailable, "no rootlist seam", sortedFix);
            return false;
        }
        move(NavDropRules.RefsOf(source), target, placement, destinationName, undo);
        return true;
    }

    /// <summary>Copy the payload's tracks into an editable playlist. A missing seam refuses out loud. Returns whether the deposit
    /// was sent.</summary>
    public bool DepositTracks(string playlistUri, string playlistName, DragPayload source)
    {
        if (Sidebar.LibraryWrites?.DepositTracks is { } deposit)
        {
            deposit(playlistUri, playlistName, source);
            return true;
        }
        RefuseDrop(SidebarDropRefusal.WritesUnavailable, "no deposit seam");
        return false;
    }

    /// <summary>A drop that cannot be honoured says so: a log line for us, the refusal's own sentence for the user — and
    /// "clear sorting to reorder" carries the one action a mode can fix it with (#85 H3). A missing library seam is
    /// <see cref="RefuseWrite"/>'s.</summary>
    public static void RefuseDrop(SidebarDropRefusal refusal, string why, Action? sortedFix = null)
    {
        if (refusal == SidebarDropRefusal.WritesUnavailable) { RefuseWrite(why); return; }
        Log.Warn("sidebar", "rootlist drop refused at commit: " + refusal + " (" + why + ")");
        if (NavDropRules.Sentence(refusal) is not { Length: > 0 } sentence) return;
        if (refusal == SidebarDropRefusal.SortedList && sortedFix is not null)
            Notify.Say(sentence, InfoBarSeverity.Informational, Loc.Get("sidebar.v3.sort.custom"), sortedFix);
        else
            Notify.Say(sentence, InfoBarSeverity.Informational);
    }
}
