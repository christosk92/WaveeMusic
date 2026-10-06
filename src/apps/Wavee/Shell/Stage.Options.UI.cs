// ── Shell/Stage.Options.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The "Full screen" options popover (design A, two tabs), opened from the top bar's sliders button:
//   Visualizer tab: Now showing (poster) · group chips · grid of every visualizer (posters) · "Open gallery" (G)
//   Layout tab:     four layout radio rows with schematic previews · spectrum under the cover (Large / Centered) ·
//                   artist photo options (lyrics over the photo, slow pan and zoom, dimming)
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 330 lines
// Spec: PopoverTabsVisualizer.dc.html / PopoverTabsLayout.dc.html (440 wide · 20 padding · tabs with a 3 px accent underline)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// An overlay mounts OUTSIDE the stage subtree, so the flyout re-provides the stage context. Every control takes the context's
// SIGNALS (or an adapter mirrored from them) and writes through Prefs.Stage — the gallery's SettingRow contract. Tabs are real
// tabs (Role Tab, one roving tab stop, left / right move). The layout rows and the group chips are RadioButtons groups (roving
// arrows, selection follows focus); the visualizer grid is ONE focusable box whose arrows move a cursor and Enter / Space pick,
// like the gallery. Every visualizer thumbnail is the gallery's POSTER (Lod.Poster: bound to the stage's poster slab, nothing
// ticks). The user-facing layout names changed (Card is now "Visualizer first"); the persisted values did not.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Stage
{
    static readonly Slider.SliderOptions s_dimOptions = new() { Min = 0f, Max = 100f, Step = 5f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" };

    sealed class OptionsFlyout : Component
    {
        const int GridCols = 3;
        readonly Signal<int> _spectrum = new(0), _cursor = new(-1), _tab = new(0), _group = new(0), _layoutSel = new(0);
        readonly FloatSignal _dim = new(HeroRules.DefaultDim);
        readonly Action? _close;

        public OptionsFlyout(Action? close = null) { _close = close; }

        public override Element Render()
        {
            // the flyout lives in the overlay layer: re-read the stage context from the provider the opener wrapped it in
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var accent = ctx.Accent.Value;
            int tab = _tab.Value;
            UseSignalEffect(() => _spectrum.SetIfChanged((int)ctx.SpectrumPref.Value));
            UseSignalEffect(() => _layoutSel.SetIfChanged((int)ctx.LayoutPref.Value));
            UseSignalEffect(() => _dim.SetIfChanged(ctx.HeroDim.Value));

            float width = MathF.Min(440f, L.W - 48f);
            float height = MathF.Min(720f, L.H - 48f);
            var body = tab == 0 ? VisualizerTab(ctx, accent, width) : LayoutTab(ctx, L, accent, width);
            var kids = new List<Element>(4)
            {
                new BoxEl
                {
                    Direction = 1, Gap = 12f, Padding = new Edges4(20f, 18f, 20f, 0f), Shrink = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.Options.Title)) { Size = 18f, LineHeight = 24f, Weight = 600, Color = Ink.Ink },
                        Tabs(accent),
                    ],
                },
                body,
            };
            if (tab == 0) kids.Add(Footer(ctx));
            return new BoxEl
            {
                Width = width, Height = height, Direction = 1, Corners = Radii.CardAll,
                Acrylic = Ink.Card, Shadow = Elevation.Flyout, ClipToBounds = true,
                Children = kids.ToArray(),
            };
        }

        // ── the tablist ─────────────────────────────────────────────────────────────────────────────────────────────────

        Element Tabs(AccentSet accent)
        {
            int sel = _tab.Value;
            string[] names = [Loc.Get(Strings.Stage.Options.TabViz), Loc.Get(Strings.Stage.Options.TabLayout)];
            var items = new Element[2];
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                bool on = i == sel;
                Element label = new TextEl(names[i]) { Size = 14f, LineHeight = 20f, Weight = (ushort)(on ? 600 : 400), Color = on ? Ink.Ink : Ink.InkSecondary };
                Element head = i == 0
                    ? new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, HitTestVisible = false,
                        Children =
                        [
                            label,
                            new BoxEl
                            {
                                Padding = new Edges4(7f, 1f, 7f, 1f), Corners = Radii.Circle(20f), Fill = on ? accent.Fill : Ink.GlassHover,
                                Children = [new TextEl(Visualizer.Catalog.ShownCount.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = on ? accent.Ink : Ink.InkSecondary }],
                            },
                        ],
                    }
                    : label;
                items[i] = new BoxEl
                {
                    Direction = 1, AlignItems = FlexAlign.Center, Gap = 6f, Padding = new Edges4(14f, 8f, 14f, 0f), HoverFill = Ink.GlassHover,
                    Corners = CornerRadius4.All(6f), Role = AutomationRole.Tab, TabStop = on, Cursor = CursorId.Hand,
                    OnClick = () => _tab.SetIfChanged(index),
                    OnKeyDown = e =>
                    {
                        if (e.Handled || e.Ctrl || e.Alt) return;
                        int next = e.KeyCode == Keys.Right || e.KeyCode == Keys.End ? 1 : e.KeyCode == Keys.Left || e.KeyCode == Keys.Home ? 0 : -1;
                        if (next < 0) return;
                        e.Handled = true; _tab.SetIfChanged(next);
                    },
                    Children = [head, new BoxEl { Height = 3f, Corners = CornerRadius4.All(2f), Fill = on ? accent.Fill : ColorF.Transparent, AlignSelf = FlexAlign.Stretch }],
                };
            }
            return new BoxEl { Direction = 0, Gap = 4f, Shrink = 0f, Children = items };
        }

        // ── Visualizer tab ──────────────────────────────────────────────────────────────────────────────────────────────

        Element VisualizerTab(StageCtx ctx, AccentSet accent, float width)
        {
            var current = ctx.Kind.Value;
            var pal = ctx.Palette.Value;
            var slab = ctx.PosterSlab;
            float wash = Ink.IsDark ? 0.12f : 0.08f;
            ColorF bg = ColorF.Lerp(Ink.Veil, slab.A.Value, wash);

            Element Poster(Visualizer.Kind kind, float w, float h) => new BoxEl
            {
                Width = w, Height = h, Corners = CornerRadius4.All(6f), ClipToBounds = true, ZStack = true, HitTestVisible = false, Fill = bg, Shrink = 0f,
                Children = [new BoxEl { Key = "poster", Width = w, Height = h, HitTestVisible = false,
                    Children = [Visualizer.Face(kind, slab, in pal, new Visualizer.FaceSpec(w, h, Preview: true, CoverUrl: null, Poster: true))] }],
            };

            // the group filter: All + each catalog group (the chips are one RadioButtons group)
            var groups = Visualizer.Catalog.Groups;
            int g = Math.Clamp(_group.Value, 0, groups.Length);
            var list = new List<Visualizer.Kind>(Visualizer.Catalog.ShownCount);
            foreach (var k in Visualizer.Catalog.Shown)
                if (g == 0 || Visualizer.Catalog.GroupOf(k) == groups[g - 1]) list.Add(k);
            string[] chipNames = new string[groups.Length + 1];
            chipNames[0] = Loc.Get(Strings.Stage.Options.GroupAll);
            for (int i = 0; i < groups.Length; i++) chipNames[i + 1] = Loc.Get(GroupName(groups[i]));

            var chips = new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(20f, 0f, 20f, 0f),
                Children =
                [
                    RadioButtons.Create(chipNames.Length, i =>
                    {
                        bool on = i == g;
                        return new BoxEl
                        {
                            MinHeight = 32f, Padding = new Edges4(12f, 0f, 12f, 0f), Corners = Radii.Circle(32f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                            Fill = on ? accent.Fill : ColorF.Transparent, BorderWidth = on ? 0f : 1f, BorderColor = Ink.Stroke,
                            Children = [new TextEl(chipNames[i]) { Size = 13f, LineHeight = 18f, Color = on ? accent.Ink : Ink.Ink, Weight = (ushort)(on ? 600 : 400) }],
                        };
                    }, _group, _ => _cursor.SetIfChanged(-1), maxColumns: chipNames.Length, style: PlainRadio, parts: ChipParts),
                ],
            };

            float tileW = MathF.Floor((width - 40f - 10f * (GridCols - 1)) / GridCols);
            float tileH = MathF.Round(tileW * 9f / 16f);
            int cursor = _cursor.Value;
            var rows = new List<Element>(8);
            for (int r = 0; r < list.Count; r += GridCols)
            {
                var row = new Element[Math.Min(GridCols, list.Count - r)];
                for (int c = 0; c < row.Length; c++)
                {
                    int index = r + c;
                    var kind = list[index];
                    bool on = Visualizer.Catalog.Successor(current) == kind;
                    row[c] = new BoxEl
                    {
                        Width = tileW, Direction = 1, Gap = 4f, Padding = Edges4.All(2f), Corners = CornerRadius4.All(8f), Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = false,
                        BorderWidth = 2f, BorderColor = on ? accent.Fill : cursor == index ? Ink.Ink : ColorF.Transparent, HoverFill = Ink.GlassHover,
                        OnClick = () => { _cursor.SetIfChanged(index); PickKind(kind); },
                        Children =
                        [
                            Poster(kind, tileW - 8f, tileH - 4f),
                            new TextEl(Loc.Get(Visualizer.NameKey(kind))) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = new Edges4(2f, 0f, 2f, 2f) },
                        ],
                    };
                }
                rows.Add(new BoxEl { Direction = 0, Gap = 10f, Shrink = 0f, Children = row });
            }
            int count = list.Count;
            var grid = new BoxEl
            {
                Direction = 1, Gap = 10f, Padding = new Edges4(20f, 2f, 20f, 8f), Focusable = true,
                OnFocusChanged = got => { if (got && _cursor.Peek() < 0) _cursor.Value = Math.Max(0, list.IndexOf(Visualizer.Catalog.Successor(ctx.Kind.Peek()))); },
                OnKeyDown = e =>
                {
                    if (e.Handled || e.Ctrl || e.Alt || count == 0) return;
                    int cur = Math.Clamp(_cursor.Peek(), 0, count - 1);
                    if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; if (!e.IsRepeat) PickKind(list[cur]); return; }
                    int next = e.KeyCode switch
                    {
                        Keys.Left => Math.Max(0, cur - 1), Keys.Right => Math.Min(count - 1, cur + 1),
                        Keys.Up => cur - GridCols >= 0 ? cur - GridCols : cur, Keys.Down => cur + GridCols < count ? cur + GridCols : cur,
                        Keys.Home => 0, Keys.End => count - 1, _ => -1,
                    };
                    if (next < 0) return;
                    e.Handled = true; _cursor.SetIfChanged(next);
                },
                Children = rows.ToArray(),
            };

            var now = new BoxEl
            {
                Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center, Padding = new Edges4(20f, 0f, 20f, 0f), Shrink = 0f,
                Children =
                [
                    Poster(current, 136f, 76f),
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f, MinWidth = 0f,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.Options.NowShowing)) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary },
                            new TextEl(Loc.Get(Visualizer.NameKey(current))) { Size = 18f, LineHeight = 24f, Weight = 600, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(Loc.Get(GroupName(Visualizer.Catalog.GroupOf(Visualizer.Catalog.Successor(current)))) + " · "
                                + Loc.Get(Visualizer.Catalog.PaceOf(current) == Visualizer.Pace.Lively ? Strings.Stage.VizEnergy.Lively : Strings.Stage.VizEnergy.Calm)) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary },
                        ],
                    },
                ],
            };
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, Direction = 1, Gap = 14f, Padding = new Edges4(0f, 16f, 0f, 0f),
                Children =
                [
                    now, chips,
                    new ScrollEl { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageoptionsviz", Content = grid },
                ],
            };
        }

        static void PickKind(Visualizer.Kind kind)
        {
            Prefs.Stage.SetVisualizer((int)kind);
            Diagnostics.NotePick(kind);
        }

        static string GroupName(Visualizer.Group g) => g switch
        {
            Visualizer.Group.Lyrics => Strings.Stage.VizGroup.Lyrics, Visualizer.Group.Classics => Strings.Stage.VizGroup.Classics,
            Visualizer.Group.Zune => Strings.Stage.VizGroup.Zune, Visualizer.Group.ITunes => Strings.Stage.VizGroup.Itunes, _ => Strings.Stage.VizGroup.Fluent,
        };

        /// <summary>The footer: the promise, and the gallery (the same switch G makes: back to Card, then the pane opens).</summary>
        Element Footer(StageCtx ctx)
        {
            void OpenGallery()
            {
                if (ctx.LayoutPref.Peek() != VizLayout.Card) Prefs.Stage.SetLayout((int)VizLayout.Card);
                if (ctx.Mode.Peek() != Mode.Visualizer) Prefs.Stage.SetMode((int)Mode.Visualizer);
                Prefs.Stage.SetGalleryOpen(true);
                _close?.Invoke();
            }
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = 12f, Shrink = 0f, Padding = new Edges4(20f, 12f, 20f, 16f),
                Children =
                [
                    new TextEl(Loc.Get(Strings.Stage.Options.Footer)) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, MinHeight = 36f, Padding = new Edges4(12f, 0f, 12f, 0f), Shrink = 0f, Corners = Radii.ControlAll,
                        Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = OpenGallery,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.Options.OpenGallery)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink },
                            new BoxEl
                            {
                                Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(4f), BorderWidth = 1f, BorderColor = Ink.InkSecondary, HitTestVisible = false,
                                Children = [new TextEl("G") { Size = 11f, LineHeight = 16f, Color = Ink.InkSecondary }],
                            },
                        ],
                    },
                ],
            };
        }

        // ── Layout tab ──────────────────────────────────────────────────────────────────────────────────────────────────

        Element LayoutTab(StageCtx ctx, Layout L, AccentSet accent, float width)
        {
            int layout = (int)ctx.LayoutPref.Value;
            bool compact = L.Aspect == Aspect.Compact;
            bool reduced = Design.Reduced;
            var pal = ctx.Palette.Value;
            var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };
            var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = 40f, OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary, Foreground = Ink.Ink };

            var kids = new List<Element>(5)
            {
                new TextEl(Loc.Get(Strings.Stage.Options.LayoutHelp)) { Size = 13f, LineHeight = 19f, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, Shrink = 0f },
            };
            if (!compact)
            {
                string[] names = [Loc.Get(Strings.Stage.Layout.Card), Loc.Get(Strings.Stage.Layout.Large), Loc.Get(Strings.Stage.Layout.Centered), Loc.Get(Strings.Stage.Layout.Artist)];
                string[] subs = [Loc.Get(Strings.Stage.LayoutSub.Card), Loc.Get(Strings.Stage.LayoutSub.Large), Loc.Get(Strings.Stage.LayoutSub.Centered), Loc.Get(Strings.Stage.LayoutSub.Artist)];
                kids.Add(RadioButtons.Create(4, i => LayoutRow(i, names[i], subs[i], i == layout, accent, pal), _layoutSel, static i => Prefs.Stage.SetLayout(i), header: null, maxColumns: 1, style: PlainRadio, parts: RowParts));
                if (layout is (int)VizLayout.LargeArt or (int)VizLayout.Centered)
                    kids.Add(Section(Loc.Get(Strings.Stage.Options.SpectrumUnder), Segmented.Create(
                        [new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Bars)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Ring)),
                         new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Line)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Off))],
                        _spectrum, static i => Prefs.Stage.SetSpectrum(i))));
            }
            if (compact || layout == (int)VizLayout.Artist)
                kids.Add(new BoxEl
                {
                    Direction = 1, Gap = 12f, Padding = new Edges4(0f, 10f, 0f, 0f), Shrink = 0f,
                    Children =
                    [
                        Row(Loc.Get(Strings.Stage.Options.HeroLyrics), ToggleSwitch.Create(ctx.HeroLyricsPref, static on => Prefs.Stage.SetHeroLyrics(on), style: toggleStyle)),
                        Row(Loc.Get(Strings.Stage.Options.HeroMotion), ToggleSwitch.Create(ctx.HeroMotion, static on => Prefs.Stage.SetHeroMotion(on), isEnabled: !reduced, style: toggleStyle),
                            reduced ? Loc.Get(Strings.Stage.Options.HeroMotionReduced) : null),
                        new BoxEl
                        {
                            Direction = 1, Gap = 6f,
                            Children =
                            [
                                new TextEl(Loc.Get(Strings.Stage.Options.HeroDim)) { Size = 14f, LineHeight = 20f, Color = Ink.Ink },
                                Slider.Create(_dim, static v => Prefs.Stage.SetHeroDim((int)MathF.Round(v / 5f) * 5), s_dimOptions, length: width - 40f, thickness: 24f, style: sliderStyle),
                            ],
                        },
                        new TextEl(Loc.Get(Strings.Stage.Options.HeroNote)) { Size = 12f, LineHeight = 17f, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap },
                    ],
                });
            return new ScrollEl
            {
                Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageoptionslayout",
                Content = new BoxEl { Direction = 1, Gap = 12f, Padding = new Edges4(20f, 16f, 20f, 16f), Children = kids.ToArray() },
            };
        }

        static readonly RadioButton.Style PlainRadio = RadioButton.DefaultStyle with { ShowGlyph = false, MinWidth = 0f, MinHeight = 0f, FocusVisualMargin = Edges4.All(-2f) };
        static readonly TemplateParts ChipParts = new()
        {
            [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = 6f },
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
            Direction = 1, Gap = 8f, Shrink = 0f, Padding = new Edges4(0f, 10f, 0f, 0f),
            Children = [new TextEl(title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink }, body],
        };

        static Element Row(string label, Element control, string? note = null) => new BoxEl
        {
            Direction = 1, Gap = 2f, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, MinHeight = 32f,
                    Children = [new TextEl(label) { Size = 14f, LineHeight = 20f, Color = Ink.Ink }, control],
                },
                .. (note is null ? Array.Empty<Element>() : [new TextEl(note) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary }]),
            ],
        };

        /// <summary>One layout radio row: an 84x48 schematic over the visualizer, the name and what it does; a 2 px accent
        /// border and a check glyph when selected (never colour alone).</summary>
        static Element LayoutRow(int index, string name, string sub, bool on, AccentSet accent, Visualizer.Palette pal)
        {
            ColorF viz = ColorF.Lerp(Ink.Veil, pal.A, 0.55f);
            Element thumb = new BoxEl
            {
                Width = 84f, Height = 48f, Corners = CornerRadius4.All(6f), Fill = index == 3 ? Ink.InkSecondary with { A = 0.45f } : viz, ClipToBounds = true, ZStack = true, HitTestVisible = false, Shrink = 0f,
                Children = index switch
                {
                    1 => [Dot(8f, 9f, 22f, 22f, accent.Fill)],
                    2 => [Dot(31f, 9f, 22f, 22f, accent.Fill)],
                    3 => [Dot(6f, 37f, 30f, 5f, Ink.Ink with { A = 0.85f })],
                    _ => [Dot(6f, 6f, 14f, 9f, accent.Fill)],
                },
            };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = 12f, Padding = Edges4.All(10f), Corners = Radii.ControlAll, HitTestVisible = false,
                Fill = on ? accent.Fill with { A = 0.16f } : Ink.GlassRest,
                BorderWidth = on ? 2f : 1f, BorderColor = on ? accent.Fill : Ink.Stroke,
                Children =
                [
                    thumb,
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f, Grow = 1f, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
                                Children =
                                [
                                    new TextEl(name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink },
                                    .. (on ? [new TextEl(Icons.Accept) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.Ink }] : Array.Empty<Element>()),
                                ],
                            },
                            new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap },
                        ],
                    },
                ],
            };
        }

        static Element Dot(float x, float y, float w, float h, ColorF fill) => new BoxEl
        {
            Width = w, Height = h, Corners = CornerRadius4.All(3f), Fill = fill, HitTestVisible = false,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(x, y, 0f, 0f),
        };
    }

    /// <summary>The options popover, re-providing the stage context (it mounts in the overlay layer, outside the stage subtree).</summary>
    static Element OptionsContent(StageCtx ctx, Action close) => Ctx.Provide(StageContext, ctx, Embed.Comp(() => new OptionsFlyout(close)));
}
