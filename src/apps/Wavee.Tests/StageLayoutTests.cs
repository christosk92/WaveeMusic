using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Pins the immersive STAGE: its pure width/height ladder (<see cref="StageLayout"/>) and the on-media ink ladders its
/// renderers are only allowed to speak.
///
/// <para>Every boundary is DERIVED from the constants rather than written down — the <c>MergedChromeLayoutTests</c>
/// pattern — so retuning a threshold retunes the tests with it. The ink half drives <c>StageArm</c> / <c>WaveeOnMedia</c>
/// directly: the stage's whole premise is "everything is ink on the scrim", and a rung that loses its polarity, its
/// alpha ordering or its contrast against its own veil is a silent way to lose that premise.</para>
/// </summary>
public class StageLayoutTests
{
    const float SweepMax = 2600f;

    /// <summary>A column height at which the HEIGHT ladder is inert — nothing folds and the art sits at its cap — so
    /// the width tests below keep testing exactly the width ladder and nothing else. DERIVED from the ladder rather
    /// than authored, so it cannot drift away from the thing it is meant to neutralise.</summary>
    static readonly float TallH =
        StageLayout.ColumnChromeH(StageControl.None, StageLayout.WidePlayBoxW) + StageLayout.WideArtW;

    /// <inheritdoc cref="TallH"/>
    static StageLayout SeedTall(float w) => StageLayout.Seed(w, TallH);
    /// <inheritdoc cref="TallH"/>
    static StageLayout StepTall(float w, StageLayout prev) => StageLayout.Resolve(w, TallH, prev);

    // ── the width ladder ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The one threshold, derived: the narrowest width the seed resolution calls WIDE is exactly
    /// <see cref="StageLayout.WideEnterW"/>, and everything below it is compact.</summary>
    [Fact]
    public void TheWideThreshold_IsTheDeclaredOne()
    {
        float first = FirstWidthWhere(l => l.Wide);
        Assert.Equal(StageLayout.WideEnterW, first);
        Assert.False(SeedTall(first - 1f).Wide);
        Assert.True(SeedTall(first).Wide);
    }

    /// <summary>A degenerate / not-yet-measured viewport resolves COMPACT, never a 352-DIP column inside a 0-DIP
    /// window. (The surface seeds its signal from <c>Viewport.Size.Peek()</c>, which is 0 on the very first render.)</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-100f)]
    [InlineData(1f)]
    public void ADegenerateWidth_IsCompact(float w) => Assert.False(SeedTall(w).Wide);

    /// <summary>Sweeping the width up 1 DIP at a time — threading the previous layout, exactly as the surface's
    /// viewport effect does — flips the stage EXACTLY ONCE. A second flip anywhere in the sweep is the thrash the
    /// hysteresis exists to prevent (and it would remount nothing, but it would re-render a surface that owns a
    /// measured LyricsView on every one of 2600 resize steps).</summary>
    [Fact]
    public void AnUpwardSweep_FlipsExactlyOnce()
    {
        var cur = SeedTall(0f);
        int flips = 0;
        for (float w = 0f; w <= SweepMax; w += 1f)
        {
            var next = StepTall(w, cur);
            if (next.Wide != cur.Wide) flips++;
            cur = next;
        }
        Assert.Equal(1, flips);
        Assert.True(cur.Wide);
    }

    /// <summary>And the same going DOWN.</summary>
    [Fact]
    public void ADownwardSweep_FlipsExactlyOnce()
    {
        var cur = SeedTall(SweepMax);
        int flips = 0;
        for (float w = SweepMax; w >= 0f; w -= 1f)
        {
            var next = StepTall(w, cur);
            if (next.Wide != cur.Wide) flips++;
            cur = next;
        }
        Assert.Equal(1, flips);
        Assert.False(cur.Wide);
    }

    /// <summary>Demotion is IMMEDIATE and promotion costs the reserve — the asymmetry that makes the sweep above flip
    /// once. Inside the band a compact stage stays compact while a wide one stays wide, which is the definition of
    /// hysteresis and the reason a window edge parked on the boundary does not strobe.</summary>
    [Fact]
    public void PromotionCostsTheReserve_DemotionIsFree()
    {
        float inBand = StageLayout.WideEnterW + StageLayout.PromotionHysteresisW * 0.5f;

        // Coming UP through the band from compact: still compact.
        Assert.False(StepTall(inBand, StageLayout.CompactStage).Wide);
        // Coming DOWN through the band from wide: still wide.
        Assert.True(StepTall(inBand, StageLayout.WideStage).Wide);
        // Past the reserve: promoted.
        Assert.True(StepTall(StageLayout.WideEnterW + StageLayout.PromotionHysteresisW, StageLayout.CompactStage).Wide);
        // Below the threshold: demoted on the spot, no reserve.
        Assert.False(StepTall(StageLayout.WideEnterW - 1f, StageLayout.WideStage).Wide);
    }

    /// <summary>Narrowing never ADDS. The stage is a two-stage ladder, so its richness score is monotone by
    /// construction — this pins that no future third stage can break it.</summary>
    [Fact]
    public void NarrowingNeverAdds()
    {
        int prev = int.MinValue;
        for (float w = 0f; w <= SweepMax; w += 1f)
        {
            int r = SeedTall(w).Richness;
            Assert.True(r >= prev, $"richness went DOWN as the window widened, at {w}");
            prev = r;
        }
    }

    // ── the height ladder ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The app's OWN DEFAULT WINDOW must carry the whole column. This is the reported defect, pinned: at
    /// 1180 x 760 the column had no height ladder at all, so its fixed 620-DIP stack overflowed the 552 it was given,
    /// <c>FlexJustify.Center</c> clamped the leftover at 0 and the surplus fell off the BOTTOM — the output-device line
    /// was simply clipped away. The ladder spends the surplus on the COVER instead, so every control survives.</summary>
    [Fact]
    public void AtTheDefaultWindow_NoControlIsLost()
    {
        var l = StageLayout.Seed(1180f, DefaultColumnAvailH);
        Assert.True(l.Wide);
        Assert.True(l.ShowDeviceLine, "the output-device line was the control this defect ate");
        Assert.True(l.ShowVolume);
        Assert.True(l.ShowSatellites);
        // …and it fits, which is the whole point: chrome + art is exactly what the band offers, never more.
        Assert.True(StageLayout.ColumnChromeH(l.Folded, l.PlayBox) + l.ArtSize <= DefaultColumnAvailH);
        Assert.True(l.ArtSize < StageLayout.WideArtW, "the cover is what absorbed the shortfall");
        Assert.True(l.ArtSize >= StageLayout.MinArtW);
    }

    /// <summary>The column NEVER asks for more height than it was given — at any height, folded or not. This is the
    /// invariant the clipped device line violated, stated once for the whole sweep.</summary>
    [Fact]
    public void TheColumn_NeverExceedsItsBand()
    {
        for (float h = 0f; h <= 1400f; h += 1f)
        {
            var l = StageLayout.Seed(1180f, h);
            if (!l.Wide) continue;   // compact is a header row, not this column
            Assert.True(StageLayout.ColumnChromeH(l.Folded, l.PlayBox) + l.ArtSize <= h + 0.001f,
                $"the wide column wants more than the {h} DIP it was given");
        }
    }

    /// <summary>LOSSY IN THE ART, NEVER IN A CONTROL: shrinking the band shrinks the COVER first, and a control folds
    /// only when KEEPING it would push the cover below <see cref="StageLayout.MinArtW"/>. A control that folds while
    /// the cover could still have absorbed the shortfall is the ladder spending the wrong currency.
    /// <para>Note the fold HANDS ITS HEIGHT BACK to the cover, so the art after a fold sits above the floor rather than
    /// on it — the fold buys the cover room, which is the whole point.</para></summary>
    [Fact]
    public void AControlOnlyFolds_WhenKeepingItWouldBreakTheCoverFloor()
    {
        const float box = StageLayout.WidePlayBoxW;
        for (float h = 0f; h <= 1400f; h += 1f)
        {
            var l = StageLayout.Seed(1180f, h);
            if (!l.Wide) continue;
            Assert.True(l.ArtSize >= StageLayout.MinArtW);

            if (!l.ShowDeviceLine)
                Assert.True(h - StageLayout.ColumnChromeH(StageControl.None, box) < StageLayout.MinArtW,
                    $"the device line folded at h={h} while the cover could still have absorbed it");
            if (!l.ShowVolume)
                Assert.True(h - StageLayout.ColumnChromeH(StageControl.OutputDevice, box) < StageLayout.MinArtW,
                    $"the volume row folded at h={h} while the cover could still have absorbed it");
        }
    }

    /// <summary>The cover is quantised to the 4-DIP grid. NOT cosmetic: the surface's reflow signal is
    /// <c>!next.Equals(prev)</c>, so an unquantised residual would re-render the surface — and its mounted
    /// <c>LyricsView</c> — on every vertical resize PIXEL.</summary>
    [Fact]
    public void TheCoverIsQuantised_SoAResizePixelIsNotARerender()
    {
        var seen = new HashSet<StageLayout>();
        for (float h = 0f; h <= 1400f; h += 1f)
        {
            var l = StageLayout.Seed(1180f, h);
            if (l.Wide) Assert.Equal(0f, l.ArtSize % StageLayout.ArtQuantum);
            seen.Add(l);
        }
        // A 1400-DIP sweep must not produce a distinct layout per DIP — that is what "coarse band signal" means.
        Assert.True(seen.Count <= 1400f / StageLayout.ArtQuantum + 8f,
            $"the height ladder produced {seen.Count} distinct layouts across a 1400 DIP sweep");
    }

    /// <summary>A vertical sweep folds each rung EXACTLY ONCE. Folding is immediate, unfolding costs
    /// <see cref="StageLayout.FoldHysteresisH"/> — the height twin of the width ladder's asymmetry, and the reason a
    /// window edge parked on a fold boundary does not strobe a mounted LyricsView.</summary>
    [Fact]
    public void AVerticalSweep_FoldsEachRungExactlyOnce()
    {
        foreach (var rung in new[] { StageControl.OutputDevice, StageControl.Volume })
        {
            var cur = StageLayout.Seed(1180f, 1400f);
            int flips = 0;
            for (float h = 1400f; h >= 0f; h -= 1f)
            {
                var next = StageLayout.Resolve(1180f, h, cur);
                if (next.Wide && cur.Wide && next.Shows(rung) != cur.Shows(rung)) flips++;
                cur = next;
            }
            Assert.True(flips <= 1, $"{rung} folded/unfolded {flips} times on one downward sweep");
        }
    }

    /// <summary>Richness is monotone in BOTH axes — the 2-D form of "narrowing never adds". Growing the window may
    /// never take something away, whichever edge was dragged.</summary>
    [Fact]
    public void GrowingEitherAxis_NeverTakesSomethingAway()
    {
        for (float h = 120f; h <= 1200f; h += 20f)
        {
            int prev = int.MinValue;
            for (float w = 0f; w <= SweepMax; w += 20f)
            {
                int r = StageLayout.Seed(w, h).Richness;
                Assert.True(r >= prev, $"richness dropped as the window WIDENED at w={w}, h={h}");
                prev = r;
            }
        }
        for (float w = 620f; w <= SweepMax; w += 40f)
        {
            int prev = int.MinValue;
            for (float h = 0f; h <= 1200f; h += 20f)
            {
                int r = StageLayout.Seed(w, h).Richness;
                Assert.True(r >= prev, $"richness dropped as the window grew TALLER at w={w}, h={h}");
                prev = r;
            }
        }
    }

    /// <summary>The height threshold is DERIVED from the ladder it describes, never authored beside it — the same rule
    /// <see cref="StageLayout.ColumnContentW"/> follows, and the reason a retune cannot leave the two disagreeing.</summary>
    [Fact]
    public void TheHeightThreshold_IsDerivedFromTheLadder()
    {
        Assert.Equal(StageLayout.ColumnChromeH(StageLayout.HeightFoldable, StageLayout.WidePlayBoxW)
                     + StageLayout.MinArtW, StageLayout.WideEnterH);
        // Below it, the wide column cannot keep a legible cover even fully folded ⇒ the shape demotes.
        Assert.False(StageLayout.Seed(1180f, StageLayout.WideEnterH - 1f).Wide);
        Assert.True(StageLayout.Seed(1180f, StageLayout.WideEnterH).Wide);
    }

    /// <summary>The satellites are deliberately NOT a height rung — shuffle/repeat sit INSIDE the transport row, so
    /// folding them saves exactly zero vertical space. Pinning it stops a future "make it fold more" pass from adding
    /// a rung that costs a control and buys nothing.</summary>
    [Fact]
    public void TheSatellites_AreNotAHeightRung()
    {
        Assert.Equal(StageControl.None, StageLayout.HeightFoldable & StageControl.Shuffle);
        Assert.Equal(StageControl.None, StageLayout.HeightFoldable & StageControl.Repeat);
        Assert.Equal(StageLayout.ColumnChromeH(StageControl.None, StageLayout.WidePlayBoxW),
                     StageLayout.ColumnChromeH(StageControl.Shuffle | StageControl.Repeat, StageLayout.WidePlayBoxW));
    }

    /// <summary>The column height the surface actually hands the allocator at the app's default window — viewport less
    /// the caption band (48), the docked player bar (72) and the stage's own top band (88).</summary>
    const float DefaultColumnAvailH = 760f - 48f - 72f - 88f;

    // ── the sizes ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wide stage's authored geometry: a 352 column carrying a 300 cover, a 56 filled play between two 40
    /// steps between two 32 satellites. The transport is a strict ladder outward from the primary — that ordering is
    /// what makes the cluster read as one control rather than five.</summary>
    [Fact]
    public void TheWideStage_IsTheAuthoredGeometry()
    {
        var w = StageLayout.WideStage;
        Assert.True(w.Wide);
        Assert.Equal(352f, w.ColumnWidth);
        Assert.Equal(300f, w.ArtSize);
        Assert.Equal(56f, w.PlayBox);
        Assert.Equal(40f, w.StepBox);
        Assert.Equal(32f, w.SatelliteBox);
        Assert.True(w.PlayBox > w.StepBox && w.StepBox > w.SatelliteBox);
    }

    /// <summary>The compact stage keeps the same ORDERING one rung down: 64 cover, 40 play, 32 steps.</summary>
    [Fact]
    public void TheCompactStage_IsTheSameLadderOneRungDown()
    {
        var c = StageLayout.CompactStage;
        var w = StageLayout.WideStage;
        Assert.False(c.Wide);
        Assert.Equal(64f, c.ArtSize);
        Assert.Equal(40f, c.PlayBox);
        Assert.Equal(32f, c.StepBox);
        Assert.True(c.ArtSize < w.ArtSize);
        Assert.True(c.PlayBox < w.PlayBox);
        Assert.True(c.StepBox < w.StepBox);
        Assert.True(c.PlayBox > c.StepBox);
    }

    /// <summary>The column BOX ***is*** the designed column — the box and the design agree, and the air beside it is
    /// <see cref="StageLayout.RegionGapW"/>, spent by the BAND as a real <c>Gap</c>.
    /// <para>It used to be the column plus a 120-DIP "falloff" that the renderer then padded straight back out, i.e.
    /// 120 DIP of dead padding INSIDE the column rather than air between the two regions. Together with a centred
    /// reading column that put the first lyric glyph ~390 DIP from the artwork with nothing in between. Deleting the
    /// falloff is what closed that void, and it moved nothing inside the column: <see cref="StageLayout.ColumnContentW"/>
    /// is still 304.</para>
    /// <para>The compact stage has no column at all, so it claims no layout width.</para></summary>
    [Fact]
    public void TheColumnBox_IsTheDesignedColumn_AndTheGapIsTheBands()
    {
        var w = StageLayout.WideStage;
        Assert.Equal(w.ColumnWidth, w.LayoutWidth);
        Assert.Equal(StageLayout.WideColumnW, w.LayoutWidth);
        Assert.Equal(StageLayout.WideColumnW - 2f * StageLayout.ColumnPadX, StageLayout.ColumnContentW);
        Assert.True(StageLayout.RegionGapW > 0f, "the two regions need air between them");

        Assert.Equal(0f, StageLayout.CompactStage.LayoutWidth);
    }

    // ── the fold ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wide stage folds NOTHING; the compact stage folds exactly four controls — shuffle, repeat, the
    /// volume row and the output-device line — and nothing else. The seek is deliberately NOT on the list: a
    /// now-playing surface you cannot scrub is not a now-playing surface.</summary>
    [Fact]
    public void TheFoldedSet_IsExactlyTheFourSecondaryControls()
    {
        Assert.Equal(StageControl.None, StageLayout.WideStage.Folded);
        Assert.Equal(
            StageControl.Shuffle | StageControl.Repeat | StageControl.Volume | StageControl.OutputDevice,
            StageLayout.CompactStage.Folded);
    }

    /// <summary>Every folded control is REACHABLE: the compact stage always shows an overflow, and the wide stage never
    /// needs one. "Folded" means moved address, never lost — the Friends rule from the merged chrome row.</summary>
    [Fact]
    public void FoldedMeansMovedAddress_NeverLost()
    {
        Assert.True(StageLayout.CompactStage.ShowOverflow);
        Assert.False(StageLayout.WideStage.ShowOverflow);

        foreach (var c in new[] { StageControl.Shuffle, StageControl.Repeat, StageControl.Volume, StageControl.OutputDevice })
        {
            // In the row XOR in the "…" — the exact complement, at both widths.
            Assert.True(StageLayout.WideStage.Shows(c));
            Assert.False(StageLayout.CompactStage.Shows(c));
        }
    }

    /// <summary>The three derived reads the renderer actually branches on agree with <see cref="StageLayout.Folded"/>,
    /// so a call site can never disagree with the fold set.</summary>
    [Fact]
    public void TheDerivedShowReads_AgreeWithTheFoldSet()
    {
        var w = StageLayout.WideStage;
        Assert.True(w.ShowSatellites && w.ShowVolume && w.ShowDeviceLine);
        var c = StageLayout.CompactStage;
        Assert.False(c.ShowSatellites || c.ShowVolume || c.ShowDeviceLine);
    }

    /// <summary>Only two shapes exist at ANY width — the "one structure, one reflow flag" rule in arithmetic form.</summary>
    [Fact]
    public void TheLadderHasExactlyTwoShapes()
    {
        var seen = new HashSet<StageLayout>();
        for (float w = 0f; w <= SweepMax; w += 1f) seen.Add(SeedTall(w));
        Assert.Equal(2, seen.Count);
        Assert.Contains(StageLayout.WideStage, seen);
        Assert.Contains(StageLayout.CompactStage, seen);
    }

    // ── the scrim ladder ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stage is SINGLE-THEME art-dark, and its scrim is ONE continuous vertical system: a deepening at the
    /// top (the caption cluster), a deepening at the bottom (the pivot band and the transport), and a genuine PLATEAU at
    /// the base value through the middle where the lyrics read. The stops are FRACTIONS of the body height on purpose —
    /// that is what makes each deepening a feather hundreds of DIP long at any window size, where the boxed 88-DIP top
    /// veil it replaced was a band you could point at.</summary>
    [Fact]
    public void TheScrim_IsOneContinuousSystemWithAPlateau()
    {
        Assert.True(StageLayout.ScrimTopA > StageLayout.ScrimBaseA, "the top must be DEEPER than the base");
        Assert.True(StageLayout.ScrimBottomA > StageLayout.ScrimBaseA, "the bottom must be DEEPER than the base");
        Assert.True(StageLayout.ScrimBaseA > 0f && StageLayout.ScrimTopA < 1f);

        Assert.True(StageLayout.ScrimTopStop > 0f);
        Assert.True(StageLayout.ScrimTopStop < StageLayout.ScrimBottomStop);
        Assert.True(StageLayout.ScrimBottomStop < 1f);
        // Long feathers, both ends: a fifth of the surface each, minimum.
        Assert.True(StageLayout.ScrimTopStop >= 0.2f, "the top feather is too short to be edgeless");
        Assert.True(1f - StageLayout.ScrimBottomStop >= 0.2f, "the bottom feather is too short to be edgeless");
        // …and a real flat middle between them, not two ramps meeting.
        Assert.True(StageLayout.ScrimBottomStop - StageLayout.ScrimTopStop >= 0.3f);
    }

    /// <summary>The column shade is a PAINT layer, not a layout one — which is exactly why it can be much wider than the
    /// column BOX and feather to zero over a ramp the eye cannot locate. It must be wider than the box (otherwise it is
    /// the old boxed veil again) and its falloff must be a long multiple of the box's layout gutter.</summary>
    [Fact]
    public void TheColumnShade_IsWiderThanTheBoxAndFeathersToZero()
    {
        Assert.Equal(StageLayout.WideColumnW + StageLayout.ColumnShadeFalloffW, StageLayout.ColumnShadeW);
        Assert.True(StageLayout.ColumnShadeW > StageLayout.WideStage.LayoutWidth,
            "the shade must overhang the column BOX — a shade that stops at the box edge IS the edge");
        // …and it reaches well past the air between the regions, so the ramp is still resolving inside the PANE rather
        // than ending on the gap's far edge (which would put a locatable seam exactly where the lyrics begin).
        Assert.True(StageLayout.ColumnShadeFalloffW >= 2f * StageLayout.RegionGapW);
        Assert.True(StageLayout.ColumnShadeFalloffW >= 240f, "a short ramp to zero still reads as a smear");

        // The hold stop is exactly where the DESIGNED column ends inside the shade, so the type never sits on a moving
        // value; the mid stop is strictly inside the feather, which is what curves the ramp (a straight alpha line is
        // the shape the eye resolves as a Mach band).
        Assert.Equal(StageLayout.WideColumnW / StageLayout.ColumnShadeW, StageLayout.ColumnShadeHoldStop, 4);
        Assert.True(StageLayout.ColumnShadeMidStop > StageLayout.ColumnShadeHoldStop);
        Assert.True(StageLayout.ColumnShadeMidStop < 1f);
        Assert.True(StageLayout.ColumnShadeMidFrac > 0f && StageLayout.ColumnShadeMidFrac < 1f);
        Assert.True(StageLayout.ColumnShadeA > 0f && StageLayout.ColumnShadeA < StageLayout.ScrimBaseA);

        // The queue pane's local shade comes up out of ZERO before it is anywhere near the pane's content.
        Assert.True(StageLayout.PaneShadeFeatherStop > 0.1f && StageLayout.PaneShadeFeatherStop < 0.5f);
        Assert.True(StageLayout.PaneShadeA > 0f && StageLayout.PaneShadeA < StageLayout.ScrimBaseA);
    }

    /// <summary>The column's content span, and the reason it is authored in the pure allocator: the volume track is
    /// DERIVED from it rather than guessed at the call site.</summary>
    [Fact]
    public void TheColumnContent_IsTheDesignedColumnLessItsGutters()
    {
        Assert.Equal(StageLayout.WideColumnW - 2f * StageLayout.ColumnPadX, StageLayout.ColumnContentW);
        Assert.Equal(304f, StageLayout.ColumnContentW);
        // It still carries the cover with room to spare — the reason the gutter is 24 in the first place.
        Assert.True(StageLayout.ColumnContentW >= StageLayout.WideArtW);
    }

    // ── the interaction ramps ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every stage surface reaches the interaction ramp through the on-media GLASS rungs, and those rungs are
    /// derived from the on-media ink rather than minted as a fourth white.
    /// <para>GLASS IS A HOVER RAMP, NOT A GROUND: its rest rung is alpha ZERO. That is the property that makes it
    /// correct for a control standing on the scrim's own deepening and WRONG for one standing on artwork — which is
    /// why the exit uses the SCRIM ramp instead, whose rest rung is a real plate. Both ladders live in
    /// <c>WaveeOnMedia</c>; the second half of this test pins that they are two ramps and not one.</para></summary>
    [Fact]
    public void TheGlassRamp_IsDerivedFromTheOnMediaInk()
    {
        Assert.Equal(FluentGpu.Dsl.Tok.OnMediaPrimary.R, WaveeOnMedia.GlassHover.R);
        Assert.Equal(FluentGpu.Dsl.Tok.OnMediaPrimary.R, WaveeOnMedia.GlassPressed.R);
        Assert.Equal(0f, WaveeOnMedia.GlassRest.A);
        Assert.True(WaveeOnMedia.GlassRest.A < WaveeOnMedia.GlassHover.A);
        Assert.True(WaveeOnMedia.GlassHover.A < WaveeOnMedia.GlassPressed.A);
        // "White ~10%" — a hover rung any louder is a plate.
        Assert.True(WaveeOnMedia.GlassHover.A <= 0.12f);

        // The SCRIM ramp: a real ground at rest, monotone through hover and press, and a ring that is not the ink.
        Assert.True(WaveeOnMedia.ScrimRest.A > 0f, "a plate whose rest rung is transparent is not a plate");
        Assert.True(WaveeOnMedia.ScrimRest.A < WaveeOnMedia.ScrimHover.A);
        Assert.True(WaveeOnMedia.ScrimHover.A < WaveeOnMedia.ScrimPressed.A);
        Assert.True(WaveeOnMedia.Stroke.A > 0f && WaveeOnMedia.Stroke.A < 0.5f);
    }

    // ── the ink seam ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE DARK ARM IS <c>WaveeOnMedia</c> VERBATIM. "Dark theme is byte-identical to what shipped" is a claim
    /// worth executing rather than promising — it is what makes the light arm a pure addition instead of a retune of
    /// the surface everyone already uses.</summary>
    [Fact]
    public void TheStageInkDarkArm_IsWaveeOnMediaVerbatim()
    {
        var d = StageArm.For(ThemeKind.Dark);
        Assert.Equal(Tok.MediaStage, d.Veil);
        Assert.Equal(Tok.MediaStage, d.Floor);
        Assert.Equal(WaveeOnMedia.Ink, d.Ink);
        Assert.Equal(WaveeOnMedia.InkSecondary, d.InkSecondary);
        Assert.Equal(WaveeOnMedia.InkTertiary, d.InkTertiary);
        Assert.Equal(WaveeOnMedia.GlassRest, d.GlassRest);
        Assert.Equal(WaveeOnMedia.GlassHover, d.GlassHover);
        Assert.Equal(WaveeOnMedia.GlassPressed, d.GlassPressed);
        Assert.Equal(WaveeOnMedia.GlassPlate, d.GlassPlate);
        Assert.Equal(WaveeOnMedia.GlassPlateHover, d.GlassPlateHover);
        Assert.Equal(WaveeOnMedia.GlassPlatePressed, d.GlassPlatePressed);
        Assert.Equal(WaveeOnMedia.ScrimRest, d.ScrimRest);
        Assert.Equal(WaveeOnMedia.ScrimHover, d.ScrimHover);
        Assert.Equal(WaveeOnMedia.ScrimPressed, d.ScrimPressed);
        Assert.Equal(WaveeOnMedia.Stroke, d.Stroke);
        Assert.Equal(WaveeOnMedia.LightButton, d.ButtonFill);
        Assert.Equal(WaveeOnMedia.LightButtonHover, d.ButtonFillHover);
        Assert.Equal(WaveeOnMedia.LightButtonPressed, d.ButtonFillPressed);
        Assert.Equal(WaveeOnMedia.LightButtonInk, d.ButtonInk);
    }

    /// <summary>The light arm MIRRORS the dark one rather than being a second, independently-tuned ladder: the same
    /// alphas applied to an inverted ground. Two ladders is how the two arms drift apart.</summary>
    [Fact]
    public void TheStageInkLightArm_MirrorsTheDarkOne()
    {
        var d = StageArm.For(ThemeKind.Dark);
        var l = StageArm.For(ThemeKind.Light);

        // Inverted polarity: a light ground under dark ink.
        Assert.True(l.Veil.R > 0.9f, "the light stage's ground must actually be light");
        Assert.True(l.Ink.R < 0.1f, "the light stage's ink must actually be dark");
        Assert.Equal(1f, l.Ink.A);   // opaque, like the dark arm's white — not the theme text rung's 0.894

        // ONE alpha ladder, two grounds.
        Assert.Equal(d.InkSecondary.A, l.InkSecondary.A, 4);
        Assert.Equal(d.InkTertiary.A, l.InkTertiary.A, 4);
        Assert.Equal(d.GlassHover.A, l.GlassHover.A, 4);
        Assert.Equal(d.GlassPressed.A, l.GlassPressed.A, 4);
        Assert.Equal(d.GlassPlate.A, l.GlassPlate.A, 4);
        Assert.Equal(d.ScrimRest.A, l.ScrimRest.A, 4);
        Assert.Equal(d.Stroke.A, l.Stroke.A, 4);

        // The ramps keep their ORDER in both arms (rest < hover < pressed), which is what makes them read as one control.
        Assert.True(l.GlassHover.A < l.GlassPressed.A);
        Assert.True(l.GlassPlate.A < l.GlassPlateHover.A && l.GlassPlateHover.A < l.GlassPlatePressed.A);

        // The one filled control inverts WHOLE — a dark disc carrying the light ground as its glyph.
        Assert.Equal(l.Ink, l.ButtonFill);
        Assert.Equal(l.Veil, l.ButtonInk);
        Assert.True(ColorContrast.Ratio(l.ButtonInk, l.ButtonFill) > 10f, "the play glyph must read on its own disc");

        // Every plate stays ACHROMATIC — the stage tints with artwork, never with a minted hue.
        foreach (var c in new[] { l.Veil, l.Ink, l.ScrimRest, l.GlassPlate })
            Assert.True(MathF.Abs(c.R - c.G) < 0.02f && MathF.Abs(c.G - c.B) < 0.02f, "a stage rung invented a hue");
    }

    /// <summary>The light arm is NO WORSE than the dark arm already shipping — at each arm's own WORST cover.
    ///
    /// <para>This is the load-bearing claim behind leaving <see cref="StageLayout"/>'s scrim alphas alone. The two
    /// failure cases are mirror images (a near-white cover under a dark veil; a near-black cover under a light one),
    /// and the sRGB transfer curve is not symmetric: mixing toward BLACK at a partial alpha destroys far more
    /// perceptual luminance than mixing toward white. So the light arm's alpha'd ink clears a HIGHER ratio than the
    /// dark arm's — one set of alphas is correct for both.</para></summary>
    [Fact]
    public void TheLightArm_IsNoWorseThanTheShippedDarkOne()
    {
        var d = StageArm.For(ThemeKind.Dark);
        var l = StageArm.For(ThemeKind.Light);
        var white = new ColorF(1f, 1f, 1f, 1f);
        var black = new ColorF(0f, 0f, 0f, 1f);

        // Each arm's worst case: the cover whose luminance fights its own veil hardest.
        var darkGround = ColorContrast.Over(d.Veil with { A = StageLayout.ScrimBaseA }, white);
        var lightGround = ColorContrast.Over(l.Veil with { A = StageLayout.ScrimBaseA }, black);

        foreach (var (dc, lc, name) in new[]
        {
            (d.Ink, l.Ink, "primary"),
            (d.InkSecondary, l.InkSecondary, "secondary"),
            (d.InkTertiary, l.InkTertiary, "tertiary"),
        })
        {
            float dark = ColorContrast.Ratio(ColorContrast.Over(dc, darkGround), darkGround);
            float light = ColorContrast.Ratio(ColorContrast.Over(lc, lightGround), lightGround);
            Assert.True(light >= dark,
                $"the light arm's {name} ink ({light:0.00}:1) is worse than the dark arm already ships ({dark:0.00}:1)");
        }
    }

    /// <summary>The stage's interaction glass is at least as audible as the app's own LIGHT ROW hover, and it is BLACK
    /// ink — the rule <c>LightModeOverhaulTests</c> establishes for every light row in the product. A stage that used a
    /// quieter or inverted ramp would be a second, contradictory answer to "what does hover feel like in light".</summary>
    [Fact]
    public void TheStageGlass_IsAtLeastAsAudibleAsTheAppsLightRowHover()
    {
        var l = StageArm.For(ThemeKind.Light);
        Assert.True(l.GlassHover.R < 0.5f, "light-theme glass must be BLACK ink, not white");
        Assert.True(l.GlassHover.A >= 0.045f, "below the app's audible floor for a light row hover");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static float FirstWidthWhere(Func<StageLayout, bool> predicate)
    {
        for (float w = 0f; w <= 4000f; w += 1f)
            if (predicate(SeedTall(w))) return w;
        return -1f;
    }
}
