using System;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee.Features.Video;

/// <summary>
/// The DOCKED music-video surface — the third face of the placement ladder, and the whole reason it exists: video
/// that simply LIVES in the app while the user browses, at zero commitment (no OS window, no overlay to dismiss).
/// This is <see cref="InWindowVideoPip"/> reduced BY SUBTRACTION, not a fresh design — every semantic that survives
/// below is the mini player's own, carried over verbatim:
///
/// <list type="bullet">
/// <item>The mount gate — the ONE placement value, never a standalone flag. It is now
///   <see cref="DockedVideoHosting.ShouldMount"/> rather than a bare <see cref="PlaybackBridge.VideoPlacementNow"/>
///   test, because <see cref="SurfacePlacement.Docked"/> has TWO hosts (this rail card and a module watch page's
///   in-page stage) and only one of them shows the app's one surface at a time. Still one gate, still derived, still
///   no claim/release handshake — see that class for why a handshake would deadlock against a parked page.</item>
/// <item>The <c>UseSignalEffect</c> reality report — <see cref="PlaybackBridge.SetVideoSurfaceLive"/> tells the model
///   whether THIS surface is actually mounted, scoped to Docked only (the mirror of the PiP's Floating report).</item>
/// <item><see cref="BuildVideoArea"/>'s three-way branch: a live stage when a player exists, a Loading overlay stacked
///   over it while the resolved source is still null (a player must never stop pumping just because a manifest/DRM
///   round-trip is in flight), and a dimmed-artwork poster + spinner when there is no player at all.</item>
/// <item>The hover-reveal chrome idiom: the top scrim strip is <c>Opacity = 0, HoverOpacity = 1</c> and the card earns
///   HOVER-CONTAINER status with a no-op <c>OnPointerExit</c> (the TrackRow / PiP idiom) — one pointer registration,
///   the engine's hover cascade does the rest, no signal and no re-render. The three-glyph strip is placement chrome;
///   the TRANSPORT is the global 72-DIP player bar, which owns it for every in-window placement
///   (<see cref="PlacementCore.TransportOwnerFor"/>) — so both faces pass
///   <see cref="MediaPlayerElement.SuppressTransport"/> and the card never stacks a second scrub row above the bar.
///   Both faces still mount the element's More (⋯) affordances (Aspect ratio + the placement ladder).</item>
/// </list>
///
/// <para><b>What is gone, and why.</b> A docked card is pinned inline layout, not a free-floating overlay: there is
/// no <c>_x/_y/_w/_h/_placed</c>, no bound <c>Transform</c>, no <c>Clamp*</c>/<c>Default*</c> anchor math, none of the
/// eight resize bands or <see cref="Wavee.Features.Video.InWindowVideoPip"/>'s <c>PipResizeEdge</c>, no 2D drag
/// gesture or the node bookkeeping they needed, no <c>VideoPipRect</c> persistence, no
/// <see cref="PlaybackBridge.FloatingSurfaceReserve"/> (a docked card reserves nothing — it costs real flex space, the
/// rail's own layout already accounts for it), no <see cref="Elevation.Flyout"/> shadow (docked is a content-layer
/// rung, never an elevated card — <c>RightRail.cs</c> states the identical rule for the rail itself), no pass-through
/// overlay wrapper of its own (this card is a normal flex child, not a top-Z layer), and no viewport subscription
/// (nothing here anchors to a corner). The Cap face's HEIGHT is the one exception: at rest it FOLLOWS THE CONTENT —
/// <c>ShellResponsiveLayout.FitDockedVideoHeight</c> of the player's <c>NaturalSize</c> at the rail's width, so a video
/// fills the card edge to edge rather than sitting in letterbox bars, in EVERY rail body — and <c>RightRail</c>
/// overlays the house <c>Splitter</c> on the card's bottom edge so the user can override that fit for the source that
/// is playing. Position and width still move only through the placement ladder.</para>
///
/// <para><b>Never builds a player.</b> <see cref="PlaybackBridge.VideoPlayer"/> is presented, never constructed — the
/// same ownership inversion <see cref="InWindowVideoPip"/> and the pop-out window already rely on. Building our own
/// player here is exactly the mistake that restarts playback from 0 on every placement move.</para>
///
/// <para><b>Park-but-keep-pumping under immersive lyrics (B15).</b> <see cref="MediaPlayerElement"/> exposes no public
/// "are you active" prop — its OWN <c>_isActive</c> field (<c>MediaPlayerElement.cs</c> around the <c>PumpNow</c> park
/// check) is populated by the hooks-level <c>UseIsActive()</c>, which AND-folds the ambient
/// <see cref="Activation.IsActive"/> window-visibility signal with the component's own KeepAlive-parked state — there
/// is no per-instance settable field to assign. <see cref="Activation.IsActive"/> IS a real, overridable
/// <c>Context&lt;T&gt;</c> though (the same <c>Ctx.Provide</c> mechanism as any other), so this is the one lever that
/// actually exists: <see cref="_activeGate"/> re-derives the SAME window-visibility read (via <c>UseContext</c>, so a
/// minimized window still parks this surface exactly as it would anywhere else) AND-ed with "immersive lyrics is not
/// covering the rail", and re-provides it just for the <see cref="MediaPlayerElement"/> subtree below. A parked,
/// non-decorative element still calls <c>PumpVideo</c> (only <c>SetVisible(false)</c> is skipped-early), so MF keeps
/// advancing and the video picks up mid-song instead of restarting when immersive lyrics closes.</para>
///
/// <para><b>No <see cref="LayoutTransition"/>, ever, on this node or any ancestor added here.</b> The video composites
/// as a passive hole a DESCENDANT erases against the real back buffer (<c>DrawOp.DrawVideo</c>, a DestOut punch). An
/// ancestor <c>TransitionChannels.Opacity</c> multiplies straight into <c>DrawVideoCmd.Opacity</c> — a washed-out,
/// see-through video with the page bleeding through. An ancestor blur/edge-fade/opacity-GROUP pushes an offscreen RT —
/// the punch never reaches the real back buffer from inside one, so THE HOLE VANISHES ENTIRELY, silently and totally.
/// That is also exactly why this card must never be scrolled inside <c>NowPlayingPanel</c>'s
/// <c>ScrollView(...) with { AutoEdgeFade = true }</c> — see the docked-video design's §1 for the full three-reason
/// case. The rail's own 300ms <c>TranslateX</c> slide is the only motion this card ever rides, for free, because a
/// translate composes on the <c>AbsoluteRect</c> the punch already reads from — nobody needs to animate the hole for
/// the hole to move correctly.</para>
///
/// <para><b>Two faces, one card (<see cref="Face"/>).</b> <see cref="DockedVideoFace.Cap"/> is the RAIL's one card: a
/// full-bleed rail-width tile whose height follows the playing content's own aspect (splitter-overridable, clamped to
/// the rail's floor/ceiling), mounted in every rail body including Details.
/// <see cref="DockedVideoFace.PageStage"/> is the module watch page's in-page stage: the same card again, FULL-BLEED,
/// with the PAGE owning the envelope (the 16:9 box, the rounded silhouette and the idle <see cref="PosterGround"/>
/// ground under it) so the idle→live swap shows exactly ONE cross-fade — the element's own poster motion — instead of
/// two layers fading past each other. BOTH faces mount the stock transport; the ONE gate is
/// <c>PlaybackBridge.TransportOwnerNow</c>, and the faces are mutually exclusive by VALUE
/// (<see cref="DockedVideoHosting.ShouldMount"/>), not merely by which site happened to mount them. See
/// <see cref="Render"/>'s tail for the geometry split.</para>
///
/// <para><b>The square is gone, deliberately.</b> The rail carried a THIRD face until now — an Art-tile hero that
/// forced the card into a fixed square inset inside the Details body — so the identical 16:9 stream sat in fat
/// letterbox bars in Details and full-bleed at the rail's width in every other body, silently changing shape and
/// width as the user switched bodies. The video always follows its own aspect at the rail's width now; there is no
/// per-body geometry left to diverge, and the reflow the square was protecting against does not arise because the
/// card is a pinned <c>Shrink=0f</c> sibling of the scrolled body rather than the scroller's first child.</para>
/// </summary>
sealed class DockedVideoSurface : Component
{
    const float ScrimH = 30f;      // the hover-revealed top strip (three glyphs, right-aligned)
    const float GlyphBox = 24f;    // each glyph's square hit target, the InWindowVideoPip close-button rung
    const float ChromeFadeMs = WaveeMotion.Fast;

    /// <summary>Where does this card live? <see cref="DockedVideoFace.Cap"/> is the RAIL's one full-bleed slot, pinned
    /// above the header in every rail body; <see cref="DockedVideoFace.PageStage"/> is a module watch page's in-page
    /// stage. Default is <see cref="DockedVideoFace.Cap"/>, so the rail's two mount sites (the Details arm's pinned
    /// hero and every other arm's cap slot) both take it without saying so — the watch page is the one caller that
    /// sets a face at all.</summary>
    public DockedVideoFace Face { get; init; }

    /// <summary>The PLAYABLE uri the page that mounted THIS card would stage — the same id space as
    /// <c>ShellUi.ActiveStagePlayable</c> and as <c>PlaybackBridge.CurrentTrack.Uri</c>, and
    /// <see cref="DockedVideoFace.PageStage"/> only; the two rail faces leave it null because they have no page of
    /// their own, they live in the shell.
    ///
    /// <para>This is the PARKED-PAGE discriminator, and it is the whole reason the host is derived rather than claimed.
    /// Two keep-alive'd watch pages can be alive in the tree at once and only ONE of them is attached; a parked page —
    /// or one exit-frozen mid-navigation — is skipped by <c>RunComponent</c> entirely, so it can never re-render to
    /// hand a claimed surface back. Comparing its OWN value against the ACTIVE one makes its stage false without it
    /// having to run at all. Frozen at mount, per the component-props contract: the playable a watch page stages never
    /// changes without a remount (the route IS its key).</para></summary>
    public string? OwnerStagePlayable { get; init; }

    /// <summary>The Activation.IsActive OVERRIDE for this card's own <see cref="MediaPlayerElement"/> — see the class
    /// doc's "park-but-keep-pumping" section for why this, and not an invented prop, is the real lever. A stable
    /// instance (never reassigned) is load-bearing: <c>Ctx.Provide</c> only re-notifies existing subscribers when THIS
    /// signal's <c>.Value</c> changes, not when a fresh instance replaces it, and <c>UseIsActive()</c> resolves the
    /// provided instance once and subscribes to ITS value stream for the life of the element.</summary>
    readonly Signal<bool> _activeGate = new(true);

    /// <summary>The <c>PopOutVideoSource.Key</c> the cap height was last fitted for (Cap face only). A change means a
    /// NEW source, which is what ends the previous splitter drag's override — see the height-fit effect in
    /// <see cref="Render"/>.</summary>
    string? _fittedFor;

    /// <summary>The ALWAYS-ON fit report (no env switch) — the four numbers that decide whether the Cap card is the
    /// content's own shape or a letterboxing box. Value-gated on the whole tuple so the effect can run freely.</summary>
    static readonly WaveeLogger FitLog = new(WaveeLog.Instance, "video");
    (string Key, float RailW, int Nw, int Nh, float H, bool Pinned) _loggedFit;
    (VideoAspectMode Mode, double Custom, TransportOwner Owner) _loggedPolicy = ((VideoAspectMode)255, -1, (TransportOwner)255);

    /// <summary>Dedupe for the host-arbitration line: every TERM of the mount decision, so the line fires when any one
    /// of them moves and stays silent while the picture simply keeps playing where it is.</summary>
    (SurfacePlacement Resolved, string? Playing, string Active, string? Owner, bool Mounts) _loggedHost =
        ((SurfacePlacement)255, " ", " ", " ", false);

    /// <summary>A module playable uri is 60+ characters of base64 that differs from its neighbours only in the tail, so
    /// a log line carrying three of them in full is unreadable exactly where it has to be read. Keep the tail.</summary>
    static string Show(string? uri)
        => string.IsNullOrEmpty(uri) ? "(none)" : uri.Length <= 24 ? uri : "…" + uri[^24..];

    void LogFit(string key, float railW, SizeI natural, float height, bool pinned)
    {
        var now = (key, railW, natural.Width, natural.Height, height, pinned);
        if (now == _loggedFit) return;
        _loggedFit = now;
        FitLog.Info($"docked cap fit face={Face} rail={railW:0.#} natural={natural.Width}x{natural.Height} " +
                    $"height={height:0.##} pinned={pinned} key={(key.Length == 0 ? "(none)" : key)}");
    }

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var ui = UseContext(ShellUi.Slot);
        var svc = UseContext(Services.Slot);   // before the null-guard: hook order must not shift when context arrives
        if (b is null || ui is null) return new BoxEl();

        // The ambient window-visibility signal, read the SAME way UseIsActive reads it, so folding it back into
        // _activeGate below does not regress "a minimized window stops pumping" for this one surface — only the
        // "immersive lyrics is up" term is new.
        var windowVisible = UseContext(Activation.IsActive);
        UseSignalEffect(() =>
            _activeGate.Value = (windowVisible is null || windowVisible.Value) && !ui.ImmersiveLyrics.Value);

        // ── the card follows the CONTENT'S aspect ────────────────────────────────────────────────────────────────
        // The Cap face is full-bleed at the rail's width, so its HEIGHT is the only thing that decides whether a video
        // fills it or sits in Tok.MediaLetterbox bars. Sizing it purely from the rail's persisted/dragged height made
        // letterboxing the DEFAULT for anything that was not exactly as tall as whatever was left there — a 16:9
        // YouTube stream in a taller card showed bars above and below. So at rest the height IS the content fit
        // (16:9 until the player reports a natural size, so nothing flashes at the wrong shape), and an explicit
        // splitter drag pins it — for THIS source only, because a decision about one video is not a standing one.
        // Written from an effect, never during render; the PageStage face early-returns (the PAGE owns that height).
        // FitDockedVideoHeight(railW, ...) assumes the card's WIDTH is the rail's width, and that assumption is now
        // unconditional for the rail: Cap is the rail's only face, mounted in every body, always full-bleed.
        UseSignalEffect(() =>
        {
            string sourceKey = b.PopOutVideoSource.Value?.Key ?? "";      // subscribe → a new source re-fits
            var natural = b.VideoPlayer.Value.Player?.NaturalSize.Value ?? default;   // subscribe → fit when MF reports
            float railW = ui.RailWidth.Value;                             // subscribe → re-fit as the rail resizes
            if (Face != DockedVideoFace.Cap) return;
            if (!string.Equals(sourceKey, _fittedFor, StringComparison.Ordinal))
            {
                _fittedFor = sourceKey;
                if (ui.DockedVideoHeightPinned.Peek()) ui.DockedVideoHeightPinned.Value = false;
            }
            if (ui.DockedVideoHeightPinned.Peek()) { LogFit(sourceKey, railW, natural, ui.DockedVideoHeight.Peek(), true); return; }
            float fitted = ShellResponsiveLayout.FitDockedVideoHeight(railW, natural.Width, natural.Height);
            ui.DockedVideoHeight.Value = fitted;
            LogFit(sourceKey, railW, natural, fitted, false);
        });

        // ALWAYS-ON aspect/transport report (no env switch): the two policy values whose effect on this card is
        // otherwise only visible as pixels — the aspect mode the element is driven with, and who owns the transport
        // (which is what decides whether the element mounts its hover chrome at all).
        UseSignalEffect(() =>
        {
            var mode = b.VideoAspectPolicy.Value;
            double custom = b.VideoCustomAspectRatio.Value;
            var owner = b.TransportOwnerNow.Value;
            var now = (mode, custom, owner);
            if (now == _loggedPolicy) return;
            _loggedPolicy = now;
            FitLog.Info($"docked policy face={Face} aspect={mode} custom={custom:0.###} transportOwner={owner} " +
                        $"transportSuppressed={owner != TransportOwner.Docked}");
        });

        // ALWAYS-ON host-arbitration report (no env switch), for the same reason as the policy line above: which of the
        // two docked hosts owns the one surface is a decision with NO visible output of its own — you see only where the
        // picture ended up, and "the rail kept it" looks identical whether the page never claimed it, claimed it with the
        // wrong id, or was correctly outranked. That ambiguity cost a full debugging cycle when ActiveStagePlayable was
        // still carrying the page ENTITY uri (`video:x`) while CurrentTrack carried the PLAYABLE uri (`x`), a mismatch
        // that no pixel and no test could show. Logging every TERM of the decision — not just its outcome — is what makes
        // that class of bug readable straight from the log.
        UseSignalEffect(() =>
        {
            var resolved = b.VideoPlacementNow();
            string? playing = b.CurrentTrack.Value?.Uri;
            string active = ui.ActiveStagePlayable.Value;
            bool mounts = DockedVideoHosting.ShouldMount(Face, resolved, OwnerStagePlayable, active, playing);
            var now = (resolved, playing, active, OwnerStagePlayable, mounts);
            if (now == _loggedHost) return;
            _loggedHost = now;
            FitLog.Info($"docked host face={Face} mounts={mounts} placement={resolved} " +
                        $"stageHosts={DockedVideoHosting.PageStageHosts(active, playing)} " +
                        $"owner={Show(OwnerStagePlayable)} active={Show(active)} playing={Show(playing)}");
        });

        // ── THE mount decision, derived once and used twice ──────────────────────────────────────────────────────
        // There are now TWO docked hosts for the app's ONE video surface — this rail card and a module watch page's
        // in-page stage — so "the placement resolved to Docked" is no longer the same question as "THIS face is the
        // one showing it". DockedVideoHosting.ShouldMount is that one question, asked identically by every face: at
        // most one face is ever true, and exactly one is true iff Docked resolved.
        //
        // No rail-body term: the rail has ONE card now, mounted in every body, so "the stage hosts" is the whole
        // arbitration. The PageStage face never reads rail state at all either — its own staged playable vs the active
        // one is what decides it, in the ONE id space both the signal and CurrentTrack.Uri speak: the PLAYABLE uri.
        string? playingUri = b.CurrentTrack.Value?.Uri;             // subscribe → the stage's claim follows the playing item
        string activeStage = ui.ActiveStagePlayable.Value;          // subscribe → and follows navigation
        bool mount = DockedVideoHosting.ShouldMount(Face, b.VideoPlacementNow(),
            OwnerStagePlayable, activeStage, playingUri);

        // Reality + reports, scoped to Docked only (the mirror of InWindowVideoPip's Floating report) — no layout
        // reservation to publish: a docked card is inline flex, not a free-floating overlay reserving space nobody
        // else can see coming. It reports the DERIVED mount, never the placement alone: a rail face that has YIELDED
        // to the page stage is not mounted, and a placement-only report would swear it was — a lie the model would act
        // on, and the exact shape OneSurfacePerPlayerGuard exists to catch.
        UseSignalEffect(() => b.SetVideoSurfaceLive(SurfacePlacement.Docked, mount));
        // Unmount discipline: if this whole surface goes away (logout / shell swap) while still reporting live, take
        // the report back — the model must not believe a card is mounted that no longer exists.
        UseEffect(() => () => b.SetVideoSurfaceLive(SurfacePlacement.Docked, false), DepKey.Empty);

        // Mount/unmount as the derivation above changes. Every host embeds this component UNCONDITIONALLY — RightRail
        // in its Details arm's pinned-hero slot and in its every-other-body cap slot (the same one Cap face either
        // way), the watch page in its stage — and THIS gate is what makes it invisible (and Shrink=0f collapsed, so
        // nothing reflows) the moment the video is anywhere else, or the moment the other face owns it.
        if (!mount) return new BoxEl();

        void EnterFullscreen()
        {
            Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
            b.ShowVideoAt(SurfacePlacement.Fullscreen);
        }

        // The interactive video card ITSELF — video area + hover chrome, ZStack-overlaid — is shared VERBATIM between
        // both faces (the class doc's "two faces, one card" paragraph): only what wraps it, and at what height,
        // differs below. Declared as BoxEl (not Element) so the `with` expressions below can reach BoxEl-only members
        // (Corners, ZStack, ...) — Element itself carries none of them.
        //
        // NO Corners, NO border: both surviving faces are FULL-BLEED, so the silhouette belongs to whoever wraps the
        // card (the rail clips its own rounded top-left; the watch page draws its stage's). The square art tile was
        // the one face that carried a rounded 1px-stroked envelope of its own, and it is gone.
        BoxEl card = new BoxEl
        {
            ZStack = true, ClipToBounds = true,
            // NO Shadow: see the class doc — docked is a content-layer rung, never an elevated card.
            // NO Layout/Enter/Exit transition of any kind — see the class doc's motion paragraph. This is not an
            // oversight to "fix" later; adding one here is exactly the mistake that erases or washes out the hole.
            OnPointerExit = static () => { },   // hover-container registration only, the TrackRow/PiP idiom
            OnKeyDown = e =>
            {
                // Space = play/pause, mirroring MediaPlayerElement.HandleKey. Escape is deliberately NOT mirrored:
                // HandleKey's Escape case only fires `when IsFullscreenPresentation`, which this face never is — the
                // fullscreen surface (a separate, later phase) owns Escape for real.
                if (e.KeyCode != Keys.Space) return;
                e.Handled = true;
                if (b.VideoPlayer.Peek().Player is not { } p) return;
                if (p.IsPlayRequested.Peek()) _ = b.Player.PauseAsync(); else _ = b.Player.ResumeAsync();
            },
            Focusable = true,
            Children = [ BuildVideoArea(b, EnterFullscreen, svc?.Settings), BuildChrome(b, EnterFullscreen) ],
        };

        if (Face == DockedVideoFace.PageStage)
        {
            // Watch-page stage: FULL-BLEED. The page owns the envelope — the 16:9 aspect box, the rounded silhouette,
            // the idle poster ground beneath it (PosterGround, so the idle→live swap is ONE cross-fade, the element's
            // own PosterMotion, rather than two layers fading past each other) — so this face contributes no corners,
            // no border and no height of its own. Grow=1f/MinHeight=0f is the whole geometry: fill whatever the page
            // reserved. It must never touch ShellUi.DockedVideoHeight either; that is the RAIL's cap, and the
            // height-fit effect above already early-returns for every face but Cap.
            return card with { Grow = 1f, MinHeight = 0f, Fill = Tok.MediaLetterbox };
        }

        // Cap face — the RAIL's one card, in every body: full-bleed at the rail's width (the parent clips the top-left
        // radius). Height is the SAME FloatSignal RightRail's wrapper, the vertical splitter and the content-fit effect
        // above all write — a declared size, not Grow=1 inside a NaN-height ZStack (that measured as 0 once
        // AspectRatio came off). Stretch stays Uniform (Fit): with the height fitted to the content there are no bars
        // to fit INSIDE, and Crop remains a More-menu click for when the user has deliberately grown the tile past the
        // content's own shape.
        return card with
        {
            Shrink = 0f, MinWidth = 0f,
            Height = ui.DockedVideoHeight,
            Fill = Tok.MediaLetterbox,
        };
    }

    // ── the video area — mirrors InWindowVideoPip.BuildVideoArea's three-way branch, built directly against
    // MediaPlayerElement (not the shared PopOutVideoStage: both faces want the stock transport + a custom poster +
    // the fullscreen delegate). ──────────────────────────────────────────────────────────────────────────────────
    Element BuildVideoArea(PlaybackBridge b, Action enterFullscreen, IAppSettings? settings)
    {
        var src = b.PopOutVideoSource.Value;                          // subscribe → remount the stage on a source change
        var binding = b.VideoPlayer.Value;                            // subscribe → poster ↔ hole
        var track = b.CurrentTrack.Value;
        bool mount = VideoSurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
        if (mount && binding.Player is { } player)
        {
            // ONE transport per session. The docked card shares the window with the global 72-DIP player bar, and
            // PlacementCore.TransportOwnerFor(Docked) hands the transport to the BAR — full-width, always visible,
            // never scrolled away — so the card SUPPRESSES its own rather than stacking a second scrub row 30 DIP above
            // it. Read through the ONE derived signal, never a local bool: two independent visibility flags is exactly
            // how the fullscreen surface and the bar ended up rendering both at once.
            bool suppress = b.TransportOwnerNow.Value != TransportOwner.Docked;
            string stageKey = src?.Key ?? ("gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Element element = Embed.Comp(() => new MediaPlayerElement
            {
                Player = player,
                PlayRequested = () => _ = b.Player.ResumeAsync(),
                PauseRequested = () => _ = b.Player.PauseAsync(),
                // The seek MODE travels with the target (scrub-in-flight = Keyframe, commit = Accurate); dropping it
                // forced every seek down the accurate path.
                SeekRequested = (target, mode) => _ = b.Player.SeekAsync((long)target.TotalMilliseconds, mode),
                Stretch = MediaStretch.Uniform,            // house Fit; Crop/Stretch live in the transport More menu
                AspectMode = b.VideoAspectPolicy,
                CustomAspectRatio = b.VideoCustomAspectRatio,
                AspectModeChanged = b.SetVideoAspect,
                CornerRadius = 0f,   // both faces are full-bleed; the rail / the page clips its own silhouette
                // ONE gate, and it is the derived owner signal below — never a second per-face flag. A hard-false
                // `AreTransportControlsEnabled` here is what once made the Details hero a picture with no controls:
                // MediaPlayerElement only adds its chrome layer when `AreTransportControlsEnabled &&
                // !SuppressTransport`. The chrome is an auto-hiding ZStack OVERLAY pinned to the card's bottom edge,
                // so it reflows nothing, and the single transport per window follows from
                // PlacementCore.TransportOwnerFor alone.
                AreTransportControlsEnabled = true,
                SuppressTransport = suppress,
                ShowLetterboxBars = true,
                IsDecorative = false,                      // MUST stay false: decorative skips the pump while parked
                PosterContent = Poster(track),
                // The transport's More button, right-click and the Menu key all open this same complete menu.
                MoreMenuItems = () => VideoPlacementMenu.Items(b, settings, includeFullscreen: false),
                // E3: F11 / F / the transport fullscreen button / the ⋯ Fullscreen row delegate to us instead of
                // opening MediaPlayerElement's own modal overlay fullscreen.
                FullscreenRequested = enterFullscreen,
            }) with { Key = "dockstage:" + stageKey + (suppress ? ":t0" : ":t1") };
            // The Activation.IsActive override lives HERE, tight around the element that actually reads it — see the
            // class doc's park-but-keep-pumping paragraph for why this Ctx.Provide, and not a settable prop, is real.
            Element stage = Ctx.Provide<IReadSignal<bool>?>(FluentGpu.Hooks.Activation.IsActive, _activeGate, element);
            if (src is not null)
                return new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent, Children = [ stage ] };
            // Player present, source still resolving (a manifest/DRM round-trip in flight) — keep pumping under Loading.
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = ColorF.Transparent,
                Children = [ stage, LoadingOverlay() ],
            };
        }

        return Poster(track);
    }

    // The shared "no player yet" composition — the track's own artwork, dimmed, with a spinner. Used both as the
    // outer fallback (no player at all) and as MediaPlayerElement.PosterContent (shown until the element's own first
    // frame): a resolving manifest/DRM licence takes real time on every track change, and a black rectangle for those
    // seconds reads as broken rather than as loading.
    static Element Poster(Track? track) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
        Children = [ PosterGround(track?.Image), LoadingOverlay() ],
    };

    /// <summary>The poster's GROUND alone — dimmed artwork, no spinner, no letterbox fill of its own. Extracted so the
    /// watch page can draw a BYTE-IDENTICAL idle layer under its stage while nothing is docked there.
    ///
    /// <para>Identical is the point, not merely convenient: if the page's idle layer differed from the card's own
    /// poster, the moment video went live the viewer would see two cross-fades — the page swapping its idle art for the
    /// card, and the card swapping its poster for the first frame. With the same pixels underneath, the only visible
    /// transition is the element's OWN <c>PosterMotion</c>: exactly one cross-fade, from the art the user was already
    /// looking at to the first decoded frame.</para></summary>
    internal static Element PosterGround(Image? art) => new BoxEl
    {
        Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [ Surfaces.ArtworkFill(art, 0f) ],
    };

    static Element LoadingOverlay() => new BoxEl
    {
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.S,
        HitTestPassThrough = true,
        Children =
        [
            ProgressRing.Indeterminate(size: 20f, foreground: Tok.TextOnAccentPrimary),
            new TextEl(Loc.Get(Strings.Player.Loading))
            {
                Size = 12f, Weight = 600, Color = Tok.TextOnAccentPrimary,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    // ── chrome — the hover-revealed top strip: pop out · fullscreen · close, right-aligned, 30 DIP tall. ────────────
    static Element BuildChrome(PlaybackBridge b, Action enterFullscreen) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Height = ScrimH, Shrink = 0f, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XXS,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Gradient = Tok.ScrimTop,
                // NO corners: both faces are full-bleed, so the strip runs the card's full width and whoever wraps the
                // card clips the silhouette. The rounded top pair existed only for the square art tile's own envelope.
                Opacity = 0f, HoverOpacity = 1f,
                HoverDurationMs = ChromeFadeMs, HoverEasing = Easing.FluentDecelerate,
                Children =
                [
                    Glyph(Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), () =>
                    {
                        Announcer.Say(Loc.Get(Strings.Player.VideoMiniPlayer));
                        b.ShowVideoAt(SurfacePlacement.Floating);
                    }),
                    Glyph(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), enterFullscreen),
                    Glyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), () =>
                    {
                        Announcer.Say(Loc.Get(Strings.Player.TurnOffVideo));
                        // Sticky off, via the model — never TurnVideoOff directly: NotifyVideoSurfaceClosed carries the
                        // stale-close identity guard PlacementCore.HostClosed needs to make an in-app close stick.
                        b.NotifyVideoSurfaceClosed(SurfacePlacement.Docked);
                    }),
                ],
            },
        ],
    };

    static Element Glyph(string glyph, string tip, Action onClick) => ToolTip.Wrap(new BoxEl
    {
        Width = GlyphBox, Height = GlyphBox, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Fill = ColorF.Transparent,
        HoverFill = Tok.OnMediaPrimary with { A = 0.14f },
        PressedFill = Tok.OnMediaPrimary with { A = 0.22f },
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = 11f, FontFamily = Theme.IconFont,
                Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary,
            },
        ],
    }, tip);
}
