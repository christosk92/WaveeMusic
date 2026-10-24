// ── Themes/Zune/Structure/Views/Home/ZuneHomePage.cs — Zune's `Pages.Home` (themes plan 6.3; P5 Z5) ──────────────────────────────────
//
// Board Z-Home (and Z-Home-Podcasts, Z-Light-Home; the Compact / Small / Huge boards are ZT4's). The page has NO title and no facet row of its
// own: Zune's band is the page head, and the facet words are its row 2 (the core host publishes them, `HomeHost`; `Home.FacetRow` is Nothing).
//
//   Pages.Home
//   └─ ZStack [ Wash (the host's shell-tint claim),
//      ScrollView (keyed per PUBLISHED facet: each facet keeps its own offset; the host's handle)
//        └─ column  `zune-home-column`  (the frame's gutter, 8 under the band, capped at the style's page width, on the page's left edge)
//           └─ ZStack [ SkelRegionEl (the zone column; the shimmer is the SAME column over the seed),
//                       the status layer (the busy bar over the top edge, the failed-switch bar: neither moves the zones) ] ]
//
//   THE COLUMN (`ZuneHomePlan.Rows`): on All the quickplay strip first (pins · history · new: `Home.Quickplay` through its core host, with the
//   Recents zone as its history), then the zones in the planner's order, 28 apart; two neighbours that pair (the release list and the
//   clusters, the new and continue episodes) share a row 1 : 1 where the tier has room (`PairBeside`, `PairGap` apart), and stack where it has not (the same
//   two nodes: only the row's direction is a live prop). Every zone is a chapter keyed by its zone: the entrance row rides its inner box
//   (`Design.Entrance.Row`, production motion), and a tier crossing reflows it (`H.ChapterResize`). On All "customize home" closes the page.
//   THE BODIES are the `Home.Zone.<Kind>` slots (`ZuneHomeZones`: a body draws its own heading row), the daylist and the history their core hosts
//   (`DaylistCard`, `RecentGrid`). Each slot and host carries a SkeletonProxy that is the SAME static builder its template runs.
//   THE FACET SWAP is the production one: the content is one keyed subtree per published facet that fades out and in, over ONE dim host whose
//   opacity and hit-testing are binds on the switch phase (`FacetDimPlan`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Home page.</summary>
internal static class ZuneHomePage
{
    /// <summary>The registered template of <see cref="ViewKeys.HomePage"/>.</summary>
    public static Element Template(HomePageProps props, in ViewContext ctx)
    {
        var lay = ZuneHomeTiers.Of(ctx.Tier());
        // Reveal None: the zones own their entrance (the entrance rows in the column); the shimmer is the SAME column over the seed.
        Element region = new SkelRegionEl(
            Pending: props.Pending,
            Failed: props.Failed,
            Content: () => FacetContent(props, lay),
            ShimmerSource: () => Column(props.Seed(), props.Overlay, lay),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.RetryFirstLoad),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: props.Group, SmoothResize: false);

        Element content = new BoxEl
        {
            Key = "zune-home-content", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ZStack = true,
            Children = [region, ZuneHomeStatus.Layer(props.FacetRow)],
        };

        // The page column (A1): capped at 2400 + the gutters and centred, so the content shares the band's x at every width.
        Element column = ZuneFrameRules.PageRow(new BoxEl
        {
            Key = H.ColumnKey, Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(0f, H.PageTop, 0f, H.PageBottom),
            Children = [content],
        }, lay.Gutter);

        // Keyed per PUBLISHED facet: each facet keeps its own offset under its own ScrollKey, and the handle feeds the Refreshing swap's
        // at-top test (FacetSwitch.Landed).
        Element scroll = Ui.ScrollView(new BoxEl { Direction = 0, MinWidth = 0f, Children = [column] }) with
        {
            Key = props.Facet.Length > 0 ? "zune-home-scroll:" + props.Facet : "zune-home-scroll", Grow = 1f, MinWidth = 0f, ScrollKey = props.ScrollKey, Handle = props.Scroll,
        };

        return new BoxEl { Grow = 1f, Direction = 1, ZStack = true, Children = [props.Wash, scroll] };
    }

    /// <summary>The facet content: one keyed subtree per published facet (the swap is the keyed Exit/Enter fade) over the dim host.</summary>
    static Element FacetContent(HomePageProps props, ZuneHomeLayout lay)
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
                new BoxEl   // the dim host: compositor binds only, no pointer handler
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Opacity = Prop.Of(() => FacetDimPlan.Of(props.Switch.Value.Phase).Opacity),
                    HitTestVisible = Prop.Of(() => !props.Switch.Value.Dim),
                    Children = [Column(m, props.Overlay, lay)],
                },
            ],
        };
    }

    // ══ THE COLUMN ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page's rows (<see cref="ZuneHomePlan.Rows"/>), then "customize home" on All.</summary>
    internal static Element Column(ScreenModel m, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var zones = m.Zones;
        var kinds = new ZoneKind[zones.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = zones[i].Kind;
        bool all = ZuneHomePlan.HasQuickplay(m.Facet);
        var rows = ZuneHomePlan.Rows(kinds, all);

        var kids = new List<Element>(rows.Length * 2 + 1);
        for (int r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            if (r > 0) kids.Add(ZoneAir(r, lay));
            switch (row.Kind)
            {
                case ZuneHomeRowKind.Quickplay:
                    kids.Add(Quickplay(row.First >= 0 ? zones[row.First] : null, row.Second >= 0 ? zones[row.Second] : null, overlay, lay, r));
                    break;
                case ZuneHomeRowKind.Pair:
                    kids.Add(Pair(zones[row.First], zones[row.Second], overlay, lay, r));
                    break;
                default:
                    kids.Add(Chapter(zones[row.First], overlay, lay, r));
                    break;
            }
        }
        if (all)
        {
            if (rows.Length > 0) kids.Add(ZoneAir(rows.Length, lay));
            // A chapter like the zones: its link's target grows to 40 at Compact, and the row's height eases with it.
            kids.Add(Box(H.CustomizeKey, new BoxEl
            {
                Direction = 0, MinWidth = 0f,
                Children = [K.Link("zune-home-customize-link", Loc.Get(Strings.Home.Customizer.Title), HomeVerbs.OpenCustomize, lay.LinkTarget)],
            }, rows.Length));
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>The air between two rows: the tier's zone gap (18 / 22 / 28 / 36) as a spacer whose height eases, never the column's Gap (a gap cannot
    /// ease: every crossing moved every chapter below the first by the difference at once).</summary>
    static Element ZoneAir(int row, ZuneHomeLayout lay)
        => ZuneMotion.Air(H.ZoneAirPrefix + row.ToString(System.Globalization.CultureInfo.InvariantCulture), lay.ZoneGap);

    /// <summary>The quickplay strip's chapter: its core host (pins, the Recents zone's host as the history, the release zone), its skeleton the view's
    /// own static tree.</summary>
    static Element Quickplay(Zone? history, Zone? fresh, IOverlayService? overlay, ZuneHomeLayout lay, int index)
    {
        Element host = Embed.Comp(new QuickplayHostProps(history, fresh, overlay), static () => new QuickplayHost())
            with { SkeletonProxy = () => ZuneHomeQuickplay.Skeleton(history, fresh, lay) };
        return Box(H.QuickplayKey, host, index);
    }

    /// <summary>Two zones side by side (1 : 1, 48 apart) where the tier has room, stacked at the zone gap where it has not: the SAME two chapters
    /// either way, so a crossing moves them.</summary>
    static Element Pair(Zone a, Zone b, IOverlayService? overlay, ZuneHomeLayout lay, int index)
    {
        bool beside = lay.PairBeside;
        return new BoxEl
        {
            Key = H.PairPrefix + a.Key + "+" + b.Key,
            Direction = (byte)(beside ? 0 : 1), Gap = beside ? lay.PairGap : lay.ZoneGap, MinWidth = 0f,
            AlignItems = beside ? FlexAlign.Start : FlexAlign.Stretch,
            Layout = H.ChapterResize,
            Children =
            [
                Chapter(a, overlay, lay, index) with { Grow = beside ? 1f : 0f, Shrink = beside ? 1f : 0f, Basis = beside ? 0f : float.NaN },
                Chapter(b, overlay, lay, index) with { Grow = beside ? 1f : 0f, Shrink = beside ? 1f : 0f, Basis = beside ? 0f : float.NaN },
            ],
        };
    }

    /// <summary>One zone's chapter: the keyed box (its height resizes by `H.ChapterResize` on a crossing) carrying its entrance on an inner box, around the
    /// zone's body. A chapter in a pair does not reveal itself: the pair does (two nested flow reveals would count a height change twice).</summary>
    static BoxEl Chapter(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay, int index, bool reveal = true)
        => Box(H.ZonePrefix + zone.Key, Body(zone, overlay, lay), index, reveal);

    static BoxEl Box(string key, Element body, int index, bool reveal = true) => new()
    {
        Key = key, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Layout = reveal ? H.ChapterResize : null,
        Children =
        [
            new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Animate = Design.Entrance.Row(index), Children = [body] },
        ],
    };

    /// <summary>A zone's body: the daylist and the history through their core hosts, every other kind through its `Home.Zone.<Kind>` slot. Each
    /// carries a SkeletonProxy (the deriver cannot run a component's render): the SAME static tree its template builds.</summary>
    static Element Body(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        switch (zone.Kind)
        {
            case ZoneKind.Daylist:
                return Embed.Comp(new DaylistProps(zone, overlay), static () => new DaylistCard())
                    with { SkeletonProxy = () => ZuneHomeDaylist.Skeleton(lay) };
            case ZoneKind.RecentGrid:
                return Embed.Comp(new RecentGridProps(zone), static () => new RecentGrid())
                    with { SkeletonProxy = () => ZuneHomeQuickplay.HistorySkeleton(zone, lay) };
            default:
                if (!HomeZoneKeys.TryKeyOf(zone.Kind, out var key)) return new BoxEl();
                Element slot = Slots.View(key, new HomeZoneProps(zone, overlay));
                return slot is ComponentEl component ? component with { SkeletonProxy = () => ZuneHomeZones.Tree(zone, overlay, lay) } : slot;
        }
    }
}

/// <summary>The status layer over the top of Home's content: the busy bar while a facet switch is in flight and the bar of a failed switch. A
/// layer, so the zones never move when either comes or goes; pass-through but for the bar itself. Re-renders on the switch alone.</summary>
internal static class ZuneHomeStatus
{
    static readonly MotionTokenDef s_fade = MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>The layer for the page's facet machine.</summary>
    public static Element Layer(FacetRowProps p) => Embed.Comp(() => new StatusView(p)) with { Key = "zune-home-status" };

    sealed class StatusView(FacetRowProps p) : Component
    {
        public override Element Render()
        {
            var state = p.Switch.Value;
            string failedTarget = state.FailedTarget ?? "";
            string failedLabel = state.FailedTarget is { } ft ? LabelOf(p.Words.Value, ft) ?? ft : "";
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Justify = FlexJustify.Start, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Key = H.BusyKey, Height = H.BusyH, MinWidth = 0f, HitTestVisible = false, Direction = 1,
                        Children =
                        [
                            Flow.Show(() => p.Switch.Value.Bar, new BoxEl
                            {
                                Key = "zune-home-busy-bar", Height = H.BusyH, MinWidth = 0f, HitTestVisible = false,
                                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true), Transition = s_fade,
                                Children = [ProgressBar.Indeterminate(width: float.NaN)],
                            }),
                        ],
                    },
                    Flow.Show(() => p.Switch.Value.Phase == FacetPhase.Failed, new BoxEl
                    {
                        Key = H.FailureKey, Direction = 1, MinWidth = 0f,
                        Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true), Transition = s_fade,
                        Children =
                        [
                            InfoBar.Create(InfoBarSeverity.Error,
                                Strings.Home.CouldntLoad(failedLabel), Loc.Get(Strings.Home.CheckConnection),
                                onClose: p.Dismiss,
                                actionButton: Button.Create(Loc.Get(Strings.Common.Retry), () => p.Select(failedTarget), ButtonAppearance.Standard)),
                        ],
                    }),
                ],
            };
        }
    }

    static string? LabelOf(FacetWord[] words, string id)
    {
        foreach (var w in words)
        {
            if (w.Id == id) return w.Label;
            if (w.HasSub && w.SubId == id) return w.SubLabel;
        }
        return null;
    }
}
