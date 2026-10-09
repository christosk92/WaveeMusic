// ── Shell/Shell.Masthead.UI.cs ─────────────────────────────────────────────────────────────────────────────────────
// masthead band, material layer, omnibar + suggestion popup (a cross-owner contract with P's Search.cs)
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 950 lines
// Spec: ch 18 §4 ("each is a named surface, not optional chrome")
//
// THREE NAMED SURFACES the frame mounts once each:
//   · the MASTHEAD BAND ("Browse › Category") — one overlay above the keep-alive boundary, never a page's own copy, so a
//     page swap cannot double-expose it and crossing out of the family never changes the boundary's height;
//   · the MATERIAL LAYER — the one layer between live Mica and the chrome column: a flat page tint or Home's three
//     clipped radial washes, re-rendered at navigation rate only;
//   · the OMNIBAR — the field-mode field and the icon-mode flyout are two mounts of ONE omnibar over ONE suggestion
//     store, so a search-mode flip keeps the rows, the pending generation and the cursor. The SUGGESTION SOURCE is owner
//     P's (`Shell.Omnibar.Source`, Wave 5); with none installed the popup answers from the navigation log.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. THE MASTHEAD BAND (ch 18 W13) ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The one masthead, mounted by the content host as an overlay on its page-swap boundary.</summary>
    // MOUNT POINT (stage B contract)
    public static Element Masthead() => Embed.Comp(static () => new MastheadBand());

    sealed class MastheadBand : Component
    {
        static readonly MotionTokenDef Hide = MotionTokenDef.Eased(FadeThroughExitMs, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade);
        static readonly MotionTokenDef Show = MotionTokenDef.Eased(FadeThroughExitMs, Easing.SmoothOut, ReducedMotionPolicy.KeepFade);

        // An unknown family KEEPS the last trail and fades opacity; it never collapses to height 0 mid-swap.
        IReadOnlyList<Crumb> _heldTrail = [];
        string? _heldTitle;
        bool _heldToolsVisible, _heldToolsLoading;
        Route _heldRoute;

        public override Element Render()
        {
            var route = Current.Value;
            var published = Mastheads.For(route);
            var origin = Origins.For(route);
            // THE HOIST. The browse ROOT under the Zune band (PRESENTED style, so it lags the card's slide: Shell.UI.cs
            // NavStylePresenter) hands its title to the band's pivots: it takes the HELD-title path below — faded out, not hit-
            // testable, never collapsed — so the band's height never changes and the fade runs in the quiet commit.
            bool hoisted = PageHeadRules.Hoisted(Ui.PresentedNavStyle.Value, NameOf(route));
            string? title = null;
            IReadOnlyList<Crumb> trail = [];
            bool live = !hoisted && TryMasthead(route, published?.Title, origin, out title, out trail);
            if (live)
            {
                _heldTitle = title;
                _heldTrail = trail;
                _heldToolsVisible = published is { ToolsVisible: true };
                _heldToolsLoading = published is { ToolsLoading: true };
                _heldRoute = route;
            }

            if (_heldTitle is null) return new BoxEl { MinWidth = 0f, Opacity = 0f, HitTestVisible = false };

            float g = Ui.PageGutter.Value;

            // "Show all" resolves the LATEST delegate at click time: a re-publish that changes only the delegate never
            // re-renders the band.
            Element tools = _heldToolsVisible
                ? Button.Create(Loc.Get(Strings.Browse.ShowAll), RunTools, ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !_heldToolsLoading)
                : new BoxEl();

            return new BoxEl
            {
                Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
                // The page's own column: the shared gutter and the page head's top air (PageGeometry), so the title sits on
                // the same line as every PageHead title.
                Padding = new Edges4(g, PageGeometry.HeadTop, g, 0f),
                Opacity = live ? 1f : 0f,
                HitTestVisible = live,
                Transition = live ? Show : Hide,
                Children = [MastheadTitleRow(_heldTrail, _heldTitle), tools],
            };
        }

        void RunTools() => Mastheads.Peek(_heldRoute)?.ToolsAction?.Invoke();
    }

    /// <summary>The trail-as-title row: every crumb but the last dimmed and clickable, a <c>›</c> after each, then the
    /// CURRENT title under a stable key (Browse-home → category does not remount it). No parent ⇒ no prefix child at all
    /// (a zero-width first sibling still padded the row a rung right).</summary>
    static Element MastheadTitleRow(IReadOnlyList<Crumb> trail, string title)
    {
        int last = trail.Count - 1;
        var segs = new List<Element>(Math.Max(0, last) * 2);
        for (int i = 0; i < last; i++)
        {
            var crumb = trail[i];
            segs.Add(new BoxEl
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = crumb.Route is { } target ? () => GoTo(target) : null,
                Children =
                [
                    Design.Type.SurfaceDisplay(crumb.Label) with
                    {
                        Color = Tok.TextTertiary, HoverColor = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                    },
                ],
            });
            segs.Add(Design.Type.SurfaceDisplay("›") with { Color = Tok.TextTertiary, Shrink = 0f });
        }
        // The page head's title metrics (one line): the masthead reserve is PageGeometry.TitleLine, so a long title
        // ellipsises rather than wrapping to a second line the reserve never made room for.
        var current = Design.Type.PageTitle(title) with
        {
            Key = "masthead-current", Grow = 1f, Basis = 0f, Shrink = 1f,
        };
        if (segs.Count == 0)
            return new BoxEl { Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Children = [current] };
        var prefix = new BoxEl
        {
            Key = "masthead-prefix:" + trail[0].Label,
            Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.S, Shrink = 0f,
            Children = segs.ToArray(),
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Gap = Spacing.S,
            Children = [prefix, current],
        };
    }

    // ══ 2. THE MATERIAL LAYER (ch 00 §material, ch 18 W12) ════════════════════════════════════════════════════════

    /// <summary>The material between the window's base layer (live Mica — the root paints nothing) and the chrome column.
    /// A component, not inline elements: a GradientSpec is not a Prop (a wash changes only by re-rendering), and the
    /// implicit brush transition arms only on a re-rendered STATIC fill — so the tint cross-fades page to page.</summary>
    static Element MaterialLayer() => Embed.Comp(static () => new MaterialLayerView());

    sealed class MaterialLayerView : Component
    {
        /// <summary>Gradients carry no brush-fade channel, so a wash cross-fades BY MOUNT (keyed on its artwork). A VALUE
        /// under reduced motion, never a hook branch.</summary>
        static EnterExit? WashFade => Design.Reduced ? null : new EnterExit(Opacity: 0f, Active: true);

        public override Element Render()
        {
            var state = MaterialState.Value;                  // navigation rate
            var vp = UseContextSignal(Viewport.Size);         // read through BOUND sizes: a resize never re-renders this
            bool light = Tok.Theme == ThemeKind.Light;

            // Always mounted, even at the neutral ground, so the node is live across a change and the brush transition has
            // a colour to fade FROM. Never Transparent (premultiplied black drags the ramp dark).
            Element tint = new BoxEl
            {
                Key = "shell.material.tint", Grow = 1f, HitTestVisible = false,
                Fill = state.Tint ?? Design.Wash.NeutralGround,
                BrushTransitionMs = Design.Motion.Standard,
            };
            if (state.Wash is not { } wash)
                return new BoxEl { Grow = 1f, ZStack = true, HitTestVisible = false, Children = [tint] };

            bool rich = Prefs.Appearance.SurfaceWash() == WashLevel.Rich;
            var legs = new List<Element>(3);
            AddWash(legs, wash.Hero, Design.Wash.Hero, Design.Wash.HeroAlpha(light, rich), "shell.wash.hero", vp);
            AddWash(legs, wash.Weekly, Design.Wash.Weekly, Design.Wash.ShelfAlpha(light, rich), "shell.wash.weekly", vp);
            AddWash(legs, wash.Mix, Design.Wash.Mix, Design.Wash.ShelfAlpha(light, rich), "shell.wash.mix", vp);
            return new BoxEl
            {
                Grow = 1f, ZStack = true, HitTestVisible = false,
                Children =
                [
                    tint,
                    // THE WASHES STOP AT THE DOCK LINE: the dock paints nothing, so a peak landing under it read as "the
                    // dock has a gradient". An inset MARGIN keeps every placement ratio viewport-independent.
                    new BoxEl
                    {
                        Grow = 1f, ZStack = true, HitTestVisible = false, ClipToBounds = true,
                        Margin = new Edges4(0f, 0f, 0f, Design.Wash.HostBottomInset),
                        Children = legs.ToArray(),
                    },
                ],
            };
        }

        static void AddWash(List<Element> legs, WashLayer? layer, ShellWashPlacement p, float alpha, string key, IReadSignal<Size2> vp)
        {
            if (layer is not { } w) return;
            float wf = p.W, hf = p.H;
            legs.Add(new BoxEl
            {
                // Keyed on the ARTWORK: a re-grade remounts (the cross-fade); a theme flip keeps the node and re-records.
                Key = key + ":" + (w.ArtworkKey ?? ""),
                HitTestVisible = false,
                Width = Prop.Of(() => vp.Value.Width * wf),
                Height = Prop.Of(() => vp.Value.Height * hf),
                JustifySelf = p.AnchorRight ? FlexAlign.End : FlexAlign.Start,
                AlignSelf = p.AnchorBottom ? FlexAlign.End : FlexAlign.Start,
                // Straight-alpha stops: the transparent stop carries the wash's own RGB.
                Gradient = new GradientSpec(GradientShape.Radial, 0f,
                [
                    new GradientStop(0f, w.Color with { A = alpha }),
                    new GradientStop(p.FadeOffset, Design.Wash.Vanish(w.Color)),
                ])
                {
                    RadialCenter = p.Center,
                    RadialRadius = p.Radius,
                },
                Enter = WashFade,
                Exit = WashFade,
            });
        }
    }

    // ══ 3. THE OMNIBAR (ch 18 W15) ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The chrome row's ONE suggestion store: the lifecycle (<see cref="Omnibar.Query"/>), a version bumped on
    /// every change (the render subscription), and the keyboard cursor over the visible rows.</summary>
    sealed class OmnibarStore
    {
        public readonly Omnibar.Query Query = new();
        public readonly Signal<int> Version = new(0);
        public readonly Signal<int> Highlight = new(-1);

        public OmnibarStore() => Query.Changed += () => Version.Value = Version.Peek() + 1;
    }

    static readonly OmnibarStore s_omnibar = new();

    /// <summary>The search field has focus / the icon-mode flyout is open — the chrome allocator reads both to hand the
    /// caret to the new form across a mode flip.</summary>
    static readonly Signal<bool> s_searchFocused = new(false);
    static readonly Signal<bool> s_searchFlyoutOpen = new(false);

    /// <summary>The last focus ticket a search form consumed. A form mounting later (a mode flip) acts only on a NEWER
    /// ticket, so a Ctrl+F from minutes ago never pops the flyout open on mount.</summary>
    static int s_focusTicketHandled;

    /// <summary>The bar's flexible centre column: the field in Field mode, nothing in Icon mode (the icon sits in the
    /// caption-leading island). <paramref name="avail"/> is the bar's LIVE centre measurement.
    /// <para>This is the ONE mount/unmount decision for the field, and it reads the same <see cref="SearchMode"/> the
    /// allocator used to decide the FORM (Shell.Chrome.cs's form-vs-width split, #88) — never a width. It is,
    /// however, only re-evaluated when the engine's <c>TitleBar</c> re-invokes the bar's centre content, which is
    /// gated on <c>ContentVersion</c>, not on every <see cref="ChromeLayout"/> publish; a mode flip can therefore be
    /// visible to the already-mounted <see cref="SearchField"/>'s own <c>Render()</c> a frame before it reaches here.
    /// That is why <see cref="SearchField"/> asks only for a FIELD width (<see cref="Chrome.FieldWidthFor"/>) and
    /// never for "whichever form is current" — so the still-mounted field can never be laid out at the icon's
    /// 44.</para></summary>
    static Element CenterIsland(IReadSignal<float> avail)
        => ChromeLayout.Value.SearchMode == MergedSearchMode.Field
            ? Embed.Comp(() => new SearchField(avail)) with { Key = "chrome-search-host" }
            : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

    static Element SearchFlyoutButton() => Embed.Comp(static () => new SearchFlyoutButtonView()) with { Key = "chrome-search-button-host" };

    sealed class SearchField(IReadSignal<float> avail) : Component
    {
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var field = UseRef<NodeHandle>(default);
            var parts = UseMemo(() =>
            {
                var p = new TemplateParts();
                p[AutoSuggestBox.PartRoot] = b => b with
                {
                    OnRealized = h => field.Value = h,
                    OnFocusChanged = static f => s_searchFocused.SetIfChanged(f),
                };
                return p;
            }, DepKey.Empty);

            int ticket = SearchFocusRequest.Value;
            UseLayoutEffect(() =>
            {
                // PartRoot is the chrome, not the editor: focusing it paints a ring that cannot type. FirstFocusableIn lands
                // on the editable text; the root's focus handler still fires because focus bubbles.
                if (ticket <= s_focusTicketHandled || field.Value.IsNull) return;
                s_focusTicketHandled = ticket;
                var editor = hooks.FirstFocusableIn?.Invoke(field.Value) ?? NodeHandle.Null;
                if (!editor.IsNull) hooks.FocusNode?.Invoke(editor, true);
            }, DepKey.From(ticket));

            // This component asks a FIELD WIDTH, never a search WIDTH-OR-FORM (Shell.Chrome.cs's form-vs-width split,
            // #88). `CenterIsland` above keys ITS mount/unmount decision on `SearchMode`, but the engine's `TitleBar`
            // only re-invokes that decision when `ContentVersion` changes, while THIS `Render()` re-runs on every
            // `ChromeLayout` publish regardless — so a mode flip to Icon can be visible here for up to one frame
            // before the host gets around to unmounting this component. `Chrome.LaidOutSearchWidth` would answer that
            // frame with the icon's fixed 44 (a real text field, 44 DIP wide, rendering the placeholder as a clipped
            // "Sear" stub that then latches until a resize, because under the elastic tabs lane the bar's measured
            // centre width is itself a feedback of whatever this field just asked for). `Chrome.FieldWidthFor` cannot
            // answer 44: it never reads `SearchMode`, and it clamps whatever `SearchWidth` it is given — including the
            // icon form's own 44 — back up to `ChromeSearchMinW`. The measured centre width may still TRIM the result
            // (that part is real: the row genuinely narrowed), but it can never take the field below its floor, so the
            // worst case across a mode flip is a field briefly wider than its lane, never a clipped stub.
            float width = Chrome.FieldWidthFor(ChromeLayout.Value.SearchWidth, avail.Value);
            // A COLUMN, so the omnibar is STRETCHED to this slot's width (the cross axis). In fill mode it sizes its
            // editor from its own last bounds; in a row its width was its content's width, so one narrow frame (a visit
            // to the search page re-mounts the field) latched it at the query button's ~37 DIP inside a 330-DIP slot:
            // a "Search" stub overflowing a tiny box that no resize undid. Justify centres it vertically instead.
            return new BoxEl
            {
                Key = "chrome-search-field", Direction = 1, Shrink = 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Stretch,
                Width = width,
                Children = [Embed.Comp(() => new RichOmnibar(parts, Layout.ChromeSearchMaxW, AutoSuggestBoxSuggestionPresentation.Popup, allowNarrow: false))],
            };
        }
    }

    sealed class SearchFlyoutButtonView : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var viewport = UseContext(Viewport.Size);
            float flyoutWidth = MathF.Max(Layout.ChromeSearchIconW,
                MathF.Min(Layout.ChromeSearchMaxW, viewport.Width - 2f * Spacing.M));

            void CloseFlyout()
            {
                handle.Value?.Close();
                handle.Value = null;
                s_searchFlyoutOpen.SetIfChanged(false);
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
                            Embed.Comp(() => new RichOmnibar(null, flyoutWidth, AutoSuggestBoxSuggestionPresentation.Inline,
                                allowNarrow: true, afterChoose: CloseFlyout)),
                        ],
                    },
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    {
                        ConstrainToRootBounds = true,
                    });
                handle.Value = h;
                s_searchFlyoutOpen.SetIfChanged(true);
                h.ClosedAction = () =>
                {
                    handle.Value = null;
                    s_searchFlyoutOpen.SetIfChanged(false);
                };
            }

            int ticket = SearchFocusRequest.Value;
            UseLayoutEffect(() =>
            {
                if (ticket <= s_focusTicketHandled) return;
                s_focusTicketHandled = ticket;
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
                    with { Key = "chrome-search-button", Margin = ChromeButtonMargin, OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Nav.Search));
        }
    }

    /// <summary>The real AutoSuggestBox (focus, editing, accessibility, popup lifetime) with Wavee's artwork-aware rows.
    /// The text IS <see cref="SearchText"/>, synced to the route on navigation only.</summary>
    sealed class RichOmnibar(TemplateParts? parts, float maxWidth, AutoSuggestBoxSuggestionPresentation presentation,
        bool allowNarrow, Action? afterChoose = null) : Component
    {
        // The request in flight for the current generation. A superseding keystroke, a clear and an unmount cancel it; the
        // store drops whatever a cancelled request would have said, so cancelling is only ever a saving.
        CancellationTokenSource? _inflight;

        void CancelInflight()
        {
            _inflight?.Cancel();
            _inflight = null;
        }

        public override Element Render()
        {
            var post = UsePost();
            var query = s_omnibar.Query;
            var highlight = s_omnibar.Highlight;
            _ = s_omnibar.Version.Value;
            string typed = SearchText.Value.Trim();

            // THE KEYSTROKE EDGE, undebounced: Pending starts NOW, so "No results found" never flashes between a keystroke
            // and its request.
            UseEffect(() =>
            {
                int before = query.Generation;
                if (query.Begin(typed) != before) CancelInflight();
            }, typed);

            // The fetch edge: the GENERATION settles (a retype to the same text is a new generation and must be answered),
            // 150 ms after the last change. A re-mount finds a Pending generation whose request died and re-issues it.
            int settled = UseDebouncedValue(() => { _ = s_omnibar.Version.Value; return query.Generation; },
                AutoSuggestBox.TextChangedDebounceMs).Value;
            UseEffect(() =>
            {
                if (settled != query.Generation || !query.IsPending) return;
                Fetch(post, settled, query.Text);
            }, DepKey.From(settled));
            // Unmount: the request dies with the component; the store keeps Pending so the next mount re-issues it.
            UseEffect(() => (Action?)CancelInflight, DepKey.Empty);

            var completion = UseComputed(() =>
            {
                if (highlight.Value >= 0) return "";
                _ = s_omnibar.Version.Value;
                return Omnibar.Suggestions.GhostFor(SearchText.Value.Trim(), query.Suggestions.Queries) ?? "";
            });

            bool InvokeSelection(int selection)
            {
                var s = query.Suggestions;
                int queryRows = Omnibar.QueryRowCount(s);
                if (selection < 0 || selection >= Omnibar.SelectableCount(s)) return false;
                if (selection < queryRows)
                {
                    string chosen = s.Queries[selection];
                    SearchText.Value = chosen;
                    GoTo(Parse("search", chosen));
                }
                else
                {
                    ChooseOmnibarItem(s.Items[selection - queryRows], query.Text);
                }
                afterChoose?.Invoke();
                return true;
            }

            void Submit(string q)
            {
                GoTo(Parse("search", q.Trim()));
                afterChoose?.Invoke();
            }

            var presenter = new AutoSuggestBoxPresenter(
                Build: context => Embed.Comp(() => new SuggestionsPopup(context.Width,
                    selection => { if (InvokeSelection(selection)) context.Close(); }, context.Close, allowNarrow)),
                MoveSelection: delta => highlight.Value = Omnibar.MoveHighlight(highlight.Peek(), delta, Omnibar.SelectableCount(query.Suggestions)),
                SubmitSelection: () => InvokeSelection(highlight.Peek()),
                ResetSelection: () => highlight.Value = -1);

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
                        grow: 1f, maxFillWidth: maxWidth, text: SearchText, onQuerySubmitted: Submit,
                        minHeight: 32f, cornerRadius: 0f, presenter: presenter, parts: parts,
                        chrome: AutoSuggestBoxChrome.Standard, suggestionPresentation: presentation, completion: completion),
                ],
            };
        }

        void Fetch(Action<Action> post, int generation, string text)
        {
            CancelInflight();
            // No source installed (a Wave-4 build, `--fake`): the navigation log answers, synchronously.
            if (Omnibar.Source is not { } source)
            {
                s_omnibar.Query.Complete(generation, Omnibar.FromHistory(History.Store.Entries, text));
                return;
            }
            var cts = new CancellationTokenSource();
            _inflight = cts;
            _ = RunAsync(source, post, generation, text, cts.Token);
        }

        static async Task RunAsync(Func<string, CancellationToken, Task<Omnibar.Suggestions>> source, Action<Action> post,
            int generation, string text, CancellationToken ct)
        {
            try
            {
                var answer = await source(text, ct).ConfigureAwait(false);
                post(() => s_omnibar.Query.Complete(generation, answer));
            }
            catch (OperationCanceledException) { }   // superseded or torn down: the store has already moved on
            catch (Exception ex) { post(() => s_omnibar.Query.Fail(generation, ex)); }
        }
    }

    /// <summary>A rich row's choice: a track or an episode PLAYS (through the context seam); everything else navigates,
    /// and a genre carries its search-lookup origin so its masthead reads <c>"query" › Genre</c>.</summary>
    static void ChooseOmnibarItem(Omnibar.Item item, string? query)
    {
        if (Omnibar.ChoosePlays(item.Kind))
        {
            OnPlayContext?.Invoke(item.Uri);
            return;
        }
        var route = Omnibar.RouteFor(item);
        if (route.IsNone) return;
        GoTo(route, item.Kind == Omnibar.ItemKind.Genre ? Omnibar.GenreOrigin(query) : null);
    }

    /// <summary>The popup body, rendered BY the store's state. Its one sentence, "No results found", is reserved for a
    /// confirmed empty answer; pending is a progress bar over the previous rows, failed is a retry offer.</summary>
    sealed class SuggestionsPopup(IReadSignal<float> width, Action<int> choose, Action? close, bool allowNarrow) : Component
    {
        public override Element Render()
        {
            string typed = SearchText.Value.Trim();
            _ = s_omnibar.Version.Value;
            var query = s_omnibar.Query;
            var s = query.Suggestions;
            var state = query.State;
            int highlighted = s_omnibar.Highlight.Value;
            // A FLOOR, not just a fallback: the icon-mode anchor can be 44 DIP wide, and a popup may be wider than its anchor.
            float measured = width.Value > 0f ? width.Value : 720f;
            float w = allowNarrow ? measured : MathF.Max(measured, 400f);

            // No client-side re-filter: the source's fuzzy matching is authoritative; staleness is handled at publish.
            var rows = new List<Element>(Omnibar.MaxQueryRows + Omnibar.MaxRichRows + 1);
            int index = 0;
            int queryRows = Omnibar.QueryRowCount(s);
            for (int i = 0; i < queryRows; i++, index++)
                rows.Add(QueryRow(s.Queries[i], typed, index, highlighted == index));
            int richRows = Omnibar.RichRowCount(s);
            for (int i = 0; i < richRows; i++, index++)
            {
                if (i == 0 && rows.Count > 0) rows.Add(new BoxEl { Height = 1f, Margin = new Edges4(16f, 4f, 16f, 4f), Fill = Tok.StrokeDividerDefault });
                rows.Add(RichRow(s.Items[i], index));
            }

            Element body;
            if (rows.Count == 0)
            {
                body = state switch
                {
                    Omnibar.State.Empty => OmniNotice(w, Loc.Get(Strings.Search.NoResults), null),
                    Omnibar.State.Failed => OmniNotice(w, Loc.Get(Strings.Search.SuggestFailed),
                        Button.Subtle(Loc.Get(Strings.Common.Retry), static () => s_omnibar.Query.Retry())),
                    // Pending (the bar is the whole answer) and Idle (closing): no sentence.
                    _ => new BoxEl { Width = w, MinWidth = w, MinHeight = AutoSuggestBox.ItemMinHeight },
                };
            }
            else
            {
                body = new ScrollEl
                {
                    Width = w, MinWidth = w, MaxHeight = 560f, ContentSized = true,
                    Content = new BoxEl { Direction = 1, Width = w, MinWidth = w, Margin = new Edges4(-1f, 0f, -1f, 0f), Children = rows.ToArray() },
                };
            }

            // The popup chrome supplies the plate, border, corners, shadow and clip.
            return new BoxEl
            {
                Direction = 1, Width = w, MinWidth = w, Padding = new Edges4(0f, 2f, 0f, 2f),
                Children = state == Omnibar.State.Pending ? [ProgressBar.Indeterminate(w), body] : [body],
            };
        }

        Element QueryRow(string text, string typed, int index, bool selected) => new BoxEl
        {
            MinHeight = AutoSuggestBox.ItemMinHeight, AlignItems = FlexAlign.Center,
            Padding = new Edges4(12f, 0f, 8f, 0f), Margin = new Edges4(4f, 2f, 4f, 2f), Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem,
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => choose(index),
            Children = OmniQueryContent(text, typed),
        };

        /// <summary>A rich row is the shared media surface in SLOT mode (<see cref="Controls.SlotSurface"/>): the field keeps
        /// focus, the arrow-key cursor (<c>Highlight</c>) is the row's "focused" fact, and Enter / a click is the
        /// <see cref="RowScope.OnInteraction"/> choose. Keyed by index + uri: the component's props freeze at mount.</summary>
        Element RichRow(Omnibar.Item item, int index)
            => Embed.Comp(() => new OmniRow { Item = item, Index = index, Choose = choose, Close = close })
                with { Key = "omni-row:" + index + ":" + item.Uri.Text };
    }

    /// <summary>One rich flyout row. Synthesizes the <see cref="RowScope"/> a bound slot would hand its surface: the
    /// highlight IS the focus, tap / Enter / Space choose, the root never takes focus (the field keeps it).</summary>
    sealed class OmniRow : Component
    {
        public required Omnibar.Item Item;
        public required int Index;
        public required Action<int> Choose;
        public Action? Close;

        public override Element Render()
        {
            var item = Item;
            int index = Index;
            var choose = Choose;
            var close = Close;
            var at = UseSignal(index);
            var highlighted = UseComputed(() => s_omnibar.Highlight.Value == index);
            var scope = new RowScope(at, static () => false, () => highlighted.Value, static () => true,
                (trigger, _) => { if (trigger != ItemContainerTrigger.DoubleTap) choose(index); },
                static _ => { })
            { IsFocused = highlighted };

            var kind = item.Kind;
            var rf = item.Ref;                                  // allocates the table row if unseen (UI thread, render)
            string uri = item.Uri.Text;
            Action? play = Omnibar.CanPlay(kind)
                ? Omnibar.ChoosePlays(kind)
                    ? () =>
                    {
                        // A track/episode that is the current item toggles pause (the row verb every list row uses);
                        // else the host's play. Without a table slot, the uri toggle compares playables with the item.
                        if (kind == Omnibar.ItemKind.Track && rf.Kind == EntityKind.Track && rf.Slot > 0)
                            Track.Invoke(new Track(rf.Slot), () => OnPlayContext?.Invoke(item.Uri));
                        else if (OnPlayContext is { } start && ContextPlayRules.For(item.Uri.Id, Playback.ContextUri.Peek(), Playback.CurrentId.Peek(), Playback.Error.Peek()) == ContextPlayAction.Start)
                            start(item.Uri);
                        else
                            Playback.PlayOrToggleContext(item.Uri.Id);
                        close?.Invoke();
                    }
                    : () => { Playback.PlayOrToggleContext(uri); close?.Invoke(); }
                : null;

            Func<ContextMenuModel?>? menu = OmnibarRowRules.MenuOf(kind) switch
            {
                OmnibarRowRules.MenuKind.Track when !rf.IsNone => () =>
                {
                    Track.EnsureActions();
                    return Track.Menu([new Track(rf.Slot)], new Track.MenuOptions(ShowGoToAlbum: true));
                },
                OmnibarRowRules.MenuKind.Container => () => Menus.Container(ContainerTargetOf(item), item.ImageUrl, item.Subtitle),
                _ => null,
            };

            DragSource? drag = OmnibarRowRules.Drags(kind) && !rf.IsNone
                ? Drag.Source(() => kind == Omnibar.ItemKind.Track
                    ? new DragPayload(DragKind.Track, uri, uri, item.Title, rf, Tracks: [new Track(rf.Slot)], ArtUrl: item.ImageUrl)
                    : new DragPayload(Drag.KindOf(rf.Kind), uri, uri, item.Title, rf, ArtUrl: item.ImageUrl))
                : null;

            var data = new Controls.CardData(uri, item.Title,
                Design.Type.TrackMeta(Search.SubtitleText(OmniKindWord(item), item.Subtitle))
                    with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                item.ImageUrl, () => choose(index), play, Circular: Omnibar.IsCircular(kind), Drag: drag)
            {
                Trailing = rf.IsNone ? null : Search.TrailingOf(rf, uri, item.Title, compact: true),
                Menu = menu,
            };

            return Controls.SlotSurface(scope, data, OmnibarRowRules.RowShape)
                with { Role = AutomationRole.MenuItem, Margin = new Edges4(4f, 2f, 4f, 2f) };
        }

        static ActionTarget ContainerTargetOf(Omnibar.Item item) => item.Kind switch
        {
            Omnibar.ItemKind.Album => ActionTarget.ForAlbum(item.Uri, item.Title),
            Omnibar.ItemKind.Artist => ActionTarget.ForArtist(item.Uri, item.Title),
            Omnibar.ItemKind.Playlist => ActionTarget.ForPlaylist(item.Uri, item.Title),
            _ => ActionTarget.ForShow(item.Uri, item.Title),
        };
    }

    /// <summary>One sentence in the row slot, optionally with a trailing affordance (the failure's Retry).</summary>
    static Element OmniNotice(float width, string text, Element? trailing) => new BoxEl
    {
        Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(24f, 0f, trailing is null ? 24f : 12f, 0f),
        Children = trailing is null
            ? [new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }]
            : [new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, trailing],
    };

    /// <summary>The row's kind word: an album's release kind (Single / EP / Compilation) when the wire said it, else the
    /// kind's own label.</summary>
    static string OmniKindWord(Omnibar.Item item)
        => item.Kind == Omnibar.ItemKind.Album && item.ReleaseKind is { } release
            ? Detail.Text.KindLabel(release)
            : OmniTypeLabel(item.Kind);

    static string OmniTypeLabel(Omnibar.ItemKind kind) => Loc.Get(kind switch
    {
        Omnibar.ItemKind.Track => Strings.Search.TypeSong,
        Omnibar.ItemKind.Artist => Strings.Search.TypeArtist,
        Omnibar.ItemKind.Album => Strings.Search.TypeAlbum,
        Omnibar.ItemKind.Playlist => Strings.Search.TypePlaylist,
        Omnibar.ItemKind.Genre => Strings.Search.TypeGenre,
        Omnibar.ItemKind.Episode => Strings.Search.TypeEpisode,
        Omnibar.ItemKind.Podcast => Strings.Search.TypePodcast,
        Omnibar.ItemKind.Audiobook => Strings.Search.TypeAudiobook,
        _ => Strings.Search.TypeUser,
    });

    /// <summary>A query row: the search glyph, then the text with the typed run in bold (case-insensitive).</summary>
    static Element[] OmniQueryContent(string text, string typed)
    {
        var kids = new List<Element>(4)
        {
            Icon(Icons.Search, 16f, Tok.TextSecondary) with { Margin = new Edges4(0f, 0f, 12f, 0f) },
        };
        int at = typed.Length > 0 ? text.IndexOf(typed, StringComparison.OrdinalIgnoreCase) : -1;
        if (at < 0)
        {
            kids.Add(new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            return kids.ToArray();
        }
        if (at > 0) kids.Add(Segment(text[..at], match: false, grow: false));
        kids.Add(Segment(text.Substring(at, typed.Length), match: true, grow: false));
        int after = at + typed.Length;
        kids.Add(after < text.Length ? Segment(text[after..], match: false, grow: true) : new BoxEl { Grow = 1f });
        return kids.ToArray();

        static Element Segment(string s, bool match, bool grow) => new TextEl(s)
        {
            Size = 14f, Weight = (ushort)(match ? 700 : 400), Color = match ? Tok.TextPrimary : Tok.TextSecondary,
            Grow = grow ? 1f : 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}
