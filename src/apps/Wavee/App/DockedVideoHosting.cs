using System;

namespace Wavee;

/// <summary>The two places a DOCKED video can render (<c>DockedVideoSurface.Face</c>). Both are the SAME placement
/// value (<see cref="SurfacePlacement.Docked"/>) and the SAME single mounted surface — this enum only picks which
/// envelope wraps it, never a second gate on top of <c>PlaybackBridge.VideoPlacementNow</c>.
///
/// <para>There is exactly ONE face per host, which is what makes the ≤1-mounted invariant a VALUE rather than a
/// coincidence of mount sites — see <see cref="DockedVideoHosting.ShouldMount"/>. The rail used to carry a SECOND
/// face, a fixed SQUARE art tile for the Details body, and it was a defect rather than a design: the same 16:9 stream
/// changed shape and width the moment the user switched rail bodies, sitting in fat letterbox bars in Details and
/// full-bleed everywhere else. The video now always follows its own aspect at the rail's width, in every body.</para>
///
/// <para>Declared here (rather than next to the component) for the reason <see cref="RailMode"/> moved into
/// <c>RailVideoCoupling.cs</c>: it is System-only, so it travels with the pure arbitration rules below into the
/// engine-free test project, which cannot compile the engine-bound component the faces belong to.</para></summary>
public enum DockedVideoFace : byte
{
    /// <summary><c>RightRail</c>'s ONE card, pinned above the rail header in EVERY body (Details included): full-bleed
    /// at the rail's width. The default — every mount that does not set <c>DockedVideoSurface.Face</c> keeps this slot.
    /// Its HEIGHT follows the playing content's own aspect (<c>ShellResponsiveLayout.FitDockedVideoHeight</c>),
    /// splitter-overridable.</summary>
    Cap = 0,

    /// <summary>The module watch page's IN-PAGE stage: a full-width 16:9 surface owned by the page itself, the
    /// YouTube-style watch layout. FULL-BLEED — the page draws the envelope, the rounded silhouette and the idle
    /// poster ground, so this face contributes no corners, no border and no height of its own. The one face that is
    /// NOT in the rail, and therefore the one that makes the rail yield (<see cref="DockedVideoHosting"/>).</summary>
    PageStage = 2,
}

/// <summary>WHICH of the two docked hosts owns the app's ONE video surface right now. Derived, never stored: see
/// <see cref="DockedVideoHosting"/> for why a claim/release protocol between the two is not merely redundant but
/// deadlock-prone.</summary>
public enum DockedVideoHost : byte
{
    /// <summary>The right rail's ONE docked card (<see cref="DockedVideoFace.Cap"/>), the same card in every rail
    /// body. The RESTING owner: whenever nothing else is hosting, the rail is.</summary>
    Rail = 0,

    /// <summary>The module watch page's in-page stage (<see cref="DockedVideoFace.PageStage"/>). Owns the surface only
    /// while the ATTACHED page's entity is the very thing that is playing.</summary>
    PageStage = 1,
}

/// <summary>
/// The PURE arbitration between the two DOCKED hosts — the right rail's card and a module watch page's in-page stage.
/// Like its siblings <see cref="PlacementCore"/>, <see cref="RailVideoCoupling"/> and <see cref="VideoUpgradeGate"/> it
/// takes plain values read at the call site and returns a decision — no <c>Signal&lt;T&gt;</c>, no FluentGpu type — so
/// it is verifiable without a GPU or a window.
///
/// <para><b>Why the host is DERIVED, never claimed.</b> There is exactly ONE video surface per player
/// (<c>VideoSurfaceRegistry</c>'s <c>OneSurfacePerPlayerGuard</c>), and two independent mount sites now want it. The
/// obvious design — a claim/release handshake where the page takes the surface on entry and hands it back on exit —
/// cannot work here, and the reason is structural rather than a matter of care: <c>Flow.KeepAlive</c> exit-freezes the
/// outgoing page in the SAME reconcile pass as the route change (<c>Reconciler.SetSubtreeExitFrozen</c>), and
/// <c>RunComponent</c> skips frozen AND parked components. A page that is navigating away, or that is parked in the
/// keep-alive cache, therefore CANNOT re-render to give the surface back — the claim would be stuck on a page that is
/// no longer running, and the guard would trip the moment any other face mounted. So there is no ownership token at
/// all: every mount site asks <see cref="ShouldMount"/> the same question against the same plain values, and the
/// answer is a pure function of navigation + what is playing. A dead page cannot hold what it never held.</para>
///
/// <para><b>The rail is the resting owner.</b> <see cref="HostFor"/> answers <see cref="DockedVideoHost.Rail"/> for
/// every state that is not "a watch page for the playing entity is attached" — including the state where nothing is
/// docked at all. That asymmetry is deliberate: the rail is the placement's permanent home (it is what
/// <see cref="SurfacePlacement.Docked"/> has always meant), and the page stage is a temporary tenant. It also means the
/// rail's own coupling rules (<see cref="RailVideoCoupling"/>) never have to reason about a third "nobody" state.</para>
///
/// <para><b>The rail yields at RENDER time, never by writing state.</b> Nothing here writes <c>ShellUi.Mode</c> or the
/// placement. The rail simply renders a different body (<see cref="RailVideoCoupling.BodyModeFor"/>) and mounts no
/// card, so the instant the stage stops hosting — a navigation, a track change, an unavailability drop — the rail is
/// exactly where the user left it, with no displaced-state memory to restore and no ordering to get wrong.</para>
/// </summary>
public static class DockedVideoHosting
{
    /// <summary>Is the thing the attached page would STAGE the very thing that is playing — i.e. can the in-page stage
    /// host the surface? PLACEMENT-FREE on purpose: <see cref="DockedHostAvailable"/> feeds the availability set that
    /// <c>Resolve</c> consumes, and <see cref="HostFor"/> consumes the RESOLVED placement, so a placement term here
    /// would close the loop into a cycle (availability → resolve → host → availability).
    ///
    /// <para><b>ONE id space, and it is the PLAYABLE uri.</b> <paramref name="activeStagePlayable"/> is
    /// <c>ShellUi.ActiveStagePlayable</c> — what the attached page's stage would host, or <c>""</c> when nothing is
    /// staged, so an empty value can never accidentally equal an empty playing uri. It used to be the page's own
    /// ENTITY uri, which reads as the same thing and is not: a module's entity ids and its playable ids are different
    /// namespaces by design (YouTube's page is <c>video:tRsQsTMvPNg</c>, its playable is <c>tRsQsTMvPNg</c>), and
    /// <paramref name="playingUri"/> is always a PLAYABLE uri — so the two could never match and the stage never
    /// mounted. Ordinal, because these are uris (<c>spotify:track:…</c>, <c>wavee:module:…</c>) and not display text:
    /// a culture-aware or case-insensitive compare on an identifier is how two different entities start looking like
    /// one.</para></summary>
    public static bool PageStageHosts(string? activeStagePlayable, string? playingUri)
        => !string.IsNullOrEmpty(activeStagePlayable)
        && !string.IsNullOrEmpty(playingUri)
        && string.Equals(activeStagePlayable, playingUri, StringComparison.Ordinal);

    /// <summary>THE derived host of the app's one docked video surface. <see cref="DockedVideoHost.Rail"/> whenever
    /// nothing is docked, so the rail is the resting owner and callers never need a "nobody" case.</summary>
    /// <param name="resolved"><c>PlaybackBridge.VideoPlacementNow()</c> — the RESOLVED placement, never
    /// <c>Requested</c>: a dock request that resolved away (no availability) hosts nothing anywhere.</param>
    public static DockedVideoHost HostFor(SurfacePlacement resolved, string? activeStagePlayable, string? playingUri)
        => resolved == SurfacePlacement.Docked && PageStageHosts(activeStagePlayable, playingUri)
            ? DockedVideoHost.PageStage
            : DockedVideoHost.Rail;

    /// <summary>Which host a face BELONGS to. Static structure (which envelope is where), not a decision — the
    /// decision is <see cref="HostFor"/>.</summary>
    public static DockedVideoHost HostOf(DockedVideoFace face)
        => face == DockedVideoFace.PageStage ? DockedVideoHost.PageStage : DockedVideoHost.Rail;

    /// <summary>The ONE gate every docked mount site calls. At most one face is ever true, and exactly one is true iff
    /// the docked placement is resolved — the structural analogue of <c>PlacementCore.SingleTransportInvariant</c>, and
    /// what keeps <c>OneSurfacePerPlayerGuard</c> from ever having anything to catch.
    ///
    /// <list type="bullet">
    /// <item>Nothing mounts unless <paramref name="resolved"/> is <see cref="SurfacePlacement.Docked"/> — the ONE
    /// placement value, never a standalone flag. That is also the fullscreen collapse-and-return: entering fullscreen
    /// resolves away from Docked, every docked face goes false, and exiting restores exactly the face that was up.</item>
    /// <item>The <see cref="DockedVideoFace.PageStage"/> face additionally requires
    /// <paramref name="ownerStagePlayable"/> == <paramref name="activeStagePlayable"/>. That term is the whole
    /// parked-page discriminator: TWO keep-alive'd watch pages can be alive in the tree at once, and only the ATTACHED
    /// one wrote the active value — so the parked one's stage is false without the parked page having to (being able
    /// to) re-render and say so. It still discriminates now that both sides are PLAYABLE uris rather than page uris,
    /// including the degenerate case of two pages for the SAME video: they would carry equal owner values, but only
    /// one of them is attached and only the attached one is ever mounted-and-rendering — the parked twin's stage node
    /// is inside a parked subtree, so "both true" is not reachable, and even if it were, both would be pointing the
    /// one surface at the one playing item rather than at two different things.</item>
    /// <item>The RAIL face requires that the stage is NOT hosting. It passes <c>null</c> for
    /// <paramref name="ownerStagePlayable"/>: it has no page of its own, it lives in the shell.</item>
    /// </list>
    ///
    /// <para><b>The ≤1 bound holds unconditionally, as a VALUE.</b> No rail-body term is needed — and none is
    /// accepted — because the rail has exactly ONE card now, mounted in every body. It used to have two competing
    /// faces (a full-bleed cap and a fixed square art tile for Details), and separating them meant threading the
    /// substituted rail body through this gate purely so that "two faces are never both true" was a value rather than
    /// a coincidence of which <c>RightRail</c> arm happened to mount which. With one rail face and one page face the
    /// two questions are disjoint by construction: the stage hosts, or the rail does.</para></summary>
    public static bool ShouldMount(DockedVideoFace face, SurfacePlacement resolved,
                                   string? ownerStagePlayable, string? activeStagePlayable, string? playingUri)
    {
        if (resolved != SurfacePlacement.Docked) return false;
        bool stage = PageStageHosts(activeStagePlayable, playingUri);
        if (face == DockedVideoFace.PageStage)
            return stage
                && !string.IsNullOrEmpty(ownerStagePlayable)
                && string.Equals(ownerStagePlayable, activeStagePlayable, StringComparison.Ordinal);
        return !stage;                                             // the rail's ONE card; the rail yields whole
    }

    /// <summary>Is the DOCKED capability bit available at all right now — the input <c>WaveeShell</c> folds into
    /// <c>PlaybackBridge.HostPlacementCapability</c>, which <c>VideoUpgradeGate.AvailabilityFor</c> intersects.
    ///
    /// <para>TWO INDEPENDENT SUPPLIERS, one OR. The rail can host a docked card only when the rail FITS (a docked card
    /// needs exactly the room the rail needs). A watch page can ALWAYS host one: its stage is full-width page content
    /// and needs no rail at all. Before this OR the bit was the rail-fit test alone, so narrowing the window demoted a
    /// watch page's in-page video to the floating mini player — an overlay dismissable over a page whose whole purpose
    /// was to show that video. The rail's own cap is still demoted by a narrow window, because that half of the OR is
    /// unchanged.</para></summary>
    public static bool DockedHostAvailable(bool railFits, bool pageStageWouldHost)
        => railFits || pageStageWouldHost;

    /// <summary>Every value <see cref="DockedVideoFace"/> can take — the mount gate's domain, so the ≤1 invariant can
    /// be enumerated without reflection (this file stays <see cref="System"/>-only, the
    /// <c>PlacementCore.AllPlacements</c> precedent).</summary>
    public static readonly DockedVideoFace[] AllFaces =
    [
        DockedVideoFace.Cap, DockedVideoFace.PageStage,
    ];
}
