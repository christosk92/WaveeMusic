// ── Themes/Zune/Structure/Views/Stage/ZuneStageOptions.cs — Zune's `Stage.Options` (P5 Z4b) ────────────────────────────────────────
//
// The full-screen options popover the "options" and "layout · …" words open. No Zune board draws it, so it is built from Zune's parts (owner
// Q18): a flat raised plate, square; the title in the group voice (heading case); the two tabs as pivot words (350 at rest, 600 selected,
// lowercase, no underline):
//   visualizer   now showing (poster) · the group chips (square, the accent fill on the picked one) · the poster grid · "open gallery" (G)
//   layout       four layout rows (square, a schematic, the accent border and a check on the picked one) · the spectrum under the cover
//                (Large art, Centered) · the artist photo's options (lyrics over the photo, slow pan and zoom, dimming)
// The behaviour is the stage's: the core opener mounts this view in the overlay layer and re-provides the stage context; every control
// takes the context's signals (or a mirrored cell) and writes through `IStageSettingsVerbs`; the tabs are tabs (one stop, left / right),
// the chips and the layout rows RadioButtons groups, the grid one focusable box whose arrows move a cursor. The popover's own state lives in
// the slot's hook cells.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage options popover.</summary>
internal static class ZuneStageOptions
{
    static readonly Slider.SliderOptions s_dimOptions = new()
    {
        Min = G.DimMin, Max = G.DimMax, Step = G.DimStep, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>The popover's state: hook cells of the slot.</summary>
    sealed class Cells(Signal<int> spectrum, Signal<int> cursor, Signal<int> tab, Signal<int> group, Signal<int> layoutSel, FloatSignal dim)
    {
        public readonly Signal<int> Spectrum = spectrum, Cursor = cursor, Tab = tab, Group = group, LayoutSel = layoutSel;
        public readonly FloatSignal Dim = dim;
    }

    /// <summary>The registered template of <see cref="ViewKeys.StageOptions"/>.</summary>
    public static Element Template(StageOptionsProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks;
        var cells = new Cells(h.UseSignal(0), h.UseSignal(-1), h.UseSignal(0), h.UseSignal(0), h.UseSignal(0), h.UseFloatSignal(Stage.HeroRules.DefaultDim));
        var st = props.Stage;
        var verbs = props.Verbs;
        var L = st.Layout.Value;
        var accent = st.Accent.Value;
        int tab = cells.Tab.Value;
        h.UseSignalEffect(() => cells.Spectrum.SetIfChanged((int)st.SpectrumPref.Value));
        h.UseSignalEffect(() => cells.LayoutSel.SetIfChanged((int)st.LayoutPref.Value));
        h.UseSignalEffect(() => cells.Dim.SetIfChanged(st.HeroDim.Value));

        float width = MathF.Min(G.OptionsMaxW, L.W - G.OptionsEdgeInset);
        float height = MathF.Min(G.OptionsMaxH, L.H - G.OptionsEdgeInset);
        var body = tab == 0 ? VisualizerTab(st, verbs, cells, accent, width) : LayoutTab(st, verbs, cells, L, accent, width);
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 1, Gap = G.OptionsHeaderGap, Padding = G.OptionsHeaderPadding, Shrink = 0f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Stage.Options.Title)) { Size = G.OptionsTitleSize, LineHeight = G.OptionsTitleLine, Weight = G.TitleLight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink, Case = R.Case.For(LabelKind.Heading) },
                    Tabs(cells),
                ],
            },
            body,
        };
        if (tab == 0) kids.Add(Footer(st, props.Close));
        return new BoxEl
        {
            Width = width, Height = height, Direction = 1, Corners = R.Shape.All(ShapeRole.Overlay),
            Fill = R.Layer.FloatingChrome, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    // ── the tabs: pivot words ────────────────────────────────────────────────────────────────────────────────────────────

    static Element Tabs(Cells cells)
    {
        int sel = cells.Tab.Value;
        var facet = R.Type.FacetTitleStyle;
        string[] names = [Loc.Get(Strings.Stage.Options.TabViz), Loc.Get(Strings.Stage.Options.TabLayout)];
        var items = new Element[G.TabCount];
        for (int i = 0; i < G.TabCount; i++)
        {
            int index = i;
            bool on = i == sel;
            items[i] = new BoxEl
            {
                Height = G.TabH, Direction = 0, AlignItems = FlexAlign.Center, Role = AutomationRole.Tab, TabStop = on, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => cells.Tab.SetIfChanged(index),
                OnKeyDown = e =>
                {
                    if (e.Handled || e.Ctrl || e.Alt) return;
                    int next = e.KeyCode == Keys.Right || e.KeyCode == Keys.End ? 1 : e.KeyCode == Keys.Left || e.KeyCode == Keys.Home ? 0 : -1;
                    if (next < 0) return;
                    e.Handled = true; cells.Tab.SetIfChanged(next);
                },
                Children =
                [
                    new TextEl(names[i])
                    {
                        Size = G.TabSize, LineHeight = G.TabLine, FontFamily = R.Type.DisplayFace, Weight = (ushort)(on ? facet.SelectedWeight : facet.RestWeight),
                        Color = on ? Ink.Ink : Ink.InkSecondary, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Navigation),
                    },
                ],
            };
        }
        return new BoxEl { Direction = 0, Gap = G.TabGap, Shrink = 0f, Children = items };
    }

    // ── the visualizer tab ───────────────────────────────────────────────────────────────────────────────────────────────

    static Element Poster(Visualizer.Kind kind, float w, float h, ColorF bg, Visualizer.Slab slab, Visualizer.Palette pal) => new BoxEl
    {
        Width = w, Height = h, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true, ZStack = true, HitTestVisible = false, Fill = bg, Shrink = 0f,
        Children = [new BoxEl { Key = "poster", Width = w, Height = h, HitTestVisible = false,
            Children = [Visualizer.Face(kind, slab, in pal, new Visualizer.FaceSpec(w, h, Preview: true, CoverUrl: null, Poster: true))] }],
    };

    static Element VisualizerTab(Stage.StageCtx st, IStageSettingsVerbs verbs, Cells cells, AccentSet accent, float width)
    {
        var current = st.Kind.Value;
        var pal = st.Palette.Value;
        var slab = st.PosterSlab;
        ColorF bg = ColorF.Lerp(Ink.Veil, slab.A.Value, Ink.SelectedTileWash);
        var label = R.Case.For(LabelKind.Label);

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
            Shrink = 0f, Padding = G.OptionsSidePadding,
            Children =
            [
                RadioButtons.Create(chipNames.Length, i =>
                {
                    bool on = i == g;
                    return new BoxEl
                    {
                        MinHeight = G.ChipMinHeight, Padding = G.ChipPadding, Corners = R.Shape.All(ShapeRole.Control), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                        Fill = on ? accent.Fill : Ink.Plate,
                        Children = [new TextEl(chipNames[i]) { Size = G.ChipSize, LineHeight = G.ChipLine, Color = on ? accent.Ink : Ink.Ink, Case = label }],
                    };
                }, cells.Group, _ => cells.Cursor.SetIfChanged(-1), maxColumns: chipNames.Length, style: PlainRadio, parts: ChipParts),
            ],
        };

        float tileW = MathF.Floor((width - G.GridPadX - G.GridGap * (G.OptionsGridCols - 1)) / G.OptionsGridCols);
        float tileH = MathF.Round(tileW * G.PosterAspectH / G.PosterAspectW);
        int cursor = cells.Cursor.Value;
        var rows = new List<Element>(8);
        for (int r = 0; r < list.Count; r += G.OptionsGridCols)
        {
            var row = new Element[Math.Min(G.OptionsGridCols, list.Count - r)];
            for (int c = 0; c < row.Length; c++)
            {
                int index = r + c;
                var kind = list[index];
                bool on = Visualizer.Catalog.Successor(current) == kind;
                row[c] = new BoxEl
                {
                    Width = tileW, Direction = 1, Gap = G.GridTileGap, Padding = G.GridTilePadding, Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = false,
                    BorderWidth = G.GridTileBorder, BorderColor = on ? accent.Fill : cursor == index ? Ink.Ink : ColorF.Transparent, HoverFill = Ink.PlateHover,
                    OnClick = () => { cells.Cursor.SetIfChanged(index); verbs.PickFace(kind); },
                    Children =
                    [
                        Poster(kind, tileW - G.TilePosterInsetW, tileH - G.TilePosterInsetH, bg, slab, pal),
                        new TextEl(Loc.Get(Visualizer.NameKey(kind))) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.Ink, Case = label, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = G.GridTileNameMargin },
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
                    Keys.Up => cur - G.OptionsGridCols >= 0 ? cur - G.OptionsGridCols : cur, Keys.Down => cur + G.OptionsGridCols < count ? cur + G.OptionsGridCols : cur,
                    Keys.Home => 0, Keys.End => count - 1, _ => -1,
                };
                if (next < 0) return;
                e.Handled = true; cells.Cursor.SetIfChanged(next);
            },
            Children = rows.ToArray(),
        };

        var now = new BoxEl
        {
            Direction = 0, Gap = G.NowGap, AlignItems = FlexAlign.Center, Padding = G.OptionsSidePadding, Shrink = 0f,
            Children =
            [
                Poster(current, G.NowPosterW, G.NowPosterH, bg, slab, pal),
                new BoxEl
                {
                    Direction = 1, Gap = G.NowTextGap, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.Options.NowShowing)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary, Case = label },
                        new TextEl(Loc.Get(Visualizer.NameKey(current))) { Size = G.OptionsTitleSize, LineHeight = G.OptionsTitleLine, Weight = G.TitleLight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink, Case = label, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(Loc.Get(GroupName(Visualizer.Catalog.GroupOf(Visualizer.Catalog.Successor(current)))) + " · "
                            + Loc.Get(Visualizer.Catalog.PaceOf(current) == Visualizer.Pace.Lively ? Strings.Stage.VizEnergy.Lively : Strings.Stage.VizEnergy.Calm)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary, Case = label },
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

    /// <summary>The footer: the promise, and "open gallery" (the same switch G makes).</summary>
    static Element Footer(Stage.StageCtx st, Action close)
    {
        void OpenGallery()
        {
            st.OpenGallery();
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
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = G.FooterButtonGap, MinHeight = G.FooterButtonMinH, Shrink = 0f,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = OpenGallery,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.Options.OpenGallery)) { Size = G.BodySize, LineHeight = G.BodyLine, Color = Ink.Ink, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command) },
                        new BoxEl
                        {
                            Padding = G.KeyCapPadding, BorderWidth = 1f, BorderColor = Ink.InkSecondary, HitTestVisible = false,
                            Children = [new TextEl("G") { Size = G.KeyCapSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary }],
                        },
                    ],
                },
            ],
        };
    }

    // ── the layout tab ───────────────────────────────────────────────────────────────────────────────────────────────────

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
        Children = [new TextEl(title) { Size = G.BodySize, LineHeight = G.BodyLine, Color = Ink.InkSecondary, Case = R.Case.For(LabelKind.Heading) }, body],
    };

    static Element Row(string label, Element control, string? note = null) => new BoxEl
    {
        Direction = 1, Gap = G.RowGap, Shrink = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, MinHeight = G.OptionRowMinH,
                Children = [new TextEl(label) { Size = G.BodySize, LineHeight = G.BodyLine, Color = Ink.Ink }, control],
            },
            .. (note is null ? Array.Empty<Element>() : [new TextEl(note) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary }]),
        ],
    };

    /// <summary>One layout row: an 84 x 48 schematic over the face's colour, the name and what it does; the accent border and a check on the
    /// picked one (never colour alone). Square.</summary>
    static Element LayoutRow(int index, string name, string sub, bool on, AccentSet accent, Visualizer.Palette pal)
    {
        ColorF viz = ColorF.Lerp(Ink.Veil, pal.A, G.ThumbPlateMix);
        Element thumb = new BoxEl
        {
            Width = G.ThumbW, Height = G.ThumbH, Fill = index == 3 ? Ink.InkSecondary with { A = G.ThumbArtistPlateA } : viz, ClipToBounds = true, ZStack = true, HitTestVisible = false, Shrink = 0f,
            Children = index switch
            {
                1 => [Mark(G.LargeMark, accent.Fill)],
                2 => [Mark(G.CenteredMark, accent.Fill)],
                3 => [Mark(G.ArtistMark, Ink.Ink with { A = G.ThumbArtistDotA })],
                _ => [Mark(G.CardMark, accent.Fill)],
            },
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = G.LayoutRowGap, Padding = G.LayoutRowPadding, HitTestVisible = false,
            Fill = on ? accent.Fill with { A = G.LayoutRowSelectedA } : Ink.GlassRest,
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
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = G.ChipGridGap,
                            Children =
                            [
                                new TextEl(name) { Size = G.BodySize, LineHeight = G.BodyLine, Color = Ink.Ink, Case = R.Case.For(LabelKind.Label) },
                                .. (on ? [new TextEl(Icons.Accept) { Size = G.CheckSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }] : Array.Empty<Element>()),
                            ],
                        },
                        new TextEl(sub) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap },
                    ],
                },
            ],
        };
    }

    static Element Mark(ZuneRect r, ColorF fill) => new BoxEl
    {
        Width = r.W, Height = r.H, Fill = fill, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(r.X, r.Y, 0f, 0f),
    };
}
