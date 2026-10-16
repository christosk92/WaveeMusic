// ── Entities/User.Page.Library.cs ──────────────────────────────────────────────────────────────────────────────────
// the albums / artists / podcasts / audiobooks MASTER-DETAIL page's CORE HOST: LibraryPageFor + LibraryPage (the two-rung browser's
// persisted per-kind signals, the shape memo, the letter groups, the selection, the collapse state, the search mode) over the structure's
// `Pages.Library` view. The page's PICTURE is Classic's (T10c1: `Themes\Classic\Structure\Views\Pages\ClassicLibraryPage*.cs`, props in
// `Theming\Contracts\LibraryPageProps.cs`); the panes it mounts are `Album.Pane` (Entities/Album.Pane.cs), `Show.Pane` (Entities/Show.Pane.cs)
// and the artists' `Artist.Reader` (Entities/Artist.Reader.cs), each a core host over its own view key, and `SavedEpisodesReader`
// (Entities/User.SavedEpisodes.cs): this file mounts them, it does not build them.
//
// Role: CORE host
// Owner: O
// Wave: 5 (reworked 2026-09-17: library-rework-implementation.md §5.3, wave L2; 2026-09-18: A1-A4 of
//       library-stabilization-plan.md — the navigator's remount key moved off an inert single-child-slot root onto a
//       keyed CHILD, one mint (`LibraryNavMount.For`) now owns everything frozen at mount, and the a–z gate waits for
//       every listed row's title before the list sorts by one)
// Spec: ch 15 §0-§9 (0.2.9 Features/Library/LibraryPage.cs) + library-rework-implementation.md §2 (W1, W2, W3, W6, W7),
//       §3.1/§3.2 (the two component trees), §4 (the interaction contract), §5.3, §5.4, §6, §7;
//       library-stabilization-plan.md §3.3 (the target view-switching design), §5 wave 1 (A1-A4).
//
// ── THE SHAPE (the host hands `Pages.Library` one props record per render; Classic draws this tree) ─────────────────────────────────
//
//  LibraryPage (keyed "library:"+route)            ─ seeds every persisted signal in its CONSTRUCTOR (frame one = saved)
//  └─ Pages.Library → root BoxEl (OnBoundsChanged → `SetPageWidth` → the structure's layout ladder → `_layoutTier`)
//     ├─ WIDE "lib:wide": NavPanel "lib:nav"[Toolbar · ListBody | LeftSearchBody] │ "lib:grip" ColumnGrip(LeftW) │ right
//     │        Toolbar = PageHero + the bound count · the word rail + the view toggle · the filter box
//     │        right   = the pane (`PaneFor`): Album.Pane (albums) | Show.Pane (podcasts, audiobooks)
//     │                  the reader (artists, 280 + the rest — no MidW, no third pane)
//     │                  the two SEARCH column shapes (unchanged, still MidW + "lib:midgrip")
//     └─ COLLAPSED "lib:collapsed": CrumbBar "lib:crumbs" · depth 0 (toolbar + list) | depth 1 (the pane / the reader) —
//                   BROWSE has no depth 2 any more (the reader is one pane); SEARCH keeps three rungs in the artists
//                   view (2 = the track hits)
//
// EVERY BRANCH ROOT ON THAT DIAGRAM IS KEYED, and the collapse arm is why (the full note is in `ClassicLibraryPage.cs`): the reconciler
// pairs two unkeyed branch roots at the same ordinal into ONE node that survives the swap, and the nav column's `Width` is the persisted
// `LeftW` SIGNAL, a bound channel whose wiring runs only at MOUNT.
//
//  ListBody = Skel.Region(eight Skeletons.ListItemRow  ⇄  NavHost, StaggerRows)         ← the shimmer CROSS-DISSOLVES
//             NavHost (unkeyed, single-child slot — see REMOUNT VS REFRESH below)
//             └─ BoxEl "nav:body" ZStack: BoxEl[Mount.Key] ItemsView.CreateBound<LibraryNavItem>(_items, template, …)
//                                          + ClassicLetterKit.StickyLetter "nav:sticky" (only while Mount.Lettered)
//                        │ ClassicLetterKit.JumpStrip "nav:strip" on the right edge (only while Mount.Strip, i.e. a–z in ANY view)
//             (drawn by `ClassicLibraryPage.Nav.cs` over `ResolveMount()`: the host resolves the mount, the view draws it)
//
// THE NAVIGATOR IS A MEMO, NOT A RENDER. `ComputeShape` (a UseComputed) filters the SOURCE into pooled buffers, sorts
// them with the reusable `LibraryNavSorter`, builds the letter groups (`LibraryLetters`) when the sort is a–z, folds one
// version per row plus whether every one of them KNOWS its title (`FillRowVersions`), mints the mount identity through
// `LibraryNavMount.For` and publishes a value
// `NavShape(Count, FlatCount, OrderKey, FactsKey, RowsKey, LettersKey, Answered, TitlesKnown, Mount)`; the bound list
// reads its rows out of the sorted buffer. Nothing is copied into a record array, so a selection click allocates no row
// data (ch 15 §9's "per-render array churn" trap).
//
// REMOUNT VS REFRESH — the 0.2.10 defect this page was reworked around, and the regression (library-stabilization-plan
// RC1) that a `Skel.Region` wrap reintroduced on top of it. A `Key` is honored ONLY on a CHILD the reconciler pairs
// through `ReconcileChildren`; the ROOT of a single-child slot (a `Skel.Region` content thunk, a `Show` body, a
// component's own root) is paired by `Element.ElementTypeId` alone and its `Key` is INERT — so the view's `NavList` root is an
// UNKEYED, always-the-same-shape host (`NavHost`, "the region's single child: unkeyed, shape-stable" below), and the
// list that actually needs to remount is a KEYED CHILD of it, one level down. That key — `LibraryNavMount.Key`, minted
// in ONE place (`LibraryNavMount.For`, `ComputeShape` calls it, everyone else reads `shape.Mount`) — is `view:size:
// LettersKey` (size folded to 0 outside a grid) and NOTHING else: those are the only facts the frozen ItemsView shape
// (its template, its layout, its options) depends on. A row's FACTS landing — a title, a cover, a year, an artist's
// counts — moves `FactsKey`/`RowsKey`, which republishes the memo, which makes every mounted slot re-resolve its
// equality-gated item and re-fire the binds of the rows that really changed. `OrderKey` moving — a sort, a play, a
// facts-driven re-rank — is REFRESH too: the bound source re-resolves every realized slot in the SAME mounted list, no
// remount, which is what makes a six-changes-in-600ms cold start harmless instead of a remount storm. Keying the list on
// any of `FactsKey`/`OrderKey` (0.2.10, then the rework's own `NavKey`) is exactly the defect this rule exists to name.
// The truthful `library.nav.mount` line fires only from the keyed child's own `OnRealized` — a REAL mount, not a string
// compare — and names which half of the mount identity moved (`first` | `view` | `size` | `letters`); a `library.nav.
// reorder` line fires from `SyncNav` when only `OrderKey` moved. The measured layout is cached per mount key
// (`NavMountCache`, resolved beside the key itself) for the same reason `RepeatLayout.Extents` is stateful: a fresh one
// per render throws every measured row away.
//
// THE FLAT PROJECTION. With letters on, the item space INTERLEAVES 28-DIP header items with the rows: a header is the
// item `Slot = -(letter + 1)` (so `LetterText(-Slot - 1)` is its glyph), `ContentType 1` keeps its recycle pool apart
// from the rows', and `IsItemEnabledTyped` skips it for arrows/typeahead. `ItemAt`/`IndexOfSlot`/`RowOfFlat` are the
// only places that know both index spaces.
//
// SELECTION NEVER REMOUNTS AND NEVER NAVIGATES. A pick writes `SelectedKey` (a persisted "album:"/"artist:"/"show:"
// route key); the panes re-skin in place off a RE-PUSHED props record. `_syncingSel` guards the page's OWN re-sync from
// re-entering the user-pick path (two shipped 0.2.9 fixes). Row 0 is adopted whenever the key is empty or absent from a
// non-empty shown set.
//
// DATA: the relation (Edges.SavedAlbums / FollowedArtists / SavedShows off User.Me) is demanded ONCE per scope when it
// is Unknown, the rows' identity from an auto-tracked effect; the panes demand their whole model on mount. No page reads
// Platform.Args.Fake. Search is the cache-only matcher (`User.SearchLibrary`) over the resident tables, debounced 180 ms.
//
// THE ARTISTS TAB IS NOT THE FOLLOWED RELATION (the 2026-09-18 correction, User.cs §11). Its source is
// `LibraryArtistsOf` — followed ∪ the billed artists of your saved albums ∪ the credited artists of your liked songs —
// so `DemandArtistRows` asks for the identity of every saved ALBUM and every liked TRACK as well as of the artists
// themselves: those two asks are what fill the unpersisted AlbumArtists / TrackArtists edges the §11 helpers read, and
// without them a cold start shows a followed-only list whose every count is 0. The page demands its WHOLE model and the
// query layer batches it (300 per POST) — there are no page-side fetch windows here.
//
// WHERE A NON-FOLLOWED ARTIST LANDS UNDER EACH WORD. The artists rail is three words (`LibraryWordRail`) and none of
// them is "added", so no artist ever needs a FollowedArtists `AddedAt` and a row without one is never a hole:
//   · recents — played newest-first off `Shell.PlayLog` (a uri read: following has nothing to do with it), then the
//     never-played block in SOURCE order, which is `LibraryArtistsOf`'s: followed first, then the saved-album artists,
//     then the liked-song ones. Followed rows therefore stay at the top of that block, which is the point of the
//     helper's ordering.
//   · a–z — the title, and nothing else: a letter band that skipped your non-followed artists would be a lie.
//   · albums — the library RELEASE count, descending, then title; an artist with one liked feature ranks below one with
//     six saved records, whether either is followed.
//
// RETIRED SETTINGS: `library.<kind>.album.desc` / `.album.view` / `.album.size` are no longer read or written (the
// discography grid they steered is deleted); a user's store.json may still carry them, so the strings are never reused
// (see the note in `Platform.Keys`). `library.<kind>.album.sort` IS still used: it is where `Artist.Reader`'s own sort
// persists (its old 0..4 values clamp inside the reader).

using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct User
{
    // ══ 1. THE FACTORY ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>RouteKind.LibraryAlbums / LibraryArtists / LibraryPodcasts / LibraryAudiobooks (A2 plan §3.6) → the
    /// master-detail browser. ONE component keyed by the route's name, so each kind keeps its own keep-alive instance
    /// (the kind freezes at mount).</summary>
    // MOUNT POINT (stage B contract)
    public static Element LibraryPageFor(in Shell.Route route)
    {
        var kind = route.Kind is Shell.RouteKind.LibraryArtists or Shell.RouteKind.LibraryPodcasts
            or Shell.RouteKind.LibraryAudiobooks ? route.Kind : Shell.RouteKind.LibraryAlbums;
        return Embed.Comp(() => new LibraryPage(kind)) with { Key = "library:" + Shell.NameOf(route) };
    }

    /// <summary>What the navigator shows, as a VALUE: <paramref name="OrderKey"/> and <paramref name="LettersKey"/> are
    /// REFRESH facts now (a bound-source re-resolve, never a remount — library-stabilization-plan RC3), the other two
    /// REFRESH keys (<paramref name="FactsKey"/>, <paramref name="RowsKey"/>), the two counts (rows / FLAT items),
    /// whether the relation has ANSWERED — the shimmer's only off-switch, so an empty library resolves to "empty"
    /// instead of shimmering forever (0.2.10's defect A, in miniature) — <paramref name="TitlesKnown"/> (A4: every
    /// listed row knows its title, the a–z gate's other half), and <paramref name="Mount"/>: the ONE remount identity
    /// (<see cref="LibraryNavMount"/>), minted once here by <c>LibraryNavMount.For</c> and read everywhere else off this
    /// record — nobody re-derives a key from <c>View.Peek()</c>.
    /// <para>The refresh keys are in the record because the bound source reads its items out of THIS memo: a landed fact
    /// has to move the shape or no slot re-resolves. They are NOT in <see cref="LibraryNavMount.Key"/> — the row's binds
    /// do the refreshing (<see cref="LibraryNavItem"/>'s version), and keying the list on them was the remount storm the
    /// user saw as a jumping scrollbar, hover ghosts and a selection that never lit. <paramref name="RowsKey"/> is the
    /// FNV over the rows' folded versions (<see cref="FillRowVersions"/>), which is what carries a fact that lives on
    /// another table: an album row's billed-artist NAME, an artist row's release/song counts.</para></summary>
    sealed record NavShape(int Count, int FlatCount, string OrderKey, string FactsKey, ulong RowsKey, ulong LettersKey,
                           bool Answered, bool TitlesKnown, LibraryNavMount Mount);

    /// <summary>Podcasts row-1 words (0 Followed shows · 1 Your Episodes, A2 plan §3.5): the standard toolbar's picker
    /// row IS the mode switch for this kind. Shared rather than per page instance so a navigation can open Your
    /// Episodes (<see cref="YourEpisodesRoute"/>).</summary>
    internal static readonly Signal<int> PodcastsMode = new(0);

    /// <summary>Where "Your Episodes" lives: the Podcasts library with its Your Episodes word selected. Selects the
    /// word and returns the route — the target of <c>Shell.Redirect</c> for Spotify's listen-later playlist.</summary>
    public static Shell.Route YourEpisodesRoute()
    {
        PodcastsMode.Value = 1;
        return new Shell.Route(Shell.RouteKind.LibraryPodcasts);
    }

    static string PrefixOf(EntityKind kind) => kind switch
    {
        EntityKind.Artist => SidebarPinId.ArtistPrefix,
        EntityKind.Show => SidebarPinId.ShowPrefix,
        _ => SidebarPinId.AlbumPrefix,
    };

    /// <summary>The persisted route key for a row ("album:spotify:album:…").</summary>
    static string KeyOf(EntityKind kind, int slot) => slot <= Table.None ? "" : PrefixOf(kind) + LibraryRows.IdOf(kind, slot).Text;

    /// <summary>A persisted key back to a handle slot (the factory allocates an empty row for an unseen uri, which is the
    /// state the pane renders as its skeleton). 0 for an empty or foreign key.</summary>
    static int SlotOfKey(EntityKind kind, string key)
    {
        string prefix = PrefixOf(kind);
        if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal)) return Table.None;
        var id = EntityId.Parse(key.AsSpan(prefix.Length));
        return id.IsValid && id.Kind == kind && Entities.TableFor(kind) is { } table ? table.Slot(id) : Table.None;
    }

    // ══ 2. THE PAGE ══════════════════════════════════════════════════════════════════════════════════════════════════

    [CoreHost]
    sealed class LibraryPage : Component
    {
        const float SearchDebounceMs = 180f;
        // The list row's TRUTHFUL outer extent — the 56 / 40 plate the prototype draws PLUS the bound chrome's 2+2
        // margin (`NavRowExtent`/`NavRowCompactExtent`, User.UI.cs), so 60 and 44 — now lives in ONE place,
        // `LibraryNavMount.For`'s `RowExtent`, alongside every other fact frozen at mount. A seed that counted the plate
        // alone drifted every letter offset by 4 DIP a row, which is why nothing here re-derives it.

        readonly Shell.RouteKind _route;
        readonly string _kind;                 // "albums" | "artists" | "podcasts" — the persisted key segment
        readonly EntityKind _entity;
        readonly LibraryEdgeKind _relation;
        readonly FetchEdge _fetch;

        // persisted per kind, seeded here so frame one paints the saved layout (§0 #4)
        internal readonly Signal<float> LeftW, MidW;
        internal readonly Signal<int> Sort, View, Size;
        internal readonly Signal<bool> Desc;
        internal readonly Signal<string> SelectedKey, AlbumKey;
        internal readonly Signal<string> Filter = new("");   // NOT persisted
        /// <summary>The artists reader's scope (0 in your library · 1 all releases) and its OWN sort (`Artist.ReaderSort`,
        /// persisted through the existing `library.<kind>.album.sort`; the old discography's 0..4 clamp in the reader).
        /// Both are Signal INSTANCES handed to the reader — never frozen data.</summary>
        internal readonly Signal<int> Scope, RSort;

        readonly SelectionModel _navSel = new();
        readonly ItemsViewController _navCtl = new();
        readonly ScrollOptions _navScroll;
        readonly Signal<int> _sArtist = new(0), _sAlbum = new(0);   // the search drill-down, by slot, apart from browse
        readonly object _skelGroup = new();
        /// <summary>The page's own measured width (written by the structure's view through <see cref="_setPageWidth"/>, value-gated) and the structure's LAYOUT tier over it
        /// (<see cref="Theming.LibraryLadders.Layout"/>: 0 the one-column drill-in). The tier is a <c>UseTier</c> signal: a first mount resolves in its own frame from the same-frame
        /// estimate, a crossing after it commits on the first non-resized frame and animates (themes plan 3.3).</summary>
        readonly Signal<float> _pageW = new(0f);
        IReadSignal<int>? _layoutTier;
        IReadSignal<Size2>? _viewport;
        readonly Func<float> _pageSeed;
        readonly Signal<int> _depth = new(0);
        bool _syncingSel, _fullSearch;
        /// <summary><c>SyncNav</c>'s own memory of what it last saw, to tell a REMOUNT (<see cref="LibraryNavMount.Key"/>
        /// moved: the fresh ItemsView starts with nothing highlighted) from an in-place REORDER (only `OrderKey` moved:
        /// the same mounted list, a different index for the selected item) from a pick that came off the list itself
        /// (already revealed, nothing to do — <see cref="_pickedFromList"/>).</summary>
        string? _lastNavKey, _syncOrderKey;
        /// <summary>Set by <see cref="OnNavSel"/>, consumed and cleared by <see cref="SyncNav"/> in the SAME effect run:
        /// a selection that came FROM the list is already revealed by the engine, so the re-assert effect must not also
        /// bring it into view (that would be the D8 "unanimated jump" for a row the user is already looking at).</summary>
        bool _pickedFromList;

        int[] _filtered = new int[64], _perm = new int[64], _sorted = new int[64], _counts = new int[64], _songs = new int[64];
        long[] _played = new long[64];
        uint[] _rowVer = new uint[64];
        /// <summary>The Podcasts/Audiobooks split's own scratch buffer (A2 plan §3.6) — reused across computes,
        /// never sized beyond what the last <c>SavedShows</c> edge read needed.</summary>
        int[] _showKindSrc = new int[64];
        /// <summary>The ARTISTS navigator's source (C1): followed ∪ the billed artists of your saved albums ∪ the
        /// credited artists of your liked songs (<see cref="User.LibraryArtistsOf"/>) — NOT the followed relation, which
        /// is only the first of the three groups. Two pooled buffers because the memo and the demand effect each read the
        /// set independently and neither may see the other's half-written buffer.</summary>
        int[] _artistSrc = new int[64], _artistDemand = new int[64];
        int _sortedCount;
        readonly LibraryNavSorter<LibraryRows> _sorter = new();
        readonly LibraryHits _hits = new();

        // the letters (W2). `_lettersBuilt` = the grouping exists (a–z at ANY view: the jump strip needs its mask and its
        // first-card index even in a grid); `_hasLetters` = the FLAT projection is live (a–z in a LIST view).
        readonly LibraryLetters _letters = new();
        readonly Signal<int> _stickyLetter = new(-1);
        readonly Signal<float> _stickyPush = new(0f);
        readonly Signal<uint> _present = new(0u);
        bool _lettersBuilt, _hasLetters;

        /// <summary>The navigator's loading state as a value the engine's skeleton boundary owns: Pending while the
        /// relation has not answered, Ready(count) after (0 ⇒ the boundary's own Empty arm). Flipped from an EFFECT,
        /// never from a render (rule 6).</summary>
        readonly Loadable<int> _navLoad = Loadable<int>.Pending(0);

        Memo<NavShape>? _shape;
        internal Memo<int>? SelectedSlot;   // NOT "Album": a member named like the type would shadow it
        Memo<bool>? _searchActive;
        IReadSignal<string>? _query;
        BoundItemsSource<LibraryNavItem>? _items;
        readonly ListOptions<LibraryNavItem> _navOptions, _navOptionsLettered;
        /// <summary>The list's mount identity for THIS render (<see cref="ComputeShape"/> mints it, everything else
        /// reads it): what <see cref="ExtentOf"/>/<see cref="RowExtent"/> ask for instead of peeking <c>View</c>, so a
        /// mounted list's measured-extent seed never disagrees with the layout it was actually built with.</summary>
        LibraryNavMount _mountSpec;
        /// <summary>The mount key <c>ClassicLibraryPage.NavList</c> has already pre-written a selection for (<see cref="PrepareMount"/>)
        /// — NOT the same field as <c>_lastNavKey</c>: this one guards the pre-write against running twice for the same
        /// key inside one render pass; that one is <c>SyncNav</c>'s own after-the-fact bookkeeping.</summary>
        string _pendingKey = "";
        /// <summary>The mount <see cref="OnNavMounted"/> last logged, kept ONLY to name which half of the identity moved
        /// (<c>reason=view|size|letters</c>) — never consulted for control flow.</summary>
        LibraryNavMount _lastMount;
        /// <summary>True once the FIRST navigator mount has fired — the mount log's <c>reason=first</c>.</summary>
        bool _navEverMounted;
        /// <summary>Whether any row this shape is trying to gate a–z on has a TERMINALLY FAILED title fetch
        /// (<see cref="FillRowVersions"/>) — folded alongside <see cref="NavShape.TitlesKnown"/> so the readiness gate
        /// (A4) gives up waiting instead of shimmering forever on a row that will never answer.</summary>
        bool _anyTitleFailed;
        /// <summary>One (<see cref="RepeatLayout"/>, <see cref="ListOptions{T}"/>) pair per mount KEY, built once and
        /// reused for as long as the key does not change (<see cref="NavMountCache"/>): the same statefulness reason the
        /// old per-key <c>_layout</c> cache existed — a fresh <c>RepeatLayout.Extents</c> per render throws every
        /// measured row away.</summary>
        readonly NavMountCache _mounts = new();

        readonly Func<NavShape> _computeShape;
        readonly Func<int> _resolveSelected;
        readonly Func<bool> _isSearching;
        readonly Func<string> _filterText;
        readonly Func<uint> _runSearch;
        readonly Action _demandEdge, _demandRows, _syncNav, _syncLetters, _syncLoad, _saveState, _resetDepth, _autoTop, _autoAlbum;
        readonly Action _onNavSel, _commitLeft, _commitMid, _restLeft;
        /// <summary>The user has set the navigator's width (contract 1.21): no tier re-rests it from here on.</summary>
        bool _leftCustomized;
        readonly Action<int> _pickDepth, _jump, _onAlsoBy;
        /// <summary>The truthful mount signal (A3): wired ONCE, frozen, on the keyed nav-list box — the engine calls it
        /// only when THAT box is actually realized (a real mount), never on a refresh. It reads <c>_shape.Peek()</c> for
        /// "which mount is this" rather than closing over a per-render value, which is what lets it stay a frozen field
        /// instead of a per-render allocation.</summary>
        readonly Action<NodeHandle> _onNavMounted;
        readonly Func<int, float> _extentOf;
        readonly Func<int, int> _contentType;
        /// <summary>The nav list's scroll handle (every nav mount re-binds the same one): the sticky letter watches it.</summary>
        readonly ScrollHandle _navHandle = new();
        string _scrollScope = "";   // the tab (Shell.PageScrollScope), composed onto the restore keys
        readonly Action _watchLetters;
        long _lettersKey = long.MinValue;
        /// <summary>The toolbar's count, as ONE bound property allocated once: a row landing re-fires this thunk and
        /// rewrites one text node instead of re-rendering the toolbar.</summary>
        readonly Prop<string> _countText;
        LibraryNavigatorProps? _navigator;
        readonly Func<LibraryNavMountPlan> _resolveMount;
        readonly Func<IReadOnlyList<LibraryHitRow>> _artistHitRows, _albumHitRows, _artistAlbumHitRows;
        readonly Func<IReadOnlyList<LibraryTrackHitRow>> _trackHitRows;
        readonly Action<int> _selectArtist, _selectAlbum;
        readonly Action<float> _setPageWidth;
        readonly Func<IReadOnlyList<LibraryKindChip>> _kinds;
        // The writable cells the view binds its controls to, as Funcs (WVTH006): the same instances for the page's life.
        readonly Func<Signal<float>> _leftW, _midW;
        readonly Func<Signal<int>> _sort, _view, _size, _podcastsMode;
        readonly Func<Signal<bool>> _desc;
        readonly Func<Signal<string>> _filter;

        /// <summary>The columns are folded into the one-column drill-in: the structure's layout tier (<see cref="Theming.LibraryLadders.Layout"/>) is its index 0. Read where
        /// a verb decides, never from a render (a render reads the tier signal itself).</summary>
        bool Collapsed => _layoutTier is { } tier && Theming.LibraryLadders.IsSingleColumn(tier.Peek());

        static readonly Shell.RouteKind[] s_kindRoutes =
            [Shell.RouteKind.LibraryAlbums, Shell.RouteKind.LibraryArtists, Shell.RouteKind.LibraryPodcasts, Shell.RouteKind.LibraryAudiobooks, Shell.RouteKind.Liked];

        /// <summary>The one-column head's kind chips: the library's kinds in the nav's order, this page's selected. Built when the structure's view asks (only the Compact head
        /// draws them), so a split page allocates nothing for them.</summary>
        IReadOnlyList<LibraryKindChip> KindChips()
        {
            var chips = new LibraryKindChip[s_kindRoutes.Length];
            for (int i = 0; i < chips.Length; i++)
            {
                var kind = s_kindRoutes[i];
                chips[i] = new LibraryKindChip(Shell.Dest(new Shell.Route(kind)).Title, kind == _route, () => Shell.GoTo(new Shell.Route(kind)));
            }
            return chips;
        }

        /// <summary>The layout ladder's seed before the first measure: the content region the frame resolved the window tiers on, else the viewport less a sidebar;
        /// unknown seeds the split layout, as the page always did.</summary>
        float PageSeed()
        {
            float seed = Detail.Breakpoints.EstimatePageWidth(Theming.ThemeHost.Facts.Peek().Tiers, _viewport?.Peek().Width ?? 0f);
            return seed > 0f ? seed : float.PositiveInfinity;
        }

        bool IsArtists => _entity == EntityKind.Artist;
        /// <summary>The Show ENTITY, whichever of the two show-kind library routes this is (A2 plan §3.6): the
        /// structural facts that do not care WHICH half of <c>SavedShows</c> is showing (no full-search UI, the
        /// "select a show" placeholder) key off this.</summary>
        bool IsShowEntity => _entity == EntityKind.Show;
        /// <summary>The Podcasts route specifically — NOT just "a show entity" any more, now that Audiobooks (A2 plan
        /// §3.6) is the entity's other library route: only Podcasts owns the Followed shows / Your Episodes mode
        /// switch.</summary>
        bool IsPodcasts => _route == Shell.RouteKind.LibraryPodcasts;
        bool IsAudiobooks => _route == Shell.RouteKind.LibraryAudiobooks;

        public LibraryPage(Shell.RouteKind route)
        {
            _route = route;
            (_kind, _entity, _relation, _fetch) = route switch
            {
                Shell.RouteKind.LibraryArtists => ("artists", EntityKind.Artist, LibraryEdgeKind.FollowedArtists, FetchEdge.FollowedArtists),
                Shell.RouteKind.LibraryPodcasts => ("podcasts", EntityKind.Show, LibraryEdgeKind.SavedShows, FetchEdge.SavedShows),
                // Audiobooks (A2 plan §3.6): the SAME SavedShows edge as Podcasts, split client-side
                // (LibraryAudiobookFilter) rather than a relation of its own — an audiobook is still filed under
                // `SavedShows` on the wire, only its flags say which half of the list it belongs to.
                Shell.RouteKind.LibraryAudiobooks => ("audiobooks", EntityKind.Show, LibraryEdgeKind.SavedShows, FetchEdge.SavedShows),
                _ => ("albums", EntityKind.Album, LibraryEdgeKind.SavedAlbums, FetchEdge.SavedAlbums),
            };
            _navScroll = new ScrollOptions { ScrollKey = "lib:nav:" + _kind, Handle = _navHandle };
            var saved = LibraryViewPrefs.Read(_kind);
            // The navigator rests at the structure's width for the content tier until the user drags it (contract 1.21): read from the tiers the frame already
            // resolved, so frame one is final, and re-rested on a crossing (`_restLeft`); the first drag makes the stored width the user's.
            _leftCustomized = LibraryViewPrefs.LeftWCustomized(_kind);
            LeftW = new(LibraryViewPrefs.RestingLeftW(_kind, saved.LeftW, _leftCustomized, Theming.ThemeHost.Facts.Peek().Tiers.Content, ShellPolicy.Now.LibraryTiers));
            MidW = new(saved.MidW);
            Sort = new(saved.Sort);
            Desc = new(saved.Desc);
            View = new(saved.View);
            Size = new(saved.Size);
            SelectedKey = new(saved.SelectedKey);
            AlbumKey = new(saved.AlbumKey);
            Scope = new(saved.Scope);
            RSort = new(saved.AlbumSort);

            _computeShape = ComputeShape;
            _resolveSelected = () => { _ = Entities.ScopeEpoch.Value; return SlotOfKey(_entity, SelectedKey.Value); };
            _isSearching = () => Filter.Value.AsSpan().Trim().Length > 0;
            _filterText = () => Filter.Value.Trim();
            _runSearch = RunSearch;
            _demandEdge = DemandEdge;
            _demandRows = DemandRows;
            _syncNav = SyncNav;
            _syncLetters = SyncLetters;
            _syncLoad = SyncNavLoad;
            _saveState = SaveState;
            _resetDepth = () => { if (Collapsed) _depth.Value = 0; };
            _pageSeed = PageSeed;
            _autoTop = AutoSelectTop;
            _autoAlbum = AutoSelectAlbum;
            _onNavSel = OnNavSel;
            _commitLeft = CommitLeft;
            _restLeft = RestLeft;
            _commitMid = () => LibraryViewPrefs.SetMidW(_kind, MidW.Peek());
            _setPageWidth = w => { if (w > 0f && w != _pageW.Peek()) _pageW.Value = w; };
            _kinds = KindChips;
            _pickDepth = i => _depth.Value = i;
            _jump = Jump;
            _onAlsoBy = Select;                       // the ONE library-internal jump that stays: select in place
            _onNavMounted = OnNavMounted;
            _extentOf = ExtentOf;
            _contentType = ContentTypeOf;
            _watchLetters = WatchLetters;
            _countText = Prop.Of(() => FormatCache.Int(_shape is { } shape ? shape.Value.Count : 0));
            _resolveMount = ResolveMount;
            _artistHitRows = ArtistHitRows;
            _albumHitRows = AlbumHitRows;
            _artistAlbumHitRows = ArtistAlbumHitRows;
            _trackHitRows = TrackHitRows;
            _selectArtist = SelectArtist;
            _selectAlbum = SelectAlbum;
            _leftW = () => LeftW; _midW = () => MidW;
            _sort = () => Sort; _view = () => View; _size = () => Size; _podcastsMode = () => PodcastsMode;
            _desc = () => Desc; _filter = () => Filter;

            // `ListOptions<T>` is UNPACKED at factory time and FROZEN at mount, so BOTH variants are built once, here —
            // the lettered one adds the header recycle pool, the header skip, and the sticky band's clip.
            _navOptions = new ListOptions<LibraryNavItem>
            {
                SelectionMode = ItemsSelectionMode.Single, Selection = _navSel, Controller = _navCtl, Grow = 1f,
                Scroll = _navScroll, OnChange = _onNavSel,
                // typeahead (item 68) — a header item carries a NEGATIVE slot and has no title to type at.
                ItemTextTyped = static (_, it) => it.Slot > Table.None ? LibraryRows.TitleOf(it.Kind, it.Slot) : "",
            };
            _navOptionsLettered = _navOptions with
            {
                ContentType = _contentType,
                IsItemEnabledTyped = static (_, it) => it.Slot > Table.None,
                Scroll = _navScroll with
                {
                    // rows scroll UNDER the pinned letter: clipped at exactly 28 with a 24-DIP feather
                    ItemClipTopInset = LibraryLetters.HeaderExtent,
                    ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
                },
            };
        }

        /// <summary>A grip release: the width becomes the user's (a release that moved nothing from the tier's rest keeps the navigator the tier's).</summary>
        void CommitLeft()
        {
            float w = LeftW.Peek();
            if (!_leftCustomized && w == RestingLeftNow()) return;
            LibraryViewPrefs.SetLeftW(_kind, w);
            _leftCustomized = true;
        }

        /// <summary>Auto-tracked on the tiers and the structure's library policy: an undragged navigator follows the tier's resting width.</summary>
        void RestLeft()
        {
            float rest = LibraryViewPrefs.RestingLeftW(_kind, LibraryViewPrefs.LeftWKey(_kind).Default, _leftCustomized,
                Theming.ThemeHost.Facts.Value.Tiers.Content, ShellPolicy.Tracked.LibraryTiers);
            if (!_leftCustomized) LeftW.SetIfChanged(rest);
        }

        float RestingLeftNow()
            => LibraryViewPrefs.RestingLeftW(_kind, LibraryViewPrefs.LeftWKey(_kind).Default, false,
                Theming.ThemeHost.Facts.Peek().Tiers.Content, ShellPolicy.Now.LibraryTiers);

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;          // FIRST: a scope switch re-points every table below
            _scrollScope = UseContext(Shell.PageScrollScope);
            _shape = UseComputed(_computeShape);
            SelectedSlot = UseComputed(_resolveSelected);
            _searchActive = UseComputed(_isSearching);
            _query = UseDebouncedValue(_filterText, SearchDebounceMs);
            uint hitsGeneration = UseComputed(_runSearch).Value;
            UseEffect(_demandEdge, DepKey.From((int)epoch, Entities.Current.MeSlot));   // once per scope; a failed ask never re-arms here
            UseEffect(_demandRows);                                                     // re-runs as the relation lands
            UseEffect(_restLeft);                                                       // auto-tracked: a content-tier crossing re-rests an undragged navigator
            UseSignalEffect(_watchLetters);                                             // the sticky letter off the nav scroll
            // The structure's LAYOUT ladder over the page's measured width (themes plan 3.3): the first frame resolves from the seed (the content region the frame resolved the
            // window tiers on), so a page mounted at Compact is one column from its first frame; a crossing after it commits on the first non-resized frame and animates.
            _viewport = UseContextSignal(Viewport.Size);
            _layoutTier = UseTier(Theming.LibraryLadders.Of(ShellPolicy.Now.Tiers), TierSource.Measured(_pageW, _pageSeed));

            _items ??= BoundItems.Project(_shape, static s => s.FlatCount, (_, i) => ItemAt(i), default(LibraryNavItem));

            var shape = _shape.Value;
            string raw = Filter.Value.Trim();
            string query = _query.Value;
            bool fullSearch = raw.Length > 0 && !IsShowEntity;
            _fullSearch = fullSearch;
            bool awaiting = fullSearch && !string.Equals(query, raw, StringComparison.Ordinal);
            bool shimmer = awaiting && _hits.IsEmpty;
            int view = View.Value, size = Size.Value;
            int sortCode = Sort.Value;
            string sel = SelectedKey.Value, albumKey = AlbumKey.Value;
            int selectedSlot = SelectedSlot.Value;
            bool railOpen = Shell.Ui.RailOpen.Value;
            bool collapsed = Theming.LibraryLadders.IsSingleColumn(_layoutTier.Value);
            int sArtist = _sArtist.Value;

            // The bound list's KEY is `shape.Mount.Key` (minted ONCE, in `ComputeShape`, by `LibraryNavMount.For`) — what
            // would make its frozen template/layout/options lie, and nothing else (the row facts AND the order refresh
            // through the memo/the rows' own binds — see NavShape). `SyncNav` reads `shape.Mount.Key` and `shape.OrderKey`
            // itself to tell a remount from an in-place reorder from a plain selection change (§3.3's three cases).
            string listKey = shape.Mount.Key;

            UseEffect(_syncLetters, shape.OrderKey + "|" + shape.FactsKey + "|" + sortCode + "|" + view);
            UseEffect(_syncLoad, DepKey.From(shape.Count, (shape.Answered ? 1 : 0) | (raw.Length > 0 ? 2 : 0)
                                                          | (shape.TitlesKnown ? 4 : 0) | (_anyTitleFailed ? 8 : 0)));
            UseEffect(_syncNav, listKey + "|" + shape.OrderKey + "|" + sel + "|" + (fullSearch ? "s" : "b"));
            UseEffect(_saveState, sortCode + "|" + Desc.Value + "|" + view + "|" + size + "|" + sel + "|" + albumKey + "|"
                                  + Scope.Value + "|" + RSort.Value);
            UseEffect(_resetDepth, DepKey.From(collapsed));
            UseEffect(_autoTop, DepKey.From((int)hitsGeneration, fullSearch ? 1 : 0));
            UseEffect(_autoAlbum, DepKey.From((int)hitsGeneration, sArtist));

            // Your Episodes (A2 plan §3.5): Podcasts' row-1 words ARE the mode switch, so the selected word swaps the WHOLE body for
            // the flat episode table while the toolbar stays put.
            bool savedMode = IsPodcasts && PodcastsMode.Value == 1;
            // The collapsed rung and its crumbs: TWO rungs when BROWSING (the navigator, the pane or reader); SEARCH keeps three columns,
            // so the artists view keeps a third rung (artist hits, album hits, track hits).
            int depth = 0;
            IReadOnlyList<string> crumbs = Array.Empty<string>();
            if (collapsed)
            {
                depth = Math.Clamp(_depth.Value, 0, IsArtists && fullSearch ? 2 : 1);
                crumbs = CrumbsOf(depth, fullSearch, selectedSlot);
            }

            _navigator ??= new LibraryNavigatorProps
            {
                Load = _navLoad, Items = _items, Mount = _resolveMount, OnMounted = _onNavMounted,
                StickyLetter = _stickyLetter, StickyPush = _stickyPush, Present = _present, Jump = _jump,
            };

            // The structure draws the page (T10c1): this host keeps every decision and hands the view the values and verbs it draws.
            return Slots.View(ViewKeys.LibraryPage, new LibraryPageProps
            {
                Title = Shell.Dest(new Shell.Route(_route)).Title, Entity = _entity, IsPodcasts = IsPodcasts,
                Collapsed = collapsed, SavedEpisodesMode = savedMode, FullSearch = fullSearch, Shimmer = shimmer, Awaiting = awaiting,
                RailOpen = railOpen, HasSelection = sel.Length > 0, Depth = depth, Crumbs = crumbs,
                SetPageWidth = _setPageWidth, Kinds = _kinds, PickDepth = _pickDepth,
                LeftW = _leftW, MidW = _midW, Sort = _sort, Desc = _desc, View = _view, Size = _size, Filter = _filter, PodcastsMode = _podcastsMode,
                CommitLeft = _commitLeft, CommitMid = _commitMid, CountText = _countText,
                Pane = () => PaneFor(selectedSlot),
                SavedEpisodes = () => Embed.Comp(() => new SavedEpisodesReader()) with { Key = "saved-episodes:" + epoch },
                SkelGroup = _skelGroup, Navigator = _navigator,
                Search = fullSearch ? SearchPropsOf() : null,
            });
        }

        // ── the navigator shape ─────────────────────────────────────────────────────────────────────────────────────

        NavShape ComputeShape()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var relation = Relation(_relation);
            _ = relation.Changed.Value;
            _ = Entities.TableFor(_entity)!.Changed.Value;
            if (_entity == EntityKind.Album) { _ = scope.Edges.AlbumArtists.Changed.Value; _ = scope.Artists.Changed.Value; }
            if (IsArtists)
            {
                // The Artists SET and both of its counts are reads over five relations plus the albums' track counts, so
                // every one of them is subscribed here whatever the rail's word is — the set itself grows as they land
                // (a cold start sees followed-only until SavedAlbums / Liked answer), and the row's second line is a
                // fact about all five. `Tracks` is deliberately absent: `Tracks.Album` lands in the same batch as the
                // TrackArtists edge (TrackFields.Identity), and subscribing the whole track table would recompute this
                // navigator on every playlist page in the app.
                var ed = scope.Edges;
                _ = ed.SavedAlbums.Changed.Value; _ = ed.AlbumArtists.Changed.Value;
                _ = ed.Liked.Changed.Value; _ = ed.TrackArtists.Changed.Value;
                _ = scope.Albums.Changed.Value;                 // an album's TrackCount is half the songs line
            }
            string filter = Filter.Value;
            var sort = LibraryWordRail.Clamp(_entity, Sort.Value);   // a code this kind's rail does not offer reads as Recents
            bool desc = Desc.Value;
            // Both read here (not just View): a list<->grid or an S/M/L toggle must always republish this memo, or a
            // toggle that leaves every OTHER field of NavShape identical would leave NavList rebuilding nothing (the
            // "memo compares equal" trap the old `:902-904` comment worked around by re-reading View a second time
            // inside NavList — gone now that the mount identity IS part of the published value).
            int view = View.Value, size = Size.Value;

            var source = scope.MeSlot <= Table.None ? default
                       : IsArtists ? ArtistsInto(ref _artistSrc)
                       : relation.Targets(scope.MeSlot);
            bool answered = scope.MeSlot > Table.None && relation.State(scope.MeSlot) != EdgeState.Unknown;
            // Audiobooks/Podcasts split the SAME SavedShows edge client-side (A2 plan §3.6): Podcasts applies the
            // inverse of Audiobooks' own predicate, so a show can never appear on neither or both lists.
            if (IsShowEntity) source = SplitShowKind(source);
            Grow(source.Length);
            int n = LibraryRows.Filter(_entity, source, filter, _filtered);
            long[]? played = null;
            int[]? counts = null;
            if (sort == LibraryNavSort.Recents)
            {
                _ = Shell.PlayLog.Version.Value;                 // a play re-orders Recents in place
                LibraryRows.FillPlayed(_entity, _filtered.AsSpan(0, n), Shell.PlayLog.Recency, _played);
                played = _played;
            }
            if (IsArtists)
            {
                // PRECOMPUTED, exactly like `played`, and now for EVERY word rather than only the "albums" one: the two
                // numbers are the row's own second line, and a read per row would be O(rows · (saved · billed + liked)).
                // One walk of the library each (§11's FillReleaseCounts / FillSongCounts), in FILTERED order.
                LibraryRows.FillCounts(_entity, _filtered.AsSpan(0, n), _counts);
                FillSongCounts(_filtered.AsSpan(0, n), _songs);
                counts = _counts;
            }
            _sorter.Order(new LibraryRows(_entity, _filtered, n, played, counts), sort, desc, _perm);
            for (int i = 0; i < n; i++) _sorted[i] = _filtered[_perm[i]];
            _sortedCount = n;
            var display = new LibraryRows(_entity, _sorted, n);

            // The grouping is derived from the SORTED titles, so it is only meaningful under a–z. It is built for a grid
            // too (the strip's mask + its first-card index come out of it), but the FLAT projection is a list-only shape.
            bool alpha = sort == LibraryNavSort.Alphabetical;
            bool lettered = alpha && !IsGridView(view);
            // ONE MINT (library-stabilization-plan §3.3): `LibraryNavMount.For` is the only place that turns
            // (view, size, alphabetical, lettersKey) into a remount key + the template/layout/options facts frozen at
            // mount. `RowExtent` depends only on (view, size) — NOT on the letters key — so it is safe to mint a
            // provisional mount to read it before the grouping (which needs a row extent to seed itself) produces the
            // real letters key, then mint the real mount once that key is known. A grid or a non-alphabetical sort never
            // pays the second mint: `Key` folds `lettersKey` in only while `lettered` is true.
            var mount = LibraryNavMount.For(_kind, view, size, alpha, 0UL);
            if (alpha) _letters.Build(display, mount.RowExtent);
            _lettersBuilt = alpha;
            _hasLetters = lettered;
            ulong lettersKey = lettered ? _letters.Key() : 0UL;
            if (lettered) mount = LibraryNavMount.For(_kind, view, size, alpha, lettersKey);
            _mountSpec = mount;

            ulong rowsKey = FillRowVersions(n, out bool titlesKnown, out bool anyTitleFailed);
            _anyTitleFailed = anyTitleFailed;

            return new NavShape(n, lettered ? _letters.FlatCount : n, LibraryNavOrder.OrderKey(display),
                                LibraryNavOrder.FactsKey(display), rowsKey, lettersKey, answered, titlesKnown, mount);
        }

        /// <summary>Podcasts keeps the shows for which <see cref="LibraryAudiobookFilter.IsAudiobookRow"/> is false;
        /// Audiobooks keeps the inverse (A2 plan §3.6) — the ONE predicate, applied both ways, so a show can never
        /// land on neither list or both.</summary>
        ReadOnlySpan<int> SplitShowKind(ReadOnlySpan<int> source)
        {
            if (_showKindSrc.Length < source.Length) _showKindSrc = new int[source.Length];
            bool wantAudiobooks = IsAudiobooks;
            int n = 0;
            for (int i = 0; i < source.Length; i++)
            {
                int slot = source[i];
                bool audiobook = LibraryAudiobookFilter.IsAudiobookRow(new Show(slot).Flags);
                if (audiobook == wantAudiobooks) _showKindSrc[n++] = slot;
            }
            return _showKindSrc.AsSpan(0, n);
        }

        /// <summary>The Artists navigator's source, into a pooled buffer. <see cref="User.LibraryArtistsOf"/> returns the
        /// TOTAL and writes only what fits, so a buffer that was too small is grown and the set is READ AGAIN — the
        /// helper's documented contract, and the reason the two call sites do not share one buffer.</summary>
        static ReadOnlySpan<int> ArtistsInto(ref int[] buffer)
        {
            int n = LibraryArtistsOf(buffer);
            if (n > buffer.Length)
            {
                buffer = new int[Math.Max(n, buffer.Length * 2)];
                n = LibraryArtistsOf(buffer);
            }
            return buffer.AsSpan(0, Math.Min(n, buffer.Length));
        }

        /// <summary>THE row-refresh rule. Each row's version is folded from every table its cells read, and the FNV over
        /// them is the shape's <c>RowsKey</c>: the shape moves ⇒ every mounted slot re-resolves its item ⇒ the
        /// equality-gated item signal re-fires the binds of the rows that ACTUALLY changed and nothing else. No remount
        /// is involved anywhere in that sentence, which is the whole point.
        /// <list type="bullet">
        /// <item>ALBUM: its own row version, plus the first billed artist's — the subtitle is "artist · year" and the
        /// name lives on the ARTISTS table, so the album's version alone left a landed name unread (<c>AlbumSubtitleKey</c>).</item>
        /// <item>ARTIST: its own row version, plus the release and song counts this compute already batched — the second
        /// line is those two numbers and they move when an album or a like lands, not when the artist row does.</item>
        /// <item>SHOW: its own row version; the publisher is a column of the same row.</item>
        /// </list>
        /// Written in SORTED order (the flat projection's row index); the counts are read through <c>_perm</c> because
        /// they were filled in filtered order.
        /// <para>THE SAME WALK folds the a–z readiness gate (A4): <paramref name="titlesKnown"/> is true only when every
        /// listed row already knows its title (<see cref="TitleFieldOf"/>) — sorting by a title that has not landed is
        /// wrong data, not partial data (project rule) — and <paramref name="anyTitleFailed"/> is true the moment one of
        /// them never will (a terminally failed identity fetch, <c>Table.Failed</c>), which is what lets the gate give up
        /// waiting instead of shimmering forever.</para></summary>
        ulong FillRowVersions(int n, out bool titlesKnown, out bool anyTitleFailed)
        {
            const ulong FnvBasis = 14695981039346656037UL, FnvPrime = 1099511628211UL;
            if (_rowVer.Length < n) _rowVer = new uint[Math.Max(n, _rowVer.Length * 2)];
            var scope = Entities.Current;
            Table? table = Entities.TableFor(_entity);
            uint titleField = TitleFieldOf(_entity);
            ulong h = FnvBasis;
            bool titles = true, failed = false;
            for (int i = 0; i < n; i++)
            {
                int slot = _sorted[i];
                bool inRange = table is not null && slot > Table.None && slot < table.Count;
                uint v = inRange ? table!.Version[slot] : 0u;
                if (titles && !(inRange && table!.Knows(slot, titleField)))
                {
                    titles = false;
                    if (inRange && table!.IsFailed(slot, titleField)) failed = true;
                }
                if (IsArtists)
                {
                    int f = _perm[i];
                    v = (v * 31u) + (uint)_counts[f];
                    v = (v * 31u) + (uint)_songs[f];
                }
                else if (_entity == EntityKind.Album)
                {
                    var billed = scope.Edges.AlbumArtists.Targets(slot);
                    int artist = billed.Length > 0 ? billed[0] : Table.None;
                    if (artist > Table.None && artist < scope.Artists.Count) v = (v * 31u) + scope.Artists.Version[artist];
                }
                _rowVer[i] = v;
                h = (h ^ v) * FnvPrime;
            }
            titlesKnown = titles;
            anyTitleFailed = failed;
            return h;
        }

        /// <summary>The row-identity field the a–z gate (A4) waits on: the title for albums/shows, the NAME for artists
        /// (the same field <see cref="LibraryRows.TitleOf"/> reads).</summary>
        static uint TitleFieldOf(EntityKind kind) => kind switch
        {
            EntityKind.Artist => (uint)ArtistFields.Name,
            EntityKind.Show => (uint)ShowFields.Title,
            _ => (uint)AlbumFields.Title,
        };

        void Grow(int n)
        {
            if (_filtered.Length >= n) return;
            int size = Math.Max(n, _filtered.Length * 2);
            _filtered = new int[size]; _perm = new int[size]; _sorted = new int[size];
            _played = new long[size]; _counts = new int[size]; _songs = new int[size]; _rowVer = new uint[size];
        }

        // ── the flat index space (rows ⇄ flat items) ─────────────────────────────────────────────────────────────────

        /// <summary>The bound source's projection. A header is the item <c>Slot = -(letter + 1)</c>; everything else is a
        /// row, looked up through the letters' flat→row map when the grouping is live. The row's version is the FOLDED
        /// one this compute wrote (<see cref="FillRowVersions"/>), not <c>LibraryNavItem.Of</c>'s single-table read.</summary>
        LibraryNavItem ItemAt(int flat)
        {
            if (_hasLetters)
            {
                if (_letters.IsHeader(flat)) return new LibraryNavItem(_entity, -_letters.LetterOf(flat) - 1, 0u);
                flat = _letters.RowOf(flat);
            }
            return (uint)flat < (uint)_sortedCount ? new LibraryNavItem(_entity, _sorted[flat], _rowVer[flat]) : default;
        }

        int RowOfFlat(int flat) => flat < 0 ? -1 : _hasLetters ? _letters.RowOf(flat) : flat;

        /// <summary>The row extent the CURRENT shape's mount was built for (<see cref="_mountSpec"/>) — never
        /// <c>IsCompactView(View.Peek())</c>: peeking the live signal here let a mounted list's measured-extent seed
        /// disagree with the layout it was actually built with the instant a view toggle changed `View` a frame before
        /// the new mount landed (library-stabilization-plan §3.3).</summary>
        float RowExtent => _mountSpec.RowExtent;

        /// <summary>The analytic seed `RepeatLayout.Extents` asks for: the header band, or the row.</summary>
        float ExtentOf(int flat) => _hasLetters ? _letters.ExtentOf(flat, RowExtent) : RowExtent;

        /// <summary>The recycle-pool discriminator: a letter plate must never rebind into a row slot.</summary>
        int ContentTypeOf(int flat) => _hasLetters && _letters.IsHeader(flat) ? 1 : 0;

        /// <summary>A handle slot → its FLAT index (one pass over the flat space when the grouping is live, over the rows
        /// when it is not). -1 when this set does not hold the slot.</summary>
        int IndexOfSlot(int slot)
        {
            if (slot <= Table.None) return -1;
            if (!_hasLetters)
            {
                for (int i = 0; i < _sortedCount; i++) if (_sorted[i] == slot) return i;
                return -1;
            }
            for (int f = 0, n = _letters.FlatCount; f < n; f++)
            {
                int row = _letters.RowOf(f);
                if (row >= 0 && (uint)row < (uint)_sortedCount && _sorted[row] == slot) return f;
            }
            return -1;
        }

        // ── demand ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The page's own relation, once per scope. The ARTISTS tab asks for three, because its set is three
        /// groups (§11): a user who opens Artists first on a cold start must not see a followed-only list because
        /// nothing else on screen happened to want the saved albums or the likes.</summary>
        void DemandEdge()
        {
            int me = Entities.Current.MeSlot;
            if (me <= Table.None) return;
            if (Relation(_relation).State(me) == EdgeState.Unknown) Entities.EnsureEdge(_fetch, me);
            if (!IsArtists) return;
            var e = Entities.Current.Edges;
            if (e.SavedAlbums.State(me) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.SavedAlbums, me);
            if (e.Liked.State(me) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.Liked, me);
        }

        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var relation = Relation(_relation);
            _ = relation.Changed.Value;
            int me = scope.MeSlot;
            if (me <= Table.None) return;
            if (IsArtists) { DemandArtistRows(me); return; }
            var slots = relation.Targets(me);
            if (slots.Length == 0) return;
            if (_entity == EntityKind.Album) Entities.Ensure(MemoryMarshal.Cast<int, Album>(slots), AlbumFields.Identity);
            else Entities.Ensure(MemoryMarshal.Cast<int, Show>(slots), ShowFields.Identity);
        }

        /// <summary>The Artists navigator demands its WHOLE model, which is three asks and not one (C1): the identity of
        /// every saved ALBUM and every liked TRACK — because that is what fills the unpersisted <c>AlbumArtists</c> /
        /// <c>TrackArtists</c> edges the §11 helpers read, and without them the list is followed-only and every count is
        /// 0 on a cold start — and then the identity of the artists those edges produced. No windows: the page asks for
        /// its model and the query layer batches it (300 per POST), which is the whole reason a page may.
        /// <para>It is auto-tracked, so the two library relations' <c>Changed</c> re-arm it: a track liked now is
        /// demanded now. An ask a row already knows is dropped by the planner's own seal, so the re-run is a walk.</para></summary>
        void DemandArtistRows(int me)
        {
            var e = Entities.Current.Edges;
            _ = e.SavedAlbums.Changed.Value; _ = e.Liked.Changed.Value;
            _ = e.AlbumArtists.Changed.Value; _ = e.TrackArtists.Changed.Value;

            var saved = e.SavedAlbums.Targets(me);
            if (saved.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Album>(saved), AlbumFields.Identity);
            var liked = e.Liked.Targets(me);
            if (liked.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(liked), TrackFields.Identity);
            var artists = ArtistsInto(ref _artistDemand);
            if (artists.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Artist>(artists), ArtistFields.Identity);
        }

        // ── selection ───────────────────────────────────────────────────────────────────────────────────────────────

        void Select(int slot)
        {
            SelectedKey.Value = KeyOf(_entity, slot);
            if (IsArtists) AlbumKey.Value = "";   // a new artist clears the reader's initial spine target
        }

        void OnNavSel()
        {
            int row = RowOfFlat(_navSel.FirstSelectedIndex);
            if (_syncingSel || row < 0 || row >= _sortedCount) return;   // the page's own re-sync is a VIEW update, never a pick
            _pickedFromList = true;   // consumed + cleared by SyncNav in the SAME effect run: already revealed, don't re-bring
            Select(_sorted[row]);
            if (Collapsed) _depth.Value = 1;
        }

        /// <summary><paramref name="alignmentRatio"/> is the WinUI `StartBringItemIntoView` alignment: <c>NaN</c> is
        /// minimal (a visible row never moves — Jump()'s unanimated tap, an in-place reorder), <c>0.5f</c> centres (a
        /// fresh mount, RC4 — the restored scroll offset belongs to a DIFFERENT layout family and means nothing here).</summary>
        void SyncSelect(int idx, float alignmentRatio)
        {
            _syncingSel = true;
            try { if (idx < 0) _navSel.DeselectAll(); else _navSel.Select(idx); }
            finally { _syncingSel = false; }
            if (idx >= 0) _navCtl.StartBringItemIntoView(idx, alignmentRatio);   // unanimated: a jump is a jump (ch 15 §6)
        }

        /// <summary>Adopt / re-assert the navigator's selection — the pre-write (see <see cref="PrepareMount"/>) already
        /// put the right INDEX into the model before a fresh ItemsView mounted; this is the after-the-fact re-assert +
        /// adopt-row-0 effect, and it distinguishes THREE cases (library-stabilization-plan §3.3) by comparing what it
        /// last saw (<see cref="_lastNavKey"/>, <see cref="_syncOrderKey"/>) to what <c>shape</c> holds now:
        /// <list type="bullet">
        /// <item><see cref="LibraryNavMount.Key"/> moved → a REMOUNT: the fresh ItemsView starts with nothing
        /// highlighted however old the model is → always re-assert, CENTRED (no restored offset to trust).</item>
        /// <item>only <c>OrderKey</c> moved → an in-place REORDER: the same mounted list, but the selected item may sit
        /// at a NEW index → re-assert if the model disagrees, MINIMAL reveal (the row was already on screen somewhere).</item>
        /// <item>neither moved, but the model still disagrees with the route key → a programmatic change that did NOT
        /// come off the list itself (<see cref="_pickedFromList"/> false: the persisted-selection restore, row-0
        /// adoption) → re-assert, MINIMAL. A pick that DID come off the list needs nothing: the engine already
        /// revealed it, and <c>_navSel.FirstSelectedIndex</c> already agrees with <c>idx</c>.</item>
        /// </list></summary>
        void SyncNav()
        {
            bool pickedFromList = _pickedFromList;
            _pickedFromList = false;
            if (_fullSearch) return;
            var shape = _shape!.Peek();
            string mountKey = shape.Mount.Key;
            bool remounted = !string.Equals(mountKey, _lastNavKey, StringComparison.Ordinal);
            bool reordered = !remounted && !string.Equals(shape.OrderKey, _syncOrderKey, StringComparison.Ordinal);
            _lastNavKey = mountKey;
            _syncOrderKey = shape.OrderKey;
            if (_sortedCount == 0)
            {
                // A filter that matched nothing drops the selection (and the release); an empty set with NO filter is still
                // loading — keep the persisted selection (the launch restore).
                if (Filter.Peek().Length > 0 && SelectedKey.Peek().Length > 0)
                {
                    SelectedKey.Value = "";
                    if (IsArtists) AlbumKey.Value = "";
                    if (_navSel.SelectedCount > 0) SyncSelect(-1, float.NaN);
                }
                return;
            }
            string key = SelectedKey.Peek();
            int idx = key.Length == 0 ? -1 : IndexOfSlot(SlotOfKey(_entity, key));
            if (idx < 0)
            {
                // no selection, or a key this set no longer holds → row 0 (whose FLAT index is 1 under a letter header)
                Select(_sorted[0]);
                idx = IndexOfSlot(_sorted[0]);
                if (idx < 0) return;
            }
            if (remounted)
            {
                SyncSelect(idx, 0.5f);
            }
            else if (reordered)
            {
                if (_navSel.FirstSelectedIndex != idx) SyncSelect(idx, float.NaN);
                LogReorder(shape, idx);
            }
            else if (!pickedFromList && _navSel.FirstSelectedIndex != idx)
            {
                SyncSelect(idx, float.NaN);
            }
        }

        /// <summary>The truthful line for a mount that DID happen (see <see cref="OnNavMounted"/> for the one that
        /// fires from the keyed box's <c>OnRealized</c>): everything else — sort, play, a facts-driven re-rank —
        /// re-resolves the SAME mounted list in place, and this is what says so, every time it does.</summary>
        void LogReorder(NavShape shape, int selectedIndex)
        {
            Log.Event(WaveeLogLevel.Info, "ui", "library.nav.reorder", "Library navigator reordered in place", null, -1, null,
                WaveeLogField.Of("kind", _kind), WaveeLogField.Of("count", shape.Count),
                WaveeLogField.Of("selectedIndex", selectedIndex));
        }

        void SaveState()
        {
            LibraryViewPrefs.Save(_kind, new LibraryViewState(
                LeftW.Peek(), MidW.Peek(), Sort.Peek(), Desc.Peek(), View.Peek(), Size.Peek(),
                SelectedKey.Peek(), AlbumKey.Peek(), Scope.Peek(), RSort.Peek()), IsArtists);
        }

        // ── the letters: the sticky overlay, the jump strip (W2) ─────────────────────────────────────────────────────

        /// <summary>The strip's mask and the overlay's letter are page state, so they are written from an EFFECT (a memo
        /// that writes signals is the loop rule's #1 trap). A re-grouped list also drops a stale pinned letter: the
        /// overlay must not claim a band the new grouping no longer has.</summary>
        void SyncLetters()
        {
            _present.SetIfChanged(_lettersBuilt ? _letters.Present : 0u);
            if (!_hasLetters || (_stickyLetter.Peek() >= 0 && !_letters.Has(_stickyLetter.Peek())))
            {
                _stickyLetter.SetIfChanged(-1);
                _stickyPush.SetIfChanged(0f);
            }
        }

        /// <summary>The sticky letter's watch over the nav list's scroll handle: it re-runs on every published offset (and
        /// extent) and writes the two signals only when the coarse key moves.</summary>
        void WatchLetters()
        {
            float offsetY = (float)_navHandle.Offset.Value;
            _ = _navHandle.ExtentSignal.Value;   // a measured-extent correction re-projects too
            long key = ProjectLetters(offsetY);
            if (key == _lettersKey) return;
            _lettersKey = key;
            UpdateLetters(offsetY);
        }

        /// <summary>The coarse key: the pinned letter plus its push quantized to 2 DIP. Two signals per key CHANGE, never
        /// per frame.</summary>
        long ProjectLetters(float offsetY)
        {
            if (!_hasLetters) return 0L;
            int letter = _letters.StickyLetterAt(offsetY);
            int quantized = letter < 0 ? 0 : (int)MathF.Round(PushOf(offsetY, letter) / Spacing.XXS);
            return ((long)(letter + 1) << 16) | (uint)(ushort)(quantized + 32768);
        }

        void UpdateLetters(float offsetY)
        {
            int letter = _hasLetters ? _letters.StickyLetterAt(offsetY) : -1;
            _stickyLetter.SetIfChanged(letter);
            _stickyPush.SetIfChanged(letter < 0 ? 0f : MathF.Round(PushOf(offsetY, letter) / Spacing.XXS) * Spacing.XXS);
        }

        /// <summary>The next header's top minus the viewport top minus the overlay's height, clamped ≤ 0: the pinned
        /// letter is pushed up by the one arriving under it.</summary>
        float PushOf(float offsetY, int letter)
        {
            for (int l = letter + 1; l < LibraryLetters.Count; l++)
            {
                int next = _letters.HeaderFlat(l);
                if (next >= 0) return MathF.Min(0f, _letters.OffsetOf(next) - offsetY - LibraryLetters.HeaderExtent);
            }
            return 0f;
        }

        /// <summary>A strip tap. UNANIMATED (ch 15 §6: a jump is a jump). A letter with no rows resolves to -1 and the
        /// tap NO-OPS — which is what makes the strip's absent letters inert without a bound enabled flag.</summary>
        void Jump(int letter)
        {
            if (!_lettersBuilt) return;
            int flat = _hasLetters ? _letters.HeaderFlat(letter) : FirstRowOfLetter(letter);
            if (flat >= 0) _navCtl.StartBringItemIntoView(flat, alignmentRatio: 0f);
        }

        /// <summary>A grid has no header items, so the strip jumps to the letter's first CARD: the row that sits one flat
        /// step past the letter's header in the grouping the memo built anyway. O(1).</summary>
        int FirstRowOfLetter(int letter)
        {
            int header = _letters.HeaderFlat(letter);
            return header < 0 ? -1 : _letters.RowOf(header + 1);
        }

        // ── the navigator's loading state ───────────────────────────────────────────────────────────────────────────

        /// <summary>"Loading" is decided by the pure <see cref="LibraryNavReadiness"/> (A4), never inline here: the
        /// relation not having answered (as always), OR — new — the sort being a–z while a listed row's title has not
        /// landed. During HYDRATION the letters' set/positions move every time a title lands, which would move
        /// <see cref="LibraryNavMount.Key"/> repeatedly (RC4's sibling for the a–z projection specifically); gating on
        /// <c>TitlesKnown</c> means the a–z list only ever mounts once, sorted by data that has actually arrived —
        /// sorting by a title that has not landed is wrong data, not partial data (project rule). A row whose title
        /// fetch TERMINALLY FAILED (<c>_anyTitleFailed</c>) releases the gate instead of shimmering forever.</summary>
        void SyncNavLoad()
        {
            var shape = _shape!.Peek();
            bool pending = LibraryNavReadiness.IsPending(shape.Count, Filter.Peek().Length > 0, shape.Answered,
                shape.Mount.Strip, shape.TitlesKnown, _anyTitleFailed);
            if (pending) { if (!_navLoad.IsLoading) _navLoad.SetPending(); }
            else if (!_navLoad.IsReady || _navLoad.Value.Peek() != shape.Count) _navLoad.SetReady(shape.Count);
        }

        /// <summary>The navigator's mount at the moment the region asks for its content (<c>ClassicLibraryPage.NavList</c>): the shape's mount identity (the ONLY
        /// reactive read — it rides the shape memo), the selection pre-write for a fresh list (<see cref="PrepareMount"/>) and the one cached (layout, options) pair for
        /// that key (<see cref="NavMountCache.Resolve"/>), built once and reused for as long as the key does not change. The structure draws the list, the letters and the
        /// strip around it; every one that must actually remount is a KEYED CHILD of the region's unkeyed single-child root (RC1).</summary>
        LibraryNavMountPlan ResolveMount()
        {
            var mount = _shape!.Value.Mount;
            PrepareMount(mount);
            var (layout, options) = _mounts.Resolve(mount, this);
            return new LibraryNavMountPlan(mount, layout, options);
        }

        /// <summary>The mount-key pre-write (A2, risk (b)). The FIRST render to carry a new <see cref="LibraryNavMount.Key"/>
        /// computes the flat index the CURRENTLY selected route key resolves to under the NEW projection and writes it
        /// into <see cref="_navSel"/> before the fresh <c>ItemsView</c> is built — a plain MODEL write, not a signal the
        /// page's own render subscribes to (<c>SelectionModel.Version</c> is read only by non-bound ItemsViews), so this
        /// does not loop. Without it the fresh view mounts with nothing selected and the pane briefly disagrees with the
        /// list until <see cref="SyncNav"/>'s effect catches up a frame later.</summary>
        void PrepareMount(in LibraryNavMount mount)
        {
            if (mount.Key == _pendingKey) return;
            _pendingKey = mount.Key;
            int idx = IndexOfSlot(SlotOfKey(_entity, SelectedKey.Peek()));
            _syncingSel = true;
            try { if (idx < 0) _navSel.DeselectAll(); else _navSel.Select(idx); }
            finally { _syncingSel = false; }
        }

        /// <summary>The mount's OPTIONS, built once per key inside <see cref="NavMountCache"/>: the base record
        /// (constructor-built, frozen) plus the one fact that varies PER MOUNT rather than per page —
        /// <c>Scroll.ScrollKey</c> (per layout FAMILY, so a list's restored offset is never handed to a grid — RC4/F7).
        /// Every mount, the first included, realizes its whole window in its first frame (the engine has no cold-realize
        /// ramp any more).</summary>
        ListOptions<LibraryNavItem> OptionsFor(in LibraryNavMount mount)
        {
            var baseOptions = mount.Lettered ? _navOptionsLettered : _navOptions;
            return baseOptions with { Scroll = baseOptions.Scroll! with { ScrollKey = _scrollScope + mount.ScrollKey } };
        }

        /// <summary>The truthful mount signal (A3): wired on the keyed nav-list box, the engine calls this only when
        /// THAT box is actually realized — a real mount, never a refresh. It reads <c>_shape.Peek()</c> for "which mount
        /// is this" (the field is frozen and shared across every mount, so it cannot close over a per-render value) and
        /// names which half of the identity moved by diffing against <see cref="_lastMount"/>: <c>view</c> (template,
        /// layout kind, grid padding, or the row extent itself changed — list↔grid, compact toggle), <c>size</c> (the
        /// grid cell minimum moved and nothing else — S/M/L), or <c>letters</c> (only the lettered/strip flags moved —
        /// entering or leaving a–z in a LIST view; a–z in a GRID never changes <c>Mount.Key</c> at all, so it never
        /// reaches here).</summary>
        void OnNavMounted(NodeHandle _)
        {
            var shape = _shape!.Peek();
            var mount = shape.Mount;
            string reason = !_navEverMounted ? "first" : ReasonFor(_lastMount, mount);
            Log.Event(WaveeLogLevel.Info, "ui", "library.nav.mount", "Library navigator mounted", null, -1, null,
                WaveeLogField.Of("kind", _kind), WaveeLogField.Of("view", View.Peek()), WaveeLogField.Of("size", Size.Peek()),
                WaveeLogField.Of("lettered", mount.Lettered), WaveeLogField.Of("key", mount.Key),
                WaveeLogField.Of("reason", reason), WaveeLogField.Of("count", shape.Count));
            _lastMount = mount;
            _navEverMounted = true;
        }

        static string ReasonFor(in LibraryNavMount prev, in LibraryNavMount next)
            => prev.Template != next.Template || prev.Layout != next.Layout || prev.GridPadding != next.GridPadding
               || prev.RowExtent != next.RowExtent ? "view"
             : prev.CellMin != next.CellMin ? "size"
             : "letters";

        /// <summary>One (<see cref="RepeatLayout"/>, <see cref="ListOptions{T}"/>) pair per <see cref="LibraryNavMount.Key"/>,
        /// built ONCE and reused while the key does not change — page-private, NOT a component. <c>RepeatLayout.Extents</c>
        /// wraps a STATEFUL measured layout (it accumulates every row's measurement), so a fresh one per render would
        /// throw every measured row away; this is the direct replacement for the old per-key <c>_layout</c> cache, now
        /// keyed off the ONE mount identity instead of a hand-assembled string.</summary>
        sealed class NavMountCache
        {
            LibraryNavMount _spec;
            RepeatLayout _layout;
            ListOptions<LibraryNavItem>? _options;

            public (RepeatLayout Layout, ListOptions<LibraryNavItem> Options) Resolve(in LibraryNavMount spec, LibraryPage page)
            {
                if (_options is null || _spec.Key != spec.Key)
                {
                    _spec = spec;
                    _layout = spec.Layout == LibraryNavLayoutKind.GridFit
                        ? RepeatLayout.GridFit(spec.CellMin, spec.Gap)
                        : RepeatLayout.Extents(page._extentOf, spec.RowExtent);
                    _options = page.OptionsFor(spec);
                }
                return (_layout, _options!);
            }
        }

        // ── the panes and the crumbs ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The selected row's pane, mounted by the structure inside its column. NO `Key` on the album pane: a selection change RE-PUSHES its props and the pane
        /// re-skins in place (it owns its own crossfade) — a keyed remount would throw away its scroll and its warm demand. The artists view's one right-hand rung is the
        /// reader: the navigator's slot plus the three Signal instances it steers itself with (there is no MidW and no third pane — the reader is the leaf).</summary>
        Element PaneFor(int slot) => _entity switch
        {
            EntityKind.Show => Embed.Comp(new Show.PaneProps(slot), static () => new Show.Pane()),
            EntityKind.Artist => Embed.Comp(new Artist.ReaderProps(slot, Scope, RSort, AlbumKey), static () => new Artist.Reader()),
            _ => Embed.Comp(new Album.PaneProps(slot, ShowAlsoBy: true, OnAlsoBy: _onAlsoBy), static () => new Album.Pane()),
        };

        /// <summary>The collapsed layout's breadcrumb, root first: the kind, then the selected row (or the search hit), then (artists search) the album hit.</summary>
        IReadOnlyList<string> CrumbsOf(int depth, bool fullSearch, int selectedSlot)
        {
            int sArtist = _sArtist.Value, sAlbum = _sAlbum.Value;
            string level1 = IsArtists
                ? NameOrEllipsis(EntityKind.Artist, fullSearch ? sArtist : selectedSlot)
                : NameOrEllipsis(_entity, fullSearch ? sAlbum : selectedSlot);
            var crumbs = new List<string>(3) { KindCrumb() };
            if (depth >= 1) crumbs.Add(level1);
            if (depth >= 2) crumbs.Add(NameOrEllipsis(EntityKind.Album, sAlbum));
            return crumbs;
        }

        string KindCrumb() => _entity switch
        {
            EntityKind.Artist => Loc.Get(Strings.Search.Artists),
            EntityKind.Album => Loc.Get(Strings.Search.Albums),
            _ => Loc.Get(Strings.Podcast.Show),
        };

        static string NameOrEllipsis(EntityKind kind, int slot)
        {
            string name = slot > Table.None ? LibraryRows.TitleOf(kind, slot) : "";
            return name.Length > 0 ? name : "…";
        }

        // ── search mode (W11-W13, W23, W26 — UNCHANGED by the rework: still three columns under MidW) ────────────────

        uint RunSearch()
        {
            _ = Entities.ScopeEpoch.Value;
            bool active = _searchActive!.Value && !IsShowEntity;
            string q = _query!.Value;
            if (active)
            {
                var scope = Entities.Current;
                _ = scope.Edges.FollowedArtists.Changed.Value; _ = scope.Edges.SavedAlbums.Changed.Value;
                _ = scope.Edges.ArtistAlbums.Changed.Value; _ = scope.Edges.ArtistSingles.Changed.Value;
                _ = scope.Edges.ArtistCompilations.Changed.Value; _ = scope.Edges.AlbumTracks.Changed.Value;
                _ = scope.Albums.Changed.Value; _ = scope.Artists.Changed.Value; _ = scope.Tracks.Changed.Value;
                // The search walks the LIBRARY model (§11): likes and the two attribution edges move it too.
                _ = scope.Edges.Liked.Changed.Value; _ = scope.Edges.TrackArtists.Changed.Value; _ = scope.Edges.AlbumArtists.Changed.Value;
            }
            SearchLibrary(IsArtists ? LibrarySearchScope.Artists : LibrarySearchScope.Albums, active ? q : "", _hits);
            return _hits.Generation;
        }

        void AutoSelectTop()
        {
            if (!_fullSearch) return;
            if (IsArtists)
            {
                var artists = _hits.Artists;
                if (artists.Length == 0) { if (_sArtist.Peek() != 0) { _sArtist.Value = 0; _sAlbum.Value = 0; } return; }
                if (LibraryHits.IndexOf(artists, _sArtist.Peek()) < 0) { _sArtist.Value = artists[0].Slot; _sAlbum.Value = 0; }
                return;
            }
            var albums = _hits.Albums;
            if (albums.Length == 0) { if (_sAlbum.Peek() != 0) _sAlbum.Value = 0; return; }
            if (LibraryHits.IndexOf(albums, _sAlbum.Peek()) < 0) _sAlbum.Value = albums[0].Slot;
        }

        void AutoSelectAlbum()
        {
            if (!_fullSearch || !IsArtists) return;
            var albums = AlbumsOfSelectedArtist();
            if (albums.Length == 0) { if (_sAlbum.Peek() != 0) _sAlbum.Value = 0; return; }
            if (LibraryHits.IndexOf(albums, _sAlbum.Peek()) < 0) _sAlbum.Value = albums[0].Slot;
        }

        ReadOnlySpan<LibraryAlbumHit> AlbumsOfSelectedArtist()
        {
            int ai = LibraryHits.IndexOf(_hits.Artists, _sArtist.Peek());
            return ai < 0 ? default : _hits.AlbumsOf(_hits.Artists[ai]);
        }

        /// <summary>The hits and the selection the structure draws. The lists are built lazily, so a collapsed rung builds only the column it shows.</summary>
        LibrarySearchProps SearchPropsOf() => new()
        {
            ArtistHits = _artistHitRows, AlbumHits = _albumHitRows, ArtistAlbumHits = _artistAlbumHitRows, TrackHits = _trackHitRows,
            SelectedArtist = _sArtist.Value, SelectedAlbum = _sAlbum.Value,
            SelectArtist = _selectArtist, SelectAlbum = _selectAlbum,
        };

        /// <summary>The artists view's left column: the artist hits, each with the "why" eyebrow when the match is not the name.</summary>
        IReadOnlyList<LibraryHitRow> ArtistHitRows()
        {
            var artists = _hits.Artists;
            var rows = new LibraryHitRow[artists.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var a = artists[i];
                rows[i] = new LibraryHitRow(a.Slot, a.MatchStart, a.MatchLen, "", MatchEyebrow(a.Match));
            }
            return rows;
        }

        /// <summary>The albums view's left column: explained album hits (the match eyebrow, the "artist · year" line).</summary>
        IReadOnlyList<LibraryHitRow> AlbumHitRows()
        {
            var albums = _hits.Albums;
            var rows = new LibraryHitRow[albums.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var al = albums[i];
                rows[i] = new LibraryHitRow(al.Slot, al.MatchStart, al.MatchLen, AlbumSubtitle(al.Slot), MatchEyebrow(al.Match));
            }
            return rows;
        }

        /// <summary>The artists view's middle column: browse context under the selected artist (no eyebrow, no message — W23).</summary>
        IReadOnlyList<LibraryHitRow> ArtistAlbumHitRows()
        {
            var albums = AlbumsOfSelectedArtist();
            var rows = new LibraryHitRow[albums.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var al = albums[i];
                rows[i] = new LibraryHitRow(al.Slot, al.MatchStart, al.MatchLen, AlbumSubtitle(al.Slot), null);
            }
            return rows;
        }

        /// <summary>The selected album's track hits; a click plays the album from that track in place.</summary>
        IReadOnlyList<LibraryTrackHitRow> TrackHitRows()
        {
            var albums = IsArtists ? AlbumsOfSelectedArtist() : _hits.Albums;
            int ali = LibraryHits.IndexOf(albums, _sAlbum.Peek());
            if (ali < 0) return Array.Empty<LibraryTrackHitRow>();
            var album = albums[ali];
            var tracks = _hits.TracksOf(album);
            var rows = new LibraryTrackHitRow[tracks.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var t = tracks[i];
                int albumSlot = album.Slot, trackSlot = t.Slot;
                rows[i] = new LibraryTrackHitRow(trackSlot, t.MatchStart, t.MatchLen,
                    () => Playback.PlayContext(LibraryRows.IdOf(EntityKind.Album, albumSlot), LibraryRows.IdOf(EntityKind.Track, trackSlot)));
            }
            return rows;
        }

        static string AlbumSubtitle(int slot)
        {
            int year = LibraryRows.YearOf(EntityKind.Album, slot);
            var a = new Album(slot);
            string kind = Detail.Text.KindLabel(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album);
            return year > 0 ? year + " · " + kind : kind;
        }

        static string? MatchEyebrow(in MatchReason reason)
        {
            if (!reason.ShouldExplain) return null;
            return reason.Kind switch
            {
                LibraryMatchKind.Album => Strings.Library.MatchedAlbum(reason.Term!),
                LibraryMatchKind.Track => Strings.Library.MatchedSong(reason.Term!),
                _ => null,
            };
        }

        // Search hits SELECT IN PLACE (the pure LibrarySelectionCommit decides which signals move).
        void SelectArtist(int slot)
        {
            _sArtist.Value = slot;
            _sAlbum.Value = 0;
            Apply(LibrarySelectionCommit.ForArtist(IsArtists, Collapsed, LibraryRows.IdOf(EntityKind.Artist, slot).Text));
        }

        void SelectAlbum(int slot)
        {
            _sAlbum.Value = slot;
            int owner = _sArtist.Peek();
            Apply(LibrarySelectionCommit.ForAlbum(IsArtists, Collapsed, LibraryRows.IdOf(EntityKind.Album, slot).Text,
                owner > Table.None ? LibraryRows.IdOf(EntityKind.Artist, owner).Text : ""), albumPick: true);
        }

        /// <summary>The CORE rule's commit, applied. Its <c>Depth</c> is 1 for an album pick in the artists view because
        /// the BROWSE reader is one pane (<c>LibrarySelectionCommit.ForAlbum</c>, pinned by its test) — but SEARCH mode is
        /// still three columns, so an album pick there opens the THIRD rung, the track hits. The map lives here, at the
        /// page's consumption of <c>c.Depth</c>, and never in the pure rule.</summary>
        void Apply(in LibrarySelectionCommit c, bool albumPick = false)
        {
            if (c.SelectedKey is { } selected) SelectedKey.Value = selected;
            if (c.AlbumKey is { } album) AlbumKey.Value = album;
            if (c.ClearFilter && Filter.Peek().Length > 0) Filter.Value = "";
            if (c.Depth is { } d) _depth.Value = albumPick && d == 1 && IsArtists && _fullSearch && Collapsed ? 2 : d;
        }
    }
}

/// <summary>The Podcasts/Audiobooks split (A2 plan §3.6): pure over <see cref="ShowFlags"/>, the same bit
/// <see cref="Show.IsAudiobook"/> checks first — no episode-table scan here, because a library ROW filter runs over
/// every saved show on every relation change and must stay O(1) per show; the full <c>Show.IsAudiobook</c> (chapter
/// fallback for a show the WIRE never flagged) is the detail pane's own concern, not the library list's.</summary>
public static class LibraryAudiobookFilter
{
    public static bool IsAudiobookRow(ShowFlags flags) => (flags & ShowFlags.Audiobook) != 0;
}
