// ── Themes/Classic/Structure/Views/Pages/ClassicRecents.cs — Classic's `Pages.Recents` (themes plan 6.1, T10c2) ──────────────────────────────────
//
// From `Entities\Recents.Page.cs` (`PageView.Render`, `Hero`, `Content`, `PendingContent`, `PivotTabs`): the page's skeleton. The grouped list, the rows, the drawer,
// the rail and the calendar are `ClassicRecentsList.cs`, `ClassicRecentsRows.cs` and `ClassicRecentsCalendar.cs`.
//
//   Pages.Recents
//   └─ Ctx.Provide(Design.AccentCtx = the page accent)
//      └─ Column (focusable, Escape zooms back in)
//         ├─ the masthead: "Recents" + the Overview button, then the summary line (the two lines arrive 45 ms apart)
//         ├─ the pivot strip (keyed on the shape epoch: a pivot switch re-renders the live strip, its underline enters)
//         ├─ the body: SkelRegionEl  (the FLIP on a pivot switch; reduced motion is a VALUE: no transition at all)
//         │    ├─ Content  the empty vacancy, or the SemanticSurface (list + rail  ⇄  calendar)
//         │    └─ Shimmer  the real day headers and the row's real geometry over the pending seed rows
//         ├─ the accent binder leaf (core)
//         └─ the owner demand leaf (core)
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

    /// <summary>The region's delegates, built once per mounted page: a fresh delegate per render would re-arm the skeleton region.</summary>
    sealed class Cache
    {
        public Func<Element>? Content, Shimmer, FailedPanel;
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
        var kids = new Element[]
        {
            Hero(p, gutter, layout),
            new BoxEl
            {
                Padding = new Edges4(gutter, 0f, gutter, Spacing.S),
                // Keyed on the SHAPE, not the token: a pivot switch re-renders the live strip (its underline enters).
                // …and on the tier: the Compact strip is its own shape (40 tabs), a crossing remounts the strip once.
                Children = [Embed.Comp(() => new PivotTabs(host, layout)) with { Key = "recents-pivots:" + FormatCache.Int(p.ShapeEpoch) + ":" + FormatCache.Int((int)layout.Tier) }],
            },
            new BoxEl
            {
                Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
                // No dock reserve (§9.1 #10): the shell clips above the player bar; Spacing.L is breathing room.
                Padding = new Edges4(gutter, 0f, gutter, Spacing.L),
                // The FLIP on a pivot switch. Reduced motion is a VALUE: no transition at all.
                Layout = Design.Reduced ? null : BodyFlip,
                Children = [body],
            },
            p.AccentBinder,
            p.OwnerDemand,
        };
        Element page = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Focusable = true,
            OnKeyDown = p.OnKeyDown, Children = kids,
        };
        // The page PROVIDES its accent: the equalizer, the # cell and the save button read "this page's accent"
        // through the context instead of knowing they sit in Recents (§0 #16).
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent, page);
    }

    // ── the masthead (W3, §0 #1-#2) ──────────────────────────────────────────────────────────────────────────────

    static Element Hero(RecentsPageProps p, float gutter, ClassicRecentsLayout layout)
    {
        var title = R.Type.SurfaceDisplay(Loc.Get(Strings.Home.Recents)) with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f, Basis = 0f,
            Enter = new EnterExit(Dy: ClassicRecentsRules.EnterDy, Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
        };
        // 24 at Small and Standard (the Small control), 32 at Huge and 40 at Compact (a target): the record's.
        var overview = Button.Create(Loc.Get(Strings.Recents.Overview), p.OpenOverview, ButtonAppearance.Subtle,
            layout.OverviewH > ClassicRecentsRules.OverviewSmallH ? ControlSize.Medium : ControlSize.Small, glyph: Icons.Calendar) with { Shrink = 0f, MinHeight = layout.OverviewH };
        Element titleRow = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, Children = [title, overview],
        };
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS,
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
    /// MOUNTS its underline, so the scaleX 0 → 1 enter is a real mount; the others reserve the 2-DIP baseline.</summary>
    sealed class PivotTabs(IRecentsHost host, ClassicRecentsLayout layout) : Component
    {
        static MotionTokenDef UnderlineEnter => MotionTokenDef.Eased(ClassicRecentsRules.UnderlineEnterMs, Easing.SmoothOut);

        public override Element Render()
        {
            string? selected = host.Chip.Value;
            _ = host.ShapeEpoch.Value;
            var rows = host.ShapeNow.Rows;
            Element strip = new BoxEl
            {
                Direction = 0, Gap = layout.PivotGap, AlignItems = FlexAlign.Center, Shrink = 0f,
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
            // Compact (C-Recents-Compact): the four tabs are one scrolling line whose end fades, so a 300-wide page never runs the strip off its card.
            return layout.PivotH <= 0f ? strip : ScrollView(strip, horizontal: true) with
            {
                Grow = 1f, Basis = 0f, Height = layout.PivotH, MinWidth = 0f, AutoEdgeFade = true, SuppressScrollBar = true, ScrollKey = "recents:pivots",
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
                Direction = 1, Gap = Spacing.XS, Shrink = 0f,
                // Compact: a 40 target (the label sits under 8 of air, the underline on the bottom edge) with the board's 8 of side padding.
                MinHeight = layout.PivotH, Padding = new Edges4(layout.PivotPadX, layout.PivotH > 0f ? Spacing.S : 0f, layout.PivotPadX, 0f),
                Role = AutomationRole.Button, Focusable = available, Cursor = available ? CursorId.Hand : CursorId.Arrow,
                IsEnabled = available,
                FocusVisualMargin = new Edges4(ClassicRecentsRules.PivotFocusMargin, ClassicRecentsRules.PivotFocusMargin,
                    ClassicRecentsRules.PivotFocusMargin, ClassicRecentsRules.PivotFocusMargin),
                OnClick = available ? () => host.SelectPivot(token) : null,
                Children = [text, underline],
            };
        }
    }

    /// <summary>The brush budget every accent consumer shares — a VALUE under reduced motion (0 ⇒ an instant swap).</summary>
    internal static float AccentTransitionMs => Design.Reduced ? 0f : Design.Motion.Standard;
}
