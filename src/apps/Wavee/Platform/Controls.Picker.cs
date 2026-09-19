// ── Platform/Controls.Picker.cs ────────────────────────────────────────────────────────────────────────────────────
// WaveePicker, WaveeEqualizerCurve
//
// Role: UI
// Owner: L
// Wave: 4
// Budget: 850 lines
// Spec: DERIVED
//
// ── THE ONE PREVIEW-CARD RADIO ───────────────────────────────────────────────────────────────────────────────────────
//
// Four pickers had grown drifted copies of the same four things — the accent/neutral ink pair, the card shell, the
// selected-label treatment, and the wrapping strip: row density, track page layout, the (now deleted) palette picker,
// and the sidebar design chooser (which is ALSO the fresh-install dialog). The differences that actually matter are the
// wireframe drawn INSIDE a card and its footprint, so those are the parameters and everything else lives here.
//
// FOUR PIECES, NOT ONE RECORD. A swatch is a 30-DIP circle, not a card — forcing it through a card shell would be worse
// than the duplication it removes. So each picker composes only what it genuinely shares: all of them take Strip +
// Label, most take Ink, two take Tile.
//
// WHY `Strip` IS THE POINT. It delegates GROUP behaviour to the engine's radio group instead of leaving every card its
// own tab stop: ONE tab stop per picker that lands on the current value, Up/Down/Left/Right roving, selection FOLLOWING
// focus (Ctrl+arrow to move without applying), Space to select. The two things the group was missing for this — a
// glyph-less item and a wrapping container — are now template parts.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Controls
{
    // ══ 1. THE PICKER ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The accent/neutral ink pair every miniature tints itself with: <see cref="Block"/> for the solid shapes
    /// (covers, tiles, pills), <see cref="Faint"/> for the skeleton bars behind them.
    /// <para>The SELECTED card tints its WHOLE wireframe, so the choice reads from across the page and not only from the
    /// border.</para></summary>
    public readonly record struct PickerInk(ColorF Block, ColorF Faint)
    {
        /// <inheritdoc cref="PickerInk"/>
        public static PickerInk For(bool on) => on
            ? new(Tok.AccentDefault, Tok.AccentDefault with { A = 0.45f })
            : new(Tok.AccentDefault with { A = 0.58f }, Tok.AccentDefault with { A = 0.22f });
    }

    /// <summary>A card footprint. <paramref name="Inset"/> is the RESTING padding: a selected card spends 1 DIP of it on
    /// the 1 → 2 border growth, so <b>the border draws INWARD and the wireframe never shifts by a pixel</b>.
    /// <paramref name="Height"/> may be <c>NaN</c> for a content-sized card; <paramref name="Gap"/> is the spacing
    /// between the card's own stacked children.</summary>
    public readonly record struct PickerShell(float Width, float Height, float Inset, float Gap);

    /// <summary>The settings wireframe tile — row density, track page layout.</summary>
    public static readonly PickerShell PickerTile = new(116f, 84f, 8f, 4f);
    /// <summary>The sidebar design card as it appears in Settings, where it shares a page column.</summary>
    public static readonly PickerShell PickerPaneCompact = new(200f, float.NaN, 10f, 7f);
    /// <summary>The sidebar design card at full size — the fresh-install chooser.</summary>
    public static readonly PickerShell PickerPane = new(224f, float.NaN, 10f, 7f);
    /// <summary>The cover-style thumbnail: a 76-DIP square miniature inside the shared card shell (92 wide, 8 of resting
    /// inset each side). Content-sized in height — the miniature IS the card's content, and the label sits UNDER the
    /// card rather than in it.</summary>
    public static readonly PickerShell PickerCoverMini = new(92f, float.NaN, 8f, 5f);
    /// <summary>A wide horizontal row card — the setup wizard's chooser, where the picker reads as a vertical LIST of
    /// full-width rows rather than a strip of square cards.</summary>
    public static readonly PickerShell PickerWideRow = new(480f, 100f, 8f, 0f);

    /// <summary>The card SHELL: fill, radius, the accent border that grows INWARD on selection, the subtle hover/press
    /// scale.
    /// <para>Deliberately carries NO role, NO focus stop and NO click handler — inside <see cref="PickerStrip"/> the
    /// radio item root owns all three, and a second radio role here would announce the card TWICE.</para>
    /// <para><c>ClipToBounds</c> is ON: a miniature that outgrows its card must be CUT, not painted over its neighbours
    /// — the failure that produced the overlapping sidebar header. <c>Shrink 0</c> goes with it: the cards are
    /// FIXED-width, so a narrowing window drops COLUMNS and the cards never shrink and never overflow.</para></summary>
    public static BoxEl PickerCard(bool on, in PickerShell s, params Element[] body) => new()
    {
        Width = s.Width,
        Height = s.Height,
        Shrink = 0f,
        Direction = 1,
        Gap = s.Gap,
        Padding = Edges4.All(on ? s.Inset - 1f : s.Inset),
        ClipToBounds = true,
        Corners = CornerRadius4.All(Radii.Card),
        Fill = on ? Tok.AccentSubtle : Tok.FillCardDefault,
        HoverFill = on ? Design.Colors.SelectedHover : Tok.FillCardSecondary,
        PressedFill = on ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
        BorderWidth = on ? 2f : 1f,
        BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
        HoverScale = Design.Motion.ScaleSubtle.Hover,
        PressScale = Design.Motion.ScaleSubtle.Press,
        Cursor = CursorId.Hand,
        Children = body,
    };

    /// <summary>The selected-label treatment: the current choice goes SEMIBOLD and PRIMARY, the rest stay regular and
    /// secondary — so the selection survives a colour-blind read of the accent border.
    /// <para>At the default 12 the <c>size + 4</c> line box lands exactly on the 12/16 ramp rung. Any other
    /// <paramref name="size"/> leaves the eight-rung ramp silently, which is why this is named in the type ramp's own
    /// doc as the second sanctioned off-ramp and why a caller should not change it.</para></summary>
    public static TextEl PickerLabel(string text, bool on, float size = 12f) => new(text)
    {
        Size = size,
        LineHeight = size + 4f,
        Weight = (ushort)(on ? 600 : 400),
        Color = on ? Tok.TextPrimary : Tok.TextSecondary,
        MaxLines = 1,
        Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A card (or swatch) over its label — the shape most of the pickers want.</summary>
    public static BoxEl PickerTitled(Element card, string label, bool on, float gap = Spacing.S, float labelSize = 12f)
        => new()
        {
            Direction = 1, Gap = gap, AlignItems = FlexAlign.Center,
            Children = [card, PickerLabel(label, on, labelSize)],
        };

    // ── the miniatures ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A tinted bar segment for a wireframe row — <paramref name="strong"/> picks the identity ink over the
    /// skeleton ink, <paramref name="height"/> the bar's thickness (and, through the circle radius, its pill
    /// shape).</summary>
    public static Element PickerBar(float grow, PickerInk ink, bool strong = false, float height = Spacing.XXS)
        => new BoxEl
        {
            Grow = grow, Basis = 0f, MinWidth = 0f, Height = height,
            Corners = Radii.Circle(height), Fill = strong ? ink.Block : ink.Faint,
        };

    /// <summary>The ROW-DENSITY miniature: three rows whose proportions come from the REAL row geometry, compressed by
    /// one scale factor, so the settings tile and the real list cannot drift into two different explanations of
    /// Compact / Default / Cozy / Comfortable. The bars stay native, so they inherit the live theme and the card's
    /// selected-state ink instead of embedding a screenshot.
    ///
    /// <para><paramref name="rowHeight"/> and <paramref name="artEdge"/> are ALREADY SCALED and are supplied by the
    /// caller, not derived here: the ladders and the preview scale live in owner M's pure table rules (ch 01 §8), so
    /// "the preview mirrors the real row" stays a TESTED decision rather than a private constant this picker could drift
    /// from unnoticed.</para>
    ///
    /// <para>ONE KNOWN LIMITATION, recorded rather than fixed: the real ladders differ between the Modern and Classic
    /// skins, and the settings preview has always drawn the MODERN one regardless of the user's chosen skin. Pass the
    /// classic ladder here if 0.3 decides to change that; the geometry below does not care.</para></summary>
    public static Element PickerDensityRows(float rowHeight, float artEdge, bool on)
    {
        var ink = PickerInk.For(on);

        Element Row() => new BoxEl
        {
            Height = rowHeight, Shrink = 0f, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Children =
            [
                new BoxEl
                {
                    Width = artEdge, Height = artEdge, Shrink = 0f,
                    Corners = Radii.ControlAll, Fill = ink.Block,
                },
                PickerBar(1f, ink, strong: true),
                new BoxEl { Width = Spacing.XXL, Height = Spacing.XXS, Shrink = 0f, Corners = Radii.PillAll, Fill = ink.Faint },
                new BoxEl { Width = Spacing.L, Height = Spacing.XXS, Shrink = 0f, Corners = Radii.PillAll, Fill = ink.Faint },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            AlignSelf = FlexAlign.Stretch, Justify = FlexJustify.Center,
            Children = [Row(), Row(), Row()],
        };
    }

    /// <summary>The "Modern" track-row wireframe: a square art tile beside a title+subtitle bar pair (or, with
    /// <paramref name="twoLine"/> false, a single bar) — the art-led stacked-row grammar every Modern track list
    /// uses.</summary>
    public static Element PickerModernRow(PickerInk ink, float height = Spacing.XL, float art = Spacing.L,
                                          bool twoLine = true) => new BoxEl
    {
        Height = height, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = Radii.ControlAll, Fill = ink.Faint,
        Children =
        [
            new BoxEl { Width = art, Height = art, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
            twoLine
                ? new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children = [PickerBar(1f, ink, strong: true), PickerBar(0.65f, ink)],
                }
                : PickerBar(1f, ink, strong: true),
        ],
    };

    /// <summary>The "Classic" track-row wireframe: three aligned text-lane bars over a hairline — the three-lane grid +
    /// divider grammar every Classic track list uses.</summary>
    public static Element PickerClassicRow(PickerInk ink, float height = Spacing.XL) => new BoxEl
    {
        Height = height, Direction = 1,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
                Children = [PickerBar(1f, ink, strong: true), PickerBar(0.75f, ink), PickerBar(0.75f, ink)],
            },
            new BoxEl { Height = 1f, Fill = ink.Faint },
        ],
    };

    // ── the strip ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The GLYPH-LESS radio: the CARD is the control, so the ring/dot column is not built at all and the item
    /// root shrinks to its content.
    /// <para>The focus ring insets 2 DIP — WinUI's own radio margin (−7, −3) would draw the ring straight THROUGH the
    /// card's own border.</para></summary>
    static readonly RadioButton.Style BareRadio = RadioButton.DefaultStyle with
    {
        ShowGlyph = false,
        MinWidth = 0f,
        MinHeight = 0f,
        ContentGap = 0f,
        FocusVisualMargin = Design.FocusInsetBordered,
    };

    /// <summary>Container styling for the items grid. The radio group lays items out COLUMN-MAJOR and never wraps
    /// (WinUI's column-major uniform layout has no wrap state of its own); a strip of fixed-width preview cards has to
    /// drop to fewer COLUMNS on a narrow window instead of overflowing, so the grid wraps and the columns hold their
    /// width.</summary>
    static readonly TemplateParts StripParts = new()
    {
        [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = Spacing.M },
        [RadioButtons.PartColumn] = c => c with { Shrink = 0f },
    };

    /// <summary>Mount <paramref name="count"/> cards as ONE radio group. <paramref name="item"/>(index, isSelected)
    /// builds each card; <paramref name="onChange"/> is the single apply path and fires on CLICK <b>and</b> on a
    /// keyboard rove (selection follows focus — the WinUI contract), so it must be safe to call repeatedly.
    ///
    /// <para><paramref name="maxColumns"/> defaults to <paramref name="count"/> — ONE row, which is what every
    /// settings-page picker wants. Pass a smaller number for a picker whose cards do not fit on a line. Note what that
    /// actually arranges: the group is COLUMN-MAJOR, so items fill the first column top-to-bottom before starting the
    /// second — and the keyboard follows the same geometry, Up/Down stepping ±1 in DATA order while Left/Right jump
    /// column to column at the same row. The default of one-item-per-column is the degenerate case of exactly that,
    /// which is why the two consumers cannot disagree. It is CLAMPED, so a 0 or a negative can never reach the
    /// grid.</para>
    ///
    /// <para>The selected index is a FRESH signal per render carrying the live value — NOT a mirror kept in step by a
    /// write-during-render, which is the backwards-write guard's exact tripwire. The group re-pushes its props, so the
    /// throwaway is re-seeded from the caller's TRUTH every render and discarded; the real write happens in
    /// <paramref name="onChange"/>.</para>
    ///
    /// <para><paramref name="parts"/> overrides the container for a picker that reads as a vertical LIST rather than a
    /// wrapped horizontal strip.</para></summary>
    public static Element PickerStrip(int count, int selected, Func<int, bool, Element> item, Action<int> onChange,
                                      int? maxColumns = null, TemplateParts? parts = null)
        => RadioButtons.Create(
            count,
            i => item(i, i == selected),
            selectedIndex: new Signal<int>(selected),
            onChange: onChange,
            maxColumns: Math.Max(1, maxColumns ?? count),
            style: BareRadio,
            parts: parts ?? StripParts);

    // ══ 2. THE EQUALIZER CURVE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ten ISO band centres, as labels. <c>Cascadia Code</c> everywhere on this surface: the numerals must
    /// be TABULAR or the value pill's width jumps between "+1.5 dB" and "−12 dB" as the user drags.</summary>
    public static readonly string[] EqualizerBands = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    /// <summary>The ten-band equalizer curve: a draggable spline over a dashed grid, with a node per band and a value
    /// pill on the active one.
    ///
    /// <para>RE-PUSHED LIVE PROPS: the record-equality gate re-renders the core only when the gains, the handler, the
    /// enabled flag or the height actually change — so a settings page that re-renders for an unrelated row does not
    /// rebuild 240 child nodes.</para>
    ///
    /// <para><paramref name="height"/> null takes the width-derived heuristic; a card with its own budget fixes it
    /// rather than letting the curve claim whatever its width would imply.</para></summary>
    public static Element EqualizerCurve(float[] gains, Action<int, float> onBandChanged, bool isEnabled = true,
                                         float? height = null)
        => Embed.Comp(new EqCurveProps(gains, onBandChanged, isEnabled, height), static () => new EqCurveCore());

    /// <inheritdoc cref="EqualizerCurve"/>
    public sealed record EqCurveProps(float[] Gains, Action<int, float> OnBandChanged, bool IsEnabled, float? Height);

    sealed class EqCurveCore : Component
    {
        const float MinGain = -12f, MaxGain = 12f;
        const float PadLeft = 40f, PadRight = 16f, PadTop = 18f, PadBottom = 34f;
        const float NodeRest = 14f, NodeHot = 18f;
        const float FallbackWidth = 720f;
        const int Samples = 96;

        readonly Signal<int> _active = new(5);
        readonly Signal<int> _hover = new(-1);
        int _dragBand = -1;

        public override Element Render()
        {
            var p = UsePropsOrDefault<EqCurveProps>();
            // Self-measured root width — never a hand-rolled bounds→signal mirror.
            var measuredW = UseMeasuredWidth(1f);
            if (p is null) return new BoxEl { MinHeight = 250f };

            float measured = measuredW.Value;
            float width = measured > 0.5f ? measured : FallbackWidth;
            int active = Math.Clamp(_active.Value, 0, 9);
            int hover = _hover.Value;
            return new BoxEl { Direction = 1, Children = [Surface(p, MathF.Max(width, 260f), active, hover)] };
        }

        Element Surface(EqCurveProps p, float width, int active, int hover)
        {
            float height = p.Height ?? Math.Clamp(width * 0.38f, 252f, 360f);
            float plotW = MathF.Max(120f, width - PadLeft - PadRight);
            float plotH = MathF.Max(120f, height - PadTop - PadBottom);
            float zeroY = PadTop + GainToY(0f, plotH);
            bool enabled = p.IsEnabled;

            void Commit(int band, float y)
            {
                if (!enabled) return;
                band = Math.Clamp(band, 0, 9);
                _dragBand = band;
                _active.Value = band;
                // Quantized to half a decibel: a continuous drag would write a new float every pointer sample, and the
                // ear cannot hear the difference between 1.47 and 1.5 dB.
                float gain = MathF.Round(YToGain(y - PadTop, plotH) * 2f) * 0.5f;
                p.OnBandChanged(band, gain);
            }

            void Down(Point2 pt) => Commit(NearestBand(pt.X, plotW), pt.Y);
            // Once a drag has claimed a band it KEEPS it: a vertical drag that strays sideways must not hop to the
            // neighbouring band mid-gesture.
            void Drag(Point2 pt) => Commit(_dragBand >= 0 ? _dragBand : NearestBand(pt.X, plotW), pt.Y);
            void Hover(Point2 pt) => _hover.Value = NearestBand(pt.X, plotW);
            void Exit() { _dragBand = -1; _hover.Value = -1; }

            void Key(KeyEventArgs e)
            {
                if (!enabled) return;
                int band = Math.Clamp(_active.Peek(), 0, 9);
                float current = GainAt(p.Gains, band);
                float next;
                switch (e.KeyCode)
                {
                    // Left/Right MOVE the selection; Up/Down change the value. The two axes are the two things a
                    // ten-band curve has, and collapsing them onto one pair is what makes a keyboard user unable to
                    // reach band 9.
                    case Keys.Left: _active.Value = Math.Max(0, band - 1); e.Handled = true; return;
                    case Keys.Right: _active.Value = Math.Min(9, band + 1); e.Handled = true; return;
                    case Keys.Up: next = current + 0.5f; break;
                    case Keys.Down: next = current - 0.5f; break;
                    case Keys.PageUp: next = current + 3f; break;
                    case Keys.PageDown: next = current - 3f; break;
                    case Keys.Home: next = 0f; break;
                    case Keys.End: next = current >= 0f ? MinGain : MaxGain; break;
                    default: return;
                }
                e.Handled = true;
                _active.Value = band;
                p.OnBandChanged(band, Math.Clamp(next, MinGain, MaxGain));
            }

            var kids = new System.Collections.Generic.List<Element>(240);
            GridLines(kids, plotW, plotH);
            FillArea(kids, p.Gains, plotW, plotH, zeroY, enabled);
            CurveLine(kids, p.Gains, plotW, plotH, enabled);
            BandNodes(kids, p.Gains, plotW, plotH, active, hover, enabled);
            BandLabels(kids, plotW, height);
            ValuePill(kids, p.Gains, plotW, plotH, active, width, height);

            return new BoxEl
            {
                Width = width, Height = height, ZStack = true, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card),
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Opacity = enabled ? 1f : 0.58f,
                Role = AutomationRole.Slider,
                Focusable = enabled, IsEnabled = enabled,
                Cursor = enabled ? CursorId.Hand : null,
                // A bare box deliberately wearing the STOCK ring: a slider role on a bordered card is a CONTROL, and the
                // stock template's own −3 is the right answer — which is why this declares it explicitly rather than
                // taking one of the two named app insets.
                FocusVisualMargin = Edges4.All(-3f),
                OnPointerDown = enabled ? Down : null,
                OnDrag = enabled ? Drag : null,
                OnHoverMove = enabled ? Hover : null,
                OnPointerExit = enabled ? Exit : null,
                OnKeyDown = enabled ? Key : null,
                Children = kids.ToArray(),
            };
        }

        static void GridLines(System.Collections.Generic.List<Element> kids, float plotW, float plotH)
        {
            float[] rungs = [12f, 6f, 0f, -6f, -12f];
            for (int i = 0; i < rungs.Length; i++)
            {
                float g = rungs[i];
                bool zero = MathF.Abs(g) < 0.01f;
                float y = PadTop + GainToY(g, plotH);
                kids.Add(new BoxEl
                {
                    Width = 34f, Height = 14f, OffsetX = 2f, OffsetY = y - 7f,
                    Children =
                    [
                        new TextEl(zero ? "0 dB" : g.ToString("+0;-0", System.Globalization.CultureInfo.InvariantCulture))
                        {
                            Size = 10f, Weight = zero ? (ushort)650 : (ushort)400, FontFamily = "Cascadia Code",
                            Color = zero ? Tok.TextSecondary : Tok.TextTertiary,
                        },
                    ],
                });
                // The ZERO line is louder and longer-dashed than the rest: it is the one rung a user aims at.
                DashH(kids, PadLeft, y, plotW,
                      zero ? Tok.TextSecondary with { A = 0.34f } : Tok.StrokeDividerDefault,
                      zero ? 1.2f : 1f, zero ? 5f : 2.5f, zero ? 5f : 5f);
            }
            for (int i = 0; i < 10; i++)
                DashV(kids, PadLeft + (i / 9f) * plotW, PadTop, plotH, Tok.StrokeDividerDefault with { A = 0.58f },
                      1f, 2f, 5f);
        }

        // The area under the curve, as vertical strips. A real filled path would need a polygon primitive the recorder
        // does not carry; at 96 samples the strips are sub-pixel and read as a solid wash.
        static void FillArea(System.Collections.Generic.List<Element> kids, float[] gains, float plotW, float plotH,
                         float zeroY, bool enabled)
        {
            ColorF fill = (enabled ? Tok.AccentDefault : Tok.TextDisabled) with { A = enabled ? 0.15f : 0.10f };
            float lastX = PadLeft, lastY = PadTop + GainToY(Sample(gains, 0f), plotH);
            for (int s = 1; s < Samples; s++)
            {
                float u = (s / (float)(Samples - 1)) * 9f;
                float x = PadLeft + (u / 9f) * plotW;
                float y = PadTop + GainToY(Sample(gains, u), plotH);
                float midX = (lastX + x) * 0.5f, midY = (lastY + y) * 0.5f;
                float stripW = MathF.Max(1.5f, x - lastX + 0.75f);
                float top = MathF.Min(midY, zeroY), h = MathF.Abs(midY - zeroY);
                if (h > 0.5f)
                    kids.Add(new BoxEl { Width = stripW, Height = h, OffsetX = midX - stripW * 0.5f, OffsetY = top, Fill = fill });
                lastX = x; lastY = y;
            }
        }

        // Two passes: a wide, faint UNDERLAY and the crisp line over it. That is what gives the curve a soft edge where
        // the renderer has no stroke feathering of its own.
        static void CurveLine(System.Collections.Generic.List<Element> kids, float[] gains, float plotW, float plotH,
                          bool enabled)
        {
            ColorF under = (enabled ? Tok.AccentDefault : Tok.TextDisabled) with { A = enabled ? 0.28f : 0.22f };
            ColorF main = enabled ? Tok.AccentDefault : Tok.TextDisabled;
            Point2 last = CurvePoint(gains, 0f, plotW, plotH);
            for (int s = 1; s < Samples; s++)
            {
                Point2 pt = CurvePoint(gains, (s / (float)(Samples - 1)) * 9f, plotW, plotH);
                kids.Add(Segment(last, pt, under, 5.5f));
                kids.Add(Segment(last, pt, main, 2.5f));
                last = pt;
            }
        }

        static void BandNodes(System.Collections.Generic.List<Element> kids, float[] gains, float plotW, float plotH,
                          int active, int hover, bool enabled)
        {
            for (int i = 0; i < 10; i++)
            {
                float x = PadLeft + (i / 9f) * plotW;
                float y = PadTop + GainToY(GainAt(gains, i), plotH);
                bool hot = enabled && (i == active || i == hover);
                float d = hot ? NodeHot : NodeRest;
                kids.Add(new BoxEl
                {
                    Width = d, Height = d, OffsetX = x - d * 0.5f, OffsetY = y - d * 0.5f,
                    Corners = Radii.Circle(d),
                    Fill = enabled ? Tok.FillControlSolid : Tok.FillControlDisabled,
                    BorderWidth = hot ? 3f : 2.5f,
                    BorderBrush = enabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
                    BorderColor = enabled ? Tok.AccentDefault : Tok.TextDisabled,
                    Shadow = hot ? Elevation.Flyout : null,
                    BrushTransitionMs = Design.Motion.Faster,
                });
            }
        }

        // Below ~420 DIP of plot the ten labels collide, so every other one is dropped — but the LAST is always kept,
        // because the right edge of the band range is what tells the user the axis is complete.
        static void BandLabels(System.Collections.Generic.List<Element> kids, float plotW, float height)
        {
            bool dense = plotW < 420f;
            for (int i = 0; i < 10; i++)
            {
                if (dense && (i % 2) != 0 && i != 9) continue;
                kids.Add(new BoxEl
                {
                    Width = 42f, Height = 14f,
                    OffsetX = PadLeft + (i / 9f) * plotW - 21f, OffsetY = height - 24f,
                    Children = [new TextEl(EqualizerBands[i])
                        { Size = 10f, FontFamily = "Cascadia Code", Color = Tok.TextTertiary }],
                });
            }
        }

        static void ValuePill(System.Collections.Generic.List<Element> kids, float[] gains, float plotW, float plotH,
                              int active, float width, float height)
        {
            if ((uint)active >= 10u) return;
            float gain = GainAt(gains, active);
            float x = PadLeft + (active / 9f) * plotW;
            float y = PadTop + GainToY(gain, plotH);
            const float pillW = 104f;
            // Clamped INSIDE the surface on both axes: a pill for band 0 or band 9 would otherwise hang off the card.
            float px = Math.Clamp(x - pillW * 0.5f, 6f, MathF.Max(6f, width - pillW - 6f));
            float py = Math.Clamp(y - 42f, 6f, MathF.Max(6f, height - 58f));
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 5f,
                Width = pillW, Height = 28f, OffsetX = px, OffsetY = py,
                Padding = new Edges4(8f, 0f, 8f, 0f),
                Corners = CornerRadius4.All(14f),
                Fill = Tok.FillControlDefault,
                BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                Shadow = Elevation.Tooltip,
                Children =
                [
                    new TextEl(EqualizerBands[active])
                        { Size = 11f, FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Shrink = 0f },
                    new TextEl(FormatDb(gain))
                        { Size = 12f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1,
                          Trim = TextTrim.CharacterEllipsis },
                ],
            });
        }

        static Element Segment(Point2 a, Point2 b, ColorF color, float thickness)
            => new PolylineStrokeEl
            {
                Width = MathF.Abs(b.X - a.X) + thickness,
                Height = MathF.Abs(b.Y - a.Y) + thickness,
                OffsetX = MathF.Min(a.X, b.X) - thickness * 0.5f,
                OffsetY = MathF.Min(a.Y, b.Y) - thickness * 0.5f,
                P0 = new Point2(a.X - MathF.Min(a.X, b.X) + thickness * 0.5f,
                                a.Y - MathF.Min(a.Y, b.Y) + thickness * 0.5f),
                P1 = new Point2(b.X - MathF.Min(a.X, b.X) + thickness * 0.5f,
                                b.Y - MathF.Min(a.Y, b.Y) + thickness * 0.5f),
                PointCount = 2, Color = color, Thickness = thickness, RoundCaps = true,
            };

        static void DashH(System.Collections.Generic.List<Element> kids, float x, float y, float w, ColorF color,
                          float h, float dash, float gap)
        {
            for (float dx = 0f; dx < w; dx += dash + gap)
                kids.Add(new BoxEl { Width = MathF.Min(dash, w - dx), Height = h, OffsetX = x + dx, OffsetY = y, Fill = color });
        }

        static void DashV(System.Collections.Generic.List<Element> kids, float x, float y, float h, ColorF color,
                          float w, float dash, float gap)
        {
            for (float dy = 0f; dy < h; dy += dash + gap)
                kids.Add(new BoxEl { Width = w, Height = MathF.Min(dash, h - dy), OffsetX = x, OffsetY = y + dy, Fill = color });
        }

        static Point2 CurvePoint(float[] gains, float u, float plotW, float plotH)
            => new(PadLeft + (u / 9f) * plotW, PadTop + GainToY(Sample(gains, u), plotH));

        /// <summary>A CATMULL-ROM sample between the band nodes, clamped to the gain range. A straight polyline would
        /// read as a set of hinges rather than as a filter response; a spline is what makes ten discrete bands look like
        /// one curve.</summary>
        static float Sample(float[] gains, float u)
        {
            if (u <= 0f) return GainAt(gains, 0);
            if (u >= 9f) return GainAt(gains, 9);
            int i = Math.Clamp((int)MathF.Floor(u), 0, 8);
            float t = u - i;
            float p0 = GainAt(gains, Math.Max(0, i - 1));
            float p1 = GainAt(gains, i);
            float p2 = GainAt(gains, i + 1);
            float p3 = GainAt(gains, Math.Min(9, i + 2));
            float t2 = t * t, t3 = t2 * t;
            return Math.Clamp(
                0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                        + (-p0 + 3f * p1 - 3f * p2 + p3) * t3),
                MinGain, MaxGain);
        }

        static int NearestBand(float x, float plotW)
            => Math.Clamp((int)MathF.Round(Math.Clamp((x - PadLeft) / MathF.Max(plotW, 1f), 0f, 1f) * 9f), 0, 9);

        static float GainAt(float[] gains, int band)
            => (uint)band < 10u && band < gains.Length ? Math.Clamp(gains[band], MinGain, MaxGain) : 0f;

        static float GainToY(float gain, float plotH)
            => (MaxGain - Math.Clamp(gain, MinGain, MaxGain)) / (MaxGain - MinGain) * plotH;

        static float YToGain(float y, float plotH)
            => Math.Clamp(MaxGain - (Math.Clamp(y, 0f, plotH) / MathF.Max(plotH, 1f)) * (MaxGain - MinGain),
                          MinGain, MaxGain);

        static string FormatDb(float gain)
            => gain.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
    }
}
