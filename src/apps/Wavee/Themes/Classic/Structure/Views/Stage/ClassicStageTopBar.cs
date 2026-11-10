// ── Themes/Classic/Structure/Views/Stage/ClassicStageTopBar.cs — Classic's `Stage.TopBar` (themes plan 6.1, T7a1) ───────────
//
// From `Shell\Stage.UI.cs`'s `TopBar` component: 48 DIP, brand · the SelectorBar (pill tinted through TemplateParts) · the options
// toggle · exit. With a music video on (the stage's Video mode, the board "Classic · Cinema") the tabs lead with Video and the options
// toggle gives way to "Switch to audio". It fills its wrapper (Grow); the root places it under the chrome's Show. The mode, the layout class and the accent are the
// stage context's signals (`props.Stage`), the verbs are its actions (`SetMode`, `OpenOptions`, `Exit`, `OverControls`); the options
// button's realized node is the TeachingTip's anchor SIGNAL (V-U16). On a Compact window (C-Stage-Compact) the mode tabs and the glyph
// buttons are 40 tall, the Compact target rule (`ClassicStageTiers.StageOf`). The template owns the selector's index cell and the pill's
// parts through the slot's hook context, as the component owned them. The face chip under the Compact window's identity card (C-Stage-Compact)
// is chrome too: `ClassicStageFaceChip`, placed by the root under its own Show.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's stage top bar.</summary>
internal static class ClassicStageTopBar
{
    /// <summary>The registered template of <see cref="ViewKeys.StageTopBar"/>.</summary>
    public static Element Template(StageTopBarProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks;
        var st = props.Stage;
        var L = st.Layout.Value;
        var set = st.Accent.Value;
        float targetH = ClassicStageTiers.StageOf(ctx.Tier()).BarTargetH;
        // The tabs: today's four, or (a music video on, the board "Classic · Cinema") Video first, then Lyrics, Up next and Artist. A stage
        // that shows the Visualizer keeps today's tabs (the Video set has no Visualizer tab to stand on).
        bool videoTabs = VideoTabs(st);
        bool videoMode = st.Mode.Value == Stage.Mode.Video;
        var modes = videoTabs ? s_videoModes : s_modes;
        var index = h.UseSignal(0);
        h.UseSignalEffect(() => index.SetIfChanged(Math.Max(0, Array.IndexOf(VideoTabs(st) ? s_videoModes : s_modes, st.Mode.Value))));
        var parts = h.UseMemo(() => { var p = new TemplateParts(); p.Set<BoxEl>(SelectorBar.PartPill, b => b with { Fill = set.Fill }); return p; }, DepKey.From(set.GetHashCode()));
        string[] items = L.IconOnlySelector ? ["", "", "", ""]
            : videoTabs ? [Loc.Get(Strings.Stage.Mode.Video), Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)]
            : [Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Visualizer), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)];
        string?[] icons = videoTabs ? [Icons.Video, Icons.Document, SegoeGlyphs.Queue, Icons.Contact] : [Icons.Document, Icons.Equalizer, SegoeGlyphs.Queue, Icons.Contact];
        var style = new SelectorBarStyle
        {
            RestColor = Ink.InkSecondary, SelectedColor = Ink.Ink, HoverColor = Ink.Ink, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
            SelectedWeight = ClassicStageGeometry.SelectorWeight, ItemHeight = MathF.Max(ClassicStageGeometry.SelectorItemHeight, targetH), ItemGap = ClassicStageGeometry.SelectorItemGap,
        };
        bool optionsOn = st.OptionsOpen.Value;
        var setMode = st.SetMode;
        var openOptions = st.OpenOptions;
        var exit = st.Exit;
        var toAudio = st.SwitchToAudio;
        var overControls = st.OverControls;
        var galleryButton = st.GalleryButton;
        return new BoxEl
        {
            Grow = 1f, Height = Stage.Layout.TopBarH, Direction = 0, AlignItems = FlexAlign.Center,
            Padding = ClassicStageGeometry.TopBarPadding, Gap = Spacing.S,
            Gradient = GradientDown(new GradientStop(0f, Shade(ClassicStageGeometry.TopBarVeilA)), new GradientStop(1f, Shade(0f))),
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                new BoxEl
                {
                    // the brand never paints over the selector: clipped, and the subtitle is the part that gives way. Beside the icon-only
                    // selector (a Compact bar) the column holds the mark and "Wavee" only, so the subtitle shows whole or not at all (never
                    // "N…", audit V40; the exit label's rule, `ExitLabelShown`)
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl { Width = ClassicStageGeometry.BrandMark, Height = ClassicStageGeometry.BrandMark, Corners = ClassicStageGeometry.BrandMarkCorners, Fill = Prop.Bind(st.Slab.Accent), Shrink = 0f },
                        new TextEl("Wavee") { Size = ClassicStageGeometry.BrandSize, LineHeight = ClassicStageGeometry.BrandLine, Weight = ClassicStageGeometry.BrandWeight, Color = Ink.Ink, Shrink = 0f },
                        new TextEl(Loc.Get(videoMode ? Strings.Player.VideoFullScreen : Strings.Stage.Brand))
                        {
                            Size = ClassicStageGeometry.BrandSize, LineHeight = ClassicStageGeometry.BrandLine, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            MinWidth = 0f, Shrink = 1f, Visible = !L.IconOnlySelector,
                        },
                    ],
                },
                SelectorBar.Create(items, index, onChange: i => setMode(modes[Math.Clamp(i, 0, modes.Length - 1)]), parts: parts, icons: icons, style: style),
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Stage.Layout.BarButtonGap,
                    Children =
                    [
                        // Video mode: "Switch to audio" (the video off, the stage on the cover and lyrics) where the options toggle stands (the
                        // visualizer's options have nothing to set over the picture); labelled only where the allocator says the column holds it
                        videoMode
                            ? L.AudioLabelShown
                                ? Button.Create(Loc.Get(Strings.Player.SwitchToAudio), toAudio, ButtonAppearance.Subtle, ControlSize.Small, glyph: Icons.MusicNote) with { Shrink = 0f }
                                : ToolTip.Wrap(BarGlyph(Icons.MusicNote, toAudio, lit: false, onRealized: null, targetH), Loc.Get(Strings.Player.SwitchToAudio))
                            : ToolTip.Wrap(BarGlyph(Icons.Equalizer, openOptions, lit: optionsOn, onRealized: n => galleryButton.SetIfChanged(n), targetH), Loc.Get(Strings.Stage.Options.Button)),   // the TeachingTip's anchor SIGNAL (V-U16)
                        // Labelled only where the allocator says the column holds it (never "Ex…"); else the glyph with the label as its tooltip.
                        L.ExitLabelShown
                            ? Button.Create(Loc.Get(Strings.Stage.Exit), exit, ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.BackToWindow) with { Shrink = 0f }
                            : ToolTip.Wrap(BarGlyph(Icons.BackToWindow, exit, lit: false, onRealized: null, targetH), Loc.Get(Strings.Stage.Exit)),
                    ],
                },
            ],
        };
    }

    /// <summary>The selector's modes, tab by tab: today's, and the Video set (contract 1.95).</summary>
    static readonly Stage.Mode[] s_modes = [Stage.Mode.Lyrics, Stage.Mode.Visualizer, Stage.Mode.Queue, Stage.Mode.Artist];
    static readonly Stage.Mode[] s_videoModes = [Stage.Mode.Video, Stage.Mode.Lyrics, Stage.Mode.Queue, Stage.Mode.Artist];

    /// <summary>The Video set of tabs shows: the stage offers the picture and is not on the Visualizer (subscribes).</summary>
    static bool VideoTabs(Stage.StageCtx st) => st.VideoOffered.Value && st.Mode.Value != Stage.Mode.Visualizer;

    /// <summary>A 40×36 glass glyph button of the top bar (the options toggle, the icon-only exit); 40×40 on a Compact window.</summary>
    static BoxEl BarGlyph(string glyph, Action onClick, bool lit, Action<NodeHandle>? onRealized, float targetH) => new()
    {
        Width = Stage.Layout.BarButtonW, Height = MathF.Max(ClassicStageGeometry.BarButtonHeight, targetH), Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = lit ? Ink.GlassHover : Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = ClassicStageGeometry.BarGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }],
    };

    /// <summary>The stage veil at alpha <paramref name="a"/>.</summary>
    static ColorF Shade(float a) => Ink.Veil with { A = a };
}

/// <summary>The face chip under the Compact window's identity card (C-Stage-Compact, Visualizer mode): the face's name between its glyph and a
/// chevron on a 40-tall pill, the door to the gallery sheet (the stage's one gallery rule, <c>ToggleGallery</c>). Its own component so a face
/// change re-renders the chip only. The root is a full-bleed layer: this anchor is a flex column (V-U9).</summary>
internal sealed class ClassicStageFaceChip : Component
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
            Height = Stage.Layout.FaceChipH, Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicStageLayerGeometry.FaceChipGap,
            Padding = new Edges4(ClassicStageLayerGeometry.FaceChipPadX, 0f, ClassicStageLayerGeometry.FaceChipPadX, 0f), Corners = Radii.Circle(Stage.Layout.FaceChipH),
            Fill = Ink.Plate, HoverFill = Ink.PlateHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster, BorderWidth = 1f, BorderColor = Ink.Stroke,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = toggle,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                new TextEl(Icons.Equalizer) { Size = ClassicStageLayerGeometry.FaceChipGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink },
                new TextEl(name) { Size = ClassicStageLayerGeometry.FaceChipText, LineHeight = ClassicStageLayerGeometry.FaceChipLine, Weight = ClassicStageLayerGeometry.FaceChipWeight, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                new TextEl(Icons.ChevronDown) { Size = ClassicStageLayerGeometry.FaceChipChevron, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
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
