// ── Themes/Zune/Structure/Views/Stage/ZuneStageTopBar.cs — Zune's `Stage.TopBar` (P5 Z4b, tiers ZT3) ───────────────────────────────
//
// The stage's head, per window tier (`ZuneStageLayout`):
//
//   Standard / Huge / Huge 4K  (Z-NowPlaying, -Huge, -Huge4K) one bar (48 / 56 / 72): "now playing · full screen" on the left, the four
//            PIVOTS centred (lyrics · visualizer · up next · artist: lowercase through the case role, 350 at rest and 600 selected, no
//            pill), the WORD ACTIONS on the right: in Visualizer mode "layout · …" (the options popover) and "face · …" (the gallery), then
//            "options" and "exit full screen". The side columns split what the pivots leave (Grow 1, Basis 0), so the pivots stay centred.
//   Small    (Z-NowPlaying-Small) the pivots LEAD the 48 bar, then the look's words, then the options ("…") and exit glyphs.
//   Compact  (Z-NowPlaying-Compact, Z-Lyrics-Compact) the 48 bar (the exit chevron, "now playing", the options "…") over the pivots' OWN
//            48 row: 28/36, the lit word ROTATED to the front (the band's pivot idiom, sliding on the pivot slide), the run scrolling under
//            an edge fade, so a pivot never clips at 300 wide.
//
// Every target is the tier's `TargetH` tall (40 at Compact). The options word / glyph is the TeachingTip's anchor (`GalleryButton`). The
// pivots keep their keys (`stage:pivot:<mode>`) at every tier, so a crossing moves them, never remounts them.
//
// The face chip under a Compact window's identity card (Visualizer mode) is chrome too: `ZuneStageFaceChip`, placed by the root.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage top bar.</summary>
internal static class ZuneStageTopBar
{
    static readonly string[] s_modes = [Strings.Stage.Mode.Lyrics, Strings.Stage.Mode.Visualizer, Strings.Stage.Mode.Queue, Strings.Stage.Mode.Artist];

    /// <summary>The key of the Compact pivots' row.</summary>
    public const string PivotRowKey = "stage:pivots:row";

    /// <summary>The registered template of <see cref="ViewKeys.StageTopBar"/>.</summary>
    public static Element Template(StageTopBarProps props, in ViewContext ctx)
    {
        var st = props.Stage;
        var L = st.Layout.Value;
        var mode = st.Mode.Value;
        var t = ZuneStageTiers.StageOf(ctx.Tier());
        bool glyphs = t.GlyphActions || L.IconOnlySelector;
        bool lookWords = t.LookWords && t.PivotRowH <= 0f && mode == Stage.Mode.Visualizer && G.LookWordsFit(in L, t);
        bool optionsOn = st.OptionsOpen.Value;
        var overControls = st.OverControls;
        var galleryButton = st.GalleryButton;
        float target = t.TargetH;

        var right = new List<Element>(4);
        if (lookWords)
        {
            string layout = Loc.Get(LayoutName(st.LayoutPref.Value));
            string face = Loc.Get(Visualizer.NameKey(st.Kind.Value));
            right.Add(Word(Strings.Theme.Zune.Stage.LayoutWord(layout), st.OpenOptions, Ink.InkSecondary, t, null, "stage:word:layout"));
            right.Add(Word(Strings.Theme.Zune.Stage.FaceWord(face), st.ToggleGallery, Ink.InkSecondary, t, null, "stage:word:face"));
        }
        if (mode == Stage.Mode.Lyrics)
        {
            // Z-Lyrics: "translate" (the second-line cycle; quiet and inert when the document has no second layer, lit while one shows) and "resync"
            bool hasSecond = st.HasSecondLine(), secondOn = hasSecond && st.SecondLineOn();
            right.Add(Word(Loc.Get(Strings.Theme.Zune.Player.Translate), hasSecond ? st.ToggleSecondLine : static () => { },
                secondOn ? Ink.Ink : Ink.InkSecondary, t, null, "stage:word:translate"));
            right.Add(Word(Loc.Get(Strings.Player.ResyncLyrics), st.ResyncLyrics, Ink.InkSecondary, t, null, "stage:word:resync"));
        }
        if (glyphs)
        {
            right.Add(ToolTip.Wrap(Glyph(Icons.More, st.OpenOptions, optionsOn, n => galleryButton.SetIfChanged(n), target, "stage:glyph:options"), Loc.Get(Strings.Theme.Zune.Stage.Options)));
            if (t.PivotRowH <= 0f)
                right.Add(ToolTip.Wrap(Glyph(Icons.BackToWindow, st.Exit, false, null, target, "stage:glyph:exit"), Loc.Get(Strings.Stage.Exit)));
        }
        else
        {
            right.Add(Word(Loc.Get(Strings.Theme.Zune.Stage.Options), st.OpenOptions, optionsOn ? Ink.Ink : Ink.InkSecondary, t, n => galleryButton.SetIfChanged(n), "stage:word:options"));
            right.Add(Word(Loc.Get(Strings.Stage.Exit), st.Exit, Ink.Ink, t, null, "stage:word:exit"));
        }

        var barKids = new List<Element>(4);
        if (t.PivotRowH > 0f)
        {
            // Compact: the exit chevron, the label, the options glyph; the pivots on their own row below
            barKids.Add(ToolTip.Wrap(Glyph(Icons.ChevronDown, st.Exit, false, null, target, "stage:glyph:exit"), Loc.Get(Strings.Stage.Exit)));
            barKids.Add(new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true,
                Children = t.BarLabel ? [Label(Loc.Get(Strings.Stage.NowPlayingEyebrow), t)] : [],
            });
            barKids.AddRange(right);
        }
        else if (t.PivotsLead)
        {
            barKids.Add(Pivots(st, mode, t, rotate: false));
            barKids.Add(new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f });
            barKids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Shrink = 0f, Gap = glyphs ? G.BarGlyphGap : t.BarGap, Children = right.ToArray() });
        }
        else
        {
            barKids.Add(new BoxEl
            {
                // the label never paints over the pivots: clipped, and it is the part that gives way
                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true,
                Children = t.BarLabel && !L.IconOnlySelector ? [Label(Loc.Get(Strings.Stage.Brand), t)] : [],
            });
            barKids.Add(Pivots(st, mode, t, rotate: false));
            barKids.Add(new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, ClipToBounds = true,
                Gap = glyphs ? G.BarGlyphGap : t.BarGap,
                Children = right.ToArray(),
            });
        }

        var bar = new BoxEl
        {
            Key = "stage:bar", Height = t.BarH, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Layout = ZuneMotion.HeightResize,
            Padding = new Edges4(t.BarPadL, 0f, t.BarPadR, 0f), Gap = t.PivotRowH > 0f ? G.BarGlyphGap : t.BarGap,
            Children = barKids.ToArray(),
        };
        var head = new List<Element>(2) { bar };
        if (t.PivotRowH > 0f)
        {
            head.Add(new ScrollEl
            {
                Key = PivotRowKey, Horizontal = true, Height = t.PivotRowH, Shrink = 0f, MinWidth = 0f,
                AutoEdgeFade = true, AutoEdgeFadeBand = ZuneBandRules.EdgeFadeBand, SuppressScrollBar = true,
                Content = new BoxEl { Padding = new Edges4(t.Gutter, 0f, t.Gutter, 0f), Direction = 0, Shrink = 0f, Children = [Pivots(st, mode, t, rotate: true)] },
            });
        }
        return new BoxEl
        {
            // fills the head slot, whose height eases across a crossing (ZuneStageHead): the veil follows it
            Grow = 1f, Direction = 1,
            Gradient = mode == Stage.Mode.Visualizer ? GradientDown(new GradientStop(0f, Shade(G.TopBarVeilA)), new GradientStop(1f, Shade(0f))) : null,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children = head.ToArray(),
        };
    }

    static Element Label(string text, ZuneStageLayout t) => new TextEl(text)
    {
        Size = t.WordSize, LineHeight = t.WordLine, Color = Ink.InkSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>The four pivots: words, not a pill. Each is a tab (one tab stop: the selected word); Left / Right / Home / End move the mode.
    /// Rotated (Compact): the lit word leads and the others follow in order, sliding to their places.</summary>
    static Element Pivots(Stage.StageCtx st, Stage.Mode mode, ZuneStageLayout t, bool rotate)
    {
        var facet = R.Type.FacetTitleStyle;
        ushort rest = (ushort)facet.RestWeight, selected = (ushort)facet.SelectedWeight;
        var setMode = st.SetMode;
        int count = Stage.ModeRules.Count;
        int current = (int)mode;
        var order = ZuneBandRules.PivotOrder(count, current, rotate);
        var words = new Element[order.Length];
        float h = MathF.Max(t.PivotLine, MathF.Max(t.TargetH, t.PivotRowH));
        for (int slot = 0; slot < order.Length; slot++)
        {
            int index = order[slot];
            bool on = index == current;
            words[slot] = new BoxEl
            {
                Key = "stage:pivot:" + index, Height = h, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Role = AutomationRole.Tab, Focusable = true, TabStop = on, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                Animate = rotate && !Design.Reduced ? ZuneMotion.PivotSlide : null,
                OnClick = () => setMode((Stage.Mode)index),
                OnKeyDown = e =>
                {
                    if (e.Handled || e.Ctrl || e.Alt) return;
                    int next = e.KeyCode switch
                    {
                        Keys.Left => index - 1, Keys.Right => index + 1, Keys.Home => 0, Keys.End => count - 1, _ => int.MinValue,
                    };
                    if (next == int.MinValue) return;
                    e.Handled = true;
                    if ((uint)next < (uint)count && next != current) setMode((Stage.Mode)next);
                },
                Children =
                [
                    new TextEl(Loc.Get(s_modes[index]))
                    {
                        Size = t.PivotSize, LineHeight = t.PivotLine, FontFamily = R.Type.DisplayFace, Weight = on ? selected : rest,
                        Color = on ? Ink.Ink : Ink.InkSecondary, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Navigation), MaxLines = 1,
                    },
                ],
            };
        }
        return new BoxEl
        {
            // a crossing that moves the run (W1008: centred <-> leading) slides it on the pivot slide instead of snapping (audit B3bc)
            Key = "stage:pivots", Direction = 0, AlignItems = FlexAlign.Center, Gap = t.PivotGap, Shrink = rotate ? 0f : 1f, MinWidth = 0f, ClipToBounds = !rotate,
            Layout = Design.Reduced ? null : ZuneMotion.PivotSlide,
            Children = words,
        };
    }

    /// <summary>A word action: lowercase through the command case, the secondary ink lifting to the ink on hover, no plate.</summary>
    static BoxEl Word(string text, Action onClick, ColorF color, ZuneStageLayout t, Action<NodeHandle>? onRealized, string key) => new()
    {
        Key = key, Height = t.TargetH, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick, OnRealized = onRealized,
        Children = [new TextEl(text) { Size = t.WordSize, LineHeight = t.WordLine, Color = color, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };

    /// <summary>A glyph of the bar: at least 40 wide, the tier's target tall, no plate (the ink lifts on hover).</summary>
    static BoxEl Glyph(string glyph, Action onClick, bool lit, Action<NodeHandle>? onRealized, float target, string key) => new()
    {
        Key = key, Width = MathF.Max(G.BarGlyphW, target), Height = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = G.BarGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = lit ? Ink.Ink : Ink.InkSecondary, HoverColor = Ink.Ink }],
    };

    /// <summary>The stored layout's user-facing name.</summary>
    internal static string LayoutName(Stage.VizLayout layout) => layout switch
    {
        Stage.VizLayout.LargeArt => Strings.Stage.Layout.Large,
        Stage.VizLayout.Centered => Strings.Stage.Layout.Centered,
        Stage.VizLayout.Artist => Strings.Stage.Layout.Artist,
        _ => Strings.Stage.Layout.Card,
    };

    static ColorF Shade(float a) => Ink.Veil with { A = a };
}

/// <summary>The face chip under a Compact window's identity card (Visualizer mode): the face's name between its glyph and a chevron on a
/// square 40-tall plate, the door to the gallery sheet. Its own component so a face change re-renders the chip only. It moves down with the
/// tier's head, gives way where the plate would cover it, and is not drawn in the Stack arrangement (the face's word sits at its foot).</summary>
internal sealed class ZuneStageFaceChip : Component
{
    public sealed record Props(Stage.StageCtx Stage);

    public override Element Render()
    {
        var sc = UseProps<Props>().Stage;
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        var t = ZuneStageTiers.StageOf(in tiers);
        var L = sc.Layout.Value;
        var a = ZuneStageKit.Arrangement(sc, in tiers);
        float y = L.FaceChipY + t.Drop;
        if (a.Form == ZuneStageForm.Stack || y + Stage.Layout.FaceChipH > L.TransportTop) return Stage.Layer with { HitTestVisible = false };
        string name = Loc.Get(Visualizer.NameKey(sc.Kind.Value));
        var toggle = sc.ToggleGallery;
        var overControls = sc.OverControls;
        var chip = new BoxEl
        {
            Height = Stage.Layout.FaceChipH, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneStageGeometry.FaceChipGap,
            Padding = new Edges4(ZuneStageGeometry.FaceChipPadX, 0f, ZuneStageGeometry.FaceChipPadX, 0f), Corners = R.Shape.All(ShapeRole.Control),
            Fill = Ink.Plate, HoverFill = Ink.PlateHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = toggle,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                new TextEl(Icons.Equalizer) { Size = ZuneStageGeometry.FaceChipGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink },
                new TextEl(name) { Size = ZuneStageGeometry.FaceChipText, LineHeight = ZuneStageGeometry.FaceChipLine, Color = Ink.Ink, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                new TextEl(Icons.ChevronDown) { Size = ZuneStageGeometry.FaceChipChevron, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
            ],
        };
        float inset = MathF.Max(L.TransportInset, t.Gutter);
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 0, MaxWidth = MathF.Max(0f, L.W - 2f * inset),
                    Margin = new Edges4(inset, y, 0f, 0f),
                    Children = [ToolTip.Wrap(chip, Loc.Get(Strings.Stage.Options.OpenGallery))],
                },
            ],
        };
    }
}
