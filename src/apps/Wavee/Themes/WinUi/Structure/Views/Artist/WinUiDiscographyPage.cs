// ── Themes/WinUi/Structure/Views/Pages/WinUiDiscographyPage.cs — WinUI's `Pages.Discography` (themes plan 6.1, T10b2) ────────
//
// From `Entities\Artist.Discography.cs` §9 (`DiscoPageHost.Render`): the render half of the standalone discography page.
//
//   Pages.Discography
//   └─ ScrollView  ScrollKey = the tab's scope + the route key, Handle = the host's (the host provides LazyScroll above the slot)
//      └─ Column  (page padding, the dock reserve under it)
//         ├─ head: the BreadcrumbBar (the artist, then the facet) over the Title Large page title · the facet SelectorBar
//         └─ the core's grid + drawer (the SAME one the artist page's facets use), at the page's reveal inset
//
// Deliberately NO cover palette (the system accent) and NO shell tint (ch 08 W22, parity 88-89). The route parse, the demand, the scroll plumbing and the
// navigation the crumbs and the selector cause stay core (`Artist.DiscoPageHost`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's standalone discography page.</summary>
internal static class WinUiDiscographyPage
{
    static readonly string[] s_facetLabelKeys = [Strings.Artist.Albums, Strings.Artist.SinglesEps, Strings.Artist.Compilations];

    /// <summary>The registered template of <see cref="ViewKeys.Discography"/> (W-Discography): the breadcrumb ("Arash > Discography") over the title in Title Large and the meta, the
    /// facet SelectorBar (Albums · Singles &amp; EPs · Compilations), then the core's grid and album drawer, on the tier's inset.</summary>
    public static Element Template(DiscographyPageProps p, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var tier = WinUiArtistTiers.TierOf(in facts);
        float inset = WinUiDetailRules.InsetOf(tier);
        if (!p.Parsed) return Controls.Vacancy(Controls.VacancyVoice.Error);

        var labels = new string[s_facetLabelKeys.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(s_facetLabelKeys[i]);

        // W-Discography: the page is "Discography" whichever facet the selector shows (the facet is the selector's, not the title's).
        string discography = Loc.Get(Strings.Theme.Winui.Detail.Discography);
        Element trail = new BoxEl
        {
            Key = "disco-trail", Direction = 0, Height = WinUiFrameRules.TrailSlotH, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.End,
            Children = [BreadcrumbBar.Create([p.Name, discography], i => { if (i == 0) p.OnCrumb(0); })],
        };
        Element head = new BoxEl
        {
            Direction = 1, Gap = WinUiDiscographyRules.HeadGap, MinWidth = 0f,
            Children =
            [
                trail,
                R.Type.SurfaceDisplay(discography) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Key = "winui.page.title" },
                // A FRESH signal per render: the ROUTE, not the control, is the truth; a change navigates and the next route re-seeds it.
                SelectorBar.Create(labels, new Signal<int>((int)p.Facet), onChange: p.OnFacet, style: TabStyle(tier)),
            ],
        };

        var content = new BoxEl
        {
            Key = "discography-page", Direction = 1, Gap = R.Space.SectionGap,
            Padding = new Edges4(inset, WinUiDetailRules.InsetTopOf(tier), inset, R.Space.DockReserve + inset),
            Children = [head, p.Grid is { } grid ? grid(WinUiDiscographyRules.PageTopInset) : new BoxEl()],
        };
        return ScrollView(content) with { Grow = 1f, ScrollKey = p.ScrollKey, Handle = p.Scroll };
    }

    /// <summary>The release-type tabs' style: the stock item, 44 tall at Compact (the touch target).</summary>
    static SelectorBarStyle? TabStyle(WidthTier tier)
        => tier == WidthTier.Compact ? new SelectorBarStyle { ItemHeight = WinUiDiscographyRules.CompactTab } : null;
}
