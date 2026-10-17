// ── Entities/Artist.Reader.cs — the library's artist READER: the core host (shape, demand, cells, spy) ──────────────
//
// Role: CORE host (`[CoreHost]`; T10c1). The PICTURE — the band, the name column, the two word rails, the cover spine, the block faces and their counted
//       skeletons, the tail — is Classic's `Library.ArtistReader` view (`Themes\Classic\Structure\Views\Pages\ClassicLibraryArtistReader*.cs`; keys in
//       `Theming\Contracts\ViewKeys.Library.cs`, props and the `IArtistReaderHost` / `IArtistReaderBlock` seam in `LibraryArtistReaderProps.cs`). What is here is
//       ReaderProps, Reader (the shape memo, demand, readiness, extents, spy, mount key, verbs) and the per-album BlockCell. The CORE it renders —
//       the block order, the extents and the two width arms — is `Artist.Reader.Shape.cs`.
// Owner: N (N-b wrote it); the narrow-pane arms are track L (#158)
// Wave: L2 (UI)
// Budget: 900 lines for BOTH halves (+30 % = 1170); this half alone is ~1,790 — the overrun is the props CHANNEL and
//         its per-album BlockCell (deviations 1-2 below), which replace machinery the plan's sketch assumed the engine
//         already did, plus the 2026-09-18 corrections: the LIKED-ONLY release group (and the block that paints only
//         its liked rows), the reader-width breakpoints, and the trailing item's three states — each one a behaviour
//         the plan had as a sentence and no code; the 2026-09-18 STICKY rework, which folded the band and the sub-rail
//         INTO the one scroller (a flat index space with a persistent prefix, two small item components and the
//         pinned-chrome clip) and turned the spine into a second virtualized list that follows it; the 2026-09-18
//         STABILIZATION pass (library-stabilization-plan.md R1-R4, C2), which added a truthful mount log, a catalogue
//         readiness gate and a per-dot version memo; and the 2026-10-01 narrow-pane arms (#158), which replaced W6's
//         one-row compact band with a STACKED band and gave the album head a stacked face of its own.
//         The split this note used to owe is DONE: the CORE half is `Artist.Reader.Shape.cs` (engine-free, and the only
//         thing the two reader test classes read); nothing left here is duplicated machinery to delete.
// Spec: docs/plans/wavee/library-rework-implementation.md §5.7 — W3/W4/W6 are the wireframes, §3.2 the component tree,
//       §4 the interaction contract, §6 the data & readiness rules, §7 the motion; the narrow-pane arms are
//       docs/plans/wavee/library-reader-narrow-heads-implementation.md §3.3-§3.6

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

// ══ CORE host: the reader (the picture is Classic's `Library.ArtistReader`, T10c1) ═══════════════════════════════════
//
// T10c1: this file is the DECIDING half. The structure's view (`ClassicLibraryArtistReader`) draws every element the sections below describe — the band, the sub-rail,
// the spine and its dots, the block faces and the tail — through `IArtistReaderHost` / `IArtistReaderBlock`; the mechanisms (sticky planes, the persistent prefix, the
// one shared clip, the per-album region cache, the per-dot memo) are unchanged and are the view's to keep. Where a paragraph says "built here", read "read here, drawn there".
//
// WHAT THE UI HALF IS (§5.7, W3/W4/W6). ONE scroller, and everything above the blocks is IN it. Its flat index space is
// `0` the artist band · `1` the sub-rail (the two word rails) · `Prefix + b` album block b — cover + caption on the
// left, the album's head (title link · meta · ▶ ♥ ⋯) and its tracks on the right — · last, the trailing facet line.
// Beside it, in the lane and NOT in the scroller, runs a 56-wide cover spine that is a second virtualized list.
//
// THE PROTOTYPE IS A STACK OF STICKY PLANES, AND SO IS THIS (`library-rework-mica.html`: `.subrail{top:0}`,
// `.spine{top:52px}`, `.block .side{top:64px}`). Three mechanisms carry it, all engine, none of them app-side geometry:
//   · The BAND scrolls away because it is simply the first item of the list.
//   · The SUB-RAIL pins because it is item 1 of the PERSISTENT PREFIX (`ListOptions.PersistentPrefixCount = Prefix`)
//     carrying one sticky scroll effect (`.Sticky(0)`), and because everything after the prefix is guillotined at
//     its lower edge by ONE shared clip (`ScrollOptions.ItemClipTopInset` + a 24-DIP `ItemClipTopFadeBand`) — never a
//     per-row clip. The persistent prefix exists ONLY on the BOUND realize path (the unbound recycler clamps it to 0,
//     `Reconciler.cs:3176`), which is why the list is `ItemsView.CreateBound` and each slot is a small `ReaderItem`
//     component over its index signal — the hero system's own shape (`Track.Table.VerticalList`, `TableVerticalItem`).
//   · The BLOCK COVER rides down its own block and stops at its bottom edge, because the cover column carries a sticky
//     of `SubRailH + BlockPadTop` and its containing block — the block's `AlignItems.Start` row — is the clamp.
// A sticky scroll effect (engine `Scroll/Effects`, `ScrollEffect.Sticky`) is a containing-block-clamped
// `position:sticky`, evaluated from the same device-snapped offset on the UI thread (hit-test) and the render thread
// (pixels); the reconciler re-bakes a node's effects wholesale on every re-render, so a recycled slot self-cleans.
//
// THE ONE THING THAT DECIDES WHERE A STICKY NODE MAY LIVE: a sticky with no named scope clamps to its IMMEDIATE parent,
// and a component anchor is layout-TRANSPARENT — it MIRRORS its child's size (`Reconciler.MirrorParticipation`). So a
// scope-less sticky root returned FROM a component sits in a parent of exactly its own height and never pins, and only
// a DIRECT child of the list's content is painted above the rows sliding under it (NodeFlags.StickyPinned). That is why
// `ItemAt` hands slot 1 back as a RAW element (its parent is then the scroller's content node, whose height is the
// whole list) while every other slot is a component, and why the cover's sticky sits on a plain column inside the
// block's own row rather than on anything a component returned.
//
// SMOOTHNESS IS THE POINT ("no animations, no loading states, clicking feels bad"). Four mechanisms, each named where it
// lives below:
//   1. THE MOUNT IS A HARD CUT, AND DATA NEVER TRIGGERS ONE (the 2026-09-18 stabilization, library-stabilization-plan.md
//      R1/R2). `ReaderMountPolicy.MountKey(artist, scope, sort, narrow)` is the ONLY remount key — it moves for what the
//      USER changed or what the layout freezes, never for a landing edge. No `Animate` on the keyed list: a key swap is
//      synchronous inside one flush, so the new band paints from resident `ArtistFields.Identity` in the SAME frame the
//      old one leaves — a hard cut, not a fade. A data-driven reshape (a facet page landing, a re-sort as a year lands)
//      is not a key change at all: the mounted list's realized slots re-render off `_shape` in place — `ReaderItem`
//      swaps its keyed block child (`blk:<albumSlot>`) under itself — and the count rides `ListOptions.CountSignal`,
//      the same chained-memo path an append always used, now the only path there is.
//   2. EVERY BLOCK CROSS-DISSOLVES. One `Loadable<int>` per ALBUM (not per realize) lives on its `BlockCell`, and the
//      readiness effect writes it; the mounted `Skel.Region` flips Pending→Ready on its own signal, with no list
//      re-render at all, and the engine cross-dissolves the counted skeleton into the real block — a plain opacity
//      fade (`SkelReveal.FadeOnly`), not the blur-rise `Soft` used to play: R1 found a blur-rise per block, stacked on
//      top of the old remount storm, read as flicker.
//      The region also re-diffs its content in place when the cell's VERSION moves (a title, a cover, a row count
//      landing) — `Reconciler.ReconcileSkeletonRegion` diffs an unchanged branch rather than replacing it.
//   3. THE SPINE IS A GLIDE, AND IT FOLLOWS. The ring/ink are BOUND props over `_current` (compositor-only, 167 ms brush
//      fade), a dot click is `StartBringItemIntoView(Prefix + b, 0f, animate: true)` — the engine's programmatic
//      glide, not a jump — and the spine's own viewport chases `_current` with a MINIMAL bring-into-view (alignment NaN)
//      from a layout effect, never from the scroll-spy watch (that one runs inline in the reactive flush).
//   4. THE BAND NEVER SKELETONS. It paints from `ArtistFields.Identity` the moment the navigator's row has it; an
//      unnamed artist shows "…" in the title only. Nothing here reads a frame clock.
//
// ZERO ALLOCATION ON A SCROLL FRAME. Every delegate is allocated once in the constructor; each album's cell caches its
// `Loadable`, its two builder thunks, its menu factory and the finished `SkelRegionEl`, so a realize is a dictionary
// lookup returning a cached `Element`. The block trees themselves are built at realize (bounded by the engine's
// velocity-sized realize window) and on a readiness edge — never per frame. On the bound path a SCROLL frame writes index signals: a slot that crosses a
// boundary re-renders (one `ReaderItem` / one `SpineDot`), everything else is a compositor write. The sorter and the
// comparator are the shape's (`Artist.Reader.Shape.cs`).
//
// DEVIATIONS FROM §5.7, and why (reported to the orchestrator):
//   1. The wrapper key carries NO data fact at all — not the order key, not a reshape generation (see 1 above,
//      library-stabilization-plan.md R2). §5.7 imagined the key riding the block sequence, but with the order key (or a
//      generation derived from it) in the key, a landed facet page remounted the whole list and faded it — RC6/RC7 of
//      the stabilization investigation. `ReaderMountPolicy.MountKey` mints from artist/scope/sort/narrow only.
//      `ItemCount` freezes at mount on `ItemsView` (`ItemsView.DebugCheckReuse` says so in as many words), so EVERY
//      reshape — append or not — reaches the list through `CountSignal`, a chained `Memo<int>` over the shape (no
//      signal written during a render); a block whose position or identity changed re-renders through its own keyed
//      child, never through the list's mount key.
//   2. Readiness is `AlbumPaneReadiness.Of` (O1's tested rule) on a per-album `Loadable`, not a fresh
//      `Loadable<int>.Ready/Pending()` per `BlockBody` call: a Loadable minted inside the item template can never flip
//      (on the bound path the template runs ONCE per slot), which is the "skeleton forever" bug §5.7 warns
//      about one paragraph earlier. A FAILED verdict resolves the region to Ready and the block paints its own Retry
//      strip, so no `Exception` is ever allocated to carry a state the block already shows.
//   3. The block's ▶ is `Controls.PlayFab(play, Icons.Play, 32f)` under `Controls.Named`, not
//      `ClassicDetailKit.CommandCircle(…) with { Fill = Tok.AccentDefault }`: `CommandCircle` returns `Element` (it wraps its box in
//      a tooltip), so `with { Fill = … }` does not compile, and `Interaction.Subtle` OWNS the circle's `Fill` — an accent
//      rest face has to come from a control that declares one. `PlayFab` is that control, at the head's 32-DIP tier.
//   4. The head's meta is `Detail.Text.AlbumMeta(n, totalMs, durationsKnown)`, not `Detail.Identity.For(a).Meta`:
//      `Identity.For` allocates a record plus a `List<Controls.Face>` of billed artists this surface never draws and
//      walks the tracklist a second time. (The meta line carries no year anywhere: it belongs to the cover's caption
//      here, W3, and to the eyebrow on the album page.)
//   5. The shimmer branch draws the head's title/meta/verbs as explicitly-sized boxes instead of live text and live
//      `SaveButton`/`MoreButton` components. `SkeletonDeriver` derives the shimmer from this tree, so an empty title
//      would derive a zero-width bar, and `Skel.Region`'s own doc forbids mounting stateful components during load.
//      Each face declares the same stated height per arm (`ReaderShape.HeadHeight`), which is what keeps
//      `ReaderShape.ExtentOf` honest across the reveal.
//   6. `SlotOfKey` is local: the page's is `private static` inside `User` (`User.Page.Library.cs:80`), and that file is
//      owner O3's. Same two lines, same prefix constant (`SidebarPinId.AlbumPrefix`).
//   7. Every item ROOT is a plain stretched column — `Direction 1 / MinWidth 0`, no `Grow`/`Basis`. On the UNBOUND path
//      an item was wrapped by the selector skin in a ROW box (`SelectorVisuals.None` → `BoxEl { Padding, Children }`,
//      Direction 0), so it sat on that row's MAIN axis and needed `Grow 1 / Basis 0` or it measured its intrinsic width
//      (the cover's 140 DIP) and clipped everything right of the cover to nothing. The BOUND path has no selector skin
//      at all — the template returns the complete slot root, mounted straight under the scroller's content node through
//      one layout-transparent anchor — and that node is a COLUMN with `AlignItems.Stretch`. Full width comes for free,
//      and the same `Basis 0 / Grow 1` would now aim at the HEIGHT and zero every block. `Track.Table`'s `TableSlot`,
//      `HeroItem` and `FooterItem` are all this shape. The band, the sub-rail and the tail additionally DECLARE their
//      stated height, which is what makes analytic extent == measured extent for the three rows `ReaderShape.ExtentOf`
//      cannot answer.
//   8. `Skel.Region(…, smoothResize: false)`. A Reflow size track inside a MEASURED virtualized item fights the extent
//      table (`Album.Pane.cs:168` says the same thing for the pane), and the block's reveal is extent-exact by
//      construction — the counted skeleton is the same geometry as the content.
//   9. TWO reader-width edges, not the plan's one at 1200 (the reader is 344-650 DIP in a non-maximized window, so a
//      1200-DIP arm was never seen), decided by the reader's two arms (#158): under the narrow edge (640) the block cover
//      drops 120 → 88 and the album head STACKS (`_narrow` — the arm the extents and the list mount key read); under
//      the medium edge (748, derived) the spine is gone and the band STACKS (`_stacked` — not a mount input; the band's
//      extent is corrected in place). Since TC6 both edges are the structure's reader ladder (`ArtistLadders.Reader`, Classic
//      `Rules\Tiers.Artist.cs`: 24 DIP of hold on widening each), over the width `OnBoundsChanged` writes. W6's
//      one-row compact band, which this replaced, crushed the name to "Tro…" at 391 DIP.
//  10. The SPINE is `ItemsView.CreateBound` with a `SpineDot` component per slot, not the plain `ItemsView.Create` over
//      a cached-Element template the rework note asked for. A `Create` template is re-invoked only when the ItemsView
//      component itself re-renders, and that component is mounted through a PROPLESS `Embed.Comp` — a parent re-render
//      never reaches it (`Reconciler.cs:1039-1090`: "the component is AUTONOMOUS"). So a cover landing after the count
//      settled would never have repainted a realized dot, which is a regression on the old eager column; and a cached
//      element handed to a positional recycler crosses album identities on a scroll. A component per slot reads the
//      album table itself and re-renders exactly on a recycle or a publish. For the same reason there is no per-slot
//      Element cache: a cached dot cannot observe its own cover.
//  11. `ListOptions.KeyOf` and `Selector` are declared on the main list but IGNORED by the bound factory — the
//      non-generic `ItemsView.CreateBound` forwards neither (`ItemsView.cs:651-700`; the typed `CreateBound<T>` does
//      forward `KeyOf`, at :746), because a bound slot is keyed by its index signal and carries no container skin.
//      They stay as the true statement of the list's identity; see the report's engine notes.

public readonly partial struct Artist
{
    /// <summary>Re-pushed props. The three <see cref="Signal{T}"/> INSTANCES are the page's persisted state, so equality
    /// is the slot plus reference identity — a re-push with the same signals and the same artist is not a change.
    /// <paramref name="AlbumKey"/> is the search select-in-place's target: the reader reads it once, brings that block to
    /// the top, and clears it.</summary>
    public sealed record ReaderProps(int Slot, Signal<int> Scope, Signal<int> Sort, Signal<string> AlbumKey)
    {
        public bool Equals(ReaderProps? other)
            => other is not null && other.Slot == Slot
               && ReferenceEquals(other.Scope, Scope) && ReferenceEquals(other.Sort, Sort)
               && ReferenceEquals(other.AlbumKey, AlbumKey);

        public override int GetHashCode() => Slot;
    }

    /// <summary>The library's artist reader (§5.7): band · word rails · spine · one virtualized list of album blocks.
    /// <para>It owns its props CHANNEL (<see cref="IPropsHost"/>, the <c>ClassicDetailKit.MoreButton</c> host's pattern) instead of
    /// reading them through <c>UseProps</c>: the shape is a memo, and a memo only recomputes when a SIGNAL it read
    /// changed. With the slot arriving as a plain field, selecting a different artist would re-render the band over the
    /// previous artist's blocks — so the props land in a signal the shape memo reads, and one flush carries both.</para></summary>
    [CoreHost]
    public sealed class Reader : Component, IPropsHost, IArtistReaderHost
    {
        // ── metrics ──────────────────────────────────────────────────────────────────────────────────────────────────
        // Every band and block metric — the avatar, the circles, the paddings, the two width edges — is `ReaderShape`'s:
        // the tree declares the same fields the estimator and the tests read. What stays here is the lane's own.

        /// <summary>The two CHROME items at the head of the one scroller: 0 = the artist band (scrolls away), 1 = the
        /// sub-rail (pinned at the viewport top). Everything after them is a block, and the last index is the tail —
        /// so every flat index the reader hands the list is <c>Prefix + blockIndex</c>, and every index the list hands
        /// back is <c>flat − Prefix</c>. Two, and named, because five call sites have to agree about it
        /// (<see cref="_extentOf"/>, <see cref="_keyOf"/>, the spy, the seek and <see cref="Settle"/>'s corrections),
        /// and because <see cref="ListOptions.PersistentPrefixCount"/> reads the same number.
        /// <para>The persistent prefix works ONLY on the BOUND path: the unbound recycler clamps it to 0
        /// (<c>Reconciler.cs:3176</c> — <c>ve.RowBind is null ? 0 : …</c>), which is half the reason the list moved to
        /// <c>ItemsView.CreateBound</c>.</para></summary>
        const int Prefix = ReaderShape.Prefix;

        /// <summary>The sub-rail's STATED height; the band's two are <see cref="ReaderShape.BandHeight"/> (100 wide, 164
        /// stacked). Constants rather than measurements because <see cref="_extentOf"/> has to answer indices 0 and 1
        /// BEFORE either item renders (the same contract <see cref="ReaderShape.ExtentOf"/> is under), and because a
        /// pinned rail whose analytic extent disagrees with its measured one leaves a seam between the rail and the first
        /// block.
        /// <para>TRUTHFUL by construction, not by guess: the sub-rail is one <c>Controls.Words.Rail</c>, which declares
        /// <c>Height = Controls.Words.Height</c> (32 DIP) outright, inside the rail row's own padding,
        /// plus the 1-DIP divider under it; each band arm's root declares <c>ReaderShape.BandHeight(stacked)</c> as its
        /// <c>Height</c>. Every chrome root DECLARES the number the table is seeded with, so analytic == measured by
        /// identity — and <see cref="CheckChrome"/> logs the exception if a root ever disagrees.</para></summary>
        const float SubRailH = ReaderShape.SubRailH;   // 6 + Controls.Words.Height + 10 + 1

        /// <summary>The seed a block gets before <see cref="ReaderShape.ExtentOf"/> can answer (index out of range during
        /// a resize) — a mid-sized block, so a pre-shape content extent is never absurd.</summary>
        const float EstimatedBlockExtent = 320f;
        /// <summary>The three heights the trailing item can be — air, one line, or the "nothing here yet" vacancy. They
        /// are constants because <see cref="TailExtent"/> has to answer them BEFORE the item renders (the same contract
        /// <see cref="ReaderShape.ExtentOf"/> is under), from the same <see cref="TailKind"/> decision the item reads.</summary>
        const float TailLineH = 48f, TailVacancyH = 208f;
        static float TailAirH => Spacing.L;


        // ── state ────────────────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>The re-pushed props as a SIGNAL (seeded before the first render, written inside the reconciler's batch
        /// on every re-push, equality-gated by <see cref="ReaderProps"/>'s own Equals).</summary>
        readonly Signal<ReaderProps?> _propsSig = new(null);
        ReaderProps? _p;
        IOverlayService? _overlay;
        int _artist;

        // The shape's pooled buffers (`ReaderShape.Build` fills them; nothing here derives an order). `_library*` are the
        // three PARALLEL rows of `User.LibraryReleasesOf` — slot, "liked-only", and that block's liked-track count — which
        // `Build` compacts in step (the shape file's deviation 4); `_likedScratch` is the demand pass's row buffer.
        ReaderBlock[] _blocks = new ReaderBlock[32];
        int[] _libraryAlbums = new int[32], _libraryRows = new int[32];
        bool[] _libraryLiked = new bool[32];
        int[] _scratch = new int[96], _perm = new int[96], _likedScratch = new int[32];
        float[] _extents = new float[32];
        float _tailExtent = TailAirH;
        /// <summary>What the extent table was last told about item 0, the band — its <see cref="_extents"/> entry, kept
        /// apart because the band is not a block. Seeded in <see cref="SeedExtents"/>, corrected in <see cref="Settle"/>.</summary>
        float _bandExtent = ReaderShape.BandH;
        int _count, _library;
        /// <summary>The two arms as THIS render read them (<see cref="_arm"/>): the extent
        /// thunk, the block builders and the chrome check read these, never the signals, so a measure inside the layout
        /// pass cannot subscribe anything.</summary>
        bool _narrow, _stacked;
        /// <summary>R3: is scope 1's catalogue union withheld THIS compute because one of the three facets' first page
        /// has neither answered nor failed yet? <see cref="TailKind"/> and <see cref="ReadTail"/> read it (the tail
        /// line reads differently while gated) — set only inside <see cref="Compute"/>, the one place that knows.</summary>
        bool _catalogueGated;
        /// <summary>R4: the PREVIOUS logged shape's album-slot sequence — a pooled <c>int[]</c> that replaces the old
        /// `_order` block buffer, which existed only to decide a remount (R2 removed that decision entirely) — plus
        /// the artist/scope/sort it was logged under, so the next `library.reader.shape` line can say WHY it fired: a
        /// new artist/scope/sort, an appended tail, a reordered sequence, or the SAVED/LIKED-ONLY flags moving under an
        /// otherwise unchanged sequence.</summary>
        int[] _prevSlots = new int[32];
        int _prevSlotCount;
        int _loggedArtist = Table.None, _loggedScope = -1, _loggedSort = -1;

        // The mounted list. `_layout` and `_options` are rebuilt ONLY when the list remounts, because both freeze at mount.
        readonly ItemsViewController _ctl = new();
        /// <summary>The reader list's scroll handle (every remount re-binds the same one): the scroll-spy watches it.</summary>
        readonly ScrollHandle _listScroll = new();
        string _scrollScope = "";
        /// <summary>The SPINE's own viewport (§3 of the rework: the spine is a second, narrow virtualized list that
        /// follows the first). It is keyed once and never remounts, so this handle is live for the reader's whole
        /// lifetime — which is what lets the follow effect glide it without ever touching the main list.</summary>
        readonly ItemsViewController _spineCtl = new();
        RepeatLayout _layout;
        ListOptions _options;
        string _listKey = "", _extentKey = "", _spyKey = "", _loggedOrder = "";
        int _visit, _lastArtist = Table.None;
        /// <summary>R4: fires the truthful mount log from the keyed list's <see cref="BoxEl.OnRealized"/> — allocated
        /// once, like every other delegate this component owns.</summary>
        readonly Action<NodeHandle> _onListMounted;

        readonly Signal<int> _current = new(0);        // the spine's highlighted block (scroll-spy; signals only)
        /// <summary>The reader's own arranged width, written from <see cref="OnBounds"/> (0 until the first measure: the seed answers until then).</summary>
        readonly Signal<float> _readerW = new(0f);
        /// <summary>The reader's ARM on the structure's reader ladder (<see cref="Theming.ArtistLadders.Reader"/>, TC6), resolved over <see cref="_readerW"/>:
        /// NARROW (cover 88 + the stacked album head: the list's mount input, so a flip reseeds every block extent), MEDIUM (the spine is GONE and the band
        /// STACKS: NOT a mount input, the band's extent is corrected in place, <see cref="Settle"/>) and wide. One ladder for both edges, so the two arms can
        /// never disagree for a frame; a crossing found after mount commits on the first frame that is not resized.</summary>
        IReadSignal<int>? _arm;
        readonly Func<float> _readerSeed;
        /// <summary>The facts this reader is drawn in (`TierContext.DrawnIn`, set in render): its root's, never the process's last window. The seeds
        /// read them before the first render through the process's.</summary>
        IReadSignal<Theming.ThemeFacts>? _facts;
        /// <summary>The narrow arm, read TRACKED (the running render or effect re-runs on a crossing).</summary>
        bool NarrowArm => (_arm?.Value ?? Theming.ArtistLadders.ReaderArms - 1) == Theming.ArtistLadders.ReaderNarrow;
        /// <summary>The medium (or narrow) arm: no spine and the stacked band, read TRACKED.</summary>
        bool MediumArm => (_arm?.Value ?? Theming.ArtistLadders.ReaderArms - 1) <= Theming.ArtistLadders.ReaderMedium;
        /// <summary>One cell per ALBUM, minted on its first realize: it owns that block's readiness signal, its two
        /// builder thunks, its menu factory and the finished region element, so a re-realize allocates nothing.</summary>
        readonly Dictionary<int, BlockCell> _cells = new();

        // Every delegate this component owns, allocated once here (§5's rule).
        readonly Func<ReaderShapeKey> _compute;
        readonly Func<int> _countOf, _spineCountOf, _totalOf;
        readonly Func<int, float> _extentOf;
        readonly Func<int, string> _keyOf;
        readonly Action _demandBand, _demandBlocks, _settle, _seekAlbumKey, _playAll, _shuffleAll, _goArtist, _followSpine, _showAll;
        readonly Action<int, int> _onVisibleRange;
        readonly Action<RectF> _onBounds, _onBandBounds;
        readonly Action _watchSpy;
        /// <summary>The band root's last LOGGED disagreement (<see cref="CheckChrome"/>'s dedupe; NaN = none yet).</summary>
        float _bandLoggedDeclared = float.NaN, _bandLoggedMeasured = float.NaN;
        readonly Prop<int> _total;
        Memo<ReaderShapeKey>? _shape;
        Memo<int>? _listCount, _spineCount;
        /// <summary>The viewport-height fraction the scroll-spy samples at — recomputed from the live geometry so the
        /// probe lands just BELOW the pinned sub-rail rather than under it. A fixed 0.2 read the rail's own strip at a
        /// tall viewport and the band's at a short one; the block that "is" the current one is the one whose top edge
        /// has just cleared the rail.</summary>
        float _spyRatio = 0.2f;

        public Reader()
        {
            _compute = Compute;
            // Prefix + blocks + 1: the band, the sub-rail, every block, and the trailing facet line — ONE flat index
            // space, because there is now ONE scroller.
            _countOf = () => Prefix + _shape!.Value.Count + 1;
            _spineCountOf = () => _shape!.Value.Count;                      // the spine has no chrome and no tail
            // The rail's "all releases · N" reads the SHAPE, not the edge tables: a memo read inside a Prop thunk is
            // tracked, a plain `EdgeTable.Total(_artist)` is not — and the rail element is cached across artists, so the
            // untracked form froze at the first artist's number (the shape file's deviation 5).
            _totalOf = () => _shape?.Value.TotalReleases ?? 0;
            // FLAT index space: 0 band · 1 sub-rail · Prefix + b block b · Prefix + _count the tail. The two chrome
            // extents are the stated constants, so the analytic table and the declared item heights are one number.
            _extentOf = i => i == 0 ? ReaderShape.BandHeight(_stacked)
                           : i == 1 ? SubRailH
                           : i - Prefix == _count ? _tailExtent
                           : (uint)(i - Prefix) < (uint)_count ? ReaderShape.ExtentOf(in _blocks[i - Prefix], _narrow)
                           : EstimatedBlockExtent;
            _keyOf = i => i == 0 ? "band"
                        : i == 1 ? "rail"
                        : (uint)(i - Prefix) < (uint)_count
                            ? "blk:" + _blocks[i - Prefix].AlbumSlot.ToString(CultureInfo.InvariantCulture)
                            : "tail";
            _demandBand = () => { var a = new Artist(_artist); if (a.IsValid) Entities.Ensure(a, ArtistFields.Identity | ArtistFields.Stats); };
            _demandBlocks = DemandBlocks;
            _settle = Settle;
            _seekAlbumKey = SeekAlbumKey;
            _playAll = () => { var a = new Artist(_artist); if (a.IsValid) Playback.PlayOrToggleContext(a.Id); };
            _shuffleAll = () =>
            {
                var a = new Artist(_artist);
                if (!a.IsValid) return;
                Playback.SetShuffle(true);
                Playback.PlayContext(a.Id);
            };
            _goArtist = () => { var a = new Artist(_artist); if (a.IsValid) Shell.GoTo(Shell.For(a.Uri, a.Name)); };
            _followSpine = FollowSpine;
            _showAll = ShowAllReleases;
            _onVisibleRange = OnVisibleRange;
            _onBounds = OnBounds;
            // Before the first measure the reader is seeded from the content region the frame resolved the window tiers on (an upper bound of the lane).
            _readerSeed = () => (_facts ?? Theming.ThemeHost.Facts).Peek().Tiers.ContentWidth is var w && w > 0f ? w : float.MaxValue;
            _onBandBounds = r => CheckChrome("band", Table.None, _stacked ? "stacked" : "wide", ReaderShape.BandHeight(_stacked),
                                             r.H, ref _bandLoggedDeclared, ref _bandLoggedMeasured);
            _onListMounted = OnListMounted;
            _watchSpy = WatchSpy;
            _total = Prop.Of(_totalOf);
            _layout = RepeatLayout.Extents(_extentOf, EstimatedBlockExtent);
            _options = OptionsFor("");
        }

        /// <summary>The props sink. A write here marks BOTH the render effect and the shape memo stale, and the reconciler
        /// has already wrapped it in a batch, so the two drain in one flush — never a band ahead of its blocks.</summary>
        public void ApplyProps(object props)
        {
            var next = (ReaderProps)props;
            _p = next;
            _propsSig.Value = next;
        }

        public override Element Render()
        {
            var p = _propsSig.Value;                                         // subscribe: a re-push re-renders
            _overlay = UseContext(Overlay.Service);
            _scrollScope = UseContext(Shell.PageScrollScope);                          // the tab: composed onto the restore key
            uint epoch = Entities.ScopeEpoch.Value;
            // The BAND and the SPINE DOTS are painted values, not binds — but neither is built here any more: the band
            // is item 0 of the list and a dot is an item of the spine, and each of those components subscribes to the
            // table it paints from (`Artists.Changed` for the band, `Albums.Changed` for a cover). Subscribing to them
            // HERE as well would re-render the whole reader — the lane and both list hosts — every time one artist row
            // in the navigator's page landed. `Tracks`/`AlbumTracks` were never here for the same reason: a landing
            // row reaches its block through the block's own readiness signal.
            _artist = p?.Slot ?? Table.None;
            _facts = Theming.TierContext.DrawnIn(UseContext(Theming.ThemeFactsContext.Signal));
            _arm = UseTier(Theming.ArtistLadders.Of(ShellPolicy.Now.Tiers, Theming.ArtistLadders.Reader), TierSource.Measured(_readerW, _readerSeed));
            _narrow = NarrowArm;
            _stacked = MediumArm;                                            // subscribe: the spine's presence is a tree edit
            bool spine = !_stacked;
            _shape = UseComputed(_compute);
            _listCount = UseComputed(_countOf);
            _spineCount = UseComputed(_spineCountOf);
            UseEffect(_demandBand, DepKey.From(_artist, (int)epoch));
            UseEffect(_demandBlocks);                                        // auto-tracked: re-runs as edges/scope land
            UseEffect(_settle);                                              // readiness + the measured-extent corrections
            // The spine FOLLOWS the reader: a layout effect, so the glide is posted after the frame's layout settles and
            // the target row's band is real. Never from `WatchSpy` — that runs inline in the reactive flush, where a
            // programmatic scroll would be posted against geometry that frame has not laid out yet.
            UseLayoutEffect(_followSpine);
            UseSignalEffect(_watchSpy);
            var shape = _shape.Value;
            // Hooks first, then the guard: props are SEEDED before the first render, so null is unreachable after mount —
            // but a conditional hook would be a real bug, and this is the shipped order (`EagerTrackRowHost`).
            UseEffect(_seekAlbumKey, (p?.AlbumKey.Value ?? "") + "|" + shape.OrderKey);
            if (p is null) return Slots.View(ViewKeys.LibraryArtistReader, new LibraryArtistReaderProps { Host = this, Vacant = true });

            // A NEW ARTIST forgets the album cell cache (a stale cell keyed by another artist's album slot must never
            // paint into this pane) and opens the reader at its own top. R2 removed the reshape generation this reset
            // used to also clear (`_orderCount`/`_lastOrder`): the mount key no longer carries one, so there is nothing
            // left here but the cache and the scroll visit — plus forcing the next shape log (see Compute), so a hash
            // coincidence between two different artists' blocks can never swallow it.
            if (_lastArtist != _artist)
            {
                _lastArtist = _artist;
                _visit++;                                                    // a new selection opens at the TOP (see OptionsFor)
                _cells.Clear();
                _loggedOrder = "";
            }
            // The TRIPLE is what owns a remembered scroll offset (artist · scope · sort — the Recents pivot rule). The
            // MOUNT key is R2's: artist, scope, sort and the NARROW arm — every input FROZEN at mount — and NOTHING a data
            // landing can move. A reshape (a facet page, a re-sort) never reaches here at all; the mounted list's
            // realized slots re-render off `_shape` in place instead (see the class remarks, mechanism 1). The MEDIUM arm
            // is deliberately not in it: the band it reshapes is one row whose extent `Settle` corrects in place.
            string triple = _artist.ToString(CultureInfo.InvariantCulture) + ":" + shape.Scope + ":" + shape.Sort;
            string key = ReaderMountPolicy.MountKey(_artist, shape.Scope, shape.Sort, _narrow);
            if (!string.Equals(_listKey, key, StringComparison.Ordinal)) Rebase(key, triple);

            // The structure draws the lane (the keyed list host, the spine beside it, the band and the pinned sub-rail as items 0 and 1 of the one scroller). It is handed
            // the mount's layout and options (rebuilt only when the mount key moves) and the host seam its slots read live state through.
            return Slots.View(ViewKeys.LibraryArtistReader, new LibraryArtistReaderProps
            {
                Host = this, ListKey = _listKey, ItemCount = Prefix + _count + 1, BlockCount = _count,
                Layout = _layout, Options = _options, Spine = spine, Scope = () => p.Scope, Sort = () => p.Sort,
                SpineController = _spineCtl, SpineCount = _spineCount, OnBounds = _onBounds, OnListMounted = _onListMounted,
            });
        }

        // ── the mounted list: one layout + one options record per MOUNT (both freeze at mount) ────────────────────────

        /// <summary>One options record per MOUNT. <see cref="ListOptions.PersistentPrefixCount"/> is what makes the band
        /// and the sub-rail REAL items that are never recycled and never clipped, and
        /// <see cref="ScrollOptions.ItemClipTopInset"/> is the single shared band that guillotines the recyclable suffix
        /// at the rail's lower edge — one clip owner for the whole list, never a per-row clip (the hero system's own
        /// contract, <c>Track.Table.cs:1094-1127</c>).
        /// <para><c>AutoEdgeFade</c> is OFF here for the same reason the reference turns it off: the top feather is
        /// already owned by <see cref="ScrollOptions.ItemClipTopFadeBand"/> under the pinned rail, and a second
        /// alpha-mask pass over the whole viewport would feather the rail itself.</para>
        /// <para><c>KeyOf</c> is declared and, today, IGNORED: the bound factory does not forward it
        /// (<c>ItemsView.cs:651-700</c> — the typed <c>CreateBound&lt;T&gt;</c> at :746 does), because a bound slot is
        /// keyed by its index signal rather than by a diff key. It stays because it is the true statement of this
        /// list's identity and costs one field; see the report.</para></summary>
        ListOptions OptionsFor(string triple) => new()
        {
            SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None, Controller = _ctl,
            Grow = 1f, KeyOf = _keyOf, CountSignal = _listCount, OnVisibleRange = _onVisibleRange,
            PersistentPrefixCount = Prefix,
            // Each (artist, scope, sort) triple remembers its OWN offset — so a RESHAPE under the same triple restores
            // where the reader was, and tapping a sort word lands where that sort last was — but only WITHIN one visit:
            // the band is item 0 of this scroller now, so an offset restored across selections opened some artists with
            // their header scrolled away and others at the top. `_visit` moves on every artist change.
            Scroll = new ScrollOptions
            {
                ScrollKey = _scrollScope + "lib:reader:" + triple + ":" + _visit.ToString(CultureInfo.InvariantCulture), AutoEdgeFade = false, Handle = _listScroll,
                ItemClipTopInset = SubRailH, ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
            },
        };

        /// <summary>A new mounted list: a fresh analytic layout (its extent table must not carry another artist's rows),
        /// fresh options for the scroll key, and the extent cache re-seeded to what <see cref="_extentOf"/> will seed the
        /// table with, so the first correction diff is honest.</summary>
        void Rebase(string key, string triple)
        {
            _listKey = key;
            _layout = RepeatLayout.Extents(_extentOf, EstimatedBlockExtent);
            _options = OptionsFor(triple);
            SeedExtents();
        }

        /// <summary>R4: the TRUTHFUL mount signal — <c>OnRealized</c> fires only when the node is actually (re)created,
        /// never on an in-place update, so this line is proof a remount happened (unlike the old `library.nav.remount`
        /// RC2 documented as firing from the key STRING). One line per real mount; a data reshape never reaches it,
        /// because R2 means a reshape never changes <see cref="_listKey"/> in the first place.</summary>
        void OnListMounted(NodeHandle h)
        {
            Log.Event(WaveeLogLevel.Info, "ui", "library.reader.mount", "Artist reader list mounted", null, -1, null,
                WaveeLogField.Of("artist", _artist), WaveeLogField.Of("scope", _p?.Scope.Peek() ?? 0),
                WaveeLogField.Of("sort", _p?.Sort.Peek() ?? 0), WaveeLogField.Of("narrow", _narrow),
                WaveeLogField.Of("stacked", _stacked), WaveeLogField.Of("key", _listKey));
        }

        /// <summary>A fresh mount: the extent cache mirrors what the new table will seed itself with — the band, the
        /// blocks AND the trailing item, each a row of the list like any other.</summary>
        void SeedExtents()
        {
            _bandExtent = ReaderShape.BandHeight(_stacked);
            if (_extents.Length < _count) _extents = new float[Math.Max(_count, _extents.Length * 2)];
            for (int i = 0; i < _count; i++) _extents[i] = ReaderShape.ExtentOf(in _blocks[i], _narrow);
            _tailExtent = TailHeight(TailKind());
            _extentKey = _listKey;
        }

        /// <summary>An APPEND grew the block count: copy the cache (a surviving row's pending correction must not be
        /// forgotten — that is exactly the row whose `TrackCount` landed late) and seed only the appended tail, which is
        /// also all `ExtentTable.Resize` seeds.</summary>
        void GrowExtents()
        {
            int old = _extents.Length;
            var next = new float[Math.Max(_count, old * 2)];
            Array.Copy(_extents, next, old);
            for (int i = old; i < _count; i++) next[i] = ReaderShape.ExtentOf(in _blocks[i], _narrow);
            _extents = next;
        }

        // ── shape ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The shape memo: the shape's pure `ReaderShape.Build` over pooled buffers. It writes NO signal (the count crosses into the
        /// list as a chained memo instead) and it is the one place the order key is minted.</summary>
        ReaderShapeKey Compute()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = e.SavedAlbums.Changed.Value; _ = e.AlbumArtists.Changed.Value; _ = e.AlbumTracks.Changed.Value;
            _ = e.ArtistAlbums.Changed.Value; _ = e.ArtistSingles.Changed.Value; _ = e.ArtistCompilations.Changed.Value;
            // The LIKED relation and the track→artist credits are what group 2 (the liked-only releases) is made of, so
            // the shape moves with them. `scope.Tracks.Changed` is deliberately NOT here: the one track column the shape
            // reads (`Tracks.Album`) lands WITH `Edges.TrackArtists` (User.cs §11), in the same publish.
            _ = e.Liked.Changed.Value; _ = e.TrackArtists.Changed.Value;
            _ = scope.Albums.Changed.Value;
            // The PROPS are read here, reactively: this is what makes selecting another artist recompute the shape in the
            // same flush that re-renders the band (see the class remarks).
            var p = _propsSig.Value;
            if (p is null) { _count = 0; _library = 0; _catalogueGated = false; return ReaderShapeKey.Empty; }
            int artist = p.Slot;
            _artist = artist;
            int scopeWord = p.Scope.Value;
            var sort = ReaderShape.SortOf(p.Sort.Value);
            if (artist <= Table.None) { _count = 0; _library = 0; _catalogueGated = false; return ReaderShapeKey.Empty; }

            int lib = User.LibraryReleasesOf(artist, _libraryAlbums, _libraryLiked);
            if (lib > _libraryAlbums.Length)                                  // the total, not what fitted: grow and re-read
            {
                int size = Math.Max(lib, _libraryAlbums.Length * 2);
                _libraryAlbums = new int[size];
                _libraryLiked = new bool[size];
                _libraryRows = new int[size];
                lib = User.LibraryReleasesOf(artist, _libraryAlbums, _libraryLiked);
            }
            if (_libraryRows.Length < _libraryAlbums.Length) _libraryRows = new int[_libraryAlbums.Length];
            lib = Math.Min(lib, _libraryAlbums.Length);
            // The liked-track count per liked-only release — its block's row count exactly, and the one thing `Build`
            // cannot derive from a slot. Counted through an EMPTY span, so nothing is allocated; O(liked) per liked-only
            // release, and there are ones of those, not thousands (a compute runs on an edge, never per frame).
            for (int i = 0; i < lib; i++)
                _libraryRows[i] = _libraryLiked[i] ? User.LikedTracksOfAlbum(artist, _libraryAlbums[i], default) : 0;

            var albums = e.ArtistAlbums.Targets(artist);
            var singles = e.ArtistSingles.Targets(artist);
            var comps = e.ArtistCompilations.Targets(artist);
            // R3: scope 1's catalogue union lands ONCE, as a pure append, instead of facet by facet (RC7/F13 — three
            // separate reshapes interleaving by date, each one a dissolve of its own). Gated, `Build` sees only the
            // library group; `_catalogueGated` is read back by `TailKind`/`Trailing` (the tail line) and by the return
            // below (the rail's total holds at the bare word until the gate opens).
            _catalogueGated = scopeWord == 1 && !ReaderMountPolicy.CatalogueReady(
                e.ArtistAlbums.State(artist), e.ArtistSingles.State(artist), e.ArtistCompilations.State(artist),
                e.ArtistAlbums.IsFailed(artist), e.ArtistSingles.IsFailed(artist), e.ArtistCompilations.IsFailed(artist));
            if (_catalogueGated) { albums = default; singles = default; comps = default; }
            int cap = ReaderShape.Capacity(lib, albums.Length, singles.Length, comps.Length);
            if (_scratch.Length < cap) { _scratch = new int[cap]; _perm = new int[cap]; }
            if (_blocks.Length < cap) _blocks = new ReaderBlock[cap];
            _count = ReaderShape.Build(artist, scopeWord, sort, _libraryAlbums, _libraryLiked, _libraryRows, lib,
                                       albums, singles, comps, _scratch, _perm, _blocks, out _library);
            string order = ReaderShape.OrderKey(_blocks.AsSpan(0, _count));
            if (!string.Equals(order, _loggedOrder, StringComparison.Ordinal))
            {
                // R4: WHY did the sequence move — never guessed from the outside again (RC2's lesson). `reason` checks
                // the coarsest fact first (a different artist/scope/sort explains everything downstream of it), then
                // classifies the block sequence itself against the PREVIOUS logged one: an exact prefix match is an
                // append (a facet page landing), an identical sequence of a different length than expected doesn't
                // occur (covered by append/the shrink fallthrough to reorder), and an IDENTICAL sequence whose OrderKey
                // still moved can only be the SAVED/LIKED-ONLY bits flipping (`flags` — the RC7 "Year landed" suspect
                // when it ISN'T this and isn't `append` either: a same-count `reorder` is the re-sort signature).
                string reason;
                int firstDiff = -1;
                if (_loggedArtist != artist) reason = "artist";
                else if (_loggedScope != scopeWord) reason = "scope";
                else if (_loggedSort != (int)sort) reason = "sort";
                else
                {
                    int minLen = Math.Min(_prevSlotCount, _count);
                    for (int i = 0; i < minLen; i++)
                        if (_prevSlots[i] != _blocks[i].AlbumSlot) { firstDiff = i; break; }
                    if (firstDiff < 0 && _prevSlotCount != _count) firstDiff = minLen;
                    reason = firstDiff < 0 ? "flags" : firstDiff >= _prevSlotCount ? "append" : "reorder";
                }
                Log.Event(WaveeLogLevel.Info, "ui", "library.reader.shape", "Artist reader reshaped", null, -1, null,
                    WaveeLogField.Of("artist", artist), WaveeLogField.Of("scope", scopeWord),
                    WaveeLogField.Of("sort", (int)sort), WaveeLogField.Of("library", _library),
                    WaveeLogField.Of("blocks", _count), WaveeLogField.Of("reason", reason),
                    WaveeLogField.Of("firstDiff", firstDiff));
                _loggedOrder = order;
                _loggedArtist = artist;
                _loggedScope = scopeWord;
                _loggedSort = (int)sort;
                if (_prevSlots.Length < _count) _prevSlots = new int[Math.Max(_count, _prevSlots.Length * 2)];
                for (int i = 0; i < _count; i++) _prevSlots[i] = _blocks[i].AlbumSlot;
                _prevSlotCount = _count;
            }
            // The rail's total and the band's songs line ride the KEY (the shape file's deviation 5): both are edge answers, both are
            // read from cached elements, and both were frozen as live reads. The total holds at 0 while the catalogue
            // is gated (R3/F15): "all releases" is a bare word until the three facets have actually answered.
            return new ReaderShapeKey(_count, _library, order, scopeWord, (int)sort,
                                      _catalogueGated ? 0 : TotalReleases(), User.LibrarySongCountOf(artist));
        }

        // ── demand: the WHOLE model, batched by the runner (§6 rule 4) ────────────────────────────────────────────────

        /// <summary>The demand for EVERY block, whatever group it is in — the whole model, batched by the runner (§6 rule
        /// 4; the runner batches 300 uris, four in flight, so the reader keeps NO window). Per block:
        /// <list type="bullet">
        /// <item>its IDENTITY — <c>AlbumFields.Identity</c> for a library block, <c>DiscoCard</c> for a catalogue one. A
        /// library block used to get NOTHING here, on the assumption the navigator had already asked: it has not, for an
        /// album the navigator never listed, so a cold start left saved blocks as skeletons forever.</item>
        /// <item>its TRACKS edge — first page when Unknown, the NEXT page when <c>Partial</c> (the album page's own
        /// paging call, <c>Album.Page.cs:362</c>). Skipped entirely for a LIKED-ONLY block: that block paints the liked
        /// rows and nothing else, so its album's tracklist is not a thing it waits on or a thing it would draw.</item>
        /// <item>its ROWS — the album's listed tracks for a normal block, the LIKED ones for a liked-only block, both at
        /// <c>Row | Audio</c>.</item>
        /// </list>
        /// Scope 1 additionally asks for the three facets' first pages (<see cref="DemandDiscography"/>); pages beyond
        /// the first are scroll-paced by <see cref="OnVisibleRange"/>.
        /// <para>Nothing here asks for a listed track's CREDITED ARTIST names: they are not a readiness gate any more
        /// (<see cref="AlbumPaneReadiness"/>'s 2026-09-18 correction) and this surface does not draw them.</para></summary>
        void DemandBlocks()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = e.SavedAlbums.Changed.Value; _ = e.AlbumArtists.Changed.Value; _ = e.AlbumTracks.Changed.Value;
            _ = e.ArtistAlbums.Changed.Value; _ = e.ArtistSingles.Changed.Value; _ = e.ArtistCompilations.Changed.Value;
            _ = e.Liked.Changed.Value; _ = e.TrackArtists.Changed.Value;
            _ = _shape!.Value;                                               // pull the shape before reading its buffer
            var p = _p;
            if (p is null) return;
            int scopeWord = p.Scope.Value;
            var a = new Artist(_artist);
            if (!a.IsValid) return;
            if (scopeWord == 1) DemandDiscography(a);
            for (int i = 0; i < _count; i++)
            {
                var b = _blocks[i];
                var album = new Album(b.AlbumSlot);
                if (!album.IsValid) continue;
                var priority = b.Saved ? FetchPriority.Visible : FetchPriority.Prefetch;
                Entities.Ensure(album, b.Saved ? AlbumFields.Identity : AlbumFields.DiscoCard, priority);
                if (b.LikedOnly)
                {
                    int n = LikedRowsOf(b.AlbumSlot);
                    if (n > 0)
                        Entities.Ensure(scope.Tracks, _likedScratch.AsSpan(0, n),
                                        (uint)(TrackFields.Row | TrackFields.Audio), FetchPriority.Visible);
                    continue;
                }
                var state = e.AlbumTracks.State(b.AlbumSlot);
                if (!e.AlbumTracks.IsFailed(b.AlbumSlot))
                {
                    if (state == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.AlbumTracks, b.AlbumSlot, 0, priority);
                    else if (state == EdgeState.Partial)
                        Entities.EnsureEdge(FetchEdge.AlbumTracks, b.AlbumSlot, e.AlbumTracks.Count(b.AlbumSlot), priority);
                }
                var tracks = album.TrackSlots;
                if (tracks.Length > 0)
                    Entities.Ensure(scope.Tracks, tracks, (uint)(TrackFields.Row | TrackFields.Audio), priority);
            }
        }

        /// <summary>The liked rows of one album into the owner's shared demand buffer, growing it once and re-reading —
        /// the <c>LibraryAlbumsOf</c> total-then-refill contract. Returns how many landed in <see cref="_likedScratch"/>.
        /// The BLOCK keeps its own copy (<see cref="BlockCell"/>): this one is overwritten by the next block in the loop
        /// and must never be held past the call that filled it.</summary>
        int LikedRowsOf(int albumSlot)
        {
            int n = User.LikedTracksOfAlbum(_artist, albumSlot, _likedScratch);
            if (n <= _likedScratch.Length) return n;
            _likedScratch = new int[Math.Max(n, _likedScratch.Length * 2)];
            return Math.Min(User.LikedTracksOfAlbum(_artist, albumSlot, _likedScratch), _likedScratch.Length);
        }

        /// <summary>The realized window reached the tail of what landed → the next page of each facet (the discography's
        /// own scroll-paced rule). Never a button, never a whole-model page loop.</summary>
        void OnVisibleRange(int first, int last)
        {
            // The list speaks FLAT indices; the paging rule is about BLOCKS. Convert before the test, or the two chrome
            // rows would make every reader ask for the next facet page two blocks early.
            int lastBlock = last - Prefix;
            if (_p is not { } p || p.Scope.Peek() != 1 || lastBlock < _count - 3) return;
            var a = new Artist(_artist);
            if (!a.IsValid) return;
            DemandNextPage(a, DiscoFacet.Albums);
            DemandNextPage(a, DiscoFacet.Singles);
            DemandNextPage(a, DiscoFacet.Compilations);
        }

        // ── readiness + the measured-extent contract ──────────────────────────────────────────────────────────────────

        /// <summary>The one effect that turns landed data into motion. For every block: refresh its cell (so a realized
        /// region re-diffs its own content in place), flip its readiness signal (so a counted skeleton cross-dissolves),
        /// and correct the analytic extent of any block whose ROW COUNT moved after the layout seeded it.
        ///
        /// <para>The correction is why this exists at all: `RepeatLayout.Extents` calls `_extentOf` only on a
        /// seed/resize/splice (`MeasuredStackVirtualLayout.Ensure` RESIZES the table and seeds only the appended tail —
        /// verified in the engine), so a surviving row whose `TrackCount` lands afterwards keeps the extent it was seeded
        /// with. `ItemsViewController.CorrectMeasuredExtent(index, extent)` is the sanctioned repair: it rebases the
        /// visible anchor and every live scroll intent with the table write, and it works for UNREALIZED rows too. The
        /// tail and the band (item 0, whose arm is not a mount input) get the same correction.</para></summary>
        void Settle()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Albums.Changed.Value;
            _ = scope.Tracks.Changed.Value;                                  // a failed row batch turns a block's face
            _ = scope.Edges.AlbumTracks.Changed.Value;
            _ = scope.Edges.Liked.Changed.Value;                             // a liked-only block's rows are that edge
            _ = scope.Edges.SavedAlbums.Changed.Value;                       // ...and the tail's vacancy is its state
            _ = _shape!.Value;                                               // pull the shape before reading its buffer
            if (!string.Equals(_spyKey, _listKey, StringComparison.Ordinal)) { _spyKey = _listKey; _current.SetIfChanged(0); }
            if (!string.Equals(_extentKey, _listKey, StringComparison.Ordinal)) SeedExtents();
            else if (_extents.Length < _count) GrowExtents();
            bool narrow = _narrow;
            for (int i = 0; i < _count; i++)
            {
                var b = _blocks[i];
                if (_cells.TryGetValue(b.AlbumSlot, out var cell)) cell.Settle(b);
                float extent = ReaderShape.ExtentOf(in b, narrow);
                if (_extents[i] == extent) continue;
                // The cache stays BLOCK-indexed (it mirrors `_blocks`); only the correction crosses into flat space.
                // Before the viewport is live there is no table to correct — the seed will read the CURRENT extent anyway.
                if (_ctl.CorrectMeasuredExtent(Prefix + i, extent) || _ctl.Viewport.IsNull) _extents[i] = extent;
            }
            // The TAIL changes height too — air (16) → a line (48) → the "nothing here yet" vacancy — and it is the one
            // row `_extentOf` cannot re-derive from `_blocks`, so it gets the same correction the blocks get.
            float tail = TailHeight(TailKind());
            if (tail != _tailExtent && (_ctl.CorrectMeasuredExtent(Prefix + _count, tail) || _ctl.Viewport.IsNull))
                _tailExtent = tail;
            // The BAND has two heights too — 100 wide, 164 stacked — and the MEDIUM arm that picks one is deliberately
            // not a mount input (a 24-DIP grip drag must not remount the list), so a flip reaches the table HERE, as a
            // correction, exactly like a block whose row count landed. Item 0 is a persistent-prefix row, but a real row
            // of the same extent table — `ArrangeVirtual` measures and commits it on every pass like any realized row —
            // so this is the same sanctioned seam one step earlier: the anchor below it is rebased with the write rather
            // than on the next measure. Read off the SIGNAL (it subscribes this effect), never off `_stacked`, so the
            // answer cannot depend on whether the render or this effect drains first.
            float band = ReaderShape.BandHeight(MediumArm);
            if (band != _bandExtent && (_ctl.CorrectMeasuredExtent(0, band) || _ctl.Viewport.IsNull)) _bandExtent = band;
        }

        /// <summary>The search select-in-place wrote AlbumKey: bring that block to the top once, then clear the key so a
        /// later re-render never re-scrolls. A key absent from the blocks is ignored (and cleared).</summary>
        void SeekAlbumKey()
        {
            if (_p is not { } p) return;
            string key = p.AlbumKey.Peek();
            if (key.Length == 0) return;
            int slot = SlotOfAlbumKey(key);
            if (slot > Table.None)
                for (int i = 0; i < _count; i++)
                    if (_blocks[i].AlbumSlot == slot) { BringBlockToTop(i); break; }
            p.AlbumKey.Value = "";
        }

        /// <summary>Glide block <paramref name="blockIndex"/> to the top of the reader — the ONE place the flat shift and
        /// the alignment live (the spine's dots and the search select-in-place both come through here).
        /// <para><b>Known engine gap, NOT worked around here.</b> <c>alignmentRatio: 0</c> aligns the item's start to the
        /// VIEWPORT's start, and <c>ItemsView.BringIntoView</c> (<c>ItemsView.cs:990-1027</c>) never consults
        /// <c>ScrollState.ItemClipTopInset</c> — so on a list with pinned prefix chrome the target lands exactly
        /// <see cref="SubRailH"/> DIP too high, with its head under the rail. The honest fix is an inset-aware
        /// bring-into-view in the engine; an app-side <c>ScrollBy</c> chaser would fight the same programmatic glide
        /// and is exactly the workaround this codebase refuses.</para></summary>
        void BringBlockToTop(int blockIndex)
            => _ctl.StartBringItemIntoView(Prefix + blockIndex, alignmentRatio: 0f, animate: true);

        /// <summary>The page's persisted row key ("album:spotify:album:…") back to a slot. The page's own parse is private
        /// to <c>User</c> (deviation 6); this is the same two lines over the same prefix constant.</summary>
        static int SlotOfAlbumKey(string key)
        {
            const string prefix = SidebarPinId.AlbumPrefix;
            if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal)) return Table.None;
            var id = EntityId.Parse(key.AsSpan(prefix.Length));
            return id.IsValid && id.Kind == EntityKind.Album && Entities.TableFor(EntityKind.Album) is { } table
                ? table.Slot(id) : Table.None;
        }

        // ── scroll-spy (signals only: one probe per moved frame, one SetIfChanged — never a render) ──────────────────

        /// <summary>Re-runs whenever the reader's scroll handle publishes a new offset or viewport extent.</summary>
        void WatchSpy()
        {
            _ = _listScroll.Offset.Value;
            // Sample just BELOW the pinned rail: the current block is the one whose top edge has cleared the chrome,
            // not whichever one happens to be hidden under it. A DIP line converted to the viewport fraction the
            // controller's probe takes, clamped so a degenerate viewport cannot produce a NaN ratio.
            float h = (float)_listScroll.ViewportSignal.Value;
            _spyRatio = h > 1f ? Math.Clamp((SubRailH + Spacing.XS) / h, 0f, 1f) : 0.2f;
            if (_count <= 0 || !_ctl.TryGetItemIndex(0f, _spyRatio, out int i)) return;
            // Flat → block. The two chrome rows clamp to block 0, which is what the spine should highlight while the
            // reader is parked at its top.
            _current.SetIfChanged(Math.Clamp(i - Prefix, 0, _count - 1));
        }

        /// <summary>The spine follows the reader: a MINIMAL scroll (alignment NaN) toward the current dot, so a dot that
        /// is already on screen does not move the spine at all and one that has fallen off an edge glides just inside
        /// it. Reads <see cref="_current"/>, so it re-runs on exactly the scroll-spy edges and nothing else.</summary>
        void FollowSpine()
        {
            int i = _current.Value;
            if ((uint)i < (uint)_count) _spineCtl.StartBringItemIntoView(i, float.NaN, animate: true);
        }

        /// <summary>The two arms off the READER's own width (deviation 9), both DECIDED by the structure's reader ladder (<see cref="_arm"/>: the edges and
        /// their holds are the structure's, `Rules\Tiers.Artist.cs`); this only feeds it the arranged width. An unmeasured width keeps the previous arm.</summary>
        void OnBounds(RectF r)
        {
            if (r.W > 0f) _readerW.SetIfChanged(r.W);
        }

        /// <summary>The always-on truthfulness line (renderer == estimator, the narrow-heads plan §3.4). A chrome root — the
        /// band, or a block's head — reports its ARRANGED height here, and when that differs by more than half a DIP from
        /// the constant the extent estimator reads for the same arm, one <c>library.reader.chrome</c> line says by exactly
        /// how much; no line at all IS the proof. Deduped per root on (declared, measured), so a disagreement is one line
        /// rather than one per arrange; an unarranged root (H 0) is not a measurement. Nothing is allocated unless it
        /// logs, and it logs only on a defect.</summary>
        static void CheckChrome(string item, int album, string arm, float declared, float measured,
                                ref float loggedDeclared, ref float loggedMeasured)
        {
            if (measured <= 0f || MathF.Abs(measured - declared) <= 0.5f) return;
            if (declared == loggedDeclared && measured == loggedMeasured) return;
            loggedDeclared = declared;
            loggedMeasured = measured;
            Log.Event(WaveeLogLevel.Warning, "ui", "library.reader.chrome", "Artist reader chrome height disagrees with its extent",
                null, -1, null,
                WaveeLogField.Of("item", item), WaveeLogField.Of("album", album), WaveeLogField.Of("arm", arm),
                WaveeLogField.Of("declared", declared), WaveeLogField.Of("measured", measured),
                WaveeLogField.Of("delta", measured - declared));
        }

        /// <summary>How many releases the catalogue says this artist has. Read from the three facet edges HERE, inside
        /// the shape memo's compute (which subscribes all three), and carried out on <see cref="ReaderShapeKey"/> — the
        /// rail's own `Prop` reads the KEY, because a thunk that reads an edge table reads no signal and the rail element
        /// outlives the artist (the shape file's deviation 5).</summary>
        int TotalReleases()
        {
            var e = Entities.Current.Edges;
            return e.ArtistAlbums.Total(_artist) + e.ArtistSingles.Total(_artist) + e.ArtistCompilations.Total(_artist);
        }

        // ── the host seam: what the structure's view reads (`IArtistReaderHost`) ─────────────────────────────────────────
        //
        // The view's slots are components of its own (the band, a block or the tail, a spine dot) and they read the reader's live state through these members,
        // in THEIR render, exactly as the nested `ReaderItem` / `SpineDot` components read the owner: a read subscribes the slot that made it, never the page.

        /// <summary>The band's own subscriptions, in the slot that paints them. <c>_shape</c> carries the artist slot the compute settled on plus the two counts the
        /// subline reads, so pulling it first is also what keeps the band from painting one artist's name over another's numbers. The arm is read off the signal: a flip
        /// swaps the keyed band root.</summary>
        public ArtistReaderBand ReadBand()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            _ = _propsSig.Value;
            var shape = _shape!.Value;
            bool stacked = MediumArm;
            var a = new Artist(_artist);
            return new ArtistReaderBand(a, a.IsValid && a.Knows(ArtistFields.Name), shape, stacked);
        }

        /// <summary>A block (<c>Prefix + b</c>) or, past the last block, null — the tail. The shape is pulled first: the sequence (and the tail's three states) moved.
        /// The cell is refreshed with the block record as a plain field (a realize must not write a signal).</summary>
        public IArtistReaderBlock? ReadBlock(int blockIndex)
        {
            _ = _shape!.Value;
            return (uint)blockIndex < (uint)_count ? Cell(_blocks[blockIndex]) : null;
        }

        /// <summary>The list's last item over <see cref="TailKind"/>'s three answers (decided ONCE, here, because <see cref="TailHeight"/> has to answer its height
        /// before the item renders and the two must not be able to disagree). The strings are the view's; these are the facts they read.</summary>
        public ArtistReaderTail ReadTail()
        {
            _ = _shape!.Value;
            int kind = TailKind();
            if (kind == 0) return new ArtistReaderTail(0, TailHeight(0), false, false, 0, "", _showAll);
            string name = new Artist(_artist).Name;
            if (kind == 2) return new ArtistReaderTail(2, TailHeight(2), false, false, 0, name, _showAll);
            if (_catalogueGated) return new ArtistReaderTail(1, TailHeight(1), true, false, 0, name, _showAll);
            var e = Entities.Current.Edges;
            bool partial = e.ArtistAlbums.State(_artist) == EdgeState.Partial
                        || e.ArtistSingles.State(_artist) == EdgeState.Partial
                        || e.ArtistCompilations.State(_artist) == EdgeState.Partial;
            return new ArtistReaderTail(1, TailHeight(1), false, partial, TotalReleases() - _count, name, _showAll);
        }

        /// <summary>The band's verbs and the rail's reads (stable objects: the band slot hands them to controls that freeze them at mount).</summary>
        public Action PlayAll => _playAll;
        /// <inheritdoc cref="PlayAll"/>
        public Action ShuffleAll => _shuffleAll;
        /// <inheritdoc cref="PlayAll"/>
        public Action GoArtist => _goArtist;
        /// <summary>The band root's bounds callback — the chrome truthfulness check.</summary>
        public Action<RectF> OnBandBounds => _onBandBounds;
        /// <summary>The scope rail's "all releases · N": a bound prop over the SHAPE (a memo read inside a Prop thunk is tracked).</summary>
        public Prop<int> ReleaseTotal => _total;
        /// <summary>The narrow arm, read inside a bound text (the rail folds "all · N").</summary>
        public bool NarrowNow() => NarrowArm;
        /// <summary>The spine's highlighted block.</summary>
        public Signal<int> Current => _current;

        /// <summary>Block <paramref name="index"/> as this render's buffer holds it.</summary>
        public bool TryBlockAt(int index, out ReaderBlock block)
        {
            if ((uint)index < (uint)_count) { block = _blocks[index]; return true; }
            block = default;
            return false;
        }

        /// <summary>A dot's click: glide that block to the top; a stale index (a reshape raced the click) is ignored.</summary>
        public void BringToTop(int blockIndex)
        {
            if ((uint)blockIndex < (uint)_count) BringBlockToTop(blockIndex);
        }

        /// <summary>The triple a spine dot re-renders on: the flat index (a recycle always changes it), the album SLOT that index resolves to right now (a reorder can
        /// change it without a recycle), and that album's own row <see cref="Table.Version"/> (a cover, a title, a year landing). <see cref="Entities.ScopeEpoch"/> is
        /// read first, same as every other read in this file, so an account switch invalidates it too.
        /// <para>C2 (RC9): the OLD render read the whole-table <c>Albums.Changed</c> and the whole <c>_shape</c> memo directly, so every album publish anywhere
        /// re-rendered every mounted dot. The dot's PER-DOT memo over this triple still wakes on every reshape and every album publish (one array read each), but it only
        /// PUBLISHES — and so only re-renders that dot — when ITS OWN triple actually moved.</para></summary>
        public (int Index, int Slot, uint Ver) WatchDot(RowScope scope)
        {
            _ = Entities.ScopeEpoch.Value;
            int i = scope.Index.Value;
            _ = _shape!.Value;                                  // a reorder can change which album sits at THIS index
            int slot = (uint)i < (uint)_count ? _blocks[i].AlbumSlot : Table.None;
            var albums = Entities.Current.Albums;
            uint ver = slot > Table.None && (uint)slot < (uint)albums.Count ? albums.Version[slot] : 0u;
            return (i, slot, ver);
        }

        /// <summary>The vacancy's one action: switch to all releases.</summary>
        void ShowAllReleases()
        {
            if (_p is { } p) p.Scope.Value = 1;
        }

        /// <summary>The cell for a block, minted on first realize. A realize NEVER writes a signal (that would be a write
        /// during a render): a brand-new cell CONSTRUCTS its loadable in the right state, and an existing one only takes
        /// the refreshed block record — every readiness WRITE happens in <see cref="Settle"/>, an effect.</summary>
        BlockCell Cell(ReaderBlock b)
        {
            if (_cells.TryGetValue(b.AlbumSlot, out var cell)) { cell.Block = b; return cell; }
            cell = new BlockCell(this, b);
            _cells[b.AlbumSlot] = cell;
            return cell;
        }

        /// <summary>What the list's last item is — decided ONCE, here, because <see cref="TailHeight"/> has to answer its
        /// height before the item renders and the two must not be able to disagree:
        /// <list type="bullet">
        /// <item><b>2, the VACANCY</b> — scope 0, nothing in your library, and the artist is named: "Nothing from {name}
        /// in your library yet" with one action that switches to all releases. Gated on BOTH account relations being
        /// <c>Complete</c>: with the saved or the liked edge still landing, "nothing" is not yet an answer, it is a
        /// half-read list, and the page would flash an empty state over data on its way in.</item>
        /// <item><b>1, the LINE</b> — <see cref="Strings.Library.FetchingReleases"/> while R3's catalogue gate is still
        /// closed, else "Fetching N more releases…" while a facet is Partial (scope 1), or "Everything {name} has
        /// released is already in your library" when scope 1 lists nothing beyond the library set.</item>
        /// <item><b>0, AIR</b> — the bottom gutter.</item>
        /// </list></summary>
        int TailKind()
        {
            var scope = Entities.Current;
            var e = scope.Edges;
            int scopeWord = _p?.Scope.Peek() ?? 0;
            if (scopeWord == 0)
            {
                int me = scope.MeSlot;
                var artist = new Artist(_artist);
                bool answered = me > Table.None
                             && e.SavedAlbums.State(me) == EdgeState.Complete
                             && e.Liked.State(me) == EdgeState.Complete;
                return _count == 0 && answered && artist.IsValid && artist.Knows(ArtistFields.Name) ? 2 : 0;
            }
            if (_catalogueGated) return 1;    // R3: the catalogue hasn't answered yet — say so, never "N more" or "all in"
            bool partial = e.ArtistAlbums.State(_artist) == EdgeState.Partial
                        || e.ArtistSingles.State(_artist) == EdgeState.Partial
                        || e.ArtistCompilations.State(_artist) == EdgeState.Partial;
            if (partial && TotalReleases() - _count > 0) return 1;
            return _count == _library && _library > 0 ? 1 : 0;
        }

        static float TailHeight(int kind) => kind switch { 2 => TailVacancyH, 1 => TailLineH, _ => TailAirH };

        /// <summary>ONE cell per album: its readiness signal, its record, its liked rows, its verbs and the finished region the view caches on it. The cell is the
        /// reason a block can reveal without the list re-rendering, and the reason a re-realize is a dictionary lookup instead of a tree of fresh closures.</summary>
        sealed class BlockCell : IArtistReaderBlock
        {
            readonly Reader _r;
            readonly int _slot;
            readonly Loadable<int> _load;
            int _version = int.MinValue;

            /// <summary>A LIKED-ONLY block's own rows (<see cref="User.LikedTracksOfAlbum"/>), in liked-edge order — the
            /// only rows it ever paints. Per CELL rather than shared with the owner's demand buffer because the block
            /// reads them at RENDER time, long after the effect that would have overwritten a shared one; grown once and
            /// then reused for the life of the cell, so a re-realize allocates nothing.</summary>
            int[] _liked = [];
            int _likedCount;

            /// <summary>The block record this cell paints, refreshed by the owner (a plain field: a realize must not write
            /// a signal). Rows and Failed move; the album slot never does.</summary>
            public ReaderBlock Block { get; set; }

            /// <inheritdoc />
            public Element? Face { get; set; }

            /// <inheritdoc />
            public int AlbumSlot => _slot;

            /// <inheritdoc />
            public Loadable<int> Load => _load;

            /// <summary>The arm this mount froze (it is a list mount input).</summary>
            public bool Narrow => _r._narrow;

            /// <inheritdoc />
            public Func<ContextMenuModel?> Menu { get; }

            /// <summary>The head root's bounds callback — the chrome truthfulness check (<see cref="CheckChrome"/>) —
            /// allocated once per cell like every other delegate here, and the last disagreement it logged.</summary>
            public Action<RectF> OnHeadBounds { get; }

            /// <inheritdoc />
            public Action Open { get; }

            /// <inheritdoc />
            public Action PlayAlbum { get; }

            /// <inheritdoc />
            public Action Retry { get; }

            float _loggedDeclared = float.NaN, _loggedMeasured = float.NaN;

            public BlockCell(Reader r, ReaderBlock b)
            {
                _r = r;
                _slot = b.AlbumSlot;
                Block = b;
                int slot = _slot;
                Menu = () => { var a = new Album(slot); return a.IsValid ? Album.HeroMenu(a, _r._overlay) : null; };
                OnHeadBounds = rect => CheckChrome("head", slot, _r._narrow ? "stacked" : "inline",
                                                   ReaderShape.HeadHeight(_r._narrow), rect.H,
                                                   ref _loggedDeclared, ref _loggedMeasured);
                Open = () => { var al = new Album(slot); if (al.IsValid) Shell.GoTo(Shell.For(al.Uri, al.Title)); };
                PlayAlbum = () => Playback.PlayOrToggleContext(LibraryRows.IdOf(EntityKind.Album, slot));
                Retry = () => Entities.RefreshEdge(FetchEdge.AlbumTracks, slot);
                // CONSTRUCTED in its answer state, never written into it: this runs inside the list's render.
                bool answered = Verdict(out int version);
                _version = answered ? version : int.MinValue;
                _load = answered ? Loadable<int>.Ready(version) : Loadable<int>.Pending(0);
            }

            /// <inheritdoc />
            public ReadOnlySpan<int> LikedRows() => _liked.AsSpan(0, _likedCount);

            /// <inheritdoc />
            public void PlayTrack(int trackSlot)
                => Playback.PlayContext(LibraryRows.IdOf(EntityKind.Album, _slot), LibraryRows.IdOf(EntityKind.Track, trackSlot));

            /// <summary>Republish readiness — from the owner's EFFECT, the only place that writes. Ready/Failed BOTH
            /// resolve the region to Ready: a failed tracklist is an answer the block paints itself (the head plus the
            /// Retry strip), not a skeleton and not an engine failure branch.</summary>
            public void Settle(ReaderBlock b)
            {
                Block = b;
                if (!Verdict(out int version))
                {
                    if (!_load.IsLoading) _load.SetPending();
                    _version = int.MinValue;
                    return;
                }
                if (_version == version && _load.IsReady) return;
                _version = version;
                _load.SetReady(version);
            }

            /// <summary>Has this block ANSWERED, and what is it painting? The verdict is O1's tested rule
            /// (<see cref="AlbumPaneReadiness.Of(bool,EdgeState,bool,AlbumRowFacts)"/>); <paramref name="version"/> is a
            /// fingerprint of exactly what the block paints, so the region re-diffs its content when a title, a cover, a
            /// year, a kind or the row count lands, and never otherwise. It writes only this cell's own liked-row buffer,
            /// never a signal, which is what lets the constructor call it.
            ///
            /// <para>A LIKED-ONLY block has a verdict of its own: it paints the liked rows, so the album's TRACKS edge is
            /// not a thing it waits on — the one gate is the album's own TITLE. Waiting on an edge nobody demands for it
            /// (<see cref="DemandBlocks"/> skips it on purpose) would be exactly the "skeleton forever" defect.</para>
            ///
            /// <para>For every other block the row fact that gates is <c>AnyUntitled</c> — a listed row whose own title
            /// has not landed. An unnamed CREDIT is NOT a gate any more (the 2026-09-18 correction in
            /// <see cref="AlbumPaneReadiness"/>): nobody demands `ArtistFields.Name` for a track's credits, so that bit
            /// can simply never land, and the old `Detail.NoticeRules.ForAlbum` gate held such a block in its skeleton
            /// for the life of the app. An unnamed credit is a NOTICE over a rendered list, not a reason to withhold one.</para></summary>
            bool Verdict(out int version)
            {
                var scope = Entities.Current;
                var edge = scope.Edges.AlbumTracks;
                var a = new Album(_slot);
                bool named = a.IsValid && a.Knows(AlbumFields.Title);

                if (Block.LikedOnly)
                {
                    RefreshLiked();
                    version = HashCode.Combine(_likedCount, a.IsValid ? a.ImageId : default, a.IsValid ? a.Year : 0,
                                               a.IsValid ? (int)a.Kind : 0, named, true, false);
                    return named;
                }

                var slots = a.IsValid ? a.TrackSlots : default;
                bool edgeFailed = edge.IsFailed(_slot);
                bool anyUntitled = false, rowsFailed = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (!new Track(slots[i]).Knows(TrackFields.Title)) anyUntitled = true;
                    if (scope.Tracks.IsFailed(slots[i], (uint)TrackFields.Row)) rowsFailed = true;
                    if (anyUntitled && rowsFailed) break;
                }
                version = HashCode.Combine(slots.Length, a.IsValid ? a.ImageId : default, a.IsValid ? a.Year : 0,
                                           a.IsValid ? (int)a.Kind : 0, named, edgeFailed, rowsFailed, anyUntitled);
                var state = AlbumPaneReadiness.Of(named, edge.State(_slot), edgeFailed,
                                                  new AlbumRowFacts(anyUntitled, rowsFailed));
                return state is AlbumPaneState.Ready or AlbumPaneState.Failed;
            }

            /// <summary>Re-read this block's liked rows into its own buffer (the total-then-refill contract
            /// <see cref="User.LikedTracksOfAlbum"/> shares with every other library helper). Called from
            /// <see cref="Verdict"/>, so the rows the block renders are always the ones the last verdict counted.</summary>
            void RefreshLiked()
            {
                int n = User.LikedTracksOfAlbum(_r._artist, _slot, _liked);
                if (n > _liked.Length)
                {
                    _liked = new int[Math.Max(n, 8)];
                    n = User.LikedTracksOfAlbum(_r._artist, _slot, _liked);
                }
                _likedCount = Math.Min(n, _liked.Length);
            }

            /// <summary>The head's meta line: "3 songs · 10 min" (deviation 4), or a liked-only block's own count.</summary>
            public string Meta()
            {
                // A liked-only block does not READ the album's tracklist, so it must not describe one: "2 liked
                // songs" is the whole truth about what is under this head, where `AlbumMeta` would have claimed the
                // record's own length off rows this block never asked for. `nLikedSongs` is an ICU plural and
                // renders "1 liked song" itself — no `== 1` arm, in any locale's plural categories.
                if (Block.LikedOnly) return Strings.Library.NLikedSongs(_likedCount);
                var a = new Album(_slot);
                var slots = a.IsValid ? a.TrackSlots : default;
                long totalMs = 0;
                bool durationsKnown = Entities.Current.Edges.AlbumTracks.State(_slot) == EdgeState.Complete;
                for (int i = 0; i < slots.Length; i++)
                {
                    var t = new Track(slots[i]);
                    totalMs += t.DurationMs;
                    if (!t.Knows(TrackFields.Duration)) durationsKnown = false;
                }
                // "3 songs · 10 min": the cover's caption states the year (W3).
                return Detail.Text.AlbumMeta(slots.Length, totalMs, durationsKnown) ?? "";
            }
        }
    }
}
