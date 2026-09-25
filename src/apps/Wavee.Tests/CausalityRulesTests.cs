// ── Wavee.Tests/CausalityRulesTests.cs — `CausalityRules.NewEvent` (§6.5) ───────────────────────────────────────────
//
// The id-propagation table: a user input mints a fresh root; an event under a known cause inherits that cause's
// root; a remote push with no threaded cause (`threadedCauseId == 0`) mints its OWN root and never inherits
// whatever the lookup would otherwise report — the pool-thread-leak guard from §2.2, made assertable here because
// the function takes the lookup as a parameter instead of reading ambient state itself.

using System;
using Xunit;

namespace Wavee.Tests;

public class CausalityRulesTests
{
    private static long NeverCalled(long id) => throw new InvalidOperationException("lookup must not be consulted when threadedCauseId == 0");

    [Fact]
    public void A_user_input_with_no_threaded_cause_mints_a_fresh_root()
    {
        var (id, causeId, rootId) = CausalityRules.NewEvent(nextSeq: 42, threadedCauseId: 0, NeverCalled);

        Assert.Equal(42, id);
        Assert.Equal(0, causeId);
        Assert.Equal(42, rootId); // RootId == Id when the event IS the root
    }

    [Fact]
    public void An_event_under_a_known_cause_inherits_its_root_but_keeps_its_own_id()
    {
        long Lookup(long id) => id == 7 ? 1 : throw new InvalidOperationException("unexpected lookup id " + id);

        var (id, causeId, rootId) = CausalityRules.NewEvent(nextSeq: 99, threadedCauseId: 7, Lookup);

        Assert.Equal(99, id);
        Assert.Equal(7, causeId);
        Assert.Equal(1, rootId);
    }

    [Fact]
    public void A_cause_whose_own_root_is_unknown_falls_back_to_the_cause_itself()
    {
        // The ring-buffer-backed lookup a real host uses is only approximate (bounded memory, §Capture.cs) — a
        // stale/evicted slot must degrade gracefully, never crash and never silently attribute to root 0.
        var (id, causeId, rootId) = CausalityRules.NewEvent(nextSeq: 100, threadedCauseId: 55, static _ => 0);

        Assert.Equal(100, id);
        Assert.Equal(55, causeId);
        Assert.Equal(55, rootId);
    }

    [Fact]
    public void A_remote_push_with_no_threaded_cause_never_inherits_a_previous_ambient_value()
    {
        // Simulates a pool thread reused for an unrelated unit of work: even though a lookup COULD report a root
        // for some stale id, threadedCauseId == 0 must short-circuit before the lookup is ever consulted.
        var (id, causeId, rootId) = CausalityRules.NewEvent(nextSeq: 5, threadedCauseId: 0, NeverCalled);

        Assert.Equal(0, causeId);
        Assert.Equal(5, rootId);
        Assert.Equal(5, id);
    }
}
