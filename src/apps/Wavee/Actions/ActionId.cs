namespace Wavee;

// Stable action identities — the ONE part kept from a CommandManager design: a future shortcut map / command palette
// keys off these, and every AppAction carries its Id. Values are persisted-safe (append only; never reorder/reuse).
public enum ActionId : ushort
{
    None = 0,
    Play, PlayNext, AddToQueue, ToggleLike, AddToPlaylist, AddToDefaultPlaylist,
    GoToAlbum, GoToArtist, CopyLink, RemoveFromThisPlaylist, RemoveFromQueue, SelectAll,
    OpenItem, PlayContext, SaveContext /* save album · follow artist · save playlist */,
    RenamePlaylist, TogglePlaylistPublic, InviteCollaborators, DeletePlaylist,
    PinToSidebar,
    GoToSongRadio, GoToArtistRadio, ViewCredits, CopySpotifyUri, OpenInSpotifyWeb,
    // The Video ▸ submenu (local video overrides): attach/replace are the same verb over the uri primary key, so they
    // are still two IDENTITIES — the label, the icon and the undo semantics differ.
    AttachVideo, ReplaceVideo, LocateVideo, RemoveVideo, ShowVideoInExplorer,
    // The pin pair's second half. PinToSidebar was already reserved above; pin/unpin are an ABSOLUTE-STATE pair
    // (Menus.AccessItem's precedent), not one toggle — so they are two IDENTITIES with two labels and two icons.
    UnpinFromSidebar,
    // The CONTAINER half of the transport/collection verbs (menu-grammar convergence, D48). A container's tracks are
    // not in hand at menu-open time — they resolve through the SAME reader the drag payload uses — so these are their
    // own identities rather than the track verbs with a different target: the label is the same, the enablement rule
    // and the execution path are not.
    PlayContextNext, AddContextToQueue, AddContextToPlaylist,
    /// <summary>Album card → its primary artist (the track menu's Go-to-artist, for a target that carries no Track).</summary>
    GoToAlbumArtist,
}
