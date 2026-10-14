// ── Entities/Track.Table.Chrome.cs ────────────────────────────────────────────────────────────────────────────────
// the detail track table's CHROME: the command bar (labeled commands promoted into the measured pane, the rest evicted
// into "…" with 16-DIP promotion hysteresis and a fit latch while search is open), the in-list search disclosure
// (66 → 160…280 over 260 ms, Reflow), the context band's words Find · Filter · Play, the column header (one ColumnSet +
// one TrackSize[] with the rows, keyed cells, sort carets), the Sort / Row size / More flyouts, the 368-wide filter card
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
        const float ToolbarPaneInset = 12f;

        Action<Action>? _post;
        InputHooks? _hooks;

        /// <summary>The structure's command-bar kit (T8b); a host with no registry gets one that draws nothing (logged with the rows).</summary>
        ITrackCommandKit Kit => _listStyle?.Chrome.Commands ?? NoChromeKit.Instance;
        readonly Signal<bool> _searchExpanded = new(false);
        readonly Signal<bool> _searchFocused = new(false);
        bool _restoreSearchFocus;
        NodeHandle _searchButtonNode;

        // Conservative first-frame LABELED widths (Play next · Tune · Shuffle · Sort · Row size · Select · Filter), refined by the
        // measured commands; the fit resolves against these and the pane.
        readonly Signal<int> _toolbarEpoch = new(0);
        readonly float[] _toolbarWidths = [120f, 92f, 96f, 156f, 144f, 82f, CommandBarLayout.FilterLabelledNominal];
        CommandBarFit? _toolbarFit;
        // The fit LATCHED when search opened, with the pane it resolved against: promoting/evicting mid-flight re-measures,
        // bumps the epoch and would hand the width tween a new target. A genuine pane resize drops the latch.
        (float Available, CommandBarFit Fit)? _searchOpenFit;
        int _selectionPrevCount;

        Func<float, Element> _buildToolbar = null!;
        Func<int, Element> _selectionCommands = null!;
        Action _openSearch = null!, _toggleFind = null!, _exitSelection = null!, _selectAllTracks = null!;
        Action<NodeHandle> _captureSearchButton = null!;
        // the shared selection lane's providers (cached: the lane's "…" re-reads them when it OPENS)
        Func<IReadOnlyList<Track>> _laneTracks = null!;
        Func<IReadOnlyList<Episode>> _laneEpisodes = null!;
        Func<PlaylistHost> _laneHost = null!;

        void InitChrome()
        {
            _buildToolbar = BuildToolbar;
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
        static PopupOptions RichPopup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
        { ConstrainToRootBounds = false };

        // ══ 1. THE CHROME STACK ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Toolbar · chips · lens · header (two-column); lens · header (no toolbar); chips · lens · header (the
        /// hero arm, whose toolbar lives in the hero). The chips sit BETWEEN the bar and the header — they change what
        /// the rows contain — and the lens directly above the header, in the header's own register (W18). No fill: the
        /// stuck stratum is an unpainted omission and the rows are clipped at its lower edge.</summary>
        Element Chrome(in Shape shape, Element? chips, Element? lens)
        {
            float padX = RowMetrics.PadXFor(shape.Set.Tier);
            bool vertical = VerticalArm;
            Element header = Embed.Comp(() => new TableHeader(this)) with { Key = "header" };
            var stack = new List<Element>(4);
            if (!vertical && _latest.ShowToolbar) stack.Add(Toolbar());
            if (chips is not null && (vertical || _latest.ShowToolbar)) stack.Add(chips);
            if (lens is not null) stack.Add(lens);
            stack.Add(header);
            Element[] children = !vertical && _latest.ShowToolbar
                ? [new BoxEl { Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, 0f, 0f, Spacing.XS), Children = stack.ToArray() }]
                : stack.ToArray();
            // The 8 DIP above the header is AIR UNDER THE TOOLBAR, so an arm with no toolbar must not pay it: the
            // embedded pane's shimmer draws its own header shim (`ClassicLibraryAlbumPane`, compact), which states none, and the 8 the real
            // chrome used to add anyway moved every row down by that much on the frame the shimmer released.
            return new BoxEl
            {
                Key = "chrome", Direction = 1,
                Padding = new Edges4(padX, vertical || !_latest.ShowToolbar ? 0f : Spacing.S, padX, 0f),
                Children = children,
            };
        }

        Element Toolbar() => Responsive.Of("detail-track-commandbar", _buildToolbar, fallback: _lastW > 0f ? _lastW : 760f);

        // ══ 2. THE COMMAND BAR ══════════════════════════════════════════════════════════════════════════════════════

        Element BuildToolbar(float available)
        {
            _ = _toolbarEpoch.Value;   // measured labeled widths refine the first-frame budgets
            if (_selectionVisible?.Value == true) return SelectionSurface("selection");

            bool vertical = VerticalArm;
            bool hasTune = P.Tune is not null;
            bool hasSelect = Cfg.Selection != ItemsSelectionMode.None;
            bool explicitSearch = _searchExpanded.Value;
            var w = _toolbarWidths;
            var widths = new CommandWidths(w[0], w[1], w[2], w[3], w[4], w[5], w[6]);
            float pane = HeldPane(MathF.Max(0f, available - ToolbarPaneInset));
            var fit = CommandBarLayout.Resolve(pane, in widths, vertical, hasTune, hasSelect, explicitSearch, _toolbarFit);
            if (!explicitSearch) _searchOpenFit = null;
            else if (_searchOpenFit is { } latched && MathF.Abs(latched.Available - pane) <= 0.5f) fit = latched.Fit;
            else _searchOpenFit = (pane, fit);
            _toolbarFit = fit;

            var kids = new List<Element>(9);
            if (!vertical)
            {
                IReadOnlyList<MenuFlyoutItem> playItems =
                [
                    new(Loc.Get(Strings.Detail.AddToQueue), ActionIcons.Resolve(ActionIcons.Queue), true,
                        () => RunOnContext(ActionId.AddToQueue)),
                ];
                kids.Add(MeasuredCommand(0, "cmd:play-next:" + _contextText,
                    Kit.PlayNext(Loc.Get(Strings.Detail.PlayNext), () => RunOnContext(ActionId.PlayNext), playItems)));
            }
            if (fit.Has(InlineCommand.Shuffle))
                kids.Add(MeasuredCommand(2, "cmd:shuffle", Kit.Labeled(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), false, Shuffle, null)));
            if (hasTune)
                kids.Add(MeasuredCommand(1, "cmd:tune", ToolTip.Wrap(
                    Kit.Labeled(Icons.RefineSparkle, Loc.Get(Strings.Detail.Tuning.Tune), false, () => _latest.Profile.Tune?.Invoke(), null),
                    Loc.Get(Strings.Detail.Tuning.Tooltip))));

            bool viewInline = fit.Has(InlineCommand.Filter) || fit.Has(InlineCommand.Sort) || fit.Has(InlineCommand.Density) || fit.Has(InlineCommand.Select);
            if (viewInline && kids.Count > 0) kids.Add(Kit.Separator() with { Key = "cmd:separator" });
            if (fit.Has(InlineCommand.Filter))
                kids.Add(FilterCommandSlot(fit.Has(InlineCommand.FilterLabel)));
            if (fit.Has(InlineCommand.Sort))
                kids.Add(MeasuredCommand(3, "cmd:sort", Embed.Comp(() => new TableSortButton(this))));
            if (fit.Has(InlineCommand.Density))
                kids.Add(MeasuredCommand(4, "cmd:density", Embed.Comp(() => new TableDensityButton(this))));
            if (fit.Has(InlineCommand.Select))
                kids.Add(MeasuredCommand(5, "cmd:select", Embed.Comp(() => new TableSelectButton(this))));

            var overflow = InlineCommand.Shuffle | InlineCommand.Filter | InlineCommand.Sort | InlineCommand.Density | (hasSelect ? InlineCommand.Select : InlineCommand.None);
            overflow &= ~fit.Inline;
            kids.Add(Embed.Comp(() => new TableMoreButton(this, overflow))
                with { Key = "cmd:more:" + (int)overflow + ":" + _contextText });

            Element search = Embed.Comp(new SearchProps(fit.SearchExpanded, fit.SearchWidth, false, Funnel: false), () => new TableSearchHost(this))
                with { Key = "search-host" };
            return Kit.Surface("normal", Kit.Bar(kids.ToArray(), search));
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
            ToggleOverlay(overlay, anchor, handle, () => Embed.Comp(() => new TableFilterFlyout(this)), FilterPopup, () => HoldBarForFilterCard(false));
            if (handle.Value is { IsOpen: true }) HoldBarForFilterCard(true);
        }

        /// <summary>The inline Filter command of the bar. Its props carry the label, so a rung that drops the word re-renders the SAME
        /// button (an open card keeps its anchor and handle); only the labelled form reports its width to the fit.</summary>
        Element FilterCommandSlot(bool labelled)
            => MeasuredCommand(labelled ? 6 : -1, "cmd:filter",
                Embed.Comp(new FilterCommandProps(labelled), () => new TableFilterButton(this, textMode: false, command: true)));

        void MeasureToolbarCommand(int slot, float width)
        {
            if ((uint)slot >= (uint)_toolbarWidths.Length || width <= 1f) return;
            if (MathF.Abs(_toolbarWidths[slot] - width) <= 0.5f) return;
            _toolbarWidths[slot] = width;
            _toolbarEpoch.Value = _toolbarEpoch.Peek() + 1;
        }

        static string SortLabelFor(SortColumn c) => c switch
        {
            SortColumn.Index => Loc.Get(Strings.Detail.Sort.CustomOrder),
            SortColumn.Title => Loc.Get(Strings.Detail.Sort.Title),
            SortColumn.Artist => Loc.Get(Strings.Detail.Sort.Artist),
            SortColumn.Album => Loc.Get(Strings.Detail.Sort.Album),
            SortColumn.DateAdded => Loc.Get(Strings.Detail.Sort.DateAdded),
            SortColumn.Duration => Loc.Get(Strings.Detail.Sort.Duration),
            SortColumn.Plays => Loc.Get(Strings.Detail.Column.Plays),
            _ => "",
        };

        static string DensityLabel(int d) => d switch
        {
            0 => Loc.Get(Strings.Detail.Density.Compact),
            2 => Loc.Get(Strings.Detail.Density.Cozy),
            3 => Loc.Get(Strings.Detail.Density.Comfortable),
            _ => Loc.Get(Strings.Detail.Density.Default),
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

        static List<MenuFlyoutItem> DensityItems()
        {
            int current = Math.Clamp(Platform.Settings.Get(Platform.Keys.RowDensity), 0, 3);
            var items = new List<MenuFlyoutItem>(4);
            for (int i = 0; i < 4; i++)
            {
                int value = i;
                items.Add(MenuFlyoutItem.RadioItem(DensityLabel(value), current == value, () => SetDensity(value)));
            }
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
                float width = p.Compact && !expanded ? CommandBarLayout.SearchWithFilterWidth : p.Width;
                bool queryActive = h._query.Value.Length > 0;
                bool focused = expanded && h._searchFocused.Value;
                Element query = expanded
                    ? new BoxEl
                    {
                        Key = "search:field", Direction = 1, Height = 32f, Animate = s_searchSwap,
                        Children = [Embed.Comp(() => new TableSearchField(h, band: p.Compact))],
                    }
                    : new BoxEl
                    {
                        Key = "search:icon", Direction = 1, Width = 32f, Height = 32f, JustifySelf = FlexAlign.Start, Animate = s_searchSwap,
                        Children = [ToolTip.Wrap(h.Kit.IconButton(Icons.Search, queryActive, h._openSearch, h._captureSearchButton),
                                                 Loc.Get(Strings.Detail.Filter.SearchThisList))],
                    };
                // No explicit row width: it fills the host whose width is the tween, so the funnel rides the right edge
                // for the whole flight instead of hanging past a narrower clip. The query region SHRINKS (G-254): a ZStack
                // measures its widest layer and the engine's flex items do not shrink by default (MinWidth 0 is no stand-in),
                // so without it a long query, or the 66-DIP start of the expand tween, pushes the fixed 32-DIP funnel out.
                var row = new BoxEl
                {
                    Direction = 0, Gap = expanded ? 0f : CommandBarLayout.Gap, Height = 32f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Key = "search-query-region", ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = 32f, ClipToBounds = true, Children = [query] },
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
                    ZStack = true, Width = width, Height = 32f, Shrink = 0f, Corners = Radii.ControlAll,
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
                    Checks = h._checksVisible?.Value ?? false, Vertical = h.VerticalArm,
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
                bool active = current.Column != SortColumn.Index;
                void Toggle() => ToggleOverlay(overlay, anchor, handle,
                    () => MenuFlyout.Create(h.SortItems(), () => handle.Value?.Close()), MenuPopup);
                Element trailing = h.Kit.Trail(active ? [h.Kit.SortCaret(h._sort), h.Kit.Chevron()] : [h.Kit.Chevron()]);
                return h.Kit.Labeled(Icons.Sort, SortLabelFor(current.Column), active, Toggle, n => anchor.Value = n, trailing);
            }
        }

        /// <summary>Row size: a stepped slider in a rich popup. While the popup is open the button keeps the label it OPENED
        /// with — relabelling mid-drag would move its right edge and re-anchor the popup under the pointer. Never accent:
        /// density is a view preference, not an active filter.</summary>
        sealed class TableDensityButton(TableHost host) : Component
        {
            public override Element Render()
            {
                var overlay = UseContext(Overlay.Service);
                var anchor = UseRef<NodeHandle>(default);
                var handle = UseRef<OverlayHandle?>(null);
                var frozen = UseRef<string?>(null);
                var closedEpoch = UseSignal(0);
                _ = closedEpoch.Value;   // the close re-renders the button so the frozen label is released
                int current = Prefs.Appearance.RowDensity();
                void Toggle()
                {
                    if (handle.Value is not { IsOpen: true }) frozen.Value = DensityLabel(Prefs.Appearance.RowDensity());
                    ToggleOverlay(overlay, anchor, handle, static () => Embed.Comp(static () => new TableDensityPanel()), RichPopup,
                        () => { frozen.Value = null; closedEpoch.Value = closedEpoch.Peek() + 1; });
                }
                string label = handle.Value is { IsOpen: true } && frozen.Value is { } f ? f : DensityLabel(current);
                _ = host;
                return host.Kit.Labeled(Icons.RowSize, label, false, Toggle, n => anchor.Value = n, host.Kit.Chevron());
            }
        }

        sealed class TableDensityPanel : Component
        {
            public override Element Render()
            {
                int d = Prefs.Appearance.RowDensity();
                // The slider rides a FloatSignal; mirror the int preference into it so an external change moves the thumb.
                var dv = UseFloatSignal(d);
                UseEffect(() => { dv.Value = d; }, DepKey.From(d));
                return Layer(Edges4.All(Spacing.M), new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, MinWidth = 240f,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center,
                            Children = [Ui.BodyStrong(Loc.Get(Strings.Detail.Density.RowSize)) with { Grow = 1f }, Ui.Caption(DensityLabel(d))],
                        },
                        Slider.Create(dv, v => SetDensity((int)MathF.Round(v)),
                            new Slider.SliderOptions
                            {
                                Min = 0f, Max = 3f, Step = 1f, TickFrequency = 1f,
                                ThumbToolTipValueConverter = v => DensityLabel(Math.Clamp((int)MathF.Round(v), 0, 3)),
                            },
                            length: 216f),
                    ],
                });
            }
        }

        sealed class TableSelectButton(TableHost host) : Component
        {
            public override Element Render()
            {
                var h = host;
                bool on = h._multi.Value;
                return h.Kit.Labeled(Icons.MultiSelect, Loc.Get(Strings.Detail.Select), on, () => h.SetMultiSelect(!h._multi.Peek()), null);
            }
        }

        /// <summary>"…": the evicted commands first (Shuffle · Sort ▸ · Row size ▸), the two column opt-ins, the Select
        /// toggle, then — only if anything above exists — a separator and the playlist deposit ("Copy to playlist" on a
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
                    if ((overflow & InlineCommand.Density) != 0)
                        items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Detail.Density.RowSize), DensityItems(), Icons.List));
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
                    if ((overflow & InlineCommand.Select) != 0)
                    {
                        bool selecting = h._multi.Peek();
                        items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.Select), selecting, () => h.SetMultiSelect(!selecting)));
                    }
                    if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);

                    var src = h._latest.Source;
                    var tracks = new Track[src.Count];
                    for (int i = 0; i < tracks.Length; i++) tracks[i] = src.At(i);
                    var ctx = new ActionContext(ActionTarget.ForTracks(tracks), Actions.Services);
                    bool copy = cfg.Heart == HeartMode.Follow || cfg.Kind == DetailKind.Liked;
                    items.Add(AddToPlaylistItem(in ctx, overlay) with
                    {
                        Label = Loc.Get(copy ? Strings.Detail.CopyToPlaylist : Strings.Detail.AddToPlaylist),
                    });
                    return items;
                }

                void Toggle() => ToggleOverlay(overlay, anchor, handle, () => MenuFlyout.Create(Items(), () => handle.Value?.Close()), MenuPopup);
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

            var kind = SelectionLaneKind.Tracks;
            if (_latest.Source.HasEpisodes)
            {
                var (anyTrack, anyEpisode) = SelectedKindMix();
                if (anyEpisode && !anyTrack) kind = SelectionLaneKind.Episodes;
                else if (anyEpisode) kind = SelectionLaneKind.Mixed;
            }
            var args = new SelectionLaneArgs(kind, count, _laneTracks, _laneEpisodes, _laneHost, _exitSelection, _selectAllTracks, wasVisible);
            return Track.SelectionLane(fit, in args);
        }

        /// <summary>The kit of a host mounted where no registry provides <c>StyleKeys.TrackTable</c>: every command draws an empty box.</summary>
        sealed class NoChromeKit : ITrackCommandKit
        {
            public static readonly NoChromeKit Instance = new();

            public Element Surface(string mode, Element content) => content;
            public Element Command(string key, Element command, Action<float> onWidth) => new BoxEl { Key = key };
            public Element Bar(Element[] commands, Element search) => new BoxEl();
            public Element PlayNext(string label, Action onPlay, IReadOnlyList<MenuFlyoutItem> items) => new BoxEl();
            public Element Labeled(string glyph, string label, bool active, Action onClick, Action<NodeHandle>? onRealized, Element? trailing) => new BoxEl();
            public Element IconButton(string glyph, bool active, Action onClick, Action<NodeHandle>? onRealized) => new BoxEl();
            public Element Separator() => new BoxEl();
            public Element Chevron() => new BoxEl();
            public Element Trail(Element[] parts) => new BoxEl();
            public Element SortCaret(IReadSignal<SortSpec> sort) => new BoxEl();
            public Element FilterCommand(int count, bool labelled, string label, Action onClick, Action<NodeHandle> onRealized) => new BoxEl();
            public Element FilterFunnel(int count, Action onClick, Action<NodeHandle> onRealized) => new BoxEl();
            public Element MoreSlot(Element more, int filterCount) => more;
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

    // ══ 6. THE EMBEDDED ARM: THE LANE CONSTANTS (library rework §5.5, wave L1) ═══════════════════════════════════════
    //
    // What a pane that has no table YET shimmers with: the lane set the embedded album table converges to and its width tracks. The real header is a
    // component on TableHost (it reads the live sort, the check lane and the measured tier), so a surface with no host cannot mount it — hence the
    // pane's own header shim (Classic's `ClassicLibraryAlbumPane.TableHeaderShim`, T10c1), built from the same GridEl over the same TrackSize[] with
    // the same gap, header height and hairline, so the crossing from the shim to the real header is a cross-fade and never a reflow.
    //
    // WHY A CONSTANT SET, NOT A COMPUTED ONE: the live shape is a memo over the MEASURED tier, the relief ladder and the
    // appearance prefs (TableHost.ComputeShape) — none of which exists before the table mounts. At `ShowPlays = false`
    // the album arm's lanes are the SAME at every tier 0-4: Album / By / Date / Plays / Tempo / Artist / Expand are all
    // off for an album source (Detail.Config.Album carries ShowArtThumb false, so Thumb is off too, and a profile with no
    // Drawer seam has no Expand lane), and Heart folds only at tier ≥ 5 — a pane under 300 DIP, which the library's
    // master-detail never gives the album column. So the compact-tier set below is EXACT for every width this surface
    // has, and it is derived from the same rules the table applies rather than spelled out lane by lane.

    /// <summary>The tier the embedded pane converges to: the 720-859 rung the ~784-DIP album column lands on (W1). Tiers
    /// 0-3 share <see cref="RowMetrics.PadXFor"/> and <see cref="RowMetrics.ColGapFor"/>, so the shim's geometry is
    /// identical across all of them; the number only has to be one of them.</summary>
    public const int EmbeddedTier = 1;

    /// <summary>The embedded album table's lanes at <c>ShowPlays = false</c>: <c># · ♥ · Title* · ⏱ · …</c>. The ONE
    /// constant the pane's shimmer, its header shim and its width tracks all read (library rework §5.5).
    /// <para>A release with a video track trades the "…" lane for the film lane (<c>TableRules.TrailingColumns</c>) — a
    /// trade of CELL, not of width, since 2026-09-18: both are <c>Lane.Actions</c> wide. So the shim can show the common
    /// arm without knowing what the rows will carry, and the real header lands on exactly the shim's geometry.</para></summary>
    public static readonly ColumnSet EmbeddedColumns = new(
        Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true, Thumb: false,
        Actions: true, Tier: EmbeddedTier);

    /// <summary>The width tracks for <see cref="EmbeddedColumns"/> — the SAME cached instance the real rows would get
    /// (<see cref="TracksFor"/>), so the shimmer rows and the header shim share the array the table itself hands out.
    /// <para>A property over a lazy holder, NOT a <c>static readonly</c> field: field initializers of a partial type run
    /// in an order C# does not specify across FILES, and this one would have to read <c>Track.UI.cs</c>'s track cache. The
    /// holder is initialized on first touch instead, by which point the struct's own statics are in.</para></summary>
    public static TrackSize[] EmbeddedTracks => EmbeddedLanes.Tracks;

    static class EmbeddedLanes
    {
        // Art is irrelevant here (the set carries no Thumb lane) but it is part of the cache key, so pass the ladder's
        // own default rung rather than a zero that would fork the cache.
        internal static readonly TrackSize[] Tracks = TracksFor(in EmbeddedColumns, RowMetrics.ThumbSize);
    }
}
