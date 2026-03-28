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

public sealed class HomeSectionPref
{
    public string SectionUri { get; set; } = "";
    public string? Title { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsPinned { get; set; }
}
