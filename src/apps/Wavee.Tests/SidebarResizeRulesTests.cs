// ── Wavee.Tests/SidebarResizeRulesTests.cs — the free-range sidebar resize + rail detent rules ─────────────────────
//
// docs/plans/wavee/sidebar-free-resize-implementation.md §2.2/§2.3/§4.1/§5. The sidebar width is FREE above the icon
// rail (no snapping), the collapsed rail has three detents (Compact 48 / Default 56 / Large 80), and the window never
// changes the pane regime. Every decision lives in the engine-free `SidebarResizeRules` + `SidebarRailMetrics`, so
// this file drives each branch with plain numbers — no engine, no signal, no clock, no settings store.
//
// The numbers below are worked by hand from the plan's formulas, not copied from the code:
//   · strips 48 / 56 / 80 (tile + 2·8) → detent midpoints 52 (Compact|Default) and 68 (Default|Large), ±4 hysteresis;
//   · RailEnterW = 180 − 64 = 116 (drag left past it ⇒ Rail), ExpandedEnterW = 116 + 24 = 140 (drag right past it ⇒ Expanded);
//   · Present() yields an expanded pane to (viewport − 480) but never below 180: 300 → 276 at a 756 window;
//   · the last-resort band enters below 180 + 480 = 660 and leaves at 700 (+40 hysteresis).
//
// HYSTERESIS IS HISTORY-DEPENDENT: `Track`/`Resolve` take the previous state, and a detent edge holds the PREVIOUS
// detent inside ±4 DIP. A fact that asserts "49 is Compact" therefore states which detent the pointer came from.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarResizeRulesTests
{
    static SidebarResizeRules.State Expanded(float width = 300f, SidebarRailDetent detent = SidebarRailDetent.Default)
        => new(SidebarRegime.Expanded, detent, width);

    static SidebarResizeRules.State Rail(SidebarRailDetent detent, float expandedWidth = 300f)
        => new(SidebarRegime.Rail, detent, expandedWidth);

    static SidebarResizeRules.State After(SidebarResizeRules.Settle s) => new(s.Regime, s.Detent, s.ExpandedWidth);

    static void Near(float expected, float actual, float tol = 1e-3f)
        => Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected} but was {actual}");

    // ── detent metrics (§2.2) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Detents_DeriveFromTheThumbLadder()
    {
        // (Tile, Art, Glyph, Corner, StripW, Pitch, DividerW)
        Assert.Equal(new SidebarRailMetrics(32f, 28f, 16f, 6f, 48f, 38f, 16f), SidebarRailMetrics.For(SidebarRailDetent.Compact));
        Assert.Equal(new SidebarRailMetrics(40f, 36f, 16f, 6f, 56f, 46f, 24f), SidebarRailMetrics.For(SidebarRailDetent.Default));
        Assert.Equal(new SidebarRailMetrics(64f, 60f, 20f, 8f, 80f, 70f, 48f), SidebarRailMetrics.For(SidebarRailDetent.Large));

        // The Default row reproduces today rail byte-for-byte.
        Assert.Equal(Design.Size.NavCompactW, SidebarRailMetrics.For(SidebarRailDetent.Default).StripW);
        Assert.Equal(Design.Size.Thumb32, SidebarRailMetrics.TileOf(SidebarRailDetent.Compact));
        Assert.Equal(Design.Size.Thumb40, SidebarRailMetrics.TileOf(SidebarRailDetent.Default));
        Assert.Equal(Design.Size.Thumb64, SidebarRailMetrics.TileOf(SidebarRailDetent.Large));
    }

    [Fact]
    public void Of_IsOneFormulaOverTheTile_ForAnyTile()
    {
        // Of(For's tile) == For: there is no per-detent literal table behind it.
        foreach (var d in new[] { SidebarRailDetent.Compact, SidebarRailDetent.Default, SidebarRailDetent.Large })
            Assert.Equal(SidebarRailMetrics.For(d), SidebarRailMetrics.Of(SidebarRailMetrics.TileOf(d)));

        // An off-ladder tile still obeys the relations: ring 2 each side, strip = tile + 16, pitch = tile + 6.
        var m = SidebarRailMetrics.Of(48f);
        Assert.Equal(44f, m.Art);
        Assert.Equal(64f, m.StripW);
        Assert.Equal(54f, m.Pitch);
        Assert.Equal(32f, m.DividerW);
        Assert.Equal(16f, m.Glyph);   // 20 only from the 64 tile up
    }

    [Theory]
    [InlineData(0, SidebarRailDetent.Compact)]
    [InlineData(1, SidebarRailDetent.Default)]
    [InlineData(2, SidebarRailDetent.Large)]
    [InlineData(-1, SidebarRailDetent.Default)]
    [InlineData(3, SidebarRailDetent.Default)]
    [InlineData(int.MaxValue, SidebarRailDetent.Default)]
    [InlineData(int.MinValue, SidebarRailDetent.Default)]
    public void Coerce_UnknownStoredDetentIsDefault(int stored, SidebarRailDetent expected)
        => Assert.Equal(expected, SidebarRailMetrics.Coerce(stored));

    [Fact]
    public void DetentAndRegimeValues_ArePersistedAndStable()
    {
        Assert.Equal(0, (int)SidebarRailDetent.Compact);
        Assert.Equal(1, (int)SidebarRailDetent.Default);
        Assert.Equal(2, (int)SidebarRailDetent.Large);
        Assert.Equal(0, (int)SidebarRegime.Expanded);   // 0 is load-bearing: collapsed=false (the default) is Expanded
        Assert.Equal(1, (int)SidebarRegime.Rail);
    }

    // ── thresholds (§2.3) ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Thresholds_AreOrderedAndDerived()
    {
        Assert.Equal(48f, SidebarResizeRules.RailFloorW);
        Assert.Equal(80f, SidebarResizeRules.RailCeilW);
        Assert.Equal(116f, SidebarResizeRules.RailEnterW);
        Assert.Equal(140f, SidebarResizeRules.ExpandedEnterW);
        Assert.Equal(180f, SidebarResizeRules.ExpandedMinW);
        Assert.Equal(460f, SidebarResizeRules.ExpandedMaxW);

        Assert.True(SidebarResizeRules.RailFloorW < SidebarResizeRules.RailCeilW);
        Assert.True(SidebarResizeRules.RailCeilW < SidebarResizeRules.RailEnterW);
        Assert.True(SidebarResizeRules.RailEnterW < SidebarResizeRules.ExpandedEnterW);
        Assert.True(SidebarResizeRules.ExpandedEnterW < SidebarResizeRules.ExpandedMinW);
        Assert.True(SidebarResizeRules.ExpandedMinW <= SidebarResizeRules.ExpandedMaxW);

        Assert.Equal(SidebarPaneBounds.NavPaneMinW - 64f, SidebarResizeRules.RailEnterW);
        Assert.Equal(SidebarPaneBounds.NavPaneHysteresisDip, SidebarResizeRules.ExpandedEnterW - SidebarResizeRules.RailEnterW);
        Assert.Equal(SidebarPaneBounds.NavPaneMinW, SidebarResizeRules.ExpandedMinW);
        Assert.Equal(SidebarPaneBounds.NavPaneMaxW, SidebarResizeRules.ExpandedMaxW);
    }

    [Theory]
    [InlineData(SidebarRailDetent.Compact, 48f)]
    [InlineData(SidebarRailDetent.Default, 56f)]
    [InlineData(SidebarRailDetent.Large, 80f)]
    public void StripOf_IsTheMetricsStrip(SidebarRailDetent d, float strip)
    {
        Assert.Equal(strip, SidebarResizeRules.StripOf(d));
        Assert.Equal(SidebarRailMetrics.For(d).StripW, SidebarResizeRules.StripOf(d));
    }

    // ── Track: live tracking during a drag (§2.3) ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(180f)]
    [InlineData(181f)]
    [InlineData(300f)]
    [InlineData(459.5f)]
    [InlineData(460f)]
    public void Track_ExpandedIsFreeAboveTheFloor(float raw)
    {
        var live = SidebarResizeRules.Track(raw, Expanded(333f, SidebarRailDetent.Large));
        Assert.Equal(SidebarRegime.Expanded, live.Regime);
        Assert.Equal(raw, live.PresentedWidth);                 // free: no snapping at all
        Assert.Equal(1f, live.Fade);
        Assert.Equal(SidebarRailDetent.Large, live.Detent);     // the detent memory is untouched while expanded
    }

    [Fact]
    public void Track_HoldsAtTheFloorThroughTheApproachBand()
    {
        // 170: still Expanded (≥ 116), presented holds at 180, fade = 1 − (10/64)·0.65 ≈ 0.898.
        var live = SidebarResizeRules.Track(170f, Expanded());
        Assert.Equal(SidebarRegime.Expanded, live.Regime);
        Assert.Equal(180f, live.PresentedWidth);
        Assert.InRange(live.Fade, 0.35f, 1f);
        Near(0.8984375f, live.Fade);

        // 116.5: 63.5/64 into the band → fade ≈ 0.355, i.e. just above the 0.35 floor.
        var deep = SidebarResizeRules.Track(116.5f, Expanded());
        Assert.Equal(SidebarRegime.Expanded, deep.Regime);
        Assert.Equal(180f, deep.PresentedWidth);
        Assert.InRange(deep.Fade, 0.35f, 0.36f);

        // 116 exactly is NOT below RailEnterW: still Expanded, fade at the floor.
        var edge = SidebarResizeRules.Track(116f, Expanded());
        Assert.Equal(SidebarRegime.Expanded, edge.Regime);
        Near(0.35f, edge.Fade);
    }

    [Fact]
    public void Track_EntersTheRailPastRailEnter()
    {
        var live = SidebarResizeRules.Track(115.9f, Expanded(300f));
        Assert.Equal(SidebarRegime.Rail, live.Regime);
        Assert.Equal(SidebarRailDetent.Large, live.Detent);     // entering from the expanded side lands on Large
        Assert.Equal(80f, live.PresentedWidth);                 // the strip holds at the ceiling
        Assert.InRange(live.Fade, 0.35f, 1f);                   // …with the symmetric "about to switch" cue

        // Even if the stored detent memory was Compact, the entry detent is Large (the pointer is far right of it).
        Assert.Equal(SidebarRailDetent.Large, SidebarResizeRules.Track(115.9f, Expanded(300f, SidebarRailDetent.Compact)).Detent);
    }

    [Fact]
    public void Track_RailStripFollowsThePointer()
    {
        Assert.Equal((60f, SidebarRailDetent.Default), Pick(SidebarResizeRules.Track(60f, Rail(SidebarRailDetent.Default))));
        // 49 is within 4 of the 52 edge: it is Compact only when the pointer already was Compact.
        Assert.Equal((49f, SidebarRailDetent.Compact), Pick(SidebarResizeRules.Track(49f, Rail(SidebarRailDetent.Compact))));
        Assert.Equal((79f, SidebarRailDetent.Large), Pick(SidebarResizeRules.Track(79f, Rail(SidebarRailDetent.Default))));

        // The strip is clamped to [48, 80] and the fade is 1 anywhere inside it.
        var below = SidebarResizeRules.Track(10f, Rail(SidebarRailDetent.Compact));
        Assert.Equal(48f, below.PresentedWidth);
        Assert.Equal(1f, below.Fade);
        Assert.Equal(1f, SidebarResizeRules.Track(80f, Rail(SidebarRailDetent.Large)).Fade);
    }

    static (float, SidebarRailDetent) Pick(SidebarResizeRules.Live l) => (l.PresentedWidth, l.Detent);

    [Fact]
    public void Track_DetentEdgesHaveHysteresis()
    {
        // Compact | Default edge at 52 (±4).
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Track(50.5f, Rail(SidebarRailDetent.Default)).Detent);   // holds
        Assert.Equal(SidebarRailDetent.Compact, SidebarResizeRules.Track(47.9f, Rail(SidebarRailDetent.Default)).Detent);   // clamps to 48, |48−52| = 4
        Assert.Equal(SidebarRailDetent.Compact, SidebarResizeRules.Track(53.5f, Rail(SidebarRailDetent.Compact)).Detent);   // holds
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Track(56.1f, Rail(SidebarRailDetent.Compact)).Detent);   // |56.1−52| > 4

        // Default | Large edge at 68 (±4).
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Track(69f, Rail(SidebarRailDetent.Default)).Detent);     // holds
        Assert.Equal(SidebarRailDetent.Large, SidebarResizeRules.Track(72.1f, Rail(SidebarRailDetent.Default)).Detent);
        Assert.Equal(SidebarRailDetent.Large, SidebarResizeRules.Track(66.5f, Rail(SidebarRailDetent.Large)).Detent);       // holds
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Track(63.9f, Rail(SidebarRailDetent.Large)).Detent);

        // A pointer resting exactly on a midpoint does not flicker: the previous detent is kept either way.
        Assert.Equal(SidebarRailDetent.Compact, SidebarResizeRules.Track(52f, Rail(SidebarRailDetent.Compact)).Detent);
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Track(52f, Rail(SidebarRailDetent.Default)).Detent);
    }

    [Fact]
    public void NearestDetent_SplitsAtTheMidpoints()
    {
        // Far from every edge the previous detent is irrelevant.
        Assert.Equal(SidebarRailDetent.Compact, SidebarResizeRules.NearestDetent(48f, SidebarRailDetent.Large));
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.NearestDetent(60f, SidebarRailDetent.Compact));
        Assert.Equal(SidebarRailDetent.Large, SidebarResizeRules.NearestDetent(80f, SidebarRailDetent.Compact));
    }

    [Fact]
    public void Track_LeavesTheRailOnlyPastExpandedEnter()
    {
        // 139: still Rail, the strip holds at 80 and the fade is already dimming (60-DIP band: 59/60 in).
        var held = SidebarResizeRules.Track(139f, Rail(SidebarRailDetent.Large));
        Assert.Equal(SidebarRegime.Rail, held.Regime);
        Assert.Equal(80f, held.PresentedWidth);
        Assert.InRange(held.Fade, 0.35f, 0.37f);

        // 140: Expanded, presented clamps UP to the floor 180. The fade is NOT 1 here: raw 140 is still 40 DIP under
        // the floor, so the approach-band cue (1 − (40/64)·0.65 = 0.59375) applies until the pointer reaches 180.
        var popped = SidebarResizeRules.Track(140f, Rail(SidebarRailDetent.Large));
        Assert.Equal(SidebarRegime.Expanded, popped.Regime);
        Assert.Equal(180f, popped.PresentedWidth);
        Near(0.59375f, popped.Fade);

        // Past the floor the width is free again and the fade is 1.
        var free = SidebarResizeRules.Track(220f, Rail(SidebarRailDetent.Large));
        Assert.Equal(SidebarRegime.Expanded, free.Regime);
        Assert.Equal(220f, free.PresentedWidth);
        Assert.Equal(1f, free.Fade);
    }

    [Fact]
    public void Track_FadeCueIsSymmetricAcrossBothBands()
    {
        // Same curve, 1 → 0.35 across each band. The bands differ in length (expanded 180→116 = 64, rail 80→140 = 60),
        // so compare the MIDPOINT of each: both 0.5 of the way in ⇒ 1 − 0.5·0.65 = 0.675.
        var expandedMid = SidebarResizeRules.Track(148f, Expanded());                         // into = 32 of 64
        var railMid = SidebarResizeRules.Track(110f, Rail(SidebarRailDetent.Large));          // into = 30 of 60
        Near(0.675f, expandedMid.Fade);
        Near(0.675f, railMid.Fade);
        Near(expandedMid.Fade, railMid.Fade);

        // Both bands start at 1 and fall monotonically.
        Assert.Equal(1f, SidebarResizeRules.Track(180f, Expanded()).Fade);
        Assert.Equal(1f, SidebarResizeRules.Track(80f, Rail(SidebarRailDetent.Large)).Fade);
        Assert.True(SidebarResizeRules.Track(150f, Expanded()).Fade > SidebarResizeRules.Track(130f, Expanded()).Fade);
        Assert.True(SidebarResizeRules.Track(90f, Rail(SidebarRailDetent.Large)).Fade > SidebarResizeRules.Track(130f, Rail(SidebarRailDetent.Large)).Fade);

        // Never below the shared floor.
        Assert.True(SidebarResizeRules.Track(116f, Expanded()).Fade >= SidebarResizeRules.MinFade - 1e-4f);
        Assert.True(SidebarResizeRules.Track(139.99f, Rail(SidebarRailDetent.Large)).Fade >= SidebarResizeRules.MinFade - 1e-4f);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Track_NonFiniteRawPresentsThePreviousState(float raw)
    {
        var rail = SidebarResizeRules.Track(raw, Rail(SidebarRailDetent.Large, 333f));
        Assert.Equal(new SidebarResizeRules.Live(SidebarRegime.Rail, SidebarRailDetent.Large, 80f, 1f), rail);

        var expanded = SidebarResizeRules.Track(raw, Expanded(333f, SidebarRailDetent.Compact));
        Assert.Equal(new SidebarResizeRules.Live(SidebarRegime.Expanded, SidebarRailDetent.Compact, 333f, 1f), expanded);
    }

    // ── Resolve: the release (§2.3) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_SnapsToTheNearestDetent()
    {
        // The detent at release is the one Track last produced (hysteresis included), so each case names where the
        // pointer came from.
        var a = SidebarResizeRules.Resolve(60f, Rail(SidebarRailDetent.Default));
        Assert.Equal((SidebarRailDetent.Default, 56f), (a.Detent, a.TargetWidth));

        var b = SidebarResizeRules.Resolve(69f, Rail(SidebarRailDetent.Large));
        Assert.Equal((SidebarRailDetent.Large, 80f), (b.Detent, b.TargetWidth));

        var c = SidebarResizeRules.Resolve(49f, Rail(SidebarRailDetent.Compact));
        Assert.Equal((SidebarRailDetent.Compact, 48f), (c.Detent, c.TargetWidth));

        // Coming in from the expanded side: 100 is inside the rail regime and far right of every edge ⇒ Large.
        var d = SidebarResizeRules.Resolve(100f, Expanded());
        Assert.Equal((SidebarRegime.Rail, SidebarRailDetent.Large, 80f), (d.Regime, d.Detent, d.TargetWidth));

        // …and 40 (clamped to the 48 floor, 4 past the 52 edge) lands on Compact.
        var e = SidebarResizeRules.Resolve(40f, Expanded());
        Assert.Equal((SidebarRegime.Rail, SidebarRailDetent.Compact, 48f), (e.Regime, e.Detent, e.TargetWidth));

        // The target is always exactly the detent strip.
        foreach (var s in new[] { a, b, c, d, e })
            Assert.Equal(SidebarResizeRules.StripOf(s.Detent), s.TargetWidth);
    }

    [Fact]
    public void Resolve_FlickPicksTheNeighbourInTheDirectionOfTravel()
    {
        var prev = Rail(SidebarRailDetent.Default);
        Assert.Equal(SidebarRailDetent.Compact, SidebarResizeRules.Resolve(58f, prev, -900f).Detent);
        Assert.Equal(SidebarRailDetent.Large, SidebarResizeRules.Resolve(58f, prev, +900f).Detent);
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Resolve(58f, prev, -300f).Detent);   // under the threshold
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Resolve(58f, prev, +300f).Detent);

        // The threshold itself flicks (>=), one DIP/s under does not.
        Assert.Equal(SidebarRailDetent.Large, SidebarResizeRules.Resolve(58f, prev, SidebarResizeRules.FlickDipPerSec).Detent);
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Resolve(58f, prev, SidebarResizeRules.FlickDipPerSec - 1f).Detent);

        // At either end a flick toward the wall stays put (no wrap, no expand — expanding is a drag past 140).
        Assert.Equal(SidebarRailDetent.Compact, SidebarResizeRules.Resolve(49f, Rail(SidebarRailDetent.Compact), -900f).Detent);
        Assert.Equal(SidebarRailDetent.Large, SidebarResizeRules.Resolve(79f, Rail(SidebarRailDetent.Large), +900f).Detent);

        // A flick away from the wall steps off it.
        Assert.Equal(SidebarRailDetent.Default, SidebarResizeRules.Resolve(50f, Rail(SidebarRailDetent.Compact), +900f).Detent);

        // The flicked detent target is that detent strip.
        Assert.Equal(48f, SidebarResizeRules.Resolve(58f, prev, -900f).TargetWidth);
        Assert.Equal(80f, SidebarResizeRules.Resolve(58f, prev, +900f).TargetWidth);
    }

    [Fact]
    public void Resolve_ExpandedCommitsTheClampedRawWidth()
    {
        var mid = SidebarResizeRules.Resolve(300f, Expanded(333f));
        Assert.Equal(new SidebarResizeRules.Settle(SidebarRegime.Expanded, SidebarRailDetent.Default, 300f, 300f), mid);

        var huge = SidebarResizeRules.Resolve(999f, Expanded(333f));
        Assert.Equal(SidebarRegime.Expanded, huge.Regime);
        Assert.Equal(460f, huge.ExpandedWidth);
        Assert.Equal(460f, huge.TargetWidth);

        // A flick means nothing to an expanded release.
        Assert.Equal(300f, SidebarResizeRules.Resolve(300f, Expanded(333f), -5000f).ExpandedWidth);
    }

    [Fact]
    public void Resolve_ApproachBandReleaseStaysExpandedAtTheFloor()
    {
        var s = SidebarResizeRules.Resolve(150f, Expanded(333f));
        Assert.Equal(SidebarRegime.Expanded, s.Regime);
        Assert.Equal(180f, s.ExpandedWidth);
        Assert.Equal(180f, s.TargetWidth);
    }

    [Fact]
    public void Resolve_ReleasingARailDragPastExpandedEnterExpandsAtTheFloorOrWhereReleased()
    {
        var floor = SidebarResizeRules.Resolve(140f, Rail(SidebarRailDetent.Large, 333f));
        Assert.Equal((SidebarRegime.Expanded, 180f, 180f), (floor.Regime, floor.ExpandedWidth, floor.TargetWidth));

        var free = SidebarResizeRules.Resolve(250f, Rail(SidebarRailDetent.Large, 333f));
        Assert.Equal((SidebarRegime.Expanded, 250f, 250f), (free.Regime, free.ExpandedWidth, free.TargetWidth));
    }

    [Fact]
    public void Resolve_RailSettleNeverTouchesTheExpandedMemory()
    {
        var s = SidebarResizeRules.Resolve(60f, Rail(SidebarRailDetent.Default, expandedWidth: 333f));
        Assert.Equal(SidebarRegime.Rail, s.Regime);
        Assert.Equal(333f, s.ExpandedWidth);

        // Dragging from the expanded pane down into the rail also keeps the remembered width.
        var fromExpanded = SidebarResizeRules.Resolve(100f, Expanded(333f));
        Assert.Equal(SidebarRegime.Rail, fromExpanded.Regime);
        Assert.Equal(333f, fromExpanded.ExpandedWidth);
    }

    // ── Present: the window yield (§2.4) ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1440f, 300f)]   // cap = 960
    [InlineData(780f, 300f)]    // cap = 300: exactly fits
    [InlineData(756f, 276f)]    // cap = 276
    [InlineData(700f, 220f)]
    [InlineData(661f, 181f)]
    [InlineData(660f, 180f)]
    [InlineData(600f, 180f)]    // never below the expanded floor
    [InlineData(310f, 180f)]
    [InlineData(0f, 300f)]      // unmeasured viewport: the preference
    [InlineData(float.NaN, 300f)]
    public void Present_YieldsToTheContentFloorWithoutWritingThePreference(float viewport, float expected)
    {
        var state = Expanded(300f);
        Assert.Equal(expected, SidebarResizeRules.Present(state, viewport, 480f));
        Assert.Equal(300f, state.ExpandedWidth);   // a pure function of its inputs: the preference is untouched
    }

    [Fact]
    public void Present_ClampsAnOutOfRangePreference()
    {
        Assert.Equal(460f, SidebarResizeRules.Present(Expanded(999f), 1440f, 480f));
        Assert.Equal(180f, SidebarResizeRules.Present(Expanded(40f), 1440f, 480f));
    }

    [Fact]
    public void Present_RailIsNeverYielded()
    {
        Assert.Equal(80f, SidebarResizeRules.Present(Rail(SidebarRailDetent.Large), 400f, 480f));
        Assert.Equal(56f, SidebarResizeRules.Present(Rail(SidebarRailDetent.Default), 100f, 480f));
        Assert.Equal(48f, SidebarResizeRules.Present(Rail(SidebarRailDetent.Compact), 1440f, 480f));
        Assert.Equal(80f, SidebarResizeRules.Present(Rail(SidebarRailDetent.Large), 0f, 480f));
    }

    // ── LastResort: the degenerate-window band (§2.4 option b) ──────────────────────────────────────────────────────

    [Fact]
    public void LastResort_IsHystereticAndDerived()
    {
        const float floor = 480f;   // Shell.MinContentW
        // Enter below 180 + 480 = 660.
        Assert.True(SidebarResizeRules.LastResort(659.9f, current: false, floor));
        Assert.False(SidebarResizeRules.LastResort(660f, current: false, floor));
        Assert.False(SidebarResizeRules.LastResort(680f, current: false, floor));   // inside the hysteresis band: does not enter
        Assert.False(SidebarResizeRules.LastResort(1440f, current: false, floor));

        // Once in, hold until 660 + 40 = 700.
        Assert.True(SidebarResizeRules.LastResort(699f, current: true, floor));
        Assert.True(SidebarResizeRules.LastResort(661f, current: true, floor));
        Assert.False(SidebarResizeRules.LastResort(700f, current: true, floor));
        Assert.False(SidebarResizeRules.LastResort(1440f, current: true, floor));

        // A zero (unmeasured) width never moves it.
        Assert.True(SidebarResizeRules.LastResort(0f, current: true, floor));
        Assert.False(SidebarResizeRules.LastResort(0f, current: false, floor));
    }

    [Fact]
    public void LastResort_IsUnreachableWhereThePaneStillFits()
    {
        // 756 is the owner-window width: the yield (300 → 276) handles it, the band must not fire.
        Assert.False(SidebarResizeRules.LastResort(756f, current: false, 480f));
        // The band's enter threshold is exactly the width where Present() bottoms out at the floor.
        Assert.Equal(SidebarResizeRules.ExpandedMinW, SidebarResizeRules.Present(Expanded(300f), 660f, 480f));
    }

    // ── Step: the keyboard (§2.5) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Step_NudgesExpandedByEightOrForty()
    {
        var s = Expanded(300f, SidebarRailDetent.Large);
        Assert.Equal(new SidebarResizeRules.Settle(SidebarRegime.Expanded, SidebarRailDetent.Large, 308f, 308f), SidebarResizeRules.Step(s, +1, large: false));
        Assert.Equal(new SidebarResizeRules.Settle(SidebarRegime.Expanded, SidebarRailDetent.Large, 292f, 292f), SidebarResizeRules.Step(s, -1, large: false));
        Assert.Equal(340f, SidebarResizeRules.Step(s, +1, large: true).ExpandedWidth);
        Assert.Equal(260f, SidebarResizeRules.Step(s, -1, large: true).ExpandedWidth);
        Assert.Equal(8f, SidebarResizeRules.NudgeW);
        Assert.Equal(40f, SidebarResizeRules.NudgeLargeW);

        // Clamped at both ends.
        Assert.Equal(460f, SidebarResizeRules.Step(Expanded(455f), +1, large: true).ExpandedWidth);
        Assert.Equal(460f, SidebarResizeRules.Step(Expanded(460f), +1, large: false).ExpandedWidth);
        var floor = SidebarResizeRules.Step(Expanded(185f), -1, large: true);   // 145 would undershoot, but 185 is above the floor
        Assert.Equal((SidebarRegime.Expanded, 180f, 180f), (floor.Regime, floor.ExpandedWidth, floor.TargetWidth));
    }

    [Fact]
    public void Step_ZeroDirectionChangesNothing()
    {
        Assert.Equal(new SidebarResizeRules.Settle(SidebarRegime.Expanded, SidebarRailDetent.Default, 300f, 300f),
            SidebarResizeRules.Step(Expanded(300f), 0, large: false));
        Assert.Equal(new SidebarResizeRules.Settle(SidebarRegime.Rail, SidebarRailDetent.Compact, 300f, 48f),
            SidebarResizeRules.Step(Rail(SidebarRailDetent.Compact), 0, large: true));
    }

    [Fact]
    public void Step_LeftAtTheFloorEntersTheRailAtLarge()
    {
        var s = SidebarResizeRules.Step(Expanded(180f, SidebarRailDetent.Compact), -1, large: false);
        Assert.Equal((SidebarRegime.Rail, SidebarRailDetent.Large, 180f, 80f), (s.Regime, s.Detent, s.ExpandedWidth, s.TargetWidth));

        // The Shift variant behaves the same at the floor.
        Assert.Equal(SidebarRegime.Rail, SidebarResizeRules.Step(Expanded(180f), -1, large: true).Regime);
    }

    [Fact]
    public void Step_RightPastLargeExpandsAtTheRememberedWidth()
    {
        var s = SidebarResizeRules.Step(Rail(SidebarRailDetent.Large, 333f), +1, large: false);
        Assert.Equal((SidebarRegime.Expanded, 333f, 333f), (s.Regime, s.ExpandedWidth, s.TargetWidth));
        Assert.Equal(SidebarRailDetent.Large, s.Detent);   // the detent memory is kept

        // A remembered width under the floor expands AT the floor, but the memory itself is not rewritten here.
        var low = SidebarResizeRules.Step(Rail(SidebarRailDetent.Large, 150f), +1, large: false);
        Assert.Equal((SidebarRegime.Expanded, 180f, 180f), (low.Regime, low.ExpandedWidth, low.TargetWidth));
    }

    [Fact]
    public void Step_WalksTheDetents()
    {
        var s = Rail(SidebarRailDetent.Compact, 333f);

        var up1 = SidebarResizeRules.Step(s, +1, large: false);
        Assert.Equal((SidebarRegime.Rail, SidebarRailDetent.Default, 333f, 56f), (up1.Regime, up1.Detent, up1.ExpandedWidth, up1.TargetWidth));
        var up2 = SidebarResizeRules.Step(After(up1), +1, large: false);
        Assert.Equal((SidebarRegime.Rail, SidebarRailDetent.Large, 333f, 80f), (up2.Regime, up2.Detent, up2.ExpandedWidth, up2.TargetWidth));
        var up3 = SidebarResizeRules.Step(After(up2), +1, large: false);
        Assert.Equal(SidebarRegime.Expanded, up3.Regime);

        // And back down; the left wall of the rail does not wrap.
        var down1 = SidebarResizeRules.Step(Rail(SidebarRailDetent.Large, 333f), -1, large: false);
        Assert.Equal((SidebarRailDetent.Default, 56f), (down1.Detent, down1.TargetWidth));
        var down2 = SidebarResizeRules.Step(After(down1), -1, large: true);   // Shift does not change a detent step
        Assert.Equal((SidebarRailDetent.Compact, 48f), (down2.Detent, down2.TargetWidth));
        var wall = SidebarResizeRules.Step(After(down2), -1, large: false);
        Assert.Equal((SidebarRegime.Rail, SidebarRailDetent.Compact, 48f), (wall.Regime, wall.Detent, wall.TargetWidth));
    }

    // ── Toggle: hamburger / double-click / "<" ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggle_RoundTripsKeepingBothMemories()
    {
        var start = Expanded(333f, SidebarRailDetent.Large);

        var toRail = SidebarResizeRules.Toggle(start);
        Assert.Equal(new SidebarResizeRules.Settle(SidebarRegime.Rail, SidebarRailDetent.Large, 333f, 80f), toRail);

        var back = SidebarResizeRules.Toggle(After(toRail));
        Assert.Equal(new SidebarResizeRules.Settle(SidebarRegime.Expanded, SidebarRailDetent.Large, 333f, 333f), back);
        Assert.Equal(start, After(back));
    }

    [Fact]
    public void Toggle_ExpandingClampsTheTargetNotTheMemory()
    {
        var low = SidebarResizeRules.Toggle(Rail(SidebarRailDetent.Default, 100f));
        Assert.Equal((SidebarRegime.Expanded, 100f, 180f), (low.Regime, low.ExpandedWidth, low.TargetWidth));

        var high = SidebarResizeRules.Toggle(Rail(SidebarRailDetent.Default, 999f));
        Assert.Equal(460f, high.TargetWidth);
    }
}
