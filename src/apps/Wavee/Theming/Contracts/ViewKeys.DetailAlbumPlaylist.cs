// ── Theming/Contracts/ViewKeys.DetailAlbumPlaylist.cs — the album and playlist sub-contracts of the detail frame (themes plan 6.0, T9b) ──────────
//
// T9a published the shared detail frame; the album and the playlist hand it SLOTS (the billed-artist line, the prerelease card, the release facts,
// the trailing band; the cover, the inline title and description editors, the owner block, the daylist strip, the recommendations section) and the
// pages' own panels (the every-artist flyout, the collaborator flyout, the access panel, the Tune panel). T9b gives each of those pictures a key; the
// core hosts (`Album.FacePileHost`, `Album.CountdownHost`, `Playlist.InlineEditor`, `Playlist.OwnerBlock`...) keep every hook, stamp, signal, demand and
// verb and mount the key with the values the picture shows.
//
// What stays core: the album and playlist models, the demand of their relations, the page and trailing gates (`Album.PageRules`), the optimistic switch
// rule, the edit verbs (`Spotify.PlaylistEdits`) and the permission decisions (`Playlist.EditableMetadata`, `CanAdministratePermissions`, `IsOwner`), the
// clock behind the countdown and the daylist strip, the file pick / encode / write of a cover (`PlaylistCover`), and the plain data each template draws.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    // ── the detail frame's two page-level leaves ──

    /// <summary>The notice strip over a page (a deleted / revoked / failed playlist, a minified album): one Informational bar, or a zero-height box.
    /// Mounted by `Detail.NoticeBar` (the frame host and the library album pane). Props: <see cref="DetailNoticeProps"/>.</summary>
    public static readonly ViewKey<DetailNoticeProps> DetailNotice = new("Detail.Notice");

    /// <summary>The vertical arm's body under the track rows: the prerelease card, the release facts, then the album trailing band. Mounted by
    /// `Detail.FrameHost` (the table's trailing thunk). Props: <see cref="DetailVerticalTrailingProps"/>.</summary>
    public static readonly ViewKey<DetailVerticalTrailingProps> DetailVerticalTrailing = new("Detail.VerticalTrailing");

    // ── album ──

    /// <summary>The billed-artist line in one width mode: the faces, the names, the "+N", the chevron, as ONE trigger. Mounted by `Album.FacePileHost`.
    /// Props: <see cref="AlbumArtistLineProps"/>.</summary>
    public static readonly ViewKey<AlbumArtistLineProps> AlbumArtistLine = new("Album.ArtistLine");

    /// <summary>The every-artist flyout: 44-DIP rows (portrait and name) in a scrolling list. Mounted by the face pile when it opens. Props:
    /// <see cref="PersonFlyoutProps"/>.</summary>
    public static readonly ViewKey<PersonFlyoutProps> AlbumArtistFlyout = new("Album.ArtistFlyout");

    /// <summary>The prerelease card (ring, "Coming soon", four unit tiles) or its bare body. Mounted by `Album.CountdownHost`. Props:
    /// <see cref="AlbumCountdownProps"/>.</summary>
    public static readonly ViewKey<AlbumCountdownProps> AlbumCountdown = new("Album.Countdown");

    /// <summary>"About this release": the released date and label, the ℗ / © notes and the Other versions picker. Mounted by `Album.ReleasePanelHost`.
    /// Props: <see cref="AlbumReleasePanelProps"/>.</summary>
    public static readonly ViewKey<AlbumReleasePanelProps> AlbumReleasePanel = new("Album.ReleasePanel");

    /// <summary>The trailing band's reserved region: one skeleton boundary shimmering from the first frame and revealing once. Mounted by
    /// `Album.TrailingHost`. Props: <see cref="AlbumTrailingProps"/>.</summary>
    public static readonly ViewKey<AlbumTrailingProps> AlbumTrailing = new("Album.Trailing");

    /// <summary>The resolved trailing band: the music videos, About the artist, Fans also like and the list sections, in their fixed order. Mounted by
    /// `Album.SectionsHost`. Props: <see cref="AlbumSectionsProps"/>.</summary>
    public static readonly ViewKey<AlbumSectionsProps> AlbumSections = new("Album.Sections");

    /// <summary>The Huge aside's body (C-Album-Huge): About this release, the artist card, Fans also like and "More by", in that order. Mounted by
    /// `Album.SectionsHost`'s aside arm, placed by the frame's aside column (contract 1.21). Props: <see cref="AlbumAsideProps"/>.</summary>
    public static readonly ViewKey<AlbumAsideProps> AlbumAside = new("Album.Aside");

    /// <summary>One capped list section: its title, the one-way "Show all N" and the rows. Mounted by `Album.StackHost`. Props:
    /// <see cref="AlbumTrailStackProps"/>.</summary>
    public static readonly ViewKey<AlbumTrailStackProps> AlbumTrailStack = new("Album.TrailStack");

    /// <summary>An unresolvable prerelease route: a centred "nothing here yet" line. Mounted by the album page. Props:
    /// <see cref="AlbumUnresolvedProps"/>.</summary>
    public static readonly ViewKey<AlbumUnresolvedProps> AlbumUnresolved = new("Album.Unresolved");

    // ── playlist ──

    /// <summary>The playlist's cover face: the server mosaic, the art, a member mosaic or the neutral placeholder. Mounted by `Playlist.CoverArt` (the
    /// frame's compact cover, the profile's list cards). Props: <see cref="PlaylistCoverArtProps"/>.</summary>
    public static readonly ViewKey<PlaylistCoverArtProps> PlaylistCoverArt = new("Playlist.CoverArt");

    /// <summary>The editable cover: the art under a hover / file-drag / saving scrim, the file-drop target and the click-to-pick. Mounted by
    /// `Playlist.CoverEditor`. Props: <see cref="PlaylistCoverEditorProps"/>.</summary>
    public static readonly ViewKey<PlaylistCoverEditorProps> PlaylistCoverEditor = new("Playlist.CoverEditor");

    /// <summary>The inline title and description editor: the read arm with its pencil, the edit arm with Save / Cancel, the status row. Mounted by
    /// `Playlist.InlineEditor`. Props: <see cref="PlaylistEditorProps"/>.</summary>
    public static readonly ViewKey<PlaylistEditorProps> PlaylistEditor = new("Playlist.Editor");

    /// <summary>The owner block: the collaborator pile or the owner run, and the invite affordance. Mounted by `Playlist.OwnerBlock`. Props:
    /// <see cref="PlaylistOwnerProps"/>.</summary>
    public static readonly ViewKey<PlaylistOwnerProps> PlaylistOwner = new("Playlist.Owner");

    /// <summary>The collaborator flyout: 44-DIP rows (portrait and name) in a scrolling list. Mounted when the pile opens. Props:
    /// <see cref="PersonFlyoutProps"/>.</summary>
    public static readonly ViewKey<PersonFlyoutProps> PlaylistMembersFlyout = new("Playlist.MembersFlyout");

    /// <summary>The invite and access panel: the hint, the copy-invite call to action and the two optimistic switches. Mounted by
    /// `Playlist.AccessPanel`. Props: <see cref="PlaylistAccessProps"/>.</summary>
    public static readonly ViewKey<PlaylistAccessProps> PlaylistAccess = new("Playlist.Access");

    /// <summary>The Tune panel: the accent header, the choices (the current one disabled) and the reset row. Mounted when a Tune dialog opens.
    /// Props: <see cref="PlaylistTuneProps"/>.</summary>
    public static readonly ViewKey<PlaylistTuneProps> PlaylistTune = new("Playlist.Tune");

    /// <summary>The daylist flip strip: the digits, the ring while rolling, the next-update caption. Mounted by `Playlist.DaylistHost`. Props:
    /// <see cref="PlaylistDaylistProps"/>.</summary>
    public static readonly ViewKey<PlaylistDaylistProps> PlaylistDaylist = new("Playlist.Daylist");

    /// <summary>The Recommended section: its header row (the title, the "No suggestions" note, the spinner or the refresh) over the rows. Mounted by
    /// `Playlist.RecsSection`. Props: <see cref="PlaylistRecsProps"/>.</summary>
    public static readonly ViewKey<PlaylistRecsProps> PlaylistRecs = new("Playlist.Recs");

    /// <summary>One recommendation row on the shared media surface, with its add button. Mounted by the Recommended section's view (one slot per row: the
    /// row reads the track table, so a title landing re-renders that row and not the section). Props: <see cref="PlaylistRecRowProps"/>.</summary>
    public static readonly ViewKey<PlaylistRecRowProps> PlaylistRecRow = new("Playlist.RecRow");
}
