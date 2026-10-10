// ── Entities/Track.Table.Chrome.cs ────────────────────────────────────────────────────────────────────────────────
// the detail track table's CHROME: the command bar (labeled commands promoted into the measured pane, the rest evicted
// into "…" with 16-DIP promotion hysteresis and a fit latch while search is open), the in-list search disclosure
// (66 → 160…280 over 260 ms, Reflow), the context band's words Find · Filter · Play, the column header (one ColumnSet +
// one TrackSize[] with the rows, keyed cells, sort carets), the Sort / Row size / More flyouts, the 368-wide filter card
// and the selection command bar's commands. The TableHost partial of Track.Table.cs.
// Plus, at STRUCT level, the embedded arm's lane constants and its host-free header shim (§6) — what the library's panes
// shimmer under before a tracklist has answered (library rework §5.5).
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
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
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

        static readonly LayoutTransition s_toolbarCommandMotion = new(TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(220f, Easing.SmoothOut),
            Enter: new EnterExit(Dx: 8f, Opacity: 0f, Active: true), Exit: new EnterExit(Dx: 8f, Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

        static readonly LayoutTransition s_toolbarModeMotion = new(TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(210f, Easing.SmoothOut),
            Enter: new EnterExit(Dx: 10f, Opacity: 0f, Active: true), Exit: new EnterExit(Dx: -8f, Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(150f, Easing.FluentAccelerate));

        // Reflow, never Reveal: the field must PUSH its neighbours through real layout. No ExitDynamics — the width runs
        // 260 ms both ways; only the swap and the underline take the 180 ms exit.
        static readonly LayoutTransition s_searchDisclosure = new(TransitionChannels.Position | TransitionChannels.Size,
            TransitionDynamics.Tween(SearchExpandMs, Easing.SmoothOut), Size: SizeMode.Reflow, Axes: SizeAxes.Width);

        static readonly LayoutTransition s_searchSwap = new(TransitionChannels.Opacity,
            TransitionDynamics.Tween(SearchExpandMs, Easing.SmoothOut),
            Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(SearchCollapseMs, Easing.FluentAccelerate));

        static readonly LayoutTransition s_headerShift = new(TransitionChannels.Position,
            TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));

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
            // embedded pane's shimmer draws `TableHeaderShim(compact: true)`, which states none, and the 8 the real
            // chrome used to add anyway moved every row down by that much on the frame the shimmer released.
            return new BoxEl
            {
                Key = "chrome", Direction = 1,
                Padding = new Edges4(padX, vertical || !_latest.ShowToolbar ? 0f : Spacing.S, padX, 0f),
                Children = children,
            };
        }

        Element Toolbar() => Responsive.Of(_buildToolbar, fallback: _lastW > 0f ? _lastW : 760f) with { Key = "detail-track-commandbar" };

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
                // SplitButton owns its content after mount: the ink rides a binding so a theme flip still reaches it.
                Element playNext = new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(WaveeIcons.PlayNext) { Size = 14f, FontFamily = WaveeIcons.Font, Color = Prop.Of(static () => Tok.TextSecondary) },
                        new TextEl(Loc.Get(Strings.Detail.PlayNext)) { Size = Ui.Caption("").Size, Weight = 600, Color = Prop.Of(static () => Tok.TextSecondary) },
                    ],
                };
                kids.Add(MeasuredCommand(0, "cmd:play-next:" + _contextText,
                    SplitButton.Create(playNext, () => RunOnContext(ActionId.PlayNext), playItems, parts: s_commandBarSplitParts)));
            }
            if (fit.Has(InlineCommand.Shuffle))
                kids.Add(MeasuredCommand(2, "cmd:shuffle", LabeledButton(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), false, Shuffle, null)));
            if (hasTune)
                kids.Add(MeasuredCommand(1, "cmd:tune", ToolTip.Wrap(
                    LabeledButton(Icons.RefineSparkle, Loc.Get(Strings.Detail.Tuning.Tune), false, () => _latest.Profile.Tune?.Invoke(), null),
                    Loc.Get(Strings.Detail.Tuning.Tooltip))));

            bool viewInline = fit.Has(InlineCommand.Filter) || fit.Has(InlineCommand.Sort) || fit.Has(InlineCommand.Density) || fit.Has(InlineCommand.Select);
            if (viewInline && kids.Count > 0) kids.Add(Separator() with { Key = "cmd:separator" });
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
            Element normal = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Shrink = 0f, Children = kids.ToArray() },
                    new BoxEl { Grow = 1f, MinWidth = 0f },
                    search,
                ],
            };
            return CommandSurface("normal", normal);
        }

        /// <summary>The chromeless 44-DIP lane both modes share; the keyed mode box is what animates browse ↔ selection.</summary>
        static Element CommandSurface(string mode, Element content) => new BoxEl
        {
            Direction = 1, Height = Detail.VerticalLayout.ToolbarRowHeight, MinWidth = 0f, ClipToBounds = true,
            Padding = new Edges4(Detail.VerticalLayout.ToolbarSurfacePadX, Detail.VerticalLayout.ToolbarSurfacePadY,
                                 Detail.VerticalLayout.ToolbarSurfacePadX, Detail.VerticalLayout.ToolbarSurfacePadY),
            Children =
            [
                new BoxEl
                {
                    Key = "commandbar-mode:" + mode, Direction = 1, Grow = 1f, MinWidth = 0f,
                    Animate = s_toolbarModeMotion, Children = [content],
                },
            ],
        };

        Element MeasuredCommand(int slot, string key, Element command) => new BoxEl
        {
            Key = key, Direction = 1, Shrink = 0f, Animate = s_toolbarCommandMotion,
            OnBoundsChanged = r => MeasureToolbarCommand(slot, r.W),
            Children = [command],
        };

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

        /// <summary>Real SplitButton behaviour (two hit targets, keyboard chords) wearing CommandBar visuals: the joined
        /// root is ghosted at rest instead of painting the boxed form-control surface.</summary>
        static readonly TemplateParts s_commandBarSplitParts = new()
        {
            [SplitButton.PartRoot] = static e => e with
            {
                AlignSelf = FlexAlign.Center, MinHeight = 32f, Fill = ColorF.Transparent, BorderWidth = 0f, BorderBrush = null,
                Corners = Radii.ControlAll,
            },
            [SplitButton.PartPrimaryButton] = static e => e with { Grow = 0f, MinWidth = 0f, Height = 32f, Padding = new Edges4(9f, 0f, 8f, 0f) },
            [SplitButton.PartSecondaryButton] = static e => e with { Width = 24f, Height = 32f, Padding = default, Justify = FlexJustify.Center },
            [SplitButton.PartDivider] = static e => e with
            {
                Width = 1f, Height = 20f, AlignSelf = FlexAlign.Center, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
            },
        };

        /// <summary>The 32×32 toolbar glyph button: active → the accent@0.11 plate + accent glyph; idle → ghost.</summary>
        static BoxEl IconButton(string glyph, bool active, Action onClick, Action<NodeHandle>? onRealized) => new()
        {
            Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f }) : (Prop<ColorF>)ColorF.Transparent,
            HoverFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.17f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
            PressedFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.08f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            Role = AutomationRole.Button, Focusable = true,
            OnClick = onClick, OnRealized = onRealized,
            Children = [Icon(glyph, 14f) with { Color = active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextSecondary) }],
        };

        /// <summary>The labeled (icon + text) command — every inline command is labeled; one that does not fit is evicted,
        /// never shrunk to a glyph (ch 04 §0.10).</summary>
        static BoxEl LabeledButton(string glyph, string label, bool active, Action onClick, Action<NodeHandle>? onRealized,
                                   Element? trailing = null)
        {
            Prop<ColorF> ink = active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextSecondary);
            Element[] kids = trailing is null
                ? [Icon(glyph, 14f) with { Color = ink }, Ui.Caption(label) with { Weight = 600, Color = ink }]
                : [Icon(glyph, 14f) with { Color = ink }, Ui.Caption(label) with { Weight = 600, Color = ink }, trailing];
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Height = 32f, Padding = new Edges4(9f, 0f, 10f, 0f),
                Corners = Radii.ControlAll,
                Fill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f }) : (Prop<ColorF>)ColorF.Transparent,
                HoverFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.17f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
                PressedFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.08f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
                HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = onClick, OnRealized = onRealized,
                Children = kids,
            };
        }

        static Element Separator() => new BoxEl
        {
            Width = 1f, Height = 20f, AlignSelf = FlexAlign.Center, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
            Margin = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        };

        static Element Chevron() => Icon(Icons.ChevronDown, 8f, Tok.TextTertiary);

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
            if (cfg.ShowPlays || (cfg.PlaysColumnOptIn && Platform.Settings.Get(Platform.Keys.PlaysColumn))) Field(SortColumn.Plays);
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
                        Children = [ToolTip.Wrap(IconButton(Icons.Search, queryActive, h._openSearch, h._captureSearchButton),
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
                var set = shape.Set;
                var sort = h._sort.Value;
                bool checks = h._checksVisible?.Value ?? false;
                bool vertical = h.VerticalArm;
                bool classic = set.Classic;
                var cells = new List<Element>(shape.Tracks.Length);
                void Add(string key, Element cell) => cells.Add(cell with { Key = key });

                // Classic's # cell is EMPTY: no clickable #, no caret slots; Sort → Custom order is the route back (W22).
                Add(CellKey.Num, classic ? new BoxEl() : IndexSortCell(h, sort));
                if (set.Heart) Add(CellKey.Heart, new BoxEl());
                if (set.Thumb) Add(CellKey.Art, new BoxEl());
                // Title owns Artist while the artist is folded into its subline; with a dedicated Artist lane each cell
                // runs its own cycle. The label component freezes its flags, so they are in its key.
                Element title = Embed.Comp(() => new TableSortLabel(h, vertical, set.Artist, classic))
                    with { Key = "sort-label:" + (classic ? "c" : "m") + (set.Artist ? ":a" : "") + (vertical ? ":song" : "") };
                Add(CellKey.Title, SortCell(h, title, SortColumn.Title, sort, FlexJustify.Start, set.Artist));
                if (set.Artist)
                    Add(CellKey.Artist, SortCell(h, HLabel(Loc.Get(Strings.Detail.Column.Artist), SortColumn.Artist, sort, classic),
                        SortColumn.Artist, sort, FlexJustify.Start, artistColumn: true));
                if (set.Album)
                    Add(CellKey.Album, SortCell(h, HLabel(Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort, classic),
                        SortColumn.Album, sort, FlexJustify.Start));
                if (set.By) Add(CellKey.By, PlainHeader(Loc.Get(Strings.Detail.Column.AddedBy), FlexJustify.Start, classic));
                if (set.Date)
                    Add(CellKey.Date, SortCell(h, HLabel(Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort, classic),
                        SortColumn.DateAdded, sort, FlexJustify.Start));
                if (set.Plays)
                    Add(CellKey.Plays, SortCell(h, HLabel(Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort, classic),
                        SortColumn.Plays, sort, FlexJustify.End));
                // BPM · Key is not sortable: tempo lands asynchronously per row, a sort would reorder under the cursor.
                if (RowMetrics.ShowTempo(in set)) Add(CellKey.Tempo, PlainHeader(Loc.Get(Strings.Detail.Column.Tempo), FlexJustify.End, classic));
                Add(CellKey.Duration, SortCell(h, vertical
                        ? HLabel(Loc.Get(Strings.Detail.Column.Time), SortColumn.Duration, sort, classic)
                        : Icon(Icons.Clock, 14f, sort.Column == SortColumn.Duration ? Tok.TextSecondary : Tok.TextTertiary),
                    SortColumn.Duration, sort, FlexJustify.End));
                if (set.Video) Add(CellKey.Video, new BoxEl());
                if (set.Actions) Add(CellKey.More, new BoxEl());
                if (set.Expand) Add(CellKey.Expand, new BoxEl());

                return new BoxEl
                {
                    Direction = 1, ClipToBounds = true,
                    Padding = new Edges4(checks ? 28f : 0f, 0f, 0f, 0f),
                    Animate = s_headerShift,
                    Children =
                    [
                        new GridEl
                        {
                            Columns = shape.Tracks, ColGap = RowMetrics.ColGapFor(set.Tier),
                            RowHeight = TableRules.HeaderHeightFor(classic), Children = cells.ToArray(),
                        },
                        new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                    ],
                };
            }

            /// <summary>The owning header brightens — except Index, the default order, which carries no indicator.</summary>
            static TextEl HLabel(string s, SortColumn col, SortSpec sort, bool classic)
            {
                var t = classic ? Design.Type.MicroMeta(s) : Ui.Caption(s);
                return t with
                {
                    Weight = 600,
                    Color = TableRules.HeaderActive(col, sort.Column, false) ? Tok.TextSecondary : Tok.TextTertiary,
                    MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                };
            }

            static Element PlainHeader(string label, FlexJustify justify, bool classic)
            {
                var t = classic ? Design.Type.MicroMeta(label) : Ui.Caption(label);
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true,
                    Children =
                    [
                        t with
                        {
                            Weight = 600, Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                };
            }

            /// <summary>The # sits at the exact centre of its lane: the SAME caret slot is reserved on both sides and the
            /// descending caret lives only in the right one, so the indicator never nudges # off the row numbers.</summary>
            static Element IndexSortCell(TableHost h, SortSpec sort)
            {
                bool caret = sort.Column == SortColumn.Index && sort.Descending;
                Element Side(bool trailing) => new BoxEl
                {
                    Width = Lane.NumCaretSlot, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = trailing && caret ? [Embed.Comp(() => new TableSortCaret(h))] : [],
                };
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
                    Corners = Radii.ControlAll, HoverFill = Prop.Of(static () => Tok.FillSubtleSecondary),
                    Role = AutomationRole.Button,
                    OnClick = () => h.SetSort(TableRules.NextSort(h._sort.Peek(), SortColumn.Index, false)),
                    Children =
                    [
                        Side(false),
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = [HLabel(Loc.Get(Strings.Detail.Column.Number), SortColumn.Index, sort, false)],
                        },
                        Side(true),
                    ],
                };
            }

            /// <summary>A clickable header: the NextSort cycle, the caret after the label (before it on an end-aligned
            /// lane). Shrinkable + clipped, the same squeeze contract as the row cells.</summary>
            static Element SortCell(TableHost h, Element content, SortColumn col, SortSpec sort, FlexJustify justify,
                                    bool artistColumn = false)
            {
                bool caret = TableRules.HeaderActive(col, sort.Column, artistColumn) && (col != SortColumn.Index || sort.Descending);
                Element[] kids = !caret ? [content]
                    : justify == FlexJustify.End ? [Embed.Comp(() => new TableSortCaret(h)), content]
                    : [content, Embed.Comp(() => new TableSortCaret(h))];
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, Gap = Spacing.XS, MinWidth = 0f, ClipToBounds = true,
                    Corners = Radii.ControlAll, HoverFill = Prop.Of(static () => Tok.FillSubtleSecondary),
                    Role = AutomationRole.Button,
                    OnClick = () => h.SetSort(TableRules.NextSort(h._sort.Peek(), col, artistColumn)),
                    Children = kids,
                };
            }
        }

        /// <summary>The caret pops in when its column becomes the sort and SPRINGS its rotation 0°↔180° on every flip, so
        /// Title↑ → Title↓ → Artist↑ → Artist↓ reads as one continuous rotation, never a glyph swap.</summary>
        sealed class TableSortCaret(TableHost host) : Component
        {
            public override Element Render()
            {
                bool desc = host._sort.Value.Descending;
                UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.EaseInOut, "in");
                UseTransition(AnimChannel.ScaleX, 0.3f, 1f, Expressive.Fast, Easing.Overshoot, "in");
                UseTransition(AnimChannel.ScaleY, 0.3f, 1f, Expressive.Fast, Easing.Overshoot, "in");
                UseSpring(AnimChannel.Rotation, desc ? 180f : 0f, SpringParams.FromResponse(0.30f, 0.7f), desc);
                return new BoxEl
                {
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Icon(Icons.CaretSolidUp, 9f, Tok.TextSecondary)],
                };
            }
        }

        /// <summary>"Title" (the hero arm's "Song"), or "Artist" while the Title header owns an artist sort; the word swap
        /// rises 4 DIP and fades, keyed on the text.</summary>
        sealed class TableSortLabel(TableHost host, bool song, bool artistColumn, bool classic) : Component
        {
            public override Element Render()
            {
                var col = host._sort.Value.Column;
                string text = !artistColumn && col == SortColumn.Artist
                    ? Loc.Get(Strings.Detail.Column.Artist)
                    : Loc.Get(song ? Strings.Detail.Column.Song : Strings.Detail.Column.Title);
                UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut, text);
                UseTransition(AnimChannel.TranslateY, 4f, 0f, Expressive.Fast, Easing.SmoothOut, text);
                var t = classic ? Design.Type.MicroMeta(text) : Ui.Caption(text);
                return t with
                {
                    Weight = 600,
                    Color = TableRules.HeaderActive(SortColumn.Title, col, artistColumn) ? Tok.TextSecondary : Tok.TextTertiary,
                    MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                };
            }
        }

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
                Element trailing = new BoxEl
                {
                    Direction = 0, Gap = 3f, AlignItems = FlexAlign.Center,
                    Children = active ? [Embed.Comp(() => new TableSortCaret(h)), Chevron()] : [Chevron()],
                };
                return LabeledButton(Icons.Sort, SortLabelFor(current.Column), active, Toggle, n => anchor.Value = n, trailing);
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
                return LabeledButton(Icons.RowSize, label, false, Toggle, n => anchor.Value = n, Chevron());
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
                return LabeledButton(Icons.MultiSelect, Loc.Get(Strings.Detail.Select), on, () => h.SetMultiSelect(!h._multi.Peek()), null);
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
                        bool tempoOn = Platform.Settings.Get(Platform.Keys.TempoColumn);
                        items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.TempoColumn), tempoOn,
                            () => Prefs.Appearance.Set(Platform.Keys.TempoColumn, !tempoOn)));
                    }
                    if (cfg.PlaysColumnOptIn)
                    {
                        bool playsOn = Platform.Settings.Get(Platform.Keys.PlaysColumn);
                        items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.PlaysColumn), playsOn,
                            () => Prefs.Appearance.Set(Platform.Keys.PlaysColumn, !playsOn)));
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
                Element more = IconButton(Icons.More, false, Toggle, n => anchor.Value = n);
                // The button's slot never changes size: the badge is an overlay on a fixed 32-DIP box, so a filter turning on moves nothing.
                return ToolTip.Wrap(new BoxEl
                {
                    ZStack = true, Width = 32f, Height = 32f, Shrink = 0f, AlignSelf = FlexAlign.Center,
                    Children = activeFilters > 0
                        ? [more, InfoBadge.Count(activeFilters, parts: s_badgeCornerMore) with { HitTestVisible = false, Key = "more:filter-badge" }]
                        : [more],
                }, Loc.Get(Strings.Common.More));
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

        /// <summary>The count badge's corner placement: top-right of a 32×32 button, ringed in the page's base so it reads over a plate.
        /// Shared by the funnel and by "…" (an active filter badges the button that now owns "Filter…").</summary>
        static readonly TemplateParts s_badgeCorner = new()
        {
            [InfoBadge.PartRoot] = static b => b with
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = 1.5f, BorderColor = Tok.FillSolidBase,
            },
        };

        /// <summary>The same corner badge nudged out by 3 DIP, so on "…" (a centred row of dots) it clears the glyph.</summary>
        static readonly TemplateParts s_badgeCornerMore = new()
        {
            [InfoBadge.PartRoot] = static b => b with
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = 1.5f, BorderColor = Tok.FillSolidBase,
                OffsetX = 3f, OffsetY = -3f,
            },
        };

        /// <summary>The Filter command's props: <paramref name="Labelled"/> = the button wears its word (else the funnel alone).</summary>
        sealed record FilterCommandProps(bool Labelled);

        /// <summary>The count badge of the command bar's Filter button: 18 DIP round, accent. Inline it sits in the labelled button's reserved slot.</summary>
        static readonly TemplateParts s_badgeCommand = new()
        {
            [InfoBadge.PartRoot] = static b => b with
            {
                Width = 18f, MinWidth = 18f, Height = 18f, MaxHeight = 18f, Corners = CornerRadius4.All(9f), Shrink = 0f,
            },
        };

        /// <summary>The same 18-DIP badge on the icon-only button's top-right corner, ringed in the page's base, nudged out by 4 DIP (never past the bar's gap to the next button).</summary>
        static readonly TemplateParts s_badgeCommandCorner = new()
        {
            [InfoBadge.PartRoot] = static b => b with
            {
                Width = 18f, MinWidth = 18f, Height = 18f, MaxHeight = 18f, Corners = CornerRadius4.All(9f), Shrink = 0f,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = 1.5f, BorderColor = Tok.FillSolidBase,
                OffsetX = MathF.Min(4f, CommandBarLayout.Gap), OffsetY = -4f, HitTestVisible = false,
            },
        };

        /// <summary>The command bar's Filter button: a subtle button, funnel + "Filter" (<paramref name="labelled"/>) or the funnel alone,
        /// 32 DIP tall. While <paramref name="count"/> filters are on it takes the accent tint (14% fill, accent border, accent ink) and
        /// an 18-DIP count badge. The border is always laid (transparent at rest) and the labelled form always reserves the badge's
        /// slot, so a filter turning on or off changes colour and nothing else: the fit never sees the count.</summary>
        static Element FilterCommand(int count, bool labelled, Action onClick, Action<NodeHandle> onRealized)
        {
            bool active = count > 0;
            Prop<ColorF> ink = active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextSecondary);
            Element glyph = Icon(Icons.Filter, 14f) with { Color = ink };
            var box = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = Controls.ButtonHeight, Shrink = 0f,
                Corners = Radii.ControlAll, BorderWidth = 1f,
                Fill = active ? Prop.Of(static () => Tok.AccentDefault with { A = 0.14f }) : (Prop<ColorF>)ColorF.Transparent,
                HoverFill = active ? Prop.Of(static () => Tok.AccentDefault with { A = 0.22f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
                PressedFill = active ? Prop.Of(static () => Tok.AccentDefault with { A = 0.10f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
                BorderColor = active ? Prop.Of(static () => Tok.AccentDefault) : (Prop<ColorF>)ColorF.Transparent,
                HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster, BrushTransitionMs = Motion.ControlFaster,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = onClick, OnRealized = onRealized,
            };
            if (labelled)
                return box with
                {
                    Gap = 6f, Padding = new Edges4(9f, 0f, 6f, 0f),
                    Children =
                    [
                        glyph,
                        Ui.Caption(Loc.Get(Strings.Detail.Filter.Short)) with { Weight = 600, Color = ink },
                        new BoxEl
                        {
                            Width = 18f, Height = 18f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = active ? [InfoBadge.Count(count, parts: s_badgeCommand)] : [],
                        },
                    ],
                };
            // Icon-only: the funnel carries more ink above its midpoint (+1 optical offset at rest); with a badge it steps (-4, +4) so glyph
            // and pill never share ink.
            return box with
            {
                ZStack = true, Width = CommandBarLayout.FilterIconWidth, Justify = FlexJustify.Center,
                Children =
                [
                    new BoxEl
                    {
                        Width = 14f, Height = 14f, AlignSelf = FlexAlign.Center, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        OffsetX = active ? -4f : 0f, OffsetY = active ? 4f : 1f, Children = [glyph],
                    },
                    .. active ? new Element[] { InfoBadge.Count(count, parts: s_badgeCommandCorner) } : [],
                ],
            };
        }

        /// <summary>The Filter affordance: on the command bar (<paramref name="command"/>) the labelled-or-icon <see cref="FilterCommand"/>;
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
                    Element button = FilterCommand(activeCount, labelled, Toggle, n => anchor.Value = n);
                    return ToolTip.Wrap(button, Loc.Get(Strings.Detail.Filter.Title));
                }

                if (textMode)
                    return ToolTip.Wrap(Controls.TextAction(Loc.Get(Strings.Detail.Filter.Short), Toggle, toggledOn: active)
                        with { OnRealized = n => anchor.Value = n }, Loc.Get(Strings.Detail.Filter.Title));

                ColorF accent = Tok.AccentTextPrimary;
                // The funnel carries more ink above its midpoint: +1 optical offset at rest; with a badge it steps (−4, +4)
                // so glyph and pill never share ink. Accent belongs to the plate and badge, never the glyph.
                Element glyph = new BoxEl
                {
                    Width = 14f, Height = 14f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    OffsetX = active ? -4f : 0f, OffsetY = active ? 4f : 1f,
                    Children = [Icon(Icons.Filter, 14f, active ? Tok.TextPrimary : Tok.TextSecondary)],
                };
                return ToolTip.Wrap(new BoxEl
                {
                    ZStack = true, Width = 32f, Height = 32f, AlignSelf = FlexAlign.Center,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                    Fill = active ? accent with { A = 0.16f } : ColorF.Transparent,
                    HoverFill = active ? accent with { A = 0.24f } : Tok.FillSubtleSecondary,
                    PressedFill = active ? accent with { A = 0.12f } : Tok.FillSubtleTertiary,
                    Role = AutomationRole.Button, Focusable = true,
                    OnClick = Toggle, OnRealized = n => anchor.Value = n,
                    Children = active ? [glyph, InfoBadge.Count(activeCount, parts: s_badgeCorner)] : [glyph],
                }, Loc.Get(Strings.Detail.Filter.Title));
            }
        }

        /// <summary>The immediate-apply filter card ("Flyout B"): 360 wide, a header ("Filter", what the list shows against what it has,
        /// "Reset"), a divider, then ONE 44-DIP row per facet - its word on the left, a 168-DIP <see cref="ComboBox"/> on the right
        /// (a row off its default tints its combo with the accent and its word turns primary) - a divider, and the two status
        /// <see cref="ToggleSwitch"/> rows. The card normally fits whole; the scroll region (<= 500) is the fallback only.</summary>
        sealed class TableFilterFlyout : Component
        {
            const float CardWidth = 360f, CardMaxHeight = 620f, ScrollMaxHeight = 500f, RowHeight = 44f, RowPadX = 8f, ComboWidth = 168f;

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

                Element Label(string text, bool on) => new TextEl(text)
                {
                    Size = 14f, Color = on ? Tok.TextPrimary : Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                };

                BoxEl Row(string key, bool on, string label, Element control) => new()
                {
                    Key = key, Direction = 0, Height = RowHeight, AlignItems = FlexAlign.Center, Gap = 8f, Padding = new Edges4(RowPadX, 0f, RowPadX, 0f),
                    Children = [Label(label, on), control],
                };

                // One facet: the label and a combo whose tint (accent 14% fill + accent border, laid over the stock field, which has no
                // style seam) reads "this row is filtering". The overlay is always there (transparent at rest), so a value change
                // never restructures the row and the combo keeps its focus.
                Element Pick(string key, string label, Signal<int> signal, string[] options, Action<int> changed, string placeholder = "", bool? tinted = null)
                {
                    bool on = tinted ?? signal.Value != 0;
                    return Row(key, on, label, new BoxEl
                    {
                        ZStack = true, Width = ComboWidth, Shrink = 0f,
                        Children =
                        [
                            ComboBox.Create(options, signal, width: ComboWidth, placeholder: placeholder, onChange: changed) with { Key = key + ":combo" },
                            new BoxEl
                            {
                                Width = ComboWidth, AlignSelf = FlexAlign.Stretch, Corners = Radii.ControlAll, HitTestVisible = false, BorderWidth = 1f,
                                Fill = on ? Prop.Of(static () => Tok.AccentDefault with { A = 0.14f }) : (Prop<ColorF>)ColorF.Transparent,
                                BorderColor = on ? Prop.Of(static () => Tok.AccentDefault) : (Prop<ColorF>)ColorF.Transparent,
                            },
                        ],
                    });
                }

                Element Switch(string key, string label, Signal<bool> signal, FilterFlags flag)
                    => Row(key, signal.Value, label, ToggleSwitch.Create(signal, onChange: on =>
                    {
                        var f = Current;
                        Set(f with { Flags = on ? f.Flags | flag : f.Flags & ~flag });
                    }, style: ToggleSwitch.DefaultStyle with { MinWidth = 0f }) with { Key = key + ":switch" });

                void ClearAll()
                {
                    _scope.Value = 0; _explicit.Value = 0; _video.Value = 0; _liked.Value = false; _playable.Value = false;
                    _duration.Value = 0; _added.Value = 0; _tempo.Value = 0; _origin.Value = 0;
                    Set(FilterState.Default);
                }

                var rows = new List<Element>(7);
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

                var toggles = new List<Element>(2);
                if (caps.HasLibrary || current.LikedOnly)
                    toggles.Add(Switch("filter:liked", Loc.Get(Strings.Detail.Filter.OnlyLiked), _liked, FilterFlags.LikedOnly));
                if (caps.HasUnavailable || current.PlayableOnly)
                    toggles.Add(Switch("filter:playable", Loc.Get(Strings.Detail.Filter.HideUnplayable), _playable, FilterFlags.PlayableOnly));

                static BoxEl Divider() => new() { Height = 1f, Fill = Tok.StrokeDividerDefault };
                var body = new List<Element>(3)
                {
                    new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(8f, 4f, 8f, toggles.Count > 0 ? 4f : 8f), Children = rows.ToArray() },
                };
                if (toggles.Count > 0)
                {
                    body.Add(Divider());
                    body.Add(new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(8f, 4f, 8f, 8f), Children = toggles.ToArray() });
                }

                return new BoxEl
                {
                    Direction = 1, Width = CardWidth, MinWidth = 320f, MaxWidth = CardWidth, MaxHeight = CardMaxHeight, MinHeight = 0f,
                    ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Height = 48f, Padding = new Edges4(16f, 0f, 8f, 0f),
                            Children =
                            [
                                new TextEl(Loc.Get(Strings.Detail.Filter.Short)) { Size = 16f, Weight = 600, Color = Tok.TextPrimary },
                                Ui.Caption(status) with { Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                HyperlinkButton.Create(Loc.Get(Strings.Detail.Filter.Reset), ClearAll, isEnabled: !current.IsDefault),
                            ],
                        },
                        Divider(),
                        new ScrollEl
                        {
                            ContentSized = true, Grow = 1f, MinHeight = 0f, MaxHeight = ScrollMaxHeight,
                            Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = body.ToArray() },
                        },
                    ],
                };
            }
        }


        // ══ 7. THE SELECTION BAR ════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The selection mode of the command lane. Count 0 + minCount 0: the bar's props never change, and its
        /// commands read the live selection themselves — so "1 selected" can never sit beside "Play 4 next".</summary>
        Element SelectionSurface(string mode)
            => CommandSurface(mode, Controls.SelectionBar(0, _selectionCommands, minCount: 0));

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

        void SelectAllTracks()
        {
            int n = _rowItems?.Count.Peek() ?? 0;
            if (n <= 0) return;
            int start = TrackStart;
            _selection.DeselectAll();
            _selection.SelectRange(start, start + n - 1);
        }

    }

    // ══ 6. THE EMBEDDED ARM: THE LANE CONSTANTS + THE HEADER SHIM (library rework §5.5, wave L1) ══════════════════════
    //
    // What a pane that has no table YET shimmers with: the lane set the embedded album table converges to, its width
    // tracks, and the column header ALONE. The real header is a component on TableHost (it reads the live sort, the check
    // lane and the measured tier), so a surface with no host cannot mount it — hence this shim, built from the same
    // GridEl over the same TrackSize[] with the same gap, header height and hairline, so the crossing from the shim to
    // the real header is a cross-fade and never a reflow.
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

    /// <summary>The embedded table's column header, WITHOUT a host: <c>#</c> (centred between the same two caret slots the
    /// real <c>IndexSortCell</c> reserves, so it sits over the row numbers), an empty ♥ lane, "Title", the clock and an
    /// empty "…" lane, then the band's single 1-DIP hairline. Inert on purpose — there is nothing to sort yet.
    /// <para><paramref name="compact"/> is the register: true = the pane's (no air above the header, because no toolbar
    /// sits over it — the embedded arm), false = the full table's two-column chrome, which pays <c>Spacing.S</c> above it.
    /// The horizontal padding is the chrome's own <c>PadXFor(tier)</c> in both cases, so a caller adds NONE of its own.</para>
    /// <para>The colours are the default sort's (Index): the <c>#</c> reads as the active header, everything else as
    /// tertiary — exactly what the real header paints the instant it replaces this one.</para></summary>
    public static Element TableHeaderShim(bool compact)
    {
        var set = EmbeddedColumns;
        var tracks = EmbeddedTracks;
        var cells = new List<Element>(tracks.Length);
        cells.Add(new BoxEl
        {
            Key = CellKey.Num, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl { Width = Lane.NumCaretSlot, Shrink = 0f },
                new BoxEl
                {
                    Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [ShimLabel(Loc.Get(Strings.Detail.Column.Number), active: true)],
                },
                new BoxEl { Width = Lane.NumCaretSlot, Shrink = 0f },
            ],
        });
        if (set.Heart) cells.Add(new BoxEl { Key = CellKey.Heart });
        cells.Add(new BoxEl
        {
            Key = CellKey.Title, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
            MinWidth = 0f, ClipToBounds = true,
            Children = [ShimLabel(Loc.Get(Strings.Detail.Column.Title), active: false)],
        });
        cells.Add(new BoxEl
        {
            Key = CellKey.Duration, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            MinWidth = 0f, ClipToBounds = true,
            Children = [Icon(Icons.Clock, 14f, Tok.TextTertiary)],
        });
        if (set.Actions) cells.Add(new BoxEl { Key = CellKey.More });

        float padX = RowMetrics.PadXFor(set.Tier);
        return new BoxEl
        {
            Key = "header:shim", Direction = 1, ClipToBounds = true,
            Padding = new Edges4(padX, compact ? 0f : Spacing.S, padX, 0f),
            Children =
            [
                new GridEl
                {
                    Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier),
                    RowHeight = TableRules.HeaderHeightFor(classic: false), Children = cells.ToArray(),
                },
                new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
            ],
        };
    }

    /// <summary>The header label at the Modern rung (12/600), in the real header's two colours.</summary>
    static TextEl ShimLabel(string text, bool active) => Ui.Caption(text) with
    {
        Weight = 600, Color = active ? Tok.TextSecondary : Tok.TextTertiary,
        MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };
}
