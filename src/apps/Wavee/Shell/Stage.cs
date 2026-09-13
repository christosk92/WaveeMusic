// ── Shell/Stage.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// StageLayout, StageArm, StageInk, StagePane
//
// Role: CORE
// Owner: K
// Wave: 4
// Budget: 500 lines
// Spec: ch 21 §9.5 (which restates its own earlier 400: it omitted StageInk; 578 lines of 0.2.9, so 500 is a floor)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE IMMERSIVE STAGE'S CORE — and nothing that paints:
//
//   • `Stage.Layout`  the PURE space allocator: wide⇄compact on ONE threshold with hysteresis, the HEIGHT fold ladder
//     (device line → volume row → demote), the art as a quantised residual, and the whole scrim alpha ladder.
//   • `Stage.Pane`    which pane the right-hand region shows. A `static` signal on purpose: "last pane wins" for the
//     SESSION — not a setting (nothing is written to disk) and not per-track state.
//
// STAGEARM / STAGEINK ARE `Platform/Design.cs`'s (`Design.StageArm`, `Design.StageInk`, owner L). §2 homed them here,
// and this file carried a copy until owner L landed the real one in the same wave — WITH the design system's own
// accent-as-ink solve (`Palette.TextInk`) that a CORE file here cannot reach. One authority: the copy is deleted, the
// stage renderers (`Stage.UI.cs`, `Lyrics.UI.cs`) take every rung from `Design.StageInk`, and `Layout.ScrimBaseA` below
// REFERENCES `Design.StageArm.ScrimBaseA` rather than restating 0.46 (ch 00 §4.4) — the accent ground and the scrim
// plateau are then one number by construction.
//
// WHY THE ALPHAS LIVE IN `Layout` AND THE COLOURS IN THE ARM. The scrim ladder is theme-INVARIANT, and that is a result
// rather than an assumption: compositing happens in sRGB-encoded space, whose transfer curve is not symmetric —
// mixing toward BLACK at a partial alpha destroys far more perceptual luminance than mixing toward white — so at the
// plateau the LIGHT arm clears a higher contrast ratio at every rung than the dark arm already ships (ch 21 §4.4's
// table: 4.29 vs 3.57 primary, 3.55 vs 2.89 secondary, 6.70 vs 5.70 under the column shade). One ladder is correct
// for both, and `StageTests` asserts exactly that rather than trusting this comment. If tuning ever disagrees, the ONE
// sanctioned addition is a `ScrimBaseLightA` beside `ScrimBaseA`: **alphas may live in `Layout`, colours may not.**
//
// Rules: `Layout` is `System`-only (apart from the one referenced constant) so the tests drive the real allocator
// rather than a copy of its arithmetic — no `Element`, no component, no entity read. No allocation after warm-up (P8);
// no LINQ, no closures, no async, no boxing (P9).

using FluentGpu.Signals;

namespace Wavee;

public static partial class Stage
{
    // ── 1. the controls the column can fold ─────────────────────────────────────────────────────────────────────────

    /// <summary>The controls the stage's identity column can FOLD away when it runs out of room. A folded control is
    /// never LOST — it moves address into the compact header's "…" overflow, exactly like the player bar's own
    /// ladder.</summary>
    [Flags]
    public enum Control : byte
    {
        None = 0,
        /// <summary>The shuffle satellite (32-DIP, latches accent).</summary>
        Shuffle = 1 << 0,
        /// <summary>The repeat satellite (32-DIP, latches accent).</summary>
        Repeat = 1 << 1,
        /// <summary>The volume row (thin slider over the scrim).</summary>
        Volume = 1 << 2,
        /// <summary>The output-device line under the volume row.</summary>
        OutputDevice = 1 << 3,
    }

    // ── 2. the allocator ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The PURE space allocator behind the immersive stage. It answers one question: at this surface width
    /// and column height, is the stage in its WIDE two-region shape (a fixed identity + transport column beside the
    /// pane region) or its COMPACT one-column shape (an identity HEADER row above a full-width pane) — and, if wide,
    /// how much does the column carry?
    ///
    /// <para><b>One structure, one reflow flag.</b> There are not two stage layouts; there is one tree whose row
    /// direction, art size, transport sizes and folded-control set all fall out of <see cref="Wide"/>. Everything a
    /// renderer needs is a field on this struct, so a call site never re-derives a breakpoint.</para>
    ///
    /// <para><b>Hysteresis, per rung, in both axes.</b> A demotion or a fold is IMMEDIATE — nothing may clip while the
    /// window contracts — and a promotion needs <see cref="PromotionHysteresisW"/> while an unfold needs
    /// <see cref="FoldHysteresisH"/>. So dragging a window edge across a boundary flips each rung exactly ONCE per
    /// crossing instead of thrashing a surface that owns a mounted lyrics view and a mounted queue.</para></summary>
    public readonly record struct Layout(
        bool Wide,
        float ColumnWidth,
        float ArtSize,
        float PlayBox,
        float StepBox,
        float SatelliteBox,
        Control Folded)
    {
        // ── the one threshold ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>At or above this surface width the stage is WIDE. 600 is the width at which a 352-DIP column plus
        /// its falloff still leaves the pane region a readable measure; under it the pane would be narrower than the
        /// column beside it.</summary>
        public const float WideEnterW = 600f;

        /// <summary>The reserve a PROMOTION needs on top of <see cref="WideEnterW"/>. Demotion takes none.</summary>
        public const float PromotionHysteresisW = 40f;

        // ── the wide shape ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The identity column's DESIGNED width — what the user sees as "the column". Fixed, never fluid: it
        /// carries a 300-square cover and an authored transport cluster, so stretching it would only stretch its
        /// whitespace.</summary>
        public const float WideColumnW = 352f;

        /// <summary>The air between the identity column and the pane region, spent as the stage band's <c>Gap</c>.
        /// A <c>Gap</c>, not a margin: the flex layout accounts for it in BOTH measure and arrange, so the band's
        /// arithmetic and the renderer's can never disagree about it. It replaced a 120-DIP "falloff" added to the
        /// column BOX and then padded back out by the renderer — 120 DIP of dead padding inside the column rather than
        /// air between the two regions, which put the first lyric glyph ~390 DIP from the artwork.</summary>
        public const float RegionGapW = 56f;

        /// <summary>The column's internal gutter. Authored here rather than in the renderer so
        /// <see cref="ColumnContentW"/> (and the volume track that spans it) is derived arithmetic rather than a
        /// second copy of the same number.</summary>
        public const float ColumnPadX = 24f;

        /// <summary>What the column's content actually gets: 352 − 2 × 24 = 304 — the 300 cover with a hairline to
        /// spare, AND the span every full-width row in the column (the seek block, the volume rail) must fill.</summary>
        public const float ColumnContentW = WideColumnW - 2f * ColumnPadX;

        /// <summary>The cover on the wide stage.</summary>
        public const float WideArtW = 300f;
        /// <summary>The cover in the compact header row (the app's Thumb64 rung, restated to keep this type
        /// engine-free).</summary>
        public const float CompactArtW = 64f;

        /// <summary>The filled circular play/pause — the ONE filled control on the stage.</summary>
        public const float WidePlayBoxW = 56f;
        /// <inheritdoc cref="WidePlayBoxW"/>
        public const float CompactPlayBoxW = 40f;

        /// <summary>Previous / next.</summary>
        public const float WideStepBoxW = 40f;
        /// <inheritdoc cref="WideStepBoxW"/>
        public const float CompactStepBoxW = 32f;

        /// <summary>Shuffle / repeat — the transport's two SATELLITES. One rung below prev/next in both shapes,
        /// because they are modes rather than actions.</summary>
        public const float SatelliteBoxW = 32f;

        // ── the column's HEIGHT budget ──────────────────────────────────────────────────────────────────────────────
        //
        // The column is a fixed stack of authored rows, and on a short window that stack simply did not fit: centred
        // justification clamps its leftover at 0, so the surplus fell off the BOTTOM and the output-device line was
        // clipped away — silently, because a clipped control looks exactly like a control that was never designed.
        //
        // THE RULE: THE LADDER IS LOSSY IN THE ART, NEVER IN A CONTROL. A control that falls off the bottom is
        // unreachable; a smaller cover is merely smaller. So the ART is a RESIDUAL of the height left after the
        // chrome, and only when the art cannot keep MinArtW does a control fold — device line first, then the volume
        // row, then the whole shape demotes to Compact.
        //
        // Every number below is MEASURED off the real arranged column, not estimated: art 300, identity row 32, seek
        // block 50, transport = PlayBox, volume 32, device 24, with StackGapH between the big blocks and RowGapS
        // between the tight ones. Non-art chrome totals exactly 320 with everything shown.
        //
        // NOTE the satellites are deliberately NOT a height rung: shuffle/repeat sit INSIDE the transport row, so
        // folding them saves zero vertical space. They stay what they always were — a WIDTH concern.

        /// <summary>The column's vertical gutter, spent at both ends.</summary>
        public const float ColumnPadY = 28f;
        /// <summary>The gap between the column's big blocks.</summary>
        public const float StackGapH = 18f;
        /// <summary>The tighter gap (the app's Spacing.S, restated so this type stays engine-free).</summary>
        public const float RowGapS = 8f;

        /// <summary>Title + artist + heart + "…".</summary>
        public const float IdentityRowH = 32f;
        /// <summary>The seek bar plus the elapsed/remaining row under it.</summary>
        public const float SeekBlockH = 50f;
        /// <summary>The volume rail row.</summary>
        public const float VolumeRowH = 32f;
        /// <summary>The output-device line.</summary>
        public const float DeviceRowH = 24f;

        /// <summary>The smallest cover the stage will show before it starts folding CONTROLS instead. Below this the
        /// cover stops reading as the surface's subject and the surface stops being worth its own window.</summary>
        public const float MinArtW = 168f;

        /// <summary>The art size is quantised to this. NOT cosmetic: the surface's reflow signal is value inequality,
        /// so an unquantised residual would re-render the surface — and its mounted lyrics view — on EVERY vertical
        /// resize pixel, destroying the one-coarse-flag property the width ladder is built on. On the 4-DIP grid a
        /// drag re-renders at most once per 4 DIP.</summary>
        public const float ArtQuantum = 4f;

        /// <summary>The reserve an UNFOLD needs, per rung. Folding takes none — nothing may clip while the window
        /// contracts — exactly <see cref="PromotionHysteresisW"/>'s asymmetry, applied to the height axis.</summary>
        public const float FoldHysteresisH = 24f;

        /// <summary>The two controls the HEIGHT ladder may fold. (<see cref="CompactFold"/> is a superset; the two
        /// compose as a union.)</summary>
        public const Control HeightFoldable = Control.Volume | Control.OutputDevice;

        /// <summary>Everything the column spends vertically EXCEPT the cover — the term the art is the residual of.</summary>
        public static float ColumnChromeH(Control folded, float playBox) =>
            2f * ColumnPadY
            + StackGapH + IdentityRowH
            + StackGapH + SeekBlockH
            + RowGapS + playBox
            + ((folded & Control.Volume) == 0 ? StackGapH + VolumeRowH : 0f)
            + ((folded & Control.OutputDevice) == 0 ? RowGapS + DeviceRowH : 0f);

        /// <summary>Can the column carry <see cref="MinArtW"/> at this height with this much folded?</summary>
        static bool FitsArt(float availH, Control folded, float playBox) =>
            availH - ColumnChromeH(folded, playBox) >= MinArtW;

        /// <summary>The height at which the WIDE column stops being viable at all — DERIVED, never authored, so it
        /// cannot drift from the ladder it describes.</summary>
        public static float WideEnterH => ColumnChromeH(HeightFoldable, WidePlayBoxW) + MinArtW;

        /// <summary>What the compact shape folds into the header's "…". Shuffle and repeat lose their satellites, and
        /// the volume row and the device line lose their space entirely — all four still reachable, one tap deeper.</summary>
        public const Control CompactFold =
            Control.Shuffle | Control.Repeat | Control.Volume | Control.OutputDevice;

        // ── the scrim ladder (alphas only — see the file header) ────────────────────────────────────────────────────
        //
        // EDGE-INVISIBILITY IS THE WHOLE MECHANISM. A veil is invisible iff it either (a) reaches its own boundary at
        // alpha 0 after a feather long enough that the ramp is below the eye's banding threshold, or (b) ends at a
        // WINDOW edge, where there is no "outside" to contrast with. Every stop below satisfies one of the two, and
        // `StageTests` asserts it in DIP rather than in stop fractions.

        /// <summary>The base scrim — what the artwork is dimmed to across the whole surface. One flat value, no theme
        /// branch: this is the ground everything else deepens FROM.</summary>
        public const float ScrimBaseA = Design.StageArm.ScrimBaseA;

        /// <summary>The scrim at the very top of the body band, under the caption cluster.</summary>
        public const float ScrimTopA = 0.76f;

        /// <summary>…and at the very bottom, under the pivot band and the transport.</summary>
        public const float ScrimBottomA = 0.70f;

        /// <summary>Where the top deepening has fully resolved into <see cref="ScrimBaseA"/>, as a FRACTION of the
        /// body height. A fraction, not a DIP box, is what makes it edge-free by construction: at any window the
        /// feather is hundreds of DIP long, where the deleted 88-DIP top veil was a band you could point at.</summary>
        public const float ScrimTopStop = 0.22f;

        /// <summary>Where the bottom deepening starts, same units.</summary>
        public const float ScrimBottomStop = 0.62f;

        /// <summary>How much darker the scrim goes behind the identity column, on top of <see cref="ScrimBaseA"/>.</summary>
        public const float ColumnShadeA = 0.26f;

        /// <summary>How far the column shade keeps fading past <see cref="WideColumnW"/> before it reaches exactly 0.
        /// The shade is a full-bleed PAINT layer with no layout consequence at all, so its feather is free, and a
        /// 260-DIP ramp to zero has no locatable edge.</summary>
        public const float ColumnShadeFalloffW = 260f;

        /// <summary>The column shade layer's width. Not a layout width.</summary>
        public const float ColumnShadeW = WideColumnW + ColumnShadeFalloffW;

        /// <summary>The stop at which the shade stops holding <see cref="ColumnShadeA"/> and starts feathering.</summary>
        public static float ColumnShadeHoldStop => WideColumnW / ColumnShadeW;

        /// <summary>One mid stop inside the feather so the ramp is a CURVE rather than a straight line — a linear
        /// alpha ramp is exactly the shape the eye resolves as a Mach band.</summary>
        public static float ColumnShadeMidStop => ColumnShadeHoldStop + 0.55f * (1f - ColumnShadeHoldStop);

        /// <summary>The mid stop's alpha, as a fraction of <see cref="ColumnShadeA"/>.</summary>
        public const float ColumnShadeMidFrac = 0.34f;

        /// <summary>How much darker the scrim goes under the QUEUE pane while it is up — its rows carry hover glass,
        /// and glass needs something under it. Feathers to 0 on the pane's left; its deep end is the window edge.</summary>
        public const float PaneShadeA = 0.24f;

        /// <summary>Where the queue pane's shade has finished coming up out of 0.</summary>
        public const float PaneShadeFeatherStop = 0.22f;

        // ── the two shapes ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The wide stage: nothing folded.</summary>
        public static readonly Layout WideStage = new(
            Wide: true, ColumnWidth: WideColumnW, ArtSize: WideArtW,
            PlayBox: WidePlayBoxW, StepBox: WideStepBoxW, SatelliteBox: SatelliteBoxW, Folded: Control.None);

        /// <summary>The compact stage: the identity column is a header row and <see cref="CompactFold"/> is in the
        /// "…".</summary>
        public static readonly Layout CompactStage = new(
            Wide: false, ColumnWidth: 0f, ArtSize: CompactArtW,
            PlayBox: CompactPlayBoxW, StepBox: CompactStepBoxW, SatelliteBox: SatelliteBoxW, Folded: CompactFold);

        // ── derived reads the renderer uses ─────────────────────────────────────────────────────────────────────────

        /// <summary>The column BOX's width. It IS the designed column — the box and the design agree, and the air
        /// beside it is <see cref="RegionGapW"/> spent by the band. 0 in the compact shape, where the identity is a
        /// full-width header row rather than a column.</summary>
        public float LayoutWidth => Wide ? ColumnWidth : 0f;

        /// <summary>Is this control ON the stage (rather than folded into the compact "…")?</summary>
        public bool Shows(Control c) => (Folded & c) == 0;

        /// <inheritdoc cref="Shows"/>
        public bool ShowSatellites => Shows(Control.Shuffle) && Shows(Control.Repeat);
        /// <inheritdoc cref="Shows"/>
        public bool ShowVolume => Shows(Control.Volume);
        /// <inheritdoc cref="Shows"/>
        public bool ShowDeviceLine => Shows(Control.OutputDevice);
        /// <summary>The compact shape is the only one with an overflow, and it always has one (four controls fold).</summary>
        public bool ShowOverflow => Folded != Control.None;

        /// <summary>A monotone "how much is the stage carrying" score — the comparand the narrowing-never-adds test
        /// asserts on. Every component is non-decreasing in width.</summary>
        public int Richness => (Wide ? 1 : 0)
            + (ShowSatellites ? 1 : 0) + (ShowVolume ? 1 : 0) + (ShowDeviceLine ? 1 : 0)
            + (int)(ArtSize * 0.01f) + (int)(PlayBox * 0.1f) + (int)(StepBox * 0.1f);

        // ── resolution ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The seed (no previous layout ⇒ no promotion/unfold reserve) — the value the surface's signal is
        /// constructed with before its first viewport effect runs.</summary>
        public static Layout Seed(float width, float columnAvailH) => Resolve(width, columnAvailH, null);

        /// <summary>The live resolve. <paramref name="columnAvailH"/> is the height the identity column actually gets
        /// — the surface owns that arithmetic (viewport less the caption band, the player bar and the stage's own top
        /// band) and passes it in, so the renderer and this allocator cannot hold two different opinions about it.
        ///
        /// <para><b>Two axes, ONE shape decision.</b> The width ladder decides wide⇄compact; the height ladder decides
        /// how much the wide column carries, and its terminal rung is the SAME <see cref="CompactStage"/> the width
        /// ladder already ends at. So there is still one shape, one fold set (a union) and one <see cref="Shows"/> —
        /// not two competing breakpoint tables.</para></summary>
        public static Layout Resolve(float width, float columnAvailH, Layout? previous = null)
        {
            width = MathF.Max(0f, width);
            float availH = MathF.Max(0f, columnAvailH);

            bool wideByWidth = previous is { } old
                ? width >= WideEnterW && (old.Wide || width >= WideEnterW + PromotionHysteresisW)
                : width >= WideEnterW;
            if (!wideByWidth) return CompactStage;

            Control was = previous is { } p ? p.Folded & HeightFoldable : Control.None;
            const float box = WidePlayBoxW;

            // Rung 1 — the output-device line. Informational, and the picker it opens is one tap away in the transport.
            Control fold = Control.None;
            if (Refold(availH, fold, box, (was & Control.OutputDevice) != 0)) fold |= Control.OutputDevice;

            // Rung 2 — the volume row. It has a system twin and hardware keys; the cover does not.
            if (fold != Control.None && Refold(availH, fold, box, (was & Control.Volume) != 0))
                fold |= Control.Volume;

            // Rung 3 — even the folded column cannot keep MinArtW ⇒ the whole shape demotes, the width ladder's end.
            if (!FitsArt(availH, fold, box)) return CompactStage;

            float art = availH - ColumnChromeH(fold, box);
            art = MathF.Min(art, MathF.Min(WideArtW, ColumnContentW));
            art = MathF.Max(art, MinArtW);
            art = MathF.Floor(art / ArtQuantum) * ArtQuantum;
            return WideStage with { ArtSize = art, Folded = fold };
        }

        /// <summary>Should the NEXT rung fold? Folding is immediate; unfolding demands <see cref="FoldHysteresisH"/>
        /// of reserve, which is what makes a slow drag flip it once rather than oscillate on the boundary.</summary>
        static bool Refold(float availH, Control folded, float playBox, bool wasFolded) =>
            wasFolded ? !FitsArt(availH - FoldHysteresisH, folded, playBox)
                      : !FitsArt(availH, folded, playBox);
    }

    // ── 3. which pane the right-hand region is showing ──────────────────────────────────────────────────────────────

    /// <summary>The stage's pane choice. The signal is <b>static</b> on purpose: it persists for the SESSION, so
    /// re-opening the surface lands on the pane you left it on. It is not a setting — nothing is written to disk — and
    /// it is not per-track state either; it is exactly "last pane wins".</summary>
    public static class Pane
    {
        public const int Lyrics = 0;
        public const int Queue = 1;

        /// <summary>Read it in a Render to subscribe; the two panes stay MOUNTED either way — the switch is an opacity
        /// cross-fade, never a conditional mount.</summary>
        public static readonly Signal<int> Current = new(Lyrics);

        /// <summary>Flip to the other pane — the pivot links' one writer.</summary>
        public static void Toggle() => Current.Value = Current.Peek() == Lyrics ? Queue : Lyrics;
    }
}
