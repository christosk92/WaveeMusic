// ── Theming/Contracts/LibraryPageProps.cs — the props of the library page (themes plan 6.0, T10c1) ──────────────────────────────
//
// `User.LibraryPage` (a `[CoreHost]`) keeps everything that decides: the persisted per-kind signals, the navigator's shape memo (filter, sort, the letter
// groups, the a-z gate, the mount identity), the bound item source, the selection and its persistence, the demands, the search matcher and its selection
// commit, the collapse state and the crumbs. It hands the view the VALUES and VERBS it draws, once per render it makes. Every member is read-only (WVTH006).
//
// The records compare by reference: the host rebuilds one on every render it makes (that is when the page tree used to be rebuilt), and a data-equal record
// that carried newer closures would keep the old ones.

using System.Runtime.CompilerServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.LibraryPage"/>.</summary>
public sealed record LibraryPageProps
{
    // ── what the page is ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's name in the masthead's words ("Albums", "Artists", "Podcasts", "Audiobooks"): the toolbar's title.</summary>
    public required string Title { get; init; }

    /// <summary>The entity the page lists: Album, Artist or Show (a Podcasts or an Audiobooks page).</summary>
    public required EntityKind Entity { get; init; }

    /// <summary>The Podcasts page specifically: it alone owns the Followed shows / Your Episodes word switch.</summary>
    public required bool IsPodcasts { get; init; }

    // ── which arm the page draws ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The columns have become a breadcrumb drill-in (the structure's layout ladder over <see cref="SetPageWidth"/>).</summary>
    public required bool Collapsed { get; init; }

    /// <summary>Podcasts with the Your Episodes word selected: the whole body is the saved-episodes page under the toolbar.</summary>
    public required bool SavedEpisodesMode { get; init; }

    /// <summary>A filter is typed on a kind that has a search presentation (not shows): the left column and the panes show library hits.</summary>
    public required bool FullSearch { get; init; }

    /// <summary>The search answer is awaited and there is nothing worth keeping on screen: the hit columns shimmer.</summary>
    public required bool Shimmer { get; init; }

    /// <summary>The query the hits answer is older than the filter text: the facet counts fade while a newer answer is coming.</summary>
    public required bool Awaiting { get; init; }

    /// <summary>The right rail is open: the search albums column keeps a lower floor.</summary>
    public required bool RailOpen { get; init; }

    /// <summary>A row is selected: the detail pane shows it (otherwise the "pick one" placeholder).</summary>
    public required bool HasSelection { get; init; }

    /// <summary>The collapsed layout's rung (0 the navigator, 1 the pane or reader, 2 the artists search's track hits), already clamped.</summary>
    public required int Depth { get; init; }

    /// <summary>The collapsed layout's breadcrumb, root first.</summary>
    public required IReadOnlyList<string> Crumbs { get; init; }

    // ── the page's measured width → collapse ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's own measured width, reported by the structure's view on every bounds change (value-gated by the host). The host feeds it to the structure's
    /// layout ladder (<see cref="Wavee.Theming.LibraryLadders.Layout"/>) through <c>UseTier</c>, which answers <see cref="Collapsed"/>: the first frame is already in its tier
    /// and a crossing commits on the first non-resized frame.</summary>
    public required Action<float> SetPageWidth { get; init; }

    /// <summary>The kind chips of the one-column head (Albums, Artists, Podcasts, Audiobooks, Liked Songs), in order: each opens its page.</summary>
    public required Func<IReadOnlyList<LibraryKindChip>> Kinds { get; init; }

    /// <summary>A crumb was picked (collapsed layout): the rung to show.</summary>
    public required Action<int> PickDepth { get; init; }

    // ── persisted per-kind state (a writable cell travels as `Func<Signal<T>>`: WVTH006 keeps a writable `Signal<T>` out of a props member; the control binds the cell once) ──

    /// <summary>The navigator column's width; the grip writes it and commits it on release.</summary>
    public required Func<Signal<float>> LeftW { get; init; }

    /// <summary>The search layout's middle column width.</summary>
    public required Func<Signal<float>> MidW { get; init; }

    /// <summary>The sort code (<see cref="LibraryNavSort"/>); the rail writes it.</summary>
    public required Func<Signal<int>> Sort { get; init; }

    /// <summary>Descending order; a re-tap of the active word flips it.</summary>
    public required Func<Signal<bool>> Desc { get; init; }

    /// <summary>The view code (0 compact list, 1 list, 2 compact grid, 3 grid).</summary>
    public required Func<Signal<int>> View { get; init; }

    /// <summary>The grid size (0 S, 1 M, 2 L).</summary>
    public required Func<Signal<int>> Size { get; init; }

    /// <summary>The filter text (not persisted).</summary>
    public required Func<Signal<string>> Filter { get; init; }

    /// <summary>Podcasts' mode word (0 Followed shows, 1 Your Episodes).</summary>
    public required Func<Signal<int>> PodcastsMode { get; init; }

    /// <summary>Commit <see cref="LeftW"/> to the store (a grip release).</summary>
    public required Action CommitLeft { get; init; }

    /// <summary>Commit <see cref="MidW"/> to the store (a grip release).</summary>
    public required Action CommitMid { get; init; }

    /// <summary>The toolbar's count as ONE bound text: a row landing re-fires this property and nothing else.</summary>
    public required Prop<string> CountText { get; init; }

    /// <summary>The same count as a number (a bound read), for a structure that words it through a plural ("1 album", "18 albums"). Contract addition after 1.22.</summary>
    public Prop<int> Count { get; init; }

    // ── slots the core mounts ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The selected row's pane: the album pane, the show pane or the artists' reader, mounted without a key so a selection re-skins it in place.</summary>
    public required Func<Element> Pane { get; init; }

    /// <summary>The albums' artist grouping (null unless the structure asked for it through <see cref="Wavee.Theming.LibraryTierPolicy.GroupAlbumsByArtist"/>, this is the albums
    /// page and the page is split): the structure draws the "all artists" column from it, and the navigator already shows only the picked artist's albums.</summary>
    public LibraryGroupsProps? Groups { get; init; }

    /// <summary>The Your Episodes page, keyed by the scope epoch.</summary>
    public required Func<Element> SavedEpisodes { get; init; }

    /// <summary>The skeleton group the navigator's region and the search columns settle in together.</summary>
    public required object SkelGroup { get; init; }

    /// <summary>The navigator (the bound list and its letters).</summary>
    public required LibraryNavigatorProps Navigator { get; init; }

    /// <summary>The hit model; null unless <see cref="FullSearch"/>.</summary>
    public LibrarySearchProps? Search { get; init; }

    /// <inheritdoc />
    public bool Equals(LibraryPageProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>What the navigator's mount needs, resolved by the host at the moment the region asks for its content: the shape's mount identity, the cached
/// layout for that identity and the options (selection, controller, scroll, typeahead, the letter clip) built once per mount key. A selection pre-write for
/// the fresh list has already happened.</summary>
public readonly record struct LibraryNavMountPlan(LibraryNavMount Mount, RepeatLayout Layout, ListOptions<LibraryNavItem> Options);

/// <summary>The navigator column's body: the loading state, the bound source, the mount and the letters' signals.</summary>
public sealed record LibraryNavigatorProps
{
    /// <summary>Pending while the relation (or the a-z titles) have not answered; Ready(count) after (0 is the region's empty arm).</summary>
    public required Loadable<int> Load { get; init; }

    /// <summary>The bound item source over the shape memo (rows, and letter headers when a-z is live).</summary>
    public required BoundItemsSource<LibraryNavItem> Items { get; init; }

    /// <summary>The current mount (the host's read of the shape memo, the selection pre-write and the cached layout and options). Called inside the region's
    /// content so the memo's moves re-run it.</summary>
    public required Func<LibraryNavMountPlan> Mount { get; init; }

    /// <summary>The truthful mount signal, wired once on the keyed list box: it fires only for a real mount.</summary>
    public required Action<NodeHandle> OnMounted { get; init; }

    /// <summary>The sticky letter's glyph index (-1: none).</summary>
    public required IReadSignal<int> StickyLetter { get; init; }

    /// <summary>The sticky letter's upward push by the header arriving under it.</summary>
    public required IReadSignal<float> StickyPush { get; init; }

    /// <summary>The a-z strip's mask of present letters.</summary>
    public required IReadSignal<uint> Present { get; init; }

    /// <summary>A strip tap (unanimated; an absent letter resolves to nothing).</summary>
    public required Action<int> Jump { get; init; }
}

/// <summary>One artist or album name hit (a navigator-shaped row): which row, where the match sits in its title, the subtitle and the "why" eyebrow.</summary>
public sealed record LibraryHitRow(int Slot, int MatchStart, int MatchLen, string Subtitle, string? Eyebrow);

/// <summary>One track hit: the track row, the match in its title and the play verb (the album from this track, in place).</summary>
public sealed record LibraryTrackHitRow(int TrackSlot, int MatchStart, int MatchLen, Action Play);

/// <summary>The library search's hits and selection. The lists are built lazily by the host, so a collapsed rung builds only the column it shows.</summary>
public sealed record LibrarySearchProps
{
    /// <summary>The artists view's left column.</summary>
    public required Func<IReadOnlyList<LibraryHitRow>> ArtistHits { get; init; }

    /// <summary>The albums view's left column: explained hits (with the match eyebrow and the zero-hit message).</summary>
    public required Func<IReadOnlyList<LibraryHitRow>> AlbumHits { get; init; }

    /// <summary>The artists view's middle column: the selected artist's albums (no eyebrow, no message).</summary>
    public required Func<IReadOnlyList<LibraryHitRow>> ArtistAlbumHits { get; init; }

    /// <summary>The selected album's track hits (the albums view's detail, the artists view's right column).</summary>
    public required Func<IReadOnlyList<LibraryTrackHitRow>> TrackHits { get; init; }

    /// <summary>The selected artist's slot (0: none).</summary>
    public required int SelectedArtist { get; init; }

    /// <summary>The selected album's slot (0: none).</summary>
    public required int SelectedAlbum { get; init; }

    /// <summary>An artist hit was picked (selects in place).</summary>
    public required Action<int> SelectArtist { get; init; }

    /// <summary>An album hit was picked (selects in place).</summary>
    public required Action<int> SelectAlbum { get; init; }
}

/// <summary>One kind chip of the one-column library head: the kind's page title, whether it is the page on screen and the verb that opens it.</summary>
public sealed record LibraryKindChip(string Title, bool Selected, Action Open);

/// <summary>The albums page's artist groups: what the structure's "all artists" column draws and the one verb that picks. <see cref="Read"/> is a reactive read (call it from the
/// view's own render: it follows the library, the artists' names and the pick); <see cref="Pick"/> takes a <see cref="LibraryGroupRow.Key"/>, or "" for every artist.</summary>
public sealed record LibraryGroupsProps(Func<LibraryGroupSet> Read, Action<string> Pick)
{
    /// <summary>The picked artist's verbs (no-ops while every artist is shown): play (toggle), shuffle, open the artist's page. Contract addition after 1.33.</summary>
    public Action Play { get; init; } = static () => { };

    /// <inheritdoc cref="Play"/>
    public Action Shuffle { get; init; } = static () => { };

    /// <inheritdoc cref="Play"/>
    public Action Open { get; init; } = static () => { };
}

/// <summary>One read of the groups: the artists with a saved album (a-z), the picked one's key ("" = every artist; a pick the library no longer holds reads as ""), its name and its
/// album count (the whole library's count when none is picked).</summary>
public sealed record LibraryGroupSet(IReadOnlyList<LibraryGroupRow> Rows, string Selected, string SelectedName, int SelectedAlbums, int TotalAlbums)
{
    /// <summary>The picked artist's picture (an art url; "" for none or while every artist is shown) and how many songs of theirs the library holds. Contract addition after 1.33.</summary>
    public string SelectedArt { get; init; } = "";

    /// <inheritdoc cref="SelectedArt"/>
    public int SelectedSongs { get; init; }
}

/// <summary>An artist of the groups column: the route key, the name, how many of the library's albums are billed first to them, and the letter that heads their run ("" when the
/// previous row shares it; "#" for a name that does not start with a letter).</summary>
public readonly record struct LibraryGroupRow(string Key, string Name, int Albums, string Letter);
