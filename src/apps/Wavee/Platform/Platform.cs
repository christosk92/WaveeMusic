// ── Platform/Platform.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the settings key table + epochs, boot, credentials, NetworkPolicy, ZoomAutoPolicy (+ MigrateMode in Main),
// AppLocaleBootstrap, the ambient power policy, --fake args + Clock.SeedEpoch, the WaveeLog seam,
// PlaybackRuntimeSetupModel.Phase's enum (ch 28 §11-21: it must land ONCE, here, not in both the wizard and the
// shell card)
//
// Role: CORE
// Owner: S
// Wave: 6
// Budget: 1100 lines
// Spec: DERIVED (ch 29 §9.10 share 400 + ch 30 §9.4 plumbing + ch 27 §9.5)
// Wave 0 first cut (orchestrator): settings, credential slot, log seam, ZoomAutoPolicy, Glyphs; owner S completes in Wave 6
//
// WHY THIS FILE EXISTS BEFORE ITS WAVE (plan §5, the Wave 1 note). §3.5's `Main` calls `Platform.Boot()` before
// anything else; Wave 2 reads the stored credential through it and Wave 4 reads the settings key table. The pieces
// below are therefore orchestrator-owned from Wave 0 and owner S finishes the file in Wave 6 — see "LEFT FOR OWNER S"
// at the bottom for the exact list, so nothing missing here is mistaken for a decision that was already made.
// CORE rules (P8/P9/C1): nothing on a read path allocates after warm-up, no LINQ, no closures, no async, no boxing,
// one writer. BOOT IS ONE-SHOT and may allocate: the key table, the ring and the credential decode all run once.
//
// ON-DISK / IN-REGISTRY LAYOUT IS 0.2.9's, UNCHANGED. Settings are HKCU values under the engine's AppDataStore
// ("Wavee"/"Wavee"); the credential and the device id are `%LOCALAPPDATA%\Wavee\store.json` (redirected into the
// package's LocalCache on a packaged run); logs are `%LOCALAPPDATA%\Wavee\logs\wavee-yyyyMMdd.log`. An existing
// profile must open in 0.3 with everything remembered, so no key name, no default and no file name moves.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentGpu.Signals;

namespace Wavee;

// ── 1. the settings seam ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A statically-typed persisted-setting key: its storage name + the value returned when the key is absent.
/// A key can only be read/written as its declared <typeparamref name="T"/>, so a call site cannot mismatch types or
/// fat-finger a magic string. A STRUCT in 0.3 (0.2.9 had a sealed record): the per-subject factories below build one
/// per call site, and a key that is a value carries no object into the GC for a sort the user did once.</summary>
public readonly record struct SettingKey<T>(string Name, T Default);

/// <summary>The app's persisted settings, abstracted away from the concrete (Windows-registry) backing store: call
/// sites depend only on this interface + the typed keys, so the store is trivially fakeable in a test or swappable
/// per platform. Ch 30 §7.1's contract holds — an ABSENT store answers every read with the key's default and drops
/// every write; nothing dereferences a settings store unconditionally.</summary>
public interface IAppSettings
{
    T Get<T>(SettingKey<T> key);
    void Set<T>(SettingKey<T> key, T value);
}

// ── 2. the key registry ──────────────────────────────────────────────────────────────────────────────────────────────

// NESTED IN Platform, not top-level, for one hard reason: the engine already exports
// FluentGpu.Foundation.Keys (the VK_* codes) and every app file that handles a key writes Keys.Escape
// unqualified. A top-level Wavee.Keys would shadow it from inside namespace Wavee and break every one of those
// call sites. Ch 30 §9.3(7) asks for Platform.cs's "static class Keys"; this IS it — spelled Platform.Keys.
public static partial class Platform
{
    /// <summary>Every persisted setting, as one statically-typed key — the single registry of what the app remembers
    /// (ported name-for-name and default-for-default from 0.2.9's `WaveeSettings`; a rename here is a user who lost a
    /// preference). The comments are the documentation: each one records a decision with a report behind it and is the
    /// only place that reason exists (ch 30 §9.1).
    ///
    /// <para>Keys whose DEFAULT is an enum member of a type a later wave owns carry the int literal plus the member name
    /// in a comment — the enum's VALUES are the wire, so a treatment appended later never re-means a stored int, and this
    /// file does not have to depend on `Design.cs`/`Notify.cs`/`Sidebar.cs` to boot. The same applies to the four rail
    /// widths, whose defaults are `Design.cs` tokens: Wave 4 owes a convergence test pinning the two (the
    /// `ZoomAutoPolicy.DesignW` precedent).</para></summary>
    public static class Keys
    {
        // ── sidebar: legacy (v0) global pane keys — read only by the v0→v1 migration. Deliberately NOT deleted: a
        //    downgrade to an older build must still find a sane pane width. The pane state is per DESIGN (SidebarWidth
        //    below) since v1.
        public static readonly SettingKey<float> SidebarWidthLegacy = new("sidebar.width", 300f);
        public static readonly SettingKey<bool> SidebarWidthUserSetLegacy = new("sidebar.width.userSet", false);
        public static readonly SettingKey<bool> SidebarCollapsedLegacy = new("sidebar.collapsed", false);
        /// <summary>The active sidebar design as an int. DEFAULT 0 = Classic IS LOAD-BEARING: an existing install that
        /// never wrote the key silently stays Classic.</summary>
        public static readonly SettingKey<int> SidebarDesign = new("sidebar.design", 0);
        /// <summary>The one-time design-chooser marker; set true for EXISTING installs so they never see the chooser.</summary>
        public static readonly SettingKey<bool> SidebarOnboardingSeen = new("sidebar.onboarding.seen", false);
        /// <summary>Monotonic "which sidebar startup migrations have run" (0 = never; target 1). There is no key-exists
        /// probe, so this is the ONLY thing that tells "never written" from "written as the default".</summary>
        public static readonly SettingKey<int> SidebarBootstrapVersion = new("sidebar.bootstrap.version", 0);
        public static readonly SettingKey<bool> ClassicPinnedOpen = new("sidebar.classic.section.pinned", true);
        public static readonly SettingKey<bool> ClassicLibraryOpen = new("sidebar.classic.section.library", true);
        public static readonly SettingKey<bool> ClassicPlaylistsOpen = new("sidebar.classic.section.playlists", true);
        public static readonly SettingKey<int> V3Filter = new("sidebar.v3.filter", 0);
        public static readonly SettingKey<int> V3Qualifier = new("sidebar.v3.qualifier", 0);
        public static readonly SettingKey<int> V3Sort = new("sidebar.v3.sort", 0);
        /// <summary>Ignored while the sort is Custom; the stored value is PRESERVED so returning to another sort restores it.</summary>
        public static readonly SettingKey<bool> V3Desc = new("sidebar.v3.desc", false);
        public static readonly SettingKey<int> V3View = new("sidebar.v3.view", 1);
        public static readonly SettingKey<int> V3GridSize = new("sidebar.v3.size", 1);          // 0 S · 1 M · 2 L
        public static readonly SettingKey<string> CuratedTemplateId = new("sidebar.curated.template", "wavee.curated.default");
        public static readonly SettingKey<bool> CuratedRailLabels = new("sidebar.curated.rail.labels", false);
        /// <summary>One-time upgrade latch: until the first converged walk of the server's ylpin set has pushed every
        /// pre-existing local pin up, an empty/partial mirror must never look like "unpin everything".</summary>
        public static readonly SettingKey<bool> PinsMigratedToServer = new("sidebar.pins.migratedToServer", false);

        // ── first-run setup wizard ──
        /// <summary>Armed for a fresh install; EVERY wizard exit path clears it, so it can never reappear uninvited.</summary>
        public static readonly SettingKey<bool> SetupPending = new("setup.pending", false);
        /// <summary>Reached the wizard's Done page at least once — distinct from Pending being false, which a DEFERRED
        /// wizard also produces.</summary>
        public static readonly SettingKey<bool> SetupCompleted = new("setup.completed", false);
        public static readonly SettingKey<int> SetupBootstrapVersion = new("setup.bootstrap.version", 0);
        /// <summary>The terms version accepted in the wizard (0 = never); a bump re-arms the Terms page next launch.</summary>
        public static readonly SettingKey<int> TermsAcceptedVersion = new("setup.terms.acceptedVersion", 0);

        // ── appearance / theme / zoom ──
        /// <summary>0 System (follow the OS live) · 1 Light · 2 Dark. Seeded before the first frame.</summary>
        public static readonly SettingKey<int> ThemeMode = new("theme.mode", 0);
        /// <summary>"system" asks the startup root to resolve the Windows UI locale; an explicit BCP-47 tag selects the
        /// matching bundled JSON table. Applied before first mount on the NEXT launch.</summary>
        public static readonly SettingKey<string> UiCulture = new("localization.culture", "system");
        public static readonly SettingKey<int> RowDensity = new("detail.rowdensity", 1);        // 0 Compact · 1 Default · 2 Cozy · 3 Comfortable
        public static readonly SettingKey<int> TrackRowStyle = new("detail.rowstyle", 0);       // 0 Modern · 1 Classic
        /// <summary>Artwork inside TRACK cells only: page heroes, media cards, sidebar covers and the player's identity
        /// artwork are deliberately unaffected.</summary>
        public static readonly SettingKey<bool> HideTrackArtwork = new("appearance.trackArtwork.hidden", false);
        /// <summary>The Liked Songs cover treatment. DEFAULT 1 = LikedCoverStyle.Lens, not Stock: the point of the feature
        /// is that the collection cover is made of the user's own music, and every treatment degrades to the bundled PNG
        /// until the library owns enough distinct artwork — so a fresh install still paints what it painted before.</summary>
        public static readonly SettingKey<int> LikedCoverStyle = new("appearance.likedCover.style", 1);
        /// <summary>BPM · Key as its own COLUMN. Off: it is enrichment most listeners never scan for and it costs width on
        /// every row; it is always available inside a row's expander regardless.</summary>
        public static readonly SettingKey<bool> TempoColumn = new("detail.tempoColumn", false);
        /// <summary>Stream counts as their own COLUMN. VISIBILITY ONLY — the trait bundle rides every list surface
        /// regardless, because gating the FETCH on this setting permanently starved lists opened while it was off.</summary>
        public static readonly SettingKey<bool> PlaysColumn = new("detail.playsColumn", false);
        /// <summary>0 Automatic (rail on wide windows, hero on narrow) · 1 Hero (the hero composition at every width).</summary>
        public static readonly SettingKey<int> DetailPageLayout = new("detail.page.layout", 0);
        /// <summary>TRUE (the default) scrolls overflowing text; FALSE truncates. Renamed from the negative DisableMarquee
        /// (no migration — pre-1.0, cosmetic).</summary>
        public static readonly SettingKey<bool> MarqueeEnabled = new("appearance.marquee.enabled", true);
        /// <summary>FALSE keeps the neutral surface: every tone plane / wash / tint binder paints nothing.</summary>
        public static readonly SettingKey<bool> ColorWashesEnabled = new("appearance.colorWashes.enabled", true);
        /// <summary>App-wide UI zoom (effective scale = OS DPI × zoom). Seeded into the window BEFORE it comes up and
        /// SNAPPED on that read: a hand-edited value must never seed a non-ladder scale, because off-rung zooms alias the
        /// glyph-atlas raster buckets — which is the whole reason the ladder is discrete.</summary>
        public static readonly SettingKey<float> ZoomLevel = new("appearance.zoom", 1f);
        /// <summary>The zoom picker's mode (<see cref="ZoomAutoMode"/>). DEFAULT Auto is fresh-install-only in effect:
        /// <see cref="ZoomAutoPolicy.MigrateMode"/> pins an upgrade that already stores a non-1.0 zoom to Manual.</summary>
        public static readonly SettingKey<int> ZoomMode = new("appearance.zoom.mode", (int)ZoomAutoMode.Auto);
        public static readonly SettingKey<int> ZoomModeBootstrapVersion = new("appearance.zoom.mode.bootstrap.version", 0);

        // ── lyrics ──
        public static readonly SettingKey<bool> LyricsAnimatedBackdrop = new("lyrics.backdrop.animated", true);
        public static readonly SettingKey<int> LyricsSecondaryLine = new("lyrics.secondary", 0);   // 0 Off · 1 Translation · 2 Romanization
        public static readonly SettingKey<int> LyricsBlurStrength = new("appearance.lyrics.blurStrength", -1);   // -1 = Auto

        // ── detail rails + shell rail. The four widths default to Design.cs tokens (RailAlbum 280 / RailPlaylist 240 /
        //    ShellResponsiveLayout.RailDefaultW 340); Wave 4 owes the convergence test that pins these literals to them.
        public static readonly SettingKey<float> DetailAlbumRailWidth = new("detail.rail.album.width", 280f);
        public static readonly SettingKey<float> DetailPlaylistRailWidth = new("detail.rail.playlist.width", 240f);
        public static readonly SettingKey<float> DetailLikedRailWidth = new("detail.rail.liked.width", 240f);
        public static readonly SettingKey<float> DetailShowRailWidth = new("detail.rail.show.width", 280f);
        public static readonly SettingKey<float> ShellRailWidth = new("shell.rail.width", 340f);
        public static readonly SettingKey<float> ShellDockedVideoHeight = new("shell.rail.docked-video.height", 0f);
        public static readonly SettingKey<bool> DetailAlbumRailCollapsed = new("detail.rail.album.collapsed", false);
        public static readonly SettingKey<bool> DetailPlaylistRailCollapsed = new("detail.rail.playlist.collapsed", false);
        public static readonly SettingKey<bool> DetailLikedRailCollapsed = new("detail.rail.liked.collapsed", false);
        public static readonly SettingKey<bool> DetailShowRailCollapsed = new("detail.rail.show.collapsed", false);
        public static readonly SettingKey<bool> DetailRailUniform = new("detail.rail.uniform", false);
        public static readonly SettingKey<float> DetailUniformRailWidth = new("detail.rail.uniform.width", 240f);
        public static readonly SettingKey<bool> DetailUniformRailCollapsed = new("detail.rail.uniform.collapsed", false);

        // ── shell state ──
        public static readonly SettingKey<bool> PlayerBarShowRemaining = new("playerbar.duration.remaining", true);
        public static readonly SettingKey<string> TipsSeen = new("tips.seen", "");
        public static readonly SettingKey<string> SavedLibrary = new("library.saved", "");
        public static readonly SettingKey<string> PlaylistDepositRecents = new("playlist.deposit.recents", "");
        public static readonly SettingKey<string> WorkspacePinnedTabs = new("workspace.tabs.pinned", "");

        // ── video surfaces ──
        public static readonly SettingKey<string> VideoPreferredPlacement = new("video.placement", "");
        public static readonly SettingKey<string> VideoPipRect = new("video.pip.rect", "");        // window-DIP "x,y,w,h"
        public static readonly SettingKey<string> VideoWindowRect = new("video.window.rect", "");  // screen-px "x,y,w,h"
        public static readonly SettingKey<string> VideoAspectMode = new("video.aspect.mode", "fit");
        public static readonly SettingKey<double> VideoCustomAspectRatio = new("video.aspect.customRatio", 16.0 / 9.0);
        public static readonly SettingKey<bool> VideoWindowAlwaysOnTop = new("video.window.ontop", true);

        // ── local playback runtime (the provisioning card; RuntimePhase below) ──
        public static readonly SettingKey<string> PlaybackRuntimePath = new("playback.runtime.path", "");
        public static readonly SettingKey<string> PlaybackRuntimePackId = new("playback.runtime.packId", "");
        public static readonly SettingKey<bool> PlaybackRuntimeSetupDismissed = new("playback.runtime.dismissed", false);
        public static readonly SettingKey<string> PlaybackRuntimeCatalogUrl = new("playback.runtime.catalogUrl", "");

        // ── playback ──
        public static readonly SettingKey<int> PlaybackQuality = new("playback.quality", 2);
        public static readonly SettingKey<int> MeteredQualityCap = new("playback.quality.meteredCap", 1);
        public static readonly SettingKey<int> VideoQuality = new("playback.video.quality", 0);
        public static readonly SettingKey<int> VideoMeteredMaxHeight = new("playback.video.quality.meteredMaxHeight", 480);
        public static readonly SettingKey<bool> RememberVolume = new("playback.volume.remember", true);
        public static readonly SettingKey<float> SavedVolume = new("playback.volume", 0.7f);
        public static readonly SettingKey<string> OutputDeviceId = new("playback.output.deviceId", "");
        public static readonly SettingKey<string> OutputDeviceName = new("playback.output.deviceName", "");
        public static readonly SettingKey<bool> EqualizerEnabled = new("playback.eq.enabled", false);
        public static readonly SettingKey<string> EqualizerPreset = new("playback.eq.preset", "flat");
        public static readonly SettingKey<string> EqualizerGains = new("playback.eq.gains", "0,0,0,0,0,0,0,0,0,0");
        public static readonly SettingKey<bool> CrossfadeEnabled = new("playback.crossfade.enabled", false);
        public static readonly SettingKey<int> CrossfadeMs = new("playback.crossfade.ms", 5000);
        public static readonly SettingKey<bool> AutoplayEnabled = new("playback.autoplay", true);
        public static readonly SettingKey<bool> NormalizationEnabled = new("playback.normalization", true);

        // ── notifications (levels are NotifyLevel: 0 Off · 1 InApp · 2 Windows) ──
        public static readonly SettingKey<bool> NotifyWindows = new("notify.windows", false);
        public static readonly SettingKey<bool> NotifySound = new("notify.sound", true);
        public static readonly SettingKey<bool> NotifyQuietEnabled = new("notify.quiet.enabled", false);
        public static readonly SettingKey<int> NotifyQuietFromHour = new("notify.quiet.from", 22);
        public static readonly SettingKey<int> NotifyQuietToHour = new("notify.quiet.to", 8);
        public static readonly SettingKey<int> NotifyNewAlbums = new("notify.topic.newAlbums", 1);
        public static readonly SettingKey<int> NotifyNewEpisodes = new("notify.topic.newEpisodes", 1);
        public static readonly SettingKey<int> NotifyReleaseDrops = new("notify.topic.releaseDrops", 2);
        public static readonly SettingKey<int> NotifyConcerts = new("notify.topic.concerts", 1);
        public static readonly SettingKey<int> NotifyFollowers = new("notify.topic.followers", 1);
        public static readonly SettingKey<int> NotifyDaylist = new("notify.topic.daylistRefresh", 1);
        public static readonly SettingKey<int> NotifyAppUpdates = new("notify.topic.appUpdates", 1);
        public static readonly SettingKey<int> NotifyLibraryActivity = new("notify.topic.libraryActivity", 1);
        public static readonly SettingKey<long> NotifyLastToastedMs = new("notify.lastToastedMs", 0L);
        public static readonly SettingKey<long> NotificationsGanderLastSeenMs = new("notifications.gander.lastSeenMs", 0L);
        public static readonly SettingKey<long> NotificationsWhatsNewLastSeenMs = new("notifications.whatsnew.lastSeenMs", 0L);
        public static readonly SettingKey<string> NotificationsReadIds = new("notifications.readIds", "");

        // ── now playing / deck ──
        public static readonly SettingKey<int> NpvPresentation = new("npv.presentation", 0);
        public static readonly SettingKey<int> NpvPlayerStyle = new("npv.player.style", 0);

        // ── caches ──
        public static readonly SettingKey<bool> AudioBodyCacheEnabled = new("audio.cache.body.enabled", true);
        public static readonly SettingKey<bool> AudioKeyCacheEnabled = new("audio.cache.keys.enabled", true);
        public static readonly SettingKey<int> AudioBodyCacheBudgetMode = new("audio.cache.body.budgetMode", 1);
        public static readonly SettingKey<long> AudioBodyCacheBudgetBytes = new("audio.cache.body.budgetBytes", 32L << 30);
        public static readonly SettingKey<int> AudioBodyCacheBudgetPercent = new("audio.cache.body.budgetPercent", 0);
        public static readonly SettingKey<string> AudioBodyCacheBasePath = new("audio.cache.body.basePath", "");
        public static readonly SettingKey<long> MetadataCacheBudgetBytes = new("cache.metadata.budgetBytes", 64L << 20);

        // ── session / account ──
        /// <summary>Spotify Connect private session (published on the device state; profile-menu toggle).</summary>
        public static readonly SettingKey<bool> PrivateSession = new("session.private", false);
        /// <summary>The normalized username of the last account that went live — a change resets the local library
        /// projection, and it is what <see cref="Platform.Scope"/> falls back to when the credential slot is empty.</summary>
        public static readonly SettingKey<string> LastAccount = new("session.lastAccount", "");

        // ── app / update / crash ──
        public static readonly SettingKey<bool> HandleSpotifyLinks = new("app.protocol.spotify", false);
        public static readonly SettingKey<bool> StartOnLogin = new("app.startOnLogin", false);
        public static readonly SettingKey<string> LastRunVersion = new("app.lastRunVersion", "");
        public static readonly SettingKey<long> UpdateLastCheckedMs = new("app.update.lastCheckedMs", 0L);
        public static readonly SettingKey<string> UpdateSnoozedVersion = new("app.update.snoozedVersion", "");
        public static readonly SettingKey<bool> UpdateInstallOnQuit = new("app.update.installOnQuit", false);
        public static readonly SettingKey<bool> UpdateOnMetered = new("app.update.onMetered", false);
        public static readonly SettingKey<bool> ReleaseNotesAutoShow = new("app.whatsnew.autoShow", true);
        public static readonly SettingKey<string> ReleaseNotesLastSeen = new("app.whatsnew.lastSeenVersion", "");
        public static readonly SettingKey<string> ReleaseNotesPendingFrom = new("app.whatsnew.pendingFrom", "");
        public static readonly SettingKey<string> ReleaseNotesPreviousVersion = new("app.whatsnew.previousVersion", "");
        public static readonly SettingKey<string> RunMarker = new("app.runMarker", "");
        public static readonly SettingKey<string> PendingCrashReport = new("crash.pendingReport", "");
        public static readonly SettingKey<bool> CrashPromptOptOut = new("crash.promptOptOut", false);
        public static readonly SettingKey<bool> UncleanExitOffered = new("crash.uncleanExitOffered", false);
        public static readonly SettingKey<string> LastSeenCrashDumpPath = new("diagnostics.crash.lastDumpPath", "");
        public static readonly SettingKey<long> LastSeenCrashDumpTicksUtc = new("diagnostics.crash.lastDumpTicksUtc", 0L);

        // ── notification area (docs/plans/wavee/wavee-0.3-tray-implementation.md §8, defaults per its §13) ──
        /// <summary>0 Always · 1 Only while the window is hidden · 2 Never (<see cref="Tray.IconMode"/>; read it through
        /// <see cref="Tray.ModeFrom"/>). DEFAULT 1 = WhileHidden (§13): a user who never hides Wavee keeps a clean tray.
        /// Never forces both hide modes off — a hidden window with no icon is a ghost.</summary>
        public static readonly SettingKey<int> TrayIconMode = new("tray.icon.mode", 1);
        /// <summary>The caption ✕ / Alt+F4 hide the window instead of quitting. OFF (§13): the Close button quits.</summary>
        public static readonly SettingKey<bool> TrayCloseToTray = new("tray.closeToTray", false);
        /// <summary>The caption – / Win+Down hide the window (taskbar button gone) instead of iconifying it. OFF (§13).</summary>
        public static readonly SettingKey<bool> TrayMinimizeToTray = new("tray.minimizeToTray", false);
        /// <summary>Start hidden on the SIGN-IN launch only (the StartupTask / the Run value's --tray); meaningless without
        /// <see cref="StartOnLogin"/>.</summary>
        public static readonly SettingKey<bool> TrayStartHidden = new("tray.startHidden", false);

        // ── gpu ──
        public static readonly SettingKey<long> PreferredGpuLuid = new("gpu.preferredLuid", 0L);
        public static readonly SettingKey<string> PreferredGpuName = new("gpu.preferredName", "");

        // ── telemetry ──
        public static readonly SettingKey<long> GaboGlobalSequence = new("telemetry.gabo.globalSequence", 0L);

        // ── diagnostics ── (-1 = "use the build default"; owner S's LogCapturePolicy is the ONE place that resolves it)
        public static readonly SettingKey<int> LogMinLevel = new("diagnostics.log.minLevel", -1);
        public static readonly SettingKey<int> LogFileMinLevel = new("diagnostics.log.fileMinLevel", -1);
        public static readonly SettingKey<bool> DeveloperMode = new("diag.developerMode", false);
        public static readonly SettingKey<bool> FpsOverlay = new("diag.fpsOverlay", false);
        public static readonly SettingKey<bool> DealerArchiveEnabled = new("diag.dealerArchive", false);

        // ── PER-SUBJECT keys (ch 30 §9.3(7)) ────────────────────────────────────────────────────────────────────────────
        // There is one of these per album/playlist/library kind/sidebar design the user has ever touched, so they cannot
        // be static members — they are BUILT at the call site. This is the named seam that stops each Wave-5 page owner
        // inventing their own; the string shape is 0.2.9's and is persisted, so it never changes. A key is a struct, so
        // the only allocation is the name, and these are read at mount, never per frame.

        /// <summary>Per-context track-table sort column / direction (`detail.sort.col:&lt;ctxUri&gt;`).</summary>
        public static SettingKey<int> DetailSortCol(string contextUri) => new("detail.sort.col:" + contextUri, 0);
        public static SettingKey<bool> DetailSortDesc(string contextUri) => new("detail.sort.desc:" + contextUri, false);

        /// <summary>Per-library-kind three-column state (`library.&lt;kind&gt;.*`; kind is "artists"/"albums"/…).</summary>
        public static SettingKey<float> LibraryLeftW(string kind) => new("library." + kind + ".leftw", kind == "artists" ? 280f : 340f);
        public static SettingKey<float> LibraryMidW(string kind) => new("library." + kind + ".midw", 440f);
        public static SettingKey<int> LibrarySort(string kind) => new("library." + kind + ".sort", 0);
        public static SettingKey<bool> LibraryDesc(string kind) => new("library." + kind + ".desc", false);
        public static SettingKey<int> LibraryView(string kind) => new("library." + kind + ".view", 1);
        public static SettingKey<int> LibrarySize(string kind) => new("library." + kind + ".size", 1);
        public static SettingKey<string> LibrarySelected(string kind) => new("library." + kind + ".selected", "");
        public static SettingKey<string> LibraryAlbumKey(string kind) => new("library." + kind + ".albumkey", "");
        public static SettingKey<int> LibraryAlbumSort(string kind) => new("library." + kind + ".album.sort", 0);
        public static SettingKey<bool> LibraryAlbumDesc(string kind) => new("library." + kind + ".album.desc", false);
        public static SettingKey<int> LibraryAlbumView(string kind) => new("library." + kind + ".album.view", 3);   // Grid
        public static SettingKey<int> LibraryAlbumSize(string kind) => new("library." + kind + ".album.size", 1);

        /// <summary>Per-DESIGN sidebar pane state (`sidebar.&lt;slug&gt;.*`; slugs are "classic"/"library-v3"/"curated"
        /// and are PERSISTED — never rename one). The tier default is passed IN, from the design table that owns it, so
        /// this file does not duplicate — and drift from — `Sidebar.cs`'s ladder.</summary>
        public static SettingKey<float> SidebarWidth(string designSlug, float tierNarrow) => new("sidebar." + designSlug + ".width", tierNarrow);
        /// <summary>While a design's WidthUserSet is false its width follows that design's tier ladder; the first
        /// committed seam drag in that design latches it forever, for that design only. Collapsing the pane is NOT a
        /// width choice and must never set it.</summary>
        public static SettingKey<bool> SidebarWidthUserSet(string designSlug) => new("sidebar." + designSlug + ".width.userSet", false);
        public static SettingKey<bool> SidebarCollapsed(string designSlug) => new("sidebar." + designSlug + ".collapsed", false);

        /// <summary>A deck/player preset's own option (`npv.player.&lt;preset&gt;.&lt;option&gt;`).</summary>
        public static SettingKey<int> NpvOption(string presetSlug, string optionSlug) => new("npv.player." + presetSlug + "." + optionSlug, 0);
    }
}

// ── 3. the zoom policy ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The zoom picker's three states. Stored as an int, so a value this build does not define clamps rather
/// than throws. Auto/Dense re-derive <c>appearance.zoom</c> from the display; Manual is the browser-style Ctrl+±
/// ladder alone, unmanaged by this policy. <see cref="Dense"/> is BUILT but unreachable from the Appearance tab
/// (ch 00 §6.5) — the policy supports it; nothing writes it.</summary>
public enum ZoomAutoMode { Auto = 0, Manual = 1, Dense = 2 }

/// <summary>Derives the app zoom from the window's DIP extent against the app's own design box, so a single "one
/// number for every display" zoom stops being wrong on a 2×-DIP-wider monitor. BCL-only by construction, so the
/// tests drive the REAL decision rather than a copy of it.
///
/// <para>THE DESIGN BOX IS NOT A FRESH NUMBER. <see cref="DesignW"/> is `Design.cs`'s page-measure cap (the number
/// every page centres at) and <see cref="DesignH"/> is the detail frame's tall-hero gate (winH >= 900). Both are
/// kept as literals so this stays BCL-only; Wave 4 owes the convergence test that pins them to the tokens.</para>
///
/// <para>BOTH AXES BIND. Width alone would pick a zoom that DEMOTES structural decisions the app already made on
/// height purely because a monitor happens to be short — the failure mode of every naive "match my display"
/// auto-zoom. min() over the two axis ratios makes the policy incapable of buying width by giving up height.</para>
///
/// <para><see cref="Suggest"/> takes BASE dips — client px ÷ the OS DPI scale ALONE, at zoom 1 — never the live,
/// already-zoomed viewport: feeding that back closes a loop that converges on the design box whatever the display
/// actually is. Two very different panels presenting the same base box must get the SAME answer, and a monitor with
/// twice the DIPs must never suggest a SMALLER zoom.</para></summary>
public static class ZoomAutoPolicy
{
    /// <summary>= the page-measure cap — a literal, not a reference; see the type doc.</summary>
    public const float DesignW = 1600f;

    /// <summary>= the detail frame's tall-hero gate; see the type doc.</summary>
    public const float DesignH = 900f;

    /// <summary>Never suggest more than 200%: past this, on any real panel measured for this policy, the DIP viewport
    /// drops below the design box on at least one axis.</summary>
    public const float Ceiling = 2f;

    /// <summary>The floor <see cref="ZoomAutoMode.Dense"/> may suggest below 100%. Auto never goes below 100%.</summary>
    public const float DenseFloor = 0.75f;

    /// <summary>The plateau-clean subset of the engine's zoom ladder — Microsoft's 4-epx rule lands on whole pixels
    /// only at the 100/125/150/175/200% plateaus; the ladder's 0.67/0.8/0.9/1.1 rungs put a 4-DIP metric on a
    /// fractional pixel and stay available to a MANUAL pick only.</summary>
    static readonly float[] Plateaus = [0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];

    /// <summary>The neutral zoom returned for a degenerate input (a headless read, or a corrupt base extent).</summary>
    const float NeutralZoom = 1f;

    // Plateau-membership comparisons tolerate float noise exactly like the ladder's own snap epsilon.
    const float Epsilon = 1e-4f;

    /// <summary>The zoom that makes THIS window's DIP viewport match the design box — see the type doc for the full
    /// contract (base, not live, dips; both axes bind).</summary>
    public static float Suggest(float baseW, float baseH, ZoomAutoMode mode)
    {
        if (!float.IsFinite(baseW) || !float.IsFinite(baseH) || baseW <= 0f || baseH <= 0f) return NeutralZoom;
        float ratio = MathF.Min(baseW / DesignW, baseH / DesignH);
        float lo = mode == ZoomAutoMode.Dense ? DenseFloor : NeutralZoom;
        return SnapPlateauDown(Math.Clamp(ratio, lo, Ceiling));
    }

    /// <summary>Snap DOWN to the nearest plateau at or below <paramref name="ratio"/> — 1.20 → 100% and 1.55 → 150%,
    /// never the nearer-by-distance rung. The caller has already clamped to [lo, Ceiling], both of which are plateau
    /// members, so this always finds a member at or below it.</summary>
    static float SnapPlateauDown(float ratio)
    {
        float best = Plateaus[0];
        for (int i = 0; i < Plateaus.Length; i++)
            if (Plateaus[i] <= ratio + Epsilon) best = Plateaus[i];
        return best;
    }

    /// <summary>Monotonic "has the zoom-mode migration run" guard. There is no key-exists probe, so this is the only
    /// way to tell "never written" from "written as the default" — which is what lets a factory-reset store re-arm
    /// the migration cleanly. Bump this AND extend <see cref="MigrateMode"/> for a future one-time step.</summary>
    public const int MigrationTargetVersion = 1;

    /// <summary>One-shot, run at settings load (<see cref="Platform.Boot"/>, before ANYTHING reads
    /// <c>appearance.zoom.mode</c> — ch 00 §9.5). A FRESH install leaves the mode at its default (Auto): nothing to
    /// write, since <c>appearance.zoom</c> is also still 1.0. An UPGRADE that already stores a non-1.0 zoom is pinned
    /// to Manual so this policy can never silently override a zoom the user already picked — a user who chose 125%
    /// must not wake up at 150%. An upgrade sitting exactly at 1.0 (never touched the picker, or deliberately reset
    /// to 100%) is indistinguishable from a fresh install and is left on Auto, which is the better default for that
    /// install too.</summary>
    public static void MigrateMode(IAppSettings settings)
    {
        if (settings.Get(Platform.Keys.ZoomModeBootstrapVersion) >= MigrationTargetVersion) return;
        float stored = settings.Get(Platform.Keys.ZoomLevel);
        if (MathF.Abs(stored - NeutralZoom) > 0.004f)
            settings.Set(Platform.Keys.ZoomMode, (int)ZoomAutoMode.Manual);
        settings.Set(Platform.Keys.ZoomModeBootstrapVersion, MigrationTargetVersion);
    }
}

// ── 4. the local-playback provisioning phase ─────────────────────────────────────────────────────────────────────────

/// <summary>The eight local-playback provisioning phases. ONE enum, here, for the whole app (ch 28 §11 item 21 and
/// DATA GAP D3): the wizard's LocalPlayback page, the shell's provisioning card, the runtime footer and the setup
/// facet fold all switch on THIS type. 0.2.9 had it as `PlaybackRuntimeSetupModel.Phase`, already split into its own
/// engine-free file so the tests could drive the real fold; 0.3 keeps that property by putting it in CORE. Do not
/// mint a second one in `Screens/Setup.cs`.</summary>
public enum RuntimePhase { Offer, FetchingCatalog, Downloading, Verifying, Untrusted, Ready, Failed, Advanced }

// ── 5. the credential slot ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What the stored secret IS. The value is persisted as its NAME, so the wire is the spelling — but the
/// NUMBERS deliberately match <c>Spotify.CredentialKind</c> (Wave 2, <c>Spotify.Session.cs</c>) member for member, so
/// the one-line wiring that file's seam documents is a value-preserving cast and not a switch that can drift.</summary>
public enum CredentialKind : byte { None = 0, ReusableBlob = 1, OAuthToken = 2 }

/// <summary>The stored Spotify credential — exactly the four fields 0.2.9 persisted. A struct: it is read once at
/// boot and handed to the session, and it must not leave a long-lived object holding a secret behind.
///
/// <para>This is the PERSISTED slot, not the session's login input. <c>Spotify.Session.cs</c> declares its own
/// <c>Spotify.Credential</c> (the same three fields, no Refresh) and READS THIS ONE DIRECTLY —
/// <c>Spotify.Session.LoadCredential</c> calls <c>Platform.TryLoadCredential</c> and casts the
/// kind across. There is no delegate seam to wire: the dependency runs one way, from Spotify to Platform, which is
/// the direction that works, because Platform boots first and must not reach into Spotify. The one field that does
/// not survive that map is <see cref="Refresh"/> — login5's refresh token, which Wave 2's login state does not model
/// yet and which 0.2.9 persisted but also never read back.</para></summary>
public readonly record struct Credential(CredentialKind Kind, string Username, string Secret, string? Refresh)
{
    public bool IsEmpty => Kind == CredentialKind.None || string.IsNullOrEmpty(Secret);
    /// <summary>Never print the secret. A credential in a log line is the credential in a bug report.</summary>
    public override string ToString() => "Credential(" + Kind + ", " + Platform.Redact(Username) + ", ***)";
}

/// <summary>At-rest encryption for the credential blob — a SWAPPABLE seam, never required. A blob carries its
/// protector's scheme tag, so a credential protected on one machine/platform is cleanly REJECTED (→ re-auth) rather
/// than mis-decrypted on another.</summary>
public interface ICredentialProtector
{
    string Scheme { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>The portable default — no OS keystore needed (the file lives in the user-only profile dir). The Windows
/// build swaps DPAPI in at boot; a test swaps this back in so nothing ever touches the real profile.</summary>
public sealed class NoOpProtector : ICredentialProtector
{
    public string Scheme => "none";
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] ciphertext) => ciphertext;
}

/// <summary>A portable persisted key/value store — 0.2.9's "localStorage" over `%LOCALAPPDATA%\Wavee\store.json`.
/// The credential blob and the launch-stable device id live in it. Injectable so a test runs against a temp file.</summary>
public interface ILocalStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}

// ── 6. the launch locale ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The immutable locale captured for ONE Wavee process: UI strings and Spotify metadata move together, on
/// restart. Launch-scoped by design — the Settings picker persists a new value and the next process applies it
/// atomically to UI strings, metadata requests and the locale-partitioned scope.</summary>
public readonly record struct AppLocale(string UiCulture, string SpotifyLanguage)
{
    public static readonly AppLocale English = new("en-US", "en");

    /// <summary>Resolve the launch locale from the persisted pick: "system" (the default) takes
    /// <paramref name="osCulture"/>, anything else is the stored BCP-47 tag. The OS culture is a PARAMETER, not a
    /// <c>CultureInfo.CurrentUICulture</c> read, because this app builds with <c>InvariantGlobalization</c> and that
    /// property is then always the invariant culture — 0.2.9 went to the OS through
    /// <c>GetUserDefaultLocaleName</c> for exactly this reason, and <c>Platform.Host.cs</c> supplies it. Pure, so the
    /// engine-side wiring (loading `assets/loc/*.json`, installing the culture provider, `SetCulture`) is owner S's
    /// `AppLocaleBootstrap` in Wave 6 reading the SAME answer, never computing a second one.</summary>
    public static AppLocale Resolve(IAppSettings settings, string? osCulture = null)
    {
        string selected = settings.Get(Platform.Keys.UiCulture);
        string culture = selected.Length == 0 || string.Equals(selected, "system", StringComparison.OrdinalIgnoreCase)
            ? osCulture ?? ""
            : selected;
        if (culture.Length == 0) culture = English.UiCulture;
        return new AppLocale(culture, LanguageOf(culture));
    }

    /// <summary>The two-letter language Spotify's metadata requests take, folded out of a culture tag. Anything that
    /// is not a clean two-letter ASCII language is "en", because English is what the UI will actually display.</summary>
    public static string LanguageOf(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "en";
        ReadOnlySpan<char> value = culture.AsSpan().Trim();
        int separator = value.IndexOfAny('-', '_');
        ReadOnlySpan<char> language = separator >= 0 ? value[..separator] : value;
        return language.Length == 2 && char.IsAsciiLetter(language[0]) && char.IsAsciiLetter(language[1])
            ? language.ToString().ToLowerInvariant()
            : "en";
    }
}

// ── 7. the log seam (CORE half: the types, the pure format, the ring, the facade) ─────────────────────────────────────

public enum WaveeLogLevel : byte { Trace, Debug, Info, Warning, Error, Critical }

/// <summary>One bounded key/value attached to a log event. Values are already rendered and safe to store.</summary>
public readonly record struct WaveeLogField(string Name, string Value)
{
    public static WaveeLogField Of(string name, string? value) => new(name, value ?? "");
    public static WaveeLogField Of(string name, int value) => new(name, value.ToString(CultureInfo.InvariantCulture));
    public static WaveeLogField Of(string name, long value) => new(name, value.ToString(CultureInfo.InvariantCulture));
    public static WaveeLogField Of(string name, double value) => new(name, value.ToString(CultureInfo.InvariantCulture));
    public static WaveeLogField Of(string name, bool value) => new(name, value ? "true" : "false");
    /// <summary>A field whose presence matters and whose value must never reach the file.</summary>
    public static WaveeLogField Secret(string name) => new(name, "***");

    public void AppendTo(StringBuilder sb)
    {
        sb.Append(Name).Append('=');
        AppendValue(sb, Value);
    }

    static void AppendValue(StringBuilder sb, string value)
    {
        bool quote = value.Length == 0;
        for (int i = 0; i < value.Length && !quote; i++)
        {
            char c = value[i];
            quote = char.IsWhiteSpace(c) || c is '"' or '=' or '|';
        }
        if (!quote) { sb.Append(value); return; }
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
    }
}

/// <summary>One structured log record, kept in the UI ring and written to the local file sink.</summary>
public readonly record struct WaveeLogEntry(
    long Sequence, long UnixMs, WaveeLogLevel Level, string Category, string EventId, string Message,
    string? OperationId, int ThreadId, long ElapsedMs, WaveeLogField[]? Fields, string? Exception)
{
    public int FieldCount => Fields?.Length ?? 0;

    /// <summary>The one line format, pure and testable:
    /// <c>I [connect] session.start op=x elapsed=12ms - started k=v</c>. The file sink prefixes it with the
    /// seq/tid/t/sid/pid tokens (<see cref="Log.FormatFileLine"/>); the diagnostics page re-reads both halves. Older
    /// builds omit the prefix tokens and the parser treats them as absent.</summary>
    public string Format()
    {
        char l = Level switch
        {
            WaveeLogLevel.Trace => 'T', WaveeLogLevel.Debug => 'D', WaveeLogLevel.Info => 'I',
            WaveeLogLevel.Warning => 'W', WaveeLogLevel.Error => 'E', _ => 'C',
        };
        var sb = new StringBuilder(96 + Message.Length);
        sb.Append(l).Append(" [").Append(Category).Append(']');
        if (EventId.Length > 0) sb.Append(' ').Append(EventId);
        if (OperationId is { Length: > 0 }) sb.Append(" op=").Append(OperationId);
        if (ElapsedMs >= 0) sb.Append(" elapsed=").Append(ElapsedMs).Append("ms");
        bool hasEventHeader = EventId.Length > 0 || OperationId is { Length: > 0 } || ElapsedMs >= 0;
        if (Message.Length > 0) sb.Append(hasEventHeader ? " - " : " ").Append(Message);
        if (Fields is { Length: > 0 } fields)
            for (int i = 0; i < fields.Length; i++) { sb.Append(' '); fields[i].AppendTo(sb); }
        if (Exception is { Length: > 0 } ex) sb.Append(" | ").Append(ex);
        return sb.ToString();
    }
}

/// <summary>THE always-on app log. The engine's `Diag` is debug-gated and compiled out in Release; this one is always
/// on, structured, ring-buffered for the in-app Diagnostics page and mirrored to an Info+ rolling file. Logging never
/// throws, never reflects and never depends on a third-party sink.
///
/// <para>Categories are 0.2.9's and stay: "app", "crash", "log", "engine", "connect", "playback", "spotify", "store",
/// "update", "sidebar", "notify". A category is a string because the set is open — a playback module names its own.</para>
///
/// <para>CORE half (this file): the ring, the levels, the pure line format. SHELL half (`Platform.Host.cs`): the file
/// sink, the daily/size rolling, retention, the engine `Diag` bridge and the flush-on-exit contract. When the shell
/// half is absent the partial hooks are erased and the ring still works — which is what lets a unit test log without
/// a file, and what keeps this type engine-free apart from those hooks.</para></summary>
public static partial class Log
{
    const int DefaultRingCapacity = 4096;

    /// <summary>8 lowercase hex chars identifying THIS process run — stamped on every file line (sid=) so sessions
    /// re-read from disk split by run without the fragile "Wavee starting" heuristic.</summary>
    public static readonly string SessionId = Guid.NewGuid().ToString("N")[..8];
    static readonly int ProcessId = Environment.ProcessId;

    // The one baseline every startup-timeline line reports. Anchored to the REAL process start where the OS will say,
    // so a line written before the first paint reports how long the USER has been waiting, not how long ago the
    // logger happened to be touched.
    static readonly long StartTicks = Environment.TickCount64 - ProcessUptimeMs();

    /// <summary>Milliseconds since this process started.</summary>
    public static long SinceStartMs => Environment.TickCount64 - StartTicks;

    static long ProcessUptimeMs()
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            long ms = (long)(DateTime.Now - self.StartTime).TotalMilliseconds;
            return ms is >= 0 and < 24L * 60 * 60 * 1000 ? ms : 0;   // a nonsense clock is no anchor at all
        }
        catch { return 0; }
    }

    static readonly Lock s_ringGate = new();
    static WaveeLogEntry[] s_ring = new WaveeLogEntry[DefaultRingCapacity];
    static int s_head, s_count;
    static long s_nextSequence, s_version;

    /// <summary>The master gate: an entry below this level is never built into a record at all.</summary>
    public static WaveeLogLevel MinLevel { get; set; } = WaveeLogLevel.Info;

    /// <summary>Upward-only FILE filter: a line reaches the file when its level is >= both this and
    /// <see cref="MinLevel"/>. Lowering it below MinLevel has no effect — MinLevel already dropped the entry.</summary>
    public static WaveeLogLevel FileMinLevel { get; set; } = WaveeLogLevel.Info;

    /// <summary>Bumped on every ring write, so the diagnostics page re-reads only when something moved.</summary>
    public static long Version { get { lock (s_ringGate) return s_version; } }

    public static bool IsEnabled(WaveeLogLevel level) => level >= MinLevel;

    public static void Trace(string category, string message) => Write(WaveeLogLevel.Trace, category, "", message, null, -1, null, null);
    public static void Debug(string category, string message) => Write(WaveeLogLevel.Debug, category, "", message, null, -1, null, null);
    public static void Info(string category, string message) => Write(WaveeLogLevel.Info, category, "", message, null, -1, null, null);
    public static void Warn(string category, string message, Exception? ex = null) => Write(WaveeLogLevel.Warning, category, "", message, null, -1, null, ex);
    public static void Error(string category, string message, Exception? ex = null) => Write(WaveeLogLevel.Error, category, "", message, null, -1, null, ex);
    public static void Critical(string category, string message, Exception? ex = null) => Write(WaveeLogLevel.Critical, category, "", message, null, -1, null, ex);

    /// <summary>A structured event. The field array materializes ONLY when the level passes, so a filtered call site
    /// costs a level compare (C# 14 params-span).</summary>
    public static void Event(WaveeLogLevel level, string category, string eventId, string message,
        string? operationId = null, long elapsedMs = -1, Exception? ex = null, params ReadOnlySpan<WaveeLogField> fields)
    {
        if (level < MinLevel) return;
        Write(level, category, eventId, message, operationId, elapsedMs, fields.Length == 0 ? null : fields.ToArray(), ex);
    }

    /// <summary>Snapshot of recent entries, oldest to newest — the diagnostics page's whole input.</summary>
    public static WaveeLogEntry[] Snapshot()
    {
        lock (s_ringGate) return SnapshotLocked();
    }

    public static void ClearRing()
    {
        lock (s_ringGate) { Array.Clear(s_ring); s_head = 0; s_count = 0; s_version++; }
    }

    static WaveeLogEntry[] SnapshotLocked()
    {
        var outp = new WaveeLogEntry[s_count];
        int start = (s_head - s_count + s_ring.Length) % s_ring.Length;
        for (int i = 0; i < s_count; i++) outp[i] = s_ring[(start + i) % s_ring.Length];
        return outp;
    }

    static void Write(WaveeLogLevel level, string category, string eventId, string message,
        string? operationId, long elapsedMs, WaveeLogField[]? fields, Exception? ex)
    {
        if (level < MinLevel) return;
        var entry = new WaveeLogEntry(
            Interlocked.Increment(ref s_nextSequence),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            level,
            string.IsNullOrWhiteSpace(category) ? "app" : category,
            eventId,
            message,
            operationId,
            Environment.CurrentManagedThreadId,
            elapsedMs,
            fields,
            ex?.ToString());
        PushRing(in entry);
        if (level >= FileMinLevel) FileWrite(entry);
    }

    static void PushRing(in WaveeLogEntry entry)
    {
        lock (s_ringGate)
        {
            s_ring[s_head] = entry;
            s_head = (s_head + 1) % s_ring.Length;
            if (s_count < s_ring.Length) s_count++;
            s_version++;
        }
    }

    /// <summary>A self-report ("log" category) pushed ring-only — never enqueued to the file, which is what stops a
    /// file-sink failure from recursing through the sink that failed.</summary>
    internal static void PushInternalRingOnly(WaveeLogLevel level, string message)
    {
        var entry = new WaveeLogEntry(Interlocked.Increment(ref s_nextSequence),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), level, "log", "", message, null,
            Environment.CurrentManagedThreadId, -1, null, null);
        PushRing(in entry);
    }

    /// <summary>The file-line prefix, carried before <see cref="WaveeLogEntry.Format"/>:
    /// <c>seq=17 tid=4 t=1720512345678 sid=3f9c2a1b pid=31544 I [connect] session.start - started</c>. t= (unix ms),
    /// sid= (per-run id) and pid= let the diagnostics page rebuild timestamps, session boundaries and the owning
    /// process when re-reading. Pure, so the format is pinned by a test instead of by reading a file back.</summary>
    public static string FormatFileLine(in WaveeLogEntry e)
        => "seq=" + e.Sequence.ToString(CultureInfo.InvariantCulture)
            + " tid=" + e.ThreadId.ToString(CultureInfo.InvariantCulture)
            + " t=" + e.UnixMs.ToString(CultureInfo.InvariantCulture)
            + " sid=" + SessionId
            + " pid=" + ProcessId.ToString(CultureInfo.InvariantCulture)
            + " " + e.Format();

    // The SHELL seams, implemented by Platform.Host.cs as parts of THIS partial class. An unimplemented partial
    // method is erased by the compiler, so this file — and every test over it — stands alone with a ring and no file.
    static partial void FileWrite(WaveeLogEntry entry);
}

// ── 8. glyphs (the font faces, set BEFORE the harness runs) ──────────────────────────────────────────────────────────

/// <summary>The custom WaveeIcons font — Spotify's real "Play next" / "Add to queue" marks the Segoe Fluent set does
/// not carry (the engine's generated Icons.* superset carries every other Wavee glyph; only these three custom-font
/// marks stay app-local). ASCII-safe <c>Of(0x____)</c> convention: built from hex codepoints at runtime so the SOURCE
/// stays pure-ASCII, because raw PUA chars and escapes get mangled by the edit/encoding chain.</summary>
public static class WaveeIcons
{
    static string Of(int cp) => ((char)cp).ToString();

    public static readonly string PlayNext = Of(0xE900);    // play-on-top mark (front of queue)
    public static readonly string PlayAfter = Of(0xE901);   // play-on-bottom / add-to-queue mark (end of queue)
    public static readonly string Lyrics = Of(0xE902);      // lyrics / chat bubble

    /// <summary>Absolute path + #family. The engine loads by PATH; the #suffix is a stable cache key only.</summary>
    public static readonly string Font =
        Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "wavee-icons.otf") + "#WaveeIcons";
}

/// <summary>The app's font FILES, resolved next to the exe (assets/fonts/*, shipped by the csproj's assets Content
/// glob and the MSIX layout's recursive copy of the publish dir). Every entry is the engine's "path#Family" form: the
/// renderer splits at '#', loads the PATH through DirectWrite's font-file reference and never hands the family name
/// to DirectWrite, so the "#Family" half is only the (family, weight) cache key.</summary>
public static class WaveeFonts
{
    /// <summary>Segoe Fluent Icons, BUNDLED. The SYSTEM family of the same name ships with Windows 11 only: on
    /// Windows 10 DirectWrite substitutes Segoe MDL2 Assets for the shared PUA range and every glyph ADDED in Fluent
    /// Icons (RefineSparkle U+F1D5, the "Tune" toolbar mark, RowSize, …) rendered as tofu. Loading by path removes
    /// the OS dependency outright.</summary>
    public static readonly string Icons =
        Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "SegoeFluentIcons.ttf") + "#Segoe Fluent Icons";

    /// <summary>The bundled icon file's absolute path (the half before '#'), for the startup log / diagnostics.</summary>
    public static string IconsPath => Icons[..Icons.IndexOf('#')];
}

/// <summary>Font registration. <see cref="Register"/> runs from `App.Main` BEFORE the harness — the engine interns
/// the icon face once when the host is constructed and every control reads it at render, so a later assignment is too
/// late and the whole Fluent-Icons era draws as tofu on Windows 10 (ch 00 §9.5). A MISSING file is not fatal: the
/// engine's face loader falls back to the body font, and <see cref="Platform.Boot"/> logs the path and whether it
/// exists, so a wrong-icons screenshot has its explanation in the log beside it. (The log cannot do it HERE: Register
/// runs before Boot configures the file sink.)</summary>
public static partial class Glyphs
{
    public static void Register() => RegisterHost();

    static partial void RegisterHost();
}

// ── 9. boot ──────────────────────────────────────────────────────────────────────────────────────────────────────────

public static partial class Platform
{
    // ── settings ────────────────────────────────────────────────────────────────────────────────────────────────────

    static IAppSettings? s_backing;
    static readonly Facade s_settings = new();

    /// <summary>The ONE door to persisted settings. Always non-null: with no backing store every read answers the
    /// key's default and every write is dropped (ch 30 §7.1), so a test harness never has to start a shell to read a
    /// preference.</summary>
    public static IAppSettings Settings => s_settings;

    /// <summary>Bumped by every write through <see cref="Settings"/>. The four cross-surface preference epochs
    /// (`Platform/Prefs.cs`, owner L, Wave 4) are built ON this and are deliberately NOT declared here, so that a
    /// preference and the epoch that announces it cannot drift into two owners' files.</summary>
    public static uint SettingsEpoch { get; private set; }

    /// <summary>The same number as a signal, for a binder that wants to re-read a preference reactively. Written on
    /// the UI thread only (C1), like every other signal in the app.</summary>
    public static readonly Signal<uint> SettingsChanged = new(0u);

    /// <summary>Install the backing store. Called once by <see cref="Boot"/> with the registry store; called by a
    /// test with an in-memory one, and with <c>null</c> to go back to defaults-only.</summary>
    public static void UseSettings(IAppSettings? backing) => s_backing = backing;

    sealed class Facade : IAppSettings
    {
        public T Get<T>(SettingKey<T> key) => s_backing is { } b ? b.Get(key) : key.Default;

        public void Set<T>(SettingKey<T> key, T value)
        {
            s_backing?.Set(key, value);
            SettingsEpoch++;
            SettingsChanged.Value = SettingsEpoch;
        }
    }

    // ── the credential slot ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The store key the credential blob lives under. PUBLIC so the fresh-install probe can ask "are there
    /// stored credentials?" without duplicating the literal.</summary>
    public const string CredentialKey = "spotify.credential";
    const string DeviceIdKey = "device.id";

    static ILocalStore? s_local;
    static ICredentialProtector s_protector = new NoOpProtector();
    static string? s_deviceId;

    /// <summary>Install the credential slot's store and at-rest protector. <see cref="Boot"/> passes the profile's
    /// `store.json` plus DPAPI on Windows; a test passes a temp file plus <see cref="NoOpProtector"/>, which is why
    /// no test ever has to be allowed near the real profile.</summary>
    public static void UseCredentialSlot(ILocalStore store, ICredentialProtector protector)
    {
        s_local = store;
        s_protector = protector;
        s_deviceId = null;
    }

    /// <summary>The at-rest protector's scheme tag ("dpapi" / "keyring" / "none"), for the log and the login result.</summary>
    public static string CredentialScheme => s_protector.Scheme;

    /// <summary>The persisted, launch-stable device id Spotify Connect publishes. Created on first use and never
    /// rotated: a new id every launch is a new phantom device in every other client's picker.</summary>
    public static string DeviceId => s_deviceId ??= s_local is { } store ? GetOrCreateDeviceId(store) : "";

    static string GetOrCreateDeviceId(ILocalStore store)
    {
        var id = store.Get(DeviceIdKey);
        if (string.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString("N"); store.Set(DeviceIdKey, id); }
        return id;
    }

    /// <summary>The stored credential for THIS machine/platform, or false — over the boot-time slot.</summary>
    public static bool TryLoadCredential(out Credential credential)
    {
        if (s_local is { } store) return TryLoadCredential(store, s_protector, out credential);
        credential = default;
        return false;
    }

    public static void SaveCredential(in Credential credential)
    {
        if (s_local is { } store) SaveCredential(store, s_protector, in credential);
    }

    /// <summary>Wipe the persisted credential (logout). The ONLY thing that may remove it — a read failure never
    /// does, because "I could not decode this" and "the user signed out" are different facts and only one of them
    /// should cost the user their session.</summary>
    public static void ClearCredential()
    {
        if (s_local is { } store) ClearCredential(store);
    }

    /// <summary>True iff a reusable credential for this machine/platform is on disk (scheme-matched). The silent
    /// resume uses it to resolve "no stored credential" to the Welcome card instead of an Error card.</summary>
    public static bool HasStoredCredential() => TryLoadCredential(out _);

    // The injectable half — pure over the two seams, so every rule below is driven directly by the tests.

    /// <summary>Decode the slot. The blob is <c>&lt;scheme&gt;:&lt;base64&gt;</c>; a DIFFERENT scheme means the
    /// profile moved machine or platform, which is a clean re-auth, not a decrypt attempt. Any failure answers "no
    /// credential" AND LEAVES THE BLOB ON DISK — a transient DPAPI failure (a roamed profile, a locked keystore)
    /// must not become a permanent logout.</summary>
    public static bool TryLoadCredential(ILocalStore store, ICredentialProtector protector, out Credential credential)
    {
        credential = default;
        var raw = store.Get(CredentialKey);
        if (string.IsNullOrEmpty(raw)) return false;
        int idx = raw.IndexOf(':');
        if (idx < 0 || raw[..idx] != protector.Scheme) return false;
        try
        {
            var json = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(raw[(idx + 1)..])));
            var dto = JsonSerializer.Deserialize(json, CredentialJson.Default.CredentialDto);
            if (dto is null || !Enum.TryParse<CredentialKind>(dto.Kind, out var kind)) return false;
            credential = new Credential(kind, dto.Username, dto.Secret, dto.Refresh);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Encode the slot, scheme-tagged. The on-disk shape is 0.2.9's byte for byte, so an existing profile
    /// resumes without a re-login.</summary>
    public static void SaveCredential(ILocalStore store, ICredentialProtector protector, in Credential credential)
    {
        var dto = new CredentialDto(credential.Kind.ToString(), credential.Username, credential.Secret, credential.Refresh);
        var json = JsonSerializer.Serialize(dto, CredentialJson.Default.CredentialDto);
        var blob = protector.Protect(Encoding.UTF8.GetBytes(json));
        store.Set(CredentialKey, protector.Scheme + ":" + Convert.ToBase64String(blob));
    }

    public static void ClearCredential(ILocalStore store) => store.Remove(CredentialKey);

    /// <summary>Redact an account identifier before it reaches a log: keep a short hint, hide the rest.</summary>
    public static string Redact(string? s)
        => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= 6 ? "***" : s[..3] + "***" + s[^2..];

    // ── the launch locale and the scope ─────────────────────────────────────────────────────────────────────────────

    /// <summary>This launch's locale. Resolved in <see cref="Boot"/>, immutable afterwards.</summary>
    public static AppLocale Locale { get; private set; } = AppLocale.English;

    /// <summary>The table set `Entities.Boot` opens (§3.5): the LAST LOGGED-IN ACCOUNT's scope when a credential is
    /// stored, otherwise the offline demo scope.
    ///
    /// <para>MARKET IS EMPTY AT BOOT, deliberately. 0.2.9 never persisted a country — it learned it from the login
    /// welcome — so there is nothing on disk to carry, and inventing an OS-region guess would move the scope (and
    /// every cold row keyed by it) for an expat on every launch. Wave 2's session calls `Entities.Switch` with the
    /// account's real market, tier and explicit filter the moment the welcome lands; until then the scope says only
    /// what is actually known. Tier 0 and AllowExplicit true are the same "not yet told" position.</para></summary>
    public static CatalogScope Scope { get; private set; } = CatalogScope.Fake();

    static CatalogScope ResolveScope()
    {
        string account = TryLoadCredential(out var credential) ? credential.Username : "";
        if (account.Length == 0) account = Settings.Get(Keys.LastAccount);
        return account.Length == 0
            ? CatalogScope.Fake(Locale.UiCulture)
            : new CatalogScope("spotify", account, Locale.UiCulture, "", Tier: 0, AllowExplicit: true);
    }

    // ── boot / shutdown ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything the app needs before there is a window. ORDER IS THE CONTRACT:
    /// <list type="number">
    /// <item>the settings store, because everything below reads it;</item>
    /// <item><see cref="ZoomAutoPolicy.MigrateMode"/>, BEFORE anything reads <c>appearance.zoom.mode</c> (ch 00 §9.5);</item>
    /// <item>the launch locale, because the scope is partitioned by it;</item>
    /// <item>the log — levels from settings, the file sink, the engine `Diag` bridge, and the startup line whose
    ///       <c>logResolved=</c> field is the only thing that tells a packaged run's redirected LocalCache from a real
    ///       <c>%LOCALAPPDATA%\Wavee</c> (a split settings/log store reads to a user as "the app forgot everything");</item>
    /// <item>the credential slot, because <see cref="Scope"/> asks it who the last account was.</item>
    /// </list>
    /// One-shot and allowed to allocate. Nothing here opens a socket, a database or a window.</summary>
    public static void Boot()
    {
        HostOpenSettings();
        ZoomAutoPolicy.MigrateMode(Settings);
        string osCulture = "";
        HostOsCulture(ref osCulture);
        Locale = AppLocale.Resolve(Settings, osCulture);
        HostOpenLog();
        HostOpenCredentials();
        Scope = ResolveScope();
        Log.Event(WaveeLogLevel.Info, "app", "startup", "platform booted", null, -1, null,
            WaveeLogField.Of("account", Redact(Scope.Account)),
            WaveeLogField.Of("provider", Scope.Provider),
            WaveeLogField.Of("locale", Locale.UiCulture),
            WaveeLogField.Of("spotifyLang", Locale.SpotifyLanguage),
            WaveeLogField.Of("credential", CredentialScheme),
            WaveeLogField.Of("iconFont", WaveeFonts.IconsPath),
            WaveeLogField.Of("iconFontExists", File.Exists(WaveeFonts.IconsPath)));
    }

    /// <summary>The exit path. The file sink writes on a background thread, so a bare process teardown right after the
    /// last log line tears it down before the tail — the line that matters most — ever reaches the file.</summary>
    public static void Shutdown()
    {
        Log.Info("app", "Wavee exiting");
        Log.Flush();
    }

    // The SHELL seams (Platform.Host.cs). Erased when that file is absent, so a unit test gets a Platform with
    // defaults-only settings, a ring-only log and no credential slot — and no file, registry or Win32 call at all.
    static partial void HostOpenSettings();
    static partial void HostOsCulture(ref string culture);
    static partial void HostOpenLog();
    static partial void HostOpenCredentials();
}

// The persisted credential DTO. The PROPERTY NAMES ARE THE WIRE (0.2.9 wrote them with the default naming policy);
// renaming one silently logs every existing install out.
internal sealed record CredentialDto(string Kind, string Username, string Secret, string? Refresh);

[JsonSerializable(typeof(CredentialDto))]
internal sealed partial class CredentialJson : JsonSerializerContext { }

// ── LEFT FOR OWNER S (Wave 6), deliberately not stubbed here ─────────────────────────────────────────────────────────
//   · NetworkPolicy (ch 29 §9.10, ~160) and the three composition sites that read it.
//   · The ambient power policy (~60) and its cadence block.
//   · `--fake` argument parsing and `Clock.SeedEpoch`; `Scope` gets its `--fake` arm THERE, not here.
//   · AppLocaleBootstrap's engine half: Localization.LoadFolder(assets/loc), OsCultureProvider, SetCulture — the PURE
//     resolution is above (AppLocale.Resolve) and owner S must read the same answer, not compute a second one.
//   · The rest of ch 28's DATA GAP D3 beside `RuntimePhase`: `RuntimeStatus`, `ProvisioningOutcome` and the pure
//     `ProgressFraction` / `ShortHash`.
//   · LogCapturePolicy (the -1-means-build-default fold) and WaveeLogSessions (re-reading the dated file set).
//   · The Win32 seams, the detached-window owner and the zoom/display bridges — all `Platform.Host.cs`.
