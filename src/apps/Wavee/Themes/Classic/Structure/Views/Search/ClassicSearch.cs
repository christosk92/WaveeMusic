// ── Themes/Classic/Structure/Views/Search/ClassicSearch.cs — Classic's `Shell.Search` and `Shell.SearchSuggestions` (T4b) ─────
//
// From `Shell.Masthead.UI.cs`: `SearchField`, `SearchFlyoutButtonView`, `RichOmnibar`, `SuggestionsPopup`, `OmniRow`, `OmniNotice`
// and `OmniQueryContent`. The core owns the omnibar's model (`OmnibarModel`: the suggestion store over the request lifecycle, the
// keystroke and fetch edges, the focus tickets, the keyboard cursor and every verb) and publishes the answer the popup draws
// (`SearchAnswer`: the state, the visible completions and the rich rows with their facts); the picture is Classic's.
//
//   Shell.Search            the omnibar field (`SearchForm.Field`) or the magnifier and its flyout (`SearchForm.Icon`): two mounts of
//                           ONE omnibar over ONE model, so a search-mode flip keeps the rows, the pending generation and the cursor
//   Shell.SearchSuggestions the popup body, rendered BY the store's state: its one sentence, "No results found", is reserved for a
//                           confirmed empty answer; pending is the always-mounted progress slot over the previous rows (or skeleton
//                           rows in the height the previous answer took when there are none), failed is a retry offer, idle
//                           (closing) is an empty body
//
// Elements, keys, transitions and the focus handling are exactly what the shell built before the move: the field takes the caret from
// a focus ticket (Ctrl+F, or the allocator handing it to the new form across a mode flip), the icon opens its flyout from the same
// ticket, and a rich row is the shared media surface in slot mode (the field keeps focus; the arrow-key cursor is the row's "focused").

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

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's search.</summary>
internal static class ClassicSearch
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellSearch"/>.</summary>
    public static Element Template(ShellSearchProps props, in ViewContext ctx)
    {
        if (props.Form != SearchForm.Field) return Embed.Comp(() => new SearchFlyoutButtonView(props));
        var tiers = ctx.TierReader();
        return Embed.Comp(() => new SearchField(props, tiers));
    }

    /// <summary>The registered template of <see cref="ViewKeys.ShellSearchSuggestions"/>.</summary>
    public static Element SuggestionsTemplate(SearchSuggestionsProps props, in ViewContext ctx)
        => Embed.Comp(() => new SuggestionsPopup(props));

    /// <summary>The island-button footprint: 40×44, the bar's own nav metric. A property: the default style captures theme brushes.</summary>
    private static IconButton.Style ChromeButtonStyle
        => IconButton.DefaultStyle with { Size = ClassicChromeLayout.ButtonSize, Height = ClassicChromeLayout.ButtonHeight };

    private sealed class SearchField(ShellSearchProps props, Func<TierFacts> tiers) : Component
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
                // PartRoot is the chrome, not the editor: focusing it paints a ring that cannot type. FirstFocusableIn lands
                // on the editable text; the root's focus handler still fires because focus bubbles.
                if (field.Value.IsNull || !model.ConsumeFocusTicket(ticket)) return;
                var editor = hooks.FirstFocusableIn?.Invoke(field.Value) ?? NodeHandle.Null;
                if (!editor.IsNull) hooks.FocusNode?.Invoke(editor, true);
            }, DepKey.From(ticket));

            // This component asks a FIELD WIDTH, never a search WIDTH-OR-FORM (the form-vs-width split, #88). The title bar's
            // centre island keys ITS mount/unmount decision on the search form, but the engine's `TitleBar` only re-invokes that
            // decision when `ContentVersion` changes, while THIS `Render()` re-runs on every allocation publish regardless — so a
            // mode flip to Icon can be visible here for up to one frame before the host gets around to unmounting this
            // component. Asking for "whichever form is current" would answer that frame with the icon's fixed 44 (a real text
            // field, 44 DIP wide, rendering the placeholder as a clipped "Sear" stub that then latches until a resize, because
            // under the elastic tabs lane the bar's measured centre width is itself a feedback of whatever this field just
            // asked for). `FieldWidth` cannot answer 44: it never reads the form, and it clamps whatever the allocation holds —
            // including the icon form's own 44 — back up to the field's minimum, so the worst case across a mode flip is a field
            // briefly wider than its lane, never a clipped stub. The core reads no measurement (the centre column hugs this field).
            //
            // FOCUSED (tuned F6): while the field has focus or its suggestions are open, `FieldWidth` answers the allocator's focused
            // width (+80, never closer than 12 to the tabs or the avatar). The box EASES between the two widths as one reflow, so its
            // text moves with it. Only the focus flip animates: a rest-width change without one (a resize, a tab added) snaps with its
            // cluster, so the old wider box never overruns the row for the motion's length. A re-render keeps a motion in flight.
            // A TIER CROSSING is no resize step: the first rest-width change after one (Huge's wider field at the 1920 edge; the allocation
            // can publish it a render after the tier) eases the same way, so the field never steps 60 DIP on the crossing frame.
            float width = props.FieldWidth();
            bool expanded = model.FieldExpanded;
            var lastExpanded = UseRef(expanded);
            bool flipped = lastExpanded.Value != expanded;
            lastExpanded.Value = expanded;
            WidthTier window = tiers().Window;
            var lastWindow = UseRef(window);
            var crossing = UseRef(false);
            if (lastWindow.Value != window) crossing.Value = true;
            lastWindow.Value = window;
            var lastWidth = UseRef(width);
            // The box always carries a width transition (the eased one or the snap), so the engine holds its rect before every commit: a
            // transition the same render first set has no "before" to move from, and the change it should have eased landed at once.
            var motion = UseRef(ClassicChromeLayout.FieldWidthSnap);
            bool resized = lastWidth.Value != width;
            if (flipped || (crossing.Value && resized))
                motion.Value = Design.Reduced ? ClassicChromeLayout.FieldWidthSnap : ClassicChromeLayout.FieldWidthMotion;
            else if (resized) motion.Value = ClassicChromeLayout.FieldWidthSnap;
            if (resized) crossing.Value = false;
            lastWidth.Value = width;
            // A COLUMN, so the omnibar is STRETCHED to this slot's width (the cross axis). In fill mode it sizes its
            // editor from its own last bounds; in a row its width was its content's width, so one narrow frame (a visit
            // to the search page re-mounts the field) latched it at the query button's ~37 DIP inside a 330-DIP slot:
            // a "Search" stub overflowing a tiny box that no resize undid. Justify centres it vertically instead.
            // The box sits under an auto-sized root: the view's anchors mirror their root's DECLARED width, so a field that was the root
            // left the host (and the regions either side) at the new width at once while the field eased inside it.
            return new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "chrome-search-field", Direction = 1, Shrink = 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Stretch,
                        Width = width, Animate = motion.Value,
                        // The settled width re-pushes the title bar's regions (the model counts a settle; the core folds it in).
                        OnBoundsChanged = r => model.ReportFieldWidth(r.W, width),
                        Children =
                        [
                            // No fill cap (0): the box above sets the width, and the focused width runs past the rest maximum.
                            Embed.Comp(() => new RichOmnibar(props, parts, 0f,
                                AutoSuggestBoxSuggestionPresentation.Popup)),
                        ],
                    },
                ],
            };
        }
    }

    private sealed class SearchFlyoutButtonView(ShellSearchProps props) : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var viewport = UseContext(Viewport.Size);
            var model = props.Model;
            var budget = ShellPolicy.Now.Chrome.Budget;
            float flyoutWidth = MathF.Max(budget.SearchIconW,
                MathF.Min(budget.SearchMaxW, viewport.Width - 2f * Spacing.M));

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
                            Embed.Comp(() => new RichOmnibar(props, null, flyoutWidth, AutoSuggestBoxSuggestionPresentation.Inline,
                                afterChoose: CloseFlyout)),
                        ],
                    },
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    {
                        ConstrainToRootBounds = true,
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
                IconButton.Create(Icons.Search, Toggle, ChromeButtonStyle)
                    with { Key = "chrome-search-button", Margin = ClassicChromeLayout.ButtonMargin, OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Nav.Search));
        }
    }

    /// <summary>The real AutoSuggestBox (focus, editing, accessibility, popup lifetime) with Wavee's artwork-aware rows.
    /// The text IS the omnibar's text cell, synced to the route on navigation only.</summary>
    private sealed class RichOmnibar(ShellSearchProps props, TemplateParts? parts, float maxWidth,
        AutoSuggestBoxSuggestionPresentation presentation, Action? afterChoose = null) : Component
    {
        // The request in flight for the current generation (this form's slot). A superseding keystroke, a clear and an
        // unmount cancel it; the store drops whatever a cancelled request would have said, so cancelling is only ever a saving.
        readonly OmnibarModel.Fetch _fetch = props.Model.NewFetch();

        public override Element Render()
        {
            var model = props.Model;
            var text = props.TextCell();
            var post = UsePost();
            var highlight = model.Highlight;
            _ = model.Version.Value;
            string typed = text.Value.Trim();

            // THE KEYSTROKE EDGE, undebounced: Pending starts NOW, so "No results found" never flashes between a keystroke
            // and its request.
            UseEffect(() =>
            {
                if (model.Begin(typed)) _fetch.Cancel();
            }, typed);

            // The fetch edge: the GENERATION settles (a retype to the same text is a new generation and must be answered),
            // 150 ms after the last change. A re-mount finds a Pending generation whose request died and re-issues it.
            int settled = UseDebouncedValue(() => { _ = model.Version.Value; return model.QueryGeneration; },
                AutoSuggestBox.TextChangedDebounceMs).Value;
            UseEffect(() =>
            {
                if (!model.NeedsFetch(settled)) return;
                _ = _fetch.Start(post, settled, model.QueryText);
            }, DepKey.From(settled));
            // Unmount: the request dies with the component; the store keeps Pending so the next mount re-issues it.
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
                    // The field form's list holds the field at its focused width while it is open (a row press may take focus).
                    return presentation == AutoSuggestBoxSuggestionPresentation.Popup
                        ? Embed.Comp(() => new SuggestionsOpenMark(model, body))
                        : body;
                },
                MoveSelection: delta => model.MoveHighlight(delta),
                SubmitSelection: () => InvokeSelection(highlight.Peek()),
                ResetSelection: () => model.ResetHighlight());

            // Stock metrics: a 32-DIP field at the control corner radius with the control-default chrome. Wrapped in a
            // growing COLUMN so the box is STRETCHED to this component's width: a fill-mode box contributes only its query
            // button to its measured width, so laid out as a row's content it stayed ~37 DIP once one narrow frame
            // (leaving the search page or full screen re-mounts the title bar's field) set its self-measured width there.
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    AutoSuggestBox.Create(Array.Empty<string>(), Loc.Get(Strings.Shell.SearchPlaceholder),
                        grow: 1f, maxFillWidth: maxWidth, text: text, onQuerySubmitted: Submit,
                        minHeight: ClassicChromeLayout.FieldMinHeight, cornerRadius: 0f, presenter: presenter, parts: parts,
                        chrome: AutoSuggestBoxChrome.Standard, suggestionPresentation: presentation, completion: completion,
                        // A field that MOUNTS holding the query (a tier crossing on a results page swaps the field form) opens its list only when
                        // the omnibar was live as it swapped (focused, or its list open): the page's query is no fresh edit (Zune's B4 fix, audit B6b).
                        openOnSeed: presentation == AutoSuggestBoxSuggestionPresentation.Inline || model.SearchFocused.Peek() || model.SuggestionsOpen.Peek()),
                ],
            };
        }
    }

    /// <summary>The popup body, rendered BY the store's state. Its one sentence, "No results found", is reserved for a
    /// confirmed empty answer; pending is the always-mounted progress slot over the previous rows (or skeleton rows in the
    /// height the previous answer took when there are none), failed is a retry offer. Idle (closing) is an empty
    /// body.</summary>
    private sealed class SuggestionsPopup(SearchSuggestionsProps props) : Component
    {
        public override Element Render()
        {
            var source = props.Source;
            string typed = source.Text.Value.Trim();
            var answer = source.Answer();
            var state = answer.State;
            int highlighted = source.Highlight.Value;
            // The popup is exactly the box's width and hangs from its left edge (tuned F6: BottomStretch on the field, no floor).
            float w = props.Width.Value > 0f ? props.Width.Value : ClassicChromeLayout.FallbackPopupWidth;

            // No client-side re-filter: the source's fuzzy matching is authoritative; staleness is handled at publish.
            var rows = new List<Element>(answer.Queries.Count + answer.Rows.Count + 1);
            int index = 0;
            int queryRows = answer.Queries.Count;
            for (int i = 0; i < queryRows; i++, index++)
                rows.Add(QueryRow(answer.Queries[i], typed, index, highlighted == index));
            int richRows = answer.Rows.Count;
            for (int i = 0; i < richRows; i++, index++)
            {
                if (i == 0 && rows.Count > 0) rows.Add(new BoxEl { Height = 1f, Margin = ClassicChromeLayout.DividerMargin, Fill = Tok.StrokeDividerDefault });
                rows.Add(RichRow(answer.Rows[i], index));
            }

            // What the last SETTLED answer took (rows, or a notice), so a Pending body with nothing to show reserves the
            // same space: a no-match query is a notice-high answer, not a stale list.
            if (rows.Count > 0) { s_lastQueryRows = queryRows; s_lastRichRows = richRows; s_lastWasNotice = false; }
            else if (state is SearchAnswerState.Empty or SearchAnswerState.Failed) { s_lastQueryRows = s_lastRichRows = 0; s_lastWasNotice = true; }

            Element body;
            if (rows.Count == 0)
            {
                body = state switch
                {
                    SearchAnswerState.Empty => OmniNotice(w, Loc.Get(Strings.Search.NoResults), null),
                    SearchAnswerState.Failed => OmniNotice(w, Loc.Get(Strings.Search.SuggestFailed),
                        Button.Subtle(Loc.Get(Strings.Common.Retry), source.Retry)),
                    // Pending: skeleton rows in the reserved height, so the answer fills space instead of growing it.
                    SearchAnswerState.Pending => PendingBody(w),
                    // Idle (closing): no sentence.
                    _ => new BoxEl { Width = w, MinWidth = w, MinHeight = AutoSuggestBox.ItemMinHeight },
                };
            }
            else
            {
                body = new ScrollEl
                {
                    Width = w, MinWidth = w, MaxHeight = source.PopupBodyMaxHeight, ContentSized = true,
                    Content = new BoxEl { Direction = 1, Width = w, MinWidth = w, Margin = ClassicChromeLayout.RowListInset, Children = rows.ToArray() },
                };
            }

            // The progress bar's 3-DIP slot is ALWAYS the first child: it fades, it never mounts or unmounts, so a keystroke
            // moves nothing; the indeterminate bar inside it runs only while a request is pending. The popup chrome supplies the plate, border, corners, shadow and clip; the root's height eases.
            return new BoxEl
            {
                Direction = 1, Width = w, MinWidth = w, Padding = ClassicChromeLayout.PopupPadding,
                Layout = Design.Reduced ? null : ClassicChromeLayout.BodyHeightMotion,
                Children =
                [
                    new BoxEl
                    {
                        Key = "omni-progress", Width = w, Height = ClassicChromeLayout.ProgressSlotHeight, Shrink = 0f, ClipToBounds = true,
                        HitTestVisible = false, Opacity = state == SearchAnswerState.Pending ? 1f : 0f, Transition = MotionTok.ControlFast,
                        Children = state == SearchAnswerState.Pending ? [ProgressBar.Indeterminate(w)] : [],
                    },
                    body,
                ],
            };
        }

        // The previous answer's row counts (UI thread only): what a Pending body reserves while nothing is on screen.
        // ROOT-LIFETIME (RootScope.cs): what the MOUNTED popup measured; a remounted root starts with the typical-answer default.
        sealed class AnswerMemory { public int QueryRows, RichRows; public bool WasNotice; }
        static readonly RootState<AnswerMemory> s_answerMemory = new("Omnibar.AnswerMemory", static () => new AnswerMemory());
        static int s_lastQueryRows { get => s_answerMemory.Value.QueryRows; set => s_answerMemory.Value.QueryRows = value; }
        static int s_lastRichRows { get => s_answerMemory.Value.RichRows; set => s_answerMemory.Value.RichRows = value; }
        static bool s_lastWasNotice { get => s_answerMemory.Value.WasNotice; set => s_answerMemory.Value.WasNotice = value; }   // the last settled answer was Empty / Failed: one notice slot high

        /// <summary>Skeleton rows for a Pending request with no rows to show: the previous answer's counts (else a typical
        /// answer), each row the real row's height, inert geometry only (<see cref="Controls.PendingBar"/>). The body is
        /// exactly the core's pending-body height high and clips its last row, so the answer fills the space; after
        /// a notice (Empty / Failed) it is one notice slot.</summary>
        private Element PendingBody(float w)
        {
            var source = props.Source;
            float height = source.PendingBodyHeight(s_lastQueryRows, s_lastRichRows, s_lastWasNotice);
            if (s_lastWasNotice)
                return new BoxEl
                {
                    Direction = 0, Width = w, MinWidth = w, Height = height, AlignItems = FlexAlign.Center,
                    Padding = ClassicChromeLayout.SkeletonNoticePadding, ClipToBounds = true,
                    Children = [Controls.PendingBar(ClassicChromeLayout.SkeletonNoticeBarW, ClassicChromeLayout.SkeletonBarH)],
                };
            var (q, r) = source.PendingSkeletonRows(s_lastQueryRows, s_lastRichRows);
            var kids = new List<Element>(q + r + 1);
            for (int i = 0; i < q; i++)
                kids.Add(new BoxEl
                {
                    Height = source.QueryRowHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = ClassicChromeLayout.SkeletonRowPadding,
                    Children =
                    [
                        Controls.PendingBar(ClassicChromeLayout.SkeletonQueryBarW
                            + (i % ClassicChromeLayout.SkeletonPeriod) * ClassicChromeLayout.SkeletonQueryBarStep, ClassicChromeLayout.SkeletonBarH),
                    ],
                });
            for (int i = 0; i < r; i++)
            {
                if (i == 0 && q > 0) kids.Add(new BoxEl { Height = 1f, Shrink = 0f, Margin = ClassicChromeLayout.DividerMargin, Fill = Tok.StrokeDividerDefault });
                kids.Add(new BoxEl
                {
                    Height = source.RichRowHeight, Shrink = 0f, Direction = 0, Gap = ClassicChromeLayout.SkeletonRichGap, AlignItems = FlexAlign.Center,
                    Padding = ClassicChromeLayout.SkeletonRowPadding,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = ClassicChromeLayout.SkeletonArt, Height = ClassicChromeLayout.SkeletonArt, Shrink = 0f,
                            Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                        },
                        new BoxEl
                        {
                            Direction = 1, Gap = ClassicChromeLayout.SkeletonTextGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                Controls.PendingBar(ClassicChromeLayout.SkeletonRichBarW
                                    + (i % ClassicChromeLayout.SkeletonPeriod) * ClassicChromeLayout.SkeletonRichBarStep, ClassicChromeLayout.SkeletonBarH),
                                Controls.PendingBar(ClassicChromeLayout.SkeletonSubBarW, ClassicChromeLayout.SkeletonSubBarH),
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

        Element QueryRow(string text, string typed, int index, bool selected) => new BoxEl
        {
            MinHeight = AutoSuggestBox.ItemMinHeight, AlignItems = FlexAlign.Center,
            Padding = ClassicChromeLayout.QueryRowPadding, Margin = ClassicChromeLayout.RowMargin, Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem, AllowFocusOnInteraction = false,   // the field keeps focus through a press
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => props.Choose(index),
            Children = OmniQueryContent(text, typed),
        };

        /// <summary>A rich row is the shared media surface in SLOT mode (<see cref="Controls.SlotSurface"/>): the field keeps
        /// focus, the arrow-key cursor (the highlight) is the row's "focused" fact, and Enter / a click is the
        /// <see cref="RowScope.OnInteraction"/> choose. Keyed by index + uri: the component's props freeze at mount.</summary>
        Element RichRow(SearchRichRow row, int index)
            => Embed.Comp(() => new OmniRow(row, index, props.Source.Highlight, props.Choose, props.Close))
                with { Key = "omni-row:" + index + ":" + row.Uri };
    }

    /// <summary>One rich flyout row. Synthesizes the <see cref="RowScope"/> a bound slot would hand its surface: the
    /// highlight IS the focus, tap / Enter / Space choose, the root never takes focus (the field keeps it).</summary>
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

            // The facts are built HERE, in the row's render: the entity's table row is allocated if unseen (UI thread).
            var f = row.Facts(close);
            var data = new Controls.CardData(f.Uri, f.Title,
                R.Type.TrackMeta(f.Meta)
                    with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                f.ImageUrl, () => choose(index), f.Play, Circular: f.Circular, Drag: f.Drag)
            {
                Trailing = f.Trailing,
                Menu = f.Menu,
            };

            return Controls.SlotSurface(scope, data, OmnibarRowRules.RowShape)
                with { Role = AutomationRole.MenuItem, Margin = ClassicChromeLayout.RowMargin, AllowFocusOnInteraction = false };
        }
    }

    /// <summary>Marks the field form's suggestions list open while it is mounted (<see cref="OmnibarModel.SuggestionsOpen"/>).</summary>
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
    private static Element OmniNotice(float width, string text, Element? trailing) => new BoxEl
    {
        Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = ClassicChromeLayout.NoticePadding(trailing is not null),
        Children = trailing is null
            ? [new TextEl(text) { Size = ClassicChromeLayout.TextSize, Color = Tok.TextPrimary, Grow = 1f }]
            : [new TextEl(text) { Size = ClassicChromeLayout.TextSize, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, trailing],
    };

    /// <summary>A query row: the search glyph, then the text with the typed run in bold (case-insensitive).</summary>
    private static Element[] OmniQueryContent(string text, string typed)
    {
        var kids = new List<Element>(4)
        {
            Icon(Icons.Search, ClassicChromeLayout.QueryGlyphSize, Tok.TextSecondary) with { Margin = ClassicChromeLayout.QueryGlyphMargin },
        };
        int at = typed.Length > 0 ? text.IndexOf(typed, StringComparison.OrdinalIgnoreCase) : -1;
        if (at < 0)
        {
            kids.Add(new TextEl(text) { Size = ClassicChromeLayout.TextSize, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            return kids.ToArray();
        }
        if (at > 0) kids.Add(Segment(text[..at], match: false, grow: false));
        kids.Add(Segment(text.Substring(at, typed.Length), match: true, grow: false));
        int after = at + typed.Length;
        kids.Add(after < text.Length ? Segment(text[after..], match: false, grow: true) : new BoxEl { Grow = 1f });
        return kids.ToArray();

        static Element Segment(string s, bool match, bool grow) => new TextEl(s)
        {
            Size = ClassicChromeLayout.TextSize, Weight = match ? ClassicChromeLayout.MatchWeight : ClassicChromeLayout.RestWeight,
            Color = match ? Tok.TextPrimary : Tok.TextSecondary,
            Grow = grow ? 1f : 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}
