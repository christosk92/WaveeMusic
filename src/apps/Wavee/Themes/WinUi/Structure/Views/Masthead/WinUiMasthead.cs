// ── Themes/WinUi/Structure/Views/Masthead/WinUiMasthead.cs — WinUI's `Shell.Masthead` and `Page.Head` (themes plan 6.2; P6 W2a) ────────
//
// Boards W-Browse (the page title in Title Large) and W-BrowseCategory / W-Discography / W-Concert ("Browse › Pop": a BreadcrumbBar over the
// page, the current page last). The ONE masthead is mounted by the content host as an overlay on its page-swap boundary (the core host keeps
// the state machine: which route's publication is live, the trail it held while an unknown family fades, the "Show all" delegate); this file
// draws what it was handed:
//
//   Shell.Masthead  the band on the tier's inset (16 / 24 / 36 / 48 either side, 16 / 20 / 24 / 32 from the card's top): the page head and the
//                   "Show all" tool on its baseline, keyed on the head it shows, swapping on the page swap's clock (opacity only).
//   Page.Head       by the route's form, never the trail's length (a head keeps one height on its route):
//                     Title  the title alone, Title Large (40/52)
//                     Drill  the breadcrumb's 28 slot (the ancestors and the current page) over the title
//                     Hero   the breadcrumb's slot alone: the page's own hero carries its title
//                   A trail shorter than two crumbs (a root page) keeps its slot empty.
//
// The band is never taller than the reserve the pages under it keep (`BrowseMastheadMetrics`): its top is the tier's inset, at most the 32 the
// reserve assumes, and its heads are the same 52 / 28 lines.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's masthead band and page head.</summary>
internal static class WinUiMasthead
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellMasthead"/>.</summary>
    public static Element Template(MastheadProps props, in ViewContext ctx)
    {
        var frame = WinUiTiers.FrameOf(ctx.Tier());   // the band's head sits on the page's inset
        if (!props.Live || props.Held is not { } chrome)
            return new BoxEl { MinWidth = 0f, Direction = 1, HitTestPassThrough = true };

        Element tools = chrome.ToolsVisible
            ? Button.Create(Loc.Get(Strings.Browse.ShowAll), props.RunTools, ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !chrome.ToolsLoading)
            : new BoxEl();

        return new BoxEl
        {
            MinWidth = 0f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Key = BandKey(chrome),
                    Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
                    Padding = new Edges4(frame.InsetX, frame.InsetTop, frame.InsetX, 0f),
                    Animate = WinUiFrameRules.MastheadSwap,
                    Children = [ctx.View(ViewKeys.PageHead, new PageHeadProps(chrome)), tools],
                },
            ],
        };
    }

    /// <summary>The band's identity: the head it shows (form, trail, title). A different head swaps with the page; a re-publish stays.</summary>
    private static string BandKey(PageChrome chrome)
    {
        var key = new System.Text.StringBuilder("shell.masthead:").Append((int)chrome.Form).Append(':');
        foreach (var crumb in chrome.Crumbs) key.Append(crumb.Label).Append('›');
        return key.Append(chrome.Title).ToString();
    }

    /// <summary>The registered template of <see cref="ViewKeys.PageHead"/>.</summary>
    public static Element PageHead(PageHeadProps props, in ViewContext ctx)
    {
        var chrome = props.Chrome;
        return chrome.Form switch
        {
            PageHeadForm.Hero => Trail(chrome.Crumbs),
            PageHeadForm.Drill => new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiFrameRules.TrailTitleGap,
                Children = [Trail(chrome.Crumbs), Title(chrome.Title)],
            },
            _ => new BoxEl { Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Children = [Title(chrome.Title)] },
        };
    }

    /// <summary>The page title: Title Large, one line, keyed for the inset lint (`winui.page.title`).</summary>
    private static Element Title(string title) => R.Type.PageHero(title) with
    {
        Key = WinUiFrameLayout.PageTitleKey, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>The breadcrumb's 28 slot: WinUI's BreadcrumbBar over every crumb (the current page last, not a link) on the slot's bottom edge;
    /// empty below two crumbs, keeping its height.</summary>
    private static Element Trail(IReadOnlyList<PageCrumb> crumbs)
    {
        Element[] kids = [];
        if (crumbs.Count >= WinUiFrameRules.TrailMinDepth)
        {
            var labels = new string[crumbs.Count];
            for (int i = 0; i < labels.Length; i++) labels[i] = crumbs[i].Label;
            kids = [BreadcrumbBar.Create(labels, i => { if ((uint)i < (uint)crumbs.Count) crumbs[i].Go?.Invoke(); })];
        }
        return new BoxEl
        {
            Key = "masthead-trail", Direction = 0, Height = WinUiFrameRules.TrailSlotH, Shrink = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.End, Children = kids,
        };
    }
}
