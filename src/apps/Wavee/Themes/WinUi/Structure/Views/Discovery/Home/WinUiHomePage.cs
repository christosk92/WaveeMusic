// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomePage.cs — WinUI's `Pages.Home` (themes plan 6.2; P6 W5) ──────────────────────
//
// Boards W-Home (Standard), W-Home-Compact / -Small / -Huge / -Huge4K and W-Home2 (the sticky facet row). ONE plain `ScrollView` column, no `ItemsView` of
// zones, no width query:
//
//   Pages.Home
//   └─ ZStack [ Wash (the shell tint claim the host built; WinUI paints none),
//      ScrollView (Key/ScrollKey per PUBLISHED facet, Handle = the host's scroll handle)
//        └─ column  Padding=(0, frame top inset, 0, dock reserve)  ScrollScope=PageScope
//           ├─ head      the greeting, Title Large 40/52 (Title 28/36 below Standard), keyed `winui.page.title`; at Small the Following button ends its row
//           ├─ Home.FacetRow   the SelectorBar with the 3-px pill and the Following button: a 48 BAND that pins to the card's top edge, full width
//           └─ content   the zone column, cut at the band's lower edge while the band is pinned (`StickyClip(BandH)`)
//
// THE GREETING IS THE PAGE TITLE (W-Home: "Good evening, Chris"); the daylist's eyebrow is "Your daylist". The text is a bound read of the core's greeting
// (`HomePageProps.Greeting`, contract 1.53), so a late sign-in re-describes it without re-rendering the page.
//
// THE INSETS ARE THE FRAME'S (`WinUiTiers.InsetOf`, 16 / 24 / 36 / 48), applied by each child (the band is full width so a pinned bar reaches the card's
// edges, W-Home2); no page cap at any tier (owner Q2), so Huge and 4K fill the region.
//
// THE PINNED BAND paints nothing but a hairline while it is pinned (W-Home2): the content under it cuts ITSELF at the band's lower edge (the engine's
// `StickyClip`, the same idiom as the Browse masthead and the artist page), so nothing shows through and nothing is softened at rest. The pin's own edge
// signal (`engaged:`) is the hairline's switch.
//
// THE SKELETON is derived from the real page: `smoothResize false` (a page-level region whose branches differ by hundreds of DIP lands its height at once)
// and Reveal None (the zones own their entrance), the shimmer the SAME tree rendered against the seed.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's Home page.</summary>
internal static class WinUiHomePage
{
    /// <summary>The registered template of <see cref="ViewKeys.HomePage"/>.</summary>
    public static Element Template(HomePageProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var frame = WinUiTiers.FrameOf(facts);
        float inset = frame.InsetX;
        var layout = WinUiHomeTiers.Of(facts);   // the tier's record: greeting rung, rhythm, daylist form, column bands

        Element region = new SkelRegionEl(
            Pending: props.Pending,
            Failed: props.Failed,
            Content: () => FacetContent(props, layout),
            ShimmerSource: () => WinUiHomeZones.Column(props.Seed(), props.Facet, props.Overlay, layout),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.RetryFirstLoad),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: props.Group, SmoothResize: false);

        // THE CLIP IS THE CONTRACT: the band paints nothing, so the content cuts itself at the band's lower edge. The facet failure's InfoBar has
        // its own slot: a layer stacked over the content's top edge, so the zones keep their place when a switch fails and when the bar closes.
        Element content = new BoxEl
        {
            Key = "home:content", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ZStack = true,
            Padding = new Edges4(inset, 0f, inset, 0f),
            Children = [region, WinUiHomeFacetRow.FailureLayer(props.FacetRow)],
        }.StickyClip(WinUiHomeRules.BandH);

        // The head: the greeting, and at Small the Following button at its end (W-Home-Small).
        var greeting = Greeting(props, layout);
        Element head = new BoxEl
        {
            Key = "home:head", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
            Padding = new Edges4(inset, 0f, inset, layout.GreetingGap),
            Children = layout.Following == WinUiFollowingPlace.GreetingRow
                ? [greeting with { Grow = 1f, Shrink = 1f }, WinUiHomeFacetRow.Following(props.FacetRow, layout)]
                : [greeting with { Grow = 1f, Shrink = 1f }],
        };

        Element column = new BoxEl
        {
            Key = "home:column", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, AlignItems = FlexAlign.Stretch,
            ScrollScope = WinUiHomeRules.PageScope,
            Padding = new Edges4(0f, frame.InsetTop, 0f, R.Space.DockReserve + Spacing.XXXL),
            Children =
            [
                head,
                ctx.View(ViewKeys.HomeFacetRow, props.FacetRow) with { Key = "home:facet-row" },
                new BoxEl { Height = layout.HeadGap, HitTestVisible = false },   // the band's foot air to the first zone
                content,
            ],
        };

        // Keyed per PUBLISHED facet: each facet keeps its own scroll offset under its own ScrollKey, and the handle feeds the Refreshing swap's at-top test.
        Element scroll = Ui.ScrollView(new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [column] })
            with
            {
                Key = "home:scroll:" + props.Facet, Grow = 1f, MinWidth = 0f,
                ScrollKey = props.ScrollKey,
                Handle = props.Scroll,
                EdgeCues = ScrollEdgeCues.None,   // the band's own cut does the dissolving: a viewport top cue would fade the pinned row
            };

        return new BoxEl
        {
            Grow = 1f, Direction = 1, ZStack = true,
            Children = [props.Wash, scroll],
        };
    }

    /// <summary>The greeting: Title Large 40/52 (Title 28/36 below Standard), one line, keyed for the inset lint. The words are a bound read of the host's greeting.</summary>
    static TextEl Greeting(HomePageProps props, WinUiHomeLayout layout)
    {
        var greet = props.Greeting;
        var text = layout.GreetingLarge ? R.Type.PageHero("") : R.Type.Pivot("");
        return text with
        {
            Key = WinUiFrameLayout.PageTitleKey,
            Text = Prop.Of(() => greet?.Invoke() ?? ""),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    /// <summary>The facet content: one KEYED subtree per published facet — the swap IS the reconciler's keyed Exit/Enter reconcile, not a timer — over ONE
    /// dim host for the whole zone column: opacity and hit-testing are compositor binds on the switch phase, and NOTHING ELSE. The host carries no pointer
    /// handler (any would make the whole zone column a hover scope).</summary>
    static Element FacetContent(HomePageProps props, WinUiHomeLayout layout)
    {
        var m = props.Content();
        props.OnContent(m);
        return new BoxEl
        {
            Key = "facet:" + m.Facet, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
            Transition = MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
            Children =
            [
                new BoxEl   // the dim host
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Opacity = Prop.Of(() => FacetDimPlan.Of(props.Switch.Value.Phase).Opacity),      // compositor-only
                    HitTestVisible = Prop.Of(() => !props.Switch.Value.Dim),
                    Children = [WinUiHomeZones.Column(m, m.Facet, props.Overlay, layout)],
                },
            ],
        };
    }
}
