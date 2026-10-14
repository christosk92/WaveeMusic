// ── Wavee.Tests/Theming/ListSurfaceStylesTests.cs — the list surfaces' styles, registered (themes plan 6.1, T8c) ──────────────
//
// T8c moves the picture of five list-shaped surfaces to the structure: the eager track row, the podcast episode row, the media surface
// (the one card, tile and row), the artist's Top-tracks chart and the track drawer. Each is a STYLE (`StyleKeys.EagerTrackRow`,
// `EpisodeRow`, `Surface`, `ArtistChart`, `TrackDrawer`) and the core host that keeps its hooks and decisions reads it through
// `ListStyleLookup`. What a registry can say without mounting: Classic registers all five, the published ids are what the contract says
// and are distinct, a registry without them answers none, and the lookup returns the style or null (the host then logs once and draws
// nothing). The pictures themselves are asserted mounted (`Wavee.ShellTests\ListSurfacesMountedTests.cs`).

using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ListSurfaceStylesTests
{
    static ViewRegistry ClassicRegistry()
    {
        var views = new ViewRegistryBuilder();
        new ClassicStructure().Register(views);
        return views.Build();
    }

    [Fact]
    public void Classic_registers_the_eager_row_style()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.EagerTrackRow, out var style));
        Assert.NotNull(style);
    }

    [Fact]
    public void Classic_registers_the_episode_row_style()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.EpisodeRow, out var style));
        Assert.NotNull(style);
    }

    [Fact]
    public void Classic_registers_the_media_surface_style()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.Surface, out var style));
        Assert.NotNull(style);
    }

    [Fact]
    public void Classic_registers_the_artist_chart_style()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.ArtistChart, out var style));
        Assert.NotNull(style);
    }

    [Fact]
    public void Classic_registers_the_track_drawer_style()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.TrackDrawer, out var style));
        Assert.NotNull(style);
    }

    [Fact]
    public void The_key_ids_are_the_published_ones()
    {
        Assert.Equal("Track.EagerRow", StyleKeys.EagerTrackRow.Id);
        Assert.Equal("Episode.Row", StyleKeys.EpisodeRow.Id);
        Assert.Equal("Media.Surface", StyleKeys.Surface.Id);
        Assert.Equal("Artist.Chart", StyleKeys.ArtistChart.Id);
        Assert.Equal("Track.Drawer", StyleKeys.TrackDrawer.Id);
    }

    [Fact]
    public void The_key_ids_are_distinct_from_each_other_and_from_the_tables()
    {
        string[] ids =
        [
            StyleKeys.TrackTable.Id, StyleKeys.EagerTrackRow.Id, StyleKeys.EpisodeRow.Id, StyleKeys.Surface.Id,
            StyleKeys.ArtistChart.Id, StyleKeys.TrackDrawer.Id,
        ];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_registry_without_the_styles_answers_none()
    {
        Assert.False(ViewRegistry.Empty.TryGetStyle(StyleKeys.EagerTrackRow, out _));
        Assert.False(ViewRegistry.Empty.TryGetStyle(StyleKeys.EpisodeRow, out _));
        Assert.False(ViewRegistry.Empty.TryGetStyle(StyleKeys.Surface, out _));
        Assert.False(ViewRegistry.Empty.TryGetStyle(StyleKeys.ArtistChart, out _));
        Assert.False(ViewRegistry.Empty.TryGetStyle(StyleKeys.TrackDrawer, out _));
    }

    [Fact]
    public void The_lookup_returns_the_registered_style()
    {
        var registry = ClassicRegistry();
        Assert.NotNull(ListStyleLookup.Of(registry, StyleKeys.EagerTrackRow));
        Assert.NotNull(ListStyleLookup.Of(registry, StyleKeys.Surface));
        Assert.NotNull(ListStyleLookup.Of(registry, StyleKeys.TrackDrawer));
    }

    [Fact]
    public void The_lookup_answers_null_for_a_registry_without_the_style_and_for_no_registry()
    {
        Assert.Null(ListStyleLookup.Of(ViewRegistry.Empty, StyleKeys.ArtistChart));
        Assert.Null(ListStyleLookup.Of(null, StyleKeys.EpisodeRow));
    }
}
