using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.UI;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Identity of a single region band in the redesigned home page.
/// 21 raw sections collapse into 4 regions so the page reads as 4
/// chapters instead of one long scroll.
/// </summary>
public enum HomeRegionKind
{
    Recents,
    LocalFiles,        // "Local files" catch-all (legacy single-rail)
    LocalShows,        // TV shows indexed locally
    LocalMovies,       // Movies indexed locally
    LocalMusic,        // Music albums indexed locally
    LocalMusicVideos,  // Music videos indexed locally
    MadeForYou,
    Discover,
    Podcasts
}

/// <summary>
/// A bucket of one or more <see cref="HomeSection"/>s sharing a single
/// region identity (eyebrow + header + accent tint). The mica band
/// background and section nesting (16 px bullet titles) live in the
/// <c>HomeRegionView</c> control; this class is just the data anchor.
/// </summary>
public sealed partial class HomeRegion : ObservableObject
{
    public HomeRegionKind Kind { get; init; }

    /// <summary>Caps eyebrow rendered above the header — e.g. "MADE FOR YOU".</summary>
    public string Eyebrow { get; init; } = "";

    /// <summary>User-facing 28 px region header — e.g. "Picked for you".</summary>
    public string Header { get; init; } = "";

    /// <summary>Drives the mica band horizontal-fade tint.</summary>
    public Color AccentColor { get; init; } = Color.FromArgb(255, 0x60, 0xCD, 0xFF);

    public ObservableCollection<HomeSection> Sections { get; } = [];

    /// <summary>Static identity table keyed by <see cref="HomeRegionKind"/>.</summary>
    public static HomeRegion Create(HomeRegionKind kind) => kind switch
    {
        HomeRegionKind.Recents => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_YourActivity_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_RecentlyPlayed_Header"),
            AccentColor = Color.FromArgb(255, 0xF5, 0x9E, 0x0B)
        },
        HomeRegionKind.LocalFiles => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_OnThisPC_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_LocalFiles_Header"),
            AccentColor = Color.FromArgb(255, 0x8B, 0x5C, 0xF6)
        },
        HomeRegionKind.LocalShows => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_OnThisPC_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_LocalShows_Header"),
            AccentColor = Color.FromArgb(255, 0x8B, 0x5C, 0xF6)
        },
        HomeRegionKind.LocalMovies => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_OnThisPC_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_LocalMovies_Header"),
            AccentColor = Color.FromArgb(255, 0x8B, 0x5C, 0xF6)
        },
        HomeRegionKind.LocalMusic => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_OnThisPC_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_LocalMusic_Header"),
            AccentColor = Color.FromArgb(255, 0x8B, 0x5C, 0xF6)
        },
        HomeRegionKind.LocalMusicVideos => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_OnThisPC_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_LocalMusicVideos_Header"),
            AccentColor = Color.FromArgb(255, 0x8B, 0x5C, 0xF6)
        },
        HomeRegionKind.MadeForYou => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_MadeForYou_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_MadeForYou_Header"),
            AccentColor = Color.FromArgb(255, 0x3B, 0x82, 0xF6)
        },
        HomeRegionKind.Discover => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_Discover_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_Discover_Header"),
            AccentColor = Color.FromArgb(255, 0x10, 0xB9, 0x81)
        },
        HomeRegionKind.Podcasts => new HomeRegion
        {
            Kind = kind,
            Eyebrow = AppLocalization.GetString("Home_Region_Podcasts_Eyebrow"),
            Header = AppLocalization.GetString("Home_Region_Podcasts_Header"),
            AccentColor = Color.FromArgb(255, 0xF4, 0x3F, 0x5E)
        },
        _ => new HomeRegion { Kind = kind }
    };
}