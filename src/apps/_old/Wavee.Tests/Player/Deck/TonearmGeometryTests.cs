using System;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>
/// Pins the headshell drag's arithmetic (<see cref="TonearmGeometry"/>): a pointer sample lands somewhere on the
/// deck, and these two functions are the whole path from there to a playback position. Both are pure, so the drag's
/// behaviour is testable without a pointer, a face or a window.
/// </summary>
public sealed class TonearmGeometryTests
{
    const float Cx = 160f, Cy = 160f, RecordD = 200f;      // a 200-DIP record centred at (160, 160)
    const float R = RecordD * 0.5f;

    static void Near(float expected, float actual, float tol = 0.001f)
        => Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual} (tolerance {tol})");

    /// <summary>A deck point at a given radius, at a given bearing — the shape a pointer sample actually has.</summary>
    static (float X, float Y) At(float radiusFrac, float bearingDeg)
    {
        float r = radiusFrac * R, a = bearingDeg * (MathF.PI / 180f);
        return (Cx + r * MathF.Cos(a), Cy + r * MathF.Sin(a));
    }

    static float FracAt(float radiusFrac, float bearingDeg = 0f)
    {
        var (x, y) = At(radiusFrac, bearingDeg);
        return TonearmGeometry.FracFromDeckPoint(x, y, Cx, Cy, RecordD);
    }

    // ── deck point → position ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band the grooves live in: from the label's edge out to the rim, and those two add up to the radius.</summary>
    [Fact]
    public void GrooveBand_RunsFromTheLabelEdgeToTheRim()
    {
        Assert.Equal(0.34f, TonearmGeometry.LabelRadiusFrac);
        Near(1f, TonearmGeometry.LabelRadiusFrac + TonearmGeometry.GrooveBandFrac);
    }

    /// <summary>
    /// A stylus travels INWARD: the rim is where the music starts (position 0) and the label edge is where it ends
    /// (position 1). Getting this backwards would make every drag run the track in reverse.
    /// </summary>
    [Fact]
    public void Rim_IsPositionZero_AndTheLabelEdge_IsPositionOne()
    {
        Near(0f, FracAt(1f));
        Near(1f, FracAt(TonearmGeometry.LabelRadiusFrac));
    }

    /// <summary>Across the groove band the mapping is linear — half way in is half way through.</summary>
    [Theory]
    [InlineData(0.34f, 1f)]
    [InlineData(0.5f, 0.757576f)]
    [InlineData(0.67f, 0.5f)]
    [InlineData(0.835f, 0.25f)]
    [InlineData(1f, 0f)]
    public void FracFromDeckPoint_IsLinearAcrossTheGrooveBand(float radiusFrac, float expected)
        => Near(expected, FracAt(radiusFrac), 0.0005f);

    /// <summary>Inside the label and outside the rim the drag saturates instead of running off the end of the track.</summary>
    [Fact]
    public void FracFromDeckPoint_ClampsInsideTheLabelAndOutsideTheRim()
    {
        Assert.Equal(1f, FracAt(0f));                                  // the spindle
        Assert.Equal(1f, FracAt(0.1f));                                // on the label
        Assert.Equal(0f, FracAt(1.4f));                                // off the record entirely
        Assert.Equal(0f, TonearmGeometry.FracFromDeckPoint(-500f, -500f, Cx, Cy, RecordD));
    }

    /// <summary>Only the DISTANCE from the spindle matters: a groove is a circle, so bearing cannot change the position.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(37f)]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(-125f)]
    public void FracFromDeckPoint_DependsOnlyOnTheDistanceFromTheSpindle(float bearingDeg)
        => Near(FracAt(0.67f, 0f), FracAt(0.67f, bearingDeg), 0.0005f);

    /// <summary>A 7″ single is a smaller record, and the same physical point on it is a DIFFERENT position.</summary>
    [Fact]
    public void FracFromDeckPoint_ScalesWithTheRecordDiameter()
    {
        // 50 DIP out from the spindle is the rim of a 100-DIP single, but only half way out on a 200-DIP LP.
        Near(0f, TonearmGeometry.FracFromDeckPoint(Cx + 50f, Cy, Cx, Cy, 100f));
        Near(0.757576f, TonearmGeometry.FracFromDeckPoint(Cx + 50f, Cy, Cx, Cy, 200f), 0.0005f);
    }

    // ── arm-local point → deck point ───────────────────────────────────────────────────────────────────────────────

    const float ArmX = 100f, ArmY = -8f, ArmW = 100f, ArmH = 100f;
    const float PivotLx = ArmW * 0.5f, PivotLy = ArmH * 0.08f;          // the face's TransformOrigin (.5, .08)
    const float PivotDx = ArmX + PivotLx, PivotDy = ArmY + PivotLy;

    static (float X, float Y) ToDeck(float lx, float ly, float armDeg)
        => TonearmGeometry.ArmLocalToDeck(lx, ly, armDeg, ArmX, ArmY, ArmW, ArmH);

    /// <summary>An unrotated arm is just its own box offset into the deck.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(50f, 8f)]
    [InlineData(50f, 92f)]
    [InlineData(12f, 71f)]
    public void ArmLocalToDeck_IsAPlainTranslationAtZeroDegrees(float lx, float ly)
    {
        var (x, y) = ToDeck(lx, ly, 0f);
        Near(ArmX + lx, x);
        Near(ArmY + ly, y);
    }

    /// <summary>The pivot is the one point the arm rotates ABOUT, so no angle may move it.</summary>
    [Theory]
    [InlineData(-34f)]
    [InlineData(-20f)]
    [InlineData(-3f)]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(180f)]
    public void ArmLocalToDeck_LeavesThePivotFixed(float armDeg)
    {
        var (x, y) = ToDeck(PivotLx, PivotLy, armDeg);
        Near(PivotDx, x, 0.0005f);
        Near(PivotDy, y, 0.0005f);
    }

    /// <summary>A rotation is rigid: the stylus never gets nearer to or further from the pivot.</summary>
    [Theory]
    [InlineData(-34f)]
    [InlineData(-20f)]
    [InlineData(-3f)]
    [InlineData(45f)]
    [InlineData(137f)]
    public void ArmLocalToDeck_PreservesTheDistanceFromThePivot(float armDeg)
    {
        const float StylusLy = PivotLy + 80f;
        var (x, y) = ToDeck(PivotLx, StylusLy, armDeg);
        float dx = x - PivotDx, dy = y - PivotDy;
        Near(80f, MathF.Sqrt(dx * dx + dy * dy), 0.001f);
    }

    /// <summary>…and it rotates the RIGHT way: +90° swings a downward headshell to the pivot's left (screen y grows down).</summary>
    [Fact]
    public void ArmLocalToDeck_RotatesClockwiseInScreenSpace()
    {
        var (x, y) = ToDeck(PivotLx, PivotLy + 80f, 90f);
        Near(PivotDx - 80f, x, 0.001f);
        Near(PivotDy, y, 0.001f);

        var (x2, y2) = ToDeck(PivotLx, PivotLy + 80f, -90f);
        Near(PivotDx + 80f, x2, 0.001f);
        Near(PivotDy, y2, 0.001f);
    }

    // A plausible deck: the pivot at (150, 0) sits outside a 200-DIP record whose spindle is at (120, 100), and an
    // 80-DIP arm sweeping the machine's own −34° → −3° range walks the stylus from off the record in to the label.
    const float StylusLy = PivotLy + 80f, SpindleX = 120f, SpindleY = 100f;

    static float FracForArm(float armDeg)
    {
        var (x, y) = ToDeck(PivotLx, StylusLy, armDeg);
        return TonearmGeometry.FracFromDeckPoint(x, y, SpindleX, SpindleY, RecordD);
    }

    /// <summary>
    /// The whole drag path in one go: a pointer sample on the headshell, mapped through the arm's current angle into
    /// deck space, and from there onto the record as a position.
    /// </summary>
    [Fact]
    public void HeadshellSample_MapsThroughTheArmOntoTheRecord()
    {
        var (x, y) = ToDeck(PivotLx, StylusLy, TonearmMachine.LeadInDeg);
        Near(177.3616f, x, 0.001f);
        Near(75.1754f, y, 0.001f);
        Near(0.568138f, TonearmGeometry.FracFromDeckPoint(x, y, SpindleX, SpindleY, RecordD), 0.0005f);
    }

    /// <summary>…and a deeper swing always reads as a LATER position: the two functions compose monotonically.</summary>
    [Fact]
    public void DeeperSwing_AlwaysReadsAsALaterPosition()
    {
        float previous = -1f;
        for (float deg = TonearmMachine.RestDeg; deg <= TonearmMachine.RunOutDeg; deg += 1f)
        {
            float frac = FracForArm(deg);
            Assert.True(frac > previous, $"at {deg}° the position went backwards ({frac} after {previous})");
            previous = frac;
        }
        Assert.True(previous < 1f, "the sweep must stop short of the label, not run off the end of the record");
    }
}
