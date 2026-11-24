// ── Theming/Contracts/LocalCollectionProps.cs — what the "On this PC" page hands a theme (#240 P2, boards 18-21) ──────────────
//
// The core host (`Entities/LocalCollection.Page.cs`) folds the local index into ONE immutable view (`LocalCollectionView`,
// computed off the UI thread by `Playback/LocalCollection.cs` on every `LocalLibrary.Epoch` / `LocalArt.Epoch` move) and hands the
// active theme this props record: the view as a live signal, the remembered pivot, the album sort and the open detail as live
// signals, and every verb as a delegate built once. Albums and artists here are NAMES from the files' tags, never Spotify entities.
// Covers are `file:///` uris from `LocalArt` (null: not extracted yet, or none). Quality is a short label ("FLAC", "Hi-Res 24/96",
// "MP3 320") or "".
//
// Contract additions (declared, not numbered): `ViewKeys.LocalCollectionPage` and every type in this file; later (#240 P2 gaps):
// `LocalShelf`, `LocalShelfSlot`, `LocalCollectionPageProps.Shelves/ShowShelf/MoveShelf/Drop`, `LocalCollectionView.HiResSongs/LastScanTicks`; later still
// (#240 detail motion): `LocalOpened`, `LocalCollectionPageProps.Opened` in place of the bare `Folder` signal.
// Later ("Your files and Spotify", phase 1): `LocalCollectionPageProps.Link` (LocalLinkProps.cs). Later (the library look, #240): `LocalArtistSort`,
// `LocalCollectionPageProps.ArtistSort/SetArtistSort/ListView/ListSize/Filter` (the Albums and Artists pivots wear the library's toolbar).

using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The page's views, in the order the pivot shows them (the page remembers the last one).</summary>
public enum LocalPivot : byte { Home, Albums, Artists, Songs, Folders, Insights }

/// <summary>The Albums wall's orders.</summary>
public enum LocalAlbumSort : byte { Added, Artist, Title, Year }

/// <summary>The Artists list's orders: by name, or by how many songs the collection holds of each.</summary>
public enum LocalArtistSort : byte { Name, Songs }

/// <summary>The library in one word (`LocalLibrary.StateWord`).</summary>
public enum LocalLibraryPhase : byte { None, Scanning, Ready, Offline }

/// <summary>One music folder's state, for its chip.</summary>
public enum LocalChipState : byte { Ok, Scanning, Offline, NoAccess, Waiting }

/// <summary>A shelf of Home that "Edit home" shows, hides and reorders.</summary>
public enum LocalShelf : byte { Recent, Lossless, Decades }

/// <summary>One Home shelf in the user's order, and whether it shows.</summary>
public readonly record struct LocalShelfSlot(LocalShelf Shelf, bool Shown);

/// <summary>When an album arrived, for the Recently added shelf's groups.</summary>
public enum LocalAddedWhen : byte { Today, ThisWeek, ThisMonth, Earlier }

/// <summary>One album built from the tags: its (album artist or first artist, album) pair.</summary>
/// <param name="Key">Stable per (artist, album): a node key and the verbs' argument.</param>
/// <param name="AddedTicks">UTC ticks of when Wavee first indexed the album's newest file (never the file's own write time).</param>
/// <param name="FolderKey">The Folders browser node its first file sits in (opening the album opens it).</param>
public sealed record LocalAlbumCard(string Key, string Title, string Artist, int Year, int Tracks, long DurationMs, string Quality,
    bool Lossless, bool IsNew, string? Cover, long AddedTicks, LocalAddedWhen When, string FolderKey);

/// <summary>One artist (album artist or first artist) with what the collection holds of them.</summary>
public sealed record LocalArtistCard(string Key, string Name, int Albums, int Tracks, string? Cover);

/// <summary>One file in the Folders browser's song list.</summary>
public sealed record LocalSongItem(long Id, int Number, string Title, string Album, string File, string Quality, int DurationMs, bool Playable);

/// <summary>One folder of the Folders browser (a music folder, or a folder under it).</summary>
/// <param name="Key">"root id|relative path"; a music folder's relative path is "".</param>
/// <param name="ParentKey">Null for a music folder.</param>
/// <param name="Songs">Every playable-or-not audio file under it (recursive).</param>
/// <param name="Name">The folder's own name ("Music"), a music folder's too (its full path is <paramref name="Path"/>).</param>
/// <param name="Mosaic">Up to four album covers from inside it (recursive), when it has no cover of its own.</param>
/// <param name="Path">Its full path, for a tooltip or a secondary line; "" when unknown. Appended (#240).</param>
public sealed record LocalFolderNode(string Key, string Name, string? ParentKey, int Songs, long DurationMs, string Quality, string? Cover,
    IReadOnlyList<string> Mosaic, IReadOnlyList<string> Children, IReadOnlyList<LocalSongItem> Files, long DurationHereMs, string Path = "");

/// <summary>One song with no album tag, as a card on Home's shelves (a loose song is its own group: it never reaches the album wall).</summary>
/// <param name="Id">The file's LocalId (<see cref="LocalCollectionVerbs.PlayFile"/>).</param>
/// <param name="Title">What its row shows: an accepted Spotify match's title, else the tags, else the file name's split.</param>
public sealed record LocalSongCard(long Id, string Title, string Artist, string? Cover, string Quality, bool Lossless, int Year, long AddedTicks,
    LocalAddedWhen When, bool IsNew);

/// <summary>One group of the Recently added shelf: its albums, then its songs with no album tag (newest first in each).</summary>
public sealed record LocalShelfGroup(LocalAddedWhen When, IReadOnlyList<LocalAlbumCard> Albums, IReadOnlyList<LocalSongCard>? Songs = null)
{
    /// <summary>The group's songs with no album tag (empty for an albums-only group).</summary>
    public IReadOnlyList<LocalSongCard> SongCards => Songs ?? [];
}

/// <summary>One decade of "Shuffle by decade" (from the year tags): its albums and its songs with no album tag.</summary>
public sealed record LocalDecade(int Decade, int Albums, IReadOnlyList<string> Covers, int Songs = 0);

/// <summary>One music folder's chip under the hero.</summary>
public sealed record LocalFolderChip(string Path, string Label, LocalChipState State, int Songs);

/// <summary>One bar of a share chart (formats, bitrates).</summary>
public sealed record LocalShare(string Label, int Count);

/// <summary>Insights (UX S10): what is here, how good it is, what needs attention. Counts only; nothing leaves the PC.</summary>
public sealed record LocalInsights(
    IReadOnlyList<LocalShare> Formats, IReadOnlyList<LocalShare> Bitrates, int LosslessSongs, int LossySongs,
    int AlbumsWithCover, int AlbumsKnownCover, int Untagged, int NoAlbumArtist, int NoTrackNumber, int Duplicates,
    IReadOnlyList<LocalShare> Unplayable, int Missing, int CloudOnly, IReadOnlyList<LocalFolderChip> Folders);

/// <summary>The whole collection, as one immutable value (a new instance per recompute).</summary>
public sealed record LocalCollectionView
{
    /// <summary>Bumped per recompute; 0 = nothing computed yet (the page shimmers).</summary>
    public int Version { get; init; }
    public LocalLibraryPhase Phase { get; init; }
    public int Songs { get; init; }
    public int AlbumCount { get; init; }
    /// <summary>Playable songs with no album tag: each is its own group for "Your files and Spotify" (the offer counts them beside the albums).</summary>
    public int LooseSongs { get; init; }
    public int ArtistCount { get; init; }
    public long DurationMs { get; init; }
    /// <summary>Lossless share of the playable songs, 0-100.</summary>
    public int LosslessPct { get; init; }
    public long Bytes { get; init; }
    public IReadOnlyList<LocalFolderChip> Chips { get; init; } = [];
    /// <summary>Album covers for the hero's mosaic, newest first (empty: the calm gradient).</summary>
    public IReadOnlyList<string> Mosaic { get; init; } = [];
    public IReadOnlyList<LocalShelfGroup> Recent { get; init; } = [];
    public int RecentThisMonth { get; init; }
    /// <summary>Songs with no album tag that arrived this month (the Recently added shelf's line counts them beside the albums).</summary>
    public int RecentSongsThisMonth { get; init; }
    public IReadOnlyList<LocalAlbumCard> Lossless { get; init; } = [];
    /// <summary>Lossless songs with no album tag, after the lossless albums on their shelf.</summary>
    public IReadOnlyList<LocalSongCard> LosslessLoose { get; init; } = [];
    public int LosslessSongs { get; init; }
    /// <summary>Lossless songs above CD quality (more than 16 bits or 48 kHz).</summary>
    public int HiResSongs { get; init; }
    /// <summary>When the last full scan ended (UTC ticks; 0: none yet), for "scanned 2 min ago".</summary>
    public long LastScanTicks { get; init; }
    public IReadOnlyList<LocalDecade> Decades { get; init; } = [];
    /// <summary>Every album, newest first; the other orders are <see cref="AlbumsBy"/>.</summary>
    public IReadOnlyList<LocalAlbumCard> Albums { get; init; } = [];
    public IReadOnlyList<LocalAlbumCard> AlbumsByArtist { get; init; } = [];
    public IReadOnlyList<LocalAlbumCard> AlbumsByTitle { get; init; } = [];
    public IReadOnlyList<LocalAlbumCard> AlbumsByYear { get; init; } = [];
    public IReadOnlyList<LocalArtistCard> Artists { get; init; } = [];
    public IReadOnlyDictionary<string, LocalFolderNode> Folders { get; init; } = new Dictionary<string, LocalFolderNode>();
    /// <summary>The music folders' node keys, in the user's order.</summary>
    public IReadOnlyList<string> RootKeys { get; init; } = [];
    public LocalInsights? Insights { get; init; }

    public IReadOnlyList<LocalAlbumCard> AlbumsBy(LocalAlbumSort sort) => sort switch
    {
        LocalAlbumSort.Artist => AlbumsByArtist,
        LocalAlbumSort.Title => AlbumsByTitle,
        LocalAlbumSort.Year => AlbumsByYear,
        _ => Albums,
    };

    public static LocalCollectionView Empty { get; } = new();
}

/// <summary>What the page has opened over a pivot (a detail: list -> detail, #240): a folder from the Folders browser's tiles, or an album's folder
/// from an album card (the Albums wall, Home's shelves). The pivot's list stays where it was; Back (the breadcrumb, the title bar's Back, Esc)
/// steps out a folder at a time and, from the folder it was opened at, returns to that list.</summary>
/// <param name="From">The pivot it was opened over: the detail shows only there.</param>
/// <param name="Folder">The open folder's node key; "" once the detail is closed (the record stays for the way back's motion: the list shows).</param>
/// <param name="Root">The folder the detail was opened at from the pivot's list; "" for the Folders browser (its trail starts at the music folders).</param>
/// <param name="Album">The album card that opened <paramref name="Root"/> (its key), or "": the root's header is then that album's cover and title.</param>
/// <param name="Back">The last step went out (Back, a breadcrumb to an ancestor), not in: the body's switch plays the drill back.</param>
/// <param name="Flown">The last step flies a cover (the connected flight is up): the body's switch keeps only its fades, so the cover is the one
/// thing that travels.</param>
public sealed record LocalOpened(LocalPivot From, string Folder, string Root, string Album, bool Back, bool Flown)
{
    /// <summary>The flight key of a folder tile and of that folder's header cover.</summary>
    public static string FolderTile(string folderKey) => "local:folder:" + folderKey;

    /// <summary>The flight key of an album card and of the header of the album's folder opened from it.</summary>
    public static string AlbumTile(string albumKey) => "local:album:" + albumKey;

    /// <summary>Is the detail open over <paramref name="pivot"/>?</summary>
    public bool IsOpenOver(LocalPivot pivot) => From == pivot && Folder.Length > 0;

    /// <summary>The header cover's flight key: the album card's at the root an album opened, else the folder tile's.</summary>
    public string HeaderTile => Album.Length > 0 && Folder == Root ? AlbumTile(Album) : FolderTile(Folder);
}

/// <summary>The verbs of the page, built once by the core host.</summary>
public sealed record LocalCollectionVerbs
{
    public required Action PlayAll { get; init; }
    public required Action Shuffle { get; init; }
    /// <summary>Opens Settings › Storage › Music on this PC (adds a folder there).</summary>
    public required Action AddMusic { get; init; }
    public required Action<string> PlayAlbum { get; init; }
    public required Action<string> OpenAlbum { get; init; }
    public required Action<string> ShuffleArtist { get; init; }
    public required Action<int> ShuffleDecade { get; init; }
    public required Action<string> PlayFolder { get; init; }
    public required Action<string> ShuffleFolder { get; init; }
    public required Action<long> PlayFile { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.LocalCollectionPage"/>.</summary>
public sealed record LocalCollectionPageProps
{
    public required IReadSignal<LocalCollectionView> View { get; init; }
    public required IReadSignal<LocalPivot> Pivot { get; init; }
    public required Action<LocalPivot> SetPivot { get; init; }
    public required IReadSignal<LocalAlbumSort> Sort { get; init; }
    public required Action<LocalAlbumSort> SetSort { get; init; }
    /// <summary>The Artists list's order (remembered).</summary>
    public required IReadSignal<LocalArtistSort> ArtistSort { get; init; }
    public required Action<LocalArtistSort> SetArtistSort { get; init; }
    // A writable cell travels as `Func<Signal<T>>` (WVTH006, as the library's props carry theirs).
    /// <summary>How Albums and Artists list their items, the library's view code (remembered; the theme's view toggle writes it): 0 compact list,
    /// 1 list, 2 compact grid, 3 grid (<see cref="LibraryViewArgs.View"/>).</summary>
    public required Func<FluentGpu.Signals.Signal<int>> ListView { get; init; }
    /// <summary>The grid's size, the library's code (remembered): 0 small, 1 medium, 2 large (<see cref="LibraryViewArgs.Size"/>).</summary>
    public required Func<FluentGpu.Signals.Signal<int>> ListSize { get; init; }
    /// <summary>The Albums and Artists filter's text (the session's; "" shows every item).</summary>
    public required Func<FluentGpu.Signals.Signal<string>> Filter { get; init; }
    /// <summary>The detail over a pivot (<see cref="LocalOpened"/>; open while its <c>Folder</c> is not ""), or null: every pivot shows its list (the Folders
    /// browser its music folders).</summary>
    public required IReadSignal<LocalOpened?> Opened { get; init; }
    /// <summary>Open a folder from a tile (a step in, the tile's cover flying into the header) or a breadcrumb (an ancestor: a step out); "" returns to
    /// the pivot's list.</summary>
    public required Action<string> OpenFolder { get; init; }
    /// <summary>The Songs pivot: the existing track table over the Local Files list, unchanged.</summary>
    public required Func<Element> Songs { get; init; }
    public required LocalCollectionVerbs Verbs { get; init; }
    /// <summary>The body with no music folder (the shared Local Files onboarding, S4), or null while the library has folders. Tracked.</summary>
    public required Func<Element?> Onboarding { get; init; }
    /// <summary>A first scan is running (the hero carries <see cref="ScanLine"/> instead of the activity line). Tracked.</summary>
    public required Func<bool> FirstScan { get; init; }
    /// <summary>The first scan's line and bar (the shared `LocalFiles.Scan` view), at a width (NaN: its own) and on one line or two.</summary>
    public required Func<float, bool, Element> ScanLine { get; init; }
    /// <summary>Home's shelves in the user's order, each shown or hidden ("Edit home"; persisted by the core).</summary>
    public required IReadSignal<IReadOnlyList<LocalShelfSlot>> Shelves { get; init; }
    public required Action<LocalShelf, bool> ShowShelf { get; init; }
    /// <summary>Move a shelf up (-1) or down (+1).</summary>
    public required Action<LocalShelf, int> MoveShelf { get; init; }
    /// <summary>The page as a precise drop target (§3.15): the theme sets it on the page's root box. Local songs play and stay in Local Files, a
    /// folder becomes a music folder.</summary>
    public required FluentGpu.Foundation.DropTargetSpec Drop { get; init; }
    /// <summary>"Your files and Spotify" (the offer and the review); null: the host offers none of it.</summary>
    public LocalLinkPageProps? Link { get; init; }
    /// <summary>"On this PC".</summary>
    public required string Title { get; init; }
    public required string RouteKey { get; init; }
}
