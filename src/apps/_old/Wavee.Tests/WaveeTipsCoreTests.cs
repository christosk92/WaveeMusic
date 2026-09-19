using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

// The teaching-tip service's PURE half (App/WaveeTipsCore.cs, source-included): the acknowledged-id SET codec and the one
// gating decision every tip passes through.
//
// Worth pinning for the same reason as the sidebar chooser's marker: these are one-string-read / one-string-write
// decisions whose failures are per-install and invisible in a diff. A codec that mis-parses turns "already dismissed"
// into a callout that returns forever; an id written twice grows a settings value without bound; a gate that forgets the
// one-at-a-time rule stacks two callouts over each other.
//
// Everything here drives the PRODUCTION type, never a copy of its rules.
public class WaveeTipsCoreTests
{
    const string A = WaveeTipIds.DetailTuning;   // "detail.tuning"
    const string B = "sidebar.customizer";       // a plausible FUTURE id — the codec must not care that it is unknown

    // ── the set codec ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptySetContainsNothing()
    {
        Assert.False(WaveeTipsCore.Contains(null, A));
        Assert.False(WaveeTipsCore.Contains("", A));
        Assert.Empty(WaveeTipsCore.Parse(null));
        Assert.Empty(WaveeTipsCore.Parse(""));
    }

    [Fact]
    public void AddThenContains()
    {
        var seen = WaveeTipsCore.Add(null, A);
        Assert.Equal(A, seen);
        Assert.True(WaveeTipsCore.Contains(seen, A));
        Assert.False(WaveeTipsCore.Contains(seen, B));
    }

    [Fact]
    public void AddIsIdempotent()
    {
        var once = WaveeTipsCore.Add(null, A);
        var twice = WaveeTipsCore.Add(once, A);
        Assert.Same(once, twice);   // the same instance back — re-acknowledging never grows the stored value
    }

    [Fact]
    public void RoundTripsManyIdsInOrder()
    {
        var seen = WaveeTipsCore.Add(WaveeTipsCore.Add(null, A), B);
        Assert.Equal(new List<string> { A, B }, WaveeTipsCore.Parse(seen));
        Assert.True(WaveeTipsCore.Contains(seen, A));
        Assert.True(WaveeTipsCore.Contains(seen, B));
        Assert.Equal(seen, WaveeTipsCore.Serialize(WaveeTipsCore.Parse(seen)));
    }

    [Fact]
    public void SerializeDropsDuplicatesAndEmpties()
        => Assert.Equal(A + WaveeTipsCore.Separator + B,
            WaveeTipsCore.Serialize(new[] { A, "", B, A, null! }));

    // A hand-edited / older value (leading, trailing and doubled separators) must not wedge the codec.
    [Fact]
    public void ToleratesMalformedStoredValue()
    {
        string raw = WaveeTipsCore.Separator + A + WaveeTipsCore.Separator + WaveeTipsCore.Separator + B + WaveeTipsCore.Separator;
        Assert.True(WaveeTipsCore.Contains(raw, A));
        Assert.True(WaveeTipsCore.Contains(raw, B));
        Assert.Equal(new List<string> { A, B }, WaveeTipsCore.Parse(raw));
    }

    // A PREFIX of a stored id is not a member — the scan compares whole segments, so "detail" never matches
    // "detail.tuning" (nor the reverse).
    [Fact]
    public void MatchesWholeSegmentsOnly()
    {
        var seen = WaveeTipsCore.Add(null, A);
        Assert.False(WaveeTipsCore.Contains(seen, "detail"));
        Assert.False(WaveeTipsCore.Contains(seen, A + ".extra"));
    }

    [Fact]
    public void EmptyIdIsNeverAddedNorFound()
    {
        Assert.Equal("", WaveeTipsCore.Add(null, ""));
        Assert.Equal(A, WaveeTipsCore.Add(A, null));
        Assert.False(WaveeTipsCore.Contains(A, ""));
    }

    // ── the gate ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShowsWhenUnseenUnarmedAndAlone()
        => Assert.True(WaveeTipsCore.ShouldShow(seen: "", A, armedThisSession: false, anotherTipActive: false, canPresent: true));

    [Fact]
    public void SuppressedOnceAcknowledged()
    {
        var seen = WaveeTipsCore.Add(null, A);
        Assert.False(WaveeTipsCore.ShouldShow(seen, A, false, false, true));
        Assert.True(WaveeTipsCore.ShouldShow(seen, B, false, false, true));   // a DIFFERENT tip is unaffected
    }

    [Fact]
    public void SuppressedOncePerLaunch()
        => Assert.False(WaveeTipsCore.ShouldShow("", A, armedThisSession: true, anotherTipActive: false, canPresent: true));

    [Fact]
    public void OneTipAtATime()
        => Assert.False(WaveeTipsCore.ShouldShow("", A, armedThisSession: false, anotherTipActive: true, canPresent: true));

    // No overlay / no persistence seam ⇒ never shown: a tip whose dismissal cannot be remembered would return on every
    // page forever.
    [Fact]
    public void SuppressedWhenItCannotBePresented()
        => Assert.False(WaveeTipsCore.ShouldShow("", A, false, false, canPresent: false));

    [Fact]
    public void EmptyIdNeverShows()
        => Assert.False(WaveeTipsCore.ShouldShow("", "", false, false, true));
}
