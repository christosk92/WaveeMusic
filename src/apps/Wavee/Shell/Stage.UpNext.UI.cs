// ── Shell/Stage.UpNext.UI.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Up next column's DRAWER: the docked queue beside the lyrics collapses off the stage's right edge and comes back, by one tab
//
// Role: UI
// Owner: K
//
// The wide stages dock an Up next column beside the lyrics (`Stage.Layout.UpNextShown`, each theme's `UpNext`). 0.3.8 gave it no way to
// go: the owner's 0.3.9 report ("queue cannot be collapsed"). ONE control does both verbs, attached to the column's left edge: a pull tab
// with a chevron that points where the column will go (›: away to the edge, ‹: back in), floating a gap off its edge. Collapsed, the column slides fully past the
// stage's right edge and only the tab stays, at the edge, so the column is never lost and the state reads at rest.
//
//   • The drawer is ONE box (tab + column) whose left margin changes: the slide is a bounds transition on that box (the gallery's yield
//     idiom), so nothing inside relayouts per frame and the column's rows keep their layout while it travels.
//   • The lyrics pane STAYS PUT: its width is the layout's (`LyricsPaneW`), which still budgets the column, so a collapse never rewraps a
//     line under the reader. The stage root clips, so the travelling column simply leaves through the edge.
//   • Collapsed, the column takes no clicks (it is off the stage, and HitTestVisible guards the frame it is in flight).
//   • The open state is a stage preference (`Prefs.Stage.UpNextOpen`, `StageCtx.UpNextOpen`): the stage reopens as it was left.
//   • The theme draws the column and the tab's plate (its acrylic or flat fill, stroke and corners); this places, sizes and wires them.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The drawer tab: its width, height, its drop from the column's top edge (beside the column's title), its glyph size and the
    /// gap it keeps from the column (a frosted plate never touches another: the compositor drops one of two touching blurred plates).</summary>
    public const float UpNextTabW = 32f, UpNextTabH = 56f, UpNextTabTop = 12f, UpNextTabGlyph = 16f, UpNextTabGap = 4f;

    /// <summary>The Up next column as a drawer at (<paramref name="x"/>, <paramref name="y"/>), <paramref name="w"/> × <paramref name="h"/>:
    /// <paramref name="column"/> is the theme's framed column (sized to w × h, no margin), <paramref name="tabPlate"/> the theme's look for the
    /// tab (fill or acrylic, stroke; this sets its size, corners, content and click), <paramref name="slideMs"/> the theme's side-column
    /// travel (its gallery yield). Place it as a full-bleed child of the stage root's ZStack.</summary>
    public static Element UpNextDrawer(StageCtx stage, float x, float y, float w, float h, Element column, BoxEl tabPlate, float slideMs)
        => Embed.Comp(new UpNextDrawerProps(stage, x, y, w, h, column, tabPlate, slideMs), static () => new UpNextDrawerView());

    internal sealed record UpNextDrawerProps(StageCtx Stage, float X, float Y, float W, float H, Element Column, BoxEl TabPlate, float SlideMs);

    sealed class UpNextDrawerView : Component
    {
        LayoutTransition? _slide;
        float _slideMs = -1f;

        public override Element Render()
        {
            var p = UseProps<UpNextDrawerProps>();
            var sc = p.Stage;
            bool open = sc.UpNextOpen.Value;
            float stageW = sc.Layout.Value.W;
            if (_slide is null || _slideMs != p.SlideMs)
            {
                _slideMs = p.SlideMs;
                _slide = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Tween(p.SlideMs, Easing.FluentDecelerate), SizeMode.Relayout);
            }
            // open: the tab floats a gap off the column's left edge; collapsed: the column is past the stage's right edge and the tab keeps
            // that gap from the edge
            float left = open ? p.X - UpNextTabW - UpNextTabGap : stageW - UpNextTabW - UpNextTabGap;
            float r = UpNextTabW * 0.5f;
            var tab = ToolTip.Wrap(p.TabPlate with
            {
                Key = "stage:upnext:tab", Width = UpNextTabW, Height = UpNextTabH, Shrink = 0f, Margin = new Edges4(0f, UpNextTabTop, UpNextTabGap, 0f),
                Corners = CornerRadius4.All(r), ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:upnext:toggle", Grow = 1f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Fill = ColorF.Transparent, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = sc.ToggleUpNext,
                        Children = [new TextEl(open ? Icons.ChevronRight : Icons.ChevronLeft) { Size = UpNextTabGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }],
                    },
                ],
            }, Loc.Get(open ? Strings.Stage.UpNextHide : Strings.Stage.UpNextShow));
            return Layer with
            {
                Key = "stage:upnext:place",
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:upnext:drawer", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 0, AlignItems = FlexAlign.Start,
                        Width = UpNextTabW + UpNextTabGap + p.W, Height = p.H, Margin = new Edges4(left, p.Y, 0f, 0f), Layout = _slide, HitTestPassThrough = true,
                        Children =
                        [
                            tab,
                            new BoxEl
                            {
                                Key = "stage:upnext:column", Width = p.W, Height = p.H, Shrink = 0f, Direction = 1, HitTestVisible = open,
                                Children = [p.Column],
                            },
                        ],
                    },
                ],
            };
        }
    }
}
