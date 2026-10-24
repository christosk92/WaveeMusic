// ── Themes/Classic/Structure/Views/Detail/ClassicDetailFrame.cs — Classic's detail frame (themes plan 6.1, T9a) ─────────────────────────────────
//
// From `Entities\Detail.UI.cs` `FrameHost.Render` (the composition half): the scaffold of every album / playlist / Liked / show / episode page.
//
//   · a TRACK page (album, playlist, Liked) is ONE tree at every width (audit B9, `OneTree`): the centred row (MaxWidth the tier's page measure) of
//     [the rail's ground | the list column (the table, the insights sheet's layer over it, the Huge dock) and the aside | the grip's seam]. The table
//     hosts the hero (`ClassicDetailHero.cs`) as its item 0 at every width and the hero draws the page's FORM: the collapsing hero over the rows (the
//     narrow page), or the rail hanging beside the rows, or the collapsed strip in its place, or (a page with no rail, Liked Songs, tuned Classic D16)
//     the head over the list bar. A crossing re-arranges the same nodes; the list, its scroll, its selection and its focus never change hands;
//   · the ASIDE (contract 1.21, Classic Huge: C-Album-Huge, C-Playlist-Huge, C-PlaylistFollowed-Huge): a column right of the list behind a hairline,
//     holding the page's aside body and, on a facts page, the insights (the toggle shows them there instead of a sheet or a dock). Its slot is in the
//     row at EVERY tier (width 0 where none docks), so a crossing never re-parents the list: the slot eases its width open and closed and the list
//     yields to it, the column inside keeping its full width while the slot reveals it;
//   · a SHOW / EPISODE page (no table) keeps its two arms: the vertical arm's fixed show header over its episodes, or the two-column row [rail |
//     episodes] with the grip in a seam layer above it and the collapsed 96-DIP strip in the rail's place; a mode crossing cross-fades the arms.

// WHAT THE HOST DECIDED (core, `Detail.FrameHost`): the page mode and both anti-flicker fail-safes, the Hero page-layout override, which rail cell
// and its live width and collapsed flag, the page-aware cap, the rung that picks the title cap and the description clamp, the accent, the tint and
// tone claim leaves, the notice, the table or the episodes, the drop target and the insights toggle with its facts latch. The view arranges what it
// is handed and mounts the rail, the strip, the show header and the sheet through their own keys.
//
// The content column keeps its key across a collapse so the table reconciles in place and keeps its scroll. The grip lives in the seam layer in BOTH
// rail states: a drag collapses (and re-opens) mid-gesture, and a grip that changed parent would remount and drop the pointer capture.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailFrame
{
    // ══ 1. THE FRAME ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.DetailFrame"/>.</summary>
    public static Element Template(DetailFrameProps p, in ViewContext ctx)
    {
        float gutter = ClassicTiers.PageOf(ctx.Tier()).Gutter;   // the one page gutter (F3)
        var detailTier = ClassicDetailTiers.Of(ctx.Tier());       // the window tier's record (Tiers.Detail.cs, TC5)
        // The page PROVIDES its accent: the heart and every other ambient consumer read it through the context.
        IReadSignal<Design.PageAccent>? accent = p.PageAccent;

        // ── a TRACK page: one tree at every width (audit B9) ──
        if (p.Vertical && p.VerticalTracks) return OneTree(p, ctx, gutter, detailTier, accent);

        // ── a show / episode page (no table): the vertical arm's fixed show header over its episodes, or the two-column row [rail | episodes] ──
        // The content column. Its Key is the host's (`right:eps`) and survives a rail collapse.
        BoxEl right = new BoxEl
        {
            Key = p.ContentKey, Grow = 1f, Shrink = 1f, MinWidth = p.ContentMinWidth, MinHeight = 0f, Direction = 1,
            Children = [p.Content],
        };
        if (p.Vertical)
        {
            Element content = new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                DropTarget = p.Drop,
                // A show in the vertical arm is the fixed ShowHeader above its episodes — never the hero system.
                Children = p.ShowHeader is { } header ? [ctx.View(ViewKeys.DetailShowHeader, header), right] : [right],
            };
            Element verticalPage = new BoxEl
            {
                Key = "detail:vertical", Direction = 1, Grow = 1f, ClipToBounds = true,
                Children = [p.Notice, content],
            };
            return Ctx.Provide(Design.AccentCtx.Slot, accent, new BoxEl
            {
                ZStack = true, Grow = 1f, OnBoundsChanged = p.OnMeasured, ClipToBounds = true,
                Children = [p.Tint, p.Tone, Arm("vertical", verticalPage)],
            });
        }

        // ── the centred two-column row [rail | grip | right] ──
        var tc = p.TwoColumn!;
        Element[] rowKids;
        Element seam = SeamOff;
        if (tc.Collapsed)
        {
            // `right` keeps its Key across the collapse, so the content reconciles in place and keeps its scroll. The collapsed grip keeps its
            // 20-DIP slot in the row, but the grip itself lives in the seam layer in BOTH arms.
            rowKids = [ctx.View(ViewKeys.DetailCompactRail, tc.CompactRail!),
                       new BoxEl { Key = "detail-rail-grip-slot", Width = tc.CollapsedGripWidth, Shrink = 0f, HitTestVisible = false },
                       right];
            seam = SeamOverlay(tc.CompactWidth, Grip(tc, collapsedNow: true));
        }
        else
        {
            // The fade wrapper is present in every non-collapsed mode so a breakpoint cross never changes the row's child shape at index 0.
            // PAINT-BOUND: the resist cue never re-renders the rail.
            Element railFaded = new BoxEl
            {
                Key = "detail-rail-fade",
                Direction = 0, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch,
                // CLIPS (bug B). `rail:cover` takes an EXPLICIT square of `RailCoverEdge(railW, rail)` and, like every flex item, defaults to
                // `Shrink = 0` — so while a two-column split is still settling, a `railW` that has not caught up mints a cover wider than
                // the column it sits in, and with nothing bounding it the square paints straight over the content's first lanes. This wrapper is
                // the one box that always knows the column's REAL width, so it is the right place to contain it.
                //
                // Deliberately NOT `Shrink = 1` on the cover itself: the rail is a COLUMN, so flex shrink there works on the VERTICAL axis
                // and would squash the cover out of square on a short rail — a worse bug than the one being fixed. A clip cannot change
                // layout, only what escapes it.
                ClipToBounds = true,
                MinHeight = 0f, Shrink = 0f, Width = tc.RailWidth,
                Opacity = tc.RailOpacity,
                Children = [ctx.View(ViewKeys.DetailRail, tc.Rail!)],
            };
            // The expanded grip takes NO width in the row: it floats over the seam (SeamOverlay), so the gap between the rail and the content
            // is the rail's own right pad plus the content's inset, the same as a fixed rail.
            rowKids = [railFaded, right];
            if (tc.Resizable) seam = SeamOverlay(tc.RailWidth, Grip(tc, collapsedNow: false));
        }

        var row = new BoxEl
        {
            // flex:1 1 0 — the row's width is the AVAILABLE region, so the right column yields to a tighter tier instead of overflowing the
            // clipped content card. A z-stack of the [rail | right] flow and the seam layer above it; both arms keep this shape, so `right`
            // reconciles in place across a collapse.
            ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
            MaxWidth = detailTier.PageMaxWidth,
            DropTarget = p.Drop,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Stretch, MinWidth = 0f, MinHeight = 0f, Children = rowKids },
                seam,
            ],
        };
        var twoColumnPage = new BoxEl
        {
            Key = "detail:two-column",
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                p.Notice,
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
                    Children = [row],
                },
            ],
        };
        return Ctx.Provide(Design.AccentCtx.Slot, accent, new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            OnBoundsChanged = p.OnMeasured, ClipToBounds = true,
            Children = [p.Tint, p.Tone, Arm("two-column", twoColumnPage)],
        });
    }

    // ══ 1a. THE ONE-TREE TRACK PAGE (audit B9) ════════════════════════════════════════════════════════════════════════════

    /// <summary>A track page at every width: ONE frame shape, the centred row (MaxWidth the tier's page measure) of [the rail's ground | the list column
    /// (the table, the insights sheet's layer over it, the Huge dock) and the aside | the grip's seam]. The table hosts the hero as its item 0 at every
    /// width and the hero draws the form (<see cref="DetailFrameProps.Form"/>): the collapsing hero over the rows, or the rail hanging beside them (the
    /// table insets the rows by the rail's width, <see cref="ListInsets"/>), so a crossing re-arranges the same nodes instead of cross-fading two arms.
    /// The rail's ground (the layer fill) is laid here, under the table, so it never blinks with the rail's loading state and eases with the rows' inset;
    /// the grip floats over the seam above the list in both rail states (a drag collapses and re-opens mid-gesture, and a grip that changed parent would
    /// remount and drop the pointer capture). The sheet's layer stands over the list column (beside the rail, under the list bar at the columns form; over
    /// the whole page at the hero form), composed open or not, so the list never remounts; the Huge aside docks beside the list, the list keeping its 8
    /// inset before the hairline.</summary>
    static Element OneTree(DetailFrameProps p, in ViewContext ctx, float gutter, ClassicDetailTier detailTier, IReadSignal<Design.PageAccent>? accent)
    {
        var swapIn = ClassicTierMotion.SwapEnter(ClassicTierMotion.Rendered(ctx.Hooks));
        bool columns = p.Form == DetailForm.Columns;
        var tc = columns ? p.TwoColumn : null;
        bool collapsed = tc is { Collapsed: true };
        bool head = columns && tc is null && p.Head is not null;
        float lead = tc is not null ? RailLead(tc) : head ? ClassicDetailLayout.HeadListInset(gutter) : 0f;

        // ── the insights: in the aside where one docks (the toggle shows them there), docked beside the list on a page with no rail at Huge (C-Liked-Huge),
        //    else the sheet over the list column ──
        bool inAside = p.Aside is not null;
        bool docked = head && !inAside && detailTier.InsightsDocked;
        Element layer;
        if (p.Sheet is { } sheet && !inAside && !docked)
        {
            var open = sheet.Toggle.Open;
            layer = new BoxEl
            {
                Key = "insights:layer", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                // Under the list bar at the columns form (and under a head page's head), so the bar's Insights button stays reachable while it is open.
                Padding = new Edges4(0f, columns ? (head ? HeadHeight : 0f) + ClassicDetailLayout.SheetListTop : 0f, 0f, 0f),
                HitTestVisible = Prop.Of(() => open.Value),
                Children = [ctx.View(ViewKeys.DetailInsights, sheet with { Page = new BoxEl { Grow = 1f, HitTestVisible = false } })],
            };
        }
        else
            layer = new BoxEl { Key = "insights:layer", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, HitTestVisible = false };
        Element host = new BoxEl
        {
            Key = "insights:host", ZStack = true, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                p.Content,
                new BoxEl
                {
                    Key = "insights:layer-row", Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
                    Children =
                    [
                        new BoxEl { Key = "insights:lead", Width = lead, Shrink = 0f, HitTestVisible = false, Animate = ClassicTierMotion.SizeReflow },
                        layer,
                    ],
                },
            ],
        };
        Element right = new BoxEl
        {
            // The content column keeps the host's key (`right:tracks`) in every form; beside a docked aside the rows keep the board's 8 before the hairline
            // (C-Album-Huge).
            Key = p.ContentKey, Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = p.ContentMinWidth, MinHeight = 0f,
            Padding = p.Aside is not null && detailTier.Aside is { } g ? new Edges4(0f, 0f, g.ListInset, 0f) : default,
            Children =
            [
                host,
                docked && p.Sheet is { } dockSheet
                    ? ctx.View(ViewKeys.DetailInsights, dockSheet with { Docked = true }) with { Key = "insights:dock-slot" }
                    : new BoxEl { Key = "insights:dock-slot", Width = 0f, Shrink = 0f },
            ],
        };

        // ── the rail's ground: the layer fill under the rail (or the collapsed strip), 0 wide at the hero form, easing with the rows' inset ──
        Prop<float> groundOpacity = tc is not null ? tc.RailOpacity : 1f;
        Element ground = new BoxEl
        {
            Key = "detail:rail-ground", Direction = 0, MinWidth = 0f, MinHeight = 0f, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Key = "detail:rail-fill", Width = tc is null ? 0f : collapsed ? tc.CompactWidth : tc.RailWidth, Shrink = 0f,
                    Fill = Tok.FillLayerDefault, Opacity = groundOpacity, Animate = ClassicTierMotion.SizeReflow,
                },
            ],
        };
        Element seam = tc is not null && (collapsed || tc.Resizable)
            ? SeamOverlay(collapsed ? tc.CompactWidth : tc.RailWidth, Grip(tc, collapsedNow: collapsed))
            : SeamOff;

        var row = new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
            MaxWidth = detailTier.PageMaxWidth,
            DropTarget = p.Drop,
            Children =
            [
                ground,
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Stretch, MinWidth = 0f, MinHeight = 0f, Children = [right, AsideSlot(p, detailTier, swapIn)] },
                seam,
            ],
        };
        var page = new BoxEl
        {
            Key = "detail:page",
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                p.Notice,
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
                    Children = [row],
                },
            ],
        };
        return Ctx.Provide(Design.AccentCtx.Slot, accent, new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            OnBoundsChanged = p.OnMeasured, ClipToBounds = true,
            Children = [p.Tint, p.Tone, Arm("page", page)],
        });
    }

    /// <summary>One arm of the frame under its own key: a show page's mode crossing (the vertical arm ↔ the two-column arm, committed with the window
    /// tier) swaps the key, and the reconciler's keyed Exit / Enter cross-fades the two arms in the frame's z-stack instead of snapping one for the other.
    /// A track page is one arm at every width (<see cref="OneTree"/>).</summary>
    static Element Arm(string arm, Element root) => new BoxEl
    {
        Key = "detail:arm:" + arm, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
        Enter = ClassicDetailLayout.ArmFade, Exit = ClassicDetailLayout.ArmFade, Transition = ClassicDetailLayout.ArmFadeMotion,
        Children = [root],
    };

    // ══ 1b. THE ASIDE (contract 1.21) ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The slot where no aside docks: a bound 0 (<see cref="AsideSlot"/> keeps the channel's shape in every form).</summary>
    static readonly Prop<float> NoSlot = Prop.Of(static () => 0f);

    /// <summary>The aside's slot, the row's last child at every tier: width 0 where no aside docks, else the hairline (24 over it, 16 each side) and the
    /// column (C-Album-Huge 520 / -Huge4K 640, C-Playlist-Huge 440, C-PlaylistFollowed-Huge 400) with its own scroller. An aside that holds only the
    /// insights follows their toggle (0 closed), the slot easing its width with the insights dock's dynamics, so the list yields instead of jumping.
    /// <para>The slot's Width is BOUND in every form (none, the full slot, the toggle's): the slot is one reused node across the forms, and the engine
    /// wires a bind at mount only, so a static-to-bound flip keeps the mounted form (FluentGpu `Reconciler.Rewire.cs`). A playlist whose facts landed
    /// after its first Huge frame kept the "none" slot's static 0 with the insights column inside it and its toggle open.</para></summary>
    static Element AsideSlot(DetailFrameProps p, ClassicDetailTier tier, EnterExit? swapIn = null)
    {
        if (p.Aside is not { } aside || tier.Aside is not { } g)
            return new BoxEl { Key = "detail:aside", Width = NoSlot, Shrink = 0f, MinHeight = 0f, Animate = ClassicDetailLayout.DockReveal };
        bool body = aside.Body is not null;
        var insights = p.Sheet is { Facts: not null } sheet ? sheet : null;
        float columnW = g.WidthFor(aside.Kind, body);
        float slotW = g.SlotWidthFor(aside.Kind, body);
        var pad = g.PadFor(aside.Kind);
        float content = g.ContentWidthFor(aside.Kind, body);
        var kids = new List<Element>(2);
        if (aside.Body is { } b) kids.Add(b);
        if (insights is { } i) kids.Add(ClassicInsights.AsideSection(i.Toggle, i.Facts!, content, g.InsightsGap));
        Prop<float> width = Prop.Of(() => slotW);
        if (!body && insights is { } only)
        {
            var open = only.Toggle.Open;
            width = Prop.Of(() => open.Value ? slotW : 0f);
        }
        return new BoxEl
        {
            Key = "detail:aside", Direction = 0, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
            Width = width, Animate = ClassicDetailLayout.DockReveal,
            Children =
            [
                // What docks here (About this release, "More by") stands in the rail and the band under the list below Huge: two places, so a crossing
                // cross-fades the column on the declared swap while the slot eases its width (audit B9).
                new BoxEl
                {
                    Key = "aside:swap", Direction = 0, Shrink = 0f, MinHeight = 0f,
                    Enter = swapIn, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
                    Children =
                    [
                        new BoxEl { Key = "aside:hairline", Width = 1f, Shrink = 0f, Margin = g.HairlineMargin, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                        Ui.ScrollView(new BoxEl
                        {
                            Direction = 1, Width = columnW, Gap = aside.Kind == DetailKind.Album ? g.AlbumGap : g.PlaylistGap, Padding = pad,
                            Children = kids.ToArray(),
                        }) with { Key = "aside:scroll:" + aside.RouteKey, Width = columnW, Shrink = 0f, MinHeight = 0f },
                    ],
                },
            ],
        };
    }

    // ══ 2. THE GRIP AND ITS SEAM ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The grip's strip around the host's splitter. Width writes are direct during the drag (bounded by the page-aware Max the host
    /// gave the splitter, so a drag can never squeeze the content column out); RELEASE commits width + collapsed to THIS scope's pair.</summary>
    static Element Grip(DetailTwoColumnProps tc, bool collapsedNow) => new BoxEl
    {
        Key = "detail-rail-grip-strip",
        Width = collapsedNow ? tc.CollapsedGripWidth : Splitter.StripW,
        Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children = tc.Grip is { } grip ? [grip] : [],
    };

    /// <summary>The grip's layer, above the row: a pass-through row whose spacer puts the strip at the rail's edge. Expanded, the 16-DIP strip takes
    /// no row width and covers the list's empty row inset (and the rows' first 8 DIP) instead of widening the gap; collapsed, it sits over its own
    /// 20-DIP slot. It starts AT the edge, never left of it: the rail's own scrollbar owns the rail's last 12 DIP.</summary>
    static Element SeamOverlay(float railW, Element grip) => new BoxEl
    {
        Key = "detail-rail-seam", Direction = 0, AlignItems = FlexAlign.Stretch, HitTestPassThrough = true,
        Children = [new BoxEl { Key = "detail-rail-seam-spacer", Width = railW, Shrink = 0f, HitTestVisible = false }, grip],
    };

    static readonly Element SeamOff = new BoxEl { Key = "detail-rail-seam", HitTestVisible = false };
}
