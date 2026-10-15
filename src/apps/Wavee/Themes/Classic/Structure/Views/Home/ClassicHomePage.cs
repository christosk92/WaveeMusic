// ── Themes/Classic/Structure/Views/Home/ClassicHomePage.cs — Classic's `Pages.Home` (themes plan 6.1, T10a1) ──────────────────
//
// From `Home\HomeScreen.cs`: the render half of the Home page, rebuilt from the app's own page idiom (`ClassicArtistPage`, `ClassicBrowseDirectory`):
// ONE plain `ScrollView` column, no `ItemsView` of zones, no lanes/extents/RowAt/Tail, no width query, no parallax, no overlay
// InfoBar.
//
//   Pages.Home
//   └─ ZStack [ Wash (the shell tint claim the host built),
//      ScrollView (Key/ScrollKey per PUBLISHED facet, Handle = the host's scroll handle)
//        └─ centred column  Gap=XL(20)  MaxWidth=PageMaxW  Padding=(PageWide, XXL(24), PageWide, Dock.Reserve+32)
//           │                ScrollScope=PageScope
//           ├─ Home.FacetRow (the SelectorBar in R.Type.FacetTitleStyle — the facet words ARE the page title —
//           │     · Following ToggleButton · busy ProgressBar)
//           │     its band is `.Sticky(StuckInset, scope: PageScope)` — transparent, no acrylic
//           └─ content  ZStack `.StickyClip(StuckBottom)` + EdgeFade(Top, 24) { WhileStuck }   (the Browse masthead idiom)
//      (the ScrollView has EdgeCues None: its viewport top feather would fade the pinned rows)
//                └─ SkelRegionEl(Content: keyed facet column → dim host (Opacity/HitTest binds only) → the zone column,
//                                ShimmerSource: the zone column over the seed, OnFailed: Controls.Vacancy(Error, retry), Reveal None)
//                + the facet failure layer (pass-through; the InfoBar of a failed switch lies over the content's top edge, so the
//                  zones never move when it comes or goes: tuning H1) ]
//
// NO GREETING HEADLINE (owner, seventh pass): the greeting lives only in the daylist card's eyebrow. The page column's own gap is the 20-px
// facet-row → content step; the 40-px rhythm between zones is the zone column's.
//
// STICKY WITHOUT A BACKGROUND: the facet band paints nothing (live Mica); the content column under it cuts ITSELF at the band's lower edge
// (`StickyClip(StuckBottom)`) with a top feather that arms only while the clip is engaged (`EdgeFadeSpec.WhileStuck`), so content dissolves
// into the band, is never guillotined, and never softens at rest. Unlike Browse the band is IN the scroll flow (an in-flow
// `.Sticky(StuckInset)` row, 12 below the viewport top), so no spacer is needed above the clipped node: the clip engages exactly when the
// content's top reaches the pinned band's lower edge. Chapter headers inside the zone column pin at `StuckBottom` under this band.
//
// THE SKELETON is derived from the real page: `smoothResize false` (a page-level region whose branches differ by hundreds of DIP lands its
// height at once) and Reveal None (the zones own their entrance, `Design.Entrance` rows in the zone column), the shimmer the SAME tree
// rendered against the seed.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Home page.</summary>
internal static class ClassicHomePage
{
    /// <summary>The registered template of <see cref="ViewKeys.HomePage"/>.</summary>
    public static Element Template(HomePageProps props, in ViewContext ctx)
    {
        // Reveal None: the zones own their entrance (Design.Entrance rows in the zone column); the shimmer is the SAME tree
        // rendered against the seed, so the skeleton is derived from the real page, never authored by hand.
        Element region = new SkelRegionEl(
            Pending: props.Pending,
            Failed: props.Failed,
            Content: () => FacetContent(props),
            ShimmerSource: () => ClassicHomeZones.Column(props.Seed(), props.Facet, props.Overlay),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.RetryFirstLoad),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: props.Group, SmoothResize: false);

        // THE CLIP IS THE CONTRACT: the band paints nothing, so the content cuts itself at the band's lower edge and
        // feathers that cut exactly while the clip is engaged (`ClassicBrowseDirectory`'s directory / `ClassicArtistPage`'s magazine).
        // The facet failure's InfoBar has its own slot (tuning H1): a layer stacked over the content's top edge, so the zones keep their
        // place when a switch fails and when the bar closes.
        Element content = new BoxEl
        {
            Key = "home:content", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ZStack = true,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.VerticalLayout.StickyFadeBand) { WhileStuck = true },
            Children = [region, ClassicHomeFacetRow.FailureLayer(props.FacetRow)],
        }.StickyClip(ClassicHomeRules.StuckBottom);

        // The centred page column (`ClassicArtistPage`'s Magazine): Grow toward the free width, capped at PageMaxW, the wide
        // gutter, the dock's reserve under the last section. It NAMES the sticky scope the facet band pins against. The
        // facet row is the page title, so the column opens 24 below the top and steps 20 to the content (the prototype's
        // facet-row → first zone step); the zones space themselves at SectionGapWide inside the zone column.
        Element column = new BoxEl
        {
            Key = "home:column", Direction = 1, Gap = Spacing.XL,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = R.Space.PageMaxW,
            AlignItems = FlexAlign.Stretch, ScrollScope = ClassicHomeRules.PageScope,
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, R.Space.DockReserve + Spacing.XXXL),
            Children =
            [
                ctx.View(ViewKeys.HomeFacetRow, props.FacetRow) with { Key = "home:facet-row" },
                content,
            ],
        };

        // Keyed per PUBLISHED facet (`ClassicArtistPage`'s idiom): each facet keeps its own scroll offset under its own
        // ScrollKey, and the handle feeds the Refreshing swap's at-top test (FacetSwitch.Landed).
        Element scroll = Ui.ScrollView(new BoxEl { Direction = 0, Justify = FlexJustify.Center, MinWidth = 0f, Children = [column] })
            with
            {
                Key = "home:scroll:" + props.Facet, Grow = 1f, MinWidth = 0f,
                ScrollKey = props.ScrollKey,
                Handle = props.Scroll,
                // No viewport edge cue: its 40-DIP top feather would fade the pinned facet band and chapter headers.
                // The page dissolves its own content under them (StickyClip + WhileStuck), as `ClassicArtistPage` does.
                EdgeCues = ScrollEdgeCues.None,
            };

        return new BoxEl
        {
            Grow = 1f, Direction = 1, ZStack = true,
            Children = [props.Wash, scroll],
        };
    }

    /// <summary>The facet content: one KEYED subtree per published facet — the swap IS the reconciler's keyed Exit/Enter reconcile
    /// (F13), not a timer — over ONE dim host for the whole zone column (F17/F18): opacity and hit-testing are compositor binds on the
    /// switch phase, and NOTHING ELSE. The host carries no pointer handler: any OnPointerDown/OnHoverMove/… sets the node's PointerBit
    /// (Reconciler.cs "hit-testable so it receives press/drag AND bare-hover/exit"), which made the whole zone column an interactive
    /// hover scope — every card → gap → card move flipped it and re-walked hover over the column (seventh pass: the late hover). F35's
    /// "interacted" mark went with it; a Refreshing landing swaps on at-top + within-window alone (FacetSwitch.Landed).</summary>
    static Element FacetContent(HomePageProps props)
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
                    Opacity = Prop.Of(() => FacetDimPlan.Of(props.Switch.Value.Phase).Opacity),      // compositor-only (F18)
                    HitTestVisible = Prop.Of(() => !props.Switch.Value.Dim),                          // E15
                    Children = [ClassicHomeZones.Column(m, m.Facet, props.Overlay)],
                },
            ],
        };
    }
}
