// ── Themes/Zune/Structure/Views/Artist/ZuneArtistPage.cs — Zune's `Pages.Artist` (themes plan 6.3; P5 Z6a2) ──────────────────────────────
//
// Board Z-Artist (Standard; -Compact / -Small / -Huge / -Huge4K are the tier records, ZT6 tunes them). The page has NO band of its own: Zune's band
// is the frame's, and its row 2 (the artist's name, the pivot of albums · singles & eps · compilations · details, play · shuffle · follow) and the
// BLEED behind it (the artist's header image, faint, fading out) are published by the core host through the page chrome (`Artist.PageHost`).
//
//   Pages.Artist
//   └─ Ctx.Provide(Design.AccentCtx = the page accent) ▸ Ctx.Provide(LazyScroll = the scroll offset)
//      └─ Column  Key=zune-artist-page:<route>  OnBoundsChanged = the host's measure
//         ├─ the shell-tint claim (a zero-size leaf the host built)
//         └─ ScrollView  Key=zune-artist-scroll:<route>
//            └─ keyed child zune-artist-body:<scope epoch>
//               └─ SkelRegionEl (Reveal.Soft, Group null: the chart's inner region must not join this one)
//                  ├─ Content  ZStack [ the blend wash over the hero, Column [ hero · (Compact) the actions row · the magazine ] ]
//                  └─ Shimmer  the SAME hero over a placeholder, the magazine's chart skeleton
//
// THE MAGAZINE is the sections in the plan's order (`ZuneArtistSections`), 28 apart in the system page column (`ZuneFrameRules.PageColumn`: the
// frame's gutter, 2400 cap, centred: the band's x, audit A1), the player dock's reserve under the last one. Each section's holder is keyed by what it is and carries the
// pivot destination's anchor; a tier crossing (or a docked panel that narrows the content tier) eases the holders' heights through real layout
// (`ZuneMotion.HeightResize`), so every section under a changed one rides it; nothing is keyed by a tier. The hero's floor, the air around its
// copy and the body's top are easing spacers for the same reason (padding cannot ease).
//
// PRODUCTION MOTION IS KEPT: ONE visual swap (overview + measured width + the chart settled, `BodyReady`) as a soft reveal over the derived shimmer,
// exactly as the page always did (Reveal.Soft; the chart's region joins no group); the artist's photograph is the material's, which fades in on
// its own ready edge. What the view does NOT own: the demands, the readiness, the section plan and the pivot's membership, the accent, the scroll
// spy and its anchors, the tint claim, the row-2 and bleed publication and the Play / Shuffle / Radio decisions.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Rules = Wavee.Themes.Zune.ZuneArtistRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's artist page.</summary>
internal static class ZuneArtistPage
{
    /// <summary>The hook cell of one mounted page: the chart's resolved style and the hero's More menu (built at open for the artist of the latest
    /// render; one delegate per mount).</summary>
    sealed class Latch
    {
        public ArtistChartStyle? Chart;
        Artist _menuArtist;
        Func<ContextMenuModel?>? _menu;

        public Func<ContextMenuModel?> ArtistMenu(Artist a)
        {
            _menuArtist = a;
            return _menu ??= () =>
            {
                var artist = _menuArtist;
                return artist.IsValid
                    ? Menus.Container(ActionTarget.ForArtist(artist.Uri, artist.Name), Controls.ArtUrl(artist.ImageId), null, new ContainerExtras { OnPage = true })
                    : null;
            };
        }
    }

    /// <summary>The registered template of <see cref="ViewKeys.ArtistPage"/>.</summary>
    public static Element Template(ArtistPageProps p, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var lay = ZuneArtistTiers.Of(in facts);
        var latchCell = ctx.Hooks.UseRef<Latch?>(null);
        var latch = latchCell.Value ??= new Latch();
        var registry = ctx.Hooks.UseContext(ViewRegistry.Context);
        var chart = latch.Chart ??= ListStyleLookup.Of(registry, StyleKeys.ArtistChart);

        // ONE visual swap: overview + measured width + chart settled. Snap, not FadeOnly (the hero must not fade from 0 over a held photograph).
        Element? body = p.BodyReady ? Compose(p, latch, lay) : null;

        // ONE region, and the shimmer is DERIVED: a representative tree at the loaded page's geometry, never a tree to mount. Reveal.Soft is the
        // page-level reveal the rest of the app uses; Group null, not the route: the chart's inner region must NOT join this one.
        Element region = new SkelRegionEl(
            Pending: p.Pending, Failed: p.Failed, Content: () => body ?? new BoxEl(), ShimmerSource: () => Shimmer(p, chart, lay),
            OnFailed: () => ZuneDetailKit.PageVacancy(Controls.Vacancy(Controls.VacancyVoice.Error, onAction: p.Retry), lay.Gutter),
            Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
            Group: null, SmoothResize: false);

        Element scroll = Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            // A keyed CHILD (a key on a single content root is inert): a scope switch remounts the sections, whose anchors re-register after the
            // host's render-time reset.
            Children = [new BoxEl { Key = Rules.BodyPrefix + p.ScopeKey, Direction = 1, MinWidth = 0f, Children = [region] }],
        }) with
        {
            Key = Rules.ScrollPrefix + p.RouteKey, Grow = 1f, ScrollKey = p.ScrollKey,
            // The spy resolves against, and a pivot click scrolls, THIS viewport: never a nested scroller.
            OnRealized = p.CaptureViewport,
            Handle = p.Scroll,
        };

        // The page PROVIDES its accent (Design.AccentCtx): the heart and every other ambient consumer read it.
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent,
            Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)p.ScrollY, new BoxEl
            {
                Key = Rules.PagePrefix + p.RouteKey, Grow = 1f, Direction = 1, MinWidth = 0f, OnBoundsChanged = p.Measure,
                Children = [p.Tint, scroll],
            }));
    }

    // ── the loaded page ──────────────────────────────────────────────────────────────────────────────────────────────

    static Element Compose(ArtistPageProps p, Latch latch, ZuneArtistLayout lay)
    {
        Artist a = p.Artist;
        string uri = a.Uri.Text;
        var acts = new ZuneArtistHero.Acts(p.Play, p.Shuffle, p.Radio, latch.ArtistMenu(a));

        // EVERY section keyed: sections appear mid-stream as data lands, and keyless siblings would pair against their neighbours' old subtrees.
        var plan = p.Sections;
        bool railed = false;
        for (int i = 0; i < plan.Count; i++) if (plan[i].Section == ArtistSection.Popular) railed = true;
        var sections = new List<Element>(plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            var slot = plan[i];
            if (!ZuneArtistSections.Draws(slot.Section, railed)) continue;
            string key = ArtistSections.Key(slot.Section);
            Element body = ZuneArtistSections.Body(slot.Section, p, lay);
            // The gap above every section but the first is an easing spacer keyed by the section, not the magazine's Gap: a gap cannot ease, and
            // a tier step that changes it (Huge 4K's 44 to Huge's 28 when the panes dock) would shove every section by its count of gaps at once.
            if (sections.Count > 0) sections.Add(ZuneMotion.Air(Rules.AirPrefix + "gap:" + key, lay.ZoneGap));
            sections.Add(new BoxEl
            {
                Key = (slot.Anchor is null ? Rules.SectionPrefix : Rules.AnchorPrefix) + key, Direction = 1, MinWidth = 0f,
                // Its height eases through real layout (a tier step re-lays the chart, the shelves, "about"), so the sections under it ride it.
                Layout = ZuneMotion.HeightResize, OnRealized = slot.Anchor,
                // The body keeps its own key (the popular band, the about band): the holder above carries the section's.
                Children = [body],
            });
        }

        Element hero = ZuneArtistHero.Build(Artist.HeroText.For(a), uri, lay, p.Accent, in acts);
        float washH = lay.HeroH + Rules.WashTail;
        // The blend wash, from the cover's grading (a cover-keyed leaf: a grading re-renders only the wash). Under artwork wash only; the leaf reads
        // the option itself.
        Element wash = Palette.ArtistBlendWash(p.PaletteUrl, washH, lay.HeroH / washH, key: Rules.WashKey + uri, payloadAccent: a.HeaderAccent);

        // The Compact row under the hero (play · shuffle · follow) stands in a slot that is ALWAYS mounted: crossing 640 its room opens and closes as an
        // easing height while the row fades in it, so the magazine under it glides instead of stepping 48 on the commit frame.
        bool below = lay.Actions == ZuneArtistActions.BelowHero;
        var column = new List<Element>(4)
        {
            hero,
            new BoxEl
            {
                Key = Rules.ActionsKey + ":below-slot", Direction = 1, MinWidth = 0f, Shrink = 0f, ClipToBounds = true,
                Height = below ? lay.Target + Rules.BelowRowTop : 0f, Layout = ZuneMotion.HeightResize,
                Children = below ? [ZuneArtistHero.BelowRow(a.Name, uri, lay, in acts)] : [],
            },
        };
        column.Add(BodyAir(lay));
        column.Add(Magazine(sections.ToArray(), lay));

        return new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Height = washH, AlignSelf = FlexAlign.Start, HitTestVisible = false, Children = [wash] },
                new BoxEl { Direction = 1, MinWidth = 0f, Children = column.ToArray() },
            ],
        };
    }

    /// <summary>The body's top under the hero (or the Compact actions row): an easing spacer, not the magazine's padding, so a tier step (a docked
    /// panel narrowing the content) moves no section.</summary>
    static Element BodyAir(ZuneArtistLayout lay) => ZuneMotion.Air(Rules.AirPrefix + "body", lay.BodyTop);

    /// <summary>The magazine: the sections in the system page column (the frame's gutter, the 2400 cap, centred), 28 apart (the easing gap spacers
    /// <see cref="Compose"/> puts between them), the player dock's reserve under the last one.</summary>
    static Element Magazine(Element[] sections, ZuneArtistLayout lay) => ZuneDetailKit.CentredPage(new BoxEl
    {
        Key = "zune-artist-magazine", Direction = 1, MinWidth = 0f, Layout = ZuneMotion.ColumnCollapse,
        AlignItems = FlexAlign.Stretch,
        Padding = new Edges4(0f, 0f, 0f, R.Space.DockReserve + Rules.PageBottom),
        Children = sections,
    }, lay.Gutter) with { Key = "zune-artist-magazine-column" };

    // ── the skeleton ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The derived shimmer's SOURCE: the same hero over a representative shape at the tier's real heights (no actions: never skeleton
    /// content), then the chart's skeleton at the tier's columns and rows. Every geometry is the loaded page's, so nothing shifts when it lands.</summary>
    static Element Shimmer(ArtistPageProps p, ArtistChartStyle? chart, ZuneArtistLayout lay)
    {
        var none = new ZuneArtistHero.Acts(null, null, null, null);
        // A name already known (Z-State-Loading: "the weeknd" in real type over the skeleton) is the loaded hero's name, so it does not move on the reveal.
        string known = p.Artist.IsValid ? p.Artist.Name : "";
        // The caption's two usual items (listeners, followers): the placeholder carries no rank, like most artists.
        var text = (known.Length > 0 ? Artist.HeroText.Placeholder with { Name = known } : Artist.HeroText.Placeholder) with { Rank = 0 };
        Element hero = ZuneArtistHero.Build(in text, "", lay, p.AccentPeek(), in none, nameKnown: known.Length > 0);
        Element chartShape = chart is null ? new BoxEl()
            : chart.Skeleton(ArtistChartSkeletonArgs.For(0, classic: false, showArtwork: true, lay.ChartColumns, lay.ChartRows,
                new ArtistChartRowShape(lay.ChartRowH, lay.ChartArt, lay.ChartLead, lay.ChartColumns > 1 ? lay.ChartColumnGap : 0f)));
        var column = new List<Element>(3) { hero };
        if (lay.Actions == ZuneArtistActions.BelowHero) column.Add(new BoxEl { Height = lay.Target + Rules.BelowRowTop });
        column.Add(BodyAir(lay));
        column.Add(Magazine([ZuneArtistSections.PopularSkeleton(chartShape, lay)], lay));
        return new BoxEl { Direction = 1, Children = column.ToArray() };
    }
}
