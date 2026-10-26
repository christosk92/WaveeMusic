// ── Themes/Classic/Structure/Views/Pages/ClassicRecents.cs — Classic's `Pages.Recents` (themes plan 6.1, T10c2) ──────────────────────────────────
//
// From `Entities\Recents.Page.cs` (`PageView.Render`, `Hero`, `Content`, `PendingContent`, `PivotTabs`): the page's skeleton. The grouped list, the rows, the drawer,
// the rail and the calendar are `ClassicRecentsList.cs`, `ClassicRecentsRows.cs` and `ClassicRecentsCalendar.cs`.
//
//   Pages.Recents
//   └─ Ctx.Provide(Design.AccentCtx = the page accent)
//      └─ Row (focusable, Escape zooms back in)
//         ├─ "recents:column" (capped at the record's ListMax: C-Recents-Huge's 1100 list)
//         │    ├─ the masthead: "Recents" + the Overview button (the aside's toggle where it docks), then the summary line (the two lines arrive 45 ms apart)
//         │    ├─ the pivot strip (keyed on the shape epoch: a pivot switch re-renders the live strip, its underline enters)
//         │    ├─ the body: SkelRegionEl  (the FLIP on a pivot switch; reduced motion is a VALUE: no transition at all)
//         │    │    ├─ Content  the empty vacancy, or the SemanticSurface (list + rail  ⇄  calendar)
//         │    │    └─ Shimmer  the real day headers and the row's real geometry over the pending seed rows
//         │    ├─ the accent binder leaf (core)
//         │    └─ the owner demand leaf (core)
//         └─ "recents:overview-aside" (width 0 below Huge; C-Recents-Huge: the docked Overview, 48 right of the list, `ClassicRecentsOverview`)
//
// What the view does NOT own: the shape, the demand, the accordion's state, the sticky band's arithmetic, the accent and its wash, and every verb (`Recents.PageView`).

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
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Recents page.</summary>
internal static class ClassicRecents
{
    static LayoutTransition BodyFlip => new(TransitionChannels.Position | TransitionChannels.Opacity,
        MotionTok.ContentResize.ToDynamics(),
        Enter: new EnterExit(Dy: Spacing.S, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The docked Overview's body entering or leaving (a tier crossing, or the toggle): opacity only, 160 ms; the column eases its width beside it.</summary>
    static readonly LayoutTransition s_overview = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(ClassicRecentsRules.OverviewFadeMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ClassicRecentsRules.OverviewFadeMs, Easing.SmoothOut));

    /// <summary>The region's delegates, built once per mounted page: a fresh delegate per render would re-arm the skeleton region. And the docked Overview's
    /// toggle (the masthead's button where the record docks the aside): shown until the user hides it, per mounted page.</summary>
    sealed class Cache
    {
        public Func<Element>? Content, Shimmer, FailedPanel;
        public readonly Signal<bool> OverviewShown = new(true);
    }

    /// <summary>The registered template of <see cref="ViewKeys.RecentsPage"/>.</summary>
    public static Element Template(RecentsPageProps p, in ViewContext ctx)
    {
        var host = p.Host;
        var cell = ctx.Hooks.UseRef<Cache?>(null);
        var cache = cell.Value ??= new Cache();
        float gutter = ClassicTiers.PageOf(ctx.Tier()).Gutter;   // the one page gutter (F3)
        var layout = ClassicRecentsTiers.Of(ctx.Tier());          // the content tier's record (Tiers.Recents.cs)
        cache.Content ??= () => Content(host);
        cache.Shimmer ??= () => PendingContent(host);
        var retry = p.Retry;
        cache.FailedPanel ??= () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Page, onAction: retry);

        Element body = new SkelRegionEl(p.Pending, p.Failed, cache.Content, cache.Shimmer, cache.FailedPanel,
            SkelReveal.None, SkeletonStyle.Default, null, SmoothResize: true);
        // C-Recents-Huge: the Overview docks beside the list while the toggle shows it (the record's; width 0 elsewhere, so a crossing eases it).
        bool asideOn = layout.OverviewDocked && cache.OverviewShown.Value;
        var kids = new Element[]
        {
            Hero(p, gutter, layout, cache.OverviewShown),
            new BoxEl
            {
                Padding = new Edges4(gutter, 0f, gutter, Spacing.S),
                // Keyed on the SHAPE, not the token: a pivot switch re-renders the live strip (its underline enters). NOT on the tier: the record is a
                // live prop, so a crossing re-sizes the SAME strip (the selected tab's underline stays, the strip's scroll offset stays).
                Children = [Embed.Comp(layout, () => new PivotTabs(host)) with { Key = "recents-pivots:" + FormatCache.Int(p.ShapeEpoch) }],
            },
            new BoxEl
            {
                Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
                // No dock reserve (§9.1 #10): the shell clips above the player bar; Spacing.L is breathing room.
                Padding = new Edges4(gutter, 0f, layout.FlushRail ? 0f : gutter, Spacing.L),
                // The FLIP on a pivot switch. Reduced motion is a VALUE: no transition at all.
                Layout = Design.Reduced ? null : BodyFlip,
                Children = [body],
            },
            p.AccentBinder,
            p.OwnerDemand,
        };
        // The column holds the masthead, the pivots and the list: capped at the record's list width (plus the gutters) where the Overview docks beside it.
        Element column = new BoxEl
        {
            Key = "recents:column", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            MaxWidth = float.IsFinite(layout.ListMax) ? layout.ListMax + 2f * gutter : float.NaN, Animate = Design.Reveal.Resize,
            Children = kids,
        };
        Element page = new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch, Focusable = true,
            OnKeyDown = p.OnKeyDown, Children = [column, OverviewAside(host, layout, gutter, asideOn, p.ShapeEpoch)],
        };
        // The page PROVIDES its accent: the equalizer, the # cell and the save button read "this page's accent"
        // through the context instead of knowing they sit in Recents (§0 #16).
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent, page);
    }

    /// <summary>The docked Overview (C-Recents-Huge): in the page's row at every tier, width 0 where the record docks none or the toggle hides it, so a crossing eases
    /// it and the column beside it is never re-parented. Its body stands <see cref="ClassicRecentsLayout.OverviewGap"/> right of the list (the column's gutter is part of
    /// it) and on the masthead's line; it fades in and out.</summary>
    static Element OverviewAside(IRecentsHost host, ClassicRecentsLayout layout, float gutter, bool on, int shapeEpoch)
    {
        float width = on ? layout.OverviewGap + layout.OverviewAside : 0f;
        return new BoxEl
        {
            Key = "recents:overview-aside", Direction = 1, Shrink = 0f, MinHeight = 0f, ClipToBounds = true, Animate = Design.Reveal.Resize, Width = width,
            Children = on
                ?
                [
                    new BoxEl
                    {
                        Key = "recents:overview", Animate = s_overview, Direction = 1, Grow = 1f, MinHeight = 0f, Width = width, Shrink = 0f,
                        Padding = new Edges4(layout.OverviewGap - gutter, layout.HeadTop + ClassicRecentsRules.OverviewTop, gutter, Spacing.L),
                        // Keyed on the shape like the calendar: a re-cut forgets the aside's scroll, a tier crossing never does.
                        Children = [Embed.Comp(() => new ClassicRecentsOverview(host)) with { Key = "recents-overview:" + FormatCache.Int(shapeEpoch) }],
                    },
                ]
                : [],
        };
    }

    // ── the masthead (W3, §0 #1-#2) ──────────────────────────────────────────────────────────────────────────────

    static Element Hero(RecentsPageProps p, float gutter, ClassicRecentsLayout layout, Signal<bool> overviewShown)
    {
        var title = R.Type.SurfaceDisplay(Loc.Get(Strings.Home.Recents)) with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f, Basis = 0f,
            Enter = new EnterExit(Dy: ClassicRecentsRules.EnterDy, Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
        };
        // 24 at Small and Standard (the Small control), 32 at Huge and 40 at Compact (a target): the record's. Where the Overview docks (Huge) the button is the aside's
        // toggle, pressed while it shows (C-Recents-Huge); elsewhere it zooms out to the calendar.
        var size = layout.OverviewH > ClassicRecentsRules.OverviewSmallH ? ControlSize.Medium : ControlSize.Small;
        Element overview = layout.OverviewDocked
            ? new BoxEl
            {
                Key = "recents:overview-toggle", Shrink = 0f, MinHeight = layout.OverviewH, AlignItems = FlexAlign.Center,
                Children = [ToggleButton.Create(Loc.Get(Strings.Recents.Overview), overviewShown, size: size, glyph: Icons.Calendar)],
            }
            : Button.Create(Loc.Get(Strings.Recents.Overview), p.OpenOverview, ButtonAppearance.Subtle, size, glyph: Icons.Calendar)
                with { Key = "recents:overview-button", Shrink = 0f, MinHeight = layout.OverviewH };
        Element titleRow = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, Children = [title, overview],
        };
        return new BoxEl
        {
            Direction = 1, Gap = layout.HeadGap,
            Padding = new Edges4(gutter, layout.HeadTop, gutter, layout.HeadBottom),
            // The two lines arrive 45 ms apart; reduced motion is a VALUE (0), never a branch.
            Stagger = Design.Reduced ? 0f : R.Motion.MastheadStaggerMs,
            Children = p.Summary.Length == 0
                ? [titleRow]
                :
                [
                    titleRow,
                    Caption(p.Summary) with
                    {
                        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        // A reserved measure in lieu of tabular figures: the line never reflows (§0 #2).
                        MinWidth = ClassicRecentsRules.SummaryMinWidth,
                        Enter = new EnterExit(Dy: ClassicRecentsRules.EnterDy, Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
                    },
                ],
        };
    }

    // ── the body arms ────────────────────────────────────────────────────────────────────────────────────────────

    static Element Content(IRecentsHost host)
    {
        int epoch = host.ShapeEpoch.Value;
        string? token = host.Chip.Value;
        if (host.ShapeNow.Rows.Count == 0)
            // W8: the display sentence alone — no subtitle, no glyph, no action.
            return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                Loc.Get(Strings.Sidebar.Section.EmptyRecents), "");
        return Embed.Comp(() => new ClassicRecentsSurface(host, token, epoch))
            with { Key = "recents-semantic:" + (token ?? "all") + ":" + FormatCache.Int(epoch) };
    }

    /// <summary>The shimmer SOURCE (§9.1 #12): the real day header and the row's real geometry over the pending
    /// seed — the stateful list must not mount while pending, and the deriver maps these leaves to bars.</summary>
    static Element PendingContent(IRecentsHost host)
    {
        var shape = host.PendingShape;
        var items = shape.Sections.Items;
        var kids = new Element[items.Length];
        for (int i = 0; i < items.Length; i++)
            kids[i] = items[i].Kind == RecentsFlatItemKind.DateHeader
                ? ClassicRecentsList.DayHeader(host, shape, items[i].DayIndex, overlay: false, s_noop)
                : new BoxEl
                {
                    Direction = 0, Height = RecentsLayout.RowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Width = ClassicRecentsRules.CardArt, Height = ClassicRecentsRules.CardArt, Shrink = 0f, Corners = Radii.ControlAll },
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS, Children = [R.Type.TrackTitle(""), Caption("")] },
                        new BoxEl { Direction = 0, Gap = Spacing.M, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [Caption(""), Caption("")] },
                    ],
                };
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Children = kids,
        };
    }

    static readonly Action s_noop = static () => { };

    // ══ THE PIVOTS (W3, W10, §0 #3) ══════════════════════════════════════════════════════════════════════════════

    /// <summary>Four FIXED tabs on frame one; a tab with no rows is dimmed and inert, never hidden. Only the selected tab
    /// MOUNTS its underline, so the scaleX 0 → 1 enter is a real mount; the others reserve the 2-DIP baseline. The tier's record is the LIVE
    /// prop (a crossing re-renders the same strip at the new target size).</summary>
    sealed class PivotTabs(IRecentsHost host) : Component
    {
        static MotionTokenDef UnderlineEnter => MotionTokenDef.Eased(ClassicRecentsRules.UnderlineEnterMs, Easing.SmoothOut);

        ClassicRecentsLayout _layout = ClassicRecentsTiers.For(WidthTier.Standard);

        public override Element Render()
        {
            _layout = UseProps<ClassicRecentsLayout>();
            string? selected = host.Chip.Value;
            _ = host.ShapeEpoch.Value;
            var rows = host.ShapeNow.Rows;
            Element strip = new BoxEl
            {
                Direction = 0, Gap = _layout.PivotGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    Tab(null, Loc.Get(Strings.Detail.Filter.All), true, selected is null),
                    Tab(RecentsView.PivotMusic, Loc.Get(Strings.Recents.Chip.Music), RecentsView.PivotAvailable(rows, RecentsView.PivotMusic),
                        string.Equals(selected, RecentsView.PivotMusic, StringComparison.OrdinalIgnoreCase)),
                    Tab(RecentsView.PivotPodcasts, Loc.Get(Strings.Recents.Chip.Podcasts), RecentsView.PivotAvailable(rows, RecentsView.PivotPodcasts),
                        string.Equals(selected, RecentsView.PivotPodcasts, StringComparison.OrdinalIgnoreCase)),
                    Tab(RecentsView.PivotArtists, Loc.Get(Strings.Recents.Pivot.Artists), RecentsView.PivotAvailable(rows, RecentsView.PivotArtists),
                        string.Equals(selected, RecentsView.PivotArtists, StringComparison.OrdinalIgnoreCase)),
                ],
            };
            // ONE tree at every tier, so a crossing keeps the tabs (and the selected one's underline): the strip always rides a horizontal scroller. At
            // Compact (C-Recents-Compact) the four tabs overrun a 300-wide card and the line scrolls, its end fading; elsewhere they fit and it never
            // scrolls (an auto edge fade shows only while content runs past an edge), its height the strip's own.
            // The line is pulled out by a tab's side padding (C-Recents-Compact: the tablist's -8 margin), so the first word sits on the gutter and its
            // target is never cut by the scroller's own clip; 0 elsewhere.
            return ScrollView(strip, horizontal: true) with
            {
                Grow = 1f, Basis = 0f, Height = _layout.PivotH > 0f ? _layout.PivotH : float.NaN, MinWidth = 0f, AutoEdgeFade = true, SuppressScrollBar = true,
                Margin = new Edges4(-_layout.PivotPadX, 0f, 0f, 0f),
                ScrollKey = "recents:pivots",
            };
        }

        Element Tab(string? token, string label, bool available, bool isSelected)
        {
            Element text = R.Type.PivotLabel(label) with
            {
                Color = !available ? Tok.TextDisabled : isSelected ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1,
            };
            Element underline = isSelected
                ? new BoxEl
                {
                    Key = "underline", Height = ClassicRecentsRules.UnderlineHeight, Corners = Radii.FullAll,
                    Fill = host.AccentFillProp, BrushTransitionMs = AccentTransitionMs,
                    TransformOriginX = 0f, Enter = new EnterExit(Sx: 0f, Active: true), Transition = UnderlineEnter,
                }
                : new BoxEl { Key = "baseline", Height = ClassicRecentsRules.UnderlineHeight, Fill = ColorF.Transparent };
            return new BoxEl
            {
                Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Start,
                // Compact: a 40 x 40 target (the label sits under 8 of air, the underline on the bottom edge) with the board's 8 of side padding; a short
                // word ("All") keeps the target's width, its label and underline at the start.
                MinWidth = _layout.PivotH, MinHeight = _layout.PivotH,
                Padding = new Edges4(_layout.PivotPadX, _layout.PivotH > 0f ? Spacing.S : 0f, _layout.PivotPadX, 0f),
                Role = AutomationRole.Button, Focusable = available, Cursor = available ? CursorId.Hand : CursorId.Arrow,
                IsEnabled = available,
                FocusVisualMargin = new Edges4(ClassicRecentsRules.PivotFocusMargin, ClassicRecentsRules.PivotFocusMargin,
                    ClassicRecentsRules.PivotFocusMargin, ClassicRecentsRules.PivotFocusMargin),
                OnClick = available ? () => host.SelectPivot(token) : null,
                // The underline spans the label (the pair's own width), never the target's.
                Children = [new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 0f, Children = [text, underline] }],
            };
        }
    }

    /// <summary>The brush budget every accent consumer shares — a VALUE under reduced motion (0 ⇒ an instant swap).</summary>
    internal static float AccentTransitionMs => Design.Reduced ? 0f : Design.Motion.Standard;
}
