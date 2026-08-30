using Xunit;
using static Wavee.Backend.Collections.CollectionSweepPolicy;

namespace Wavee.Tests;

// Which absent local members a VERIFIED snapshot may remove: in the snapshot → keep; a pending intent → keep; added
// inside the recency window → keep (write→page lag); otherwise remove.
public class CollectionSweepPolicyTests
{
    const long Walk = 1_800_000_000_000L;

    [Fact]
    public void InSnapshot_AlwaysKept_EvenWhenPendingOrOld()
    {
        Assert.Equal(Keep.InSnapshot, Decide(inSnapshot: true, hasPending: true, addedAtMs: 0, walkStartedAtMs: Walk));
        Assert.Equal(Keep.InSnapshot, Decide(inSnapshot: true, hasPending: false, addedAtMs: Walk - 100 * RecencyShieldMs, walkStartedAtMs: Walk));
    }

    [Fact]
    public void Absent_WithPendingIntent_IsShielded()
    {
        Assert.Equal(Keep.Pending, Decide(inSnapshot: false, hasPending: true, addedAtMs: 0, walkStartedAtMs: Walk));
    }

    [Fact]
    public void Absent_AddedInsideTheWindow_IsShieldedAsRecent()
    {
        Assert.Equal(Keep.Recent, Decide(false, false, addedAtMs: Walk - 60_000, walkStartedAtMs: Walk));                 // a minute before the walk
        Assert.Equal(Keep.Recent, Decide(false, false, addedAtMs: Walk - RecencyShieldMs, walkStartedAtMs: Walk));        // exactly on the edge
        Assert.Equal(Keep.Recent, Decide(false, false, addedAtMs: Walk + 5_000, walkStartedAtMs: Walk));                  // liked DURING the walk
    }

    [Fact]
    public void Absent_OlderThanTheWindow_IsRemoved()
    {
        Assert.Equal(Keep.Remove, Decide(false, false, addedAtMs: Walk - RecencyShieldMs - 1, walkStartedAtMs: Walk));
        Assert.Equal(Keep.Remove, Decide(false, false, addedAtMs: Walk - 3_600_000, walkStartedAtMs: Walk));
    }

    [Fact]
    public void Absent_WithNoTimestamp_HasNothingToBeRecentBy_AndIsRemoved()
    {
        Assert.Equal(Keep.Remove, Decide(false, false, addedAtMs: 0, walkStartedAtMs: Walk));
    }

    [Fact]
    public void TheWindow_IsTenMinutes()
    {
        Assert.Equal(10 * 60 * 1000L, RecencyShieldMs);
    }
}
