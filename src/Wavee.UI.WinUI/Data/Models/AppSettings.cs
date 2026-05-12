using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Data.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Default";
    public string Language { get; set; } = "system";
    public string SpotifyMetadataLanguage { get; set; } = "app";
    public double SidebarWidth { get; set; } = 280;
    public double RightPanelWidth { get; set; } = 300;
    public Dictionary<string, double> PanelWidths { get; set; } = new();
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool IsWindowMaximized { get; set; }
    public double Volume { get; set; } = 1.0;
    public bool IsShuffle { get; set; }
    public string RepeatMode { get; set; } = "Off";
    public string? LastOpenedTab { get; set; }
    public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = new();
    public HomeSectionSettings HomeSettings { get; set; } = new();
    public ShellSessionState ShellSession { get; set; } = new();
    public bool AskBeforeClosingTabs { get; set; } = true;
    public CloseTabsBehavior CloseTabsBehavior { get; set; } = CloseTabsBehavior.Save;

    // ── Playback behavior ──

    /// <summary>
    /// How a track click triggers playback: "SingleTap" or "DoubleTap".
    /// </summary>
    public string TrackClickBehavior { get; set; } = "DoubleTap";

    /// <summary>
    /// Default action when playing a track: "PlayAndClear", "PlayNext", or "PlayLater".
    /// </summary>
    public string DefaultPlayAction { get; set; } = "PlayAndClear";

    /// <summary>
    /// Whether to show the play action dialog every time a track is played.
    /// </summary>
    public bool AskPlayAction { get; set; } = true;

    /// <summary>
    /// True once the user has seen the first-time play behavior setup.
    /// </summary>
    public bool PlayBehaviorConfigured { get; set; }

    /// <summary>
    /// Audio processing preset: "None" or "Radio".
    /// </summary>
    public string AudioPreset { get; set; } = "None";

    /// <summary>
    /// Audio streaming quality: "Normal" (96kbps), "High" (160kbps), or "VeryHigh" (320kbps).
    /// </summary>
    public string AudioQuality { get; set; } = "VeryHigh";

    /// <summary>
    /// Whether to normalize audio volume across tracks.
    /// </summary>
    public bool NormalizationEnabled { get; set; } = true;

    /// <summary>
    /// When true, playback continues with Spotify-recommended similar tracks
    /// once the current context (album, playlist, artist discography) ends.
    /// Default true — matches Spotify's own account default. When false, the
    /// user hits end-of-context and sees a "Reached the end" notification.
    /// </summary>
    public bool AutoplayEnabled { get; set; } = true;

    /// <summary>
    /// When true, popping the player out into a second window does not hide
    /// the currently selected docked player surface (bottom bar or sidebar).
    /// </summary>
    public bool ShowDockedPlayerWithFloatingPlayer { get; set; }

    /// <summary>
    /// When true, returning focus to the main window while a video is playing
    /// can ask whether to show that video in the in-window mini player.
    /// </summary>
    public bool AskToShowVideoMiniPlayerOnFocus { get; set; } = true;

    public bool ShowLocalFilesOnHome { get; set; } = true;

    // ── Cache (applied on next launch) ──

    /// <summary>
    /// Whether audio caching is enabled.
    /// </summary>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>
    /// Maximum cache size in bytes. Default 1 GB.
    /// </summary>
    public long CacheSizeLimitBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// In-memory caching aggressiveness profile. Controls LRU and hot-cache
    /// capacities across the app. Medium matches the pre-profile defaults.
    /// Applied at DI construction time — changes take effect on next launch.
    /// </summary>
    public CachingProfile CachingProfile { get; set; } = CachingProfile.Medium;

    // ── Connection (applied on next launch) ──

    /// <summary>
    /// Whether to automatically reconnect on connection failure.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    // ── UI zoom ──

    /// <summary>
    /// App-wide zoom level (1.0 = 100%). Range: 0.5 – 2.0.
    /// </summary>
    public double ZoomLevel { get; set; } = 1.0;

    // ── Equalizer ──

    /// <summary>
    /// Whether the user EQ is enabled.
    /// </summary>
    public bool EqualizerEnabled { get; set; }

    /// <summary>
    /// Active EQ preset name: "Flat", "Bass Boost", "Treble Boost", "Vocal", "Radio", "Custom".
    /// </summary>
    public string EqualizerPreset { get; set; } = "Flat";

    /// <summary>
    /// EQ band gains in dB, indexed 0-9 for the 10-band graphic EQ.
    /// Frequencies: 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz.
    /// </summary>
    public double[] EqualizerBandGains { get; set; } = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    // ── Details panel ──

    /// <summary>
    /// Background mode for the Details panel: "None", "BlurredAlbumArt", or "Canvas".
    /// </summary>
    public string DetailsBackgroundMode { get; set; } = "Canvas";

    // ── Lyrics ──

    /// <summary>
    /// Ordered list of lyrics source preferences. Position 0 = highest priority.
    /// Each entry has a name and enabled flag. If empty, defaults are used.
    /// </summary>
    public List<LyricsSourcePref> LyricsSourcePreferences { get; set; } = [];

    // ── Feedback ──

    /// <summary>
    /// Whether to include diagnostics (logs/crash info) in feedback reports by default.
    /// </summary>
    public bool FeedbackIncludeDiagnostics { get; set; } = true;

    /// <summary>
    /// Whether to include device metadata (OS version, app version) in feedback reports by default.
    /// </summary>
    public bool FeedbackIncludeDeviceMetadata { get; set; } = true;

    /// <summary>
    /// Whether to submit feedback anonymously by default.
    /// </summary>
    public bool FeedbackAnonymous { get; set; } = true;

    /// <summary>
    /// True after the user accepts the first-time public comments consent.
    /// </summary>
    public bool PodcastCommentsConsentAccepted { get; set; }

    // ── Diagnostics / Logging ──

    /// <summary>
    /// When true, the app logs at Verbose level (and the audio process is started with --verbose).
    /// When false, the app logs at Information level. Off by default to keep production logs small.
    /// </summary>
    public bool VerboseLoggingEnabled { get; set; }

    /// <summary>
    /// When true, reveals the in-app memory diagnostics panel under Settings → Diagnostics
    /// and starts a periodic logger emitting memory metrics so leak hunts can be done from
    /// logs alone. Off by default — both the panel and the logger pay nothing while off.
    /// </summary>
    public bool MemoryDiagnosticsEnabled { get; set; }

    // ── Artist page ──

    /// <summary>
    /// Scale multiplier applied to the Albums + Singles grids on the Artist page.
    /// 1.0 = base sizing (160 × 220 px); slider range is 0.7–1.6 (matches the
    /// Library album scale). Persisted so a chosen density survives restarts.
    /// </summary>
    public double ArtistDiscographyGridScale { get; set; } = 1.0;

    // ── Updates ──

    /// <summary>
    /// When the last update check was performed.
    /// </summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>
    /// The last app version whose "What's New" dialog was shown/dismissed.
    /// </summary>
    public string? LastSeenChangelogVersion { get; set; }

    // ── Library (per-tab sort + view preferences) ──

    /// <summary>
    /// Persisted sort + view-mode preferences, keyed by library tab
    /// ("albums", "artists", "podcasts"). Unknown tabs fall back to defaults.
    /// </summary>
    public Dictionary<string, LibraryTabPreferences> LibraryTabs { get; set; } = new();

    // ── On-device AI (Copilot+ PC, opt-in) ──

    /// <summary>
    /// Master switch for any on-device AI feature. Default false — until the user
    /// flips this on in Settings, no AI affordance renders, no Phi Silica model is
    /// downloaded, and zero calls land in Microsoft.Windows.AI.Text. Per-feature
    /// toggles below are only honored when this is true.
    /// </summary>
    public bool AiFeaturesEnabled { get; set; } = false;

    /// <summary>
    /// Per-feature toggle: per-line "what does this lyric mean?" affordance on
    /// the expanded now-playing panel. Only effective when <see cref="AiFeaturesEnabled"/>
    /// is true. Default true so a user who opts in to AI gets the feature without
    /// having to toggle a second switch.
    /// </summary>
    public bool AiLyricsExplainEnabled { get; set; } = true;

    /// <summary>
    /// Per-feature toggle: header-level "summarize song themes" affordance on the
    /// expanded now-playing panel. Only effective when <see cref="AiFeaturesEnabled"/>
    /// is true. Default true.
    /// </summary>
    public bool AiLyricsSummarizeEnabled { get; set; } = true;

    /// <summary>
    /// True once the user dismisses the "Get free posters and metadata" InfoBar
    /// on the Local files landing page. The CTA never appears again. Detail-page
    /// inline banners ignore this flag — they're tied to actual content state and
    /// auto-resolve as soon as enrichment runs.
    /// </summary>
    public bool TmdbTeaserDismissed { get; set; } = false;
}

public sealed class LibraryTabPreferences
{
    public string SortBy { get; set; } = "Recents";
    public string SortDirection { get; set; } = "Descending";
    public string ViewMode { get; set; } = "DefaultGrid";
    public double GridScale { get; set; } = 1.0;
}

public sealed class HomeSectionSettings
{
    /// <summary>
    /// Ordered list of section preferences. Order = display order.
    /// </summary>
    public List<HomeSectionPref> Sections { get; set; } = [];

    /// <summary>
    /// When true, user has done initial setup (first load seeds all sections as visible).
    /// </summary>
    public bool Initialized { get; set; }
}

public sealed class LyricsSourcePref
{
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}

public sealed class HomeSectionPref
{
    public string SectionUri { get; set; } = "";
    public string? Title { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsPinned { get; set; }
}
