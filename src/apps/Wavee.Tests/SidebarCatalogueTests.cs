// ── Wavee.Tests/SidebarCatalogueTests.cs — the closed section catalogue: membership, ids, locks, items and defaults ────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P3.1 and §P3.15 (design C.1). Nothing outside SidebarCatalogue decides
// what a section is, so these facts pin the whole vocabulary the files, the ops and the planner build on.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarCatalogueTests
{
    [Fact]
    public void Classic_HasSevenSections_InOrder()
    {
        Assert.Equal(
            new[]
            {
                SidebarSectionKind.Home, SidebarSectionKind.Pinned, SidebarSectionKind.Collections,
                SidebarSectionKind.Playlists, SidebarSectionKind.Recent, SidebarSectionKind.NewReleases,
                SidebarSectionKind.Settings,
            },
            SidebarCatalogue.KindsOf(SidebarLayoutId.Classic).ToArray());
    }

    [Fact]
    public void Library_HasFour_NoCollections_NoFeeds()
    {
        Assert.Equal(
            new[] { SidebarSectionKind.Home, SidebarSectionKind.Pinned, SidebarSectionKind.Library, SidebarSectionKind.Settings },
            SidebarCatalogue.KindsOf(SidebarLayoutId.Library).ToArray());
        Assert.False(SidebarCatalogue.Has(SidebarLayoutId.Library, SidebarSectionKind.Collections));
        Assert.False(SidebarCatalogue.Has(SidebarLayoutId.Library, SidebarSectionKind.Recent));
        Assert.False(SidebarCatalogue.Has(SidebarLayoutId.Library, SidebarSectionKind.NewReleases));
    }

    [Fact]
    public void Ids_RoundTrip()
    {
        foreach (var kind in Enum.GetValues<SidebarSectionKind>())
        {
            Assert.True(SidebarCatalogue.TryKindOf(SidebarCatalogue.IdOf(kind), out var back));
            Assert.Equal(kind, back);
        }
        Assert.False(SidebarCatalogue.TryKindOf("spotlight", out _));
        Assert.False(SidebarCatalogue.TryKindOf(null, out _));
    }

    [Fact]
    public void Locks()
    {
        Assert.False(SidebarCatalogue.Hideable(SidebarSectionKind.Home));
        Assert.False(SidebarCatalogue.Hideable(SidebarSectionKind.Playlists));
        Assert.False(SidebarCatalogue.Hideable(SidebarSectionKind.Library));
        Assert.True(SidebarCatalogue.Hideable(SidebarSectionKind.Settings));

        Assert.False(SidebarCatalogue.Movable(SidebarLayoutId.Library, SidebarSectionKind.Pinned));
        Assert.True(SidebarCatalogue.Movable(SidebarLayoutId.Classic, SidebarSectionKind.Playlists));
        Assert.False(SidebarCatalogue.Movable(SidebarLayoutId.Classic, SidebarSectionKind.Home));

        Assert.False(SidebarCatalogue.Collapsible(SidebarSectionKind.Library));
    }

    [Fact]
    public void Items()
    {
        Assert.Equal(new[] { "liked", "albums", "artists", "podcasts", "audiobooks" },
            SidebarCatalogue.ItemsOf(SidebarLayoutId.Classic, SidebarSectionKind.Collections).ToArray());
        Assert.Equal(new[] { "albums", "artists", "podcasts", "audiobooks" },
            SidebarCatalogue.ItemsOf(SidebarLayoutId.Library, SidebarSectionKind.Library).ToArray());

        foreach (var layout in new[] { SidebarLayoutId.Classic, SidebarLayoutId.Library })
            foreach (var kind in Enum.GetValues<SidebarSectionKind>())
            {
                bool owns = (layout == SidebarLayoutId.Classic && kind == SidebarSectionKind.Collections)
                            || (layout == SidebarLayoutId.Library && kind == SidebarSectionKind.Library);
                if (!owns) Assert.Empty(SidebarCatalogue.ItemsOf(layout, kind));
            }
    }

    [Fact]
    public void Defaults()
    {
        Assert.Equal(5, SidebarCatalogue.DefaultLimit(SidebarSectionKind.Recent));
        Assert.Equal(10, SidebarCatalogue.DefaultLimit(SidebarSectionKind.NewReleases));
        Assert.Equal(new[] { 5, 10, 20 }, SidebarCatalogue.LimitChoices);

        var recent = SidebarCatalogue.DefaultState(SidebarSectionKind.Recent);
        Assert.True(recent.Hidden);
        Assert.Equal(5, recent.Limit);

        var library = SidebarCatalogue.DefaultState(SidebarSectionKind.Library);
        Assert.Equal(SidebarLibrarySort.Recents, library.Sort);
        Assert.False(library.Descending);
        Assert.Equal(SidebarLibraryView.List, library.View);
        Assert.True(library.ShowLiked);
    }
}
