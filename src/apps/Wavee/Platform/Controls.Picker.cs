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
    /// pill on the band under the pointer — or, with no pointer on the curve, on the ACTIVE band the arrow keys edit.
    ///
    /// <para>RE-PUSHED LIVE PROPS: the record-equality gate re-renders the core only when the gains, the handler, the
    /// enabled flag or the height actually change — so a settings page that re-renders for an unrelated row does not
    /// rebuild the curve.</para>
    ///
    /// <para>SIZING: the curve FILLS the width its parent gives it and measures that width. Its root is stretched, never
    /// content-sized, so the measurement cannot feed back into itself. Put it where the flex algorithm sizes the slot — a
    /// column child, or a row child with <c>Grow 1 / Basis 0 / MinWidth 0</c>. It has no minimum width of its own: a
    /// narrow lane thins the band labels instead (<see cref="EqCurveGeometry.BandLabelStride"/>). Every coordinate it
    /// draws or reads comes from <see cref="EqCurveGeometry"/>.</para>
    ///
    /// <para><paramref name="height"/> null takes the width-derived height; a card with its own budget fixes it
    /// rather than letting the curve claim whatever its width would imply.</para></summary>
    public static Element EqualizerCurve(float[] gains, Action<int, float> onBandChanged, bool isEnabled = true,
                                         float? height = null)
        => Embed.Comp(new EqCurveProps(gains, onBandChanged, isEnabled, height), static () => new EqCurveCore());

    /// <inheritdoc cref="EqualizerCurve"/>
    public sealed record EqCurveProps(float[] Gains, Action<int, float> OnBandChanged, bool IsEnabled, float? Height);

    /// <summary>The curve.
    /// <code>
    /// root      stretched to the lane and MEASURED · Height · the card plate, the focus stop and every handler
    ///  └ plot   Width × Height (the measured width), ZStack, clip — the relayout FIREWALL
    ///     ├ grid    EqGridLayer: dB rungs + labels, band gridlines + labels (re-renders only when the size moves)
    ///     ├ fill    95 strips, curve → 0 dB
    ///     ├ curve   95 segments × 2 (underlay + line)
    ///     ├ nodes   10
    ///     └ badge   the value pill, on <see cref="EqCurveRules.BadgeBand"/>
    /// </code>
    /// The ROOT carries no width: the plate is always exactly the lane, and that is the width it reports. The PLOT carries
    /// one: a fixed-size clipped box is what keeps a per-sample drag re-render from re-laying-out the settings page. The
    /// root's own height change (width-driven, rare) is the one thing that flows through the page.</summary>
    sealed class EqCurveCore : Component
    {
        const float NodeRest = 14f, NodeHot = 18f;
        const int Samples = 96;
        const float SampleStep = EqCurveGeometry.LastBand / (float)(Samples - 1);

        readonly Signal<int> _active = new(EqCurveRules.DefaultActiveBand);
        readonly Signal<int> _hover = new(-1);
        int _dragBand = -1;

        // What the handlers read at EVENT time, never a render-time capture: the latest props and the measured-width
        // signal. That signal is written during LAYOUT, ahead of the re-render it schedules, so a pointer sample landing
        // between the two maps through the size the surface has now, not the one last drawn. The handlers themselves are
        // allocated once, here, not per render.
        EqCurveProps? _props;
        IReadSignal<float>? _width;
        readonly Action<Point2> _onDown, _onDrag, _onHover;
        readonly Action _onExit;
        readonly Action<KeyEventArgs> _onKey;

        public EqCurveCore()
        {
            _onDown = Down;
            _onDrag = Drag;
            _onHover = Hover;
            _onExit = Exit;
            _onKey = Key;
        }

        public override Element Render()
        {
            var p = UsePropsOrDefault<EqCurveProps>();
            var measuredW = UseMeasuredWidth(1f);
            _props = p;
            _width = measuredW;
            if (p is null) return new BoxEl { AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = 250f };

            float measured = measuredW.Value;
            bool enabled = p.IsEnabled;
            // The first frame is unmeasured: the plate at the seed height and NO plot. A plot drawn at a guessed width is
            // what used to pin this curve at 720 DIP — the root wrapped that guess and then measured it back.
            bool ready = measured > 0.5f;
            var g = ready
                ? EqCurveGeometry.For(measured, p.Height)
                : EqCurveGeometry.For(0f, p.Height ?? EqCurveGeometry.AutoHeight(EqCurveGeometry.SeedWidth));
            int active = Math.Clamp(_active.Value, 0, EqCurveGeometry.LastBand);
            int hover = _hover.Value;

            return new BoxEl
            {
                Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Height = g.Height, ClipToBounds = true,
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
                OnPointerDown = enabled ? _onDown : null,
                OnDrag = enabled ? _onDrag : null,
                OnHoverMove = enabled ? _onHover : null,
                OnPointerExit = enabled ? _onExit : null,
                OnKeyDown = enabled ? _onKey : null,
                Children = ready ? new Element[] { Plot(p, g, active, hover, enabled) } : Array.Empty<Element>(),
            };
        }

        static Element Plot(EqCurveProps p, EqCurveGeometry g, int active, int hover, bool enabled)
        {
            var kids = new List<Element>(2 + 3 * (Samples - 1) + EqCurveGeometry.BandCount)
            {
                Embed.Comp(new EqGridProps(g.Width, g.Height), static () => new EqGridLayer()),
            };
            FillArea(kids, p.Gains, g, enabled);
            CurveLine(kids, p.Gains, g, enabled);
            BandNodes(kids, p.Gains, g, active, hover, enabled);
            int badge = EqCurveRules.BadgeBand(hover, active, enabled);
            if (badge >= 0) kids.Add(ValueBadge(p.Gains, g, badge));
            return new BoxEl
            {
                Width = g.Width, Height = g.Height, Shrink = 0f, ZStack = true, ClipToBounds = true,
                Children = kids.ToArray(),
            };
        }

        // ── input ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The geometry of the surface as it is NOW (see the field comment above).</summary>
        EqCurveGeometry Live() => EqCurveGeometry.For(_width?.Peek() ?? 0f, _props?.Height);

        /// <summary>A press claims the band nearest to it for the whole gesture.</summary>
        void Down(Point2 pt)
        {
            var g = Live();
            if (g.PlotWidth <= 0f) return;
            _dragBand = g.NearestBand(pt.X);
            Commit(g, _dragBand, pt.Y);
        }

        /// <summary>Once a press has claimed a band the drag KEEPS it: a vertical drag that strays sideways, or out of the
        /// surface, must not hop to the neighbouring band mid-gesture.</summary>
        void Drag(Point2 pt)
        {
            var g = Live();
            if (g.PlotWidth <= 0f) return;
            Commit(g, _dragBand >= 0 ? _dragBand : g.NearestBand(pt.X), pt.Y);
        }

        /// <summary>The band under the gesture becomes the active band AND the hovered one, so the badge rides the node
        /// being dragged even when the pointer has strayed off its column.</summary>
        void Commit(EqCurveGeometry g, int band, float y)
        {
            if (_props is not { IsEnabled: true } p) return;
            band = Math.Clamp(band, 0, EqCurveGeometry.LastBand);
            _active.Value = band;
            _hover.Value = band;
            p.OnBandChanged(band, g.SnappedGainAt(y));
        }

        void Hover(Point2 pt)
        {
            var g = Live();
            if (g.PlotWidth > 0f) _hover.Value = g.NearestBand(pt.X);
        }

        void Exit() => _hover.Value = -1;

        void Key(KeyEventArgs e)
        {
            if (_props is not { IsEnabled: true } p) return;
            int band = Math.Clamp(_active.Peek(), 0, EqCurveGeometry.LastBand);
            float current = EqCurveRules.GainAt(p.Gains, band);
            float next;
            switch (e.KeyCode)
            {
                // Left/Right MOVE the selection; Up/Down change the value. The two axes are the two things a ten-band
                // curve has, and collapsing them onto one pair is what makes a keyboard user unable to reach band 9.
                case Keys.Left: Select(Math.Max(0, band - 1)); e.Handled = true; return;
                case Keys.Right: Select(Math.Min(EqCurveGeometry.LastBand, band + 1)); e.Handled = true; return;
                case Keys.Up: next = current + 0.5f; break;
                case Keys.Down: next = current - 0.5f; break;
                case Keys.PageUp: next = current + 3f; break;
                case Keys.PageDown: next = current - 3f; break;
                case Keys.Home: next = 0f; break;
                case Keys.End: next = current >= 0f ? EqCurveGeometry.MinGain : EqCurveGeometry.MaxGain; break;
                default: return;
            }
            e.Handled = true;
            Select(band);
            p.OnBandChanged(band, Math.Clamp(next, EqCurveGeometry.MinGain, EqCurveGeometry.MaxGain));
        }

        /// <summary>The keyboard owns the badge: a pointer still resting over another band must not hold it there.</summary>
        void Select(int band)
        {
            _active.Value = band;
            _hover.Value = -1;
        }

        // ── the dynamic layers ───────────────────────────────────────────────────────────────────────────────────────

        // The area under the curve, as vertical strips. A real filled path would need a polygon primitive the recorder
        // does not carry; at 96 samples the strips are sub-pixel and read as a solid wash. ONE strip per sample interval,
        // always (a zero-height strip draws nothing): a strip count that varied with the gains would shift every later
        // child's index and turn a drag sample into a remount of the curve.
        static void FillArea(List<Element> kids, float[] gains, EqCurveGeometry g, bool enabled)
        {
            ColorF fill = (enabled ? Tok.AccentDefault : Tok.TextDisabled) with { A = enabled ? 0.15f : 0.10f };
            float zeroY = g.ZeroY;
            float x0 = g.X(0f);
            for (int s = 1; s < Samples; s++)
            {
                float x1 = g.X(s * SampleStep);
                float y = g.Y(EqCurveRules.Sample(gains, (s - 0.5f) * SampleStep));
                kids.Add(new BoxEl
                {
                    Width = x1 - x0 + 0.75f, Height = MathF.Abs(y - zeroY),
                    OffsetX = x0, OffsetY = MathF.Min(y, zeroY), Fill = fill,
                });
                x0 = x1;
            }
        }

        // Two passes: a wide, faint UNDERLAY and the crisp line over it. That is what gives the curve a soft edge where
        // the renderer has no stroke feathering of its own.
        static void CurveLine(List<Element> kids, float[] gains, EqCurveGeometry g, bool enabled)
        {
            ColorF under = (enabled ? Tok.AccentDefault : Tok.TextDisabled) with { A = enabled ? 0.28f : 0.22f };
            ColorF main = enabled ? Tok.AccentDefault : Tok.TextDisabled;
            Point2 last = CurvePoint(gains, 0f, g);
            for (int s = 1; s < Samples; s++)
            {
                Point2 pt = CurvePoint(gains, s * SampleStep, g);
                kids.Add(Segment(last, pt, under, 5.5f));
                kids.Add(Segment(last, pt, main, 2.5f));
                last = pt;
            }
        }

        static void BandNodes(List<Element> kids, float[] gains, EqCurveGeometry g, int active, int hover, bool enabled)
        {
            for (int i = 0; i < EqCurveGeometry.BandCount; i++)
            {
                float x = g.BandX(i);
                float y = g.Y(EqCurveRules.GainAt(gains, i));
                bool hot = EqCurveRules.IsHot(i, hover, active, enabled);
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

        /// <summary>The value pill for <paramref name="band"/>: that band's frequency and gain, centred above its node and
        /// clamped inside the plot (<see cref="EqCurveGeometry.BadgeAt"/>).</summary>
        static Element ValueBadge(float[] gains, EqCurveGeometry g, int band)
        {
            float gain = EqCurveRules.GainAt(gains, band);
            var (x, y) = g.BadgeAt(band, gain);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 5f,
                Width = EqCurveGeometry.BadgeWidth, Height = EqCurveGeometry.BadgeHeight, OffsetX = x, OffsetY = y,
                Padding = new Edges4(8f, 0f, 8f, 0f),
                Corners = CornerRadius4.All(EqCurveGeometry.BadgeHeight * 0.5f),
                Fill = Tok.FillControlDefault,
                BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                Shadow = Elevation.Tooltip,
                Children =
                [
                    Design.Type.MicroMeta(EqualizerBands[band])
                        with { FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Shrink = 0f },
                    new TextEl(EqCurveRules.DbText(gain))
                        { Size = 12f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1,
                          Trim = TextTrim.CharacterEllipsis },
                ],
            };
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

        /// <summary>A spline point through THE x and y mappings — the same ones the nodes use, so the curve passes through
        /// every node (<see cref="EqCurveRules.Sample"/> at u = i is band i's gain).</summary>
        static Point2 CurvePoint(float[] gains, float u, EqCurveGeometry g)
            => new(g.X(u), g.Y(EqCurveRules.Sample(gains, u)));
    }

    /// <summary>The grid layer's props: the surface size and nothing else, so its thousand-odd dash boxes are rebuilt only
    /// when the size moves — a hover, a drag sample or a gain change re-renders the curve and re-uses this layer.</summary>
    sealed record EqGridProps(float Width, float Height);

    /// <summary>The static half of the curve: the five dB rungs with their right-aligned labels, the ten band gridlines,
    /// and the band labels CENTRED under them (the text is centred in a box centred on the gridline — a box centred on
    /// the line with its text left-aligned is what put every label half a box to the left of its node).</summary>
    sealed class EqGridLayer : Component
    {
        public override Element Render()
        {
            var p = UseProps<EqGridProps>();
            var g = EqCurveGeometry.For(p.Width, p.Height);
            var kids = new List<Element>(512);
            Rungs(kids, g);
            BandLines(kids, g);
            BandLabels(kids, g);
            return new BoxEl { Width = g.Width, Height = g.Height, ZStack = true, Children = kids.ToArray() };
        }

        static void Rungs(List<Element> kids, EqCurveGeometry g)
        {
            var rungs = EqCurveRules.GainRungs;
            for (int r = 0; r < rungs.Length; r++)
            {
                float gain = rungs[r];
                bool zero = MathF.Abs(gain) < 0.01f;
                kids.Add(new BoxEl
                {
                    Width = g.GainLabelWidth, Height = EqCurveGeometry.LabelHeight, OffsetY = g.GainLabelTop(gain),
                    Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(EqCurveRules.GainRungLabels[r])
                        {
                            Size = 10f, Weight = zero ? (ushort)650 : (ushort)400, FontFamily = "Cascadia Code",
                            Color = zero ? Tok.TextSecondary : Tok.TextTertiary, MaxLines = 1,
                        },
                    ],
                });
                // The ZERO line is louder and longer-dashed than the rest: it is the one rung a user aims at. Each line is
                // centred ON its y, like the nodes are.
                float thick = zero ? 1.2f : 1f;
                DashH(kids, g.PlotLeft, g.Y(gain) - thick * 0.5f, g.PlotWidth,
                      zero ? Tok.TextSecondary with { A = 0.34f } : Tok.StrokeDividerDefault,
                      thick, zero ? 5f : 2.5f, 5f);
            }
        }

        static void BandLines(List<Element> kids, EqCurveGeometry g)
        {
            ColorF line = Tok.StrokeDividerDefault with { A = 0.58f };
            for (int i = 0; i < EqCurveGeometry.BandCount; i++)
                DashV(kids, g.BandX(i) - 0.5f, g.PlotTop, g.PlotHeight, line, 1f, 2f, 5f);
        }

        static void BandLabels(List<Element> kids, EqCurveGeometry g)
        {
            for (int i = 0; i < EqCurveGeometry.BandCount; i++)
            {
                if (!g.ShowsBandLabel(i)) continue;
                kids.Add(new BoxEl
                {
                    Width = EqCurveGeometry.BandLabelWidth, Height = EqCurveGeometry.LabelHeight,
                    OffsetX = g.BandLabelLeft(i), OffsetY = g.BandLabelTop,
                    Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                    Children = [new TextEl(EqualizerBands[i])
                        { Size = 10f, FontFamily = "Cascadia Code", Color = Tok.TextTertiary, MaxLines = 1 }],
                });
            }
        }

        static void DashH(List<Element> kids, float x, float y, float w, ColorF color, float h, float dash, float gap)
        {
            for (float dx = 0f; dx < w; dx += dash + gap)
                kids.Add(new BoxEl { Width = MathF.Min(dash, w - dx), Height = h, OffsetX = x + dx, OffsetY = y, Fill = color });
        }

        static void DashV(List<Element> kids, float x, float y, float h, ColorF color, float w, float dash, float gap)
        {
            for (float dy = 0f; dy < h; dy += dash + gap)
                kids.Add(new BoxEl { Width = w, Height = MathF.Min(dash, h - dy), OffsetX = x, OffsetY = y + dy, Fill = color });
        }
    }
}
