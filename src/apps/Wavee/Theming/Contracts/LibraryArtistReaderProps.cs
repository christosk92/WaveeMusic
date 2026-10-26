// ── Theming/Contracts/LibraryArtistReaderProps.cs — the props of `Library.ArtistReader` (themes plan 6.0, T10c1) ───────────────────────────
//
// The library's artists column: one scroller whose flat index space is the artist band (0), the pinned sub-rail (1), one block per release (`Prefix + b`)
// and the tail, with a cover spine beside it. `Artist.Reader` (`[CoreHost]`, `Entities\Artist.Reader.cs`) keeps what decides: the props channel, the
// shape memo (`Artist.ReaderShape.Build`: the block order, the catalogue gate, the extents), the demand, the readiness of each block (`BlockCell`: its
// `Loadable`, its liked rows, its verdict), the measured-extent corrections, the scroll spy, the mount key, the mounted list's layout and options, the two
// width arms and every verb. The structure's view draws the band, the name column, the rails, the spine, the block faces and the tail.
//
// The slots of the bound list are COMPONENTS the view owns, and they read the core's live state through `IArtistReaderHost` (a stable object, one per
// reader) exactly as the old nested components read the owner: a read inside a slot's render subscribes THAT slot, never the page.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.LibraryArtistReader"/>. The host builds one per render it makes (the old reader rebuilt its lane on every render),
/// so it never compares equal to another instance. The layout and options are the mount's: the host rebuilds them only when the list's mount key moves.</summary>
public sealed record LibraryArtistReaderProps
{
    /// <summary>The reader's live state and verbs (the same object for the reader's whole life).</summary>
    public required IArtistReaderHost Host { get; init; }

    /// <summary>The host was mounted without props (unreachable after mount): the view draws nothing.</summary>
    public bool Vacant { get; init; }

    /// <summary>The mounted list's remount key (<c>ReaderMountPolicy.MountKey</c>): the view keys the list box "list:" + it.</summary>
    public string ListKey { get; init; } = "";

    /// <summary>The flat item count this render (prefix + blocks + the tail): the bound list's initial count; later changes ride <see cref="Options"/>.CountSignal.</summary>
    public int ItemCount { get; init; }

    /// <summary>How many blocks there are this render (the spine's initial count).</summary>
    public int BlockCount { get; init; }

    /// <summary>The analytic extent layout of THIS mount (its table must not carry another artist's rows).</summary>
    public RepeatLayout Layout { get; init; }

    /// <summary>The list's options of THIS mount: the controller, the count signal, the persistent prefix, the scroll key and the pinned-rail clip.</summary>
    public ListOptions? Options { get; init; }

    /// <summary>The spine stands beside the list (false in the medium arm, where the band stacks and the spine is gone, not hidden).</summary>
    public bool Spine { get; init; }

    /// <summary>The page's persisted scope word (0 in your library, 1 all releases): the sub-rail's signal instance (a writable cell travels as a Func, WVTH006).</summary>
    public Func<Signal<int>>? Scope { get; init; }

    /// <summary>The page's persisted reader sort: the sub-rail's signal instance (a writable cell travels as a Func, WVTH006).</summary>
    public Func<Signal<int>>? Sort { get; init; }

    /// <summary>The spine's own viewport controller (the follow effect glides it).</summary>
    public ItemsViewController? SpineController { get; init; }

    /// <summary>The spine's count (a chained memo over the shape).</summary>
    public Memo<int>? SpineCount { get; init; }

    /// <summary>The lane's bounds callback (feeds the two width arms).</summary>
    public Action<RectF>? OnBounds { get; init; }

    /// <summary>The truthful mount signal, wired on the keyed list box.</summary>
    public Action<NodeHandle>? OnListMounted { get; init; }

    /// <summary>Equal only to itself: the host rebuilds the record on every render and the view re-runs exactly when the host did.</summary>
    public bool Equals(LibraryArtistReaderProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>What the band paints (read inside the band slot's render, which subscribes the scope epoch, the artist table, the props and the shape).</summary>
public readonly record struct ArtistReaderBand(Artist Artist, bool Named, Artist.ReaderShapeKey Shape, bool Stacked);

/// <summary>The facts of the list's last item (<see cref="Kind"/>: 0 air, 1 a line, 2 the "nothing here yet" vacancy). The strings are composed by the view.</summary>
/// <param name="Kind">0 air, 1 the line, 2 the vacancy.</param>
/// <param name="Height">The height the extent table was promised for this kind (the root declares it).</param>
/// <param name="Gated">The catalogue gate is still closed (the line reads "Fetching releases").</param>
/// <param name="Partial">A facet is Partial (the line reads "Fetching N more releases").</param>
/// <param name="Missing">How many releases the catalogue lists beyond the blocks.</param>
/// <param name="Name">The artist's name ("" for the air).</param>
/// <param name="ShowAll">The vacancy's action: switch the scope to all releases.</param>
public readonly record struct ArtistReaderTail(int Kind, float Height, bool Gated, bool Partial, int Missing, string Name, Action ShowAll);

/// <summary>One release of <see cref="IArtistReaderHost.ReadReleases"/>: the album's slot and whether the album itself is in your library (false: only liked songs of it are).</summary>
public readonly record struct ArtistReaderRelease(int AlbumSlot, bool Saved);

/// <summary>The reader's live state and verbs, as the structure's view reads them.</summary>
public interface IArtistReaderHost
{
    /// <summary>Subscribing read of the band (scope epoch, artist table, props, shape, arm).</summary>
    ArtistReaderBand ReadBand();

    /// <summary>Subscribing read of block <paramref name="blockIndex"/> (the shape): its cell, or null for the tail.</summary>
    IArtistReaderBlock? ReadBlock(int blockIndex);

    /// <summary>Subscribing read of the tail (the shape).</summary>
    ArtistReaderTail ReadTail();

    /// <summary>The band's verbs: play the artist (toggle), shuffle it, open the artist page.</summary>
    Action PlayAll { get; }

    /// <inheritdoc cref="PlayAll"/>
    Action ShuffleAll { get; }

    /// <inheritdoc cref="PlayAll"/>
    Action GoArtist { get; }

    /// <summary>The band root's bounds callback (the chrome truthfulness check).</summary>
    Action<RectF> OnBandBounds { get; }

    /// <summary>The release total the scope rail's second word reads (a bound prop over the shape).</summary>
    Prop<int> ReleaseTotal { get; }

    /// <summary>The narrow arm, read inside the rail's bound text so a drag across the edge re-fires one text bind.</summary>
    bool NarrowNow();

    /// <summary>The spine's highlighted block (the scroll spy writes it).</summary>
    Signal<int> Current { get; }

    /// <summary>Block <paramref name="index"/>, or false past the end.</summary>
    bool TryBlockAt(int index, out Artist.ReaderBlock block);

    /// <summary>The per-dot memo's triple: the flat index, the album slot it resolves to, that album's row version.</summary>
    (int Index, int Slot, uint Ver) WatchDot(RowScope scope);

    /// <summary>Glide block <paramref name="blockIndex"/> to the top (ignored past the end).</summary>
    void BringToTop(int blockIndex);

    /// <summary>Subscribing read of the releases this artist shows, in block order (the shape, the scope epoch and the album table): a structure that draws the artist as a hero, an
    /// albums grid and the picked album's songs (instead of the block list) reads them here. Contract addition after 1.33.</summary>
    IReadOnlyList<ArtistReaderRelease> ReadReleases();

    /// <summary>The album pane of <paramref name="albumSlot"/> (the host's own `Album.Pane`: its title, verbs and songs, demanding its own model). Contract addition after 1.33.</summary>
    Element AlbumPane(int albumSlot);
}

/// <summary>One block's cell: its readiness, its record, its rows and its verbs. The core refreshes it from an effect; the view only reads.</summary>
public interface IArtistReaderBlock
{
    /// <summary>The album's slot.</summary>
    int AlbumSlot { get; }

    /// <summary>The block record this cell paints (rows, failed, liked-only).</summary>
    Artist.ReaderBlock Block { get; }

    /// <summary>The block's readiness: the region cross-dissolves when it flips (Ready also stands for a failed tracklist, which the block paints itself).</summary>
    Loadable<int> Load { get; }

    /// <summary>The reader's narrow arm NOW (cover 88 and the stacked head), read tracked: a flip re-arms the block in place, so a view builds it in this arm and
    /// caches its face in <see cref="Face"/>, which the cell hands back only for the arm it was built in (audit FU2).</summary>
    bool Narrow { get; }

    /// <summary>The track rows of the arm (their height, and whether they run under the cover): the same value the reader's extents read, so the
    /// block's measure is its analytic extent by identity. Contract 1.80 (audit FU).</summary>
    Artist.ReaderRows Rows { get; }

    /// <summary>A liked-only block's own rows, in liked-edge order.</summary>
    ReadOnlySpan<int> LikedRows();

    /// <summary>The head's meta line ("3 songs · 10 min", or "2 liked songs").</summary>
    string Meta();

    /// <summary>The album's "…" menu.</summary>
    Func<ContextMenuModel?> Menu { get; }

    /// <summary>The head root's bounds callback (the chrome truthfulness check).</summary>
    Action<RectF> OnHeadBounds { get; }

    /// <summary>Open the album page.</summary>
    Action Open { get; }

    /// <summary>Play the album (toggle).</summary>
    Action PlayAlbum { get; }

    /// <summary>Re-ask the failed tracklist.</summary>
    Action Retry { get; }

    /// <summary>Play the album from <paramref name="trackSlot"/>.</summary>
    void PlayTrack(int trackSlot);

    /// <summary>The finished region element, built once by the view and cached here so a re-realize allocates nothing (cleared with the cell when the artist changes).</summary>
    Element? Face { get; set; }
}
