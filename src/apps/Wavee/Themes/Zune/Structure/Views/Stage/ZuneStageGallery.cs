// ── Themes/Zune/Structure/Views/Stage/ZuneStageGallery.cs — Zune's `Stage.Gallery` (P5 Z4b) ────────────────────────────────────────
//
// The visualizer gallery pane. No Zune board draws it, so it is built from Zune's parts (owner Q18): a flat raised plate (no acrylic, no
// shadow, square), the title in the group voice (22/28 at 300) and every heading in the heading case, square tiles whose picked face wears
// the accent border and an accent check square (never colour alone), the keyboard cursor an ink border; then the settings rows on quiet
// plates. The behaviour is the stage's: a tile is LIVE only where the eye is (the picked face, the resting hover or the cursor, a few
// neighbours on a strong GPU: `Visualizer.Catalog.LodFor`), a POSTER elsewhere; the grid takes the arrows, Enter / Space pick; every
// preference is a context signal written through the settings verbs.

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
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's visualizer gallery.</summary>
internal static class ZuneStageGallery
{
    /// <summary>The registered template of <see cref="ViewKeys.StageGallery"/>. It reads the layout (the pane's size is the layout's).</summary>
    public static Element Template(StageGalleryProps props, in ViewContext ctx)
    {
        var tiers = ctx.Tier();   // hooks first, unconditionally
        var stage = props.Stage;
        var layout = stage.Layout.Value;
        // the docked column starts under the tier's head (taller than the allocator's 48 bar at Huge 4K and on a Compact window); the sheet
        // rises from the foot and keeps its height
        float drop = layout.Aspect == Stage.Aspect.Portrait ? 0f : ZuneStageTiers.StageOf(in tiers).Drop;
        // the drop is AIR, a spacer that eases when a crossing changes the head (W1920: 56 -> 48), so the tiles ride it instead of snapping
        // (audit B3bc): padding cannot ease
        return new BoxEl
        {
            Direction = 1,
            OnHoverMove = _ => stage.OverControls(true), OnPointerExit = () => stage.OverControls(false),
            Children =
            [
                new BoxEl { Height = drop, Shrink = 0f, HitTestVisible = false, Layout = ZuneMotion.HeightResize },
                Embed.Comp(new ZuneGalleryPane.Props(layout.GalleryW, MathF.Max(0f, layout.GalleryH - drop), props), static () => new ZuneGalleryPane()),
            ],
        };
    }
}

/// <summary>The gallery's interaction state: the hovered tile, the keyboard cursor and whether the grid has focus (indices into
/// <see cref="Visualizer.Catalog.Shown"/>; -1 = none).</summary>
internal sealed class ZuneGalleryState
{
    public readonly Signal<int> Hovered = new(-1), Cursor = new(-1);

    public readonly Signal<bool> Focused = new(false);

    /// <summary><see cref="Hovered"/> after a rest; until the pane installs its debounce, the raw hover.</summary>
    public IReadSignal<int> Resting;

    public ZuneGalleryState() => Resting = Hovered;

    /// <summary>The tile the live budget follows besides the selection: the resting hover, else the cursor while focused.</summary>
    public int FocusIndex() { int h = Resting.Value; return h >= 0 ? h : Focused.Value ? Cursor.Value : -1; }
}

/// <summary>The pane: the title row, then a scroller of the grouped tiles and the settings rows.</summary>
internal sealed class ZuneGalleryPane : Component
{
    public sealed record Props(float W, float H, StageGalleryProps Gallery);

    readonly ZuneGalleryState _state = new();

    public override Element Render()
    {
        var p = UseProps<Props>();
        var ctx = p.Gallery.Stage;
        var verbs = p.Gallery.Verbs;
        var accent = ctx.Accent.Value;
        var state = _state;
        state.Resting = UseDebouncedValue((IReadSignal<int>)state.Hovered, G.HoverPromoteMs);
        float inner = MathF.Max(G.GalleryTileMinW, p.W - 2f * G.GalleryPadX);
        int cols = Math.Max(2, (int)((inner + G.GalleryTileGap) / (G.GalleryTileMinW + G.GalleryTileGap)));
        float tileW = MathF.Floor((inner - G.GalleryTileGap * (cols - 1)) / cols);
        var heading = R.Case.For(LabelKind.Heading);

        var sections = new List<Element>(Visualizer.Catalog.Groups.Length * 4);
        var shown = Visualizer.Catalog.Shown;
        foreach (var g in Visualizer.Catalog.Groups)
        {
            sections.Add(new TextEl(Loc.Get(Visualizer.GroupKey(g)))
            {
                Size = G.GroupHeaderSize, LineHeight = G.GroupHeaderLine, Color = Ink.InkSecondary, Case = heading,
                Margin = G.GroupHeaderMargin(sections.Count == 0),
            });
            int start = -1, count = 0;
            for (int i = 0; i < shown.Length; i++) if (Visualizer.Catalog.GroupOf(shown[i]) == g) { if (start < 0) start = i; count++; }
            for (int r = 0; r < count; r += cols)
            {
                var row = new Element[Math.Min(cols, count - r)];
                for (int c = 0; c < row.Length; c++)
                {
                    int index = start + r + c;
                    row[c] = Embed.Comp(new ZuneGalleryTile.Props(index, tileW, G.GalleryPreviewH, state, ctx, verbs), static () => new ZuneGalleryTile()) with { Key = "viz:" + (int)shown[index] };
                }
                sections.Add(new BoxEl { Direction = 0, Gap = G.GalleryTileGap, Children = row });
            }
        }

        var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };
        var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = G.ToggleMinWidth, OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary, Foreground = Ink.Ink };
        Action close = () => verbs.SetGalleryOpen(false);
        return new BoxEl
        {
            Width = p.W, Height = p.H, Direction = 1, Gap = G.GalleryGap, Padding = G.GalleryPadding, Corners = R.Shape.All(ShapeRole.Overlay),
            Fill = R.Layer.FloatingChrome, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.GalleryTitle)) { Size = G.GalleryTitleSize, LineHeight = G.GalleryTitleLine, Weight = G.TitleLight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink, Case = heading },
                        ToolTip.Wrap(IconButton.Create(Icons.Cancel, close, size: ControlSize.Small), Loc.Get(Strings.Stage.ClosePanel)),
                    ],
                },
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = G.GalleryScrollKey,
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = G.GalleryContentGap,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 1, Gap = G.GalleryTileGap, Focusable = true,
                                OnFocusChanged = got =>
                                {
                                    if (got && state.Cursor.Peek() < 0) state.Cursor.Value = Math.Max(0, Visualizer.Catalog.IndexOf(ctx.Kind.Peek()));
                                    state.Focused.SetIfChanged(got);
                                },
                                OnKeyDown = e => OnGridKey(e, state, cols, verbs),
                                Children = sections.ToArray(),
                            },
                            new TextEl(Loc.Get(Strings.Stage.SettingsHeader)) { Size = G.SettingsHeaderSize, LineHeight = G.SettingsHeaderLine, Color = Ink.InkSecondary, Case = heading, Margin = G.SettingsHeaderMargin },
                            SettingRow(Icons.Audio, Loc.Get(Strings.Stage.Sensitivity),
                                Slider.Create(ctx.Sensitivity, v => verbs.SetSensitivity(v), G.SensitivityOptions, length: G.SliderLength, thickness: G.SliderThickness, style: sliderStyle)),
                            SettingRow(Icons.Document, Loc.Get(Strings.Stage.LyricsOverlay),
                                ToggleSwitch.Create(ctx.LyricsOverlay, on => verbs.SetLyricsOverlay(on), style: toggleStyle)),
                            SettingRow(Icons.RefineSparkle, Loc.Get(Strings.Stage.CalmMotion),
                                ToggleSwitch.Create(ctx.Calm, on => verbs.SetCalm(on), style: toggleStyle)),
                            SettingRow(Icons.MusicNote, Loc.Get(Strings.Stage.Moments),
                                ToggleSwitch.Create(ctx.Moments, on => verbs.SetMoments(on), style: toggleStyle)),
                            SettingRow(Icons.Clock, Loc.Get(Strings.Stage.SyncOffset),
                                Slider.Create(ctx.SyncOffsetMs, v => verbs.SetSyncOffsetMs(G.SnapOffset(v)), G.OffsetOptions, length: G.SliderLength, thickness: G.SliderThickness, style: sliderStyle)),
                        ],
                    },
                },
            ],
        };
    }

    /// <summary>The grid's keys: left / right step, up / down by visual row, Home / End, Enter / Space pick the cursor's face.</summary>
    static void OnGridKey(KeyEventArgs e, ZuneGalleryState state, int cols, IStageSettingsVerbs verbs)
    {
        if (e.Handled || e.Ctrl || e.Alt) return;
        int cur = state.Cursor.Peek();
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            if (cur >= 0 && !e.IsRepeat) verbs.PickFace(Visualizer.Catalog.Shown[cur]);
            e.Handled = true;
            return;
        }
        int next = Visualizer.GalleryNav.Move(cur < 0 ? 0 : cur, e.KeyCode, cols);
        if (next < 0) return;
        e.Handled = true;
        state.Cursor.SetIfChanged(next);
    }

    static Element SettingRow(string glyph, string label, Element control) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = Spacing.M, MinHeight = G.RowMinHeight,
        Padding = G.RowPadding, Corners = R.Shape.All(ShapeRole.Control), Fill = Ink.Plate,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Children =
                [
                    new TextEl(glyph) { Size = G.RowGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
                    new TextEl(label) { Size = G.RowLabelSize, LineHeight = G.RowLabelLine, Color = Ink.Ink, Case = R.Case.For(LabelKind.Label) },
                ],
            },
            control,
        ],
    };
}

/// <summary>One gallery tile: the preview (live or poster), the check square, the name with its pace word, the blurb. Its LOD is its own
/// effect's verdict: it re-renders only when IT flips between live and poster.</summary>
internal sealed class ZuneGalleryTile : Component
{
    public sealed record Props(int Index, float W, float PreviewH, ZuneGalleryState State, Stage.StageCtx Stage, IStageSettingsVerbs Verbs);

    readonly Signal<bool> _live = new(false);
    readonly Action<NodeHandle> _onRealized;
    NodeHandle _node;
    bool _cursorShown;

    public ZuneGalleryTile() => _onRealized = h => _node = h;

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
        // the keyboard cursor arriving here brings the tile into the scroller
        UseSignalEffect(() =>
        {
            bool cursor = state.Focused.Value && state.Cursor.Value == index;
            if (cursor == _cursorShown) return;
            _cursorShown = cursor;
            if (cursor && !_node.IsNull && Context.Scene is { } scene)
                scene.BringIntoView(_node, float.NaN, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide, G.CursorGutter);
        });
        bool live = _live.Value;
        Visualizer.UseScopeReader(Context, ctx.Slab, live && Visualizer.Catalog.UsesScope(kind));
        var pal = ctx.Palette.Value;
        var accent = ctx.Accent.Value;
        var kindSig = ctx.Kind;
        var slab = live ? ctx.Slab : ctx.PosterSlab;
        float pw = p.W - G.TilePreviewInset;
        // frozen at render by design (a theme flip re-renders the stage): plain colour locals, so the thunks read only signals
        ColorF pickBorder = accent.Fill, cursorBorder = Ink.Ink, restBorder = ColorF.Transparent, veil = Ink.Veil;
        float wash = Ink.SelectedTileWash;
        var label = R.Case.For(LabelKind.Label);
        var tile = new BoxEl
        {
            Width = p.W, Direction = 1, Gap = G.TileInnerGap, Padding = G.TilePadding, Corners = R.Shape.All(ShapeRole.Tile), Fill = Ink.Plate,
            OnRealized = _onRealized,
            HoverFill = Ink.PlateHover, BrushTransitionMs = Design.Motion.Fast, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            BorderWidth = G.TileBorderWidth,
            BorderColor = Prop.Of(() => kindSig.Value == kind ? pickBorder : state.Focused.Value && state.Cursor.Value == index ? cursorBorder : restBorder),
            OnClick = () => { state.Cursor.SetIfChanged(index); verbs.PickFace(kind); },
            OnHoverMove = _ => { if (state.Hovered.Peek() != index) state.Hovered.Value = index; },
            OnPointerExit = () => { if (state.Hovered.Peek() == index) state.Hovered.Value = -1; },
            Children =
            [
                new BoxEl
                {
                    Width = pw, Height = p.PreviewH, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true, ZStack = true, HitTestVisible = false,
                    Fill = Prop.Of(() => ColorF.Lerp(veil, slab.A.Value, wash)),
                    Children =
                    [
                        new BoxEl
                        {
                            Key = live ? "live" : "poster", Width = pw, Height = p.PreviewH, HitTestVisible = false,
                            Children = [Visualizer.Face(kind, slab, in pal, new Visualizer.FaceSpec(pw, p.PreviewH, Preview: true, CoverUrl: null, Poster: !live))],
                        },
                        new BoxEl
                        {
                            Width = G.CheckBox, Height = G.CheckBox, Fill = accent.Fill, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = G.CheckMargin,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Visible = Prop.Of(() => kindSig.Value == kind),
                            Children = [new TextEl(Icons.Check) { Size = G.CheckGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = accent.Ink }],
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Padding = G.TileLabelPadding, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = G.TileLabelGap,
                            Children =
                            [
                                new TextEl(Loc.Get(Visualizer.NameKey(kind))) { Size = G.TileNameSize, LineHeight = G.TileNameLine, Color = Ink.Ink, Case = label, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                                new TextEl(Loc.Get(Visualizer.Catalog.PaceOf(kind) == Visualizer.Pace.Lively ? Strings.Stage.VizEnergy.Lively : Strings.Stage.VizEnergy.Calm)) { Size = G.TileMetaSize, LineHeight = G.TileMetaLine, Color = Ink.InkTertiary, Case = label, Shrink = 0f },
                            ],
                        },
                        new TextEl(Prop.Of(() => Visualizer.SubtitleOf(kind, kindSig.Value, ctx.Slab.Source.Value))) { Size = G.TileMetaSize, LineHeight = G.TileMetaLine, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        };
        return ToolTip.Wrap(tile, Loc.Get(Visualizer.TipKey(kind)));
    }
}
