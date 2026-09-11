using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>S3 #14: a card mounting fresh under a RESTING pointer (the back-navigation case — a page swap lands new
/// cards under a mouse that never actually moved) must not scale/reveal on its own OnPointerMoveWithin sample, since
/// the engine's stationary-cursor hover re-resolve (RefreshHoverAfterLayoutMove/RefreshHoverAfterScroll) re-fires
/// that callback at the SAME on-screen point with no real pointer edge behind it. <see cref="HoverMotionGate"/> is
/// the fix: it only arms once two DIFFERENT samples land, proving the pointer is genuinely moving.</summary>
public class HoverMotionGateTests
{
    [Fact]
    public void FirstSample_NeverArms_RegardlessOfPosition()
    {
        var gate = new HoverMotionGate();
        Assert.False(gate.Observe(new Point2(100f, 100f)));
    }

    [Fact]
    public void RepeatingTheSameSample_NeverArms()
    {
        // The stationary-cursor re-resolve delivers the SAME point over and over (a page swap under a still mouse,
        // or a scroll tick that re-hit-tests at the last known pointer position) — none of that is real motion.
        var gate = new HoverMotionGate();
        var p = new Point2(42f, 17f);
        Assert.False(gate.Observe(p));
        for (int i = 0; i < 5; i++) Assert.False(gate.Observe(p));
    }

    [Fact]
    public void SubPixelJitter_AtTheSamePoint_DoesNotArm()
    {
        // Re-hit-testing the same float geometry can drift by a hair below a device pixel — that must not itself
        // read as "moved" and falsely arm the gate.
        var gate = new HoverMotionGate();
        Assert.False(gate.Observe(new Point2(10f, 10f)));
        Assert.False(gate.Observe(new Point2(10.2f, 10.2f)));
    }

    [Fact]
    public void ADifferentLaterSample_Arms()
    {
        var gate = new HoverMotionGate();
        Assert.False(gate.Observe(new Point2(10f, 10f)));
        Assert.True(gate.Observe(new Point2(40f, 10f)));   // a real, unambiguous move
    }

    [Fact]
    public void OnceArmed_StaysArmed_EvenIfLaterSamplesReturnToTheBaseline()
    {
        // One-shot "has real input happened since mount" latch, not a per-hover-cycle re-check (WaveeMotion.cs's
        // doc): a page the user is already interacting with must not re-litigate every hover-out/in.
        var gate = new HoverMotionGate();
        Assert.False(gate.Observe(new Point2(0f, 0f)));
        Assert.True(gate.Observe(new Point2(50f, 0f)));
        Assert.True(gate.Observe(new Point2(0f, 0f)));     // back to the original point — still armed
        Assert.True(gate.Observe(new Point2(0f, 0f)));
    }

    [Fact]
    public void MovementOnEitherAxisAlone_Arms()
    {
        var xOnly = new HoverMotionGate();
        Assert.False(xOnly.Observe(new Point2(0f, 0f)));
        Assert.True(xOnly.Observe(new Point2(5f, 0f)));

        var yOnly = new HoverMotionGate();
        Assert.False(yOnly.Observe(new Point2(0f, 0f)));
        Assert.True(yOnly.Observe(new Point2(0f, 5f)));
    }
}
