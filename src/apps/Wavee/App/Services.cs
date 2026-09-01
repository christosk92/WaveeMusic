using System.Linq;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>
/// The hand-wired composition root — plain <c>new</c>, NO reflection container (AOT-visible, zero startup tax). Holds the
/// Core service instances + the <see cref="PlaybackBridge"/>. Swap <see cref="CreateFake"/> for a real wiring later;
/// nothing else changes because the UI only ever sees the interfaces + the bridge.
/// </summary>
public sealed class Services
{
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

    /// <summary>The persistent backend store (REAL backend only; null for the fake). Exposed so the live-session bootstrap
    /// can hydrate playlist headers into the SAME store the catalog reads (InMemoryStore is lock-guarded → safe).</summary>
    public Wavee.Backend.IStore? RealStore { get; private set; }

    /// <summary>The store-backed catalog source (REAL backend only). It carries NO hooks and NO settable seams at all any
    /// more: hydration goes through <see cref="Hydrator"/>, the online reads (search / suggest / home) through
    /// <see cref="OnlineCatalog"/>, and playlist owner / added-by identities straight off the store (P4-C).
    /// Kept as a handle purely for diagnostics.</summary>
    public Wavee.Backend.Library.StoreLibrarySource? RealLibrarySource { get; private set; }

    /// <summary>THE metadata façade (design §1.3 / §3): ONE entry point for every catalog fetch/enrich in the app.
    /// Never null — the fake backend answers <see cref="CompleteEntityHydrator"/> (everything it owns is already
    /// complete), the real backend the Spotify source's switchable (offline → live → offline). P4 replaces this with
    /// the router over the SourceRegistry; until then it IS the Spotify source's hydrator, which is what every uri the
    /// real backend serves needs.</summary>
    public IEntityHydrator Hydrator { get; private set; } = CompleteEntityHydrator.Instance;

    /// <summary>The go-live seam behind <see cref="Hydrator"/> on the REAL backend (null on the fake). <c>LiveSessionHost</c>
    /// swaps the <c>SpotifyProviderHydrator</c> in through it and <see cref="GoOffline"/> swaps it back — one stable
    /// reference every consumer holds for the whole process lifetime.</summary>
    internal SwitchableEntityHydrator? SpotifyHydration { get; private set; }

    /// <summary>THE online-read seam (design §2.7): full-catalog search, as-you-type suggestions and the editorial Home
    /// feed — the reads <see cref="Wavee.Backend.Library.StoreLibrarySource"/> cannot answer from the Store. Never null:
    /// the inner is <see cref="OfflineOnlineCatalog"/> until <c>LiveSessionHost</c> installs the Spotify catalog and
    /// again after <see cref="GoOffline"/>, so the source calls it unconditionally and no consumer asks "am I online?".
    /// The REAL backend re-points this at the very instance its catalog source was constructed with — one stable
    /// reference for the whole process lifetime; the fake keeps the offline default (it has no online catalog).</summary>
    public SwitchableOnlineCatalog OnlineCatalog { get; private set; } = new(OfflineOnlineCatalog.Instance);

    /// <summary>The user's LOCAL VIDEO OVERRIDE curation (REAL backend only — it is store-backed; null for the fake, where
    /// every override path is unreachable). The one instance shared by the resolver's tier 1, the playback bridge's
    /// has-video answer, and (in P3) the menu/settings surfaces: <c>Attach</c> / <c>Remove</c> / <c>All</c> / <c>Decide</c>.</summary>
    public VideoOverrideService? VideoOverrides { get; private set; }

    /// <summary>The switchable mutation transport (REAL backend only): stub until go-live, then the live dealer transport,
    /// back to stub on logout — so writes made while logged out queue in the durable outbox and replay on next login (§2.1).</summary>
    public Wavee.Backend.SwitchableTransport? MutTransport { get; private set; }
    /// <summary>The SQLite cold tier (REAL backend only) — exposed so the go-live block can wire the collection revision
    /// get/set + rootlist revision behind the sync loop.</summary>
    public Wavee.Backend.Persistence.SqliteColdStore? RealCold { get; private set; }
    /// <summary>The metadata-cache garbage collector (REAL backend only; null for the fake). Armed from the app-mount
    /// effect with the UI-thread <c>post</c> marshaller — it must snapshot <see cref="BuildPinSet"/> on the UI thread.
    /// Also owns the one-time post-migration VACUUM and the user-settable cache budget.</summary>
    public Wavee.Backend.Persistence.EntityCacheGc? CacheGc { get; private set; }
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
    public Wavee.Backend.Sync.LibrarySync? RealSync { get; internal set; }
    /// <summary>Reactive live capability for server-driven automatic-playlist tuning.</summary>
    public Signal<IPlaylistTuningSource?> PlaylistTuning { get; } = new(null);
    /// <summary>The selected home facet chip id (Spotify <c>home.homeChips[].id</c>, e.g. "music-chip" or
    /// "podcasts-following-chip"); null/empty = the unfiltered feed. Written by the home chip row, read by the live
    /// home fetch when it builds the <c>facet</c> request variable. Opaque server token — never synthesised.</summary>
    public Signal<string?> HomeFacet { get; } = new(null);
    /// <summary>Monotonic Home-feed revision, published by the live Home cache. Bumped when a Home read resolves a NEW
    /// daylist identity (so every OTHER mounted page learns its card was superseded) and when the store rewrites the
    /// header of an identity the composed feed already depends on — which is exactly what opening the daylist detail
    /// page does while Home sits KeepAlive-PARKED behind it. Home subscribes in an EFFECT (a bump re-reads and restarts
    /// the poll; effects keep running while parked) and COMPARES it on reactivation, so a page that comes back cannot
    /// render a feed the cache has already superseded. Never read in a page's Render — an epoch is a refresh trigger,
    /// not a rendered value.</summary>
    public Signal<int> HomeFeedEpoch { get; } = new(0);
    /// <summary>The reactivation compare: head-probe the hydrated Home identities and publish <see cref="HomeFeedEpoch"/>
    /// only if one has genuinely rolled over. Installed on go-live, null offline/fake (a page then simply skips the
    /// compare). Called from Home's <c>UseActivation</c>, never from a render and never on a cadence.</summary>
    public Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>? HomeFeedRevalidate { get; internal set; }
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
    // ArtistStats / ArtistPopularTracks were TWO seams for what is one question — "how hydrated is this artist?" —
    // each with its own provider, cache and freshness rule. They are the artist ladder's Rich and Full rungs now:
    // ArtistPage asks GetArtistAsync(uri, Rich), the chart asks Full, and Home's expander asks Rich (design §1.5).
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
    // Video association, row tint/tempo, play counts and ©/℗ are NOT seams any more: they are trait projectors behind
    // Hydrator.EnsureTraitsAsync (design §2.4). A surface that wants them asks the façade for its TraitSurface and then
    // READS THE STORE — which is why four service properties, their offline stand-ins and their GoOffline resets are gone.
    // Playlist owner / added-by identities are NOT a seam any more either (P4-C): an Owner is a store entity written by
    // UserHydration, so every surface that renders a byline reads IStore.GetOwner and repaints off IStore.Changes. The
    // switchable service, its Null stand-in, its private cache and its Changed event are deleted.
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
             UserPlaylistSource userPlaylists, IPlaylistMutationSource playlistEdits, IActivityStore activityStore, AppLocale appLocale)
    {
        Log = log;
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
        LibraryStore = new LibraryStore(library, mutations, userPlaylists, library as ICollectionEvents);

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
        SidebarBinder = new SidebarProjectionBinder(Sidebar, LibraryStore, PlayLog, Playback, library);
        SidebarSources = WaveeBuiltInDataSources.RegisterAll(registrar: null, SidebarBinder, library,
            WhatsNew, Concerts, Playback);
        SidebarBinder.UseHost(new WaveeBuiltInDataSources.ContributionHost(SidebarSources), SidebarSources);
        Sidebar.Binder = SidebarBinder;
        // Wire the detail caches as a sheddable arena (priority 2 = shed under MODERATE+ pressure, so at-rest A→B→A stays
        // instant; the LRU insert-cap already bounds steady state). The entity-store "unpinned drop" (priority 3/4) is the
        // documented follow-up — it needs a reachability pin-set to evict live entities safely.
        Residency.Register(2, "detail-cache", () => LibraryStore.ShedDetails(keep: 16));
        // Priority 3 = "unpinned entities" (shed only under CRITICAL OS pressure, per the governor tiers). The reachability
        // pin-set is built ON DEMAND inside the callback — no steady-state bookkeeping. Null RealStore (fake backend) ⇒ a
        // no-op 0, and the ?. short-circuit means BuildPinSet never even runs there. The closure captures `this`; RealStore
        // is populated by CreateReal after construction, long before the 30 s poll first fires.
        Residency.Register(3, "entity-store",
            () => (RealStore as Wavee.Backend.Persistence.CachedStore)?.ShedEntities(BuildPinSet(), EntityResidencyCap) ?? 0L);
    }

    /// <summary>Build the entity-eviction pin-set on demand (inside the shed callback): the now-playing + queue uris, the
    /// uris a still-cached detail model will re-render, and the saved-set heads. UI-thread (the governor Trim is posted to
    /// the UI thread); Peek reads never subscribe.
    ///
    /// UI-THREAD-AFFINE (critique #10): <see cref="LibraryStore.CollectPinnedUris"/> walks the detail caches, which only
    /// the UI thread mutates. Both callers marshal: the MemoryGovernor through its <c>post(() =&gt; Residency.Trim(...))</c>
    /// timer, and the cache GC through the very same <c>post</c> handed to <see cref="Wavee.Backend.Persistence.EntityCacheGc.Start"/>.
    /// Never call it from a background thread.</summary>
    internal System.Collections.Generic.ISet<string> BuildPinSet()
    {
        var pins = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        if (Playback.CurrentTrack.Peek()?.Uri is { Length: > 0 } cur) pins.Add(cur);
        if (Playback.CurrentContext.Peek() is { Length: > 0 } ctx) pins.Add(ctx);
        var queue = Playback.Queue.Peek();
        for (int i = 0; i < queue.Count; i++) if (queue[i].Track.Uri is { Length: > 0 } qu) pins.Add(qu);
        LibraryStore.CollectPinnedUris(pins);
        (RealStore as Wavee.Backend.Persistence.CachedStore)?.CollectSavedHeads(pins, SavedHeadPinCount);
        return pins;
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
        IEntityHydrator hydrator, Wavee.Backend.IStore store, string deviceId)
    {
        var log = new WaveeLogger(WaveeLog.Instance, "playback");
        // No PlayPlay decryptor factory and no body disk cache: neither exists without a session, and neither is
        // reachable from a local file, an internet radio station or a module's own byte stream.
        var audio = new Wavee.SpotifyLive.Audio.FluentMediaAudioHost(
            static () => null, Wavee.Backend.Spotify.HttpPools.Get(Wavee.Backend.Spotify.HttpPool.Cdn), log, bodyDisk: null);
        var projection = new Wavee.Backend.NowPlayingProjection(deviceId, hydrator, store);
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
        if (RealStore is Wavee.Backend.Persistence.CachedStore cs)
        {
            var c = cs.EntityCounts;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"store t={c.Tracks} al={c.Albums} ar={c.Artists} pl={c.Playlists} sh={c.Shows} ep={c.Episodes} v={c.Versions} estMB={cs.EstimatedEntityBytes / (1024.0 * 1024):0.0}");
            if (cs.HasEvictedEntities) sb.Append(" evicted");
        }
        else sb.Append("store (fake)");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" | detail={LibraryStore.DetailCount}");
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
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        var store = settings;
        var export = SpotifyExport.Load();

        // The Mutations facet (docs/plans/wavee/architecture.md §4.2): the user's saved/liked/followed set, persisted via the settings
        // store (the in-process outbox). Seeded on first run from the first ~300 liked uris so the Liked page reads as
        // saved; later runs load the persisted set (incl. session likes). Registered as a capability-only source.
        string rawSaved = store.Get(WaveeSettings.SavedLibrary);
        IEnumerable<string> savedSeed = string.IsNullOrEmpty(rawSaved)
            ? FakeData.LikedSongs(System.Math.Min(System.Math.Max(0, export.LikedCount), 300)).Select(t => t.Uri)
            : rawSaved.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        var mutations = new LocalMutationSource(savedSeed, snap => store.Set(WaveeSettings.SavedLibrary, string.Join("\n", snap)));

        // User-created playlists (the playlist-edit Mutations): a catalog source owning wavee:playlist:*.
        var userPlaylists = new UserPlaylistSource();
        var playlistEdits = new LocalPlaylistMutationSource(userPlaylists);

        // The unified source registry (docs/plans/wavee/architecture.md §4.3): every connected catalog source + the facets it declares.
        // Playback/Lyrics/Remote are NOT in-process sources anymore (local playback is unsupported; the roster is the live
        // Connect cluster's) — the session registers its Session facet; the catalog façade federates the catalog sources.
        var registry = new SourceRegistry(new ISource[]
        {
            new SpotifyExportSource(export),   // Catalog | Home | Search (owns spotify:*)
            new LocalSource(),                 // Catalog | Search | LocalDecode (owns local: / wavee:local:* — the peer source)
            userPlaylists,                     // Catalog (owns wavee:playlist:* — user-created playlists; before the fallback)
            new FakeSource(),                  // Catalog (synthetic collections + the non-spotify fallback)
            new FakePodcastSource(),           // Podcasts (synthetic shows / episodes; owns wavee:show:* / wavee:episode:*)
            mutations,                         // Mutations (save / like / follow)
            session,                           // Session (auth / account / market)
        });
        var library = new AggregateCatalog(registry);
        var svc = new Services(WaveeLog.Instance, session, library, player, devices, new NoLyricsProvider(), settings, mutations, userPlaylists, playlistEdits, new InMemoryActivityStore(), appLocale ?? AppLocale.English);
        // THE hydration façade: the router over the registry above (design §2.1) — the same door the real backend uses,
        // so nothing downstream can tell the two apart. Every source in the fake registry is COMPLETE at construction
        // (the export, local files, the synthetic catalog, user playlists), so every rung it routes to is already
        // reached and there is nothing to fetch; a uri no source owns answers Unsupported instead of pretending.
        svc.Hydrator = new HydrationRouter(registry);
        svc.Playback.AttachHydrator(svc.Hydrator);
        player.OnPlayIntentRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();   // any play intent → the standard toast
        svc.Log.Info("app", "Services created (sources: spotify-export, local-files, user-playlists, podcasts, fake + session facet; playback remote-only; mutations: saved-state + playlists)");
        return svc;
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
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");

        // The persistent, offline-first backend store (its own SQLite file under LocalAppData by default).
        accountDbPath ??= System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Wavee", "library.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(accountDbPath)!);
        // §G startup marks: the two ctors are the only synchronous disk work before first paint, so they are what
        // `boot.sqlite_open_ms` (open + migrate + open-time sweep) and `boot.identity_load_ms` (the DEFERRED ctor's
        // identity tier: saved sets, video map, overrides, pin mirrors) actually measure.
        long openStart = System.Environment.TickCount64;
        var cold = new Wavee.Backend.Persistence.SqliteColdStore(accountDbPath, Wavee.Backend.Persistence.SqliteColdStore.DefaultAccount, locale.SpotifyLanguage);
        long openMs = System.Environment.TickCount64 - openStart;
        long identityStart = System.Environment.TickCount64;
        var store = new Wavee.Backend.Persistence.CachedStore(cold);
        long identityMs = System.Environment.TickCount64 - identityStart;

        // The collection self-write echo registry (§7.1): the write strategy records accepted-write cuids; the sync loop
        // drops our own echoes. One instance shared between the write path and the read loop (wired below on go-live).
        var echoRing = new Wavee.Backend.Collections.CollectionEchoRing();
        var spclientBaseUrl = new Wavee.Backend.SpclientBaseUrlHolder();
        // I2 — the ONE rootlist write lane, shared by the outbox's follow/unfollow strategy and the direct rootlist ops
        // on PlaylistMutationSource (move / delete / visibility / create). Required on both; never optional.
        var rootlistLane = new Wavee.Backend.Playlists.RootlistLane();
        // The durable, multi-set mutation engine (set saves + playlist OpRebase edits) behind the IMutationSource seam.
        // I4 — the ONE post-drain revalidation queue, shared by the replay strategy and the sync loop (wired on go-live).
        var resyncQueue = new Wavee.Backend.Playlists.PlaylistResyncQueue();
        var mutEngine = new Wavee.Backend.MutationEngine(store,
            new Wavee.Backend.IMutationStrategy[]
            {
                new Wavee.Backend.SetReplayStrategy(echoRing),
                new Wavee.Backend.OpRebaseStrategy(store, () => spclientBaseUrl.Value, resyncQueue),
                new Wavee.Backend.CreatePlaylistStrategy(store, () => spclientBaseUrl.Value, resyncQueue),
                new Wavee.Backend.RootlistFollowStrategy(store, rootlistLane),
            }, cold);
        // The mutation transport is SWITCHABLE (stub → live dealer on go-live, back to stub on logout) so writes made while
        // logged out queue durably and replay on next login (§2.1); the drain binds to this stable facade once.
        var mutTransport = new Wavee.Backend.SwitchableTransport(new Wavee.Backend.StubTransport());
        var sessionHost = new Wavee.Backend.SessionContextHost(
            new Wavee.Backend.SessionContext("", "US", "premium", locale.SpotifyLanguage, Wavee.Backend.Tier.Premium, false));
        var mutations = new Wavee.Backend.EngineMutationSource(store, mutEngine, mutTransport, () => sessionHost.Current);

        // The catalog: the persistent Store-backed source (collection_items × shared entities; owns spotify:* + podcasts)
        // and the user-created playlists source (owns wavee:playlist:*).
        // THE façade for the Spotify source. It starts OFFLINE — a real implementation that answers from the store
        // (and promotes cold rows while doing it), never a null seam — and LiveSessionHost swaps the provider hydrator
        // in at go-live, GoOffline swaps it back. One reference, held forever by the source AND by svc.Hydrator.
        var spotifyHydration = new SwitchableEntityHydrator(new Wavee.Backend.Hydration.OfflineEntityHydrator(store));
        // …and THE online-read seam (design §2.7), for the same reason: search / suggest / home are ctor deps of the
        // source, which holds this one reference forever — go-live SetInner()s the Spotify catalog into it, GoOffline
        // resets it. It replaces the four Live* hooks the source used to expose.
        var onlineCatalog = new SwitchableOnlineCatalog(OfflineOnlineCatalog.Instance);
        var storeLibrary = new Wavee.Backend.Library.StoreLibrarySource(store, spotifyHydration, onlineCatalog, log: new WaveeLogger(WaveeLog.Instance, "library"));
        var userPlaylists = new UserPlaylistSource();
        userPlaylists.ExposeInCatalog = false;
        var playlistMutations = new Wavee.Backend.Playlists.PlaylistMutationSource(
            mutEngine, mutTransport, new Wavee.Backend.Spotify.HttpClientExchange(), () => sessionHost.Current,
            () => spclientBaseUrl.Value, userPlaylists, rootlistLane, store);
        // The "recommended songs" extender rides the SAME switchable transport (stub → live dealer on go-live, back on logout).
        var extender = new Wavee.Backend.Playlists.PlaylistExtenderClient(mutTransport);

        var registry = new SourceRegistry(new ISource[]
        {
            storeLibrary,          // Catalog | Podcasts (the real persistent backend; owns spotify:*)
            new LocalSource(),     // Catalog | Search | LocalDecode (the local-files peer)
            userPlaylists,         // Catalog (wavee:playlist:* — user-created playlists)
            mutations,             // Mutations (durable, multi-set save/unsave + playlist edits)
            session,               // Session
        });
        var library = new AggregateCatalog(registry);
        // P4 — THE hydration façade is the ROUTER over the registry (design §2.1). Built HERE rather than after the
        // Services ctor because the pre-login media session below needs it: its now-playing projection upgrades a thin
        // row through the same door every page open uses.
        var hydrationRouter = new HydrationRouter(registry);

        // ── the PRE-LOGIN local media session (playback modules, local files, internet radio) ───────────────────────
        // None of this needs a session, a token or a cluster — which is exactly why it is composed here and not in the
        // live bootstrap. `MediaProviderRegistry` used to be built inside LiveConnect, so a build with no Spotify
        // resolver had no routing table at all and "Play file…" was hidden until sign-in; the registry is a property of
        // the APP, not of a session, and go-live only prepends Spotify to it.
        (Wavee.Backend.Persistence.LocalCredentialStore credentials, string localDeviceId) =
            Wavee.SpotifyLive.SpotifyLiveLogin.OpenCredentialStore();
        var moduleSecrets = new Wavee.Backend.Modules.ProtectedModuleSecretStore(
            Wavee.Backend.Persistence.FileLocalStore.ForApp("Wavee"), credentials.Protector);
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
            BuildPreLoginMedia(mediaProviders, hydrationRouter, store, localDeviceId);
        PreLoginMedia preLogin = preLoginFactory();
        player.LocalPlayback = preLogin.Controller;
        player.CanPlayLocally = uri => mediaProviders.OwnerOf(uri) is not null;

        // Switchable facades over the fake playback/devices: a live Connect session swaps in at runtime (svc.GoLive)
        // without rebuilding the UI — the PlaybackBridge binds to these stable facades.
        var swPlayer = new Wavee.Backend.SwitchablePlayer(player);
        var swDevices = new Wavee.Backend.SwitchableDevices(devices);
        var swSession = new Wavee.Backend.SwitchableSession(session);
        var swLyrics = new Wavee.Backend.SwitchableLyrics(new NoLyricsProvider());   // swapped to the real AggregatingLyricsProvider on live login
        var svc = new Services(WaveeLog.Instance, swSession, library, swPlayer, swDevices, swLyrics, settings, mutations, userPlaylists, playlistMutations, new Wavee.Backend.Persistence.SqliteActivityStore(accountDbPath), locale);
        player.OnPlayIntentRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();   // pre-go-live: play intents show the "choose a remote device" toast
        svc.RealStore = store;
        // The user's local video-override curation. Wired HERE (not on go-live) precisely so attaching a custom mp4 works
        // WITHOUT Spotify: the bridge's has-video answer consults it directly, and the resolver installed below has no
        // source tier at all — an override is the only video such a build can serve. LiveSessionHost later replaces that
        // resolver with the full two-tier composite, keeping tier 1 identical.
        var videoOverrides = new VideoOverrideService(store, new WaveeLogger(WaveeLog.Instance, VideoOverrideService.LogCategory));
        videoOverrides.OnChanged = (uri, kind) => svc.Playback.NotifyVideoOverrideChanged(uri, kind);
        videoOverrides.OnBrokenLink = uri => svc.Playback.NotifyVideoOverrideMissing(uri);
        svc.VideoOverrides = videoOverrides;
        svc.Playback.AttachVideoOverrides(videoOverrides);
        svc.Playback.ResolveVideoSource = CompositeVideoResolver.OverridesOnly(videoOverrides).ResolveAsync;
        svc.RealLibrarySource = storeLibrary;
        svc.OnlineCatalog = onlineCatalog;   // the SAME switchable storeLibrary holds — never a second one
        svc.SpotifyHydration = spotifyHydration;
        // P4: THE façade is the ROUTER over the registry, not the Spotify switchable directly (design §2.1). A
        // spotify: uri still lands in `spotifyHydration` — StoreLibrarySource.Hydrator IS that switchable — but a
        // mixed batch (a playlist holding a local import, a session-created wavee:playlist:) now reaches the source
        // that actually owns each uri instead of being reported Unsupported by the Spotify ladder.
        svc.Hydrator = hydrationRouter;
        svc.Playback.AttachHydrator(svc.Hydrator);   // the queue's trait pass rides the same door
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
        svc.RealResyncQueue = resyncQueue;
        svc.RealSessionHost = sessionHost;
        svc.EchoRing = echoRing;
        svc.RealMutationSource = mutations;
        svc.RealPlaylistMutations = playlistMutations;
        svc.RealExtender = extender;
        svc.RealSpclientBaseUrl = spclientBaseUrl;
        // The metadata-cache GC (design §C). Constructed here (composition root) but NOT started: it needs the UI-thread
        // marshaller to snapshot BuildPinSet, so WaveeApp's mount effect arms it — the same place, and the same `post`,
        // the MemoryGovernor poll uses. It also owns the one-time post-migration VACUUM that used to be kicked off here.
        svc.CacheGc = new Wavee.Backend.Persistence.EntityCacheGc(cold, store, svc.Log, svc.BuildPinSet,
            System.Math.Max(MinMetadataCacheBudgetBytes, settings.Get(WaveeSettings.MetadataCacheBudgetBytes)))
        {
            OpenMillis = openMs,
            IdentityLoadMillis = identityMs,
        };
        svc.Log.Info("app", "Services created (REAL backend: persistent Store + StoreLibrarySource + durable multi-set mutations; live session/fetch/dealer connect on bootstrap)");
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
        DisposePreLoginMedia();
        var modules = Modules;
        Modules = null;
        modules?.Dispose();
    }

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
