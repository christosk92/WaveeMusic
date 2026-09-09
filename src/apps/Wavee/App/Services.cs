using System.Linq;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.SpotifyLive;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>
/// The hand-wired composition root — plain <c>new</c>, NO reflection container (AOT-visible, zero startup tax). Holds the
/// Core service instances + the <see cref="PlaybackBridge"/>. Swap <see cref="CreateFake"/> for a real wiring later;
/// nothing else changes because the UI only ever sees the interfaces + the bridge.
/// </summary>
public sealed class Services
{
    public Wavee.Backend.CatalogRuntime Data { get; }
    public IQueryService Queries => Data.Queries;
    public CatalogScope CatalogScope => CatalogScopeSignal.Value;
    public Signal<CatalogScope> CatalogScopeSignal { get; }
    public System.Threading.Tasks.Task DataReady { get; }
    public Wavee.Backend.Catalog.NativeCatalogBootstrap NativeCatalog { get; }
    readonly System.IDisposable _catalogColors;
    /// <summary>The app's UI-thread marshaller, captured once <see cref="Wavee.SpotifyLive.LiveSessionHost.StartAsync"/>
    /// knows it (findings 4.2's scope-handover fix — <see cref="Data"/>'s <c>Session</c> publication is observed from
    /// this constructor, before any login attempt exists to hand one over). Runs inline until then, which is safe:
    /// nothing publishes a <c>Session</c> change before a live session is attempted.</summary>
    // Dropped until LiveSessionHost attaches the UI marshaller; AttachUiPost then posts one catch-up Publish.
    System.Action<System.Action> _uiPost = static _ => { };
    readonly CatalogScopePublisher _scopePublisher;
    internal Wavee.Backend.Catalog.SwitchableCatalogResourceProvider? SpotifyCatalogProvider { get; private set; }
    /// <summary>Context slot — provide at the root, read with <c>UseContext(Services.Slot)</c>.</summary>
    public static readonly Context<Services?> Slot = new(null);

    /// <summary>When set (via the <c>--real-backend</c> flag), the app wires the persistent Store-backed catalog instead
    /// of the FakeData demo. Off by default until live sync (login → fetchers → dealer) is verified end to end.</summary>
    public static bool UseRealBackend;

    /// <summary>THE playback-module host: the catalog, one lazy child process per installed module, the resolve cache
    /// and the permission-gated host services. Composed PRE-LOGIN (it needs no session, no network and no credentials)
    /// and disposed with this Services; reference-stable for the app lifetime, so it is props-freeze safe. Null on the
    /// fake backend, which has no audio stack to play a module's answer with.
    /// <para>The UI reads <c>Modules.Installed</c> for the Play ▸ submenu and nothing else — a component never names a
    /// module type, exactly as it never names <c>SpotifyMediaProvider</c>.</para></summary>
    public Wavee.Backend.Modules.ModuleHost? Modules { get; private set; }

    /// <summary>THE playback routing table: <c>MediaProviderRegistry(localFile, generic, …modules)</c>, built pre-login
    /// so a non-Spotify playable never needed a session to route. Go-live PREPENDS <c>SpotifyMediaProvider</c> (first
    /// Owns wins, and Spotify is every hot playable) by building a superset registry over <see cref="MediaProviders"/>
    /// — registration order is the routing table. Null on the fake backend.</summary>
    public Wavee.Backend.MediaSources.MediaProviderRegistry? MediaProviders { get; private set; }

    /// <summary>Effective library membership projection for the mutation and protocol adapters.</summary>
    public Wavee.Backend.IStore? RealStore { get; private set; }

    /// <summary>The user's LOCAL VIDEO OVERRIDE curation (REAL backend only — it is store-backed; null for the fake, where
    /// every override path is unreachable). The one instance shared by the resolver's tier 1, the playback bridge's
    /// has-video answer, and (in P3) the menu/settings surfaces: <c>Attach</c> / <c>Remove</c> / <c>All</c> / <c>Decide</c>.</summary>
    public VideoOverrideService? VideoOverrides { get; private set; }

    /// <summary>The switchable mutation transport (REAL backend only): stub until go-live, then the live dealer transport,
    /// back to stub on logout — so writes made while logged out queue in the durable outbox and replay on next login (§2.1).</summary>
    public Wavee.Backend.SwitchableTransport? MutTransport { get; private set; }
    /// <summary>Durable normalized catalog, scoped replicas and user curation.</summary>
    public Wavee.Backend.Persistence.SqliteColdStore? RealCold { get; private set; }
    /// <summary>Serialized cache retention, budget accounting and idle compaction.</summary>
    public Wavee.Backend.Persistence.CatalogCacheMaintenance? CacheGc { get; private set; }
    /// <summary>The durable mutation engine (REAL backend only) — exposed so the sync loop drains it + the collection
    /// fetcher's mark-and-sweep can consult its pending-op shield.</summary>
    public Wavee.Backend.MutationEngine? RealMutations { get; private set; }
    /// <summary>I4 — the ONE post-drain revalidation queue (REAL backend only). Constructed with the mutation engine so
    /// the replay strategy and the go-live sync loop share the same instance; never optional on either side.</summary>
    public Wavee.Backend.Playlists.PlaylistResyncQueue? RealResyncQueue { get; private set; }
    /// <summary>The ambient session host (REAL backend only) — the real username is set into it on go-live so write bodies
    /// carry a valid account.</summary>
    public Wavee.Backend.SessionContextHost? RealSessionHost { get; private set; }
    /// <summary>The collection self-write echo registry (REAL backend only, §7.1) — the write strategy records accepted-write
    /// cuids here; the sync loop checks it to drop our own PubSubUpdate echoes before any store work.</summary>
    public Wavee.Backend.Collections.CollectionEchoRing? EchoRing { get; private set; }
    /// <summary>The single library-sync writer loop (REAL backend only, after go-live) — the on-open SWR + DetailPage
    /// live-refresh hooks reach it here. Null offline / fake backend.</summary>
    public Wavee.Backend.Sync.LibrarySync? RealSync => Data.LiveSync;
    /// <summary>Reactive live capability for server-driven automatic-playlist tuning.</summary>
    public Signal<IPlaylistTuningSource?> PlaylistTuning { get; } = new(null);
    /// <summary>The selected home facet chip id (Spotify <c>home.homeChips[].id</c>, e.g. "music-chip" or
    /// "podcasts-following-chip"); null/empty = the unfiltered feed. Written by the home chip row, read by the live
    /// home fetch when it builds the <c>facet</c> request variable. Opaque server token — never synthesised.</summary>
    public Signal<string?> HomeFacet { get; } = new(null);
    /// <summary>The engine-backed Mutations seam adapter (REAL backend only) — exposed so go-live can route its post-write
    /// drains through the sync loop (§6, <c>ScheduleDrain</c>) and GoOffline can reset them to inline.</summary>
    public Wavee.Backend.EngineMutationSource? RealMutationSource { get; private set; }
    /// <summary>Spotify playlist item/metadata/cover edits (REAL backend only).</summary>
    public Wavee.Backend.Playlists.PlaylistMutationSource? RealPlaylistMutations { get; private set; }
    /// <summary>Spotify "recommended songs" playlist extender (REAL backend only). Bound to the SAME switchable mutation
    /// transport, so it follows the go-live/logout lifecycle. Null when fake / logged out → the UI gates on it.</summary>
    public Wavee.Backend.Playlists.PlaylistExtenderClient? RealExtender { get; private set; }
    public Wavee.Backend.SpclientBaseUrlHolder? RealSpclientBaseUrl { get; private set; }

    /// <summary>Authenticated spclient HTTP pipeline (REAL backend, after go-live). Used by the API Console page for
    /// arbitrary requests with the same auth/client-token middleware as production fetchers.</summary>
    public Wavee.Backend.Spotify.IHttpExchange? LiveHttp { get; internal set; }

    /// <summary>The live Connect session host (REAL backend, after a successful login) — captured for logout teardown.
    /// Set via <see cref="AttachLive"/> BEFORE <see cref="GoLive"/> so a logout in the go-live window still tears down the
    /// live transport + dealer cleanly (not a no-op).</summary>
    public Wavee.SpotifyLive.LiveSessionHost? LiveHost { get; private set; }

    /// <summary>The pre-login/logged-out local audio host's DSP surface (local files, internet radio, playback
    /// modules) — the one <see cref="_preLogin"/> stands up before go-live and rebuilds on logout
    /// (<see cref="BuildLoggedOutPlayer"/>). Mutually exclusive with <see cref="LiveHost"/> in practice (go-live
    /// disposes it), but exposed unconditionally so <c>PlaybackDsp.Push</c> can seed/push to whichever one actually
    /// exists without knowing which mode the app is in. Null once a live session takes over, or before either media
    /// stack has been built.</summary>
    internal Wavee.Backend.IAudioDspControl? LocalAudioDsp => _preLogin?.AudioHost as Wavee.Backend.IAudioDspControl;

    /// <summary>THE go-live install ledger (design §2.6). <c>LiveSessionHost.StartAsync</c> creates it and hands it over
    /// BEFORE its first install — earlier than <see cref="AttachLive"/>, because the very first live install (the video
    /// media hooks) happens before the host object exists — so <see cref="GoOffline"/> can undo a bootstrap that failed
    /// anywhere. Null until a go-live starts and again after it is torn down.</summary>
    internal Wavee.Backend.Wiring.LiveWiring? Wiring { get; private set; }

    /// <summary>Every seam a successful go-live MUST install — with its teardown — through <see cref="Wiring"/>. The
    /// roster itself lives in <see cref="Wavee.Backend.Wiring.LiveSeams"/> (under <c>Backend/</c>, so Wavee.Tests can
    /// compile and pin it; this file drags the whole engine in and cannot be test-compiled).
    /// <c>LiveSessionHost.StartAsync</c> ends with <c>wiring.AssertCovers(Services.LiveSeams)</c>, which throws naming
    /// any seam that was installed without an inverse — the gate that replaced the hand-maintained GoOffline list.</summary>
    public static readonly string[] LiveSeams = Wavee.Backend.Wiring.LiveSeams.All;
    /// <summary>PlayPlay runtime provisioner (live session only) — drives the setup modal and banner.</summary>
    public Wavee.SpotifyLive.Audio.IPlayPlayProvisioner? PlayPlayProvisioner { get; internal set; }
    public Wavee.Sdk.Streams.ChunkDiskCache? AudioBodyCache { get; internal set; }
    public Wavee.SpotifyLive.Audio.LicenseKeyDiskCache? AudioLicenseCache { get; internal set; }
    /// <summary>The persisted-credential store backing the live session — cleared on logout so the next launch can't
    /// silently re-login.</summary>
    public Wavee.Backend.Persistence.ICredentialStore? CredStore { get; private set; }
    /// <summary>Where the live session records the exact <see cref="CatalogScope"/> it installed, so the NEXT launch can
    /// serve this account's cached library under that scope before there is any session (see the provisional-scope block
    /// in <see cref="CreateReal"/>). Null on the fake backend, which has no credentials and no cold store.</summary>
    public Wavee.Backend.Persistence.ISessionScopeStore? SessionScopes { get; private set; }

    public IWaveeLog Log { get; }
    public ISpotifySession Session { get; }
    public IMusicLibrary Library { get; }
    public IPlaybackPlayer Player { get; }
    public IConnectDevices Devices { get; }
    /// <summary>Realtime (dealer socket) connection status — so the UI can surface "Reconnecting…" on a network drop
    /// instead of silently going stale. Driven by the live transport's socket lifecycle; offline until go-live.</summary>
    public IConnectivity Connectivity { get; }
    public ILyricsProvider Lyrics { get; }
    /// <summary>Progressive, below-the-fold album data. Stable wrapper; the live Spotify implementation is installed
    /// after login while mounted pages keep the same service identity.</summary>
    public SwitchableAlbumEnrichmentService AlbumEnrichment { get; }
    /// <summary>The signed-in user's own top artists and tracks (<c>userTopContent</c>, 4-week affinity) — Home's
    /// top-artist row and its personal track badges.
    /// Stable wrapper; the live provider is installed after login, offline/fake it is <see cref="NullUserTopService"/>,
    /// which returns an empty list so the row simply does not render.</summary>
    public SwitchableUserTopService UserTop { get; }
    /// <summary>Playlist save counts (the spclient <c>popcount</c> endpoint) for the playlist header's meta line.
    /// Stable wrapper; the live provider is installed on go-live. Offline it is <see cref="NullPlaylistPopcountService"/>,
    /// which returns null so the header simply omits the segment.</summary>
    public SwitchablePlaylistPopcountService PlaylistPopcount { get; }
    /// <summary>Upcoming-release resolution (extended-metadata kind 138): prerelease uri ↔ album uri ↔ release instant.
    /// Stable wrapper; the live provider is installed on go-live. Offline it is <see cref="NullPreReleaseService"/>, and
    /// every prerelease surface then degrades to "announced, but not pre-savable / not click-through-resolvable".</summary>
    public SwitchablePreReleaseService PreRelease { get; }
    /// <summary>The uncapped credits drawer (extended-metadata kind 186). Stable wrapper; the live provider is installed
    /// on go-live. Offline it is <see cref="NullTrackCreditsService"/> (null), and both credits surfaces fall back to the
    /// NPV contributor list — capped at ten rows, but there.</summary>
    public SwitchableTrackCreditsService TrackCredits { get; }
    /// <summary>Spotify's curated Liked Songs content-filter chips. Stable wrapper; the live provider is installed on
    /// go-live. Offline it is <see cref="NullContentFilterService"/> (empty), and the Liked chip bar then derives its
    /// chips from the tracks' own kind-6 descriptors instead of showing nothing.</summary>
    public SwitchableContentFilterService ContentFilters { get; }
    /// <summary>Spotify friend-activity (presence) feed — what friends are listening to. Stable wrapper; the live provider
    /// is installed after login, offline/fake it is the permanently-offline <see cref="NullFriendActivityService"/>.</summary>
    public SwitchableFriendActivityService Friends { get; }
    public PlaybackBridge Playback { get; }
    /// <summary>The Mutations facet bridge (saved/liked/followed → engine Signal). Read via <see cref="LibraryBridge.Slot"/>.</summary>
    public LibraryBridge LibraryBridge { get; }
    /// <summary>The friends-feed facet bridge (presence snapshot → engine Signals). Read via <see cref="FriendsBridge.Slot"/>.</summary>
    public FriendsBridge FriendsBridge { get; }
    /// <summary>The local activity log (library-mutation history + Undo source). Durable (SQLite) on real, in-memory on fake.</summary>
    public ActivityLog Activity { get; }
    /// <summary>Spotify social notifications (gander). Stable wrapper; the live provider is installed after login, offline/fake
    /// it is the permanently-offline <see cref="NullSpotifyNotificationsService"/>.</summary>
    public SwitchableSpotifyNotificationsService SpotifyNotifications { get; }
    /// <summary>"What's New" (new releases/episodes from followed artists). Stable wrapper; live provider installed after login.</summary>
    public SwitchableWhatsNewService WhatsNew { get; }
    /// <summary>Concert discovery (artist schedules, hub feed, location controls). Stable wrapper; the live Spotify
    /// Pathfinder adapter is installed after login, offline/fake it is the permanently-offline <see cref="NullConcertService"/>.</summary>
    public SwitchableConcertService Concerts { get; }
    /// <summary>Spotify Browse (the category directory + category pages). Stable wrapper; the live Pathfinder adapter
    /// is installed on go-live, and offline/fake it is <see cref="NullBrowseService"/> so the directory renders its
    /// empty state instead of the UI holding a null.</summary>
    public Wavee.SpotifyLive.SwitchableBrowseService Browse { get; }
    /// <summary>Alternate versions (music videos / live / remix) + available audio formats for the expanded track
    /// drawer. Everything it serves is fetched ON EXPAND, never with the row bundle.</summary>
    public Wavee.SpotifyLive.SwitchableTrackExpansionService TrackExpansion { get; }
    /// <summary>The recents page's read seam (<c>GET /playlist/v2/list/recents/page[/diff]</c>). Stable identity; the
    /// live <c>RecentsFetcher</c> is installed on go-live and reset on logout, so a mounted page holds this for the whole
    /// session. Offline/fake it is <see cref="NullRecentsService"/> — the page renders its empty state rather than the UI
    /// holding a null. The source is STATELESS: the page owns the last revision + rows it revalidates against.</summary>
    public SwitchableRecentsService Recents { get; }
    /// <summary>Home's "Show all" read seam (the <c>homeSection</c> Pathfinder operation). Stable identity; the live
    /// adapter is installed on go-live and reset on logout. Offline/fake it is <see cref="NullHomeSectionService"/>, so
    /// a drill-in shows whatever Home seeded and pages no further instead of the UI holding a null. Separate from
    /// <see cref="Browse"/> on purpose: a <c>spotify:section:</c> URI is a HOME resource, not a browse one.</summary>
    public SwitchableHomeSectionService HomeSections { get; }
    /// <summary>One-shot OS geolocation (the "Use my location" concert flow). App/OS-scoped → hand-wired here like the other
    /// OS services (never switchable). Requested ONLY on an explicit user action; constructing it prompts nothing.</summary>
    public FluentGpu.Pal.IGeolocationProvider Geolocation { get; }
    /// <summary>The app-update seam (feed poll + packaged deployment). App-scoped → one per process, no switchable.</summary>
    public IAppUpdateService AppUpdate { get; }
    /// <summary>The release-notes ("What's new") document store: embedded → cache → release asset, plus the rolling
    /// index. App-scoped like the updater, and constructed BEFORE it — the update check names the version it is
    /// offering out of this store's index.</summary>
    public ReleaseNotesStore ReleaseNotes { get; }
    /// <summary>The notification-center bridge (four categories → one aggregated feed + bell badge). Read via <see cref="NotificationCenterBridge.Slot"/>.</summary>
    public NotificationCenterBridge Notifications { get; }
    /// <summary>The root library cache (collections + per-entity detail caches) for instant, off-page-fresh navigation.</summary>
    public LibraryStore LibraryStore { get; }
    /// <summary>Persisted app settings (sidebar width, etc.) — read/written through the interface + typed keys, never the
    /// concrete store. The real registry-backed store is wired here, in the composition root, not at the call sites.</summary>
    public IAppSettings Settings { get; }
    /// <summary>The immutable UI/Spotify locale captured at process startup.</summary>
    public AppLocale Locale { get; }
    /// <summary>All sidebar state: the active design, per-design pane/view state, the shared unlimited pin store, the entry
    /// projection cell, and the Curated layout document with its 50-step undo stack. Owned HERE (not by the shell) so the
    /// pin store, the Settings picker and the customizer's undo history survive the login-gate shell swap; provided at the
    /// app root via <c>SidebarPreferences.Slot</c>. Constructed in the shared private ctor, so both CreateFake and
    /// CreateReal get one — local preferences are real on every backend (the settings store already is).</summary>
    public SidebarPreferences Sidebar { get; }
    /// <summary>The Spotify ylpin↔sidebar-pin bridge (REAL backend only; null for the fake, which has no server pin set
    /// to mirror). Read/write both directions through the existing collection-set + mutation-outbox plumbing
    /// (docs/plans/wavee/pin-spotify-sync-implementation.md). <c>Activate</c> is called from the same mount effect that
    /// activates <see cref="Sidebar"/>.</summary>
    public SidebarPinSync? PinSync { get; private set; }
    /// <summary>Home layout preferences (visibility + order). Local document beside sidebar-layout.json; real on
    /// every backend. Provided at the app root via <c>HomePreferences.Slot</c>.</summary>
    public HomePreferences Home { get; }
    /// <summary>The local "recently played" log (§C1.8.1) — a ring of the last 200 playback starts beside
    /// <c>history.json</c>. Appended by <see cref="PlaybackBridge"/> at every real track boundary and read by the
    /// sidebar's <c>wavee.history.played</c> source. Local state, so it is real on BOTH backends (the settings-store
    /// precedent).</summary>
    public PlayLogStore PlayLog { get; } = new();
    /// <summary>The sidebar's contribution lookup: the nine first-party data sources, keyed by their namespaced ids
    /// (<c>wavee.library</c>, <c>wavee.artist.topTracks</c>, …). This is what a section's contribution id resolves through
    /// — nothing in the UI may <c>switch</c> on an extension id. Published into the platform registry by
    /// <see cref="RegisterSidebarSources"/>.</summary>
    public SidebarDataSourceTable SidebarSources { get; }
    /// <summary>The ONE driver of <c>SidebarPreferences.Entries</c> and of the Curated planner input: it rebuilds the
    /// unified projection whenever the library / history / play log / pins / view state / culture move, and resolves every
    /// extension section to a row slice. Mount <c>SidebarBinder.MountPoint()</c> once at the app root (see its remarks) —
    /// the binder does nothing until that pump is alive.</summary>
    public SidebarProjectionBinder SidebarBinder { get; }
    /// <summary>The cross-arena memory-shedding coordinator (Backend/Residency/MemoryGovernor.cs), instantiated + wired here
    /// and driven by a periodic OS-memory-pressure poll (WaveeApp). Steady-state growth is already bounded by each cache's
    /// own LRU cap; the governor sheds FURTHER under real memory pressure. (Was dead code — only referenced by tests.)</summary>
    public Wavee.Backend.Residency.MemoryGovernor Residency { get; } = new();

    // Entity-residency caps (hardcoded good defaults, no env knobs). The governor arena (priority 3, CRITICAL-only) sheds
    // resident entities down to EntityResidencyCap using the reachability pin-set; SavedHeadPinCount bounds how many members
    // of each saved set the pin-set keeps warm so a collection page still paints without a cold round-trip. The always-on
    // 12k→8k upsert backstop in InMemoryStore is the real week-long bound; these size the pressure-driven top-up.
    const int EntityResidencyCap = 4000;
    const int SavedHeadPinCount = 200;

    /// <summary>Floor for the user-settable metadata-cache budget (§G) — below this the pin set alone overflows it.</summary>
    public const long MinMetadataCacheBudgetBytes = 32L * 1024 * 1024;

    /// <summary>App-side census contributor for the engine's FG_MEM_DIAG <c>[memcensus]</c> block: the entity-store + cache
    /// attribution line. The Windows host composes this into <c>AppHost.GpuDetail</c> (wired in <c>Program</c>'s
    /// DiagnosticRun, which runs once per launch); set on app mount. Built on demand at census cadence — never per frame.
    /// Null ⇒ no app line. Last-writer-wins is fine (one Services per process; tests don't set it).</summary>
    public static System.Func<string>? MemCensusHook;

    Services(IWaveeLog log, ISpotifySession session, IMusicLibrary library,
             IPlaybackPlayer player, IConnectDevices devices, ILyricsProvider lyrics, IAppSettings settings, IMutationSource mutations,
             UserPlaylistSource userPlaylists, IPlaylistMutationSource playlistEdits, IActivityStore activityStore, AppLocale appLocale,
             Wavee.Backend.CatalogRuntime data, Wavee.Backend.Catalog.NativeCatalogBootstrap nativeCatalog)
    {
        Log = log;
        Data = data;
        CatalogScopeSignal = new(data.Catalog.Scope);
        NativeCatalog = nativeCatalog;
        DataReady = InitializeDataAsync();
        Session = session;
        Library = library;
        Player = player;
        Devices = devices;
        Connectivity = new Wavee.Backend.SwitchableConnectivity(new Wavee.Backend.Connectivity());
        Lyrics = lyrics;
        AlbumEnrichment = new SwitchableAlbumEnrichmentService(new CatalogAlbumEnrichmentService(library));
        UserTop = new SwitchableUserTopService(new NullUserTopService());
        PlaylistPopcount = new SwitchablePlaylistPopcountService(NullPlaylistPopcountService.Instance);
        PreRelease = new SwitchablePreReleaseService(NullPreReleaseService.Instance);
        TrackCredits = new SwitchableTrackCreditsService(NullTrackCreditsService.Instance);
        ContentFilters = new SwitchableContentFilterService(NullContentFilterService.Instance);
        Friends = new SwitchableFriendActivityService(new NullFriendActivityService());
        Settings = settings;
        Locale = appLocale;
        // Sidebar preferences + the sidebar-layout document. Both CreateFake and CreateReal funnel through this ctor, so
        // both backends get one; the store's Load() is the only I/O and it never throws (a missing file is a first run, a
        // bad one is a fault that suppresses writes and loads the built-in Curated default in memory).
        Sidebar = new SidebarPreferences(settings, SidebarLayoutStore.ForApp());
        Home = new HomePreferences(HomeLayoutStore.ForApp());
        Playback = new PlaybackBridge(player, devices, session);
        Playback.AttachQueueQueries(Queries, data.Catalog.Scope);
        // Seed the movable video surface from persisted settings BEFORE the first frame, so the remembered placement is
        // already in effect rather than popping in after the shell mounts.
        Playback.SeedVideoSurfaceFromSettings(settings);
        // Seed the host-capability bits the video placement policy masks availability against
        // (VideoUpgradeGate.AvailabilityFor). PERMISSIVE, not conservative: this constructor runs strictly BEFORE
        // AppHost wires ANY InputHooks delegate (WaveeApp's ctor, which builds this Services instance, is evaluated
        // as an argument to `new AppHost(...)` in FluentApp.cs, before AppHost's own body runs) — so there is no live
        // hook here to ask, and there used to be a dead read of `InputHooks.Current.Default` that always saw null and
        // silently left Detached/Fullscreen off. Since AvailabilityFor now INTERSECTS with this capability set, that
        // conservative seed was a live regression: it masked OUT Detached (breaking the existing pop-out window,
        // which nothing was ever wrong with) and masked OUT Fullscreen (the very feature this phase adds), for every
        // frame before WaveeShell's own effect narrows the set from live hooks.
        //
        // A refused placement is already discovered on ATTEMPT, not on availability — VideoPlacementHost reports a
        // refused OpenDetachedWindow as a close (falls back to the mini player), which is exactly the "unavailable"
        // outcome a correct capability bit would have produced anyway. So seeding all four bits open here costs
        // nothing behaviourally and preserves today's behaviour, while WaveeShell.cs narrows Docked/Floating/
        // Detached/Fullscreen from the real rail-fit test + live InputHooks the moment it renders its first frame.
        Playback.HostPlacementCapability.Value =
            PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;
        Activity = new ActivityLog(activityStore);
        LibraryBridge = new LibraryBridge(mutations, userPlaylists, playlistEdits, Activity);
        FriendsBridge = new FriendsBridge(Friends);
        // Notification center: the four category sources + the aggregation bridge (the friend-activity seam pattern).
        SpotifyNotifications = new SwitchableSpotifyNotificationsService(new NullSpotifyNotificationsService());
        WhatsNew = new SwitchableWhatsNewService(new NullWhatsNewService());
        Concerts = new SwitchableConcertService(new NullConcertService());   // live Pathfinder adapter installed on go-live
        Browse = new Wavee.SpotifyLive.SwitchableBrowseService();            // ditto — Null until go-live
        TrackExpansion = new Wavee.SpotifyLive.SwitchableTrackExpansionService();
        Recents = new SwitchableRecentsService();                            // ditto — the Null source until go-live
        HomeSections = new SwitchableHomeSectionService();                   // ditto — Home's "Show all" paging axis
        Geolocation = new FluentGpu.WindowsApi.Location.WindowsGeolocationProvider();   // OS one-shot; no prompt until used
        string updateArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var githubHttp = Wavee.Backend.Spotify.HttpPools.Get(Wavee.Backend.Spotify.HttpPool.GitHub);
        // The notes ride the SAME stamped download root as the update feed (AppVersion.Info.UpdateBaseUrl): a build
        // packed against a loopback feed must not silently fall back to reading its documents off GitHub.
        ReleaseNotes = new ReleaseNotesStore(githubHttp, SettingsShared.AppDataRoot, AppVersion.Info.FeedRelease, Log,
            releasesRoot: AppVersion.Info.UpdateBaseUrl);
        // "Was this launch an update?" is armed ONCE here, BEFORE the IsStore branch below, so both install shapes
        // arm identically and WaveeSettings.LastRunVersion is written exactly once per launch either way. This used
        // to live inside AppInstallerUpdateService's ctor, which a Store-installed build never constructs — so a
        // Store build silently never wrote LastRunVersion and its after-update plate (AfterUpdateChrome, which reads
        // ReleaseNotesPendingFrom straight off settings) could never open. See AppLaunchVersion's remarks.
        string updatedFrom = AppLaunchVersion.Arm(settings, AppVersion.Info, Log,
            FluentGpu.WindowsApi.Packaging.PackageIdentity.PackageFullName is { Length: > 0 } pfn ? pfn : "unpackaged");
        // A Store-installed build never polls the feed: the Store owns downloads, staging and the restart, so the
        // updater is the one that opens the product page and stays Idle (no toast, no install-on-quit).
        AppUpdate = AppVersion.Info.IsStore
            ? new StoreUpdateService(AppVersion.Info.StoreId, static url => ShellOpen.OpenUrl(url))
            : new AppInstallerUpdateService(settings, githubHttp, AppVersion.Info, updateArch,
            FluentGpu.WindowsApi.Packaging.PackageIdentity.IsPackaged
                // RestartArgument: Windows relaunches us after the deployment terminates us, and without an explicit
                // command line it reuses the original one — so the process that comes back cannot tell it came back.
                // The flag is inert (Program.Main logs one line for it); it exists so a log/E2E run can see the seam.
                ? new FluentGpu.WindowsApi.Packaging.PackageUpdater { RestartArgument = AppRelaunch.RelaunchedAfterUpdateFlag }
                : new FluentGpu.WindowsApi.Packaging.NullPackageUpdater(),
            Log,
            updatedFrom,
            isMetered: static () => NetworkPolicy.IsMetered,
            openUrl: static url => ShellOpen.OpenUrl(url),
            notes: ReleaseNotes);
        Notifications = new NotificationCenterBridge(Activity, SpotifyNotifications, WhatsNew, AppUpdate, settings,
            new ActivityUndoExecutor(LibraryBridge, library, Activity));
        LibraryStore = new LibraryStore(Queries, CatalogScope);

        // ── the local play log + the extension platform + the sidebar projection driver (M1) ──
        // Both Create paths funnel through this ctor, so the fake backend gets the same wiring over its own fake stores.
        // The play log is LOCAL state (like the settings store), so it is real on both backends.
        PlayLog.Init(PlayLogStore.DefaultPath());
        PlayLog.LoadFromDisk();
        Playback.AttachPlayLog(PlayLog);

        // The binder IS the sources' projection snapshot, so it is constructed first and handed to the registration.
        // The ACTION half of the first-party extension (BuiltInExtensionTable) is registered by
        // WaveeExtensionRegistry.Build(ActionServices) from the shell — it needs the service bag the shell owns — and this
        // table is published into that registry by RegisterSidebarSources below. The sidebar never waits for it: its own
        // host resolves the first-party table directly, and consults the registry only for contributed (third-party) ids.
        SidebarBinder = new SidebarProjectionBinder(Sidebar, Queries, CatalogScope, PlayLog, Playback);
        SidebarSources = WaveeBuiltInDataSources.RegisterAll(registrar: null, SidebarBinder,
            queries: Queries, scope: () => CatalogScope, whatsNew: WhatsNew, concerts: Concerts, playback: Playback);
        SidebarBinder.UseHost(new WaveeBuiltInDataSources.ContributionHost(SidebarSources), SidebarSources);
        Sidebar.Binder = SidebarBinder;
        // Findings 4.2: publish the UI-facing scope from the repository's own Session publication instead of a
        // one-shot postUi captured at bootstrap — the fire-and-forget callback this replaced (LiveSessionHost.cs)
        // could silently drop the handover (an epoch bump between enqueue and drain, or a throw before it ran),
        // pinning every reactive query to a dead scope with no retry and no inverse. Subscribing here, AFTER
        // Playback/LibraryStore/SidebarBinder exist, covers install AND SetOfflineCore's logout/reconnect inverse
        // through the exact same idempotent path (both publish CatalogChangeKind.Session).
        // The rebind callback receives (previous, next): a same-scope confirmation never reaches it at all (the
        // publisher answers those with one `confirmed` line and no rebind), and the ones that DO arrive can still tell
        // an account switch from a mere reshaping — LibraryStore.RebindScope and SidebarProjectionBinder.RebindScope
        // each apply that rule to their own state (ScopeRebindRules).
        _scopePublisher = new CatalogScopePublisher(() => Data.Catalog.Scope, () => Data.Catalog.Epoch, CatalogScopeSignal,
            (previous, scope) => { Playback.AttachQueueQueries(Queries, scope); LibraryStore.RebindScope(scope); SidebarBinder.RebindScope(scope); },
            (eventId, message) => Log.Event(WaveeLogLevel.Info, "catalog", eventId, message));
        _catalogColors = data.Catalog.Changes.Subscribe(Wavee.Backend.Observers.From<CatalogChangeSet>(change =>
        {
            if (change.Kind == CatalogChangeKind.Session) _uiPost(_scopePublisher.Publish);
            var activeScope = data.Catalog.Scope;
            foreach (var key in change.Keys)
            {
                if (key.Facet != FacetKind.VisualIdentity || key.Scope with { Provider = activeScope.Provider } != activeScope
                    || data.Catalog.Peek(key).Value is not VisualIdentityValue value) continue;
                var scheme = new CoverColorPlane.Scheme(value.Background ?? 0, value.BackgroundTinted ?? 0,
                    value.Text ?? 0, value.TextSubdued ?? 0, value.Accent ?? 0);
                foreach (var uri in value.ImageUris ?? [value.ImageUri]) CoverColorPlane.Current.SetDark(uri, scheme);
            }
        }));
        // Memory pressure sheds catalog facts outside current query demand.
        Residency.Register(3, "catalog",
            () => Data.Catalog.TrimUnpinned(Data.Queries.CollectActiveResourceKeys, EntityResidencyCap));
    }

    /// <summary>The account the reusable credential names, or "" when there is none, or it cannot be read on this
    /// machine (a different at-rest protector, a moved profile). Never throws: a credential that will not load is a
    /// launch without a provisional scope, not a failed launch.</summary>
    static string StoredAccount(Wavee.Backend.Persistence.ICredentialStore credentials)
    {
        try { return credentials.Load()?.Username ?? ""; }
        catch { return ""; }
    }

    async System.Threading.Tasks.Task InitializeDataAsync()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Log.Info("catalog", "Initializing catalog and library replicas");
        try
        {
            await Data.InitializeAsync().ConfigureAwait(false);
            Log.Info("catalog", "Catalog storage and library replicas ready; loading native sources");
            await NativeCatalog.StartAsync().ConfigureAwait(false);
            Log.Info("catalog", $"Catalog ready ({System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms)");
        }
        catch (System.Exception error)
        {
            Log.Error("catalog", "Catalog initialization failed", error);
            throw;
        }
    }

    internal async System.Threading.Tasks.Task EndCatalogSessionAsync(long expectedEpoch)
    {
        if (!await Data.EndSessionAsync(expectedEpoch).ConfigureAwait(false)) return;
        await NativeCatalog.RefreshAsync().ConfigureAwait(false);
    }



    // ── the PRE-LOGIN local media session ───────────────────────────────────────────────────────────────────────────
    // The audio half of "a playable that needs no session". Built by CreateReal, torn down at go-live (the live stack
    // replaces it) and rebuilt by the go-live ledger's own inverse at logout — ONE factory, no second code path.

    /// <summary>The three pieces of a session-free local media stack.</summary>
    /// <param name="AudioHost">The house mixer/decoder host.</param>
    /// <param name="Projection">Its own now-playing projection (no cluster, no publisher).</param>
    /// <param name="Controller">The player the logged-out stub forwards local playables to.</param>
    /// <param name="Relay">The module facts (live-ness, now-playing corrections) folded onto the projection.</param>
    internal sealed record PreLoginMedia(
        Wavee.Backend.IAudioHost AudioHost,
        Wavee.Backend.NowPlayingProjection Projection,
        Wavee.Backend.PlaybackController Controller,
        Wavee.Backend.Modules.ModuleProjectionRelay Relay);

    System.Func<PreLoginMedia>? _preLoginFactory;
    PreLoginMedia? _preLogin;

    /// <summary>The app's current playback preferences, as a module sees them on <c>playback/resolve</c>: the effective
    /// quality (the user's rung, capped on a metered link), whether the link IS metered, and the crossfade setting.
    /// Source-neutral by construction — a module never learns which Spotify rung the ladder picked.</summary>
    /// <param name="settings">The app settings store.</param>
    internal static Wavee.Sdk.ResolvePreferences ResolvePrefs(IAppSettings settings)
    {
        string quality = NetworkPolicy.EffectiveQualityPreference(settings) switch
        {
            Wavee.Backend.AudioQualityPreference.Normal96 => "normal",
            Wavee.Backend.AudioQualityPreference.High160 => "high",
            Wavee.Backend.AudioQualityPreference.VeryHigh320 => "veryHigh",
            Wavee.Backend.AudioQualityPreference.Lossless => "lossless",
            _ => "normal",
        };
        int crossfadeMs = settings.Get(WaveeSettings.CrossfadeEnabled)
            ? System.Math.Clamp(settings.Get(WaveeSettings.CrossfadeMs), 0, 12_000)
            : 0;
        return new Wavee.Sdk.ResolvePreferences(quality, NetworkPolicy.IsMetered, crossfadeMs);
    }

    static PreLoginMedia BuildPreLoginMedia(Wavee.Backend.MediaSources.MediaProviderRegistry providers,
        Wavee.Backend.CatalogRuntime data, string deviceId, IAppSettings settings)
    {
        var log = new WaveeLogger(WaveeLog.Instance, "playback");
        // No PlayPlay decryptor factory and no body disk cache: neither exists without a session, and neither is
        // reachable from a local file, an internet radio station or a module's own byte stream.
        var audio = new Wavee.SpotifyLive.Audio.FluentMediaAudioHost(
            static () => null, Wavee.Backend.Spotify.HttpPools.Get(Wavee.Backend.Spotify.HttpPool.Cdn),
            Wavee.SpotifyLive.Audio.FluentMediaAudioHost.CreateWasapiBackend, log, bodyDisk: null);
        // Seed persisted EQ/crossfade before this host ever opens a session — the SAME helper AudioPlaybackStack uses
        // for the live host, so a user who sets EQ before signing in (or after logging out, back on local playback)
        // hears it immediately instead of only after the next go-live.
        if (audio is Wavee.Backend.IAudioDspControl dsp) PlaybackDsp.SeedFromSettings(dsp, settings);
        var projection = new Wavee.Backend.NowPlayingProjection(deviceId, data.PlaybackQueue);
        var controller = new Wavee.Backend.PlaybackController(audio, providers, projection,
            Wavee.Backend.EmptyContextResolver.Instance, deviceId, log: log, fast: providers)
        {
            MetaResolver = providers.ResolveWireMetaAsync,
            CanPrepareNext = t => providers.SupportsPreparedNext(t.Uri),
        };
        // The module facts, wired exactly as the live session wires them (LiveConnect): LIVE-ness follows the current
        // playable, a module's metadata push becomes the now-playing override, and an ICY StreamTitle is split onto the
        // same override. One relay type, two composition points, no drift.
        var relay = Wavee.Backend.Modules.ModuleProjectionRelay.Attach(projection, Wavee.Backend.Modules.ModuleHost.Current);
        if (audio is Wavee.Backend.ILiveMetadataSource liveMeta)
            liveMeta.MetadataKnown += relay.OnLiveStreamTitle;
        return new PreLoginMedia(audio, projection, controller, relay);
    }

    /// <summary>Rebuild the logged-out player: the "choose a remote device" stub for Spotify uris, with a freshly built
    /// local media session behind it for everything that needs no account. Registered as the go-live Player seam's own
    /// inverse, so a logout gets a working local stack back instead of a mute one.</summary>
    internal UnsupportedPlaybackPlayer BuildLoggedOutPlayer()
    {
        var stub = new UnsupportedPlaybackPlayer();
        stub.OnPlayIntentRejected = () => Playback.NotifyLocalPlaybackUnsupported();
        if (_preLoginFactory is not { } make) return stub;   // the fake backend genuinely has no audio stack

        DisposePreLoginMedia();
        _preLogin = make();
        stub.LocalPlayback = _preLogin.Controller;
        var providers = MediaProviders;
        stub.CanPlayLocally = uri => providers?.OwnerOf(uri) is not null;
        Playback.LocalPlaybackSupported.Value = true;
        return stub;
    }

    void DisposePreLoginMedia()
    {
        var media = _preLogin;
        _preLogin = null;
        if (media is null) return;
        if (media.AudioHost is Wavee.Backend.ILiveMetadataSource liveMeta)
        { try { liveMeta.MetadataKnown -= media.Relay.OnLiveStreamTitle; } catch { /* already detached */ } }
        try { media.Relay.Dispose(); } catch { /* teardown is best-effort */ }
        try { media.Controller.Dispose(); } catch { /* teardown is best-effort */ }
        try { media.Projection.Dispose(); } catch { /* teardown is best-effort */ }
        try { media.AudioHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* teardown is best-effort */ }
    }

    /// <summary>Publish the first-party sidebar data sources into the platform registry. Call it ONCE from the composition
    /// root immediately after <c>WaveeExtensionRegistry.Build(actionServices)</c> — that build owns the ACTION half (it
    /// needs the shell's service bag), and this adds the DATA-SOURCE half, so the customizer's palette and M3's permission
    /// checks see one complete "wavee" extension. Skipping it does not break the sidebar (its host resolves
    /// <see cref="SidebarSources"/> directly); only the registry's enumeration would be incomplete.</summary>
    public void RegisterSidebarSources(WaveeExtensionRegistry registry)
        => WaveeBuiltInDataSources.Publish(registry, SidebarSources);

    /// <summary>One compact app census line for the FG_MEM_DIAG report (see <see cref="MemCensusHook"/>). Built on demand.</summary>
    public string CensusLine()
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"catalog resources={Data.Catalog.ResidentCount} estMB={Data.Catalog.EstimatedResidentBytes / (1024.0 * 1024):0.0}");
        return sb.ToString();
    }

    /// <summary>The fake wiring that drives the skeleton with in-memory data (no network). Persistence is real (the
    /// settings store), since it's local state, not catalog data.</summary>
    public static Services CreateFake(IAppSettings? settings = null, AppLocale? appLocale = null)
    {
        var session = new FakeSpotifySession();
        // Local audio playback is not supported yet: the player rejects every play intent (surfaced as the standard
        // "choose a remote device" toast, wired below); real playback happens only on a Connect device after live login.
        var player = new UnsupportedPlaybackPlayer();
        var devices = new NoConnectDevices();   // no in-process devices — the roster comes from the live Connect cluster
        // The composition root may create the store early (to seed the theme before the first frame) and pass it in;
        // otherwise create it here. Same registry either way (the wrapper is stateless), so a second instance is harmless.
        settings ??= AppDataSettings.ForUnpackaged(UnpackagedAppDataRoot.CurrentFolderName, UnpackagedAppDataRoot.CurrentFolderName);
        var store = settings;
        var export = SpotifyExport.Load();

        var userPlaylists = new UserPlaylistSource();
        var playlistEdits = new LocalPlaylistMutationSource(userPlaylists);
        ISource[] nativeSources = [new SpotifyExportSource(export), new LocalSource(), userPlaylists,
            new FakeSource(), new FakePodcastSource()];
        string rawSaved = store.Get(WaveeSettings.SavedLibrary);
        var savedSeed = string.IsNullOrEmpty(rawSaved) ? InitialNativeSaved(nativeSources)
            : rawSaved.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        var mutations = new LocalMutationSource(savedSeed,
            snapshot => store.Set(WaveeSettings.SavedLibrary, string.Join("\n", snapshot) + "\n"));
        var registry = new SourceRegistry(nativeSources.Concat(new ISource[] { mutations, session }).ToArray());
        string OwnerProvider(string uri) => registry.OwnerOf(uri)?.Id
            ?? registry.OfCapability(SourceCapabilities.Fallback).First().Id;
        var memoryData = new Wavee.Backend.Persistence.MemoryDataPersistence();
        var demoStore = new Wavee.Backend.InMemoryStore();
        var data = new Wavee.Backend.CatalogRuntime(new CatalogScope("spotify", "demo", (appLocale ?? AppLocale.English).SpotifyLanguage,
            "", "", -1, false), "demo", memoryData, memoryData, new Wavee.Backend.Persistence.MemoryReplicaProjection(demoStore),
            registry.All.Where(source => source is ICatalogSource or IPodcastSource)
                .Select(source => new Wavee.Backend.Catalog.NativeCatalogResourceProvider(registry, source)), OwnerProvider,
            Wavee.Backend.Catalog.ProviderExecutionPolicy.Ready);
        CatalogScope ScopeForSubject(string uri) => data.ScopeForSubject(uri);
        var library = new AggregateCatalog(registry, data.Queries, ScopeForSubject);
        var nativeCatalog = new Wavee.Backend.Catalog.NativeCatalogBootstrap(registry, data.Replicas, ScopeForSubject, mutations);
        var svc = new Services(WaveeLog.Instance, session, library, player, devices, new NoLyricsProvider(), settings, mutations, userPlaylists, playlistEdits, new InMemoryActivityStore(), appLocale ?? AppLocale.English, data, nativeCatalog);
        player.OnPlayIntentRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();   // any play intent → the standard toast
        svc.Log.Info("app", "Services created (sources: spotify-export, local-files, user-playlists, podcasts, fake + session facet; playback remote-only; mutations: saved-state + playlists)");
        return svc;
    }

    static string[] InitialNativeSaved(ISource[] sources)
    {
        var uris = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source is ICatalogSource catalog)
            {
                uris.UnionWith(catalog.GetLikedSongsAsync().GetAwaiter().GetResult().Select(item => item.Uri));
                uris.UnionWith(catalog.GetAlbumsAsync().GetAwaiter().GetResult().Select(item => item.Uri));
                uris.UnionWith(catalog.GetArtistsAsync().GetAwaiter().GetResult().Select(item => item.Uri));
                uris.UnionWith(SidebarTree.Flatten(catalog.GetPlaylistTreeAsync().GetAwaiter().GetResult()).Select(item => item.Uri));
            }
            if (source is IPodcastSource podcast)
                uris.UnionWith(podcast.GetShowsAsync().GetAwaiter().GetResult().Select(item => item.Uri));
        }
        return uris.ToArray();
    }

    /// <summary>The REAL backend wiring: the persistent Store-backed catalog (<see cref="Wavee.Backend.Library.StoreLibrarySource"/>
    /// over a SQLite cold tier) + the durable, multi-set mutation engine, behind the same Wavee.Core seams. Playback stays
    /// the in-process fake (audio is a later milestone); the live session/transport (login → spclient fetchers → the hm://
    /// dealer) are connected by a separate bootstrap. The catalog reads the persisted Store offline; a first run is empty
    /// until that bootstrap syncs. Gated behind <c>--real-backend</c> so the FakeData demo stays the default.</summary>
    public static Services CreateReal(IAppSettings? settings = null, string? accountDbPath = null, AppLocale? appLocale = null)
    {
        AppLocale locale = appLocale ?? AppLocale.English;
        var session = new FakeSpotifySession();     // Session facet — swapped for the real EngineSessionSource on live connect
        // Spotify play intents still toast "choose a remote device" until go-live swaps in the live controller — but a
        // playable that needs no session (a file, a radio station, a module link) is routed to the pre-login local
        // media session composed below. See UnsupportedPlaybackPlayer.LocalPlayback.
        var player = new UnsupportedPlaybackPlayer();
        var devices = new NoConnectDevices();           // empty roster until the live Connect cluster arrives on go-live
        settings ??= AppDataSettings.ForUnpackaged(UnpackagedAppDataRoot.CurrentFolderName, UnpackagedAppDataRoot.CurrentFolderName);

        // The persistent, offline-first backend store (its own SQLite file under the unpackaged root by default).
        accountDbPath ??= UnpackagedAppDataRoot.UnderCurrent("library.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(accountDbPath)!);
        // §G startup marks: the two ctors are the only synchronous disk work before first paint, so they are what
        // `boot.sqlite_open_ms` (open + schema-reset + open-time sweep) and `boot.identity_load_ms` (the DEFERRED ctor's
        // identity tier: saved sets, video map, overrides, pin mirrors) actually measure.
        long openStart = System.Environment.TickCount64;
        var cold = new Wavee.Backend.Persistence.SqliteColdStore(accountDbPath, Wavee.Backend.Persistence.SqliteColdStore.DefaultAccount, locale.SpotifyLanguage);
        long openMs = System.Environment.TickCount64 - openStart;
        if (cold.ResetFromSchema is { } previousSchema)
            WaveeLog.Instance.Info("catalog", $"library.db schema v{previousSchema} → v{Wavee.Backend.Persistence.SqliteColdStore.CurrentSchemaVersion}: " +
                "catalog cache and library replicas cleared; they re-fetch from Spotify on this launch");
        long identityStart = System.Environment.TickCount64;
        var store = new Wavee.Backend.InMemoryStore();
        long identityMs = System.Environment.TickCount64 - identityStart;
        var spotifyProvider = new Wavee.Backend.Catalog.SwitchableCatalogResourceProvider("spotify",
            new Wavee.Backend.Catalog.OfflineCatalogResourceProvider("spotify"));
        var userPlaylists = new UserPlaylistSource { ExposeInCatalog = false };
        var registry = new SourceRegistry(new ISource[]
        {
            new CatalogSourceRegistration("spotify", uri => EntityUri.Parse(uri).Provider == "spotify",
                SourceCapabilities.Home | SourceCapabilities.Search | SourceCapabilities.Podcasts),
            new LocalSource(), userPlaylists,
        });
        string OwnerProvider(string uri) => registry.OwnerOf(uri)?.Id ?? "spotify";
        var catalogProviders = registry.All.Where(source => source is ICatalogSource or IPodcastSource)
            .Select(source => (ICatalogResourceProvider)new Wavee.Backend.Catalog.NativeCatalogResourceProvider(registry, source))
            .Prepend(spotifyProvider).ToArray();
        // ── the PROVISIONAL catalog scope ───────────────────────────────────────────────────────────────────────────
        // The user's library is already on this machine: durable replicas, every playlist header, every cover. The one
        // thing that used to keep it off screen for the first ~2.5 s of a launch is that nothing could say WHO it
        // belonged to until the AP welcome landed — so the replica scope named no account, every replica read answered
        // Missing, and every cached catalog row sat under a scope the pre-login reads never addressed.
        //
        // Both facts are on disk. The reusable credential names the account; the session-scope memory names the exact
        // CatalogScope that account's rows were written under. Recalling them makes the replica scope and every catalog
        // ResourceKey identical to the ones the live session will install, so the sidebar joins the real library on the
        // first frame — and the network session then CONFIRMS that scope (SessionInstallRules) instead of replacing it.
        //
        // It is served OFFLINE (online: false): the scope names a context, it does not certify a connection, and no
        // provider request can leave the machine until the protocol session installs. A launch with no stored
        // credential — a first run, or a logged-out one — keeps the anonymous pre-login scope exactly as before.
        (Wavee.Backend.Persistence.LocalCredentialStore credentials, string localDeviceId) =
            Wavee.SpotifyLive.SpotifyLiveLogin.OpenCredentialStore();
        var sessionScopes = new Wavee.Backend.Persistence.LocalSessionScopeStore(credentials.Store, credentials.Protector);
        var anonymousScope = new CatalogScope("spotify", "", locale.SpotifyLanguage, "", "", -1, false,
            ContextKnown: false, StorageAccount: cold.Account);
        string storedAccount = StoredAccount(credentials);
        var remembered = sessionScopes.Recall();
        bool provisional = storedAccount.Length > 0 && remembered is { } recalled
            && recalled.ContextKnown
            && string.Equals(recalled.Provider, "spotify", System.StringComparison.Ordinal)
            && string.Equals(recalled.ProviderAccount, storedAccount, System.StringComparison.Ordinal)
            && string.Equals(recalled.StorageAccount, cold.Account, System.StringComparison.Ordinal);
        var startScope = provisional ? remembered! : anonymousScope;
        WaveeLog.Instance.Event(WaveeLogLevel.Info, "catalog", "catalog.scope.provisional",
            provisional
                ? "serving the cached library under the remembered session scope"
                : "no remembered session scope; the library waits for the session",
            fields:
            [
                WaveeLogField.Of("account", startScope.ProviderAccount),
                WaveeLogField.Of("source", provisional ? "stored-credentials" : "none"),
                WaveeLogField.Of("locale", startScope.Locale),
                WaveeLogField.Of("market", startScope.Market),
                WaveeLogField.Of("sinceStartMs", WaveeLog.SinceStartMs),
            ]);
        var data = new Wavee.Backend.CatalogRuntime(startScope, provisional ? storedAccount : "", cold, cold,
            new Wavee.Backend.Persistence.MemoryReplicaProjection(store), catalogProviders, OwnerProvider,
            Wavee.Backend.Catalog.ProviderExecutionPolicy.ProtocolSession, online: false);
        CatalogScope ScopeForSubject(string uri) => data.ScopeForSubject(uri);

        // The collection self-write echo registry (§7.1): the write strategy records accepted-write cuids; the sync loop
        // drops our own echoes. One instance shared between the write path and the read loop (wired below on go-live).
        var echoRing = new Wavee.Backend.Collections.CollectionEchoRing();
        var spclientBaseUrl = new Wavee.Backend.SpclientBaseUrlHolder();
        // I2 — the ONE rootlist write lane, shared by the outbox's follow/unfollow strategy and the direct rootlist ops
        // on PlaylistMutationSource (move / delete / visibility / create). Required on both; never optional.
        // The durable, multi-set mutation engine (set saves + playlist OpRebase edits) behind the IMutationSource seam.
        // I4 — the ONE post-drain revalidation queue, shared by the replay strategy and the sync loop (wired on go-live).
        var resyncQueue = new Wavee.Backend.Playlists.PlaylistResyncQueue();
        var mutEngine = new Wavee.Backend.MutationEngine(data.Replicas,
            new Wavee.Backend.IMutationStrategy[]
            {
                new Wavee.Backend.SetReplayStrategy(echoRing),
                new Wavee.Backend.OpRebaseStrategy(data.Replicas, () => spclientBaseUrl.Value),
                new Wavee.Backend.CreatePlaylistStrategy(data.Replicas, () => spclientBaseUrl.Value),
                new Wavee.Backend.RootlistFollowStrategy(data.Replicas, () => spclientBaseUrl.Value),
            });
        // The mutation transport is SWITCHABLE (stub → live dealer on go-live, back to stub on logout) so writes made while
        // logged out queue durably and replay on next login (§2.1); the drain binds to this stable facade once.
        var mutTransport = new Wavee.Backend.SwitchableTransport(new Wavee.Backend.StubTransport());
        var sessionHost = new Wavee.Backend.SessionContextHost(
            new Wavee.Backend.SessionContext("", "US", "premium", locale.SpotifyLanguage, Wavee.Backend.Tier.Premium, false));
        var mutations = new Wavee.Backend.EngineMutationSource(store, mutEngine, data.Commands);

        var playlistMutations = new Wavee.Backend.Playlists.PlaylistMutationSource(
            mutEngine, mutTransport, new Wavee.Backend.Spotify.HttpClientExchange(), () => sessionHost.Current,
            () => spclientBaseUrl.Value, userPlaylists, store, data.Replicas, data.Commands);
        // The "recommended songs" extender rides the SAME switchable transport (stub → live dealer on go-live, back on logout).
        var extender = new Wavee.Backend.Playlists.PlaylistExtenderClient(mutTransport);

        var library = new AggregateCatalog(registry, data.Queries, ScopeForSubject);
        var nativeCatalog = new Wavee.Backend.Catalog.NativeCatalogBootstrap(registry, data.Replicas, ScopeForSubject);

        // None of this needs a session, a token or a cluster — which is exactly why it is composed here and not in the
        // live bootstrap. `MediaProviderRegistry` used to be built inside LiveConnect, so a build with no Spotify
        // resolver had no routing table at all and "Play file…" was hidden until sign-in; the registry is a property of
        // the APP, not of a session, and go-live only prepends Spotify to it.
        var moduleSecrets = new Wavee.Backend.Modules.ProtectedModuleSecretStore(
            Wavee.Backend.Persistence.FileLocalStore.ForApp(UnpackagedAppDataRoot.CurrentFolderName), credentials.Protector);
        var modules = new Wavee.Backend.Modules.ModuleHost(
            Wavee.Backend.Modules.ModuleCatalog.Discover(),
            new WaveeLogger(WaveeLog.Instance, "modules"),
            prefs: () => ResolvePrefs(settings),
            services: new Wavee.Backend.Modules.ModuleHostServices(moduleSecrets),
            spawn: null,
            hostVersion: AppVersion.Info.SemVer,
            locale: locale.SpotifyLanguage);
        Wavee.Backend.Modules.ModuleHost.Attach(modules);
        Wavee.Backend.Modules.ChildProcessChannel.Job ??= FluentGpu.WindowsApi.Shell.ChildProcessJob.CreateKillOnClose();

        // REGISTRATION ORDER IS THE ROUTING TABLE (first Owns wins). The two engine-free local sources first, then one
        // provider per installed module; go-live builds a superset with SpotifyMediaProvider at the head.
        var preLoginProviders = new System.Collections.Generic.List<Wavee.Backend.MediaSources.IPlayableMediaProvider>(2 + modules.Providers.Count)
        {
            new Wavee.Backend.MediaSources.LocalFileMediaProvider(
                probeDurationMs: Wavee.SpotifyLive.Audio.LocalAudioDurationProbe.Probe),
            new Wavee.Backend.MediaSources.GenericMediaProvider(
                probeDurationMs: Wavee.SpotifyLive.Audio.LocalAudioDurationProbe.Probe),
        };
        preLoginProviders.AddRange(modules.Providers);
        var mediaProviders = new Wavee.Backend.MediaSources.MediaProviderRegistry(preLoginProviders.ToArray());

        // …and the LOCAL media session behind that table: one audio host, its own now-playing projection and a
        // controller. No Connect publisher, no outbound control, no context resolver — nothing about it is a session,
        // which is the whole point. Spotify play intents still reject (nothing owns a spotify: uri here), so the
        // "choose a remote device" toast is unchanged for exactly the playables that genuinely need an account.
        System.Func<PreLoginMedia> preLoginFactory = () =>
            BuildPreLoginMedia(mediaProviders, data, localDeviceId, settings);
        PreLoginMedia preLogin = preLoginFactory();
        player.LocalPlayback = preLogin.Controller;
        player.CanPlayLocally = uri => mediaProviders.OwnerOf(uri) is not null;

        // Switchable facades over the fake playback/devices: a live Connect session swaps in at runtime (svc.GoLive)
        // without rebuilding the UI — the PlaybackBridge binds to these stable facades.
        var swPlayer = new Wavee.Backend.SwitchablePlayer(player);
        var swDevices = new Wavee.Backend.SwitchableDevices(devices);
        var swSession = new Wavee.Backend.SwitchableSession(session);
        var swLyrics = new Wavee.Backend.SwitchableLyrics(new NoLyricsProvider());   // swapped to the real AggregatingLyricsProvider on live login
        var svc = new Services(WaveeLog.Instance, swSession, library, swPlayer, swDevices, swLyrics, settings, mutations, userPlaylists, playlistMutations, new Wavee.Backend.Persistence.SqliteActivityStore(accountDbPath), locale, data, nativeCatalog);
        svc.SpotifyCatalogProvider = spotifyProvider;
        player.OnPlayIntentRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();   // pre-go-live: play intents show the "choose a remote device" toast
        svc.RealStore = store;
        svc.SessionScopes = sessionScopes;
        // The user's local video-override curation. Wired HERE (not on go-live) precisely so attaching a custom mp4 works
        // WITHOUT Spotify: the bridge's has-video answer consults it directly, and the resolver installed below has no
        // source tier at all — an override is the only video such a build can serve. LiveSessionHost later replaces that
        // resolver with the full two-tier composite, keeping tier 1 identical.
        var videoOverrides = new VideoOverrideService(data.Commits, cold,
            cold.ReadVideoOverridesAsync().AsTask().GetAwaiter().GetResult(),
            new WaveeLogger(WaveeLog.Instance, VideoOverrideService.LogCategory));
        videoOverrides.OnChanged = (uri, kind) => svc.Playback.NotifyVideoOverrideChanged(uri, kind);
        videoOverrides.OnBrokenLink = uri => svc.Playback.NotifyVideoOverrideMissing(uri);
        svc.VideoOverrides = videoOverrides;
        svc.Playback.AttachVideoOverrides(videoOverrides);
        svc.Playback.ResolveVideoSource = CompositeVideoResolver.OverridesOnly(videoOverrides).ResolveAsync;
        // The pre-login module host + routing table + local media session (built above, before the switchable player
        // captured the stub's state). LocalPlaybackSupported is "an audio host exists", and one does — from now on,
        // not from go-live.
        svc.Modules = modules;
        svc.MediaProviders = mediaProviders;
        svc._preLoginFactory = preLoginFactory;
        svc._preLogin = preLogin;
        svc.Playback.LocalPlaybackSupported.Value = true;
        svc.MutTransport = mutTransport;
        svc.RealCold = cold;
        svc.RealMutations = mutEngine;
        // The pending-edit chip / notification line read the durable outbox through the bridge; the bridge is built
        // in the Services ctor, before the store and the outbox exist, so the engine is attached here instead.
        svc.LibraryBridge.AttachMutations(mutEngine);
        // …and the STORE, for the same reason: every rootlist legality question (the sidebar drop cue, the rail folder
        // tile, the "Move to folder…" picker) is decided against `IStore.Rootlist()`, the marker stream the write
        // itself indexes into.
        svc.LibraryBridge.AttachRootlist(store);
        // The header's "still syncing with Spotify" chip (I4) reads this same shared queue through the bridge.
        svc.LibraryBridge.AttachResync(resyncQueue);
        svc.RealResyncQueue = resyncQueue;
        svc.RealSessionHost = sessionHost;
        svc.EchoRing = echoRing;
        svc.RealMutationSource = mutations;
        // The Spotify ylpin↔sidebar-pin bridge (§1.5): read hydration rides the SAME store the collection fetcher
        // writes "pins" into, write-back rides the SAME mutation engine every other set save does. "Converged" is the
        // cold store's own ylpin sync-token — the collection fetcher already tokens every wire set it walks, so this
        // needs no bespoke flag.
        svc.PinSync = new SidebarPinSync(store, svc.Sidebar.Pins, mutations, settings,
            () => sessionHost.Current.Account,
            () => data.Replicas.ReadConfirmedCollection("pins").WireRevision is not null,
            uri => mutEngine.HasPending("pins", uri));
        svc.RealPlaylistMutations = playlistMutations;
        svc.RealExtender = extender;
        svc.RealSpclientBaseUrl = spclientBaseUrl;
        // Retention uses detached query demand and the same serialized SQLite writer.
        svc.CacheGc = new Wavee.Backend.Persistence.CatalogCacheMaintenance(cold, data.Catalog, data.Commits,
            data.Queries.GetActiveResourceKeys, System.TimeProvider.System,
            System.Math.Max(MinMetadataCacheBudgetBytes, settings.Get(WaveeSettings.MetadataCacheBudgetBytes)),
            error => svc.Log.Error("catalog", "Cache maintenance failed: " + error.Message));
        svc.Log.Info("app", $"Services created (normalized catalog and durable replicas; database open {openMs} ms, replica display {identityMs} ms)");
        return svc;
    }

    /// <summary>Swap the playback player + Connect device roster + session/connectivity/lyrics to a live backend at
    /// runtime. The PlaybackBridge bound to the switchable facades re-points without a rebuild (no-op if this Services
    /// wasn't built with switchables).
    ///
    /// Every swap goes through <paramref name="wiring"/> — the SAME ledger the rest of the go-live block uses — so these
    /// five seams are torn down by <see cref="GoOffline"/> exactly like the others, in reverse order, instead of being a
    /// second hand-written list. All five providers are REQUIRED: a live session that cannot supply one of them is a
    /// bootstrap bug, not a degraded mode (wiring-discipline).</summary>
    public void GoLive(IPlaybackPlayer player, IConnectDevices devices, ISpotifySession session, IConnectivity connectivity, ILyricsProvider lyrics,
                       Wavee.Backend.Wiring.LiveWiring wiring)
    {
        // The live stack owns the audio device from here on, so the PRE-LOGIN local media session is torn down as the
        // live player goes in — never left running beside it (two hosts, one endpoint, is exactly the bug that shape
        // invites). The seam's inverse rebuilds it, so a logout lands back on a working local stack.
        DisposePreLoginMedia();
        // The offline players are FACTORIES, not values: the logged-out stub carries a live callback into this Services,
        // so it is rebuilt fresh at teardown rather than captured now and held across the whole session.
        wiring.Swap<IPlaybackPlayer>(Wavee.Backend.Wiring.LiveSeams.Player,
            p => (Player as Wavee.Backend.SwitchablePlayer)?.SetInner(p), player,
            BuildLoggedOutPlayer);
        wiring.Swap<IConnectDevices>(Wavee.Backend.Wiring.LiveSeams.Devices,
            d => (Devices as Wavee.Backend.SwitchableDevices)?.SetInner(d), devices,
            static () => new NoConnectDevices());          // clears the device roster on logout
        wiring.Swap<ISpotifySession>(Wavee.Backend.Wiring.LiveSeams.Session,
            x => (Session as Wavee.Backend.SwitchableSession)?.SetInner(x), session,
            static () => new FakeSpotifySession());
        wiring.Swap<IConnectivity>(Wavee.Backend.Wiring.LiveSeams.Connectivity,
            c => (Connectivity as Wavee.Backend.SwitchableConnectivity)?.SetInner(c), connectivity,
            static () => new Wavee.Backend.Connectivity());
        wiring.Swap<ILyricsProvider>(Wavee.Backend.Wiring.LiveSeams.Lyrics,
            l => (Lyrics as Wavee.Backend.SwitchableLyrics)?.SetInner(l), lyrics,
            static () => new NoLyricsProvider());          // no lyrics until the next live login
        Log.Info("app", "playback backend swapped to LIVE (Connect device + now-playing + remote control + account active)"
            + " + real lyrics feed (aggregator + reranker)");
    }

    /// <summary>Register the live-session teardown handles. MUST be called BEFORE <see cref="GoLive"/> (which flips the
    /// shell on and makes logout reachable), so a logout fired in that window still clears credentials + disposes the host
    /// instead of leaking the live transport/dealer. Installed through the wiring, so <see cref="DetachLive"/> is its
    /// registered inverse.</summary>
    internal void AttachLive(Wavee.SpotifyLive.LiveSessionHost host, Wavee.Backend.Persistence.ICredentialStore credStore)
    {
        LiveHost = host;
        CredStore = credStore;
    }

    /// <summary>The inverse of <see cref="AttachLive"/>: drop the session handles. Disposal of the host itself is the
    /// logout path's job (<see cref="LogoutAsync"/> awaits it off the UI thread) — this only clears the references.
    ///
    /// <para>GUARDED by reference equality, because two logins race (WaveeApp runs the device-code flow and the browser
    /// flow simultaneously on one shared ct). A loser tearing itself down — whether through the normal ledger replay or
    /// through the go-live rollback — must not null out the WINNER's host and credential store, which is exactly what an
    /// unconditional clear did: the app would then be live with no reachable <see cref="LiveHost"/>, so logout could
    /// neither dispose the session nor wipe its credential.</para></summary>
    internal void DetachLive(Wavee.SpotifyLive.LiveSessionHost host)
    {
        if (!ReferenceEquals(LiveHost, host)) return;   // a sibling won and published its own — leave it alone
        LiveHost = null;
        CredStore = null;
    }

    /// <summary>The inverse of <see cref="AttachWiring"/>, on the same reference-equality terms: forget the ledger only
    /// if it is still the one this attempt handed over. Used by the go-live ROLLBACK — a bootstrap that threw part-way
    /// replays its own ledger and then drops it, so the next attempt's <see cref="AttachWiring"/> cannot orphan it.</summary>
    internal void DetachWiring(Wavee.Backend.Wiring.LiveWiring wiring)
    {
        if (ReferenceEquals(Wiring, wiring)) Wiring = null;
    }

    /// <summary>Hand this Services the go-live install ledger, BEFORE the first live install. Called once per bootstrap by
    /// <c>LiveSessionHost.StartAsync</c> — earlier than <see cref="AttachLive"/>, so even a bootstrap that fails before the
    /// host exists is undone by <see cref="GoOffline"/>.</summary>
    internal void AttachWiring(Wavee.Backend.Wiring.LiveWiring wiring) => Wiring = wiring;

    /// <summary>Hand this Services the app's UI-thread marshaller, so the catalog-scope publisher wired in the
    /// constructor (which runs long before any login attempt) can hop onto the UI thread when a session installs or
    /// ends. Called once per <c>LiveSessionHost.StartAsync</c>; the same marshaller every time, so re-attaching on a
    /// reconnect is harmless.</summary>
    internal void AttachUiPost(System.Action<System.Action> post)
    {
        _uiPost = post ?? throw new System.ArgumentNullException(nameof(post));
        // A Session publication that arrived before the marshaller existed was dropped, not run on the wrong
        // thread; Publish is idempotent against the CURRENT scope, so one catch-up post reconciles it.
        _uiPost(_scopePublisher.Publish);
    }

    /// <summary>The inverse of go-live, in ONE line: replay the install ledger backwards (design §2.6).
    ///
    /// This used to be a hand-maintained list of ~35 resets that had to be kept in step with an equally long list of
    /// installs in <c>LiveSessionHost</c> — and had drifted (metadata-entry-points-inventory.md §8.2 #18: AlbumEnrichment,
    /// the video hooks, the cover-colour filler and the whole StoreLibrarySource hook set were installed and never reset).
    /// Now every install registers its own inverse at the install site, <see cref="Wavee.Backend.Wiring.LiveWiring"/>
    /// replays them in reverse order (each guarded, so one failure cannot strand the app half-live), and
    /// <c>AssertCovers(LiveSeams)</c> fails go-live if a required seam skipped the ledger. There is deliberately NOTHING
    /// left to do by hand here: anything that needs undoing on logout belongs at its install site.
    ///
    /// Idempotent, and safe when no session was ever established (the ledger is then empty).</summary>
    public void GoOffline()
    {
        // BOTH handles, because they can differ for exactly one reason: a racing login sibling (device-code vs browser)
        // hands over its ledger before the supersede check and can therefore overwrite <see cref="Wiring"/> with a
        // bootstrap that never reached AttachLive. The winner is then only reachable through LiveHost. Uninstall is
        // idempotent, so undoing both is free and neither can be stranded.
        var host = LiveHost?.Wiring;
        var wiring = Wiring;
        Wiring = null;
        if (host is null && wiring is null)
        {
            Log.Info("app", "go-offline: no live wiring to undo (never went live, or already torn down)");
            return;
        }
        host?.Uninstall();
        if (!ReferenceEquals(host, wiring)) wiring?.Uninstall();
        // The live stack's own inverse sets LocalPlaybackSupported to false ("the LIVE audio stack is gone"), which was
        // the whole truth while local playback only existed inside a session. It no longer is: the ledger replay just
        // rebuilt the pre-login local media session, so re-assert the honest answer AFTER the replay rather than
        // letting seam ordering decide it.
        if (_preLogin is not null) Playback.LocalPlaybackSupported.Value = true;
        Log.Info("app", "session torn down → offline (every live seam back to its offline value)");
    }

    /// <summary>Release the process-scoped things this composition root owns: the playback-module host (which stops
    /// every module child process) and the pre-login local media session. Idempotent.</summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DisposePreLoginMedia();
        var modules = Modules;
        Modules = null;
        modules?.Dispose();
        try { DataReady.GetAwaiter().GetResult(); }
        catch (System.Exception error) { Log.Info("catalog", "Data initialization failed: " + error.Message); }
        SidebarBinder.Stop();
        _catalogColors.Dispose();
        LibraryStore.Dispose();
        NativeCatalog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (CacheGc is { } cache) cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
        RealMutations?.Dispose();
        Data.DisposeAsync().AsTask().GetAwaiter().GetResult();
        RealCold?.Dispose();
    }
    int _disposed;

    /// <summary>Sign out without a restart: flip the session logged-out (gate → takeover), wipe the persisted reusable
    /// credential (else the next launch silently re-logs-in), tear the live host down OFF the UI thread, then reset to the
    /// fake backend.</summary>
    public async System.Threading.Tasks.Task LogoutAsync()
    {
        // Wipe the persisted credential FIRST — BEFORE flipping the session — so the gate swap to the takeover (which
        // auto-restarts the login) can't read the old credential and silently sign back in. Clear BOTH the captured store
        // and a fresh open (robust even if no live session captured one / it was a silent resume).
        CredStore?.Clear();
        Wavee.SpotifyLive.SpotifyLiveLogin.ClearStoredCredential();
        // Through ReportLogin, not Login.Value: that is the single writer that also refolds ShellAuthState, which the
        // shell/sign-in gate now reads. The credential wipe above happens FIRST by design, so the refold here already
        // sees "no credential on disk" and lands on SignInRequired rather than Offline.
        Playback.ReportLogin(new Wavee.Core.LoginSnapshot(Wavee.Core.LoginPhase.LoggedOut));
        await Session.LogoutAsync().ConfigureAwait(false);   // LiveSpotifySession → LoggedOut → gate swaps shell → takeover
        if (LiveHost is { } h)
        {
            LiveHost = null;
            await System.Threading.Tasks.Task.Run(async () => await h.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        GoOffline();
    }
}
