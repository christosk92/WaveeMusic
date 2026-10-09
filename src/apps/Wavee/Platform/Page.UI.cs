// ── Platform/Page.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// THE PAGE HEAD: one builder for the title row, the meta line, the optional breadcrumb row above, the optional views bar
// and the hoisted strip a pivot destination keeps under the Zune band, plus the helpers that read the presented nav style
// and publish a page's views to the band.
//
// Role: UI
// Plan: docs/plans/wavee/sidebar-rework-implementation.md (the page-head package)
//
// ── KEYBOARD ─────────────────────────────────────────────────────────────────────────────────────────────────────────
// Keyboard roving, the single tab stop and Tab automation are the stock SelectorBar's: the head adds none of its own.
//
// ── LAYOUT STABILITY ─────────────────────────────────────────────────────────────────────────────────────────────────
//
// (a) A head's height is PageHeadRules.Extent(kind), and the kind is ROUTE-STATIC: hoisted, has-breadcrumb, has-views.
//     Meta, actions, the views' labels and the trailing control are not inputs, so no data arriving later changes it.
// (b) Data fills slots that were reserved from the first frame and FADES in place: the meta line is always reserved, a
//     page with views reserves the 48-DIP row while its labels are still empty (a same-size placeholder cross-fades with
//     the words).
// (c) A hoist changes the height only through Shell.Ui.PresentedNavStyle, which never changes in the commit that moves the
//     content card (it lags Sidebar.NavStyle by FrameRules.HoistSettleMs). So PageHead.Reflow is never snapped by the card's
//     descendant suppression (AppHost.cs:7392-7437). The body below is reflow-shoved: the engine lands its position on the
//     reflow's per-tick re-solve (AppHost.cs:7405-7418, 7454-7465). A page whose views moved to the Zune band's row 2
//     (ViewsInBand: Settings, Search, the people lists, the discography) changes height the same way, only on
//     PresentedNavStyle: TitleViews 164 to Title 120, CrumbTitleViews 200 to CrumbTitle 156.
// (d) A mount never animates: the engine skips FLIP capture on first layout (AppHost.cs:6024).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>What a page head shows. <see cref="Title"/> is the only required fact; every other member fills a slot whose
/// height is reserved by <see cref="PageHeadRules"/> whether or not it is filled.</summary>
public sealed record PageHeadSpec(string Title)
{
    /// <summary>The meta line ("42 songs · 3 hr"). Null or empty is reserved either way, so it can arrive late.</summary>
    public string? Meta { get; init; }

    /// <summary>The breadcrumb row above the title. Presence changes the head's kind, so it must be route-static.</summary>
    public Element? Above { get; init; }

    /// <summary>The action cluster at the title's trailing end.</summary>
    public Element? Actions { get; init; }

    /// <summary>The views' labels. Null ONLY for a page that never has views; a page whose labels arrive later passes an
    /// EMPTY list so the row is reserved from the first frame.</summary>
    public IReadOnlyList<string>? Views { get; init; }

    /// <summary>The page's own selected-view signal (one per page instance); null lets the bar own its selection.</summary>
    public Signal<int>? ViewsSelected { get; init; }

    /// <summary>A view was chosen.</summary>
    public Action<int>? OnView { get; init; }

    /// <summary>A control at the views row's trailing end (Home's Following).</summary>
    public Element? ViewsTrailing { get; init; }

    /// <summary>Shown in the reserved views row while <see cref="Views"/> is empty, and cross-faded with the words.</summary>
    public Element? ViewsPlaceholder { get; init; }

    /// <summary>Hosts the bar in a horizontal ScrollView so a long set never wraps or overflows.</summary>
    public bool ViewsScroll { get; init; }

    /// <summary>The head is hoisted into the Zune band (<see cref="PageHead.HoistedFor"/>).</summary>
    public bool Hoisted { get; init; }

    /// <summary>The views live in the Zune band's row 2 (<see cref="PageHead.ViewsInBandFor"/>), and the head keeps its title
    /// and meta. <see cref="Views"/> is still passed (the page's own bar is back the moment the style is not presented).</summary>
    public bool ViewsInBand { get; init; }

    /// <summary>The page gutter (<c>Shell.Ui.PageGutter</c>).</summary>
    public float Gutter { get; init; } = PageGeometry.GutterWide;

    /// <summary>The head's key; the rows inside derive theirs from it.</summary>
    public string Key { get; init; } = "page:head";
}

/// <summary>The page-head builder. See the file header for the layout-stability contract.</summary>
public static class PageHead
{
    /// <summary>The head's height ease: real layout, so the body below reflows with it (the AiLyrics height-reflow idiom).
    /// It only runs in quiet commits, because every caller's height depends on <c>Shell.Ui.PresentedNavStyle</c>, never on
    /// <c>Sidebar.NavStyle</c>.</summary>
    public static readonly LayoutTransition Reflow = new(TransitionChannels.Size, MotionTok.ContentResize.ToDynamics(),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>A row fades in place when it mounts.</summary>
    public static readonly EnterExit FadeIn = new(Opacity: 0f, Active: true);

    /// <summary>A row fades out in place when it leaves.</summary>
    public static readonly EnterExit FadeOut = new(Opacity: 0f, Active: true);

    static readonly MotionTokenDef s_fade =
        MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>The head's kind: three ROUTE-STATIC facts and nothing else.</summary>
    public static PageHeadKind PlanOf(PageHeadSpec s)
        => PageHeadRules.KindOf(s.Hoisted, s.Above is not null, s.Views is not null && !s.ViewsInBand);

    /// <summary>Whether a route's head hoists, read from the PRESENTED nav style (never the live one). Subscribing: call it
    /// in Render.</summary>
    public static bool HoistedFor(string routeName) => PageHeadRules.Hoisted(Shell.Ui.PresentedNavStyle.Value, routeName);

    /// <summary>Whether a non-pivot route's views live in the Zune band, read from the PRESENTED nav style (never the live
    /// one), so the head's height change lands in the quiet hoist commit. Subscribing: call it in Render.</summary>
    public static bool ViewsInBandFor(in Shell.Route route) => PageHeadRules.ViewsInBand(Shell.Ui.PresentedNavStyle.Value, in route);

    /// <summary>Hands an entity page's Zune band (title, pivots, actions) to the band's row 2 (see <c>Shell.PageBands</c>).
    /// Returns whether the band must re-render.</summary>
    public static bool PublishBand(string routeName, string title, IReadOnlyList<string> pivots, IReadSignal<int> active,
        Action<int> onPivot, Func<Element>? actions = null, Action? onTitle = null)
        => Shell.PageBands.Publish(routeName, new Shell.PageBandPublication(title, pivots, active, onPivot, actions, onTitle));

    /// <summary>Hands a page's views to the Zune band (see <c>Shell.PageViews</c>). Returns whether the band must re-render.</summary>
    public static bool Publish(string routeName, IReadOnlyList<string> labels, Signal<int> selected, Action<int> onSelect,
        Func<Element>? trailing = null)
        => Shell.PageViews.Publish(routeName, new Shell.PageViewsPublication(labels, selected, onSelect, trailing));

    /// <summary>Keeps a trailing control at its own size when the row narrows.</summary>
    static BoxEl Fixed(Element e) => new() { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [e] };

    /// <summary>The title line: the title takes the room, the actions keep their size.</summary>
    public static Element TitleRow(string title, Element? actions) => new BoxEl
    {
        Direction = 0, Height = PageGeometry.TitleLine, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Children = actions is null
            ? [Design.Type.PageTitle(title) with { Grow = 1f, Basis = 0f }]
            : [Design.Type.PageTitle(title) with { Grow = 1f, Basis = 0f }, Fixed(actions)],
    };

    /// <summary>The meta line: a box of exactly <see cref="PageGeometry.MetaLine"/>. A late text mounts and fades in place.</summary>
    public static Element MetaSlot(string? text) => new BoxEl
    {
        Direction = 0, Height = PageGeometry.MetaLine, Shrink = 0f, MinWidth = 0f,
        Children =
        [
            Design.Type.PageMeta(text ?? "") with
            {
                Key = string.IsNullOrEmpty(text) ? "meta:none" : "meta:text",
                Enter = FadeIn, Exit = FadeOut, Transition = s_fade,
            },
        ],
    };

    /// <summary>The views row: the bar (a STABLE, data-free key, so the pill slides and the bar never re-mounts), an
    /// optional same-size placeholder over it while the labels are empty, and a trailing control.</summary>
    public static Element Views(IReadOnlyList<string> labels, Signal<int>? selected, Action<int>? onChange,
        Element? trailing, Element? placeholder, bool scroll, string key)
    {
        bool empty = labels.Count == 0;
        // The bar. Scrolling: the viewport absorbs the first item's plate pad (viewport margin -12, bar inset 0 — the
        // Concert idiom), so the first WORD still sits on the gutter.
        var style = scroll ? Design.PageViewsStyle with { LeadingInset = 0f } : Design.PageViewsStyle;
        Element bar = SelectorBar.Create(labels, selected, onChange, style: style) with { Key = key + ":bar" };
        Element barHost = new BoxEl
        {
            Key = key + ":words", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
            Opacity = empty ? 0f : 1f, HitTestVisible = !empty, Transition = s_fade,
            Children = [bar],
        };
        Element core = scroll
            ? ScrollView(new BoxEl { Direction = 0, Children = [barHost] }, horizontal: true) with
            {
                Height = PageGeometry.ViewsBarH, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                SuppressScrollBar = true, AutoEdgeFade = true,
                Margin = new Edges4(PageGeometry.ViewsLeadingInset, 0f, 0f, 0f),
            }
            : barHost;
        if (placeholder is not null)
        {
            core = ZStack(core, new BoxEl
            {
                Key = key + ":placeholder", Direction = 0, AlignItems = FlexAlign.Center, HitTestVisible = false,
                Opacity = empty ? 1f : 0f, Transition = s_fade, MinWidth = 0f,
                Children = [placeholder],
            }) with { Height = PageGeometry.ViewsBarH, Shrink = scroll ? 1f : 0f, Grow = scroll ? 1f : 0f, MinWidth = 0f };
        }
        var kids = new List<Element>(3) { core };
        if (!scroll) kids.Add(new BoxEl { Grow = 1f });
        if (trailing is not null) kids.Add(Fixed(trailing));
        return new BoxEl
        {
            Direction = 0, Height = PageGeometry.ViewsBarH, Shrink = 0f, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The hoisted head's one row: the meta line at the start, the actions and the trailing control at the end.</summary>
    public static Element HoistedRow(string? meta, Element? actions, Element? trailing)
    {
        var kids = new List<Element>(4) { Design.Type.PageMeta(meta ?? ""), new BoxEl { Grow = 1f } };
        if (actions is not null) kids.Add(Fixed(actions));
        if (trailing is not null) kids.Add(Fixed(trailing));
        return new BoxEl
        {
            Direction = 0, Height = PageGeometry.ViewsBarH, Shrink = 0f, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Gap = Spacing.M, Children = kids.ToArray(),
        };
    }

    /// <summary>The head. Its height is <see cref="PageHeadRules.Extent"/> of its kind and nothing else.</summary>
    public static Element Create(PageHeadSpec s)
    {
        var kind = PlanOf(s);
        Element[] kids;
        if (kind == PageHeadKind.Hoisted)
        {
            kids =
            [
                Spacer(PageGeometry.HoistedTop),
                HoistedRow(s.Meta, s.Actions, s.ViewsTrailing) with
                {
                    Key = s.Key + ":hoisted", Enter = FadeIn, Exit = FadeOut, Transition = s_fade,
                },
            ];
        }
        else
        {
            var list = new List<Element>(8) { Spacer(PageGeometry.HeadTop) };
            if (s.Above is not null)
            {
                list.Add(new BoxEl
                {
                    Direction = 0, Height = PageGeometry.AboveLine, Shrink = 0f, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = [s.Above],
                });
                list.Add(Spacer(PageGeometry.AboveToTitle));
            }
            list.Add(TitleRow(s.Title, s.Actions) with
            {
                Key = s.Key + ":title", Enter = FadeIn, Exit = FadeOut, Transition = s_fade,
            });
            list.Add(Spacer(PageGeometry.TitleToMeta));
            list.Add(MetaSlot(s.Meta));
            if (s.Views is { } views && !s.ViewsInBand)
            {
                list.Add(Spacer(PageGeometry.HeadToViewsGap));
                list.Add(Views(views, s.ViewsSelected, s.OnView, s.ViewsTrailing, s.ViewsPlaceholder, s.ViewsScroll, s.Key + ":views")
                    with { Key = s.Key + ":views", Enter = FadeIn, Exit = FadeOut, Transition = s_fade });
            }
            kids = list.ToArray();
        }
        return new BoxEl
        {
            Key = s.Key, Direction = 1, Shrink = 0f, MinWidth = 0f, Height = PageHeadRules.Extent(kind), ClipToBounds = true,
            Animate = Reflow, Padding = new Edges4(s.Gutter, 0f, s.Gutter, 0f), Children = kids,
        };
    }
}
