using System.Collections.Generic;

namespace Wavee.UI.WinUI.Data.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Default";
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

    // ── Cache (applied on next launch) ──

    /// <summary>
    /// Whether audio caching is enabled.
    /// </summary>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>
    /// Maximum cache size in bytes. Default 1 GB.
    /// </summary>
    public long CacheSizeLimitBytes { get; set; } = 1L * 1024 * 1024 * 1024;

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

    // ── Lyrics ──

    /// <summary>
    /// Ordered list of lyrics source preferences. Position 0 = highest priority.
    /// Each entry has a name and enabled flag. If empty, defaults are used.
    /// </summary>
    public List<LyricsSourcePref> LyricsSourcePreferences { get; set; } = [];
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
