// ── Themes/Zune/Structure/ZuneStructure.Collection.cs — the collection family's registrar (themes plan 6.3; P5 Z7) ───────────────────────
//
// Z7: the library, Recents and the profile. Zune's registrations win over Classic's stand-in key by key.
//
//   Library   Pages.Library (`Views\Library\ZuneLibraryPage*.cs`: the head of words, the navigator's grid or list, the pane column, the search columns and the Compact
//             drill-in), Library.AlbumPane (`ZuneLibraryAlbumPane`), Library.ShowPane (`ZuneLibraryShowPane`), Library.ArtistReader (`ZuneLibraryArtistReader*`: the band,
//             the sub-rail, the blocks, the spine) and Library.SavedEpisodes (`ZuneLibrarySavedEpisodes`). The kinds (liked · playlists · albums · artists · podcasts ·
//             audiobooks) are the band's row 2, published by the core as route-level views; the playlists list is the core's own page (`User.LibraryPlaylists`), drawn
//             with the style's roles. Local files renders through the playlist page (Z6b1).
//   Recents   Pages.Recents (`Views\Recents\ZuneRecents*.cs`): the day-grouped list with its accordion and the overview column; the pivots (all · music · podcasts ·
//             artists) and the "overview" word are the band's row 2, published by the core host (`Recents.PageView`).
//   Profile   Pages.User (`Views\Profile\ZuneProfile.cs`) and Pages.ProfileList (`ZuneProfileList*.cs`; no board: built from Zune's parts, owner Q18). The page's
//             sections and the list's facets are the band's row 2, published by the core hosts.
//
// Common: Module (`CommonBlocks`, registered by `ZuneStructure.RegisterOwn`).

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterCollection(ViewRegistryBuilder views)
    {
        // Library.
        views.Add(ViewKeys.LibraryPage, ZuneLibraryPage.Template, "zune.library.page");
        views.Add(ViewKeys.LibraryAlbumPane, ZuneLibraryAlbumPane.Template, "zune.library.album-pane");
        views.Add(ViewKeys.LibraryShowPane, ZuneLibraryShowPane.Template, "zune.library.show-pane");
        views.Add(ViewKeys.LibraryArtistReader, ZuneLibraryArtistReader.Template, "zune.library.artist-reader");
        views.Add(ViewKeys.LibrarySavedEpisodes, ZuneLibrarySavedEpisodes.Template, "zune.library.saved-episodes");

        // Recents.
        views.Add(ViewKeys.RecentsPage, ZuneRecents.Template, "zune.recents.page");

        // Profile.
        views.Add(ViewKeys.ProfilePage, ZuneProfile.Template, "zune.profile.page");
        views.Add(ViewKeys.ProfileListPage, ZuneProfileList.Template, "zune.profile.list");
    }
}
