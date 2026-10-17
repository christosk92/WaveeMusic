// ── Themes/Zune/Structure/Views/Stage/ZuneStageTopBar.cs — Zune's `Stage.TopBar` (P5 Z4b) ──────────────────────────────────────────
//
// Z-NowPlaying's 48 bar: "now playing · full screen" on the left, the four PIVOTS centred (lyrics · visualizer · up next · artist: lowercase
// through the case role, 350 at rest and 600 selected, no pill), and the WORD ACTIONS on the right: in Visualizer mode "layout · …" (the
// options popover) and "face · …" (the gallery, the stage's one gallery rule), then "options" and "exit full screen". The side columns
// split what the pivots leave (Grow 1, Basis 0), so the pivots stay centred; the look's words show only where the record and the room
// allow (`ZuneStageGeometry.LookWordsFit`). On an icon-only stage (the narrow classes) options and exit are 40-wide glyphs and the
// label gives way. Every target is the tier's `TargetH` tall (40 at Compact). The options word is the TeachingTip's anchor (`GalleryButton`).
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

    /// <summary>The registered template of <see cref="ViewKeys.StageTopBar"/>.</summary>
    public static Element Template(StageTopBarProps props, in ViewContext ctx)
    {
        var st = props.Stage;
        var L = st.Layout.Value;
        var mode = st.Mode.Value;
        var t = ZuneStageTiers.StageOf(ctx.Tier());
        bool iconOnly = L.IconOnlySelector;
        bool lookWords = t.LookWords && mode == Stage.Mode.Visualizer && G.LookWordsFit(in L, t);
        bool optionsOn = st.OptionsOpen.Value;
        var overControls = st.OverControls;
        var galleryButton = st.GalleryButton;
        float target = t.TargetH;

        var right = new List<Element>(4);
        if (lookWords)
        {
            string layout = Loc.Get(LayoutName(st.LayoutPref.Value));
            string face = Loc.Get(Visualizer.NameKey(st.Kind.Value));
            right.Add(Word(Strings.Theme.Zune.Stage.LayoutWord(layout), st.OpenOptions, Ink.InkSecondary, target, null, "stage:word:layout"));
            right.Add(Word(Strings.Theme.Zune.Stage.FaceWord(face), st.ToggleGallery, Ink.InkSecondary, target, null, "stage:word:face"));
        }
        if (iconOnly)
        {
            right.Add(ToolTip.Wrap(Glyph(Icons.Equalizer, st.OpenOptions, optionsOn, n => galleryButton.SetIfChanged(n), target), Loc.Get(Strings.Theme.Zune.Stage.Options)));
            right.Add(ToolTip.Wrap(Glyph(Icons.BackToWindow, st.Exit, false, null, target), Loc.Get(Strings.Stage.Exit)));
        }
        else
        {
            right.Add(Word(Loc.Get(Strings.Theme.Zune.Stage.Options), st.OpenOptions, optionsOn ? Ink.Ink : Ink.InkSecondary, target, n => galleryButton.SetIfChanged(n), "stage:word:options"));
            right.Add(Word(Loc.Get(Strings.Stage.Exit), st.Exit, Ink.Ink, target, null, "stage:word:exit"));
        }

        return new BoxEl
        {
            Grow = 1f, Height = Stage.Layout.TopBarH, Direction = 0, AlignItems = FlexAlign.Center,
            Padding = new Edges4(t.Gutter, 0f, t.BarPadR, 0f), Gap = iconOnly ? G.BarGlyphGap : G.BarGap,
            Gradient = mode == Stage.Mode.Visualizer ? GradientDown(new GradientStop(0f, Shade(G.TopBarVeilA)), new GradientStop(1f, Shade(0f))) : null,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                new BoxEl
                {
                    // the label never paints over the pivots: clipped, and it is the part that gives way
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true,
                    Children = t.BarLabel && !iconOnly
                        ? [new TextEl(Loc.Get(Strings.Stage.Brand)) { Size = G.WordSize, LineHeight = G.WordLine, Color = Ink.InkSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }]
                        : [],
                },
                Pivots(st, mode, t),
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, ClipToBounds = true,
                    Gap = iconOnly ? G.BarGlyphGap : G.BarGap,
                    Children = right.ToArray(),
                },
            ],
        };
    }

    /// <summary>The four pivots: words, not a pill. Each is a tab (one tab stop: the selected word); Left / Right / Home / End move the mode.</summary>
    static Element Pivots(Stage.StageCtx st, Stage.Mode mode, ZuneStageLayout t)
    {
        var facet = R.Type.FacetTitleStyle;
        ushort rest = (ushort)facet.RestWeight, selected = (ushort)facet.SelectedWeight;
        var setMode = st.SetMode;
        int count = Stage.ModeRules.Count;
        int current = (int)mode;
        var words = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool on = i == current;
            words[i] = new BoxEl
            {
                Key = "stage:pivot:" + i, Height = MathF.Max(t.PivotLine, t.TargetH), Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Role = AutomationRole.Tab, Focusable = true, TabStop = on, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
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
                    new TextEl(Loc.Get(s_modes[i]))
                    {
                        Size = t.PivotSize, LineHeight = t.PivotLine, FontFamily = R.Type.DisplayFace, Weight = on ? selected : rest,
                        Color = on ? Ink.Ink : Ink.InkSecondary, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Navigation), MaxLines = 1,
                    },
                ],
            };
        }
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = t.PivotGap, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = words,
        };
    }

    /// <summary>A word action: lowercase through the command case, the secondary ink lifting to the ink on hover, no plate.</summary>
    static BoxEl Word(string text, Action onClick, ColorF color, float target, Action<NodeHandle>? onRealized, string key) => new()
    {
        Key = key, Height = target, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick, OnRealized = onRealized,
        Children = [new TextEl(text) { Size = G.WordSize, LineHeight = G.WordLine, Color = color, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };

    /// <summary>A glyph of the icon-only bar: 40 wide, the tier's target tall, no plate (the ink lifts on hover).</summary>
    static BoxEl Glyph(string glyph, Action onClick, bool lit, Action<NodeHandle>? onRealized, float target) => new()
    {
        Width = G.BarGlyphW, Height = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = G.BarGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = lit ? Ink.Ink : Ink.InkSecondary, HoverColor = Ink.Ink }],
    };

    /// <summary>The stored layout's user-facing name.</summary>
    static string LayoutName(Stage.VizLayout layout) => layout switch
    {
        Stage.VizLayout.LargeArt => Strings.Stage.Layout.Large,
        Stage.VizLayout.Centered => Strings.Stage.Layout.Centered,
        Stage.VizLayout.Artist => Strings.Stage.Layout.Artist,
        _ => Strings.Stage.Layout.Card,
    };

    static ColorF Shade(float a) => Ink.Veil with { A = a };
}

/// <summary>The face chip under a Compact window's identity card (Visualizer mode): the face's name between its glyph and a chevron on a
/// square 40-tall plate, the door to the gallery sheet. Its own component so a face change re-renders the chip only.</summary>
internal sealed class ZuneStageFaceChip : Component
{
    public sealed record Props(Stage.StageCtx Stage);

    public override Element Render()
    {
        var sc = UseProps<Props>().Stage;
        var L = sc.Layout.Value;
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
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 0, MaxWidth = MathF.Max(0f, L.W - 2f * L.TransportInset),
                    Margin = new Edges4(L.TransportInset, L.FaceChipY, 0f, 0f),
                    Children = [ToolTip.Wrap(chip, Loc.Get(Strings.Stage.Options.OpenGallery))],
                },
            ],
        };
    }
}
