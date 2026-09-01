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
                Tok.Use(WaveeTheme.ResolvePalette(_services.Settings.Get(WaveeSettings.PaletteId)), kind);
                if (FluentApp.SystemAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
                else if (FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
                // Windows can broadcast ImmersiveColorSet without an effective palette/accent change. Requesting a
                // transition in that case still forces RethemeAll, re-rendering the entire mounted app for identical
                // colors. Only arm the cross-fade when a guarded Tok mutator actually advanced the epoch.
                if (Tok.Epoch != oldEpoch) requestTheme?.Invoke(250f);
            }
            FluentApp.SystemColorsChanged += OnSystemColorsChanged;
            return () => FluentApp.SystemColorsChanged -= OnSystemColorsChanged;
        }, DepKey.Empty);

        var post = Context.UsePost();
        var loginSession = UseRef<System.Threading.CancellationTokenSource?>(null);
        var wasAuthed = UseRef(false);   // have we EVER authenticated this run? (fake demo: logout → takeover, but no initial-launch flash)
        var governorTimer = UseRef<System.Threading.Timer?>(null);   // rooted here so the periodic MemoryGovernor poll isn't GC-collected (the app root never unmounts)
        var volumeSaveTimer = UseRef<System.Threading.Timer?>(null); // remember-volume: debounced persist of the slider value
        var zoomSaveTimer = UseRef<System.Threading.Timer?>(null);   // app zoom: debounced persist of FluentApp.Zoom (chords/wheel never write the store themselves)

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
        void SilentResume() => _ = System.Threading.Tasks.Task.Run(async () =>
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
        });

        Context.UseEffect(() =>
        {
            // Remember-volume: seed the slider before the first frame the user sees; the live session seeds the device
            // announce/local host from the same setting (LiveSessionHost). Saved back below, debounced.
            if (_services.Settings.Get(WaveeSettings.RememberVolume))
                bridge.Volume.Value = Math.Clamp(_services.Settings.Get(WaveeSettings.SavedVolume), 0f, 1f);

            bridge.Activate(post);
            PowerBridge.Attach(bridge, post, _services);
            // Scheduled pre-save release drops: attached after the library bridge exists (it reconciles off the saved-set
            // signal, which fires once on subscribe and therefore doubles as the launch reconcile).
            ReleaseNotifier.Attach(_services.Settings, libBridge, _services.PreRelease);
            DaylistNotifier.Attach(_services.Settings);
            libBridge.Activate(post);
            friendsBridge.Activate(post);
            notifications.Activate(post);
            store.Activate(post);
            _services.Sidebar.Activate(post);
            // The cover-colour plane bumps its epoch from background batch completions; art tiles subscribe to it, so
            // the bump has to land on the UI thread like every other bridge signal. Activating here ALSO pre-warms the
            // persisted colour table off-thread, so no art slot ever pays the cold disk read inside Render().
            Wavee.SpotifyLive.CoverColorPlane.Current.Activate(post);

            // Persist volume changes (local intents AND remote echoes both land on bridge.Volume) with a coarse poll —
            // Peek is a plain field read, and the registry write happens only when the value actually moved.
            volumeSaveTimer.Value ??= new System.Threading.Timer(_ =>
            {
                if (!_services.Settings.Get(WaveeSettings.RememberVolume)) return;
                float v = bridge.Volume.Peek();
                if (Math.Abs(v - _services.Settings.Get(WaveeSettings.SavedVolume)) > 0.004f)
                    _services.Settings.Set(WaveeSettings.SavedVolume, v);
            }, null, dueTime: 2_000, period: 2_000);

            // Persist app-zoom changes the same way (the SavedVolume shape above): the chords, the Ctrl+wheel hook and
            // the palette all mutate FluentApp.Zoom without touching the store — a held-down Ctrl+= or a wheel spin
            // must not write the registry once per rung. No Remember gate: zoom has no opt-out, the setting IS the
            // memory. A Settings-page pick writes immediately at the picker and lands here as an already-equal no-op.
            zoomSaveTimer.Value ??= new System.Threading.Timer(_ =>
            {
                float z = FluentApp.Zoom;
                if (MathF.Abs(z - _services.Settings.Get(WaveeSettings.ZoomLevel)) > 0.004f)
                    _services.Settings.Set(WaveeSettings.ZoomLevel, z);
            }, null, dueTime: 2_000, period: 2_000);

            // Publish the app-side census contributor (entity store + detail caches) for the engine's FG_MEM_DIAG
            // [memcensus] block. Program's DiagnosticRun composes it into AppHost.GpuDetail once per launch; the string is
            // built only when the census invokes the hook (census cadence, never per frame).
            Services.MemCensusHook = () => _services.CensusLine();

            // Drive the MemoryGovernor from a periodic OS-memory-pressure poll. The Timer fires on a background thread but
            // marshals Trim to the UI thread (post) so the UI-thread-affine detail caches shed safely. At rest (no pressure)
            // it sheds nothing — each cache's LRU cap already bounds steady state; under real pressure it sheds further.
            governorTimer.Value ??= new System.Threading.Timer(_ =>
            {
                var info = GC.GetGCMemoryInfo();
                double load = info.HighMemoryLoadThresholdBytes > 0 ? (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes : 0.0;
                var level = load >= 1.0 ? Wavee.Backend.Residency.MemoryPressure.Critical
                          : load >= 0.85 ? Wavee.Backend.Residency.MemoryPressure.Moderate
                          : Wavee.Backend.Residency.MemoryPressure.Normal;
                post(() => _services.Residency.Trim(level));
            }, null, dueTime: 30_000, period: 30_000);

            // Arm the metadata-cache GC (design §C) with the SAME UI-thread marshaller the governor poll uses above: it
            // must snapshot Services.BuildPinSet on the UI thread (the detail caches are UI-thread-affine — critique
            // #10) before every pass. The sequence itself — warm → +30 s → GC → one-time VACUUM → every 6 h — runs off
            // the UI thread inside EntityCacheGc, so nothing here touches first paint. Null on the fake backend.
            _services.CacheGc?.Start(post);

            if (Diag.EnvFlag("WAVEE_FAKE_CHALLENGE"))
            {
                // Deterministic login screenshots (no network): seed a canned pairing challenge so the takeover renders the
                // marquee hero. The gate below forces the takeover whenever this flag is set.
                bridge.ReportLogin(new LoginSnapshot(LoginPhase.AwaitingApproval,
                    new LoginChallenge("WZY5-Q6TX", "https://spotify.com/pair", "https://spotify.com/pair?code=WZY5Q6TX", DateTimeOffset.UtcNow.AddSeconds(872))));
                _services.Log.Info("app", "WAVEE_FAKE_CHALLENGE: seeded a canned pairing challenge for the login takeover");
            }
            else if (Services.UseRealBackend)
            {
                // Cache-first shell: with a stored credential the shell already mounted this frame (see needsSignIn
                // below) — kick the silent resume that authenticates BEHIND it. With none on disk there is nothing to
                // resume; the sign-in surface is already mounted instead, and its own SignIn page owns the interactive
                // device-code/browser flow exactly as before.
                if (Wavee.SpotifyLive.SpotifyLiveLogin.HasStoredCredential())
                {
                    SilentResume();
                    _services.Log.Info("app", "Online; shell up from cache, resuming the stored session silently.");
                }
                else
                {
                    _services.Log.Info("app", "Online; no stored credential — the sign-in surface owns the Spotify login (device code / browser).");
                }
            }
            else
            {
                // Fake demo: connect the fake session instantly so the INITIAL launch lands on the shell (no takeover flash,
                // --screenshot renders the shell). Playback is NOT auto-started — local playback is unsupported, so a play
                // intent shows the "choose a remote device" toast; the bar rests at "Nothing playing". After a logout the
                // gate shows the demo two-pane instead.
                _ = _services.Session.ConnectAsync();
                _services.Log.Info("app", "Demo backend; fake session started (playback remote-only)");
            }
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
            // shipping both meant the same action looked different in two places, and "Not now" dropped the user from
            // the wizard into the other one — the exact duplication this design exists to remove.
            //   • setup never completed  ⇒ FirstRun, all seven steps from Welcome.
            //   • setup completed, signed out (a logout, or a revoked token) ⇒ Reauth, straight to the SignIn page.
            //     Re-walking terms/appearance/sidebar for someone who already chose them would be nonsense.
            bool completed = SetupGating.IsCompleted(_services.Settings);
            setupSession = SetupSession.Current ??= completed
                ? new SetupSession(SetupSession.EntryPoint.Reauth, alreadyAuthenticated: false, SetupPage.SignIn)
                : new SetupSession(SetupSession.EntryPoint.FirstRun, alreadyAuthenticated: false);
            // Publish this run's real intents into the session's auto-properties so they are non-null wherever it is
            // mounted (pre-auth here, or post-auth in SetupChrome after SignIn completes — same instance, carried via
            // SetupSession.Current). Re-assigning every render is harmless: plain fields, not signals.
            // Real backend: the PKCE browser hand-off + device-code re-mint. Fake/demo backend: the same two intents
            // mapped onto its stubs, so the wizard's sign-in page works there too now that it is the only surface.
            setupSession.StartBrowser = Services.UseRealBackend ? StartBrowser : FakeSignIn;
            setupSession.RestartCode = Services.UseRealBackend ? RestartCode : SeedDemoChallenge;
            setupSession.CancelSignIn = CancelSignIn;
            setupSession.QuitApp = CloseApp;
            // "Not me" on the Is-this-you confirmation: the same sign-out the profile menu uses (credential wiped, gate
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
