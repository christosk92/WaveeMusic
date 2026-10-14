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
// NOTHING HERE READS A CLOCK PER FRAME (ch 27 N12): the live tail polls at 750 ms and bumps only when `Log.Version` moved (and not at all on a
// past session). The sessions are re-listed once on mount and once per page activation (a keep-alive return, or the window coming back) —
// `UseActivation` fires on TRANSITIONS only, which is why the mount walk is its own effect.
//
// PROPS FREEZE AT MOUNT: every control whose interesting fields freeze (the session / category combos, the virtualised list) is REMOUNTED
// through a `Key` that changes exactly when its frozen input does, and every write that tears down the node that was clicked runs through
// `UsePost` so the click finishes first. No signal is written in render.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
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
    sealed class LogsPageView : Component
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
            var logLayout = UseMemo(static () => new MeasuredStackVirtualLayout(estimatedExtent: CommonLogsRules.EstimatedRowExtent), DepKey.Empty);

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

            return new BoxEl
            {
                Key = "logs-page", Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                OnKeyDown = _onKey ??= OnPageKey,
                Children =
                [
                    HeaderBar(),
                    Divider(),
                    Toolbar(result, live, hooks, post),
                    new BoxEl
                    {
                        Margin = new Edges4(CommonLogsRules.GutterX, 0f, CommonLogsRules.GutterX, Spacing.L),
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Corners = CornerRadius4.All(Radii.Card),
                        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                        Children =
                        [
                            ColumnHeader(post),
                            Divider(),
                            LogBody(entries, result, query, logLayout),
                            Divider(),
                            Footer(result, live, entries, past),
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
            // Every child is keyed: the picker caption comes and goes, and a positional diff would otherwise slide the
            // combo's remount key onto the wrong sibling.
            var kids = new List<Element>(4)
            {
                new BoxEl
                {
                    Key = "logs:title", Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = Spacing.XXS,
                    Children =
                    [
                        // Settings › Privacy & diagnostics › Logs. The first crumb opens Settings on its remembered tab; the
                        // second lands on the tab this page belongs to; the last is this page and does nothing.
                        BreadcrumbBar.Create([Loc.Get(Strings.Nav.Settings), Loc.Get(Strings.Settings.Tabs.Privacy), Loc.Get(Strings.Logs.Title)],
                            _onCrumb ??= host.OpenCrumb),
                        R.Type.PageHero(Loc.Get(Strings.Logs.Title)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            };
            if (host.PickerCaption() is { } caption)
                kids.Add(R.Type.MicroMeta(caption) with
                {
                    Key = "logs:caption", Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    MaxWidth = CommonLogsRules.CaptionMaxWidth, Shrink = 1f, MinWidth = 0f,
                });
            var labels = host.SessionLabels();
            kids.Add(ComboBox.Create(labels, host.Session, width: CommonLogsRules.SessionComboWidth, onChange: _ => _expandedSeq.Value = -1)
                with { Key = "logs:session:" + host.SessionsRev.Value.ToString(CultureInfo.InvariantCulture) });
            kids.Add(Button.Create(Loc.Get(Strings.Logs.OpenFolder), _openFolder ??= host.OpenLogFolder,
                ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.Folder) with { Key = "logs:folder", Shrink = 0f });

            return new BoxEl
            {
                // The page gutter (PageWide) and the hero's top room, as Profile.Lists.Page's head; the picker cluster
                // sits on the hero's baseline row, so it centres against the two-line title lane.
                Key = "logs-header", Direction = 0, Shrink = 0f, Gap = Spacing.S,
                AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.S),
                Children = kids.ToArray(),
            };
        }

        // ── the toolbar: filter · level strip (counts inside) · categories · … · copy · export · more ─────────────────

        Element Toolbar(LogViewResult result, bool live, InputHooks hooks, Action<Action> post)
        {
            var host = _host;
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

            return new BoxEl
            {
                Key = "logs-toolbar", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true, Shrink = 0f,
                Padding = new Edges4(CommonLogsRules.GutterX, Spacing.L, CommonLogsRules.GutterX, Spacing.S),
                Children =
                [
                    AutoSuggestBox.Create(Array.Empty<string>(), placeholder: Loc.Get(Strings.Logs.FilterPlaceholder),
                        grow: 1f, maxFillWidth: CommonLogsRules.FilterMaxFill, text: _search, onChange: q => _search.Value = q, onQuerySubmitted: q => _search.Value = q,
                        minHeight: CommonLogsRules.FilterMinHeight, cornerRadius: Radii.Control),
                    Segmented.Create(levelItems, _level),
                    ComboBox.Create(catLabels, _category, width: CommonLogsRules.CategoryComboWidth)
                        with { Key = "logs:cat:" + host.Session.Value.ToString(CultureInfo.InvariantCulture) + ":" + _categories.Length.ToString(CultureInfo.InvariantCulture) },
                    new BoxEl { Grow = 1f },
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
                        Children =
                        [
                            ToolTip.Wrap(IconButton.Create(Icons.Copy, () => hooks.Clipboard?.SetText(LogView.CopyText(_rows))) with { Shrink = 0f },
                                Loc.Get(Strings.Logs.CopyVisible)),
                            ToolTip.Wrap(IconButton.Create(Icons.Download, _exportSession ??= host.ExportSession) with { Shrink = 0f }, exportTip),
                            ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenMore(post)) with { Shrink = 0f, OnRealized = h => _moreAnchor = h },
                                Loc.Get(Strings.Logs.More)),
                        ],
                    },
                ],
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
            // Same anchored-flyout shape as Shell.UI.cs's history menu and `ClassicSettings.VideoOverrides.cs`'s manager menu.
            var handle = overlay.Open(() => _moreAnchor, () => MenuFlyout.Create(items, () => _menu?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
        }

        // ── the list card: column header · body · footer ────────────────────────────────────────────────────────────

        /// <summary>The same padding, gap and column widths as <see cref="LogRow"/>; only Time is interactive (flips the sort).</summary>
        Element ColumnHeader(Action<Action> post)
        {
            bool newestFirst = _newestFirst.Value != 0;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Height = CommonLogsRules.ColumnHeaderHeight, Shrink = 0f,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Children =
                [
                    new BoxEl { Width = CommonLogsRules.ChevronSlot, Shrink = 0f },
                    new BoxEl { Width = CommonLogsRules.DotSlot, Shrink = 0f },
                    TimeHeader(newestFirst, post),
                    HeaderLabel(Loc.Get(Strings.Logs.ColLevel)) with { Width = CommonLogsRules.LevelColumn, Shrink = 0f },
                    HeaderLabel(Loc.Get(Strings.Logs.ColCategory)) with { Width = CommonLogsRules.CategoryColumn, Shrink = 0f },
                    HeaderLabel(Loc.Get(Strings.Logs.ColMessage)) with { Grow = 1f, MinWidth = 0f },
                ],
            };
        }

        static TextEl HeaderLabel(string text) => R.Type.MicroMeta(text) with { Weight = 600, Color = Tok.TextTertiary };

        /// <summary>The sortable Time header: the arrow shows where the NEWEST row sits. A click flips the order.</summary>
        Element TimeHeader(bool newestFirst, Action<Action> post) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = CommonLogsRules.SortGap, Width = CommonLogsRules.TimeColumn, Shrink = 0f,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
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
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Children = [ProgressRing.Indeterminate(), new TextEl(Loc.Get(Strings.Logs.LoadingSession)) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary }],
                };
            if (result.Shown == 0)
                return new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Padding = new Edges4(0f, 64f, 0f, 64f),
                    Children = [FluentGpu.Dsl.Ui.Icon(Icons.Search, CommonLogsRules.EmptyGlyphSize, Tok.TextTertiary), R.Type.PageHero(Loc.Get(Strings.Logs.EmptyFilter))],
                };

            // The list's ItemCount/template freeze at mount: the visible SET changing remounts it; ScrollKey keeps the offset.
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
        /// 58 × 22 · category 96 · message · ×N.</summary>
        Element LogRow(LogViewRow row)
        {
            var e = row.Entry;
            long seq = e.Sequence;
            bool expanded = _expandedSeq.Value == seq;
            bool wrapAll = expanded || _wrap.Value != 0;
            var line = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = CommonLogsRules.RowMinHeight, Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Grow = 1f, Fill = expanded ? Tok.FillSubtleSecondary : ColorF.Transparent,
                // The message wraps when expanded: the taller line reveals, never snaps.
                Animate = Design.Reveal.Resize,
                Children =
                [
                    Sidebar.Chevron.Disclosure(() => _expandedSeq.Value == seq, size: CommonLogsRules.ChevronSlot),
                    SeverityDot(e.Level),
                    new TextEl(LogView.FormatTime(e.UnixMs, LocalOffset)) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = CommonLogsRules.TimeColumn, Shrink = 0f },
                    LevelPill(e.Level),
                    new TextEl(e.Category) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = CommonLogsRules.CategoryColumn, Shrink = 0f, Trim = TextTrim.CharacterEllipsis },
                    R.Type.DenseMeta(e.Message) with
                    {
                        Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f,
                        Wrap = wrapAll ? TextWrap.Wrap : TextWrap.NoWrap, Trim = wrapAll ? TextTrim.None : TextTrim.CharacterEllipsis, MaxLines = wrapAll ? 0 : 1,
                    },
                    row.Repeat > 1 ? RepeatBadge(row.Repeat) : new BoxEl(),
                ],
            }.Interactive(Interaction.ListRow);

            // The row is ALWAYS the keyed column; expanding only adds the details child, a Drawer reveal (Design.Reveal.Drawer).
            // The details: Fields (only when non-empty), Exception (only when present), and the meta line (always).
            Element[] children = [line];
            if (expanded)
            {
                var detail = new List<Element>(3);
                string fieldText = LogView.FieldText(e.Fields);
                if (fieldText.Length > 0) detail.Add(DetailSection(Loc.Get(Strings.Logs.Fields), fieldText));
                if (e.Exception is { Length: > 0 } ex) detail.Add(DetailSection(Loc.Get(Strings.Logs.Exception), ex));
                detail.Add(R.Type.MicroMeta(LogView.MetaLine(in e)) with { Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Margin = new Edges4(44f, 0f, 0f, 0f) });
                children = [line, new BoxEl
                {
                    Key = "details", Direction = 1, Gap = CommonLogsRules.DetailGap, Padding = new Edges4(0f, 4f, 0f, Spacing.S),
                    Animate = Design.Reveal.Drawer, Children = detail.ToArray(),
                }];
            }
            return new BoxEl { Key = "logs:row:" + seq.ToString(CultureInfo.InvariantCulture), Direction = 1, Children = children };
        }

        static Element DetailSection(string caption, string text) => new BoxEl
        {
            Direction = 1, Gap = CommonLogsRules.DetailGap, Padding = new Edges4(44f, 0f, Spacing.M, 4f),
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
                new TextEl(text) { Size = CommonLogsRules.CellSize, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f },
            };
            // The cap stops growing at LogView.MaxRows; past it there is nothing more to load.
            if (result.Truncated && _visibleLimit.Value < LogView.MaxRows)
                kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Logs.LoadMore), () => _visibleLimit.Value = LogView.NextCap(_visibleLimit.Peek())));
            var (ring, file) = _host.CaptureLevels();
            kids.Add(R.Type.MicroMeta(Strings.Logs.CaptureCaption(ring, file)) with { Color = Tok.TextTertiary });
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.L, Spacing.S, Spacing.M, Spacing.S),
                Children = kids.ToArray(),
            };
        }
    }
}
