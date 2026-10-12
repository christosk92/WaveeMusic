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

    /// <summary>The band's frame insets — the browse surfaces' own frame, so the band's title sits on the page's column.</summary>
    const float MastheadFrameX = 36f, MastheadFrameTop = 32f;

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
            bool live = TryMasthead(route, published?.Title, origin, out var title, out var trail);
            if (live)
            {
                _heldTitle = title;
                _heldTrail = trail;
                _heldToolsVisible = published is { ToolsVisible: true };
                _heldToolsLoading = published is { ToolsLoading: true };
                _heldRoute = route;
            }

            if (_heldTitle is null) return new BoxEl { MinWidth = 0f, Opacity = 0f, HitTestVisible = false };

            // "Show all" resolves the LATEST delegate at click time: a re-publish that changes only the delegate never
            // re-renders the band.
            Element tools = _heldToolsVisible
                ? Button.Create(Loc.Get(Strings.Browse.ShowAll), RunTools, ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !_heldToolsLoading)
                : new BoxEl();

            return new BoxEl
            {
                Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
                Padding = new Edges4(MastheadFrameX, MastheadFrameTop, MastheadFrameX, 0f),
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
                    R.Type.SurfaceDisplay(crumb.Label) with
                    {
                        Color = Tok.TextTertiary, HoverColor = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                    },
                ],
            });
            segs.Add(R.Type.SurfaceDisplay("›") with { Color = Tok.TextTertiary, Shrink = 0f });
        }
        var current = R.Type.SurfaceDisplay(title) with
        {
            Key = "masthead-current", MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Grow = 1f, Basis = 0f, Shrink = 1f,
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

            // Always mounted, even at the neutral ground, so the node is live across a change and the brush transition has
            // a colour to fade FROM. Never Transparent (premultiplied black drags the ramp dark).
            Element tint = new BoxEl
            {
                Key = "shell.material.tint", Grow = 1f, HitTestVisible = false,
                Fill = state.Tint ?? R.Wash.NeutralGround,
                BrushTransitionMs = Design.Motion.Standard,
            };
            if (state.Wash is not { } wash)
                return new BoxEl { Grow = 1f, ZStack = true, HitTestVisible = false, Children = [tint] };

            var legs = new List<Element>(3);
            var strength = R.Wash;
            AddWash(legs, wash.Hero, Design.Wash.Hero, strength.HeroAlpha, "shell.wash.hero", vp);
            AddWash(legs, wash.Weekly, Design.Wash.Weekly, strength.ShelfAlpha, "shell.wash.weekly", vp);
            AddWash(legs, wash.Mix, Design.Wash.Mix, strength.ShelfAlpha, "shell.wash.mix", vp);
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

    /// <summary>The chrome row's ONE omnibar: the suggestion store (<see cref="OmnibarModel.Query"/>, its version and the
    /// keyboard cursor), the fetch edges, the focus tickets and the verbs are <see cref="OmnibarModel"/>'s; the field and the
    /// popup below are views over it. <see cref="OmnibarModel.SearchFocused"/> / <see cref="OmnibarModel.FlyoutOpen"/> are
    /// what the chrome allocator reads to hand the caret to the new form across a mode flip.</summary>
    static readonly OmnibarModel s_omnibar = new(OmnibarPorts.Live());

    /// <summary>Ask the omnibar for keyboard focus (Ctrl+F — <see cref="IFrameVerbs.FocusSearch"/>).</summary>
    internal static void FocusSearch() => s_omnibar.Focus();

    // ── test-only seam (RootScopeTests) ─────────────────────────────────────────────────────────────────────────────
    //
    // Wavee.ShellTests has no InternalsVisibleTo, so this is public (the repo's `*ForTests` convention, see Shell.cs). It
    // reads and writes the facts only a MOUNTED omnibar form holds, which `RootScope` puts back when its root unmounts.
    // Never called from production code.
    /// <summary>The omnibar facts a mounted form holds (see <see cref="OmnibarModel.ResetMountedFacts"/>).</summary>
    public readonly record struct OmnibarFacts(bool SearchFocused, bool FlyoutOpen, int Highlight);

    /// <summary>The omnibar facts now.</summary>
    public static OmnibarFacts OmnibarFactsForTests()
        => new(s_omnibar.SearchFocused.Peek(), s_omnibar.FlyoutOpen.Peek(), s_omnibar.Highlight.Peek());

    /// <summary>Write the facts a mounted form would (the caret in the field, the flyout open, a row highlighted).</summary>
    public static void SetOmnibarFactsForTests(OmnibarFacts facts)
    {
        s_omnibar.SearchFocused.SetIfChanged(facts.SearchFocused);
        s_omnibar.FlyoutOpen.SetIfChanged(facts.FlyoutOpen);
        s_omnibar.Highlight.SetIfChanged(facts.Highlight);
    }

    // T4b (themes plan 6.1): the field, the icon-mode flyout button, the rich omnibar and the popup body are the structure's
    // (`Shell.Search` and `Shell.SearchSuggestions`: `Themes\Classic\Structure\Views\Search\ClassicSearch.cs`, mounted by the
    // title bar's template); the answer the popup draws, the rows' facts and the field width rule are `+Shell.TitleBar.cs`.
}
