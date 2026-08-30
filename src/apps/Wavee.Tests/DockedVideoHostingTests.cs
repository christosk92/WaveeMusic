using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The pure arbitration between the app's TWO docked video hosts — the right rail's ONE card and a module watch page's
/// in-page stage (<see cref="DockedVideoHosting"/>). There is exactly ONE video surface per player
/// (<c>VideoSurfaceRegistry</c>'s <c>OneSurfacePerPlayerGuard</c>), so the whole point of these tests is the structural
/// analogue of <see cref="PlacementCoreTests"/>' single-transport gate: at most one FACE ever mounts, and exactly one
/// mounts whenever the docked placement is resolved. Values only — no signal, no window, no GPU.
///
/// <para>The rail body is NOT a term of that decision any more, and the tests still sweep every
/// <see cref="RailMode"/> to pin that: the rail used to carry two competing faces (a full-bleed cap and a fixed square
/// art tile for Details) and needed the substituted body threaded through the gate to keep them apart. One rail card
/// in every body means the mount answer must be INVARIANT under the body — which is exactly the user-visible defect
/// the change fixes, since a body-dependent answer is what made the same video change shape and width.</para>
/// </summary>
public class DockedVideoHostingTests
{
    // A watch page's entity uri, the thing that is playing, and a second page that is merely parked in the keep-alive
    // cache. Uri-shaped on purpose: these are identifiers compared Ordinal, never display text.
    const string Watch = "wavee:module:youtube:vid_9aH1";
    const string Other = "wavee:module:youtube:vid_ZZZZ";
    const string Song = "spotify:track:4cOdK2wGLETKBW3PvgPWqT";

    static readonly RailMode[] AllModes = (RailMode[])Enum.GetValues(typeof(RailMode));

    static int MountingFaces(SurfacePlacement resolved, string? ownerPageUri,
                             string? activePageUri, string? playingUri)
    {
        int n = 0;
        foreach (var face in DockedVideoHosting.AllFaces)
        {
            // Only the page stage carries an owner; the rail's card has no page of its own.
            string? owner = face == DockedVideoFace.PageStage ? ownerPageUri : null;
            if (DockedVideoHosting.ShouldMount(face, resolved, owner, activePageUri, playingUri)) n++;
        }
        return n;
    }

    // ── the invariant ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE gate: exhaustively over every face, every placement, both navigation alignments (the mounting page
    /// is the attached one / it is parked behind another) and both content alignments (the attached page IS what is
    /// playing / it is not), at most ONE face mounts — and for the ATTACHED page's own instance, exactly one mounts iff
    /// the placement resolved to <see cref="SurfacePlacement.Docked"/>.
    ///
    /// <para>Two counts rather than one, because they are two different statements. The ≤1 bound must hold for EVERY
    /// instance in the tree including parked ones (that is the double-mount the surface guard would catch). The =1
    /// statement is about the attached page's instance specifically: a parked page's stage is correctly false, so its
    /// own count is legitimately zero while the attached page's is one.</para></summary>
    [Fact]
    public void ExactlyOneFaceMounts_ForEveryPlacementAndNavigation()
    {
        string?[] actives = [null, "", Watch];
        string?[] playings = [null, "", Watch, Song];
        string?[] owners = [null, "", Watch, Other];

        foreach (var resolved in PlacementCore.AllPlacements)
        foreach (var active in actives)
        foreach (var playing in playings)
        {
            foreach (var owner in owners)
                Assert.True(MountingFaces(resolved, owner, active, playing) <= 1,
                    $"two faces mounted at once: resolved={resolved} owner={owner ?? "(null)"} " +
                    $"active={active ?? "(null)"} playing={playing ?? "(null)"}");

            // The ATTACHED page's own instance — the one whose OwnerStagePlayable IS the active staged playable.
            int attached = MountingFaces(resolved, active, active, playing);
            Assert.True(attached == (resolved == SurfacePlacement.Docked ? 1 : 0),
                $"expected {(resolved == SurfacePlacement.Docked ? 1 : 0)} mounted face, got {attached}: " +
                $"resolved={resolved} active={active ?? "(null)"} playing={playing ?? "(null)"}");
        }
    }

    /// <summary>The two-keep-alive-pages race, asserted as a VALUE. Both watch pages are alive in the tree; only one is
    /// attached. The parked one cannot re-render to release anything (the reconciler skips frozen and parked
    /// components outright), so its stage must be false purely from the uris it already holds.</summary>
    [Fact]
    public void ParkedPage_NeverMounts()
    {
        // The stage IS hosting (the attached page is the playing item) — and the parked page still mounts nothing.
        Assert.True(DockedVideoHosting.PageStageHosts(Watch, Watch));
        Assert.False(DockedVideoHosting.ShouldMount(DockedVideoFace.PageStage, SurfacePlacement.Docked, Other, Watch, Watch));
        Assert.True(DockedVideoHosting.ShouldMount(DockedVideoFace.PageStage, SurfacePlacement.Docked, Watch, Watch, Watch));

        // And the parked page whose OWN entity is the playing one, while a DIFFERENT page is attached: still false —
        // "is my entity playing" is not the question, "am I the attached page" is.
        Assert.False(DockedVideoHosting.ShouldMount(DockedVideoFace.PageStage, SurfacePlacement.Docked, Watch, Other, Watch));
    }

    // ── the yield ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>When the stage hosts, the rail's card yields — whole. It is the same one surface, and leaving the rail
    /// armed would put two mounted cards on one player.</summary>
    [Fact]
    public void RailCardYields_WhenTheStageHosts()
    {
        Assert.False(DockedVideoHosting.ShouldMount(DockedVideoFace.Cap, SurfacePlacement.Docked, null, Watch, Watch));
        Assert.Equal(DockedVideoHost.PageStage,
            DockedVideoHosting.HostFor(SurfacePlacement.Docked, Watch, Watch));
    }

    /// <summary>When the stage does NOT host — no watch page attached, or the attached page is not the playing item —
    /// the rail is the resting owner and its ONE card mounts.</summary>
    [Fact]
    public void RailCardHosts_WhenTheStageDoesNot()
    {
        foreach (var (active, playing) in new (string?, string?)[] { (null, Song), ("", Song), (Watch, Song), (Watch, null) })
        {
            Assert.Equal(DockedVideoHost.Rail, DockedVideoHosting.HostFor(SurfacePlacement.Docked, active, playing));
            Assert.True(DockedVideoHosting.ShouldMount(DockedVideoFace.Cap, SurfacePlacement.Docked, null, active, playing));
            Assert.False(DockedVideoHosting.ShouldMount(DockedVideoFace.PageStage, SurfacePlacement.Docked, active, active, playing));
        }
    }

    /// <summary>THE new rule, and the user-visible defect it fixes: the rail's card mounts for a resolved Docked
    /// placement REGARDLESS of which body the rail is showing. The rail used to answer this question differently per
    /// body — Details got a fixed square art tile inset 8 DIP per side, every other body got the full-bleed cap — so
    /// the identical 16:9 stream changed both its shape and its width as the user switched bodies. One card, one
    /// answer, in every body; sweeping <see cref="RailVideoCoupling.BodyModeFor"/>'s SUBSTITUTED body too, since that
    /// is the value the rail actually renders.</summary>
    [Fact]
    public void RailCardMounts_InEveryBody_WhenDockedResolves()
    {
        foreach (var mode in AllModes)
        {
            // The body the rail actually RENDERS (chosen mode, or the substitution while the stage hosts) — asserted
            // to be irrelevant to the mount answer in both directions.
            RailMode chosen = RailVideoCoupling.BodyModeFor(mode, stageHostsVideo: false);
            RailMode substituted = RailVideoCoupling.BodyModeFor(mode, stageHostsVideo: true);
            Assert.Equal(mode, chosen);   // no substitution while the rail is the host: the user's body stands

            // No watch page in play: the rail's card mounts, under Details exactly as under Queue/Lyrics/Friends/Video.
            Assert.True(DockedVideoHosting.ShouldMount(DockedVideoFace.Cap, SurfacePlacement.Docked, null, "", Song));
            Assert.True(DockedVideoHosting.ShouldMount(DockedVideoFace.Cap, SurfacePlacement.Docked, null, Watch, Song));

            // ...and while the stage hosts, no body brings it back — including the ones BodyModeFor leaves alone.
            Assert.False(DockedVideoHosting.ShouldMount(DockedVideoFace.Cap, SurfacePlacement.Docked, null, Watch, Watch));
            Assert.True(substituted is RailMode.Queue or RailMode.Lyrics or RailMode.Friends);
        }
    }

    /// <summary>Nothing docked ⇒ nothing docked-mounted, whichever face asks. This is also the FULLSCREEN
    /// collapse-and-return: entering fullscreen resolves away from Docked so every face goes false at once, and exiting
    /// restores exactly the face the derivation names — no face has any memory of having been up.</summary>
    [Fact]
    public void NonDockedPlacement_MountsNothing()
    {
        foreach (var resolved in PlacementCore.AllPlacements)
        {
            if (resolved == SurfacePlacement.Docked) continue;
            foreach (var face in DockedVideoHosting.AllFaces)
            {
                Assert.False(DockedVideoHosting.ShouldMount(face, resolved, Watch, Watch, Watch));
                Assert.False(DockedVideoHosting.ShouldMount(face, resolved, null, "", Song));
            }
            // ...and the host derivation hands the surface straight back to the rail, its resting owner.
            Assert.Equal(DockedVideoHost.Rail, DockedVideoHosting.HostFor(resolved, Watch, Watch));
        }
    }

    // ── the comparison itself ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ordinal, and empty is "no watch page attached" rather than a wildcard. Both halves matter: a
    /// case-insensitive compare on an identifier makes two different entities look like one, and an empty
    /// <c>ActiveStagePlayable</c> matching an empty playing uri would hand the surface to a page that is not there.</summary>
    [Fact]
    public void PageStageHosts_IsOrdinal_AndRejectsEmpty()
    {
        Assert.True(DockedVideoHosting.PageStageHosts(Watch, Watch));

        Assert.False(DockedVideoHosting.PageStageHosts(null, null));
        Assert.False(DockedVideoHosting.PageStageHosts("", ""));
        Assert.False(DockedVideoHosting.PageStageHosts("", null));
        Assert.False(DockedVideoHosting.PageStageHosts(Watch, null));
        Assert.False(DockedVideoHosting.PageStageHosts(Watch, ""));
        Assert.False(DockedVideoHosting.PageStageHosts(null, Watch));
        Assert.False(DockedVideoHosting.PageStageHosts("", Watch));

        // Case is identity here, not presentation.
        Assert.False(DockedVideoHosting.PageStageHosts(Watch, Watch.ToUpperInvariant()));
        Assert.False(DockedVideoHosting.PageStageHosts(Watch.ToUpperInvariant(), Watch));
        Assert.False(DockedVideoHosting.PageStageHosts(Watch, Other));
    }

    /// <summary>Faces belong to hosts statically; the DECISION is <see cref="DockedVideoHosting.HostFor"/>.</summary>
    [Fact]
    public void HostOf_MapsBothFaces()
    {
        Assert.Equal(DockedVideoHost.Rail, DockedVideoHosting.HostOf(DockedVideoFace.Cap));
        Assert.Equal(DockedVideoHost.PageStage, DockedVideoHosting.HostOf(DockedVideoFace.PageStage));

        // Every mounting face belongs to the host the derivation names — the two answers can never disagree.
        foreach (var face in DockedVideoHosting.AllFaces)
        foreach (var (active, playing) in new (string?, string?)[] { (Watch, Watch), (Watch, Song), ("", Song) })
        {
            string? owner = face == DockedVideoFace.PageStage ? active : null;
            if (!DockedVideoHosting.ShouldMount(face, SurfacePlacement.Docked, owner, active, playing)) continue;
            Assert.Equal(DockedVideoHosting.HostFor(SurfacePlacement.Docked, active, playing),
                DockedVideoHosting.HostOf(face));
        }
    }

    // ── the capability bit ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The named regression: a narrow window used to strip the DOCKED bit outright (the rail-fit test was its
    /// only supplier), so a watch page's full-width in-page stage — which needs no rail at all — was demoted to the
    /// floating mini player. The OR keeps the page's stage alive while still demoting the RAIL's cap.</summary>
    [Fact]
    public void DockedHostAvailable_SurvivesANarrowWindowOnAWatchPage()
    {
        // Narrow window, watch page attached and playing → the page supplies the bit on its own.
        Assert.True(DockedVideoHosting.DockedHostAvailable(
            railFits: false, pageStageWouldHost: DockedVideoHosting.PageStageHosts(Watch, Watch)));

        // Narrow window, no watch page → the rail was the only supplier and it cannot fit: demote (unchanged).
        Assert.False(DockedVideoHosting.DockedHostAvailable(
            railFits: false, pageStageWouldHost: DockedVideoHosting.PageStageHosts("", Song)));
        Assert.False(DockedVideoHosting.DockedHostAvailable(
            railFits: false, pageStageWouldHost: DockedVideoHosting.PageStageHosts(Watch, Song)));

        // A rail that fits supplies it regardless of what page is attached.
        Assert.True(DockedVideoHosting.DockedHostAvailable(railFits: true, pageStageWouldHost: false));
        Assert.True(DockedVideoHosting.DockedHostAvailable(railFits: true, pageStageWouldHost: true));
    }

    // ── property test ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fold survives arbitrary placement histories. Deterministic pseudo-random
    /// <see cref="PlacementCommand"/> sequences (fixed seed, so a failure always reproduces) with the navigation and
    /// the playing item churning alongside them: after EVERY step the ≤1 bound holds for every instance, the attached
    /// page's instance count agrees with <see cref="PlacementCore.Resolve"/>, and the host the faces mount for is the
    /// host <see cref="DockedVideoHosting.HostFor"/> names.
    ///
    /// <para>This is the interesting one because the two inputs are INDEPENDENT: placement moves through its own
    /// command ladder while navigation moves through the router, and nothing sequences them. A protocol would have to
    /// order those two streams; a derivation cannot be out of order.</para></summary>
    [Fact]
    public void Property_TheOneSurfaceFoldSurvivesArbitraryHistories()
    {
        var placements = PlacementCore.AllPlacements;
        var sets = new[]
        {
            PlacementSet.None, PlacementSet.Docked, PlacementSet.Floating, PlacementSet.Docked | PlacementSet.Floating,
            PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen,
        };
        var kinds = (PlacementCommandKind[])Enum.GetValues(typeof(PlacementCommandKind));
        string?[] actives = [null, "", Watch, Other];
        string?[] playings = [null, Watch, Other, Song];

        uint rng = 0x0DE_C0DEu;
        uint Next() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }

        for (int seq = 0; seq < 1500; seq++)
        {
            var s = PlacementState.Initial(PlacementPolicy.Video);
            var trail = new List<string>(24);
            for (int step = 0; step < 24; step++)
            {
                var cmd = new PlacementCommand(
                    kinds[Next() % (uint)kinds.Length],
                    placements[Next() % (uint)placements.Length],
                    sets[Next() % (uint)sets.Length]);
                s = PlacementCore.Apply(s, cmd);

                string? active = actives[Next() % (uint)actives.Length];
                string? playing = playings[Next() % (uint)playings.Length];
                var mode = AllModes[Next() % (uint)AllModes.Length];
                trail.Add($"{cmd} @active={active ?? "(null)"} playing={playing ?? "(null)"} mode={mode}");

                var resolved = PlacementCore.Resolve(s);
                string why = string.Join(" → ", trail);

                // ≤1 for EVERY instance in the tree, parked ones included.
                foreach (var owner in new[] { active, Other, null, "" })
                    Assert.True(MountingFaces(resolved, owner, active, playing) <= 1, why);

                // The attached instance agrees with Resolve, exactly.
                int attached = MountingFaces(resolved, active, active, playing);
                Assert.Equal(resolved == SurfacePlacement.Docked ? 1 : 0, attached);

                // ...and it mounts for the host the derivation names. `mode` rides along unread by the gate on
                // purpose: the mount answer must be INVARIANT under the rail body, which is the whole point of the
                // rail having one card instead of a per-body pair.
                var host = DockedVideoHosting.HostFor(resolved, active, playing);
                foreach (var face in DockedVideoHosting.AllFaces)
                {
                    string? owner = face == DockedVideoFace.PageStage ? active : null;
                    if (DockedVideoHosting.ShouldMount(face, resolved, owner, active, playing))
                        Assert.Equal(host, DockedVideoHosting.HostOf(face));
                }
            }
        }
    }
}
