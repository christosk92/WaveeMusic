// ── Theming/Contracts/ViewKeys.Library.cs — the Library family of published view keys (themes plan 6.0, T10c1) ───────────────────
//
// T10c1 publishes the library page (`Pages.Library`: the albums / artists / podcasts / audiobooks master-detail) and its four panes: the album pane
// (`Library.AlbumPane`), the compact show pane (`Library.ShowPane`, podcasts and audiobooks), the artists' reader (`Library.ArtistReader`) and the
// Your Episodes page (`Library.SavedEpisodes`).
//
// What stays core: the library data (the relations, the rows, the demand), the navigator's shape memo (filter, sort, the letter groups, the a-z gate, the
// mount identity), the selection and its persistence (`LibraryViewPrefs`), the search matcher and its selection commit, the collapse state, the readiness of
// each pane (`AlbumPaneReadiness`), the pane models and their demands, and every verb. A theme draws what the props carry.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The library page of one kind: the navigator column (toolbar, word rail, view toggle, filter, the bound list or grid with its letters), the
    /// column grips, the detail pane beside it, the three search columns and the collapsed breadcrumb drill-in. Mounted by `User.LibraryPage`.
    /// Props: <see cref="LibraryPageProps"/>.</summary>
    public static readonly ViewKey<LibraryPageProps> LibraryPage = new("Pages.Library");

    /// <summary>The albums' pane: the header, the command row, the notice strip, the counted shimmer or the embedded table, and the "Also by" strip. Mounted by
    /// `Album.Pane`. Props: <see cref="LibraryAlbumPaneProps"/>.</summary>
    public static readonly ViewKey<LibraryAlbumPaneProps> LibraryAlbumPane = new("Library.AlbumPane");

    /// <summary>The compact show pane (a followed show or an audiobook): the header, the command row and the episode reader. Mounted by `Show.Pane`.
    /// Props: <see cref="LibraryShowPaneProps"/>.</summary>
    public static readonly ViewKey<LibraryShowPaneProps> LibraryShowPane = new("Library.ShowPane");

    /// <summary>The artists' reader: one scroller of release blocks under a sticky band and sub-rail, with a spine of dots. Mounted by `Artist.Reader`.
    /// Props: <see cref="LibraryArtistReaderProps"/>.</summary>
    public static readonly ViewKey<LibraryArtistReaderProps> LibraryArtistReader = new("Library.ArtistReader");

    /// <summary>Your Episodes: the listen-later list. The states above the table (loading, empty, failed) and the table's seed. Mounted by `SavedEpisodesReader`.
    /// Props: <see cref="LibrarySavedEpisodesProps"/>.</summary>
    public static readonly ViewKey<LibrarySavedEpisodesProps> LibrarySavedEpisodes = new("Library.SavedEpisodes");
}
