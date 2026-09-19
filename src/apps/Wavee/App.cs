// ── App.cs ─────────────────────────────────────────────────────────────────────────────────────────────────────────
// Main, window, composition order, GC latency mode, Glyphs before FluentAppHarness.Run (ch 00 §9.5)
//
// Role: CORE
// Owner: —
// Wave: 0
// Budget: 400 lines
// Spec: plan

using System;
using System.IO;
using System.Runtime;

namespace Wavee;

public static class App
{
    static bool s_shapesRegistered;

    /// <summary>Teach the store every catalog kind's disk shape (G-007) — the "kind owners write their shapes" half
    /// of persistence. MUST run before <see cref="Store.Boot"/> (<c>Store.Register</c> throws once the store is
    /// open: the registered set generates the schema and its fingerprint) and is called unconditionally, before the
    /// <c>--fake</c> branch, so a fake session and a real one register the identical set — <c>Store.Use(null)</c>
    /// under <c>--fake</c> already makes every call here a no-op catalog with no file behind it.
    /// <para>Idempotent and safe to call more than once: <c>Entities.Switch</c> re-warms the store for a new scope
    /// without re-booting it, so nothing after startup calls this again, but a guard costs one bool and removes any
    /// question of what a second call would do.</para></summary>
    internal static void RegisterShapes()
    {
        if (s_shapesRegistered) return;
        s_shapesRegistered = true;
        Store.Register(new ShowShape());
        Store.Register(new UserShape());
        Store.Register(new EpisodeShape());
        Store.Register(new TrackShape());
        Store.Register(new AlbumShape());
        Store.Register(new ArtistShape());
        Store.Register(new ConcertShape());
        Store.Register(new PlaylistShape());
        // The five library relations whose payload is `LibraryEdge` (Liked, SavedAlbums, FollowedArtists,
        // SavedShows, Pins) — same rule as the shapes above: `Store.Warm` fires inside `Entities.Boot` below and
        // drops any page whose applier slot is still null, so this must run before it too.
        Store.RegisterLibraryEdges();
    }

    /// <summary>The one input of the engine's MATERIAL policy (<c>FluentGpu.Dsl.Materials</c>) the platform layer cannot
    /// set for itself. Two of the three — <c>AdvancedEffectsEnabled</c> (the Transparency-effects switch) and
    /// <c>EffectsAreFast</c> — are written by the Win32 backend from the OS. <c>EnergySaver</c> is not: it comes from
    /// <c>FluentGpu.WindowsApi.Power.PowerSession</c>, a pillar that sits BESIDE the PAL rather than under it, so the
    /// platform layer holds no reference to it and the COMPOSITION ROOT owns this write (stated as such in
    /// <c>Materials.EnergySaver</c>'s own doc). True ⇒ every acrylic surface resolves to its authored fallback fill and
    /// pays no blur — exactly what WinUI's AcrylicBrush does in battery-saver mode.
    /// <para>Read once at startup, then re-read on the suspend/resume edges the app already handles:
    /// <c>Playback.Os.PowerPolicy</c> holds the live <c>PowerSession.Subscribe()</c> and a handler attached here is
    /// raised alongside its own (the same shape <c>Home.Host</c> already uses for its re-arm). A plain bool store, so
    /// the OS-worker callback needs no UI hop — the value is read during recording and one frame of staleness is
    /// invisible.</para></summary>
    static void WireMaterialPolicy()
    {
        ApplyEnergySaver();
        try
        {
            FluentGpu.WindowsApi.Power.PowerSession.Suspending += ApplyEnergySaver;
            FluentGpu.WindowsApi.Power.PowerSession.Resumed += ApplyEnergySaver;
        }
        catch (Exception ex) { Log.Warn("app", "material power policy subscribe failed", ex); }
    }

    static void ApplyEnergySaver()
    {
        // An unreadable status never costs the user their materials (the same fail-open rule as Platform.ReadPlugged).
        try { FluentGpu.Dsl.Materials.EnergySaver = FluentGpu.WindowsApi.Power.PowerSession.ReadPower().EnergySaverOn; }
        catch { FluentGpu.Dsl.Materials.EnergySaver = false; }
    }

    [STAThread]
    static int Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; // P16a, once
        Glyphs.Register();               // wavee-icons.otf PUA + bundled SegoeFluentIcons.ttf, BEFORE the harness runs, or every Fluent-Icons glyph is tofu on Win10 (ch 00 §9.5)
        Platform.ProfileRoot = Diagnostics.Probe.ProfileArg(args);   // "" = the default profile folder; read by LocalFolder (headless plan §3.6)
        Platform.Boot();                 // log, credentials, settings, update policy (shell); parses --fake; migrates the zoom mode at settings load (ch 00 §9.5)
        Log.Event(WaveeLogLevel.Info, "app", "boot.platform", "", null, Log.SinceStartMs);
        if (Diagnostics.Probe.TryRun(args, out int code))              // --headless (headless plan §3.2): no window, no Shell; installs its own marshallers
        {
            Platform.Shutdown();
            return code;
        }

        Shell.InstallMarshallers();      // FIRST of the GUI boot: every seam posts to the UI thread; posts queue until the root attaches (G-013)
        // The single-instance gate (D1), BEFORE anything registers a shape, opens the store or writes a setting: the
        // 2026-09-18 corruption was exactly this ordering inverted — a second launch's own boot ran ahead of the gate
        // and could delete/recreate the running instance's cache file out from under it. A refused acquire means
        // another instance already has this launch's activation payload; leave now, having touched no store.
        if (!Shell.AcquireInstance()) { Platform.Shutdown(); return 0; }
        RegisterShapes();                // the catalog's disk schema (G-007): before Store.Boot / Entities.Boot either way, so --fake and a real profile share one registration path
        if (!Platform.Args.Fake)
        {
            Setup.BootstrapInstall();    // fresh-install detection over the profile's disk witnesses (read-only probe), before any store opens (B5)
            StoreFiles.Reap(Platform.LocalFolder, Store.FileName);   // stale schemas, the legacy library.db set, .dead-* leftovers — after the gate, before the open
            Store.Use(Path.Combine(Platform.LocalFolder, Store.FileName));   // the persistent graph (G-007), one file per schema fingerprint; --fake stays memory-only
            // The persisted metadata-cache ceiling, applied at boot (until now only a Storage combo change set it); 0 disables the budget leg.
            Store.Policy = Store.Policy with { ByteBudget = Math.Max(0L, Platform.Settings.Get(Platform.Keys.MetadataCacheBudgetBytes)) };
        }
        Settings.ClearMetadataCache = Store.DropCatalog;   // Settings ▸ Storage ▸ Clear metadata cache — dead until now (never assigned); no-ops under --fake (store never opened)
        Entities.Boot(Platform.Scope);   // table set for the last scope, Store thread, Fetch
        Log.Event(WaveeLogLevel.Info, "app", "boot.entities", "", null, Log.SinceStartMs);
        if (Platform.Args.Fake) Entities.SeedFake(Platform.Clock.SeedEpoch);   // --fake: the offline seed (ch 31), after the tables exist and before any page reads them
        Spotify.Boot();                  // session static, signals created once; login later
        Spotify.Api.Boot();              // Fetch.Register(Transport): the planner's provider (G-003)
#if WAVEE_PLAYPLAY_LOCAL
        if (!Platform.Args.Fake) PlayPlayHost.Install();   // D3: the junctioned private package's one entry — the key deriver, the native body decryptor, the runtime host, the diagnostics source
#endif
        // A monotonic clock that never freezes (decision D22): the audio thread, the Connect snapshot and the SMTC throttles
        // stamp with it off the UI thread, and it keeps running while the window is idle, minimized or tray-hidden. Same QPC
        // domain as Design.FrameTime, so render sites convert explicitly.
        Playback.FrameNowMs = static () => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        Playback.Audio.EnumerateEndpoints = static () =>
        {
            var endpoints = FluentGpu.Windows.Wasapi.WasapiPcm.EnumerateEndpoints();
            var rows = new Playback.Audio.LocalAudioDevice[endpoints.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var endpoint = endpoints[i];
                byte kind = endpoint.FormFactor switch { 3 or 5 => 2, 8 or 9 => 4, 1 => 1, _ => 0 };
                rows[i] = new(endpoint.Id, endpoint.Name, kind, endpoint.IsDefault);
            }
            return rows;
        };
        Playback.Boot();                 // state, host loop, audio pump, os bridges
        WireMaterialPolicy();            // Materials.EnergySaver ← PowerSession: AFTER Playback.Boot, which holds the live PowerSession.Subscribe()
        if (Platform.Args.Fake) Playback.Audio.UseSilentEndpoint();              // --fake never opens a device
        Modules.Boot();
        Video.Install();                 // the video host: resolver tiers (chained after the module tier), demotion, engine log sink, attachments, placement preference (B7)
        Video.InstallMirror();           // G-220: the roster's ONE subscriber — attachments mirrored onto Track.VideoOverride/LocalVideo; after Install, which loads the roster
        Sidebar.Boot();                  // design, pane state, the layout document and pins; before its action seams and the UI (B6)
        Sidebar.InstallActionSeams();    // Actions.Services.IsPinned / SetPinned (J1)
        Spotify.Library.Install();       // library writes + sync on Online + the pin bridge; after Sidebar.Boot, before the pane mounts (B2)
        Log.Event(WaveeLogLevel.Info, "app", "boot.playback", "", null, Log.SinceStartMs);
        Shell.InstallUi();               // RootFactory + the Wave-4 pages; must precede Run (I1)
        Modules.InstallUi();             // the module: page, ModuleOpen/LocalPath, Shell.LinkModules/MatchLink (WP-6.T)
        Queue.InstallUi();               // rail/stage queue bodies, the queue verbs and Shell.OnPlayContext; after Actions.Registry.Build (WP-5.Q)
        Track.InstallActions();          // the track row verbs; AFTER Queue.InstallUi, whose Play/PlayNext/AddToQueue registrations win (WP-4.5)
        // EAGER, deliberately — this was made lazy and then REVERTED (2026-09-15). `Shell.PageFor` still carries the
        // install-on-miss seam (Shell.cs §1.3) as a safety net, and the ordering guard in `BootOrderingTests` is worth
        // keeping either way, but these three calls stay here.
        //
        // WHY: these installers do TWO jobs. They register page factories — which really is deferrable — and they also
        // install CROSS-CUTTING SEAMS that nothing navigational triggers. Deferring them silently disabled three
        // working features until the user happened to visit an unrelated page: "View credits" on a track row (needs
        // Album's group), the omnibar's suggestion source (Home's), and the whole-window drop-a-file-to-play target
        // (Playlist's). They fail closed rather than throwing, which makes it WORSE, not better — a feature that is
        // quietly absent reports as nothing at all.
        //
        // The measured prize was `boot.pages` ≈ 78 ms out of a 1,464 ms first frame (~5%), and the dominant cost is
        // the 735 ms the ENGINE spends between window creation and first present. Three silently-dead features is not
        // a trade worth 5%. If this is revisited: split the installers so the seams stay eager and only the factory
        // registration defers — do not defer a whole group again.
        Home.InstallPages();             // home, search, browse, recents + the omnibar's suggestion source (WP-5.P)
        Album.InstallPages();            // album, prerelease, show + the track row's "View credits" seam (WP-5.M)
        Playlist.InstallPages();         // playlist, liked, library + the window's file-drop play target (WP-5.O)
        Artist.InstallPages();           // artist + discography + concerts (hub, schedule, detail), ConcertHost, Sidebar.ConcertsFetch — EAGER: before the sidebar pane's first mount (WP-5.N)
        Settings.InstallScreens();       // the Settings page, What's new, the report dialog and the setup wizard (WP-6.R)
        Diagnostics.Install();           // run marker, diagnostics pages, network cost host, the screens' diagnostics seams (WP-6.S)
        Log.Event(WaveeLogLevel.Info, "app", "boot.pages", "", null, Log.SinceStartMs);
        Shell.StartUpdater();            // AFTER Diagnostics.Install: the updater reads Update.Host.IsMetered at start (WP-6.R)
        if (!Platform.Args.Fake)         // --fake stays providerless (ch 31 GAP 7 is a fixture owner Q's, not this seam's)
            Lyrics.Boot(Lyrics.ResolveRequest, Spotify.Api.GetTextAsync, Spotify.SpclientBaseUrl);   // G-008: compose the lyrics stack once
        if (Platform.Args.Fake) Spotify.BootFake();                        // --fake: the session presents Online over the seeded account, no network (G-065)
        else if (Platform.HasStoredCredential()) Spotify.Login();         // resume the stored credential at launch (G-002); its posts wait for the root's poster
        Shell.Run();                     // window + engine loop; never returns until exit
        Platform.UiThreadId = null;      // the loop and its SettingsChanged subscribers are gone: the exit tail writes settings from Main

        // The exit tail (G-012), after the loop and while the log is still alive: layout write, telemetry batch, audio,
        // video, the graph store, the stream caches, then the platform (whose log sink flushes last).
        Spotify.Library.Shutdown();      // its watches and settle timers, and the pin store's hook, before the sidebar's layout write
        Sidebar.Shutdown();
        Spotify.Telemetry.Shutdown();
        Playback.Audio.Shutdown();
        Playback.Video.Shutdown();
        Modules.Shutdown();              // the module processes and their channels, after playback stopped reading them
        Store.Shutdown();
        (Spotify.Audio.DiskCache.Shared as IDisposable)?.Dispose();
        Spotify.Audio.Fetcher.Shared.Dispose();
        Update.Host.ApplyOnExit();       // a staged update applies on the way out (the policy is Notify.ShutdownUpdatePolicy's), then the timers stop
        Update.Host.Shutdown();
        Diagnostics.Shutdown();          // the last frame/memory lines, the clean run marker, the cost subscriptions
        Platform.Shutdown();             // the log's file sink writes on a pool thread: without this the exit tail never reaches wavee-yyyyMMdd.log
        return 0;
    }
}
