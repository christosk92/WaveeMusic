// ── Themes/WinUi/Structure/Views/Stage/WinUiStageGallery.cs — WinUI's (from Classic's) `Stage.Gallery` (themes plan 6.1, T7a1) ─────────
//
// From `Visualizer.Gallery.UI.cs` and `Stage.UI.cs`'s GalleryHost: the visualizer gallery pane, an acrylic card with a title row
// and a SCROLLER of the grouped tiles and the settings rows (sensitivity, lyrics over the visualizer, calm motion, change with the
// music, sync offset). The root places it (a right-docked column, or Portrait's bottom sheet) and gates it on the layout's room
// and the stored flag; the pane is NOT gated on the idle chrome, because the face beside it is inset for it while it is open.
//
// LIVE ONLY WHERE THE EYE IS. Nineteen live previews would cost nineteen faces of binds per tick. A tile is LIVE (bound to the
// stage slab) when it is the selected face, the one under the pointer or the keyboard cursor, or one of the selected face's
// neighbours on a strong GPU, at most six (Visualizer.Catalog.LodFor). Every other tile is a POSTER: the same face bound to the
// stage's poster slab, which nothing ticks, a still, palette-coloured rest pose, never a dead floor, zero writes. Each tile
// decides for itself in an effect and re-renders only when ITS verdict flips. A hover promotes only after the pointer RESTS on a
// tile (HoverPromoteMs): a sweep across the grid mounts nothing.
//
// The tiles bind the slab's A/B/C like the stage faces, so a cover change re-tints in place; a tile re-renders once per LANDED
// palette fade (the live `StageCtx.Palette`, for the gradient stops a face cannot bind). Ink is stage ink: plate, hover plate,
// stroke and card acrylic from StageInk, so the pane reads on both arms. The selected tile wears a 2-DIP accent outline and a
// check disc (never colour alone); the keyboard cursor an ink outline.
//
// Every preference is a SIGNAL on the context (O8): the controls take the context's own signals (seeded ONCE by the surface's
// epoch effect) and write through the stage's settings verbs (`IStageSettingsVerbs`), a write that round-trips through the stored
// value and the same effect, which SetIfChanges the clamped value back.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's visualizer gallery.</summary>
internal static class WinUiStageGallery
{
    /// <summary>The registered template of <see cref="ViewKeys.StageGallery"/>. It reads the layout (the pane's size is this layout's), so
    /// a resize re-runs it.</summary>
    public static Element Template(StageGalleryProps props, in ViewContext ctx)
    {
        var stage = props.Stage;
        var layout = stage.Layout.Value;
        return new BoxEl
        {
            OnHoverMove = _ => stage.OverControls(true), OnPointerExit = () => stage.OverControls(false),
            Children = [Embed.Comp(new WinUiGalleryPane.Props(layout.GalleryW, layout.GalleryH, props), static () => new WinUiGalleryPane())],
        };
    }
}

/// <summary>The gallery's own interaction state: the hovered tile, the keyboard cursor and whether the grid has focus (indices into
/// <see cref="Visualizer.Catalog.Shown"/>; -1 = none). One per pane; the tiles read it in their LOD effects.</summary>
internal sealed class WinUiGalleryState
{
    public readonly Signal<int> Hovered = new(-1), Cursor = new(-1);

    public readonly Signal<bool> Focused = new(false);

    /// <summary><see cref="Hovered"/> after <see cref="WinUiStageGalleryGeometry.HoverPromoteMs"/> of quiet; the pane installs its debounce at
    /// render (before any tile's effect reads it); until then the raw hover.</summary>
    public IReadSignal<int> Resting;

    public WinUiGalleryState() => Resting = Hovered;

    /// <summary>The tile the live budget follows besides the selection: the RESTING hover, else the cursor while focused.</summary>
    public int FocusIndex() { int h = Resting.Value; return h >= 0 ? h : Focused.Value ? Cursor.Value : -1; }
}

/// <summary>The acrylic pane (title row, then a SCROLLER of the grouped tiles and the settings rows).</summary>
internal sealed class WinUiGalleryPane : Component
{
    public sealed record Props(float W, float H, StageGalleryProps Gallery);

    readonly WinUiGalleryState _state = new();

    public override Element Render()
    {
        var p = UseProps<Props>();
        var ctx = p.Gallery.Stage;
        var verbs = p.Gallery.Verbs;
        var accent = ctx.Accent.Value;
        var state = _state;
        state.Resting = UseDebouncedValue((IReadSignal<int>)state.Hovered, WinUiStageGalleryGeometry.HoverPromoteMs);   // the same cell every render
        float inner = MathF.Max(WinUiStageGalleryGeometry.TileMinW, p.W - 2f * WinUiStageGalleryGeometry.PadX);
        int cols = Math.Max(2, (int)((inner + WinUiStageGalleryGeometry.TileGap) / (WinUiStageGalleryGeometry.TileMinW + WinUiStageGalleryGeometry.TileGap)));
        float tileW = MathF.Floor((inner - WinUiStageGalleryGeometry.TileGap * (cols - 1)) / cols);

        // the grouped grid: a header, then rows of `cols` tiles (each group starts a fresh row)
        var sections = new List<Element>(Visualizer.Catalog.Groups.Length * 4);
        var shown = Visualizer.Catalog.Shown;
        foreach (var g in Visualizer.Catalog.Groups)
        {
            sections.Add(new TextEl(Loc.Get(Visualizer.GroupKey(g)))
            {
                Size = WinUiStageGalleryGeometry.GroupHeaderSize, LineHeight = WinUiStageGalleryGeometry.GroupHeaderLine, Weight = 600, Color = Ink.InkSecondary,
                Margin = WinUiStageGalleryGeometry.GroupHeaderMargin(sections.Count == 0),
            });
            int start = -1, count = 0;
            for (int i = 0; i < shown.Length; i++) if (Visualizer.Catalog.GroupOf(shown[i]) == g) { if (start < 0) start = i; count++; }
            for (int r = 0; r < count; r += cols)
            {
                var row = new Element[Math.Min(cols, count - r)];
                for (int c = 0; c < row.Length; c++)
                {
                    int index = start + r + c;
                    row[c] = Embed.Comp(new WinUiGalleryTile.Props(index, tileW, WinUiStageGalleryGeometry.PreviewH, state, ctx, verbs), static () => new WinUiGalleryTile()) with { Key = "viz:" + (int)shown[index] };
                }
                sections.Add(new BoxEl { Direction = 0, Gap = WinUiStageGalleryGeometry.TileGap, Children = row });
            }
        }

        var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };   // rail + thumb ring stay the theme's (the stage follows the app theme)
        var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = WinUiStageGalleryGeometry.ToggleMinWidth, OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary, Foreground = Ink.Ink };
        Action close = () => verbs.SetGalleryOpen(false);
        return new BoxEl
        {
            Width = p.W, Height = p.H, Direction = 1, Gap = WinUiStageGalleryGeometry.PaneGap, Padding = WinUiStageGalleryGeometry.PanePadding, Corners = Radii.CardAll,
            Acrylic = Ink.Card, Shadow = Elevation.Flyout, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.GalleryTitle)) { Size = WinUiStageGalleryGeometry.TitleSize, LineHeight = WinUiStageGalleryGeometry.TitleLine, Weight = 600, FontFamily = R.Type.DisplayFace, Color = Ink.Ink },
                        ToolTip.Wrap(IconButton.Create(Icons.Cancel, close, size: ControlSize.Small), Loc.Get(Strings.Stage.ClosePanel)),
                    ],
                },
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = WinUiStageGalleryGeometry.ScrollKey,
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = WinUiStageGalleryGeometry.ContentGap,
                        Children =
                        [
                            // the grid takes the keys: arrows move the cursor (by visual row), Enter / Space pick, Home / End
                            new BoxEl
                            {
                                Direction = 1, Gap = WinUiStageGalleryGeometry.TileGap, Focusable = true,
                                OnFocusChanged = got =>
                                {
                                    if (got && state.Cursor.Peek() < 0) state.Cursor.Value = Math.Max(0, Visualizer.Catalog.IndexOf(ctx.Kind.Peek()));
                                    state.Focused.SetIfChanged(got);
                                },
                                OnKeyDown = e => OnGridKey(e, state, cols, verbs),
                                Children = sections.ToArray(),
                            },
                            new TextEl(Loc.Get(Strings.Stage.SettingsHeader)) { Size = WinUiStageGalleryGeometry.SettingsHeaderSize, LineHeight = WinUiStageGalleryGeometry.SettingsHeaderLine, Weight = 600, Color = Ink.Ink, Margin = WinUiStageGalleryGeometry.SettingsHeaderMargin },
                            SettingRow(Icons.Audio, Loc.Get(Strings.Stage.Sensitivity),
                                Slider.Create(ctx.Sensitivity, v => verbs.SetSensitivity(v), WinUiStageGalleryGeometry.SensitivityOptions, length: WinUiStageGalleryGeometry.SliderLength, thickness: WinUiStageGalleryGeometry.SliderThickness, style: sliderStyle)),
                            SettingRow(Icons.Document, Loc.Get(Strings.Stage.LyricsOverlay),
                                ToggleSwitch.Create(ctx.LyricsOverlay, on => verbs.SetLyricsOverlay(on), style: toggleStyle)),
                            SettingRow(Icons.RefineSparkle, Loc.Get(Strings.Stage.CalmMotion),
                                ToggleSwitch.Create(ctx.Calm, on => verbs.SetCalm(on), style: toggleStyle)),
                            SettingRow(Icons.MusicNote, Loc.Get(Strings.Stage.Moments),
                                ToggleSwitch.Create(ctx.Moments, on => verbs.SetMoments(on), style: toggleStyle)),
                            SettingRow(Icons.Clock, Loc.Get(Strings.Stage.SyncOffset),
                                Slider.Create(ctx.SyncOffsetMs, v => verbs.SetSyncOffsetMs(WinUiStageGalleryGeometry.SnapOffset(v)), WinUiStageGalleryGeometry.OffsetOptions, length: WinUiStageGalleryGeometry.SliderLength, thickness: WinUiStageGalleryGeometry.SliderThickness, style: sliderStyle)),
                        ],
                    },
                },
            ],
        };
    }

    /// <summary>The grid's keys (they never reach the stage's seek/volume map while the grid has focus): left / right step,
    /// up / down move by visual row, Home / End, Enter / Space pick the cursor's face.</summary>
    static void OnGridKey(KeyEventArgs e, WinUiGalleryState state, int cols, IStageSettingsVerbs verbs)
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
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = Spacing.M, MinHeight = WinUiStageGalleryGeometry.RowMinHeight,
        Padding = WinUiStageGalleryGeometry.RowPadding, Corners = Radii.ControlAll, Fill = Ink.Plate, BorderWidth = 1f, BorderColor = Ink.Stroke,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Children =
                [
                    new TextEl(glyph) { Size = WinUiStageGalleryGeometry.RowGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
                    new TextEl(label) { Size = WinUiStageGalleryGeometry.RowLabelSize, LineHeight = WinUiStageGalleryGeometry.RowLabelLine, Color = Ink.Ink },
                ],
            },
            control,
        ],
    };
}
