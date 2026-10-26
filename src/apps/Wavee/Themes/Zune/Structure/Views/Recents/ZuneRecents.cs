// ── Themes/Zune/Structure/Views/Recents/ZuneRecents.cs — Zune's `Pages.Recents` (themes plan 6.3; P5 Z7) ─────────────────────────────────────
//
// Boards Z-Recents, -Compact, -Small, -Huge. There is no masthead and no pivot strip: the page's title is the band's "recents" pivot, and its four
// pivots (all · music · podcasts · artists) and the "overview" word are the band's row 2, published by the core host (`Recents.PageView`) through the
// page's sink. What this view draws is the body on the frame's gutter, 8 under the band:
//
//   Pages.Recents
//   └─ Ctx.Provide(Design.AccentCtx = the page accent)
//      └─ Row "zune-recents-page" (focusable, Escape zooms back in; capped at the page's 2400)
//         ├─ "zune-recents-column"  the body: SkelRegionEl  (the FLIP on a pivot switch; reduced motion is a VALUE: no transition at all)
//         │    ├─ Content  the empty vacancy, or the SemanticSurface (list  ⇄  calendar)
//         │    └─ Shimmer  the real day headings and the row's geometry over the pending seed rows
//         │    + the accent binder leaf and the owner demand leaf (core)
//         └─ "zune-recents-aside"   the overview (Small and up: 220 / 300 / 420, width 0 at Compact so a crossing eases it)
//
// What the view does NOT own: the shape, the demand, the accordion's state, the sticky band's arithmetic, the accent and its wash, the main scroller
// (the content host follows it for the band's collapse) and every verb (`Recents.PageView`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using Z = Wavee.Themes.Zune.ZuneRecentsRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Recents page.</summary>
internal static class ZuneRecents
{
    static LayoutTransition BodyFlip => new(TransitionChannels.Position | TransitionChannels.Opacity,
        MotionTok.ContentResize.ToDynamics(),
        Enter: new EnterExit(Dy: Spacing.S, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The overview entering or leaving (a tier crossing): opacity only; the column eases its width beside it.</summary>
    static readonly LayoutTransition s_aside = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(Z.AsideFadeMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Z.AsideFadeMs, Easing.SmoothOut));

    /// <summary>The region's delegates, built once per mounted page: a fresh delegate per render would re-arm the skeleton region. The shimmer reads
    /// the latest tier record off the cache.</summary>
    sealed class Cache
    {
        public Func<Element>? Content, Shimmer, FailedPanel;
        public ZuneRecentsLayout Layout = ZuneRecentsTiers.For(WidthTier.Standard);
    }

    static readonly Action s_noop = static () => { };

    /// <summary>The registered template of <see cref="ViewKeys.RecentsPage"/>.</summary>
    public static Element Template(RecentsPageProps p, in ViewContext ctx)
    {
        var host = p.Host;
        var cell = ctx.Hooks.UseRef<Cache?>(null);
        var cache = cell.Value ??= new Cache();
        var layout = ZuneRecentsTiers.Of(ctx.Tier());   // the content tier's record (Rules\ZuneRecentsRules.cs)
        cache.Layout = layout;
        cache.Content ??= () => Content(host);
        cache.Shimmer ??= () => PendingContent(host, cache.Layout);
        var retry = p.Retry;
        cache.FailedPanel ??= () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Page, onAction: retry);

        Element body = new SkelRegionEl(p.Pending, p.Failed, cache.Content, cache.Shimmer, cache.FailedPanel,
            SkelReveal.None, SkeletonStyle.Default, null, SmoothResize: true);
        Element column = new BoxEl
        {
            Key = Z.ColumnKey, Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, Padding = new Edges4(0f, 0f, 0f, Z.PageBottom),
                    // The FLIP on a pivot switch. Reduced motion is a VALUE: no transition at all.
                    Layout = Design.Reduced ? null : BodyFlip,
                    Children = [body],
                },
                p.AccentBinder,
                p.OwnerDemand,
            ],
        };
        // The page column (A1): capped at 2400 + the gutters and centred, so the page shares the band's x at every width.
        Element page = ZuneFrameRules.PageRow(new BoxEl
        {
            Key = Z.PageKey, Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            AlignItems = FlexAlign.Stretch, Focusable = true, OnKeyDown = p.OnKeyDown,
            Padding = new Edges4(0f, H.PageTop, 0f, 0f),
            Children = [column, Aside(host, layout, p.ShapeEpoch)],
        }, layout.Gutter);
        // The page PROVIDES its accent: the equalizer, the # cell and the save button read "this page's accent" through the context.
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent, page);
    }

    /// <summary>The overview: in the page's row at every tier, width 0 where the record docks none, so a crossing eases it and the column beside it is
    /// never re-parented. Its body stands <see cref="ZuneRecentsLayout.AsideGap"/> right of the list and fades in and out.</summary>
    static Element Aside(IRecentsHost host, ZuneRecentsLayout layout, int shapeEpoch)
    {
        bool on = layout.HasAside;
        float width = on ? layout.AsideGap + layout.AsideW : 0f;
        return new BoxEl
        {
            Key = Z.AsideKey, Direction = 1, Shrink = 0f, MinHeight = 0f, ClipToBounds = true, Animate = Design.Reveal.Resize, Width = width,
            Children = on
                ?
                [
                    new BoxEl
                    {
                        Key = Z.OverviewKey, Animate = s_aside, Direction = 1, Grow = 1f, MinHeight = 0f, Width = width, Shrink = 0f,
                        Padding = new Edges4(layout.AsideGap, 0f, 0f, 0f),
                        // Keyed on the shape like the calendar: a re-cut forgets the aside's scroll, a tier crossing never does.
                        Children = [Embed.Comp(() => new ZuneRecentsOverview(host)) with { Key = "zune-recents-overview:" + FormatCache.Int(shapeEpoch) }],
                    },
                ]
                : [],
        };
    }

    // ── the body arms ───────────────────────────────────────────────────────────────────────────────────────────────

    static Element Content(IRecentsHost host)
    {
        int epoch = host.ShapeEpoch.Value;
        string? token = host.Chip.Value;
        if (host.ShapeNow.Rows.Count == 0)
            // The display sentence alone: no subtitle, no glyph, no action.
            return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                Loc.Get(Strings.Sidebar.Section.EmptyRecents), "");
        return Embed.Comp(() => new ZuneRecentsSurface(host, token, epoch))
            with { Key = "zune-recents-semantic:" + (token ?? "all") + ":" + FormatCache.Int(epoch) };
    }

    /// <summary>The shimmer SOURCE: the real day heading and the row's geometry over the pending seed. The stateful list must not mount while pending,
    /// and the deriver maps these leaves to bars.</summary>
    static Element PendingContent(IRecentsHost host, ZuneRecentsLayout layout)
    {
        var shape = host.PendingShape;
        var items = shape.Sections.Items;
        var kids = new Element[items.Length];
        for (int i = 0; i < items.Length; i++)
            kids[i] = items[i].Kind == RecentsFlatItemKind.DateHeader
                ? ZuneRecentsDays.Header(host, shape, items[i].DayIndex, layout, s_noop, overlay: true)
                : new BoxEl
                {
                    Direction = 0, Height = RecentsLayout.RowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                    Children =
                    [
                        // The kind lane and the art side by side, as the loaded row lays them (its plate pads nothing: Z.RowPadX).
                        new BoxEl
                        {
                            Direction = 0, Shrink = 0f,
                            Children =
                            [
                                new BoxEl { Width = layout.KindLane, Shrink = 0f },
                                new BoxEl { Width = Z.CardArt, Height = Z.CardArt, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover) },
                            ],
                        },
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS, Children = [R.Type.TrackTitle(""), R.Type.DenseMeta("")] },
                        R.Type.DenseMeta(""),
                    ],
                };
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Children = kids,
        };
    }
}
