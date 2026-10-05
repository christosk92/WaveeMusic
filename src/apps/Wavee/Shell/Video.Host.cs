// ── Shell/Video.Host.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the pop-out window (own HWND, borderless fullscreen), the IDetachedVideoWindow seam
//
// Role: SHELL
// Owner: K
// Wave: 4
// Budget: 250 lines
// Spec: ch 24 §9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THREE THINGS, and not one pixel of video:
//
//   `Video.State`   the ONE placement chokepoint. `PlacementState` lives here as a signal, and `Commit` is the single
//                   write path — which is exactly why "closed the pop-out while fullscreen, reopened it fullscreen" is
//                   unrepresentable: `DetachedFullscreenRule.After` is applied HERE, once, and there is no list of
//                   clearing edges anywhere for anyone to forget to extend. Ch 24 §9 item 4 asks the plan for this
//                   home; `Playback.cs`'s own header says the placement machine is NOT the reducer's (A13), so it
//                   lives here, beside its only writer.
//   `Video.Prefs`   the two video preferences that are not placement (always-on-top, the aspect policy), on the
//                   epoch idiom: a setting key is a store entry with nothing to subscribe to, so the epoch IS the
//                   subscription.
//   `Video.PopOut`  the single owner of the detached window's LIFECYCLE. A controller leaf (it renders nothing) that
//                   watches the ONE resolved placement and opens/closes the window to match. It is the ONE place that
//                   holds an `IDetachedVideoWindow` handle.
//
// WHY A CONTROLLER LEAF RATHER THAN A SURFACE. This is what structurally kills the split-ownership bugs: the player
// bar only expresses INTENT, the surfaces only RENDER from the resolved placement, and no view holds a window handle
// it can desync from. Closing the window by ANY means (OS chrome, Alt+F4, programmatic) fires `OnClosed`, which
// reports the close to the placement model — and the MODEL, not this component, decides that "closed the pop-out"
// means "keep watching in the mini player" rather than "off".
//
// OPEN IS DEFERRED, CLOSE IS A PARK (F110). Opening builds a second window, swapchain and AppHost, which is far too much
// to do inside the reactive flush that noticed the placement edge, so the effect only starts the hand-off and POSTS the
// open to the next UI frame (re-deciding against the placement it finds then). A STATE-driven close does not dispose the
// window: it hides it and parks its host (`IDetachedVideoWindow.Park`: tree and swapchain kept, nothing produced), and the
// next open reuses it (`Unpark`) instead of rebuilding everything. A parked window is disposed after `ParkIdleMs` without a
// reuse, when the platform refuses to park it, or when the shell unmounts (and the engine reaps it with its parent).
//
// THE SEAM. `IDetachedVideoWindow` / `DetachedWindowRequest` / `InputHooks.OpenDetachedWindow` are the ENGINE's
// (`FluentGpu.Engine/Hooks/Context.cs`), host-wired by `AppHost`, and this file is their ONLY caller in the app — the
// shape ch 24 §9 asks for. The window's CONTENT is `Video.UI.cs`'s (stage 2), handed in through
// `PopOut.ContentFactory`: a detached window builds its OWN `AppHost`, so `Ctx.Provide` chains do not cross the
// boundary and every live value must be a FROZEN SIGNAL INSTANCE the factory closes over.
//
// Rules: UI thread only (C1). Nothing here allocates per frame — the effects are mount-wired and re-run on a signal
// edge, never on a tick.
//
// Named partials: `Video.Overrides.cs` (the local-attachment roster) and `Video.Host.Wiring.cs` (gap batch B7: the
// composition call, the placement → playback post, the boundary fold, the host observer leaf).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Video
{
    // ── 1. the placement state, and its ONE write path ──────────────────────────────────────────────────────────────

    /// <summary>The app's one movable video surface, as live state. Every affordance READS <see cref="Resolved"/> and
    /// every gesture goes through one of the verbs below — never a second flag, never a direct signal write.</summary>
    public static partial class State
    {
        /// <summary>The always-on log category. No env switch, ever.</summary>
        public const string LogCategory = "video";

        /// <summary>The whole placement state. Written ONLY by <see cref="Commit"/>.</summary>
        public static readonly Signal<PlacementState> Surface = new(PlacementState.Music);

        /// <summary>Whether the DETACHED pop-out is presenting borderless-fullscreen on its own monitor. Deliberately
        /// NOT <see cref="SurfacePlacement.Fullscreen"/>, which is the MAIN window's full-bleed surface: two different
        /// OS windows, two different states. Folding them into one enum re-introduces the monitor hop.</summary>
        public static readonly Signal<bool> DetachedFullscreen = new(false);

        /// <summary>What the host can actually do right now (rail fits ∨ a page stage would host; can a second window
        /// open; does the fullscreen hook exist). Written from exactly ONE effect in `Shell.Host.cs`. Conservative
        /// seed: an unwired host must DEGRADE, never offer a placement it cannot honour.</summary>
        public static readonly Signal<PlacementSet> HostCapability = new(PlacementSet.Docked | PlacementSet.Floating);

        /// <summary>THE derived transport owner. Republished by <see cref="Commit"/> so every transport-bearing
        /// component asks ONE question of ONE value.</summary>
        public static readonly Signal<TransportOwner> Transport = new(TransportOwner.GlobalBar);

        /// <summary>Main-window host fullscreen, published from <see cref="Commit"/> so a stay-mounted hole restyles
        /// without a generation remount. Pop-out uses <see cref="DetachedFullscreen"/> on its own HWND.</summary>
        public static readonly Signal<bool> MainHostFullscreen = new(false);

        /// <summary>What the owners actually have mounted right now (the scoped fold of every surface's
        /// <see cref="ReportLive"/>). Deliberately NOT part of <see cref="Surface"/>: nothing resolves from it and it is
        /// written from passive effects, so a report inside the shared signal re-rendered the player bar, the rail frame
        /// and every video surface once or twice per placement change for a value only the diagnostics line reads.
        /// A plain field — non-reactive by design, UI thread only.</summary>
        public static SurfacePlacement LiveSurface { get; private set; }

        /// <summary>Make-before-break for the pop-out: true once the pop-out's own element has presented its first frame
        /// (or the hand-off grace expired), which is the moment the main window's outgoing presenter may be released
        /// (<see cref="MainWindowHole.Mounted"/>). False whenever no pop-out hand-off is in flight. Written only by
        /// <see cref="BeginPopOutHandoff"/> / <see cref="ReportPopOutPresented"/> / <see cref="ExpirePopOutHandoff"/> /
        /// <see cref="EndPopOutHandoff"/>.</summary>
        public static readonly Signal<bool> PopOutPresented = new(false);

        /// <summary>How long the main presenter waits for the pop-out's first frame before it lets go anyway (a pop-out
        /// whose source has no surface yet must not pin a second pumping element for the rest of the session).</summary>
        public const int PopOutHandoffGraceMs = 2000;

        static long s_handoffStartTs;   // 0 = no hand-off in flight

        /// <summary>The pop-out is opening: the main presenter stays mounted (hidden) until the pop-out presents.</summary>
        public static void BeginPopOutHandoff()
        {
            s_handoffStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
            PopOutPresented.Value = false;
        }

        /// <summary>The pop-out's element reported its first presented frame: release the main presenter. Inert when no
        /// hand-off is in flight (a later generation remount inside the pop-out reports again).</summary>
        public static void ReportPopOutPresented() => FinishPopOutHandoff("presented");

        /// <summary>The grace ran out before the pop-out presented: release the main presenter anyway.</summary>
        public static void ExpirePopOutHandoff() => FinishPopOutHandoff("grace-expired");

        /// <summary>The pop-out window was revealed (shown) on the child's frame after an open. A FRESH window reports its
        /// presentation through its element's first presented surface (<see cref="ReportPopOutPresented"/>), so its reveal
        /// says nothing here. A REUSED (warm, F110) window keeps its mounted tree, and with it the element's one-shot
        /// "surface presented" latch, so no new first presentation will ever be raised: the reveal IS its presentation
        /// (the swapchain and the video child visual were already live when it parked). Without this the main presenter
        /// waited out the whole <see cref="PopOutHandoffGraceMs"/> on every warm reopen ("grace-expired gapMs=2014").
        /// Inert when no hand-off is in flight.</summary>
        public static void ReportPopOutRevealed(bool reusedWindow)
        {
            if (reusedWindow) ReportPopOutPresented();
        }

        /// <summary>The pop-out window is gone (closed, refused, or the owner unmounted): no hand-off remains.</summary>
        public static void EndPopOutHandoff()
        {
            s_handoffStartTs = 0;
            PopOutPresented.Value = false;
        }

        static void FinishPopOutHandoff(string how)
        {
            long start = s_handoffStartTs;
            if (start == 0) return;
            s_handoffStartTs = 0;
            // ALWAYS-ON: the gap this hand-off covered (open -> the pop-out's first composited frame) is the number the
            // make-before-break exists to hide; "grace-expired" means the pop-out never got a surface in time.
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log.Info(LogCategory, $"pop-out hand-off {how} gapMs={ms:0.#}");
            PopOutPresented.Value = true;
        }

        /// <summary>The resolved placement — what should be mounted right now.</summary>
        public static SurfacePlacement Resolved => PlacementCore.Resolve(Surface.Peek());

        /// <summary>Is anything resolved (the surface should be visible / the media should be video)?</summary>
        public static bool IsActive => PlacementCore.IsActive(Surface.Peek());

        /// <summary>THE single write path. Everything that must happen ONCE per placement change happens here: the
        /// pop-out's fullscreen bit is re-decided by the RULE (never by a clearing edge), and the derived transport
        /// owner is republished. The always-on line records every TERM of the decision, because "the rail kept it"
        /// looks identical whether the page never claimed it, claimed it with the wrong id, or was correctly
        /// outranked — and that ambiguity cost a full debugging cycle. It is also the ONE place the reducer hears about
        /// video (G-141, `PlacementPost`) and the one place the preferred home is persisted (G-150).</summary>
        public static void Commit(in PlacementState next)
        {
            var before = Surface.Peek();
            Surface.Value = next;
            var resolved = PlacementCore.Resolve(next);
            DetachedFullscreen.Value = DetachedFullscreenRule.After(DetachedFullscreen.Peek(), resolved);
            Transport.Value = PlacementCore.TransportOwnerFor(resolved);
            MainHostFullscreen.Value = resolved == SurfacePlacement.Fullscreen;
            if (before.Equals(next)) return;
            if (PlacementPost.ShouldPost(in before, in next, HostCapability.Peek(), out bool wanted)) Playback.SetVideoPlacement(wanted);
            if (next.Preferred != before.Preferred) PersistPreferred(next.Preferred);
            long gen = Playback.Video.Player.Peek().Generation;
            Log.Info(LogCategory, "video placement " + PlacementCore.Resolve(before) + " -> " + resolved
                + " requested=" + next.Requested + " preferred=" + next.Preferred + " live=" + LiveSurface
                + " transport=" + Transport.Peek() + " hostFs=" + MainHostFullscreen.Peek()
                + " gen=" + gen + " hole=" + (MainWindowHole.Owns(resolved) ? "main" : "other"));
        }

        // ── the verbs (every one of them a Commit) ──────────────────────────────────────────────────────────────────

        /// <summary>The player bar's primary: SYMMETRIC, and it COMMITS exactly what the no-mid-track-swap rule
        /// withheld — so a lit badge's first click starts the video instead of turning it off.</summary>
        public static void TogglePrimary(bool hasVideo)
            => Commit(UpgradeGate.PrimaryClick(Surface.Peek(), hasVideo, HostCapability.Peek()));

        /// <summary>Open at a chosen home (the placement menu's rows). Callers that act on a LIVE has-video answer
        /// (the menu, the rail) must <see cref="FoldAvailability"/> first: <c>Available</c> is stamped only at a track
        /// boundary or a primary click, so a session restored mid-track leaves it at <c>None</c> and an unfolded OpenAt
        /// resolves to None — the video loads and plays with no surface to show it.</summary>
        public static void OpenAt(SurfacePlacement target) => Commit(PlacementCore.OpenAt(Surface.Peek(), target));

        /// <summary>Off — globally and stickily. Every user-initiated close lands here.</summary>
        public static void TurnOff() => Commit(PlacementCore.TurnOff(Surface.Peek()));

        public static void EnterFullscreen() => Commit(PlacementCore.EnterFullscreen(Surface.Peek()));
        public static void ExitFullscreen() => Commit(PlacementCore.ExitFullscreen(Surface.Peek()));

        /// <summary>An AMBIENT move that is not the user closing the feature (the rail being closed while docked).
        /// <c>Preferred</c> survives, so restoring the condition re-docks automatically.</summary>
        public static void Demote(SurfacePlacement to) => Commit(PlacementCore.Demote(Surface.Peek(), to));

        /// <summary>A surface reporting that its OWN chrome closed it. The MODEL decides what that means.</summary>
        public static void ReportClosed(SurfacePlacement closed)
            => Commit(PlacementCore.HostClosed(Surface.Peek(), closed));

        /// <summary>A surface reporting what it actually has mounted. Scoped: a surface may claim <c>Live</c> for
        /// itself and may only RELEASE it if it still holds it.</summary>
        public static void ReportLive(SurfacePlacement surface, bool mounted)
        {
            var before = LiveSurface;
            var after = PlacementCore.LiveAfterReport(before, surface, mounted);
            if (after == before) return;
            LiveSurface = after;
            Log.Info(LogCategory, "video live " + before + " -> " + after);
        }

        /// <summary>Re-stamp the availability THIS playable actually has. Required before acting on an intent: a
        /// deferred upgrade leaves it stale at None, and both Resolve and IsActive consult it.</summary>
        public static void FoldAvailability(bool hasVideo)
            => Commit(UpgradeGate.FoldAvailability(Surface.Peek(), hasVideo, HostCapability.Peek()));
    }

    // ── 2. the video preferences that are not placement ─────────────────────────────────────────────────────────────

    /// <summary>Bumped whenever a video preference is written, so live surfaces re-read it. The `Lyrics.Prefs`
    /// idiom — a setting key has nothing to subscribe to, so the epoch is the subscription.</summary>
    public static class Prefs
    {
        public static readonly Signal<int> Epoch = new(0);
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>Whether the pop-out stays above other windows. Default true.</summary>
        public static bool AlwaysOnTop(IAppSettings? s)
        { _ = Epoch.Value; return s is null ? Platform.Keys.VideoWindowAlwaysOnTop.Default : s.Get(Platform.Keys.VideoWindowAlwaysOnTop); }

        public static void SetAlwaysOnTop(IAppSettings? s, bool onTop)
        { s?.Set(Platform.Keys.VideoWindowAlwaysOnTop, onTop); Bump(); }

        /// <summary>The global aspect policy, by its STORAGE-STABLE name (never the engine enum's number).</summary>
        public static AspectPreference Aspect(IAppSettings? s)
        { _ = Epoch.Value; return AspectPersistence.LoadMode(s?.Get(Platform.Keys.VideoAspectMode)); }

        public static double CustomRatio(IAppSettings? s)
        {
            _ = Epoch.Value;
            return AspectPersistence.LoadRatio(s is null
                ? AspectPersistence.DefaultCustomRatio
                : s.Get(Platform.Keys.VideoCustomAspectRatio));
        }

        public static void SetAspect(IAppSettings? s, AspectPreference mode, double customRatio)
        {
            s?.Set(Platform.Keys.VideoAspectMode, AspectPersistence.SaveMode(mode));
            if (mode == AspectPreference.Custom)
                s?.Set(Platform.Keys.VideoCustomAspectRatio, AspectPersistence.LoadRatio(customRatio));
            Bump();
        }
    }

    // ── 3. the detached pop-out window's owner ──────────────────────────────────────────────────────────────────────

    /// <summary>THE single owner of the pop-out window's lifecycle. Mounted once in the shell; renders nothing.</summary>
    public sealed class PopOut : Component
    {
        const SurfacePlacement Owned = SurfacePlacement.Detached;   // the ONE placement this owner is responsible for

        /// <summary>The window's open size and its floor, in DIP.</summary>
        public const float DefaultWidthDip = 640f, DefaultHeightDip = 360f, MinWidthDip = 320f, MinHeightDip = 180f;

        /// <summary>The OS's own top resize strip (SM_CXPADDEDBORDER + SM_CYSIZEFRAME ≈ 8 px at 100 %): the caption band
        /// starts below it so the window's top edge keeps answering HTTOP.</summary>
        public const float ResizeStripDip = 8f;

        /// <summary>Builds the window's ROOT content. Installed by `Video.UI.cs` (stage 2) at composition. Null means
        /// no pop-out surface is compiled in, which is a real and HANDLED state: the owner reports the close
        /// immediately, so the model falls back to the mini player instead of leaving a toggle lit over a window that
        /// was never created.
        /// <para>The factory must close over FROZEN SIGNAL INSTANCES, never over values: a detached window builds its
        /// own <c>AppHost</c>, so <c>Ctx.Provide</c> chains do not cross the boundary and the window has no ambient map
        /// of its own. Freezing a <c>Signal</c> is correct; freezing what was read out of one is not.</para></summary>
        public static Func<Component>? ContentFactory { get; set; }

        /// <summary>The window's OS title — what the taskbar and Alt+Tab show. It is the CONTENT (the track), not the
        /// name of the button that opened it, and it FOLLOWS the content rather than freezing at open.</summary>
        public static Func<string> TitleFactory { get; set; } = static () => "Now playing";

        /// <summary>The settings store (frozen at mount — a stable INSTANCE, so freezing is correct). Remembers where
        /// the user last put the window so it reopens there instead of jumping back to the default corner.</summary>
        public IAppSettings? Settings { get; init; }

        /// <summary>How long a parked (hidden, warm) pop-out is kept for a reuse before it is disposed (F110). A parked host costs
        /// only its window, swapchain and mounted tree, and no frames; the next open inside this window skips building them.</summary>
        public const int ParkIdleMs = 120_000;

        TimerHandle _handoffGrace;   // re-armed by Open: the make-before-break deadline (a stale fire is inert)
        TimerHandle _parkIdle;       // armed by a park: disposes the warm window if nothing reused it (a stale fire is inert)
        IDetachedVideoWindow? _parked;   // F110: the warm, hidden window awaiting reuse (at most one)
        bool _openQueued;            // F110: an open is posted to the next frame and has not run yet
        bool _unmounted;             // the shell swapped this owner out: a queued open must not run

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);            // the pop-out seam
            var handle = UseRef<IDetachedVideoWindow?>(null);      // the live window (null / !IsOpen = none)
            var post = UsePost();                                  // UI marshal for the render thread's OnRenderFailed
            // Armed (RestartIn) only by Open; the mount-time arm of the hook fires into no hand-off and is inert.
            _handoffGrace = UseTimeout(static () => State.ExpirePopOutHandoff(), State.PopOutHandoffGraceMs, DepKey.Empty);
            // Armed (RestartIn) only by a park; the mount-time arm fires into no parked window and is inert.
            _parkIdle = UseTimeout(() => DisposeParked("idle"), ParkIdleMs, DepKey.Empty);

            // The decode host asks "could a second window open?" when it folds the host capability; THIS file sets the
            // hook (the video-engine plan §3.4: "the pop-out HWND is K's; Video.Host.cs sets the hook"). Mount-wired
            // once, cleared on unmount so a torn-down shell never answers for a window it can no longer open.
            UseEffect(() =>
            {
                Playback.Video.CanOpenDetachedWindow = () => ContentFactory is not null
                    && (hooks.CanOpenDetachedWindow?.Invoke() ?? false);
                return () => Playback.Video.CanOpenDetachedWindow = null;
            }, DepKey.Empty);

            // Reactive reconcile: it READS the resolved placement, so it re-runs whenever that changes and drives the
            // window to match. It reports reality back through ReportLive, which only folds into the non-reactive
            // LiveSurface: the report never wakes this effect, so there is no extra pass.
            UseSignalEffect(() =>
            {
                var state = State.Surface.Value;                   // subscribe
                var live = handle.Value;
                bool alive = live is { IsOpen: true };
                var action = PlacementCore.DecideOwned(PlacementCore.Resolve(state), Owned, alive);

                if (action == MountAction.Open) QueueOpen(hooks, handle, post);
                else if (action == MountAction.Close)
                {
                    live!.OnClosed = null;   // a STATE-driven close is not a user-close → it must not trigger the fallback
                    handle.Value = null;
                    State.DetachedFullscreen.Value = false;   // the mode the window described goes with it (before the park persists its rect)
                    State.EndPopOutHandoff();
                    State.ReportLive(Owned, mounted: false);
                    ParkOrClose(live, post);
                }
            });

            // Apply the pop-out's own fullscreen mode to the LIVE window. This is the whole monitor fix: the request
            // goes to the DETACHED window's handle, so the backend resolves the target display from THAT window — a
            // pop-out dragged to a second monitor fullscreens there. Routing it through the MAIN window's fullscreen
            // hook instead would fullscreen the main window on the main window's display, which reads as the picture
            // jumping screens on a keypress.
            //
            // Read the signal FIRST and UNCONDITIONALLY: guarding on the handle before the read would leave the effect
            // subscribed to NOTHING on the pass where no window is open, and a signal effect with no dependencies
            // never runs again — the toggle would be dead for the rest of the session.
            UseSignalEffect(() =>
            {
                bool fullscreen = State.DetachedFullscreen.Value;
                if (handle.Value is { IsOpen: true } live) live.SetFullscreen(fullscreen);
            });

            // Keep the OS title on the CONTENT. A pop-out that still names the previous song in the taskbar is the
            // same frozen-at-open staleness this whole file exists to avoid.
            UseSignalEffect(() =>
            {
                _ = Playback.Current.Value;     // subscribe → re-title on every track change
                if (handle.Value is { IsOpen: true } live) live.SetTitle(TitleFactory());
            });

            // Always-on-top follows the preference LIVE, not just at open: a user who turns it off while the window is
            // up means "get out of the way NOW", and making them close and reopen the window to apply it would be that
            // staleness again. Keyed on the prefs epoch.
            UseSignalEffect(() =>
            {
                _ = Prefs.Epoch.Value;
                if (Settings is not { } st) return;
                if (handle.Value is { IsOpen: true } live) live.SetTopmost(Prefs.AlwaysOnTop(st));
            });

            // Unmount cleanup: the shell can swap this component out (logout) while the window is still open. A signal
            // effect has no disposer, so without this the window leaks with an OnClosed pointing at a dead component.
            // Null OnClosed FIRST so the (intentional) close never reports a user-close. This is also the ONLY route
            // out of Detached that does not go through Commit, so the fullscreen bit is cleared by hand here — or it
            // would survive into the next mount and open the next pop-out already fullscreen.
            UseEffect(() => () =>
            {
                _unmounted = true;
                var h = handle.Value;
                handle.Value = null;
                State.DetachedFullscreen.Value = false;
                State.EndPopOutHandoff();
                if (h is not null) { h.OnClosed = null; h.Close(); }
                DisposeParked("unmount");
            }, DepKey.Empty);

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        /// <summary>F110: the reconcile effect runs inside the parent's reactive flush, so it must not build a window, a swapchain and
        /// a whole child AppHost there. It starts the make-before-break hand-off (a signal write, so the main presenter stays mounted
        /// through the frame the open waits for) and posts the open to the next UI frame. The open re-decides against the placement
        /// it finds then, so an edge that reversed in between opens nothing; one open is ever queued.</summary>
        void QueueOpen(InputHooks hooks, Ref<IDetachedVideoWindow?> handle, Action<Action> post)
        {
            if (_openQueued) return;
            _openQueued = true;
            State.BeginPopOutHandoff();
            post(() =>
            {
                _openQueued = false;
                if (_unmounted) { State.EndPopOutHandoff(); return; }
                bool alive = handle.Value is { IsOpen: true };
                if (PlacementCore.DecideOwned(PlacementCore.Resolve(State.Surface.Peek()), Owned, alive) != MountAction.Open)
                {
                    State.EndPopOutHandoff();   // the placement moved on before the frame came: nothing to open
                    return;
                }
                Open(hooks, handle, post);
            });
        }

        /// <summary>F110: a state-driven close parks the window warm instead of disposing it, so the next open skips the window,
        /// swapchain and tree construction. Only one warm window is kept; a window the platform will not park (still revealing,
        /// render path failed, no warm reuse) is closed as before. While parked it must not report to the placement model, so its
        /// callbacks are replaced: a close of the parked window just forgets it, a render failure disposes it.</summary>
        void ParkOrClose(IDetachedVideoWindow live, Action<Action> post)
        {
            DisposeParked("replaced");
            if (!live.IsOpen || !live.Park())
            {
                live.OnClosed = null;
                live.Close();
                return;
            }
            live.BoundsChanged = null;   // a hidden window is not moved by anyone
            live.OnRevealed = null;
            live.OnFirstVideoBound = null;
            live.OnClosed = () => { if (ReferenceEquals(_parked, live)) { _parked = null; _parkIdle.Cancel(); } };
            live.OnRenderFailed = () => post(() => { if (ReferenceEquals(_parked, live)) DisposeParked("render-failed"); });
            _parked = live;
            _parkIdle.RestartIn(ParkIdleMs);
            Log.Info(State.LogCategory, "pop-out parked warm idleMs=" + ParkIdleMs);
        }

        /// <summary>F110: dispose the warm window (idle timeout, replaced by a newer park, render failure, shell unmount). A no-op when
        /// nothing is parked. The engine's own reaper tears it down on its next tick, fence-scoped like any other close.</summary>
        void DisposeParked(string why)
        {
            _parkIdle.Cancel();
            var w = _parked;
            if (w is null) return;
            _parked = null;
            w.OnClosed = null;
            w.OnRenderFailed = null;
            w.Close();
            Log.Info(State.LogCategory, "pop-out parked window disposed why=" + why);
        }

        void Open(InputHooks hooks, Ref<IDetachedVideoWindow?> handle, Action<Action> post)
        {
            // Reopen where the user left it. The host clamps a restored rect into the nearest VISIBLE monitor's work
            // area, so a position remembered on a display that is now unplugged still lands somewhere reachable — and
            // that clamp is never written back, so plugging the display in again restores the real position.
            var restored = default(RectF);
            if (Settings is { } st && PlacementPersistence.TryLoadRect(
                    st.Get(Platform.Keys.VideoWindowRect), out float rx, out float ry, out float rw, out float rh))
                restored = new RectF(rx, ry, rw, rh);

            // ALWAYS-ON cost lines. Opening the pop-out builds a SECOND AppHost, device resources and swapchain on the
            // UI thread, so a slow one is a hard freeze of the whole app with nothing else in the log to show for it
            // ("it mega froze going from the player to the out-of-process window"). The content factory is a bare
            // `new PopOutRoot()` (its tree is built by the child host), so timing it said nothing; the engine's own split
            // names the real stages instead. The open line carries the two synchronous ones (native window creation and
            // placement, then the child host constructor); the window is created hidden and only revealed once its first
            // frame has presented, so the reveal line - first RunFrame and first present - arrives on a later frame.
            // MAKE-BEFORE-BREAK: the main window's presenter stays mounted (hidden) from QueueOpen until this window's own
            // element presents its first frame, so the quarter second the window and swapchain take to build is never a
            // moment with no video composited anywhere. A reused window whose tree is unchanged raises no new first frame:
            // the hand-off grace (State.PopOutHandoffGraceMs) releases the main presenter then.
            // (The hand-off began in QueueOpen, a frame ago. This runs from a UI post, outside the reactive flush that asked for it.)
            var content = ContentFactory;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var tree = content?.Invoke();
            IDetachedVideoWindow? win = null;
            bool reused = false;
            if (tree is not null)
            {
                var request = new DetachedWindowRequest(
                    TitleFactory(), new Size2(DefaultWidthDip, DefaultHeightDip), tree,
                    AlwaysOnTop: Prefs.AlwaysOnTop(Settings),
                    InitialBoundsPx: restored,
                    MinClientSizeDip: new Size2(MinWidthDip, MinHeightDip));
                // WARM REUSE (F110): a parked window comes back with its tree and swapchain (the request's content is ignored: the
                // frozen-signal tree follows live state), skipping the window, swapchain and mount cost. A parked window that
                // cannot be reused is disposed and a fresh one opened.
                _parkIdle.Cancel();
                if (_parked is { } warm)
                {
                    _parked = null;
                    if (warm.Unpark(request)) { win = warm; reused = true; }
                    else { warm.OnClosed = null; warm.OnRenderFailed = null; warm.Close(); }
                }
                win ??= hooks.OpenDetachedWindow?.Invoke(request);
            }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            var openTiming = win?.OpenTiming ?? default;
            Log.Info(State.LogCategory, $"pop-out open syncMs={(t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.#} " +
                $"windowCreateMs={openTiming.WindowCreateMs:0.#} hostCtorMs={openTiming.HostCtorMs:0.#} " +
                $"restored={(restored.W > 0 ? "yes" : "no")} reused={(reused ? "yes" : "no")} opened={win is not null}");
            if (win is not null)
            {
                // The open line above is only the synchronous half. The reveal says the child presented ITS first frame (renderPresentMs
                // is the render thread's own stamp of that present, firstPresentMs when the UI noticed it); the picture is behind the
                // first successful video bind, which follows the native handle and can land after the reveal.
                bool reusedWindow = reused;
                win.OnRevealed = t =>
                {
                    Log.Info(State.LogCategory,
                        $"pop-out revealed firstFrameMs={t.FirstFrameMs:0.#} firstPresentMs={t.FirstPresentMs:0.#} renderPresentMs={t.RenderPresentMs:0.#} timedOut={t.TimedOut}");
                    State.ReportPopOutRevealed(reusedWindow);   // a warm window raises no new first-surface report: its reveal is the hand-off
                };
                win.OnFirstVideoBound = ms => Log.Info(State.LogCategory, $"pop-out first video bound ms={ms:0.#}");
            }
            handle.Value = win;
            if (win is null)
            {
                State.EndPopOutHandoff();
                // The platform refused a second window (a child host, headless, or no secondary swapchains) — or no
                // content is compiled in. That is a placement that is not actually AVAILABLE, so report it as a close:
                // the model falls back to the mini player instead of leaving the button lit pointing at a window that
                // was never created (the dead click).
                Log.Info(State.LogCategory, "pop-out video window refused by the host — falling back to the mini player");
                State.ReportClosed(Owned);
                return;
            }
            _handoffGrace.RestartIn(State.PopOutHandoffGraceMs);
            win.OnClosed = () =>
            {
                // Identity guard: if this dead window is no longer the CURRENT handle (a newer window B was opened in
                // the same frame while A sat !IsOpen awaiting the reaper), A's stale callback must not clobber B's
                // handle. The MODEL guards the placement half of the same race (a close for a placement that is no
                // longer resolved is inert); this guards the handle half, which the model cannot see.
                if (!ReferenceEquals(handle.Value, win)) return;
                handle.Value = null;
                // Drop the fullscreen mode BEFORE reporting the close: the window it describes is already gone, and
                // the report below may resolve the placement to something that is not Detached at all.
                State.DetachedFullscreen.Value = false;
                State.EndPopOutHandoff();
                State.ReportLive(Owned, mounted: false);
                State.ReportClosed(Owned);
            };
            // A pop-out whose render path failed (a non-device-loss exception on the shared render thread) is latched dead by
            // the engine and never presents again: it would sit on screen frozen. Report it as a close so the model falls back
            // inline, exactly as for a refused window; the reconcile then closes the dead window. The event fires on the RENDER
            // thread, so the model write is marshalled to the UI thread, behind the same identity guard as OnClosed.
            win.OnRenderFailed = () => post(() =>
            {
                if (!ReferenceEquals(handle.Value, win)) return;
                Log.Info(State.LogCategory, "pop-out render failed - falling back inline");
                State.ReportClosed(Owned);
            });
            // Persist the window's SETTLED position (the host debounces — one call per gesture, not one per pixel).
            // …but NEVER while the pop-out is presenting fullscreen. Entering borderless fullscreen is a move+resize to
            // the whole monitor rect and is reported here like any other settled change; saving it would overwrite the
            // position the USER chose with a full-screen rect, so the next launch would open a monitor-sized "pop-out"
            // nobody asked for — and exiting fullscreen would have nothing to restore to.
            if (Settings is { } save)
                win.BoundsChanged = r =>
                {
                    if (State.DetachedFullscreen.Peek()) return;   // Peek: a persistence guard is not a subscription
                    save.Set(Platform.Keys.VideoWindowRect, PlacementPersistence.SaveRect(r.X, r.Y, r.W, r.H));
                };
            State.ReportLive(Owned, mounted: true);
        }
    }
}
