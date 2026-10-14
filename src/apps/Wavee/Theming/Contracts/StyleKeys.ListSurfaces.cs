// ── Theming/Contracts/StyleKeys.ListSurfaces.cs — the list surfaces' style keys (themes plan 6.0, T8c) ────────────────────────────
//
// T8a published the track table's rows (`StyleKeys.TrackTable`) and T8b its chrome. T8c publishes the other list-shaped surfaces: the
// eager track row, the podcast episode row, the media surface (the one card, tile and row), the artist's Top-tracks chart and the
// track drawer. Each is a STYLE, not a view: the core host (a virtualized slot, a shelf, a bound list) keeps its hooks, state and
// decisions and calls the templates of the style while it renders. A core host reads the style through `ListStyleLookup`.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The eager track row's templates: the grid, the title run and the hover container (<see cref="EagerRowStyle"/>). Read by
    /// <c>Track.EagerRow</c>'s component, the table's counted shimmer and the discography drawer's rows.</summary>
    public static readonly StyleKey<EagerRowStyle> EagerTrackRow = new("Track.EagerRow");

    /// <summary>The episode row's templates: the bound content, the cold seed and the failure row (<see cref="EpisodeRowStyle"/>). Read by
    /// the show reader, the playlist table's episode kind and the cold skeletons.</summary>
    public static readonly StyleKey<EpisodeRowStyle> EpisodeRow = new("Episode.Row");

    /// <summary>The media surface's templates: the shell, the selected skin, the stack and row bodies and the labels
    /// (<see cref="SurfaceStyle"/>). Read by <c>Controls.Surface</c>'s host and the artist page's pick card.</summary>
    public static readonly StyleKey<SurfaceStyle> Surface = new("Media.Surface");

    /// <summary>The artist chart's templates: the shelf metrics, the row, the cell, the skeleton, the failed panel and the pager
    /// (<see cref="ArtistChartStyle"/>). Read by <c>Artist.ChartHost</c> and the artist page's skeleton.</summary>
    public static readonly StyleKey<ArtistChartStyle> ArtistChart = new("Artist.Chart");

    /// <summary>The track drawer's templates: the frame, the body and the format split button (<see cref="TrackDrawerStyle"/>). Read by the
    /// table host's drawer box and by <c>Track.Drawer</c>'s host.</summary>
    public static readonly StyleKey<TrackDrawerStyle> TrackDrawer = new("Track.Drawer");
}
