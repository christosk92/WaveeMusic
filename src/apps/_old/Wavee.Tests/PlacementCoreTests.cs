using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The full behavioral spec of the surface placement state machine (<see cref="PlacementCore"/>) — the pure decision
/// layer every video surface, the detached-window owner and the player-bar affordance derive from. Sections:
/// activation (the parity port of the deleted <c>VideoPlacementLogicTests</c>), the click spec, sticky-off, availability,
/// host-close, fullscreen, the owner mount decisions, one NAMED regression per historical bug, and two property tests
/// over arbitrary command sequences.
/// </summary>
public class PlacementCoreTests
{
    static readonly PlacementPolicy Policy = PlacementPolicy.Video;
    // a track that HAS a video — now all four rungs (Docked/Fullscreen widened in), since content availability is no
    // longer "floating or detached only"; HOST capability is the separate mask VideoUpgradeGate.AvailabilityFor applies.
    const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    static PlacementState Off(PlacementSet available = All)
        => PlacementState.Initial(Policy) with { Available = available };

    static PlacementState At(SurfacePlacement p, PlacementSet available = All)
        => PlacementCore.OpenAt(Off(available), p);

    /// <summary>What a TRACK CHANGE does to this model, and all it does: the bridge recomputes availability for the new
    /// track (PlaybackBridge.RecomputeHasVideo → WithAvailability). There is no longer any per-track state in here — the
    /// content generation that used to expire a dismiss is gone with the dismiss. A video-less track passes
    /// <see cref="PlacementSet.None"/>; a track with a video passes <see cref="All"/>.</summary>
    static PlacementState NextTrack(in PlacementState s, PlacementSet available = All)
        => PlacementCore.WithAvailability(s, available);

    // ── activation (parity port: the old VideoPlacementLogic.VideoActive cases, one for one) ─────────────────────────

    [Fact]
    public void Active_WhenRequestedAndContentHasVideo()
        => Assert.True(PlacementCore.IsActive(At(SurfacePlacement.Floating)));

    [Fact]
    public void Inactive_WhenTurnedOff()   // old: VideoActive_False_WhenNotPreferred
        => Assert.False(PlacementCore.IsActive(Off()));

    [Fact]
    public void Inactive_WhenContentHasNoVideo()   // old: VideoActive_False_WhenNoVideo → availability, not a flag
        => Assert.False(PlacementCore.IsActive(At(SurfacePlacement.Floating, PlacementSet.None)));

    [Fact]
    public void StaysOn_AcrossTrackChanges_WhileTheIntentIsOn()
    {
        // The other half of stickiness: an ON intent still carries video from track to track (that is the whole point of
        // it being an intent and not a per-surface flag).
        var watching = At(SurfacePlacement.Floating);
        Assert.True(PlacementCore.IsActive(NextTrack(watching)));
        // …and an audio-only track in between does not clear it.
        Assert.True(PlacementCore.IsActive(NextTrack(NextTrack(watching, PlacementSet.None))));
    }

    // ── Docked is the default (this plan) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Docked_IsTheDefaultForAFreshProfile()
        => Assert.Equal(SurfacePlacement.Docked, PlacementState.Initial(Policy).Preferred);

    [Fact]
    public void StoredFloating_SurvivesTheDefaultChange()
        // A returning user with video.placement = "floating" keeps the mini player — only a fresh ("") profile picks
        // up the new Docked default (LoadPlacement reads the stored name; B2).
        => Assert.Equal(SurfacePlacement.Floating, PlacementPersistence.LoadPlacement("floating", Policy));

    // ── the click spec ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PrimaryClick_WhenUnlit_OpensAtPreferred_WhichDefaultsToDocked()
    {
        var s = PlacementCore.TogglePrimary(Off());
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.Resolve(s));   // least-committing VISIBLE placement, not a new OS window
    }

    [Theory]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.Detached)]
    public void PrimaryClick_WhenLit_TurnsOff_FromAnyPlacement(SurfacePlacement from)
    {
        var s = PlacementCore.TogglePrimary(At(from));
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);
    }

    [Fact]
    public void PrimaryClick_IsSymmetric_OffOnOffOn()
    {
        var s = Off();
        for (int i = 0; i < 4; i++)
        {
            s = PlacementCore.TogglePrimary(s);
            Assert.Equal(i % 2 == 0, PlacementCore.IsActive(s));
        }
    }

    [Fact]
    public void OpenAt_MakesTheTargetTheNewPreferredHome()
    {
        var s = PlacementCore.OpenAt(Off(), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, s.Preferred);

        // …so turning it off and clicking the primary again returns to the placement the user last chose.
        var reopened = PlacementCore.TogglePrimary(PlacementCore.TurnOff(s));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(reopened));
    }

    [Fact]
    public void TurnOff_KeepsPreferred_SoTheNextOpenGoesHome()
    {
        var s = PlacementCore.TurnOff(At(SurfacePlacement.Detached));
        Assert.Equal(SurfacePlacement.Detached, s.Preferred);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(s));
    }

    /// <summary>Regression (2026-07-27, "I closed the video and the surface stayed on screen, paused, buffering
    /// forever"): a close that lands while a surface still CLAIMS to be mounted — a load in flight, a pop-out whose OS
    /// window has not torn down yet — must still resolve to None. Reality (<c>Live</c>) is a report, never an input to
    /// the decision; if it could hold the surface open, the ✕ would be a suggestion.</summary>
    [Fact]
    public void TurnOff_UnmountsEvenWhileASurfaceStillClaimsToBeLive()
    {
        foreach (var claimed in new[] { SurfacePlacement.Floating, SurfacePlacement.Detached })
        {
            var open = PlacementCore.WithLive(At(claimed), claimed);           // mid-load: the surface reported itself live
            Assert.Equal(claimed, PlacementCore.Resolve(open));

            var off = PlacementCore.TurnOff(open);
            Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(off));   // …and the ✕ still wins
            Assert.False(PlacementCore.IsActive(off));
            Assert.True(PlacementCore.Invariant(off));

            // The same for the surface reporting its own close: an IN-APP close is off, not a placement shuffle.
            var closed = PlacementCore.HostClosed(PlacementCore.WithLive(At(SurfacePlacement.Floating), SurfacePlacement.Floating),
                SurfacePlacement.Floating);
            Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(closed));
        }
    }

    [Fact]
    public void OpenAt_TurnsVideoBackOn_AfterAClose()   // the explicit re-enable: the picker / the attach-and-reveal flow
    {
        var closed = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(closed));

        var shown = PlacementCore.OpenAt(closed, SurfacePlacement.Floating);
        Assert.True(PlacementCore.IsActive(shown));
        Assert.True(PlacementCore.IsActive(NextTrack(shown)));   // …and it is sticky-ON again from there
    }

    [Fact]
    public void MovingPlacement_KeepsItActive_AndDoesNotStack()
    {
        var s = PlacementCore.OpenAt(At(SurfacePlacement.Floating), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(s));   // exactly one placement — an enum, not flags
    }

    // ── closing is STICKY OFF (the surface's own ✕ — product rule changed 2026-07-26) ─────────────────────────────────

    [Fact]
    public void ClosingTheSurface_TurnsVideoOff_NotHiddenForThisSong()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);       // OFF — there is no "hidden but still on" state left
        Assert.Equal(SurfacePlacement.Floating, s.Preferred);   // …but where they like to watch is remembered
    }

    [Fact]
    public void ClosingTheSurface_KeepsEveryLaterTrackOnAudio_UntilTheUserTurnsVideoBackOn()
    {
        // THE reported bug: close the video on track A, then track B (which has a video) auto-opened it again.
        var closed = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);

        var trackB = NextTrack(closed);                          // B has a video…
        Assert.False(PlacementCore.IsActive(trackB));            // …and still does not open
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(trackB));
        // The playback-side predicate is the same rule, so B starts as AUDIO rather than as a video with a hidden surface.
        Assert.Equal(SurfacePlacement.None, PlacementCore.ResolveWith(trackB, All));

        var trackC = NextTrack(trackB);                          // and it does not wear off after one more track either
        Assert.False(PlacementCore.IsActive(trackC));

        // Only an explicit re-enable brings it back (player-bar primary / placement picker).
        Assert.True(PlacementCore.IsActive(PlacementCore.TogglePrimary(trackC)));
    }

    [Fact]
    public void ClosingTheSurface_MidTrack_RoutesTheCurrentTrackBackToAudioToo()
    {
        // The kind edge the bridge watches is IsActive true→false; asserting it here is what guarantees CommitVideoSurface
        // fires RequestMediaKindRefresh, i.e. the song already playing swaps back from video to audio.
        var watching = At(SurfacePlacement.Floating);
        Assert.True(PlacementCore.IsActive(watching));

        var closed = PlacementCore.HostClosed(watching, SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(closed));                              // the active edge the bridge acts on
        Assert.Equal(SurfacePlacement.None, PlacementCore.ResolveWith(closed, All));   // ShouldPlayAsVideo(currentTrack) = false
    }

    /// <summary>B9 — the docked card's own ✕ is exactly the same sticky-off rule as the mini player's, because
    /// <see cref="PlacementCore.HostClosed"/> only special-cases <see cref="SurfacePlacement.Detached"/>: closing
    /// Docked in-app is <see cref="PlacementCore.TurnOff"/>, not a demote to Floating. Critically, a LATER
    /// availability recompute (the rail fits again; the next track has video) must not revive it — only an explicit
    /// re-enable does.</summary>
    [Fact]
    public void DockedClose_IsStickyOff()
    {
        var docked = At(SurfacePlacement.Docked);
        var closed = PlacementCore.HostClosed(docked, SurfacePlacement.Docked);
        Assert.False(PlacementCore.IsActive(closed));
        Assert.Equal(SurfacePlacement.None, closed.Requested);
        Assert.Equal(SurfacePlacement.Docked, closed.Preferred);   // remembered, but not resurrected

        // A later WithAvailability recompute does not revive it…
        var recomputed = PlacementCore.WithAvailability(closed, All);
        Assert.False(PlacementCore.IsActive(recomputed));
        Assert.Equal(SurfacePlacement.None, recomputed.Requested);

        // …and neither does a track change (even one with video).
        Assert.False(PlacementCore.IsActive(NextTrack(recomputed)));
    }

    // ── availability (content ∧ host caps) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LosingAvailability_HidesTheSurface_ButPreservesIntent_AndSnapsBack()
    {
        // An audio-only track makes every placement unavailable; the surface goes away but the intent is remembered, so
        // the next track that HAS a video returns to exactly where the user had it (rather than to a fallback).
        var watching = At(SurfacePlacement.Detached);
        var audioOnly = PlacementCore.WithAvailability(watching, PlacementSet.None);
        Assert.False(PlacementCore.IsActive(audioOnly));
        Assert.Equal(SurfacePlacement.Detached, audioOnly.Requested);

        var videoAgain = PlacementCore.WithAvailability(audioOnly, All);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(videoAgain));
    }

    [Fact]
    public void UnavailablePlacement_FallsDownTheCommitmentLadder_WithoutRewritingIntent()
    {
        // The host cannot open a second window (no detached), so a detached request resolves to the mini player — and
        // when a second window becomes possible again it snaps back, because Requested was never overwritten.
        var s = PlacementCore.WithAvailability(At(SurfacePlacement.Detached), PlacementSet.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
        Assert.Equal(SurfacePlacement.Detached, s.Requested);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(PlacementCore.WithAvailability(s, All)));
    }

    /// <summary>B4/B5 — the Docked-specific round trip: narrowing the window below the fit threshold drops the
    /// Docked bit and the video becomes the PiP WITHOUT rewriting intent (free — no new code); widening it again
    /// re-docks automatically because <see cref="PlacementState.Requested"/> was never overwritten.</summary>
    [Fact]
    public void NarrowWindow_DropsDockedBit_ResolvesFloating_PreferredIntact()
    {
        var docked = At(SurfacePlacement.Docked);   // requested = preferred = Docked, everything available
        var narrowed = PlacementCore.WithAvailability(docked,
            PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen);   // the rail no longer fits
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(narrowed));   // B4: becomes the PiP…
        Assert.Equal(SurfacePlacement.Docked, narrowed.Requested);                 // …intent untouched…
        Assert.Equal(SurfacePlacement.Docked, narrowed.Preferred);

        var widened = PlacementCore.WithAvailability(narrowed, All);
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.Resolve(widened));      // B5: …and it snaps back
    }

    [Fact]
    public void ResolveWith_AnswersPerContent_WithoutMutatingState()
    {
        var s = At(SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.ResolveWith(s, All));
        Assert.Equal(SurfacePlacement.None, PlacementCore.ResolveWith(s, PlacementSet.None));   // that track has no video
        Assert.Equal(All, s.Available);                                                          // untouched
    }

    // ── host close ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClosingTheDetachedWindow_FallsBackToTheMiniPlayer_AndAdoptsItAsPreferred()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
        Assert.Equal(SurfacePlacement.Floating, s.Preferred);   // "I don't want a separate window" is remembered
    }

    [Fact]
    public void ClosingTheDetachedWindow_TurnsOff_WhenNothingLessCommittingIsAvailable()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached, PlacementSet.Detached), SurfacePlacement.Detached);
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);
    }

    [Fact]
    public void ClosingTheMiniPlayerAfterTheDetachedFallback_TurnsVideoOff()
    {
        // The two close rules compose: closing the pop-out hands video to the mini player (still watching, just not in a
        // separate window); closing THAT is the "I closed the video" gesture and turns it off for good.
        var fellBack = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(fellBack));

        var off = PlacementCore.HostClosed(fellBack, SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.None, off.Requested);
        Assert.False(PlacementCore.IsActive(NextTrack(off)));
    }

    [Fact]
    public void AStaleClose_IsIgnored()
    {
        // The detached window died, but by the time its close arrived the user had already moved to the mini player.
        // Acting on it would clobber the newer placement; it must be inert.
        var moved = PlacementCore.OpenAt(At(SurfacePlacement.Detached), SurfacePlacement.Floating);
        var after = PlacementCore.HostClosed(moved, SurfacePlacement.Detached);
        Assert.Equal(moved, after);
    }

    // ── Demote — the rail-close AMBIENT move (§3.2): Preferred survives, unlike OpenAt/TurnOff ─────────────────────────

    [Fact]
    public void Demote_KeepsPreferred()
    {
        var docked = At(SurfacePlacement.Docked);   // everything available
        var demoted = PlacementCore.Demote(docked, SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, demoted.Requested);
        Assert.Equal(SurfacePlacement.Docked, demoted.Preferred);   // …so re-opening the rail re-docks (ReDockOnRailOpen)
        Assert.True(PlacementCore.Invariant(demoted));
    }

    [Fact]
    public void Demote_WithNothingAvailable_TurnsOff()
    {
        var docked = At(SurfacePlacement.Docked, PlacementSet.None);   // nothing is available at all
        var demoted = PlacementCore.Demote(docked, SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.None, demoted.Requested);
        Assert.Equal(SurfacePlacement.Docked, demoted.Preferred);   // still remembered
        Assert.False(PlacementCore.IsActive(demoted));
        Assert.True(PlacementCore.Invariant(demoted));
    }

    [Fact]
    public void Demote_IsInert_WhenAlreadyOff()
        => Assert.Equal(Off(), PlacementCore.Demote(Off(), SurfacePlacement.Floating));

    // ── fullscreen (reserved surface; the rules are live) ────────────────────────────────────────────────────────────

    [Fact]
    public void Fullscreen_RemembersWhereToGoBack_AndIsNeverThePreferredHome()
    {
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Detached));
        Assert.Equal(SurfacePlacement.Fullscreen, fs.Requested);
        Assert.Equal(SurfacePlacement.Detached, fs.ReturnTo);
        Assert.Equal(SurfacePlacement.Detached, fs.Preferred);   // NOT Fullscreen — it must not survive a restart

        var back = PlacementCore.ExitFullscreen(fs);
        Assert.Equal(SurfacePlacement.Detached, back.Requested);
        Assert.Equal(SurfacePlacement.None, back.ReturnTo);
    }

    [Fact]
    public void Fullscreen_EnteredFromOff_ExitsToThePreferredHome()
    {
        // Entering fullscreen from OFF leaves nothing to return TO (ReturnTo captures the previous Requested, which was
        // None), so the exit falls back to Preferred — the surface's own default home. Asserted as Policy.Default, not a
        // literal: this test is about "exits to the preferred home", and hard-coding the placement is what made it fail
        // when the video surface's default moved from Floating to Docked.
        var back = PlacementCore.ExitFullscreen(PlacementCore.EnterFullscreen(Off()));
        Assert.Equal(Policy.Default, back.Requested);
    }

    [Fact]
    public void ExitFullscreen_IsANoOp_WhenNotInFullscreen()
    {
        var s = At(SurfacePlacement.Floating);
        Assert.Equal(s, PlacementCore.ExitFullscreen(s));
    }

    [Fact]
    public void Fullscreen_WhenUnavailable_FallsToTheOnlyAvailablePlacement()
    {
        // Only Detached is available (no Docked, no Floating, no Fullscreen hook) — Fullscreen still resolves
        // somewhere real. (Was "…ResolvesDownTheLadder" and asserted the pre-fix walk-from-above-the-ladder behavior;
        // fixed alongside Fullscreen_WhenUnavailable_NeverEscalatesToDetached, §3.3.)
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Detached, PlacementSet.Detached));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(fs));
    }

    /// <summary>§3.3 — the regression this plan fixes: <c>LadderIndex(Fullscreen)</c> used to start the fallback walk
    /// ABOVE the ladder, so an unavailable Fullscreen would walk DOWN into Detached first — spawning a whole OS
    /// window nobody asked for — even when a cheaper rung (Docked/Floating) was sitting right there available. The
    /// fix walks from the CHEAPEST rung instead.</summary>
    [Fact]
    public void Fullscreen_WhenUnavailable_NeverEscalatesToDetached()
    {
        // Docked, Floating AND Detached are all available, Fullscreen alone is not — the cheapest rung must win, not
        // the most committing one.
        var avail = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached;
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.FirstAvailable(SurfacePlacement.Fullscreen, avail));

        // Without Docked in the mix, Floating (still cheaper than Detached) wins.
        Assert.Equal(SurfacePlacement.Floating,
            PlacementCore.FirstAvailable(SurfacePlacement.Fullscreen, PlacementSet.Floating | PlacementSet.Detached));

        // Only Detached is left — that, and only that, is where it falls.
        Assert.Equal(SurfacePlacement.Detached,
            PlacementCore.FirstAvailable(SurfacePlacement.Fullscreen, PlacementSet.Detached));
    }

    /// <summary>B16/B17 — entering fullscreen FROM Docked remembers Docked as <see cref="PlacementState.ReturnTo"/>
    /// (not Floating/Detached, and never Fullscreen itself as <see cref="PlacementState.Preferred"/>), and exiting
    /// restores exactly that.</summary>
    [Fact]
    public void Fullscreen_CapturesAndRestoresReturnTo_FromDocked()
    {
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Docked));
        Assert.Equal(SurfacePlacement.Docked, fs.ReturnTo);
        Assert.Equal(SurfacePlacement.Fullscreen, fs.Requested);
        Assert.Equal(SurfacePlacement.Docked, fs.Preferred);   // NOT Fullscreen — must not survive a restart

        var back = PlacementCore.ExitFullscreen(fs);
        Assert.Equal(SurfacePlacement.Docked, back.Requested);
        Assert.Equal(SurfacePlacement.None, back.ReturnTo);
    }

    /// <summary>Pins the existing behaviour explicitly under its plan-table name — <see cref="OffAndFullscreen_AreNeverPersisted"/>
    /// already covers this as a theory case; this is the named regression the plan calls out on its own.</summary>
    [Fact]
    public void Fullscreen_IsNeverPersisted()
        => Assert.Equal("", PlacementPersistence.SavePlacement(SurfacePlacement.Fullscreen));

    // ── owner mount decisions (parity port: the old DecideDetached cases) ────────────────────────────────────────────

    [Fact]
    public void Owner_Opens_WhenItsPlacementIsResolvedAndNothingIsAlive()
        => Assert.Equal(MountAction.Open,
            PlacementCore.DecideOwned(SurfacePlacement.Detached, SurfacePlacement.Detached, alive: false));

    [Fact]
    public void Owner_DoesNothing_WhenAlreadyMatching()
        => Assert.Equal(MountAction.None,
            PlacementCore.DecideOwned(SurfacePlacement.Detached, SurfacePlacement.Detached, alive: true));

    [Theory]
    [InlineData(SurfacePlacement.None)]        // turned off
    [InlineData(SurfacePlacement.Floating)]    // a DIFFERENT placement won
    public void Owner_Closes_WhenItsPlacementIsNoLongerResolved(SurfacePlacement resolved)
        => Assert.Equal(MountAction.Close,
            PlacementCore.DecideOwned(resolved, SurfacePlacement.Detached, alive: true));

    [Theory]
    [InlineData(SurfacePlacement.None)]
    [InlineData(SurfacePlacement.Floating)]
    public void Owner_DoesNothing_WhenNotResolvedAndNotAlive(SurfacePlacement resolved)
        => Assert.Equal(MountAction.None,
            PlacementCore.DecideOwned(resolved, SurfacePlacement.Detached, alive: false));

    [Fact]
    public void DecideMount_Move_WhenMountedInTheWrongPlacement()
        => Assert.Equal(MountAction.Move,
            PlacementCore.DecideMount(SurfacePlacement.Detached, SurfacePlacement.Floating));

    // ── reality reporting is scoped per surface (two independent surfaces must not clobber each other) ───────────────

    [Fact]
    public void MountedSurface_ClaimsReality()
        => Assert.Equal(SurfacePlacement.Detached,
            PlacementCore.LiveAfterReport(SurfacePlacement.None, SurfacePlacement.Detached, mounted: true));

    [Fact]
    public void UnmountedSurface_ReleasesOnlyItsOwnClaim()
        => Assert.Equal(SurfacePlacement.None,
            PlacementCore.LiveAfterReport(SurfacePlacement.Detached, SurfacePlacement.Detached, mounted: false));

    [Fact]
    public void UnmountedSurface_NeverErasesAnotherSurfacesClaim()
    {
        // Both surfaces watch the same state, so the mini player reports "not mounted" on the very same change that the
        // pop-out reports "mounted". Unscoped, that report would erase the pop-out's claim and reality would read None
        // while a window was plainly open.
        var live = PlacementCore.LiveAfterReport(SurfacePlacement.None, SurfacePlacement.Detached, mounted: true);
        live = PlacementCore.LiveAfterReport(live, SurfacePlacement.Floating, mounted: false);
        Assert.Equal(SurfacePlacement.Detached, live);
    }

    [Fact]
    public void RealityReporting_ConvergesRegardlessOfSurfaceOrder()
    {
        // A hand-off (floating → detached): whichever surface reports first, reality settles on the one that is mounted.
        var a = PlacementCore.LiveAfterReport(SurfacePlacement.Floating, SurfacePlacement.Floating, mounted: false);
        a = PlacementCore.LiveAfterReport(a, SurfacePlacement.Detached, mounted: true);

        var b = PlacementCore.LiveAfterReport(SurfacePlacement.Floating, SurfacePlacement.Detached, mounted: true);
        b = PlacementCore.LiveAfterReport(b, SurfacePlacement.Floating, mounted: false);

        Assert.Equal(SurfacePlacement.Detached, a);
        Assert.Equal(SurfacePlacement.Detached, b);
    }

    // ── the async-resolve fence (parity port: ShouldPublishResolve) ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_Publishes_WhenTheCapturedGenerationIsStillCurrent()
        => Assert.True(PlacementCore.IsCurrentGeneration(capturedGen: 3, currentGen: 3));

    [Fact]
    public void Resolve_IsDropped_WhenSuperseded()
        => Assert.False(PlacementCore.IsCurrentGeneration(capturedGen: 3, currentGen: 4));

    // ── named regressions (one per historical bug — these must never come back) ──────────────────────────────────────

    /// <summary>Bug 3: closing the pop-out left the player-bar toggle lit with no surface behind it, because "close" had
    /// no transition at all and the intent flag stayed true.</summary>
    [Fact]
    public void Regression_StuckToggle_ClosingThePopOutLandsInTheMiniPlayerInsteadOfLitNothing()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.True(PlacementCore.IsActive(s));                              // still watching…
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));   // …in a surface that actually exists
    }

    /// <summary>Bug 4: a late video resolve for the PREVIOUS track republished itself over the current one ("changing
    /// track and clicking video again opens the same old video").</summary>
    [Fact]
    public void Regression_StaleVideo_ASupersededResolveNeverPublishes()
    {
        long gen = 7;
        long captured = gen;
        gen++;                                    // the track changed while the resolve was in flight
        Assert.False(PlacementCore.IsCurrentGeneration(captured, gen));
    }

    /// <summary>Bug 5: placement lived in three owners at once (bridge signals, a view-local window handle, the engine's
    /// host table), so they could disagree. One enum makes "mounted in two places" unrepresentable.</summary>
    [Fact]
    public void Regression_PlacementSplit_AtMostOnePlacementIsEverResolved()
    {
        var s = At(SurfacePlacement.Floating);
        foreach (var move in new[] { SurfacePlacement.Detached, SurfacePlacement.Floating, SurfacePlacement.Detached })
        {
            s = PlacementCore.OpenAt(s, move);
            int mounted = 0;
            foreach (var p in new[] { SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Detached, SurfacePlacement.Fullscreen })
                if (PlacementCore.Resolve(s) == p) mounted++;
            Assert.Equal(1, mounted);
        }
    }

    /// <summary>The UX complaint: the primary click spawned an always-on-top OS window — the MOST committing placement —
    /// as its first response. It must open the lowest-commitment surface instead.</summary>
    [Fact]
    public void Regression_FirstClick_OpensDocked_NotAnAlwaysOnTopWindow()
    {
        var s = PlacementCore.TogglePrimary(PlacementState.Initial(Policy) with { Available = All });
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.Resolve(s));
        Assert.NotEqual(SurfacePlacement.Detached, PlacementCore.Resolve(s));
    }

    /// <summary>Bug 6 (2026-07-26): closing the video came back on the next song that had one. The ✕ was a CONTENT-SCOPED
    /// dismiss that expired by itself at the track boundary while the sticky intent stayed on, so "I closed it" and
    /// "show it again in 3 minutes" were the same state. Closing is now plain off, and the dismiss machinery is deleted
    /// rather than merely bypassed — there is no state left that means "off, but it will come back".</summary>
    [Fact]
    public void Regression_ClosedVideoReopenedOnTheNextTrack()
    {
        var closed = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        for (int track = 0; track < 5; track++)
        {
            closed = NextTrack(closed, track % 2 == 0 ? All : PlacementSet.None);
            Assert.False(PlacementCore.IsActive(closed));
        }
    }

    // ── persistence: "persist where you like to work; never persist whether it is running" ──────────────────────────

    [Theory]
    [InlineData(SurfacePlacement.Docked)]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.Detached)]
    public void PreferredPlacement_RoundTrips(SurfacePlacement p)
        => Assert.Equal(p, PlacementPersistence.LoadPlacement(PlacementPersistence.SavePlacement(p), Policy));

    [Theory]
    [InlineData(SurfacePlacement.None)]          // "off" — restoring it would mean nothing
    [InlineData(SurfacePlacement.Fullscreen)]    // a MODE — restoring it would trap the user in it on next launch
    public void OffAndFullscreen_AreNeverPersisted(SurfacePlacement p)
    {
        Assert.Equal("", PlacementPersistence.SavePlacement(p));
        Assert.Equal(Policy.Default, PlacementPersistence.LoadPlacement(PlacementPersistence.SavePlacement(p), Policy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("fullscreen")]   // a real word, but a MODE — no LoadPlacement arm, and never persisted anyway
    public void UnusablePreference_FallsBackToTheSurfaceDefault(string? raw)
        => Assert.Equal(Policy.Default, PlacementPersistence.LoadPlacement(raw, Policy));

    [Fact]
    public void StoredPreference_IsANameNotAnEnumNumber()
    {
        // Numeric values encode the commitment ladder; persisting them would silently reinterpret saved preferences if
        // the ladder is ever reordered.
        Assert.Equal("detached", PlacementPersistence.SavePlacement(SurfacePlacement.Detached));
        Assert.Equal("floating", PlacementPersistence.SavePlacement(SurfacePlacement.Floating));
    }

    [Fact]
    public void Geometry_RoundTrips_AndRoundsToWholeUnits()
    {
        Assert.True(PlacementPersistence.TryLoadRect(PlacementPersistence.SaveRect(1720.4f, 880.6f, 360f, 202f),
            out float x, out float y, out float w, out float h));
        Assert.Equal(1720f, x);
        Assert.Equal(881f, y);
        Assert.Equal(360f, w);
        Assert.Equal(202f, h);
    }

    [Fact]
    public void Geometry_NegativePositionsSurvive()   // a window on a monitor left of the primary has a negative X
    {
        Assert.True(PlacementPersistence.TryLoadRect(PlacementPersistence.SaveRect(-1920f, -140f, 480f, 270f),
            out float x, out float y, out float w, out float h));
        Assert.Equal(-1920f, x);
        Assert.Equal(-140f, y);
        Assert.Equal(480f, w);
        Assert.Equal(270f, h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]           // truncated
    [InlineData("1,2,3,4,5")]       // over-long
    [InlineData("a,b,c,d")]         // non-numeric
    [InlineData("10,10,0,0")]       // degenerate — would open a 0x0 window
    [InlineData("10,10,-5,-5")]     // negative size
    public void MalformedGeometry_IsRejected(string? raw)
        => Assert.False(PlacementPersistence.TryLoadRect(raw, out _, out _, out _, out _));

    [Fact]
    public void DegenerateGeometry_IsNotEvenWritten()
        => Assert.Equal("", PlacementPersistence.SaveRect(10f, 10f, 0f, 0f));

    // ── the deferred upgrade: an association landing MID-TRACK never swaps the media (VideoUpgradeGate) ──────────────
    // The bridge folds these two: RecomputeHasVideo always writes the badge, then asks DeferUpgrade whether it may also
    // commit the surface. A commit is a media-kind edge, and a media-kind edge reloads the playing track from 0.

    /// <summary>THE fix: the user is watching (standing intent ON), the track started as audio because nothing knew it
    /// had a video, and the association lands seconds later. The badge lights; the surface — and therefore playback —
    /// must not move.</summary>
    [Fact]
    public void AssociationLandingMidTrack_IsDeferred_SoThePlayingTrackIsNeverReloaded()
    {
        var playingAsAudio = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);
        Assert.False(PlacementCore.IsActive(playingAsAudio));

        var target = VideoUpgradeGate.FoldAvailability(playingAsAudio, hasVideo: true, hostCapable: All);

        Assert.True(PlacementCore.IsActive(target));                                             // it WOULD turn on…
        Assert.True(VideoUpgradeGate.DeferUpgrade(playingAsAudio, target, commitUpgrade: false)); // …and is withheld
    }

    /// <summary>The track BOUNDARY is the entitled caller (PushState's already-computed uri-changed bool), as is an
    /// explicit user action (the override paths) — both pass commitUpgrade: true and the upgrade lands.</summary>
    [Fact]
    public void ATrackBoundary_OrAnExplicitUserAction_CommitsTheUpgrade()
    {
        var playingAsAudio = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);
        var target = VideoUpgradeGate.FoldAvailability(playingAsAudio, hasVideo: true, hostCapable: All);

        Assert.False(VideoUpgradeGate.DeferUpgrade(playingAsAudio, target, commitUpgrade: true));
    }

    /// <summary>DOWNGRADES are never deferred: a video-less track, the proven-dead latch and the ✕ must unmount the
    /// surface (and route the media back to audio) the instant they happen.</summary>
    [Fact]
    public void ADowngrade_AlwaysCommits_EvenOnTheDeferredPath()
    {
        var watching = At(SurfacePlacement.Floating);
        var target = VideoUpgradeGate.FoldAvailability(watching, hasVideo: false, hostCapable: All);

        Assert.False(PlacementCore.IsActive(target));
        Assert.False(VideoUpgradeGate.DeferUpgrade(watching, target, commitUpgrade: false));
    }

    [Fact]
    public void NothingIsDeferred_WhenTheUserHasVideoTurnedOff()
    {
        // No intent ⇒ the fold resolves to None ⇒ there is no upgrade to withhold (and committing it is inert).
        var off = PlacementCore.WithAvailability(Off(), PlacementSet.None);
        var target = VideoUpgradeGate.FoldAvailability(off, hasVideo: true, hostCapable: All);

        Assert.False(PlacementCore.IsActive(target));
        Assert.False(VideoUpgradeGate.DeferUpgrade(off, target, commitUpgrade: false));
    }

    /// <summary>The companion the deferral REQUIRES: a deferred upgrade leaves Available stale at None, and both Resolve
    /// and IsActive consult it — so the click behind the freshly-lit badge has to re-fold this track's availability, or
    /// it would resolve to None and do nothing at all.</summary>
    [Fact]
    public void ClickingAfterADeferredLand_StartsTheVideo_BecauseTheIntentPathRefoldsAvailability()
    {
        var stale = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);   // badge lit, surface untouched

        Assert.False(PlacementCore.IsActive(PlacementCore.TogglePrimary(stale)));                      // the bug, if the fold is dropped
        Assert.True(PlacementCore.IsActive(VideoUpgradeGate.PrimaryClick(stale, hasVideo: true, hostCapable: All)));
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(VideoUpgradeGate.PrimaryClick(stale, hasVideo: true, hostCapable: All)));
    }

    /// <summary>…and it must take ONE click, not two: the standing Requested intent is still ON under a deferred upgrade,
    /// so a naive TogglePrimary over the folded state would read "already watching" and turn video OFF.</summary>
    [Fact]
    public void ClickingAfterADeferredLand_NeverReadsTheStandingIntentAsAlreadyWatching()
    {
        var stale = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);

        Assert.False(PlacementCore.IsActive(PlacementCore.TogglePrimary(VideoUpgradeGate.FoldAvailability(stale, true, All))));   // the two-click bug
        Assert.True(PlacementCore.IsActive(VideoUpgradeGate.PrimaryClick(stale, hasVideo: true, hostCapable: All)));
    }

    [Fact]
    public void PrimaryClick_StillTogglesNormally_WhenNothingWasDeferred()
    {
        var watching = At(SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(VideoUpgradeGate.PrimaryClick(watching, hasVideo: true, hostCapable: All)));   // lit → off

        var off = PlacementCore.TurnOff(watching);
        Assert.True(PlacementCore.IsActive(VideoUpgradeGate.PrimaryClick(off, hasVideo: true, hostCapable: All)));         // unlit → on

        // …and a track with no video at all cannot be turned on by any number of clicks.
        Assert.False(PlacementCore.IsActive(VideoUpgradeGate.PrimaryClick(off, hasVideo: false, hostCapable: All)));
    }

    [Fact]
    public void ShowVideoAt_AfterADeferredLand_AlsoRefolds()
    {
        var stale = PlacementCore.WithAvailability(Off(), PlacementSet.None);
        var opened = PlacementCore.OpenAt(VideoUpgradeGate.FoldAvailability(stale, hasVideo: true, hostCapable: All), SurfacePlacement.Detached);

        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(opened));
    }

    [Fact]
    public void AvailabilityFor_IsTheOneContentChannel()
    {
        Assert.Equal(PlacementPolicy.Video.Allowed, VideoUpgradeGate.AvailabilityFor(true, All));
        Assert.Equal(PlacementSet.None, VideoUpgradeGate.AvailabilityFor(false, All));
    }

    /// <summary>§3.4 — availability is content ∧ HOST capability, not all-or-nothing: a track with a video is further
    /// masked by what the host can actually do right now (can the rail fit it, can a second window/swapchain open,
    /// does the fullscreen hook exist).</summary>
    [Fact]
    public void AvailabilityFor_MasksByHostCapability()
    {
        // Host can only do Docked + Floating (no second window, no fullscreen hook) — Detached/Fullscreen drop out
        // even though the policy allows them and the track has video.
        var hostCapable = PlacementSet.Docked | PlacementSet.Floating;
        Assert.Equal(hostCapable, VideoUpgradeGate.AvailabilityFor(true, hostCapable));

        // No video at all → None, regardless of host capability.
        Assert.Equal(PlacementSet.None, VideoUpgradeGate.AvailabilityFor(hasVideo: false, hostCapable: All));

        // Fully capable host → the whole policy, unmasked.
        Assert.Equal(PlacementPolicy.Video.Allowed, VideoUpgradeGate.AvailabilityFor(hasVideo: true, hostCapable: All));
    }

    // ── property tests ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Property 1: the invariants hold after EVERY command in EVERY order — a resolved placement is always
    /// actually available, Preferred is always a real home, and nothing resolves while off. Deterministic pseudo-random
    /// sequences (fixed seed) so a failure always reproduces.</summary>
    [Fact]
    public void Property_InvariantsHoldForArbitraryCommandSequences()
    {
        var placements = new[]
        {
            SurfacePlacement.None, SurfacePlacement.Docked, SurfacePlacement.Floating,
            SurfacePlacement.Detached, SurfacePlacement.Fullscreen,
        };
        var sets = new[]
        {
            PlacementSet.None, PlacementSet.Floating, PlacementSet.Detached, All,
            PlacementSet.Fullscreen, PlacementSet.Docked | PlacementSet.Floating,
        };
        var kinds = (PlacementCommandKind[])Enum.GetValues(typeof(PlacementCommandKind));

        uint rng = 0x5EED_1234;
        uint Next() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }

        for (int seq = 0; seq < 2000; seq++)
        {
            var s = PlacementState.Initial(Policy);
            var trail = new List<PlacementCommand>(24);
            for (int step = 0; step < 24; step++)
            {
                var cmd = new PlacementCommand(
                    kinds[Next() % (uint)kinds.Length],
                    placements[Next() % (uint)placements.Length],
                    sets[Next() % (uint)sets.Length]);
                trail.Add(cmd);
                s = PlacementCore.Apply(s, cmd);
                Assert.True(PlacementCore.Invariant(s),
                    $"invariant broken after {string.Join(" → ", trail)}; state = {s}");
            }
        }
    }

    /// <summary>Property 2: the primary affordance is total and symmetric — from ANY reachable state, one click makes an
    /// active surface inactive, and makes an inactive surface active whenever any placement is available at all. That is
    /// the whole "the toggle can never get stuck" guarantee, checked exhaustively rather than by example.</summary>
    [Fact]
    public void Property_PrimaryToggleIsTotalAndSymmetric()
    {
        var placements = new[] { SurfacePlacement.None, SurfacePlacement.Floating, SurfacePlacement.Detached, SurfacePlacement.Fullscreen };
        var sets = new[] { PlacementSet.None, PlacementSet.Floating, PlacementSet.Detached, All };

        foreach (var requested in placements)
        foreach (var preferred in new[] { SurfacePlacement.Floating, SurfacePlacement.Detached })
        foreach (var available in sets)
        {
            var s = new PlacementState(requested, preferred, SurfacePlacement.None, SurfacePlacement.None, available);
            bool wasActive = PlacementCore.IsActive(s);
            var next = PlacementCore.TogglePrimary(s);

            if (wasActive)
                Assert.False(PlacementCore.IsActive(next));   // lit → always off
            else if (PlacementCore.FirstAvailable(preferred, available) != SurfacePlacement.None)
                Assert.True(PlacementCore.IsActive(next));    // unlit + the home resolves somewhere → always on
            Assert.True(PlacementCore.Invariant(next));
        }
    }

    // ── gate.media.single-transport ─────────────────────────────────────────────────────────────────────────────────
    // The stacked-double-bar defect: Wavee's fullscreen surface reserved a band so the global 72-DIP PlayerBar stayed
    // visible UNDER the video's own transport, and nothing suppressed either — two live scrub rows for one session in
    // one window. The structural fix is that transport ownership is DERIVED from the resolved placement, so "both are
    // visible" is unrepresentable rather than merely fixed. These pin that derivation.

    /// <summary>Exactly ONE <see cref="TransportOwner"/> claims the transport for every placement value. Two
    /// independent visibility flags is precisely what let two owners both render.</summary>
    [Fact]
    public void SingleTransport_ExactlyOneOwnerPerPlacement()
    {
        Assert.True(PlacementCore.SingleTransportInvariant());
        foreach (var p in PlacementCore.AllPlacements)
            Assert.Equal(1, PlacementCore.TransportClaimants(p));
    }

    /// <summary>The named regression: in fullscreen the transport belongs to the video surface, NOT the global bar, so
    /// the bar unmounts its scrub row and play/pause instead of stacking a second one under the video's.</summary>
    [Fact]
    public void SingleTransport_FullscreenTakesItFromTheGlobalBar()
    {
        Assert.Equal(TransportOwner.Fullscreen, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Fullscreen)));
        Assert.False(PlacementCore.OwnsTransport(TransportOwner.GlobalBar, SurfacePlacement.Fullscreen));
    }

    /// <summary>An in-window video card owns its OWN hover chrome — the controls that only make sense over a picture
    /// (aspect, fullscreen, the live DVR rail), auto-hiding inside the card's bounds. The docked rail card and the
    /// floating mini player both declare <see cref="TransportOwner.Docked"/>, so both get it.
    /// <para>This is the change that turned the docked YouTube card from a picture with no controls into a real video
    /// surface: <c>DockedVideoSurface</c> suppresses its transport iff it is NOT the owner, and the owner used to be
    /// <see cref="TransportOwner.GlobalBar"/> for every in-window placement, i.e. always suppressed.</para></summary>
    [Fact]
    public void SingleTransport_InWindowVideoCardsOwnTheirOwnChrome()
    {
        Assert.Equal(TransportOwner.Docked, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Docked)));
        Assert.Equal(TransportOwner.Docked, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Floating)));
    }

    /// <summary>With no video surface mounted there is nothing else to own the transport, so it is the bar's.</summary>
    [Fact]
    public void SingleTransport_WithNoSurfaceItIsTheBars()
        => Assert.Equal(TransportOwner.GlobalBar, PlacementCore.TransportOwnerOf(Off()));

    /// <summary>The docked card's chrome does NOT disarm the global player bar. An overlay inside a card is not a
    /// second bar stacked in the same band — the stacked-double-bar defect this model prevents is the FULLSCREEN one,
    /// where the shell keeps the 72-DIP bar visible under a full-bleed surface's own transport.
    /// <para>Pinned here because it is the one thing a reader of <c>TransportOwnerFor</c> alone would get wrong:
    /// <c>PlayerBar</c> renders for GlobalBar, PopOut AND Docked, and yields only to Fullscreen.</para></summary>
    [Fact]
    public void SingleTransport_ADockedCardDoesNotDisarmTheGlobalBar()
    {
        foreach (var p in new[] { SurfacePlacement.Docked, SurfacePlacement.Floating })
            Assert.NotEqual(TransportOwner.Fullscreen, PlacementCore.TransportOwnerFor(p));
    }

    /// <summary>The pop-out is a SEPARATE OS window, so it carries the transport for its own window while the main
    /// window's bar keeps hers. The invariant is one transport per WINDOW, not per session — stripping the main
    /// window's scrub row because a video plays in another window would be a regression, not a fix.</summary>
    [Fact]
    public void SingleTransport_PopOutDoesNotDisarmTheMainWindowBar()
    {
        Assert.Equal(TransportOwner.PopOut, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Detached)));
        // PlayerBar's cross-window exemption: it renders for GlobalBar and PopOut, and yields only to Fullscreen.
        Assert.NotEqual(TransportOwner.Fullscreen, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Detached)));
    }

    /// <summary>A placement that resolved AWAY (unavailable) hands the transport straight back to the bar — ownership
    /// is derived from the RESOLVED placement, never from what was requested.</summary>
    [Fact]
    public void SingleTransport_DerivesFromResolvedNotRequested()
    {
        var stranded = At(SurfacePlacement.Fullscreen, PlacementSet.None);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(stranded));
        Assert.Equal(TransportOwner.GlobalBar, PlacementCore.TransportOwnerOf(stranded));
    }
}
