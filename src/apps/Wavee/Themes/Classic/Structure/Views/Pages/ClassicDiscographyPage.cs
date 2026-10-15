// ── Themes/Classic/Structure/Views/Pages/ClassicDiscographyPage.cs — Classic's `Pages.Discography` (themes plan 6.1, T10b2) ────────
//
// From `Entities\Artist.Discography.cs` §9 (`DiscoPageHost.Render`): the render half of the standalone discography page.
//
//   Pages.Discography
//   └─ ScrollView  ScrollKey = the tab's scope + the route key, Handle = the host's (the host provides LazyScroll above the slot)
//      └─ Column  (page padding, the dock reserve under it)
//         ├─ head: the artist as the ancestors' trail (breadcrumb B, `ClassicPageHead.Trail`) over the 40/52 page title · the facet SelectorBar
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
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's standalone discography page.</summary>
internal static class ClassicDiscographyPage
{
    static readonly string[] s_facetLabelKeys = [Strings.Artist.Albums, Strings.Artist.SinglesEps, Strings.Artist.Compilations];

    /// <summary>The registered template of <see cref="ViewKeys.Discography"/>.</summary>
    public static Element Template(DiscographyPageProps p, in ViewContext ctx)
    {
        if (!p.Parsed) return Controls.Vacancy(Controls.VacancyVoice.Error);

        var labels = new string[s_facetLabelKeys.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(s_facetLabelKeys[i]);

        Element head = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                // Breadcrumb B: the artist is the one ancestor, the facet is the title (never repeated in the trail).
                new BoxEl
                {
                    Direction = 1, Gap = ClassicPageHeadRules.TrailTitleGap, MinWidth = 0f,
                    Children =
                    [
                        ClassicPageHead.Trail([new PageCrumb(p.Name, () => p.OnCrumb(0))]),
                        R.Type.SurfaceDisplay(p.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                // A FRESH signal per render: the ROUTE, not the control, is the truth; a change navigates and the next
                // route re-seeds it (0.2.9 DiscographyPage.cs:120-125).
                SelectorBar.Create(labels, new Signal<int>((int)p.Facet), onChange: p.OnFacet),
            ],
        };

        var content = new BoxEl
        {
            Direction = 1, Gap = R.Space.SectionGap,
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, R.Space.DockReserve + Spacing.PageWide),
            Children =
            [
                head,
                // A 28 top inset: there is no sticky facet band on this page.
                p.Grid is { } grid ? grid(ClassicDiscographyRules.PageTopInset) : new BoxEl(),
            ],
        };
        return ScrollView(content) with
        {
            Grow = 1f, ScrollKey = p.ScrollKey, Handle = p.Scroll,
        };
    }
}
