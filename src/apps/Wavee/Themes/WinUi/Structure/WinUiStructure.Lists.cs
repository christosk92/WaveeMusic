// ── Themes/WinUi/Structure/WinUiStructure.Lists.cs — the WinUI lists' registrar (themes plan 6.2; P6 W3) ───────────────────────────
//
// WinUI's list-shaped surfaces are STYLES the core hosts build from (`Theming\Contracts\ListStyleLookup.cs`): the track table (its rows and its
// chrome), the eager track row, the podcast episode row, the media surface, the lyrics lines, the artist's chart and the track drawer. Each
// registration here wins over Classic's stand-in key by key (`WinUiStructure.Register` copies only the keys WinUI does not register).
// The pictures are `Structure\Lists\WinUi*.cs`; their numbers are `Rules\WinUiListRules.cs` and the `WinUi*Layout.cs` records beside it.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterLists(ViewRegistryBuilder views)
    {
        views.AddStyle(StyleKeys.TrackTable, WinUiListStyles.TrackTable);
        views.AddStyle(StyleKeys.EagerTrackRow, WinUiListStyles.EagerTrackRow);
        views.AddStyle(StyleKeys.EpisodeRow, WinUiListStyles.EpisodeRow);
        views.AddStyle(StyleKeys.Surface, WinUiListStyles.Surface);
        views.AddStyle(StyleKeys.LyricsLines, WinUiListStyles.LyricsLines);
        views.AddStyle(StyleKeys.ArtistChart, WinUiListStyles.ArtistChart);
        views.AddStyle(StyleKeys.TrackDrawer, WinUiListStyles.TrackDrawer);
    }
}

/// <summary>WinUI's list styles, as the registry holds them (public, so a test can tell WinUI's registration from the stand-in's).</summary>
public static class WinUiListStyles
{
    /// <summary>The track table: the rows (the Modern two-line skin and the Classic one-line skin) and the chrome.</summary>
    public static TrackListStyle TrackTable => WinUiTrackTable.Style;

    /// <summary>The eager track row.</summary>
    public static EagerRowStyle EagerTrackRow => WinUiEagerRow.Style;

    /// <summary>The podcast episode row.</summary>
    public static EpisodeRowStyle EpisodeRow => WinUiEpisodeRow.Style;

    /// <summary>The media surface (cards, tiles, rows).</summary>
    public static SurfaceStyle Surface => WinUiSurface.Style;

    /// <summary>The lyrics lines.</summary>
    public static LyricsLinesStyle LyricsLines => WinUiLyricsLines.Style;

    /// <summary>The artist's Top-tracks chart.</summary>
    public static ArtistChartStyle ArtistChart => WinUiArtistChart.Style;

    /// <summary>The track drawer.</summary>
    public static TrackDrawerStyle TrackDrawer => WinUiTrackDrawer.Style;
}
