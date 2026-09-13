using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The lyrics surface's motion-demand gate: which lanes earn a per-frame stepper, and — when none do — the
/// media instant the surface has to be woken at instead. Pinned here because the alternative was the defect this
/// replaced: <c>playing</c> stood in for "has motion", so an open lyrics panel held the UI thread at panel rate for the
/// whole of a track (4146 frames in 30 s of an instrumental intro, 4085 of them record-only, to render 61).</summary>
public class LyricsMotionDemandTests
{
    const long Lead = 140L;   // LyricsView.LeadMs

    /// <summary>A settled, following, playing surface with nothing in flight — every test states only what it moves.</summary>
    static LyricsMotionLanes Rest(long nowMs = 10_000L, long nextEventMs = long.MaxValue) => new(
        Playing: true,
        VoiceActive: false,
        DotsActive: false,
        GlowFadeActive: false,
        DofRampPending: false,
        CascadePending: false,
        FollowUnsettled: false,
        Following: true,
        NowMs: nowMs,
        NextEventMs: nextEventMs);

    // ── Lanes that must never lose a frame ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASungLine_DemandsTicks()
    {
        var d = LyricsMotionDemand.Evaluate(Rest() with { VoiceActive = true });
        Assert.True(d.NeedsTicks);
        Assert.Equal(LyricsMotionDemand.None, d.WakeAtMs);   // live ⇒ nothing to re-arm
    }

    [Theory]
    [InlineData("dots")]
    [InlineData("glow")]
    [InlineData("dof")]
    [InlineData("follow")]
    public void EveryPlayingLaneInFlight_DemandsTicks(string lane)
    {
        var l = Rest();
        l = lane switch
        {
            "dots" => l with { DotsActive = true },
            "glow" => l with { GlowFadeActive = true },
            "dof" => l with { DofRampPending = true },
            _ => l with { FollowUnsettled = true },
        };
        Assert.True(LyricsMotionDemand.Evaluate(l).NeedsTicks);
    }

    [Fact]
    public void ACascadeAndADetachedFollow_DemandTicksEvenWhilePaused()
    {
        Assert.True(LyricsMotionDemand.Evaluate(Rest() with { Playing = false, CascadePending = true }).NeedsTicks);
        Assert.True(LyricsMotionDemand.Evaluate(Rest() with { Playing = false, Following = false }).NeedsTicks);
    }

    // ── The instrumental gap: quiesce, and re-arm on the media clock ─────────────────────────────────────────────────

    [Fact]
    public void AnInstrumentalGap_Quiesces_AndWakesAheadOfTheNextHandoff()
    {
        // Nothing in flight at 10 s; the next line starts at 30 s, so its lead-shifted handoff is 29.86 s.
        var d = LyricsMotionDemand.Evaluate(Rest(nowMs: 10_000, nextEventMs: 30_000 - Lead));
        Assert.False(d.NeedsTicks);
        Assert.Equal(30_000 - Lead - LyricsMotionDemand.ArmLeadMs, d.WakeAtMs);
    }

    [Fact]
    public void AnEventInsideTheArmLead_StaysLiveInsteadOfArmingATimeoutThatWouldFireAtOnce()
    {
        var l = Rest(nowMs: 10_000, nextEventMs: 10_000 + LyricsMotionDemand.ArmLeadMs);
        var d = LyricsMotionDemand.Evaluate(l);
        Assert.True(d.NeedsTicks);
        // One ms earlier is still outside the lead, so that one arms — the boundary is exact, not fuzzy.
        Assert.False(LyricsMotionDemand.Evaluate(l with { NowMs = 9_999 }).NeedsTicks);
    }

    [Fact]
    public void AnOutro_WakesForNothing()
    {
        var d = LyricsMotionDemand.Evaluate(Rest(nowMs: 300_000, nextEventMs: LyricsMotionDemand.None));
        Assert.False(d.NeedsTicks);
        Assert.Equal(LyricsMotionDemand.None, d.WakeAtMs);
    }

    // ── Paused keeps exactly the pre-existing contract ───────────────────────────────────────────────────────────────

    [Fact]
    public void Paused_DemandsNothingFromTheMediaClockLanes_AndArmsNoWake()
    {
        var paused = Rest(nowMs: 10_000, nextEventMs: 12_000) with
        {
            Playing = false, VoiceActive = true, DotsActive = true, GlowFadeActive = true,
            DofRampPending = true, FollowUnsettled = true,
        };
        var d = LyricsMotionDemand.Evaluate(paused);
        Assert.False(d.NeedsTicks);
        // A paused media clock never reaches a deadline on its own: the transport wakes the surface instead.
        Assert.Equal(LyricsMotionDemand.None, d.WakeAtMs);
    }

    // ── Deadline math at the line boundaries ─────────────────────────────────────────────────────────────────────────

    static IReadOnlyList<LyricLine> Doc() =>
    [
        new LyricLine(10_000, "one", Array.Empty<LyricSyllable>()),
        new LyricLine(20_000, "two", Array.Empty<LyricSyllable>()),
        new LyricLine(40_000, "three", Array.Empty<LyricSyllable>()),
    ];

    [Fact]
    public void BeforeTheFirstLine_TheDeadlineIsItsLeadShiftedHandoff()
        => Assert.Equal(10_000 - Lead, LyricsMotionDemand.NextEventMs(Doc(), fromLine: -1, nowMs: 0, Lead));

    [Fact]
    public void InsideTheLeadWindow_TheDeadlineIsTheLineStartItself()
    {
        // Past the handoff (9.86 s) but not yet at the line: the next thing that moves is the wipe, at the start.
        Assert.Equal(10_000, LyricsMotionDemand.NextEventMs(Doc(), fromLine: 0, nowMs: 9_900, Lead));
        // Exactly ON the handoff instant counts as past it — the step that consumed it has already run.
        Assert.Equal(10_000, LyricsMotionDemand.NextEventMs(Doc(), fromLine: 0, nowMs: 10_000 - Lead, Lead));
    }

    [Fact]
    public void InAGapBetweenLines_TheDeadlineIsTheNextHandoff()
    {
        Assert.Equal(20_000 - Lead, LyricsMotionDemand.NextEventMs(Doc(), fromLine: 0, nowMs: 15_000, Lead));
        Assert.Equal(40_000 - Lead, LyricsMotionDemand.NextEventMs(Doc(), fromLine: 1, nowMs: 25_000, Lead));
    }

    [Fact]
    public void OnTheLineStartItself_TheDeadlineIsTheFollowingHandoff()
        => Assert.Equal(20_000 - Lead, LyricsMotionDemand.NextEventMs(Doc(), fromLine: 0, nowMs: 10_000, Lead));

    [Fact]
    public void AfterTheLastLine_ThereIsNoDeadline()
        => Assert.Equal(LyricsMotionDemand.None, LyricsMotionDemand.NextEventMs(Doc(), fromLine: 2, nowMs: 50_000, Lead));

    [Fact]
    public void ADocumentWithNoLines_HasNoDeadline()
    {
        Assert.Equal(LyricsMotionDemand.None, LyricsMotionDemand.NextEventMs(null, 0, 0, Lead));
        Assert.Equal(LyricsMotionDemand.None, LyricsMotionDemand.NextEventMs(Array.Empty<LyricLine>(), 0, 0, Lead));
    }

    // ── The two together: an intro is spent asleep, and the first syllable is not ────────────────────────────────────

    [Fact]
    public void SteppingThroughAnIntro_IsQuiescentUntilTheHandoffComesIntoRange()
    {
        var doc = Doc();
        // 0 → 9.8 s: nothing has been sung, nothing is in flight. Every step must quiesce and re-arm at the handoff.
        for (long now = 0; now < 9_800; now += 500)
        {
            var d = LyricsMotionDemand.Evaluate(Rest(now, LyricsMotionDemand.NextEventMs(doc, -1, now, Lead)));
            Assert.False(d.NeedsTicks);
            Assert.Equal(10_000 - Lead - LyricsMotionDemand.ArmLeadMs, d.WakeAtMs);
        }
        // The wake lands at 9.828 s and the surface goes live there — a frame before the emphasis handoff at 9.86 s.
        long wake = 10_000 - Lead - LyricsMotionDemand.ArmLeadMs;
        Assert.True(LyricsMotionDemand.Evaluate(Rest(wake, LyricsMotionDemand.NextEventMs(doc, -1, wake, Lead))).NeedsTicks);
        // …and once the line is the voice line, the wipe holds it live on its own.
        Assert.True(LyricsMotionDemand.Evaluate(Rest(10_500, LyricsMotionDemand.NextEventMs(doc, 0, 10_500, Lead)) with { VoiceActive = true }).NeedsTicks);
    }
}
