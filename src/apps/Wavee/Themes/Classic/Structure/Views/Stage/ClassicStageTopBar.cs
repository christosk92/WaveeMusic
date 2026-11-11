// ── Themes/Classic/Structure/Views/Stage/ClassicStageTopBar.cs — Classic's `Stage.TopBar` (themes plan 6.1, T7a1) ───────────
//
// From `Shell\Stage.UI.cs`'s `TopBar` component: 48 DIP, brand · the SelectorBar (pill tinted through TemplateParts) · the options
// toggle · exit. The tabs lead with the stage's first tab (`StageCtx.PictureTab`, contract 1.105): Video while the playing track's video plays (the
// board "Classic · Cinema"), Overview otherwise (Video mode's page with the audio face in the picture's place); it relabels in place in a slot that
// reserves the wider of the two labels, so no other tab moves. In Video mode (either label) the options toggle and exit
// give way to "Switch to audio" alone: Cinema ⇄ Theatre and Exit are the control bar's over the picture (`ClassicStageVideoBar`, one home per
// verb). It fills its wrapper (Grow); the root places it under the chrome's Show. The mode, the layout class and the accent are the
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
using Wavee.Theming.Roles;
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
        // The tabs: the first tab (Video while the picture plays, else Overview), then today's four; today's four alone where the structure would
        // draw no Video mode. In Video mode the tab that stands lit is the first in Cinema and the panel's own tab in Theatre (Lyrics or Up next
        // beside the picture or the audio face).
        bool videoMode = st.Mode.Value == Stage.Mode.Video;
        var first = st.PictureTab.Value;
        bool videoTabs = first != Stage.PictureTab.None;
        bool picture = first == Stage.PictureTab.Video;
        bool audioFace = st.AudioFace.Value;
        // the bar's arm: where it stands over the AUDIO FACE's colour field (Cinema: the field fills the stage) the field decides it, the theme kind
        // does not (the face's rule, as its control bar and Cinema's shelf: a deep field on the light theme reads with the dark arm's veil and
        // light inks); in Theatre the bar stands over the page's floor, not the field (the field is the 16:9 box under it), and over a picture or
        // the audio stage, so the theme's arm there
        var armKind = audioFace && !st.Theatre.Value ? ClassicStageAudioFaceGeometry.FieldArm(st.BasePalette.Value) : Tok.Theme;
        var arm = Ink.Arm(armKind);
        var modes = videoTabs ? s_videoModes : s_modes;
        var index = h.UseSignal(0);
        h.UseSignalEffect(() => index.SetIfChanged(Math.Max(0, Array.IndexOf(st.PictureTab.Value != Stage.PictureTab.None ? s_videoModes : s_modes, LitTab(st)))));
        string videoLabel = Loc.Get(Strings.Stage.Mode.Video), overviewLabel = Loc.Get(Strings.Stage.Mode.Overview);
        // the first tab's slot holds the wider of its two labels (a ghost of each under the shown one), so a relabel on a track change moves no tab
        bool reserve = videoTabs && !L.IconOnlySelector;
        var parts = h.UseMemo(() =>
        {
            var p = new TemplateParts();
            p.Set<BoxEl>(SelectorBar.PartPill, b => b with { Fill = set.Fill });
            if (reserve) p.Set<BoxEl>(SelectorBar.PartItem, b => b.Key == VideoTabKey ? Reserved(b, videoLabel, overviewLabel) : b);
            return p;
        }, DepKey.From(set.GetHashCode(), reserve ? 1 : 0, videoLabel.GetHashCode(), overviewLabel.GetHashCode()));
        string[] items = L.IconOnlySelector ? (videoTabs ? ["", "", "", "", ""] : ["", "", "", ""])
            : videoTabs ? [picture ? videoLabel : overviewLabel, Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Visualizer), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)]
            : [Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Visualizer), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)];
        // the first tab's glyph: the film for Video, the album for Overview (the cover and the lyrics; MusicNote is "Switch to audio"'s)
        string?[] icons = videoTabs ? [picture ? Icons.Video : Icons.Album, Icons.Document, Icons.Equalizer, SegoeGlyphs.Queue, Icons.Contact] : [Icons.Document, Icons.Equalizer, SegoeGlyphs.Queue, Icons.Contact];
        // Video mode's one button: [Switch to audio], labelled where the allocator says the column holds it. Cinema ⇄ Theatre and Exit are the
        // control bar's over the picture (YouTube's bottom right: one home per verb, `ClassicStageVideoBar`); the audio modes keep [options] [Exit].
        bool audioLabel = videoMode && L.VideoAudioLabelShown(modes.Length);
        bool exitLabel = !videoMode && L.ExitLabelShown(modes.Length);
        var style = new SelectorBarStyle
        {
            RestColor = arm.InkSecondary, SelectedColor = arm.Ink, HoverColor = arm.Ink, HoverFill = arm.GlassHover, PressedFill = arm.GlassPressed,
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
            Gradient = GradientDown(new GradientStop(0f, Shade(arm, ClassicStageGeometry.TopBarVeilA)), new GradientStop(1f, Shade(arm, 0f))),
            // the chrome is held by the CONTROLS (the tabs, the buttons), not by the bar: a pointer resting on the brand or the empty bar
            // idles out like one resting on the picture (Held)
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
                        new TextEl("Wavee") { Size = ClassicStageGeometry.BrandSize, LineHeight = ClassicStageGeometry.BrandLine, Weight = ClassicStageGeometry.BrandWeight, Color = arm.Ink, Shrink = 0f },
                        new TextEl(Loc.Get(videoMode && !audioFace ? Strings.Player.VideoFullScreen : Strings.Stage.Brand))
                        {
                            Size = ClassicStageGeometry.BrandSize, LineHeight = ClassicStageGeometry.BrandLine, Color = arm.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            MinWidth = 0f, Shrink = 1f, Visible = !L.IconOnlySelector,
                        },
                    ],
                },
                Held(SelectorBar.Create(items, index, onChange: i => setMode(modes[Math.Clamp(i, 0, modes.Length - 1)]), parts: parts, icons: icons, style: style,
                    itemKeys: videoTabs ? s_videoTabKeys : null), overControls),
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    Children =
                    [
                        // the buttons hug their own row (the column's empty lead is the bar's, which holds nothing)
                        new BoxEl
                        {
                            Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Stage.Layout.BarButtonGap,
                            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
                            Children =
                            [
                                // Video mode: "Switch to audio" (the video off, the stage on the cover and lyrics) where the options toggle stands (the
                                // visualizer's options have nothing to set over the picture); the docked video menu's "Turn off video" is this verb
                                // Overview with the video off has no video to switch from: the column stays empty (the control bar holds the rest)
                                videoMode
                                    ? !st.VideosPlay.Value ? new BoxEl { Visible = false }
                                    : audioLabel
                                        ? Button.Create(Loc.Get(Strings.Player.SwitchToAudio), toAudio, ButtonAppearance.Subtle, ControlSize.Small, glyph: Icons.MusicNote,
                                            style: armKind == Tok.Theme ? null : OnArm(arm)) with { Shrink = 0f, HitSlop = ClassicStageGeometry.LabelledButtonSlop, Key = "stage:bar:audio", Enter = s_formSwap, Exit = s_formSwap, Transition = ClassicTierMotion.SwapFadeMotion }
                                        : ToolTip.Wrap(BarGlyph(Icons.MusicNote, toAudio, lit: false, onRealized: null, targetH, arm), Loc.Get(Strings.Player.SwitchToAudio)) with { Key = "stage:bar:audio", Enter = s_formSwap, Exit = s_formSwap, Transition = ClassicTierMotion.SwapFadeMotion }
                                    : ToolTip.Wrap(BarGlyph(Icons.Equalizer, openOptions, lit: optionsOn, onRealized: n => galleryButton.SetIfChanged(n), targetH, arm), Loc.Get(Strings.Stage.Options.Button)),   // the TeachingTip's anchor SIGNAL (V-U16)
                                // the audio modes' way out (Video mode's is the control bar's). Labelled only where the allocator says the column holds it
                                // (never "Ex…"); else the glyph with the label as its tooltip.
                                videoMode
                                    ? new BoxEl { Visible = false }
                                    : exitLabel
                                        ? Button.Create(Loc.Get(Strings.Stage.Exit), exit, ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.BackToWindow) with { Shrink = 0f }
                                        : ToolTip.Wrap(BarGlyph(Icons.BackToWindow, exit, lit: false, onRealized: null, targetH, arm), Loc.Get(Strings.Stage.Exit)),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary><paramref name="control"/> holding the chrome while the pointer is anywhere over it (its subtree's enter and exit), in a box that
    /// hugs it: the hold is the control's, never the bar's or the card's empty area (the top bar's and the transport card's).</summary>
    internal static BoxEl Held(Element control, Action<bool> overControls) => new()
    {
        Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
        Children = [control],
    };

    /// <summary>"Switch to audio" labelled or as its glyph (the column's room at a crossing): the two forms cross-fade as the theme's declared swap.</summary>
    static EnterExit s_formSwap => ClassicTierMotion.SwapFade;

    /// <summary>The selector's modes, tab by tab: today's, and the Video set (contract 1.95).</summary>
    static readonly Stage.Mode[] s_modes = [Stage.Mode.Lyrics, Stage.Mode.Visualizer, Stage.Mode.Queue, Stage.Mode.Artist];
    static readonly Stage.Mode[] s_videoModes = [Stage.Mode.Video, Stage.Mode.Lyrics, Stage.Mode.Visualizer, Stage.Mode.Queue, Stage.Mode.Artist];

    /// <summary>The Video set's tab keys (the first tab's, Video or Overview, is how the parts find it to reserve its slot; one key for both labels,
    /// so a relabel never remounts it).</summary>
    const string VideoTabKey = "stage:tab:video";
    static readonly string?[] s_videoTabKeys = [VideoTabKey, "stage:tab:lyrics", "stage:tab:visualizer", "stage:tab:queue", "stage:tab:artist"];

    /// <summary>The tab that stands lit (subscribes): the mode, or in Video mode's Theatre the tab of the panel beside the picture (or the audio
    /// face), or in Cinema the first tab (Video or Overview).</summary>
    static Stage.Mode LitTab(Stage.StageCtx st)
        => st.Mode.Value != Stage.Mode.Video ? st.Mode.Value
         : st.Theatre.Value ? Stage.TheatreRules.TabOf(st.TheatrePane.Value) : Stage.Mode.Video;

    /// <summary>The first tab's item with its label box over two invisible ghosts of it, one per label (<paramref name="a"/>, <paramref name="b"/>)
    /// at the selected weight: the item is as wide as the wider form whichever it shows, and the shown label stays centred in it. The control's
    /// own label box (its states, its inks) is untouched.</summary>
    static BoxEl Reserved(BoxEl item, string a, string b)
    {
        if (item.Children is not [BoxEl content, .. var rest] || content.Children is not [.., TextEl label]) return item;
        BoxEl Ghost(string text) => content with
        {
            Opacity = 0f, HitTestVisible = false, Key = null,
            Children = [.. content.Children[..^1], label with { Text = text, Weight = ClassicStageGeometry.SelectorWeight }],
        };
        var slot = new BoxEl
        {
            ZStack = true,
            Children = [Ghost(a), Ghost(b), content with { AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center }],
        };
        return item with { Children = [slot, .. rest] };
    }

    /// <summary>A 40×36 glass glyph button of the top bar (the options toggle, the icon-only exit); 40×40 on a Compact window.</summary>
    static BoxEl BarGlyph(string glyph, Action onClick, bool lit, Action<NodeHandle>? onRealized, float targetH, in StageArmView arm) => new()
    {
        Width = Stage.Layout.BarButtonW, Height = MathF.Max(ClassicStageGeometry.BarButtonHeight, targetH), Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = lit ? arm.GlassHover : arm.GlassRest, HoverFill = arm.GlassHover, PressedFill = arm.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = ClassicStageGeometry.BarGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = arm.Ink }],
    };

    /// <summary>The veil of <paramref name="arm"/> at alpha <paramref name="a"/>.</summary>
    static ColorF Shade(in StageArmView arm, float a) => arm.Veil with { A = a };

    /// <summary>"Switch to audio"'s subtle style on an arm that is not the theme's (the audio face's field decided it): the label, its glyph and its
    /// interaction plates in that arm's inks, so it reads on the field the bar stands on.</summary>
    static Button.Style OnArm(in StageArmView arm) => Button.DefaultStyle(ButtonAppearance.Subtle, ControlSize.Small) with
    {
        Foreground = arm.Ink, HoverForeground = arm.Ink, PressedForeground = arm.InkSecondary, DisabledForeground = arm.InkTertiary,
        HoverBackground = arm.GlassHover, PressedBackground = arm.GlassPressed,
    };
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
