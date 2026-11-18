// ── Themes/Classic/Structure/Views/Home/ClassicHomePage.cs — Classic's `Pages.Home` (themes plan 6.1, T10a1) ──────────────────
//
// From `Home\HomeScreen.cs`: the render half of the Home page, rebuilt from the app's own page idiom (`ClassicArtistPage`, `ClassicBrowseDirectory`):
// ONE plain `ScrollView` column, no `ItemsView` of zones, no lanes/extents/RowAt/Tail, no width query, no parallax, no overlay
// InfoBar.
//
//   Pages.Home
//   └─ ZStack [ Wash (the shell tint claim the host built),
//      ScrollView (Key/ScrollKey per PUBLISHED facet, Handle = the host's scroll handle)
//        └─ centred page row  MaxWidth=PageCap: [gutter spacer, column, gutter spacer] (the spacers ease their width: WidthReflow)
//           column: spacers PageTop, HeadGap, Dock.Reserve+32 (they ease their height): the gutter, the cap, the top and the gap
//           │                are the content tier's `ClassicHomeLayout` (`Rules/Tiers.Home.cs`: Standard is 20 / 1600 / 24, Huge caps at 2400, Short tops at 16)
//           │                ScrollScope=PageScope
//           ├─ Home.FacetRow (the SelectorBar in R.Type.FacetTitleStyle — the facet words ARE the page title —
//           │     · Following ToggleButton · busy ProgressBar)
//           │     its band is `.Sticky(StuckInset, scope: PageScope)` — transparent, no acrylic
//           └─ content  ZStack `.StickyClip(StuckBottom)`: a plain cut. The FEATHER is each headerless zone's own (`ClassicHomeZones.Chapter`)
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
// (`StickyClip(StuckBottom)`). What dissolves into the band is the content that SCROLLS under it: a headerless zone feathers itself at that
// line, and a zone with a header feathers its body under the header (`ClassicHomeZones.Chapter`). A PINNED HEADER IS NEVER FEATHERED: it is
// parked on the line, not passing under it. One feather over the whole content softened the top of every header parked there (its eyebrow
// faded, a second fade over the one its own body makes under it; owner, 2026-11-18). Unlike Browse the band is IN the scroll flow (an in-flow
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
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Home page.</summary>
internal static class ClassicHomePage
{
    /// <summary>The registered template of <see cref="ViewKeys.HomePage"/>.</summary>
    public static Element Template(HomePageProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        float gutter = ClassicTiers.PageOf(facts).Gutter;   // the one page gutter (F3): 16 / 32 / 36 by the content tier
        var layout = ClassicHomeTiers.Of(facts);             // the tier's record (plan 3.6, TC4): page cap, rhythm, daylist form, column bands
        // Reveal None: the zones own their entrance (Design.Entrance rows in the zone column); the shimmer is the SAME tree
        // rendered against the seed, so the skeleton is derived from the real page, never authored by hand.
        Element region = new SkelRegionEl(
            Pending: props.Pending,
            Failed: props.Failed,
            Content: () => FacetContent(props, layout),
            ShimmerSource: () => ClassicHomeZones.Column(props.Seed(), props.Facet, props.Overlay, layout),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.RetryFirstLoad),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: props.Group, SmoothResize: false);

        // THE CLIP IS THE CONTRACT: the band paints nothing, so the content cuts itself at the band's lower edge. The feather of that cut is
        // each headerless zone's own (see the file header): the pinned chapter headers parked on the line are not softened.
        // The facet failure's InfoBar has its own slot (tuning H1): a layer stacked over the content's top edge, so the zones keep their
        // place when a switch fails and when the bar closes.
        Element content = new BoxEl
        {
            Key = "home:content", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ZStack = true,
            // The column's gutter, top and head gap are the content tier's and step when it changes (a docked panel, a resize): the content glides there.
            Layout = ClassicTierMotion.Shove,
            Children = [region, ClassicHomeFacetRow.FailureLayer(props.FacetRow)],
        }.StickyClip(ClassicHomeRules.StuckBottom);

        // The centred page column (`ClassicArtistPage`'s Magazine): Grow toward the free width, capped at PageMaxW, the wide
        // gutter, the dock's reserve under the last section. It NAMES the sticky scope the facet band pins against. The
        // facet row is the page title, so the column opens 24 below the top and steps 20 to the content (the prototype's
        // facet-row → first zone step); the zones space themselves at SectionGapWide inside the zone column.
        Element column = new BoxEl
        {
            Key = "home:column", Direction = 1,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f,
            AlignItems = FlexAlign.Stretch, ScrollScope = ClassicHomeRules.PageScope,
            // The page top, the head gap and the dock's reserve are spacers, not padding and gap: each steps at a tier edge (the top 16 -> 12 and
            // the player bar's reserve at the Compact edge), and a padding or a gap steps the column's height at once where a spacer's height
            // eases through real layout (`HeightReflow`), so the page's extent glides with the parts that already glide (`Shove`).
            Children =
            [
                Spacer("home:page-top", layout.PageTop),
                // On All the facet row ends with the board's tools (Home's ⋯; Edit Home's bar in its slot), handed through Classic's own channel.
                // The row's glide rides a box of its own: a provider passes no layout transition to the view it wraps.
                new BoxEl
                {
                    Key = "home:facet-row", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Layout = ClassicTierMotion.Shove,
                    Children = [Ctx.Provide(ClassicHomeFacetRow.BoardTools, props.BoardTools, ctx.View(ViewKeys.HomeFacetRow, props.FacetRow))],
                },
                Spacer("home:head-gap", layout.HeadGap),
                content,
                Spacer("home:dock-reserve", R.Space.DockReserve + Spacing.XXXL),
            ],
        };

        // Keyed per PUBLISHED facet (`ClassicArtistPage`'s idiom): each facet keeps its own scroll offset under its own
        // ScrollKey, and the handle feeds the Refreshing swap's at-top test (FacetSwitch.Landed).
        // The gutter is a spacer on each side too (16 at Compact, 32 / 36 wider): a padding steps the column's width at once at the edge, where a
        // spacer's width eases through real layout (`WidthReflow`), and the column and everything in it ride it.
        Element page = new BoxEl
        {
            Key = "home:page", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = layout.PageMaxW,
            AlignItems = FlexAlign.Stretch,
            Children = [Gutter("home:gutter-start", gutter), column, Gutter("home:gutter-end", gutter)],
        };

        Element scroll = Ui.ScrollView(new BoxEl { Direction = 0, Justify = FlexJustify.Center, MinWidth = 0f, Children = [page] })
            with
            {
                Key = props.Facet.Length > 0 ? "home:scroll:" + props.Facet : "home:scroll", Grow = 1f, MinWidth = 0f,
                ScrollKey = props.ScrollKey,
                Handle = props.Scroll,
                Anchoring = true,   // what the user looks at stays put when the page above it changes size (a tier edge, a shelf standing up)
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

    /// <summary>A horizontal spacer whose width eases through real layout when a tier changes it.</summary>
    static Element Gutter(string key, float width) => new BoxEl
    {
        Key = key, Width = width, Shrink = 0f, HitTestVisible = false, Layout = ClassicTierMotion.WidthReflow,
    };

    /// <summary>A vertical spacer whose height eases through real layout when a tier changes it.</summary>
    static Element Spacer(string key, float height) => new BoxEl
    {
        Key = key, Height = height, Shrink = 0f, HitTestVisible = false, Layout = ClassicTierMotion.HeightReflow,
    };

    /// <summary>The facet content: one KEYED subtree per published facet — the swap IS the reconciler's keyed Exit/Enter reconcile
    /// (F13), not a timer — over ONE dim host for the whole zone column (F17/F18): opacity and hit-testing are compositor binds on the
    /// switch phase, and NOTHING ELSE. The host carries no pointer handler: any OnPointerDown/OnHoverMove/… sets the node's PointerBit
    /// (Reconciler.cs "hit-testable so it receives press/drag AND bare-hover/exit"), which made the whole zone column an interactive
    /// hover scope — every card → gap → card move flipped it and re-walked hover over the column (seventh pass: the late hover). F35's
    /// "interacted" mark went with it; a Refreshing landing swaps on at-top + within-window alone (FacetSwitch.Landed).</summary>
    static Element FacetContent(HomePageProps props, ClassicHomeLayout layout)
    {
        var m = props.Content();
        props.OnContent(m);
        return new BoxEl
        {
            Key = m.Facet.Length > 0 ? "facet:" + m.Facet : "facet", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
            Transition = MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
            Children =
            [
                new BoxEl   // the dim host
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Opacity = Prop.Of(() => FacetDimPlan.Of(props.Switch.Value.Phase).Opacity),      // compositor-only (F18)
                    HitTestVisible = Prop.Of(() => !props.Switch.Value.Dim),                          // E15
                    // On All the pinned board stands where the zone column stood (the scroller, the band, the clip, the dim host stay the page's).
                    Children = [props.Board is { } board ? board() : ClassicHomeZones.Column(m, m.Facet, props.Overlay, layout)],
                },
            ],
        };
    }
}
