// ── Themes/WinUi/Structure/Views/Search/WinUiSearch.cs — WinUI's `Shell.Search` and `Shell.SearchSuggestions` (P6 W2a) ──────────────────
//
// Boards Shell-Top (the field: 32 tall, centred at 520 / 640, filling the middle at Small; the magnifier at Compact) and W-Flyouts
// ("AutoSuggestBox · suggestions while typing": the recent queries with their glyph, a divider edge to edge, the entity rows with a 32 cover
// and "Kind · Artist", "See all results" at the foot; the box squares its bottom corners while the list is open; a failed fetch shows one row
// "Couldn't load suggestions" with Retry, never an empty list). The core owns the omnibar's model (`OmnibarModel`: the suggestion store over
// the request lifecycle, the keystroke and fetch edges, the focus tickets, the keyboard cursor and every verb) and the answer the popup draws
// (`SearchAnswer`); the picture is WinUI's:
//
//   Shell.Search            the field (`SearchForm.Field`) or the magnifier and its flyout (`SearchForm.Icon`): two mounts of ONE omnibar over
//                           ONE model, so a tier crossing that swaps the form keeps the rows, the pending generation and the cursor. The field's
//                           width is the title bar's (`ShellSearchProps.FieldWidth`, overridden by `WinUiTitleBar` from its tier record and the
//                           bar's measured centre): WinUI's AutoSuggestBox keeps its width on focus (no Classic focus growth).
//   Shell.SearchSuggestions the popup body, rendered BY the store's state: "No results found" only for a confirmed empty answer; pending is the
//                           always-mounted progress slot over the previous rows (or skeleton rows in the height the previous answer took);
//                           failed is the retry row; idle (closing) an empty body.

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

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's search.</summary>
internal static class WinUiSearch
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellSearch"/>.</summary>
    public static Element Template(ShellSearchProps props, in ViewContext ctx)
        => props.Form == SearchForm.Field
            ? Embed.Comp(() => new SearchField(props))
            : Embed.Comp(() => new SearchButton(props));

    /// <summary>The registered template of <see cref="ViewKeys.ShellSearchSuggestions"/>.</summary>
    public static Element SuggestionsTemplate(SearchSuggestionsProps props, in ViewContext ctx)
        => Embed.Comp(() => new SuggestionsPopup(props));

    /// <summary>A toolbar button (Shell-Top `tool`): 40 x 36, the control radius.</summary>
    internal static IconButton.Style ToolStyleFor(in TierFacts tiers)
        => IconButton.DefaultStyle with { Size = WinUiFrameRules.ToolW, Height = WinUiTiers.TitleBarOf(in tiers).ToolH };

    private sealed class SearchField(ShellSearchProps props) : Component
    {
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
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

            int ticket = props.FocusTicket();
            UseLayoutEffect(() =>
            {
                // Ctrl+F (or a form flip handing the caret over): focus the editable text, not the chrome.
                if (field.Value.IsNull || !model.ConsumeFocusTicket(ticket)) return;
                var editor = hooks.FirstFocusableIn?.Invoke(field.Value) ?? NodeHandle.Null;
                if (!editor.IsNull) hooks.FocusNode?.Invoke(editor, true);
            }, DepKey.From(ticket));

            // The title bar's width (tracked): the tier's 520 / 640, or the middle at Small. A column, so the box is STRETCHED to it.
            float width = props.FieldWidth();
            // The width EASES (WinUiFrameRules.FieldWidthMotion), and the motion is declared on every render, never only on the render that
            // changes the width: a node that gains its transition in the same commit as its new width is not captured for the FLIP and snaps
            // (the 640 -> 520 narrowing did). A live resize (the modal size loop) snaps every projection, so the field still tracks the
            // pointer with the clusters; a tier crossing, a maximize or a restore glides.
            // The keyed sizer is wrapped in a HUGGING box: a component anchor mirrors its child's DECLARED width, so a reflow on the anchor's
            // own child left the anchor (and the bar's centre island) at the final width and the field kept its final x while it eased
            // (a 60-DIP sideways jump at the 1920 edge). Wrapped, every ancestor hugs the eased width and the field re-centres each frame.
            return new BoxEl
            {
                Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                OnBoundsChanged = r => model.ReportFieldWidth(r.W, width),
                Children =
                [
                    new BoxEl
                    {
                        Key = WinUiFrameRules.SearchFieldKey, Direction = 1, Shrink = 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Stretch,
                        Width = width, Animate = Design.Reduced ? null : WinUiFrameRules.FieldWidthMotion,
                        Children = [Embed.Comp(() => new Omnibar(props, parts, 0f, AutoSuggestBoxSuggestionPresentation.Popup))],
                    },
                ],
            };
        }
    }

    /// <summary>The Compact magnifier (W-Narrow: "search collapses to a button"): a toolbar button that opens the omnibar in a flyout under it.</summary>
    private sealed class SearchButton(ShellSearchProps props) : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var viewport = UseContext(Viewport.Size);
            var model = props.Model;
            var style = ToolStyleFor(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            float flyoutWidth = MathF.Max(0f, MathF.Min(WinUiFrameRules.SearchFlyoutMaxW, viewport.Width - 2f * WinUiFrameRules.SearchFlyoutMargin));

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
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
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
                IconButton.Create(Icons.Search, Toggle, style) with { Key = WinUiFrameRules.SearchButtonKey, OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Theme.Winui.Frame.Search));
        }
    }

    /// <summary>The AutoSuggestBox (focus, editing, accessibility, popup lifetime) with Wavee's artwork rows. The text IS the omnibar's text cell.</summary>
    private sealed class Omnibar(ShellSearchProps props, TemplateParts? parts, float maxWidth,
        AutoSuggestBoxSuggestionPresentation presentation, Action? afterChoose = null) : Component
    {
        // The request in flight for the current generation; a superseding keystroke, a clear and an unmount cancel it.
        readonly OmnibarModel.Fetch _fetch = props.Model.NewFetch();

        public override Element Render()
        {
            var model = props.Model;
            var text = props.TextCell();
            var post = UsePost();
            var highlight = model.Highlight;
            _ = model.Version.Value;
            string typed = text.Value.Trim();

            // The keystroke edge, undebounced: Pending starts now, so "No results found" never flashes between a keystroke and its request.
            UseEffect(() =>
            {
                if (model.Begin(typed)) _fetch.Cancel();
            }, typed);

            // The fetch edge: the generation settles 150 ms after the last change. A re-mount re-issues a Pending generation whose request died.
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
            var presenter = new AutoSuggestBoxPresenter(
                Build: context =>
                {
                    var body = ViewSlots.View(ViewKeys.ShellSearchSuggestions, new SearchSuggestionsProps
                    {
                        Source = source,
                        Width = context.Width,
                        Choose = selection => { if (InvokeSelection(selection)) context.Close(); },
                        Close = context.Close,
                    });
                    return presentation == AutoSuggestBoxSuggestionPresentation.Popup
                        ? Embed.Comp(() => new SuggestionsOpenMark(model, body))
                        : body;
                },
                MoveSelection: delta => model.MoveHighlight(delta),
                SubmitSelection: () => InvokeSelection(highlight.Peek()),
                ResetSelection: () => model.ResetHighlight());

            // The stock AutoSuggestBox: 32 tall, the control radius, the text-box chrome (the bottom accent line on focus).
            // Small's field (filling the middle) reads "Search"; the centred 520 / 640 field and the flyout the long placeholder (Shell-Top).
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
            bool fills = WinUiTiers.TitleBarOf(facts.Value.Tiers).SearchFills && presentation == AutoSuggestBoxSuggestionPresentation.Popup;
            string placeholder = fills ? Loc.Get(Strings.Theme.Winui.Frame.Search) : Loc.Get(Strings.Shell.SearchPlaceholder);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    AutoSuggestBox.Create(Array.Empty<string>(), placeholder,
                        grow: 1f, maxFillWidth: maxWidth, text: text, onQuerySubmitted: Submit,
                        minHeight: WinUiFrameRules.SearchH, presenter: presenter, parts: parts,
                        chrome: AutoSuggestBoxChrome.Standard, suggestionPresentation: presentation, completion: completion),
                ],
            };
        }
    }

    /// <summary>The popup body, rendered BY the store's state (W-Flyouts).</summary>
    private sealed class SuggestionsPopup(SearchSuggestionsProps props) : Component
    {
        public override Element Render()
        {
            var source = props.Source;
            string typed = source.Text.Value.Trim();
            var answer = source.Answer();
            var state = answer.State;
            int highlighted = source.Highlight.Value;
            float w = props.Width.Value > 0f ? props.Width.Value : WinUiFrameRules.FallbackPopupW;

            var rows = new List<Element>(answer.Queries.Count + answer.Rows.Count + 1);
            int index = 0;
            int queryRows = answer.Queries.Count;
            for (int i = 0; i < queryRows; i++, index++)
                rows.Add(QueryRow(answer.Queries[i], typed, index, highlighted == index));
            int richRows = answer.Rows.Count;
            for (int i = 0; i < richRows; i++, index++)
            {
                if (i == 0 && rows.Count > 0) rows.Add(new BoxEl { Height = 1f, Margin = WinUiFrameRules.DividerMargin, Fill = Tok.StrokeDividerDefault });
                var rich = answer.Rows[i];
                int at = index;
                rows.Add(Embed.Comp(() => new SuggestionRow(rich, at, source.Highlight, props.Choose, props.Close))
                    with { Key = "omni-row:" + at + ":" + rich.Uri });
            }

            if (rows.Count > 0) { s_memory.Value.QueryRows = queryRows; s_memory.Value.RichRows = richRows; s_memory.Value.WasNotice = false; }
            else if (state is SearchAnswerState.Empty or SearchAnswerState.Failed) { s_memory.Value.QueryRows = s_memory.Value.RichRows = 0; s_memory.Value.WasNotice = true; }

            Element body;
            if (rows.Count == 0)
            {
                body = state switch
                {
                    SearchAnswerState.Empty => Notice(w, Loc.Get(Strings.Search.NoResults), null),
                    SearchAnswerState.Failed => Notice(w, Loc.Get(Strings.Search.SuggestFailed), Button.Subtle(Loc.Get(Strings.Common.Retry), source.Retry)),
                    SearchAnswerState.Pending => PendingBody(w),
                    _ => new BoxEl { Width = w, MinWidth = w, MinHeight = AutoSuggestBox.ItemMinHeight },
                };
            }
            else
            {
                body = new ScrollEl
                {
                    Width = w, MinWidth = w, MaxHeight = source.PopupBodyMaxHeight, ContentSized = true,
                    Content = new BoxEl { Direction = 1, Width = w, MinWidth = w, Children = rows.ToArray() },
                };
            }

            // The progress slot is ALWAYS the first child: it fades, never mounts or unmounts, so a keystroke moves nothing.
            return new BoxEl
            {
                Direction = 1, Width = w, MinWidth = w, Padding = WinUiFrameRules.PopupPadding,
                Layout = Design.Reduced ? null : WinUiFrameRules.PopupHeightMotion,
                Children =
                [
                    new BoxEl
                    {
                        Key = "omni-progress", Height = WinUiFrameRules.ProgressSlotH, Shrink = 0f, ClipToBounds = true, HitTestVisible = false,
                        Opacity = state == SearchAnswerState.Pending ? 1f : 0f, Transition = MotionTok.ControlFast,
                        Children = state == SearchAnswerState.Pending ? [ProgressBar.Indeterminate(w)] : [],
                    },
                    body,
                ],
            };
        }

        // The previous settled answer's row counts: what a Pending body reserves while nothing is on screen (root-lifetime).
        sealed class AnswerMemory { public int QueryRows, RichRows; public bool WasNotice; }
        static readonly RootState<AnswerMemory> s_memory = new("WinUi.Omnibar.AnswerMemory", static () => new AnswerMemory());

        /// <summary>Skeleton rows for a Pending request with nothing to show: the previous answer's counts (else a typical answer).</summary>
        private Element PendingBody(float w)
        {
            var source = props.Source;
            var m = s_memory.Value;
            float height = source.PendingBodyHeight(m.QueryRows, m.RichRows, m.WasNotice);
            if (m.WasNotice)
                return new BoxEl
                {
                    Direction = 0, Width = w, MinWidth = w, Height = height, AlignItems = FlexAlign.Center,
                    Padding = WinUiFrameRules.SkeletonRowPadding, ClipToBounds = true,
                    Children = [Controls.PendingBar(WinUiFrameRules.SkeletonNoticeBarW, WinUiFrameRules.SkeletonBarH)],
                };
            var (q, r) = source.PendingSkeletonRows(m.QueryRows, m.RichRows);
            var kids = new List<Element>(q + r + 1);
            for (int i = 0; i < q; i++)
                kids.Add(new BoxEl
                {
                    Height = source.QueryRowHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = WinUiFrameRules.SkeletonRowPadding,
                    Children = [Controls.PendingBar(WinUiFrameRules.SkeletonQueryBarW + (i % WinUiFrameRules.SkeletonPeriod) * WinUiFrameRules.SkeletonQueryBarStep, WinUiFrameRules.SkeletonBarH)],
                });
            for (int i = 0; i < r; i++)
            {
                if (i == 0 && q > 0) kids.Add(new BoxEl { Height = 1f, Shrink = 0f, Margin = WinUiFrameRules.DividerMargin, Fill = Tok.StrokeDividerDefault });
                kids.Add(new BoxEl
                {
                    Height = source.RichRowHeight, Shrink = 0f, Direction = 0, Gap = WinUiFrameRules.SkeletonRichGap, AlignItems = FlexAlign.Center,
                    Padding = WinUiFrameRules.SkeletonRowPadding,
                    Children =
                    [
                        new BoxEl { Width = WinUiFrameRules.SkeletonArt, Height = WinUiFrameRules.SkeletonArt, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                        new BoxEl
                        {
                            Direction = 1, Gap = WinUiFrameRules.SkeletonTextGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                Controls.PendingBar(WinUiFrameRules.SkeletonRichBarW + (i % WinUiFrameRules.SkeletonPeriod) * WinUiFrameRules.SkeletonRichBarStep, WinUiFrameRules.SkeletonBarH),
                                Controls.PendingBar(WinUiFrameRules.SkeletonSubBarW, WinUiFrameRules.SkeletonSubBarH),
                            ],
                        },
                    ],
                });
            }
            return new BoxEl { Direction = 1, Width = w, MinWidth = w, Height = height, ClipToBounds = true, Children = kids.ToArray() };
        }

        /// <summary>A recent query: the history glyph, the text with the typed run semibold; a 36 row, subtle hover (W-Flyouts).</summary>
        Element QueryRow(string text, string typed, int index, bool selected) => new BoxEl
        {
            MinHeight = AutoSuggestBox.ItemMinHeight, AlignItems = FlexAlign.Center,
            Padding = WinUiFrameRules.QueryRowPadding, Margin = WinUiFrameRules.RowMargin, Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem, AllowFocusOnInteraction = false,
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => props.Choose(index),
            Children = QueryContent(text, typed),
        };
    }

    /// <summary>One entity row: the shared media surface in SLOT mode (the field keeps focus; the arrow-key cursor is the row's "focused").</summary>
    private sealed class SuggestionRow(SearchRichRow row, int index, IReadSignal<int> highlight, Action<int> choose, Action? close) : Component
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
                with { Role = AutomationRole.MenuItem, Margin = WinUiFrameRules.RowMargin, AllowFocusOnInteraction = false };
        }
    }

    /// <summary>Marks the field form's suggestions list open while it is mounted.</summary>
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

    /// <summary>One sentence in the row slot, optionally with a trailing affordance (the failure's Retry).</summary>
    private static Element Notice(float width, string text, Element? trailing) => new BoxEl
    {
        Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = WinUiFrameRules.NoticePadding(trailing is not null),
        Children = trailing is null
            ? [new TextEl(text) { Size = WinUiFrameRules.SuggestTextSize, Color = Tok.TextPrimary, Grow = 1f }]
            : [new TextEl(text) { Size = WinUiFrameRules.SuggestTextSize, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, trailing],
    };

    /// <summary>A query row's content: the history glyph, then the text with the typed run semibold (case-insensitive).</summary>
    private static Element[] QueryContent(string text, string typed)
    {
        var kids = new List<Element>(4)
        {
            Icon(Icons.History, WinUiFrameRules.QueryGlyphSize, Tok.TextSecondary) with { Margin = WinUiFrameRules.QueryGlyphMargin },
        };
        int at = typed.Length > 0 ? text.IndexOf(typed, StringComparison.OrdinalIgnoreCase) : -1;
        if (at < 0)
        {
            kids.Add(new TextEl(text) { Size = WinUiFrameRules.SuggestTextSize, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            return kids.ToArray();
        }
        if (at > 0) kids.Add(Segment(text[..at], match: false, grow: false));
        kids.Add(Segment(text.Substring(at, typed.Length), match: true, grow: false));
        int after = at + typed.Length;
        kids.Add(after < text.Length ? Segment(text[after..], match: false, grow: true) : new BoxEl { Grow = 1f });
        return kids.ToArray();

        static Element Segment(string s, bool match, bool grow) => new TextEl(s)
        {
            Size = WinUiFrameRules.SuggestTextSize, Weight = match ? WinUiFrameRules.MatchWeight : WinUiFrameRules.RestWeight,
            Color = Tok.TextPrimary, Grow = grow ? 1f : 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}
