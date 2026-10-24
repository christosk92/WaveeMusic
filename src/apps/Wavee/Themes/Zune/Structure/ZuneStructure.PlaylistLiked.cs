// ── Themes/Zune/Structure/ZuneStructure.PlaylistLiked.cs — the playlist and Liked Songs, registered (themes plan 6.3; P5 Z6b1) ───────────────────────
//
// Zune's own views for the playlist's sub-contract (`Playlist.*`, ten keys) and the Liked Songs page (`Liked.*`, four keys). They win over Classic's stand-in key by
// key (`ZuneStructure.Register`). The frame they sit in (the panorama, the identity column, the vertical hero, the list's "songs" heading, the aside) is Z6a1's
// (`ZuneStructure.Detail.cs`); this family draws what the two pages hand it as slots and what they mount under their own keys:
//
//   Playlist.CoverArt / CoverEditor   the face, and the editable cover (the strip along its foot, the dashed "choose a cover" tile when empty)   Views\Playlist\ZunePlaylistCover.cs
//   Playlist.Editor                   the title and the description as the editor (the hairline that rises on hover, "save" / "cancel")        ZunePlaylistEditor.cs
//   Playlist.Owner / MembersFlyout    the owner run or the collaborator pile and the invite ring; the flyout is the album's every-artist one    ZunePlaylistOwner.cs
//   Playlist.Access / Tune            the invite and access panel, the tune panel                                                              ZunePlaylistPanels.cs
//   Playlist.Daylist                  the countdown in the numeral voices, or the ring                                                         ZunePlaylistExtras.cs
//   Playlist.Recs / RecRow            "recommended songs" with its "refresh" word, the rows with an "add" pill, and the empty playlist's lead  ZunePlaylistExtras.cs
//   Liked.Cover / CoverPicker / CoverStyleFlyout   the flat cover (stock, mosaic, tone), its strip and its three-look flyout                   Views\Liked\ZuneLikedCover.cs
//   Liked.ChipBar                     the flat chips                                                                                          Views\Liked\ZuneLikedChipBar.cs
//
// DECIDED HERE (plan "Zune specifics": each lane records what it decides):
//   · The pages publish their row 2 from the core hosts (`Playlist.Page.cs`, `User.Page.Liked.cs`) through the `PageChromeSink`, as the album does: the playlist's
//     view "songs" (current) and the actions play, shuffle and, on a playlist that is not the viewer's own, save / saved; Liked Songs publishes play and shuffle and
//     leaves its views to the library route (liked, playlists, albums ...).
//   · Zune draws three of the engine's nine Liked cover treatments (stock, mosaic, tone), flat; the other six are Classic's ambient loops. See `ZuneLikedCover`.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterPlaylistLiked(ViewRegistryBuilder views)
    {
        // ── the playlist ──
        views.Add(ViewKeys.PlaylistCoverArt, ZunePlaylist.CoverArtTemplate, "zune.playlist.cover-art");
        views.Add(ViewKeys.PlaylistCoverEditor, ZunePlaylist.CoverEditorTemplate, "zune.playlist.cover-editor");
        views.Add(ViewKeys.PlaylistEditor, ZunePlaylist.EditorTemplate, "zune.playlist.editor");
        views.Add(ViewKeys.PlaylistOwner, ZunePlaylist.OwnerTemplate, "zune.playlist.owner");
        views.Add(ViewKeys.PlaylistMembersFlyout, ZunePlaylist.MembersFlyoutTemplate, "zune.playlist.members-flyout");
        views.Add(ViewKeys.PlaylistAccess, ZunePlaylist.AccessTemplate, "zune.playlist.access");
        views.Add(ViewKeys.PlaylistTune, ZunePlaylist.TuneTemplate, "zune.playlist.tune");
        views.Add(ViewKeys.PlaylistDaylist, ZunePlaylist.DaylistTemplate, "zune.playlist.daylist");
        views.Add(ViewKeys.PlaylistRecs, ZunePlaylist.RecsTemplate, "zune.playlist.recs");
        views.Add(ViewKeys.PlaylistRecRow, ZunePlaylist.RecRowTemplate, "zune.playlist.rec-row");

        // ── Liked Songs ──
        views.Add(ViewKeys.LikedCover, ZuneLikedCover.CoverTemplate, "zune.liked.cover");
        views.Add(ViewKeys.LikedCoverPicker, ZuneLikedCover.PickerTemplate, "zune.liked.cover-picker");
        views.Add(ViewKeys.LikedCoverStyleFlyout, ZuneLikedCover.FlyoutTemplate, "zune.liked.cover-style-flyout");
        views.Add(ViewKeys.LikedChipBar, ZuneLikedChipBar.Template, "zune.liked.chip-bar");
    }
}
