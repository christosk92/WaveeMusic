// ── Themes/Classic/Structure/Views/Detail/ClassicDetailFrame.cs — Classic's detail frame (themes plan 6.1, T9a) ─────────────────────────────────
//
// From `Entities\Detail.UI.cs` `FrameHost.Render` (the composition half): the scaffold of every album / playlist / Liked / show / episode page.
//
//   · the VERTICAL arm: [tint, tone, page] where the page is the notice strip over the content column (a show's fixed header above its episodes,
//     or the table that hosts the hero), and a TRACK page's vertical arm is ALWAYS wrapped in the insights sheet, open or not and facts or not, so
//     the page's tree shape never changes underneath the list (the facts arriving must not remount the table and lose its scroll position);
//   · the TWO-COLUMN arm: the centred row (MaxWidth `R.Space.PageMaxW`) of [rail | content] with the grip in a seam layer above it, and the
//     collapsed 96-DIP identity strip in the rail's place; a TRACK page's content column carries the insights sheet over its list (I1);
//   · the HEAD arm of a page with no rail (Liked Songs, tuned Classic D16): the page head (`ClassicDetailHead.cs`) over the content column, in
//     the same centred measure, the list inset so its plates start 8 before the head's words.
//
// WHAT THE HOST DECIDED (core, `Detail.FrameHost`): the page mode and both anti-flicker fail-safes, the Hero page-layout override, which rail cell
// and its live width and collapsed flag, the page-aware cap, the rung that picks the title cap and the description clamp, the accent, the tint and
// tone claim leaves, the notice, the table or the episodes, the drop target and the insights toggle with its facts latch. The view arranges what it
// is handed and mounts the rail, the strip, the show header and the sheet through their own keys.
//
// IDENTICAL TREE. Every key, flex property and the order of the children are the host's old tree; the content column keeps its key across a
// collapse so the table reconciles in place and keeps its scroll. The grip lives in the seam layer in BOTH arms: a drag collapses (and re-opens)
// mid-gesture, and a grip that changed parent would remount and drop the pointer capture.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailFrame
{
    // ══ 1. THE FRAME ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.DetailFrame"/>.</summary>
    public static Element Template(DetailFrameProps p, in ViewContext ctx)
    {
        // The content column. Its Key is the host's (`right:tracks` / `right:eps`) and survives a rail collapse. Under a page head (no rail,
        // tuned Classic D16) the list sits the row plates' inset inside the page gutter, so its plates start 8 before the head's words.
        Element list = p.Head is null ? p.Content : new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(ClassicDetailLayout.HeadListInset, 0f, ClassicDetailLayout.HeadListInset, 0f),
            Children = [p.Content],
        };
        BoxEl right = new BoxEl
        {
            Key = p.ContentKey, Grow = 1f, Shrink = 1f, MinWidth = p.ContentMinWidth, MinHeight = 0f, Direction = 1,
            Children = [list],
        };

        // The page PROVIDES its accent: the heart and every other ambient consumer read it through the context.
        IReadSignal<Design.PageAccent>? accent = p.PageAccent;
        if (p.Vertical)
        {
            // ── the vertical / hero system ──
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
            // A TRACK page's vertical arm ALWAYS composes the overlay, open or not and facts or not (see the header).
            Element verticalRoot = p.VerticalTracks && p.Sheet is { } sheet
                ? ctx.View(ViewKeys.DetailInsights, sheet with { Page = verticalPage })
                : verticalPage;
            return Ctx.Provide(Design.AccentCtx.Slot, accent, new BoxEl
            {
                ZStack = true, Grow = 1f, OnBoundsChanged = p.OnMeasured, ClipToBounds = true,
                Children = [p.Tint, p.Tone, verticalRoot],
            });
        }

        // ── the centred two-column row [rail | grip | right] ──
        // A TRACK page lays the insights sheet over its list column, below the list bar (tuned Classic I1, board C-Liked): composed open or
        // not, so the list never remounts; the layer takes hits only while the sheet is open (its light-dismiss closes it on a list click),
        // so a closed sheet leaves every row and the bar reachable.
        if (p.Sheet is { } listSheet)
        {
            var open = listSheet.Toggle.Open;
            right = right with
            {
                ZStack = true,
                Children =
                [
                    list,
                    new BoxEl
                    {
                        Key = "insights:layer", Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                        Padding = new Edges4(0f, ClassicDetailLayout.SheetListTop, 0f, 0f),
                        HitTestVisible = Prop.Of(() => open.Value),
                        Children = [ctx.View(ViewKeys.DetailInsights, listSheet with { Page = new BoxEl { Grow = 1f, HitTestVisible = false } })],
                    },
                ],
            };
        }

        // ── a page with no rail (tuned Classic D16, board C-Liked): the head over the list, centred like the two-column row ──
        if (p.Head is { } head)
        {
            var headPage = new BoxEl
            {
                Key = "detail:head", Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                Children =
                [
                    p.Notice,
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                                MaxWidth = R.Space.PageMaxW, DropTarget = p.Drop,
                                Children = [PageHead(head), right],
                            },
                        ],
                    },
                ],
            };
            return Ctx.Provide(Design.AccentCtx.Slot, accent, new BoxEl
            {
                ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                OnBoundsChanged = p.OnMeasured, ClipToBounds = true,
                Children = [p.Tint, p.Tone, headPage],
            });
        }

        var tc = p.TwoColumn!;
        Element[] rowKids;
        Element seam = SeamOff;
        if (tc.Collapsed)
        {
            // `right` keeps its Key across the collapse, so the table reconciles in place and keeps its scroll. The collapsed grip keeps its
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
                // CLIPS (bug B). `rail:cover` takes an EXPLICIT square of `RailCoverEdge(railW)` and, like every flex item, defaults to
                // `Shrink = 0` — so while a two-column split is still settling, a `railW` that has not caught up mints a cover wider than
                // the column it sits in, and with nothing bounding it the square paints straight over the table's `#` lane and first
                // rows. This wrapper is the one box that always knows the column's REAL width, so it is the right place to contain it.
                //
                // Deliberately NOT `Shrink = 1` on the cover itself: the rail is a COLUMN, so flex shrink there works on the VERTICAL axis
                // and would squash the cover out of square on a short rail — a worse bug than the one being fixed. A clip cannot change
                // layout, only what escapes it.
                ClipToBounds = true,
                MinHeight = 0f, Shrink = 0f, Width = tc.RailWidth,
                Opacity = tc.RailOpacity,
                Children = [ctx.View(ViewKeys.DetailRail, tc.Rail!)],
            };
            // The expanded grip takes NO width in the row: it floats over the seam (SeamOverlay), so the gap between the rail and the list
            // is the rail's own right pad plus the rows' inset (8 + 8), the same as a fixed rail.
            rowKids = [railFaded, right];
            if (tc.Resizable) seam = SeamOverlay(tc.RailWidth, Grip(tc, collapsedNow: false));
        }

        var row = new BoxEl
        {
            // flex:1 1 0 — the row's width is the AVAILABLE region, so the right column yields to a tighter tier instead of overflowing the
            // clipped content card. A z-stack of the [rail | right] flow and the seam layer above it; both arms keep this shape, so `right`
            // reconciles in place across a collapse.
            ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
            MaxWidth = R.Space.PageMaxW,
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
            Children = [p.Tint, p.Tone, twoColumnPage],
        });
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
