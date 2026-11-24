// ── Theming/Contracts/LocalCollectionProps.cs — what the "On this PC" page hands a theme (#240 P2, boards 18-21) ──────────────
//
// The core host (`Entities/LocalCollection.Page.cs`) folds the local index into ONE immutable view (`LocalCollectionView`,
// computed off the UI thread by `Playback/LocalCollection.cs` on every `LocalLibrary.Epoch` / `LocalArt.Epoch` move) and hands the
// active theme this props record: the view as a live signal, the remembered pivot, the album sort and the open folder as live
// signals, and every verb as a delegate built once. Albums and artists here are NAMES from the files' tags, never Spotify entities.
// Covers are `file:///` uris from `LocalArt` (null: not extracted yet, or none). Quality is a short label ("FLAC", "MP3 320") or "".
//
// Contract additions (declared, not numbered): `ViewKeys.LocalCollectionPage` and every type in this file.

using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The page's views, in the order the pivot shows them (the page remembers the last one).</summary>
public enum LocalPivot : byte { Home, Albums, Artists, Songs, Folders, Insights }

/// <summary>The Albums wall's orders.</summary>
public enum LocalAlbumSort : byte { Added, Artist, Title, Year }

/// <summary>The library in one word (`LocalLibrary.StateWord`).</summary>
public enum LocalLibraryPhase : byte { None, Scanning, Ready, Offline }

/// <summary>One music folder's state, for its chip.</summary>
public enum LocalChipState : byte { Ok, Scanning, Offline, NoAccess, Waiting }

/// <summary>When an album arrived, for the Recently added shelf's groups.</summary>
public enum LocalAddedWhen : byte { Today, ThisWeek, ThisMonth, Earlier }

/// <summary>One album built from the tags: its (album artist or first artist, album) pair.</summary>
/// <param name="Key">Stable per (artist, album): a node key and the verbs' argument.</param>
/// <param name="AddedTicks">UTC ticks of the newest file's write time (the index has no first-seen time).</param>
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
/// <param name="Mosaic">Up to four album covers from inside it (recursive), when it has no cover of its own.</param>
public sealed record LocalFolderNode(string Key, string Name, string? ParentKey, int Songs, long DurationMs, string Quality, string? Cover,
    IReadOnlyList<string> Mosaic, IReadOnlyList<string> Children, IReadOnlyList<LocalSongItem> Files, long DurationHereMs);

/// <summary>One group of the Recently added shelf.</summary>
public sealed record LocalShelfGroup(LocalAddedWhen When, IReadOnlyList<LocalAlbumCard> Albums);

/// <summary>One decade of "Shuffle by decade" (from the year tags).</summary>
public sealed record LocalDecade(int Decade, int Albums, IReadOnlyList<string> Covers);

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
    public IReadOnlyList<LocalAlbumCard> Lossless { get; init; } = [];
    public int LosslessSongs { get; init; }
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
    /// <summary>The Folders browser's open folder ("" = the music folders themselves).</summary>
    public required IReadSignal<string> Folder { get; init; }
    public required Action<string> OpenFolder { get; init; }
    /// <summary>The Songs pivot: the existing track table over the Local Files list, unchanged.</summary>
    public required Func<Element> Songs { get; init; }
    public required LocalCollectionVerbs Verbs { get; init; }
    /// <summary>"On this PC".</summary>
    public required string Title { get; init; }
    public required string RouteKey { get; init; }
}
