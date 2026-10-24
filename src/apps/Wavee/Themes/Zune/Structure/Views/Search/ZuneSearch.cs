// ── Themes/Zune/Structure/Views/Search/ZuneSearch.cs — Zune's `Shell.Search` and `Shell.SearchSuggestions` (themes plan 6.3; P5 Z2a) ──
//
// Board Z-Part-TitleBar. The core owns the omnibar's model (`OmnibarModel`: the store over the request lifecycle, the keystroke and fetch
// edges, the focus tickets, the keyboard cursor and every verb) and the answer the popup draws (`SearchAnswer`); the picture is Zune's:
//
//   Shell.Search  (Field)  the pill: the one rounded field (the style's `AutoSuggestBoxFieldStyle`, an 18 % ring), 220 / 320 / 400 wide by the
//                          window tier's record, 32 tall (40 at Huge 4K). It GROWS by 120 while it has focus or its suggestions are open,
//                          as one width reflow (the box and its text move together); a rest-width change without a focus flip (a crossing)
//                          snaps with its row. The width is Zune's own record, not the allocator's field width: Zune's row has no centred
//                          column to share. The settled width is reported to the model, which re-pushes the title bar's regions.
//   Shell.Search  (Icon)   the 40 x 40 magnifier; a press or a focus ticket (Ctrl+F) opens the omnibar in a flyout under it, the tier's flyout
//                          width (never wider than the window less a gutter either side).
//   Shell.SearchSuggestions  the popup body, drawn BY the store's state, exactly the box's width (the anchor's, no floor) and hanging from its
//                          left edge (the field's popup presentation): a square plate of 36-tall rows inset 16; the selected row is the 12 %
//                          accent wash, a hovered one the 5 % ink wash; the progress line (2 tall) is always the first child; "No results
//                          found" only for a confirmed empty answer, a retry for a failure, a skeleton in the reserved height while pending.
//
// The two forms are two mounts of ONE omnibar over ONE model, so a tier crossing that swaps them keeps the rows, the pending generation and the
// cursor. A rich row is the shared media surface in slot mode (the field keeps focus; the arrow-key cursor is the row's "focused").

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee.Themes.Zune;

/// <summary>Zune's search.</summary>
internal static class ZuneSearch
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellSearch"/>.</summary>
    public static Element Template(ShellSearchProps props, in ViewContext ctx)
        => props.Form == SearchForm.Field
            ? Embed.Comp(() => new Pill(props))
            : Embed.Comp(() => new Magnifier(props));

    /// <summary>The registered template of <see cref="ViewKeys.ShellSearchSuggestions"/>.</summary>
    public static Element SuggestionsTemplate(SearchSuggestionsProps props, in ViewContext ctx)
        => Embed.Comp(() => new SuggestionsPopup(props));

    /// <summary>Provided around the suggestions body by the omnibar that hosts it: how far down the window its rows begin (the title bar's height for the
    /// pill's popup; that plus the field for the magnifier's flyout, whose field stands over the rows), so the body's cap ends the plate above the play bar.</summary>
    private static readonly Context<float> RowsTop = new(0f);

    /// <summary>The window tier's title-bar record, as the component is drawn in (a crossing re-renders it).</summary>
    private static ZuneTitleBarLayout RecordOf(IReadSignal<ThemeFacts>? provided) => ZuneFrameRules.TitleBarOf(TierContext.DrawnIn(provided).Value.Tiers);

    // ── the pill ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Pill(ShellSearchProps props) : Component
    {
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var rec = RecordOf(UseContext(ThemeFactsContext.Signal));
            var field = UseRef<NodeHandle>(default);
            var model = props.Model;
            var parts = UseMemo(() =>
            {
                var p = new TemplateParts();
                p[AutoSuggestBox.PartRoot] = b => b with
                {
                    OnRealized = h => field.Value = h,
                    OnFocusChanged = f => model.SearchFocused.SetIfChanged(f),
                };
                return p;
            }, DepKey.Empty);

            // A focus ticket (Ctrl+F, or the caret handed to this form) lands on the editable text, not the chrome root.
            int ticket = props.FocusTicket();
            UseLayoutEffect(() =>
            {
                if (field.Value.IsNull || !model.ConsumeFocusTicket(ticket)) return;
                var editor = hooks.FirstFocusableIn?.Invoke(field.Value) ?? NodeHandle.Null;
                if (!editor.IsNull) hooks.FocusNode?.Invoke(editor, true);
            }, DepKey.From(ticket));

            // The focus flip AND a crossing (220 / 320 / 400) ease the width (ZT1: a crossing never jumps). Within the pill tiers the old width
            // always fits the new row: a widening has the room, and a narrowing commits only 24 below the edge, where the wider pill sat a frame ago.
            bool expanded = model.FieldExpanded;
            float width = expanded ? rec.PillW + ZuneFrameRules.PillFocusGrow : rec.PillW;
            // Beside a docked panel the page side may cap it (the title bar's pill host provides the cap; it never binds at a docking size
            // today, see ZuneFrameRules.PillBesidePanel): bound, so a resize narrows it with no re-render, eased by the same width motion.
            var maxW = UseContext(ZuneTitleBar.PillMaxW);
            float Fit() => maxW is null ? width : MathF.Min(width, maxW());
            // Off the pill tiers the field is the title bar's (Compact, Z-Search-Compact: it fills the lead island beside Back), no rest width.
            bool fill = !rec.Pill;

            // The key hint (Z-Part-TitleBar: a small "ctrl f" chip at the pill's end) stands while the pill is at rest and empty; a focus (the pill grows) or a
            // typed query takes it away. It never takes a pointer, so the field under it stays one click target.
            bool hint = !fill && !expanded && string.IsNullOrEmpty(model.QueryText);
            // A COLUMN, so the box is STRETCHED to this slot's width (a fill-mode box in a row latches its query button's width).
            return new BoxEl
            {
                Key = ZuneFrameRules.PillKey, Direction = 1, Shrink = fill ? 1f : 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Stretch,
                Width = fill ? float.NaN : Prop.Of(Fit), Grow = fill ? 1f : 0f, MinWidth = 0f,
                Animate = Design.Reduced || fill ? null : ZuneFrameRules.PillWidthMotion,
                OnBoundsChanged = r => model.ReportFieldWidth(r.W, fill ? r.W : Fit()),
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Children =
                        [
                            Embed.Comp(() => new Omnibar(props, parts, 0f, AutoSuggestBoxSuggestionPresentation.Popup)),
                            new BoxEl
                            {
                                Key = ZuneFrameRules.PillHintKey, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, HitTestVisible = false,
                                Padding = new Edges4(0f, 0f, ZuneFrameRules.PillHintInset, 0f), Visible = hint,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Direction = 0, Padding = new Edges4(4f, 1f, 4f, 1f), BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
                                        Children =
                                        [
                                            R.Type.MicroMeta(Loc.Get(Strings.Theme.Zune.Frame.SearchHint)) with
                                            {
                                                Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                                            },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };
        }
    }

    // ── the magnifier ────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Magnifier(ShellSearchProps props) : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var viewport = UseContext(Viewport.Size);
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            float flyoutWidth = ZuneFrameRules.FlyoutWidth(in facts, viewport.Width);
            var model = props.Model;

            void CloseFlyout()
            {
                handle.Value?.Close();
                handle.Value = null;
                model.Escape();
            }

            void OpenFlyout()
            {
                if (handle.Value is { IsOpen: true }) return;
                var h = overlay.Open(
                    () => anchor.Value,
                    () => new BoxEl
                    {
                        Direction = 1, Width = flyoutWidth, MinWidth = flyoutWidth,
                        Children =
                        [
                            Embed.Comp(() => new Omnibar(props, null, flyoutWidth, AutoSuggestBoxSuggestionPresentation.Inline, afterChoose: CloseFlyout)),
                        ],
                    },
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    {
                        ConstrainToRootBounds = true,
                        Plate = ZuneControls.SuggestionsPlate(),   // the suggestions' own solid square plate, as the pill's popup draws it
                    });
                handle.Value = h;
                OverlayService.Track(h);
                model.FlyoutOpen.SetIfChanged(true);
                h.ClosedAction = () =>
                {
                    handle.Value = null;
                    model.Escape();
                };
            }

            int ticket = props.FocusTicket();
            UseLayoutEffect(() =>
            {
                if (!model.ConsumeFocusTicket(ticket)) return;
                OpenFlyout();
            }, DepKey.From(ticket));
            UseEffect(() => (Action?)CloseFlyout, DepKey.Empty);

            void Toggle()
            {
                if (handle.Value is { IsOpen: true }) CloseFlyout();
                else OpenFlyout();
            }

            return ToolTip.Wrap(
                IconButton.Create(Icons.Search, Toggle, ZuneTitleBar.ButtonStyle(Tok.TextSecondary))
                    with { Key = ZuneFrameRules.SearchIconKey, OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Nav.Search));
        }
    }

    // ── the omnibar ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The real AutoSuggestBox (focus, editing, accessibility, popup lifetime) in the pill's metrics. Its text IS the omnibar's text
    /// cell. The keystroke edge starts Pending at once; the fetch edge waits for the generation to settle (150 ms).</summary>
    private sealed class Omnibar(ShellSearchProps props, TemplateParts? parts, float maxWidth,
        AutoSuggestBoxSuggestionPresentation presentation, Action? afterChoose = null) : Component
    {
        readonly OmnibarModel.Fetch _fetch = props.Model.NewFetch();

        public override Element Render()
        {
            var model = props.Model;
            var text = props.TextCell();
            var post = UsePost();
            var highlight = model.Highlight;
            var rec = RecordOf(UseContext(ThemeFactsContext.Signal));
            _ = model.Version.Value;
            string typed = text.Value.Trim();

            UseEffect(() =>
            {
                if (model.Begin(typed)) _fetch.Cancel();
            }, typed);

            int settled = UseDebouncedValue(() => { _ = model.Version.Value; return model.QueryGeneration; },
                AutoSuggestBox.TextChangedDebounceMs).Value;
            UseEffect(() =>
            {
                if (!model.NeedsFetch(settled)) return;
                _ = _fetch.Start(post, settled, model.QueryText);
            }, DepKey.From(settled));
            UseEffect(() => (Action?)_fetch.Cancel, DepKey.Empty);

            var completion = UseComputed(() =>
            {
                if (highlight.Value >= 0) return "";
                _ = model.Version.Value;
                return model.GhostFor(text.Value.Trim());
            });

            bool InvokeSelection(int selection)
            {
                if (!model.Choose(selection)) return false;
                afterChoose?.Invoke();
                return true;
            }

            void Submit(string q)
            {
                model.Submit(q);
                afterChoose?.Invoke();
            }

            var source = props.Suggestions;
            float rowsTop = ZuneTiers.FrameOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers).TitleBarH
                + (presentation == AutoSuggestBoxSuggestionPresentation.Inline ? rec.PillH : 0f);
            var presenter = new AutoSuggestBoxPresenter(
                Build: context =>
                {
                    Element rows = ViewSlots.View(ViewKeys.ShellSearchSuggestions, new SearchSuggestionsProps
                    {
                        Source = source,
                        Width = context.Width,
                        Choose = selection => { if (InvokeSelection(selection)) context.Close(); },
                        Close = context.Close,
                    });
                    // The pill's list holds it at its focused width while it is open (a row press may take focus).
                    Element body = presentation == AutoSuggestBoxSuggestionPresentation.Popup ? Embed.Comp(() => new SuggestionsOpenMark(model, rows)) : rows;
                    // Z-Overlays: the plate closes on "see all results for <query>", the typed query's results page (what Enter submits).
                    return Ctx.Provide(RowsTop, rowsTop, new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Children = [body, Embed.Comp(() => new SeeAllRow(text, q => { Submit(q); context.Close(); }))],
                    });
                },
                MoveSelection: delta => model.MoveHighlight(delta),
                SubmitSelection: () => InvokeSelection(highlight.Peek()),
                ResetSelection: () => model.ResetHighlight());

            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    AutoSuggestBox.Create(Array.Empty<string>(), Loc.Get(Strings.Theme.Zune.Frame.SearchPlaceholder),
                        grow: 1f, maxFillWidth: maxWidth, text: text, onQuerySubmitted: Submit,
                        minHeight: rec.PillH, cornerRadius: rec.PillRadius, presenter: presenter, parts: parts,
                        chrome: AutoSuggestBoxChrome.Standard, suggestionPresentation: presentation, completion: completion,
                        // A field that MOUNTS holding the query (the Compact lead field arriving on a results page, a crossing on one) opens its list
                        // only when the omnibar was live as it swapped (focused, or its list open): the page's query is no fresh edit (V1).
                        openOnSeed: presentation == AutoSuggestBoxSuggestionPresentation.Inline || model.SearchFocused.Peek() || model.SuggestionsOpen.Peek()),
                ],
            };
        }
    }

    /// <summary>The plate's foot (Z-Overlays): "see all results for" the typed query, its results page. A command row in the query rows' voice,
    /// outside the keyboard cursor (Enter in the field submits the same query); its words follow the typing (a bound run, no re-render).</summary>
    private sealed class SeeAllRow(IReadSignal<string> text, Action<string> submit) : Component
    {
        public override Element Render()
        {
            float rowH = ZuneFrameRules.QueryRowHFor(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers.Window);
            return new BoxEl
            {
                Key = ZuneFrameRules.SeeAllKey, MinHeight = rowH, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = ZuneFrameRules.QueryRowPadding,
                Corners = R.Shape.All(ShapeRole.OmniRow), Role = AutomationRole.MenuItem, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
                OnClick = () => submit(text.Peek().Trim()),
                Children =
                [
                    R.Type.DenseTitle("") with
                    {
                        Text = Prop.Of(() => Strings.Theme.Zune.Frame.SearchSeeAll(text.Value.Trim())),
                        Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
        }
    }

    /// <summary>Marks the pill's suggestions list open while it is mounted (<see cref="OmnibarModel.SuggestionsOpen"/>).</summary>
    private sealed class SuggestionsOpenMark(OmnibarModel model, Element body) : Component
    {
        public override Element Render()
        {
            UseLayoutEffect(() =>
            {
                model.SuggestionsOpen.SetIfChanged(true);
                return () => model.SuggestionsOpen.SetIfChanged(false);
            }, DepKey.Empty);
            return body;
        }
    }

    // ── the popup body ───────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SuggestionsPopup(SearchSuggestionsProps props) : Component
    {
        public override Element Render()
        {
            var source = props.Source;
            string typed = source.Text.Value.Trim();
            var answer = source.Answer();
            var state = answer.State;
            int highlighted = source.Highlight.Value;
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            _queryRowH = ZuneFrameRules.QueryRowHFor(tiers.Window);
            // The list ends above the play bar (V16: at Small it ran off the window): the core's cap, or the room between the title bar and the dock.
            float windowH = UseContextSignal(Viewport.Size).Value.Height;
            float cap = ZuneFrameRules.PopupBodyCap(source.PopupBodyMaxHeight, windowH, UseContext(RowsTop), R.Space.DockReserve, _queryRowH);
            // Exactly the box's width (no floor under a 220 pill); the field's popup hangs from the box's left edge.
            float w = props.Width.Value > 0f ? props.Width.Value : ZuneFrameRules.FallbackPopupWidth;

            var rows = new List<Element>(answer.Queries.Count + answer.Rows.Count + 1);
            int index = 0;
            int queryRows = answer.Queries.Count;
            for (int i = 0; i < queryRows; i++, index++)
                rows.Add(QueryRow(answer.Queries[i], typed, index, highlighted == index));
            int richRows = answer.Rows.Count;
            for (int i = 0; i < richRows; i++, index++)
            {
                if (i == 0 && rows.Count > 0) rows.Add(Divider());
                rows.Add(RichRow(answer.Rows[i], index));
            }

            if (rows.Count > 0) { s_lastQueryRows = queryRows; s_lastRichRows = richRows; s_lastWasNotice = false; }
            else if (state is SearchAnswerState.Empty or SearchAnswerState.Failed) { s_lastQueryRows = s_lastRichRows = 0; s_lastWasNotice = true; }

            Element body;
            if (rows.Count == 0)
            {
                body = state switch
                {
                    SearchAnswerState.Empty => Notice(w, Loc.Get(Strings.Search.NoResults), null),
                    SearchAnswerState.Failed => Notice(w, Loc.Get(Strings.Search.SuggestFailed),
                        Button.Subtle(Loc.Get(Strings.Common.Retry), source.Retry)),
                    SearchAnswerState.Pending => PendingBody(w, cap),
                    _ => new BoxEl { Width = w, MinWidth = w, MinHeight = ZuneFrameRules.QueryRowH },
                };
            }
            else
            {
                body = new ScrollEl
                {
                    Width = w, MinWidth = w, MaxHeight = cap, ContentSized = true,
                    Content = new BoxEl { Direction = 1, Width = w, MinWidth = w, Children = rows.ToArray() },
                };
            }

            // The progress line's slot is ALWAYS the first child: it fades, never mounts or unmounts, so a keystroke moves nothing.
            return new BoxEl
            {
                Direction = 1, Width = w, MinWidth = w, Padding = ZuneFrameRules.PopupPadding,
                Layout = Design.Reduced ? null : ZuneFrameRules.PopupHeightMotion,
                Children =
                [
                    new BoxEl
                    {
                        Key = ZuneFrameRules.PopupProgressKey, Width = w, Height = ZuneFrameRules.ProgressSlotHeight, Shrink = 0f, ClipToBounds = true,
                        HitTestVisible = false, Opacity = state == SearchAnswerState.Pending ? 1f : 0f, Transition = MotionTok.ControlFast,
                        Children = state == SearchAnswerState.Pending ? [ProgressBar.Indeterminate(w)] : [],
                    },
                    body,
                ],
            };
        }

        // What the last SETTLED answer took, so a Pending body with nothing to show reserves the same space (root-lifetime).
        sealed class AnswerMemory { public int QueryRows, RichRows; public bool WasNotice; }
        static readonly RootState<AnswerMemory> s_answerMemory = new("Zune.Omnibar.AnswerMemory", static () => new AnswerMemory());
        static int s_lastQueryRows { get => s_answerMemory.Value.QueryRows; set => s_answerMemory.Value.QueryRows = value; }
        static int s_lastRichRows { get => s_answerMemory.Value.RichRows; set => s_answerMemory.Value.RichRows = value; }
        static bool s_lastWasNotice { get => s_answerMemory.Value.WasNotice; set => s_answerMemory.Value.WasNotice = value; }

        static Element Divider() => new BoxEl { Height = 1f, Shrink = 0f, Margin = ZuneFrameRules.DividerMargin, Fill = Tok.StrokeDividerDefault };

        private Element PendingBody(float w, float cap)
        {
            var source = props.Source;
            float height = MathF.Min(cap, source.PendingBodyHeight(s_lastQueryRows, s_lastRichRows, s_lastWasNotice));
            if (s_lastWasNotice)
                return new BoxEl
                {
                    Direction = 0, Width = w, MinWidth = w, Height = height, AlignItems = FlexAlign.Center,
                    Padding = ZuneFrameRules.NoticePadding, ClipToBounds = true,
                    Children = [Controls.PendingBar(ZuneFrameRules.SkeletonNoticeBarW, ZuneFrameRules.SkeletonBarH)],
                };
            var (q, r) = source.PendingSkeletonRows(s_lastQueryRows, s_lastRichRows);
            var kids = new List<Element>(q + r + 1);
            for (int i = 0; i < q; i++)
                kids.Add(new BoxEl
                {
                    Height = source.QueryRowHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = ZuneFrameRules.QueryRowPadding,
                    Children =
                    [
                        Controls.PendingBar(ZuneFrameRules.SkeletonQueryBarW
                            + (i % ZuneFrameRules.SkeletonPeriod) * ZuneFrameRules.SkeletonQueryBarStep, ZuneFrameRules.SkeletonBarH),
                    ],
                });
            for (int i = 0; i < r; i++)
            {
                if (i == 0 && q > 0) kids.Add(Divider());
                kids.Add(new BoxEl
                {
                    Height = source.RichRowHeight, Shrink = 0f, Direction = 0, Gap = ZuneFrameRules.SkeletonRichGap, AlignItems = FlexAlign.Center,
                    Padding = ZuneFrameRules.QueryRowPadding,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = ZuneFrameRules.SkeletonArt, Height = ZuneFrameRules.SkeletonArt, Shrink = 0f,
                            Corners = R.Shape.All(ShapeRole.Cover), Fill = R.Layer.ArtPlaceholder,
                        },
                        new BoxEl
                        {
                            Direction = 1, Gap = ZuneFrameRules.SkeletonTextGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                Controls.PendingBar(ZuneFrameRules.SkeletonRichBarW
                                    + (i % ZuneFrameRules.SkeletonPeriod) * ZuneFrameRules.SkeletonRichBarStep, ZuneFrameRules.SkeletonBarH),
                                Controls.PendingBar(ZuneFrameRules.SkeletonSubBarW, ZuneFrameRules.SkeletonSubBarH),
                            ],
                        },
                    ],
                });
            }
            return new BoxEl
            {
                Direction = 1, Width = w, MinWidth = w, Height = height, ClipToBounds = true,
                Children = kids.ToArray(),
            };
        }

        /// <summary>A query row: 36 tall, inset 16, square; the selected row is the accent wash, a hovered one the ink wash.</summary>
        /// <summary>The query row's height at the window the popup is drawn in (this render's).</summary>
        float _queryRowH = ZuneFrameRules.QueryRowH;

        Element QueryRow(string text, string typed, int index, bool selected) => new BoxEl
        {
            MinHeight = _queryRowH, AlignItems = FlexAlign.Center, Padding = ZuneFrameRules.QueryRowPadding,
            Corners = R.Shape.All(ShapeRole.OmniRow),
            Role = AutomationRole.MenuItem, AllowFocusOnInteraction = false,
            Fill = selected ? R.Layer.SelectedRest : ColorF.Transparent,
            HoverFill = selected ? R.Layer.SelectedHover : R.Layer.RowHover,
            PressedFill = selected ? R.Layer.SelectedPressed : R.Layer.RowPressed,
            OnClick = () => props.Choose(index),
            Children = QueryContent(text, typed),
        };

        Element RichRow(SearchRichRow row, int index)
            => Embed.Comp(() => new OmniRow(row, index, props.Source.Highlight, props.Choose, props.Close))
                with { Key = "zune-omni-row:" + index + ":" + row.Uri };
    }

    /// <summary>One rich row: the shared media surface in slot mode (the highlight IS the focus; tap / Enter choose; the field keeps focus).</summary>
    private sealed class OmniRow(SearchRichRow row, int index, IReadSignal<int> highlight, Action<int> choose, Action? close) : Component
    {
        public override Element Render()
        {
            var at = UseSignal(index);
            var highlighted = UseComputed(() => highlight.Value == index);
            var scope = new RowScope(at, static () => false, () => highlighted.Value, static () => true,
                (trigger, _) => { if (trigger != ItemContainerTrigger.DoubleTap) choose(index); },
                static _ => { })
            { IsFocused = highlighted };

            var f = row.Facts(close);
            var data = new Controls.CardData(f.Uri, f.Title,
                R.Type.TrackMeta(f.Meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                f.ImageUrl, () => choose(index), f.Play, Circular: f.Circular, Drag: f.Drag)
            {
                Trailing = f.Trailing,
                Menu = f.Menu,
            };

            return Controls.SlotSurface(scope, data, OmnibarRowRules.RowShape)
                with { Role = AutomationRole.MenuItem, Margin = ZuneFrameRules.RowMargin, AllowFocusOnInteraction = false };
        }
    }

    /// <summary>One sentence in the row slot, optionally with a trailing affordance (the failure's Retry).</summary>
    private static Element Notice(float width, string text, Element? trailing) => new BoxEl
    {
        Width = width, MinWidth = width, MinHeight = ZuneFrameRules.QueryRowH,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneFrameRules.SkeletonRichGap,
        Padding = ZuneFrameRules.NoticePadding,
        Children = trailing is null
            ? [R.Type.DenseTitle(text) with { Color = Tok.TextPrimary, Grow = 1f, Wrap = TextWrap.Wrap }]
            : [R.Type.DenseTitle(text) with { Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }, trailing],
    };

    /// <summary>A query row's content: the search glyph, then the text with the typed run at the selected weight (case-insensitive).</summary>
    private static Element[] QueryContent(string text, string typed)
    {
        var kids = new List<Element>(4)
        {
            Icon(Icons.Search, ZuneFrameRules.QueryGlyphSize, Tok.TextTertiary) with { Margin = ZuneFrameRules.QueryGlyphMargin },
        };
        int at = typed.Length > 0 ? text.IndexOf(typed, StringComparison.OrdinalIgnoreCase) : -1;
        if (at < 0)
        {
            kids.Add(R.Type.DenseTitle(text) with { Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            return kids.ToArray();
        }
        if (at > 0) kids.Add(Segment(text[..at], match: false, grow: false));
        kids.Add(Segment(text.Substring(at, typed.Length), match: true, grow: false));
        int after = at + typed.Length;
        kids.Add(after < text.Length ? Segment(text[after..], match: false, grow: true) : new BoxEl { Grow = 1f });
        return kids.ToArray();

        static Element Segment(string s, bool match, bool grow) => R.Type.DenseTitle(s) with
        {
            Weight = match ? ZuneFrameRules.MatchWeight : ZuneFrameRules.RestWeight,
            Color = match ? Tok.TextPrimary : Tok.TextSecondary,
            Grow = grow ? 1f : 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}
