// ── Wavee.Tests/LibraryContractTests.cs — the library family's published keys and Classic's registration (themes plan 6.0, T10c1) ──────────────
//
// The library page and its four panes (the album pane, the compact show pane, the artists' reader and Your Episodes) are the structure's pictures since T10c1; the core
// hosts keep the data, the shape memo, the selection and its persistence, the search matcher, the collapse state, the readiness of each pane and every verb. What is
// pure about the contract: the ids (a published id never changes), Classic registering every key under a distinct template id, the props records comparing by
// reference (a host rebuilds one per render, and a data-equal record that carried newer closures would keep the old ones), and the header's stated height.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class LibraryContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Pages.Library", ViewKeys.LibraryPage.Id);
        Assert.Equal("Library.AlbumPane", ViewKeys.LibraryAlbumPane.Id);
        Assert.Equal("Library.ShowPane", ViewKeys.LibraryShowPane.Id);
        Assert.Equal("Library.ArtistReader", ViewKeys.LibraryArtistReader.Id);
        Assert.Equal("Library.SavedEpisodes", ViewKeys.LibrarySavedEpisodes.Id);
    }

    [Fact]
    public void Classic_registers_every_library_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        (string Id, string Template)[] expected =
        [
            (ViewKeys.LibraryPage.Id, "classic.library.page"),
            (ViewKeys.LibraryAlbumPane.Id, "classic.library.album-pane"),
            (ViewKeys.LibraryShowPane.Id, "classic.library.show-pane"),
            (ViewKeys.LibraryArtistReader.Id, "classic.library.artist-reader"),
            (ViewKeys.LibrarySavedEpisodes.Id, "classic.library.saved-episodes"),
        ];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, template) in expected)
        {
            Assert.Equal(template, classic.Registry.TemplateIdOf(id));
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_props_record_is_equal_only_to_itself()
    {
        var props = new LibrarySavedEpisodesProps
        {
            Pending = true, Failed = false, Content = static () => null!, OnFailed = static () => null!,
        };
        Assert.Equal(props, props);
        Assert.NotEqual(props, props with { });
    }

    [Fact]
    public void The_pane_header_states_one_height_for_both_kinds()
    {
        // The album pane and the show pane draw the SAME header, rung for rung, and the loading twin states the same band.
        Assert.Equal(160f, ClassicLibraryPaneRules.PaneHeaderHeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_view_code_has_a_glyph_size(int view)
        => Assert.InRange(ClassicLibraryRules.ViewGlyphSizes[view], 1f, ClassicLibraryRules.ViewIconEdge);

    [Theory]
    [InlineData(0, 208f)]
    [InlineData(1, 264f)]
    [InlineData(2, 176f)]
    [InlineData(3, 232f)]
    [InlineData(4, 208f)]
    public void The_shimmer_title_bar_rotates_through_four_lengths(int row, float width)
        => Assert.Equal(width, ClassicLibraryPaneRules.ShimTitleW(row));
}
