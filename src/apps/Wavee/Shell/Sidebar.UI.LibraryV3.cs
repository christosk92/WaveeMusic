// ── Shell/Sidebar.UI.LibraryV3.cs ──────────────────────────────────────────────────────────────────────────────────
// Library V3: the mode component, its ephemeral session, and the fixed chrome above the ONE pane
// NAMED PARTIAL of Sidebar.UI.cs (J1): LibraryV3Mode + V3Session + the nav band, header, toolbar, search host, chip
// rail and sort/view flyout
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 1600 lines (the J1 split of Sidebar.UI.cs's 7,500)
// Spec: ch 25 §0.13, W11-W15, W22, §3 + §5 V3 rows, §6 (V3 overflow, chip rail keys, search Escape, sort/view flyout),
//       §10 items 40-54 and 71-76
//
// V3 IS A DOCUMENT PLUS CHROME: `LibraryV3Document` (CORE) rendered by the one `PaneView` through `PaneConfig` delegates,
// plus the fixed chrome nav band → header 44 → toolbar 36 → chip rail 40 → rule (→ breadcrumb 32) → banner / empty state,
// which never scrolls, filters or reorders with the list. Every pure decision is CALLED from `Sidebar.Modes.cs`.

using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    // ══ 1. THE MODE COMPONENT ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Mode B — Library V3 (mounted under <c>MountKey(LibraryV3)</c>). Builds its <see cref="PaneConfig"/> ONCE —
    /// every member a delegate or a flag, read live inside the pane's render — and mounts the one <see cref="PaneView"/>.</summary>
    internal sealed class LibraryV3Mode : Component
    {
        /// <summary>0.2.9's binder-pump search debounce (<c>SidebarBinderPump.SearchDebounceMs</c>).</summary>
        const float SearchDebounceMs = 90f;

        readonly bool _inDrawer;   // mount configuration: a drawer and a docked pane are separate mounts

        public LibraryV3Mode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            bool inDrawer = _inDrawer;
            var s = UseMemo(() => new V3Session(inDrawer), DepKey.Empty);

            // MEMOS, not raw width reads: a seam drag writes Width every frame, and the equality cut-off means the
            // document epoch moves only when the derived column count / folder mode actually flips.
            s.Columns = UseComputed(s.ComputeColumns);
            bool narrow = UseComputed(s.ComputeNarrow).Value;
            // Inline disclosure and a drill stack answer the same question, so only one may be live. Signal writes ⇒ effect.
            UseLayoutEffect(() =>
            {
                s.NarrowFolders.SetIfChanged(narrow);
                if (!narrow) s.ResetDrill();
            }, DepKey.From(narrow));

            // The binder has no pump in 0.3 (`Sync()` is a plain, idempotent method). V3's own writes resync at the
            // write site; this effect covers the debounced search text and the folder/order edits the pane makes.
            var search = UseDebouncedValue(V3Search, SearchDebounceMs);
            UseEffect(() =>
            {
                _ = search.Value;
                _ = FolderVersion.Value;
                _ = V3OrderVersion.Value;
                V3Session.Resync();
            });

            var config = UseMemo(() => new PaneConfig
            {
                Design = SidebarDesign.LibraryV3,
                ScrollKeyPrefix = "sidebar.v3",
                Document = s.BuildDocument,
                Input = s.ShapeInput,
                ModeEpoch = s.ReadModeEpoch,
                SetSectionCollapsed = null,   // every V3 section is title-less: no header row, nothing to collapse
                ReadOnly = true,              // the chrome owns every piece of the ephemeral document's state
                SearchHead = false,           // V3's own library-only search lives in the toolbar
                Head = s.ChromeHead,
                // §3.2.3: the design switch rides V3's overflow menu, so no header layout button. The RAIL keeps its copy
                // (0.2.9 `RailLayoutMenu = true`): a collapsed pane has no overflow menu to reach.
                ShowLayoutMenu = false,
                RailLayoutMenu = true,
                RailHead = s.BuildRailHead,
                RailFooter = s.BuildRailFooter,
                IsReorderableSection = s.IsSectionReorderable,
                TreeSortedNonCustom = s.TreeSortedNonCustom,
                SortedListRefusalAction = s.SwitchToCustomSortForReorder,
                ClampReorderSlot = s.ClampReorderSlot,
                CommitReorder = s.CommitReorder,
                ActivateFolder = s.ActivateFolder,
                DisclosesFoldersInline = s.DisclosesFoldersInline,
                // Null: V3's "+" calls PaneView.CreatePlaylist() — the pane's ONE create flow (no second one here).
                OnCreatePlaylist = null,
            }, DepKey.Empty);

            // The factory runs once at mount; keeping the instance is what lets the chrome's "+" reuse the pane's own
            // HeaderCreateDropSpec/CreateMenu/CreatePlaylist instead of a second create-from-drag flow.
            return Embed.Comp(() =>
            {
                var pane = new PaneView(config, inDrawer);
                s.PaneRef = pane;
                return pane;
            });
        }
    }

    // ══ 2. THE SESSION — ephemeral state + the PaneConfig delegates ═════════════════════════════════════════════════

    /// <summary>Everything V3's chrome shares that is neither persisted (the <c>Sidebar.V3*</c> bag) nor document. A
    /// reference-stable frozen prop; everything mutable inside is a signal or read at call time. Never persisted.</summary>
    internal sealed class V3Session
    {
        /// <summary>The published cell: the binder publishes its OWN <c>Entries</c>; <c>Sidebar.Entries</c> is the headless fallback.</summary>
        public static SidebarEntries Cell => Binder?.Entries ?? Entries;

        /// <summary>Rebuild the projection now if any trigger moved (one struct compare when nothing did).</summary>
        public static void Resync() => Binder?.Sync();

        public V3Session(bool inDrawer)
        {
            InDrawer = inDrawer;
            CreateMenuFn = () => PaneRef?.CreateMenu();
            HeaderDropActiveFn = () => PaneRef is { } p && p.HeaderCreateDropActive.Value;
        }

        public readonly bool InDrawer;
        public PaneView? PaneRef;                                  // set by the mount factory, before the pane renders
        public IReadSignal<int>? Columns;                          // the derived grid column count (a memo)
        public readonly Signal<bool> NarrowFolders = new(false);   // below DrillInWidth (or in the drawer) folders DRILL
        public readonly Signal<bool> SearchOpen = new(false);      // W1 — the narrow search host is open (session-only)
        public readonly Signal<bool> DragInFlight = new(false);    // #85 H1 — written ONLY by V3DragWatch
        public readonly Signal<int> DrillVersion = new(0);         // bumped by every push/pop/reset: one re-plan, no remount
        public readonly LibraryV3View View = new();                // the built order, shared by the input shaper and chrome
        public readonly Func<ContextMenuModel?> CreateMenuFn;
        public readonly Func<bool> HeaderDropActiveFn;

        readonly LibraryV3Window _pins = new();                 // the pin band: a WINDOW over the published list
        readonly List<string> _orderScratch = new(64);          // a custom-order commit materializes the whole order
        readonly List<string> _folderIds = new(4), _folderNames = new(4);
        LibraryV3DocState _docState;
        IReadOnlyList<SidebarItemSpec>? _docTopBar;
        SidebarCustomLayout? _docLayout;
        long _viewEpoch = long.MinValue;                        // the pane shapes its input twice per plan (list + rail)

        // ── the view state ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>THE V3 view state in one value. Reading it SUBSCRIBES the caller (document, mode epoch, chrome) to every
        /// signal the document is a function of, so none can render a state the others disagree with. Allocation-free.</summary>
        public LibraryV3DocState ReadState()
        {
            int columns = Columns is { } c ? c.Value : 2;
            int filter = LibraryV3Metrics.NormalizeFilter(V3Filter.Value);
            int qualifier = LibraryV3Metrics.NormalizeQualifier(V3Qualifier.Value);
            int sort = LibraryV3Metrics.NormalizeSort(V3Sort.Value);
            bool desc = V3Desc.Value;
            int view = LibraryV3Metrics.NormalizeView(V3View.Value);
            bool searching = LibraryV3Metrics.HasQuery(V3Search.Value);
            var cell = Cell;
            _ = cell.Version.Value;          // PinCount + QualifiersAvailable move with the projection
            _ = DrillVersion.Value;          // a push/pop re-slices the view
            return new LibraryV3DocState(filter, qualifier, sort, desc, view, columns, searching,
                DrillActive ? CurrentFolderId : null,
                cell.PinCount > 0,
                IsPinned(LibraryV3Document.LikedRouteKey),
                cell.QualifiersAvailable,
                DragInFlight.Value);
        }

        public int ComputeColumns()
        {
            int view = LibraryV3Metrics.NormalizeView(V3View.Value);
            if (!LibraryV3Metrics.IsGrid(view)) return LibraryV3Document.ClampColumns(0);
            // Against the pane's ONE inset — the grid strip derives its cell edge from exactly that width.
            float cross = Sidebar.Width.Value - PaneMetrics.PaneInsetH;
            return LibraryV3Document.ClampColumns(LibraryV3Metrics.Columns(view, cross));
        }

        public bool ComputeNarrow() => InDrawer || Sidebar.Width.Value < LibraryV3Metrics.DrillInWidth;

        // ── PaneConfig: document, epoch, head, input ─────────────────────────────────────────────────────────────

        /// <summary>Invoked in the PANE's render (that read is its subscription). Cached on the state AND the shortcut
        /// band reference: a freshly-minted document per render defeats the pane's publish reference check.</summary>
        public SidebarCustomLayout BuildDocument()
        {
            var state = ReadState();
            var topBar = TopBar;
            if (_docLayout is { } cached && _docState.Equals(state) && ReferenceEquals(_docTopBar, topBar)) return cached;
            _docState = state;
            _docTopBar = topBar;
            _docLayout = LibraryV3Document.Build(in state, topBar);
            return _docLayout;
        }

        public int ReadModeEpoch() => ReadState().GetHashCode();

        /// <summary>KEYED and type-stable: the pane rebuilds this element every render and the instance behind it must
        /// survive (its hooks own the search focus latch and the self-correcting effects).</summary>
        public Element? ChromeHead() => Embed.Comp(() => new V3Chrome(this)) with { Key = "v3-chrome" };

        /// <summary>The published projection (already filtered, searched, sorted, pins-first) as two WINDOWS: the pin band and
        /// the remainder, re-grouped into tree order or sliced to one folder level — nothing re-filtered or re-sorted.
        /// <c>ExpandedFolders = null</c>: a collapsed folder's children are already ABSENT from the projection.</summary>
        public SidebarProjectionInput ShapeInput(SidebarProjectionInput input)
        {
            var state = ReadState();
            var cell = Cell;
            var published = cell.Current;
            int pinCount = Math.Clamp(cell.PinCount, 0, published.Count);
            int skip = state.PinsBandVisible ? pinCount : 0;
            bool group = LibraryV3Document.FoldersApply(in state);
            int revision = Binder?.Revision ?? 0;

            long epoch = ViewEpoch(cell.Version.Peek(), revision, skip, group, state.DrillFolderId);
            if (epoch != _viewEpoch)
            {
                _viewEpoch = epoch;
                View.Build(published, skip, input.PlaylistTree, revision, state.DrillFolderId, group);
            }

            _pins.Set(published, 0, skip);
            input = input with { Pins = _pins, ExpandedFolders = null };
            return group ? input with { PlaylistTree = View.Rows } : input with { Library = View.Rows };
        }

        static long ViewEpoch(int entriesVersion, int revision, int skip, bool group, string? drill)
        {
            unchecked
            {
                long h = entriesVersion;
                h = h * 1099511628211L + revision;
                h = h * 1099511628211L + skip;
                h = h * 1099511628211L + (group ? 1 : 0);
                h = h * 1099511628211L + (drill is { Length: > 0 } d ? StringComparer.Ordinal.GetHashCode(d) : 0);
                return h;
            }
        }

        // ── PaneConfig: reorder (§3.2.9's LOCAL custom order) ────────────────────────────────────────────────────

        /// <summary>The pin band always reorders; the library only under the local-overlay conditions below.</summary>
        public bool IsSectionReorderable(SidebarSectionKind kind)
            => kind == SidebarSectionKind.Pinned || (kind == SidebarSectionKind.PlaylistTree && CanReorderCustom());

        /// <summary>Playlists ∧ Custom sort ∧ no query ∧ a list view ∧ no drill. Peeked: read inside a live drag's hover.</summary>
        bool CanReorderCustom()
            => CanReorderV3 && !DrillActive && !LibraryV3Metrics.HasQuery(V3Search.Peek())
               && LibraryV3Metrics.IsList(LibraryV3Metrics.NormalizeView(V3View.Peek()));

        /// <summary>D10 — the exact complement: the tree shows a SORTED view, so positional drops refuse with "clear
        /// sorting to reorder" while Into stays legal.</summary>
        public bool TreeSortedNonCustom() => !CanReorderCustom();

        /// <summary>D11 — the sibling-run clamp during the gesture. Only the library band clamps.</summary>
        public int ClampReorderSlot(SidebarSectionKind kind, int from, int to)
            => kind == SidebarSectionKind.PlaylistTree && CanReorderCustom() ? View.ClampToSiblingRun(from, to) : to;

        /// <summary>Pins commit through the shared pin store (mapped by pin id — a band position can drift from the
        /// store). The library band writes V3's local overlay and NOTHING else, after verifying the plan and the view
        /// agree (a publish mid-gesture must not persist a shuffled library) and the drop stays inside its folder.</summary>
        public void CommitReorder(PaneReorder r)
        {
            if (r.FromSlot == r.ToSlot) return;
            if (r.Section.Kind == SidebarSectionKind.Pinned)
            {
                int pf = Pins.IndexOf(r.KeyAt(r.FromSlot)), pt = Pins.IndexOf(r.KeyAt(r.ToSlot));
                if (pf < 0 || pt < 0) MovePin(r.FromSlot, r.ToSlot);
                else MovePin(pf, pt);
                Resync();
                return;
            }
            if (!CanReorderCustom() || r.SlotCount != View.Count) return;
            if (!string.Equals(r.KeyAt(r.FromSlot), View.KeyAt(r.FromSlot), StringComparison.Ordinal)) return;
            if (!View.SameParent(r.FromSlot, r.ToSlot)) return;
            View.MaterializeOrder(_orderScratch, r.FromSlot, r.ToSlot);
            SetV3CustomOrder(_orderScratch);
            Resync();
        }

        /// <summary>H3 (#85) — the refusal toast's action: the ONE state where a positional reorder is legal again needs
        /// BOTH the Playlists lens and the Custom sort, so set both.</summary>
        public void SwitchToCustomSortForReorder()
        {
            SetV3Filter((int)SidebarV3Filter.Playlists);
            SetV3Sort((int)SidebarV3Sort.Custom, V3Desc.Peek());
            Resync();
        }

        // ── drill-in (Revision 2) ────────────────────────────────────────────────────────────────────────────────
        // A STACK of ids AND names: nesting is genuine, and a folder that vanishes mid-session must still label sanely.

        public bool DrillActive => _folderIds.Count > 0;
        public string CurrentFolderId => _folderIds.Count == 0 ? "" : _folderIds[^1];
        public string CurrentFolderName => _folderNames.Count == 0 ? Loc.Get(Strings.Sidebar.V3.Title) : _folderNames[^1];
        /// <summary>The level BACK returns to — the breadcrumb's accessible name.</summary>
        public string ParentName => _folderNames.Count <= 1 ? Loc.Get(Strings.Sidebar.V3.Title) : _folderNames[^2];

        /// <summary>Enter a folder level. It also EXPANDS the folder: the projection omits a collapsed folder's children,
        /// so the level would be empty — and widening back to inline leaves you standing inside it.</summary>
        public void PushFolder(string? folderId, string? name)
        {
            if (string.IsNullOrEmpty(folderId)) return;
            SetFolderExpanded(folderId, true);
            _folderIds.Add(folderId);
            _folderNames.Add(name is { Length: > 0 } ? name : Loc.Get(Strings.Sidebar.V3.Kind.Folder));
            BumpDrill();
            Resync();
        }

        public void PopFolder()
        {
            if (_folderIds.Count == 0) return;
            _folderIds.RemoveAt(_folderIds.Count - 1);
            _folderNames.RemoveAt(_folderNames.Count - 1);
            BumpDrill();
        }

        /// <summary>Leave drill-in entirely (pane widened, a query landed, the kind filter changed).</summary>
        public void ResetDrill()
        {
            if (_folderIds.Count == 0) return;
            _folderIds.Clear();
            _folderNames.Clear();
            BumpDrill();
        }

        void BumpDrill() => DrillVersion.Value = DrillVersion.Peek() + 1;

        /// <summary>WHAT A FOLDER ROW DOES — the one decision point, so the renderer never learns about drill levels:
        /// a GRID cannot disclose, so switch to the list and open it; a NARROW pane drills; a WIDE pane toggles inline.</summary>
        public void ActivateFolder(string folderId, string name)
        {
            if (folderId.Length == 0) return;
            if (LibraryV3Metrics.IsGrid(LibraryV3Metrics.NormalizeView(V3View.Peek())))
            {
                SetV3View((int)SidebarV3View.List);
                SetFolderExpanded(folderId, true);
            }
            else if (NarrowFolders.Peek()) PushFolder(folderId, name);
            else ToggleFolder(folderId);
            Resync();
        }

        /// <summary>Only a wide LIST discloses in place; grid switches view and narrow drills, so neither seeds an
        /// inline expand/collapse animation.</summary>
        public bool DisclosesFoldersInline()
            => !NarrowFolders.Peek() && !LibraryV3Metrics.IsGrid(LibraryV3Metrics.NormalizeView(V3View.Peek()));

        // ── shared verbs ─────────────────────────────────────────────────────────────────────────────────────────

        public void Navigate(string key, string? arg)
        {
            if (PaneRef is { } pane) pane.Navigate(key, arg);
            else Shell.GoTo(Shell.Parse(key, arg));
        }

        public void CollapsePane() => SetCollapsed(true);
        public void ExpandPane() => SetCollapsed(false);
        public void CreatePlaylist() => PaneRef?.CreatePlaylist();

        public void OpenSearch() => SearchOpen.SetIfChanged(true);

        /// <summary>Close AND clear: a closed field showing leftover text next time would read as a bug.</summary>
        public void CloseSearch()
        {
            SearchOpen.SetIfChanged(false);
            V3Search.SetIfChanged("");
            Resync();
        }

        /// <summary>The overflow menu's / the ✕ chip's full reset: filter, qualifier, search text and the drill stack.</summary>
        public void ClearAllFilters()
        {
            SetV3Filter((int)SidebarV3Filter.All);
            SetV3Qualifier((int)SidebarV3Qualifier.Any);
            V3Search.SetIfChanged("");
            ResetDrill();
            Resync();
        }

        public bool AnyFilterActive
            => V3Filter.Peek() != (int)SidebarV3Filter.All
               || V3Qualifier.Peek() != (int)SidebarV3Qualifier.Any
               || V3Search.Peek().Length > 0;

        /// <summary>The retry banner / error state: invalidate + sync re-runs the contributing reads.</summary>
        public void Retry()
        {
            if (Binder is not { } binder) return;
            binder.Invalidate();
            binder.Sync();
        }

        // ── the 56-DIP rail's own affordances (§3.2.13) ──────────────────────────────────────────────────────────

        /// <summary>A rail art tile's cover edge (0.2.9 <c>SidebarRailItem.ArtEdge</c>: a 40 tile, 36 art).</summary>
        const float RailArtEdge = 36f;

        /// <summary>After the plan's tiles: "Your Library" EXPANDS the pane, and the "+" with the header's own drop spec
        /// (H2 #85) so a collapsed pane accepts what the expanded header does.</summary>
        public Element? BuildRailFooter() => new BoxEl
        {
            Key = "v3-rail-footer",
            Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                Rail.IconTile("v3-rail-expand", Icons.List, false, ExpandPane, Loc.Get(Strings.Sidebar.V3.Expand)),
                Embed.Comp(() => new CreateButton(CreatePlaylist, menu: CreateMenuFn,
                    drop: PaneRef?.HeaderCreateDropSpec(), dropActive: HeaderDropActiveFn, box: Rail.Box, glyph: 16f)),
            ],
        };

        /// <summary>W3 — the nav band's tiles drawn for the rail. The band is chrome (never a document section), so the
        /// rail planner has nothing to draw them from: this reads the same five destinations + <c>TopBar</c> and reuses
        /// <see cref="SidebarNavBandModel"/>'s rules, so the two forms of one tile cannot disagree.</summary>
        public Element? BuildRailHead()
        {
            var items = TopBar;
            string route = Shell.NameOf(Shell.Current.Peek());
            var keys = SidebarShortcutsSection.LibraryDestinations;
            var kids = new List<Element>(items.Count + keys.Length);

            // A rail is icon-only by construction, so the tooltip carries the destination name.
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                var dest = Shell.Dest(Shell.Parse(key));
                kids.Add(Rail.IconTile(key, dest.Glyph, string.Equals(route, key, StringComparison.Ordinal),
                    () => Navigate(key, null), dest.Title));
            }

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is null || item.Hidden) continue;
                Element? tile = item.Target switch
                {
                    SidebarItemTarget.Action => RailActionTile(item),
                    SidebarItemTarget.Track => RailEntityTile(item, route, track: true),
                    SidebarItemTarget.Entity => RailEntityTile(item, route, track: false),
                    _ => RailRouteTile(item, route),
                };
                if (tile is not null) kids.Add(tile);
            }

            return new BoxEl
            {
                Key = "v3-rail-head",
                Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = [.. kids],
            };
        }

        Element RailRouteTile(SidebarItemSpec item, string route)
        {
            var dest = Shell.Dest(Shell.Parse(item.Key));
            string label = item.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;
            string key = item.Key;
            return Rail.IconTile(item.Id, RowGlyphs.For(item, dest.Glyph),
                string.Equals(route, key, StringComparison.Ordinal), () => Navigate(key, null), label);
        }

        /// <summary>Resolved through the pane's action seam. A binding that cannot resolve at all is omitted — a 56-DIP
        /// tile has no room for the reason text the expanded row's tooltip carries.</summary>
        Element? RailActionTile(SidebarItemSpec item)
        {
            if (item.Action is null || PaneRef is not { } pane || pane.Registry is null) return null;
            var a = pane.ResolveActionRow(item);
            string glyph = RowGlyphs.For(item, a.Icon.Glyph is { Length: > 0 } g ? g : Icons.MusicNote);
            return Rail.IconTile(item.Id, glyph, false, a.Enabled ? a.Click : null, a.Label);
        }

        /// <summary>A hand-placed playlist/album/artist/show/track as an ART tile. A track PLAYS; everything else navigates
        /// through the pin scheme's own uri → route map.</summary>
        Element? RailEntityTile(SidebarItemSpec item, string route, bool track)
        {
            string uri = item.Key;
            if (uri.Length == 0) return null;
            string label = LabelOf(item);
            var art = Cover.ArtUrl(item.FallbackImageUrl, uri, RailArtEdge, circular: item.EntityKind == SidebarEntityKind.Artist);
            bool selected = !track && SidebarNavBandModel.SelectsRoute(item, route);
            Action? click = track
                ? () => PaneRef?.PlayTrack(uri)
                : SidebarNavBandModel.RouteKeyOf(item) is { Length: > 0 } r ? () => Navigate(r, label) : null;
            return Rail.ArtTile(item.Id, art, selected, click, label);
        }

        public static string LabelOf(SidebarItemSpec item)
            => item.LabelOverride is { Length: > 0 } alias ? alias
             : item.FallbackTitle is { Length: > 0 } cached ? cached
             : PaneText.ShortUri(item.Key);
    }

    // ══ 3. THE CHROME ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>§3.2.2's fixed stack above the pane's scroll surface (<c>PaneConfig.Head</c>). The EMPTY STATE lives here:
    /// V3's are actionable and name the query, so the library section is <c>HideBody</c> — ONE empty message on screen.</summary>
    internal sealed class V3Chrome : Component
    {
        /// <summary>Folder navigation is <c>MotionTok.ConnectedFly</c> (spring 0.45 / 1.0).</summary>
        static readonly LayoutTransition BreadcrumbFly = new(
            TransitionChannels.Position | TransitionChannels.Opacity, MotionTok.ConnectedFly.ToDynamics(),
            Enter: new EnterExit(Dx: 12f, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: 12f, Opacity: 0f, Active: true));

        readonly V3Session _s;

        public V3Chrome(V3Session s) => _s = s;

        public override Element Render()
        {
            // Subscribes to every V3 signal the document is a function of: breadcrumb, banner and empty state can never
            // disagree with the rows below them.
            var state = _s.ReadState();
            int drillVersion = _s.DrillVersion.Value;

            // Self-correcting state 1: a drilled-into folder that vanished POPS rather than pointing at nothing.
            bool missing = _s.View.DrillTargetMissing;
            UseLayoutEffect(() =>
            {
                if (missing) _s.PopFolder();
            }, DepKey.From(missing ? 1 : 0, drillVersion));

            // Self-correcting state 2: a search FLATTENS the tree, so there is no folder to be inside of.
            bool searching = state.Searching;
            UseLayoutEffect(() =>
            {
                if (searching) _s.ResetDrill();
            }, DepKey.From(searching));

            var cell = V3Session.Cell;
            var load = cell.State;
            bool anyPending = cell.AnyContributingKindPending;
            int rows = _s.View.Count + (state.PinsBandVisible ? cell.PinCount : 0);
            bool failedEmpty = load == LoadState.Failed && rows == 0;
            var error = cell.Error;
            UseLayoutEffect(() =>
            {
                if (failedEmpty) Log.Warn("sidebar", "v3.library.failed rows=0", error);
            }, DepKey.From(failedEmpty));

            var bands = new List<Element>(8)
            {
                Embed.Comp(() => new V3DragWatch(_s)) with { Key = "v3-drag-watch" },
                Embed.Comp(() => new V3NavBand(_s)) with { Key = "v3-nav" },
                Embed.Comp(() => new V3HeaderBand(_s)) with { Key = "v3-header" },
                Embed.Comp(() => new V3ToolbarBand(_s)) with { Key = "v3-toolbar" },
                Embed.Comp(() => new V3ChipRail(_s)) with { Key = "v3-chips" },
                // Spans the CONTENT LANE, like the plan's own explicit divider.
                Divider() with
                {
                    Key = "v3-chrome-rule",
                    Margin = new Edges4(PaneMetrics.ContentLane, 4f, PaneMetrics.ContentLaneEnd, 4f),
                },
            };

            if (state.Drilled) bands.Add(Breadcrumb());

            // §3.2.10: loaded content is NEVER blanked. A failure WITH rows is a one-line banner; only a failure with
            // nothing to show takes the pane. A pending library shows the pane's skeletons, never an empty state.
            if (load == LoadState.Failed && rows > 0) bands.Add(ErrorBanner());
            else if (rows == 0 && !anyPending && load != LoadState.Pending) bands.Add(EmptyBand(load, in state));

            return new BoxEl
            {
                // No horizontal padding: each band pads itself (the chip rail's inset must live INSIDE its scroller).
                Direction = 1, Shrink = 0f, Padding = new Edges4(0f, 8f, 0f, 0f),
                Children = [.. bands],
            };
        }

        /// <summary>Back + the current level's name; the BACK target's label is the button's accessible name.</summary>
        Element Breadcrumb() => new BoxEl
        {
            Key = "v3-breadcrumb",
            Direction = 0, Height = LibraryV3Metrics.BreadcrumbHeight, AlignItems = FlexAlign.Center, Gap = 4f,
            // Optical lane: the 24-DIP box starts 6 before the lane so its 12-DIP GLYPH sits on it.
            Padding = new Edges4(PaneMetrics.ContentLane - 6f, 0f, PaneMetrics.ContentLaneEnd, 0f),
            Animate = BreadcrumbFly,
            Children =
            [
                ToolTip.Wrap(new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = _s.PopFolder,
                    Children = [Icon(Icons.Back, 12f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), _s.ParentName),
                new TextEl(_s.CurrentFolderName)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextSecondary,
                    Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };

        /// <summary>Everything that leaves the pane with zero rows, in §3.2.10's priority order.</summary>
        Element EmptyBand(LoadState load, in LibraryV3DocState state)
        {
            Element body;
            if (load == LoadState.Failed)
            {
                // The FULL error state (page scale), never the compact arm and never the banner.
                body = Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Page, onAction: _s.Retry);
            }
            else if (state.Searching)
            {
                string q = V3Search.Peek();
                if (q.Length > 24) q = string.Concat(q.AsSpan(0, 24), "…");   // a pasted paragraph cannot blow out 240 DIP
                body = Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    title: Strings.Sidebar.V3.Empty.Search(q),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.SearchSub),
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearSearch),
                    onAction: static () => { V3Search.SetIfChanged(""); V3Session.Resync(); });
            }
            else if (state.Filter != (int)SidebarV3Filter.All)
            {
                // The qualifier auto-clears (§3.2.4), so there is no separate empty-by-qualifier state.
                body = Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    title: Strings.Sidebar.V3.Empty.Filter(Loc.Get(LibraryV3Labels.Filter(state.Filter))),
                    subtitle: "",
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearFilter),
                    onAction: static () => { SetV3Filter((int)SidebarV3Filter.All); V3Session.Resync(); });
            }
            else
            {
                body = Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                    title: Loc.Get(Strings.Sidebar.V3.Empty.Library),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.LibrarySub),
                    actionLabel: Loc.Get(Strings.Sidebar.CreatePlaylistTooltip),
                    onAction: _s.CreatePlaylist);
            }
            // Shrink 0, no Grow: the state reads as the content it replaces, above the (now empty) scroll surface.
            return new BoxEl { Key = "v3-empty", Direction = 1, Shrink = 0f, Children = [body] };
        }

        /// <summary>The one-line retry banner (rows present). CARD family: the plate's edge takes the pane edge.</summary>
        Element ErrorBanner() => new BoxEl
        {
            Key = "v3-error-banner",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Shrink = 0f,
            Padding = new Edges4(8f, 6f, 8f, 6f),
            Margin = new Edges4(SidebarRowGeometry.PaneEdge, 0f, SidebarRowGeometry.PaneEdge, 4f),
            Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
            Children =
            [
                Icon(Icons.StatusWarning, 12f, Tok.TextSecondary),
                new TextEl(Loc.Get(Strings.Common.ErrorTitle))
                {
                    Size = 12f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
                new BoxEl
                {
                    Padding = new Edges4(6f, 2f, 6f, 2f), Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                    HoverFill = Tok.FillSubtleTertiary,
                    OnClick = _s.Retry,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Common.Retry)) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary },
                    ],
                },
            ],
        };
    }

    /// <summary>#85 H1 — the ONE <c>UseDragState()</c> subscription feeding the pin band (<c>DragInFlight</c>): zero-size so a
    /// drag re-renders nothing else, a LAYOUT effect keyed on the drag EDGE. Only a Wavee resource payload counts.</summary>
    internal sealed class V3DragWatch : Component
    {
        readonly V3Session _s;

        public V3DragWatch(V3Session s) => _s = s;

        public override Element Render()
        {
            var drag = UseDragState();
            bool active = drag.Active && Drag.Unwrap(drag.Payload) is not null;
            UseLayoutEffect(() => _s.DragInFlight.SetIfChanged(active), DepKey.From(active));
            return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };
        }
    }

    // ══ 4. THE NAV BAND (W3 / W22) ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Home ABOVE the header: the destination word rail + <c>TopBar</c> as read-only chrome. It never filters (a
    /// search must not hide the way home), so it subscribes <c>LayoutVersion</c>, never the V3 state.</summary>
    internal sealed class V3NavBand : Component
    {
        readonly V3Session _s;
        // W4 — the word rail's pager state. Mounted once per pane, so plain fields persist across renders.
        NodeHandle _railViewport = NodeHandle.Null;
        readonly Signal<bool> _railCanScrollLeft = new(false);
        readonly Signal<bool> _railCanScrollRight = new(false);
        readonly Action _pageBack, _pageForward;

        public V3NavBand(V3Session s)
        {
            _s = s;
            _pageBack = () => ScrollRailBy(-1);
            _pageForward = () => ScrollRailBy(1);
        }

        public override Element Render()
        {
            _ = LayoutVersion.Value;
            var items = TopBar;
            string route = Shell.NameOf(Shell.Current.Value);

            // No early-out on an empty band: the user may empty it on purpose, and the destination rail is not part of it.
            var kids = new List<Element>(items.Count + 2) { DestinationRail(route) };
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is null || item.Hidden) continue;
                Element row = item.Target switch
                {
                    SidebarItemTarget.Action => ActionTile(item),
                    SidebarItemTarget.Track => TrackTile(item),
                    SidebarItemTarget.Entity => EntityTile(item, route),
                    _ => RouteTile(item, route),
                };
                // Keyed independently of the row's own wrapper; Direction 1 so the plate spans the pane (a ROW wrapper
                // would hug the label — the Reorderable.Item trap).
                kids.Add(new BoxEl { Key = item.Id, Direction = 1, Children = [row] });
            }

            kids.Add(Divider() with
            {
                Key = "v3-nav-rule",
                Margin = new Edges4(PaneMetrics.ContentLane - SidebarRowGeometry.PaneEdge, 4f,
                                    PaneMetrics.ContentLaneEnd - SidebarRowGeometry.PaneEdge, 4f),
            });

            return new BoxEl
            {
                Key = "v3-nav-band",
                Direction = 1, Shrink = 0f,
                // Rows carry their own inset, so padding to the bare pane edge lands their glyph on ArtX(0).
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, 0f, SidebarRowGeometry.PaneEdge, 0f),
                Children = [.. kids],
            };
        }

        /// <summary>The library destinations as a row of WORDS (13.5 on a 30-DIP band, gap 14): a 2-DIP accent underline
        /// and the count on the ACTIVE word only, a horizontal scroll whose clipped word peeks past a live edge fade.
        /// Labels never drop to glyphs — the rail scrolls instead.</summary>
        Element DestinationRail(string route)
        {
            var keys = SidebarShortcutsSection.LibraryDestinations;
            var words = new Element[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                var dest = Shell.Dest(Shell.Parse(key));
                bool on = string.Equals(route, key, StringComparison.Ordinal);

                var line = new List<Element>(2)
                {
                    new TextEl(dest.Title)
                    {
                        Size = LibraryV3Metrics.DestinationWordSize,
                        Weight = on ? (ushort)600 : (ushort)400,
                        Color = on ? Tok.TextPrimary : Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                };
                // Counts ride the active word only, and only once its relation is known (no pending plate here).
                if (on && DestinationCount(key) is { } n) line.Add(Counts.Badge(n));

                words[i] = new BoxEl
                {
                    Key = "v3-dest:" + key,
                    Direction = 1, Shrink = 0f, Gap = 2f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Height = LibraryV3Metrics.DestinationRailH,
                    Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                    OnClick = () => _s.Navigate(key, null),
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, Grow = 1f, Children = [.. line] },
                        // Always present (transparent when off) so the word never shifts; STRETCH or an empty box
                        // centred on the cross axis measures zero and never draws.
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Stretch, Height = 2f, Corners = CornerRadius4.All(1f),
                            Fill = on ? Tok.AccentDefault : ColorF.Transparent,
                        },
                    ],
                };
            }

            // ONE truth for "is there more to reach", feeding both the fade mask and the chevrons.
            bool canLeft = _railCanScrollLeft.Value, canRight = _railCanScrollRight.Value;
            EdgeMask fadeMask = (canLeft, canRight) switch
            {
                (true, true) => EdgeMask.Horizontal,
                (true, false) => EdgeMask.Left,
                (false, true) => EdgeMask.Right,
                _ => EdgeMask.None,
            };

            Element scroller = ScrollView(new BoxEl
            {
                Direction = 0, Gap = LibraryV3Metrics.DestinationWordGap, AlignItems = FlexAlign.Center,
                Padding = new Edges4(SidebarRowGeometry.ArtX(0) - SidebarRowGeometry.PaneEdge, 0f, 0f, 0f),
                Children = words,
            }, horizontal: true) with
            {
                ContentSized = true, Grow = 1f,
                // Change-only observer projected to a 2-bit key: fires on an enable/disable EDGE, never per pixel.
                OnScrollGeometryChanged = (
                    g => (g.OffsetX > 0.5f ? 1L : 0L) | (g.OffsetX < g.ContentW - g.ViewportW - 0.5f ? 2L : 0L),
                    g =>
                    {
                        _railCanScrollLeft.Value = g.OffsetX > 0.5f;
                        _railCanScrollRight.Value = g.OffsetX < g.ContentW - g.ViewportW - 0.5f;
                    }),
                OnRealized = h => _railViewport = h,
            };

            return new BoxEl
            {
                Key = "v3-dest-rail",
                Height = LibraryV3Metrics.DestinationRailH, Shrink = 0f,
                Margin = new Edges4(0f, 0f, 0f, 2f),
                ClipToBounds = true,
                // A fade with nothing behind it is a lie: masked by the live geometry, never a hardcoded side.
                EdgeFade = fadeMask == EdgeMask.None ? null : new EdgeFadeSpec(fadeMask, LibraryV3Metrics.DestinationRailFade),
                // The HOVER SCOPE for the chevrons' HoverOpacity reveal (nothing else under it carries a reveal style).
                OnHoverMove = static _ => { },
                OnPointerExit = static () => { },
                Children = [ZStack(scroller, RailChevrons(canLeft, canRight)) with { Grow = 1f }],
            };
        }

        Element RailChevrons(bool canLeft, bool canRight) => new BoxEl
        {
            Key = "v3-dest-chevrons",
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
            Children = [RailChevron(leading: true, canLeft), RailChevron(leading: false, canRight)],
        };

        /// <summary>A hover-revealed pager chevron (20 dia, glyph 10). Rest opacity 0 and not focusable (the words are the
        /// keyboard surface); hidden outright — never dimmed — where its direction has nothing to reach.</summary>
        Element RailChevron(bool leading, bool canScroll) => new BoxEl
        {
            Key = leading ? "v3-dest-chevron-prev" : "v3-dest-chevron-next",
            Width = LibraryV3Metrics.DestinationRailChevronSize, Height = LibraryV3Metrics.DestinationRailChevronSize,
            Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(LibraryV3Metrics.DestinationRailChevronSize / 2f),
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Opacity = 0f, HoverOpacity = canScroll ? 1f : 0f,
            HoverDurationMs = global::Wavee.Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
            Cursor = canScroll ? CursorId.Hand : null,
            OnClick = canScroll ? (leading ? _pageBack : _pageForward) : null,
            Focusable = false,
            Children = [Icon(leading ? Icons.ChevronLeft : Icons.ChevronRight,
                             LibraryV3Metrics.DestinationRailChevronGlyph, Tok.TextSecondary)],
        };

        /// <summary>Read the viewport's LIVE offset/extent (copied out before the call — <c>ScrollTo</c> takes its own ref)
        /// and glide 0.8 × the live viewport through the engine's one programmatic scroll seam.</summary>
        void ScrollRailBy(int dir)
        {
            if (Context.Scene is not { } scene) return;
            var vp = _railViewport;
            if (vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return;
            float offset, viewportW;
            {
                ref ScrollState sc = ref scene.ScrollRef(vp);
                offset = sc.OffsetX;
                viewportW = sc.ViewportW;
            }
            float target = offset + dir * viewportW * LibraryV3Metrics.DestinationRailPageStep;
            ScrollIntoView.ScrollTo(Context, vp, target, animate: !Motion.ReducedMotion);
        }

        /// <summary>The destination's library count, or null for Local files (never countable) and while its relation is
        /// still Unknown. <c>Total</c> so the number does not climb while a partial relation pages in.</summary>
        static int? DestinationCount(string routeKey)
        {
            LibraryEdgeKind kind;
            switch (routeKey)
            {
                case "albums": kind = LibraryEdgeKind.SavedAlbums; break;
                case "artists": kind = LibraryEdgeKind.FollowedArtists; break;
                case "liked": kind = LibraryEdgeKind.Liked; break;
                case "podcasts": kind = LibraryEdgeKind.SavedShows; break;
                default: return null;
            }
            var relation = User.Relation(kind);
            _ = relation.Changed.Value;                          // re-render when that relation publishes
            var me = User.Me;
            if (me.State(kind) == EdgeState.Unknown) return null;
            return relation.Total(me.Slot);
        }

        // ── the four tile shapes (the pane's row vocabulary minus drag, drop, menus, reorder and multi-select) ─────

        Element RouteTile(SidebarItemSpec item, string route)
        {
            var dest = Shell.Dest(Shell.Parse(item.Key));
            string key = item.Key;
            var spec = new RowSpec
            {
                Key = item.Id,
                Label = item.LabelOverride is { Length: > 0 } alias ? alias : dest.Title,
                Selected = string.Equals(route, key, StringComparison.Ordinal),
                Enabled = true,
                Depth = 0,
                Density = SidebarDensity.Cozy,
                Height = LibraryV3Metrics.NavRowHeight,
                Glyph = RowGlyphs.For(item, dest.Glyph),
                OnClick = () => _s.Navigate(key, null),
                Focusable = true,
                Overflow = false,
            };
            return EntityRow.Create(in spec);
        }

        /// <summary>An ACTION shortcut through the pane's action seam. Unavailable ⇒ visible-but-disabled with the reason
        /// as its tooltip; it never vanishes.</summary>
        Element ActionTile(SidebarItemSpec item)
        {
            if (_s.PaneRef is not { } pane) return new BoxEl { Height = LibraryV3Metrics.NavRowHeight };
            var a = pane.ResolveActionRow(item);
            var spec = new RowSpec
            {
                Key = item.Id,
                Label = a.Label,
                Enabled = a.Enabled,
                Density = SidebarDensity.Cozy,
                Height = LibraryV3Metrics.NavRowHeight,
                // Art-wide leading column + the row's own LeadingGap (W7): the label lines up with Home's.
                Leading = PaneIcon.Leading(item.IconOverride, a.Icon, a.Enabled, SidebarRowGeometry.ArtFor(SidebarDensity.Cozy)),
                OnClick = a.Enabled ? a.Click : null,
                Focusable = a.Enabled,
            };
            Element row = EntityRow.Create(in spec);
            return a.Reason is { Length: > 0 } r ? ToolTip.Wrap(row, r, grow: 1f) : row;
        }

        /// <summary>A hand-placed TRACK: click PLAYS, never navigates.</summary>
        Element TrackTile(SidebarItemSpec item)
        {
            string uri = item.Key;
            var spec = new RowSpec
            {
                Key = item.Id,
                Label = V3Session.LabelOf(item),
                Enabled = true,
                Density = SidebarDensity.Cozy,
                Height = LibraryV3Metrics.NavRowHeight,
                Leading = Cover.ArtUrl(item.FallbackImageUrl, uri, Cover.S32),
                Track = true,
                OnClick = () => { if (uri.Length > 0) _s.PaneRef?.PlayTrack(uri); },
                Focusable = true,
            };
            return EntityRow.WithPlayTrackHint(EntityRow.Create(in spec));
        }

        /// <summary>A hand-placed ENTITY: drawn from the item's own fallback title/art, navigating through the pin
        /// scheme's uri → route map exactly like the pane's rows.</summary>
        Element EntityTile(SidebarItemSpec item, string route)
        {
            string label = V3Session.LabelOf(item);
            string? target = SidebarNavBandModel.RouteKeyOf(item);
            var spec = new RowSpec
            {
                Key = item.Id,
                Label = label,
                Selected = SidebarNavBandModel.SelectsRoute(item, route),
                Enabled = true,
                Density = SidebarDensity.Cozy,
                Height = LibraryV3Metrics.NavRowHeight,
                Leading = Cover.ArtUrl(item.FallbackImageUrl, item.Key, Cover.S32,
                                       circular: item.EntityKind == SidebarEntityKind.Artist),
                OnClick = target is { Length: > 0 } t ? () => _s.Navigate(t, label) : null,
                Focusable = target is { Length: > 0 },
            };
            return EntityRow.Create(in spec);
        }
    }

    // ══ 5. THE HEADER BAND + TOOLBAR (§3.2.3) ═══════════════════════════════════════════════════════════════════════

    /// <summary><c>[library mark · "Your Library"]</c> · spacer · <c>[+]</c> · <c>[…]</c> · <c>[‹ collapse]</c>. Menu labels
    /// resolve at OPEN time, never in render.</summary>
    internal sealed class V3HeaderBand : Component
    {
        /// <summary>U+E71C — the library mark in Segoe MDL2 Assets, as an ESCAPE (a literal private-use char is invisible
        /// in editors and was silently dropped once). The face is named explicitly: the default face renders tofu.</summary>
        const string LibraryMark = "\uE71C";
        const string LibraryMarkFace = "Segoe MDL2 Assets";

        readonly V3Session _s;

        public V3HeaderBand(V3Session s) => _s = s;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            // The anchor capture must survive IconButton.Create's re-assertion of its root — a Parts modifier wins over
            // that. Memoised once (mutating TemplateParts bumps its epoch).
            var overflowParts = UseMemo(() =>
            {
                var m = new TemplateParts();
                m[IconButton.PartRoot] = b => b with { OnRealized = h => anchor.Value = h };
                return m;
            }, DepKey.Empty);

            void ToggleOverflow()
            {
                if (_s.PaneRef?.MenuOverlay is not { } svc) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = BuildOverflow();
                if (items.Count == 0) return;
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close(), minWidth: 220f),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            var kids = new List<Element>(5)
            {
                // W7 — the glyph box IS the art column (32), so the mark and every row's cover share one edge; the extra 6
                // (LeadingGap 10 − the row's 4 gap) rides the box's margin, not the row Gap.
                new BoxEl
                {
                    Width = Cover.S32, Height = Cover.S32, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Margin = new Edges4(0f, 0f, 6f, 0f),
                    Children = [Icon(LibraryMark, 16f, Tok.TextSecondary, LibraryMarkFace)],
                },
                new TextEl(Loc.Get(Strings.Sidebar.V3.Title))
                {
                    Size = 15f, Weight = 600, Color = Tok.TextPrimary,
                    Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                // H2 (#85) — the pane's own header-create drop spec: a rootlist selection files into a new folder, a track
                // set becomes a new playlist.
                Embed.Comp(() => new CreateButton(_s.CreatePlaylist, menu: _s.CreateMenuFn,
                    drop: _s.PaneRef?.HeaderCreateDropSpec(), dropActive: _s.HeaderDropActiveFn, box: 28f, glyph: 14f)),
                ToolTip.Wrap(
                    IconButton.Create(Icons.More, ToggleOverflow, parts: overflowParts, size: ControlSize.Small)
                        with { Key = "v3-overflow" },
                    Loc.Get(Strings.Sidebar.Layout.MenuTitle)),
            };

            // A drawer has no rail to collapse INTO: absent rather than dead (§3.2.14).
            if (!_s.InDrawer)
                kids.Add(ToolTip.Wrap(
                    IconButton.Create(Icons.ChevronLeft, _s.CollapsePane, size: ControlSize.Small) with { Key = "v3-collapse" },
                    Loc.Get(Strings.Sidebar.V3.Collapse)));

            return new BoxEl
            {
                Direction = 0, Height = LibraryV3Metrics.HeaderHeight, AlignItems = FlexAlign.Center, Gap = 4f,
                // W7 — LeadBandInset: the mark lands on the rows' art column, like the nav rows and the search host.
                Padding = PaneMetrics.LeadBandInset,
                Children = [.. kids],
            };
        }

        /// <summary>§6's V3 overflow, in order: <c>Sidebar layout ▸</c> (<see cref="LayoutMenu.Rows"/> embedded, so the
        /// three design radios are never re-declared) · ─── · Clear filters · Collapse Your Library (not in the drawer).
        /// The dev-mode "API Console" item is deleted (plan §9.6 Q7).</summary>
        List<MenuFlyoutItem> BuildOverflow()
        {
            var rows = new List<MenuFlyoutItem>(4);
            var layoutRows = LayoutMenu.Rows();
            if (layoutRows.Count > 0)
            {
                rows.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Sidebar.Layout.MenuTitle), layoutRows, Icons.SplitView));
                rows.Add(MenuFlyoutItem.Separator);
            }
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.V3.ClearFilters), Icons.Cancel,
                                        _s.AnyFilterActive, _s.ClearAllFilters));
            if (!_s.InDrawer)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.V3.Collapse), Icons.ChevronLeft, true, _s.CollapsePane));
            return rows;
        }
    }

    /// <summary>§3.2.2 band 2 — search + sort/view. Children are ALWAYS [search, spacer, trigger]: in the narrow shape the
    /// host's explicit Width does the morph and the spacer yields; swapping the child SET would remount the spacer and
    /// turn the morph back into a cross-fade.</summary>
    internal sealed class V3ToolbarBand : Component
    {
        readonly V3Session _s;

        public V3ToolbarBand(V3Session s) => _s = s;

        public override Element Render()
        {
            // ONE rule for the row's shape, read by the host and this toolbar alike, so the pill can never be icon-only
            // while the field is a button. Memos: a seam drag re-renders only when a boolean flips.
            var layout = UseComputed(() => LibraryV3SearchRules.Resolve(
                Sidebar.Width.Value, _s.SearchOpen.Value, V3Search.Value.Length > 0));
            var iconOnly = UseComputed(() => layout.Value.SortIconOnly);
            bool inline = layout.Value.Inline;

            return new BoxEl
            {
                Direction = 0, Height = LibraryV3Metrics.ToolbarHeight, AlignItems = FlexAlign.Center, Gap = 4f,
                Padding = PaneMetrics.LeadBandInset,
                Children =
                [
                    Embed.Comp(() => new V3SearchHost(_s)) with { Key = "v3-search" },
                    new BoxEl { Key = "v3-toolbar-spacer", Grow = inline ? 0f : 1f },
                    Embed.Comp(() => new V3SortTrigger(_s, iconOnly)) with { Key = "v3-sortview" },
                ],
            };
        }
    }

    // ══ 6. THE SEARCH HOST (§3.2.5) ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The library-only search: INLINE (pane ≥ 300) a transparent lane beside the full sort pill; NARROW a 32-DIP
    /// magnifier whose click morphs the SAME keyed host open with a Reflow tween. The magnifier never moves. Filters ONLY
    /// the sidebar projection (<c>V3Search</c>).</summary>
    internal sealed class V3SearchHost : Component
    {
        const float HostEdge = LibraryV3SearchRules.ClosedWidth;

        // Reflow, never Reveal: the host must PUSH the spacer/sort pill through real layout every tick.
        static readonly LayoutTransition HostMorph = new(
            TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(global::Wavee.Design.Motion.Fast, Easing.SmoothOut),
            Size: SizeMode.Reflow, Axes: SizeAxes.Width);

        // A keyed Enter/Exit: the editor genuinely mounts/unmounts, so its focus latch and caret never survive a collapse.
        static readonly LayoutTransition FieldFade = new(
            TransitionChannels.Opacity, TransitionDynamics.Tween(global::Wavee.Design.Motion.Fast, Easing.SmoothOut),
            Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(global::Wavee.Design.Motion.Faster, Easing.FluentAccelerate));

        readonly V3Session _s;

        public V3SearchHost(V3Session s) => _s = s;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var hostNode = UseRef<NodeHandle>(default);
            // Set by the narrow click, consumed by the field's first realization: the inline field mounts at startup and on
            // every seam drag past the threshold and must NOT steal focus then — only a deliberate open focuses.
            var focusOnMount = UseRef(false);

            var layout = UseComputed(() => LibraryV3SearchRules.Resolve(
                Sidebar.Width.Value, _s.SearchOpen.Value, V3Search.Value.Length > 0)).Value;
            bool inline = layout.Inline;
            bool expanded = layout.Expanded;
            // Quantized so a drag re-renders only when the INTEGER open width moves.
            var openWidth = UseComputed(() => MathF.Round(LibraryV3SearchRules.OpenWidth(
                Sidebar.Width.Value, PaneMetrics.LeadInset + PaneMetrics.ContentLaneEnd)));

            // PartRoot: focus the EDITOR after commit (the node is not laid out yet inside OnRealized) through
            // FirstFocusableIn, and keep the field a transparent lane in every state (no InputActive plate).
            // PartLane: the text lane starts past the pinned magnifier.
            var parts = UseMemo(() =>
            {
                var pr = new TemplateParts();
                pr[EditableText.PartRoot] = b => b with
                {
                    Fill = ColorF.Transparent, HoverFill = ColorF.Transparent,
                    OnRealized = h =>
                    {
                        if (!focusOnMount.Value) return;
                        focusOnMount.Value = false;
                        post(() =>
                        {
                            var ed = hooks.FirstFocusableIn?.Invoke(h) ?? h;
                            hooks.FocusNode?.Invoke(ed, true);
                        });
                    },
                };
                pr[EditableText.PartLane] = b => b with { Padding = new Edges4(HostEdge, 0f, 4f, 0f) };
                return pr;
            }, DepKey.Empty);

            void Open()
            {
                focusOnMount.Value = true;
                _s.OpenSearch();
            }

            var glyph = new BoxEl
            {
                Key = "search:glyph",
                Width = HostEdge, Height = HostEdge, Shrink = 0f, HitTestVisible = false,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start,
                Children = [Icon(Icons.Search, 16f, Tok.TextSecondary)],
            };

            // GROW-based query region: it tracks the host's width every tick of the narrow morph.
            Element layer = expanded
                ? new BoxEl
                {
                    Key = "search:field", Animate = FieldFade, Direction = 1, Grow = 1f, MinWidth = 0f, Height = HostEdge,
                    Children =
                    [
                        Embed.Comp(() => new EditableText
                        {
                            Text = V3Search, Placeholder = Loc.Get(Strings.Sidebar.V3.SearchPlaceholder),
                            Width = float.NaN, Height = HostEdge, FontSize = 14f, Chromeless = true, Parts = parts,
                            ShowDeleteButton = true,   // the WinUI inline ✕ — clears, keeps focus
                            // BEFORE the editor's own revert-then-blur; true pre-empts it (never "un-cancel").
                            PreviewKeyDown = e =>
                            {
                                if (e.KeyCode != Keys.Escape) return false;
                                if (LibraryV3SearchRules.OnEscape(V3Search.Peek()) == LibraryV3SearchRules.EscapeAction.Clear)
                                {
                                    V3Search.SetIfChanged("");
                                    V3Session.Resync();
                                    return true;
                                }
                                // Inline: nothing to close — let the editor's own Escape blur the empty field.
                                if (LibraryV3SearchRules.Resolve(Sidebar.Width.Peek(), true, false).Inline) return false;
                                _s.CloseSearch();
                                var host = hostNode.Value;
                                if (!host.IsNull) hooks.FocusNode?.Invoke(host, true);   // back to the magnifier
                                return true;
                            },
                            OnFocusChanged = gained =>
                            {
                                if (gained) return;
                                // Narrow only: an EMPTY field that lost focus collapses; a query stays on screen.
                                if (!LibraryV3SearchRules.Resolve(Sidebar.Width.Peek(), true, false).Inline
                                    && LibraryV3SearchRules.ClosesOnBlur(V3Search.Peek()))
                                    _s.CloseSearch();
                            },
                        }),
                    ],
                }
                // COLLAPSED (narrow only): the tooltip rides THIS layer, so it exists only while the control is a button.
                : ToolTip.Wrap(new BoxEl
                {
                    Key = "search:hit", Width = HostEdge, Height = HostEdge,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand,
                    OnClick = Open,
                }, Loc.Get(Strings.Sidebar.V3.SearchTooltip));

            return new BoxEl
            {
                Key = "v3-search", ZStack = true, ClipToBounds = true, Height = HostEdge,
                // INLINE grows AND yields (the placeholder is wider than a 300 pane's lane); NARROW is an explicit width so
                // the reflow tween has a from and a to.
                Grow = inline ? 1f : 0f, Shrink = inline ? 1f : 0f, MinWidth = 0f,
                Width = inline ? float.NaN : expanded ? openWidth.Value : HostEdge,
                Animate = inline ? null : HostMorph,
                Corners = Radii.ControlAll,
                Fill = ColorF.Transparent,
                HoverFill = expanded ? ColorF.Transparent : Tok.FillSubtleSecondary,
                PressedFill = expanded ? ColorF.Transparent : Tok.FillSubtleTertiary,
                BrushTransitionMs = global::Wavee.Design.Motion.Fast,
                Role = AutomationRole.Button, Focusable = !expanded, Cursor = expanded ? null : CursorId.Hand,
                OnRealized = h => hostNode.Value = h,
                OnClick = expanded ? null : Open,
                Children = [glyph, layer],
            };
        }
    }

    // ══ 7. THE CHIP RAIL (§3.2.4, W14) ══════════════════════════════════════════════════════════════════════════════

    /// <summary>The filter rail's three shapes from <see cref="LibraryV3ChipStrip"/> — idle · filtered (✕ pops in, options
    /// spill) · fused (the facet's key, the morph). An unevidenced qualifier is CLEARED. ONE tab stop roving by KEY
    /// (←/→/Home/End); Space/Enter commits; selection does not follow focus.</summary>
    internal sealed class V3ChipRail : Component
    {
        /// <summary>The ✕'s own pop-scale (0.6 → 1): its mount/unmount IS the motion.</summary>
        static readonly LayoutTransition ClearMotion = new(
            TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(global::Wavee.Design.Motion.Fast, Easing.SmoothOut),
            Enter: new EnterExit(Sx: 0.6f, Sy: 0.6f, Opacity: 0f, Active: true),
            Exit: new EnterExit(Sx: 0.6f, Sy: 0.6f, Opacity: 0f, Active: true));

        /// <summary>A facet's FLIP + an exit leg toward the ✕ (−12). No entrance: a returning facet reflows into place.</summary>
        static readonly LayoutTransition FacetMotion = new(
            TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(220f, Easing.SmoothOut),
            Exit: new EnterExit(Dx: -12f, Opacity: 0f, Active: true));

        /// <summary>A spilled option enters from the facet's side (+12) and, when picked, exits toward the pill (−56) — it
        /// flies INTO the fused pill it becomes the value of.</summary>
        static readonly LayoutTransition OptionMotion = new(
            TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
            Enter: new EnterExit(Dx: 12f, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true));

        /// <summary>The fused pill's width reflow (0.2.9 <c>ConcertUi.SegmentedPill</c>).</summary>
        static readonly LayoutTransition FusedMotion = new(
            TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(260f, Easing.SmoothOut),
            Size: SizeMode.Reflow, Axes: SizeAxes.Width);

        /// <summary>The raised segment docks in FROM the option's side.</summary>
        static readonly LayoutTransition SegmentDock = new(
            TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(300f, Easing.SmoothOut),
            Enter: new EnterExit(Dx: 56f, Opacity: 0.4f, Active: true));

        readonly V3Session _s;

        public V3ChipRail(V3Session s) => _s = s;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var focusedKey = UseSignal<string?>(null);
            var nodes = UseMemo(static () => new Dictionary<string, NodeHandle>(8), DepKey.Empty);
            var stale = UseMemo(static () => new List<string>(4), DepKey.Empty);
            var controller = UseMemo(static () => new ScrollController(), DepKey.Empty);

            int filter = LibraryV3Metrics.NormalizeFilter(V3Filter.Value);
            int qualifier = LibraryV3Metrics.NormalizeQualifier(V3Qualifier.Value);
            var cell = V3Session.Cell;
            _ = cell.Version.Value;                        // QualifiersAvailable moves with the projection
            bool qualifiersAvailable = cell.QualifiersAvailable;
            bool qualifierRelevant = filter == (int)SidebarV3Filter.Playlists && qualifiersAvailable;

            UseLayoutEffect(() =>
            {
                if (qualifierRelevant || V3Qualifier.Peek() == (int)SidebarV3Qualifier.Any) return;
                SetV3Qualifier((int)SidebarV3Qualifier.Any);
                V3Session.Resync();
            }, DepKey.From(qualifierRelevant ? 1 : 0, qualifier));

            var slots = LibraryV3ChipStrip.Slots(filter, qualifier, qualifiersAvailable);
            int focusIdx = LibraryV3ChipStrip.FocusIndex(slots, focusedKey.Value);

            // Prune handles for keys this render no longer emits — NOT a blind Clear(): OnRealized fires only at mount,
            // so a key that survives under the same identity (the facet ⇄ fused "v3f{code}") never re-registers.
            stale.Clear();
            foreach (var key in nodes.Keys)
            {
                bool live = false;
                foreach (var slot in slots) if (slot.Key == key) { live = true; break; }
                if (!live) stale.Add(key);
            }
            foreach (var key in stale) nodes.Remove(key);

            // HOME the rail on a filter/qualifier change: the ✕ and the selected facet always lead it.
            UseLayoutEffect(() => controller.ScrollTo(0f), DepKey.From(filter, qualifier));

            var chips = new Element[slots.Count];
            for (int i = 0; i < slots.Count; i++) chips[i] = Chip(nodes, slots[i], qualifier, i == focusIdx);

            return ScrollView(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                // The LEAD lane (W7): the first chip lands on the art column.
                Padding = PaneMetrics.LeadBandInset,
                OnKeyDown = e => Rove(e, hooks, nodes, focusedKey, slots, Apply),
                // The zero-size a11y label sits OUTSIDE the gapped row, or it collects a 6-DIP gap before the first pill.
                Children =
                [
                    GroupLabel(),
                    new BoxEl { Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = chips },
                ],
            }, horizontal: true) with
            {
                Grow = 0f, Height = LibraryV3Metrics.ChipRailHeight, AutoEdgeFade = true, SuppressScrollBar = true,
                ScrollKey = "sidebar.v3.chips", Controller = controller,
            };
        }

        /// <summary>What ANY non-Clear chip tap — or Space/Enter on the roved chip — writes: the slot carries the answer.</summary>
        void Apply(V3ChipSlot slot)
        {
            if (slot.Kind == V3ChipKind.Clear) { _s.ClearAllFilters(); return; }
            if (slot.SelectFilter != LibraryV3Metrics.NormalizeFilter(V3Filter.Peek())) SelectFilter(slot.SelectFilter);
            SetV3Qualifier(slot.SelectQualifier);
            V3Session.Resync();
        }

        /// <summary>A non-Playlists kind clears the qualifier and drops a Custom sort (persisted fallback); every kind
        /// change leaves the drill level (the folder may not even be in the new kind set).</summary>
        void SelectFilter(int code)
        {
            SetV3Filter(code);
            if (code != (int)SidebarV3Filter.Playlists)
            {
                SetV3Qualifier((int)SidebarV3Qualifier.Any);
                if (V3Sort.Peek() == (int)SidebarV3Sort.Custom) SetV3Sort((int)SidebarV3Sort.Recents, false);
            }
            _s.ResetDrill();
        }

        Element Chip(Dictionary<string, NodeHandle> nodes, V3ChipSlot slot, int qualifier, bool focusable)
            => slot.Kind switch
            {
                V3ChipKind.Clear => ClearPill(nodes, focusable, _s.ClearAllFilters),
                V3ChipKind.Fused => FusedPill(nodes, slot, qualifier, focusable, () => Apply(slot)),
                V3ChipKind.Option => Pill(nodes, slot, Loc.Get(LibraryV3Labels.Qualifier(slot.Code)), 12f, focusable,
                                          OptionMotion, () => Apply(slot)),
                // #85 H4 — a PLAIN tap filters; a DOUBLE-CLICK on a facet with a page (Albums/Artists/Podcasts) navigates.
                // Keyboard Space/Enter always filters.
                _ => Pill(nodes, slot, Loc.Get(LibraryV3Labels.Filter(slot.Code)), 13f, focusable, FacetMotion,
                          () => Apply(slot),
                          slot.Route is { Length: > 0 } r
                              ? () => _s.Navigate(r, Loc.Get(LibraryV3Labels.Filter(slot.Code)))
                              : null),
            };

        /// <summary>Painted at ZERO SIZE (not merely zero opacity): the only place a group label can live, and it must not
        /// cost the rail a phantom scroll inch.</summary>
        static Element GroupLabel() => new BoxEl
        {
            Key = "v3-filter-group",
            Width = 0f, Height = 0f, ClipToBounds = true, HitTestVisible = false,
            Children = [new TextEl(Loc.Get(Strings.Sidebar.A11y.FilterGroup)) { Size = 1f, MaxLines = 1 }],
        };

        /// <summary>The leading ✕ (28 circle). <c>Interaction.Control</c>, not Subtle: it has no border or label doing the
        /// work, so it must read as a control at rest.</summary>
        static Element ClearPill(Dictionary<string, NodeHandle> nodes, bool focusable, Action onClick) => ToolTip.Wrap(
            new BoxEl
            {
                Key = "v3-clear",
                Animate = ClearMotion,
                Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.FullAll,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = onClick,
                OnRealized = h => nodes["v3-clear"] = h,
                Children = [Icon(Icons.Cancel, 12f, Tok.TextPrimary)],
            }.Interactive(Interaction.Control),
            Loc.Get(Strings.Sidebar.V3.ClearFilters));

        /// <summary>A facet or option — the SAME padding selected or not, so selecting never reflows the label. Selection is
        /// COLOUR only (accent fill + border + <c>Tok.OnAccent</c> 600), cross-faded over 167 ms.</summary>
        static Element Pill(Dictionary<string, NodeHandle> nodes, V3ChipSlot slot, string label, float fontSize,
                            bool focusable, LayoutTransition animate, Action onClick, Action? onNavigate = null)
        {
            string key = slot.Key;
            return new BoxEl
            {
                Key = key,                 // the fused pill reuses a facet's key — that shared identity IS the morph
                Animate = animate,
                Direction = 0, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(12f, 0f, 12f, 0f),
                Corners = Radii.FullAll,
                Fill = slot.Selected ? Tok.AccentDefault : Tok.FillControlDefault,
                HoverFill = slot.Selected ? Tok.AccentSecondary : Tok.FillControlSecondary,
                PressedFill = slot.Selected ? Tok.AccentTertiary : Tok.FillControlTertiary,
                BorderWidth = 1f, BorderColor = slot.Selected ? Tok.AccentDefault : Tok.StrokeControlDefault,
                BrushTransitionMs = global::Wavee.Design.Motion.Fast,
                Role = AutomationRole.RadioButton, Cursor = CursorId.Hand,
                Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                // A route-bearing chip trades OnClick for OnPointerReleased so a double-click is distinguishable.
                OnClick = onNavigate is null ? onClick : null,
                OnPointerReleased = onNavigate is null ? null : args =>
                {
                    if (args.ClickCount >= 2) onNavigate();
                    else onClick();
                },
                OnRealized = h => nodes[key] = h,
                Children =
                [
                    Caption(label) with
                    {
                        Size = fontSize, Weight = (ushort)(slot.Selected ? 600 : 400),
                        Color = slot.Selected ? Tok.OnAccent : Tok.TextPrimary, MaxLines = 1,
                    },
                ],
            };
        }

        /// <summary>The FUSED pill at 0.2.9's <c>SegmentedPillStyle.Sidebar</c> register: a 28 accent capsule, an OPAQUE raised
        /// segment (✓ name), the value, and an ✕ (tapping clears one step back — no menu). A radio position, not a tab stop.</summary>
        static Element FusedPill(Dictionary<string, NodeHandle> nodes, V3ChipSlot slot, int qualifier, bool focusable,
                                 Action onClick)
        {
            string key = slot.Key;
            const float segment = 22f;
            return new BoxEl
            {
                Key = key,
                Animate = FusedMotion,
                Direction = 0, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
                Padding = new Edges4(3f, 3f, 9f, 3f),
                Corners = Radii.FullAll,
                Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
                BrushTransitionMs = global::Wavee.Design.Motion.Fast,
                Role = AutomationRole.RadioButton, Cursor = CursorId.Hand,
                Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = onClick,
                OnRealized = h => nodes[key] = h,
                Children =
                [
                    new BoxEl
                    {
                        Key = key + ":seg",
                        Animate = SegmentDock,
                        Direction = 0, Height = segment, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 4f,
                        Padding = new Edges4(8f, 0f, 8f, 0f), Corners = CornerRadius4.All(segment / 2f),
                        Fill = Tok.FillControlSolid, Shadow = Elevation.Card,
                        Children =
                        [
                            Icon(Icons.Check, 10f, Tok.AccentTextPrimary) with { Shrink = 0f },
                            new TextEl(Loc.Get(LibraryV3Labels.Filter(slot.Code)))
                            {
                                Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1,
                            },
                        ],
                    },
                    new TextEl(Loc.Get(LibraryV3Labels.Qualifier(qualifier)))
                    {
                        Size = 12f, Weight = 600, Color = Tok.OnAccent, MaxLines = 1,
                    },
                    Icon(Icons.Cancel, 9f, Tok.OnAccent) with { Shrink = 0f },
                ],
            };
        }

        /// <summary>Roving focus over whatever is currently ON the rail, tracked by KEY so it survives a relayout.</summary>
        static void Rove(KeyEventArgs e, InputHooks hooks, Dictionary<string, NodeHandle> nodes, Signal<string?> focusedKey,
                         List<V3ChipSlot> slots, Action<V3ChipSlot> activate)
        {
            if (e.Handled) return;
            int n = slots.Count;
            if (n == 0) return;
            int cur = LibraryV3ChipStrip.FocusIndex(slots, focusedKey.Peek());
            int next;
            switch (e.KeyCode)
            {
                case Keys.Left: next = cur == 0 ? n - 1 : cur - 1; break;
                case Keys.Right: next = cur == n - 1 ? 0 : cur + 1; break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = n - 1; break;
                case Keys.Space:
                case Keys.Enter:
                    activate(slots[cur]);
                    e.Handled = true;
                    return;
                default:
                    return;
            }
            focusedKey.Value = slots[next].Key;
            if (nodes.TryGetValue(slots[next].Key, out var h) && !h.IsNull)
                (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(h);
            e.Handled = true;
        }
    }

    // ══ 8. THE SORT / VIEW TRIGGER + FLYOUT (§3.2.6) ════════════════════════════════════════════════════════════════

    /// <summary><c>[sort · label · direction · rule · view]</c> 28-DIP pill (icon-only 28×28 when search owns the row or the
    /// pane is under 280). Custom order exists only under Playlists; leaving the lens falls back to Recents, persisted.</summary>
    internal sealed class V3SortTrigger : Component
    {
        readonly V3Session _s;
        readonly IReadSignal<bool> _iconOnly;

        public V3SortTrigger(V3Session s, IReadSignal<bool> iconOnly)
        {
            _s = s;
            _iconOnly = iconOnly;
        }

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);

            int sort = LibraryV3Metrics.NormalizeSort(V3Sort.Value);
            int view = LibraryV3Metrics.NormalizeView(V3View.Value);
            bool desc = V3Desc.Value;
            int filter = LibraryV3Metrics.NormalizeFilter(V3Filter.Value);
            bool iconOnly = _iconOnly.Value;

            // Returning to Playlists deliberately does NOT restore Custom (an implicit reorder mode would surprise).
            UseLayoutEffect(() =>
            {
                if (V3Sort.Peek() != (int)SidebarV3Sort.Custom || filter == (int)SidebarV3Filter.Playlists) return;
                SetV3Sort((int)SidebarV3Sort.Recents, false);
                V3Session.Resync();
            }, DepKey.From(sort, filter));

            void Toggle()
            {
                if (_s.PaneRef?.MenuOverlay is not { } svc) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = svc.Open(
                    () => anchor.Value,
                    static () => Embed.Comp(static () => new V3SortPanel()),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            bool showDirection = SidebarSort.SupportsDirection((SidebarV3Sort)sort);
            var kids = new List<Element>(5) { Icon(Icons.Sort, 14f, Tok.TextSecondary) };
            if (!iconOnly)
            {
                kids.Add(new TextEl(Loc.Get(LibraryV3Labels.Sort(sort)))
                {
                    Size = 13f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis, Shrink = 1f,
                });
                if (showDirection) kids.Add(Icon(desc ? Icons.ChevronUp : Icons.ChevronDown, 10f, Tok.TextTertiary));
                kids.Add(new BoxEl { Width = 1f, Height = 16f, Fill = Tok.StrokeDividerDefault, Shrink = 0f });
                kids.Add(Icon(LibraryV3Metrics.IsGrid(view) ? Icons.ViewGrid : Icons.ViewList, 14f, Tok.TextSecondary));
            }

            var pill = new BoxEl
            {
                Direction = 0, Height = 28f, AlignItems = FlexAlign.Center, Gap = 5f, Shrink = 0f,
                Width = iconOnly ? 28f : float.NaN,
                Justify = iconOnly ? FlexJustify.Center : FlexJustify.Start,
                Padding = iconOnly ? new Edges4(0f, 0f, 0f, 0f) : new Edges4(10f, 0f, 8f, 0f),
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnRealized = h => anchor.Value = h,
                OnClick = Toggle,
                Children = [.. kids],
            }.Interactive(Interaction.Subtle);

            // The tooltip is the accessible name in both forms ("Recents · List" alone does not say what a tap does).
            string name = Loc.Get(Strings.Sidebar.A11y.SortView);
            if (desc && showDirection) name = name + " · " + Loc.Get(Strings.Sidebar.V3.Sort.Reversed);
            return ToolTip.Wrap(pill, name);
        }
    }

    /// <summary>The flyout body (its own component: rows track live signals while open). "Sort by" · four sorts (+ Custom
    /// order under Playlists) · divider · "View as" · the 4-cell bank. NO size row: the cell derives from the pane width.</summary>
    internal sealed class V3SortPanel : Component
    {
        public override Element Render()
        {
            int sort = LibraryV3Metrics.NormalizeSort(V3Sort.Value);
            bool desc = V3Desc.Value;
            int view = LibraryV3Metrics.NormalizeView(V3View.Value);
            bool customAvailable = LibraryV3Metrics.NormalizeFilter(V3Filter.Value) == (int)SidebarV3Filter.Playlists;

            var rows = new List<Element>(10)
            {
                Header(Loc.Get(Strings.Library.SortBy)),
                SortRow((int)SidebarV3Sort.Recents, sort, desc),
                SortRow((int)SidebarV3Sort.RecentlyAdded, sort, desc),
                SortRow((int)SidebarV3Sort.Alphabetical, sort, desc),
                SortRow((int)SidebarV3Sort.Creator, sort, desc),
            };
            if (customAvailable) rows.Add(SortRow((int)SidebarV3Sort.Custom, sort, desc));
            rows.Add(PanelDivider());
            rows.Add(Header(Loc.Get(Strings.Library.ViewAs)));
            rows.Add(ViewToggles(view));

            return new BoxEl
            {
                Direction = 1, Gap = 1f, MinWidth = 220f,
                Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS),
                Children = [.. rows],
            };
        }

        /// <summary>Tapping the ACTIVE sort flips the direction; a DIFFERENT sort selects it and resets the direction.
        /// Custom order pins the direction off and shows no chevron. The chevron and check stay neutral ink — accent is
        /// spent on the label only.</summary>
        static Element SortRow(int key, int sort, bool desc)
        {
            bool active = sort == key;
            bool directional = SidebarSort.SupportsDirection((SidebarV3Sort)key);
            return new BoxEl
            {
                Key = "v3sort" + key,
                Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Padding = new Edges4(10f, 0f, 8f, 0f), Corners = CornerRadius4.All(5f),
                Role = AutomationRole.RadioButton, Cursor = CursorId.Hand, Focusable = true,
                OnClick = () =>
                {
                    if (V3Sort.Peek() == key) SetV3Sort(key, directional && !V3Desc.Peek());
                    else SetV3Sort(key, false);
                    V3Session.Resync();
                },
                Children =
                [
                    new TextEl(Loc.Get(LibraryV3Labels.Sort(key)))
                    {
                        Size = 14f, Weight = (ushort)(active ? 600 : 400),
                        Color = active ? Tok.AccentTextPrimary : Tok.TextPrimary,
                        Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    active && directional
                        ? Icon(desc ? Icons.ChevronUp : Icons.ChevronDown, 11f, Tok.TextSecondary)
                        : new BoxEl(),
                    active ? Icon(Icons.Check, 12f, Tok.TextPrimary) : new BoxEl { Width = 12f },
                ],
            }.Interactive(Interaction.Subtle);
        }

        /// <summary>The 4-cell view bank (library sort panel metrics: 40×30, radius 5, accent when on; ViewList 14 /
        /// ViewList 16 / ViewGrid 12 / ViewGrid 15). <c>Tok.OnAccent</c> is the contrast-picked ink for a live accent.</summary>
        static Element ViewToggles(int view)
        {
            var cells = new Element[4];
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                bool on = view == i;
                (string glyph, float size, string label) = i switch
                {
                    0 => (Icons.ViewList, 14f, Loc.Get(Strings.Library.View.CompactList)),
                    1 => (Icons.ViewList, 16f, Loc.Get(Strings.Library.View.List)),
                    2 => (Icons.ViewGrid, 12f, Loc.Get(Strings.Library.View.CompactGrid)),
                    _ => (Icons.ViewGrid, 15f, Loc.Get(Strings.Library.View.Grid)),
                };
                var cell = new BoxEl
                {
                    Key = "v3view" + i,
                    Width = 40f, Height = 30f, Grow = 1f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(5f),
                    Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary,
                    HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
                    BrushTransitionMs = global::Wavee.Design.Motion.Fast,
                    Role = AutomationRole.RadioButton, Cursor = CursorId.Hand, Focusable = true,
                    OnClick = () => SetV3View(idx),
                    Children = [Icon(glyph, size, on ? Tok.OnAccent : Tok.TextSecondary)],
                };
                cells[i] = ToolTip.Wrap(cell, label);   // an icon-only cell's accessible name is its tooltip
            }
            return new BoxEl { Direction = 0, Gap = 4f, Padding = new Edges4(2f, 2f, 2f, 4f), Children = cells };
        }

        static Element Header(string text) => new BoxEl
        {
            Padding = new Edges4(8f, 6f, 8f, 2f),
            Children = [global::Wavee.Design.Type.Eyebrow(text) with { Color = Tok.TextTertiary }],
        };

        static Element PanelDivider() => new BoxEl
        {
            Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(4f, 4f, 4f, 4f),
        };
    }
}
