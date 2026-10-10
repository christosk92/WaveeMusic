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
            var current = Current.Value;
            var shown = Shown.Value;
            // The crumb head changes WITH the page swap: until Shown lands on Current (the old page's exit leg) the band keeps the
            // trail of the page that is still on screen, faded out; boot (Shown None) and a same-page switch count as landed.
            bool landed = ZuneNavRules.HeadLanded(in shown, in current);
            var route = landed ? current : shown;
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
                Opacity = live && landed ? 1f : 0f,
                HitTestVisible = live && landed,
                Transition = live && landed ? Show : Hide,
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
            // The card's final top (the title bar, plus the Zune band): the depth of the veil's top fade. Changes only with the nav
            // style or the band, never per scroll or per frame.
            float cardTop = UseComputed(static () => Ui.CardRect.Value.Y).Value;
            // The chrome's static hover/pressed arms follow the ink mix across one half (Ui.ChromeArmsOnMedia): bridged here, the one
            // shell-level component, so what reads it renders once per crossing and never per scroll tick.
            UseSignalEffect(static () => Ui.ChromeArmsOnMedia.SetIfChanged(Ui.ChromeInkMix() >= 0.5f));

            // Always mounted, even at the neutral ground, so the node is live across a change and the brush transition has
            // a colour to fade FROM. Never Transparent (premultiplied black drags the ramp dark).
            Element tint = new BoxEl
            {
                Key = "shell.material.tint", Grow = 1f, HitTestVisible = false,
                Fill = state.Tint ?? Design.Wash.NeutralGround,
                BrushTransitionMs = Design.Motion.Standard,
            };
            // EXPERIMENTAL (artist bleed): the page-published photo and its chrome scrim, ABOVE the tint and the washes. Null for
            // every page that publishes none, which adds nothing to the children below. After the page lets go, the RETAINED backdrop
            // keeps the nodes mounted while BleedPresence fades them, so the photo and the card's strip share one clock.
            var bleed = (state.Backdrop ?? Ui.BleedBackdrop.Value) is { } backdrop ? BleedNodes(backdrop, vp, cardTop) : null;

            if (state.Wash is not { } wash)
                return new BoxEl { Grow = 1f, ZStack = true, HitTestVisible = false, Children = bleed is null ? [tint] : [tint, .. bleed] };

            bool rich = Prefs.Appearance.SurfaceWash() == WashLevel.Rich;
            var legs = new List<Element>(3);
            AddWash(legs, wash.Hero, Design.Wash.Hero, Design.Wash.HeroAlpha(light, rich), "shell.wash.hero", vp);
            AddWash(legs, wash.Weekly, Design.Wash.Weekly, Design.Wash.ShelfAlpha(light, rich), "shell.wash.weekly", vp);
            AddWash(legs, wash.Mix, Design.Wash.Mix, Design.Wash.ShelfAlpha(light, rich), "shell.wash.mix", vp);
            Element washes = new BoxEl
            {
                // THE WASHES STOP AT THE DOCK LINE: the dock paints nothing, so a peak landing under it read as "the
                // dock has a gradient". An inset MARGIN keeps every placement ratio viewport-independent.
                Grow = 1f, ZStack = true, HitTestVisible = false, ClipToBounds = true,
                Margin = new Edges4(0f, 0f, 0f, Design.Wash.HostBottomInset),
                Children = legs.ToArray(),
            };
            return new BoxEl
            {
                Grow = 1f, ZStack = true, HitTestVisible = false,
                Children = bleed is null ? [tint, washes] : [tint, washes, .. bleed],
            };
        }

        /// <summary>The floating rail overlay sits flush against the window's right edge (no margin), so its panel's left edge is
        /// exactly <c>viewportW - RailWidth</c>.</summary>
        const float FloatingRailMargin = 0f;

        /// <summary>EXPERIMENTAL (artist bleed): the backdrop photo, the hero's horizontal veil over it and the scrim that keeps
        /// the chrome readable over it. ONE FIELD, ONE FRAME: the photo box and the veil box both come from
        /// <see cref="ArtistBleed.FrameFor"/> (the same rule the card's own photo uses), the scrim and the veil are dark in both
        /// themes (<see cref="ArtistBleed.FieldBase"/>), and the veil rides the photo's clip, translation, strength and
        /// Enter/Exit, so chrome and hero are one field. All four
        /// layers (photo with its veil, the band extension with its own copy of the veil, scrim) are keyed on the backdrop (a new
        /// photo remounts, and so cross-fades, through the WashFade idiom) and hit-test free, and every binding below is a paint channel except Width/Height, which change on a resize, a pane or rail
        /// change or a nav-style switch (the card's rect), and the photo clip's Height, which follows the hero's presented bottom (it
        /// relays out that one leaf per scroll frame while the hero is on screen). The photo's span is the card's left..right edge
        /// (flush with the window's left edge when no pane is docked); it never sits under the right rail or the Classic/Library
        /// pane. Under Zune a FLOATING rail's panel starts under the band, so below the card's top the photo stops at the panel's
        /// left edge (<see cref="ArtistBleed.PhotoRight"/>) while a band extension keeps the photo across the chrome above the panel.
        /// The CHROME INK is window-wide (<c>Ui.ChromeInkMix</c>), so the dark field is too: two side strips (the SIDE FIELD below)
        /// run the title bar's height under whatever lies outside the photo's span, so the island above the pane column and the one
        /// above the rail read light-on-dark in a light theme as well.
        /// <para>THE PHOTO FOLLOWS THE CARD'S PRESENTED POSE. A pane toggle or a nav-style switch FLIPs the card (Reveal: its
        /// contents stay laid out at the FINAL size while a clip and a translation ease), so the photo does the same: an outer
        /// clip at the presented left/top (<see cref="Ui.CardPose"/>) over an inner box laid out at the final span, so the Cover
        /// crop never rescales mid-move and the left edge never snaps.</para></summary>
        static Element[] BleedNodes(ShellBackdrop b, IReadSignal<Size2> vp, float cardTop)
        {
            // THE VEIL'S TOP FADE: the veil's horizontal plate is near-opaque on the left, which over the chrome (the title bar and the
            // band) muddied the pivots and the search pill. It fades in from nothing at the window top to full at the card's top, so
            // the chrome sees the photo and the scrim, and from the card's top down the veil is the card's own (same pixels, no seam).
            EdgeFadeSpec? veilTopFade = cardTop > 0f ? new EdgeFadeSpec(EdgeMask.Top, cardTop) : null;
            // The span is the card's own: flush with the window edge whenever no pane is docked (FrameRules.ContentCardX is 0 under
            // Zune), so it follows the card's FLIP in BOTH directions with no style-keyed snap.
            static float CardRight() { var r = Ui.CardRect.Value; return r.X + r.W; }
            // Where the photo stops BELOW the card's top: the card's right edge, or (Zune, floating rail) the panel's left edge.
            // Only the CLIP follows it; the photo's frame stays the card's final width so the Cover crop is the card photo's.
            float Right() => ArtistBleed.PhotoRight(CardRight(), Sidebar.NavStyle.Value == ShellNavStyle.Zune,
                FrameRules.RailFloats(Ui.RailOpen.Value, Ui.RailFits.Value), vp.Value.Width, Ui.RailWidth.Value, FloatingRailMargin);
            static float FinalLeft() => Ui.CardRect.Value.X;
            static float FinalWidth() => MathF.Max(0f, CardRight() - FinalLeft());
            static float Left() => Ui.CardPose.Value.X;
            // The clip never runs past the photo's right edge (a closing pane's translated photo would otherwise reach under the rail).
            float ClipWidth() => MathF.Max(0f, MathF.Min(FinalWidth(), Right() - Left()));
            // The chrome above the card (scrim, band extension) lies wholly above a floating panel, so it keeps the card's span.
            static float ChromeClipWidth() => MathF.Max(0f, MathF.Min(FinalWidth(), CardRight() - Left()));
            float Strength() => Ui.BleedPresence.Value * ArtistBleed.HeroVisible(b.ScrollY.Value, b.CollapseDistance);
            // The taller of the final and the presented top, so a Zune-to-Classic ease never runs short of photo. The card's HeroArt
            // takes the same frame from the same rule.
            ArtistBleed.PhotoFrame Frame() => ArtistBleed.FrameFor(FinalWidth(), Ui.CardPose.Value.Y, Ui.CardRect.Value.Y, b.PhotoHeight);

            // The page's own decode (its latched size), so the page's HeroArt decode is the cache hit.
            float aspect = (float)b.DecodeW / Math.Max(1, b.DecodeH);

            Element image = new BoxEl
            {
                ZStack = true, HitTestVisible = false,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Width = Prop.Of(() => Frame().Width),
                Height = Prop.Of(() => Frame().Height),
                Children =
                [
                    Image(b.Url, ImageFit.Cover, aspect, b.DecodeW, 0f, placeholder: ColorF.Transparent)
                        with { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, FocusX = ArtistHeroLayout.PhotoFocusX, FocusY = ArtistBleed.PhotoFocusY },
                ],
            };
            // The hero's horizontal veil (the onMedia arm) over the same frame: its gradient spans the same columns as the card's own
            // veil, so the hand-over shifts no pixel.
            Element veil = new BoxEl
            {
                ZStack = true, HitTestVisible = false,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Width = Prop.Of(() => Frame().Width),
                Height = Prop.Of(() => Frame().Height),
                EdgeFade = veilTopFade,
                Children =
                [
                    Palette.ArtistHeroVeil(b.PaletteUrl, vertical: false, float.NaN, float.NaN,
                                           key: "shell.bleed.veil:" + b.Key, payloadAccent: b.PayloadAccent, onMedia: true),
                ],
            };
            Element photo = new BoxEl
            {
                Key = "shell.bleed:" + b.Key,
                ZStack = true, ClipToBounds = true, HitTestVisible = false,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Width = Prop.Of(ClipWidth),
                // The clip's ON-SCREEN bottom (this box carries the parallax translation, which PhotoClip subtracts) lands on the riser
                // line (the hero's presented bottom): no sliver below the hero. The box feathers its own bottom with the same
                // PhotoFadeBandFor band as the card media, so its on-screen feather lands in CardGround's [line - band, line] ramp (the
                // translucent fill fading in): the photo cross-fades into the ground and never ends in a hard horizontal edge.
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, ArtistHeroLayout.PhotoFadeBandFor(b.PhotoHeight)),
                Height = Prop.Of(() => Ui.CardPose.Value.Y + ArtistBleed.PhotoClip(b.ScrollY.Value, b.HeroHeight, b.Floor, b.PhotoHeight)),
                Transform = Prop.Of(() => Affine2D.Translation(Left(), ArtistBleed.ParallaxY(b.ScrollY.Value))),
                Opacity = Prop.Of(Strength),
                Enter = WashFade, Exit = WashFade,
                Children = [image, veil],
            };
            // THE BAND EXTENSION: under a floating Zune rail the panel starts under the band, so the strip between the title bar and the
            // card's top over the rail column is chrome ABOVE the panel and keeps the photo (the same pixels: the same frame, shifted by
            // the clip's offset). Zero-wide whenever the photo already reaches the card's edge.
            Element bandImage = new BoxEl
            {
                ZStack = true, HitTestVisible = false,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Width = Prop.Of(() => Frame().Width),
                Height = Prop.Of(() => Frame().Height),
                Transform = Prop.Of(() => Affine2D.Translation(Left() - Right(), ArtistBleed.ParallaxY(b.ScrollY.Value))),
                // The same veil as the photo's, over the same frame and under the same translation, so the gradient columns line up with
                // the photo box and no vertical seam shows at the panel's left edge.
                Children =
                [
                    Image(b.Url, ImageFit.Cover, aspect, b.DecodeW, 0f, placeholder: ColorF.Transparent)
                        with { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, FocusX = ArtistHeroLayout.PhotoFocusX, FocusY = ArtistBleed.PhotoFocusY },
                    new BoxEl
                    {
                        ZStack = true, HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        EdgeFade = veilTopFade,
                        Children =
                        [
                            Palette.ArtistHeroVeil(b.PaletteUrl, vertical: false, float.NaN, float.NaN,
                                                   key: "shell.bleed.band.veil:" + b.Key, payloadAccent: b.PayloadAccent, onMedia: true),
                        ],
                    },
                ],
            };
            Element photoBand = new BoxEl
            {
                Key = "shell.bleed.band:" + b.Key,
                ZStack = true, ClipToBounds = true, HitTestVisible = false,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Width = Prop.Of(() => ArtistBleed.BandExtensionWidth(CardRight(), Right())),
                Height = Prop.Of(() => ArtistBleed.ScrimHeight(Ui.CardPose.Value.Y)),
                Transform = Prop.Of(() => Affine2D.Translation(Right(), 0f)),
                Opacity = Prop.Of(Strength),
                Enter = WashFade, Exit = WashFade,
                Children = [bandImage],
            };
            var ground = ArtistBleed.FieldBase;
            Element scrim = new BoxEl
            {
                Key = "shell.bleed.scrim:" + b.Key,
                HitTestVisible = false,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Width = Prop.Of(ChromeClipWidth),
                Height = Prop.Of(() => ArtistBleed.ScrimHeight(Ui.CardPose.Value.Y)),
                Transform = Prop.Of(() => Affine2D.Translation(Left(), 0f)),
                Opacity = Prop.Of(Strength),
                // Straight-alpha stops: the transparent stop carries the ground's own RGB (Design.Wash.Vanish).
                Gradient = new GradientSpec(GradientShape.Linear, 90f, ScrimStops(ground)),
                Enter = WashFade, Exit = WashFade,
            };
            // THE SIDE FIELD: the same dark ground, flat across the chrome's height, under the title bar's columns the photo does not
            // reach (above the Classic/Library pane on the left, above the rail on the right). It rides the photo's strength and
            // Enter/Exit, so the ink and the field are one clock. Zero-width when the card is flush (Zune, no rail). The RIGHT field
            // stops at the title bar's bottom (ArtistBleed.SideFieldHeight): under Zune the inline rail runs from there to the dock
            // and the Zune band lives in the page column, so a field as tall as the card's top would show through the rail gap and
            // behind the rail coat's rounded corner. "The title bar's bottom" is TitleBar.ExpandedHeight because the chrome row is pinned at
            // y=0 and is exactly that tall; CardPose.Y is a layout coordinate from the same origin, so the two compare directly. The
            // left field keeps the card's top (Zune has no pane, so it is zero-wide there).
            Element Side(string key, Func<float> left, Func<float> width, Func<float> height) => new BoxEl
            {
                Key = key + b.Key,
                HitTestVisible = false,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Width = Prop.Of(width),
                Height = Prop.Of(height),
                Transform = Prop.Of(() => Affine2D.Translation(left(), 0f)),
                Opacity = Prop.Of(Strength),
                Gradient = new GradientSpec(GradientShape.Linear, 90f,
                [
                    new GradientStop(0f, ground with { A = ArtistBleed.SideFieldAlpha }),
                    new GradientStop(0.7f, ground with { A = ArtistBleed.SideFieldAlpha }),
                    new GradientStop(1f, Design.Wash.Vanish(ground)),
                ]),
                Enter = WashFade, Exit = WashFade,
            };
            return
            [
                photo, photoBand, scrim,
                Side("shell.bleed.side.l:", static () => 0f, static () => MathF.Max(0f, Left()), static () => ArtistBleed.ScrimHeight(Ui.CardPose.Value.Y)),
                Side("shell.bleed.side.r:", static () => CardRight(), () => MathF.Max(0f, vp.Value.Width - CardRight()),
                    static () => ArtistBleed.SideFieldHeight(Ui.CardPose.Value.Y, TitleBar.ExpandedHeight)),
            ];
        }

        /// <summary>The scrim's gradient stops (<see cref="ArtistBleed.ScrimStops"/>) over <paramref name="ground"/>: straight-alpha, the
        /// last (alpha 0) stop carrying the ground's own RGB.</summary>
        static GradientStop[] ScrimStops(ColorF ground)
        {
            var stops = ArtistBleed.ScrimStops;
            var result = new GradientStop[stops.Length];
            for (int i = 0; i < stops.Length; i++)
                result[i] = new GradientStop(stops[i].Offset, stops[i].Alpha > 0f ? ground with { A = stops[i].Alpha } : Design.Wash.Vanish(ground));
            return result;
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

    /// <summary>The omnibar's suggestions list is mounted (open). Keeps the title-bar pill expanded across a row press, which
    /// moves focus off the editor until the popup closes and the overlay restores it.</summary>
    static readonly Signal<bool> s_suggestionsOpen = new(false);

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
        => Sidebar.NavStyle.Value != ShellNavStyle.Zune && ChromeLayout.Value.SearchMode == MergedSearchMode.Field
            ? Embed.Comp(() => new SearchField(avail)) with { Key = "chrome-search-host", Exit = PageHead.FadeOut, Transition = s_chromeFade }
            : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

    /// <summary>The centre-measurement stand-in for a field that is NOT in the bar's flexible centre column (Zune's trailing,
    /// right-aligned search): there is no measured lane to trim against, so the field takes its allocated compact width.</summary>
    static readonly Signal<float> s_unboundedAvail = new(float.PositiveInfinity);

    /// <summary>Zune's compact search, seated in the trailing island just before the identity chip. The centre column is
    /// empty there. The host fades in and out in place; its width is the allocator's fixed <see cref="Layout.ChromeSearchMinW"/>
    /// from the first frame, so it never resizes on arrival.</summary>
    static Element ZuneSearchHost() => Embed.Comp(static () => new SearchField(s_unboundedAvail)) with
    {
        Key = "chrome-search-host", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_chromeFade,
    };

    static Element SearchFlyoutButton() => Embed.Comp(static () => new SearchFlyoutButtonView()) with { Key = "chrome-search-button-host" };

    /// <summary>The title-bar pill's own ink mix: <see cref="Ui.ChromeInkMix"/>, except 0 while the pill has focus (its editor's focus
    /// fill is the theme's input plate, which the theme's ink belongs on). A paint-rate read.</summary>
    static float PillInkMix() => s_searchFocused.Value ? 0f : Ui.ChromeInkMix();

    /// <summary>The pill's typed text, placeholder and query icon: the theme's text tokens at
    /// <see cref="PillInkMix"/>, cross-fading to the on-media ink. Cached thunks (the box's frozen <c>TextInk</c> / <c>PlaceholderInk</c>).</summary>
    static readonly Func<ColorF> PillInkPrimary = static () => ArtistBleed.Ink(Tok.TextPrimary, Design.OnMedia.Ink, PillInkMix());
    /// <inheritdoc cref="PillInkPrimary"/>
    static readonly Func<ColorF> PillInkSecondary = static () => ArtistBleed.Ink(Tok.TextSecondary, Design.OnMedia.InkSecondary, PillInkMix());
    /// <summary>The pill's plate and hairline: the standard field's control fill and stroke (the pill is <c>AutoSuggestBoxChrome.Standard</c>)
    /// cross-fading to the glass plate and the on-media stroke at <see cref="PillInkMix"/>. Read live, so a theme switch repaints them.</summary>
    static readonly Func<ColorF> s_pillPlate = static () => ColorF.Lerp(Tok.FillControlDefault, Design.OnMedia.GlassPlate, PillInkMix());
    /// <inheritdoc cref="s_pillPlate"/>
    static readonly Func<ColorF> s_pillHairline = static () => ColorF.Lerp(Tok.StrokeControlDefault, Design.OnMedia.Stroke, PillInkMix());

    /// <summary>The pill's width motion (F5): a REFLOW on the width axis, so the neighbours (the drag gap, the centre column's
    /// grow bands) are pushed through real layout instead of the pill overlapping them. <see cref="Design.Motion.Standard"/>.</summary>
    static readonly LayoutTransition s_searchWidthMotion = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.FluentStandard), Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The pill's LAID-OUT width (written from its box's bounds), so the hint chip's floor test reads the width that
    /// is actually on screen in both the rest and the expanded arm rather than recomputing it.</summary>
    static readonly Signal<float> s_fieldW = new(Layout.ChromeSearchMinW);

    /// <summary>Bumped once each time the pill's width SETTLES on its target (rest or expanded). <c>ChromeContentVersion</c>
    /// folds it in, so the title bar re-pushes its non-client regions at the settled width — the Zune trailing island and the
    /// Classic centre column alike — instead of at the first frame of the motion.</summary>
    static readonly Signal<int> s_searchSettle = new(0);
    static float s_searchSettledTarget = -1f;

    /// <summary>The pill box's bounds: publishes the laid-out width, and counts a settle the first time it is within half a DIP
    /// of <paramref name="target"/> for that target.</summary>
    static void OnSearchFieldBounds(RectF r, float target)
    {
        s_fieldW.SetIfChanged(r.W);
        if (MathF.Abs(r.W - target) > 0.5f || s_searchSettledTarget == target) return;
        s_searchSettledTarget = target;
        s_searchSettle.Value = s_searchSettle.Peek() + 1;
    }

    sealed class SearchField(IReadSignal<float> avail) : Component
    {
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var field = UseRef<NodeHandle>(default);
            var pill = UseRef<NodeHandle>(default);
            var parts = UseMemo(() =>
            {
                // CHROME INK (artist bleed): see Ui.ChromeInkMix. While a backdrop shows, the plate and the hairline cross-fade toward the
                // glass plate and the on-media stroke, the query icon takes the secondary ink, and the editor's typed text, placeholder
                // and ghost take theirs through the box's TextInk / PlaceholderInk (RichOmnibar). Focused, the pill is back on the theme:
                // the editor's own focus fill is the theme's light input plate, and light ink must never sit on it.
                var p = new TemplateParts();
                p[AutoSuggestBox.PartRoot] = b =>
                {
                    b = b with
                    {
                        OnRealized = h => field.Value = h,
                        OnFocusChanged = static f => s_searchFocused.SetIfChanged(f),
                    };
                    // ALWAYS bound (a mix of 0 is the theme's plate and hairline): a bind is wired only at mount, so a plate that
                    // mounted static kept the theme's colour when the bleed published (a white pill over the dark photo).
                    if (b.Fill.IsBound || b.BorderColor.IsBound) return b;
                    bool arms = Ui.ChromeArmsOnMedia.Value;
                    return b with
                    {
                        Fill = Prop.Of(s_pillPlate),
                        BorderColor = Prop.Of(s_pillHairline),
                        HoverFill = arms && !s_searchFocused.Value ? Design.OnMedia.GlassPlateHover : b.HoverFill,
                        PressedFill = arms && !s_searchFocused.Value ? Design.OnMedia.GlassPlatePressed : b.PressedFill,
                    };
                };
                p.Set<TextEl>(AutoSuggestBox.PartQueryIcon, static t => t with { Color = Prop.Of(PillInkSecondary) });   // ink: see Ui.ChromeInkMix
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
            //
            // EXPANDED (F5): while the field has focus the pill eases to the allocator's SearchExpandW. That arm does NOT trim
            // against `avail`. The centre column hugs the field, so `avail` is the feedback of the width asked for here: a trim
            // would read the rest width back as the supply and pin the pill there (the same ratchet as above, with the sign
            // flipped). The allocator bounded SearchExpandW by the RESERVED tab cluster, so the row can always seat it.
            //
            // The pill also stays expanded while its suggestions list is open: pressing a row blurs the editor (the row is the
            // pointer's focus target for a frame) and OverlayHost restores it when the popup closes, so keying on focus alone
            // would collapse the pill, and the popup anchored to it, under the pointer mid-click and then grow it back.
            var l = ChromeLayout.Value;
            float rest = Chrome.FieldWidthFor(l.SearchWidth, avail.Value);
            bool expand = FrameRules.SearchExpands(s_searchFocused.Value, s_suggestionsOpen.Value);
            float width = expand ? Chrome.ExpandedFieldWidth(l.SearchExpandW, rest) : rest;
            // The focused pill never grows over the Zune pivot row (C3). The pill is right-anchored in the trailing island, so only
            // its right edge matters: it and the pivots' right edge are read from the live layout on every expanded render (the
            // pill's right edge does not move while its width eases, and a resize or a language change is picked up), never cached
            // from an earlier layout. NaN (outside Zune, no probe, a dead handle) leaves the allocator's width untouched.
            if (expand)
            {
                float pillRight = float.NaN;
                if (hooks.GetNodeRect is { } rectOf && !pill.Value.IsNull)
                {
                    var win = rectOf(pill.Value);
                    pillRight = win.X + win.W;
                }
                width = Chrome.CapExpandForPivots(width, rest, pillRight, Ui.ZunePivotsRightProbe?.Invoke() ?? float.NaN);
            }
            // The motion belongs to the focus change. A REST-width change that is not an arm flip (a tab added in Classic's
            // tab-limited regime republishes a smaller SearchWidth while LeadClusterW widens) snaps with its cluster, so the
            // old wider pill never overruns the row for 250 ms. A frame that merely re-renders keeps the motion in flight.
            var lastExpand = UseRef(expand);
            var lastRest = UseRef(l.SearchWidth);
            var lastLead = UseRef(l.LeadClusterW);
            bool flipped = lastExpand.Value != expand;
            bool chromeMoved = !flipped && (lastRest.Value != l.SearchWidth || lastLead.Value != l.LeadClusterW);
            lastExpand.Value = expand; lastRest.Value = l.SearchWidth; lastLead.Value = l.LeadClusterW;
            // The pill is wrapped in a WIDTH-LESS slot (C1). A component node mirrors its root's DECLARED width into the layout
            // (Reconciler.MirrorParticipation copies c.Width, and a Reflow's RestoreTo is the final value), so with the Width on
            // the root the title-bar island laid out at the FINAL width at once while the pill eased inside a pinned slot. With
            // the Width on the inner pill, the component's own root declares none: the slot hugs the pill's eased width and the
            // reflow reaches the island row every frame.
            return new BoxEl
            {
                Key = "chrome-search-slot", Direction = 1, Shrink = 0f, Justify = FlexJustify.Center,
                Children =
                [
                    // A COLUMN, so the omnibar is STRETCHED to this slot's width (the cross axis). In fill mode it sizes its
                    // editor from its own last bounds; in a row its width was its content's width, so one narrow frame (a visit
                    // to the search page re-mounts the field) latched it at the query button's ~37 DIP inside a 330-DIP slot:
                    // a "Search" stub overflowing a tiny box that no resize undid. Justify centres it vertically instead.
                    new BoxEl
                    {
                        Key = "chrome-search-field", Direction = 1, Shrink = 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Stretch,
                        Width = width, Animate = Design.Reduced || chromeMoved ? null : s_searchWidthMotion,
                        OnRealized = h => pill.Value = h,
                        OnBoundsChanged = r =>
                        {
                            OnSearchFieldBounds(r, width);
                        },
                        // The suggestions popup is exactly the pill's width and left edge (BottomStretch anchors to the pill), as in the
                        // WinUI AutoSuggestBox, instead of a 400 floor that overhung a 280 pill.
                        Children = [Embed.Comp(() => new RichOmnibar(parts, Layout.ChromeSearchMaxW, AutoSuggestBoxSuggestionPresentation.Popup,
                            pillWidth: s_fieldW))],
                    },
                ],
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
                                afterChoose: CloseFlyout)),
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
                IconButton.Create(Icons.Search, Toggle, ChromeButtonStyle, parts: ChromeGlyphParts)   // ink: see Ui.ChromeInkMix
                    with { Key = "chrome-search-button", Margin = ChromeButtonMargin, OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Nav.Search));
        }
    }

    /// <summary>The real AutoSuggestBox (focus, editing, accessibility, popup lifetime) with Wavee's artwork-aware rows.
    /// The text IS <see cref="SearchText"/>, synced to the route on navigation only.</summary>
    sealed class RichOmnibar(TemplateParts? parts, float maxWidth, AutoSuggestBoxSuggestionPresentation presentation,
        Action? afterChoose = null, IReadSignal<float>? pillWidth = null) : Component
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
                    selection => { if (InvokeSelection(selection)) context.Close(); }, context.Close)),
                MoveSelection: delta => highlight.Value = Omnibar.MoveHighlight(highlight.Peek(), delta, Omnibar.SelectableCount(query.Suggestions)),
                SubmitSelection: () => InvokeSelection(highlight.Peek()),
                ResetSelection: () => highlight.Value = -1,
                // The list is a card: Esc / a pick plays the Dropdown exit instead of vanishing (the engine default stays Static).
                Chrome: PopupChrome.Dropdown);

            // Stock metrics: a 32-DIP field at the control corner radius with the control-default chrome. Wrapped in a
            // growing COLUMN so the box is STRETCHED to this component's width: a fill-mode box contributes only its query
            // button to its measured width, so laid out as a row's content it stayed ~37 DIP once one narrow frame
            // (leaving the search page or full screen re-mounts the title bar's field) set its self-measured width there.
            //
            // The field is a PILL in every nav style (corner radius = half the control height) with the short "Search"
            // placeholder. The shortcut hint is a chip in a ZStack layer over the field's trailing end, just left of the
            // engine's query slot (the editor and the fixed-width slot are flex siblings, and only the slot's plate and
            // glyph are Parts-modifiable, so the chip cannot live inside it). The layer is ALWAYS mounted and only its
            // opacity moves, so showing or hiding it never relayouts the row.
            Element field = AutoSuggestBox.Create(Array.Empty<string>(), Loc.Get(Strings.Shell.SearchShort),
                grow: 1f, maxFillWidth: maxWidth, text: SearchText, onQuerySubmitted: Submit,
                minHeight: Controls.ButtonHeight, cornerRadius: Controls.ButtonHeight / 2f, presenter: presenter, parts: parts,
                chrome: AutoSuggestBoxChrome.Standard, suggestionPresentation: presentation, completion: completion,
                // The title-bar pill (the one built with parts) sits over the bleed; the flyout's inline field sits on its own popup plate.
                textInk: parts is null ? null : PillInkPrimary, placeholderInk: parts is null ? null : PillInkSecondary);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f,
                Children = pillWidth is null ? [field] : [ZStack(field, SearchHintChip(pillWidth))],
            };
        }

        /// <summary>The chip's hairline over the bleed: the theme's control stroke cross-fading to the on-media one (<c>Ui.ChromeInkMix</c>).</summary>
        static readonly Func<ColorF> s_hintBorderOnMedia = static () => ColorF.Lerp(Tok.StrokeControlDefault, Design.OnMedia.Stroke, Ui.ChromeInkMix());

        /// <summary>The shortcut chip over the pill's trailing end. Visible ONLY while the field is unfocused AND empty and the
        /// pill is at least <see cref="Chrome.SearchPillMinW"/> wide (<see cref="Chrome.ShowSearchHint"/>); it fades on change.
        /// Never hit-testable, so a click lands on the field.</summary>
        static Element SearchHintChip(IReadSignal<float> pillWidth)
        {
            float pill = pillWidth.Value;   // the pill's LAID-OUT width (s_fieldW), rest or expanded
            bool show = Chrome.ShowSearchHint(s_searchFocused.Value, SearchText.Value.Length == 0, pill);
            return new BoxEl
            {
                Key = "search-hint", Direction = 0, Shrink = 0f, HitTestVisible = false,
                JustifySelf = FlexAlign.End, AlignSelf = FlexAlign.Center,
                Margin = new Edges4(0f, 0f, AutoSuggestBox.QueryButtonWidth + AutoSuggestBox.QueryButtonLeftMargin + AutoSuggestBox.RightButtonMargin, 0f),
                Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(4f),
                // ink: see Ui.ChromeInkMix (the chip shows only while unfocused, so the pill's focus override never applies to it)
                BorderWidth = 1f, BorderColor = Prop.Of(s_hintBorderOnMedia),
                Opacity = show ? 1f : 0f, Transition = s_chromeFade,
                Children = [Caption(Chrome.SearchHintChord) with { Color = Ui.ChromeTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
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
    /// confirmed empty answer; pending is the always-mounted progress slot over the previous rows (or skeleton rows in the
    /// height the previous answer took when there are none), failed is a retry offer. Idle (closing) keeps the last body
    /// on screen so the Dropdown exit plays over it.</summary>
    sealed class SuggestionsPopup(IReadSignal<float> width, Action<int> choose, Action? close) : Component
    {
        // The last body that was on screen: the Idle (closing) frames present it while the exit plays. Per mount.
        Element? _lastBody;

        public override Element Render()
        {
            // Mounted exactly while the list is open: the title-bar pill stays expanded for that long (see SearchField).
            UseLayoutEffect(() =>
            {
                s_suggestionsOpen.SetIfChanged(true);
                return () => s_suggestionsOpen.SetIfChanged(false);
            }, DepKey.Empty);
            string typed = SearchText.Value.Trim();
            _ = s_omnibar.Version.Value;
            var query = s_omnibar.Query;
            var s = query.Suggestions;
            var state = query.State;
            int highlighted = s_omnibar.Highlight.Value;
            // The anchor's measured width: the popup is exactly as wide as the pill (or the icon flyout's field) it hangs from.
            float w = width.Value > 0f ? width.Value : 720f;

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

            // What the last SETTLED answer took (rows, or a notice), so a Pending body with nothing to show reserves the
            // same space: a no-match query is a notice-high answer, not a stale list.
            if (rows.Count > 0) { s_lastQueryRows = queryRows; s_lastRichRows = richRows; s_lastWasNotice = false; }
            else if (state is Omnibar.State.Empty or Omnibar.State.Failed) { s_lastQueryRows = s_lastRichRows = 0; s_lastWasNotice = true; }

            Element body;
            if (rows.Count == 0)
            {
                body = state switch
                {
                    Omnibar.State.Empty => OmniNotice(w, Loc.Get(Strings.Search.NoResults), null),
                    Omnibar.State.Failed => OmniNotice(w, Loc.Get(Strings.Search.SuggestFailed),
                        Button.Subtle(Loc.Get(Strings.Common.Retry), static () => s_omnibar.Query.Retry())),
                    // Pending: skeleton rows in the reserved height, so the answer fills space instead of growing it.
                    Omnibar.State.Pending => PendingBody(w),
                    // Idle (closing): the body that was on screen, so the exit collapses real content; no sentence.
                    _ => _lastBody ?? new BoxEl { Width = w, MinWidth = w, MinHeight = AutoSuggestBox.ItemMinHeight },
                };
            }
            else
            {
                body = new ScrollEl
                {
                    Width = w, MinWidth = w, MaxHeight = Omnibar.PopupBodyMaxHeight, ContentSized = true,
                    Content = new BoxEl { Direction = 1, Width = w, MinWidth = w, Margin = new Edges4(-1f, 0f, -1f, 0f), Children = rows.ToArray() },
                };
            }

            if (state != Omnibar.State.Idle) _lastBody = body;

            // The progress bar's 3-DIP slot is ALWAYS the first child: it fades, it never mounts or unmounts, so a keystroke
            // moves nothing. The popup chrome supplies the plate, border, corners, shadow and clip; the root's height eases.
            return new BoxEl
            {
                Direction = 1, Width = w, MinWidth = w, Padding = new Edges4(0f, 2f, 0f, 2f),
                Layout = Design.Reduced ? null : s_bodyHeightMotion,
                Children =
                [
                    new BoxEl
                    {
                        Key = "omni-progress", Width = w, Height = ProgressSlotHeight, Shrink = 0f, ClipToBounds = true,
                        HitTestVisible = false, Opacity = state == Omnibar.State.Pending ? 1f : 0f, Transition = s_chromeFade,
                        Children = [ProgressBar.Indeterminate(w)],
                    },
                    body,
                ],
            };
        }

        const float ProgressSlotHeight = 3f;

        // The previous answer's row counts (UI thread only): what a Pending body reserves while nothing is on screen.
        static int s_lastQueryRows, s_lastRichRows;
        static bool s_lastWasNotice;   // the last settled answer was Empty / Failed: one notice slot high

        /// <summary>The popup root's height motion (Reflow on the height axis): rows arriving or leaving ease the card, and
        /// the top edge never moves. Standard / FluentDecelerate; the call site passes null under reduced motion.</summary>
        static readonly LayoutTransition s_bodyHeightMotion = new(TransitionChannels.Size,
            TransitionDynamics.Tween(Design.Motion.Standard, Easing.FluentDecelerate),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

        /// <summary>Skeleton rows for a Pending request with no rows to show: the previous answer's counts (else a typical
        /// answer), each row the real row's height, inert geometry only (<see cref="Controls.PendingBar"/>). The body is
        /// exactly <see cref="Omnibar.PendingBodyHeight"/> high and clips its last row, so the answer fills the space; after
        /// a notice (Empty / Failed) it is one notice slot.</summary>
        static Element PendingBody(float w)
        {
            float height = Omnibar.PendingBodyHeight(s_lastQueryRows, s_lastRichRows, s_lastWasNotice);
            if (s_lastWasNotice)
                return new BoxEl
                {
                    Direction = 0, Width = w, MinWidth = w, Height = height, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(24f, 0f, 24f, 0f), ClipToBounds = true,
                    Children = [Controls.PendingBar(160f, 11f)],
                };
            var (q, r) = Omnibar.PendingSkeletonRows(s_lastQueryRows, s_lastRichRows);
            var kids = new List<Element>(q + r + 1);
            for (int i = 0; i < q; i++)
                kids.Add(new BoxEl
                {
                    Height = Omnibar.QueryRowHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = new Edges4(16f, 0f, 16f, 0f),
                    Children = [Controls.PendingBar(120f + (i % 3) * 40f, 11f)],
                });
            for (int i = 0; i < r; i++)
            {
                if (i == 0 && q > 0) kids.Add(new BoxEl { Height = 1f, Shrink = 0f, Margin = new Edges4(16f, 4f, 16f, 4f), Fill = Tok.StrokeDividerDefault });
                kids.Add(new BoxEl
                {
                    Height = Omnibar.RichRowHeight, Shrink = 0f, Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(16f, 0f, 16f, 0f),
                    Children =
                    [
                        new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                        new BoxEl
                        {
                            Direction = 1, Gap = 6f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children = [Controls.PendingBar(140f + (i % 3) * 30f, 11f), Controls.PendingBar(90f, 9f)],
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
            Padding = new Edges4(12f, 0f, 8f, 0f), Margin = new Edges4(4f, 2f, 4f, 2f), Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem, AllowFocusOnInteraction = false,   // the field keeps focus through a press
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
                with { Role = AutomationRole.MenuItem, Margin = new Edges4(4f, 2f, 4f, 2f), AllowFocusOnInteraction = false };
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
