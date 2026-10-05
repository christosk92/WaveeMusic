// ── Shell/Visualizer.Gallery.UI.cs ─────────────────────────────────────────────────────────────────────────────────
// The visualizer gallery: grouped sections (Lyrics · Fluent · Classics · Zune · iTunes) of tiles with live or frozen
// previews, keyboard navigation, and the settings rows (sensitivity, lyrics over the visualizer, calm motion, change with
// the music, sync offset); the face names, blurbs, tips and group labels
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 520 lines
// Spec: viz-app-plan §4.1-§4.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// LIVE ONLY WHERE THE EYE IS. Nineteen live previews would cost nineteen faces of binds per tick. A tile is LIVE (bound
// to the stage slab) when it is the selected face, the one under the pointer or the keyboard cursor, or one of the
// selected face's neighbours on a strong GPU — at most six (Visualizer.Catalog.LodFor). Every other tile is a POSTER: the
// same face bound to the stage's poster slab, which nothing ticks — a still, palette-coloured rest pose, never a dead
// floor, zero writes. Each tile decides for itself in an effect and re-renders only when ITS verdict flips. A hover
// promotes only after the pointer RESTS on a tile (GalleryState.HoverPromoteMs): a sweep across the grid mounts nothing.
//
// The tiles bind the slab's A/B/C like the stage faces, so a cover change re-tints in place; a tile re-renders once per
// LANDED palette fade (the live `StageCtx.Palette`, for the gradient stops a face cannot bind). Ink is stage ink: plate,
// hover plate, stroke and card acrylic from Design.StageInk, so the pane reads on both arms. The selected tile wears a
// 2-DIP accent outline and a check disc (never colour alone); the keyboard cursor an ink outline.
//
// Every preference is a SIGNAL on the context (O8): the controls take the context's own signals (seeded ONCE by
// SurfaceCore's epoch effect and written by the controls while dragging — a write round-trips through Prefs.Stage →
// Epoch → the same effect, which SetIfChanges the clamped value back).

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Visualizer
{
    /// <summary>The acrylic pane (title row, then a SCROLLER of the grouped tiles and the settings rows). Mounted by
    /// Stage.UI.cs's GalleryHost with this layout's pane size (a right-docked column, or Portrait's bottom sheet).</summary>
    public static Element Gallery(Stage.StageCtx ctx, Stage.Layout layout, Action close)
        => Embed.Comp(new GalleryPane.Props(layout.GalleryW, layout.GalleryH, close), static () => new GalleryPane());

    /// <summary>The gallery's own interaction state: the hovered tile, the keyboard cursor and whether the grid has focus
    /// (indices into <see cref="Catalog.Shown"/>; −1 = none). One per pane; the tiles read it in their LOD effects.</summary>
    sealed class GalleryState
    {
        /// <summary>How long the pointer must rest on a tile before its preview goes live (a sweep promotes nothing).</summary>
        public const float HoverPromoteMs = 120f;
        public readonly Signal<int> Hovered = new(-1), Cursor = new(-1);
        public readonly Signal<bool> Focused = new(false);
        /// <summary><see cref="Hovered"/> after <see cref="HoverPromoteMs"/> of quiet — the pane installs its debounce at
        /// render (before any tile's effect reads it); until then the raw hover.</summary>
        public IReadSignal<int> Resting;
        public GalleryState() => Resting = Hovered;
        /// <summary>The tile the live budget follows besides the selection: the RESTING hover, else the cursor while focused.</summary>
        public int FocusIndex() { int h = Resting.Value; return h >= 0 ? h : Focused.Value ? Cursor.Value : -1; }
    }

    sealed class GalleryPane : Component
    {
        public sealed record Props(float W, float H, Action Close);
        readonly GalleryState _state = new();

        // the pane's metrics: padding 20 · 14 · 20 · 20, the tile gap, a tile's floor width, the preview strip
        const float PadX = 20f, TileGap = 10f, TileMinW = 180f, PreviewH = 78f;

        public override Element Render()
        {
            var p = UseProps<Props>();
            var ctx = UseContext(Stage.StageContext)!;
            var accent = ctx.Accent.Value;
            var state = _state;
            state.Resting = UseDebouncedValue((IReadSignal<int>)state.Hovered, GalleryState.HoverPromoteMs);   // the same cell every render
            float inner = MathF.Max(TileMinW, p.W - 2f * PadX);
            int cols = Math.Max(2, (int)((inner + TileGap) / (TileMinW + TileGap)));
            float tileW = MathF.Floor((inner - TileGap * (cols - 1)) / cols);

            // the grouped grid: a header, then rows of `cols` tiles (each group starts a fresh row)
            var sections = new List<Element>(Catalog.Groups.Length * 4);
            var shown = Catalog.Shown;
            foreach (var g in Catalog.Groups)
            {
                sections.Add(new TextEl(Loc.Get(GroupKey(g))) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Ink.InkSecondary, Margin = new Edges4(2f, sections.Count == 0 ? 0f : 6f, 0f, 0f) });
                int start = -1, count = 0;
                for (int i = 0; i < shown.Length; i++) if (Catalog.GroupOf(shown[i]) == g) { if (start < 0) start = i; count++; }
                for (int r = 0; r < count; r += cols)
                {
                    var row = new Element[Math.Min(cols, count - r)];
                    for (int c = 0; c < row.Length; c++)
                    {
                        int index = start + r + c;
                        row[c] = Embed.Comp(new GalleryTile.Props(index, tileW, PreviewH, state), static () => new GalleryTile()) with { Key = "viz:" + (int)shown[index] };
                    }
                    sections.Add(new BoxEl { Direction = 0, Gap = TileGap, Children = row });
                }
            }

            var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };   // rail + thumb ring stay the theme's (the stage follows the app theme)
            var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = 40f, OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary, Foreground = Ink.Ink };
            return new BoxEl
            {
                Width = p.W, Height = p.H, Direction = 1, Gap = 10f, Padding = new Edges4(PadX, 14f, PadX, 20f), Corners = Radii.CardAll,
                Acrylic = Ink.Card, Shadow = Elevation.Flyout, ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.GalleryTitle)) { Size = 20f, LineHeight = 28f, Weight = 600, FontFamily = "Segoe UI Variable Display", Color = Ink.Ink },
                            ToolTip.Wrap(IconButton.Create(Icons.Cancel, p.Close, size: ControlSize.Small), Loc.Get(Strings.Stage.ClosePanel)),
                        ],
                    },
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stagegallery",
                        Content = new BoxEl
                        {
                            Direction = 1, Gap = 10f,
                            Children =
                            [
                                // the grid takes the keys: arrows move the cursor (by visual row), Enter / Space pick, Home / End
                                new BoxEl
                                {
                                    Direction = 1, Gap = TileGap, Focusable = true,
                                    OnFocusChanged = got =>
                                    {
                                        if (got && state.Cursor.Peek() < 0) state.Cursor.Value = Math.Max(0, Catalog.IndexOf(ctx.Kind.Peek()));
                                        state.Focused.SetIfChanged(got);
                                    },
                                    OnKeyDown = e => OnGridKey(e, state, cols),
                                    Children = sections.ToArray(),
                                },
                                new TextEl(Loc.Get(Strings.Stage.SettingsHeader)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, Margin = new Edges4(0f, 6f, 0f, 0f) },
                                SettingRow(Icons.Audio, Loc.Get(Strings.Stage.Sensitivity),
                                    Slider.Create(ctx.Sensitivity, static v => Prefs.Stage.SetSensitivity(v), SensitivityOptions, length: 150f, thickness: 24f, style: sliderStyle)),
                                SettingRow(Icons.Document, Loc.Get(Strings.Stage.LyricsOverlay),
                                    ToggleSwitch.Create(ctx.LyricsOverlay, static on => Prefs.Stage.SetLyricsOverlay(on), style: toggleStyle)),
                                SettingRow(Icons.RefineSparkle, Loc.Get(Strings.Stage.CalmMotion),
                                    ToggleSwitch.Create(ctx.Calm, static on => Prefs.Stage.SetCalm(on), style: toggleStyle)),
                                SettingRow(Icons.MusicNote, Loc.Get(Strings.Stage.Moments),
                                    ToggleSwitch.Create(ctx.Moments, static on => Prefs.Stage.SetMoments(on), style: toggleStyle)),
                                SettingRow(Icons.Clock, Loc.Get(Strings.Stage.SyncOffset),
                                    Slider.Create(ctx.SyncOffsetMs, static v => { int ms = (int)MathF.Round(v / 10f) * 10; Prefs.Stage.SetSyncOffsetMs(ms); Playback.Audio.SetSpectrumOffsetMs(ms); }, OffsetOptions, length: 150f, thickness: 24f, style: sliderStyle)),
                            ],
                        },
                    },
                ],
            };
        }

        /// <summary>The grid's keys (they never reach the stage's seek/volume map while the grid has focus): ← → step,
        /// ↑ ↓ move by visual row, Home / End, Enter / Space pick the cursor's face.</summary>
        static void OnGridKey(KeyEventArgs e, GalleryState state, int cols)
        {
            if (e.Handled || e.Ctrl || e.Alt) return;
            int cur = state.Cursor.Peek();
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                if (cur >= 0 && !e.IsRepeat) PickFace(Catalog.Shown[cur]);
                e.Handled = true;
                return;
            }
            int next = GalleryNav.Move(cur < 0 ? 0 : cur, e.KeyCode, cols);
            if (next < 0) return;
            e.Handled = true;
            state.Cursor.SetIfChanged(next);
        }
    }

    /// <summary>The gallery's keyboard arithmetic over the grouped grid: the shown faces in order, each group starting a
    /// fresh row of <c>cols</c>. ↑ / ↓ keep the column across a group boundary (clamped to the shorter row).</summary>
    public static class GalleryNav
    {
        /// <summary>The index the key moves to from <paramref name="index"/>; −1 for a key the grid does not take.</summary>
        public static int Move(int index, int keyCode, int cols)
        {
            int n = Catalog.ShownCount;
            cols = Math.Max(1, cols);
            index = Math.Clamp(index, 0, n - 1);
            switch (keyCode)
            {
                case Keys.Left: return Math.Max(0, index - 1);
                case Keys.Right: return Math.Min(n - 1, index + 1);
                case Keys.Home: return 0;
                case Keys.End: return n - 1;
                case Keys.Up:
                case Keys.Down:
                {
                    var (start, count) = GroupSpan(index);
                    int row = (index - start) / cols, col = (index - start) % cols, rows = (count + cols - 1) / cols;
                    if (keyCode == Keys.Down)
                    {
                        if (row + 1 < rows) return Math.Min(start + count - 1, start + (row + 1) * cols + col);
                        if (start + count >= n) return index;
                        var (ns, nc) = GroupSpan(start + count);
                        return ns + Math.Min(col, nc - 1);
                    }
                    if (row > 0) return start + (row - 1) * cols + col;
                    if (start == 0) return index;
                    var (ps, pc) = GroupSpan(start - 1);
                    int lastRow = (pc - 1) / cols;
                    return ps + Math.Min(lastRow * cols + col, pc - 1);
                }
                default: return -1;
            }
        }

        /// <summary>The first index and the size of the group <paramref name="index"/> belongs to (groups are contiguous in
        /// <see cref="Catalog.Shown"/>).</summary>
        public static (int Start, int Count) GroupSpan(int index)
        {
            var shown = Catalog.Shown;
            var g = Catalog.GroupOf(shown[index]);
            int start = index, end = index;
            while (start > 0 && Catalog.GroupOf(shown[start - 1]) == g) start--;
            while (end + 1 < shown.Length && Catalog.GroupOf(shown[end + 1]) == g) end++;
            return (start, end - start + 1);
        }
    }

    static void PickFace(Kind kind)
    {
        Prefs.Stage.SetVisualizer((int)kind);
        Stage.Diagnostics.NotePick(kind);
    }

    /// <summary>One gallery tile: the preview (live or poster), the check disc, the name with its pace word, the blurb (or
    /// where the active face's motion comes from). Its LOD is its own effect's verdict: a hover elsewhere re-runs the effect,
    /// never the render, unless THIS tile flips between live and poster.</summary>
    sealed class GalleryTile : Component
    {
        public sealed record Props(int Index, float W, float PreviewH, GalleryState State);
        readonly Signal<bool> _live = new(false);
        readonly Action<NodeHandle> _onRealized;
        NodeHandle _node;
        bool _cursorShown;

        public GalleryTile() => _onRealized = h => _node = h;

        public override Element Render()
        {
            var p = UseProps<Props>();
            var ctx = UseContext(Stage.StageContext)!;
            var state = p.State;
            int index = p.Index;
            var kind = Catalog.Shown[index];
            UseSignalEffect(() =>
            {
                int selected = Catalog.IndexOf(ctx.Kind.Value);
                _live.SetIfChanged(Catalog.LodFor(index, selected, state.FocusIndex(), GpuProfile.IsWeak) == Lod.Preview);
            });
            // the keyboard cursor ARRIVING here brings the tile into the gallery's scroller (minimal move, a 10-DIP gutter;
            // the scene seam resolves the nearest scrolling ancestor — the pane's ScrollEl)
            UseSignalEffect(() =>
            {
                bool cursor = state.Focused.Value && state.Cursor.Value == index;
                if (cursor == _cursorShown) return;
                _cursorShown = cursor;
                if (cursor && !_node.IsNull && Context.Scene is { } scene)
                    scene.BringIntoView(_node, float.NaN, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide, TileGapDip);
            });
            bool live = _live.Value;
            // a LIVE Scope/Flow tile reads the stage slab's scope series: count it, so the clock fills them only then
            UseScopeReader(Context, ctx.Slab, live && Catalog.UsesScope(kind));
            var pal = ctx.Palette.Value;                       // the LIVE palette: re-renders once per landed fade (gradient stops)
            var accent = ctx.Accent.Value;
            var kindSig = ctx.Kind;
            var slab = live ? ctx.Slab : ctx.PosterSlab;
            float pw = p.W - 12f;
            // frozen at render BY DESIGN (a theme flip re-renders the stage): plain colour locals, so the thunks read only signals
            ColorF pickBorder = accent.Fill, cursorBorder = Ink.Ink, restBorder = Ink.Stroke, veil = Ink.Veil;
            float wash = Ink.IsDark ? 0.12f : 0.08f;
            var tile = new BoxEl
            {
                Width = p.W, Direction = 1, Gap = 6f, Padding = new Edges4(6f, 6f, 6f, 8f), Corners = CornerRadius4.All(6f), Fill = Ink.Plate,
                OnRealized = _onRealized,
                HoverFill = Ink.PlateHover, BrushTransitionMs = Design.Motion.Fast, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                BorderWidth = 2f,
                BorderColor = Prop.Of(() => kindSig.Value == kind ? pickBorder : state.Focused.Value && state.Cursor.Value == index ? cursorBorder : restBorder),
                OnClick = () => { state.Cursor.SetIfChanged(index); PickFace(kind); },
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
                            // keyed by the verdict: live ⇄ poster remounts the face (a poster must not keep a live face's sim)
                            new BoxEl
                            {
                                Key = live ? "live" : "poster", Width = pw, Height = p.PreviewH, HitTestVisible = false,
                                Children = [Face(kind, slab, in pal, new FaceSpec(pw, p.PreviewH, Preview: true, CoverUrl: null, Poster: !live))],
                            },
                            // the check disc: TOP (AlignSelf) RIGHT (JustifySelf) — V-U9; the selection cue that is not colour alone
                            new BoxEl
                            {
                                Width = 20f, Height = 20f, Corners = Radii.Circle(20f), Fill = accent.Fill, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 8f, 8f, 0f),
                                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Visible = Prop.Of(() => kindSig.Value == kind),
                                Children = [new TextEl(Icons.Check) { Size = 12f, FontFamily = Theme.IconFont, Color = accent.Ink }],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1, Padding = new Edges4(4f, 0f, 4f, 0f), HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = 6f,
                                Children =
                                [
                                    new TextEl(Loc.Get(NameKey(kind))) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                                    new TextEl(Loc.Get(Catalog.PaceOf(kind) == Pace.Lively ? Strings.Stage.VizEnergy.Lively : Strings.Stage.VizEnergy.Calm)) { Size = 12f, LineHeight = 16f, Color = Ink.InkTertiary, Shrink = 0f },
                                ],
                            },
                            new TextEl(Prop.Of(() => SubtitleOf(kind, kindSig.Value, ctx.Slab.Source.Value))) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            };
            return ToolTip.Wrap(tile, Loc.Get(TipKey(kind)));
        }

        /// <summary>The gutter kept between a cursor tile brought into view and the scroller's edge (the grid's tile gap).</summary>
        const double TileGapDip = 10.0;
    }

    static readonly Slider.SliderOptions SensitivityOptions = new() { Min = Bands.MinSensitivity, Max = Bands.MaxSensitivity, Step = 0.05f, IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" };
    static readonly Slider.SliderOptions OffsetOptions = new() { Min = -500f, Max = 500f, Step = 10f, IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms" };

    static Element SettingRow(string glyph, string label, Element control) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = Spacing.M, MinHeight = 48f,
        Padding = new Edges4(16f, 0f, 12f, 0f), Corners = Radii.ControlAll, Fill = Ink.Plate, BorderWidth = 1f, BorderColor = Ink.Stroke,
        Children =
        [
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary }, new TextEl(label) { Size = 14f, LineHeight = 20f, Color = Ink.Ink }] },
            control,
        ],
    };

    // ── the words ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A shown face's name key (a legacy kind names its successor).</summary>
    public static string NameKey(Kind k) => Catalog.Successor(k) switch
    {
        Kind.Verse => Strings.Stage.Viz.Verse, Kind.Bars => Strings.Stage.Viz.Bars, Kind.Ring => Strings.Stage.Viz.Ring,
        Kind.Orbit => Strings.Stage.Viz.Orbit, Kind.Aurora => Strings.Stage.Viz.Aurora, Kind.Timeline => Strings.Stage.Viz.Timeline,
        Kind.Classic => Strings.Stage.Viz.Classic, Kind.Warp => Strings.Stage.Viz.Warp, Kind.Tunnel => Strings.Stage.Viz.Tunnel,
        Kind.Ambience => Strings.Stage.Viz.Ambience, Kind.Kaleido => Strings.Stage.Viz.Kaleido, Kind.Scope => Strings.Stage.Viz.Scope,
        Kind.Drift => Strings.Stage.Viz.Drift, Kind.Type => Strings.Stage.Viz.Type, Kind.Mosaic => Strings.Stage.Viz.Mosaic,
        Kind.Spotlight => Strings.Stage.Viz.Spotlight, Kind.Magneto => Strings.Stage.Viz.Magneto, Kind.Flow => Strings.Stage.Viz.Flow,
        _ => Strings.Stage.Viz.Bloom,
    };

    static string SubKey(Kind k) => Catalog.Successor(k) switch
    {
        Kind.Verse => Strings.Stage.VizSub.Verse, Kind.Bars => Strings.Stage.VizSub.Bars, Kind.Ring => Strings.Stage.VizSub.Ring,
        Kind.Orbit => Strings.Stage.VizSub.Orbit, Kind.Aurora => Strings.Stage.VizSub.Aurora, Kind.Timeline => Strings.Stage.VizSub.Timeline,
        Kind.Classic => Strings.Stage.VizSub.Classic, Kind.Warp => Strings.Stage.VizSub.Warp, Kind.Tunnel => Strings.Stage.VizSub.Tunnel,
        Kind.Ambience => Strings.Stage.VizSub.Ambience, Kind.Kaleido => Strings.Stage.VizSub.Kaleido, Kind.Scope => Strings.Stage.VizSub.Scope,
        Kind.Drift => Strings.Stage.VizSub.Drift, Kind.Type => Strings.Stage.VizSub.Type, Kind.Mosaic => Strings.Stage.VizSub.Mosaic,
        Kind.Spotlight => Strings.Stage.VizSub.Spotlight, Kind.Magneto => Strings.Stage.VizSub.Magneto, Kind.Flow => Strings.Stage.VizSub.Flow,
        _ => Strings.Stage.VizSub.Bloom,
    };

    static string TipKey(Kind k) => Catalog.Successor(k) switch
    {
        Kind.Verse => Strings.Stage.VizTip.Verse, Kind.Bars => Strings.Stage.VizTip.Bars, Kind.Ring => Strings.Stage.VizTip.Ring,
        Kind.Orbit => Strings.Stage.VizTip.Orbit, Kind.Aurora => Strings.Stage.VizTip.Aurora, Kind.Timeline => Strings.Stage.VizTip.Timeline,
        Kind.Classic => Strings.Stage.VizTip.Classic, Kind.Warp => Strings.Stage.VizTip.Warp, Kind.Tunnel => Strings.Stage.VizTip.Tunnel,
        Kind.Ambience => Strings.Stage.VizTip.Ambience, Kind.Kaleido => Strings.Stage.VizTip.Kaleido, Kind.Scope => Strings.Stage.VizTip.Scope,
        Kind.Drift => Strings.Stage.VizTip.Drift, Kind.Type => Strings.Stage.VizTip.Type, Kind.Mosaic => Strings.Stage.VizTip.Mosaic,
        Kind.Spotlight => Strings.Stage.VizTip.Spotlight, Kind.Magneto => Strings.Stage.VizTip.Magneto, Kind.Flow => Strings.Stage.VizTip.Flow,
        _ => Strings.Stage.VizTip.Bloom,
    };

    static string GroupKey(Group g) => g switch
    {
        Group.Lyrics => Strings.Stage.VizGroup.Lyrics, Group.Classics => Strings.Stage.VizGroup.Classics,
        Group.Zune => Strings.Stage.VizGroup.Zune, Group.ITunes => Strings.Stage.VizGroup.Itunes, _ => Strings.Stage.VizGroup.Fluent,
    };

    /// <summary>The tile's second line: the face's blurb, or — while it is the active kind falling back — where its motion comes from.
    /// <paramref name="current"/> is the context's kind SIGNAL value (never a registry read — O8).</summary>
    static string SubtitleOf(Kind k, Kind current, Source live)
    {
        if (current != k || live == Source.Live) return Loc.Get(SubKey(k));
        return Loc.Get(live switch { Source.Precomputed => Strings.Stage.Source.Precomputed, Source.TempoGrid => Strings.Stage.Source.Tempo, _ => Strings.Stage.Source.Breath });
    }
}
