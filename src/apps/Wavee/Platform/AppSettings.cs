using System;
using FluentGpu.WindowsApi.Storage;

using Wavee.Core;

namespace Wavee;

// A statically-typed persisted-setting key: its storage name + the default returned when the key is absent. Type-safe —
// a key can only be read/written as its declared T, so call sites can't mismatch types or fat-finger a magic string.
public sealed record SettingKey<T>(string Name, T Default);

// The app's persisted settings, abstracted away from the concrete (Windows-registry) backing store: call sites depend
// only on this interface + the typed keys, so the store is trivially fakeable in a test or swappable per platform.
public interface IAppSettings
{
    T Get<T>(SettingKey<T> key);
    void Set<T>(SettingKey<T> key, T value);
}

// Every persisted setting lives here as one statically-typed key — the single registry of what the app remembers.
// Storage names are an internal detail of the keys; nothing else references the raw strings.
static class WaveeSettings
{
    // ── LEGACY (v0) global pane keys — READ ONLY BY THE v0→v1 MIGRATION (SidebarBootstrap.MigrateLegacyPaneKeys, F.3.3).
    // The pane state is now PER DESIGN and lives in SidebarKeys.Width/WidthUserSet/Collapsed. These three are deliberately
    // NOT deleted and are still written by nothing: a downgrade to an older build must still find a sane pane width.
    // The WidthUserSet contract carries over verbatim, now per design: while a design's WidthUserSet is false its width
    // follows that design's tier ladder; the first committed seam drag in that design latches it forever, for that design
    // only. Collapsing/expanding the pane is NOT a width choice and must never set it.
    public static readonly SettingKey<float> SidebarWidth = new("sidebar.width", 300f);
    public static readonly SettingKey<bool> SidebarWidthUserSet = new("sidebar.width.userSet", false);
    public static readonly SettingKey<bool> SidebarCollapsed = new("sidebar.collapsed", false);
    // The active sidebar DESIGN as a SidebarDesign int (the RowDensity/ThemeMode/TrackRowStyle convention —
    // AppDataSettings has no enum arm). DEFAULT 0 = Classic IS LOAD-BEARING: an existing install that never wrote the key
    // silently stays Classic. Fresh installs also get Classic written explicitly by SidebarBootstrap.
    public static readonly SettingKey<int> SidebarDesign = new("sidebar.design", 0);
    // The one-time design-chooser marker. SidebarBootstrap sets it true for EXISTING installs so they never see the
    // chooser; a fresh install leaves it false and every exit path of the chooser sets it true.
    public static readonly SettingKey<bool> SidebarOnboardingSeen = new("sidebar.onboarding.seen", false);
    // Monotonic "which sidebar startup migrations have run". 0 = never; current target 1. Guards the fresh-install probe
    // and the legacy-key migration so both run exactly once (IAppSettings has no key-exists probe — Get returns the
    // default for an absent key — so "never written" is otherwise indistinguishable from "written as the default").
    public static readonly SettingKey<int> SidebarBootstrapVersion = new("sidebar.bootstrap.version", 0);
    // ── First-run setup wizard (SetupBootstrap / SetupGating) ─────────────────────────────────────────────────────────
    // Armed for a fresh install by SetupBootstrap and shown by the shell once it has painted; every wizard exit path
    // (Continue through Done, "Not now", Escape, light-of-modal) clears it via SetupGating.MarkCompleted/MarkDeferred so
    // it can never reappear uninvited on a later launch — the same "one-time dialog that keeps coming back" failure mode
    // SidebarOnboardingSeen exists to prevent.
    public static readonly SettingKey<bool> SetupPending = new("setup.pending", false);
    // The user reached the wizard's Done page at least once. Distinct from SetupPending being false: a deferred wizard
    // ALSO clears Pending but leaves this false, so a later "you skipped setup" nudge (or Settings' own "Run setup
    // again" affordance) can still tell "finished" apart from "dismissed".
    public static readonly SettingKey<bool> SetupCompleted = new("setup.completed", false);
    // Monotonic "has SetupBootstrap run" guard — the SidebarBootstrapVersion precedent, for the same reason: IAppSettings
    // has no key-exists probe, so this is the only thing distinguishing "never written" from "written as the default",
    // which is exactly what makes a factory-reset profile (every key back at its default) re-arm the wizard automatically.
    public static readonly SettingKey<int> SetupBootstrapVersion = new("setup.bootstrap.version", 0);
    public static readonly SettingKey<bool> PlayerBarShowRemaining = new("playerbar.duration.remaining", true);
    // Theme preference: 0 = System (follow the OS live), 1 = Light, 2 = Dark. Default System so a fresh install matches
    // the OS; an explicit in-app toggle pins Light/Dark and stops following the OS. Seeded at startup before the first frame.
    public static readonly SettingKey<int> ThemeMode = new("theme.mode", 0);
    // UI culture selected in Settings. "system" asks the startup composition root to resolve the Windows UI locale;
    // an explicit BCP-47/language tag selects the matching bundled JSON table. Applied before first mount on next launch.
    public static readonly SettingKey<string> UiCulture = new("localization.culture", "system");
    public static readonly SettingKey<int> RowDensity = new("detail.rowdensity", 1);   // 0 Compact · 1 Default · 2 Cozy · 3 Comfortable
    // Track-table grammar/chrome: 0 Modern (art + stacked artist + rounded rows) · 1 Classic (separate Artist lane,
    // square hairline rows, no art). Stored as an int like RowDensity because AppDataSettings has no enum arm.
    public static readonly SettingKey<int> TrackRowStyle = new("detail.rowstyle", 0);
    // App-wide policy for artwork inside TRACK cells only. TRUE removes the thumbnail lane and returns its width to the
    // title; page heroes, media cards, sidebar covers and the player's identity artwork are deliberately unaffected.
    public static readonly SettingKey<bool> HideTrackArtwork = new("appearance.trackArtwork.hidden", false);
    // The Liked Songs collection cover treatment, as a LikedCoverStyle int (the ThemeMode / RowDensity / TrackRowStyle
    // convention — AppDataSettings has no enum arm). The enum's VALUES are the wire, so a treatment added later appends
    // and a stored int never re-means; anything this build does not define clamps to Stock (LikedCoverRules.FromSetting).
    // DEFAULT = Lens, not Stock: the point of the feature is that the collection cover is made of the user's own music,
    // and "replace outright" beats shipping the replacement switched off. It costs a fresh install nothing, because
    // LikedCoverRules.Effective degrades every treatment to the bundled PNG until the library actually owns enough
    // distinct artwork to feed it — so first launch still paints exactly what it painted before, with no setting write.
    public static readonly SettingKey<int> LikedCoverStyle = new("appearance.likedCover.style", (int)Wavee.LikedCoverStyle.Lens);
    // BPM · Key as its own track-list COLUMN. Off by default: tempo/key is enrichment most listeners never scan for, and
    // a permanent column costs width on every row. It is always available inside a row's expander, so this setting only
    // promotes it to a column for the users who do want to scan it (DJ-adjacent use). App-wide, like RowDensity.
    public static readonly SettingKey<bool> TempoColumn = new("detail.tempoColumn", false);
    // Stream counts as their own track-list COLUMN on the surfaces that do not already have one (playlists, Liked).
    // Off by default for the same reason as TempoColumn: it is enrichment most listeners never scan for, it costs width
    // on every row, and — unlike the album page, whose profile always shows the lane — a playlist's counts are not part
    // of what the page is FOR. VISIBILITY ONLY: kind 185 rides every list surface's trait bundle regardless (see
    // TraitPolicy), because gating the fetch on this setting permanently starved lists opened while it was off — 185
    // has no retry surface. App-wide, like RowDensity and TempoColumn.
    public static readonly SettingKey<bool> PlaysColumn = new("detail.playsColumn", false);
    // Marquee text (title/lyrics rows that overflow their box). TRUE (the default) = scroll the overflow; FALSE =
    // truncate with an ellipsis instead. Renamed from the negative DisableMarquee (no migration — pre-1.0, cosmetic):
    // Settings now presents this as a plain ON switch rather than a "disable" row.
    public static readonly SettingKey<bool> MarqueeEnabled = new("appearance.marquee.enabled", true);
    // Color washes (the shell/page surfaces tinted from the current artwork). TRUE (the default) = tint them; FALSE =
    // keep the neutral surface. Renamed from the negative DisableColorWashes for the same reason as MarqueeEnabled.
    public static readonly SettingKey<bool> ColorWashesEnabled = new("appearance.colorWashes.enabled", true);
    // App-wide UI zoom — the browser-style Ctrl+± ladder (the engine's ZoomLadder steps; effective window scale =
    // OS DPI × zoom). Seeded into AppOptions.Zoom BEFORE the window comes up (Program.cs) so the first frame already
    // paints at the user's scale, and SNAPPED on that read: a hand-edited registry value (or a value persisted by a
    // build with a different ladder) must never seed a non-ladder scale — off-rung zooms alias glyph-raster buckets,
    // which is the whole reason the ladder is discrete. Written back (debounced) by WaveeApp's zoom-save timer.
    public static readonly SettingKey<float> ZoomLevel = new("appearance.zoom", 1f);
    // The immersive lyrics surface's slowly-drifting blurred-cover backdrop. TRUE (the default) = the baked-blur cover
    // wanders on two incommensurate sinusoids; FALSE = the same cover, held perfectly still (and no ticker at all).
    // Deliberately a SETTING, not an env var — the ColorWashesEnabled precedent: it is a taste/comfort choice a
    // normal user makes about a surface they look at for whole songs, not a developer escape hatch, and it must apply
    // LIVE (the writer bumps AppearancePrefs.Epoch, which the surface reads). The OS reduced-motion preference
    // independently holds the drift still whatever this says — a value read, never a hook branch.
    public static readonly SettingKey<bool> LyricsAnimatedBackdrop = new("lyrics.backdrop.animated", true);
    // The lyrics SECOND line: 0 = none · 1 = translation · 2 = romanization. An int-enum (the ThemeMode / RowDensity /
    // TrackRowStyle convention — AppDataSettings has no enum arm), deliberately NOT two independent bools: the two
    // layers are mutually exclusive ON SCREEN (stacking both would add two lines to every row, blow the row heights out
    // and push the focal band off), and a tri-state int is the one value both writers agree on — the Settings picker and
    // the cycling toggle in the lyrics headers (rail + immersive). Default 0: most listeners read the original, and the
    // layers exist at all only when the winning lyric source happened to carry them (TTML ruby / ttm:role), which is
    // also why the header toggle is hidden unless the document on screen actually has one.
    public static readonly SettingKey<int> LyricsSecondaryLine = new("lyrics.secondary", 0);
    // Wide two-column detail pages: user-resizable left metadata rail. Album-like and playlist-like surfaces keep
    // separate widths because their authored defaults differ (280 vs 240 DIP). Responsive mid/narrow modes ignore these
    // values and retain their breakpoint widths; the saved width returns when the page is wide again.
    // The stored value is only ever CLAMPED at the seed (DetailShell) to the live grip bounds — a width persisted by an
    // older build with a different floor must never seed the layout raw.
    // One pair per DetailRailPolicy.RailScope: Liked and podcast shows are their own scopes (not the album fallback they
    // used to fall through to), so widening the Liked rail can never move every album page's rail with it.
    public static readonly SettingKey<float> DetailAlbumRailWidth = new("detail.rail.album.width", WaveeSize.RailAlbum);
    public static readonly SettingKey<float> DetailPlaylistRailWidth = new("detail.rail.playlist.width", WaveeSize.RailPlaylist);
    public static readonly SettingKey<float> DetailLikedRailWidth = new("detail.rail.liked.width", WaveeSize.RailPlaylist);
    public static readonly SettingKey<float> DetailShowRailWidth = new("detail.rail.show.width", WaveeSize.RailAlbum);
    // Shell right rail (lyrics / queue / now-playing). Seeded + clamped at WaveeShell; committed on splitter release.
    public static readonly SettingKey<float> ShellRailWidth = new("shell.rail.width", ShellResponsiveLayout.RailDefaultW);
    // Docked-video cap height in the right rail (Lyrics/Queue/Friends/Video). 0 = 16:9 of the live rail width (the
    // splitter only grows from there). Seeded + clamped at WaveeShell; committed on the vertical splitter's release.
    public static readonly SettingKey<float> ShellDockedVideoHeight = new("shell.rail.docked-video.height", 0f);
    // …and whether that rail is DRAGGED SHUT (the grip's force-push detent, WP-η). Separate from the width so re-opening
    // restores the width the user chose rather than a default. Collapse is a WIDE-layout (mode 0) preference only: the
    // responsive mid/narrow modes always compose their breakpoint rail, and the collapsed preference returns with the
    // wide layout — exactly the rule the widths above already follow. Written by the same drag-end commit as the width.
    public static readonly SettingKey<bool> DetailAlbumRailCollapsed = new("detail.rail.album.collapsed", false);
    public static readonly SettingKey<bool> DetailPlaylistRailCollapsed = new("detail.rail.playlist.collapsed", false);
    public static readonly SettingKey<bool> DetailLikedRailCollapsed = new("detail.rail.liked.collapsed", false);
    public static readonly SettingKey<bool> DetailShowRailCollapsed = new("detail.rail.show.collapsed", false);
    // ── Teaching tips (WaveeTips) — ONE key for EVERY tip, ever ───────────────────────────────────────────────────────
    // The set of ACKNOWLEDGED teaching-tip ids, newline-joined (the SavedLibrary precedent — AppDataStore round-trips
    // scalars only). A tip id lands in here the first time the user acknowledges that tip (its ✕, or invoking the thing it
    // points at), after which that tip never appears again on any launch. Deliberately a SET, not a key per tip: adding a
    // tip must not add a SettingKey (nor churn Wavee.Tests' settings shim), and "Show tips again" is one write of "".
    // Ids are PERSISTED — see WaveeTipIds (append-only, never renamed). Empty default = nothing acknowledged yet.
    public static readonly SettingKey<string> TipsSeen = new("tips.seen", "");
    // The saved / liked / followed library set (Mutations facet) — a newline-joined list of uris. The single in-process
    // outbox: every optimistic save/follow rewrites it. (A real source would reconcile server-side + revision conflicts.)
    public static readonly SettingKey<string> SavedLibrary = new("library.saved", "");
    // ── Add-to-playlist recency (PlaylistDepositTargets) ──────────────────────────────────────────────────────────────
    // The playlists most recently deposited into, newest first, newline-joined (the TipsSeen/SavedLibrary precedent),
    // capped at PlaylistDepositTargets.MaxRecent. This is what makes the "Add to playlist ▸" submenu's ten inline rows
    // the ten a user actually files into: rootlist order truncated to ten is the SAME ten forever for anyone with more
    // than ten playlists, very often not the one they are reaching for. A remembered uri is a preference, never an
    // assertion the playlist still exists — Order() skips any that no longer resolves. Empty = nothing filed yet.
    public static readonly SettingKey<string> PlaylistDepositRecents = new("playlist.deposit.recents", "");
    // The durable subset of the browser-style workspace: versioned JSON containing pinned route identities in visual
    // order plus the last selected pinned ordinal. Ordinary open tabs remain session-only.
    public static readonly SettingKey<string> WorkspacePinnedTabs = new("workspace.tabs.pinned", "");
    // ── the movable video surface: WHERE the user likes to watch, and where they put things. Deliberately NOT whether a
    // video is playing — a launch must never resume one on its own. See PlacementPersistence for the stored shapes.
    // Empty = never chosen ⇒ the surface's own default placement / its anchored home.
    public static readonly SettingKey<string> VideoPreferredPlacement = new("video.placement", "");
    public static readonly SettingKey<string> VideoPipRect = new("video.pip.rect", "");        // window-DIP "x,y,w,h"
    public static readonly SettingKey<string> VideoWindowRect = new("video.window.rect", "");  // screen-px "x,y,w,h"
    // One display policy for every track and presentation surface. Stable string names avoid coupling persisted data to
    // VideoAspectMode's numeric order; the ratio is retained while another mode is active so returning to Custom restores it.
    public static readonly SettingKey<string> VideoAspectMode = new("video.aspect.mode", "fit");
    public static readonly SettingKey<double> VideoCustomAspectRatio = new("video.aspect.customRatio", VideoAspectPersistence.DefaultCustomRatio);

    /// <summary>Keep the detached video window above other windows. TRUE by default — staying visible while you work in
    /// another app is the whole point of popping the video out — but it is a preference, not a law: an always-on-top
    /// window that will not get out of the way is the classic complaint about the pattern.</summary>
    public static readonly SettingKey<bool> VideoWindowAlwaysOnTop = new("video.window.ontop", true);

    // PlayPlay runtime pointer — empty string means unset (AppDataSettings cannot round-trip null strings).
    public static readonly SettingKey<string> PlaybackRuntimePath = new("playback.runtime.path", "");
    public static readonly SettingKey<string> PlaybackRuntimePackId = new("playback.runtime.packId", "");
    public static readonly SettingKey<bool> PlaybackRuntimeSetupDismissed = new("playback.runtime.dismissed", false);
    // Optional catalog-URL override (also settable via WAVEE_PLAYPLAY_CATALOG_URL). Empty = use the built-in default.
    public static readonly SettingKey<string> PlaybackRuntimeCatalogUrl = new("playback.runtime.catalogUrl", "");
    // Streaming quality preference (AudioQuality): 0 Normal 96 · 1 High 160 · 2 Very High 320 (3 Lossless is reserved —
    // shown disabled in the picker). Read per resolve, so a change applies from the next track (already-resolved tracks
    // keep their cached file selection).
    public static readonly SettingKey<int> PlaybackQuality = new("playback.quality", 2);
    // Cap applied WHEN the connection is metered (NetworkCostKind.Fixed / Variable). Same 0..2 ladder as PlaybackQuality.
    // Default 1 = High160 so a metered laptop does not silently stay on Very High.
    public static readonly SettingKey<int> MeteredQualityCap = new("playback.quality.meteredCap", 1);
    // Protected video quality: 0 = true Auto; otherwise the preferred representation height. Manual preferences are
    // pins and intentionally override viewport/metered caps. Auto is capped by the actual presentation height and, on a
    // metered connection, by VideoMeteredMaxHeight (0 there means unlimited).
    public static readonly SettingKey<int> VideoQuality = new("playback.video.quality", 0);
    public static readonly SettingKey<int> VideoMeteredMaxHeight = new("playback.video.quality.meteredMaxHeight", 480);
    // ── Notifications ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Two global gates + one LADDER per topic (Off / In-app / In-app + Windows — see Wavee.Core NotifyLevel).
    //
    // NotifyWindows is the single opt-in, per the calm contract: with it off the app behaves EXACTLY as it did before the
    // Notifications page existed (the in-app centre shows what it always showed, and Windows is never touched). Every
    // per-topic default is therefore a SHAPE for when the user opts in, not noise they have to go and switch off.
    public static readonly SettingKey<bool> NotifyWindows = new("notify.windows", false);
    // Play the Windows notification sound. Off => the toast is delivered <audio silent='true'/> — it still appears in the
    // Action Center, it just does not make a noise. Default on: a silent banner is a surprising default for a toast.
    public static readonly SettingKey<bool> NotifySound = new("notify.sound", true);
    // Quiet hours: no Windows banner inside [from, to) local hours (may wrap midnight). A LIVE toast inside the window is
    // suppressed (the centre still records it); a SCHEDULED one is shifted to the end of the window rather than dropped.
    public static readonly SettingKey<bool> NotifyQuietEnabled = new("notify.quiet.enabled", false);
    public static readonly SettingKey<int> NotifyQuietFromHour = new("notify.quiet.from", 22);
    public static readonly SettingKey<int> NotifyQuietToHour = new("notify.quiet.to", 8);
    // Per-topic levels. Keyed by the NotifyTopic name so the wire form stays readable and reordering the enum cannot
    // silently repoint a user's choice at a different topic. Defaults come from NotificationPolicy.DefaultFor.
    public static readonly SettingKey<int> NotifyNewAlbums = new("notify.topic.newAlbums", (int)NotifyLevel.InApp);
    public static readonly SettingKey<int> NotifyNewEpisodes = new("notify.topic.newEpisodes", (int)NotifyLevel.InApp);
    public static readonly SettingKey<int> NotifyReleaseDrops = new("notify.topic.releaseDrops", (int)NotifyLevel.Windows);
    public static readonly SettingKey<int> NotifyConcerts = new("notify.topic.concerts", (int)NotifyLevel.InApp);
    public static readonly SettingKey<int> NotifyFollowers = new("notify.topic.followers", (int)NotifyLevel.InApp);
    public static readonly SettingKey<int> NotifyDaylist = new("notify.topic.daylistRefresh", (int)NotifyLevel.InApp);
    public static readonly SettingKey<int> NotifyAppUpdates = new("notify.topic.appUpdates", (int)NotifyLevel.InApp);
    public static readonly SettingKey<int> NotifyLibraryActivity = new("notify.topic.libraryActivity", (int)NotifyLevel.InApp);
    // Watermark for LIVE toast escalation: the newest notification timestamp already raised as a banner. Without it a
    // rebuild (or a relaunch) would re-toast the whole feed — the loudest possible bug in a notification system.
    public static readonly SettingKey<long> NotifyLastToastedMs = new("notify.lastToastedMs", 0L);
    // Handle spotify: links with Wavee. OFF by default and deliberately so (the calm contract): silently taking the scheme
    // from an installed Spotify would break the user's muscle memory without being asked. Toggling it registers /
    // unregisters the HKCU scheme association at once, so the change is visible immediately rather than at next launch.
    public static readonly SettingKey<bool> HandleSpotifyLinks = new("app.protocol.spotify", false);
    // Volume persistence: when RememberVolume, SavedVolume (0..1) seeds the device volume at launch and is written back
    // (debounced) as the user adjusts it.
    public static readonly SettingKey<bool> RememberVolume = new("playback.volume.remember", true);
    public static readonly SettingKey<float> SavedVolume = new("playback.volume", 0.7f);
    // Output-device persistence (Phase A): the chosen WASAPI endpoint id (empty = system default) + its friendly name
    // (used in the reconnect toast while the device is absent). AppDataSettings cannot round-trip null → empty means unset.
    public static readonly SettingKey<string> OutputDeviceId = new("playback.output.deviceId", "");
    public static readonly SettingKey<string> OutputDeviceName = new("playback.output.deviceName", "");
    // Preferred render GPU (Settings › About picker). Same shape/rationale as the output-device pair above: the LUID
    // (0 = Automatic / the engine's HIGH_PERFORMANCE walk) is the fast path, but LUIDs are not stable across reboots,
    // so the adapter NAME is stored too and re-resolved to a live LUID at startup when the LUID no longer matches.
    public static readonly SettingKey<long> PreferredGpuLuid = new("gpu.preferredLuid", 0L);
    public static readonly SettingKey<string> PreferredGpuName = new("gpu.preferredName", "");
    public static readonly SettingKey<bool> EqualizerEnabled = new("playback.eq.enabled", false);
    public static readonly SettingKey<string> EqualizerPreset = new("playback.eq.preset", "flat");
    public static readonly SettingKey<string> EqualizerGains = new("playback.eq.gains", "0,0,0,0,0,0,0,0,0,0");
    public static readonly SettingKey<bool> CrossfadeEnabled = new("playback.crossfade.enabled", false);
    public static readonly SettingKey<int> CrossfadeMs = new("playback.crossfade.ms", 5000);
    public static readonly SettingKey<bool> AutoplayEnabled = new("playback.autoplay", true);
    public static readonly SettingKey<long> GaboGlobalSequence = new("telemetry.gabo.globalSequence", 0L);
    // On-disk playback caches (Phase 6): encrypted CDN bodies + DPAPI-wrapped PlayPlay license payloads.
    public static readonly SettingKey<bool> AudioBodyCacheEnabled = new("audio.cache.body.enabled", true);
    public static readonly SettingKey<bool> AudioKeyCacheEnabled = new("audio.cache.keys.enabled", true);
    // Body-cache capacity: 0=fixed bytes, 1=drive share (percent 0 means Auto), 2=unlimited.
    public static readonly SettingKey<int> AudioBodyCacheBudgetMode = new("audio.cache.body.budgetMode", 1);
    public static readonly SettingKey<long> AudioBodyCacheBudgetBytes = new("audio.cache.body.budgetBytes", 32L << 30);
    public static readonly SettingKey<int> AudioBodyCacheBudgetPercent = new("audio.cache.body.budgetPercent", 0);
    // Empty = AppData default. A custom value is the user-selected parent; Wavee owns its WaveeAudioCache child only.
    public static readonly SettingKey<string> AudioBodyCacheBasePath = new("audio.cache.body.basePath", "");
    // library.db metadata-cache budget (design §C.4/§G). The DB's `cache_budget_bytes` meta row is what the GC reads;
    // this key holds the user's CHOICE so it survives a database rebuild, and is replayed into the meta row at launch.
    public static readonly SettingKey<long> MetadataCacheBudgetBytes = new("cache.metadata.budgetBytes", 64L << 20);
    // Crash observability: the newest Windows Error Reporting dump we've already surfaced into wavee.log / Diagnostics.
    public static readonly SettingKey<string> LastSeenCrashDumpPath = new("diagnostics.crash.lastDumpPath", "");
    public static readonly SettingKey<long> LastSeenCrashDumpTicksUtc = new("diagnostics.crash.lastDumpTicksUtc", 0L);
    // Notification center: the unix-ms watermark past which a remote-feed item counts as "new" (advanced on panel open).
    // Local-only read-state for the gander + what's-new feeds (no server mark-read endpoint). Works on both backends.
    public static readonly SettingKey<long> NotificationsGanderLastSeenMs = new("notifications.gander.lastSeenMs", 0L);
    public static readonly SettingKey<long> NotificationsWhatsNewLastSeenMs = new("notifications.whatsnew.lastSeenMs", 0L);
    // The PER-ITEM half of that read state: newline-separated ids marked seen ONE AT A TIME (a Home timeline row's
    // click), applied on top of the watermarks by NotificationMerge. Bounded, and cleared whenever a watermark advance
    // subsumes it. Codec + cap: Wavee.Core's NotificationReadIds.
    public static readonly SettingKey<string> NotificationsReadIds = new("notifications.readIds", "");
    // Runtime log-level overrides for Settings › Logs › Verbose (WaveeLogLevel as int; -1 = build default — see
    // LogCapturePolicy.Resolve/ToSetting, the one place that reconciles this against the build's own default).
    public static readonly SettingKey<int> LogMinLevel = new("diagnostics.log.minLevel", -1);
    public static readonly SettingKey<int> LogFileMinLevel = new("diagnostics.log.fileMinLevel", -1);
    // ── Release-readiness keys (2026-08-26) ──────────────────────────────────────────────────────────────
    // Developer mode gates every developer-only surface (API console, lyrics inspector, test notifications, FPS HUD,
    // home image diagnostics). Off by default; Settings › Diagnostics. See App/DeveloperMode.cs.
    public static readonly SettingKey<bool> DeveloperMode = new("diag.developerMode", false);
    public static readonly SettingKey<bool> FpsOverlay = new("diag.fpsOverlay", false);
    // Dealer WebSocket frame archive (Diagnostics/DealerArchive.cs). Off by default — it is a debugging capture.
    public static readonly SettingKey<bool> DealerArchiveEnabled = new("diag.dealerArchive", false);
    // The terms version the user accepted in the setup wizard (0 = never). A bump in SetupTermsPage.CurrentVersion
    // re-arms the Terms page on the next launch.
    public static readonly SettingKey<int> TermsAcceptedVersion = new("setup.terms.acceptedVersion", 0);
    // Spotify Connect private session (published on the device state; profile-menu toggle).
    public static readonly SettingKey<bool> PrivateSession = new("session.private", false);
    // The normalized username of the last account that went live — a change resets the local library projection.
    public static readonly SettingKey<string> LastAccount = new("session.lastAccount", "");
    // Loudness normalization on/off (the per-track gain is applied when true; applies from the next track start).
    public static readonly SettingKey<bool> NormalizationEnabled = new("playback.normalization", true);
    // Start Wavee at Windows sign-in (unpackaged: HKCU Run value; packaged: manifest StartupTask).
    public static readonly SettingKey<bool> StartOnLogin = new("app.startOnLogin", false);
    // App-update bookkeeping (App/AppInstallerUpdateService.cs): the version that last ran (→ "updated to" notice)
    // and the last successful feed check.
    public static readonly SettingKey<string> LastRunVersion = new("app.lastRunVersion", "");
    public static readonly SettingKey<long> UpdateLastCheckedMs = new("app.update.lastCheckedMs", 0L);
    // The quad the user pressed "Later" on. Cleared by a successful apply; a NEWER feed version is never snoozed by it.
    public static readonly SettingKey<string> UpdateSnoozedVersion = new("app.update.snoozedVersion", "");
    // On: the orderly-shutdown path (Program.Main, after the app loop returns) downloads and stages a waiting update
    // as Wavee closes. Off (the default) means updates apply on the next launch, which is what the OS does anyway.
    public static readonly SettingKey<bool> UpdateInstallOnQuit = new("app.update.installOnQuit", false);
    // Off: an in-app "Update now" on a metered link refuses and says it is waiting for an unmetered network.
    public static readonly SettingKey<bool> UpdateOnMetered = new("app.update.onMetered", false);
    // Show the "What's new" plate the first time a new version opens.
    public static readonly SettingKey<bool> ReleaseNotesAutoShow = new("app.whatsnew.autoShow", true);
    // The newest release-notes semver the user has actually looked at (drives the "unread" dot on About/links).
    public static readonly SettingKey<string> ReleaseNotesLastSeen = new("app.whatsnew.lastSeenVersion", "");
    // Set by AppInstallerUpdateService's ctor when this launch followed an update (the version that ran BEFORE);
    // AfterUpdateDialog reads it once and clears it.
    public static readonly SettingKey<string> ReleaseNotesPendingFrom = new("app.whatsnew.pendingFrom", "");
    // The same from-version as a durable FACT: written beside pendingFrom by the updater's ctor and NEVER cleared.
    // pendingFrom is a one-shot the plate consumes; Settings › About's "Show the update summary again" needs the
    // from-quad long after that, so it reads this instead (falling back to the running quad on a never-updated install).
    public static readonly SettingKey<string> ReleaseNotesPreviousVersion = new("app.whatsnew.previousVersion", "");
    // A crash report written by the previous run that the shell has not yet surfaced (cleared after the toast).
    public static readonly SettingKey<string> PendingCrashReport = new("crash.pendingReport", "");
    // "Don't ask again after a crash" — checked in the crash prompt dialog; suppresses the modal in favor of a sticky toast.
    public static readonly SettingKey<bool> CrashPromptOptOut = new("crash.promptOptOut", false);
    // RunMarker.Begin/End bracket every launch ("running" → "clean"); a stale "running" seen on the next Begin means
    // the previous process exited without running managed shutdown (crash, kill, or an OS-forced termination).
    public static readonly SettingKey<string> RunMarker = new("app.runMarker", "");
    // True once an UNCLEAN-EXIT crash prompt (the evidence-free signal: no report, no dump — just a stale "running"
    // marker) has been offered; cleared by RunMarker.End / MarkCrashed. Bounds that prompt to ONCE PER UNCLEAN STREAK:
    // a process that is killed on every run (an IDE stop, Task Manager) is otherwise "unclean" every single launch
    // and re-asks after every dismissal (CrashPromptPolicy.Decide).
    public static readonly SettingKey<bool> UncleanExitOffered = new("crash.uncleanExitOffered", false);
}

// The LibraryPage's per-kind persisted state (the "Your Library" master–detail: albums/artists/podcasts). Keys are built
// per kind at runtime — plain record construction, AOT-clean — so the three kinds keep independent last-used state, and
// each key carries its own default so a missing key (or no store) degrades to it. Scope is per-page-global: multiple open
// tabs of one kind stay independent while live, then seed from the same saved values on a fresh launch. Filter text is
// intentionally NOT persisted (it starts empty each launch), so there is no key for it here.
static class LibraryStateKeys
{
    public static SettingKey<float> LeftW(string k) => new($"library.{k}.leftw", k == "artists" ? 280f : 340f);
    public static SettingKey<float> MidW(string k) => new($"library.{k}.midw", 440f);
    public static SettingKey<int> Sort(string k) => new($"library.{k}.sort", 0);
    public static SettingKey<bool> Desc(string k) => new($"library.{k}.desc", false);
    public static SettingKey<int> View(string k) => new($"library.{k}.view", 1);
    public static SettingKey<int> Size(string k) => new($"library.{k}.size", 1);
    public static SettingKey<string> Selected(string k) => new($"library.{k}.selected", "");
    // Artists-only: the discography (column 2) controls + the picked release (column 3).
    public static SettingKey<string> AlbumKey(string k) => new($"library.{k}.albumkey", "");
    public static SettingKey<int> AlbumSort(string k) => new($"library.{k}.album.sort", 0);
    public static SettingKey<bool> AlbumDesc(string k) => new($"library.{k}.album.desc", false);
    public static SettingKey<int> AlbumView(string k) => new($"library.{k}.album.view", 3);   // Grid — matches today's fixed grid
    public static SettingKey<int> AlbumSize(string k) => new($"library.{k}.album.size", 1);
}

// The per-design persisted sidebar state (F.3.1). Keys are built per design SLUG at runtime (the LibraryStateKeys pattern)
// — plain record construction, AOT-clean — so the three designs keep fully independent last-used state and switching
// designs is a snapshot/restore over these keys. SidebarDesignInfo.Slug is the single source of truth for the slug, and
// SLUGS ARE PERSISTED: never rename them.
//
// What deliberately does NOT live here: Curated section collapse (per user-defined section ⇒ sections[].collapsed in
// sidebar-layout.json, there is no fixed key set for user-created sections), V3 folder expansion (an unbounded id set,
// same document), and the V3 filter TEXT (session-only, starts empty each launch — the LibraryStateKeys precedent).
static class SidebarKeys
{
    // ── pane (per design) ──
    public static SettingKey<float> Width(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.width", SidebarDesignInfo.Tiers(d).Narrow);
    public static SettingKey<bool> WidthUserSet(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.width.userSet", false);
    public static SettingKey<bool> Collapsed(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.collapsed", false);

    // ── Classic section expansion ──
    public static readonly SettingKey<bool> ClassicPinnedOpen = new("sidebar.classic.section.pinned", true);
    public static readonly SettingKey<bool> ClassicLibraryOpen = new("sidebar.classic.section.library", true);
    public static readonly SettingKey<bool> ClassicPlaylistsOpen = new("sidebar.classic.section.playlists", true);

    // ── Library V3 view state ──
    public static readonly SettingKey<int> V3Filter = new("sidebar.v3.filter", 0);        // SidebarV3Filter
    public static readonly SettingKey<int> V3Qualifier = new("sidebar.v3.qualifier", 0);  // SidebarV3Qualifier
    public static readonly SettingKey<int> V3Sort = new("sidebar.v3.sort", 0);            // SidebarV3Sort (Recents)
    // Ignored while V3Sort == Custom (the direction affordance is hidden); the stored value is PRESERVED so returning to
    // another sort restores it.
    public static readonly SettingKey<bool> V3Desc = new("sidebar.v3.desc", false);
    public static readonly SettingKey<int> V3View = new("sidebar.v3.view", 1);            // SidebarV3View (List)
    public static readonly SettingKey<int> V3GridSize = new("sidebar.v3.size", 1);        // 0 S · 1 M · 2 L
    public static readonly SettingKey<bool> V3SearchOpen = new("sidebar.v3.search.open", false);

    // ── Curated ──
    public static readonly SettingKey<string> CuratedTemplateId = new("sidebar.curated.template", "wavee.curated.default");
    public static readonly SettingKey<bool> CuratedRailLabels = new("sidebar.curated.rail.labels", false);
}

// IAppSettings backed by the engine's AppDataStore (HKCU registry, unpackaged). Every access is DEFENSIVE — a storage
// failure (or no store at all) falls back to the key's default and never throws into the UI. Type dispatch is a closed
// switch over AppDataStore's supported scalars — AOT-clean (no reflection); unsupported T's fall back to the default.
sealed class AppDataSettings : IAppSettings
{
    readonly AppDataStore? _store;
    AppDataSettings(AppDataStore? store) => _store = store;

    public static IAppSettings ForUnpackaged(string publisher, string product)
    {
        try { return new AppDataSettings(AppDataStore.ForUnpackaged(publisher, product)); }
        catch { return new AppDataSettings(null); }   // storage unavailable → reads return defaults, writes no-op
    }

    public T Get<T>(SettingKey<T> key)
    {
        if (_store is null) return key.Default;
        try
        {
            object boxed = key.Default switch
            {
                float f  => (float)_store.GetDouble(key.Name, f),
                double d => _store.GetDouble(key.Name, d),
                bool b   => _store.GetBool(key.Name, b),
                int i    => _store.GetInt(key.Name, i),
                long l   => _store.GetLong(key.Name, l),
                string s => _store.GetString(key.Name, s) ?? s,
                _        => key.Default!,
            };
            return (T)boxed;
        }
        catch { return key.Default; }
    }

    public void Set<T>(SettingKey<T> key, T value)
    {
        if (_store is null || value is null) return;
        try
        {
            switch (value)
            {
                case float f:  _store.SetDouble(key.Name, f); break;
                case double d: _store.SetDouble(key.Name, d); break;
                case bool b:   _store.SetBool(key.Name, b); break;
                case int i:    _store.SetInt(key.Name, i); break;
                case long l:   _store.SetLong(key.Name, l); break;
                case string s: _store.SetString(key.Name, s); break;
            }
        }
        catch { }
    }
}
