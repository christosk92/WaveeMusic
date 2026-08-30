using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>Wave 5 of the design-system convergence: the ENTRANCE gates.
///
/// <para><c>WaveeMotion.StaggerMs</c> shipped in Wave 2 as a declared forward reference with zero call sites. A token
/// nobody reads is not a decision, it is a comment — so this wave either wires it or deletes it. It is wired, through
/// exactly one recipe (<see cref="WaveeEntrance"/>), and these tests pin the three properties that make the recipe
/// safe to reuse:</para>
/// <list type="bullet">
///   <item>the delay ladder IS <c>StaggerMs</c> per item (the token is genuinely reached, not merely referenced);</item>
///   <item>it is CAPPED — the unbounded <c>index × ms</c> spelling is what turns a 50-row list into a two-second
///         arrival whose tail animates off-screen;</item>
///   <item>it collapses to zero delay under reduced motion as a VALUE, never a branch — a previous stagger attempt
///         gated an entrance HOOK on <c>Motion.ReducedMotion</c>, which changes the hook count between renders and
///         crashes the reconciler when the flag flips mid-session (a resize grip flips it).</item>
/// </list>
///
/// <para>Shares a collection with <see cref="MotionSystemTests"/>: both mutate the process-wide
/// <c>Motion.ReducedMotion</c>, so they must not run concurrently.</para></summary>
[Collection("wavee-motion-global")]
public class EntranceStaggerTests
{
    // ── The ladder ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The token is REACHED, not merely declared: one step of the ladder is exactly one <c>StaggerMs</c>.
    /// This is the assertion that makes the Wave-2 forward reference real — change the token and the recipe moves.
    /// </summary>
    [Fact]
    public void OneRung_IsExactlyTheStaggerToken()
    {
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            Assert.Equal(WaveeMotion.StaggerMs, WaveeEntrance.DelayMs(1));
            Assert.Equal(WaveeMotion.StaggerMs, WaveeEntrance.DelayMs(2) - WaveeEntrance.DelayMs(1));
        }
        finally { Motion.ReducedMotion = saved; }
    }

    public static TheoryData<int, float> Ladder() => new()
    {
        // index, delay (ms)
        { 0,   0f },
        { 1,  40f },
        { 4, 160f },
        { 7, 280f },
        { 8, 320f },   // the cap itself
    };

    [Theory]
    [MemberData(nameof(Ladder))]
    public void TheDelayLadder_IsIndexTimesTheRung_UpToTheCap(int index, float delayMs)
    {
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            Assert.Equal(delayMs, WaveeEntrance.DelayMs(index));
            Assert.Equal(delayMs, WaveeEntrance.Row(index).DelayMs);
        }
        finally { Motion.ReducedMotion = saved; }
    }

    /// <summary>Everything past the cap lands TOGETHER, on the cap's own delay — the whole entrance is bounded at
    /// <c>StaggerCap × StaggerMs</c> regardless of list length. Without this a search result page (50 rows) would take
    /// two seconds to finish arriving, most of it below the fold.</summary>
    [Fact]
    public void PastTheCap_EveryItemSharesTheCapDelay()
    {
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            float cap = WaveeEntrance.DelayMs(WaveeEntrance.StaggerCap);
            Assert.Equal(WaveeEntrance.StaggerCap * WaveeMotion.StaggerMs, cap);
            foreach (int i in new[] { 9, 12, 50, 5000 })
                Assert.Equal(cap, WaveeEntrance.DelayMs(i));

            // …and the whole cascade is bounded well inside a second.
            Assert.True(cap <= 400f, $"the capped entrance takes {cap}ms — a list should finish arriving, not perform");
        }
        finally { Motion.ReducedMotion = saved; }
    }

    /// <summary>A negative index (a caller passing an unresolved slot) must not produce a negative delay, which the
    /// engine would read as "already elapsed" on a channel it has not seeded yet.</summary>
    [Fact]
    public void ANegativeIndex_ClampsToZero()
    {
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            Assert.Equal(0f, WaveeEntrance.DelayMs(-1));
            Assert.Equal(0f, WaveeEntrance.DelayMs(int.MinValue));
        }
        finally { Motion.ReducedMotion = saved; }
    }

    // ── The terminal ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The entrance itself: a short rise (≤8 DIP — the brief's ceiling and the same distance the engine's own
    /// skeleton reveal uses), a full fade, an active terminal, and NO position/size channel — this is an entrance, not
    /// a layout animation, so it must never arm a FLIP on the node it decorates.</summary>
    [Fact]
    public void TheEntranceTerminal_IsAShortRiseAndAFade()
    {
        var t = WaveeEntrance.Row(0);
        Assert.True(t.Enter.Active);
        Assert.Equal(0f, t.Enter.Opacity);
        Assert.Equal(WaveeEntrance.RiseDip, t.Enter.Dy);
        Assert.True(t.Enter.Dy is > 0f and <= 8f, $"the rise is {t.Enter.Dy} DIP — subtle means ≤8");
        Assert.Equal(TransitionChannels.Opacity, t.Channels);
        Assert.Equal(TransitionChannels.None, t.Channels & TransitionChannels.Position);
        Assert.Equal(TransitionChannels.None, t.Channels & TransitionChannels.Size);
        // A tween, not a spring: a cascade wants every item to take the SAME time however late it starts.
        Assert.Equal(DynamicsKind.Tween, t.Dynamics.Kind);
        Assert.True(t.Dynamics.DurationMs > 0f);
    }

    // ── Reduced motion ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE gate. Under reduced motion every delay is 0 — read inside the recipe, so no call site can forget
    /// it and, critically, so no call site needs an <c>if</c>. The recipe still returns a transition (the engine's own
    /// <c>ReducedSnap</c> parks the rise and the blur at their end state while keeping the opacity cross-fade, which
    /// is the canon answer: a fade aids orientation, it is not motion). Returning a DIFFERENT SHAPE here — null, or a
    /// second element tree — is the thing that breaks hook order when the flag flips mid-session.</summary>
    [Fact]
    public void UnderReducedMotion_EveryDelayCollapsesToZero()
    {
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = true;
            foreach (int i in new[] { 0, 1, 3, 8, 40 })
            {
                Assert.Equal(0f, WaveeEntrance.DelayMs(i));
                Assert.Equal(0f, WaveeEntrance.Row(i).DelayMs);
            }

            // Same SHAPE in both modes — only the delay is a value that moved.
            var reduced = WaveeEntrance.Row(3);
            Motion.ReducedMotion = false;
            var full = WaveeEntrance.Row(3);
            Assert.Equal(full.Channels, reduced.Channels);
            Assert.Equal(full.Enter, reduced.Enter);
            Assert.Equal(full.Dynamics, reduced.Dynamics);
            Assert.NotEqual(full.DelayMs, reduced.DelayMs);
        }
        finally { Motion.ReducedMotion = saved; }
    }
}
