// ── Wavee.Tests/AccentLadderTests.cs — the page accent ladder and the cross-page hold ─────────────────────────────────
//
// Pure: no engine loop, no palette table. AccentHold is process state, so every fact that touches it resets it first
// and the class is kept out of the parallel test collections.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class AccentLadderRuleTests
{
    static readonly ColorF Graded = new(0.1f, 0.6f, 0.3f, 1f);
    static readonly ColorF Held = new(0.9f, 0.2f, 0.2f, 1f);
    static readonly ColorF Fallback = new(0f, 0.4f, 0.8f, 1f);
    static readonly ColorF Lifted = new(0.5f, 0.5f, 0.1f, 1f);
    const uint Payload = 0xFFC2185Bu;

    static ColorF Lift(uint argb) => Lifted;

    [Fact]
    public void Graded_beats_payload()
    {
        var r = AccentLadder.Resolve(new(Graded, Payload, Definite: false), Held, Fallback, Lift);
        Assert.Equal(Graded, r.Color);
        Assert.Equal(AccentLadder.Rung.Graded, r.Rung);
    }

    [Fact]
    public void Payload_beats_held()
    {
        var r = AccentLadder.Resolve(new(null, Payload, Definite: false), Held, Fallback, Lift);
        Assert.Equal(Lifted, r.Color);
        Assert.Equal(AccentLadder.Rung.Payload, r.Rung);
    }

    [Fact]
    public void Held_stands_in_only_while_the_answer_is_pending()
    {
        var r = AccentLadder.Resolve(new(null, 0, Definite: false), Held, Fallback, Lift);
        Assert.Equal(Held, r.Color);
        Assert.Equal(AccentLadder.Rung.Held, r.Rung);
    }

    [Fact]
    public void Definite_skips_the_hold_and_lands_on_the_fallback()
    {
        var r = AccentLadder.Resolve(new(null, 0, Definite: true), Held, Fallback, Lift);
        Assert.Equal(Fallback, r.Color);
        Assert.Equal(AccentLadder.Rung.Default, r.Rung);
    }

    [Fact]
    public void No_hold_and_pending_is_the_fallback()
    {
        var r = AccentLadder.Resolve(new(null, 0, Definite: false), null, Fallback, Lift);
        Assert.Equal(Fallback, r.Color);
        Assert.Equal(AccentLadder.Rung.Default, r.Rung);
    }

    [Fact]
    public void Only_graded_and_payload_are_remembered()
    {
        Assert.True(new AccentLadder.Result(Graded, AccentLadder.Rung.Graded).Remember);
        Assert.True(new AccentLadder.Result(Lifted, AccentLadder.Rung.Payload).Remember);
        Assert.False(new AccentLadder.Result(Held, AccentLadder.Rung.Held).Remember);
        Assert.False(new AccentLadder.Result(Fallback, AccentLadder.Rung.Default).Remember);
    }

    [Fact]
    public void Hold_ignores_held_and_default_answers()
    {
        AccentHold.Reset();
        AccentHold.Remember(new AccentLadder.Result(Held, AccentLadder.Rung.Held));
        AccentHold.Remember(new AccentLadder.Result(Fallback, AccentLadder.Rung.Default));
        Assert.Null(AccentHold.Last);
        AccentHold.Reset();
    }

    [Fact]
    public void Hold_carries_a_graded_page_into_the_next_pending_page()
    {
        AccentHold.Reset();
        // Page A: graded.
        var a = AccentLadder.Resolve(new(Graded, 0, Definite: false), AccentHold.Last, Fallback, Lift);
        AccentHold.Remember(a);
        Assert.Equal(Graded, AccentHold.Last);

        // Page B: nothing yet, grading pending → A's colour, and the hold is unchanged.
        var b = AccentLadder.Resolve(new(null, 0, Definite: false), AccentHold.Last, Fallback, Lift);
        AccentHold.Remember(b);
        Assert.Equal(Graded, b.Color);
        Assert.Equal(AccentLadder.Rung.Held, b.Rung);
        Assert.Equal(Graded, AccentHold.Last);
        AccentHold.Reset();
    }

    [Fact]
    public void Reset_forgets_the_last_answer()
    {
        AccentHold.Remember(new AccentLadder.Result(Graded, AccentLadder.Rung.Graded));
        AccentHold.Reset();
        Assert.Null(AccentHold.Last);
    }

    [Fact]
    public void Greyscale_grading_equal_to_the_fallback_is_still_graded()
    {
        // ChromeAccent answers the system accent for greyscale art; that is a graded answer, not a default one.
        var r = AccentLadder.Resolve(new(Fallback, Payload, Definite: false), Held, Fallback, Lift);
        Assert.Equal(Fallback, r.Color);
        Assert.Equal(AccentLadder.Rung.Graded, r.Rung);
        Assert.True(r.Remember);
    }
}
