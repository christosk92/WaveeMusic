// ── Themes/Zune/Structure/ZuneStructure.Lists.cs — the Zune lists' registrar (themes plan 6.3; P5 Z3) ─────────────────────────────
//
// Zune's list-shaped surfaces are STYLES the core hosts build from (`Theming\Contracts\ListStyleLookup.cs`): the track table (its rows and
// its chrome), the eager track row, the podcast episode row, the media surface, the lyrics lines, the artist's chart and the track drawer.
// Each registration here wins over Classic's stand-in key by key (`ZuneStructure.Register` copies only the keys Zune does not register).
// The pictures are `Structure\Lists\Zune*.cs`; their numbers are `Rules\ZuneListRules.cs`.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterLists(ViewRegistryBuilder views)
    {
        views.AddStyle(StyleKeys.TrackTable, ZuneListStyles.TrackTable);
        views.AddStyle(StyleKeys.EagerTrackRow, ZuneListStyles.EagerTrackRow);
        views.AddStyle(StyleKeys.EpisodeRow, ZuneListStyles.EpisodeRow);
        views.AddStyle(StyleKeys.Surface, ZuneListStyles.Surface);
        views.AddStyle(StyleKeys.LyricsLines, ZuneListStyles.LyricsLines);
        views.AddStyle(StyleKeys.ArtistChart, ZuneListStyles.ArtistChart);
        views.AddStyle(StyleKeys.TrackDrawer, ZuneListStyles.TrackDrawer);
    }
}

/// <summary>Zune's list styles, as the registry holds them (public, so a test can tell Zune's registration from the stand-in's).</summary>
public static class ZuneListStyles
{
    /// <summary>The track table: the rows (one skin, both slots) and the chrome.</summary>
    public static TrackListStyle TrackTable => ZuneTrackTable.Style;

    /// <summary>The eager track row.</summary>
    public static EagerRowStyle EagerTrackRow => ZuneEagerRow.Style;

    /// <summary>The podcast episode row.</summary>
    public static EpisodeRowStyle EpisodeRow => ZuneEpisodeRow.Style;

    /// <summary>The media surface (cards, tiles, rows).</summary>
    public static SurfaceStyle Surface => ZuneSurface.Style;

    /// <summary>The lyrics lines.</summary>
    public static LyricsLinesStyle LyricsLines => ZuneLyricsLines.Style;

    /// <summary>The artist's Top-tracks chart.</summary>
    public static ArtistChartStyle ArtistChart => ZuneArtistChart.Style;

    /// <summary>The track drawer.</summary>
    public static TrackDrawerStyle TrackDrawer => ZuneTrackDrawer.Style;
}
