// ── Wavee.ShellTests/ArtistMoreMountedTests.cs — the gallery lightbox and the discography page, mounted (themes plan 6.1, T10b2) ──────
//
// The gallery lightbox's picture and the standalone discography page are the structure's since T10b2; `Artist.GalleryOpen` keeps the overlay, the open
// index, the Escape veto's wiring and the export, `Artist.DiscoPageHost` the route, the demand and the scroll plumbing (`Wavee.Tests` pins the keys and
// the registration: `ArtistMoreContractTests`). What only a mounted tree can say:
//
//   lightbox     Classic is the registered template; the viewer wires the Escape veto on mount and, not zoomed, lets Escape close; the selected
//                photo is a keyed page of the pager, and a core selection reaches the view
//   discography  Classic is the registered template; an unparsed route draws the vacancy and no grid; a parsed one mounts the core's grid at the page's
//                reveal inset (28: there is no sticky facet band on this page) under its keyed scroller

using FluentGpu.Dsl;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ArtistMoreMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── the lightbox ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly string[] s_photos = ["fixture:gallery:a", "fixture:gallery:b", "fixture:gallery:c"];

    [Fact]
    public void Classic_is_the_registered_template_of_the_lightbox()
        => host.Run(() => Assert.Equal("classic.artist.lightbox", ThemeHost.Registry.TemplateIdOf(ViewKeys.ArtistLightbox.Id)));

    [Fact]
    public void The_lightbox_wires_the_escape_veto_and_pages_the_selected_photo()
    {
        Func<bool>? consumeEscape = null;
        Action? closed = null;
        var selected = new Signal<int>(1);
        var props = new ArtistLightboxProps
        {
            Photos = s_photos,
            Selected = selected,
            Select = i => selected.Value = i,
            Saving = new Signal<bool>(false),
            Export = static (_, _) => { },
            Close = static () => { },
            Attach = (escape, onClosed) => { consumeEscape = escape; closed = onClosed; },
        };
        ViewHarness.With(host, ViewKeys.ArtistLightbox, props,
            harness =>
            {
                harness.RunFrames(10);
                Assert.NotNull(consumeEscape);
                Assert.NotNull(closed);
                Assert.False(consumeEscape!(), "not zoomed: Escape is not consumed, so the overlay closes");
                Assert.True(harness.Contains("gallery-image:" + s_photos[1]), "the selected photo is a keyed page of the pager");

                selected.Value = 2;
                harness.RunFrames(10);
                Assert.True(harness.Contains("gallery-image:" + s_photos[2]), "a core selection reaches the pager");
                closed!();
            }, UnderClassic());
    }

    // ── the discography page ────────────────────────────────────────────────────────────────────────────────────────────

    static DiscographyPageProps Page(bool parsed, Func<float, Element>? grid) => new()
    {
        Parsed = parsed,
        Name = "An artist",
        Title = "Albums",
        Facet = DiscoFacet.Albums,
        OnCrumb = static _ => { },
        OnFacet = static _ => { },
        Grid = grid,
        ScrollKey = "test:disco:fixture",
        Scroll = new ScrollHandle(),
    };

    [Fact]
    public void Classic_is_the_registered_template_of_the_discography_page()
        => host.Run(() => Assert.Equal("classic.discography.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.Discography.Id)));

    [Fact]
    public void An_unparsed_route_draws_no_grid()
    {
        bool asked = false;
        ViewHarness.With(host, ViewKeys.Discography, Page(parsed: false, _ => { asked = true; return new BoxEl { Key = "grid-probe" }; }),
            harness =>
            {
                harness.RunFrames(10);
                Assert.False(asked, "an unparsed route never asks the core for its grid");
                Assert.False(harness.Contains("grid-probe"));
            }, UnderClassic());
    }

    [Fact]
    public void A_parsed_route_mounts_the_cores_grid_at_the_pages_reveal_inset()
    {
        float inset = float.NaN;
        ViewHarness.With(host, ViewKeys.Discography, Page(parsed: true, i => { inset = i; return new BoxEl { Key = "grid-probe", Height = 10f }; }),
            harness =>
            {
                harness.RunFrames(10);
                Assert.True(harness.Contains("grid-probe"), "the core's grid stands in the page's column");
                Assert.Equal(28f, inset);
            }, UnderClassic());
    }
}
