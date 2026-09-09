using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Catalog;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Route helpers for the dedicated discography facet page: "disco:{kindInt}:{artistUri}". The uri may itself contain ':'
// (spotify uris), but the kind is a single leading digit, so parsing is unambiguous (kind at [6], uri from [8]).
static class DiscographyRoute
{
    public static string Make(DiscographyKind kind, string artistUri) => $"disco:{(int)kind}:{artistUri}";
    public static bool Is(string name) => name.StartsWith("disco:", StringComparison.Ordinal);

    public static (DiscographyKind Kind, string Uri) Parse(string name)
    {
        int k = name.Length > 6 && char.IsDigit(name[6]) ? name[6] - '0' : 0;
        string uri = name.Length > 8 ? name[8..] : "";
        return ((DiscographyKind)k, uri);
    }

    public static string FacetTitle(DiscographyKind k) => k switch
    {
        DiscographyKind.Albums => "Albums",
        DiscographyKind.Singles => "Singles",
        DiscographyKind.Compilations => "Compilations",
        _ => "Releases",
    };

    public static string FacetWord(DiscographyKind k, int n) => k switch
    {
        DiscographyKind.Albums => n == 1 ? "album" : "albums",
        DiscographyKind.Singles => n == 1 ? "single" : "singles",
        DiscographyKind.Compilations => n == 1 ? "compilation" : "compilations",
        _ => n == 1 ? "release" : "releases",
    };
}

// The full discography facet page: a breadcrumb ("{Artist} › {Facet}") over the WHOLE facet — the uncapped, virtualized
// DiscoGrid in its own scroll view. ONE instance serves successive facet routes (ContentHost collapses the slot); the VC +
// the grid re-key on the route so a different facet reloads cleanly in place.
sealed class DiscographyPage : Component
{
    readonly Signal<Route> _route;
    public DiscographyPage(Signal<Route> route) { _route = route; }

    // The facet set, in ONE order: the SelectorBar's item order, the index the route maps to, and the labels the
    // breadcrumb's last crumb shows all read these two arrays.
    static readonly DiscographyKind[] FacetKinds =
        [DiscographyKind.Albums, DiscographyKind.Singles, DiscographyKind.Compilations];
    static readonly string[] FacetLabels =
        [DiscographyRoute.FacetTitle(DiscographyKind.Albums),
         DiscographyRoute.FacetTitle(DiscographyKind.Singles),
         DiscographyRoute.FacetTitle(DiscographyKind.Compilations)];

    static int FacetIndex(DiscographyKind k)
    {
        for (int i = 0; i < FacetKinds.Length; i++) if (FacetKinds[i] == k) return i;
        return 0;
    }

    IReadSignal<Album[]>? _items;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        if (svc is null) return new BoxEl { Grow = 1f };

        var route = _route.Value;                    // subscribe → serve successive facet routes in place
        var (kind, uri) = DiscographyRoute.Parse(route.Name);
        string artistName = string.IsNullOrEmpty(route.Arg) ? "Artist" : route.Arg!;

        var releases = QueryHooks.Use(Context, static (page, value) => page.SetReady(value),
            svc.Queries, new ArtistReleasesQuery(svc.CatalogScope, uri, kind),
            new Wavee.Core.DiscographyPage([], 0),
            new QueryDemand(true, QueryPriority.Visible, []));
        var itemsSignal = UseComputed(() => releases.Loadable.Value.Value.Items.ToArray());
        _items = itemsSignal;

        var pageScroll = UseSignal(0f);
        void Play(string u) => _ = svc.Player.PlayAsync(u, 0);

        // THE PAGE HEAD — stock breadcrumb, page title, facet bar. Three shared controls replacing three bespoke ones:
        // a hand-rolled clickable crumb, a hand-drawn ChevronRight separator, and a DropDownButton skinned up to the
        // 28-DIP Title rung so it could impersonate the last crumb. HomeSectionPage does exactly this one drill-in from
        // Home, which is what a drill-in page is supposed to look like in this app.
        Element breadcrumb = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                BreadcrumbBar.Create(
                    [artistName, DiscographyRoute.FacetTitle(kind)],
                    i => { if (i == 0) go("artist:" + uri, artistName); }),
                WaveeType.PageHero(DiscographyRoute.FacetTitle(kind)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                // Albums / Singles / Compilations is a SINGLE-SELECT facet set, so it is a SelectorBar — the app's one
                // facet control (Search, History and Settings already speak it). It used to be three radio items inside
                // a flyout, which hid two of the three choices behind a click on a control shaped like a page title.
                // A fresh selection signal per render is the ArtistSchedulePage idiom: props are re-pushed live, and the
                // route (not the control) is the source of truth for which facet is showing.
                SelectorBar.Create(FacetLabels, new Signal<int>(FacetIndex(kind)),
                    onChange: i =>
                    {
                        var next = FacetKinds[i];
                        if (next != kind) go(DiscographyRoute.Make(next, uri), artistName);
                    }),
            ],
        };

        var content = new BoxEl
        {
            // W1a-alias: WaveeSize.SectionGap is the shared page-section gap W1a adds to WaveeTokens.cs.
            Direction = 1, Gap = WaveeSize.SectionGap,
            // The desktop page gutter (Spacing.PageWide 36) with the standard 24 top — the same frame every other
            // full page takes. Was a bespoke 32/40.
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, PlayerDock.Reserve + Spacing.PageWide),
            Children =
            [
                breadcrumb,
                // Deep-link compatibility uses the same complete, virtualized grid as the inline artist section.
                Embed.Comp(() => new DiscoGrid(() => _items!.Value.Length,
                    index => (uint)index < (uint)_items!.Value.Length ? _items.Value[index] : null,
                    svc, go, Play)) with { Key = "disco-grid:" + route.Name },
            ],
        };

        var scroll = ScrollView(content) with
        {
            Grow = 1f, ScrollKey = route.Name,
            OnScrollGeometryChanged = (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY),
        };
        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll, scroll);
    }

}
