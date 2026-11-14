// ── Themes/WinUi/Structure/Views/Stage/WinUiStageTopBar.cs — WinUI's `Stage.TopBar` (P6 W4b) ──────────────────────────────────────────
//
// W-Stage / W-Lyrics: three columns on the bar (`WinUiStageLayout.BarH`: 48, 56 at Huge). Left the wordmark (the accent tile, "Wavee", and from
// Standard "Now playing · full screen"); centred the mode SelectorBar on an acrylic pill (Lyrics · Visualizer · Up next · Artist: 38 items with
// the accent pill under the selected word); right the look's words (Huge, Visualizer mode: the face and the layout, each opening the options),
// "Romanization" in Lyrics mode where the lyrics carry a second line (Standard, Huge), the options glyph (the popover's anchor and the
// TeachingTip's) and exit. The Compact SHEET's header (W-Stage-Compact): the exit chevron, the glyph-only pill and "..." (the options), every
// target 40. The bar is chrome that never hides (W-Tiers: the controls are visible at rest), and it carries no veil: the boards draw none.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.WinUi.WinUiStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's stage top bar.</summary>
internal static class WinUiStageTopBar
{
    /// <summary>The registered template of <see cref="ViewKeys.StageTopBar"/>.</summary>
    public static Element Template(StageTopBarProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks;
        var st = props.Stage;
        var set = st.Accent.Value;
        var mode = st.Mode.Value;
        var t = WinUiStageTiers.StageOf(ctx.Tier());
        // The tabs: the stage's FIRST tab wherever WinUI draws Video mode (contract 1.105: "Video" while the playing track's music video plays,
        // "Overview" otherwise: Video mode's page with the audio face in the picture's place), then today's four. The first tab relabels in place on a
        // track change (one item key, its width reserved for the wider label: nothing beside it moves). In Video mode the lit tab is the first in
        // Cinema and the pane's own in Theatre.
        bool videoMode = mode == Stage.Mode.Video;
        var first = st.PictureTab.Value;
        bool videoTabs = first != Stage.PictureTab.None || videoMode;
        bool overview = first == Stage.PictureTab.Overview;
        var modes = videoTabs ? s_videoModes : s_modes;
        var index = h.UseSignal(0);
        h.UseSignalEffect(() => index.SetIfChanged(Math.Max(0, Array.IndexOf(st.PictureTab.Value != Stage.PictureTab.None || st.Mode.Value == Stage.Mode.Video ? s_videoModes : s_modes, LitTab(st)))));
        float firstMinW = t.PillIcons ? 0f : G.FirstTabMinW;
        var parts = h.UseMemo(() =>
        {
            var p = new TemplateParts();
            p.Set<BoxEl>(SelectorBar.PartPill, b => b with { Fill = set.Fill });
            if (firstMinW > 0f) p.Set<BoxEl>(SelectorBar.PartItem, b => b.Key == FirstTabKey ? b with { MinWidth = firstMinW, Justify = FlexJustify.Center } : b);
            return p;
        }, DepKey.From(set.GetHashCode(), firstMinW));
        string[] items = t.PillIcons
            ? (videoTabs ? ["", "", "", "", ""] : ["", "", "", ""])
            : videoTabs ? [Loc.Get(overview ? Strings.Stage.Mode.Overview : Strings.Stage.Mode.Video), Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Visualizer), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)]
            : [Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Visualizer), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)];
        string?[]? icons = t.PillIcons ? (videoTabs ? [overview ? Icons.Album : Icons.Video, Icons.Document, Icons.Equalizer, SegoeGlyphs.Queue, Icons.Contact] : [Icons.Document, Icons.Equalizer, SegoeGlyphs.Queue, Icons.Contact]) : null;
        bool audioFace = st.AudioFace.Value;
        var style = new SelectorBarStyle
        {
            LabelSize = t.PillTextSize, RestColor = Ink.InkSecondary, SelectedColor = Ink.Ink, HoverColor = Ink.Ink, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
            ItemHeight = t.PillItemH, ItemGap = t.PillGap, ItemPadding = new Edges4(t.PillItemPadX, 0f, t.PillItemPadX, 0f),
        };
        var setMode = st.SetMode;
        var overControls = st.OverControls;
        var galleryButton = st.GalleryButton;
        bool optionsOn = st.OptionsOpen.Value;

        // In Video mode the pill stands over the picture, a video hole no acrylic can sample: it takes the smoke plate (the acrylic's fallback) instead
        var pill = new BoxEl
        {
            Key = G.PillKey, Height = G.PillH, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(G.PillPadX, 0f, G.PillPadX, 0f), Shrink = 0f,
            Corners = Radii.CardAll, BorderWidth = 1f, BorderColor = Ink.Stroke,
            Children = [SelectorBar.Create(items, index, onChange: i => setMode(modes[Math.Clamp(i, 0, modes.Length - 1)]), parts: parts, icons: icons, style: style,
                itemKeys: videoTabs ? s_videoTabKeys : null)],
        };
        pill = videoMode ? pill with { Fill = WinUiStageVideoBar.Plate() } : pill with { Acrylic = Ink.Card };

        // the right cluster: the look's words, Romanization, options, exit (the sheet: "..." only). Video mode: "Switch to audio" alone (Cinema ⇄ Theatre
        // and exit are the command bar's over the picture: one home per verb)
        var right = new List<Element>(5);
        // ("Switch to audio" only while a video plays as video: Overview with video off has no video to switch from, and the column stays empty)
        if (videoMode && st.VideosPlay.Value)
            right.Add(t.PillIcons || t.Sheet
                ? ToolTip.Wrap(Glyph(Icons.MusicNote, st.SwitchToAudio, false, null, t) with { Fill = WinUiStageVideoBar.Plate() }, Loc.Get(Strings.Player.SwitchToAudio)) with { Key = "stage:bar:audio" }
                : Word(Icons.MusicNote, Loc.Get(Strings.Player.SwitchToAudio), st.SwitchToAudio, chevron: false, lit: false, t) with { Key = "stage:bar:audio", Fill = WinUiStageVideoBar.Plate() });
        else if (t.LookWords && mode == Stage.Mode.Visualizer)
        {
            right.Add(Word(Icons.Design, Loc.Get(Visualizer.NameKey(st.Kind.Value)), st.OpenOptions, chevron: true, lit: false, t));
            right.Add(Word(Icons.SplitView, Loc.Get(WinUiStageText.LayoutName(st.Effective.Value)), st.OpenOptions, chevron: true, lit: false, t));
        }
        if (!videoMode && t.RomanizationWord && mode == Stage.Mode.Lyrics && st.HasSecondLine())
            right.Add(Word(Icons.Globe, Loc.Get(Strings.Theme.Winui.Stage.Romanization), st.ToggleSecondLine, chevron: false, lit: st.SecondLineOn(), t));
        if (!videoMode && t.Sheet)
            right.Add(ToolTip.Wrap(Glyph(Icons.More, st.OpenOptions, optionsOn, n => galleryButton.SetIfChanged(n), t) with { Key = G.OptionsKey }, Loc.Get(Strings.Stage.Options.Button)));
        else if (!videoMode)
        {
            right.Add(ToolTip.Wrap(Glyph(Icons.Equalizer, st.OpenOptions, optionsOn, n => galleryButton.SetIfChanged(n), t) with { Key = G.OptionsKey }, Loc.Get(Strings.Stage.Options.Button)));   // the TeachingTip's anchor SIGNAL (V-U16)
            right.Add(ToolTip.Wrap(Glyph(Icons.BackToWindow, st.Exit, false, null, t), Loc.Get(Strings.Stage.Exit)));
        }

        // the sheet's exit chevron leads the audio modes; over the picture exit is the command bar's (and the picture is no sheet)
        Element lead = t.Sheet
            ? videoMode ? new BoxEl { HitTestVisible = false } : ToolTip.Wrap(Glyph(Icons.ChevronDown, st.Exit, false, null, t), Loc.Get(Strings.Stage.Exit))
            : Wordmark(st, t, videoMode && !audioFace);

        return new BoxEl
        {
            Grow = 1f, Height = t.BarH, Direction = 0, AlignItems = FlexAlign.Center, Padding = t.BarPad, Gap = Spacing.S,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true, Children = [lead] },
                pill,
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = G.BarButtonGap,
                    Children = right.ToArray(),
                },
            ],
        };
    }

    /// <summary>The selector's modes, tab by tab: today's, and the Video set.</summary>
    static readonly Stage.Mode[] s_modes = [Stage.Mode.Lyrics, Stage.Mode.Visualizer, Stage.Mode.Queue, Stage.Mode.Artist];
    static readonly Stage.Mode[] s_videoModes = [Stage.Mode.Video, Stage.Mode.Lyrics, Stage.Mode.Visualizer, Stage.Mode.Queue, Stage.Mode.Artist];

    /// <summary>The Video set's tab keys: the first tab keeps ONE key under both labels (Video, Overview), so a relabel remounts nothing; the parts
    /// find it by it to reserve the wider label's width.</summary>
    const string FirstTabKey = "stage:tab:video";
    static readonly string?[] s_videoTabKeys = [FirstTabKey, "stage:tab:lyrics", "stage:tab:visualizer", "stage:tab:queue", "stage:tab:artist"];

    /// <summary>The tab that stands lit (subscribes): the mode, or in Video mode's Theatre the tab of the pane beside the picture; Video mode's Cinema
    /// lights the first tab (Video or Overview).</summary>
    static Stage.Mode LitTab(Stage.StageCtx st)
        => st.Mode.Value != Stage.Mode.Video ? st.Mode.Value
         : st.Theatre.Value ? Stage.TheatreRules.TabOf(st.TheatrePane.Value)
         : Stage.Mode.Video;

    /// <summary>The wordmark: the 16 accent tile, "Wavee", and (Standard, Huge) "Now playing · full screen", which gives way first.</summary>
    static Element Wordmark(Stage.StageCtx st, WinUiStageLayout t, bool video)
    {
        var kids = new List<Element>(3)
        {
            new BoxEl { Width = G.BrandMark, Height = G.BrandMark, Corners = G.BrandMarkCorners, Fill = Prop.Bind(st.Slab.Accent), Shrink = 0f },
            new TextEl("Wavee") { Size = G.BrandSize, LineHeight = G.BrandLine, Weight = G.BrandWeight, Color = Ink.Ink, Shrink = 0f },
        };
        if (t.Subtitle)
            kids.Add(new TextEl(Loc.Get(video ? Strings.Player.VideoFullScreen : Strings.Stage.Brand)) { Size = G.BrandSize, LineHeight = G.BrandLine, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = G.BrandGap, MinWidth = 0f, Shrink = 1f, Children = kids.ToArray() };
    }

    /// <summary>A plateless glyph button of the bar (options, exit, the sheet's chevron and "..."): 40 x 36, 40 x 40 in the sheet.</summary>
    static BoxEl Glyph(string glyph, Action onClick, bool lit, Action<NodeHandle>? onRealized, WinUiStageLayout t) => new()
    {
        Width = t.BarButtonW, Height = t.BarButtonH, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = lit ? Ink.GlassHover : ColorF.Transparent, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = G.BarGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }],
    };

    /// <summary>A word button of the bar on the subtle plate (W-Lyrics "Romanization", W-Stage-Huge "Mosaic ▾", "Card ▾"): 36 tall, 10 / 12 at the sides.</summary>
    static BoxEl Word(string glyph, string text, Action onClick, bool chevron, bool lit, WinUiStageLayout t)
    {
        var kids = new List<Element>(3)
        {
            new TextEl(glyph) { Size = G.WordGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink },
            new TextEl(text) { Size = G.WordSize, LineHeight = G.WordLine, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
        };
        if (chevron) kids.Add(new TextEl(Icons.ChevronDown) { Size = G.WordChevron, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary });
        return new BoxEl
        {
            Height = t.BarButtonH, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.WordGap, Padding = new Edges4(G.WordPadX, 0f, G.WordPadX, 0f), Shrink = 1f, MinWidth = 0f,
            Corners = Radii.ControlAll, Fill = lit ? Ink.GlassHover : Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
            Children = kids.ToArray(),
        };
    }
}

/// <summary>The stage's words WinUI names its own way (W-Stage-Huge4K's layout segments: Card, Large art, Centered, Artist).</summary>
internal static class WinUiStageText
{
    /// <summary>The loc key of a layout's short name.</summary>
    public static string LayoutName(Stage.VizLayout layout) => layout switch
    {
        Stage.VizLayout.LargeArt => Strings.Theme.Winui.Stage.LayoutLarge,
        Stage.VizLayout.Centered => Strings.Theme.Winui.Stage.LayoutCentered,
        Stage.VizLayout.Artist => Strings.Theme.Winui.Stage.LayoutArtist,
        _ => Strings.Theme.Winui.Stage.LayoutCard,
    };
}
