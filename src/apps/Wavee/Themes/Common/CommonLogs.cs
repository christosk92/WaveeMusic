// ── Themes/Common/CommonLogs.cs — the Common `Pages.Logs` block (themes plan 6.5, T11c) ──────────────────────────────────────
//
// From `Screens\LogsPage.UI.cs`: the log viewer page (route `logs`), a full page of its own that replaces the Settings › Logs tab's embedded
// viewer. It re-hosts the working viewer (the `LogView` pure model, the keyed session / category combos, the measured virtualised list, the row
// anatomy, the 750 ms live poll) under one header, one toolbar and a list card with a column header. One copy of the view for every theme
// (Classic registers it too).
//
// THE RULE THIS FILE KEEPS: it lays out and binds, it never decides. The filter/group/cap is `Diagnostics.LogView`, the capture levels are
// `LogCapturePolicy`, the strip's labels / the picker's caption / where a selection lands after a re-list are `LogsPage.Rules`
// (`LogsPage.cs`, tested); the session model (the folder walk, the load of a past session, the labels, the export and the verbs) is the core
// host behind `ILogsHost` (`Screens\LogsPage.Host.cs`).
//
// FRAME: the page frame the old sidebar customizer used (deleted in the sidebar rework): a header (crumbs over the title, the picker cluster),
// Esc → back (the host's one exit). The capture level and file level combos left the viewer (they are Settings › Privacy & diagnostics › Logs ›
// Detail level); Verbose stays here as a checkable item of the "…" menu.
//
// TIERS (themes plan 3.2, TC10): the viewer reads `ctx.Tier()` through `CommonTierComponent` into a `CommonLogsLayout` (Rules\Tiers.cs). Compact draws a row as two lines
// (time, level and count over category and message; no column header: the sort lives in the "..." menu), the toolbar wraps with 40-DIP targets, the ComboBoxes become
// stock combos at their 40-DIP field, the crumbs at their Large (40 x 40) size; Compact and Small put the picker cluster under the title; Short at those widths drops the crumbs and the caption and scrolls the toolbar as one line; every
// tier is one column capped at the page's max width (the Huge fill rule).
//
// NOTHING HERE READS A CLOCK PER FRAME (ch 27 N12): the live tail polls at 750 ms and bumps only when `Log.Version` moved (and not at all on a
// past session). The sessions are re-listed once on mount and once per page activation (a keep-alive return, or the window coming back) —
// `UseActivation` fires on TRANSITIONS only, which is why the mount walk is its own effect.
//
// PROPS FREEZE AT MOUNT: every control whose interesting fields freeze (the session / category combos, the virtualised list) is REMOUNTED
// through a `Key` that changes exactly when its frozen input does, and every write that tears down the node that was clicked runs through
// `UsePost` so the click finishes first. No signal is written in render. A tier crossing is NOT such an input (plan 3.3): the list keeps its
// node, its scroll offset and its expanded row; its rows read the row form from the tier facts and re-render in place (one line <-> two lines,
// the height revealed), and the column header folds to no height and back with a height reveal.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using LogLevelBucket = Wavee.Diagnostics.LogLevelBucket;
using LogView = Wavee.Diagnostics.LogView;
using LogViewQuery = Wavee.Diagnostics.LogViewQuery;
using LogViewResult = Wavee.Diagnostics.LogViewResult;
using LogViewRow = Wavee.Diagnostics.LogViewRow;

namespace Wavee.Themes.Common;

/// <summary>The Common log viewer.</summary>
internal static class CommonLogs
{
    /// <summary>Registers `Pages.Logs`.</summary>
    public static void Register(ViewRegistryBuilder views)
        => views.Add(ViewKeys.LogsPage, Template, "common.logs.page");

    /// <summary>The registered template of <see cref="ViewKeys.LogsPage"/>: the viewer is a stateful component the slot embeds (its signals,
    /// effects and menu are the old page component's, moved).</summary>
    public static Element Template(LogsPageProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new LogsPageView());

    static TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);

    /// <summary>The viewer. <see cref="_rows"/> and <see cref="_categories"/> are FIELDS, refreshed at the top of every
    /// render, so a command built by an OLDER render (the toolbar does not remount for a plain search edit) still reads the
    /// CURRENT rows when clicked.</summary>
    sealed class LogsPageView : CommonTierComponent
    {
        readonly Signal<string> _search = new("");
        readonly Signal<int> _level = new((int)LogLevelBucket.All);
        readonly Signal<int> _category = new(0);
        readonly Signal<int> _newestFirst = new(1);
        readonly Signal<int> _groupRepeats = new(1);
        readonly Signal<int> _wrap = new(0);
        readonly Signal<int> _visibleLimit = new(LogView.PageRows);
        readonly Signal<long> _expandedSeq = new(-1);

        ILogsHost _host = null!;
        CommonLogsLayout _layout;
        IReadSignal<ThemeFacts>? _facts;   // the tier facts, read by the rows themselves (a crossing re-renders them in place)
        LogViewRow[] _rows = [];
        string[] _categories = [];
        readonly ItemsViewController _listCtrl = new();
        IOverlayService? _overlay;
        OverlayHandle? _menu;
        NodeHandle _moreAnchor = NodeHandle.Null;
        Action<KeyEventArgs>? _onKey;
        Action<int>? _onCrumb;
        Action? _openFolder;
        Action? _exportSession;

        public override Element Render()
        {
            var host = _host = UseProps<LogsPageProps>().Host;
            var layout = _layout = CommonLogsLayout.For(ReadTier());
            _facts = UseContext(ThemeFactsContext.Signal);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var lastVersion = UseRef(-1L);
            _overlay = UseContext(Overlay.Service);

            // Mount: the first walk. Activation: every later one (a keep-alive return re-reads the folder; it fires on
            // TRANSITIONS only, so it never doubles the mount walk). The footer's capture caption reads levels the Detail
            // level card can change while this page is parked — a bump re-renders it.
            UseEffect(() => host.RefreshSessions(post), DepKey.Empty);
            UseActivation(onActivated: () =>
            {
                host.RefreshSessions(post, force: true);
                host.Bump();
            });
            UseSignalEffect(() =>
            {
                _ = host.Session.Value;
                _expandedSeq.Value = -1;
                _visibleLimit.Value = LogView.PageRows;
                host.EnsureSessionLoaded(post);
            });
            // The live tail (session 0 only; auto-pauses while parked): bumps only when the log actually moved.
            UseInterval(() =>
            {
                long v = host.LogVersion;
                if (v == lastVersion.Value) return;
                lastVersion.Value = v;
                host.Bump();
            }, CommonLogsRules.PollMs, enabled: host.Session.Value == 0);
            // ONE measured layout for the list's life: a crossing re-measures its rows in place (the list is not remounted, so a fresh layout would
            // never reach it); the mount's estimate only seeds rows never measured yet.
            var logLayout = UseMemo(() => new MeasuredStackVirtualLayout(estimatedExtent: layout.RowExtent), 0);

            _ = host.Refresh.Value; _ = _expandedSeq.Value; _ = _search.Value; _ = _level.Value; _ = _category.Value;
            _ = _newestFirst.Value; _ = _groupRepeats.Value; _ = _wrap.Value; _ = _visibleLimit.Value; _ = host.SessionsRev.Value;

            bool live = LogsPage.Rules.IsLive(host.Session.Value);
            WaveeLogEntry[]? entries = host.EntriesFor(live, out var past);
            _categories = entries is null ? [] : LogView.Categories(entries);
            // The category clamp lives in an effect (a signal write in render is the backwards-write tripwire).
            UseEffect(() => { if (_category.Peek() > _categories.Length) _category.Value = 0; }, _categories.Length);

            var query = Query();
            var result = entries is null ? LogViewResult.Empty : LogView.Build(entries, query);
            _rows = result.Rows;

            // The page is a row holding ONE column, so the column can stop growing at the tier's max width and keep the page's left edge (the Huge fill rule).
            // Every child keyed. The column header belongs to the one-line rows: it is ALWAYS mounted and the two-line form folds it to no height (a
            // height reveal either way), so a crossing never inserts it above the list (no jump) nor remounts its sort button.
            bool header = !layout.TwoLineRows;
            var card = new List<Element>(5)
            {
                ColumnHeader(post, folded: !header) with
                {
                    Key = "logs:colhead", Animate = Design.Reveal.Resize, ClipToBounds = true,
                    Height = header ? CommonLogsRules.ColumnHeaderHeight : 0f, HitTestVisible = header,
                },
                Divider() with { Key = "logs:colhead-rule", Animate = Design.Reveal.Resize, Height = header ? 1f : 0f },
            };
            card.Add(LogBody(entries, result, query, logLayout));
            card.Add(Divider() with { Key = "logs:footer-rule" });
            card.Add(Footer(result, live, entries, past) with { Key = "logs:footer" });
            return new BoxEl
            {
                Key = "logs-page", Grow = 1f, Shrink = 1f, Direction = 0, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                OnKeyDown = _onKey ??= OnPageKey,
                Children =
                [
                    new BoxEl
                    {
                        Key = "logs-column", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, MaxWidth = layout.MaxWidth,
                        Children =
                        [
                            HeaderBar(),
                            Divider(),
                            Toolbar(result, live, hooks, post),
                            new BoxEl
                            {
                                Margin = new Edges4(layout.Gutter, 0f, layout.Gutter, Spacing.L),
                                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Corners = CornerRadius4.All(Radii.Card),
                                Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                                Children = card.ToArray(),
                            },
                        ],
                    },
                ],
            };
        }

        static BoxEl Divider() => new() { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault };

        /// <summary>Esc leaves (the one exit, same as Back) when nothing inside handled it first (an open combo, a filter
        /// being cleared) — the same rule as the customizer: only with focus inside the page.</summary>
        void OnPageKey(KeyEventArgs e)
        {
            if (e.Handled || e.KeyCode != Keys.Escape || !_host.IsActive) return;
            e.Handled = true;
            _host.GoBack();
        }

        LogViewQuery Query() => new((LogLevelBucket)_level.Value, CurrentCategory(), _search.Value,
            _newestFirst.Value != 0, _groupRepeats.Value != 0, _visibleLimit.Value);

        string? CurrentCategory()
        {
            int idx = _category.Value;
            return idx <= 0 || idx > _categories.Length ? null : _categories[idx - 1];
        }

        // ── the header: crumbs over the title lane · [picker caption] · session combo · Open folder ─────────────────

        /// <summary>The child-page head the app uses everywhere a page sits under a parent (Profile lists, the artist
        /// discography): a <c>BreadcrumbBar</c> whose earlier crumbs navigate, over the page hero — no back arrow. The title
        /// lane is the only flexible child (Grow 1 · Basis 0 · Shrink 1 · MinWidth 0), so under pressure the title
        /// ellipsizes and the picker cluster holds its width.</summary>
        Element HeaderBar()
        {
            var host = _host;
            var layout = _layout;
            // Every child is keyed: the picker caption comes and goes, and a positional diff would otherwise slide the
            // combo's remount key onto the wrong sibling.
            var title = R.Type.PageHero(Loc.Get(Strings.Logs.Title)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
            // Settings › Privacy & diagnostics › Logs. The first crumb opens Settings on its remembered tab; the
            // second lands on the tab this page belongs to; the last is this page and does nothing. At a hit target the crumbs are the Large ones (40 x 40); the bare
            // Short header drops them (the Back of the title bar stays).
            Element[] lane = !layout.ShowCrumbs
                ? [title]
                : [BreadcrumbBar.Create([Loc.Get(Strings.Nav.Settings), Loc.Get(Strings.Settings.Tabs.Privacy), Loc.Get(Strings.Logs.Title)],
                    _onCrumb ??= host.OpenCrumb, size: layout.Target > 0f ? ControlSize.Large : ControlSize.Medium), title];
            BoxEl titleLane = new()
            {
                Key = "logs:title", Direction = 1, MinWidth = 0f, Justify = FlexJustify.Center, Gap = Spacing.XXS, Children = lane,
            };
            // Beside the cluster the title lane is the only flexible child (Grow 1 · Basis 0 · Shrink 1 · MinWidth 0), so under
            // pressure the title ellipsizes and the picker cluster holds its width; over it (Compact, Small) it is just a row.
            if (!layout.StackHeader || layout.BareHeader) titleLane = titleLane with { Grow = 1f, Basis = 0f, Shrink = 1f };

            // The bare Short header has no room for the selection's label at a hit target: the picker is an icon button there.
            var picker = CommonTouch.Picker(host.SessionLabels(), host.Session, CommonLogsRules.SessionComboWidth, layout.Target, _ => _expandedSeq.Value = -1,
                    layout.BareHeader ? Icons.Clock : null)
                with { Key = "logs:session:" + host.SessionsRev.Value.ToString(CultureInfo.InvariantCulture) };
            // Short at a narrow width the head gives up its top room too (the page is 210 tall or less).
            var padding = new Edges4(layout.Gutter, layout.BareHeader ? Spacing.S : Spacing.L, layout.Gutter, Spacing.S);

            // Short at a narrow width: the title and the picker on one line; Open folder moves into the "..." menu.
            if (layout.BareHeader)
                return new BoxEl
                {
                    Key = "logs-header", Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Padding = padding,
                    Children = [titleLane, picker],
                };

            var cluster = new List<Element>(3);
            if (host.PickerCaption() is { } caption)
                cluster.Add(R.Type.MicroMeta(caption) with
                {
                    Key = "logs:caption", Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    MaxWidth = CommonLogsRules.CaptionMaxWidth, Shrink = 1f, MinWidth = 0f,
                });
            cluster.Add(picker);
            cluster.Add((layout.Target > 0f
                ? CommonTouch.Button(Loc.Get(Strings.Logs.OpenFolder), _openFolder ??= host.OpenLogFolder, layout.Target, glyph: Icons.Folder)
                : Button.Create(Loc.Get(Strings.Logs.OpenFolder), _openFolder ??= host.OpenLogFolder, ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.Folder))
                with { Key = "logs:folder", Shrink = 0f });

            if (layout.StackHeader)
                return new BoxEl
                {
                    Key = "logs-header", Direction = 1, Shrink = 0f, Gap = Spacing.S, Padding = padding,
                    Children =
                    [
                        titleLane,
                        new BoxEl { Key = "logs:cluster", Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = cluster.ToArray() },
                    ],
                };

            // The page gutter and the hero's top room, as Profile.Lists.Page's head; the picker cluster sits on the hero's
            // baseline row, so it centres against the two-line title lane.
            cluster.Insert(0, titleLane);
            return new BoxEl
            {
                Key = "logs-header", Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Padding = padding,
                Children = cluster.ToArray(),
            };
        }

        // ── the toolbar: filter · level strip (counts inside) · categories · … · copy · export · more ─────────────────

        Element Toolbar(LogViewResult result, bool live, InputHooks hooks, Action<Action> post)
        {
            var host = _host;
            var layout = _layout;
            float target = layout.Target;
            var catLabels = new string[_categories.Length + 1];
            catLabels[0] = Loc.Get(Strings.Logs.AllCategories);
            Array.Copy(_categories, 0, catLabels, 1, _categories.Length);

            // Rebuilt every render: Segmented re-pushes its props, so the labels follow the live counts with no remount.
            var texts = LogsPage.Rules.LevelLabels(Loc.Get(Strings.Logs.LevelAll), Loc.Get(Strings.Logs.LevelInfo),
                Loc.Get(Strings.Logs.LevelWarnings), Loc.Get(Strings.Logs.LevelErrors), result.WarningCount, result.ErrorCount);
            var levelItems = new SegmentedItem[texts.Length];
            for (int i = 0; i < texts.Length; i++) levelItems[i] = new SegmentedItem(texts[i]);

            string exportTip = live
                ? Loc.Get(Strings.Logs.ExportSession) + "\n" + Loc.Get(Strings.Logs.ExportSessionLive)
                : Loc.Get(Strings.Logs.ExportSession);

            // Short: one scrolling line (a wrapping block would take the page); everywhere else the filter grows and the block wraps.
            bool oneLine = layout.OneLineToolbar;
            var kids = new List<Element>(5)
            {
                AutoSuggestBox.Create(Array.Empty<string>(), placeholder: Loc.Get(Strings.Logs.FilterPlaceholder),
                    width: oneLine ? CommonLogsRules.ShortFilterWidth : CommonLogsRules.FilterWidth,
                    grow: oneLine ? 0f : 1f, maxFillWidth: CommonLogsRules.FilterMaxFill, text: _search, onChange: q => _search.Value = q, onQuerySubmitted: q => _search.Value = q,
                    minHeight: target > 0f ? target : CommonLogsRules.FilterMinHeight, cornerRadius: Radii.Control),
                CommonTouch.Segmented(levelItems, _level, target),
                CommonTouch.Picker(catLabels, _category, CommonLogsRules.CategoryComboWidth, target)
                    with { Key = "logs:cat:" + host.Session.Value.ToString(CultureInfo.InvariantCulture) + ":" + _categories.Length.ToString(CultureInfo.InvariantCulture) },
            };
            if (!oneLine) kids.Add(new BoxEl { Grow = 1f });
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    ToolTip.Wrap(CommonTouch.IconButton(Icons.Copy, () => hooks.Clipboard?.SetText(LogView.CopyText(_rows)), target) with { Shrink = 0f },
                        Loc.Get(Strings.Logs.CopyVisible)),
                    ToolTip.Wrap(CommonTouch.IconButton(Icons.Download, _exportSession ??= host.ExportSession, target) with { Shrink = 0f }, exportTip),
                    ToolTip.Wrap(CommonTouch.IconButton(Icons.More, () => OpenMore(post), target) with { Shrink = 0f, OnRealized = h => _moreAnchor = h },
                        Loc.Get(Strings.Logs.More)),
                ],
            });

            var padding = new Edges4(layout.Gutter, oneLine ? Spacing.S : Spacing.L, layout.Gutter, Spacing.S);
            if (oneLine)
                return new BoxEl
                {
                    Key = "logs-toolbar", Direction = 0, Shrink = 0f, Padding = padding,
                    Children =
                    [
                        FluentGpu.Dsl.Ui.ScrollView(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = kids.ToArray() }, horizontal: true) with
                        {
                            Grow = 1f, Basis = 0f, MinWidth = 0f, Height = CommonLogsRules.ToolbarShortHeight, SuppressScrollBar = true, AutoEdgeFade = true,
                        },
                    ],
                };
            return new BoxEl
            {
                Key = "logs-toolbar", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true, Shrink = 0f, Padding = padding,
                Children = kids.ToArray(),
            };
        }

        /// <summary>The "…" menu, built AT OPEN TIME so its checks and its enabled states are always the live state. Every
        /// write is posted: the menu closes and the keyed list remounts under the click.</summary>
        void OpenMore(Action<Action> post)
        {
            if (_overlay is not { } overlay) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }

            var host = _host;
            bool newest = _newestFirst.Peek() != 0, group = _groupRepeats.Peek() != 0, wrap = _wrap.Peek() != 0;
            bool verbose = host.Verbose;
            bool canClear = LogView.CanClear(host.Session.Peek());
            var items = new List<MenuFlyoutItem>(8)
            {
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.NewestFirst), newest, () => post(() => { _newestFirst.Value = newest ? 0 : 1; _expandedSeq.Value = -1; })),
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.GroupRepeats), group, () => post(() => { _groupRepeats.Value = group ? 0 : 1; _expandedSeq.Value = -1; })),
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.WrapLines), wrap, () => post(() => _wrap.Value = wrap ? 0 : 1)),
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.Verbose), verbose, () => post(() =>
                {
                    host.SetVerbose(!verbose);
                    host.Bump();
                })),
                MenuFlyoutItem.Separator,
                new(Loc.Get(Strings.Logs.ClearView), Icons.ClearText, canClear, () => Controls.Confirm(overlay,
                    Loc.Get(Strings.Logs.ClearView), Loc.Get(Strings.Logs.ClearViewBody), Loc.Get(Strings.Logs.ClearView),
                    () => { host.ClearRing(); host.Bump(); })),
                new(Loc.Get(Strings.Report.ThisSession), Icons.Attention, true, host.ReportThisSession),
            };
            // The bare Short header has no Open folder button: it lives here.
            if (_layout.BareHeader) items.Add(new(Loc.Get(Strings.Logs.OpenFolder), Icons.Folder, true, host.OpenLogFolder));
            // Same anchored-flyout shape as Shell.UI.cs's history menu and `ClassicSettings.VideoOverrides.cs`'s manager menu.
            var handle = overlay.Open(() => _moreAnchor, () => MenuFlyout.Create(items, () => _menu?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
        }

        // ── the list card: column header · body · footer ────────────────────────────────────────────────────────────

        /// <summary>The same padding, gap and column widths as <see cref="LogRow"/>; only Time is interactive (flips the sort). <paramref name="folded"/> (the two-line
        /// rows, which have no column header): the SAME nodes, folded to no height by the caller, whose columns give way to the card's width (shrink to it, never
        /// past it: a folded header lays nothing out beyond the card) and whose sort button is no target (no role, no focus, no input) until it unfolds.</summary>
        BoxEl ColumnHeader(Action<Action> post, bool folded)
        {
            bool newestFirst = _newestFirst.Value != 0;
            float give = folded ? 1f : 0f;   // the fixed columns' shrink: none while the header stands (they mirror the rows' columns), all of it while folded
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Height = CommonLogsRules.ColumnHeaderHeight, Shrink = 0f, MinWidth = 0f,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Children =
                [
                    new BoxEl { Width = CommonLogsRules.ChevronSlot, Shrink = give, MinWidth = 0f },
                    new BoxEl { Width = CommonLogsRules.DotSlot, Shrink = give, MinWidth = 0f },
                    TimeHeader(newestFirst, post, folded),
                    HeaderLabel(Loc.Get(Strings.Logs.ColLevel)) with { Width = CommonLogsRules.LevelColumn, Shrink = give, MinWidth = 0f },
                    HeaderLabel(Loc.Get(Strings.Logs.ColCategory)) with { Width = CommonLogsRules.CategoryColumn, Shrink = give, MinWidth = 0f },
                    HeaderLabel(Loc.Get(Strings.Logs.ColMessage)) with { Grow = 1f, Shrink = give, MinWidth = 0f },
                ],
            };
        }

        static TextEl HeaderLabel(string text) => R.Type.MicroMeta(text) with { Weight = 600, Color = Tok.TextTertiary };

        /// <summary>The sortable Time header: the arrow shows where the NEWEST row sits. A click flips the order. Folded (see <see cref="ColumnHeader"/>), it stays
        /// the same node but is no control: no button role, no tab stop, no input, and it shrinks with the other columns.</summary>
        Element TimeHeader(bool newestFirst, Action<Action> post, bool folded) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = CommonLogsRules.SortGap, Width = CommonLogsRules.TimeColumn,
            Shrink = folded ? 1f : 0f, MinWidth = 0f, ClipToBounds = folded,
            Role = folded ? AutomationRole.None : AutomationRole.Button, Focusable = !folded, IsEnabled = !folded, HitTestVisible = !folded,
            Cursor = folded ? null : CursorId.Hand,
            OnClick = () => post(() => { _newestFirst.Value = newestFirst ? 0 : 1; _expandedSeq.Value = -1; }),
            Children =
            [
                HeaderLabel(Loc.Get(Strings.Logs.ColTime)) with { HoverColor = Tok.TextPrimary },
                new TextEl(newestFirst ? Icons.ChevronDown : Icons.ChevronUp)
                    { Size = CommonLogsRules.SortArrowSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary, Shrink = 0f },
            ],
        };

        Element LogBody(WaveeLogEntry[]? entries, LogViewResult result, LogViewQuery query, MeasuredStackVirtualLayout layout)
        {
            if (entries is null)
                return new BoxEl
                {
                    Key = "logs:loading", Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Children = [ProgressRing.Indeterminate(), new TextEl(Loc.Get(Strings.Logs.LoadingSession)) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary }],
                };
            if (result.Shown == 0)
                return new BoxEl
                {
                    Key = "logs:empty", Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Padding = new Edges4(Spacing.L, CommonLogsRules.EmptyInsetY, Spacing.L, CommonLogsRules.EmptyInsetY),
                    Children = [FluentGpu.Dsl.Ui.Icon(Icons.Search, CommonLogsRules.EmptyGlyphSize, Tok.TextTertiary), R.Type.PageHero(Loc.Get(Strings.Logs.EmptyFilter)) with { Wrap = TextWrap.Wrap, MinWidth = 0f }],
                };

            // The list's ItemCount/template freeze at mount: the visible SET changing remounts it; ScrollKey keeps the offset. The row form is NOT in
            // the key: the rows read it themselves (RowLayout), so a crossing re-renders them in place.
            string scrollKey = "logs:scroll:" + _host.Session.Value.ToString(CultureInfo.InvariantCulture);
            var rows = result.Rows;
            return new BoxEl
            {
                Key = "logs:list:" + LogView.RemountKey(_host.Session.Value, query, result.Shown),
                Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Children =
                [
                    ItemsView.Create(rows.Length, i => LogRow(rows[i]), RepeatLayout.Measured(layout), new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.None, Controller = _listCtrl, Selector = SelectorVisual.None,
                        KeyOf = i => scrollKey + ":" + rows[i].Entry.Sequence.ToString(CultureInfo.InvariantCulture),
                        IsItemInvokedEnabled = true, OnInvoked = i => ToggleExpand(rows[i].Entry.Sequence, rows), Grow = 1f,
                        Scroll = new ScrollOptions { ScrollKey = scrollKey },
                    }),
                ],
            };
        }

        void ToggleExpand(long seq, LogViewRow[] rows)
        {
            _expandedSeq.Value = _expandedSeq.Peek() == seq ? -1 : seq;
            int idx = LogView.IndexOfSequence(rows, seq);
            if (idx >= 0) _listCtrl.StartBringItemIntoView(idx, alignmentRatio: 0f);
        }

        /// <summary>36 DIP, fixed columns (<see cref="ColumnHeader"/> mirrors them): chevron 12 · dot 6 · time 92 · pill
        /// 58 × 22 · category 96 · message · ×N. A two-line row (Compact) has no column header: time, level and count on the first line,
        /// category and message on the second, indented under the time.</summary>
        Element LogRow(LogViewRow row)
        {
            var e = row.Entry;
            long seq = e.Sequence;
            var layout = RowLayout();
            bool expanded = _expandedSeq.Value == seq;
            bool wrapAll = expanded || _wrap.Value != 0;
            var message = R.Type.DenseMeta(e.Message) with
            {
                Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f,
                Wrap = wrapAll ? TextWrap.Wrap : TextWrap.NoWrap, Trim = wrapAll ? TextTrim.None : TextTrim.CharacterEllipsis, MaxLines = wrapAll ? 0 : 1,
            };
            Element[] cells = layout.TwoLineRows
                ?
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                        Children =
                        [
                            Sidebar.Chevron.Disclosure(() => _expandedSeq.Value == seq, size: CommonLogsRules.ChevronSlot),
                            SeverityDot(e.Level),
                            new TextEl(LogView.FormatTime(e.UnixMs, LocalOffset)) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Grow = 1f, MinWidth = 0f },
                            LevelPill(e.Level),
                            row.Repeat > 1 ? RepeatBadge(row.Repeat) : new BoxEl(),
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, MinWidth = 0f, Padding = new Edges4(CommonLogsRules.CompactMessageInset, 0f, 0f, 0f),
                        Children =
                        [
                            new TextEl(e.Category) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", MaxWidth = CommonLogsRules.CategoryColumn, Shrink = 0f, Trim = TextTrim.CharacterEllipsis },
                            message,
                        ],
                    },
                ]
                :
                [
                    Sidebar.Chevron.Disclosure(() => _expandedSeq.Value == seq, size: CommonLogsRules.ChevronSlot),
                    SeverityDot(e.Level),
                    new TextEl(LogView.FormatTime(e.UnixMs, LocalOffset)) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = CommonLogsRules.TimeColumn, Shrink = 0f },
                    LevelPill(e.Level),
                    new TextEl(e.Category) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = CommonLogsRules.CategoryColumn, Shrink = 0f, Trim = TextTrim.CharacterEllipsis },
                    message,
                    row.Repeat > 1 ? RepeatBadge(row.Repeat) : new BoxEl(),
                ];
            var line = new BoxEl
            {
                Direction = layout.TwoLineRows ? (byte)1 : (byte)0, AlignItems = layout.TwoLineRows ? FlexAlign.Stretch : FlexAlign.Center,
                Gap = layout.TwoLineRows ? CommonLogsRules.CompactLineGap : Spacing.S,
                MinHeight = layout.Target > 0f ? layout.Target : CommonLogsRules.RowMinHeight,
                Padding = layout.TwoLineRows ? new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S) : new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Grow = 1f, Fill = expanded ? Tok.FillSubtleSecondary : ColorF.Transparent,
                // The message wraps when expanded: the taller line reveals, never snaps.
                Animate = Design.Reveal.Resize,
                Children = cells,
            }.Interactive(Interaction.ListRow);

            // The row is ALWAYS the keyed column; expanding only adds the details child, a Drawer reveal (Design.Reveal.Drawer).
            // The details: Fields (only when non-empty), Exception (only when present), and the meta line (always).
            Element[] children = [line];
            if (expanded)
            {
                var detail = new List<Element>(3);
                string fieldText = LogView.FieldText(e.Fields);
                if (fieldText.Length > 0) detail.Add(DetailSection(Loc.Get(Strings.Logs.Fields), fieldText, layout.DetailInset));
                if (e.Exception is { Length: > 0 } ex) detail.Add(DetailSection(Loc.Get(Strings.Logs.Exception), ex, layout.DetailInset));
                detail.Add(R.Type.MicroMeta(LogView.MetaLine(in e)) with { Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap, MinWidth = 0f, Margin = new Edges4(layout.DetailInset, 0f, 0f, 0f) });
                children = [line, new BoxEl
                {
                    Key = "details", Direction = 1, Gap = CommonLogsRules.DetailGap, Padding = new Edges4(0f, 4f, 0f, Spacing.S),
                    Animate = Design.Reveal.Drawer, Children = detail.ToArray(),
                }];
            }
            return new BoxEl { Key = "logs:row:" + seq.ToString(CultureInfo.InvariantCulture), Direction = 1, Children = children };
        }

        /// <summary>The row form on screen now, read from the tier facts INSIDE the list's realize (like <see cref="_expandedSeq"/>), so a crossing re-renders the
        /// realized rows in place, keyed as they were, instead of the list being remounted for the other form.</summary>
        CommonLogsLayout RowLayout() => _facts is { } facts ? CommonLogsLayout.For(facts.Value.Tiers) : _layout;

        static Element DetailSection(string caption, string text, float inset) => new BoxEl
        {
            Direction = 1, Gap = CommonLogsRules.DetailGap, Padding = new Edges4(inset, 0f, Spacing.M, 4f),
            Children = [R.Type.MicroMeta(caption) with { Weight = 600, Color = Tok.TextTertiary }, CodeBlock.Create(text, copyable: true, fontSize: CommonLogsRules.CellSize)],
        };

        /// <summary>A dot only for Warning and ≥ Error; every other level holds the 6-DIP column with a blank spacer.</summary>
        static Element SeverityDot(WaveeLogLevel level) => level switch
        {
            WaveeLogLevel.Warning => InfoBadge.Dot(InfoBadgeSeverity.Caution),
            >= WaveeLogLevel.Error => InfoBadge.Dot(InfoBadgeSeverity.Critical),
            _ => new BoxEl { Width = CommonLogsRules.DotSlot, Height = CommonLogsRules.DotSlot, Shrink = 0f },
        };

        static Element RepeatBadge(int repeat) => new BoxEl
        {
            Padding = new Edges4(7f, 1f, 7f, 2f), Corners = CornerRadius4.All(Radii.Full), Fill = Tok.FillSubtleSecondary,
            Children = [R.Type.MicroMeta("×" + repeat.ToString(CultureInfo.InvariantCulture)) with { Weight = 700, Color = Tok.TextSecondary }],
        };

        static BoxEl LevelPill(WaveeLogLevel level)
        {
            var color = level switch
            {
                WaveeLogLevel.Critical or WaveeLogLevel.Error => Tok.SystemFillCritical,
                WaveeLogLevel.Warning => Tok.SystemFillCaution,
                WaveeLogLevel.Debug or WaveeLogLevel.Trace => Tok.TextTertiary,
                _ => Tok.AccentDefault,
            };
            return new BoxEl
            {
                Width = CommonLogsRules.LevelColumn, Height = CommonLogsRules.PillHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(Radii.Full),
                Fill = color with { A = 0.12f }, BorderWidth = 1f, BorderColor = color with { A = 0.38f },
                Children = [new TextEl(level.ToString().ToUpperInvariant()) { Size = CommonLogsRules.PillTextSize, Weight = 800, Color = color }],
            };
        }

        // ── the footer: shown/total + Load more + the capture caption ────────────────────────────────────────────────

        /// <summary>A past session the loader capped says so ("last 4,096 of 17,936 events · log file") instead of
        /// advertising the full count over a partial list (audit #9, G8).</summary>
        Element Footer(LogViewResult result, bool live, WaveeLogEntry[]? entries, WaveeLogSessions.Info? past)
        {
            string text;
            if (live) text = Strings.Logs.FooterLive(LogsPage.Rules.Thousands(result.Shown), LogsPage.Rules.Thousands(result.Total));
            else if (past is not null && entries is not null && entries.Length < past.EntryCount)
                text = Strings.Logs.FooterPastCapped(LogsPage.Rules.Thousands(entries.Length), LogsPage.Rules.Thousands(past.EntryCount));
            else text = Strings.Logs.FooterPast(LogsPage.Rules.Thousands(result.Shown), LogsPage.Rules.Thousands(result.Total));

            var kids = new List<Element>(3)
            {
                new TextEl(text) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            };
            // The cap stops growing at LogView.MaxRows; past it there is nothing more to load.
            if (result.Truncated && _visibleLimit.Value < LogView.MaxRows)
                kids.Add(CommonTouch.Link(Loc.Get(Strings.Logs.LoadMore), () => _visibleLimit.Value = LogView.NextCap(_visibleLimit.Peek()), _layout.Target));
            var (ring, file) = _host.CaptureLevels();
            kids.Add(R.Type.MicroMeta(Strings.Logs.CaptureCaption(ring, file)) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f });
            return new BoxEl
            {
                // Compact: the caption does not fit beside the count, so the footer wraps (two-line rows are the Compact form).
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = _layout.TwoLineRows, Padding = new Edges4(Spacing.L, Spacing.S, Spacing.M, Spacing.S),
                Children = kids.ToArray(),
            };
        }
    }
}
