using System.IO;
using FluentGpu;               // FluentApp
using FluentGpu.Dsl;           // Theme (startup theme seed)
using FluentGpu.Foundation;    // Diag
using FluentGpu.Localization;  // Loc (the crash dialog's copy)
using FluentGpu.WindowsApi.Activation;
using FluentGpu.WindowsApi.Packaging;   // PackageIdentity (packaged builds get protocol/startup from the manifest)

namespace Wavee;

// Wavee Music — composition root. Installs observability + the global crash net BEFORE the window comes up, then runs
// the engine. FluentApp.Run (FluentGpu.Windows/Hosting/FluentApp.cs) brings up the DPI-aware window, the D3D12 device,
// Mica + the real OS accent, DirectWrite, and the full image pipeline + smooth scroll.
static class Program
{
    static WaveeLogger CliLog(string category) => new(WaveeLog.Instance, category);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static extern bool AttachConsole(int dwProcessId);

    // A WinExe (GUI subsystem) gets NO console on a bare terminal launch, so headless-CLI output prints to nowhere. Attach
    // the PARENT terminal's console + re-point Console.Out/Error at it, so --spotify-metadata / --spotify-login output —
    // including the interactive device-code prompt — is visible. No-op (returns false) on a normal GUI launch.
    static void AttachParentConsole()
    {
        if (!AttachConsole(-1)) return;   // -1 = ATTACH_PARENT_PROCESS
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    // [STAThread]: the GUI thread must be an STA apartment — file pickers / SMTC / taskbar are STA-only coclasses.
    [STAThread]
    static void Main(string[] args)
    {
        // ── `--relaunch-after <pid>` — the restart broker (spawned by AppRelaunch.RestartAfterExit) ─────────────────
        // This arm is FIRST on purpose, ahead of FactoryReset.ApplyIfPending: the broker is a courier, not an app
        // instance. It waits for the process that spawned it to exit (which is what drops the single-instance mutex and
        // unlocks library.db), starts a fresh Wavee, and exits. Applying the pending wipe here would consume the marker
        // that the real relaunch has to act on, and would run the wipe while the old process still holds those files.
        int relaunchIdx = Array.IndexOf(args, "--relaunch-after");
        if (relaunchIdx >= 0)
        {
            if (relaunchIdx + 1 < args.Length && int.TryParse(args[relaunchIdx + 1], out int parentPid))
            {
                // 15 s ceiling: a hung parent must not strand the user without a window. ArgumentException = the pid is
                // already gone (the common case — it exited while we were starting), which is exactly what we waited for.
                try { System.Diagnostics.Process.GetProcessById(parentPid).WaitForExit(15_000); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            try
            {
                if (PackageIdentity.IsPackaged && PackageIdentity.ApplicationUserModelId is { Length: > 0 } aumid)
                    // A packaged build must be re-activated through the shell by AUMID; starting the exe directly gives
                    // the new process no package identity (no MSIX activation, no manifest protocol/startup registration).
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = true });
                else if (Environment.ProcessPath is { Length: > 0 } selfExe)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(selfExe) { UseShellExecute = true });
            }
            catch { /* a factory-reset marker (if any) is on disk — a manual launch still applies the wipe */ }
            Environment.Exit(0);
        }

        // Factory-reset wipe MUST run before settings / logs / library.db open. The previous process only armed a
        // marker (files were still locked); this process is a clean first launch afterwards.
        FactoryReset.ApplyIfPending();

        // CLI flags below print to the console; a WinExe has none on a bare terminal launch, so attach the parent's first
        // (otherwise a --spotify-* run — incl. the device-code prompt — runs invisibly and looks "stuck").
        if (args.Length > 0 && OperatingSystem.IsWindows()) AttachParentConsole();

        // ── Observability ───────────────────────────────────────────────────────────────────────────────────────────
        // Settings are hoisted above Configure so the persisted log-level overrides seed it (env still wins inside
        // Configure). One instance, reused for theme/backend below.
        var settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        // Launch-scoped by design: the Settings picker persists a new value, and the next process applies it atomically
        // to UI strings, Spotify metadata requests, and locale-partitioned caches.
        AppLocale appLocale = AppLocaleBootstrap.Initialize(settings);
        // Fresh-install probe + the legacy sidebar pane-key migration (F.3.3 + F.4.3). MUST run before anything constructs
        // Services / opens library.db — WaveeApp's ctor does, and probing after that point would make every install look
        // "existing". Settings writes only; the one-time chooser is armed via WaveeSettings.SidebarOnboardingSeen and shown
        // later by SidebarOnboardingChrome, once the shell has painted.
        SidebarBootstrap.Run(settings);
        // First-run setup wizard (App/SetupBootstrap.cs): same ordering rule as SidebarBootstrap above, and reuses its
        // fresh-install probe so the two features can never disagree about whether this install is fresh. Settings
        // writes only; the wizard itself is shown later by the shell once it has painted, gated on SetupPending.
        SetupBootstrap.Run(settings);
        // Developer mode (App/DeveloperMode.cs) is a process-wide latch read by the diagnostic surfaces. Load it here,
        // before anything can ask, so a single settings read decides it for the whole launch.
        DeveloperMode.Load(settings);
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "logs");
        string logPath = Path.Combine(logDir, "wavee.log");
#if DEBUG
        // Dev default: the in-memory ring keeps full Debug detail for the in-app viewer, but the FILE defaults to Info so a
        // dev run doesn't bloat wavee.log with the demoted verbose flow. The settings-backed level UI (or WAVEE_LOG_FILE_LEVEL)
        // can raise the file level to Debug/Trace on demand.
        WaveeLogLevel defaultLevel = WaveeLogLevel.Debug;
        WaveeLogLevel defaultFileLevel = WaveeLogLevel.Info;
#else
        WaveeLogLevel defaultLevel = WaveeLogLevel.Info;
        WaveeLogLevel defaultFileLevel = WaveeLogLevel.Info;
#endif
        int minSetting = settings.Get(WaveeSettings.LogMinLevel);
        int fileSetting = settings.Get(WaveeSettings.LogFileMinLevel);
        // dailyRolling: the main app log splits into wavee-yyyyMMdd.log per calendar day (the WaveeMusic scheme) —
        // the old single ever-growing wavee.log is migrated into the dated set on first launch.
        WaveeLog.Instance.Configure(crashLogPath: logPath, echo: DebugEcho(),
            minLevel: minSetting >= 0 ? (WaveeLogLevel)minSetting : defaultLevel,
            fileMinLevel: fileSetting >= 0 ? (WaveeLogLevel)fileSetting : defaultFileLevel,
            dailyRolling: true);
        // The dealer firehose archive is opt-in (Settings › Diagnostics): it writes every dealer frame to disk, which is
        // invaluable when reproducing a sync bug and pure cost otherwise. The directory is always configured so turning
        // the setting on mid-session has somewhere to write.
        DealerArchive.Instance.Configure(Path.Combine(logDir, "dealer"), settings.Get(WaveeSettings.DealerArchiveEnabled));
        Diag.Sink = WaveeLog.DiagSink;                 // fold engine diagnostics (FG_DIAG) into the app log stream
        WaveeLog.Instance.Info("app", "startup", "Wavee starting",
            WaveeLogField.Of("pid", Environment.ProcessId),
            WaveeLogField.Of("args", args.Length),
            WaveeLogField.Of("log", logPath),
            // Where that path REALLY lands (MSIX redirects %LOCALAPPDATA% into the package's LocalCache; an unpackaged
            // run, or a real folder that already exists, writes the literal path). The two look identical above and
            // differ in every consequence — a split settings/log store reads as "the app forgot everything".
            WaveeLogField.Of("logResolved", FluentGpu.WindowsApi.Storage.FinalPath.Resolve(logDir) ?? "?"),
            WaveeLogField.Of("dealerArchive", Path.Combine(logDir, "dealer")),
            WaveeLogField.Of("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            WaveeLogField.Of("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription));
        // `--relaunched-after-update` is appended to the restart command line PackageUpdater hands
        // RegisterApplicationRestart, so the process Windows brings back after a deployment can say so. It is otherwise
        // INERT: nothing branches on it. (RegisterApplicationRestart(null, 0) reuses the original command line, which
        // made the relaunched process indistinguishable from a normal launch — you could not tell from the log whether
        // an update had just been applied.) It parses as neither a URI nor a path, so ActivationArgs.Classify still
        // reports ActivationKind.Launch and the single-instance / deep-link path is unaffected.
        if (Array.IndexOf(args, AppRelaunch.RelaunchedAfterUpdateFlag) >= 0)
            WaveeLog.Instance.Info("app", "startup", "relaunched by Windows after an update");

        // ── Global crash net (the two process-level handlers; the UI-thread one lives in the engine loop) ─────────────
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WaveeLog.Instance.Critical("crash", $"Unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
            WaveeLog.Instance.Flush();
            DealerArchive.Instance.Flush();
            // Same treatment as the app-loop catch below: a crash on ANY thread leaves a report on disk and arms the
            // pending-report key, so the NEXT launch can offer it. Everything here is best-effort — we are terminating.
            try
            {
                if (e.ExceptionObject is Exception fatal)
                {
                    string report = CrashReport.Write(fatal, logPath);
                    if (report.Length > 0) settings.Set(WaveeSettings.PendingCrashReport, report);
                }
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Describe() adds the build stamp, module base and per-frame RVAs, so an offset-only NativeAOT trace from a
            // background task is symbolicatable the same way a terminating crash is.
            WaveeLog.Instance.Error("crash", "Unobserved task exception\n" + CrashReport.Describe(e.Exception), e.Exception);
            WaveeLog.Instance.Flush();
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { DealerArchive.Instance.Flush(); } catch { }
        };

        if (Array.IndexOf(args, "--perf-bench") >= 0)
            Environment.SetEnvironmentVariable("WAVEE_PERF_BENCH", "1");
        if (Array.IndexOf(args, "--startup-bench") >= 0)
            Environment.SetEnvironmentVariable("WAVEE_STARTUP_BENCH", "1");

        int frames = -1;
        string? screenshot = null;
        // --width/--height override the startup client size. They exist for the screenshot loop: a responsive page's
        // breakpoints can only be verified by capturing AT each width, and the alternative is dragging the window by hand.
        int winW = 1180, winH = 760;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int f)) frames = f;
            if (args[i] == "--screenshot" && i + 1 < args.Length) screenshot = args[i + 1];
            if (args[i] == "--width" && i + 1 < args.Length && int.TryParse(args[i + 1], out int ww) && ww >= 300) winW = ww;
            if (args[i] == "--height" && i + 1 < args.Length && int.TryParse(args[i + 1], out int wh) && wh >= 300) winH = wh;
        }

        // Headless backend-engine self-test (no window): exercises the five backend engines and exits 0 (pass) / 1 (fail).
        if (Array.IndexOf(args, "--backend-selftest") >= 0)
        {
            int code = Wavee.Backend.BackendSelfTest.Run(CliLog("probe"));
            Environment.Exit(code);
        }

        // QR diagnostic: encode [text] with the REAL Qr encoder → ASCII + a crisp PNG (isolates the encoder from the GUI
        // renderer). Usage: --qr-dump [text] [outpath.png]
        int qrIdx = Array.IndexOf(args, "--qr-dump");
        if (qrIdx >= 0)
        {
            string qtext = qrIdx + 1 < args.Length && !args[qrIdx + 1].StartsWith("--") ? args[qrIdx + 1] : "https://spotify.com/pair";
            string qpath = qrIdx + 2 < args.Length && !args[qrIdx + 2].StartsWith("--") ? args[qrIdx + 2] : "qr.png";
            Environment.Exit(QrDump.Run(qtext, qpath, CliLog("probe")));
        }

        // Headless LIVE Spotify login (real network): OAuth device-code → AP handshake + login → APWelcome.
        if (Array.IndexOf(args, "--spotify-login") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLiveLogin.RunAsync(CliLog("auth"), cts.Token).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE metadata round-trip: login -> login5 -> client-token -> spclient extended-metadata for one URI -> print.
        // Usage: --spotify-metadata [spotify:track:... | spotify:album:... | spotify:artist:...] (defaults to a known track).
        int metaIdx = Array.IndexOf(args, "--spotify-metadata");
        if (metaIdx >= 0)
        {
            string uri = metaIdx + 1 < args.Length && !args[metaIdx + 1].StartsWith("--")
                ? args[metaIdx + 1]
                : "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyMetadataProbe.RunAsync(uri, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE RAW music-video manifest dump: login -> resolve the track's manifest_id (TrackV4 OriginalVideo, else the
        // VIDEO_ASSOCIATIONS counterpart) -> GET /manifests/v9/json/sources/{id}/options/supports_drm -> print every
        // profile / encryption_info / template field AS SERVED + a VERDICT on whether an AUDIO profile exists (the parsed
        // model drops audio-only profiles, so only the raw body can answer it) + a JSON dump under %LOCALAPPDATA%\Wavee\diag.
        // Usage: --spotify-video-manifest [spotify:track:...] (defaults to Blinding Lights — a mainstream track that should
        // carry a music video). Exit 3 = that track has no video at all, so pass one that does.
        int vidIdx = Array.IndexOf(args, "--spotify-video-manifest");
        if (vidIdx >= 0)
        {
            string uri = vidIdx + 1 < args.Length && !args[vidIdx + 1].StartsWith("--")
                ? args[vidIdx + 1]
                : "spotify:track:0VjIjW4GlUZAMYd2vXMi3b";
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyVideoManifestProbe.RunAsync(uri, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE has-video signal probe: fetch extended-metadata kinds 99 (VIDEO_ASSOCIATIONS), 85 (ORIGINAL_VIDEO),
        // 182 (CONSUMPTION_EXPERIENCE_TRAIT), 212 (PLAYBACK_TRAIT) for known SAZ hit/miss tracks (+ optional CLI URIs),
        // dump wire fields, and print concordance vs kind-99. Settles whether 182/212 are usable has-video signals.
        // Usage: --spotify-video-traits [spotify:track:... ...]
        int traitsIdx = Array.IndexOf(args, "--spotify-video-traits");
        if (traitsIdx >= 0)
        {
            var extra = new System.Collections.Generic.List<string>();
            for (int i = traitsIdx + 1; i < args.Length && !args[i].StartsWith("--"); i++) extra.Add(args[i]);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyVideoTraitProbe.RunAsync(extra, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE playlist membership round-trip: login -> GET /playlist/v2 -> thin header + ordered membership -> hydrate -> print.
        // Usage: --spotify-playlist spotify:playlist:<id>
        int plIdx = Array.IndexOf(args, "--spotify-playlist");
        if (plIdx >= 0)
        {
            string uri = plIdx + 1 < args.Length && !args[plIdx + 1].StartsWith("--") ? args[plIdx + 1] : "";
            if (uri.Length == 0) { Console.Error.WriteLine("usage: --spotify-playlist spotify:playlist:<id>"); Environment.Exit(2); }
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunPlaylistAsync(uri, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE rootlist round-trip: login -> GET /playlist/v2/user/{me}/rootlist -> the folder/playlist tree -> print.
        if (Array.IndexOf(args, "--spotify-rootlist") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunRootlistAsync(CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE collection round-trip: login -> POST /collection/v2/{paging|delta} for the set's WIRE set (CollectionSets.WireSet:
        // liked+albums both walk "collection") -> set membership -> hydrate -> print the logical set.
        // Usage: --spotify-collection [liked|albums|artists|shows|episodes] (defaults to liked).
        int colIdx = Array.IndexOf(args, "--spotify-collection");
        if (colIdx >= 0)
        {
            string setId = colIdx + 1 < args.Length && !args[colIdx + 1].StartsWith("--") ? args[colIdx + 1] : "liked";
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            int code = Wavee.SpotifyLive.SpotifyLibraryProbe.RunCollectionAsync(setId, CliLog("probe"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        // LIVE full library sync into the REAL persistent store (rootlist + all collections + hydrate + the dealer firehose).
        // After this, `--real-backend` reads the library offline from disk. Usage: --spotify-sync
        if (Array.IndexOf(args, "--spotify-sync") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
            int code = Wavee.SpotifyLive.SpotifyLibrarySync.RunAsync(CliLog("sync"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

#if WAVEE_PLAYPLAY_LOCAL
        if (Array.IndexOf(args, "--playplay-runtime-status") >= 0)
        {
            int code = Wavee.SpotifyLive.PlayPlayRuntimeProbe.RunStatus(args, CliLog("audio"));
            Environment.Exit(code);
        }

        int regIdx = Array.IndexOf(args, "--playplay-runtime-register");
        if (regIdx >= 0)
        {
            string dir = regIdx + 1 < args.Length && !args[regIdx + 1].StartsWith("--") ? args[regIdx + 1] : "";
            if (dir.Length == 0) { Console.Error.WriteLine("usage: --playplay-runtime-register <dir>"); Environment.Exit(2); }
            int code = Wavee.SpotifyLive.PlayPlayRuntimeProbe.RunRegister(dir, CliLog("audio"));
            Environment.Exit(code);
        }

        if (Array.IndexOf(args, "--playplay-runtime-check") >= 0)
        {
            int code = Wavee.SpotifyLive.PlayPlayRuntimeProbe.RunCheck(args, CliLog("audio"));
            Environment.Exit(code);
        }
#endif

        // LIVE Connect session bring-up demo: login -> dealer + AP channel -> swap the live playback backend into a REAL
        // Services (svc.GoLive) and log the now-playing the UI bridge sees through the switchable. Usage: --connect-live
        if (Array.IndexOf(args, "--connect-live") >= 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
            int code = Wavee.SpotifyLive.LiveSessionHost.RunAsync(CliLog("connect"), cts.Token, appLocale.SpotifyLanguage).GetAwaiter().GetResult();
            Environment.Exit(code);
        }

        if (StartupPreflight.TryGetBlockingIssue(out var startupIssueTitle, out var startupIssueBody))
        {
            WaveeLog.Instance.Critical("app", "startup preflight failed: " + startupIssueBody);
            StartupNotice.Error(startupIssueTitle, startupIssueBody + Environment.NewLine + Environment.NewLine + "Log: " + logPath);
            return;
        }

        // Seed the theme BEFORE the window comes up (no startup flash): honor the persisted preference, falling back to
        // the live OS theme for a fresh install (mode == System). FluentApp.Run then applies the matching Mica material
        // and the in-app surfaces mount with the right tokens; the store is reused by the app so there's one instance.
        CrashDumpProbe.LogPendingCrashDump(settings, WaveeLog.Instance);
        int themeMode = settings.Get(WaveeSettings.ThemeMode);
        var themeKind = themeMode switch { 1 => ThemeKind.Light, 2 => ThemeKind.Dark, _ => FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark };
        Tok.Use(WaveeTheme.ResolvePalette(settings.Get(WaveeSettings.PaletteId)), themeKind);

        // The icon face is the BUNDLED Segoe Fluent Icons file (assets/fonts/SegoeFluentIcons.ttf), not the system family
        // of the same name: the system one ships with Windows 11 only, and on Windows 10 every glyph added in Fluent Icons
        // (RefineSparkle, the "Tune" toolbar mark, RowSize, ...) drew as tofu. Set BEFORE FluentAppHarness.Run: the
        // AppHost ctor interns Theme.IconFont once for the overlay scrollbar arrows, and every control reads it at render.
        // A missing file is logged (not fatal): the engine's face loader falls back to the body font, so the app still
        // runs, with visibly wrong icons that this line explains.
        Theme.IconFont = WaveeFonts.Icons;
        WaveeLog.Instance.Info("app", "icon font: " + WaveeFonts.IconsPath + " exists=" + File.Exists(WaveeFonts.IconsPath));

        // ── Localization: load the bundled culture tables (assets/loc/*.json, copied next to the exe) before the first
        // frame, so every Loc.Get(Strings.*) resolves. en-US is the base + terminal fallback; more cultures drop in later.
        // Real (live Spotify) backend is the DEFAULT: the persistent Store-backed catalog + durable mutations, hydrated by
        // the live session (login → spclient fetchers → the hm:// dealer) that the login takeover starts on launch. Pass
        // --fake for the offline FakeData demo (populated UI with no login/network — used by --screenshot and UI iteration).
        Services.UseRealBackend = Array.IndexOf(args, "--fake") < 0;

        // NOTE: there is no pre-window premium gate. The account tier is a LOGIN-TIME fact (ProductInfo), so the tier
        // check lives where the tier is actually known — Backend/Seam.cs (GoLive), SpotifyAuthSession, SpotifyLiveLogin —
        // and a Free account gets a window and an in-app explanation rather than a message box before anything paints.

        // Single-instance + wavee:// protocol — normal windowed path only. Probes already returned above;
        // --screenshot / --frames (harness) skip the gate so a visual-diff loop can spawn freely.
        SingleInstanceGate? instanceGate = null;
        // WaveeNavProbe.Requested skips it too: a diagnostic probe drives its OWN window and must never be handed to a
        // Wavee the user already has open. Without this the probe process exits 0 having done nothing — which reads
        // exactly like "the probe ran and found nothing", the worst possible failure mode for a measurement tool.
        if (screenshot is null && frames < 0 && !WaveeNavProbe.Requested)
        {
            instanceGate = new SingleInstanceGate();
            var activation = ActivationArgs.FromCurrentProcess("wavee");
            string payload = activation.Kind == ActivationKind.Launch ? "" : activation.Argument;
            if (!instanceGate.TryAcquire("Wavee", "FluentGpuWindow", payload))
            {
                instanceGate.Dispose();
                return;
            }
            // Unpackaged only. A packaged (MSIX) build declares its protocols AND its startup task in the manifest, and
            // the OS owns both: writing the same HKCU keys from here would fight the manifest registration and leave a
            // stale association pointing at an install path that moves on every update.
            if (!PackageIdentity.IsPackaged)
            {
                try
                {
                    string? exe = Environment.ProcessPath;
                    if (exe is { Length: > 0 })
                        ProtocolRegistrar.RegisterProtocol("wavee", exe, "Wavee", iconPath: WaveeAppIcon.Path());
                    // The opt-in spotify: handler follows the setting in both directions, so turning it off in a previous
                    // session actually gives the scheme back rather than leaving a stale HKCU association behind.
                    DeepLink.SyncSpotifySchemeRegistration(settings.Get(WaveeSettings.HandleSpotifyLinks));
                    // Same both-directions contract for "start Wavee when I sign in".
                    DeepLink.SyncStartupRegistration(settings.Get(WaveeSettings.StartOnLogin));
                }
                catch (Exception ex)
                {
                    WaveeLog.Instance.Warn("app", "wavee:// protocol registration failed", ex);
                }
            }
            if (activation.Kind is ActivationKind.Protocol or ActivationKind.File or ActivationKind.ToastActivated)
                DeepLinkChannel.Post(activation.Argument);
            FluentApp.ActivationRedirected += raw =>
            {
                DeepLinkChannel.Post(raw);
                DeepLink.WakeWindow();
            };
        }

        try
        {
            // Diagnostic harness chain (each gated by its own env flag; all return false in a normal run): the nav/scroll
            // FPS stress probe (WAVEE_NAV_PROBE) first, then the resize probe (WAVEE_RESIZE_PROBE). DiagnosticRun is invoked
            // ONCE per launch (before the loop) regardless of flags, so it doubles as the app's hook to compose the
            // entity-store census (Services.MemCensusHook) into the engine's FG_MEM_DIAG [memcensus] GpuDetail line — the
            // only app-reachable point that holds the AppHost. The hook builds its string lazily (at census cadence).
            FluentApp.DiagnosticRun = (h, w, d) =>
            {
                if (d is FluentGpu.Rhi.D3D12.D3D12Device gpuDev)
                    h.GpuDetail = () =>
                    {
                        string gpu = gpuDev.DiagGpuDetail;
                        string app = Services.MemCensusHook?.Invoke() ?? "";
                        return app.Length == 0 ? gpu : gpu.Length == 0 ? app : gpu + " | " + app;
                    };
                // Bind the ambient-cadence policy (power + attention) to the host — same rationale as the census hook:
                // this is the only app-reachable point that holds the AppHost. AmbientPowerPolicy.Watcher (mounted in
                // WaveeShell) feeds it the focus edges and the debounced power poll from there on.
                AmbientPowerPolicy.Attach(h);
                WaveeStartupBench.NoteHost(h);
                return WaveeStartupBench.TryRun(h, w, d) || WaveePerfBench.TryRun(h, w, d) || WaveeNavProbe.TryRun(h, w, d) || WaveeResizeProbe.TryRun(h, w, d) || WaveeMemSoak.TryRun(h, w, d);
            };
            // customFrame:true → the in-app TitleBar (WaveeShell) draws the extended caption buttons + drag region.
            // micaAlt → Mica BaseAlt (DWMSBT_TABBEDWINDOW) unless the profile carries base Mica
            // (WaveeSettings.WindowMaterialBaseMica, now TRUE by default). Alt is a STRONGER tint of the desktop
            // wallpaper than base Mica (Microsoft's documented behaviour) — matching WaveeMusic's MicaBackdrop
            // Kind="BaseAlt" was the historical reason Alt was the default, and that parity is now REJECTED: the
            // visible result was an over-saturated navy chrome next to apps (Wino Mail) that default to
            // the neutral base Mica on the same wallpaper. The DWM material is visible through EVERY chrome band —
            // titlebar, sidebar, player dock — because WaveeShell's root is transparent and each band is a deliberate
            // paint-site omission over live Mica, not just under the login screen and the host fallback.
            // NO AmbientFps here any more (it used to hard-code 60): the pacing of PERPETUAL ambient motion — the
            // seek playhead, now-playing equalizer, skeleton shimmer, buffering spinner, karaoke lyrics wipe — is
            // AmbientPowerPolicy's call, attached above. Explicit ~30 fps always (HalfRefresh is avoided — on a 120 Hz
            // panel it is 60 fps, still too hot for Wavee's full-window Present). Ambient Present is a full-window
            // translucent pass on this engine, so Uncapped is never used (it disabled the adaptive-FPS governor and
            // let a 13 px equalizer pin ~56% GPU). Latency-sensitive input (scroll/hover/drag) and FrameClock consumers
            // (lyrics) stay at the display rate; FG_ANIM_FPS still overrides everything (=30 to pin a fixed cadence,
            // =0 for uncapped).
            FluentAppHarness.Run(() => new WaveeApp(settings, appLocale),
                new AppOptions
                {
                    // MinWidth 300 (DIP): the shell is verified sound below 360 — the detail surface is in vertical mode,
                    // the track table is at tier 6 and the hero artwork is at its 64-DIP floor — and 360 DIP was a hard
                    // ~564 physical-px floor at 150% DPI (300 → ~450) that stopped the window fitting a half-screen split.
                    Title = "Wavee Music", Width = winW, Height = winH,
                    MinWidth = 300, CustomFrame = true,
                    MicaAlt = !settings.Get(WaveeSettings.WindowMaterialBaseMica),
                    // The engine's decoded-image disk cache lands under Wavee's own app data (next to logs/ and the
                    // library), not in the engine's default location — one folder for "everything Wavee wrote", which is
                    // what the Storage settings tab measures and what factory reset wipes.
                    ImageCacheDirectory = Path.Combine(SettingsShared.AppDataRoot, "cache", "images"),
                },
                new HarnessOptions { Frames = frames, Screenshot = screenshot });
            // Process-exit flush for session.json (nav + the playback restore section): the shell's unmount cleanup never
            // runs on shutdown (AppHost.Dispose doesn't unmount the tree), so a pending debounced save would be lost.
            SessionSnapshotStore.FlushActive();
            // ...then, only if the user asked for it, stage a waiting update before the process goes away.
            InstallPendingUpdateOnQuit(settings);
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Critical("app", "Fatal error in the app loop", ex);
            WaveeLog.Instance.Flush();
            string reportPath = "";
            try { reportPath = CrashReport.Write(ex, WaveeLog.Instance.FilePath); }
            catch { }
            // Arm the report for the NEXT launch too: the message box below only reaches a user who is still sitting in
            // front of the machine, and a crash the user walked away from should still be offered when they come back.
            try { if (reportPath.Length > 0) settings.Set(WaveeSettings.PendingCrashReport, reportPath); }
            catch { }
            try
            {
                var body = Loc.Get(Strings.Crash.Body) + "\n\n"
                         + (reportPath.Length > 0 ? Strings.Crash.ReportLine(reportPath) + "\n" : "")
                         + (WaveeLog.Instance.FilePath is { Length: > 0 } lp ? Strings.Crash.LogLine(lp) + "\n" : "")
                         + "\n" + Loc.Get(Strings.Crash.OpenFolderQuestion);
                if (StartupNotice.ErrorYesNo(Loc.Get(Strings.Crash.Title), body) && reportPath.Length > 0)
                    ShellOpen.OpenFolderOf(reportPath);
            }
            catch { }
            throw;
        }
        finally
        {
            instanceGate?.Dispose();
        }
        WaveeLog.Instance.Info("app", "Wavee exiting");
        WaveeLog.Instance.Flush();
    }

    /// <summary>
    /// "Install a waiting update when I quit Wavee" (<c>app.update.installOnQuit</c>, off by default), honoured HERE —
    /// after the app loop has returned and the session snapshot is durable, before the process actually goes away.
    ///
    /// <para>This is the ONE place that reads the setting; the decision itself is the pure, unit-tested
    /// <see cref="Wavee.Core.ShutdownUpdatePolicy.ShouldApply"/> (on = true AND the updater is sitting on an
    /// <c>Available</c>/<c>Snoozed</c> target — a Failed attempt is never retried silently).</para>
    ///
    /// <para>It WAITS, deliberately and with a bound — but it does NOT block the thread: the apply runs on the thread
    /// pool while this thread pumps Win32 messages (<see cref="MessagePump.RunUntil"/>). A blocked GUI thread is what
    /// made Windows kill every in-app-quit update as a hung app; see the comment in the body. The whole point of "when I quit" is that the download happens
    /// while the user is done with the app, so returning immediately and letting the process exit would stage nothing
    /// at all. Ten minutes is the ceiling: a link too slow to finish in ten minutes leaves the update exactly where it
    /// was — still offered, still applied on the next launch — which is the same outcome as never having asked. The
    /// deployment may terminate this process itself (<c>ForceTargetApplicationShutdown</c>) partway through, and at
    /// quit time that is a perfectly good ending.</para>
    ///
    /// <para>And it STOPS waiting the moment Windows asks us to leave (<see cref="PumpOutcome.ShutdownRequested"/>):
    /// the deployment's Restart Manager pass waits for this process to exit before it can finish, so continuing to
    /// wait is a deadlock the OS resolves by killing us and filing a hang report. Leaving costs nothing — the package
    /// is staged and <c>RegisterApplicationRestart</c> has already been called, so Windows brings Wavee back.</para>
    /// </summary>
    static void InstallPendingUpdateOnQuit(IAppSettings settings)
    {
        try
        {
            var updater = AppInstallerUpdateService.Instance;
            if (updater is null) return;
            if (!Wavee.Core.ShutdownUpdatePolicy.ShouldApply(settings.Get(WaveeSettings.UpdateInstallOnQuit), updater.Current.State))
                return;

            // Read ONCE: the two lines this method can write about the target must name the same quad, and Current is
            // republished by every progress tick of the apply below.
            string targetQuad = updater.Current.TargetQuad ?? "?";
            WaveeLog.Instance.Info("update", "install-on-quit: staging " + targetQuad);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            // The apply runs OFF this thread and this thread becomes a message pump for the duration. It must not
            // simply block: the deployment ends in ForceTargetApplicationShutdown, which runs the Restart Manager
            // against our process — WM_QUERYENDSESSION (ENDSESSION_CLOSEAPP) → WM_ENDSESSION → WM_CLOSE to the
            // top-level windows — and then waits ~30 s for us to EXIT before killing us. A thread parked in
            // GetAwaiter().GetResult() answers nothing, so Windows declared the process hung (WER MoAppHang +
            // Application event 1002 "Wavee.exe stopped interacting with Windows and was closed") and killed it.
            //
            // Pumping alone did NOT fix that, and the measurement is unambiguous: a pumped run took the same ~32 s and
            // filed the same hang report (deployment started 23:23:07.6, event 1002 at 23:23:40.05, package registered
            // 23:23:40.5 — the instant the OS killed us). The shutdown request is not a question, it is an eviction
            // notice: the deployment is waiting for US to go away while we wait for the deployment. So the pump owns a
            // hidden top-level sentinel window (message-only windows never see the broadcast) and reports
            // ShutdownRequested, and this method's answer to that is to RETURN and let Main fall through to its exit.
            // Nothing is lost by leaving: the package is already staged, and PackageUpdater.Deploy calls
            // RegisterApplicationRestart BEFORE the deployment starts, so the Restart Manager brings Wavee back with
            // --relaunched-after-update once the registration lands.
            Task apply = Task.Run(() => updater.ApplyAsync(cts.Token));
            // A slightly longer pump ceiling than the token's: let the cancellation land and settle the state, so the
            // ordinary "gave up in state X" line below is what the user (and the E2E harness) sees.
            var outcome = MessagePump.RunUntil(apply, TimeSpan.FromMinutes(10.5));
            if (outcome == PumpOutcome.ShutdownRequested)
            {
                // NOT Environment.Exit: the normal path out of Main still has work to do (the single-instance mutex,
                // the "Wavee exiting" line, the log flush) and none of it blocks. Returning is the fastest correct exit.
                WaveeLog.Instance.Info("update", "install-on-quit: Windows asked us to exit "
                    + "(the deployment is taking over); staged " + targetQuad);
                WaveeLog.Instance.Flush();
                return;
            }
            if (outcome == PumpOutcome.TimedOut)
            {
                WaveeLog.Instance.Warn("update", "install-on-quit gave up in state " + updater.Current.State
                    + " (the update is still offered and applies on the next launch)");
                return;
            }
            apply.GetAwaiter().GetResult();   // observe faults so the catch below logs them

            var state = updater.Current.State;
            if (Wavee.Core.ShutdownUpdatePolicy.IsSettled(state))
                WaveeLog.Instance.Info("update", "install-on-quit finished: " + state);
            else
                WaveeLog.Instance.Warn("update", "install-on-quit gave up in state " + state
                    + " (the update is still offered and applies on the next launch)");
        }
        catch (Exception ex)
        {
            // Never turn a quit into a crash. The update is still pending; the next launch offers it again.
            WaveeLog.Instance.Warn("update", "install-on-quit failed", ex);
        }
        finally
        {
            WaveeLog.Instance.Flush();
        }
    }

    static Action<string>? DebugEcho()
    {
#if DEBUG
        return Console.Out.WriteLine;
#else
        return null;
#endif
    }
}
