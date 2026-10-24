// ── Entities/Track.Table.Chrome.cs ────────────────────────────────────────────────────────────────────────────────
// the detail track table's CHROME: the command bar (labeled commands promoted into the measured pane, the rest evicted
// into "…" with 16-DIP promotion hysteresis and a fit latch while search is open), the in-list search disclosure
// (66 → 160…280 over 260 ms, Reflow), the context band's words Find · Filter · Play, the column header (one ColumnSet +
// one TrackSize[] with the rows, keyed cells, sort carets), the Sort / More flyouts (Row size lives in "…"), the 368-wide filter card
// and the selection command bar's commands. The TableHost partial of Track.Table.cs.
// T8b: the PICTURES of the header, the command bar's buttons and surface (the selection bar rides it), the filter card and the
// placeholder row are the structure's (`TrackListStyle.Chrome`, Classic: `Themes\Classic\Structure\Lists\ClassicTrackChrome.cs`);
// what stays here is every decision, signal and overlay behind them.
// Plus, at STRUCT level, the embedded arm's lane constants (§6) — what the library's album pane shimmers under before a tracklist has answered (library rework §5.5;
// the header shim and the counted shimmer are Classic's `ClassicLibraryAlbumPane`, T10c1).
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: 2600 lines (this file + Track.Table.cs)
// Spec: ch 01 §9 / ch 04 §9 (W4 · W10 · W11 · W21, §5 motion, §6 interaction)
//
// Every piece here that re-renders often is its OWN component reading its own signals (the header on shape/sort/checks,
// the search host on query/focus, the sort/density/select/filter buttons on their state), so typing a query or flipping
// a sort never re-renders the host and never touches the rows. The command bar itself is a Responsive box over ONE
// cached builder: it rebuilds on its measured width and on the signals the builder reads, not on a host render.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Track
{
    sealed partial class TableHost
    {
        /// <summary>The ONE search-disclosure duration: the width tween, the icon↔field cross-fade, the chrome brush fade
        /// and the underline all run on it, so the box growing and its styling resolving read as one motion.</summary>
        const float SearchExpandMs = 260f, SearchCollapseMs = 180f;
        /// <summary>What the bar's lane takes from the measured width before the commands fit: its own horizontal padding, both sides (the
        /// surface's 6 + 6). Named from the lane it describes, so the fit and the picture cannot drift apart (TC5 resolved the old literal 12).</summary>
        const float ToolbarPaneInset = 2f * Detail.VerticalLayout.ToolbarSurfacePadX;

        Action<Action>? _post;
        InputHooks? _hooks;

        /// <summary>The structure's command-bar kit (T8b); a host with no registry gets one that draws nothing (logged with the rows).</summary>
        /// <summary>The structure's kit as drawn in this table's tiers, read TRACKED (the calling render re-runs on a crossing).</summary>
        ITrackCommandKit Kit => (_listStyle?.Chrome.Commands ?? NoChromeKit.Instance).In(TiersNow());
        readonly Signal<bool> _searchExpanded = new(false);
        readonly Signal<bool> _searchFocused = new(false);
        bool _restoreSearchFocus;
        NodeHandle _searchButtonNode;

        // Conservative first-frame LABELED widths (Play next · Tune · Shuffle · Sort · Filter), refined by the measured commands; the
        // fit resolves against these and the pane.
        readonly Signal<int> _toolbarEpoch = new(0);
        readonly float[] _toolbarWidths =
            [CommandBarLayout.Nominal.Play, CommandBarLayout.Nominal.Tune, CommandBarLayout.Nominal.Shuffle, CommandBarLayout.Nominal.Sort, CommandBarLayout.Nominal.Filter];
        CommandBarFit? _toolbarFit;
        // The fit LATCHED when search opened, with the pane it resolved against: promoting/evicting mid-flight re-measures,
        // bumps the epoch and would hand the width tween a new target. A genuine pane resize drops the latch.
        (float Available, CommandBarFit Fit)? _searchOpenFit;
        int _selectionPrevCount;
        // A structure's worded Find and inline Select (ITrackCommandKit.FindCommand / SelectCommand): their measured widths join the fit's budget (the
        // first frame resolves against the nominals), and an inline Select leaves the "…" menu.
        float _findWordW = FindWordNominal, _selectW = SelectNominal;
        bool _inlineSelect;
        const float FindWordNominal = 72f, SelectNominal = 64f;

        Func<float, Element> _buildToolbar = null!;
        Func<int, Element> _selectionCommands = null!;
        Action _openSearch = null!, _toggleFind = null!, _exitSelection = null!, _selectAllTracks = null!;
        Action<NodeHandle> _captureSearchButton = null!;
        // the shared selection lane's providers (cached: the lane's "…" re-reads them when it OPENS)
        Func<IReadOnlyList<Track>> _laneTracks = null!;
        Func<IReadOnlyList<Episode>> _laneEpisodes = null!;
        Func<PlaylistHost> _laneHost = null!;
        // the unavailable-songs bar (contract 1.22): its verb, whether it is on (the hero arm's clip inset follows it), and its cached tally
        Action _hideUnavailable = null!;
        readonly Signal<bool> _unavailableShown = new(false);
        UnavailableTally _tally;
        uint _tallyVersion, _tallyPublication;
        bool _tallySeeded;

        void InitChrome()
        {
            _buildToolbar = BuildToolbar;
            _hideUnavailable = () => { var f = _filters.Peek(); SetFilters(f with { Flags = f.Flags | FilterFlags.PlayableOnly }); };
            _selectionCommands = SelectionCommands;
            _openSearch = () => _searchExpanded.Value = true;
            _toggleFind = () => { if (_searchExpanded.Peek()) CollapseSearch(restoreFocus: false); else _searchExpanded.Value = true; };
            _exitSelection = () => { _selection.ClearSelection(); if (_multi.Peek()) SetMultiSelect(false); };
            _selectAllTracks = SelectAllTracks;
            _laneTracks = SelectedTracks;
            _laneEpisodes = SelectedEpisodes;
            _laneHost = HostFor;
            _captureSearchButton = CaptureSearchButton;
        }

        // Reflow, never Reveal: the field must PUSH its neighbours through real layout. No ExitDynamics — the width runs
        // 260 ms both ways; only the swap and the underline take the 180 ms exit.
        static readonly LayoutTransition s_searchDisclosure = new(TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(SearchExpandMs, Easing.SmoothOut), Size: SizeMode.Reflow, Axes: SizeAxes.Width);

        static readonly LayoutTransition s_searchSwap = new(TransitionChannels.Opacity,
            TransitionDynamics.Tween(SearchExpandMs, Easing.SmoothOut),
            Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(SearchCollapseMs, Easing.FluentAccelerate));

        internal static PopupOptions MenuPopup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false };

        // ══ 1. THE CHROME STACK ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Toolbar · [unavailable bar] · chips · lens · header (two-column); lens · header (no toolbar); [unavailable bar] · chips · lens ·
        /// header (the hero arm, whose toolbar lives in the hero). The chips sit BETWEEN the bar and the header — they change what
        /// the rows contain — and the lens directly above the header, in the header's own register (W18). No fill: the
        /// stuck stratum is an unpainted omission and the rows are clipped at its lower edge.</summary>
        Element Chrome(in Shape shape, Element? chips, Element? lens)
        {
            // An empty list that shows its recommendations lead has nothing to sort, filter or head: a theme that says so draws no chrome over it (one box, so a row of chrome
            // that comes back with the first song does so as a change of the box's height, not of the tree).
            if (EmptyLeadHides) return new BoxEl { Key = "chrome", Height = 0f, Shrink = 0f, HitTestVisible = false };
            float padX = RowMetrics.PadXFor(shape.Set.Tier);
            bool vertical = VerticalArm;
            bool toolbar = !vertical && _latest.ShowToolbar;
            // The header pays its OWN horizontal padding (the structure's header template insets its rule to the row plates' left
            // edge, tuned Classic D9 / V10); the toolbar sits at the plates' edge less the bar's own 6-DIP inset and keeps the
            // rows' right inset (the list bar's 6 / 14, board C-Album); the chips and the lens keep the rows' gutter.
            Element Gutter(Element e) => new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(padX, 0f, padX, 0f), Children = [e] };
            Element header = Embed.Comp(() => new TableHeader(this)) with { Key = "header" };
            var stack = new List<Element>(4);
            // The page bar (a page with no rail, tuned Classic D16) sits under the page head: its commands start on the head's words (the
            // rows' 8-DIP plate inset less the bar's own 6) on both sides, it carries the chips inline (D8), and 4 DIP of air part it from
            // the column header (board C-Liked: the head is 156).
            bool pageBar = PageBar;
            float pageBarInset = RowMetrics.RowInset - Detail.VerticalLayout.ToolbarSurfacePadX;
            if (toolbar)
                stack.Add(new BoxEl
                {
                    Key = "chrome:bar", Direction = 1, MinWidth = 0f,
                    Padding = pageBar ? new Edges4(pageBarInset, 0f, pageBarInset, 0f) : new Edges4(0f, 0f, RowMetrics.RowInset, 0f),
                    Margin = pageBar ? new Edges4(0f, 0f, 0f, Spacing.XS) : default,
                    Children = [Toolbar()],
                });
            // The unavailable-songs bar (boards C-Unavailable / C-Unavailable-Compact, contract 1.22) leads what narrows the rows: under the
            // list bar in the two-column arm, first in the hero arm's pinned chrome (the hero above it). Its slot stays mounted on a track list
            // and eases its own height, so it never moves the rows by a snap.
            if (HasUnavailableBar)
                stack.Add(Embed.Comp(() => new TableUnavailableBar(this)) with { Key = "chrome:unavailable" });
            if (chips is not null && !pageBar && (vertical || _latest.ShowToolbar))
                stack.Add(Gutter(chips) with { Key = "chrome:chips" });
            if (lens is not null) stack.Add(Gutter(lens) with { Key = "chrome:lens" });
            stack.Add(header);
            // The air under the header follows the bar above it: the two-column arm's, and the hero list's columns form, which draws the bar in its hero
            // (audit B9). An easing spacer (a margin cannot ease) in one wrapper in every form, so the form never re-parents the header.
            stack.Add(new BoxEl
            {
                Key = "chrome:air", Height = toolbar || ColumnsForm ? Spacing.XS : 0f, Shrink = 0f, HitTestVisible = false,
                Animate = MotionRecipes.CardResizeHeight,
            });
            Element[] children = [new BoxEl { Direction = 1, MinWidth = 0f, Children = stack.ToArray() }];
            // The 8 DIP above the header is AIR UNDER THE TOOLBAR, so an arm with no toolbar must not pay it: the
            // embedded pane's shimmer draws its own header shim (`ClassicLibraryAlbumPane`, compact), which states none, and the 8 the real
            // chrome used to add anyway moved every row down by that much on the frame the shimmer released.
            return new BoxEl
            {
                Key = "chrome", Direction = 1,
                Padding = new Edges4(0f, toolbar ? Spacing.S : 0f, 0f, 0f),
                Children = children,
            };
        }

        Element Toolbar() => Responsive.Of("detail-track-commandbar", _buildToolbar, fallback: _lastW > 0f ? _lastW : 760f);

        // ══ 2. THE COMMAND BAR ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The facts sheet's toggle this table's bar carries (tuned Classic I1): the frame's own answer, threaded down as
        /// the presence of the toggle object — the vertical arm's through its spec, the two-column arm's through the args.</summary>
        Detail.InsightsToggle? BarInsights => VerticalArm ? _latest.Vertical?.Insights : _latest.Insights;

        /// <summary>The list bar is the PAGE's bar (tuned Classic D16): a page with no rail (<see cref="Detail.Config.Rail"/> false) in its
        /// wide arm leads the bar with its Play split, a labelled Shuffle and its chips, under the page head.</summary>
        bool PageBar => (!VerticalArm || ColumnsForm) && !_latest.Embedded && !Cfg.Rail;

        /// <summary>The list bar (tuned Classic D3, decisions D-6 / D-10): left [Play next │ ⌄] [Shuffle] [Tune]; right [Filter (n)]
        /// [Sort ⌄] [Find] [Insights] [⋯]. Row size, Select and the column opt-ins live in "…" only.</summary>
        Element BuildToolbar(float available)
        {
            _ = _toolbarEpoch.Value;   // measured labeled widths refine the first-frame budgets
            if (_selectionVisible?.Value == true && _dockHost is null) return SelectionSurface("selection");

            // The hero list's columns form (audit B9) draws the two-column arm's bar; the read is tracked, so a crossing that flips the form rebuilds it.
            bool vertical = VerticalArm && HeroFormLive;
            bool pageBar = PageBar;
            // A structure that draws Tune in its hero at this tier (DetailTableTier.TuneInHero, WinUI) leaves it off the bar: one home per verb.
            bool hasTune = P.Tune is not null && !(vertical && ShellPolicy.Tracked.DetailTiers.For(TiersNow()).Table.TuneInHero);
            var insights = BarInsights;
            bool explicitSearch = _searchExpanded.Value;
            var w = _toolbarWidths;
            // The page bar's lead (split · Shuffle · divider) is ONE measured command in the Play slot and Shuffle never leaves it; the chips
            // keep their least width (D8), so the view commands are what a narrow bar evicts.
            Element? chips = pageBar ? P.ContentFilterBar?.Invoke() : null;
            var widths = new CommandWidths(w[0], w[1], pageBar ? 0f : w[2], w[3], w[4]);
            float pane = HeldPane(MathF.Max(0f, available - ToolbarPaneInset));
            float fitPane = chips is null ? pane : MathF.Max(0f, pane - CommandBarLayout.InlineChipsMin - CommandBarLayout.InlineChipsTrail);
            // A structure's worded Find and inline Select take their measured room before the ladder runs (the fit itself reserves the 32-DIP glyph).
            bool findWord = !explicitSearch && Kit.FindCommand("", false, _openSearch, _captureSearchButton) is not null;
            Element? select = Cfg.Selection != ItemsSelectionMode.None
                ? Kit.SelectCommand(Loc.Get(Strings.Detail.Select), false, () => SetMultiSelect(!_multi.Peek()))
                : null;
            _inlineSelect = select is not null;
            if (findWord) fitPane = MathF.Max(0f, fitPane - MathF.Max(0f, _findWordW - CommandBarLayout.SearchIconWidth));
            if (select is not null) fitPane = MathF.Max(0f, fitPane - CommandBarLayout.Gap - _selectW);
            var fit = CommandBarLayout.Resolve(fitPane, in widths, vertical, hasTune, insights is not null, explicitSearch, _toolbarFit);
            if (pageBar) fit = fit with { Inline = fit.Inline | InlineCommand.Shuffle };
            if (!explicitSearch) _searchOpenFit = null;
            else if (_searchOpenFit is { } latched && MathF.Abs(latched.Available - pane) <= 0.5f) fit = latched.Fit;
            else _searchOpenFit = (pane, fit);
            _toolbarFit = fit;

            var left = new List<Element>(3);
            if (pageBar)
                left.Add(MeasuredCommand(0, "cmd:page-lead:" + _contextText,
                    Kit.PageLead(_latest.Source.Context, _accent, _playAll, Loc.Get(Strings.Detail.Shuffle), _shuffle)));
            else if (!vertical)
            {
                IReadOnlyList<MenuFlyoutItem> playItems =
                [
                    new(Loc.Get(Strings.Detail.AddToQueue), ActionIcons.Resolve(ActionIcons.Queue), true,
                        () => RunOnContext(ActionId.AddToQueue)),
                ];
                left.Add(MeasuredCommand(0, "cmd:play-next:" + _contextText,
                    Kit.PlayNext(Loc.Get(Strings.Detail.PlayNext), () => RunOnContext(ActionId.PlayNext), playItems)));
            }
            if (!pageBar && fit.Has(InlineCommand.Shuffle))
                left.Add(MeasuredCommand(2, "cmd:shuffle", Kit.Labeled(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), false, Shuffle, null)));
            if (hasTune)
                left.Add(MeasuredCommand(1, "cmd:tune", ToolTip.Wrap(
                    Kit.Labeled(Icons.RefineSparkle, Loc.Get(Strings.Detail.Tuning.Tune), false, () => _latest.Profile.Tune?.Invoke(), null),
                    Loc.Get(Strings.Detail.Tuning.Tooltip))));

            var view = new List<Element>(2);
            if (fit.Has(InlineCommand.Filter))
                view.Add(FilterCommandSlot(fit.Has(InlineCommand.FilterLabel)));
            if (fit.Has(InlineCommand.Sort))
                view.Add(MeasuredCommand(3, "cmd:sort", Embed.Comp(() => new TableSortButton(this))));

            var overflow = (InlineCommand.Shuffle | InlineCommand.Filter | InlineCommand.Sort) & ~fit.Inline;
            Element more = Embed.Comp(() => new TableMoreButton(this, overflow))
                with { Key = "cmd:more:" + (int)overflow + ":" + _contextText };
            var tailList = new List<Element>(3);
            if (insights is not null) tailList.Add(Detail.InsightsBarButton(insights));
            if (select is not null)
                tailList.Add(Kit.Command("cmd:select", select, w => MeasureExtra(ref _selectW, w)));
            tailList.Add(more);
            Element[] tail = tailList.ToArray();

            Element search = Embed.Comp(new SearchProps(fit.SearchExpanded, fit.SearchWidth, false, Funnel: false), () => new TableSearchHost(this))
                with { Key = "search-host" };
            if (findWord)
                search = new BoxEl
                {
                    Key = "search-host:worded", Direction = 1, Shrink = 0f,
                    OnBoundsChanged = r => { if (!_searchExpanded.Peek()) MeasureExtra(ref _findWordW, r.W); },
                    Children = [search],
                };
            return Kit.Surface("normal", Kit.Bar(left.ToArray(), view.ToArray(), search, tail, chips));
        }

        Element MeasuredCommand(int slot, string key, Element command) => Kit.Command(key, command, w => MeasureToolbarCommand(slot, w));

        /// <summary>The pane the bar's fit last resolved against, and the hold that pins it while the filter card is open: a resize that
        /// would evict the Filter button (the card's anchor) re-fits only after the card closes, so the card is never closed by its own
        /// bar. <see cref="Overlay"/>'s PinsAnchor is the auto-hide scope's contract, not this one.</summary>
        float _barPane;
        float? _filterHoldPane;

        float HeldPane(float pane)
        {
            _barPane = pane;
            return _filterHoldPane ?? pane;
        }

        /// <summary>The filter card opened (holds the bar's rung) or closed (releases it and re-fits to the pane as it is now).</summary>
        void HoldBarForFilterCard(bool open)
        {
            if (open) { _filterHoldPane = _barPane > 0f ? _barPane : null; return; }
            if (_filterHoldPane is null) return;
            _filterHoldPane = null;
            _toolbarEpoch.Value = _toolbarEpoch.Peek() + 1;
        }

        /// <summary>Open or close the filter card under <paramref name="anchor"/>, holding the bar's rung while it is open.</summary>
        void ToggleFilterCard(IOverlayService overlay, Ref<NodeHandle> anchor, Ref<OverlayHandle?> handle)
        {
            // Where the structure draws menus as a sheet (Classic Compact, board C-Flyouts-Compact) the card opens in that sheet, not anchored.
            if (!Controls.IsNullOverlay(overlay) && handle.Value is not { IsOpen: true } && ContextSheets.SheetNow())
            {
                var sheet = ContextSheets.OpenContent(overlay, () => Embed.Comp(() => new TableFilterFlyout(this)),
                    () => { handle.Value = null; HoldBarForFilterCard(false); });
                handle.Value = sheet;
                HoldBarForFilterCard(true);
                return;
            }
            ToggleOverlay(overlay, anchor, handle, () => Embed.Comp(() => new TableFilterFlyout(this)), FilterPopup, () => HoldBarForFilterCard(false));
            if (handle.Value is { IsOpen: true }) HoldBarForFilterCard(true);
        }

        /// <summary>The inline Filter command of the bar. Its props carry the label, so a rung that drops the word re-renders the SAME
        /// button (an open card keeps its anchor and handle); only the labelled form reports its width to the fit.</summary>
        Element FilterCommandSlot(bool labelled)
            => MeasuredCommand(labelled ? 4 : -1, "cmd:filter",
                Embed.Comp(new FilterCommandProps(labelled), () => new TableFilterButton(this, textMode: false, command: true)));

        void MeasureExtra(ref float slot, float width)
        {
            if (width <= 1f || MathF.Abs(slot - width) <= 0.5f) return;
            slot = width;
            _toolbarEpoch.Value = _toolbarEpoch.Peek() + 1;
        }

        void MeasureToolbarCommand(int slot, float width)
        {
            if ((uint)slot >= (uint)_toolbarWidths.Length || width <= 1f) return;
            if (MathF.Abs(_toolbarWidths[slot] - width) <= 0.5f) return;
            _toolbarWidths[slot] = width;
            _toolbarEpoch.Value = _toolbarEpoch.Peek() + 1;
        }

        /// <summary>The sort's one word: the list's own order reads "Album order" on an album and "Custom order" elsewhere.</summary>
        string SortLabelFor(SortColumn c) => c switch
        {
            SortColumn.Index => Loc.Get(Cfg.Kind == DetailKind.Album ? Strings.Detail.Sort.AlbumOrder : Strings.Detail.Sort.CustomOrder),
            SortColumn.Title => Loc.Get(Strings.Detail.Sort.Title),
            SortColumn.Artist => Loc.Get(Strings.Detail.Sort.Artist),
            SortColumn.Album => Loc.Get(Strings.Detail.Sort.Album),
            SortColumn.DateAdded => Loc.Get(Strings.Detail.Sort.DateAdded),
            SortColumn.Duration => Loc.Get(Strings.Detail.Sort.Duration),
            SortColumn.Plays => Loc.Get(Strings.Detail.Column.Plays),
            _ => "",
        };

        /// <summary>The sort FIELDS the surface can honour as a radio group, then the direction pair. Plays is offered only
        /// while its lane is visible — sorting by a column the reader cannot see reorders for no stated reason.</summary>
        List<MenuFlyoutItem> SortItems()
        {
            var cur = _sort.Peek();
            var cfg = Cfg;
            var items = new List<MenuFlyoutItem>(10);
            void Field(SortColumn col) => items.Add(MenuFlyoutItem.RadioItem(SortLabelFor(col), cur.Column == col,
                () => SetSort(col == SortColumn.Index ? SortSpec.Default : new SortSpec(col, _sort.Peek().Descending))));
            Field(SortColumn.Index);
            Field(SortColumn.Title);
            Field(SortColumn.Artist);
            if (cfg.ShowAlbumColumn) Field(SortColumn.Album);
            if (_latest.Source.HasDateAdded) Field(SortColumn.DateAdded);
            if (TableColumnsPref.PlaysVisible(cfg.ShowPlays, cfg.PlaysColumnOptIn, TableColumnsPref.PlaysNow())) Field(SortColumn.Plays);
            Field(SortColumn.Duration);
            // Direction applies to custom order too: descending is the explicit "invert this list".
            items.Add(MenuFlyoutItem.Separator);
            items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Detail.Sort.Ascending), !cur.Descending,
                () => SetSort(_sort.Peek() with { Descending = false })));
            items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Detail.Sort.Descending), cur.Descending,
                () => SetSort(_sort.Peek() with { Descending = true })));
            return items;
        }

        // ══ 3. SEARCH ═══════════════════════════════════════════════════════════════════════════════════════════════

        sealed record SearchProps(bool Expanded, float Width, bool Compact, bool Funnel = true);

        /// <summary>The context band's search field. Its width is DERIVED: the band is <paramref name="availW"/> wide with
        /// the gutter either side, and the right cluster is its words plus one cluster gap — the identity block is
        /// unmounted while search is open, so all of that room is the field's.
        /// <para><paramref name="insights"/> = this arm hosts the facts sheet, so the cluster is FOUR words, not three
        /// (<see cref="Detail.InsightsSheet.BandActionsWidth"/>). Threaded from the frame's own answer, never
        /// re-derived — the field must reserve for exactly the cluster <see cref="BandActions"/> builds.</para></summary>
        Element CompactSearch(float availW, float left, bool insights)
        {
            float claim = Detail.BandActionsClaim(Loc.Get(Strings.Detail.Filter.Find), Loc.Get(Strings.Detail.Filter.Short),
                                                  Loc.Get(Strings.Detail.Play), insights);
            float room = availW - left * 2f - claim - Detail.BandLayout.ClusterGap;
            float width = MathF.Max(CommandBarLayout.SearchWithFilterWidth, MathF.Min(room, CommandBarLayout.SearchMax));
            return Embed.Comp(new SearchProps(false, width, true), () => new TableSearchHost(this)) with { Key = "compact-search-host" };
        }

        /// <summary>The band's RIGHT cluster: Find · Filter · [Insights ·] Play as plateless words — the same handlers the
        /// rest-state search glyph, the funnel, the sheet's hero toggle and the play FAB carry. Find keeps its node
        /// capture so a collapse restores focus.
        /// <para><paramref name="insights"/> non-null ⇒ this arm hosts the facts SHEET and the pinned band is where its
        /// toggle stays reachable after the hero has collapsed (Detail.Insights.cs §7 carries the whole argument, and
        /// why the hero keeps its own). It sits between the view verbs and the terminal primary.</para></summary>
        Element BandActions(Detail.InsightsToggle? insights)
        {
            Element[] kids = insights is null
                ?
                [
                    Controls.TextAction(Loc.Get(Strings.Detail.Filter.Find), _toggleFind) with { Key = "band:find", OnRealized = _captureSearchButton },
                    Embed.Comp(() => new TableFilterButton(this, textMode: true)) with { Key = "band:filter" },
                    Controls.TextAction(Loc.Get(Strings.Detail.Play), _playAll, primary: true) with { Key = "band:play" },
                ]
                :
                [
                    Controls.TextAction(Loc.Get(Strings.Detail.Filter.Find), _toggleFind) with { Key = "band:find", OnRealized = _captureSearchButton },
                    Embed.Comp(() => new TableFilterButton(this, textMode: true)) with { Key = "band:filter" },
                    Detail.InsightsBandAction(insights),
                    Controls.TextAction(Loc.Get(Strings.Detail.Play), _playAll, primary: true) with { Key = "band:play" },
                ];
            return new BoxEl
            {
                Direction = 0, Gap = Detail.BandLayout.ActionGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children = kids,
            };
        }

        void CaptureSearchButton(NodeHandle node)
        {
            _searchButtonNode = node;
            if (!_restoreSearchFocus) return;
            _restoreSearchFocus = false;
            var hooks = _hooks;
            _post?.Invoke(() => hooks?.FocusNode?.Invoke(node, true));
        }

        void CollapseSearch(bool restoreFocus)
        {
            _restoreSearchFocus |= restoreFocus;
            _searchFocused.Value = false;
            _searchExpanded.Value = false;
            // The button that opened the field is already mounted when the field closed from the band (Find stays up):
            // hand focus back now rather than waiting for a capture that will not re-run.
            if (restoreFocus && !_searchButtonNode.IsNull && VerticalArm) CaptureSearchButton(_searchButtonNode);
        }

        /// <summary>The search host: two keyed LAYERS (icon, field) cross-fading inside a box whose WIDTH reflows; the
        /// chrome (corners, border width) is mounted at all times and only its colours fade, because neither radius nor
        /// border width is an animatable channel.</summary>
        sealed class TableSearchHost(TableHost host) : Component
        {
            public override Element Render()
            {
                var h = host;
                var p = UseProps<SearchProps>();
                bool expanded = p.Compact ? h._searchExpanded.Value : p.Expanded;
                float edge = h.Kit.CommandEdge;   // the structure's command edge at this tier (Classic Compact: 40)
                float width = p.Compact && !expanded ? CommandBarLayout.SearchWithFilterWidth : p.Width;
                if (!expanded) width = MathF.Max(width, edge);
                bool queryActive = h._query.Value.Length > 0;
                bool focused = expanded && h._searchFocused.Value;
                // A structure's worded Find (ITrackCommandKit.FindCommand) sizes to its word at rest; the open field keeps the fit's width.
                Element? word = !expanded && !p.Compact
                    ? h.Kit.FindCommand(Loc.Get(Strings.Detail.Filter.Find), queryActive, h._openSearch, h._captureSearchButton)
                    : null;
                if (word is not null) width = float.NaN;
                Element query = expanded
                    ? new BoxEl
                    {
                        Key = "search:field", Direction = 1, Height = edge, Animate = s_searchSwap,
                        Children = [Embed.Comp(() => new TableSearchField(h, band: p.Compact))],
                    }
                    : word is not null
                    ? new BoxEl
                    {
                        Key = "search:word", Direction = 1, Height = edge, JustifySelf = FlexAlign.Start, Animate = s_searchSwap,
                        Children = [ToolTip.Wrap(word, Loc.Get(Strings.Detail.Filter.SearchThisList))],
                    }
                    : new BoxEl
                    {
                        Key = "search:icon", Direction = 1, Width = edge, Height = edge, JustifySelf = FlexAlign.Start, Animate = s_searchSwap,
                        Children = [ToolTip.Wrap(h.Kit.IconButton(Icons.Search, queryActive, h._openSearch, h._captureSearchButton),
                                                 Loc.Get(Strings.Detail.Filter.SearchThisList))],
                    };
                // No explicit row width: it fills the host whose width is the tween, so the funnel rides the right edge
                // for the whole flight instead of hanging past a narrower clip. The query region SHRINKS (G-254): a ZStack
                // measures its widest layer and the engine's flex items do not shrink by default (MinWidth 0 is no stand-in),
                // so without it a long query, or the 66-DIP start of the expand tween, pushes the fixed 32-DIP funnel out.
                var row = new BoxEl
                {
                    Direction = 0, Gap = expanded ? 0f : CommandBarLayout.Gap, Height = edge, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Key = "search-query-region", ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = edge, ClipToBounds = true, Children = [query] },
                    ],
                };
                // STABLE key: keying on capabilities would remount the funnel and orphan an open flyout. The command bar's host has no
                // funnel: its Filter is a command of its own.
                if (p.Funnel)
                    row = row with { Children = [.. row.Children, Embed.Comp(() => new TableFilterButton(h, textMode: false)) with { Key = "search-filter" }] };
                Element[] layers = focused
                    ?
                    [
                        row,
                        new BoxEl
                        {
                            Key = "search-underline", Height = 2f, AlignSelf = FlexAlign.End, Fill = Prop.Of(static () => Tok.AccentDefault),
                            HitTestVisible = false, Animate = s_searchSwap,
                        },
                    ]
                    : [row];
                return new BoxEl
                {
                    ZStack = true, Width = width, Height = edge, Shrink = 0f, Corners = Radii.ControlAll,
                    Fill = expanded ? (focused ? Tok.FillControlInputActive : Tok.FillControlDefault) : ColorF.Transparent,
                    BorderWidth = 1f,
                    BorderColor = expanded ? Tok.StrokeControlDefault : ColorF.Transparent,
                    BrushTransitionMs = SearchExpandMs,
                    Animate = s_searchDisclosure,
                    ClipToBounds = true,
                    Children = layers,
                };
            }
        }

        /// <summary>The chromeless editor on the host's query signal. It collapses itself on the query-EMPTY edge (✕ or
        /// backspacing out) and on Esc — both handing focus back — and on a blur while empty, without restoring focus.
        /// <para><paramref name="band"/>: this is the vertical arm's sticky-band copy. The hero bar and the band both mount a
        /// field on the same expand, so only the one that owns input (<c>_compactInteractive</c>) takes focus on realize:
        /// two focus requests made the loser blur while empty, and that blur collapsed the find the moment it opened.</para></summary>
        sealed class TableSearchField(TableHost host, bool band = false) : Component
        {
            public override Element Render()
            {
                var h = host;
                var hooks = UseContext(InputHooks.Current);
                var post = UsePost();
                bool hasQuery = h._query.Value.Length > 0;
                var hadQuery = UseRef(hasQuery);
                UseEffect(() =>
                {
                    bool had = hadQuery.Value;
                    hadQuery.Value = hasQuery;
                    if (had && !hasQuery) post(() => h.CollapseSearch(restoreFocus: true));
                }, DepKey.From(hasQuery));
                var parts = UseMemo(() => new TemplateParts
                {
                    [EditableText.PartRoot] = b => b with
                    {
                        OnRealized = n =>
                        {
                            if (!h.VerticalArm || band == h._compactInteractive.Peek()) post(() => hooks.FocusNode?.Invoke(n, false));
                        },
                    },
                }, DepKey.Empty);
                // The field mounts once (its props freeze): the clear affix is a live component of its own, not a field.
                return Embed.Comp(() => new EditableText
                {
                    Text = h._query,
                    Width = float.NaN,
                    Height = 32f,
                    Chromeless = true,
                    Placeholder = Loc.Get(Strings.Detail.Filter.SearchThisList),
                    LeftAffix = new BoxEl
                    {
                        Width = 28f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        HitTestVisible = false, Children = [Icon(Icons.Search, 13f, Tok.TextTertiary)],
                    },
                    RightAffix = Embed.Comp(() => new TableSearchClear(h)),
                    OnCancel = () => h.CollapseSearch(restoreFocus: true),
                    OnFocusChanged = isFocused =>
                    {
                        h._searchFocused.Value = isFocused;
                        if (!isFocused && h._query.Peek().Length == 0) post(() => h.CollapseSearch(restoreFocus: false));
                    },
                    Parts = parts,
                });
            }
        }

        sealed class TableSearchClear(TableHost host) : Component
        {
            public override Element Render()
            {
                var h = host;
                bool hasQuery = h._query.Value.Length > 0;
                return new BoxEl
                {
                    Direction = 0, Gap = 1f, Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 0f, 3f, 0f),
                    Children = hasQuery
                        ?
                        [
                            ToolTip.Wrap(new BoxEl
                            {
                                Width = 26f, Height = 26f, AlignSelf = FlexAlign.Center, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Corners = Radii.ControlAll, Focusable = true, Role = AutomationRole.Button,
                                OnClick = () => h._query.Value = "",
                                Children = [Icon(Icons.ClearText, 12f, Tok.TextSecondary)],
                            }.Interactive(Interaction.Subtle), Loc.Get(Strings.Detail.Filter.Clear)),
                        ]
                        : [],
                };
            }
        }

        // ══ 4. THE COLUMN HEADER ════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The header grid over the SAME TrackSize[] the rows use, keyed exactly like the row cells so a lane that
        /// leaves at a breakpoint is removed rather than reconciled against its neighbour. Shifts +28 while the check lane
        /// is in (333 ms), with a 1-DIP divider as the band's single hairline.</summary>
        sealed class TableHeader(TableHost host) : Component
        {
            public override Element Render()
            {
                var h = host;
                var shape = h.ShapeValue;
                var sort = h._sort.Value;
                var template = h._listStyle?.Chrome.Header;
                if (template is null) return new BoxEl();
                return template(new TrackHeaderArgs
                {
                    Set = shape.Set, Tracks = shape.Tracks, Sort = sort, SortSignal = h._sort,
                    Checks = h._checksVisible?.Value ?? false, Vertical = h.HeroFormLive,
                    OnSort = h.SortBy,
                });
            }
        }

        /// <summary>A header was clicked: the sort cycle (<see cref="TableRules.NextSort"/>) is the core's.</summary>
        void SortBy(SortColumn col, bool artistColumn) => SetSort(TableRules.NextSort(_sort.Peek(), col, artistColumn));

        // ══ 5. THE FLYOUT BUTTONS ═══════════════════════════════════════════════════════════════════════════════════

        /// <summary>One anchored overlay at a time per button: a second click closes it.</summary>
        internal static void ToggleOverlay(IOverlayService overlay, Ref<NodeHandle> anchor, Ref<OverlayHandle?> handle,
                                  Func<Element> content, PopupOptions options, Action? closed = null)
        {
            if (Controls.IsNullOverlay(overlay)) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var opened = overlay.Open(() => anchor.Value, content, FlyoutPlacement.BottomEdgeAlignedRight, options);
            handle.Value = opened;
            opened.ClosedAction = () => { handle.Value = null; closed?.Invoke(); };
        }

        sealed class TableSortButton(TableHost host) : Component
        {
            public override Element Render()
            {
                var h = host;
                var overlay = UseContext(Overlay.Service);
                var anchor = UseRef<NodeHandle>(default);
                var handle = UseRef<OverlayHandle?>(null);
                var current = h._sort.Value;
                bool active = TableRules.SortDiffersFromDefault(current, h.P.DefaultSort);
                void Toggle() => ToggleOverlay(overlay, anchor, handle,
                    () => MenuFlyout.Create(h.SortItems(), () => handle.Value?.Close(), size: OverlayFit.MenuSize), MenuPopup);
                return h.Kit.SortCommand(h.SortLabelFor(current.Column), active, Toggle, n => anchor.Value = n);
            }
        }

        /// <summary>"…": the evicted commands first (Shuffle · Sort ▸ · Filter…), then what the bar never carries (tuned Classic D3):
        /// Row size ▸, the two column opt-ins and the Select toggle; then a separator and the playlist deposit ("Copy to playlist" on a
        /// playlist/Liked, "Add to playlist" on an album). Never accent-lit (W21).</summary>
        sealed class TableMoreButton(TableHost host, InlineCommand overflow) : Component
        {
            public override Element Render()
            {
                var h = host;
                var overlay = UseContext(Overlay.Service);
                var anchor = UseRef<NodeHandle>(default);
                var handle = UseRef<OverlayHandle?>(null);
                // The filter card has its own handle: the menu's invoke runs BEFORE the menu closes, and the close must not take the card with it.
                var filterHandle = UseRef<OverlayHandle?>(null);
                var post = UsePost();
                // The count rides "…" only while the Filter button is not on the bar (the button wears it otherwise).
                bool filterInMore = (overflow & InlineCommand.Filter) != 0;
                int activeFilters = filterInMore ? h._filters.Value.ActiveCountFor(h._query.Value.Length > 0) : 0;
                // Next tick, so the menu is already gone and the card anchors to "…" alone.
                void OpenFilter() => post(() => h.ToggleFilterCard(overlay, anchor, filterHandle));

                List<MenuFlyoutItem> Items()
                {
                    var cfg = h.Cfg;
                    var items = new List<MenuFlyoutItem>(10);
                    if ((overflow & InlineCommand.Shuffle) != 0)
                        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Shuffle), Icons.Shuffle, true, h.Shuffle));
                    if ((overflow & InlineCommand.Sort) != 0)
                        items.Add(MenuFlyoutItem.SubMenu(Loc.Get("detail.sort.menu"), h.SortItems(), Icons.Sort));   // key from batch-loc WP-4.5-U2a.json; a literal so the build does not wait on the merge
                    if (filterInMore)
                        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Filter.MenuItem), Icons.Filter, true, OpenFilter));
                    items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Detail.Density.RowSize), h.Kit.DensityItems(), Icons.List));
                    if (cfg.ShowTempo)
                    {
                        bool tempoOn = TableColumnsPref.TempoNow();
                        items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.TempoColumn), tempoOn,
                            () => TableColumnsPref.SetTempo(!tempoOn)));
                    }
                    if (cfg.PlaysColumnOptIn)
                    {
                        bool playsOn = TableColumnsPref.PlaysNow();
                        items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.PlaysColumn), playsOn,
                            () => TableColumnsPref.SetPlays(!playsOn)));
                    }
                    if (cfg.Selection != ItemsSelectionMode.None && !h._inlineSelect)
                    {
                        bool selecting = h._multi.Peek();
                        items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.Select), selecting, () => h.SetMultiSelect(!selecting)));
                    }
                    items.Add(MenuFlyoutItem.Separator);

                    var src = h._latest.Source;
                    var tracks = new Track[src.Count];
                    for (int i = 0; i < tracks.Length; i++) tracks[i] = src.At(i);
                    var ctx = new ActionContext(ActionTarget.ForTracks(tracks), Actions.Services);
                    bool copy = cfg.Kind is DetailKind.Playlist or DetailKind.Liked;
                    items.Add(AddToPlaylistItem(in ctx, overlay) with
                    {
                        Label = Loc.Get(copy ? Strings.Detail.CopyToPlaylist : Strings.Detail.AddToPlaylist),
                    });
                    return items;
                }

                void Toggle() => ToggleOverlay(overlay, anchor, handle, () => MenuFlyout.Create(Items(), () => handle.Value?.Close(), size: OverlayFit.MenuSize), MenuPopup);
                Element more = h.Kit.IconButton(Icons.More, false, Toggle, n => anchor.Value = n);
                return ToolTip.Wrap(h.Kit.MoreSlot(more, activeFilters), Loc.Get(Strings.Common.More));
            }
        }

        // ══ 6. THE FILTER ═══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Which facets this list can EARN (ch 04 §10 #67): a facet that would match nothing reads as a broken
        /// filter. Cached per (membership version, track publication) — the scan is not per render.</summary>
        internal readonly record struct FilterCaps(bool HasVideo, bool HasDateAdded, bool HasMixedOrigin, bool HasUnavailable,
                                                   bool HasLibrary, bool HasTempo);

        FilterCaps _caps;
        uint _capsVersion, _capsPublication;
        bool _capsSeeded;

        /// <summary>What the list shows against what it has, for the filter card's header: the live (filtered, searched) view's length and
        /// the membership total, whether a find or filter narrows it, and whether it lists episodes. Reading it subscribes the caller to the snapshot.</summary>
        (int Shown, int Total, bool Filtered, bool Episodes) VisibleCounts()
        {
            var snap = _snapshot!.Value;
            var src = _latest.Source;
            return (ViewOf(in snap).Length, Math.Max(src.Total, src.Count), snap.Query.Length > 0 || snap.Filters.ActiveCountFor(snap.Query.Length > 0) > 0,
                    Cfg.Content == DetailContent.Episodes);
        }

        FilterCaps CapsNow()
        {
            var src = _latest.Source;
            src.Subscribe();
            uint version = src.Version, publication = Entities.Current.Tracks.Changed.Value;
            if (_capsSeeded && version == _capsVersion && publication == _capsPublication) return _caps;
            bool local = false, streamed = false, unavailable = false, tempo = false;
            for (int i = 0, n = src.Count; i < n; i++)
            {
                var t = src.At(i);
                if (t.IsLocal) local = true; else streamed = true;
                // Deliberately broader than the filter it gates (no verdict counts): over-offering is inert.
                if (!t.IsPlayable || !t.Knows(TrackFields.Availability)) unavailable = true;
                if (t.Tempo > 0) tempo = true;
                if (local && streamed && unavailable && tempo) break;
            }
            _caps = new FilterCaps(src.HasVideo, src.HasDateAdded, local && streamed, unavailable, User.Me.IsValid, tempo);
            _capsSeeded = true; _capsVersion = version; _capsPublication = publication;
            return _caps;
        }

        // ══ 6a. THE UNAVAILABLE-SONGS BAR (boards C-Unavailable / C-Unavailable-Compact, contract 1.22) ════════════════════════════════

        /// <summary>Does this table carry the bar's slot? A detail page's track list (the hero arm, or the two-column arm with its list bar) under a
        /// structure that draws one; never a library pane's embedded table nor an episode list.</summary>
        bool HasUnavailableBar => _listStyle?.Chrome.UnavailableBar is not null && !_latest.Embedded && Cfg.Content == DetailContent.Tracks
                                  && (VerticalArm || _latest.ShowToolbar);

        /// <summary>The band the bar takes in the hero arm's pinned chrome while it is on (0 while off): the rows' clip inset adds it. Reading it
        /// subscribes the caller to the bar's on / off edge and to the structure's tier read, never to the tally.</summary>
        float UnavailableBarExtent()
            => HasUnavailableBar && _unavailableShown.Value ? MathF.Max(0f, _listStyle!.Chrome.UnavailableBarExtent?.Invoke(TiersNow()) ?? 0f) : 0f;

        /// <summary>The rows <see cref="FilterFlags.PlayableOnly"/> would hide, by why (<see cref="FilterModel.HiddenByPlayable"/>: the filter's own
        /// rule), over the whole membership. Cached per (membership version, track publication) like <see cref="CapsNow"/>; reading it subscribes
        /// the caller to both.</summary>
        UnavailableTally UnavailableNow()
        {
            var src = _latest.Source;
            src.Subscribe();
            uint version = src.Version, publication = Entities.Current.Tracks.Changed.Value;
            if (_tallySeeded && version == _tallyVersion && publication == _tallyPublication) return _tally;
            long now = Store.ToUnix(Entities.Now);
            var tally = default(UnavailableTally);
            for (int i = 0, n = src.Count; i < n; i++)
            {
                var t = src.At(i);
                if (!t.IsValid) continue;
                bool unplayable = t.Unplayable(), notYetOut = t.NotYetOut(now);
                if (FilterModel.HiddenByPlayable(notYetOut, unplayable)) tally = tally.With(notYetOut, unplayable);
            }
            _tally = tally;
            _tallySeeded = true; _tallyVersion = version; _tallyPublication = publication;
            return _tally;
        }

        /// <summary>The bar's sentence with its reasons: "2 songs here can’t be played: one isn’t available in your country, one isn’t out yet." (board
        /// C-Unavailable), or one shared reason ("…: they aren’t out yet.").</summary>
        static string UnavailableExplained(in UnavailableTally tally)
        {
            string reasons = tally.NotAvailable > 0 && tally.NotYetOut > 0
                ? Strings.Detail.Filter.Unavailable.Region(count: tally.NotAvailable) + ", " + Strings.Detail.Filter.Unavailable.Upcoming(count: tally.NotYetOut)
                : tally.NotAvailable > 0
                    ? Strings.Detail.Filter.Unavailable.AllRegion(count: tally.NotAvailable)
                    : Strings.Detail.Filter.Unavailable.AllUpcoming(count: tally.NotYetOut);
            return Strings.Detail.Filter.Unavailable.Explained(count: tally.Total, reasons: reasons);
        }

        /// <summary>The bar's slot: the core decides whether it is on (<see cref="FilterModel.OffersHide"/>: rows the filter would hide, the filter
        /// off) and words it; the structure's template draws it and eases its band. Its own component, so a track publication re-renders the
        /// bar only (the tally is read here, never in the host), and the host hears the on / off edge alone (<c>_unavailableShown</c>).</summary>
        sealed class TableUnavailableBar(TableHost host) : Component
        {
            public override Element Render()
            {
                var h = host;
                var tally = h.UnavailableNow();
                var filters = h._filters.Value;
                var template = h._listStyle?.Chrome.UnavailableBar;
                bool shown = template is not null && FilterModel.OffersHide(in tally, in filters);
                UseLayoutEffect(() => h._unavailableShown.Value = shown, DepKey.From(shown));
                if (template is null) return new BoxEl();
                bool vertical = h.HeroFormLive;
                float insetX = vertical ? RowMetrics.PadXFor(h.ShapeValue.Set.Tier) : RowMetrics.RowInset;
                int count = Math.Max(1, tally.Total);
                return template(new TrackUnavailableBarArgs
                {
                    Shown = shown,
                    Summary = Strings.Detail.Filter.Unavailable.Summary(count: count),
                    Explained = tally.Total > 0 ? UnavailableExplained(in tally) : Strings.Detail.Filter.Unavailable.Summary(count: count),
                    ActionLabel = Strings.Detail.Filter.Unavailable.Hide(count: count),
                    Hide = h._hideUnavailable,
                    InsetX = insetX,
                    Tiers = h.TiersNow(),
                });
            }
        }

        static PopupOptions FilterPopup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
        { ConstrainToRootBounds = true };

        /// <summary>The Filter command's props: <paramref name="Labelled"/> = the button wears its word (else the funnel alone).</summary>
        sealed record FilterCommandProps(bool Labelled);

        /// <summary>The Filter affordance: on the command bar (<paramref name="command"/>) the labelled-or-icon Filter command (the kit's <c>FilterCommand</c>);
        /// otherwise the funnel (32×32, accent plate + corner count badge while any facet is on), or — in the context band —
        /// the plateless word whose accent ink stands in for plate and badge. Same flyout every way.</summary>
        sealed class TableFilterButton(TableHost host, bool textMode, bool command = false) : Component
        {
            public override Element Render()
            {
                var h = host;
                var overlay = UseContext(Overlay.Service);
                var anchor = UseRef<NodeHandle>(default);
                var handle = UseRef<OverlayHandle?>(null);
                var props = UsePropsOrDefault<FilterCommandProps>();
                int activeCount = h._filters.Value.ActiveCountFor(h._query.Value.Length > 0);
                bool active = activeCount > 0;
                void Toggle() => h.ToggleFilterCard(overlay, anchor, handle);

                if (command)
                {
                    bool labelled = props?.Labelled ?? true;
                    Element button = h.Kit.FilterCommand(activeCount, labelled, Loc.Get(Strings.Detail.Filter.Short), Toggle, n => anchor.Value = n);
                    return ToolTip.Wrap(button, Loc.Get(Strings.Detail.Filter.Title));
                }

                if (textMode)
                    return ToolTip.Wrap(Controls.TextAction(Loc.Get(Strings.Detail.Filter.Short), Toggle, toggledOn: active)
                        with { OnRealized = n => anchor.Value = n }, Loc.Get(Strings.Detail.Filter.Title));

                return ToolTip.Wrap(h.Kit.FilterFunnel(activeCount, Toggle, n => anchor.Value = n), Loc.Get(Strings.Detail.Filter.Title));
            }
        }

        /// <summary>The immediate-apply filter card ("Flyout B"): the core decides WHICH facets and switches this list can earn, their
        /// values and what each commit writes; the structure's <c>FilterCard</c> template draws the card (a header with what the list
        /// shows against what it has and "Reset", one row per facet with a combo, the status switches).</summary>
        sealed class TableFilterFlyout : Component
        {
            readonly TableHost _h;
            readonly Signal<int> _scope, _explicit, _video, _duration, _added, _tempo, _origin;
            readonly Signal<bool> _liked, _playable;

            public TableFilterFlyout(TableHost host)
            {
                _h = host;
                var f = host._filters.Peek();
                _scope = new((int)f.SearchScope);
                _explicit = new((int)f.ExplicitMode);
                _video = new((int)f.VideoMode);
                _liked = new(f.LikedOnly);
                _playable = new(f.PlayableOnly);
                _duration = new((int)f.Duration);
                _added = new(AddedIndex(f));
                _tempo = new((int)f.Tempo);
                _origin = new((int)f.Origin);
            }

            /// <summary>The Date added combo's index: the preset, or -1 (the "Custom range" placeholder) while the rail's explicit window is set.</summary>
            static int AddedIndex(FilterState f) => f.AddedAfterMs != 0L || f.AddedBeforeMs != 0L ? -1 : (int)f.Added;

            /// <summary>Write every local signal from <paramref name="f"/>. A signal write fires no onChange (ComboBox and ToggleSwitch call it
            /// only on a user commit), so syncing from an external filter write (a view load, the rail) cannot loop back into the filters.</summary>
            void Sync(FilterState f)
            {
                _scope.Value = (int)f.SearchScope; _explicit.Value = (int)f.ExplicitMode; _video.Value = (int)f.VideoMode;
                _liked.Value = f.LikedOnly; _playable.Value = f.PlayableOnly; _duration.Value = (int)f.Duration;
                _added.Value = AddedIndex(f); _tempo.Value = (int)f.Tempo; _origin.Value = (int)f.Origin;
            }

            /// <summary>What the card cannot show a row for, named in the header so the badge's number can be explained.</summary>
            static string? HiddenFacets(FilterState f)
            {
                var names = new List<string>(5);
                if (!string.IsNullOrEmpty(f.Tag)) names.Add(Loc.Get(Strings.Detail.Filter.FacetGenre));
                if (f.ArtistSlot != 0) names.Add(Loc.Get(Strings.Detail.Filter.FacetArtist));
                if (f.Camelot != 0) names.Add(Loc.Get(Strings.Detail.Filter.FacetKey));
                if (f.ReleaseYearMin != 0 || f.ReleaseYearMax != 0) names.Add(Loc.Get(Strings.Detail.Filter.FacetYear));
                if (f.AddedAfterMs != 0L || f.AddedBeforeMs != 0L) names.Add(Loc.Get(Strings.Detail.Filter.FacetDates));
                return names.Count == 0 ? null : string.Join(", ", names);
            }

            void Set(FilterState next) => _h.SetFilters(next);
            FilterState Current => _h._filters.Peek();

            public override Element Render()
            {
                var current = _h._filters.Value;
                var caps = _h.CapsNow();
                var (shown, total, filtered, episodes) = _h.VisibleCounts();
                // External writes (a view load, the rail) while the card is open: the local signals follow the filters, never the reverse.
                UseEffect(() => Sync(_h._filters.Value));
                string status = total <= 0 ? ""
                    : !filtered
                        ? (episodes ? Strings.Detail.Filter.AllEpisodes(total) : Strings.Detail.Filter.AllSongs(total))
                        : (episodes ? Strings.Detail.EpisodeCountOf(shown.ToString("N0", CultureInfo.CurrentCulture), total)
                                    : Strings.Detail.SongCountOf(shown.ToString("N0", CultureInfo.CurrentCulture), total));
                if (HiddenFacets(current) is { } hidden && total > 0) status = Strings.Detail.MetaLine(status, hidden);

                void ClearAll()
                {
                    _scope.Value = 0; _explicit.Value = 0; _video.Value = 0; _liked.Value = false; _playable.Value = false;
                    _duration.Value = 0; _added.Value = 0; _tempo.Value = 0; _origin.Value = 0;
                    Set(FilterState.Default);
                }

                // One facet: the signal, its words and what a commit writes; `On` (off its default) is read here so this component
                // subscribes to the signal exactly as the inline rows did.
                TrackFilterFacet Pick(string key, string label, Signal<int> signal, string[] options, Action<int> changed, string placeholder = "", bool? tinted = null)
                    => new(key, label, signal, options, changed, placeholder, tinted ?? signal.Value != 0);

                TrackFilterToggle Switch(string key, string label, Signal<bool> signal, FilterFlags flag)
                    => new(key, label, signal, on =>
                    {
                        var f = Current;
                        Set(f with { Flags = on ? f.Flags | flag : f.Flags & ~flag });
                    }, signal.Value);

                var rows = new List<TrackFilterFacet>(7);
                if (caps.HasDateAdded || current.Added != AddedRange.Any || current.AddedAfterMs != 0L || current.AddedBeforeMs != 0L)
                    rows.Add(Pick("filter:added", Loc.Get(Strings.Detail.Filter.DateAdded), _added,
                        [Loc.Get(Strings.Detail.Filter.AnyTime), Loc.Get(Strings.Detail.Filter.LastSevenDays), Loc.Get(Strings.Detail.Filter.LastThirtyDays),
                         Loc.Get(Strings.Detail.Filter.LastSixMonths), Loc.Get(Strings.Detail.Filter.LastYear)],
                        // WithAddedRange, never a bare `with`: a preset must retire the rail's explicit window.
                        v => Set(Current.WithAddedRange((AddedRange)v)),
                        // The rail's explicit window seeds the combo with -1: no preset names it, and "Any time" (index 0) then differs from the
                        // selection, so choosing it commits and clears the window (ComboBox.Commit ignores the same index).
                        placeholder: Loc.Get(Strings.Detail.Filter.CustomRange)));
                rows.Add(Pick("filter:length", Loc.Get(Strings.Detail.Filter.Length), _duration,
                    [Loc.Get(Strings.Detail.Filter.AnyDuration), Loc.Get(Strings.Detail.Filter.UnderThree),
                     Loc.Get(Strings.Detail.Filter.ThreeToFive), Loc.Get(Strings.Detail.Filter.OverFive)],
                    v => Set(Current with { Duration = (DurationRange)v })));
                if (caps.HasTempo || current.Tempo != TempoBand.Any)
                    rows.Add(Pick("filter:tempo", Loc.Get(Strings.Detail.Filter.Tempo), _tempo,
                        [Loc.Get(Strings.Detail.Filter.AnyTempo), Loc.Get(Strings.Detail.Filter.TempoSlow), Loc.Get(Strings.Detail.Filter.TempoMid),
                         Loc.Get(Strings.Detail.Filter.TempoFast), Loc.Get(Strings.Detail.Filter.TempoVeryFast)],
                        v => Set(Current with { Tempo = (TempoBand)v })));
                rows.Add(Pick("filter:explicit", Loc.Get(Strings.Detail.Filter.Explicit), _explicit,
                    [Loc.Get(Strings.Detail.Filter.Show), Loc.Get(Strings.Detail.Filter.Hide), Loc.Get(Strings.Detail.Filter.OnlyExplicit)],
                    v => Set(Current with { ExplicitMode = (TraitMode)v })));
                rows.Add(Pick("filter:video", Loc.Get(Strings.Detail.Filter.Video), _video,
                    [Loc.Get(Strings.Detail.Filter.Show), Loc.Get(Strings.Detail.Filter.Hide), Loc.Get(Strings.Detail.Filter.OnlyVideo)],
                    v => Set(Current with { VideoMode = (TraitMode)v })));
                if (caps.HasMixedOrigin || current.Origin != OriginFilter.Any)
                    rows.Add(Pick("filter:source", Loc.Get(Strings.Detail.Filter.Source), _origin,
                        [Loc.Get(Strings.Detail.Filter.AnySource), Loc.Get(Strings.Detail.Filter.Streamed), Loc.Get(Strings.Detail.Filter.Local)],
                        v => Set(Current with { Origin = (OriginFilter)v })));
                // Search in is the query's scope, not a list facet, but it stays a row so the capability is not lost.
                rows.Add(Pick("filter:scope", Loc.Get(Strings.Detail.Filter.SearchIn), _scope,
                    [Loc.Get(Strings.Detail.Filter.Everything), Loc.Get(Strings.Detail.Filter.TitleOnly),
                     Loc.Get(Strings.Detail.Filter.ArtistOnly), Loc.Get(Strings.Detail.Filter.AlbumOnly)],
                    v => Set(Current with { SearchScope = (SearchScope)v }),
                    tinted: _scope.Value != 0 && _h._query.Value.Length > 0));

                var toggles = new List<TrackFilterToggle>(2);
                if (caps.HasLibrary || current.LikedOnly)
                    toggles.Add(Switch("filter:liked", Loc.Get(Strings.Detail.Filter.OnlyLiked), _liked, FilterFlags.LikedOnly));
                if (caps.HasUnavailable || current.PlayableOnly)
                    toggles.Add(Switch("filter:playable", Loc.Get(Strings.Detail.Filter.HideUnplayable), _playable, FilterFlags.PlayableOnly));

                var template = _h._listStyle?.Chrome.FilterCard;
                if (template is null) return new BoxEl();
                return template(new TrackFilterCardArgs
                {
                    Title = Loc.Get(Strings.Detail.Filter.Short), Status = status,
                    ResetLabel = Loc.Get(Strings.Detail.Filter.Reset), CanReset = !current.IsDefault, Reset = ClearAll,
                    Facets = rows, Toggles = toggles,
                });
            }
        }


        // ══ 7. THE SELECTION BAR ════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The selection mode of the command lane. Count 0 + minCount 0: the bar's props never change, and its
        /// commands read the live selection themselves — so "1 selected" can never sit beside "Play 4 next".</summary>
        Element SelectionSurface(string mode)
            => Kit.Surface(mode, Controls.SelectionBar(0, _selectionCommands, minCount: 0));

        /// <summary>The table's host for the shared lane (<see cref="Track.SelectionLane"/>): the live selection picks the
        /// variant (an episode-capable source may select episode rows, track rows, or both), plus the table's exit and
        /// select-all. An all-track selection (every OTHER table, always) is the plain track lane.</summary>
        Element SelectionCommands(int fit)
        {
            _ = _selection.Version.Value;
            int count = _selectedCount?.Value ?? 0;
            if (count <= 0) { _selectionPrevCount = 0; return new BoxEl(); }
            bool wasVisible = _selectionPrevCount > 0;
            _selectionPrevCount = count;
            var args = LaneArgs(count, wasVisible);
            return Track.SelectionLane(fit, in args);
        }

        SelectionLaneArgs LaneArgs(int count, bool wasVisible)
        {
            var kind = SelectionLaneKind.Tracks;
            if (_latest.Source.HasEpisodes)
            {
                var (anyTrack, anyEpisode) = SelectedKindMix();
                if (anyEpisode && !anyTrack) kind = SelectionLaneKind.Episodes;
                else if (anyEpisode) kind = SelectionLaneKind.Mixed;
            }
            return new SelectionLaneArgs(kind, count, _laneTracks, _laneEpisodes, _laneHost, _exitSelection, _selectAllTracks, wasVisible);
        }

        /// <summary>Publish (or withdraw) the docked bar's model: an auto-tracked effect over the selection, so the verbs name it as it stands.</summary>
        void PublishSelectionDock()
        {
            var host = _dockHost;
            if (host is null) return;
            _ = _selection.Version.Value;
            int count = _selectedCount?.Value ?? 0;
            bool show = _selectionVisible?.Value == true && count > 0;
            if (!show)
            {
                WithdrawSelectionDock();
                return;
            }
            var props = Track.SelectionDock(LaneArgs(count, wasVisible: _dockPublished is not null), _rowItems?.Count.Value ?? 0);
            _dockPublished = props;
            host.Model.Value = props;
        }

        /// <summary>Take this table's model off the host (only if it is still the one showing: another page's table may have published since).</summary>
        void WithdrawSelectionDock()
        {
            if (_dockPublished is null) return;
            if (_dockHost is { } host && ReferenceEquals(host.Model.Peek(), _dockPublished)) host.Model.Value = null;
            _dockPublished = null;
        }

        /// <summary>The kit of a host mounted where no registry provides <c>StyleKeys.TrackTable</c>: every command draws an empty box.</summary>
        sealed class NoChromeKit : ITrackCommandKit
        {
            public static readonly NoChromeKit Instance = new();

            public Element Surface(string mode, Element content) => content;
            public float CommandEdge => 32f;
            public Element Command(string key, Element command, Action<float> onWidth) => new BoxEl { Key = key };
            public Element Bar(Element[] left, Element[] view, Element search, Element[] tail, Element? middle) => new BoxEl();
            public Element PageLead(EntityUri subject, Func<ColorF> accent, Action play, string shuffleLabel, Action shuffle) => new BoxEl();
            public Element PlayNext(string label, Action onPlay, IReadOnlyList<MenuFlyoutItem> items) => new BoxEl();
            public Element Labeled(string glyph, string label, bool active, Action onClick, Action<NodeHandle>? onRealized, Element? trailing) => new BoxEl();
            public Element IconButton(string glyph, bool active, Action onClick, Action<NodeHandle>? onRealized) => new BoxEl();
            public Element Chevron() => new BoxEl();
            public Element FilterCommand(int count, bool labelled, string label, Action onClick, Action<NodeHandle> onRealized) => new BoxEl();
            public Element FilterFunnel(int count, Action onClick, Action<NodeHandle> onRealized) => new BoxEl();
            public Element MoreSlot(Element more, int filterCount) => more;
            public IReadOnlyList<MenuFlyoutItem> DensityItems() => [];
        }

        void SelectAllTracks()
        {
            int n = _rowItems?.Count.Peek() ?? 0;
            if (n <= 0) return;
            int start = TrackStart;
            _selection.DeselectAll();
            _selection.SelectRange(start, start + n - 1);
        }

    }

    // ══ 6. THE EMBEDDED ARM: THE LANE SET (library rework §5.5, wave L1) ════════════════════════════════════════════
    //
    // What a pane that has no table YET shimmers with: the lane set the embedded album table converges to and its width tracks. The real header is a
    // component on TableHost (it reads the live sort, the check lane and the measured tier), so a surface with no host cannot mount it — hence the
    // pane's own header shim (Classic's `ClassicLibraryAlbumPane.TableHeaderShim`, T10c1), built from the same GridEl over the same TrackSize[] with
    // the same gap, header height and hairline, so the crossing from the shim to the real header is a cross-fade and never a reflow.
    //
    // WHY NO MEASURED SHAPE: the live shape is a memo over the MEASURED tier, the relief ladder and the appearance prefs (TableHost.ComputeShape) —
    // none of which exists before the table mounts. At `ShowPlays = false` the album arm's lanes are the SAME at every tier 0-3: Album / By / Date /
    // Plays / Tempo / Expand are all off for an album source (Detail.Config.Album carries ShowArtThumb false, so Thumb is off too, and a profile with
    // no Drawer seam has no Expand lane), Heart folds only at tier ≥ 5 — a pane under 300 DIP, which the library's master-detail never gives the album
    // column — and a compilation's Classic Artist lane only at tier 4. What DOES vary is the ROW STYLE (tuned Classic X2): a Classic row is its own
    // skin with its own lane identity (a compilation's Artist lane), so the set is a function of the style and the album's per-track artist, derived
    // from the same rule the table applies (`TableRules.IdentityColumns`) rather than spelled out lane by lane.

    /// <summary>The tier the embedded pane converges to: the 720-859 rung the ~784-DIP album column lands on (W1). Tiers
    /// 0-3 share <see cref="RowMetrics.PadXFor"/> and <see cref="RowMetrics.ColGapFor"/>, so the shim's geometry is
    /// identical across all of them; the number only has to be one of them.</summary>
    public const int EmbeddedTier = 1;

    /// <summary>The embedded album table's lanes at <c>ShowPlays = false</c> for a row style: <c># · ♥ · Title* · ⏱ · …</c>, plus the Artist
    /// lane a Classic row gives an album whose tracks name their artists (a compilation). The ONE set the pane's shimmer, its header shim and
    /// its width tracks all read (library rework §5.5); the host hands the pane the set its table will lay out.
    /// <para>A video is the film glyph after the title, never a lane (<c>TableRules.TrailingColumns</c>), so the shim
    /// needs nothing the rows will carry and the real header lands on exactly the shim's geometry.</para></summary>
    public static ColumnSet EmbeddedColumns(bool classic, bool showTrackArtist) => new(
        Album: false, By: false, Date: false, Plays: false, Heart: true, Thumb: false,
        Actions: true, Tier: EmbeddedTier,
        Artist: TableRules.IdentityColumns(classic, showArtThumb: false, artworkHidden: false, showTrackArtist, EmbeddedTier).Artist,
        Classic: classic);

    /// <summary>The width tracks for an <see cref="EmbeddedColumns"/> set — the SAME cached instance the real rows would get
    /// (<see cref="TracksFor"/>), so the shimmer rows and the header shim share the array the table itself hands out. Art is irrelevant
    /// (the set carries no Thumb lane) but it is part of the cache key, so this passes the ladder's own default rung.</summary>
    public static TrackSize[] EmbeddedTracks(in ColumnSet set) => TracksFor(in set, RowMetrics.ThumbSize);
}
