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
using FluentGpu.Scroll;
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

        // Conservative first-frame LABELED widths (Play next · Tune · Shuffle · Sort · Row size · Select), refined by the
        // measured commands; the fit resolves against these and the pane.
        readonly Signal<int> _toolbarEpoch = new(0);
        readonly float[] _toolbarWidths = [120f, 92f, 96f, 156f, 144f, 82f];
        CommandBarFit? _toolbarFit;
        // The fit LATCHED when search opened, with the pane it resolved against: promoting/evicting mid-flight re-measures,
        // bumps the epoch and would hand the width tween a new target. A genuine pane resize drops the latch.
        (float Available, CommandBarFit Fit)? _searchOpenFit;
        int _selectionPrevCount;

        Func<float, Element> _buildToolbar = null!;
        Func<int, Element> _selectionCommands = null!;
        Action _openSearch = null!, _toggleFind = null!, _exitSelection = null!, _selectAllTracks = null!;
        Action<NodeHandle> _captureSearchButton = null!;

        void InitChrome()
        {
            _buildToolbar = BuildToolbar;
            _selectionCommands = SelectionCommands;
            _openSearch = () => _searchExpanded.Value = true;
            _toggleFind = () => { if (_searchExpanded.Peek()) CollapseSearch(restoreFocus: false); else _searchExpanded.Value = true; };
            _exitSelection = () => { _selection.ClearSelection(); if (_multi.Peek()) SetMultiSelect(false); };
            _selectAllTracks = SelectAllTracks;
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
            TransitionDynamics.Tween(MotionTok.DisclosureExpand.DurationMs, Easing.FluentDecelerate));

        static PopupOptions MenuPopup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false };
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
            var widths = new CommandWidths(w[0], w[1], w[2], w[3], w[4], w[5]);
            float pane = MathF.Max(0f, available - ToolbarPaneInset);
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
                        new TextEl(Loc.Get(Strings.Detail.PlayNext)) { Size = 12f, Weight = 600, Color = Prop.Of(static () => Tok.TextSecondary) },
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

            bool viewInline = fit.Has(InlineCommand.Sort) || fit.Has(InlineCommand.Density) || fit.Has(InlineCommand.Select);
            if (viewInline && kids.Count > 0) kids.Add(Separator() with { Key = "cmd:separator" });
            if (fit.Has(InlineCommand.Sort))
                kids.Add(MeasuredCommand(3, "cmd:sort", Embed.Comp(() => new TableSortButton(this))));
            if (fit.Has(InlineCommand.Density))
                kids.Add(MeasuredCommand(4, "cmd:density", Embed.Comp(() => new TableDensityButton(this))));
            if (fit.Has(InlineCommand.Select))
                kids.Add(MeasuredCommand(5, "cmd:select", Embed.Comp(() => new TableSelectButton(this))));

            var overflow = InlineCommand.Shuffle | InlineCommand.Sort | InlineCommand.Density | (hasSelect ? InlineCommand.Select : InlineCommand.None);
            overflow &= ~fit.Inline;
            kids.Add(Embed.Comp(() => new TableMoreButton(this, overflow))
                with { Key = "cmd:more:" + (int)overflow + ":" + _contextText });

            Element search = Embed.Comp(new SearchProps(fit.SearchExpanded, fit.SearchWidth, false), () => new TableSearchHost(this))
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
                ? [Icon(glyph, 14f) with { Color = ink }, new TextEl(label) { Size = 12f, Weight = 600, Color = ink }]
                : [Icon(glyph, 14f) with { Color = ink }, new TextEl(label) { Size = 12f, Weight = 600, Color = ink }, trailing];
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

        sealed record SearchProps(bool Expanded, float Width, bool Compact);

        /// <summary>The context band's search field. Its width is DERIVED: the band is <paramref name="availW"/> wide with
        /// the gutter either side, and the right cluster is the three words plus one cluster gap — the identity block is
        /// unmounted while search is open, so all of that room is the field's.</summary>
        Element CompactSearch(float availW, float left)
        {
            Span<float> actions =
            [
                Detail.BandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.Filter.Find), Detail.BandLayout.ActionPadX),
                Detail.BandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.Filter.Short), Detail.BandLayout.ActionPadX),
                Detail.BandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.Play), Detail.BandLayout.ActionPadX),
            ];
            float room = availW - left * 2f - Detail.BandLayout.ActionsWidth(actions) - Detail.BandLayout.ClusterGap;
            float width = MathF.Max(CommandBarLayout.SearchIconWidth, MathF.Min(room, CommandBarLayout.SearchMax));
            return Embed.Comp(new SearchProps(false, width, true), () => new TableSearchHost(this)) with { Key = "compact-search-host" };
        }

        /// <summary>The band's RIGHT cluster: Find · Filter · Play as plateless words — the same handlers the rest-state
        /// search glyph, the funnel and the play FAB carry. Find keeps its node capture so a collapse restores focus.</summary>
        Element BandActions() => new BoxEl
        {
            Direction = 0, Gap = Detail.BandLayout.ActionGap, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children =
            [
                Controls.TextAction(Loc.Get(Strings.Detail.Filter.Find), _toggleFind) with { Key = "band:find", OnRealized = _captureSearchButton },
                Embed.Comp(() => new TableFilterButton(this, textMode: true)) with { Key = "band:filter" },
                Controls.TextAction(Loc.Get(Strings.Detail.Play), _playAll, primary: true) with { Key = "band:play" },
            ],
        };

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
                float width = p.Compact && !expanded ? CommandBarLayout.SearchIconWidth : p.Width;
                bool queryActive = h._query.Value.Length > 0;
                bool focused = expanded && h._searchFocused.Value;
                Element query = expanded
                    ? new BoxEl
                    {
                        Key = "search:field", Direction = 1, Height = 32f, Animate = s_searchSwap,
                        Children = [Embed.Comp(() => new TableSearchField(h))],
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
                        // STABLE key: keying on capabilities would remount the funnel and orphan an open flyout.
                        Embed.Comp(() => new TableFilterButton(h, textMode: false)) with { Key = "search-filter" },
                    ],
                };
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
        /// backspacing out) and on Esc — both handing focus back — and on a blur while empty, without restoring focus.</summary>
        sealed class TableSearchField(TableHost host) : Component
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
                    [EditableText.PartRoot] = b => b with { OnRealized = n => post(() => hooks.FocusNode?.Invoke(n, false)) },
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

            static string Caps(string label, bool classic) => classic ? label.ToUpper(CultureInfo.CurrentUICulture) : label;

            /// <summary>The owning header brightens — except Index, the default order, which carries no indicator.</summary>
            static TextEl HLabel(string s, SortColumn col, SortSpec sort, bool classic) => new(Caps(s, classic))
            {
                Size = classic ? 11f : 12f, Weight = 600, CharSpacing = classic ? Design.Type.EyebrowTracking : 0f,
                Color = TableRules.HeaderActive(col, sort.Column, false) ? Tok.TextSecondary : Tok.TextTertiary,
                MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };

            static Element PlainHeader(string label, FlexJustify justify, bool classic) => new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true,
                Children =
                [
                    new TextEl(Caps(label, classic))
                    {
                        Size = classic ? 11f : 12f, Weight = 600, CharSpacing = classic ? Design.Type.EyebrowTracking : 0f,
                        Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };

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
                if (classic) text = text.ToUpper(CultureInfo.CurrentUICulture);
                UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut, text);
                UseTransition(AnimChannel.TranslateY, 4f, 0f, Expressive.Fast, Easing.SmoothOut, text);
                return new TextEl(text)
                {
                    Size = classic ? 11f : 12f, Weight = 600, CharSpacing = classic ? Design.Type.EyebrowTracking : 0f,
                    Color = TableRules.HeaderActive(SortColumn.Title, col, artistColumn) ? Tok.TextSecondary : Tok.TextTertiary,
                    MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                };
            }
        }

        // ══ 5. THE FLYOUT BUTTONS ═══════════════════════════════════════════════════════════════════════════════════

        /// <summary>One anchored overlay at a time per button: a second click closes it.</summary>
        static void ToggleOverlay(IOverlayService overlay, Ref<NodeHandle> anchor, Ref<OverlayHandle?> handle,
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

                List<MenuFlyoutItem> Items()
                {
                    var cfg = h.Cfg;
                    var items = new List<MenuFlyoutItem>(10);
                    if ((overflow & InlineCommand.Shuffle) != 0)
                        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Shuffle), Icons.Shuffle, true, h.Shuffle));
                    if ((overflow & InlineCommand.Sort) != 0)
                        items.Add(MenuFlyoutItem.SubMenu(Loc.Get("detail.sort.menu"), h.SortItems(), Icons.Sort));   // key from batch-loc WP-4.5-U2a.json; a literal so the build does not wait on the merge
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
                return ToolTip.Wrap(IconButton(Icons.More, false, Toggle, n => anchor.Value = n), Loc.Get(Strings.Common.More));
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

        /// <summary>The funnel (32×32, accent plate + corner count badge while any facet is on), or — in the context band —
        /// the plateless word whose accent ink stands in for plate and badge. Same flyout either way.</summary>
        sealed class TableFilterButton(TableHost host, bool textMode) : Component
        {
            static readonly TemplateParts s_badgeCorner = new()
            {
                [InfoBadge.PartRoot] = static b => b with
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = 1.5f, BorderColor = Tok.FillSolidBase,
                },
            };

            public override Element Render()
            {
                var h = host;
                var overlay = UseContext(Overlay.Service);
                var anchor = UseRef<NodeHandle>(default);
                var handle = UseRef<OverlayHandle?>(null);
                int activeCount = h._filters.Value.ActiveCount;
                bool active = activeCount > 0;
                void Toggle() => ToggleOverlay(overlay, anchor, handle, () => Embed.Comp(() => new TableFilterFlyout(h)),
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = true });

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

        /// <summary>The immediate-apply filter card: 368 wide, ≤ 620 tall with a ≤ 500 scroll region — "Search in" as ONE
        /// segmented row, the trait facets with label and control on the same line, the status checkboxes in a grid, four
        /// one-at-a-time disclosures (the opened one scrolls under the header once its sibling's collapse settles), and a
        /// footer whose Clear is disabled while nothing is set.</summary>
        sealed class TableFilterFlyout : Component
        {
            const float CardWidth = 368f, CardMaxHeight = 620f, ScrollMaxHeight = 500f, TraitControlWidth = 150f, RevealDelayMs = 170f;

            sealed class Section
            {
                public readonly Signal<bool> Open = new(false);
                public NodeHandle Node;
                public TemplateParts Parts = null!;
            }

            readonly TableHost _h;
            readonly Signal<int> _scope, _explicit, _video, _duration, _added, _tempo, _origin;
            readonly Signal<bool> _liked, _playable;
            readonly Section _durationSec = new(), _addedSec = new(), _tempoSec = new(), _originSec = new();
            readonly Section[] _sections;
            NodeHandle _scrollNode;

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
                _added = new((int)f.Added);
                _tempo = new((int)f.Tempo);
                _origin = new((int)f.Origin);
                _sections = [_durationSec, _addedSec, _tempoSec, _originSec];
                foreach (var s in _sections) s.Parts = DisclosureParts(s);
            }

            static TemplateParts DisclosureParts(Section s) => new()
            {
                [Expander.PartRoot] = r => r with { OnRealized = n => s.Node = n },
                [Expander.PartHeader] = static b => b with
                {
                    MinHeight = 42f, Padding = new Edges4(8f, 0f, 0f, 0f), Fill = ColorF.Transparent, BorderWidth = 0f, Corners = Radii.ControlAll,
                },
                [Expander.PartChevron] = static c => c with { Width = 28f, Height = 28f, Margin = new Edges4(8f, 0f, 4f, 0f) },
                [Expander.PartContent] = static c => c with
                {
                    Padding = new Edges4(10f, 2f, 8f, 10f), MinHeight = 0f, Fill = ColorF.Transparent, BorderWidth = 0f, Margin = default, Corners = default,
                },
            };

            int OpenSectionIndex()
            {
                for (int i = 0; i < _sections.Length; i++) if (_sections[i].Open.Value) return i;
                return -1;
            }

            void Set(FilterState next) => _h.SetFilters(next);
            FilterState Current => _h._filters.Peek();

            public override Element Render()
            {
                var current = _h._filters.Value;
                var caps = _h.CapsNow();
                ColorF accent = Tok.AccentTextPrimary;
                string status = current.ActiveCount == 0
                    ? Loc.Get(Strings.Detail.Filter.AllTracks)
                    : Strings.Detail.Filter.ActiveCount(current.ActiveCount.ToString(CultureInfo.CurrentCulture));

                // Expanding a section collapses its sibling, which slides everything below it: wait for that to settle,
                // then park the opened section's TOP edge under the card header (its body is still reflowing).
                UseTimeout(() =>
                {
                    int i = OpenSectionIndex();
                    if (i < 0) return;
                    ScrollIntoView.BringInto(Context, _scrollNode, _sections[i].Node, margin: Spacing.S, alignmentRatio: 0f, animate: true);
                }, RevealDelayMs, DepKey.From(OpenSectionIndex()));

                Element Group(string title, Element[] children) => new BoxEl
                {
                    Direction = 1, Gap = 8f, Padding = new Edges4(12f, 10f, 12f, 10f),
                    Children = [Design.Type.Eyebrow(title) with { Color = Tok.TextTertiary, Margin = new Edges4(4f, 0f, 4f, 8f) }, .. children],
                };

                Element Trait(string glyph, string label, Signal<int> signal, Action<int> changed) => new BoxEl
                {
                    Direction = 0, Gap = 9f, MinHeight = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(4f, 0f, 0f, 0f),
                    Children =
                    [
                        Icon(glyph, 16f, Tok.TextTertiary),
                        new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Width = TraitControlWidth, Shrink = 0f,
                            Children =
                            [
                                Segmented.Create(
                                [
                                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.All)),
                                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.Hide)),
                                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.Only)),
                                ], signal, changed),
                            ],
                        },
                    ],
                };

                Element Status(string glyph, string label, Signal<bool> signal, FilterFlags flag) => new BoxEl
                {
                    Direction = 0, Gap = 8f, MinHeight = 32f, Padding = new Edges4(9f, 1f, 7f, 1f), AlignItems = FlexAlign.Center,
                    Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BrushTransitionMs = Design.Motion.Faster,
                    Children =
                    [
                        Icon(glyph, 15f, Tok.TextTertiary),
                        CheckBox.Create(label, signal, on =>
                        {
                            var f = Current;
                            Set(f with { Flags = on ? f.Flags | flag : f.Flags & ~flag });
                        }, style: CheckBox.DefaultStyle with { MinWidth = 0f, MinHeight = 30f, FontSize = 13f, ContentGap = 7f }),
                    ],
                };

                Element Disclosure(string glyph, string label, Signal<int> value, Section section, string[] labels, Action<int> changed)
                {
                    var header = new BoxEl
                    {
                        Direction = 0, Gap = 9f, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            Icon(glyph, 16f, Tok.TextTertiary),
                            new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                            new BoxEl
                            {
                                Grow = 1f, AlignItems = FlexAlign.End,
                                Children = [new TextEl(Prop.Of(() => labels[Math.Clamp(value.Value, 0, labels.Length - 1)])) { Size = 12f, Color = Tok.TextTertiary }],
                            },
                        ],
                    };
                    var sections = _sections;
                    return Embed.Comp(new Expander.ExpanderSlots(header, RadioButtons.Create(labels, value, changed, maxColumns: 2), section.Parts),
                        () => new Expander
                        {
                            IsExpanded = section.Open,
                            OnChange = open =>
                            {
                                if (!open) return;
                                foreach (var s in sections) if (!ReferenceEquals(s, section)) s.Open.Value = false;
                            },
                        });
                }

                void ClearAll()
                {
                    _scope.Value = 0; _explicit.Value = 0; _video.Value = 0; _liked.Value = false; _playable.Value = false;
                    _duration.Value = 0; _added.Value = 0; _tempo.Value = 0; _origin.Value = 0;
                    foreach (var s in _sections) s.Open.Value = false;
                    Set(FilterState.Default);
                }

                var content = new List<Element>(3)
                {
                    Trait(Icons.Important, Loc.Get(Strings.Detail.Filter.ExplicitContent), _explicit, v => Set(Current with { ExplicitMode = (TraitMode)v })),
                    Trait(Icons.Movie, Loc.Get(Strings.Detail.Filter.VideoTracks), _video, v => Set(Current with { VideoMode = (TraitMode)v })),
                };
                var status2 = new List<Element>(2);
                if (caps.HasLibrary || current.LikedOnly)
                    status2.Add(Status(Icons.Heart, Loc.Get(Strings.Detail.Filter.LikedOnly), _liked, FilterFlags.LikedOnly));
                if (caps.HasUnavailable || current.PlayableOnly)
                    status2.Add(Status(Icons.Accept, Loc.Get(Strings.Detail.Filter.PlayableOnly), _playable, FilterFlags.PlayableOnly));
                if (status2.Count > 0)
                    content.Add(new GridEl
                    {
                        Columns = status2.Count > 1 ? [TrackSize.Star(), TrackSize.Star()] : [TrackSize.Star()],
                        ColGap = 5f, RowGap = 5f, Children = status2.ToArray(),
                    });

                var more = new List<Element>(4)
                {
                    Disclosure(Icons.Clock, Loc.Get(Strings.Detail.Filter.Duration), _duration, _durationSec,
                        [Loc.Get(Strings.Detail.Filter.AnyDuration), Loc.Get(Strings.Detail.Filter.UnderThree),
                         Loc.Get(Strings.Detail.Filter.ThreeToFive), Loc.Get(Strings.Detail.Filter.OverFive)],
                        v => Set(Current with { Duration = (DurationRange)v })),
                };
                if (caps.HasDateAdded || current.Added != AddedRange.Any)
                    more.Add(Disclosure(Icons.Calendar, Loc.Get(Strings.Detail.Filter.DateAdded), _added, _addedSec,
                        [Loc.Get(Strings.Detail.Filter.AnyTime), Loc.Get(Strings.Detail.Filter.LastSevenDays), Loc.Get(Strings.Detail.Filter.LastThirtyDays),
                         Loc.Get(Strings.Detail.Filter.LastSixMonths), Loc.Get(Strings.Detail.Filter.LastYear)],
                        // WithAddedRange, never a bare `with`: a preset must retire the rail's explicit window.
                        v => Set(Current.WithAddedRange((AddedRange)v))));
                if (caps.HasTempo || current.Tempo != TempoBand.Any)
                    more.Add(Disclosure(Icons.MusicNote, Loc.Get(Strings.Detail.Filter.Tempo), _tempo, _tempoSec,
                        [Loc.Get(Strings.Detail.Filter.AnyTempo), Loc.Get(Strings.Detail.Filter.TempoUnder90), Loc.Get(Strings.Detail.Filter.Tempo90To119),
                         Loc.Get(Strings.Detail.Filter.Tempo120To139), Loc.Get(Strings.Detail.Filter.Tempo140Up)],
                        v => Set(Current with { Tempo = (TempoBand)v })));
                if (caps.HasMixedOrigin || current.Origin != OriginFilter.Any)
                    more.Add(Disclosure(Icons.MusicNote, Loc.Get(Strings.Detail.Filter.Source), _origin, _originSec,
                        [Loc.Get(Strings.Detail.Filter.AnySource), Loc.Get(Strings.Detail.Filter.Streamed), Loc.Get(Strings.Detail.Filter.Local)],
                        v => Set(Current with { Origin = (OriginFilter)v })));

                static BoxEl Divider(float inset) => new() { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(inset, 0f, inset, 0f) };
                Element scope = Segmented.Create(
                [
                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.Everything)),
                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.TitleOnly)),
                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.ArtistOnly)),
                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.AlbumOnly)),
                ], _scope, v => Set(Current with { SearchScope = (SearchScope)v }));

                return new BoxEl
                {
                    Direction = 1, Width = CardWidth, MinWidth = 320f, MaxWidth = CardWidth, MaxHeight = CardMaxHeight, MinHeight = 0f,
                    ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = 11f, Padding = new Edges4(14f, 12f, 14f, 10f),
                            Children =
                            [
                                new BoxEl
                                {
                                    Width = 34f, Height = 34f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                    Corners = CornerRadius4.All(10f), Fill = accent with { A = 0.14f },
                                    Children = [Icon(Icons.Filter, 17f, accent)],
                                },
                                new BoxEl
                                {
                                    Direction = 1, Gap = 1f, Grow = 1f,
                                    Children =
                                    [
                                        new TextEl(Loc.Get(Strings.Detail.Filter.Title)) { Size = 15f, Weight = 650, Color = Tok.TextPrimary },
                                        new TextEl(status) { Size = 12f, Color = Tok.TextSecondary },
                                    ],
                                },
                            ],
                        },
                        Divider(8f),
                        new ScrollEl
                        {
                            ContentSized = true, Grow = 1f, MinHeight = 0f, MaxHeight = ScrollMaxHeight,
                            OnRealized = n => _scrollNode = n,
                            Content = new BoxEl
                            {
                                Direction = 1, MinWidth = 0f,
                                Children =
                                [
                                    Group(Loc.Get(Strings.Detail.Filter.SearchIn), [scope]),
                                    Divider(0f),
                                    Group(Loc.Get(Strings.Detail.Filter.Content), content.ToArray()),
                                    Divider(0f),
                                    Group(Loc.Get(Strings.Detail.Filter.MoreFilters), more.ToArray()),
                                ],
                            },
                        },
                        Divider(8f),
                        new BoxEl
                        {
                            Direction = 0, Height = 46f, Padding = new Edges4(14f, 6f, 10f, 6f), AlignItems = FlexAlign.Center,
                            Children =
                            [
                                new TextEl(current.IsDefault ? Loc.Get(Strings.Detail.Filter.NoFiltersApplied) : status)
                                { Size = 11f, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                Button.Standard(Loc.Get(Strings.Detail.Filter.ClearFilters), ClearAll, isEnabled: !current.IsDefault),
                            ],
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

        /// <summary>Thumbs · count · Play (+ Play next · Add to queue · Like · Select all at fit ≤ 1) · "…" · ✕, over the
        /// registered verbs; every verb exits selection after it runs. Fit 0 labels, 1 glyphs, 2 essentials.</summary>
        Element SelectionCommands(int fit)
        {
            _ = _selection.Version.Value;
            int count = _selectedCount?.Value ?? 0;
            if (count <= 0) { _selectionPrevCount = 0; return new BoxEl(); }
            bool wasVisible = _selectionPrevCount > 0;
            _selectionPrevCount = count;
            var tracks = SelectedTracks();
            var ctx = new ActionContext(ActionTarget.ForTracks(tracks, HostFor()), Actions.Services);

            var kids = new List<Element>(12);
            if (fit <= 1 && Thumbs(tracks) is { Length: > 0 } thumbs)
                kids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = thumbs });
            kids.Add(new BoxEl
            {
                Key = "selection-count:" + count,
                Animate = wasVisible ? MotionRecipes.TextSwap : MotionRecipes.TextSwap with { Enter = default },
                MinWidth = fit == 2 ? 66f : float.NaN,
                Children = [new TextEl(Strings.Detail.SelectedCount(count)) { Size = 12f, Weight = 650, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            });
            kids.Add(SelectionDivider());
            if (VerbCommand(ActionId.Play, in ctx, fit) is { } play) kids.Add(play);
            if (fit <= 1)
            {
                if (VerbCommand(ActionId.PlayNext, in ctx, fit) is { } next) kids.Add(next);
                if (VerbCommand(ActionId.AddToQueue, in ctx, fit) is { } queue) kids.Add(queue);
                if (VerbCommand(ActionId.ToggleLike, in ctx, fit) is { } like) kids.Add(like);
                kids.Add(SelectionDivider());
                kids.Add(Command(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, _selectAllTracks, null, true));
            }
            kids.Add(Embed.Comp(() => new TableSelectionMore(this, fit)) with { Key = "selection-more:" + fit });
            kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
            kids.Add(ToolTip.Wrap(GlyphButton(Icons.Cancel, _exitSelection, null, null, true), Loc.Get(Strings.Detail.ClearSelection)));
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 3f, Grow = 1f, MinWidth = 0f, ClipToBounds = true,
                Children = kids.ToArray(),
            };
        }

        void SelectAllTracks()
        {
            int n = _rowItems?.Count.Peek() ?? 0;
            if (n <= 0) return;
            int start = TrackStart;
            _selection.DeselectAll();
            _selection.SelectRange(start, start + n - 1);
        }

        /// <summary>Up to three stacked covers, de-duplicated by image (an album's tracks share one).</summary>
        static Element[] Thumbs(List<Track> tracks)
        {
            var result = new List<Element>(3);
            Span<int> seen = stackalloc int[3];
            for (int i = 0; i < tracks.Count && result.Count < 3; i++)
            {
                var t = tracks[i];
                var image = t.ImageId.IsEmpty && t.Album.IsValid ? t.Album.ImageId : t.ImageId;
                int key = image.IsEmpty ? -t.Slot : image.GetHashCode();
                bool dup = false;
                for (int j = 0; j < result.Count; j++) dup |= seen[j] == key;
                if (dup) continue;
                seen[result.Count] = key;
                result.Add(new BoxEl
                {
                    Width = 28f, Height = 28f, Shrink = 0f, Corners = CornerRadius4.All(5f), ClipToBounds = true,
                    Margin = new Edges4(result.Count == 0 ? 0f : -11f, 0f, 0f, 0f),
                    BorderWidth = 2f, BorderColor = Tok.FillCardSecondary,
                    Children = [Controls.Artwork(Controls.ArtUrl(image), 28f, 28f, 5f, decodePx: 56)],
                });
            }
            return result.ToArray();
        }

        Element? VerbCommand(ActionId id, in ActionContext ctx, int fit)
        {
            if (AppActions.Find(id) is not { } action) return null;
            var c = ctx;
            bool enabled = action.EnabledFor(in c);
            var icon = ActionIcons.Resolve(action.IconKey, action.CheckedFor(in c));
            var exit = _exitSelection;
            Action invoke = enabled ? () => { action.Execute(c); exit(); } : static () => { };
            return Command(icon.Glyph ?? "", action.Label(c), fit, invoke, icon.Font, enabled);
        }

        static Element Command(string glyph, string label, int fit, Action invoke, string? font, bool enabled)
            => fit == 0
                ? new BoxEl
                {
                    Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = 6f, Padding = new Edges4(9f, 0f, 10f, 0f),
                    Corners = Radii.ControlAll, IsEnabled = enabled, Focusable = enabled, Role = AutomationRole.Button, OnClick = invoke,
                    Children =
                    [
                        Icon(glyph, 14f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font),
                        new TextEl(label) { Size = 12f, Weight = 600, Color = enabled ? Tok.TextSecondary : Tok.TextDisabled },
                    ],
                }.Interactive(Interaction.Subtle)
                : ToolTip.Wrap(GlyphButton(glyph, invoke, null, font, enabled), label);

        static BoxEl GlyphButton(string glyph, Action invoke, Action<NodeHandle>? realized, string? font, bool enabled) => new BoxEl
        {
            Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            IsEnabled = enabled, Focusable = enabled, Role = AutomationRole.Button, OnClick = invoke, OnRealized = realized,
            Children = [Icon(glyph, 13f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font)],
        }.Interactive(Interaction.Subtle);

        static Element SelectionDivider() => new BoxEl
        {
            Width = 1f, Height = 20f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault), Margin = new Edges4(4f, 0f, 4f, 0f),
        };

        /// <summary>The selection "…": at the essentials fit the transport verbs move here, then the track menu's rows for the
        /// selection, then Select all. Built at OPEN, so it names the selection as it stands when clicked.</summary>
        sealed class TableSelectionMore(TableHost host, int fit) : Component
        {
            public override Element Render()
            {
                var h = host;
                var overlay = UseContext(Overlay.Service);
                var anchor = UseRef<NodeHandle>(default);
                var handle = UseRef<OverlayHandle?>(null);

                List<MenuFlyoutItem> Items()
                {
                    var tracks = h.SelectedTracks();
                    var hostRows = h.HostFor();
                    var ctx = new ActionContext(ActionTarget.ForTracks(tracks, hostRows), Actions.Services);
                    var exit = h._exitSelection;
                    var items = new List<MenuFlyoutItem>(16);
                    if (fit >= 2)
                    {
                        if (Actions.Menu.Row(ActionId.PlayNext, in ctx, exit) is { } next) items.Add(next);
                        if (Actions.Menu.Row(ActionId.AddToQueue, in ctx, exit) is { } queue) items.Add(queue);
                        if (Actions.Menu.Row(ActionId.ToggleLike, in ctx, exit) is { } like) items.Add(like);
                        if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
                    }
                    if (Track.Menu(tracks, new MenuOptions(Host: hostRows, ShowGoToAlbum: false, PickerOverlay: overlay)) is { } model)
                        foreach (var row in model.Rows) items.Add(WithExit(row, exit));
                    items.Add(MenuFlyoutItem.Separator);
                    items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.SelectAll), Icons.Accept, true, h._selectAllTracks));
                    return items;
                }

                void Toggle() => ToggleOverlay(overlay, anchor, handle, () => MenuFlyout.Create(Items(), () => handle.Value?.Close()), MenuPopup);
                return ToolTip.Wrap(GlyphButton(Icons.More, Toggle, n => anchor.Value = n, null, true), Loc.Get(Strings.Common.More));
            }

            static MenuFlyoutItem WithExit(MenuFlyoutItem item, Action exit)
            {
                if (item.Kind == MenuItemKind.Separator) return item;
                if (item.Kind == MenuItemKind.SubMenu && item.SubItems is { } nested)
                {
                    var mapped = new MenuFlyoutItem[nested.Count];
                    for (int i = 0; i < nested.Count; i++) mapped[i] = WithExit(nested[i], exit);
                    return item with { SubItems = mapped };
                }
                if (item.Invoke is not { } invoke) return item;
                return item with { Invoke = () => { invoke(); exit(); } };
            }
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
    static TextEl ShimLabel(string text, bool active) => new(text)
    {
        Size = 12f, Weight = 600, Color = active ? Tok.TextSecondary : Tok.TextTertiary,
        MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };
}
