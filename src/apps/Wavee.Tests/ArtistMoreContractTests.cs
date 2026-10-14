// ── Wavee.Tests/ArtistMoreContractTests.cs — the rest of the artist family's published keys and Classic's registration (themes plan 6.0, T10b2) ──
//
// T10b2 moves the gallery lightbox (`Artist.Lightbox`), the standalone discography page (`Pages.Discography`) and the discography's picture
// (`StyleKeys.Discography`, pinned with the other list styles in `Theming\ListSurfaceStylesTests`) into the structure. What is pure about the contract:
// the ids (a published id never changes), Classic registering both keys under its own template ids with a complete registry, and the props never
// comparing equal to another instance (the lightbox's record is one per open; the page's host rebuilds its record on every render).

using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ArtistMoreContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Artist.Lightbox", ViewKeys.ArtistLightbox.Id);
        Assert.Equal("Pages.Discography", ViewKeys.Discography.Id);
    }

    [Fact]
    public void Classic_registers_the_lightbox_and_the_discography_page_under_their_own_template_ids()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("classic.artist.lightbox", classic.Registry.TemplateIdOf(ViewKeys.ArtistLightbox.Id));
        Assert.Equal("classic.discography.page", classic.Registry.TemplateIdOf(ViewKeys.Discography.Id));
        Assert.Empty(classic.Validate());
    }

    static ArtistLightboxProps Lightbox() => new()
    {
        Photos = ["a", "b"],
        Selected = new Signal<int>(0),
        Select = static _ => { },
        Saving = new Signal<bool>(false),
        Export = static (_, _) => { },
        Close = static () => { },
        Attach = static (_, _) => { },
    };

    static DiscographyPageProps Page() => new()
    {
        Parsed = true,
        Name = "Artist",
        Title = "Albums",
        Facet = DiscoFacet.Albums,
        OnCrumb = static _ => { },
        OnFacet = static _ => { },
        ScrollKey = "test:disco",
        Scroll = new ScrollHandle(),
    };

    [Fact]
    public void A_data_equal_copy_of_the_props_is_not_equal()
    {
        var lightbox = Lightbox();
        Assert.NotEqual(lightbox, lightbox with { });

        var page = Page();
        Assert.NotEqual(page, page with { });
    }
}
