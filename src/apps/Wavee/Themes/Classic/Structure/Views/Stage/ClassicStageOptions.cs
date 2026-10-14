// ── Themes/Classic/Structure/Views/Stage/ClassicStageOptions.cs — Classic's `Stage.Options` (themes plan 6.1, T7a1) ──────────────
//
// From `Shell/Stage.Options.UI.cs`: the "Full screen" options popover (design A, two tabs), opened from the top bar's sliders button:
//   Visualizer tab: Now showing (poster) · group chips · grid of every visualizer (posters) · "Open gallery" (G)
//   Layout tab:     four layout radio rows with schematic previews · spectrum under the cover (Large / Centered) ·
//                   artist photo options (lyrics over the photo, slow pan and zoom, dimming)
//
// Spec: PopoverTabsVisualizer.dc.html / PopoverTabsLayout.dc.html (440 wide · 20 padding · tabs with a 3 px accent underline)
//
// The core opener (`Stage.OptionsContent`) mounts this view inside the overlay layer, outside the stage subtree, and re-provides
// the stage context. Every control takes the context's SIGNALS (or an adapter mirrored from them) and writes through the
// `IStageSettingsVerbs` (the gallery's SettingRow contract). Tabs are real tabs (Role Tab, one roving tab stop, left / right move).
// The layout rows and the group chips are RadioButtons groups (roving arrows, selection follows focus); the visualizer grid is ONE
// focusable box whose arrows move a cursor and Enter / Space pick, like the gallery. Every visualizer thumbnail is the gallery's
// POSTER (Lod.Poster: bound to the stage's poster slab, nothing ticks). The user-facing layout names changed (Card is now
// "Visualizer first"); the persisted values did not.
//
// The popover's own state (the tab, the grid cursor, the group filter, the mirrored layout / spectrum / dimming cells) lives in the
// slot's hook cells, so it survives the template's re-runs and goes with the popover.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using G = Wavee.Themes.Classic.ClassicStageOptionsGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's stage options popover.</summary>
internal static class ClassicStageOptions
{
    static readonly Slider.SliderOptions s_dimOptions = new()
    {
        Min = G.DimMin, Max = G.DimMax, Step = G.DimStep, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>The popover's state: hook cells of the slot, gathered so the pieces below can take them as one argument.</summary>
    sealed class Cells(Signal<int> spectrum, Signal<int> cursor, Signal<int> tab, Signal<int> group, Signal<int> layoutSel, FloatSignal dim)
    {
        public readonly Signal<int> Spectrum = spectrum, Cursor = cursor, Tab = tab, Group = group, LayoutSel = layoutSel;
        public readonly FloatSignal Dim = dim;
    }

    /// <summary>The registered template of <see cref="ViewKeys.StageOptions"/>.</summary>
    public static Element Template(StageOptionsProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks;
        // the cells are created in a fixed order, once per popover
        var cells = new Cells(h.UseSignal(0), h.UseSignal(-1), h.UseSignal(0), h.UseSignal(0), h.UseSignal(0), h.UseFloatSignal(Stage.HeroRules.DefaultDim));
        // the flyout lives in the overlay layer: the stage context arrives on the props (the opener wrapped the slot in the provider)
        var st = props.Stage;
        var verbs = props.Verbs;
        var L = st.Layout.Value;
        var accent = st.Accent.Value;
        int tab = cells.Tab.Value;
        h.UseSignalEffect(() => cells.Spectrum.SetIfChanged((int)st.SpectrumPref.Value));
        h.UseSignalEffect(() => cells.LayoutSel.SetIfChanged((int)st.LayoutPref.Value));
        h.UseSignalEffect(() => cells.Dim.SetIfChanged(st.HeroDim.Value));

        float width = MathF.Min(G.PanelMaxWidth, L.W - G.PanelEdgeInset);
        float height = MathF.Min(G.PanelMaxHeight, L.H - G.PanelEdgeInset);
        var body = tab == 0 ? VisualizerTab(st, verbs, cells, accent, width) : LayoutTab(st, verbs, cells, L, accent, width);
        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Direction = 1, Gap = G.HeaderGap, Padding = G.HeaderPadding, Shrink = 0f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Stage.Options.Title)) { Size = G.TitleSize, LineHeight = G.TitleLine, Weight = G.WeightSemiBold, Color = Ink.Ink },
                    Tabs(cells, accent),
                ],
            },
            body,
        };
        if (tab == 0) kids.Add(Footer(st, props.Close));
        return new BoxEl
        {
            Width = width, Height = height, Direction = 1, Corners = Radii.CardAll,
            Acrylic = Ink.Card, Shadow = Elevation.Flyout, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    // ── the tablist ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Tabs(Cells cells, AccentSet accent)
    {
        int sel = cells.Tab.Value;
        string[] names = [Loc.Get(Strings.Stage.Options.TabViz), Loc.Get(Strings.Stage.Options.TabLayout)];
        var items = new Element[G.TabCount];
        for (int i = 0; i < G.TabCount; i++)
        {
            int index = i;
            bool on = i == sel;
            Element label = new TextEl(names[i]) { Size = G.BodySize, LineHeight = G.BodyLine, Weight = on ? G.WeightSemiBold : G.WeightRegular, Color = on ? Ink.Ink : Ink.InkSecondary };
            Element head = i == 0
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = G.TabHeadGap, HitTestVisible = false,
                    Children =
                    [
                        label,
                        new BoxEl
                        {
                            Padding = G.CountBadgePadding, Corners = G.CountBadgeCorners, Fill = on ? accent.Fill : Ink.GlassHover,
                            Children = [new TextEl(Visualizer.Catalog.ShownCount.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Weight = G.WeightSemiBold, Color = on ? accent.Ink : Ink.InkSecondary }],
                        },
                    ],
                }
                : label;
            items[i] = new BoxEl
            {
                Direction = 1, AlignItems = FlexAlign.Center, Gap = G.TabGap, Padding = G.TabPadding, HoverFill = Ink.GlassHover,
                Corners = G.TabCorners, Role = AutomationRole.Tab, TabStop = on, Cursor = CursorId.Hand,
                OnClick = () => cells.Tab.SetIfChanged(index),
                OnKeyDown = e =>
                {
                    if (e.Handled || e.Ctrl || e.Alt) return;
                    int next = e.KeyCode == Keys.Right || e.KeyCode == Keys.End ? 1 : e.KeyCode == Keys.Left || e.KeyCode == Keys.Home ? 0 : -1;
                    if (next < 0) return;
                    e.Handled = true; cells.Tab.SetIfChanged(next);
                },
                Children = [head, new BoxEl { Height = G.TabUnderlineHeight, Corners = G.TabUnderlineCorners, Fill = on ? accent.Fill : ColorF.Transparent, AlignSelf = FlexAlign.Stretch }],
            };
        }
        return new BoxEl { Direction = 0, Gap = G.TabsGap, Shrink = 0f, Children = items };
    }

    // ── Visualizer tab ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Poster(Visualizer.Kind kind, float w, float h, ColorF bg, Visualizer.Slab slab, Visualizer.Palette pal) => new BoxEl
    {
        Width = w, Height = h, Corners = G.PosterCorners, ClipToBounds = true, ZStack = true, HitTestVisible = false, Fill = bg, Shrink = 0f,
        Children = [new BoxEl { Key = "poster", Width = w, Height = h, HitTestVisible = false,
            Children = [Visualizer.Face(kind, slab, in pal, new Visualizer.FaceSpec(w, h, Preview: true, CoverUrl: null, Poster: true))] }],
    };

    static Element VisualizerTab(Stage.StageCtx st, IStageSettingsVerbs verbs, Cells cells, AccentSet accent, float width)
    {
        var current = st.Kind.Value;
        var pal = st.Palette.Value;
        var slab = st.PosterSlab;
        float wash = Ink.SelectedTileWash;
        ColorF bg = ColorF.Lerp(Ink.Veil, slab.A.Value, wash);

        // the group filter: All + each catalog group (the chips are one RadioButtons group)
        var groups = Visualizer.Catalog.Groups;
        int g = Math.Clamp(cells.Group.Value, 0, groups.Length);
        var list = new List<Visualizer.Kind>(Visualizer.Catalog.ShownCount);
        foreach (var k in Visualizer.Catalog.Shown)
            if (g == 0 || Visualizer.Catalog.GroupOf(k) == groups[g - 1]) list.Add(k);
        string[] chipNames = new string[groups.Length + 1];
        chipNames[0] = Loc.Get(Strings.Stage.Options.GroupAll);
        for (int i = 0; i < groups.Length; i++) chipNames[i + 1] = Loc.Get(GroupName(groups[i]));

        var chips = new BoxEl
        {
            Shrink = 0f, Padding = G.ChipsPadding,
            Children =
            [
                RadioButtons.Create(chipNames.Length, i =>
                {
                    bool on = i == g;
                    return new BoxEl
                    {
                        MinHeight = G.ChipMinHeight, Padding = G.ChipPadding, Corners = G.ChipCorners, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                        Fill = on ? accent.Fill : ColorF.Transparent, BorderWidth = on ? 0f : 1f, BorderColor = Ink.Stroke,
                        Children = [new TextEl(chipNames[i]) { Size = G.ChipSize, LineHeight = G.ChipLine, Color = on ? accent.Ink : Ink.Ink, Weight = on ? G.WeightSemiBold : G.WeightRegular }],
                    };
                }, cells.Group, _ => cells.Cursor.SetIfChanged(-1), maxColumns: chipNames.Length, style: PlainRadio, parts: ChipParts),
            ],
        };

        float tileW = MathF.Floor((width - G.GridPadX - G.GridGap * (G.GridCols - 1)) / G.GridCols);
        float tileH = MathF.Round(tileW * G.PosterAspectH / G.PosterAspectW);
        int cursor = cells.Cursor.Value;
        var rows = new List<Element>(8);
        for (int r = 0; r < list.Count; r += G.GridCols)
        {
            var row = new Element[Math.Min(G.GridCols, list.Count - r)];
            for (int c = 0; c < row.Length; c++)
            {
                int index = r + c;
                var kind = list[index];
                bool on = Visualizer.Catalog.Successor(current) == kind;
                row[c] = new BoxEl
                {
                    Width = tileW, Direction = 1, Gap = G.TileGap, Padding = G.TileFramePadding, Corners = G.TileCorners, Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = false,
                    BorderWidth = G.TileBorder, BorderColor = on ? accent.Fill : cursor == index ? Ink.Ink : ColorF.Transparent, HoverFill = Ink.GlassHover,
                    OnClick = () => { cells.Cursor.SetIfChanged(index); verbs.PickFace(kind); },
                    Children =
                    [
                        Poster(kind, tileW - G.TilePosterInsetW, tileH - G.TilePosterInsetH, bg, slab, pal),
                        new TextEl(Loc.Get(Visualizer.NameKey(kind))) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Weight = G.WeightSemiBold, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = G.TileNameMargin },
                    ],
                };
            }
            rows.Add(new BoxEl { Direction = 0, Gap = G.GridGap, Shrink = 0f, Children = row });
        }
        int count = list.Count;
        var grid = new BoxEl
        {
            Direction = 1, Gap = G.GridGap, Padding = G.GridPadding, Focusable = true,
            OnFocusChanged = got => { if (got && cells.Cursor.Peek() < 0) cells.Cursor.Value = Math.Max(0, list.IndexOf(Visualizer.Catalog.Successor(st.Kind.Peek()))); },
            OnKeyDown = e =>
            {
                if (e.Handled || e.Ctrl || e.Alt || count == 0) return;
                int cur = Math.Clamp(cells.Cursor.Peek(), 0, count - 1);
                if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; if (!e.IsRepeat) verbs.PickFace(list[cur]); return; }
                int next = e.KeyCode switch
                {
                    Keys.Left => Math.Max(0, cur - 1), Keys.Right => Math.Min(count - 1, cur + 1),
                    Keys.Up => cur - G.GridCols >= 0 ? cur - G.GridCols : cur, Keys.Down => cur + G.GridCols < count ? cur + G.GridCols : cur,
                    Keys.Home => 0, Keys.End => count - 1, _ => -1,
                };
                if (next < 0) return;
                e.Handled = true; cells.Cursor.SetIfChanged(next);
            },
            Children = rows.ToArray(),
        };

        var now = new BoxEl
        {
            Direction = 0, Gap = G.NowGap, AlignItems = FlexAlign.Center, Padding = G.NowPadding, Shrink = 0f,
            Children =
            [
                Poster(current, G.NowPosterWidth, G.NowPosterHeight, bg, slab, pal),
                new BoxEl
                {
                    Direction = 1, Gap = G.NowTextGap, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.Options.NowShowing)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary },
                        new TextEl(Loc.Get(Visualizer.NameKey(current))) { Size = G.TitleSize, LineHeight = G.TitleLine, Weight = G.WeightSemiBold, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(Loc.Get(GroupName(Visualizer.Catalog.GroupOf(Visualizer.Catalog.Successor(current)))) + " · "
                            + Loc.Get(Visualizer.Catalog.PaceOf(current) == Visualizer.Pace.Lively ? Strings.Stage.VizEnergy.Lively : Strings.Stage.VizEnergy.Calm)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary },
                    ],
                },
            ],
        };
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, Direction = 1, Gap = G.VizBodyGap, Padding = G.VizBodyPadding,
            Children =
            [
                now, chips,
                new ScrollEl { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageoptionsviz", Content = grid },
            ],
        };
    }

    static string GroupName(Visualizer.Group g) => g switch
    {
        Visualizer.Group.Lyrics => Strings.Stage.VizGroup.Lyrics, Visualizer.Group.Classics => Strings.Stage.VizGroup.Classics,
        Visualizer.Group.Zune => Strings.Stage.VizGroup.Zune, Visualizer.Group.ITunes => Strings.Stage.VizGroup.Itunes, _ => Strings.Stage.VizGroup.Fluent,
    };

    /// <summary>The footer: the promise, and the gallery (the same switch G makes: back to Card, then the pane opens).</summary>
    static Element Footer(Stage.StageCtx st, Action close)
    {
        void OpenGallery()
        {
            st.OpenGallery();                  // the one gallery rule (StageModel.PlanGalleryOpen)
            close();
        }
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = G.FooterGap, Shrink = 0f, Padding = G.FooterPadding,
            Children =
            [
                new TextEl(Loc.Get(Strings.Stage.Options.Footer)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = G.FooterButtonGap, MinHeight = G.FooterButtonMinHeight, Padding = G.FooterButtonPadding, Shrink = 0f, Corners = Radii.ControlAll,
                    Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = OpenGallery,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.Options.OpenGallery)) { Size = G.BodySize, LineHeight = G.BodyLine, Weight = G.WeightSemiBold, Color = Ink.Ink },
                        new BoxEl
                        {
                            Padding = G.KeyCapPadding, Corners = G.KeyCapCorners, BorderWidth = 1f, BorderColor = Ink.InkSecondary, HitTestVisible = false,
                            Children = [new TextEl("G") { Size = G.KeyCapSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary }],
                        },
                    ],
                },
            ],
        };
    }

    // ── Layout tab ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element LayoutTab(Stage.StageCtx st, IStageSettingsVerbs verbs, Cells cells, Stage.Layout L, AccentSet accent, float width)
    {
        int layout = (int)st.LayoutPref.Value;
        bool compact = L.Aspect == Stage.Aspect.Compact;
        bool reduced = Design.Reduced;
        var pal = st.Palette.Value;
        var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };
        var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = G.ToggleMinWidth, OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary, Foreground = Ink.Ink };

        var kids = new List<Element>(5)
        {
            new TextEl(Loc.Get(Strings.Stage.Options.LayoutHelp)) { Size = G.HelpSize, LineHeight = G.HelpLine, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, Shrink = 0f },
        };
        if (!compact)
        {
            string[] names = [Loc.Get(Strings.Stage.Layout.Card), Loc.Get(Strings.Stage.Layout.Large), Loc.Get(Strings.Stage.Layout.Centered), Loc.Get(Strings.Stage.Layout.Artist)];
            string[] subs = [Loc.Get(Strings.Stage.LayoutSub.Card), Loc.Get(Strings.Stage.LayoutSub.Large), Loc.Get(Strings.Stage.LayoutSub.Centered), Loc.Get(Strings.Stage.LayoutSub.Artist)];
            kids.Add(RadioButtons.Create(4, i => LayoutRow(i, names[i], subs[i], i == layout, accent, pal), cells.LayoutSel, i => verbs.SetLayout(i), header: null, maxColumns: 1, style: PlainRadio, parts: RowParts));
            if (layout is (int)Stage.VizLayout.LargeArt or (int)Stage.VizLayout.Centered)
                kids.Add(Section(Loc.Get(Strings.Stage.Options.SpectrumUnder), Segmented.Create(
                    [new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Bars)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Ring)),
                     new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Line)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Off))],
                    cells.Spectrum, i => verbs.SetSpectrum(i))));
        }
        if (compact || layout == (int)Stage.VizLayout.Artist)
            kids.Add(new BoxEl
            {
                Direction = 1, Gap = G.HeroGap, Padding = G.HeroPadding, Shrink = 0f,
                Children =
                [
                    Row(Loc.Get(Strings.Stage.Options.HeroLyrics), ToggleSwitch.Create(st.HeroLyricsPref, on => verbs.SetHeroLyrics(on), style: toggleStyle)),
                    Row(Loc.Get(Strings.Stage.Options.HeroMotion), ToggleSwitch.Create(st.HeroMotion, on => verbs.SetHeroMotion(on), isEnabled: !reduced, style: toggleStyle),
                        reduced ? Loc.Get(Strings.Stage.Options.HeroMotionReduced) : null),
                    new BoxEl
                    {
                        Direction = 1, Gap = G.HeroDimGap,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.Options.HeroDim)) { Size = G.BodySize, LineHeight = G.BodyLine, Color = Ink.Ink },
                            Slider.Create(cells.Dim, v => verbs.SetHeroDim((int)MathF.Round(v / G.DimStep) * (int)G.DimStep), s_dimOptions, length: width - G.HeroSliderInset, thickness: G.HeroSliderThickness, style: sliderStyle),
                        ],
                    },
                    new TextEl(Loc.Get(Strings.Stage.Options.HeroNote)) { Size = G.CaptionSize, LineHeight = G.NoteLine, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap },
                ],
            });
        return new ScrollEl
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageoptionslayout",
            Content = new BoxEl { Direction = 1, Gap = G.LayoutGap, Padding = G.LayoutPadding, Children = kids.ToArray() },
        };
    }

    static readonly RadioButton.Style PlainRadio = RadioButton.DefaultStyle with { ShowGlyph = false, MinWidth = 0f, MinHeight = 0f, FocusVisualMargin = G.RadioFocusMargin };
    static readonly TemplateParts ChipParts = new()
    {
        [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = G.ChipGridGap },
        [RadioButtons.PartColumn] = c => c with { Shrink = 0f },
    };
    static readonly TemplateParts RowParts = new()
    {
        [RadioButtons.PartGrid] = g => g with { AlignItems = FlexAlign.Stretch },
        [RadioButtons.PartColumn] = c => c with { Grow = 1f, Basis = 0f, MinWidth = 0f },
        [RadioButton.PartRoot] = r => r with { Grow = 1f, MinWidth = 0f },
    };

    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = G.SectionGap, Shrink = 0f, Padding = G.SectionPadding,
        Children = [new TextEl(title) { Size = G.BodySize, LineHeight = G.BodyLine, Weight = G.WeightSemiBold, Color = Ink.Ink }, body],
    };

    static Element Row(string label, Element control, string? note = null) => new BoxEl
    {
        Direction = 1, Gap = G.RowGap, Shrink = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, MinHeight = G.RowMinHeight,
                Children = [new TextEl(label) { Size = G.BodySize, LineHeight = G.BodyLine, Color = Ink.Ink }, control],
            },
            .. (note is null ? Array.Empty<Element>() : [new TextEl(note) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary }]),
        ],
    };

    /// <summary>One layout radio row: an 84x48 schematic over the visualizer, the name and what it does; a 2 px accent
    /// border and a check glyph when selected (never colour alone).</summary>
    static Element LayoutRow(int index, string name, string sub, bool on, AccentSet accent, Visualizer.Palette pal)
    {
        ColorF viz = ColorF.Lerp(Ink.Veil, pal.A, G.ThumbPlateMix);
        Element thumb = new BoxEl
        {
            Width = G.ThumbWidth, Height = G.ThumbHeight, Corners = G.ThumbCorners, Fill = index == 3 ? Ink.InkSecondary with { A = G.ThumbArtistPlateAlpha } : viz, ClipToBounds = true, ZStack = true, HitTestVisible = false, Shrink = 0f,
            Children = index switch
            {
                1 => [Dot(G.LargeDot, accent.Fill)],
                2 => [Dot(G.CenteredDot, accent.Fill)],
                3 => [Dot(G.ArtistDot, Ink.Ink with { A = G.ThumbArtistDotAlpha })],
                _ => [Dot(G.CardDot, accent.Fill)],
            },
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = G.LayoutRowGap, Padding = G.LayoutRowPadding, Corners = Radii.ControlAll, HitTestVisible = false,
            Fill = on ? accent.Fill with { A = G.LayoutRowSelectedAlpha } : Ink.GlassRest,
            BorderWidth = on ? G.LayoutRowBorderOn : G.LayoutRowBorderOff, BorderColor = on ? accent.Fill : Ink.Stroke,
            Children =
            [
                thumb,
                new BoxEl
                {
                    Direction = 1, Gap = G.LayoutRowTextGap, Grow = 1f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = G.LayoutRowTitleGap,
                            Children =
                            [
                                new TextEl(name) { Size = G.BodySize, LineHeight = G.BodyLine, Weight = G.WeightSemiBold, Color = Ink.Ink },
                                .. (on ? [new TextEl(Icons.Accept) { Size = G.CheckSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }] : Array.Empty<Element>()),
                            ],
                        },
                        new TextEl(sub) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap },
                    ],
                },
            ],
        };
    }

    static Element Dot(ClassicStageThumbDot d, ColorF fill) => new BoxEl
    {
        Width = d.W, Height = d.H, Corners = G.ThumbDotCorners, Fill = fill, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(d.X, d.Y, 0f, 0f),
    };
}
