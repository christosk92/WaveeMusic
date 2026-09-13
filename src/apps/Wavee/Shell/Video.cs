// ── Shell/Video.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// the nine pure rule classes and the placement state machine (PlacementCore + PlacementState, the home ch 20 and ch
// 24 both ask for)
//
// Role: CORE
// Owner: K
// Wave: 4
// Budget: 900 lines
// Spec: ch 24 §9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. Every DECISION the four video surfaces — the docked cap, the in-window PiP, the pop-out window
// and the app's own fullscreen presentation — obey, as pure functions over plain values. `System`-only by
// construction: no `Signal<T>`, no FluentGpu type, no `Entities` read, no disk. That is what makes the whole surface
// verifiable from `Wavee.Tests` without a GPU, a window, or a Media Foundation session.
//
// A13 (2026-09-12): `Playback/Playback.Video.cs` (owner H, Wave 3) is the DECODE half — it resolves a playable into a
// `VideoSource`, opens one long-lived `MediaPlayer` and publishes `Playback.Video.Player` / `.Source`. It holds no
// placement state and not one `Element`. This file holds no player and touches no MF handle. `Video.UI.cs` (stage 2)
// mounts the surfaces; `Video.Host.cs` owns the pop-out's HWND and the attachment roster.
//
// THE ONE IDEA. There is ONE movable video surface and ONE `PlacementState` value describing it. Everything else —
// which surface mounts, who draws the transport, whether the pop-out is borderless-fullscreen, whether the rail
// yields to a watch page, what a restart restores — is DERIVED from that one value by a function in this file. No
// second visibility flag exists anywhere, which is what makes the shipped defects (a toggle stuck lit over a window
// that no longer exists; two transports stacked in one band; a closed video re-opening on the next song; a pop-out
// that came back fullscreen because a clearing edge was forgotten) UNREPRESENTABLE rather than merely fixed.
//
// Rules: no allocation after warm-up on any path a frame can reach (P8) — the roster builders here are load-time and
// allocate freely, by design, and are the only ones that do; no LINQ, no closures on a hot path, no async, no boxing
// (P9). Ported VERBATIM from 0.2.9 (`App/PlacementCore.cs`, `DockedVideoHosting.cs`, `VideoUpgradeGate.cs`,
// `VideoStageInput.cs`, `DetachedFullscreenRule.cs`, `VideoSurfaceMount.cs`, `VideoAspectPersistence.cs`,
// `VideoOverrideUx.cs`, `VideoOverrideMutationCore.cs`); the decisions are not re-derived, only their input types
// change from records to plain values.

using System.Globalization;

namespace Wavee;

public static partial class Video
{
    // ── the vocabulary ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where a movable surface (today: the now-playing music video) is shown. Ordered by COMMITMENT —
    /// <see cref="Docked"/> costs the user nothing, <see cref="Detached"/> spawns a whole OS window — because the
    /// fallback ladder walks that order (<see cref="PlacementCore.FirstAvailable"/>). The numbers are load-bearing in
    /// exactly one place (the ladder) and are deliberately NOT what persistence stores
    /// (<see cref="PlacementPersistence.SavePlacement"/> stores a name).</summary>
    public enum SurfacePlacement : byte
    {
        /// <summary>Not shown at all. The only value that means "off".</summary>
        None = 0,
        /// <summary>Inline in the shell (the right rail's docked cap, or a watch page's in-page stage). The DEFAULT:
        /// no new OS window, no overlay, and the least-committing placement that is actually VISIBLE.</summary>
        Docked = 1,
        /// <summary>The in-window, draggable + resizable mini player. The fallback when <see cref="Docked"/> cannot
        /// fit (the rail has no room) — still low-commitment: no new OS window, dismissible, stays with the app.</summary>
        Floating = 2,
        /// <summary>A separate always-on-top OS window. The most committing placement.</summary>
        Detached = 3,
        /// <summary>Fills the shell. A MODE, not a home: entering remembers <see cref="PlacementState.ReturnTo"/> and
        /// exiting restores it, and it is never persisted as <see cref="PlacementState.Preferred"/>.</summary>
        Fullscreen = 4,
    }

    /// <summary>A set of <see cref="SurfacePlacement"/> values. Used for both what a surface ALLOWS (its policy) and
    /// what is AVAILABLE right now (allowed ∧ host-capable ∧ content-capable) — ONE bit-set, so "this track has no
    /// video" and "a second swapchain cannot be opened" degrade through the exact same path.</summary>
    [Flags]
    public enum PlacementSet : byte
    {
        None = 0,
        Docked = 1,
        Floating = 2,
        Detached = 4,
        Fullscreen = 8,
    }

    /// <summary>WHO draws the playback transport (scrub row + play/pause) right now. Exactly ONE owner at any instant,
    /// DERIVED from the resolved placement — never a second visibility flag per surface, because two independent flags
    /// is precisely how the shipping build ended up stacking the fullscreen video transport ON TOP of the global
    /// 72-DIP player bar (both owners rendered, nothing suppressed either).</summary>
    public enum TransportOwner : byte
    {
        /// <summary>The global 72-DIP player bar — the DEFAULT owner, and the owner whenever no video surface is
        /// mounted at all. It yields only to a FULL-BLEED surface in its own window (<see cref="Fullscreen"/>), where
        /// the shell unmounts it outright.</summary>
        GlobalBar = 0,
        /// <summary>An IN-WINDOW video card's own transport — the docked cap and the floating mini player both declare
        /// this identity. Each draws its own auto-hiding hover chrome OVER the picture; the 72-DIP bar keeps rendering
        /// below it. An overlay inside a card is not a second bar in the same band. (The three 0.2.9 doc comments that
        /// claim the docked card SUPPRESSES its transport contradict the code they sit on — ch 24 §9; the code ships.)</summary>
        Docked = 1,
        /// <summary>The detached pop-out window's stage. That window has NO player bar of its own, so the video's
        /// transport is the session transport while the video lives there. The invariant is one transport per WINDOW,
        /// not per session, so the main window's bar keeps rendering meanwhile.</summary>
        PopOut = 2,
        /// <summary>The full-bleed fullscreen surface's transport — the ONLY transport while fullscreen (the shell
        /// unmounts the title bar and the global player bar for the duration).</summary>
        Fullscreen = 3,
    }

    /// <summary>What a given surface allows and where it opens by default.</summary>
    /// <param name="Allowed">The placements this surface can ever occupy.</param>
    /// <param name="Default">The initial <see cref="PlacementState.Preferred"/> — where an unlit primary click opens.</param>
    public readonly record struct PlacementPolicy(PlacementSet Allowed, SurfacePlacement Default)
    {
        /// <summary>The music-video surface: docked cap (default), in-window mini player, detached pop-out, full-bleed
        /// fullscreen — the full commitment ladder plus the fullscreen mode. Docked is the default because it is the
        /// least-committing placement that is actually VISIBLE.</summary>
        public static readonly PlacementPolicy Music = new(
            PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen,
            SurfacePlacement.Docked);
    }

    /// <summary>The COMPLETE placement state of one surface, as a single value.
    /// <list type="bullet">
    /// <item><b>Intent vs reality.</b> <see cref="Requested"/>/<see cref="Preferred"/> are what the USER asked for;
    /// <see cref="Live"/> is what the host actually has mounted and is written ONLY by that host. A stuck toggle is
    /// exactly those two disagreeing with nowhere to say so.</item>
    /// <item><b>One placement, not a set of booleans.</b> <see cref="Requested"/> is an enum, so "mounted in two
    /// places at once" (the MF double-pump hazard) cannot be expressed.</item>
    /// <item><b>Closing IS off.</b> Every user-initiated close writes <see cref="Requested"/> =
    /// <see cref="SurfacePlacement.None"/>. The old content-scoped dismiss that expired on the next track is DELETED:
    /// keeping it would leave "off, but it will come back" expressible.</item>
    /// </list></summary>
    /// <param name="Requested">The user's live intent, or <see cref="SurfacePlacement.None"/> for "off". STICKY across
    /// content changes in BOTH directions.</param>
    /// <param name="Preferred">The last non-off, non-fullscreen placement the user chose. Where an unlit primary click
    /// opens, and the only placement worth persisting.</param>
    /// <param name="ReturnTo">Where <see cref="SurfacePlacement.Fullscreen"/> exits back to.</param>
    /// <param name="Live">What the owner actually has mounted right now. Written ONLY by the owner/host.</param>
    /// <param name="Available">Allowed ∧ host-capable ∧ content-capable, right now.</param>
    public readonly record struct PlacementState(
        SurfacePlacement Requested,
        SurfacePlacement Preferred,
        SurfacePlacement ReturnTo,
        SurfacePlacement Live,
        PlacementSet Available)
    {
        /// <summary>The off, nothing-mounted, nothing-available starting state for a surface with the given policy.</summary>
        public static PlacementState Initial(in PlacementPolicy policy) => new(
            Requested: SurfacePlacement.None,
            Preferred: policy.Default,
            ReturnTo: SurfacePlacement.None,
            Live: SurfacePlacement.None,
            Available: PlacementSet.None);

        /// <summary>The resting music-video state — what the reducer's video slot is seeded with.</summary>
        public static PlacementState Music => Initial(PlacementPolicy.Music);
    }

    /// <summary>What an owner must do to make reality match intent.</summary>
    public enum MountAction : byte
    {
        /// <summary>Reality already matches — do nothing.</summary>
        None,
        /// <summary>Nothing is mounted and something should be.</summary>
        Open,
        /// <summary>Something is mounted and nothing should be.</summary>
        Close,
        /// <summary>Mounted in the wrong placement — hand it over (close then open, in that order: exactly one surface
        /// may be mounted at a time).</summary>
        Move,
    }

    /// <summary>The kinds of thing that can happen to a surface. Exists so the whole state machine can be driven from
    /// data — and therefore property-tested over arbitrary command sequences. The app calls the named transitions.</summary>
    public enum PlacementCommandKind : byte
    {
        TogglePrimary, OpenAt, TurnOff, Availability, HostClosed, EnterFullscreen, ExitFullscreen, LiveChanged, Demote,
    }

    /// <summary>One command for <see cref="PlacementCore.Apply"/>. Unused fields are ignored per its kind.</summary>
    public readonly record struct PlacementCommand(
        PlacementCommandKind Kind,
        SurfacePlacement Placement = SurfacePlacement.None,
        PlacementSet Available = PlacementSet.None);

    // ── placement state machine ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The PURE, engine-free placement state machine. Every rule the surfaces, the transport owner and the
    /// player-bar affordance obey lives here exactly once.</summary>
    public static class PlacementCore
    {
        // The commitment ladder (cheap → expensive). Fallback walks DOWN it first (prefer the less committing surface)
        // and only then up, so losing a detached window lands you in the mini player rather than turning video off.
        static readonly SurfacePlacement[] Ladder =
        [
            SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Detached,
        ];

        /// <summary>The single-bit set for a placement (<see cref="PlacementSet.None"/> for
        /// <see cref="SurfacePlacement.None"/>).</summary>
        public static PlacementSet Bit(SurfacePlacement p) => p switch
        {
            SurfacePlacement.Docked => PlacementSet.Docked,
            SurfacePlacement.Floating => PlacementSet.Floating,
            SurfacePlacement.Detached => PlacementSet.Detached,
            SurfacePlacement.Fullscreen => PlacementSet.Fullscreen,
            _ => PlacementSet.None,
        };

        /// <summary>Whether <paramref name="set"/> contains <paramref name="p"/> (never true for
        /// <see cref="SurfacePlacement.None"/> — "off" is not a placement you can be available at).</summary>
        public static bool Allows(PlacementSet set, SurfacePlacement p)
            => p != SurfacePlacement.None && (set & Bit(p)) != PlacementSet.None;

        /// <summary>The placement to actually mount for <paramref name="want"/>: itself when available, else the
        /// nearest available placement walking DOWN the commitment ladder first and then up, else
        /// <see cref="SurfacePlacement.None"/>.</summary>
        public static SurfacePlacement FirstAvailable(SurfacePlacement want, PlacementSet available)
        {
            if (want == SurfacePlacement.None) return SurfacePlacement.None;
            if (Allows(available, want)) return want;
            // Fullscreen is a MODE, not a rung: when it is unavailable, do NOT walk DOWN from "above the ladder" (that
            // lands on Detached and spawns a whole OS window nobody asked for — headless, no fullscreen hook, or a
            // detached child host). Fall back to the CHEAPEST available placement instead; "where would Fullscreen
            // have exited to" is ReturnTo/ExitFullscreen's job, not this one's.
            if (want == SurfacePlacement.Fullscreen)
            {
                for (int f = 0; f < Ladder.Length; f++) if (Allows(available, Ladder[f])) return Ladder[f];
                return SurfacePlacement.None;
            }
            int i = LadderIndex(want);
            for (int d = i - 1; d >= 0; d--) if (Allows(available, Ladder[d])) return Ladder[d];
            for (int u = i + 1; u < Ladder.Length; u++) if (Allows(available, Ladder[u])) return Ladder[u];
            return SurfacePlacement.None;
        }

        // Fullscreen sits above the ladder: when unavailable the walk starts at the most committing real placement.
        static int LadderIndex(SurfacePlacement p) => p switch
        {
            SurfacePlacement.Docked => 0,
            SurfacePlacement.Floating => 1,
            SurfacePlacement.Detached => 2,
            _ => 3,
        };

        /// <summary>THE derived truth every surface, owner and affordance reads: the placement that should be mounted
        /// right now, or <see cref="SurfacePlacement.None"/>. Off when the user turned it off (including by closing
        /// it) or when nothing is available (the track has no video).</summary>
        public static SurfacePlacement Resolve(in PlacementState s) => ResolveWith(s, s.Available);

        /// <summary><see cref="Resolve"/> with the availability overridden — for asking "what WOULD resolve for
        /// <em>that</em> content?" without mutating state (the playback path asks this per track).</summary>
        public static SurfacePlacement ResolveWith(in PlacementState s, PlacementSet available)
        {
            if (s.Requested == SurfacePlacement.None) return SurfacePlacement.None;
            return FirstAvailable(s.Requested, available);
        }

        /// <summary>Whether anything is resolved (the surface should be visible / the media should be video).</summary>
        public static bool IsActive(in PlacementState s) => Resolve(s) != SurfacePlacement.None;

        // ── transitions ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The primary affordance: SYMMETRIC. Lit (anything resolved) → always off, from ANY placement.
        /// Unlit → open at <see cref="PlacementState.Preferred"/>. That symmetry is the whole reason the toggle cannot
        /// get stuck.</summary>
        public static PlacementState TogglePrimary(in PlacementState s)
            => IsActive(s) ? TurnOff(s) : OpenAt(s, s.Preferred);

        /// <summary>Show the surface at <paramref name="target"/> — the ONE way video comes back on after any close —
        /// and, for a real home (not Fullscreen), make the target the new <see cref="PlacementState.Preferred"/>.</summary>
        public static PlacementState OpenAt(in PlacementState s, SurfacePlacement target)
        {
            if (target == SurfacePlacement.None) return TurnOff(s);
            if (target == SurfacePlacement.Fullscreen) return EnterFullscreen(s);
            return s with
            {
                Requested = target,
                Preferred = target,
                ReturnTo = SurfacePlacement.None,
            };
        }

        /// <summary>Off — GLOBALLY and stickily, which is what EVERY user-initiated close means (the surface's own ✕,
        /// the picker's "turn off video", the primary's "switch to audio"). No subsequent track re-opens the surface;
        /// only an explicit <see cref="OpenAt"/>/<see cref="TogglePrimary"/> does. Keeps
        /// <see cref="PlacementState.Preferred"/> and clears the transient fullscreen bookkeeping.</summary>
        public static PlacementState TurnOff(in PlacementState s) => s with
        {
            Requested = SurfacePlacement.None,
            ReturnTo = SurfacePlacement.None,
        };

        /// <summary>Republish what is possible right now. Deliberately does NOT rewrite
        /// <see cref="PlacementState.Requested"/>: intent outlives a temporary loss of availability, so the next track
        /// with a video returns to where the user had it instead of to a fallback it silently got stuck in.</summary>
        public static PlacementState WithAvailability(in PlacementState s, PlacementSet available)
            => s with { Available = available };

        /// <summary>Owner-only: record what is actually mounted.</summary>
        public static PlacementState WithLive(in PlacementState s, SurfacePlacement live) => s with { Live = live };

        /// <summary>Fold ONE surface's mounted/unmounted report into the single <see cref="PlacementState.Live"/> field
        /// without letting it speak for any other surface: a surface may claim <c>Live</c> for itself, and may only
        /// release it if it still holds it. Unscoped, the mini player's "I am not mounted" would erase the pop-out's
        /// claim moments after it opened, and the reality field would settle on a lie — the very thing intent-vs-reality
        /// exists to prevent.</summary>
        /// <param name="live">The currently recorded live placement.</param>
        /// <param name="surface">The reporting surface's own placement.</param>
        /// <param name="mounted">Whether that surface is mounted right now.</param>
        public static SurfacePlacement LiveAfterReport(SurfacePlacement live, SurfacePlacement surface, bool mounted)
            => mounted ? surface
             : live == surface ? SurfacePlacement.None
             : live;

        /// <summary>Enter fullscreen, remembering where to go back to. Never touches
        /// <see cref="PlacementState.Preferred"/> — fullscreen is a mode, and persisting it would trap the user in it
        /// on the next launch.</summary>
        public static PlacementState EnterFullscreen(in PlacementState s) => s with
        {
            ReturnTo = s.Requested == SurfacePlacement.Fullscreen ? s.ReturnTo : s.Requested,
            Requested = SurfacePlacement.Fullscreen,
        };

        /// <summary>Leave fullscreen for <see cref="PlacementState.ReturnTo"/> (or the preferred home if it entered
        /// from off). A no-op when not in fullscreen.</summary>
        public static PlacementState ExitFullscreen(in PlacementState s)
        {
            if (s.Requested != SurfacePlacement.Fullscreen) return s;
            var back = s.ReturnTo != SurfacePlacement.None ? s.ReturnTo : s.Preferred;
            return s with { Requested = back, ReturnTo = SurfacePlacement.None };
        }

        /// <summary>Move the surface WITHOUT changing where the user says they like it — for an AMBIENT change that
        /// takes the current placement away but is not the user closing the feature (the rail being closed while video
        /// is docked). <see cref="PlacementState.Preferred"/> survives, so restoring the condition (re-opening the
        /// rail) re-docks automatically instead of leaving the user stuck wherever this demoted them to.</summary>
        public static PlacementState Demote(in PlacementState s, SurfacePlacement to)
        {
            if (s.Requested == SurfacePlacement.None || to == SurfacePlacement.None) return s;
            var next = FirstAvailable(to, s.Available);
            return next == SurfacePlacement.None ? s with { Requested = SurfacePlacement.None }
                                                 : s with { Requested = next };
        }

        /// <summary>The user closed the surface by its OWN chrome (an OS ✕ / Alt+F4 on the detached window, the mini
        /// player's ✕).
        /// <list type="bullet">
        /// <item>Closing the DETACHED window means "not in a separate window", not "stop watching" → fall to the next
        /// available less-committing placement and make that the new preference. Only if nothing is left does it turn
        /// off.</item>
        /// <item>Closing an in-app surface is <see cref="TurnOff"/>: video is off, globally and stickily, until the
        /// user asks for it again.</item>
        /// <item>Leaving fullscreen by its own chrome is <see cref="ExitFullscreen"/>.</item>
        /// </list>
        /// A close reported for a placement that is no longer resolved is STALE (a newer placement already won the
        /// race) and is ignored — the identity guard the owner would otherwise have to open-code.</summary>
        public static PlacementState HostClosed(in PlacementState s, SurfacePlacement closed)
        {
            if (closed == SurfacePlacement.None || Resolve(s) != closed) return s;
            if (closed == SurfacePlacement.Fullscreen) return ExitFullscreen(s);
            if (closed != SurfacePlacement.Detached) return TurnOff(s);
            var next = FirstAvailable(SurfacePlacement.Floating, s.Available & ~PlacementSet.Detached);
            return next == SurfacePlacement.None ? TurnOff(s) : OpenAt(s, next);
        }

        // ── transport ownership (gate.media.single-transport) ───────────────────────────────────────────────────────

        /// <summary>Every value <see cref="TransportOwner"/> can take, so the single-transport gate can enumerate the
        /// claimants without reflection (this file stays `System`-only).</summary>
        public static readonly TransportOwner[] AllTransportOwners =
        [
            TransportOwner.GlobalBar, TransportOwner.Docked, TransportOwner.PopOut, TransportOwner.Fullscreen,
        ];

        /// <summary>Every value <see cref="SurfacePlacement"/> can take — the gate's domain.</summary>
        public static readonly SurfacePlacement[] AllPlacements =
        [
            SurfacePlacement.None, SurfacePlacement.Docked, SurfacePlacement.Floating,
            SurfacePlacement.Detached, SurfacePlacement.Fullscreen,
        ];

        /// <summary>THE derived transport owner, from the RESOLVED placement (never from <c>Requested</c>: a placement
        /// that resolved away to None must hand the transport straight back to the bar). Fullscreen → Fullscreen (the
        /// shell unmounts the bar); Detached → PopOut (a separate window with no bar of its own); Docked/Floating →
        /// Docked (the card's own auto-hiding overlay INSIDE its bounds, with the 72-DIP bar still below); None →
        /// GlobalBar.</summary>
        public static TransportOwner TransportOwnerFor(SurfacePlacement resolved) => resolved switch
        {
            SurfacePlacement.Fullscreen => TransportOwner.Fullscreen,
            SurfacePlacement.Detached => TransportOwner.PopOut,
            SurfacePlacement.Docked or SurfacePlacement.Floating => TransportOwner.Docked,
            _ => TransportOwner.GlobalBar,
        };

        /// <summary><see cref="TransportOwnerFor"/> against a whole state (resolves first).</summary>
        public static TransportOwner TransportOwnerOf(in PlacementState s) => TransportOwnerFor(Resolve(s));

        /// <summary>The ONE question every transport-bearing component asks: "am I the owner right now?".</summary>
        public static bool OwnsTransport(TransportOwner claimant, SurfacePlacement resolved)
            => claimant == TransportOwnerFor(resolved);

        /// <summary>How many of <see cref="AllTransportOwners"/> claim the transport for <paramref name="resolved"/>.
        /// The gate asserts this is exactly 1 for every placement — the structural statement that the
        /// stacked-double-bar defect is unrepresentable rather than merely fixed.</summary>
        public static int TransportClaimants(SurfacePlacement resolved)
        {
            int n = 0;
            for (int i = 0; i < AllTransportOwners.Length; i++)
                if (OwnsTransport(AllTransportOwners[i], resolved)) n++;
            return n;
        }

        /// <summary>The gate assertion itself: exactly one transport owner for EVERY placement value. Pure and
        /// engine-free, so the harness can call it without a scene.</summary>
        public static bool SingleTransportInvariant()
        {
            for (int i = 0; i < AllPlacements.Length; i++)
                if (TransportClaimants(AllPlacements[i]) != 1) return false;
            return true;
        }

        // ── owner helpers ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What the owner of ALL placements must do to reconcile <paramref name="live"/> with
        /// <paramref name="desired"/>.</summary>
        public static MountAction DecideMount(SurfacePlacement desired, SurfacePlacement live)
            => desired == live ? MountAction.None
             : live == SurfacePlacement.None ? MountAction.Open
             : desired == SurfacePlacement.None ? MountAction.Close
             : MountAction.Move;

        /// <summary>What the owner of ONE placement (the detached-window host) must do: it opens iff that exact
        /// placement is resolved, and closes whenever it is not — including when a DIFFERENT placement won.</summary>
        public static MountAction DecideOwned(SurfacePlacement resolved, SurfacePlacement owned, bool alive)
            => DecideMount(resolved == owned ? owned : SurfacePlacement.None, alive ? owned : SurfacePlacement.None);

        /// <summary>The async-resolve fence: an in-flight content resolve may only publish if the generation it
        /// captured when it started is still current — otherwise a superseded track's result overwrites the current
        /// one ("changed track, got the previous video").</summary>
        public static bool IsCurrentGeneration(long capturedGen, long currentGen) => capturedGen == currentGen;

        // ── data-driven driver (property tests) ─────────────────────────────────────────────────────────────────────

        /// <summary>Apply one command. Equivalent to calling the named transition; exists so arbitrary command
        /// SEQUENCES can be generated and checked against <see cref="Invariant"/>.</summary>
        public static PlacementState Apply(in PlacementState s, in PlacementCommand c) => c.Kind switch
        {
            PlacementCommandKind.TogglePrimary => TogglePrimary(s),
            PlacementCommandKind.OpenAt => OpenAt(s, c.Placement),
            PlacementCommandKind.TurnOff => TurnOff(s),
            PlacementCommandKind.Availability => WithAvailability(s, c.Available),
            PlacementCommandKind.HostClosed => HostClosed(s, c.Placement),
            PlacementCommandKind.EnterFullscreen => EnterFullscreen(s),
            PlacementCommandKind.ExitFullscreen => ExitFullscreen(s),
            PlacementCommandKind.LiveChanged => WithLive(s, c.Placement),
            PlacementCommandKind.Demote => Demote(s, c.Placement),
            _ => s,
        };

        /// <summary>The invariants that must hold after EVERY command, whatever the order: (1) a resolved placement is
        /// always actually available; (2) <see cref="PlacementState.Preferred"/> is always a real home to return to
        /// (never off, never the fullscreen mode); (3) nothing is resolved while the surface is off; (4) exactly ONE
        /// transport owner, always.</summary>
        public static bool Invariant(in PlacementState s)
        {
            var r = Resolve(s);
            if (r != SurfacePlacement.None && !Allows(s.Available, r)) return false;
            if (s.Preferred == SurfacePlacement.None || s.Preferred == SurfacePlacement.Fullscreen) return false;
            if (s.Requested == SurfacePlacement.None && r != SurfacePlacement.None) return false;
            if (TransportClaimants(r) != 1) return false;
            return true;
        }
    }

    // ── pop-out fullscreen ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The one rule governing whether the DETACHED pop-out window is presenting itself borderless-fullscreen
    /// on its own monitor. Expressed as a rule rather than a set of clearing edges: "keep iff still Detached",
    /// evaluated at the SINGLE placement write path, makes "closed the pop-out while fullscreen, reopened it, it came
    /// back fullscreen" unrepresentable — there is no list of edges (the ✕, Alt+F4, a placement move, turn-off, a
    /// track with no video, a host that can no longer open a second window) for anyone to forget to extend.
    ///
    /// <para>Deliberately NOT <see cref="SurfacePlacement.Fullscreen"/>, which is the MAIN window's full-bleed
    /// surface. Two different OS windows, two different states.</para></summary>
    public static class DetachedFullscreenRule
    {
        /// <summary>The pop-out fullscreen bit AFTER a placement commit: the current bit, kept only while
        /// <paramref name="resolved"/> is still <see cref="SurfacePlacement.Detached"/>. Never turns the mode ON —
        /// only the user's toggle does, which is what guarantees a freshly opened pop-out starts windowed.</summary>
        public static bool After(bool current, SurfacePlacement resolved)
            => current && resolved == SurfacePlacement.Detached;
    }

    // ── docked host arbitration ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The two places a DOCKED video can render. Both are the SAME placement value
    /// (<see cref="SurfacePlacement.Docked"/>) and the SAME single mounted surface — this enum only picks which
    /// envelope wraps it, never a second gate on top of the resolved placement.</summary>
    public enum DockedFace : byte
    {
        /// <summary>The right rail's ONE card, pinned above the rail header in EVERY body (Details included):
        /// full-bleed at the rail's width, its HEIGHT following the playing content's own aspect,
        /// splitter-overridable. The default — every mount that does not say otherwise keeps this slot. (The rail's
        /// old SECOND face, a fixed square art tile for the Details body, was a defect, not a design: the same 16:9
        /// stream changed shape and width the moment the user switched rail bodies.)</summary>
        Cap = 0,

        /// <summary>The module watch page's IN-PAGE stage: a full-width surface owned by the page itself, the
        /// YouTube-style watch layout. FULL-BLEED — the page draws the envelope, the rounded silhouette and the idle
        /// poster ground, so this face contributes no corners, no border and no height of its own. The one face that
        /// is NOT in the rail, and therefore the one that makes the rail yield.</summary>
        PageStage = 2,
    }

    /// <summary>WHICH of the two docked hosts owns the app's ONE video surface right now. Derived, never stored.</summary>
    public enum DockedHost : byte
    {
        /// <summary>The right rail's ONE docked card. The RESTING owner: whenever nothing else is hosting, the rail
        /// is.</summary>
        Rail = 0,
        /// <summary>The module watch page's in-page stage. Owns the surface only while the ATTACHED page's playable is
        /// the very thing that is playing.</summary>
        PageStage = 1,
    }

    /// <summary>The PURE arbitration between the two DOCKED hosts — the right rail's card and a module watch page's
    /// in-page stage.
    ///
    /// <para><b>Why the host is DERIVED, never claimed.</b> There is exactly ONE video surface per player, and two
    /// independent mount sites want it. A claim/release handshake cannot work, and the reason is structural rather
    /// than a matter of care: <c>Flow.KeepAlive</c> exit-freezes the outgoing page in the SAME reconcile pass as the
    /// route change, and a frozen or parked component cannot re-render — so the claim would be stuck on a page that is
    /// no longer running, and the one-surface guard would trip the moment any other face mounted. There is no
    /// ownership token at all: every mount site asks <see cref="ShouldMount"/> the same question against the same
    /// plain values. A dead page cannot hold what it never held.</para>
    ///
    /// <para><b>The rail yields at RENDER time, never by writing state.</b> Nothing here writes the rail's mode or the
    /// placement. The rail simply renders a different body (<see cref="Rail.VideoCoupling.BodyModeFor"/>) and mounts
    /// no card, so the instant the stage stops hosting the rail is exactly where the user left it — with no displaced
    /// state to restore and no ordering to get wrong.</para></summary>
    public static class DockedHosting
    {
        /// <summary>Is the thing the attached page would STAGE the very thing that is playing? PLACEMENT-FREE on
        /// purpose: <see cref="DockedHostAvailable"/> feeds the availability set that <c>Resolve</c> consumes and
        /// <see cref="HostFor"/> consumes the RESOLVED placement, so a placement term here would close the loop into a
        /// cycle (availability → resolve → host → availability).
        ///
        /// <para><b>ONE id space, and it is the PLAYABLE uri.</b> <paramref name="activeStagePlayable"/> is what the
        /// attached page's stage would host, or <c>""</c> when nothing is staged, so an empty value can never
        /// accidentally equal an empty playing uri. It used to be the page's own ENTITY uri, which reads as the same
        /// thing and is not: a module's entity ids and its playable ids are different namespaces by design, so the two
        /// could never match and the stage never mounted. Ordinal, because these are uris and not display text.</para></summary>
        public static bool PageStageHosts(string? activeStagePlayable, string? playingUri)
            => !string.IsNullOrEmpty(activeStagePlayable)
            && !string.IsNullOrEmpty(playingUri)
            && string.Equals(activeStagePlayable, playingUri, StringComparison.Ordinal);

        /// <summary>THE derived host of the app's one docked video surface. <see cref="DockedHost.Rail"/> whenever
        /// nothing is docked, so the rail is the resting owner and callers never need a "nobody" case.</summary>
        /// <param name="resolved">The RESOLVED placement, never <c>Requested</c>: a dock request that resolved away
        /// (no availability) hosts nothing anywhere.</param>
        public static DockedHost HostFor(SurfacePlacement resolved, string? activeStagePlayable, string? playingUri)
            => resolved == SurfacePlacement.Docked && PageStageHosts(activeStagePlayable, playingUri)
                ? DockedHost.PageStage
                : DockedHost.Rail;

        /// <summary>Which host a face BELONGS to. Static structure (which envelope is where), not a decision — the
        /// decision is <see cref="HostFor"/>.</summary>
        public static DockedHost HostOf(DockedFace face)
            => face == DockedFace.PageStage ? DockedHost.PageStage : DockedHost.Rail;

        /// <summary>The ONE gate every docked mount site calls. At most one face is ever true, and exactly one is true
        /// iff the docked placement is resolved — the structural analogue of
        /// <see cref="PlacementCore.SingleTransportInvariant"/>.
        /// <list type="bullet">
        /// <item>Nothing mounts unless <paramref name="resolved"/> is <see cref="SurfacePlacement.Docked"/>. That is
        /// also the fullscreen collapse-and-return: entering fullscreen resolves away from Docked, every docked face
        /// goes false, and exiting restores exactly the face that was up.</item>
        /// <item>The <see cref="DockedFace.PageStage"/> face additionally requires
        /// <paramref name="ownerStagePlayable"/> == <paramref name="activeStagePlayable"/> — the whole parked-page
        /// discriminator: two keep-alive'd watch pages can be alive at once, and only the ATTACHED one wrote the
        /// active value, so the parked one's stage is false without the parked page having to (being able to)
        /// re-render and say so.</item>
        /// <item>The RAIL face requires that the stage is NOT hosting. It passes <c>null</c> for
        /// <paramref name="ownerStagePlayable"/>: it has no page of its own, it lives in the shell.</item>
        /// </list></summary>
        public static bool ShouldMount(DockedFace face, SurfacePlacement resolved,
                                       string? ownerStagePlayable, string? activeStagePlayable, string? playingUri)
        {
            if (resolved != SurfacePlacement.Docked) return false;
            bool stage = PageStageHosts(activeStagePlayable, playingUri);
            if (face == DockedFace.PageStage)
                return stage
                    && !string.IsNullOrEmpty(ownerStagePlayable)
                    && string.Equals(ownerStagePlayable, activeStagePlayable, StringComparison.Ordinal);
            return !stage;                                         // the rail's ONE card; the rail yields whole
        }

        /// <summary>Is the DOCKED capability bit available at all right now — the input the shell folds into the host
        /// placement capability, which <see cref="UpgradeGate.AvailabilityFor"/> intersects.
        ///
        /// <para>TWO INDEPENDENT SUPPLIERS, one OR. The rail can host a docked card only when the rail FITS. A watch
        /// page can ALWAYS host one: its stage is full-width page content and needs no rail at all. Before this OR the
        /// bit was the rail-fit test alone, so narrowing the window demoted a watch page's in-page video to the
        /// floating mini player — an overlay dismissable over a page whose whole purpose was to show that video. The
        /// rail's own cap is still demoted by a narrow window, because that half of the OR is unchanged.</para></summary>
        public static bool DockedHostAvailable(bool railFits, bool pageStageWouldHost)
            => railFits || pageStageWouldHost;

        /// <summary>Every value <see cref="DockedFace"/> can take — the mount gate's domain, so the ≤1 invariant can be
        /// enumerated without reflection.</summary>
        public static readonly DockedFace[] AllFaces = [DockedFace.Cap, DockedFace.PageStage];

        /// <summary>The ≤1 bound as a VALUE: how many faces would mount for these inputs. Exactly 0 when the docked
        /// placement is not resolved, exactly 1 when it is.</summary>
        public static int MountedFaces(SurfacePlacement resolved, string? ownerStagePlayable,
                                       string? activeStagePlayable, string? playingUri)
        {
            int n = 0;
            for (int i = 0; i < AllFaces.Length; i++)
            {
                var face = AllFaces[i];
                string? owner = face == DockedFace.PageStage ? ownerStagePlayable : null;
                if (ShouldMount(face, resolved, owner, activeStagePlayable, playingUri)) n++;
            }
            return n;
        }
    }

    // ── availability + the no-mid-track-swap rule ───────────────────────────────────────────────────────────────────

    /// <summary>The pure half of the "no mid-track auto-swap" rule.
    ///
    /// <para>A music-video association is detected ASYNCHRONOUSLY, so it routinely lands while the song is already
    /// playing. Committing that as an availability UPGRADE swaps the media host and restarts the track at position 0 —
    /// the reported "it jumped back to the start on its own". The product rule is the opposite: the badge lights,
    /// playback stays exactly where it is, and the user's click is what starts the video. DOWNGRADES are never
    /// deferred — a video-less track, a proven-dead playable and the ✕ must unmount immediately.</para></summary>
    public static class UpgradeGate
    {
        /// <summary>Content availability × HOST capability → the placement set. A playable WITHOUT a video makes every
        /// placement unavailable; one WITH a video is further masked by what the host can actually do right now (can
        /// the rail fit it, can a second window open, does the fullscreen hook exist).</summary>
        public static PlacementSet AvailabilityFor(bool hasVideo, PlacementSet hostCapable)
            => hasVideo ? PlacementPolicy.Music.Allowed & hostCapable : PlacementSet.None;

        /// <summary>Re-stamp a state with the availability THIS playable actually has. Required before acting on a user
        /// intent: a deferred upgrade leaves <c>Available</c> stale at <see cref="PlacementSet.None"/>, and both
        /// <see cref="PlacementCore.Resolve"/> and <see cref="PlacementCore.IsActive"/> consult it — so a lit badge's
        /// toggle would otherwise resolve to None and do nothing at all.</summary>
        public static PlacementState FoldAvailability(in PlacementState s, bool hasVideo, PlacementSet hostCapable)
            => PlacementCore.WithAvailability(s, AvailabilityFor(hasVideo, hostCapable));

        /// <summary>True when <paramref name="target"/> would turn an inactive surface ON and the caller is not
        /// entitled to commit that (neither a track boundary nor an explicit user action). The badge still updates;
        /// only the surface commit — and with it the media-kind refresh and the pop-out warm — is withheld.</summary>
        public static bool DeferUpgrade(in PlacementState before, in PlacementState target, bool commitUpgrade)
            => !commitUpgrade && !PlacementCore.IsActive(before) && PlacementCore.IsActive(target);

        /// <summary>The primary affordance's next state, folded onto this playable's real availability.
        /// <para>A deferred upgrade is why this is not simply <c>TogglePrimary(FoldAvailability(...))</c>: after a
        /// mid-track land the standing <c>Requested</c> intent is still ON while the surface is deliberately off, so
        /// the naive fold would read the state as "already watching" and the user's FIRST click would turn video off —
        /// they would have to click twice to start it. The click therefore COMMITS exactly what
        /// <see cref="DeferUpgrade"/> withheld; every other state toggles as before.</para></summary>
        public static PlacementState PrimaryClick(in PlacementState s, bool hasVideo, PlacementSet hostCapable)
        {
            var folded = FoldAvailability(s, hasVideo, hostCapable);
            return DeferUpgrade(s, folded, commitUpgrade: false) ? folded : PlacementCore.TogglePrimary(folded);
        }
    }

    /// <summary>The Connect-wire half of the same rule: which video facts the cluster has ALREADY been told about the
    /// current track. A badge-only association land changes no media host and fires no playback event, so nothing
    /// would otherwise re-publish the player state — remote controllers would never see the offer for the song they
    /// are watching us play. This tracker turns that land into EXACTLY ONE extra PutState: the FIRST observation of a
    /// track is only a baseline (its facts ride the track-change PutState the publisher already sends), and afterwards
    /// only a real gain — has-video false→true, or a gid arriving/changing — announces. Idempotent re-observations are
    /// silent.</summary>
    public sealed class ConnectVideoFacts
    {
        string? _uri;
        bool _hasVideo;
        string? _gidHex;

        /// <summary>Fold the current track's video facts. Returns true iff a PLAYER_STATE_CHANGED PutState must be
        /// enqueued.</summary>
        public bool Observe(string? trackUri, bool hasVideo, string? videoGidHex)
        {
            bool sameTrack = string.Equals(trackUri, _uri, StringComparison.Ordinal);
            bool gained = sameTrack
                && ((hasVideo && !_hasVideo)
                    || (videoGidHex is { Length: > 0 } && !string.Equals(videoGidHex, _gidHex, StringComparison.Ordinal)));
            _uri = trackUri;
            _hasVideo = hasVideo;
            _gidHex = videoGidHex;
            return gained;
        }
    }

    // ── stage input affordances ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Which input affordances a video stage gets, by the surface it sits in. Returns plain bools rather than
    /// the engine's cursor policy so the test assembly stays FluentGpu-free.</summary>
    public static class StageInput
    {
        /// <summary>Whether a press on the PICTURE that travels past the drag box moves the WINDOW (the OS move loop —
        /// Aero Snap, the snap bar and monitor hops included). Only the pop-out OWNS its window, and that window is
        /// chromeless, so the picture is the only thing to grab. Dragging the mini player's or the docked card's
        /// picture must never move the MAIN window (and a drag capture inside a scroller would steal touch pans); a
        /// fullscreen window has nowhere to go.</summary>
        /// <param name="identity">The surface's transport identity (constant for the life of the surface).</param>
        /// <param name="hostFullscreen">Whether the surface is presenting fullscreen right now.</param>
        public static bool DragMovesWindow(TransportOwner identity, bool hostFullscreen)
            => identity == TransportOwner.PopOut && !hostFullscreen;

        /// <summary>Whether the idle cursor hides with the chrome while WINDOWED. A dedicated video window hides it
        /// (mpv's windowed default); an inline surface keeps the page's cursor and hides it only in fullscreen —
        /// hiding it over a small video steals it from the page around it, and the user cannot tell whether the app
        /// has hung.</summary>
        public static bool HidesCursorWindowed(TransportOwner identity) => identity == TransportOwner.PopOut;
    }

    /// <summary>Pure mount rule for the now-playing video stage (PiP / pop-out). The MF session only advances while a
    /// mounted element pumps it — unmounting the stage because the resolved source is briefly null (override
    /// re-resolve, track-edge handoff) leaves a Loading poster with no pump and a black/stuck surface over audio.</summary>
    public static class SurfaceMount
    {
        /// <summary>Mount the player stage whenever a player exists. Source may be null — overlay Loading/poster on
        /// top; do not tear down the only pump.</summary>
        public static bool ShouldMountPlayerStage(bool playerPresent) => playerPresent;
    }

    // ── persistence codecs ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How a surface's placement and geometry survive a restart, as pure string ↔ value conversions (the
    /// caller owns the actual <c>SettingKey</c>s — <c>Platform.Keys.VideoPreferredPlacement</c> / <c>VideoPipRect</c>
    /// / <c>VideoWindowRect</c>, whose NAMES are byte-identical to 0.2.9's so an in-place upgrade does not lose where
    /// the user likes to watch). The rule the shapes encode: <b>persist where the user likes to work; never persist
    /// whether something is running.</b> So the PREFERRED placement and the geometry round-trip, while
    /// <see cref="SurfacePlacement.None"/> (off) and <see cref="SurfacePlacement.Fullscreen"/> (a mode) deliberately
    /// do not — restoring "off" would be meaningless and restoring fullscreen would trap the user in it.</summary>
    public static class PlacementPersistence
    {
        /// <summary>The stored form of a preferred placement. Deliberately a NAME, not the enum's number: the numeric
        /// values encode the commitment ladder, so a future reordering would silently reinterpret everyone's saved
        /// preference. Empty for values that must never be persisted (off / fullscreen).</summary>
        public static string SavePlacement(SurfacePlacement p) => p switch
        {
            SurfacePlacement.Docked => "docked",
            SurfacePlacement.Floating => "floating",
            SurfacePlacement.Detached => "detached",
            _ => "",
        };

        /// <summary>Read a stored preference back, falling back to the surface's default for anything unrecognised,
        /// empty, or no longer <c>Allowed</c> — a saved placement whose surface has since been removed must not
        /// resurrect a placement nothing can honor.</summary>
        public static SurfacePlacement LoadPlacement(string? raw, in PlacementPolicy policy)
        {
            var p = (raw ?? "").Trim().ToLowerInvariant() switch
            {
                "docked" => SurfacePlacement.Docked,
                "floating" => SurfacePlacement.Floating,
                "detached" => SurfacePlacement.Detached,
                _ => SurfacePlacement.None,
            };
            return p != SurfacePlacement.None && PlacementCore.Allows(policy.Allowed, p) ? p : policy.Default;
        }

        /// <summary>Geometry as a comma-separated rect. Rounded to whole units — sub-pixel drag precision is not worth
        /// persisting.</summary>
        public static string SaveRect(float x, float y, float w, float h)
            => w <= 0f || h <= 0f
                ? ""
                : ((int)MathF.Round(x)).ToString(CultureInfo.InvariantCulture) + "," +
                  ((int)MathF.Round(y)).ToString(CultureInfo.InvariantCulture) + "," +
                  ((int)MathF.Round(w)).ToString(CultureInfo.InvariantCulture) + "," +
                  ((int)MathF.Round(h)).ToString(CultureInfo.InvariantCulture);

        /// <summary>Parse geometry back. False for anything malformed or degenerate, so a corrupt value falls back to
        /// the surface's default position instead of opening a 0×0 window somewhere off-screen.</summary>
        public static bool TryLoadRect(string? raw, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var parts = raw.Split(',');
            if (parts.Length != 4) return false;
            var ci = CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0], NumberStyles.Float, ci, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, ci, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, ci, out w)) return false;
            if (!float.TryParse(parts[3], NumberStyles.Float, ci, out h)) return false;
            if (!(w > 0f) || !(h > 0f) || float.IsNaN(x) || float.IsNaN(y)) { x = y = w = h = 0f; return false; }
            return true;
        }
    }

    /// <summary>The app-owned, storage-stable names for the control kit's video aspect policies. Deliberately does not
    /// reuse the engine enum's numeric values: persisted data must survive an engine enum reorder.</summary>
    public enum AspectPreference : byte { Fit, Crop, Stretch, Native, Custom }

    /// <summary>Codec for the global video-aspect preference. Missing/corrupt values degrade to Fit and 16:9; wire
    /// names are append-only, culture-invariant settings data rather than UI strings.</summary>
    public static class AspectPersistence
    {
        public const double DefaultCustomRatio = 16.0 / 9.0;

        public static AspectPreference LoadMode(string? raw) => raw switch
        {
            "crop" => AspectPreference.Crop,
            "stretch" => AspectPreference.Stretch,
            "native" => AspectPreference.Native,
            "custom" => AspectPreference.Custom,
            _ => AspectPreference.Fit,
        };

        public static string SaveMode(AspectPreference mode) => mode switch
        {
            AspectPreference.Crop => "crop",
            AspectPreference.Stretch => "stretch",
            AspectPreference.Native => "native",
            AspectPreference.Custom => "custom",
            _ => "fit",
        };

        public static double LoadRatio(double raw)
            => double.IsFinite(raw) && raw > 0.01 && raw < 100.0 ? raw : DefaultCustomRatio;
    }

    // ── local video attachments ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One persisted attachment: a playable uri → a local <c>.mp4</c>. Device-wide and account-independent,
    /// which is why it is NOT entity state (it survives scope switches, and a scope does not). The roster that holds
    /// these is `Video.Host.cs`'s; every DECISION about them is here.</summary>
    /// <param name="Uri">The playable uri — the primary key, so a duplicate attach IS the replace.</param>
    /// <param name="Path">The absolute path of the attached file.</param>
    /// <param name="AddedAtUnix">When it was attached (seconds). The roster's sort key.</param>
    /// <param name="SourceKey">The resolved source identity the player would open, for the quarantine pair.</param>
    public readonly record struct OverrideRecord(string Uri, string Path, long AddedAtUnix, string SourceKey);

    /// <summary>Which tier-1 branch a playable takes before the source's own video resolver is consulted.</summary>
    public enum OverrideTier : byte
    {
        /// <summary>No attachment for this playable — fall through to the source tier.</summary>
        None,
        /// <summary>An attachment exists and its file is present: play it (it always wins over the source's own
        /// video).</summary>
        UseOverride,
        /// <summary>An attachment exists but its file is gone (moved / drive offline). Fall through, KEEP the link for
        /// repair, and surface it once per session — never delete the user's curation behind their back.</summary>
        Broken,
        /// <summary>An attachment exists but this exact (uri, source key) already failed to open this session. Skip
        /// tier 1 silently — the one-shot fallback latch that makes a bad file impossible to loop on.</summary>
        Quarantined,
    }

    /// <summary>The pure tier-1 outcome: the branch plus (for the two "we have a record" branches) the record.</summary>
    public readonly record struct OverrideDecision(OverrideTier Tier, OverrideRecord Override)
    {
        public static OverrideDecision None => new(OverrideTier.None, default);
        /// <summary>True when the resolver should stop here and play <see cref="Override"/>.</summary>
        public bool Wins => Tier == OverrideTier.UseOverride;
    }

    /// <summary>The status chip a curated attachment shows in Settings (and the badge language the toasts reuse).</summary>
    public enum OverrideStatus : byte
    {
        /// <summary>The file is where the user left it — this attachment plays.</summary>
        Ok,
        /// <summary>The file is gone but its volume is still mounted: a move/rename. "Locate…" repairs it.</summary>
        Missing,
        /// <summary>The whole volume is absent (unplugged drive / offline share). NOT a repair prompt — it heals by
        /// itself when the drive comes back, so the copy must never suggest removing the link.</summary>
        DriveOffline,
        /// <summary>The file exists but failed to open this session (bad codec / corrupt container). Quarantined until
        /// a replace or a restart; no Retry CTA, because retrying the same file is not a fix.</summary>
        Unplayable,
    }

    /// <summary>Why an attach was refused. Validation is deliberately shallow — extension + existence — because a deep
    /// MF probe would block the UI and the honest deep failure surfaces at play time through the recovery hook.</summary>
    public enum AttachRejection : byte
    {
        /// <summary>Accepted.</summary>
        None,
        /// <summary>Not an <c>.mp4</c>.</summary>
        NotMp4,
        /// <summary>The path does not point at an existing file.</summary>
        NotFound,
    }

    /// <summary>Which rows the <c>Video ▸</c> submenu builds for a playable, decided at open time from the curation
    /// state. Attach and Replace are mutually exclusive (the uri is the primary key, so a duplicate attach IS the
    /// replace).</summary>
    [Flags]
    public enum MenuItems : byte
    {
        None = 0,
        /// <summary>"Attach video file…" — nothing attached yet.</summary>
        Attach = 1,
        /// <summary>"Replace video file…" — something is attached.</summary>
        Replace = 2,
        /// <summary>"Remove video" (destructive, behind a separator; applies immediately + toast-undo, never a
        /// dialog).</summary>
        Remove = 4,
        /// <summary>"Locate video file…" — the link is broken, so offer the repair pick.</summary>
        Locate = 8,
        /// <summary>"Show in Explorer" — the file is present, so revealing it is meaningful.</summary>
        ShowInExplorer = 16,
    }

    /// <summary>One Settings roster row: the persisted attachment plus everything the row renders, resolved once at
    /// load time (never per frame). <see cref="Title"/>/<see cref="Subtitle"/> fall back to the uri when the catalog
    /// has never seen the playable — a device-wide roster outlives any one account's catalog.</summary>
    public readonly record struct OverrideRow(
        OverrideRecord Override, OverrideStatus Status, string Title, string? Subtitle, string FileName)
    {
        public string Uri => Override.Uri;
        public string Path => Override.Path;
        /// <summary>The link is broken in a way "Locate video file…" can repair (a move/rename, not an absent
        /// volume).</summary>
        public bool CanLocate => Status == OverrideStatus.Missing;
        /// <summary>Revealing the file in Explorer only makes sense while it is actually there.</summary>
        public bool CanReveal => Status is OverrideStatus.Ok or OverrideStatus.Unplayable;
    }

    /// <summary>What the attachment manager's ROOT view is showing right now. A pure function of "how many attachments
    /// exist" × "is the user searching" × "did the search hit anything" — so the flyout never re-decides it inline.</summary>
    public enum ManagerSection : byte
    {
        /// <summary>Nothing is attached at all: teach the context-menu attach path instead of showing an empty
        /// list.</summary>
        Empty,
        /// <summary>The resting root: the newest few attachments plus the "Browse all…" drill-in.</summary>
        Recent,
        /// <summary>A live query with hits — the results take the place of the recent section (no drill required).</summary>
        Results,
        /// <summary>A live query that matched nothing.</summary>
        NoMatches,
    }

    /// <summary>The pure decisions behind every video-override UX surface: the context submenu, the Settings roster,
    /// the manager flyout and the row indicator. Every engine-bound file is a thin adapter that renders what this
    /// decides and never re-decides it.</summary>
    public static class OverrideUx
    {
        /// <summary>How many attachments the manager's "Recently added" section shows before the user has to Browse
        /// all. Small on purpose: the section answers "did the thing I just attached land?", not "what do I own".</summary>
        public const int RecentCount = 4;

        /// <summary>The one accepted container. Deliberately narrow: the media host's file branch is MP4-only, and a
        /// filter the user cannot get wrong beats an error they have to read.</summary>
        public const string Extension = ".mp4";

        /// <summary>The one roster ordering: newest attachment first, ties broken by uri so the list can never shuffle
        /// between two rows attached in the same second.</summary>
        public static readonly Comparison<OverrideRow> RecencyOrder = static (a, b) =>
        {
            int c = b.Override.AddedAtUnix.CompareTo(a.Override.AddedAtUnix);
            return c != 0 ? c : string.CompareOrdinal(a.Uri, b.Uri);
        };

        /// <summary>The picker filter tuple (label, spec) — one definition shared by "Attach…", "Replace…" and
        /// "Locate…".</summary>
        public static (string Name, string Spec) PickerFilter(string label) => (label, "*" + Extension);

        /// <summary>The picker filter for the "Play file…" command: everything Wavee can play from disk in one row (an
        /// mp4 is playable because a dropped/picked video attaches as its own override and plays with its embedded
        /// audio).</summary>
        public static (string Name, string Spec) PlayableFilter(string label)
            => (label, "*.mp3;*.ogg;*.flac;*" + Extension);

        /// <summary>Extension test (case-insensitive, culture-invariant — a path is not prose).</summary>
        public static bool IsMp4(string? path)
            => path is { Length: > 0 } && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>Is this a file the AUDIO host can play? The format list is the local-file resolver's, restated
        /// here as ONE predicate so a surface can never accept a file the resolver would then refuse. It is kept in
        /// step with <see cref="PlayableFilter"/> by the one test that reads both.</summary>
        public static bool IsAudioFile(string? path)
        {
            if (path is not { Length: > 0 }) return false;
            return path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Validate a picked/dropped path before it becomes an attachment: the right container, and actually
        /// there. <paramref name="fileExists"/> is injected so the rule is testable without a disk.</summary>
        public static AttachRejection Validate(string? path, Func<string, bool> fileExists)
        {
            if (!IsMp4(path)) return AttachRejection.NotMp4;
            bool exists;
            try { exists = fileExists(path!); }
            catch { exists = false; }
            return exists ? AttachRejection.None : AttachRejection.NotFound;
        }

        /// <summary>The FIRST <c>.mp4</c> in a file drop, or null when the drop carries none. A mixed drop is not an
        /// error — the user aimed at a track row with something video-shaped in the set, so take it and ignore the
        /// rest.</summary>
        public static string? FirstMp4(IReadOnlyList<string>? paths)
        {
            if (paths is null) return null;
            for (int i = 0; i < paths.Count; i++)
                if (IsMp4(paths[i])) return paths[i];
            return null;
        }

        /// <summary>The pure tier-1 walk: play the attachment, fall through because its file is gone, skip it because
        /// it already failed this session, or no attachment at all. Every input is a plain value, so the whole of the
        /// resolver's first tier is testable without a disk or a store. Quarantine is checked BEFORE existence: a
        /// quarantined pair is skipped silently whether or not the file is still there.</summary>
        /// <param name="has">Is a record attached to this playable?</param>
        /// <param name="record">That record (ignored when <paramref name="has"/> is false).</param>
        /// <param name="quarantined">Has this exact (uri, source key) pair already failed to open this session?</param>
        /// <param name="fileExists">Existence probe for <see cref="OverrideRecord.Path"/>.</param>
        public static OverrideDecision Decide(bool has, in OverrideRecord record, bool quarantined,
                                              Func<string, bool> fileExists)
        {
            if (!has) return OverrideDecision.None;
            if (quarantined) return new OverrideDecision(OverrideTier.Quarantined, record);
            bool exists;
            try { exists = fileExists(record.Path); }
            catch { exists = false; }
            return exists ? new OverrideDecision(OverrideTier.UseOverride, record)
                          : new OverrideDecision(OverrideTier.Broken, record);
        }

        /// <summary>Map a tier-1 decision onto the roster's status chip. The Broken tier splits here — and ONLY here —
        /// into "Missing" (the volume is mounted, the file moved: repairable) and "Drive offline" (the volume itself is
        /// gone: it heals by itself when the drive returns, so never prompt to remove it).</summary>
        public static OverrideStatus StatusOf(in OverrideDecision d, Func<string, bool> directoryExists)
            => d.Tier switch
            {
                OverrideTier.UseOverride => OverrideStatus.Ok,
                OverrideTier.Quarantined => OverrideStatus.Unplayable,
                OverrideTier.Broken => RootExists(d.Override.Path, directoryExists)
                    ? OverrideStatus.Missing
                    : OverrideStatus.DriveOffline,
                _ => OverrideStatus.Ok,
            };

        static bool RootExists(string path, Func<string, bool> directoryExists)
        {
            try
            {
                string? root = System.IO.Path.GetPathRoot(path);
                // A rootless (relative) path has no volume to be offline — treat it as a plain move.
                if (root is not { Length: > 0 }) return true;
                return directoryExists(root);
            }
            catch { return true; }
        }

        /// <summary>The deepest still-existing ancestor directory of a (now missing) path — where a "Locate video
        /// file…" picker should open, so the user restarts from the closest surviving landmark rather than from My
        /// Computer (the Lightroom repair pattern). Null when nothing on the chain exists (an offline volume).</summary>
        public static string? NearestExistingAncestor(string? path, Func<string, bool> directoryExists)
        {
            if (path is not { Length: > 0 }) return null;
            string? dir;
            try { dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)); }
            catch { return null; }
            while (dir is { Length: > 0 })
            {
                bool exists;
                try { exists = directoryExists(dir); }
                catch { exists = false; }
                if (exists) return dir;
                string? parent;
                try { parent = System.IO.Path.GetDirectoryName(dir); }
                catch { return null; }
                if (string.Equals(parent, dir, StringComparison.Ordinal)) return null;   // hit the root and it is gone
                dir = parent;
            }
            return null;
        }

        /// <summary>Which rows the <c>Video ▸</c> submenu shows for one playable. The submenu exists only for a SINGLE
        /// selection (attaching one file to N tracks is not a thing the model expresses) and only on a build that has
        /// the curation roster — with no roster the whole feature is unreachable, which is its kill switch.
        /// <paramref name="tier"/> is <see cref="Decide"/>'s answer, the same tier walk playback takes, so the menu can
        /// never disagree with what will actually play.</summary>
        public static MenuItems MenuFor(bool singleSelection, string? playableUri, bool rosterPresent,
                                        bool hasOverride, OverrideTier tier)
        {
            if (!singleSelection || playableUri is not { Length: > 0 } || !rosterPresent) return MenuItems.None;
            if (!hasOverride) return MenuItems.Attach;

            var items = MenuItems.Replace | MenuItems.Remove;
            switch (tier)
            {
                case OverrideTier.Broken:
                    items |= MenuItems.Locate;
                    break;
                case OverrideTier.UseOverride:
                case OverrideTier.Quarantined:
                    items |= MenuItems.ShowInExplorer;
                    break;
            }
            return items;
        }

        /// <summary>Build the Settings roster: newest attachment first (the roster answers "what have I attached?",
        /// and the thing you just attached is the thing you are looking for), each row carrying its resolved status
        /// and display copy. Allocates freely — it runs once per load, never per frame.</summary>
        /// <param name="all">The whole persisted roster.</param>
        /// <param name="decide">The tier-1 walk for one record (the host binds its quarantine set into this).</param>
        /// <param name="directoryExists">Volume probe for the Missing / DriveOffline split.</param>
        /// <param name="title">Playable uri → its known title, or null. Never an entity read from CORE.</param>
        /// <param name="artistLine">Playable uri → its precomputed credit line, or null.</param>
        public static List<OverrideRow> BuildRoster(
            IReadOnlyList<OverrideRecord>? all,
            Func<OverrideRecord, OverrideDecision> decide,
            Func<string, bool> directoryExists,
            Func<string, string?>? title = null,
            Func<string, string?>? artistLine = null)
        {
            if (all is null || all.Count == 0) return [];
            var rows = new List<OverrideRow>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var o = all[i];
                var status = StatusOf(decide(o), directoryExists);
                string? t = null, a = null;
                if (title is not null) { try { t = title(o.Uri); } catch { t = null; } }
                if (artistLine is not null) { try { a = artistLine(o.Uri); } catch { a = null; } }
                rows.Add(new OverrideRow(o, status, TitleFor(o.Uri, t), SubtitleFor(a), FileNameOf(o.Path)));
            }
            rows.Sort(RecencyOrder);
            return rows;
        }

        /// <summary>The newest <paramref name="count"/> attachments, newest first — the manager's "Recently added"
        /// section. Re-sorts defensively rather than trusting the caller's order, so a roster built by some other path
        /// still yields a truthful "recent". Never mutates the input.</summary>
        public static List<OverrideRow> RecentlyAdded(IReadOnlyList<OverrideRow>? rows, int count = RecentCount)
        {
            if (rows is null || rows.Count == 0 || count <= 0) return [];
            var copy = new List<OverrideRow>(rows);
            copy.Sort(RecencyOrder);
            if (copy.Count > count) copy.RemoveRange(count, copy.Count - count);
            return copy;
        }

        /// <summary>Is the user actually searching? Whitespace is not a query — an all-space field must restore the
        /// resting root rather than show "no matches".</summary>
        public static bool IsSearching(string? query)
            => query is { Length: > 0 } && !string.IsNullOrWhiteSpace(query);

        /// <summary>Does this row match the query? Case-insensitive substring over the three things the user could
        /// plausibly remember — the track title, the artist line, and the file name. NOT the full path: a path match
        /// would make every row under one folder a hit, which is noise, and the folder is visible anyway.</summary>
        public static bool Matches(in OverrideRow row, string? query)
        {
            if (!IsSearching(query)) return true;
            string q = query!.Trim();
            return Contains(row.Title, q) || Contains(row.Subtitle, q) || Contains(row.FileName, q);
        }

        static bool Contains(string? haystack, string needle)
            => haystack is { Length: > 0 } && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        /// <summary>Filter the roster by the manager's search box, preserving newest-first order. An empty/whitespace
        /// query returns the input untouched (the caller then shows the resting root, not "all results").</summary>
        public static IReadOnlyList<OverrideRow> Search(IReadOnlyList<OverrideRow>? rows, string? query)
        {
            if (rows is null || rows.Count == 0) return [];
            if (!IsSearching(query)) return rows;
            var hits = new List<OverrideRow>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
                if (Matches(rows[i], query)) hits.Add(rows[i]);
            return hits;
        }

        /// <summary>Which section the manager's ROOT view renders. <paramref name="matchCount"/> is the size of
        /// <see cref="Search"/>'s result — passed in rather than recomputed so the caller filters exactly once.</summary>
        public static ManagerSection RootSection(int total, string? query, int matchCount)
        {
            if (total <= 0) return ManagerSection.Empty;
            if (!IsSearching(query)) return ManagerSection.Recent;
            return matchCount > 0 ? ManagerSection.Results : ManagerSection.NoMatches;
        }

        /// <summary>Does the root offer the "Browse all…" drill-in? Whenever anything is attached and no query is live
        /// — even when the recent section already lists everything, because the LEAF is where the full action set
        /// lives (the compact recent rows deliberately carry status only).</summary>
        public static bool ShowsBrowseAll(int total, string? query)
            => total > 0 && !IsSearching(query);

        /// <summary>Row title: the playable's title when the catalog knows it, else the raw uri (a device-wide roster
        /// survives accounts and catalogs — showing the uri is honest, showing nothing is not).</summary>
        public static string TitleFor(string uri, string? knownTitle)
            => knownTitle is { Length: > 0 } ? knownTitle : uri;

        /// <summary>Row subtitle: the precomputed credit line, or null when the playable is unknown. The line is
        /// already joined at commit (P11) — this never concatenates.</summary>
        public static string? SubtitleFor(string? artistLine)
            => artistLine is { Length: > 0 } ? artistLine : null;

        /// <summary>File name for display (the full path rides in the tooltip / secondary line).</summary>
        public static string FileNameOf(string? path)
        {
            if (path is not { Length: > 0 }) return "";
            try { return System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : path; }
            catch { return path; }
        }
    }

    /// <summary>What the warm override roster just did to one playable.</summary>
    public enum OverrideMutationKind : byte { Attach = 0, Replace = 1, Remove = 2 }

    /// <summary>The pure plan for one override mutation — latch clears, whether to commit a has-video upgrade, whether
    /// to force a same-kind video reload, and whether a reveal may <c>OpenAt</c> after the commit.</summary>
    public readonly record struct OverrideMutationPlan(
        bool ClearHasVideoLatch,
        bool ClearDeadVideoLatch,
        bool CommitHasVideoUpgrade,
        bool ForceReloadIfVideo,
        bool RevealSurfaceIfCurrent);

    /// <summary>Engine-free decision layer for local video-override mutations and the track-boundary / reveal rules.</summary>
    public static class OverrideMutation
    {
        /// <summary>Plan the side-effects for one attach/replace/remove.</summary>
        /// <param name="kind">What the roster just did.</param>
        /// <param name="isCurrentPlayable">Is the mutated uri the now-playing track?</param>
        /// <param name="videoAlreadyActive">Is the video surface already resolved?</param>
        /// <param name="previousSourceKey">The resolved source key before this mutation (null = none published).</param>
        /// <param name="nextSourceKey">The override's source key after the mutation (null on remove).</param>
        public static OverrideMutationPlan Plan(
            OverrideMutationKind kind,
            bool isCurrentPlayable,
            bool videoAlreadyActive,
            string? previousSourceKey,
            string? nextSourceKey)
        {
            bool remove = kind == OverrideMutationKind.Remove;
            // Attach must NOT clear the has-video latch — that latch absorbs transient has=false / null-uri glitches so
            // we do not pay a Video→Audio→Video round trip. Only a real user removal ends it.
            bool clearHas = remove;
            // Dead latch always clears: attach/replace re-arms a prior failed open; remove drops the playable entirely.
            bool clearDead = true;
            bool commitUpgrade = true;   // override mutations are explicit user actions — never deferred
            // Force only when video is already live AND the source identity really changed (a replace). A first attach
            // that flips Audio→Video is handled by the availability edge alone — forcing would double-load.
            bool sourceChanged = nextSourceKey is { Length: > 0 }
                && !string.Equals(previousSourceKey, nextSourceKey, StringComparison.Ordinal);
            bool force = !remove && isCurrentPlayable && videoAlreadyActive && sourceChanged;
            // Reveal after the has-video commit so OpenAt never runs against Available=None.
            bool reveal = !remove && isCurrentPlayable;
            return new OverrideMutationPlan(clearHas, clearDead, commitUpgrade, force, reveal);
        }

        /// <summary>Real track boundary for latch teardown. A null/empty next (or previous) uri is a mid-push glitch —
        /// the has-video latch's own null-suppression only works if we do NOT clear the latch first.</summary>
        public static bool IsRealTrackBoundary(string? previousUri, string? nextUri)
            => previousUri is { Length: > 0 }
               && nextUri is { Length: > 0 }
               && !string.Equals(previousUri, nextUri, StringComparison.Ordinal);

        /// <summary>Whether a reveal may <c>OpenAt</c>: the playable is current, has-video is already committed, and
        /// the surface is not already active (opening an already-active surface is a no-op for media).</summary>
        public static bool CanReveal(bool isCurrent, bool hasVideoCommitted, bool alreadyActive)
            => isCurrent && hasVideoCommitted && !alreadyActive;
    }

    // ── the in-window mini player's geometry ────────────────────────────────────────────────────────────────────────

    /// <summary>The PiP's constants and its fit. The numbers are 0.2.9's verbatim, including the deliberately-inexact
    /// default (<see cref="DefaultW"/>×<see cref="DefaultH"/> is 0.5611, not 9/16 = 0.5625): a rect restored from a
    /// 0.2.9 profile must land identically, so the 0.25 % is kept rather than "corrected".</summary>
    public static class Pip
    {
        public const float DefaultW = 360f, DefaultH = 202f;
        public const float MinW = 240f, MinH = 135f;
        /// <summary>The resize hit bands. Corner rows are 12 tall while the edge bands stay 6, so the bottom band
        /// grazes only the transport's 8-DIP bottom padding. The chrome strip is inset by <see cref="CornerW"/>
        /// left/right and <see cref="EdgeBand"/> top so neither the drag surface nor the ✕ sits under a resize band —
        /// a ✕ whose corner is stolen by the NE zone is the classic overlay bug.</summary>
        public const float EdgeBand = 6f, CornerW = 14f, CornerH = 12f;
        /// <summary>The viewport margins the fit reserves (the player bar below, the chrome above).</summary>
        public const float ReserveBottom = 72f, ReserveTop = 32f;
        /// <summary>The no-report fallback ratio — <see cref="DefaultH"/>/<see cref="DefaultW"/>, NOT 9/16.</summary>
        public const float FallbackRatio = DefaultH / DefaultW;

        /// <summary>The card's height for a given width and source ratio, with BOTH a floor and a CEILING. Drop the
        /// free term and a 9:16 portrait source in a 360-wide mini player asks for 640 DIP of height in a 900-tall
        /// window and the card walks off the bottom. The floor wins the tie, so a very short window still gets a
        /// 135-tall card rather than a degenerate one.</summary>
        public static float FitHeight(float w, float ratio, float viewportH)
        {
            float r = ratio > 0f && float.IsFinite(ratio) ? ratio : FallbackRatio;
            float free = MathF.Max(MinH, viewportH - ReserveBottom - ReserveTop);
            return MathF.Max(MinH, MathF.Min(w * r, free));
        }

        /// <summary>Clamp a placed rect back inside the viewport — what a window resize re-applies so a card parked at
        /// the old bottom-right does not end up off-screen.</summary>
        public static void ClampToViewport(ref float x, ref float y, float w, float h, float viewportW, float viewportH)
        {
            x = MathF.Max(0f, MathF.Min(x, MathF.Max(0f, viewportW - w)));
            y = MathF.Max(0f, MathF.Min(y, MathF.Max(0f, viewportH - h)));
        }

        /// <summary>The anchored resting position: bottom-right, above the player bar. While the card is not yet
        /// user-placed this is re-derived from the viewport every frame, which is what makes it track the corner.</summary>
        public static (float X, float Y) Anchor(float w, float h, float viewportW, float viewportH)
            => (MathF.Max(0f, viewportW - w - 16f), MathF.Max(0f, viewportH - h - ReserveBottom - 16f));
    }

    // ── the mini player's gestures, the scrub preview, the join spinner, the fullscreen entry (stage B) ─────────────
    //
    // 0.2.9 kept these inside `InWindowVideoPip` / `WaveeShell`; stage B lifted them here so `Video.UI.cs` decides
    // nothing. Values are 0.2.9's verbatim (ch 24 W8/W11/W12, `InWindowVideoPip.cs:452-534`, `WaveeShell.cs:793-807`).

    /// <summary>Which edges a resize gesture drags; the anchored edges are the ones NOT named. Two edges at once is a
    /// corner, so one handler covers all eight zones.</summary>
    [Flags]
    public enum PipEdge : byte { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    /// <summary>The in-window mini player's drag, resize, clamp and layout reservation.</summary>
    public static class PipGesture
    {
        /// <summary>The gap kept from every window edge (and above the player bar).</summary>
        public const float Margin = 16f;

        /// <summary>A card may sit no closer than <see cref="Margin"/> to either side.</summary>
        public static float ClampX(float x, float viewportW, float w)
            => Math.Clamp(x, Margin, MathF.Max(Margin, viewportW - w - Margin));

        /// <summary>…and no closer than <see cref="Margin"/> to the top or to the player bar.</summary>
        public static float ClampY(float y, float viewportH, float h)
            => Math.Clamp(y, Margin, MathF.Max(Margin, viewportH - h - Pip.ReserveBottom - Margin));

        /// <summary>The bottom space the page keeps clear: the card's height + the gap while it sits ANCHORED, 0 once the
        /// user has placed it (a deliberately placed card is a free-floating overlay) or when it is not mounted.</summary>
        public static float Reserve(bool mounted, bool placed, float h) => mounted && !placed ? h + Margin : 0f;

        /// <summary>Should the card's HEIGHT follow the content? Always until the user sizes it; after that only when the
        /// content's own shape changes by more than 0.01 (the user owns the width, the content owns the shape).</summary>
        public static bool ShouldRefit(bool userSized, float fittedRatio, float contentRatio)
            => !userSized || (fittedRatio > 0f && MathF.Abs(contentRatio - fittedRatio) > 0.01f);

        /// <summary>One resize sample. The ANCHORED edge is the one not being dragged: growing right/bottom is bounded by
        /// the viewport, growing left/top by the frozen far edge. Floors are <see cref="Pip.MinW"/> × <see cref="Pip.MinH"/>.</summary>
        public static (float X, float Y, float W, float H) Resize(PipEdge edge, float startX, float startY, float startW,
            float startH, float dx, float dy, float viewportW, float viewportH)
        {
            float x = startX, y = startY, w = startW, h = startH;
            float right = startX + startW, bottom = startY + startH;
            if ((edge & PipEdge.Left) != 0)
            {
                w = Math.Clamp(startW - dx, Pip.MinW, MathF.Max(Pip.MinW, right - Margin));
                x = right - w;
            }
            else if ((edge & PipEdge.Right) != 0)
            {
                w = Math.Clamp(startW + dx, Pip.MinW, MathF.Max(Pip.MinW, viewportW - Margin - startX));
            }
            if ((edge & PipEdge.Top) != 0)
            {
                h = Math.Clamp(startH - dy, Pip.MinH, MathF.Max(Pip.MinH, bottom - Margin));
                y = bottom - h;
            }
            else if ((edge & PipEdge.Bottom) != 0)
            {
                h = Math.Clamp(startH + dy, Pip.MinH, MathF.Max(Pip.MinH, viewportH - Margin - Pip.ReserveBottom - startY));
            }
            return (x, y, w, h);
        }
    }

    /// <summary>A scrub PREVIEW (a pointer still down on the transport's rail) is a COARSE seek planned through
    /// <see cref="Playback.Video.SeekPlanner"/>: a buffered keyframe when there is one, the segment start when the grid is
    /// known, and the raw target as a keyframe seek when the host has no index yet. It is never accurate — the commit on
    /// release is.</summary>
    public static class Scrub
    {
        /// <summary>Where a preview lands for <paramref name="plan"/>.</summary>
        public static long PreviewTargetMs(in Playback.Video.SeekPlan plan, long targetMs, long segmentLengthMs)
            => plan.Verb switch
            {
                Playback.Video.SeekVerb.Fetch => segmentLengthMs > 0 ? plan.KeyframeMs : targetMs,
                Playback.Video.SeekVerb.Ride => targetMs,
                _ => plan.KeyframeMs >= 0 ? plan.KeyframeMs : targetMs,
            };

        /// <summary>A preview never asks for decode-to-exact-PTS.</summary>
        public const bool PreviewAccurate = false;
    }

    /// <summary>The poster's loading affordance: the artwork shows at once, the spinner only for a join that outlasts
    /// <see cref="Playback.Video.Budgets.JoiningNoSpinnerMs"/> — a spinner that flashes for 200 ms reports trouble that
    /// did not happen.</summary>
    public static class Joining
    {
        public const int SpinnerDelayMs = Playback.Video.Budgets.JoiningNoSpinnerMs;
        public static bool ShowsSpinner(long elapsedMs) => elapsedMs >= SpinnerDelayMs;
    }

    /// <summary>The fullscreen surface's focus-steal guard. The surface remounts fresh every time it mounts, so only the
    /// shell-lifetime observer can tell "Requested just BECAME Fullscreen" (F11, the menu, the glyph) from "Requested was
    /// already Fullscreen and the RESOLVE is only now catching up" (availability returned after a video-less track).</summary>
    public static class FullscreenEntry
    {
        /// <summary>Did the resolved placement just become fullscreen?</summary>
        public static bool Entered(in PlacementState before, in PlacementState after)
            => PlacementCore.Resolve(after) == SurfacePlacement.Fullscreen && PlacementCore.Resolve(before) != SurfacePlacement.Fullscreen;

        /// <summary>…and was it the user asking (so the surface may take focus)?</summary>
        public static bool UserInitiated(in PlacementState before, in PlacementState after)
            => after.Requested == SurfacePlacement.Fullscreen && before.Requested != SurfacePlacement.Fullscreen;
    }
}
