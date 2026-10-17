// ── Shell/Sidebar.Cards.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's media-card FACTS: every decision a grid tile makes about an entry (selected, what activating does, play, drop, drag, the
// title gate, pending, circular) is a function in `SidebarCardRules`, pinned by `SidebarCardsTests`
//
// Role: CORE
// Spec: docs/plans/wavee/shared-media-surface-implementation.md §2.7, §4 wave 3
//
// T5f: the adapter that turns an entry into the media surface's `CardData` (the cover box, the glyph, the subtitle's look, the cover's
// size in the card's plate) is the structure's: `Themes/Classic/Structure/Views/Nav/ClassicNavGridCell.cs`. It asks these rules and
// reaches the pane through `INavRowHost`.

namespace Wavee;

/// <summary>What activating a projected entry does: a track PLAYS (it has no detail route), everything with a route
/// NAVIGATES, a folder (it expands in place) and a route-less entry do nothing — their surface carries NO click, so it is
/// display-only (no hand, no Button role, no tab stop).</summary>
public enum SidebarCardActivation : byte { None, Play, Navigate }

/// <summary>What a card is as a drop destination: nothing, a track DEPOSIT (an editable playlist), or a REFUSAL that
/// says why (a playlist the user cannot edit — the sidebar names the reason).</summary>
public enum SidebarCardDrop : byte { None, Deposit, Refuse }

/// <summary>The pure facts behind the sidebar's media cards, per entry kind. No engine type is touched, so each answer is
/// pinned by a fact (<c>SidebarCardsTests</c>) instead of a source read. Public only because this assembly has no
/// <c>InternalsVisibleTo</c>.</summary>
public static class SidebarCardRules
{
    /// <summary>Selected = the entry's nav route IS the live route (<see cref="SidebarRowResolve.EntrySelects"/> — the ONE
    /// rule the row sweep also uses). A folder and a track have no route, so they are never selected.</summary>
    public static bool Selected(in SidebarLibraryEntry entry, string route) => SidebarRowResolve.EntrySelects(in entry, route);

    public static SidebarCardActivation Activation(in SidebarLibraryEntry entry)
        => entry.IsTrack ? SidebarCardActivation.Play
         : entry.RouteKey is { Length: > 0 } ? SidebarCardActivation.Navigate
         : SidebarCardActivation.None;

    /// <summary>A play affordance exists on the grid tile for an entry that has a playable context. <c>IsPlayable</c> is
    /// Playlist / Album / Show / Track: an artist has no single context to start.</summary>
    public static bool HasPlay(in SidebarLibraryEntry entry) => entry.IsPlayable && !string.IsNullOrEmpty(entry.Uri);

    /// <summary>A playlist is the one drop destination a card can be. An editable one takes a track deposit; a read-only one
    /// REFUSES with a reason — exactly what Classic's entity row does.</summary>
    public static SidebarCardDrop Drop(in SidebarLibraryEntry entry)
        => entry.Kind != SidebarEntryKind.Playlist ? SidebarCardDrop.None
         : entry.CanEdit ? SidebarCardDrop.Deposit
         : SidebarCardDrop.Refuse;

    /// <summary>A card is a drag source (pin it, add its tracks to a playlist) unless it is a track — a track is never a pin
    /// source, enforced by the kind and not per surface.</summary>
    public static bool CanDrag(in SidebarLibraryEntry entry) => !entry.IsTrack;

    /// <summary>A playlist or a folder is a rootlist member, so its drag payload is file-able in the tree.</summary>
    public static bool RootlistMember(in SidebarLibraryEntry entry)
        => entry.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder;

    /// <summary>The title a tile paints — Trap 5: an entry whose identity has not landed shows NOTHING, never
    /// the raw uri fragment.</summary>
    public static string TitleOf(in SidebarLibraryEntry entry)
        => entry.Name.Length > 0 ? entry.Name
         : SidebarProjection.ShouldShowUriFallbackTitle(entry.IsPinned, entry.IdentityKnown) ? NavRowFacts.ShortUri(entry.Uri)
         : "";

    /// <summary>An entry with no title yet, which renders as a bone instead of a tile with an empty title — and, like an
    /// unresolved entity row, is not invokable.</summary>
    public static bool IsPending(in SidebarLibraryEntry entry) => TitleOf(in entry).Length == 0;

    /// <summary>An artist and a circular entry are drawn as a circle (the grid tile's cover box follows the same answer).</summary>
    public static bool Circular(in SidebarLibraryEntry entry) => entry.Circular || entry.Kind == SidebarEntryKind.Artist;
}
