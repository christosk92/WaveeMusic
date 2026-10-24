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
// Every target is the tier's `TargetH` tall (40 at Compact). The options word / glyph is the TeachingTip's anchor (`GalleryButton`). ONE
// bar at every tier (audit B2z r3): the label, the pivots' scroller and the actions are keyed children the tier re-orders, never re-parents
// (Compact wraps the scroller onto the second line), so a crossing slides the pivots and the options, never remounts them. The forms a tier
// swaps (the exit's chevron, glyph and word; the options' glyph and word) cross-fade on the declared swap family.
//
// The face chip under a Compact window's identity card (Visualizer mode) is chrome too: `ZuneStageFaceChip`, placed by the root.

using FluentGpu.Animation;
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

    /// <summary>The key of the pivots' run (at every tier: their own row at Compact, a run in the bar above it).</summary>
    public const string PivotRowKey = "stage:pivots:row";

    /// <summary>The registered template of <see cref="ViewKeys.StageTopBar"/>.</summary>
    public static Element Template(StageTopBarProps props, in ViewContext ctx)
    {
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        var st = props.Stage;
        var L = st.Layout.Value;
        var mode = st.Mode.Value;
        var t = ZuneStageTiers.StageOf(ctx.Tier());
        bool own = t.PivotRowH > 0f;   // Compact: the pivots wrap onto their own row under the bar
        bool glyphs = t.GlyphActions || L.IconOnlySelector;
        bool lookWords = t.LookWords && !own && mode == Stage.Mode.Visualizer && G.LookWordsFit(in L, t);
        bool optionsOn = st.OptionsOpen.Value;
        var overControls = st.OverControls;
        var galleryButton = st.GalleryButton;
        float target = t.TargetH;

        var actions = new List<Element>(5);
        if (lookWords)
        {
            string layout = Loc.Get(LayoutName(st.LayoutPref.Value));
            string face = Loc.Get(Visualizer.NameKey(st.Kind.Value));
            actions.Add(Word(Strings.Theme.Zune.Stage.LayoutWord(layout), st.OpenOptions, Ink.InkSecondary, t, null, "stage:word:layout"));
            actions.Add(Word(Strings.Theme.Zune.Stage.FaceWord(face), st.ToggleGallery, Ink.InkSecondary, t, null, "stage:word:face"));
        }
        if (mode == Stage.Mode.Lyrics)
        {
            // Z-Lyrics: "translate" (the second-line cycle; quiet and inert when the document has no second layer, lit while one shows) and "resync"
            bool hasSecond = st.HasSecondLine(), secondOn = hasSecond && st.SecondLineOn();
            actions.Add(Word(Loc.Get(Strings.Theme.Zune.Player.Translate), hasSecond ? st.ToggleSecondLine : static () => { },
                secondOn ? Ink.Ink : Ink.InkSecondary, t, null, "stage:word:translate"));
            actions.Add(Word(Loc.Get(Strings.Player.ResyncLyrics), st.ResyncLyrics, Ink.InkSecondary, t, null, "stage:word:resync"));
        }
        // the options and the exit change form with the tier (a glyph, a word; Compact's exit is the chevron leading the bar): each form is
        // its own keyed tree that cross-fades into the next on the declared swap family; a form both tiers draw keeps its node and slides
        if (glyphs)
            actions.Add(Form("stage:options:glyph", ToolTip.Wrap(Glyph(Icons.More, st.OpenOptions, optionsOn, n => galleryButton.SetIfChanged(n), target, "stage:glyph:options"),
                Loc.Get(Strings.Theme.Zune.Stage.Options)), enter));
        else
            actions.Add(Form("stage:options:word", Word(Loc.Get(Strings.Theme.Zune.Stage.Options), st.OpenOptions, optionsOn ? Ink.Ink : Ink.InkSecondary, t,
                n => galleryButton.SetIfChanged(n), "stage:word:options"), enter));
        if (!own)
            actions.Add(glyphs
                ? Form("stage:exit:glyph", ToolTip.Wrap(Glyph(Icons.BackToWindow, st.Exit, false, null, target, "stage:glyph:exit"), Loc.Get(Strings.Stage.Exit)), enter)
                : Form("stage:exit:word", Word(Loc.Get(Strings.Stage.Exit), st.Exit, Ink.Ink, t, null, "stage:word:exit"), enter));

        // ONE bar at every tier: its parts are keyed children the tier only re-orders (they slide on the pivot slide), never re-parents, so a
        // crossing keeps the pivots' focus and hover. Compact wraps the pivots onto their own row (the bar is then the head's two rows).
        bool centred = !own && !t.PivotsLead;
        LayoutTransition? slide = Design.Reduced ? null : ZuneMotion.PivotSlide;
        var label = new BoxEl
        {
            // Compact: "now playing" between the exit and the options; Standard and up: the brand, the side that gives way (clipped, so it never
            // paints over the pivots); Small: the air between the pivots and the actions
            Key = "stage:bar:label", Height = t.BarH, Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true,
            Margin = own ? new Edges4(G.BarGlyphGap, 0f, G.BarGlyphGap, 0f) : default, Layout = slide,
            Children = t.BarLabel && (own || !L.IconOnlySelector) ? [Label(Loc.Get(own ? Strings.Stage.NowPlayingEyebrow : Strings.Stage.Brand), t)] : [],
        };
        var right = new BoxEl
        {
            // centred pivots: the side columns split what the pivots leave (Grow 1, Basis 0), so the pivots stay centred
            Key = "stage:bar:actions", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Layout = slide,
            Grow = centred ? 1f : 0f, Basis = centred ? 0f : float.NaN, Shrink = 0f, MinWidth = 0f, ClipToBounds = centred,
            Gap = glyphs ? G.BarGlyphGap : t.BarGap,
            Children = actions.ToArray(),
        };
        var pivots = PivotRow(st, mode, t, L.W, own, slide);
        Element[] parts = own
            ? [Form("stage:exit:lead", ToolTip.Wrap(Glyph(Icons.ChevronDown, st.Exit, false, null, target, "stage:glyph:exit"), Loc.Get(Strings.Stage.Exit)), enter), label, right, pivots]
            : t.PivotsLead ? [pivots, label, right] : [label, pivots, right];

        var bar = new BoxEl
        {
            // Compact's row break is the wrap: the pivots' row is the stage's width, so it starts the second line (no gap between the lines: the
            // label's margin spaces the first)
            Key = "stage:bar", Height = t.HeadH, Direction = 0, Wrap = own, AlignItems = FlexAlign.Center, Shrink = 0f, Layout = ZuneMotion.HeightResize,
            Padding = new Edges4(t.BarPadL, 0f, t.BarPadR, 0f), Gap = own ? 0f : t.BarGap,
            Children = parts,
        };
        return new BoxEl
        {
            // fills the head slot, whose height eases across a crossing (ZuneStageHead): the veil follows it
            Grow = 1f, Direction = 1,
            Gradient = mode == Stage.Mode.Visualizer ? GradientDown(new GradientStop(0f, Shade(G.TopBarVeilA)), new GradientStop(1f, Shade(0f))) : null,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children = [bar],
        };
    }

    /// <summary>One form of a part that changes form with the tier (the exit, the options): a keyed host that enters (after the view's first
    /// render) and exits on the declared swap cross-fade.</summary>
    static BoxEl Form(string key, Element body, EnterExit? enter) => new()
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
        Enter = enter, Exit = ZuneMotion.SwapFade, Transition = MotionTok.ControlNormal,
        Children = [body],
    };

    /// <summary>The pivots' run, the same nodes at every tier: their own row at Compact (the stage's width, bled over the bar's padding, the
    /// words inset by the gutter and scrolling under an edge fade, so a pivot never clips at 300 wide); sized to its words in the bar above it
    /// (where the bar runs short it scrolls under the fade, never clips). The box carries the slide (a scroller takes no layout transition).</summary>
    static BoxEl PivotRow(Stage.StageCtx st, Stage.Mode mode, ZuneStageLayout t, float stageW, bool own, LayoutTransition? slide) => new()
    {
        Key = PivotRowKey, Direction = 0, Shrink = own ? 0f : 1f, MinWidth = 0f, Layout = slide,
        Width = own ? stageW : float.NaN, Height = own ? t.PivotRowH : float.NaN,
        Margin = own ? new Edges4(-t.BarPadL, 0f, -t.BarPadR, 0f) : default,
        Children =
        [
            new ScrollEl
            {
                Horizontal = true, ContentSized = true, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                AutoEdgeFade = true, AutoEdgeFadeBand = ZuneBandRules.EdgeFadeBand, SuppressScrollBar = true,
                Content = new BoxEl
                {
                    Padding = own ? new Edges4(t.Gutter, 0f, t.Gutter, 0f) : default, Direction = 0, Shrink = 0f,
                    Children = [Pivots(st, mode, t, rotate: own)],
                },
            },
        ],
    };

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
            Key = "stage:pivots", Direction = 0, AlignItems = FlexAlign.Center, Gap = t.PivotGap, Shrink = 0f,
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
