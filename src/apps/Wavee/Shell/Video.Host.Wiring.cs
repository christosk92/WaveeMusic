// ── Shell/Video.Host.Wiring.cs ─────────────────────────────────────────────────────────────────────────────────────
// the video host's composition and its playback edges: `Video.Install`, the placement → playback post rule, the
// track-boundary availability fold, the size seed, and the host observer leaf. NAMED PARTIAL of Shell/Video.Host.cs
// (the 30 % rule: Video.Host.cs is already 349 lines against its 250)
//
// Role: SHELL
// Owner: K + H (gap batch B7)
// Wave: 4
// Budget: 300 lines
// Spec: ch 24 §7 DATA GAPS 1-4, §9; docs/plans/wavee/wavee-0.3-video-engine-implementation.md §3.4, §6.2 K;
//       gap register G-058, G-141, G-142, G-146, G-148, G-150
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. The five places the placement machine (`Video.State`, this class) and the decode host
// (`Playback.Video`) meet:
//   1. `Install` — the composition root's one call: the resolver tiers, the demotion handler, the engine's log sink,
//      the local-attachment roster, the persisted preferred placement.
//   2. `PlacementPost` — what `State.Commit` tells the reducer (G-141). The reducer's `VideoWanted` is the INTENT, sticky
//      across rows with no video (so the next row with one opens straight on the video host), and every ACTIVATION is
//      re-posted (so a lit badge's click on a row already playing as audio swaps it to video).
//   3. `BoundaryFold` — the row's availability re-folded at every track change and every late catalogue land (G-150):
//      a downgrade always commits; an upgrade commits only at a real track boundary (the no-mid-track-swap rule).
//   4. `NaturalSeed` — the size a surface opens at before a frame exists (G-058): the frame, else the manifest, else
//      the catalogue's kind-99 rendition size, else nothing (16:9).
//   5. `HostObserver` — the leaf that runs the effects: the session mirror (`Playback.Video.Observe`), the live quality
//      ceiling, the fold, the badge-lit prefetch (G-146) and the warm keeper's request (D15: the playing row and the
//      next queued one, held warm while a video surface is wanted, dropped 30 s after it closes).
// Every decision above is a pure function tested by `VideoHostRulesTests`; the leaf only reads signals and forwards.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using InfoBarSeverity = FluentGpu.Controls.InfoBarSeverity;
using Loc = FluentGpu.Localization.Loc;

namespace Wavee;

public static partial class Video
{
    // ── 1. composition ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The loc key of the demotion toast (G-142). New in this batch — `batch-loc/B7.json`.</summary>
    public const string DemotedToastKey = "player.videoUnavailable";

    /// <summary>THE composition call (`App.cs`, after <c>Playback.Boot()</c> and before <c>Shell.InstallUi()</c>). Idempotent.</summary>
    public static void Install()
    {
        Overrides.Attach(Platform.Settings);
        Overrides.FakeClip = Platform.Args.FakeVideo;   // --fake --fake-video: in memory only (never in the roster, so never persisted)
        Overrides.BrokenLink -= OnBrokenLink;
        Overrides.BrokenLink += OnBrokenLink;
        Playback.Video.InstallLog();   // before any warm-up, so its `[video.native]` lines land in the app's log
        Hdcp.Install();                // before the window exists: its first monitor edge (the swapchain's) is the guard's first check
        Playback.Video.OnOverrideFailed = OnOverrideFailed;
        Playback.Video.InstallResolver();   // IN FRONT of the resolver installed before (the modules' tier), never replacing it
        Playback.OnVideoIntent = OnIntent;
        var s = State.Surface.Peek();
        var preferred = PlacementPersistence.LoadPlacement(Platform.Settings.Get(Platform.Keys.VideoPreferredPlacement), PlacementPolicy.Music);
        if (s.Preferred != preferred) State.Commit(s with { Preferred = preferred });
    }

    /// <summary>The reducer decided the video mode (<c>Effects.VideoIntent</c>). UI thread (the drain's Execute). ON opens the
    /// preferred placement — folded first, a controller's switch may land before the row's facts did — unless it carries a
    /// fault, which means "this row has no video; the mode stays for the next one". OFF turns the surface off (a controller's
    /// switch to audio; a load demoted after its retry, G-142). A fault is always said. Every change goes through the ONE
    /// write path, and the placement post it causes is a no-op echo in the reducer.</summary>
    static void OnIntent(bool on, Playback.Fault why)
    {
        Log.Info(State.LogCategory, "video intent on=" + (on ? "true" : "false") + " fault=" + why);
        if (!on) State.TurnOff();
        else if (why == Playback.Fault.None)
        {
            var s = State.Surface.Peek();
            if (!PlacementCore.IsActive(s))
                State.Commit(PlacementCore.OpenAt(UpgradeGate.FoldAvailability(s, true, State.HostCapability.Peek()), s.Preferred));
        }
        if (why != Playback.Fault.None)
            Notify.Say(Loc.Get(DemotedToastKey), InfoBarSeverity.Warning, dedupeKey: "video.demoted");
    }

    /// <summary>An attached file failed to open: skip it for the rest of the session, so the reducer's retry resolves
    /// to the source's own video (or to audio). UI thread.</summary>
    static void OnOverrideFailed(string playableUri, string sourceKey)
    {
        Overrides.Quarantined(playableUri, sourceKey);
        Notify.Say(Loc.Get(Strings.VideoOverride.UnplayableToast), InfoBarSeverity.Warning, dedupeKey: "video.override.unplayable");
    }

    /// <summary>Raised by the resolver's tier-1 walk on an api thread, at most once per session per uri.</summary>
    static void OnBrokenLink(string playableUri)
        => Playback.ToUi(static () => Notify.Say(Loc.Get(Strings.VideoOverride.MissingToast), InfoBarSeverity.Warning,
            dedupeKey: "video.override.missing"));

    /// <summary>G-150: where the user likes to watch survives a restart; whether it is on never does.</summary>
    static void PersistPreferred(SurfacePlacement preferred)
    {
        string stored = PlacementPersistence.SavePlacement(preferred);
        if (stored.Length > 0) Platform.Settings.Set(Platform.Keys.VideoPreferredPlacement, stored);
    }

    public static partial class State
    {
        /// <summary>Fold the current row's availability (G-150). <paramref name="boundary"/>: the row's first ruling, so an
        /// upgrade may commit; otherwise only a downgrade does.</summary>
        public static void FoldForTrack(bool hasVideo, bool boundary)
        {
            var before = Surface.Peek();
            if (BoundaryFold.Commits(in before, hasVideo, HostCapability.Peek(), boundary, out var folded)) Commit(in folded);
        }
    }

    /// <summary>Does the deck's row carry a video the SURFACE offers, from its one source (<c>Playback.DeckVideo</c>)? Songs only:
    /// a video podcast keeps its own story. The availability fold, the placement menu, the player bar and the now-playing face
    /// all read this SAME answer, so the menu's "no video" reason never disagrees with the fold that stamped <c>Available</c>.</summary>
    public static bool DeckHasVideo(in Playback.VideoFacts facts, EntityId id) => facts.HasVideo && id.Kind != EntityKind.Episode;

    // ── 2-4. the pure edges ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a placement commit tells the reducer (G-141).</summary>
    public static class PlacementPost
    {
        /// <summary>The intent the reducer folds into <c>VideoWanted</c>: would a row WITH a video resolve to a surface?</summary>
        public static bool WantsVideo(in PlacementState s, PlacementSet hostCapable)
            => PlacementCore.ResolveWith(s, UpgradeGate.AvailabilityFor(true, hostCapable)) != SurfacePlacement.None;

        /// <summary>Post on the intent's edge, and on every activation (the lit badge's click, a boundary upgrade) — the
        /// reducer re-decides the row's kind on the second.</summary>
        public static bool ShouldPost(in PlacementState before, in PlacementState after, PlacementSet hostCapable, out bool wanted)
        {
            wanted = WantsVideo(after, hostCapable);
            bool activated = !PlacementCore.IsActive(before) && PlacementCore.IsActive(after);
            return activated || wanted != WantsVideo(before, hostCapable);
        }
    }

    /// <summary>The availability fold at a track change or a late catalogue land (G-150).</summary>
    public static class BoundaryFold
    {
        /// <summary>A real boundary: both ids known and different. An empty side is a mid-push glitch, not a change.</summary>
        public static bool IsBoundary(EntityId previous, EntityId next)
            => !previous.IsEmpty && !next.IsEmpty && !previous.Equals(next);

        /// <summary>True with the state to commit; false when nothing changes or the upgrade is withheld mid-track.</summary>
        public static bool Commits(in PlacementState before, bool hasVideo, PlacementSet hostCapable, bool boundary, out PlacementState folded)
        {
            folded = UpgradeGate.FoldAvailability(before, hasVideo, hostCapable);
            if (folded.Equals(before)) return false;
            return !UpgradeGate.DeferUpgrade(before, folded, commitUpgrade: boundary);
        }
    }

    /// <summary>Which fact sized a surface — the `source=` of the always-on fit line.</summary>
    public enum NaturalSource : byte { None, Catalogue, Manifest, Decoder }

    /// <summary>The size a video surface fits before, and while, frames arrive (G-058, ch 24 DATA GAP 1).</summary>
    public static class NaturalSeed
    {
        /// <summary>The most recent fact wins: the decoder's frame, the manifest's top rung, the catalogue's largest kind-99
        /// rendition. All zero ⇒ <see cref="NaturalSource.None"/> and the caller's 16:9.</summary>
        public static NaturalSource Pick(int decoderW, int decoderH, int manifestW, int manifestH, int catalogueW, int catalogueH,
                                         out int width, out int height)
        {
            if (decoderW > 0 && decoderH > 0) { width = decoderW; height = decoderH; return NaturalSource.Decoder; }
            if (manifestW > 0 && manifestH > 0) { width = manifestW; height = manifestH; return NaturalSource.Manifest; }
            if (catalogueW > 0 && catalogueH > 0) { width = catalogueW; height = catalogueH; return NaturalSource.Catalogue; }
            width = 0; height = 0;
            return NaturalSource.None;
        }

        public static string Name(NaturalSource s) => s switch
        {
            NaturalSource.Decoder => "decoder", NaturalSource.Manifest => "manifest", NaturalSource.Catalogue => "track", _ => "none",
        };
    }

    /// <summary>The current track's kind-99 rendition size (0×0 when unknown). Subscribes to the playing row only.</summary>
    static (int W, int H) CurrentCatalogueSize()
    {
        var cur = Playback.Current.Value;
        if (cur.Kind != EntityKind.Track || cur.IsNone || Entities.Current is null) return (0, 0);
        var t = new Track(cur.Slot);
        return t.IsValid ? ((int)t.VideoWidth, (int)t.VideoHeight) : (0, 0);
    }

    // ── 5. the host observer ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mounted once by <see cref="PipLayer"/>; renders nothing. Every effect is signal-edge driven and idle when
    /// nothing moves.</summary>
    sealed class HostObserver : Component
    {
        EntityId _foldedFor;

        public override Element Render()
        {
            // The session mirror: re-run whenever the bound player publishes — the pump just wrote what it learnt.
            UseSignalEffect(static () =>
            {
                if (Playback.Video.Player.Value.Player is { } p)
                {
                    _ = p.State.Value;
                    _ = p.Position.Value;
                    _ = p.NaturalSize.Value;
                    _ = p.Buffering.Value;
                }
                Playback.Video.Observe();
            });

            // The HDCP guard's engagement edge (Video.Hdcp): a protected picture is up on the video host (its first frame landed: the
            // session presents or plays), or it is gone. Up is when the safety net asks Media Foundation whether HDCP took.
            UseSignalEffect(static () =>
            {
                var source = Playback.Video.Source.Value;
                var phase = Playback.Video.Phase.Value;
                bool onVideo = Playback.VideoActive.Value;
                Hdcp.NoteEngaged(onVideo && source is { IsDrm: true }
                                 && phase is Playback.Video.SwitchPhase.Presenting or Playback.Video.SwitchPhase.Playing);
            });

            // The quality ceiling follows the network cost and the metered setting live (G-148).
            UseSignalEffect(static () =>
            {
                _ = Platform.Network.Metered.Value;
                _ = Platform.Network.MeteredVideoMaxHeight.Value;
                Playback.Video.ApplyQualityCaps();
            });

            // The availability fold: the row's facts (Playback.DeckVideo), the host's capability. The placement is PEEKED —
            // the fold writes it. TRI-STATE: a row nobody has ruled on yet keeps what the surface has (no Docked → None flash
            // on a skip while the association is in the air); the FIRST ruling of a row is its boundary, so a late "has video"
            // still opens the surface the reducer is about to swap the row onto; a later change of the same row is not.
            UseSignalEffect(() =>
            {
                EntityId id = Playback.CurrentId.Value;
                _ = State.HostCapability.Value;
                var facts = Playback.DeckVideo.Value;
                if (!facts.Known) return;
                bool boundary = BoundaryFold.IsBoundary(_foldedFor, id);
                if (!id.IsEmpty) _foldedFor = id;
                State.FoldForTrack(DeckHasVideo(in facts, id), boundary);
            });

            // The badge-lit prefetch (§3.1.5) AND the warm keeper (D15), one effect because they read the same facts.
            // The prefetch brings the CURRENT row's video to the schedule's level while it plays as audio — the row
            // already playing on the video host needs none, its load IS the fetch. The keeper is the other half: while a
            // video surface is wanted it holds the content keys of the playing row and the next queued one, which is
            // what makes the second and third switch of a session near-instant and what keeps the native runtime up —
            // while one of them is PROTECTED (F165), and for the playing row alone once the surface is off but a video
            // was asked for this session (F218). The row the video host already owns is not re-warmed (F225).
            // A FOREIGN owner's row is warmed too, its key and the runtime only (no segments, no next row): "Play here" on a
            // video row then finds the native runtime up and the key in its cache instead of paying the whole 1.2-1.4 s
            // bring-up plus a cold challenge (sid=996ef8e4: 1.8-2.1 s to the first frame). The keeper's own rules decide
            // whether it is wanted: the surface on (Beats), or a video asked for this session (WarmsOnIntent).
            UseSignalEffect(static () =>
            {
                EntityRef row = Playback.Current.Value;
                EntityId id = Playback.CurrentId.Value;
                bool onVideoHost = Playback.VideoActive.Value;
                var placement = State.Surface.Value;
                bool metered = Platform.Network.Metered.Value;
                bool local = Playback.OwnerSignal.Value != Playback.Owner.Foreign;   // a controller's row: its key, never its media
                // Audio ALWAYS wins: while a track is opening, the keeper does not touch the api pool (§3.1.4). Reading
                // the phase here is what re-runs this effect — and re-arms the beat — the moment the open lands.
                bool audioBusy = Playback.PhaseSignal.Value == Playback.Phase.Loading;
                if (Entities.Current is { } scope) _ = scope.Edges.Queue.Changed.Value;   // the next queued row moved
                if (Platform.Args.Fake || id.Provider != EntityProvider.Spotify)
                {
                    Playback.Video.KeepWarm(Playback.Video.WarmRequest.Off);
                    return;
                }

                bool videoOn = PlacementPost.WantsVideo(in placement, State.HostCapability.Peek());
                var facts = Playback.DeckVideo.Value;
                bool hasVideo = DeckHasVideo(in facts, id);
                string gid = hasVideo && !facts.ManifestId.IsEmpty ? Entities.Strings.Resolve(facts.ManifestId) : "";
                // A video-capable row PLAYING is also what boots the PlayReady component (the DLL and the CDM's one-time
                // initialisation, Playback.Video.Boot): the keeper's one warm-up path, so a user who only plays songs pays nothing.
                bool playing = Playback.PhaseSignal.Value == Playback.Phase.Playing;
                if (!local)
                {
                    Playback.Video.KeepWarm(new Playback.Video.WarmRequest(
                        videoOn, audioBusy, hasVideo ? id : default, gid, default, "", HostOwns: false, Playing: playing));
                    return;
                }
                NextVideoRow(out EntityId nextId, out string nextGid);
                Playback.Video.KeepWarm(new Playback.Video.WarmRequest(
                    videoOn, audioBusy, hasVideo ? id : default, gid, nextId, nextGid, HostOwns: onVideoHost, Playing: playing));

                if (onVideoHost || !hasVideo) return;
                var already = Playback.Video.PrefetchedLevel(id);
                var input = new Playback.Video.PrefetchInput(
                    HasVideo: true, VideoOn: videoOn, Metered: metered,
                    IsCurrent: true, MsToBoundary: 0, Already: already,
                    ManifestFresh: gid.Length > 0 ? Playback.Video.ManifestMemo.IsFresh(gid) : already != Playback.Video.PrefetchLevel.None);
                var level = Playback.Video.PrefetchSchedule.Decide(in input);
                if (level != Playback.Video.PrefetchLevel.None)
                    Playback.Video.Prefetch(id, gid, level, Playback.Video.PrefetchSchedule.WhyFor(in input), Playback.PositionMs.Peek());
            });

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        /// <summary>How far down "Next up" the keeper looks for a row that carries a video. One key ahead is the promise
        /// ("the next one is instant too"); looking further would pre-fetch for a queue the user has not committed to.</summary>
        const int WarmLookahead = 3;

        /// <summary>The next QUEUED row with a video, and the manifest id its catalogue row carries — the keeper's second
        /// target, so the switch AFTER this one costs no licence round trip either. Empty when there is none.</summary>
        static void NextVideoRow(out EntityId id, out string manifestId)
        {
            id = default;
            manifestId = "";
            if (Entities.Current is null || !Queue.UpNext(out int start, out int length)) return;
            int n = Math.Min(WarmLookahead, length);
            for (int i = start; i < start + n; i++)
            {
                EntityRef r = Queue.RefAt(i);
                if (r.Kind != EntityKind.Track || r.IsNone) continue;
                var t = new Track(r.Slot);
                if (!t.IsValid || !t.HasVideo) continue;
                id = t.Id;
                StringId gid = t.ManifestGidId;
                manifestId = gid.IsEmpty ? "" : Entities.Strings.Resolve(gid);
                return;
            }
        }
    }
}
