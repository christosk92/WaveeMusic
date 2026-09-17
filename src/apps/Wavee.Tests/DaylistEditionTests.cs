// ── Wavee.Tests/DaylistEditionTests.cs — which daylist window outranks the held one ──────────────────────────────────
//
// Pure: no clock, no decoder.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DaylistEditionTests
{
    [Fact]
    public void Unknown_window_never_outranks()
    {
        Assert.False(DaylistEdition.Outranks(incomingKnowsWindow: false, incomingWindowEndS: 500, heldWindowEndS: 0));
        Assert.False(DaylistEdition.Outranks(incomingKnowsWindow: false, incomingWindowEndS: 500, heldWindowEndS: 100));
    }

    [Fact]
    public void Incoming_zero_never_outranks()
    {
        Assert.False(DaylistEdition.Outranks(incomingKnowsWindow: true, incomingWindowEndS: 0, heldWindowEndS: 0));
    }

    [Fact]
    public void Held_zero_and_incoming_known_positive_outranks()
    {
        Assert.True(DaylistEdition.Outranks(incomingKnowsWindow: true, incomingWindowEndS: 1, heldWindowEndS: 0));
    }

    [Fact]
    public void Equal_windows_do_not_outrank()
    {
        Assert.False(DaylistEdition.Outranks(incomingKnowsWindow: true, incomingWindowEndS: 500, heldWindowEndS: 500));
    }

    [Fact]
    public void Older_window_does_not_outrank()
    {
        Assert.False(DaylistEdition.Outranks(incomingKnowsWindow: true, incomingWindowEndS: 400, heldWindowEndS: 500));
    }

    [Fact]
    public void Newer_window_outranks()
    {
        Assert.True(DaylistEdition.Outranks(incomingKnowsWindow: true, incomingWindowEndS: 600, heldWindowEndS: 500));
    }
}
