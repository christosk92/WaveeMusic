// ── Wavee.Tests/ShowContractTests.cs — the show page's style, registered (themes plan 6.1, T9c) ──────────────────────────────────
//
// T9c moves the picture of the podcast show page (the rail's slots and satellites, the library pane's column, the reader's toolbar, visit head,
// headers, foot, sticky month, year strip and cold seed) to the structure as ONE style: `StyleKeys.ShowReader` carrying a `ShowReaderStyle`. The
// core hosts keep the model, the snapshot, the stamps and the verbs and read the style through `ListStyleLookup`. What a registry can say
// without mounting: Classic registers it, the published id is what the contract says and is distinct, a registry without it answers none, and
// the lookup returns the style or null (the host then logs once and draws nothing). The pictures are asserted mounted
// (`Wavee.ShellTests\ShowMountedTests`); the arithmetic they must agree with stays `ShowReaderTests`, `ShowReaderShapeTests`, `PodcastShowToolbarTests`.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public sealed class ShowContractTests
{
    static ViewRegistry ClassicRegistry()
    {
        var views = new ViewRegistryBuilder();
        new ClassicStructure().Register(views);
        return views.Build();
    }

    [Fact]
    public void Classic_registers_the_show_style()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.ShowReader, out var style));
        Assert.NotNull(style);
    }

    [Fact]
    public void The_key_id_is_the_published_one_and_distinct_from_the_other_styles()
    {
        Assert.Equal("Show.Reader", StyleKeys.ShowReader.Id);
        string[] ids =
        [
            StyleKeys.TrackTable.Id, StyleKeys.EagerTrackRow.Id, StyleKeys.EpisodeRow.Id, StyleKeys.Surface.Id,
            StyleKeys.ArtistChart.Id, StyleKeys.TrackDrawer.Id, StyleKeys.ShowReader.Id,
        ];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_registry_without_the_style_answers_none()
    {
        Assert.False(ViewRegistry.Empty.TryGetStyle(StyleKeys.ShowReader, out _));
        Assert.Null(ListStyleLookup.Of(ViewRegistry.Empty, StyleKeys.ShowReader));
        Assert.Null(ListStyleLookup.Of(null, StyleKeys.ShowReader));
    }

    [Fact]
    public void The_lookup_returns_the_registered_style()
        => Assert.NotNull(ListStyleLookup.Of(ClassicRegistry(), StyleKeys.ShowReader));
}
