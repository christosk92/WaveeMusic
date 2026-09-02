using System.Collections.Generic;

namespace Wavee;

// Minimal settings seam for source-included Runtime tests (the full AppSettings.cs pulls FluentGpu.WindowsApi).
public interface IAppSettings
{
    T Get<T>(SettingKey<T> key);
    void Set<T>(SettingKey<T> key, T value);
}

public sealed record SettingKey<T>(string Name, T Default);

static class WaveeSettings
{
    public static readonly SettingKey<string> PlaybackRuntimePath = new("playback.runtime.path", "");
    public static readonly SettingKey<string> PlaybackRuntimePackId = new("playback.runtime.packId", "");
    public static readonly SettingKey<bool> PlaybackRuntimeSetupDismissed = new("playback.runtime.dismissed", false);
    public static readonly SettingKey<string> PlaybackRuntimeCatalogUrl = new("playback.runtime.catalogUrl", "");
    public static readonly SettingKey<bool> AudioBodyCacheEnabled = new("audio.cache.body.enabled", true);
    public static readonly SettingKey<bool> AudioKeyCacheEnabled = new("audio.cache.keys.enabled", true);
    public static readonly SettingKey<int> AudioBodyCacheBudgetMode = new("audio.cache.body.budgetMode", 1);
    public static readonly SettingKey<long> AudioBodyCacheBudgetBytes = new("audio.cache.body.budgetBytes", 32L << 30);
    public static readonly SettingKey<int> AudioBodyCacheBudgetPercent = new("audio.cache.body.budgetPercent", 0);
    public static readonly SettingKey<string> AudioBodyCacheBasePath = new("audio.cache.body.basePath", "");
    // ── diagnostics — MIRRORS src/apps/Wavee/Platform/AppSettings.cs VERBATIM (same rule as the sidebar keys below).
    // DeveloperMode is read by the source-included App\DeveloperMode.cs; the default MUST stay false.
    public static readonly SettingKey<bool> DeveloperMode = new("diag.developerMode", false);
    public static readonly SettingKey<bool> FpsOverlay = new("diag.fpsOverlay", false);
    public static readonly SettingKey<bool> DealerArchiveEnabled = new("diag.dealerArchive", false);
    // Settings › Logs (LogCapturePolicy) — MIRRORS src/apps/Wavee/Platform/AppSettings.cs VERBATIM, same rule as the
    // sidebar keys below: LogCapturePolicyTests assert against these exact names and the -1 = build-default meaning.
    public static readonly SettingKey<int> LogMinLevel = new("diagnostics.log.minLevel", -1);
    public static readonly SettingKey<int> LogFileMinLevel = new("diagnostics.log.fileMinLevel", -1);
    public static readonly SettingKey<int> TermsAcceptedVersion = new("setup.terms.acceptedVersion", 0);
    public static readonly SettingKey<bool> PrivateSession = new("session.private", false);
    public static readonly SettingKey<string> LastAccount = new("session.lastAccount", "");
    public static readonly SettingKey<bool> NormalizationEnabled = new("playback.normalization", true);
    // ── equalizer / crossfade (App\EqualizerSettings.cs, App\PlaybackDsp.cs) — MIRRORS src/apps/Wavee/Platform/AppSettings.cs
    // VERBATIM, same rule as the sidebar keys below: EqualizerSettingsTests assert against these exact names/defaults.
    public static readonly SettingKey<bool> EqualizerEnabled = new("playback.eq.enabled", false);
    public static readonly SettingKey<string> EqualizerGains = new("playback.eq.gains", "0,0,0,0,0,0,0,0,0,0");
    public static readonly SettingKey<bool> CrossfadeEnabled = new("playback.crossfade.enabled", false);
    public static readonly SettingKey<int> CrossfadeMs = new("playback.crossfade.ms", 5000);
    public static readonly SettingKey<bool> StartOnLogin = new("app.startOnLogin", false);
    public static readonly SettingKey<string> LastRunVersion = new("app.lastRunVersion", "");
    public static readonly SettingKey<long> UpdateLastCheckedMs = new("app.update.lastCheckedMs", 0L);
    // ── app update + release notes — MIRRORS src/apps/Wavee/Platform/AppSettings.cs VERBATIM (same rule as the sidebar
    // keys below): AppInstallerUpdateServiceTests assert against these exact names and defaults.
    public static readonly SettingKey<string> UpdateSnoozedVersion = new("app.update.snoozedVersion", "");
    public static readonly SettingKey<bool> UpdateInstallOnQuit = new("app.update.installOnQuit", false);
    public static readonly SettingKey<bool> UpdateOnMetered = new("app.update.onMetered", false);
    public static readonly SettingKey<bool> ReleaseNotesAutoShow = new("app.whatsnew.autoShow", true);
    public static readonly SettingKey<string> ReleaseNotesLastSeen = new("app.whatsnew.lastSeenVersion", "");
    public static readonly SettingKey<string> ReleaseNotesPendingFrom = new("app.whatsnew.pendingFrom", "");
    public static readonly SettingKey<string> ReleaseNotesPreviousVersion = new("app.whatsnew.previousVersion", "");
    public static readonly SettingKey<string> PendingCrashReport = new("crash.pendingReport", "");
    public static readonly SettingKey<bool> CrashPromptOptOut = new("crash.promptOptOut", false);
    public static readonly SettingKey<string> RunMarker = new("app.runMarker", "");
    public static readonly SettingKey<bool> UncleanExitOffered = new("crash.uncleanExitOffered", false);
    public static readonly SettingKey<string> LastSeenCrashDumpPath = new("diagnostics.crash.lastDumpPath", "");
    public static readonly SettingKey<long> LastSeenCrashDumpTicksUtc = new("diagnostics.crash.lastDumpTicksUtc", 0L);

    // ── sidebar (F.3.1) — MIRRORS src/apps/Wavee/Platform/AppSettings.cs VERBATIM ─────────────────────────────────────
    // Storage names and defaults must match the production keys exactly: the sidebar tests assert against these names and
    // the bootstrap/preferences code under test reads them by name. If you change one there, change it here.
    // Legacy v0 global pane keys — read only by the v0→v1 migration (SidebarBootstrap.MigrateLegacyPaneKeys).
    public static readonly SettingKey<float> SidebarWidth = new("sidebar.width", 300f);
    public static readonly SettingKey<bool> SidebarWidthUserSet = new("sidebar.width.userSet", false);
    public static readonly SettingKey<bool> SidebarCollapsed = new("sidebar.collapsed", false);
    public static readonly SettingKey<int> SidebarDesign = new("sidebar.design", 0);
    public static readonly SettingKey<bool> SidebarOnboardingSeen = new("sidebar.onboarding.seen", false);
    public static readonly SettingKey<int> SidebarBootstrapVersion = new("sidebar.bootstrap.version", 0);
    // ── first-run setup wizard (F. setup) — MIRRORS src/apps/Wavee/Platform/AppSettings.cs VERBATIM, same rule as the
    // sidebar keys above: SetupGatingTests/SetupBootstrap tests assert against these exact names.
    public static readonly SettingKey<bool> SetupPending = new("setup.pending", false);
    public static readonly SettingKey<bool> SetupCompleted = new("setup.completed", false);
    public static readonly SettingKey<int> SetupBootstrapVersion = new("setup.bootstrap.version", 0);
    public static readonly SettingKey<string> VideoAspectMode = new("video.aspect.mode", "fit");
    public static readonly SettingKey<double> VideoCustomAspectRatio = new("video.aspect.customRatio", VideoAspectPersistence.DefaultCustomRatio);
    // ── appearance — MIRRORS src/apps/Wavee/Platform/AppSettings.cs VERBATIM (same rule as the sidebar keys above).
    public static readonly SettingKey<float> ZoomLevel = new("appearance.zoom", 1f);
}

// The per-design sidebar keys (F.3.1), mirroring the production SidebarKeys. Depends on SidebarDesignInfo.Slug/Tiers —
// so Features/Sidebar/SidebarDesign.cs must be source-included by Wavee.Tests.csproj alongside these tests.
static class SidebarKeys
{
    public static SettingKey<float> Width(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.width", SidebarDesignInfo.Tiers(d).Narrow);
    public static SettingKey<bool> WidthUserSet(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.width.userSet", false);
    public static SettingKey<bool> Collapsed(SidebarDesign d)
        => new($"sidebar.{SidebarDesignInfo.Slug(d)}.collapsed", false);

    public static readonly SettingKey<bool> ClassicPinnedOpen = new("sidebar.classic.section.pinned", true);
    public static readonly SettingKey<bool> ClassicLibraryOpen = new("sidebar.classic.section.library", true);
    public static readonly SettingKey<bool> ClassicPlaylistsOpen = new("sidebar.classic.section.playlists", true);

    public static readonly SettingKey<int> V3Filter = new("sidebar.v3.filter", 0);
    public static readonly SettingKey<int> V3Qualifier = new("sidebar.v3.qualifier", 0);
    public static readonly SettingKey<int> V3Sort = new("sidebar.v3.sort", 0);
    public static readonly SettingKey<bool> V3Desc = new("sidebar.v3.desc", false);
    public static readonly SettingKey<int> V3View = new("sidebar.v3.view", 1);
    public static readonly SettingKey<int> V3GridSize = new("sidebar.v3.size", 1);
    public static readonly SettingKey<bool> V3SearchOpen = new("sidebar.v3.search.open", false);

    public static readonly SettingKey<string> CuratedTemplateId = new("sidebar.curated.template", "wavee.curated.default");
    public static readonly SettingKey<bool> CuratedRailLabels = new("sidebar.curated.rail.labels", false);
}

/// <summary>An in-memory <see cref="IAppSettings"/> for tests: no registry, no file, no defaults magic beyond the key's
/// own. Shared by every sidebar test (bootstrap, preferences, settings-page models) so they all agree on the seam.</summary>
public sealed class MemoryAppSettings : IAppSettings
{
    readonly Dictionary<string, object> _values = new();

    public T Get<T>(SettingKey<T> key) =>
        _values.TryGetValue(key.Name, out var value) && value is T typed ? typed : key.Default;

    public void Set<T>(SettingKey<T> key, T value) { if (value is not null) _values[key.Name] = value; }

    /// <summary>True when the key has been WRITTEN (the real IAppSettings has no such probe — tests use it to assert that
    /// a code path deliberately did NOT write a key, e.g. "an existing install must not stomp sidebar.design").</summary>
    public bool WasWritten<T>(SettingKey<T> key) => _values.ContainsKey(key.Name);

    /// <summary>Number of distinct keys written — for "the bootstrap is idempotent" style assertions.</summary>
    public int WrittenCount => _values.Count;
}
