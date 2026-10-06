// ── Shell/Stage.Options.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The visualizer OPTIONS popover (Options.dc.html): Layout (2×2 cards) · Spectrum (Bars / Ring / Line / Off) · Artist hero
// (lyrics over the image, slow pan and zoom, image dimming), opened from the top bar's sliders button
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 260 lines
// Spec: Options.dc.html (420 wide · 20 padding · 20 gap · cards 56-tall thumb + 14/600 name + 12 sub · 2 px accent border when selected)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// An overlay mounts OUTSIDE the stage subtree, so the flyout re-provides the stage context (the stage's DeviceButton uses a plain
// MenuFlyout that reads none). Every control takes the context's SIGNALS (or an adapter mirrored from them) and writes through
// Prefs.Stage — the gallery's SettingRow contract. The layout cards are RadioButtons under ONE focusable grid whose arrow keys move a
// cursor (LayoutRules.CardNav) and Enter / Space pick it; the selected card also wears a check glyph (never colour alone).

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Stage
{
    static readonly Slider.SliderOptions s_dimOptions = new() { Min = 0f, Max = 100f, Step = 5f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" };

    sealed class OptionsFlyout : Component
    {
        readonly Signal<int> _spectrum = new(0), _cursor = new(0);
        readonly FloatSignal _dim = new(HeroRules.DefaultDim);
        Action? _close;

        public OptionsFlyout(Action? close = null) { _close = close; }

        public override Element Render()
        {
            // the flyout lives in the overlay layer: re-read the stage context from the provider the opener wrapped it in
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var accent = ctx.Accent.Value;
            int layout = (int)ctx.LayoutPref.Value;
            bool compact = L.Aspect == Aspect.Compact;
            bool reduced = Design.Reduced;
            UseSignalEffect(() => _spectrum.SetIfChanged((int)ctx.SpectrumPref.Value));
            UseSignalEffect(() => _dim.SetIfChanged(ctx.HeroDim.Value));
            var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };
            var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = 40f, OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary, Foreground = Ink.Ink };

            var kids = new List<Element>(8)
            {
                new TextEl(Loc.Get(Strings.Stage.Options.Title)) { Size = 18f, LineHeight = 24f, Weight = 600, Color = Ink.Ink, Shrink = 0f },
            };
            if (!compact)
            {
                kids.Add(Section(Loc.Get(Strings.Stage.Options.Layout), LayoutGrid(layout, accent)));
                if (layout == (int)VizLayout.Card)
                    kids.Add(Button.Create(Loc.Get(Strings.Stage.Options.Faces), () => { Prefs.Stage.SetGalleryOpen(true); _close?.Invoke(); },
                        ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.Equalizer) with { Shrink = 0f, AlignSelf = FlexAlign.Start });
                else
                    kids.Add(Section(Loc.Get(Strings.Stage.Options.Spectrum), Segmented.Create(
                        [new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Bars)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Ring)),
                         new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Line)), new SegmentedItem(Loc.Get(Strings.Stage.Spectrum.Off))],
                        _spectrum, static i => Prefs.Stage.SetSpectrum(i))));
            }
            kids.Add(new BoxEl
            {
                Direction = 1, Gap = 12f, Padding = new Edges4(0f, 16f, 0f, 0f), BorderWidth = 0f, Shrink = 0f,
                Children =
                [
                    new BoxEl { Height = 1f, Margin = new Edges4(0f, -16f, 0f, 4f), Fill = Ink.Stroke, Shrink = 0f },
                    new TextEl(Loc.Get(Strings.Stage.Options.ArtistHero)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink },
                    Row(Loc.Get(Strings.Stage.Options.HeroLyrics), ToggleSwitch.Create(ctx.HeroLyricsPref, static on => Prefs.Stage.SetHeroLyrics(on), style: toggleStyle)),
                    Row(Loc.Get(Strings.Stage.Options.HeroMotion), ToggleSwitch.Create(ctx.HeroMotion, static on => Prefs.Stage.SetHeroMotion(on), isEnabled: !reduced, style: toggleStyle),
                        reduced ? Loc.Get(Strings.Stage.Options.HeroMotionReduced) : null),
                    new BoxEl
                    {
                        Direction = 1, Gap = 6f,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.Options.HeroDim)) { Size = 14f, LineHeight = 20f, Color = Ink.Ink },
                            Slider.Create(_dim, static v => Prefs.Stage.SetHeroDim((int)MathF.Round(v / 5f) * 5), s_dimOptions, length: 360f, thickness: 24f, style: sliderStyle),
                        ],
                    },
                    new TextEl(Loc.Get(Strings.Stage.Options.HeroNote)) { Size = 12f, LineHeight = 17f, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap },
                ],
            });
            float width = MathF.Min(420f, L.W - 48f);
            return new BoxEl
            {
                Width = width, Direction = 1, Gap = 20f, Padding = Edges4.All(20f), Corners = Radii.CardAll,
                Acrylic = Ink.Card, Shadow = Elevation.Flyout, ClipToBounds = true,
                Children = kids.ToArray(),
            };
        }

        static Element Section(string title, Element body) => new BoxEl
        {
            Direction = 1, Gap = 8f, Shrink = 0f,
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

        /// <summary>The 2×2 grid: one focusable box whose arrows move the cursor and whose Enter / Space pick it; each card is a RadioButton.</summary>
        Element LayoutGrid(int selected, AccentSet accent)
        {
            string[] names = [Loc.Get(Strings.Stage.Layout.Card), Loc.Get(Strings.Stage.Layout.Large), Loc.Get(Strings.Stage.Layout.Centered), Loc.Get(Strings.Stage.Layout.Artist)];
            string[] subs = [Loc.Get(Strings.Stage.LayoutSub.Card), Loc.Get(Strings.Stage.LayoutSub.Large), Loc.Get(Strings.Stage.LayoutSub.Centered), Loc.Get(Strings.Stage.LayoutSub.Artist)];
            var rows = new Element[2];
            for (int r = 0; r < 2; r++)
            {
                var cells = new Element[2];
                for (int c = 0; c < 2; c++)
                {
                    int i = r * 2 + c;
                    cells[c] = Card(i, names[i], subs[i], i == selected, accent);
                }
                rows[r] = new BoxEl { Direction = 0, Gap = 8f, Children = cells };
            }
            return new BoxEl
            {
                Direction = 1, Gap = 8f, Focusable = true,
                OnFocusChanged = got => { if (got) _cursor.SetIfChanged(selected); },
                OnKeyDown = e =>
                {
                    if (e.Ctrl || e.Alt) return;
                    int next = LayoutRules.CardNav(_cursor.Peek(), e.KeyCode);
                    if (next != _cursor.Peek()) { e.Handled = true; _cursor.Value = next; return; }
                    if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.Handled = true; Prefs.Stage.SetLayout(_cursor.Peek()); }
                },
                Children = rows,
            };
        }

        static Element Card(int index, string name, string sub, bool on, AccentSet accent)
        {
            // a tiny schematic per layout, on the board's plate
            Element thumb = new BoxEl
            {
                Height = 56f, Corners = CornerRadius4.All(6f), Fill = Ink.Plate, ClipToBounds = true, ZStack = true, HitTestVisible = false,
                Children = index switch
                {
                    1 => [Dot(10f, 10f, 30f, 30f, accent.Fill)],
                    2 => [Dot(0f, 0f, 26f, 26f, accent.Fill, center: true)],
                    3 => [new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = Ink.InkSecondary with { A = 0.35f } },
                          Dot(10f, 28f, 60f, 10f, Ink.Ink with { A = 0.7f })],
                    _ => [Dot(10f, 10f, 22f, 14f, accent.Fill)],
                },
            };
            return new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Gap = 4f, Padding = Edges4.All(10f), Corners = Radii.ControlAll,
                Fill = on ? accent.Fill with { A = 0.16f } : Ink.GlassRest, HoverFill = Ink.GlassHover,
                BorderWidth = on ? 2f : 1f, BorderColor = on ? accent.Fill : Ink.Stroke,
                Role = AutomationRole.RadioButton, Focusable = false, Cursor = CursorId.Hand,
                OnClick = () => Prefs.Stage.SetLayout(index),
                Children =
                [
                    thumb,
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
                        Children =
                        [
                            new TextEl(name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink },
                            .. (on ? [new TextEl(Icons.Accept) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.Ink }] : Array.Empty<Element>()),
                        ],
                    },
                    new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            };
        }

        static Element Dot(float x, float y, float w, float h, ColorF fill, bool center = false) => new BoxEl
        {
            Width = w, Height = h, Corners = CornerRadius4.All(3f), Fill = fill, HitTestVisible = false,
            AlignSelf = center ? FlexAlign.Center : FlexAlign.Start, JustifySelf = center ? FlexAlign.Center : FlexAlign.Start,
            Margin = center ? default : new Edges4(x, y, 0f, 0f),
        };
    }

    /// <summary>The options popover, re-providing the stage context (it mounts in the overlay layer, outside the stage subtree).</summary>
    static Element OptionsContent(StageCtx ctx, Action close) => Ctx.Provide(StageContext, ctx, Embed.Comp(() => new OptionsFlyout(close)));
}
