// ── Themes/WinUi/Structure/Views/Artist/WinUiArtistPage.cs — WinUI's `Pages.Artist` (themes plan 6.2; P6 W6a) ──────────────────────────────────────────
//
// Board W-Artist (C / S / Std / H / 4K): the page is one scroll column on the tier's inset:
//
//   Pages.Artist
//   └─ Ctx.Provide(Design.AccentCtx = the page accent) ▸ Ctx.Provide(LazyScroll = the scroll offset)
//      └─ Column  Key=artist-page:<route>  OnBoundsChanged = the host's measure
//         ├─ the shell-tint claim (a zero-size leaf the host built)
//         └─ ScrollView  Key=artist-scroll:<route>
//            └─ keyed child artist-body:<scope epoch>
//               └─ SkelRegionEl (Reveal.Soft)
//                  ├─ Content  Column [ the hero card · (Compact: the actions row) · the PIVOT (sticky) · the overview · the sections ]
//                  └─ Shimmer  the SAME card over placeholders, the pivot's height, the overview's rows
//
// THE PIVOT (`WinUiArtistPivot`): a SelectorBar of Overview · Albums · Singles & EPs · Compilations · Appears on · About, sticky under the card on an opaque layer with a hairline,
// the lit tab the core's scroll spy's answer. Its height, 41 with the hairline, is the tier policy's `BandClearance`: a pivot click parks the section's top exactly under it
// (`scene.BringIntoView(viewport, anchor, margin: clearance)`), and the spy's activation line is measured from the same edge. Overview scrolls home.
//
// What the view does NOT own: the demands, the readiness, the section plan and the pivot's membership, the metrics' hysteresis, the accent, the spy and its anchors (the host hands
// each destination's `OnRealized`), the tint claim, and the Play / Shuffle / Radio decisions. The collapse arithmetic of Classic's band does not exist here: nothing pins but the pivot.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's artist page.</summary>
internal static class WinUiArtistPage
{
    static readonly string[] s_secKeys = SectionKeys("sec:");
    static readonly string[] s_anchorKeys = SectionKeys("anchor:");

    static string[] SectionKeys(string prefix)
    {
        var keys = new string[ArtistSections.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = prefix + ArtistSections.Key((ArtistSection)i);
        return keys;
    }

    /// <summary>The registered template of <see cref="ViewKeys.ArtistPage"/>.</summary>
    public static Element Template(ArtistPageProps p, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var tier = WinUiArtistTiers.TierOf(in facts);
        var latchCell = ctx.Hooks.UseRef<WinUiArtistLatch?>(null);
        var latch = latchCell.Value ??= new WinUiArtistLatch();
        var registry = ctx.Hooks.UseContext(ViewRegistry.Context);
        latch.Surface ??= ListStyleLookup.Of(registry, StyleKeys.Surface);
        var chart = ListStyleLookup.Of(registry, StyleKeys.ArtistChart);

        Element? body = p.BodyReady ? Compose(p, latch, tier) : null;
        // ONE region, and the shimmer is DERIVED (a representative tree at the loaded page's geometry, never mounted directly).
        Element region = new SkelRegionEl(
            Pending: p.Pending, Failed: p.Failed, Content: () => body ?? new BoxEl(), ShimmerSource: () => Shimmer(p, chart, tier),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: p.Retry),
            Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);

        Element scroll = ScrollView(new BoxEl
        {
            Direction = 1,
            // Keyed CHILD (a key on a single content root is inert): a scope switch remounts the sections, whose anchors re-register after the host's reset.
            Children = [new BoxEl { Key = "artist-body:" + p.ScopeKey, Direction = 1, Children = [region] }],
        }) with
        {
            Key = "artist-scroll:" + p.RouteKey, Grow = 1f, ScrollKey = p.ScrollKey,
            // The spy resolves against, and a pivot click scrolls, THIS viewport: never a nested scroller.
            OnRealized = p.CaptureViewport, EdgeCues = ScrollEdgeCues.None, Handle = p.Scroll,
        };

        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent,
            Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)p.ScrollY, new BoxEl
            {
                Key = "artist-page:" + p.RouteKey, Grow = 1f, Direction = 1, OnBoundsChanged = p.Measure,
                Children = [p.Tint, scroll],
            }));
    }

    // ── the loaded page ──────────────────────────────────────────────────────────────────────────────────────────────

    static Element Compose(ArtistPageProps p, WinUiArtistLatch latch, WidthTier tier)
    {
        Artist a = p.Artist;
        string uri = a.Uri.Text;
        float inset = WinUiDetailRules.InsetOf(tier);
        float cardW = MathF.Max(1f, p.Width - 2f * inset);
        var shelf = WinUiArtistTiers.ShelfOf(tier);
        var acts = new WinUiArtistHero.Acts(p.Play, p.Shuffle, p.Radio, latch.ArtistMenu(a));
        var text = Artist.HeroText.For(a);

        var head = new List<Element>(2)
        {
            WinUiArtistHero.Card(in text, uri, p.HeroUrl, cardW, tier, in acts),
        };
        if (tier == WidthTier.Compact) head.Add(WinUiArtistHero.CompactActions(a.Name, uri, in acts));
        Element hero = new BoxEl
        {
            Key = "artist-head", Direction = 1, MinWidth = 0f,
            Padding = new Edges4(inset, WinUiDetailRules.InsetTopOf(tier), inset, WinUiArtistRules.HeroToPivot),
            Children = head.ToArray(),
        };

        // EVERY section keyed: sections appear mid-stream as data lands, and keyless siblings would pair against their neighbours' old subtrees. The overview takes
        // Popular, Upcoming and Latest release; the rest follow in the plan's order, a destination's holder being its anchor.
        var plan = p.Sections;
        var sections = new List<Element>(plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            var slot = plan[i];
            if (slot.Section is ArtistSection.Popular or ArtistSection.Upcoming or ArtistSection.LatestRelease) continue;
            Element sectionBody = WinUiArtistSections.Body(slot.Section, p, shelf);
            sections.Add(new BoxEl
            {
                Key = slot.Anchor is null ? s_secKeys[(int)slot.Section] : s_anchorKeys[(int)slot.Section], Direction = 1, MinWidth = 0f,
                OnRealized = slot.Anchor,
                Children = [slot.Anchor is null ? sectionBody : sectionBody with { Key = s_secKeys[(int)slot.Section] }],
            });
        }
        Element magazine = new BoxEl
        {
            Key = "artist-magazine", Direction = 1, Gap = WinUiArtistRules.SectionGap, MinWidth = 0f,
            Padding = new Edges4(inset, WinUiArtistRules.OverviewTop, inset, R.Space.DockReserve + WinUiArtistRules.DockExtra),
            Children = [WinUiArtistOverview.Block(p, latch, tier, inset), .. sections],
        };
        return new BoxEl { Direction = 1, Children = [hero, WinUiArtistPivot.Bar(p, inset), magazine] };
    }

    // ── the skeleton ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The derived shimmer's SOURCE: the same card over placeholders at the tier's real height, the pivot's row and the chart's own skeleton, so nothing shifts when
    /// the content lands.</summary>
    static Element Shimmer(ArtistPageProps p, ArtistChartStyle? chart, WidthTier tier)
    {
        float inset = WinUiDetailRules.InsetOf(tier);
        float cardW = MathF.Max(1f, p.Width - 2f * inset);
        var acts = new WinUiArtistHero.Acts(null, null, null, null);
        Element hero = new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Padding = new Edges4(inset, WinUiDetailRules.InsetTopOf(tier), inset, WinUiArtistRules.HeroToPivot),
            Children = [WinUiArtistHero.Card(Artist.HeroText.Placeholder, "", null, cardW, tier, in acts)],
        };
        Element chartShape = chart is null ? new BoxEl()
            : chart.Skeleton(ArtistChartSkeletonArgs.For(0, !WinUiOptions.ArtworkInRows(), WinUiOptions.ArtworkInRows(), WinUiArtistTiers.ChartColumnsOf(tier), WinUiArtistTiers.ChartRowsOf(tier)));
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                hero,
                new BoxEl { Height = WinUiArtistRules.PivotClearance },
                new BoxEl { Direction = 1, Padding = new Edges4(inset, WinUiArtistRules.OverviewTop, inset, 0f), Children = [chartShape] },
            ],
        };
    }
}
