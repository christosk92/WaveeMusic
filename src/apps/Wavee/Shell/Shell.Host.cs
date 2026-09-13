// ── Shell/Shell.Host.cs ────────────────────────────────────────────────────────────────────────────────────────────
// window, activation, session snapshot, history.json / play-log.json / play-recency.json (ch 16), the WaveeTips host
// (A10), the file-drop hook
//
// Role: SHELL
// Owner: I
// Wave: 4
// Budget: 1000 lines
// Spec: plan 800 + ch 16 + ch 29
//
// THE COMPOSITION ROOT'S SECOND HALF. `Platform.Boot()` already opened the settings store, the log, the credential
// slot and the locale — everything that must exist BEFORE there is a window. This file owns everything from the window
// outwards: the crash net, the single-instance gate, the `wavee://` registration, the activation intake, the theme
// seed, the engine loop, and the three documents the shell persists.
//
// THE ROOT SEAM. `Shell.UI.cs` (stage 2) owns the frame component; this file must run the loop before that file
// exists. The seam is the FIELD `RootFactory`, not a `static partial Element Root()`: a partial method with a return
// value REQUIRES an implementing declaration, so the partial-method form does not compile until `Shell.UI.cs` is
// written — and stage 1 has to build. Stage 2 assigns `Shell.RootFactory = static () => Embed.Comp(() => new Frame());`
// from its own boot, before `Run` is called; until then the loop paints an empty content region, which is exactly what
// the Wave-4 gate's "`--fake` shows the shell frame with an empty content host" describes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Activation;
using FluentGpu.WindowsApi.Packaging;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. THE SHELL'S LIVE CELLS ═══════════════════════════════════════════════════════════════════════════════════
    //
    // Created ONCE, for the process. `Shell.Root` is mounted once and never remounted for a theme or a route change,
    // so every value it shows reaches it through one of these or through a bound prop — nothing is a constructor
    // argument (the props-freeze contract).

    /// <summary>The current destination. Pages receive route VALUES, never this signal.</summary>
    public static readonly Signal<Route> Current = new(new Route(RouteKind.Home));

    /// <summary>Which direction the NEXT swap travels. Written BEFORE <see cref="Current"/> in the same flush, so the
    /// reconciler can Peek it and get the direction that belongs to the route it is about to activate.</summary>
    public static readonly Signal<NavTransitionKind> Motion = new(NavTransitionKind.Neutral);

    public static readonly Signal<bool> CanBack = new(false);
    public static readonly Signal<bool> CanForward = new(false);

    /// <summary>The merged chrome row's resolved allocation. Published ONLY when a stage flips, so a resize drag does
    /// not re-render the bar per pixel.</summary>
    public static readonly Signal<Chrome> ChromeLayout = new(Chrome.FromWidth(1180f, 1));

    /// <summary>Bumped whenever the tab list changes shape; the strip rebuilds its items on the bump.</summary>
    public static readonly Signal<int> TabsVersion = new(0);

    /// <summary>The strip's selection cell — a PROJECTION the shell re-asserts after every workspace op. Never read
    /// back to decide whether a tab changed; ask the workspace model.</summary>
    public static readonly Signal<int> SelectedTab = new(0);

    /// <summary>The omnibar's text. Synced to the route on NAVIGATION only, never on keystrokes.</summary>
    public static readonly Signal<string> SearchText = new("");

    /// <summary>A monotonic ticket the omnibar watches to take focus (Ctrl+F, the icon-mode flyout).</summary>
    public static readonly Signal<int> SearchFocusRequest = new(0);

    /// <summary>The command palette is open (Ctrl+K).</summary>
    public static readonly Signal<bool> PaletteOpen = new(false);

    /// <summary>An OS file drag is over the window — the centred drop pill's gate.</summary>
    public static readonly Signal<bool> FileDropOver = new(false);

    /// <summary>The window's material: who owns the tint and whether its colour is settled.</summary>
    public static readonly Signal<TintOwner> Material = new(TintOwner.Neutral);

    /// <summary>The profile chip's one verb.</summary>
    public static readonly Signal<AuthState> Auth = new(AuthState.SignInRequired);

    /// <summary>The nav model. A mutable struct field, not a signal: its ARITHMETIC is pure and its OBSERVABLE half is
    /// the four signals above, so a reader never has to copy two lists to learn that Back is enabled.</summary>
    static Nav s_nav = new(new Route(RouteKind.Home));

    /// <summary>The tab workspace.</summary>
    public static TabWorkspace Tabs { get; } = new();

    static int s_savedPinnedRevision;

    /// <summary>Stage 2's frame component. See the file header for why this is a FIELD and not a partial method.</summary>
    internal static Func<Element>? RootFactory = null;

    /// <summary>The OS file-drop hook. `Shell.UI.cs`'s drop target calls it; owner L's `Drag.cs` owns the rule tables
    /// that decide what a path is, and the local-file verbs are owner O's. A null hook means a drop is ignored, which
    /// is what a build without the local-file surface should do.</summary>
    public static Action<IReadOnlyList<string>>? OnFilesDropped;

    /// <summary>The app palette seam. Owner L's `Design.cs` installs the Wavee palette here BEFORE the first frame; a
    /// build without it runs on the engine's own palette rather than not running.</summary>
    public static Action<ThemeKind>? SeedPalette;

    // ══ 2. RUN — the window and the engine loop ═════════════════════════════════════════════════════════════════════

    /// <summary>Everything from the window outwards. NEVER RETURNS until exit.
    /// <para>ORDER IS THE CONTRACT, and every step names what breaks without it:</para>
    /// <list type="number">
    /// <item>the CLI/harness args, because the single-instance gate and the window size read them;</item>
    /// <item>the crash net, BEFORE the window — a crash during device init must still leave a report;</item>
    /// <item>the single-instance gate + <c>wavee://</c> registration + the first activation payload, because a second
    /// launch must hand its deep link to the running instance and exit rather than opening a second window;</item>
    /// <item>the theme seed, BEFORE the window comes up, or the first frame flashes the wrong palette;</item>
    /// <item>the three documents, because the first render reads the restored route;</item>
    /// <item>the loop;</item>
    /// <item>the exit tail: flush the session document (the shell's unmount cleanup never runs on shutdown, so a
    /// pending debounced save would be lost), then the toast platform, then the instance gate.</item>
    /// </list></summary>
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        ParseArgs(args, out int frames, out string? screenshot, out int winW, out int winH);

        InstallCrashNet();
        InstallMarshallers();

        // The harness arms (`--frames` / `--screenshot`) skip the gate so a visual-diff loop can spawn freely.
        SingleInstanceGate? gate = null;
        if (screenshot is null && frames < 0)
        {
            gate = new SingleInstanceGate();
            var activation = ActivationArgs.FromCurrentProcess("wavee");
            string payload = activation.Kind == ActivationKind.Launch ? "" : activation.Argument;
            if (!gate.TryAcquire("Wavee", "FluentGpuWindow", payload))
            {
                // A second launch handed its payload to the running instance through the gate; leaving is the whole
                // point. Nothing below runs.
                gate.Dispose();
                return;
            }
            RegisterProtocols();
            if (activation.Kind is ActivationKind.Protocol or ActivationKind.File or ActivationKind.ToastActivated)
                s_pendingActivation = activation.Argument;
            FluentApp.ActivationRedirected += OnActivationRedirected;
        }

        SeedTheme();
        Session.Load();
        History.Store.Load();
        PlayLog.Load();
        RestoreNav();
        Tips.Load();

        try
        {
            FluentAppHarness.Run(
                static () => new RootHost(),
                new AppOptions
                {
                    // MinWidth 300 (DIP): the shell is verified sound below 360 — the detail surface is in vertical
                    // mode, the track table is at its narrowest tier and the hero artwork is at its 64-DIP floor — and
                    // 360 was a hard ~564 physical-px floor at 150 % DPI that stopped the window fitting a half-screen
                    // split.
                    Title = "Wavee Music", Width = winW, Height = winH,
                    MinWidth = 300, CustomFrame = true,
                    // Always base Mica, never Mica Alt: Alt's stronger tint reads as an over-saturated navy chrome
                    // next to apps that default to base Mica on the same wallpaper. The material is visible through
                    // EVERY chrome band — title bar, sidebar band, player dock — because the root is transparent and
                    // each band is a deliberate paint-site OMISSION over live Mica, not a fill.
                    MicaAlt = false,
                    // Every frame over the panel's refresh interval is logged with WHICH components rendered and who
                    // allocated. ALWAYS ON: a slow frame that cannot be attributed is a slow frame that does not get
                    // fixed.
                    RenderCensus = true,
                    // App-wide UI zoom, seeded BEFORE the first frame (the theme discipline: no startup jump from
                    // 100 % to the user's scale).
                    Zoom = Platform.Settings.Get(Platform.Keys.ZoomLevel),
                    // The engine's decoded-image disk cache lands under Wavee's own app data, next to logs/ and the
                    // library — ONE folder for "everything Wavee wrote", which is what the Storage tab measures and
                    // what factory reset wipes.
                    ImageCacheDirectory = Path.Combine(Platform.LocalFolder, "cache", "images"),
                },
                new HarnessOptions { Frames = frames, Screenshot = screenshot });
        }
        catch (Exception ex)
        {
            Log.Critical("app", "Fatal error in the app loop", ex);
            Log.Flush();
            throw;
        }
        finally
        {
            // The shell's unmount cleanup never runs on shutdown (the host does not unmount the tree), so a pending
            // debounced save would be LOST. These three are the only documents that can be mid-debounce.
            Session.Flush();
            History.Store.Flush();
            PlayLog.Flush();
            Notify.HostShutdown();
            Playback.Os.Shutdown();
            gate?.Dispose();
        }
    }

    static void ParseArgs(string[] args, out int frames, out string? screenshot, out int winW, out int winH)
    {
        frames = -1;
        screenshot = null;
        // --width/--height override the startup client size. They exist for the screenshot loop: a responsive page's
        // breakpoints can only be verified by capturing AT each width, and the alternative is dragging by hand.
        winW = 1180;
        winH = 760;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int f)) frames = f;
            else if (args[i] == "--screenshot" && i + 1 < args.Length) screenshot = args[i + 1];
            else if (args[i] == "--width" && i + 1 < args.Length && int.TryParse(args[i + 1], out int w) && w >= 300) winW = w;
            else if (args[i] == "--height" && i + 1 < args.Length && int.TryParse(args[i + 1], out int h) && h >= 300) winH = h;
        }
    }

    // ── 2.1 the UI-thread marshallers and the frame tick ────────────────────────────────────────────────────────────
    //
    // FOUR CORE seams default to running a callback INLINE, which is what keeps their unit suites single-threaded:
    // `Playback.ToUi`, `Spotify.Post`, `Store.Post` and `Palette.Post`. Left at that default in the GUI, a network or
    // audio thread folds Playback's state and writes the entity tables concurrently with the UI thread. ONLY this method
    // (and the future headless host, which owns its own loop) may assign them — no page, test double or feature file.
    //
    // The engine's poster only exists once the host does (inside `FluentAppHarness.Run`), so the seams are installed
    // HERE, before the first frame, with a poster that QUEUES until the root component hands over the real one on its
    // first render (`AttachUiPoster`). A callback posted between the two is delivered in order on the UI thread, never
    // inline on the thread that posted it.

    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_earlyPosts = new();
    static volatile Action<Action>? s_uiPost;

    static void InstallMarshallers()
    {
        Action<Action> post = static a =>
        {
            if (s_uiPost is { } live) { live(a); return; }
            s_earlyPosts.Enqueue(a);
            // The root may have attached between the check and the enqueue: drain through it so nothing strands.
            if (s_uiPost is { } late) DrainEarlyPosts(late);
        };
        Playback.ToUi = post;
        Spotify.Post = post;
        Store.Post = post;
        Wavee.Palette.Post = post;

        // THE frame tick. `Fetch.Pump()` is how an expired backoff re-sends with nothing else happening, and
        // `Palette.Tick()` is how the grading debounce fires; both are two comparisons when idle. It rides the host's
        // per-RENDERED-frame relay, so an idle window (no frames) ticks nothing — a deadline that lands while idle is
        // served on the next frame, which both layers document as a delay, never a loss. The lambda is static: zero
        // allocation per frame.
        FluentApp.FrameCompleted += static _ =>
        {
            Fetch.Pump();
            Wavee.Palette.Tick();
        };
    }

    /// <summary>The root's first render hands over the engine's UI-thread poster. Idempotent.</summary>
    static void AttachUiPoster(Action<Action> post)
    {
        if (s_uiPost is not null) return;
        s_uiPost = post;
        DrainEarlyPosts(post);
    }

    static void DrainEarlyPosts(Action<Action> post)
    {
        while (s_earlyPosts.TryDequeue(out var queued)) post(queued);
    }

    /// <summary>The two PROCESS-level handlers. The UI-thread one lives in the engine loop; these catch everything
    /// else, and everything here is best-effort because we are terminating — but a failure is LOGGED, never
    /// swallowed: a silent catch here is how a lost crash report went unnoticed.</summary>
    static void InstallCrashNet()
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Log.Critical("crash", "Unhandled exception (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);
            try { Log.Flush(); } catch (Exception) { }
        };
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            Log.Error("crash", "Unobserved task exception", e.Exception);
            Log.Flush();
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            // Belt and braces: the normal path already flushes, but this is the one hook that ALSO fires when Windows
            // force-closes us during an otherwise-orderly shutdown before that line runs.
            try { Session.Flush(); } catch (Exception) { }
            try { Log.Flush(); } catch (Exception) { }
        };
    }

    /// <summary>Seed the theme BEFORE the window comes up (no startup flash): honour the persisted preference, falling
    /// back to the live OS theme for a fresh install.</summary>
    static void SeedTheme()
    {
        int mode = Platform.Settings.Get(Platform.Keys.ThemeMode);
        var kind = mode switch
        {
            1 => ThemeKind.Light,
            2 => ThemeKind.Dark,
            _ => FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark,
        };
        if (SeedPalette is { } seed) seed(kind);
        else FluentGpu.Dsl.Tok.Use(kind);
        if (FluentApp.SystemAccentRamp() is { } ramp) FluentGpu.Dsl.Tok.SetAccent(in ramp);
        else if (FluentApp.SystemAccent() is { } accent) FluentGpu.Dsl.Tok.SetAccent(accent);
        // The icon face is the BUNDLED Segoe Fluent Icons file, not the system family of the same name: the system one
        // ships with Windows 11 only, and on Windows 10 every glyph added in Fluent Icons drew as tofu.
        FluentGpu.Dsl.Theme.IconFont = WaveeFonts.Icons;
    }

    /// <summary>UNPACKAGED ONLY. A packaged build declares its protocols AND its startup task in the manifest and the
    /// OS owns both: writing the same HKCU keys from here would fight the manifest registration and leave a stale
    /// association pointing at an install path that moves on every update.</summary>
    static void RegisterProtocols()
    {
        if (PackageIdentity.IsPackaged) return;
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } exe) return;
            ProtocolRegistrar.RegisterProtocol("wavee", exe, "Wavee");
            // The opt-in `spotify:` handler follows the setting in BOTH directions, so turning it off in a previous
            // session actually gives the scheme back rather than leaving a stale association behind.
            if (Platform.Settings.Get(Platform.Keys.HandleSpotifyLinks)) ProtocolRegistrar.RegisterProtocol("spotify", exe, "Wavee");
            else ProtocolRegistrar.UnregisterProtocol("spotify");
            // Same both-directions contract for "start Wavee when I sign in".
            if (Platform.Settings.Get(Platform.Keys.StartOnLogin)) ProtocolRegistrar.RegisterStartup("Wavee", exe);
            else ProtocolRegistrar.UnregisterStartup("Wavee");
        }
        catch (Exception ex)
        {
            Log.Warn("app", "wavee:// protocol registration failed", ex);
        }
    }

    // ══ 3. ACTIVATION → THE ONE DEEP-LINK INTAKE ════════════════════════════════════════════════════════════════════

    static string s_pendingActivation = "";

    /// <summary>A second launch's payload, redirected here by the single-instance gate. Fires on the gate's thread, so
    /// it only PARKS the payload; the shell drains it on the UI thread.</summary>
    static void OnActivationRedirected(string raw)
    {
        Volatile.Write(ref s_pendingActivation, raw ?? "");
        WakeWindow();
    }

    /// <summary>The payload waiting to be applied, taken atomically. `Shell.UI.cs` polls this from a UI-thread effect;
    /// a toast activation reaches the same door through <c>Notify.HostInstall</c>'s hop.</summary>
    public static string TakePendingActivation() => Interlocked.Exchange(ref s_pendingActivation, "");

    /// <summary>Bring the window to the foreground when a second launch or a toast asks for it. A minimized window is
    /// RESTORED first: <c>SetForegroundWindow</c> on an iconic window raises a window nobody can see.</summary>
    public static void WakeWindow()
    {
        nint hwnd = FluentApp.WindowHandle;
        if (hwnd == 0) return;
        ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
        SetForegroundWindow(hwnd);
    }

    const int SwShow = 5, SwRestore = 9;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    /// <summary>THE deep-link door. Every intake — an OS protocol activation, a redirected second launch, a toast
    /// click, the play-a-link dialog — arrives here, so a toast can never reach a destination a link cannot.</summary>
    public static void ApplyDeepLink(ReadOnlySpan<char> raw)
    {
        var verb = DeepLink(raw, Platform.Settings.Get(Platform.Keys.DeveloperMode));
        switch (verb.Kind)
        {
            case DeepLinkKind.Open:
                GoTo(verb.Route);
                break;
            case DeepLinkKind.Play:
                if (verb.Context.Length > 0) OnPlayContext?.Invoke(EntityUri.Parse(verb.Context));
                else OnPlayLink?.Invoke(verb.Link);
                break;
            case DeepLinkKind.Resume:
                Playback.Resume();
                break;
            case DeepLinkKind.Pause:
                Playback.Pause();
                break;
            case DeepLinkKind.Report:
                OnReportRequested?.Invoke(verb.Arg);
                break;
            default:
                Log.Warn("nav", "deeplink.refused: " + raw.ToString());
                break;
        }
    }

    /// <summary>`wavee://play?ctx=&lt;uri&gt;` — play a CONTEXT. A seam rather than a direct `Playback` call: the
    /// transport verb needs a resolved row + cursor, and resolving a bare uri into one is the context resolver's job
    /// (Wave 5). The shell knows WHERE the intent came from; it does not know how to seed a queue.</summary>
    public static Action<EntityUri>? OnPlayContext;

    /// <summary>A pasted module url (`wavee://play?link=…`, the Play ▸ Link… dialog). Owner T's `Modules.cs` installs
    /// the router; a null hook means the app has no playback modules, which is a real build.</summary>
    public static Action<string>? OnPlayLink;

    /// <summary>`wavee://open?route=report&amp;arg=bug|crash|…` — a report is a DIALOG, never a tab and never a
    /// history entry. `+Shell.Overlays.UI.cs` installs it.</summary>
    public static Action<string>? OnReportRequested;

    // ══ 4. THE NAV VERBS (the SHELL half — the reducer is Shell.cs) ═════════════════════════════════════════════════

    /// <summary>Commit a forward navigation and run the SIX side effects 0.2.9's 19-line `Go` carries beside the stack
    /// push. Each one names what it is for:</summary>
    public static void GoTo(in Route route, NavOrigin? origin = null, NavTransitionKind motion = NavTransitionKind.Forward)
    {
        // History always opens in its OWN tab (a global view — the browser convention).
        if (route.Kind == RouteKind.History && Current.Peek().Kind != RouteKind.History)
        {
            OpenTab(route);
            return;
        }
        Commit(route, origin, motion);
    }

    /// <summary>The commit half of <see cref="GoTo"/>, without the own-tab rule — a fresh tab's FIRST route lands
    /// here, or opening History in a new tab would open another new tab, forever.</summary>
    static void Commit(in Route route, NavOrigin? origin, NavTransitionKind motion)
    {
        var normalized = route with { Tab = Tabs.ActiveId };
        if (!Go(ref s_nav, normalized)) return;

        Motion.Value = motion;                    // BEFORE the route, in the same flush
        Origins.Write(normalized, origin);        // every Go writes; null OVERWRITES — latest arrival wins
        Current.Value = normalized;
        CanBack.Value = s_nav.CanBack;
        CanForward.Value = false;
        History.Store.Add(normalized);            // the navigation log
        SyncActiveTab(normalized);                // the tab follows the page
        SyncOmnibar(normalized);
        Session.CaptureNav();
    }

    public static void GoBack()
    {
        if (!BackStep(ref s_nav)) return;
        Motion.Value = NavTransitionKind.Back;
        Current.Value = s_nav.Current;
        CanBack.Value = s_nav.CanBack;
        CanForward.Value = s_nav.CanForward;
        History.Store.Add(s_nav.Current);
        SyncActiveTab(s_nav.Current);
        SyncOmnibar(s_nav.Current);
        Session.CaptureNav();
    }

    public static void GoForward()
    {
        if (!ForwardStep(ref s_nav)) return;
        Motion.Value = NavTransitionKind.Forward;
        Current.Value = s_nav.Current;
        CanBack.Value = s_nav.CanBack;
        CanForward.Value = s_nav.CanForward;
        History.Store.Add(s_nav.Current);
        SyncActiveTab(s_nav.Current);
        SyncOmnibar(s_nav.Current);
        Session.CaptureNav();
    }

    /// <summary>A tab SWITCH. Neutral motion, no stack push, no origin write, no history record.</summary>
    static void RestoreTo(in Route route)
    {
        Restore(ref s_nav, route);
        Motion.Value = NavTransitionKind.Neutral;
        Current.Value = route;
        SyncOmnibar(route);
        Session.CaptureNav();
    }

    /// <summary>The omnibar IS the search page's query. Leaving Search clears it back to the placeholder; arriving on
    /// Search restores the route arg. Live typing is untouched because this runs on NAVIGATION only, never on
    /// keystrokes.</summary>
    static void SyncOmnibar(in Route r)
    {
        string next = r.Kind == RouteKind.Search ? ArgOf(r) ?? "" : "";
        if (!string.Equals(SearchText.Peek(), next, StringComparison.Ordinal)) SearchText.Value = next;
    }

    static void SyncActiveTab(in Route r)
    {
        Tabs.SetActiveRoute(r);
        TabsVersion.Value++;
        PersistPinsIfChanged();
    }

    // ── 4.1 the workspace ops ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE place a workspace op meets the shell: re-assert the strip's selection cell FROM THE MODEL (the
    /// strip may have pre-written it, the model may have moved the tab), persist pins if they changed, then follow the
    /// intent.</summary>
    static void Apply(in TabNavResult result)
    {
        if (SelectedTab.Peek() != result.ActiveIndex) SelectedTab.Value = result.ActiveIndex;
        TabsVersion.Value++;
        PersistPinsIfChanged();
        switch (result.Intent)
        {
            case TabNavIntent.Restore when result.Route is { } r:
                RestoreTo(r with { Tab = Tabs.ActiveId });
                break;
            case TabNavIntent.Push when result.Route is { } r:
                // A fresh tab's first route is a real navigation but NOT a direction: neutral motion.
                Commit(r, null, NavTransitionKind.Neutral);
                break;
        }
    }

    public static void ActivateTab(int index) => Apply(Tabs.Activate(index));
    public static void ActivateTabById(int id) => Apply(Tabs.ActivateById(id));
    public static void OpenTab(in Route route) => Apply(Tabs.Open(route));
    public static void CloseTab(int index) => Apply(Tabs.Close(index));
    public static void CloseTabById(int id) => Apply(Tabs.CloseById(id));
    public static void SetTabPinned(int id, bool pinned) => Apply(Tabs.SetPinned(id, pinned));
    public static void CloseOtherTabs(int keepId) => Apply(Tabs.CloseWhere(tab => tab.Id != keepId && !tab.Pinned));
    public static void CloseAllUnpinnedTabs() => Apply(Tabs.CloseWhere(static tab => !tab.Pinned));

    public static void CloseTabsToRight(int tabId)
    {
        int index = Tabs.IndexOf(tabId);
        if (index < 0) return;
        var tabs = Tabs.Tabs;
        var rightIds = new HashSet<int>();
        for (int i = index + 1; i < tabs.Count; i++) if (!tabs[i].Pinned) rightIds.Add(tabs[i].Id);
        Apply(Tabs.CloseWhere(tab => rightIds.Contains(tab.Id)));
    }

    /// <summary>Every op that touches pins persists them; no op that does not pays for a settings write.</summary>
    static void PersistPinsIfChanged()
    {
        if (Tabs.PinnedRevision == s_savedPinnedRevision) return;
        s_savedPinnedRevision = Tabs.PinnedRevision;
        Platform.Settings.Set(Platform.Keys.WorkspacePinnedTabs, WorkspaceTabs.Encode(Tabs.PinnedSnapshot()));
    }

    /// <summary>Cold start: the pinned workspace first, then the session document on top of it. A session that names a
    /// tab id the pins no longer contain simply keeps the pinned default — fail-soft in both directions.</summary>
    static void RestoreNav()
    {
        var snapshot = WorkspaceTabs.Decode(Platform.Settings.Get(Platform.Keys.WorkspacePinnedTabs));
        var route = Tabs.RestorePinned(in snapshot);
        s_savedPinnedRevision = Tabs.PinnedRevision;

        if (Session.TryApplyNav(s_nav, out var active, out int tabId))
        {
            if (tabId >= 0) Tabs.TrySelect(tabId);
            route = active;
        }
        route = route with { Tab = Tabs.ActiveId };
        s_nav.Current = route;
        Current.Value = route;
        CanBack.Value = s_nav.CanBack;
        CanForward.Value = s_nav.CanForward;
        SelectedTab.Value = Tabs.ActiveIndex;
        TabsVersion.Value++;
        SyncOmnibar(route);

        // The taskbar's jump list attaches LATE by design (it shipped in Wave 3 with the category empty): the log only
        // exists now. Each attach earns exactly one rebuild.
        Playback.Os.JumpList.Attach(
            recentContexts: static max => PlayLog.RecentContexts(max),
            recentSurfaces: static max => History.RecentSurfaces(History.Store.Entries, max));
    }

    // ══ 5. THE ROOT ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The engine's root component. It reads NO signal, so it renders exactly once for the process and a
    /// route change never re-runs it — everything below reaches its own state through a signal or a bound prop. When
    /// stage 2 has not installed a frame it paints one empty full-bleed box, which is a running loop rather than a
    /// crash.</summary>
    sealed class RootHost : Component
    {
        public override Element Render()
        {
            // The first render is the earliest point the engine's poster exists; the marshallers queued until now.
            AttachUiPoster(UsePost());
            return RootFactory is { } factory ? factory() : new BoxEl { Grow = 1f };
        }
    }

    // ══ 6. HISTORY.JSON — the navigation log's store (ch 16) ════════════════════════════════════════════════════════

    public static partial class History
    {
        /// <summary>The live navigation log. UI thread only; the pool task touches the snapshot array and the path
        /// string, never the live list.</summary>
        public static class Store
        {
            static readonly List<HistoryEntry> s_entries = new(MaxEntries);
            static readonly Signal<int> s_version = new(0);
            static string? s_path;
            static Timer? s_saveTimer;
            static int s_savePending;
            static int s_dirty;

            /// <summary>Debounce window. A burst of navigations (a drill down three levels) becomes one write.</summary>
            public const int SaveDebounceMs = 2000;

            /// <summary>Bumped on every mutation; the history page keys its rebuild on it.</summary>
            public static IReadSignal<int> Version => s_version;

            /// <summary>Oldest FIRST. Live view — do not mutate.</summary>
            public static IReadOnlyList<HistoryEntry> Entries => s_entries;

            public static string DefaultPath() => Path.Combine(Platform.LocalFolder, "WaveeMusic", "history.json");

            /// <summary>Point the store at a file. Injectable so a test writes to a temp path.</summary>
            public static void UsePath(string path) => s_path = path;

            public static void Load()
            {
                s_path ??= DefaultPath();
                s_entries.Clear();
                if (!File.Exists(s_path)) return;
                try
                {
                    var bytes = File.ReadAllBytes(s_path);
                    var dtos = JsonSerializer.Deserialize(bytes, HistoryJson.Default.HistoryEntryDtoArray);
                    if (dtos is null) return;
                    for (int i = 0; i < dtos.Length; i++)
                    {
                        var d = dtos[i];
                        if (string.IsNullOrEmpty(d.Name)) continue;
                        s_entries.Add(new HistoryEntry(Parse(d.Name, d.Arg ?? ""),
                            new DateTime(d.TicksUtc, DateTimeKind.Utc).ToLocalTime()));
                    }
                    // No version bump: no listeners exist yet at startup.
                }
                catch (Exception ex)
                {
                    s_entries.Clear();
                    Log.Warn("nav", "history.json could not be read; this session starts with an empty log", ex);
                    return;
                }

                // ONE-TIME PURGE of the demo seed 0.2.9 wrote on a fresh install: those playlist uris address nothing,
                // so every such row is a dead destination the page would still offer. Rewriting the FILE — rather than
                // filtering at render time — means the log stops carrying them at all.
                int before = s_entries.Count;
                s_entries.RemoveAll(static e => NameOf(e.Route).StartsWith(DeadSeedPrefix, StringComparison.Ordinal));
                if (s_entries.Count != before) SaveNow();
            }

            public static void Add(in Route r)
            {
                if (r.Kind == RouteKind.NotFound) return;
                s_entries.Add(new HistoryEntry(r, DateTime.Now));
                if (s_entries.Count > MaxEntries) s_entries.RemoveAt(0);   // FIFO: evict the oldest
                s_version.Value++;
                ScheduleSave();
            }

            public static void Remove(in HistoryEntry e)
            {
                if (!s_entries.Remove(e)) return;
                s_version.Value++;
                ScheduleSave();
            }

            public static void Clear()
            {
                if (s_entries.Count == 0) return;
                s_entries.Clear();
                s_version.Value++;
                Interlocked.Exchange(ref s_savePending, 0);
                Interlocked.Exchange(ref s_dirty, 0);
                if (s_path is { } p) _ = Task.Run(() => { try { File.Delete(p); } catch (Exception) { } });
            }

            /// <summary>Issue any debounced write NOW (shutdown, a deliberate drain point).</summary>
            public static void Flush()
            {
                Interlocked.Exchange(ref s_savePending, 0);
                if (Interlocked.Exchange(ref s_dirty, 0) == 0) return;
                SaveNow();
            }

            static void ScheduleSave()
            {
                if (s_path is null) return;
                Interlocked.Exchange(ref s_savePending, 1);
                Interlocked.Exchange(ref s_dirty, 1);
                s_saveTimer ??= new Timer(static _ => OnSaveTimer(), null, Timeout.Infinite, Timeout.Infinite);
                try { s_saveTimer.Change(SaveDebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }

            static void OnSaveTimer()
            {
                if (Interlocked.Exchange(ref s_savePending, 0) == 0) return;
                if (Interlocked.Exchange(ref s_dirty, 0) == 0) return;
                SaveNow();
            }

            /// <summary>Snapshot on the CALLER's thread, write on the pool. The persisted shape is UTC TICKS, not a
            /// <c>DateTime</c>: the kind does not round-trip, and a local time written on one side of a DST boundary
            /// and read on the other moves an entry into the wrong day group.</summary>
            static void SaveNow()
            {
                if (s_path is not { } path) return;
                int count = Math.Min(s_entries.Count, MaxEntries);
                int start = s_entries.Count - count;
                var snapshot = new HistoryEntryDto[count];
                for (int i = 0; i < count; i++)
                {
                    var e = s_entries[start + i];
                    snapshot[i] = new HistoryEntryDto(NameOf(e.Route), ArgOf(e.Route), e.VisitedAt.ToUniversalTime().Ticks);
                }
                _ = Task.Run(() => WriteThenRename(path, JsonSerializer.SerializeToUtf8Bytes(
                    snapshot, HistoryJson.Default.HistoryEntryDtoArray), "history"));
            }
        }
    }

    // ══ 7. SESSION.JSON — reopen where you left off ═════════════════════════════════════════════════════════════════

    /// <summary>v1 session document. Every section is ADDITIVE and optional, so an older v1 snapshot stays
    /// readable.</summary>
    public sealed class SessionDto
    {
        public int Version { get; set; } = Session.CurrentVersion;
        public SessionNavDto? Nav { get; set; }
        public SessionShellDto? Shell { get; set; }
    }

    /// <summary>Browser-style nav: the active route, both stacks (oldest first), and the active tab id.</summary>
    public sealed class SessionNavDto
    {
        public SessionRouteDto? Active { get; set; }
        public SessionRouteDto[]? Back { get; set; }
        public SessionRouteDto[]? Forward { get; set; }
        public int ActiveTabId { get; set; } = -1;
    }

    /// <summary>Restorable shell chrome. The rail WIDTH is a durable preference; this is the session-only
    /// PRESENTATION that must reopen exactly as it was when the app closed.</summary>
    public sealed class SessionShellDto
    {
        public bool RailOpen { get; set; }
        public int RailMode { get; set; }
    }

    /// <summary>The opaque route key + its display arg, plus the JOURNEY parent crumb. Old snapshots omit the origin
    /// fields and fail-soft to the IA trail.</summary>
    public readonly record struct SessionRouteDto(
        string Name, string? Arg,
        string? OriginLabel = null, string? OriginName = null, string? OriginArg = null);

    /// <summary>The session snapshot. UI thread for every update and for <see cref="Flush"/>; the pool task only ever
    /// touches a FROZEN dto and the path string.
    /// <para>FAIL-SOFT: a missing file is a first run. A corrupt or unreadable one is left IN PLACE and writes stay
    /// enabled, so the first successful save replaces it. A TOO-NEW version blocks writes entirely — a newer build
    /// owns the document, and a corrupt file must never be replaced by an empty one.</para></summary>
    public static class Session
    {
        public const int CurrentVersion = 1;
        /// <summary>Per stack. Deep enough to reopen a real journey, shallow enough that the document stays small.</summary>
        public const int MaxStack = 50;
        public const int SaveDebounceMs = 2000;

        static string? s_path;
        static readonly object s_gate = new();
        static SessionDto s_doc = new();
        static Timer? s_saveTimer;
        static int s_savePending, s_dirty;
        static bool s_writesBlocked;

        public static bool WritesBlocked => s_writesBlocked;

        public static string DefaultPath() => Path.Combine(Platform.LocalFolder, "WaveeMusic", "session.json");

        public static void UsePath(string path) => s_path = path;

        public static void Load()
        {
            s_path ??= DefaultPath();
            if (!File.Exists(s_path)) return;
            try
            {
                var bytes = File.ReadAllBytes(s_path);
                var dto = JsonSerializer.Deserialize(bytes, SessionJson.Default.SessionDto);
                if (dto is null) { Log.Warn("session", "session.json is unreadable; the pinned workspace stands"); return; }
                if (dto.Version > CurrentVersion)
                {
                    s_writesBlocked = true;
                    Log.Warn("session", "session.json was written by a newer build; writes are blocked this run");
                    return;
                }
                if (dto.Version < 1) return;
                lock (s_gate) s_doc = dto;
            }
            catch (Exception ex)
            {
                Log.Warn("session", "session.json could not be loaded; the pinned workspace stands", ex);
            }
        }

        /// <summary>Apply the persisted nav onto the live stacks. Returns false when there is no usable active route
        /// (the caller keeps the pinned-workspace default).</summary>
        internal static bool TryApplyNav(Nav nav, out Route active, out int tabId)
        {
            active = new Route(RouteKind.Home);
            tabId = -1;
            SessionNavDto? dtoNav;
            lock (s_gate) dtoNav = s_doc.Nav;
            if (dtoNav?.Active is not { } a || string.IsNullOrWhiteSpace(a.Name)) return false;

            nav.Back.Clear();
            nav.Forward.Clear();
            AppendCapped(dtoNav.Back, nav.Back);
            AppendCapped(dtoNav.Forward, nav.Forward);
            active = Parse(a.Name, a.Arg ?? "");
            RestoreOrigin(active, a);
            tabId = dtoNav.ActiveTabId;
            return true;
        }

        static void AppendCapped(SessionRouteDto[]? src, List<Route> dest)
        {
            if (src is null || src.Length == 0) return;
            int start = src.Length > MaxStack ? src.Length - MaxStack : 0;
            for (int i = start; i < src.Length; i++)
            {
                var r = src[i];
                if (string.IsNullOrEmpty(r.Name)) continue;
                var route = Parse(r.Name, r.Arg ?? "");
                RestoreOrigin(route, r);
                dest.Add(route);
            }
        }

        static void RestoreOrigin(in Route route, in SessionRouteDto dto)
        {
            if (dto.OriginLabel is not { Length: > 0 } label || dto.OriginName is not { Length: > 0 } name) return;
            Origins.Restore(route, new NavOrigin(label, Parse(name, dto.OriginArg ?? "")));
        }

        /// <summary>Mark the nav section dirty and debounce a save. Called from every nav verb.</summary>
        internal static void CaptureNav()
        {
            if (s_writesBlocked) return;
            var nav = new SessionNavDto
            {
                Active = Dto(s_nav.Current),
                Back = Snapshot(s_nav.Back),
                Forward = Snapshot(s_nav.Forward),
                ActiveTabId = Tabs.ActiveId,
            };
            lock (s_gate) s_doc.Nav = nav;
            Interlocked.Exchange(ref s_dirty, 1);
            ScheduleSave();
        }

        /// <summary>Persist the session-only rail presentation. EQUALITY-GATED, because the shell's effect also runs
        /// once at mount after adopting the saved values.</summary>
        public static void CaptureShell(bool railOpen, int railMode)
        {
            if (s_writesBlocked) return;
            lock (s_gate)
            {
                if (s_doc.Shell is { } cur && cur.RailOpen == railOpen && cur.RailMode == railMode) return;
                s_doc.Shell = new SessionShellDto { RailOpen = railOpen, RailMode = railMode };
            }
            Interlocked.Exchange(ref s_dirty, 1);
            ScheduleSave();
        }

        /// <summary>The shell section as last loaded/written — no second disk read.</summary>
        public static SessionShellDto? ShellSection { get { lock (s_gate) return s_doc.Shell; } }

        static SessionRouteDto Dto(in Route r)
        {
            var o = Origins.Peek(r);
            return o is { } x
                ? new SessionRouteDto(NameOf(r), ArgOf(r), x.Label, NameOf(x.Route), ArgOf(x.Route))
                : new SessionRouteDto(NameOf(r), ArgOf(r));
        }

        static SessionRouteDto[] Snapshot(List<Route> src)
        {
            int n = Math.Min(src.Count, MaxStack);
            int start = src.Count - n;
            var dest = new SessionRouteDto[n];
            for (int i = 0; i < n; i++) dest[i] = Dto(src[start + i]);
            return dest;
        }

        static void ScheduleSave()
        {
            if (s_path is null || s_writesBlocked) return;
            Interlocked.Exchange(ref s_savePending, 1);
            s_saveTimer ??= new Timer(static _ => OnSaveTimer(), null, Timeout.Infinite, Timeout.Infinite);
            try { s_saveTimer.Change(SaveDebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        static void OnSaveTimer()
        {
            if (Interlocked.Exchange(ref s_savePending, 0) == 0) return;
            if (Interlocked.Exchange(ref s_dirty, 0) == 0) return;
            SaveNow(async: true);
        }

        /// <summary>Synchronous best-effort write for shutdown. Cancels the debounce and writes on the caller's
        /// thread. A no-op when nothing is dirty or writes are blocked.</summary>
        public static void Flush()
        {
            Interlocked.Exchange(ref s_savePending, 0);
            if (Interlocked.Exchange(ref s_dirty, 0) == 0) return;
            SaveNow(async: false);
        }

        static void SaveNow(bool async)
        {
            if (s_path is not { } path || s_writesBlocked) return;
            byte[] bytes;
            lock (s_gate)
            {
                s_doc.Version = CurrentVersion;
                bytes = JsonSerializer.SerializeToUtf8Bytes(s_doc, SessionJson.Default.SessionDto);
            }
            if (async) _ = Task.Run(() => WriteThenRename(path, bytes, "session"));
            else WriteThenRename(path, bytes, "session");
        }
    }

    // ══ 8. PLAY-LOG.JSON + PLAY-RECENCY.JSON ═══════════════════════════════════════════════════════════════════════

    /// <summary>One playback start. <see cref="Context"/> is what the user pressed play ON; it is default for a bare
    /// track play, in which case a projection falls back to a Track row.</summary>
    public readonly record struct PlayEntry(EntityUri Track, EntityUri Context, long PlayedAtMs, string? ContextTitle);

    /// <summary>"Recently PLAYED" — things you listened to — as distinct from <see cref="History"/>'s "recently
    /// VISITED". A ring capped at 200, written BESIDE history.json, plus a recency sidecar.
    /// <para><b>Why the sidecar exists.</b> uri → last-played unix ms over EVERY uri a play touches: the track, the
    /// context, its album and each billed artist. That is the one "recently played" fact the library panes sort on,
    /// and it is derived HERE, at the writer, never joined at read time — a read-time join would need the entity
    /// resident and would silently answer wrong for a row nobody has fetched. MAX-MERGE: a stamp never moves
    /// backwards, so a server history older than a local play cannot demote an artist you just listened to.</para></summary>
    public static class PlayLog
    {
        /// <summary>The ring cap. 200 plays is far more than any projection needs and keeps the file a few KB.</summary>
        public const int MaxEntries = 200;

        /// <summary>Debounce. One album side is ~10 boundaries; at 2 s they become one write.</summary>
        public const int SaveDebounceMs = 2000;

        /// <summary>Hard cap on distinct recency uris. 4096 covers a few thousand artists/albums/tracks — far beyond
        /// any library pane — and keeps the sidecar a few tens of KB.</summary>
        public const int RecencyCap = 4096;

        /// <summary>Trim target once the cap is crossed: the oldest 256 stamps go in ONE pass, so a trim costs one
        /// sort per 256 NEW uris rather than one per append.</summary>
        public const int RecencyTrimTo = RecencyCap - 256;

        static readonly List<PlayEntry> s_entries = new(MaxEntries);
        static readonly Dictionary<string, long> s_recency = new(StringComparer.Ordinal);
        static readonly Signal<int> s_version = new(0);
        static string? s_path, s_recencyPath;
        static Timer? s_saveTimer;
        static int s_savePending;

        /// <summary>Bumped on every accepted append. A projection keys its rebuild on this.</summary>
        public static IReadSignal<int> Version => s_version;

        /// <summary>Newest LAST (the history convention). Live view — do not mutate.</summary>
        public static IReadOnlyList<PlayEntry> Entries => s_entries;

        /// <summary>uri → last-played unix ms.</summary>
        public static IReadOnlyDictionary<string, long> Recency => s_recency;

        public static string DefaultPath() => Path.Combine(Platform.LocalFolder, "WaveeMusic", "play-log.json");

        public static void UsePath(string path)
        {
            s_path = path;
            s_recencyPath = Path.Combine(Path.GetDirectoryName(path) ?? "", "play-recency.json");
        }

        public static void Load()
        {
            if (s_path is null) UsePath(DefaultPath());
            // The SIDECAR first, then fold the ring in: the ring is the fresher source for the last 200 plays, and
            // max-merge makes the fold idempotent either way.
            LoadRecency();
            if (s_path is { } path && File.Exists(path))
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var dtos = JsonSerializer.Deserialize(bytes, PlayLogJson.Default.PlayEntryDtoArray);
                    if (dtos is not null)
                        for (int i = 0; i < dtos.Length; i++)
                        {
                            var d = dtos[i];
                            if (string.IsNullOrEmpty(d.Track)) continue;   // a row with no track is unusable
                            s_entries.Add(new PlayEntry(EntityUri.Parse(d.Track),
                                string.IsNullOrEmpty(d.Context) ? default : EntityUri.Parse(d.Context), d.AtMs, d.Title));
                        }
                    TrimRing();
                }
                catch (Exception ex)
                {
                    s_entries.Clear();
                    PreserveCorrupt(path, ex);
                }
            }
            for (int i = 0; i < s_entries.Count; i++) StampAll(s_entries[i]);
        }

        /// <summary>Record one playback start. IDEMPOTENT at the boundary: a repeat of the SAME (track, context) pair
        /// within one second is the same play — a push storm at a track edge must not fill the ring.</summary>
        public static bool Append(EntityUri track, EntityUri context, long atMs = 0, string? contextTitle = null,
            EntityUri album = default, ReadOnlySpan<EntityUri> artists = default)
        {
            if (!track.IsValid) return false;
            if (atMs <= 0) atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (s_entries.Count > 0)
            {
                var last = s_entries[^1];
                if (last.Track == track && last.Context == context && Math.Abs(atMs - last.PlayedAtMs) < 1000)
                    return false;
            }

            var entry = new PlayEntry(track, context, atMs, string.IsNullOrEmpty(contextTitle) ? null : contextTitle);
            s_entries.Add(entry);
            TrimRing();
            StampAll(entry);
            if (album.IsValid) Stamp(album.Text, atMs);
            for (int i = 0; i < artists.Length; i++) if (artists[i].IsValid) Stamp(artists[i].Text, atMs);
            s_version.Value++;
            ScheduleSave();
            return true;
        }

        /// <summary>Fold externally-known plays in (a server recents snapshot). Only a NEWER stamp changes anything,
        /// so a revalidation that returned the same history costs no re-render.</summary>
        public static bool MergeRecency(IReadOnlyDictionary<string, long> stamps)
        {
            bool changed = false;
            foreach (var kv in stamps) changed |= Stamp(kv.Key, kv.Value);
            if (!changed) return false;
            s_version.Value++;
            ScheduleSave();
            return true;
        }

        /// <summary>The jump list's play-log half: the newest distinct CONTEXTS, newest first. Composes the SAME route
        /// key the history half does, so one <c>seen</c> set covers both.</summary>
        public static Playback.Os.JumpRow[] RecentContexts(int max)
        {
            if (max <= 0 || s_entries.Count == 0) return [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<Playback.Os.JumpRow>(Math.Min(max, s_entries.Count));
            for (int i = s_entries.Count - 1; i >= 0 && rows.Count < max; i--)
            {
                var e = s_entries[i];
                var uri = e.Context.IsValid ? e.Context : e.Track;
                if (!uri.IsValid) continue;
                var route = For(uri);
                if (route.Kind == RouteKind.NotFound) continue;
                string key = NameOf(route);
                if (!seen.Add(key)) continue;
                rows.Add(new Playback.Os.JumpRow(key, e.ContextTitle ?? Dest(route).Title, (byte)uri.Kind));   // ENTITY kind, not route kind
            }
            return rows.ToArray();
        }

        /// <summary>Drop the ring AND the recency index and delete both files (a sign-out wipe, a clear-history). The
        /// two can DIVERGE — a merge can stamp uris the local ring never saw — so this checks both.</summary>
        public static void Clear()
        {
            if (s_entries.Count == 0 && s_recency.Count == 0) return;
            s_entries.Clear();
            s_recency.Clear();
            s_version.Value++;
            Interlocked.Exchange(ref s_savePending, 0);
            if (s_path is { } p) _ = Task.Run(() => { try { File.Delete(p); } catch (Exception) { } });
            if (s_recencyPath is { } rp) _ = Task.Run(() => { try { File.Delete(rp); } catch (Exception) { } });
        }

        public static void Flush()
        {
            if (Interlocked.Exchange(ref s_savePending, 0) == 0) return;
            SaveNow();
        }

        static void StampAll(in PlayEntry e)
        {
            if (e.Track.IsValid) Stamp(e.Track.Text, e.PlayedAtMs);
            if (e.Context.IsValid) Stamp(e.Context.Text, e.PlayedAtMs);
        }

        /// <summary>Stamp one uri. Returns true when the map CHANGED (a first sighting, or a NEWER play).</summary>
        static bool Stamp(string? uri, long atMs)
        {
            if (string.IsNullOrEmpty(uri) || atMs <= 0) return false;
            if (s_recency.TryGetValue(uri, out long cur) && cur >= atMs) return false;
            s_recency[uri] = atMs;
            if (s_recency.Count > RecencyCap) TrimRecency();
            return true;
        }

        static void TrimRing()
        {
            int overflow = s_entries.Count - MaxEntries;
            if (overflow > 0) s_entries.RemoveRange(0, overflow);   // FIFO — drop the oldest
        }

        static void TrimRecency()
        {
            // Oldest-first drop down to the trim target. Allocates once per trim, which is rare by construction.
            var all = new KeyValuePair<string, long>[s_recency.Count];
            int i = 0;
            foreach (var kv in s_recency) all[i++] = kv;
            Array.Sort(all, static (a, b) => a.Value.CompareTo(b.Value));
            int drop = all.Length - RecencyTrimTo;
            for (int k = 0; k < drop; k++) s_recency.Remove(all[k].Key);
        }

        static void LoadRecency()
        {
            if (s_recencyPath is not { } path || !File.Exists(path)) return;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var map = JsonSerializer.Deserialize(bytes, PlayLogJson.Default.DictionaryStringInt64);
                if (map is null) return;
                foreach (var kv in map) Stamp(kv.Key, kv.Value);
            }
            catch (Exception ex)
            {
                // An unreadable sidecar is moved aside and logged once; it never blocks startup, because the ring fold
                // right after this call rebuilds whatever it would have contributed.
                try { File.Move(path, path + ".corrupt", overwrite: true); } catch (Exception) { }
                Log.Warn("nav", "play-recency.json could not be read; it will be rebuilt from the play log", ex);
            }
        }

        static void PreserveCorrupt(string path, Exception ex)
        {
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch (Exception) { }
            Log.Warn("nav", "play-log.json could not be read; the session starts with an empty log", ex);
        }

        static void ScheduleSave()
        {
            if (s_path is null) return;
            Interlocked.Exchange(ref s_savePending, 1);
            s_saveTimer ??= new Timer(static _ => OnSaveTimer(), null, Timeout.Infinite, Timeout.Infinite);
            try { s_saveTimer.Change(SaveDebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        static void OnSaveTimer()
        {
            if (Interlocked.Exchange(ref s_savePending, 0) == 0) return;
            SaveNow();
        }

        /// <summary>Snapshot BOTH on the CALLER's thread; the pool task then only touches the snapshots and the paths.
        /// Two write-then-rename moves, ONE pool task — a crash between them is healed by the load-time fold, so they
        /// need not be atomic together.</summary>
        static void SaveNow()
        {
            if (s_path is not { } path) return;
            int count = Math.Min(s_entries.Count, MaxEntries);
            int start = s_entries.Count - count;
            var ring = new PlayEntryDto[count];
            for (int i = 0; i < count; i++)
            {
                var e = s_entries[start + i];
                ring[i] = new PlayEntryDto(e.Track.Text, e.Context.IsValid ? e.Context.Text : null, e.PlayedAtMs, e.ContextTitle);
            }
            var recency = new Dictionary<string, long>(s_recency, StringComparer.Ordinal);
            string? recencyPath = s_recencyPath;
            _ = Task.Run(() =>
            {
                WriteThenRename(path, JsonSerializer.SerializeToUtf8Bytes(ring, PlayLogJson.Default.PlayEntryDtoArray), "play-log");
                if (recencyPath is not null)
                    WriteThenRename(recencyPath,
                        JsonSerializer.SerializeToUtf8Bytes(recency, PlayLogJson.Default.DictionaryStringInt64), "play-recency");
            });
        }
    }

    // ══ 9. THE TEACHING-TIP HOST (A10) ══════════════════════════════════════════════════════════════════════════════

    /// <summary>The IMPERATIVE half of the teaching-tip service: the persisted set, the per-session arm and the
    /// one-at-a-time latch. Every DECISION is <see cref="TipsCore"/>'s.</summary>
    public static class Tips
    {
        static string s_seen = "";
        static readonly HashSet<string> s_armedThisSession = new(StringComparer.Ordinal);

        /// <summary>The tip currently on screen, or "" — ONE at a time, process-wide. Two callouts up together read as
        /// an error state.</summary>
        public static readonly Signal<string> Active = new("");

        internal static void Load() => s_seen = Platform.Settings.Get(Platform.Keys.TipsSeen);

        /// <summary>May this tip appear right now? <paramref name="canPresent"/> is the caller's "I have an anchor and
        /// somewhere to draw it".</summary>
        public static bool ShouldShow(string tipId, bool canPresent)
            => TipsCore.ShouldShow(s_seen, tipId, s_armedThisSession.Contains(tipId), Active.Peek().Length > 0, canPresent);

        /// <summary>Arm a tip for this session and mark it on screen. Returns false when the gate refuses.</summary>
        public static bool Arm(string tipId, bool canPresent)
        {
            if (!ShouldShow(tipId, canPresent)) return false;
            s_armedThisSession.Add(tipId);
            Active.Value = tipId;
            return true;
        }

        /// <summary>The user acknowledged it — the DURABLE "don't show again".</summary>
        public static void Acknowledge(string tipId)
        {
            string next = TipsCore.Add(s_seen, tipId);
            if (!string.Equals(next, s_seen, StringComparison.Ordinal))
            {
                s_seen = next;
                Platform.Settings.Set(Platform.Keys.TipsSeen, next);
            }
            if (string.Equals(Active.Peek(), tipId, StringComparison.Ordinal)) Active.Value = "";
        }

        /// <summary>Dismissed WITHOUT acknowledging: it stays armed for this session (so it does not re-open on the
        /// next page that hosts it) but is not written to the durable set.</summary>
        public static void Dismiss(string tipId)
        {
            if (string.Equals(Active.Peek(), tipId, StringComparison.Ordinal)) Active.Value = "";
        }
    }

    // ══ 10. ONE WRITE PRIMITIVE ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>WRITE-THEN-RENAME, so a crash cannot leave a half-written document. Shared by all three stores; a
    /// failure is logged ONCE per document rather than per attempt, and the temp file is always cleaned up.</summary>
    static void WriteThenRename(string path, byte[] bytes, string what)
    {
        string tmp = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("session", what + " could not be saved; the in-memory state remains available", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        }
    }
}

// ── the three persisted documents' wire shapes ───────────────────────────────────────────────────────────────────────
//
// Namespace level, not nested inside `Shell`: a source-generated `JsonSerializerContext` cannot live inside a STATIC
// class, and the DTOs travel with their context.

/// <summary>UTC TICKS, not a <c>DateTime</c>: the kind does not round-trip, and a local time written on one side of a
/// DST boundary and read on the other moves an entry into the wrong day group.</summary>
internal readonly record struct HistoryEntryDto(string Name, string? Arg, long TicksUtc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HistoryEntryDto[]))]
internal sealed partial class HistoryJson : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Shell.SessionDto))]
internal sealed partial class SessionJson : JsonSerializerContext;

/// <summary>Short member names: the file is written at every listening session, and 200 rows of verbose keys is pure
/// waste.</summary>
internal readonly record struct PlayEntryDto(string Track, string? Context, long AtMs, string? Title = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlayEntryDto[]))]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class PlayLogJson : JsonSerializerContext;
