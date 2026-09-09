using System;
using FluentGpu;          // FluentApp (OS theme facade + SystemColorsChanged relay)
using FluentGpu.Dsl;
using FluentGpu.Foundation;   // Diag.EnvFlag (the screenshot/probe harness switches)
using FluentGpu.Hooks;
using FluentGpu.Localization;   // Loc.Get (the login-failure copy is localized, never a literal)
using Wavee.Core;             // AuthStatus / LoginSnapshot / LoginPhase (the login gate + takeover)

namespace Wavee;

// The app root. Owns Services, provides the Services + PlaybackBridge contexts, wires the Core→Signal bridge on mount
// (and starts a fake session + playback so the shell is live), then renders the shell. The whole app blur-rises in.
sealed class WaveeApp : Component
{
    // The hard deadline for StartupActivation's deferred Window ladder (see the mount effect below) — the OS media
    // surfaces (SMTC, taskbar, Jump List, network-cost hooks) always appear by this long after launch even if the
    // first route never fires PageRevealWatch.FirstContentRevealed.
    const double WindowLadderFallbackMs = 2000;

    readonly Services _services;

    internal static PlaybackBridge? ProbePlayback;
    internal static Services? ProbeServices;

    // The composition root passes the settings store created early (so the theme is seeded before the first frame);
    // null in tests falls back to the store Services creates itself.
    public WaveeApp(IAppSettings? settings = null, AppLocale? appLocale = null)
        => _services = Services.UseRealBackend
            ? Services.CreateReal(settings, appLocale: appLocale)
            : Services.CreateFake(settings, appLocale);

    public override Element Render()
    {
        var bridge = _services.Playback;
        var libBridge = _services.LibraryBridge;
        var friendsBridge = _services.FriendsBridge;
        var notifications = _services.Notifications;
        var store = _services.LibraryStore;
        if (Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_PROBE") || Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_NAV_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE"))
        {
            ProbePlayback = bridge;
            ProbeServices = _services;
            // Silence the async lyrics ticker BEFORE it can mount so the advance-probe alone drives OnFrame synchronously
            // (deterministic, timer-decoupling-free). Set here at the root so it is true before the rail/ticker renders.
            if (Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE")) LyricsView.ProbeSyncMode = true;
        }

        // Follow the OS dark-mode / accent live WHILE the user hasn't pinned an explicit theme (mode == System). The host
        // relays WM_SETTINGCHANGE on the UI thread; we re-read the OS state, apply it, and animate the in-place re-theme.
        var requestTheme = UseContext(ThemeControl.Request);
        Context.UseEffect(() =>
        {
            void OnSystemColorsChanged()
            {
                if (_services.Settings.Get(WaveeSettings.ThemeMode) != 0) return;
                int oldEpoch = Tok.Epoch;
                var kind = FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark;
                Tok.Use(WaveeTheme.ResolvePalette(), kind);
                if (FluentApp.SystemAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
                else if (FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
                // Windows can broadcast ImmersiveColorSet without an effective palette/accent change. Requesting a
                // transition in that case still forces RethemeAll, re-rendering the entire mounted app for identical
                // colors. Only arm the cross-fade when a guarded Tok mutator actually advanced the epoch.
                if (Tok.Epoch != oldEpoch) requestTheme?.Invoke(250f);
            }
            FluentApp.SystemColorsChanged += OnSystemColorsChanged;
            NavigationFrameWatch.Attach();
            return () => FluentApp.SystemColorsChanged -= OnSystemColorsChanged;
        }, DepKey.Empty);

        var post = Context.UsePost();
        var loginSession = UseRef<System.Threading.CancellationTokenSource?>(null);
        var wasAuthed = UseRef(false);   // have we EVER authenticated this run? (fake demo: logout → takeover, but no initial-launch flash)
        var governorTimer = UseRef<System.Threading.Timer?>(null);   // rooted here so the periodic MemoryGovernor poll isn't GC-collected (the app root never unmounts)
        var windowLadderFallback = UseRef<System.Threading.Timer?>(null);   // rooted so the Window-ladder deadline timer isn't GC-collected before it fires
        var volumeSaveTimer = UseRef<System.Threading.Timer?>(null); // remember-volume: debounced persist of the slider value
        var zoomSaveTimer = UseRef<System.Threading.Timer?>(null);   // app zoom: debounced persist of FluentApp.Zoom (chords/wheel never write the store themselves)
        var resumeInFlight = UseRef(false);   // one silent resume at a time (launch kick + the chip's Reconnect share it)

        // ── Simultaneous live login (device code + browser race) ─────────────────────────────────────────────────────
        // The takeover runs BOTH methods at once: RestartCode polls the device code (the two-pane's QR + pairing code), and
        // the "Log in" button fires StartBrowser to race the PKCE loopback alongside it (QUIET — it can't replace the
        // two-pane; it only surfaces success). They share ONE session CTS; the FIRST to GoLive cancels it so the loser
        // bails (the supersede check). The winning host owns an INDEPENDENT CTS, so this cancel never touches its hydration.
        // Everything runs off the UI thread (the login/dealer/AP handshake must not couple to the render loop).
        void RestartCode()
        {
            loginSession.Value?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            loginSession.Value = cts;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var host = await Wavee.SpotifyLive.LiveSessionHost.StartAsync(_services, new WaveeLogger(_services.Log, "connect"), cts.Token, bridge.Progress(post), uiPost: post, interactive: true, useBrowser: false).ConfigureAwait(false);
                    if (host is not null) { post(() => { if (loginSession.Value == cts) loginSession.Value = null; }); cts.Cancel(); }   // success → stop the browser sibling
                }
                catch (OperationCanceledException) { }   // superseded by a newer attempt
                catch (Exception ex)
                {
                    _services.Log.Event(WaveeLogLevel.Warning, "connect", "login.code.failed",
                        "Code login failed", ex: ex, fields: [WaveeLogField.Of("phase", bridge.Login.Peek().Phase.ToString())]);
                    post(() => { if (loginSession.Value == cts) bridge.ReportLogin(new LoginSnapshot(LoginPhase.Failed, Error: Loc.Get(Strings.Auth.GenericError))); });
                }
            });
        }

        // The "Log in" button: race the browser-loopback (PKCE) alongside the running device code, on the SAME session.
        void StartBrowser()
        {
            var cts = loginSession.Value;
            if (cts is null || cts.IsCancellationRequested) return;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var host = await Wavee.SpotifyLive.LiveSessionHost.StartAsync(_services, new WaveeLogger(_services.Log, "connect"), cts.Token, bridge.Progress(post), uiPost: post, interactive: true, useBrowser: true, quietPhases: true).ConfigureAwait(false);
                    if (host is not null) { post(() => { if (loginSession.Value == cts) loginSession.Value = null; }); cts.Cancel(); }
                }
                catch (OperationCanceledException) { }   // the device-code sibling won the race, or a newer attempt superseded this one
                catch (Exception ex)
                {
                    // NOT silent any more. The old empty catch meant a browser hand-off that failed outright (no default
                    // browser, a blocked loopback port, a refused redirect) left the user staring at a pairing pane with
                    // no idea their click had died — and the log had nothing to explain it afterwards either. The device
                    // code IS still polling, so this reports rather than aborts: the message names the browser as the
                    // thing that failed, so "scan the code instead" reads as the obvious next move.
                    _services.Log.Event(WaveeLogLevel.Warning, "connect", "login.browser.failed",
                        "Browser login failed", ex: ex, fields: [WaveeLogField.Of("phase", bridge.Login.Peek().Phase.ToString())]);
                    post(() =>
                    {
                        if (loginSession.Value == cts)
                            bridge.ReportLogin(new LoginSnapshot(LoginPhase.Failed, Error: Strings.Auth.BrowserFailed(ex.Message)));
                    });
                }
            });
        }

        // The Busy footer's "Cancel": stop waiting, WITHOUT quitting. Cancels the shared session CTS (both racers bail),
        // drops the session reference so a later restart mints a clean one, and resets the login snapshot to LoggedOut —
        // which folds to SetupSignInPhase.Idle, so the sign-in page lands back on its two option cards rather than on a
        // frozen "Signing in…". Wired onto the session below beside RestartCode/QuitApp.
        void CancelSignIn()
        {
            loginSession.Value?.Cancel();
            loginSession.Value = null;
            bridge.ReportLogin(new LoginSnapshot(LoginPhase.LoggedOut));
        }

        void CloseApp()
        {
            loginSession.Value?.Cancel();
            Environment.Exit(0);   // the takeover is the whole window when logged out → Close quits Wavee
        }

        // The FAKE demo has no real auth: "Log in" just connects the fake session; "Get a new code" re-seeds a demo
        // challenge. This lets the SAME two-pane takeover model the logged-out → logged-in round-trip without a real backend.
        void FakeSignIn() => _ = _services.Session.ConnectAsync();
        void SeedDemoChallenge() => bridge.ReportLogin(new LoginSnapshot(LoginPhase.AwaitingApproval,
            new LoginChallenge("WAVE-DEMO", "https://spotify.com/pair", "https://spotify.com/pair?code=WAVEDEMO", DateTimeOffset.UtcNow.AddMinutes(15))));

        // ── Cache-first background resume (the fix's core wiring) ──────────────────────────────────────────────────
        // The old design only ever attempted a silent resume from INSIDE the sign-in wizard (SetupSignInPage owned the
        // request — see its own remarks) — which worked because a returning user with valid cached credentials sat on
        // the wizard the whole time anyway. Now that a stored credential means the SHELL mounts instead (needsSignIn
        // below), nothing would ever kick that resume off, so the app root does it directly the moment it decides not
        // to show a sign-in surface. Non-interactive, no challenge, no CTS shared with RestartCode/StartBrowser — those
        // only ever run once the wizard is ALREADY mounted (needsSignIn was already true to get there), so there is no
        // live sign-in surface for this to race. A rejection clears the stored credential itself (SpotifyLiveLogin) —
        // needsSignIn flips true on the next render and the wizard's own sign-in page takes over cleanly; a mere
        // network failure leaves the credential in place, which PlaybackBridge.ProjectAuthState folds to Offline
        // (shell stays up, nothing further to do here).
        //
        // Re-entrancy guarded, because this is no longer only the launch kick: it is also what the shell's chip invokes
        // (PlaybackBridge.SignIn, below) when a resume failed and left the shell Offline. Two concurrent
        // LiveSessionHost.StartAsync calls would race two go-live stacks onto one Services. The flag is UI-thread-only
        // (set before the Task, cleared through `post`), so no interlock is needed.
        void SilentResume()
        {
            if (resumeInFlight.Value) return;
            resumeInFlight.Value = true;
            // The chip's Reconnect starts from Offline/Failed; say "connecting" straight away so the press has an
            // immediate answer instead of leaving the failed phase — and so the chip itself swaps to "Connecting...".
            bridge.ReportLogin(new LoginSnapshot(LoginPhase.SilentResume));
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await Wavee.SpotifyLive.LiveSessionHost.StartAsync(_services, new WaveeLogger(_services.Log, "connect"),
                        System.Threading.CancellationToken.None, bridge.Progress(post), uiPost: post,
                        interactive: false, useBrowser: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _services.Log.Event(WaveeLogLevel.Warning, "connect", "resume.silent.failed", "Silent resume failed", ex: ex);
                    post(() => bridge.ReportLogin(new LoginSnapshot(LoginPhase.Failed, Error: Loc.Get(Strings.Auth.GenericError))));
                }
                finally { post(() => resumeInFlight.Value = false); }
            });
        }

        // THE sign-in/reconnect verb the shell chip presses. Assigned every render (a plain property, not a signal):
        // the real backend gets the silent resume; the fake/demo backend keeps its stub connect. Without this the chip
        // called ISpotifySession.ConnectAsync, which on the real backend reaches the SwitchableSession's pre-go-live
        // inner — the FakeSpotifySession — and authenticates the demo account over a real session.
        bridge.SignIn = Services.UseRealBackend ? SilentResume : FakeSignIn;

        Context.UseEffect(() =>
        {
            var auth = bridge.AuthState.Value;
            if (auth == ShellAuthState.Live) return;
            // Transport startup has an explicit offline conclusion too. This does not drive page readiness.
            _ = FinishConnectionIntent(auth == ShellAuthState.Connecting, _services.Data.Catalog.Epoch);
        });
        async System.Threading.Tasks.Task FinishConnectionIntent(bool connecting, long epoch)
        {
            try { await _services.Data.SetConnectionIntentAsync(connecting, epoch).ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Catalog startup transition failed: {0}", error); }
        }

        // -- Startup activation: a SCHEDULE, not a straight line ----------------------------------------------------
        // This one mount effect used to activate everything inline, and it measurably WAS the first frame. Native ARM64
        // tours showed the first completed frame at 109-143 ms with unaccounted=106-134 - i.e. essentially all of it
        // after submit, inside the engine's passive-effect drain - with `[playback.buckets] startup - bridge -
        // activated` landing +73..98 ms into that window and `[app] Online; shell up from cache` +17..24 ms after it.
        // What sat in there: a WinRT activation (SMTC), shell COM (CoCreateInstance(CLSID_TaskbarList) plus four
        // synchronous .ico loads; the Jump List's per-item CoCreateInstance(CLSID_ShellLink) chain ending in a
        // .customDestinations-ms disk write), ~9 HKCU writes plus one-or-two CoRegisterClassObject (the toast
        // activator), a Network List Manager Advise, and a DPAPI unprotect of the stored credential. None of that is
        // work a rendered frame has any business paying for.
        //
        // The engine's poster is NOT the fix by itself: it drains at the TOP of the next frame, so posting the same
        // blob only moves the stall. And almost none of this work is legal off the UI thread - those wrappers cache
        // raw, un-marshalled COM pointers and issue an unbalanced CoInitializeEx(APARTMENTTHREADED), which on a pool
        // thread flips a shared thread to a non-pumping STA (the engine says so in as many words), while
        // NetworkStatus.Subscribe on a bare pool thread returns a silently INERT subscription. So each step declares a
        // phase instead (see StartupActivation):
        //   Core   - inside THIS frame. Signal subscriptions, query bindings and field writes only: no disk, no
        //            registry, no COM, no WinRT, no credential store. The transport accepts commands from the end of
        //            this phase, and anything that arrived early is RELEASED there rather than dropped.
        //   Window - the same UI thread, but one step per POSTED DRAIN, for the HWND/apartment-affine OS surfaces. The
        //            engine's drain snapshots the queue length before it runs, so a step that posts the next one is
        //            guaranteed to land in a LATER drain instead of collapsing back into a single stall.
        //   Worker - the thread pool, for the steps with no affinity at all.
        // Order within a phase is list order, and two orderings are load-bearing: the toast activator must register
        // before the Jump List (the shell keys a custom destination list by AUMID, which is empty until then) and
        // before the release/daylist toast scheduling.
        //
        // -- The Window ladder is GATED, not fired the instant Run collects it -----------------------------------
        // A native tour with the ladder starting immediately still showed 124.8 ms of UI-thread time (toast-register
        // 4.2, smtc 40.8, taskbar 17.9, power-os 11.0, jumplist 50.8) landing in the first ~150 ms after the first
        // route — interleaved with the route's own first content frames (frame.slow seq=38 unaccounted=27.4ms and
        // seq=63 77.9ms) and stretching the startup reveal (navId=1 revealMs=122.4, target <=100 ms) even though none
        // of those steps are charged to any frame. None of SMTC/taskbar/jump-list/network-cost is needed until the
        // user actually reaches for it, so the ladder now waits for BOTH: the app's first-content-reveal signal
        // (PageRevealWatch.FirstContentRevealed — fires once, off the first route's page.reveal) AND one more posted
        // drain after it (the closest proxy to "the dispatch queue is idle" available without an engine-exposed queue
        // depth: posting again means whatever was already queued when reveal fired gets to run first, FIFO). A hard
        // fallback deadline starts it regardless after WindowLadderFallbackMs, so the OS surfaces always appear even
        // on a route that never reveals.
        Context.UseEffect(() =>
        {
            bool fakeChallenge = Diag.EnvFlag("WAVEE_FAKE_CHALLENGE");
            var activation = new StartupActivation(
                post: post,
                // ONE hand-off for the whole worker phase, so its steps stay sequential relative to each other.
                worker: static work => _ = System.Threading.Tasks.Task.Run(work),
                measured: static (step, phase, ms) =>
                    Wavee.Backend.PlaybackBucketDiagnostics.ActivationStep(step, phase.ToString(), ms),
                phaseCompleted: static (phase, steps, ms) =>
                    Wavee.Backend.PlaybackBucketDiagnostics.ActivationPhase(phase.ToString(), steps, ms),
                failed: static (step, error) =>
                    Wavee.Backend.PlaybackBucketDiagnostics.ActivationStepFailed(step, error),
                // The SAME gate the bridge holds: one owner for "core before commands", not two that can drift.
                commands: bridge.Commands,
                // The hard fallback: a Timer (off the UI thread — StartWindowLadder itself hops back via `post`), so
                // the ladder starts even if the first route never reveals (a stuck query, a crash-looping page).
                scheduleFallback: (callback, delayMs) =>
                    windowLadderFallback.Value = new System.Threading.Timer(_ => callback(), null,
                        dueTime: (int)delayMs, period: System.Threading.Timeout.Infinite),
                windowLadderFallbackMs: WindowLadderFallbackMs);

            // The reveal-triggered half of the race above. One more `post` after the reveal is the idle proxy (see the
            // remark above); StartWindowLadder is idempotent, so if the fallback timer already fired this is a no-op.
            // Unsubscribed on cleanup even though the app root never actually unmounts, for the same reason every
            // other static-event subscription in this file is — a static event outliving its one subscriber is a leak
            // by construction if that ever stops being true.
            void OnFirstContentRevealed() => post(activation.StartWindowLadder);
            PageRevealWatch.FirstContentRevealed += OnFirstContentRevealed;

            activation.Run(
            [
                // -- Core ------------------------------------------------------------------------------------------
                new StartupStep("volume", StartupAffinity.Core, () =>
                {
                    // Remember-volume: seed the slider before the first frame the user sees; the live session seeds the
                    // device announce/local host from the same setting (LiveSessionHost). Saved back below, debounced.
                    if (_services.Settings.Get(WaveeSettings.RememberVolume))
                        bridge.Volume.Value = Math.Clamp(_services.Settings.Get(WaveeSettings.SavedVolume), 0f, 1f);
                }),
                new StartupStep("bridge", StartupAffinity.Core, () => bridge.Activate(post)),
                // The toast activator's DISPATCHER (two field writes) - separate from its registration, which is a
                // window step below, so an activation arriving in the first milliseconds already has somewhere to land.
                new StartupStep("toast-dispatcher", StartupAffinity.Core, () => WaveeNativeBoot.InstallDispatcher(post)),
                new StartupStep("power", StartupAffinity.Core, () => PowerBridge.Attach(bridge, post, _services)),
                new StartupStep("bridges", StartupAffinity.Core, () =>
                {
                    libBridge.Activate(post);
                    friendsBridge.Activate(post);
                    notifications.Activate(post);
                    store.Activate(post);
                    _services.Sidebar.Activate(post);
                    _services.PinSync?.Activate(post);
                }),
                // The cover-colour plane bumps its epoch from background batch completions; art tiles subscribe to it,
                // so the bump has to land on the UI thread like every other bridge signal. Activating here ALSO
                // pre-warms the persisted colour table off-thread, so no art slot ever pays the cold disk read inside
                // Render() - the Activate call itself is a field write plus a Task.Run.
                new StartupStep("cover-colors", StartupAffinity.Core,
                    () => Wavee.SpotifyLive.CoverColorPlane.Current.Activate(post)),
                new StartupStep("timers", StartupAffinity.Core, () =>
                {
                    // Persist volume changes (local intents AND remote echoes both land on bridge.Volume) with a coarse
                    // poll - Peek is a plain field read, and the registry write happens only when the value moved.
                    volumeSaveTimer.Value ??= new System.Threading.Timer(_ =>
                    {
                        if (!_services.Settings.Get(WaveeSettings.RememberVolume)) return;
                        float v = bridge.Volume.Peek();
                        if (Math.Abs(v - _services.Settings.Get(WaveeSettings.SavedVolume)) > 0.004f)
                            _services.Settings.Set(WaveeSettings.SavedVolume, v);
                    }, null, dueTime: 2_000, period: 2_000);

                    // Persist app-zoom changes the same way (the SavedVolume shape above): the chords, the Ctrl+wheel
                    // hook and the palette all mutate FluentApp.Zoom without touching the store - a held-down Ctrl+= or
                    // a wheel spin must not write the registry once per rung. No Remember gate: zoom has no opt-out,
                    // the setting IS the memory. A Settings-page pick writes immediately at the picker and lands here
                    // as an already-equal no-op.
                    zoomSaveTimer.Value ??= new System.Threading.Timer(_ =>
                    {
                        float z = FluentApp.Zoom;
                        if (MathF.Abs(z - _services.Settings.Get(WaveeSettings.ZoomLevel)) > 0.004f)
                            _services.Settings.Set(WaveeSettings.ZoomLevel, z);
                    }, null, dueTime: 2_000, period: 2_000);

                    // Drive the MemoryGovernor from a periodic OS-memory-pressure poll. The Timer fires on a background
                    // thread but marshals Trim to the UI thread (post) so the UI-thread-affine detail caches shed
                    // safely. At rest (no pressure) it sheds nothing - each cache's LRU cap already bounds steady
                    // state; under real pressure it sheds further.
                    governorTimer.Value ??= new System.Threading.Timer(_ =>
                    {
                        var info = GC.GetGCMemoryInfo();
                        double load = info.HighMemoryLoadThresholdBytes > 0 ? (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes : 0.0;
                        var level = load >= 1.0 ? Wavee.Backend.Residency.MemoryPressure.Critical
                                  : load >= 0.85 ? Wavee.Backend.Residency.MemoryPressure.Moderate
                                  : Wavee.Backend.Residency.MemoryPressure.Normal;
                        post(() => _services.Residency.Trim(level));
                    }, null, dueTime: 30_000, period: 30_000);

                    // Publish the app-side census contributor (entity store + detail caches) for the engine's
                    // FG_MEM_DIAG [memcensus] block. Program's DiagnosticRun composes it into AppHost.GpuDetail once
                    // per launch; the string is built only when the census invokes the hook (census cadence, never per
                    // frame).
                    Services.MemCensusHook = () => _services.CensusLine();
                }),
                new StartupStep("login-seed", StartupAffinity.Core, () =>
                {
                    if (fakeChallenge)
                    {
                        // Deterministic login screenshots (no network): seed a canned pairing challenge so the takeover
                        // renders the marquee hero. The gate below forces the takeover whenever this flag is set.
                        bridge.ReportLogin(new LoginSnapshot(LoginPhase.AwaitingApproval,
                            new LoginChallenge("WZY5-Q6TX", "https://spotify.com/pair", "https://spotify.com/pair?code=WZY5Q6TX", DateTimeOffset.UtcNow.AddSeconds(872))));
                        _services.Log.Info("app", "WAVEE_FAKE_CHALLENGE: seeded a canned pairing challenge for the login takeover");
                    }
                    else if (!Services.UseRealBackend)
                    {
                        // Fake demo: connect the fake session instantly so the INITIAL launch lands on the shell (no
                        // takeover flash, --screenshot renders the shell). Playback is NOT auto-started - local playback
                        // is unsupported, so a play intent shows the "choose a remote device" toast; the bar rests at
                        // "Nothing playing". After a logout the gate shows the demo two-pane instead.
                        _ = _services.Session.ConnectAsync();
                        _services.Log.Info("app", "Demo backend; fake session started (playback remote-only)");
                    }
                }),

                // -- Window (UI thread, one step per posted drain) ---------------------------------------------------
                // FIRST, because both the Jump List and the scheduled toasts are keyed by the AUMID it publishes.
                // Measured (native tour, ladder starting immediately): 4.2 ms — well inside budget, one drain.
                new StartupStep("toast-register", StartupAffinity.Window, WaveeNativeBoot.Register),
                // SMTC was ONE 40.8 ms step; split into its four stages (see SystemMediaControlsBridge.ActivateStep),
                // each its own drain. Acquire (the WinRT GetForWindow activation) is expected to carry most of that
                // 40.8 ms; enable/seed/timeline are the three cheap tail calls, each well under budget on its own.
                new StartupStep("smtc-acquire", StartupAffinity.Window, bridge.ActivateMediaControls),
                new StartupStep("smtc-enable", StartupAffinity.Window, bridge.ActivateMediaControls),
                new StartupStep("smtc-seed", StartupAffinity.Window, bridge.ActivateMediaControls),
                new StartupStep("smtc-timeline", StartupAffinity.Window, bridge.ActivateMediaControls),
                // Taskbar was ONE 17.9 ms step; split into wiring (cheap: HWND + event subscriptions, icons already
                // resolved by the "taskbar-icons" Worker step below) and the actual shell apply
                // (ThumbBarAddButtons + overlay + progress — the one part TaskbarBridge.ActivateStep's remarks explain
                // cannot be split further from this file: the engine's ThumbButton only accepts an icon PATH, so
                // LoadImageW still runs inside the same apartment-bound call as ThumbBarAddButtons).
                new StartupStep("taskbar-wire", StartupAffinity.Window, bridge.ActivateTaskbar),
                new StartupStep("taskbar-apply", StartupAffinity.Window, bridge.ActivateTaskbar),
                new StartupStep("notifiers", StartupAffinity.Window, () =>
                {
                    // Scheduled pre-save release drops: attached after the library bridge exists (it reconciles off the
                    // saved-set signal, which fires once on subscribe and therefore doubles as the launch reconcile).
                    // After toast-register: both of these SCHEDULE Windows toasts, which needs the AUMID.
                    ReleaseNotifier.Attach(_services.Settings, libBridge, _services.PreRelease);
                    DaylistNotifier.Attach(_services.Settings, _services.Data.Catalog);
                }),
                new StartupStep("power-os", StartupAffinity.Window, PowerBridge.InstallOsHooks),
                // LAST of the window steps: the most expensive surface (an unavoidable, single O(items)
                // CoCreateInstance(ShellLink)+IPropertyStore chain inside JumpList.SetCategory plus a shell disk
                // write — see ActivateJumpList's remarks for why this file cannot split that call itself further) and
                // the least urgent - nobody can see a Jump List until they right-click the taskbar button. The
                // "jumplist-prep" Worker step already did the CPU-only title/route resolution, so this drain pays for
                // the COM call alone; expected well under the unprepared 50.8 ms.
                new StartupStep("jumplist", StartupAffinity.Window, bridge.ActivateJumpList),

                // -- Worker ------------------------------------------------------------------------------------------
                // Prep for the "taskbar-apply" / "jumplist" Window steps above: pure CPU/disk work with no HWND or COM
                // affinity, done here so those drains have as little left to do as the engine's API surface allows.
                new StartupStep("taskbar-icons", StartupAffinity.Worker, bridge.PrepareTaskbarIcons),
                new StartupStep("jumplist-prep", StartupAffinity.Worker, bridge.PrepareJumpList),
                // Cache-first shell: with a stored credential the shell ALREADY mounted this frame (see needsSignIn
                // below - it reads the AuthState the bridge seeded at construction, which is already-in-memory state),
                // so the only thing left is to authenticate BEHIND it. The probe itself is a DPAPI unprotect plus a
                // file read, so it happens HERE and the decision comes back through the poster; with nothing on disk
                // there is nothing to resume and the sign-in surface is already mounted instead, its own SignIn page
                // owning the interactive device-code/browser flow exactly as before.
                new StartupStep("session-resume", StartupAffinity.Worker, () =>
                {
                    if (fakeChallenge || !Services.UseRealBackend) return;
                    bool stored = Wavee.SpotifyLive.SpotifyLiveLogin.HasStoredCredential();
                    post(() =>
                    {
                        if (stored)
                        {
                            SilentResume();
                            _services.Log.Info("app", "Online; shell up from cache, resuming the stored session silently.");
                        }
                        else
                        {
                            _services.Log.Info("app", "Online; no stored credential - the sign-in surface owns the Spotify login (device code / browser).");
                        }
                    });
                }),
                // Cache maintenance snapshots active query demand and runs through the shared data owner.
                new StartupStep("cache-gc", StartupAffinity.Worker, () => _services.CacheGc?.Start()),
                // The app-update poll (30 s after launch, then daily). Scheduled from the app root rather than the
                // composition root so it never runs in a headless/CLI process: activation is the moment a real UI
                // session exists. The updater is app-scoped (one per process) and `Settings` was seeded by the
                // composition root long before this. A Timer registration, so it has no thread affinity at all.
                new StartupStep("update-poll", StartupAffinity.Worker, () =>
                {
                    if (!OperatingSystem.IsWindowsVersionAtLeast(8, 0)) return;
                    if (AppInstallerUpdateService.Instance is { } updater && bridge.Settings is { } updateSettings)
                        AppUpdateScheduler.Start(updater, updateSettings);
                }),
            ]);

            return (Action?)(() => PageRevealWatch.FirstContentRevealed -= OnFirstContentRevealed);
        }, DepKey.Empty);

        // Keep-awake is edge-triggered off IsPlaying + VideoSurface. Auto-tracked so those reads subscribe THIS
        // effect, not the app-root render (a play/pause must not re-render the shell).
        Context.UseEffect(PowerBridge.SyncFromSignals);

        // ── The login GATE's booleans, computed early ───────────────────────────────────────────────────────────────
        // `authed` keeps its ORIGINAL meaning exactly — "fully signed in" (profile chip, "is this you" logic, the
        // fake-demo's post-first-auth logout handling) — because other code below still needs that precise question
        // answered, WAVEE_FAKE_CHALLENGE and the fake-demo's own bootstrap-order quirk included.
        //
        // Computed HERE (not down at the gate itself, as before) because both the setup wizard's pre-auth mount below AND
        // the device-code restart effect right after it need to know "authenticated yet?" before the gate is built.
        var authState = bridge.Auth.Value;   // subscribe → re-run on the flip
        bool authed = !Diag.EnvFlag("WAVEE_FAKE_CHALLENGE")
                   && (authState == AuthStatus.Authenticated || (!Services.UseRealBackend && !wasAuthed.Value));

        // `needsSignIn` is the thing that actually decides which LEAF mounts (below) — see the cache-first-shell design.
        // Old rule: mount the shell only once FULLY authenticated, so every relaunch with valid cached credentials sat
        // behind "Signing you in…" until the entire Spotify bootstrap finished. New rule: the shell mounts the moment
        // there is EITHER nothing left to prove (authed) OR something to authenticate BEHIND it (a stored credential a
        // resume can retry) — the sign-in surface is reserved for when auth is truly required and currently
        // unsatisfiable.
        //   • WAVEE_FAKE_CHALLENGE — preserved exactly: always forces the sign-in surface (deterministic screenshots).
        //   • Real backend — reads bridge.AuthState (subscribing) rather than re-deriving from HasStoredCredential()
        //     here: AuthState is the one signal ReportLogin/the StatusChanged handler ALWAYS refold on every relevant
        //     change (including a resume's credential getting REJECTED-and-cleared mid-flight — SpotifyLiveLogin wipes
        //     the blob, ProjectAuthState folds that to SignInRequired the same tick), so this render is guaranteed to
        //     re-run the instant it matters. A plain HasStoredCredential() re-check here would be a non-reactive read —
        //     nothing subscribes THIS render to a disk write that happens off in LiveSessionHost — so a rejection could
        //     land and never trigger the re-render that was supposed to swap the leaf.
        //   • Fake/demo backend — preserved exactly: unchanged from the old `!authed` (the demo's own two-pane governs
        //     logged-out, with no stored-credential concept of its own; see AuthState's remarks for why it must NOT be
        //     consulted here for the fake backend — its "no takeover flash" trick relies on `authed` alone).
        bool needsSignIn = Diag.EnvFlag("WAVEE_FAKE_CHALLENGE")
            || (Services.UseRealBackend
                ? bridge.AuthState.Value == ShellAuthState.SignInRequired
                : !authed);

        // ── The first-run setup wizard's PRE-AUTH mount ──────────────────────────────────────────────────────────────
        // needsSignIn ⇒ SetupPreAuthRoot is the whole window (it IS the sign-in surface). Reads
        // SetupSession.MarkerEpoch (subscribing) so a completion burned by SetupDialog.Open's ClosedAction — the marker
        // discipline, see that method — makes THIS re-evaluate immediately rather than waiting on an unrelated re-render.
        _ = SetupSession.MarkerEpoch.Value;   // subscribe
        SetupSession? setupSession = null;
        if (needsSignIn)
        {
            // The wizard is Wavee's ONE sign-in surface. There is deliberately no second standalone login takeover:
            // shipping both meant the same action looked different in two places, and "Quit" dropped the user from
            // the wizard into the other one — the exact duplication this design exists to remove.
            //   • setup never completed  ⇒ FirstRun, Welcome (+terms) → Sign in → Local playback.
            //   • setup completed, signed out (a logout, or a revoked token) ⇒ Reauth, straight to the SignIn page.
            //     Re-walking Welcome/terms for someone who already accepted them would be nonsense. A COMPLETED,
            //     still-SIGNED-IN install re-armed for new terms (SetupGating.NeedsTermsRearm) never reaches this
            //     branch at all — needsSignIn is false for it, so SetupChrome builds that TermsRearm session
            //     post-auth instead (Features/Setup/SetupChrome.cs).
            bool completed = SetupGating.IsCompleted(_services.Settings);
            setupSession = SetupSession.Current ??= completed
                ? new SetupSession(SetupEntryPoint.Reauth, alreadyAuthenticated: false, SetupPage.SignIn)
                : new SetupSession(SetupEntryPoint.FirstRun, alreadyAuthenticated: false);
            // Publish this run's real intents into the session's auto-properties so they are non-null wherever it is
            // mounted (pre-auth here, or post-auth in SetupChrome after SignIn completes — same instance, carried via
            // SetupSession.Current). Re-assigning every render is harmless: plain fields, not signals.
            // Real backend: the PKCE browser hand-off + device-code re-mint. Fake/demo backend: the same two intents
            // mapped onto its stubs, so the wizard's sign-in page works there too now that it is the only surface.
            setupSession.StartBrowser = Services.UseRealBackend ? StartBrowser : FakeSignIn;
            setupSession.RestartCode = Services.UseRealBackend ? RestartCode : SeedDemoChallenge;
            setupSession.CancelSignIn = CancelSignIn;
            setupSession.QuitApp = CloseApp;
            // "Not me" / "Not you? Switch account": the same sign-out the profile menu uses (credential wiped, gate
            // flips to LoggedOut, the wizard re-mints a pairing code). Fake backend: Switchable.LogoutAsync flips its stub.
            setupSession.SwitchAccount = () => _ = _services.LogoutAsync();
        }

        // Remember a successful fake/demo authentication so a later logout enters the re-auth wizard. Challenge startup
        // belongs to SetupSignInPage itself: that component knows when its keep-alive page is actually active, and owning
        // the request there prevents both premature expiry and competing root/page restarts.
        Context.UseEffect(() =>
        {
            if (Diag.EnvFlag("WAVEE_FAKE_CHALLENGE")) return;
            if (authState == AuthStatus.Authenticated) wasAuthed.Value = true;
        }, (int)authState);

        this.UseSoftReveal(); // app entrance (compositor-only, reduced-motion-aware)

        // ── The login GATE (cache-first shell) ──────────────────────────────────────────────────────────────────────
        // Providers stay ABOVE the gate so the bridges' subscriptions survive the pre-auth-wizard ↔ shell swap (and
        // back, on logout). TWO leaves, not three: needsSignIn ⇒ the setup wizard, which owns sign-in; otherwise ⇒ the
        // shell — which is now the leaf for "fully authenticated" AND "authenticating behind it" AND "offline with a
        // cached library" alike (see needsSignIn's remarks above and PlaybackBridge.ShellAuthState). Every relaunch
        // WITH valid cached credentials lands here instead of behind a blocking "Signing you in…" screen. The old
        // standalone LoginView takeover component is DELETED — only its shared building blocks survive, as the statics
        // the wizard's SignIn page composes (LoginView.cs: CompactRightPane, OrDivider, BrowserLoginButton, GlyphBadge,
        // LoginStepRow/Bar, WaitingDots, LoginCountdown, OpenUrl).
        Element leaf = needsSignIn
            ? Embed.Comp(() => new SetupPreAuthRoot(setupSession!, _services.Settings))
            : Embed.Comp(() => new WaveeShell(_services.Settings, _services.Sidebar));

        var root = Ctx.Provide(Services.Slot, _services,
            Ctx.Provide(PlaybackBridge.Slot, bridge,
            Ctx.Provide(LibraryBridge.Slot, libBridge,
            Ctx.Provide(FriendsBridge.Slot, friendsBridge,
            Ctx.Provide(NotificationCenterBridge.Slot, notifications,
            Ctx.Provide(LibraryStore.Slot, store,
            // The sidebar design + per-design state + shared pin store. Provided at the APP ROOT (above the login gate) so
            // the Settings page, the customizer route and the pin actions all read the SAME reference-stable instance, and
            // so the pin store / undo stack survive the takeover ↔ shell swap.
            Ctx.Provide(SidebarPreferences.Slot, _services.Sidebar,
            Ctx.Provide(HomePreferences.Slot, _services.Home,
                leaf))))))));

        // The FPS HUD, pinned top-right by a full-bleed PASS-THROUGH positioner (a PLAIN BoxEl — its HitTestPassThrough
        // IS honoured, unlike a component wrapper's mirrored-but-not-passthrough node, which would swallow every hit and
        // silently kill scrolling). ZStack carries Grow=1 to fill the window + stretch the shell exactly like the
        // OverlayHost stack.
        //
        // Two SETTINGS gate it, not an environment variable: Developer mode has to be on AND the overlay toggled on
        // (Settings › Diagnostics). A relaunch-with-an-env-var is not a feature a user can find, and it cannot be turned
        // back off without another relaunch. Both reads happen inside Render, so the DeveloperMode signal subscribes
        // this component and flipping the switch re-renders the root immediately.
        if (!(DeveloperMode.Enabled.Value && DeveloperMode.FpsOverlay.Value)) return root;
        var hud = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.End,
            Padding = new Edges4(0f, 104f, 14f, 0f),   // clear the title bar + toolbar; pinned top-right of the content
            Children = [ Embed.Comp(() => new FpsOverlay()) ],
        };
        return Ui.ZStack(root, hud) with { Grow = 1f };
    }
}
