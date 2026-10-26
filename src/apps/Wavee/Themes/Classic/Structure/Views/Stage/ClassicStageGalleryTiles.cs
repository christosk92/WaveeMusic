// ── Themes/Classic/Structure/Views/Stage/ClassicStageGalleryTiles.cs — one tile of Classic's visualizer gallery (T7a1) ────
//
// One gallery tile: the preview (live or poster), the check disc, the name with its pace word, the blurb (or where the active
// face's motion comes from). Its LOD is its own effect's verdict: a hover elsewhere re-runs the effect, never the render, unless
// THIS tile flips between live and poster. Moved from `Visualizer.Gallery.UI.cs`; the stage context and the verbs arrive on the
// tile's props (the template hands them down), not through a context read.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>One gallery tile.</summary>
internal sealed class ClassicGalleryTile : Component
{
    public sealed record Props(int Index, float W, float PreviewH, ClassicGalleryState State, Stage.StageCtx Stage, IStageSettingsVerbs Verbs);

    readonly Signal<bool> _live = new(false);
    readonly Action<NodeHandle> _onRealized;
    NodeHandle _node;
    bool _cursorShown;

    public ClassicGalleryTile() => _onRealized = h => _node = h;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var ctx = p.Stage;
        var verbs = p.Verbs;
        var state = p.State;
        int index = p.Index;
        var kind = Visualizer.Catalog.Shown[index];
        UseSignalEffect(() =>
        {
            int selected = Visualizer.Catalog.IndexOf(ctx.Kind.Value);
            _live.SetIfChanged(Visualizer.Catalog.LodFor(index, selected, state.FocusIndex()) == Visualizer.Lod.Preview);
        });
        // the keyboard cursor ARRIVING here brings the tile into the gallery's scroller (minimal move, a 10-DIP gutter;
        // the scene seam resolves the nearest scrolling ancestor — the pane's ScrollEl)
        UseSignalEffect(() =>
        {
            bool cursor = state.Focused.Value && state.Cursor.Value == index;
            if (cursor == _cursorShown) return;
            _cursorShown = cursor;
            if (cursor && !_node.IsNull && Context.Scene is { } scene)
                scene.BringIntoView(_node, float.NaN, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide, ClassicStageGalleryGeometry.CursorGutter);
        });
        bool live = _live.Value;
        // a LIVE Scope/Flow tile reads the stage slab's scope series: count it, so the clock fills them only then
        Visualizer.UseScopeReader(Context, ctx.Slab, live && Visualizer.Catalog.UsesScope(kind));
        var pal = ctx.Palette.Value;                       // the LIVE palette: re-renders once per landed fade (gradient stops)
        var accent = ctx.Accent.Value;
        var kindSig = ctx.Kind;
        var slab = live ? ctx.Slab : ctx.PosterSlab;
        float pw = p.W - ClassicStageGalleryGeometry.PreviewInset;
        // frozen at render BY DESIGN (a theme flip re-renders the stage): plain colour locals, so the thunks read only signals
        ColorF pickBorder = accent.Fill, cursorBorder = Ink.Ink, restBorder = Ink.Stroke, veil = Ink.Veil;
        float wash = Ink.SelectedTileWash;
        var tile = new BoxEl
        {
            Width = p.W, Direction = 1, Gap = ClassicStageGalleryGeometry.TileInnerGap, Padding = ClassicStageGalleryGeometry.TilePadding,
            Corners = ClassicStageGalleryGeometry.TileCorners, Fill = Ink.Plate,
            OnRealized = _onRealized,
            HoverFill = Ink.PlateHover, BrushTransitionMs = Design.Motion.Fast, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            BorderWidth = ClassicStageGalleryGeometry.TileBorderWidth,
            BorderColor = Prop.Of(() => kindSig.Value == kind ? pickBorder : state.Focused.Value && state.Cursor.Value == index ? cursorBorder : restBorder),
            OnClick = () => { state.Cursor.SetIfChanged(index); verbs.PickFace(kind); },
            OnHoverMove = _ => { if (state.Hovered.Peek() != index) state.Hovered.Value = index; },
            OnPointerExit = () => { if (state.Hovered.Peek() == index) state.Hovered.Value = -1; },
            Children =
            [
                new BoxEl
                {
                    Width = pw, Height = p.PreviewH, Corners = Radii.ControlAll, ClipToBounds = true, ZStack = true, HitTestVisible = false,
                    Fill = Prop.Of(() => ColorF.Lerp(veil, slab.A.Value, wash)),
                    Children =
                    [
                        // keyed by the verdict: live <-> poster remounts the face (a poster must not keep a live face's sim)
                        new BoxEl
                        {
                            Key = live ? "live" : "poster", Width = pw, Height = p.PreviewH, HitTestVisible = false,
                            Children = [Visualizer.Face(kind, slab, in pal, new Visualizer.FaceSpec(pw, p.PreviewH, Preview: true, CoverUrl: null, Poster: !live))],
                        },
                        // the check disc: TOP (AlignSelf) RIGHT (JustifySelf) — V-U9; the selection cue that is not colour alone
                        new BoxEl
                        {
                            Width = ClassicStageGalleryGeometry.CheckDisc, Height = ClassicStageGalleryGeometry.CheckDisc, Corners = ClassicStageGalleryGeometry.CheckDiscCorners,
                            Fill = accent.Fill, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = ClassicStageGalleryGeometry.CheckDiscMargin,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Visible = Prop.Of(() => kindSig.Value == kind),
                            Children = [new TextEl(Icons.Check) { Size = ClassicStageGalleryGeometry.CheckGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = accent.Ink }],
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Padding = ClassicStageGalleryGeometry.LabelPadding, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = ClassicStageGalleryGeometry.LabelGap,
                            Children =
                            [
                                new TextEl(Loc.Get(Visualizer.NameKey(kind))) { Size = ClassicStageGalleryGeometry.NameSize, LineHeight = ClassicStageGalleryGeometry.NameLine, Weight = 600, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                                new TextEl(Loc.Get(Visualizer.Catalog.PaceOf(kind) == Visualizer.Pace.Lively ? Strings.Stage.VizEnergy.Lively : Strings.Stage.VizEnergy.Calm)) { Size = ClassicStageGalleryGeometry.PaceSize, LineHeight = ClassicStageGalleryGeometry.PaceLine, Color = Ink.InkTertiary, Shrink = 0f },
                            ],
                        },
                        new TextEl(Prop.Of(() => Visualizer.SubtitleOf(kind, kindSig.Value, ctx.Slab.Source.Value))) { Size = ClassicStageGalleryGeometry.BlurbSize, LineHeight = ClassicStageGalleryGeometry.BlurbLine, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        };
        return ToolTip.Wrap(tile, Loc.Get(Visualizer.TipKey(kind)));
    }
}
