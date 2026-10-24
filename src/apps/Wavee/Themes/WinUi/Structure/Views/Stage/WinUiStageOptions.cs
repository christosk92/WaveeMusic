// ── Themes/WinUi/Structure/Views/Stage/WinUiStageOptions.cs — WinUI's `Stage.Options` (P6 W4b) ─────────────────────────────────────────
//
// The popover W-Stage-Huge4K draws under the options glyph: one acrylic r8 column, no tabs.
//   Face               a two-column grid of every visualizer's poster (the gallery's POSTER: bound to the stage's poster slab, nothing ticks),
//                      the shown face outlined in the accent; Enter / Space and the arrows work on the focused grid, as the gallery's.
//   Layout             a segmented row of the layouts THIS WINDOW TIER ALLOWS (`Stage.LayoutRules.Allows` over `Stage.Layout.AllowedLayouts`:
//                      W-Tiers' Compact = Card, Small = Card | Large art), the spectrum under a Large art / Centered cover, the Artist layout's
//                      photo rows.
//   Background blur    the lyrics' depth blur (0 = off .. 100; `LyricsLinesStyle.StageDepthBlur` makes it reach the stage's far lines).
//   Open gallery (G)   the gallery lives behind the options (W-Tiers): the stage's one gallery rule.
//
// The core opener mounts this view inside the overlay layer, outside the stage subtree, and hands it the stage context on the props. Every
// write goes through `IStageSettingsVerbs`, the blur through the lyrics' own writer (`Lyrics.Prefs`). The popover's mirrored cells live in the
// slot's hook cells.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using G = Wavee.Themes.WinUi.WinUiStageOptionsGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's stage options popover.</summary>
internal static class WinUiStageOptions
{
    static readonly Slider.SliderOptions s_blurOptions = new()
    {
        Min = G.BlurMin, Max = G.BlurMax, Step = G.BlurStep, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    static readonly Slider.SliderOptions s_dimOptions = new()
    {
        Min = G.DimMin, Max = G.DimMax, Step = G.DimStep, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    static readonly Stage.VizLayout[] s_layouts = [Stage.VizLayout.Card, Stage.VizLayout.LargeArt, Stage.VizLayout.Centered, Stage.VizLayout.Artist];

    /// <summary>The registered template of <see cref="ViewKeys.StageOptions"/>.</summary>
    public static Element Template(StageOptionsProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks;
        var spectrum = h.UseSignal(0);
        var layoutSel = h.UseSignal(0);
        var cursor = h.UseSignal(-1);
        var blur = h.UseFloatSignal(0f);
        var dim = h.UseFloatSignal(Stage.HeroRules.DefaultDim);
        var st = props.Stage;
        var verbs = props.Verbs;
        var L = st.Layout.Value;
        var accent = st.Accent.Value;

        // the layouts this window tier allows (Card always): the segmented row offers only these
        var allowed = new List<Stage.VizLayout>(4);
        foreach (var l in s_layouts) if (L.Aspect != Stage.Aspect.Compact && Stage.LayoutRules.Allows(L.AllowedLayouts, l)) allowed.Add(l);
        if (allowed.Count == 0) allowed.Add(Stage.VizLayout.Card);
        h.UseSignalEffect(() => spectrum.SetIfChanged((int)st.SpectrumPref.Value));
        h.UseSignalEffect(() => layoutSel.SetIfChanged(Math.Max(0, allowed.IndexOf(st.Effective.Value))));
        h.UseSignalEffect(() => dim.SetIfChanged(st.HeroDim.Value));
        h.UseSignalEffect(() => blur.SetIfChanged(Lyrics.Prefs.BlurStrength(onStage: false)));

        float width = MathF.Min(G.PanelMaxWidth, L.W - G.PanelEdgeInset);
        float height = MathF.Min(G.PanelMaxHeight, L.H - G.PanelEdgeInset);
        var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };
        float inner = width - 2f * G.Pad;

        var layout = st.LayoutPref.Value;
        var sections = new List<Element>(6)
        {
            Section(Loc.Get(Strings.Theme.Winui.Stage.Face), Faces(st, verbs, cursor, accent, inner)),
        };
        if (allowed.Count > 1)
        {
            var items = new SegmentedItem[allowed.Count];
            for (int i = 0; i < items.Length; i++) items[i] = new SegmentedItem(Loc.Get(WinUiStageText.LayoutName(allowed[i])));
            sections.Add(Section(Loc.Get(Strings.Theme.Winui.Stage.Layout), new BoxEl
            {
                Key = "winui-stage-options-layouts", Direction = 1,
                Children = [Segmented.Create(items, layoutSel, i => verbs.SetLayout((int)allowed[Math.Clamp(i, 0, allowed.Count - 1)]))],
            }));
        }
        if (layout is Stage.VizLayout.LargeArt or Stage.VizLayout.Centered && allowed.Contains(layout))
            sections.Add(Section(Loc.Get(Strings.Stage.Options.SpectrumUnder), Segmented.Create(
                [new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Bars)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Ring)),
                 new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Line)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Off))],
                spectrum, i => verbs.SetSpectrum(i))));
        if (layout == Stage.VizLayout.Artist && allowed.Contains(layout))
        {
            var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = G.ToggleMinWidth };
            sections.Add(new BoxEl
            {
                Direction = 1, Gap = G.HeadGap, Shrink = 0f,
                Children =
                [
                    Row(Loc.Get(Strings.Stage.Options.HeroLyrics), ToggleSwitch.Create(st.HeroLyricsPref, on => verbs.SetHeroLyrics(on), style: toggleStyle)),
                    Row(Loc.Get(Strings.Stage.Options.HeroMotion), ToggleSwitch.Create(st.HeroMotion, on => verbs.SetHeroMotion(on), isEnabled: !Design.Reduced, style: toggleStyle)),
                    Section(Loc.Get(Strings.Stage.Options.HeroDim), Slider.Create(dim, v => verbs.SetHeroDim((int)MathF.Round(v / G.DimStep) * (int)G.DimStep), s_dimOptions, length: inner, thickness: G.SliderThickness, style: sliderStyle)),
                ],
            });
        }
        int blurNow = (int)MathF.Round(blur.Value);
        sections.Add(new BoxEl
        {
            Key = "winui-stage-options-blur", Direction = 1, Gap = G.HeadGap, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.SpaceBetween,
                    Children =
                    [
                        Head(Loc.Get(Strings.Theme.Winui.Stage.BackgroundBlur)),
                        new TextEl(blurNow.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary },
                    ],
                },
                Slider.Create(blur, v => Lyrics.Prefs.SetBlurStrength((int)MathF.Round(v / G.BlurStep) * (int)G.BlurStep), s_blurOptions, length: inner, thickness: G.SliderThickness, style: sliderStyle),
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.SpaceBetween,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Theme.Winui.Stage.BlurOff)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkTertiary },
                        new TextEl(((int)G.BlurMax).ToString(System.Globalization.CultureInfo.InvariantCulture)) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkTertiary },
                    ],
                },
            ],
        });

        // the panel is as tall as its sections (the face grid capped), never taller than the stage allows: a scroller has no natural height
        float tileW = MathF.Floor((inner - G.GridGap * (G.GridCols - 1)) / G.GridCols);
        float posterH = MathF.Round((tileW - 2f * (G.TilePadding.Left + G.TileBorder)) * G.PosterAspectH / G.PosterAspectW);
        int faceRows = (Visualizer.Catalog.ShownCount + G.GridCols - 1) / G.GridCols;
        float facesH = MathF.Min(G.GridMaxH, faceRows * (posterH + 2f * (G.TilePadding.Top + G.TileBorder) + G.TileGap + G.CaptionLine + G.GridGap));
        float want = 2f * G.Pad + (G.CaptionLine + G.HeadGap + facesH) + (sections.Count - 1) * (G.SectionGap + G.CaptionLine + G.HeadGap + G.SectionBodyH)
                     + G.FooterButtonH + G.Pad;
        return new BoxEl
        {
            Key = "winui-stage-options", Width = width, Height = MathF.Min(height, want), Direction = 1, Corners = Radii.CardAll,
            Acrylic = Ink.Card, BorderWidth = 1f, BorderColor = Ink.Stroke, Shadow = Elevation.Flyout, ClipToBounds = true,
            Children =
            [
                new ScrollEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "winuistageoptions",
                    Content = new BoxEl { Direction = 1, Gap = G.SectionGap, Padding = Edges4.All(G.Pad), Children = sections.ToArray() },
                },
                Footer(st, props.Close),
            ],
        };
    }

    static TextEl Head(string text) => new(text) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Weight = G.WeightSemiBold, Color = Ink.Ink };

    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = G.HeadGap, Shrink = 0f,
        Children = [Head(title), body],
    };

    static Element Row(string label, Element control) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, MinHeight = G.RowMinHeight, Shrink = 0f,
        Children = [new TextEl(label) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.Ink, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap }, control],
    };

    static Element Poster(Visualizer.Kind kind, float w, float h, ColorF bg, Visualizer.Slab slab, Visualizer.Palette pal) => new BoxEl
    {
        Width = w, Height = h, Corners = G.PosterCorners, ClipToBounds = true, ZStack = true, HitTestVisible = false, Fill = bg, Shrink = 0f,
        Children = [new BoxEl { Key = "poster", Width = w, Height = h, HitTestVisible = false,
            Children = [Visualizer.Face(kind, slab, in pal, new Visualizer.FaceSpec(w, h, Preview: true, CoverUrl: null, Poster: true))] }],
    };

    /// <summary>The face grid: every shown visualizer as a poster with its name, two columns; the current one outlined in the accent.</summary>
    static Element Faces(Stage.StageCtx st, IStageSettingsVerbs verbs, Signal<int> cursor, AccentSet accent, float inner)
    {
        var current = st.Kind.Value;
        var pal = st.Palette.Value;
        var slab = st.PosterSlab;
        ColorF bg = ColorF.Lerp(Ink.Veil, slab.A.Value, Ink.SelectedTileWash);
        var list = new List<Visualizer.Kind>(Visualizer.Catalog.ShownCount);
        foreach (var k in Visualizer.Catalog.Shown) list.Add(k);
        float tileW = MathF.Floor((inner - G.GridGap * (G.GridCols - 1)) / G.GridCols);
        float posterW = tileW - 2f * (G.TilePadding.Left + G.TileBorder);
        float posterH = MathF.Round(posterW * G.PosterAspectH / G.PosterAspectW);
        int cur = cursor.Value;
        var rows = new List<Element>(list.Count / G.GridCols + 1);
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
                    Width = tileW, Direction = 1, Gap = G.TileGap, Padding = G.TilePadding, Corners = G.TileCorners, Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = false,
                    BorderWidth = G.TileBorder, BorderColor = on ? accent.Fill : cur == index ? Ink.Ink : ColorF.Transparent, HoverFill = Ink.GlassHover,
                    OnClick = () => { cursor.SetIfChanged(index); verbs.PickFace(kind); },
                    Children =
                    [
                        Poster(kind, posterW, posterH, bg, slab, pal),
                        new TextEl(Loc.Get(Visualizer.NameKey(kind))) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                };
            }
            rows.Add(new BoxEl { Direction = 0, Gap = G.GridGap, Shrink = 0f, Children = row });
        }
        int count = list.Count;
        var grid = new BoxEl
        {
            Key = "winui-stage-options-faces", Direction = 1, Gap = G.GridGap, Focusable = true,
            OnFocusChanged = got => { if (got && cursor.Peek() < 0) cursor.Value = Math.Max(0, list.IndexOf(Visualizer.Catalog.Successor(st.Kind.Peek()))); },
            OnKeyDown = e =>
            {
                if (e.Handled || e.Ctrl || e.Alt || count == 0) return;
                int at = Math.Clamp(cursor.Peek(), 0, count - 1);
                if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; if (!e.IsRepeat) verbs.PickFace(list[at]); return; }
                int next = e.KeyCode switch
                {
                    Keys.Left => Math.Max(0, at - 1), Keys.Right => Math.Min(count - 1, at + 1),
                    Keys.Up => at - G.GridCols >= 0 ? at - G.GridCols : at, Keys.Down => at + G.GridCols < count ? at + G.GridCols : at,
                    Keys.Home => 0, Keys.End => count - 1, _ => -1,
                };
                if (next < 0) return;
                e.Handled = true; cursor.SetIfChanged(next);
            },
            Children = rows.ToArray(),
        };
        float gridH = MathF.Min(G.GridMaxH, rows.Count * (posterH + 2f * (G.TilePadding.Top + G.TileBorder) + G.TileGap + G.CaptionLine + G.GridGap));
        return new ScrollEl { Height = gridH, Shrink = 0f, AutoEdgeFade = true, ScrollKey = "winuistagefaces", Content = grid };
    }

    /// <summary>The foot: the gallery's door (the same switch G makes).</summary>
    static Element Footer(Stage.StageCtx st, Action close)
    {
        void OpenGallery()
        {
            st.OpenGallery();
            close();
        }
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Shrink = 0f, Padding = new Edges4(G.Pad, 0f, G.Pad, G.Pad),
            Children =
            [
                new BoxEl
                {
                    Key = "winui-stage-options-gallery", Direction = 0, AlignItems = FlexAlign.Center, Gap = G.FooterButtonGap, Height = G.FooterButtonH, Padding = new Edges4(G.FooterButtonPadX, 0f, G.FooterButtonPadX, 0f),
                    Corners = Radii.ControlAll, Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = OpenGallery,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.Options.OpenGallery)) { Size = G.BodySize, LineHeight = G.BodyLine, Color = Ink.Ink },
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
}
