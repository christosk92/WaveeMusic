// ── Wavee.Tests/Theming/TrackListStyleTests.cs — the track table's row style, registered (themes plan 6.1, T8a) ──────────────
//
// T8a moves the track table's rows (the bound grid of lanes and the row's skin, in both row styles) to the structure: Classic
// registers a `TrackListStyle` under `StyleKeys.TrackTable` and the core table host builds its rows from it. What a registry can
// say without mounting a list: the style is registered, it carries both row styles as separate template pairs, and a registry
// that has none answers no style (the table then logs once and draws bare containers). The rows themselves, in both styles, are
// asserted mounted (`Wavee.ShellTests\TrackRowsMountedTests.cs`) and the recycle budget on the real table is
// `TrackRowRecycleAllocationTests`.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class TrackListStyleTests
{
    static ViewRegistry ClassicRegistry()
    {
        var views = new ViewRegistryBuilder();
        new ClassicStructure().Register(views);
        return views.Build();
    }

    [Fact]
    public void Classic_registers_the_track_table_style()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.TrackTable, out var style));
        Assert.NotNull(style);
    }

    [Fact]
    public void The_style_holds_a_Modern_and_a_Classic_template_pair()
    {
        Assert.True(ClassicRegistry().TryGetStyle(StyleKeys.TrackTable, out var style));
        Assert.NotNull(style.Modern);
        Assert.NotNull(style.Classic);
        Assert.NotSame(style.Modern, style.Classic);
    }

    [Fact]
    public void A_registry_without_the_style_answers_none()
        => Assert.False(ViewRegistry.Empty.TryGetStyle(StyleKeys.TrackTable, out _));

    [Fact]
    public void The_key_id_is_the_published_one()
        => Assert.Equal("Track.Table", StyleKeys.TrackTable.Id);
}
