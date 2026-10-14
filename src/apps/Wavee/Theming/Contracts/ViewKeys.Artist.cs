// ── Theming/Contracts/ViewKeys.Artist.cs — the artist page's published view key (themes plan 6.0, T10b1) ─────────────────────
//
// T10b1 publishes the artist PAGE (`Pages.Artist`): the scroll column, the page's skeleton region, the full-bleed hero and its collapse into
// the 56-DIP context band, the section anchors under the band, the Top-tracks band, the shelves, the biography band, the banners and the
// artist pick card. The hero is NOT a key of its own: its pinned, collapsing root has to stay a direct child of the page column, and the
// derived skeleton cannot run a view slot, so the page view builds the hero for the live page and for the shimmer with the same builder.
//
// What stays core: the models and their demands (`Artist.PageHost`: the overview, the chart and concert edges, the discography facets, the
// rows each list points at), the readiness and the page-failure verdict, the section plan and the pivot's membership
// (`ArtistSections`), the latched hero metrics (`ArtistHeroLayout`: the profile page reads the same tiers), the watched chrome accent, the
// scroll spy and its section anchors, the shell-tint claim, and the Play / Shuffle / Radio / follow decisions. The Top-tracks chart
// (`StyleKeys.ArtistChart`) and the discography facets (`StyleKeys.Discography`) are core hosts the view mounts as components.
//
// T10b2 publishes the rest of the artist family: the gallery LIGHTBOX (`Artist.Lightbox`: the full-window pager, the pan/zoom mode, the auto-hiding
// chrome and the filmstrip; the overlay, the open index, the Escape veto's wiring and the export stay core in `Artist.GalleryOpen`) and the standalone
// discography PAGE (`Pages.Discography`: the crumbs, the title, the facet selector and the scroll column over the core's grid; the route, the demand and
// the scroll plumbing stay core in `Artist.DiscoPageHost`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The artist page. Mounted by `Artist.PageHost` (the host). Props: <see cref="ArtistPageProps"/>.</summary>
    public static readonly ViewKey<ArtistPageProps> ArtistPage = new("Pages.Artist");

    /// <summary>The artist gallery's lightbox. Mounted by `Artist.GalleryOpen` in its modal overlay. Props: <see cref="ArtistLightboxProps"/>.</summary>
    public static readonly ViewKey<ArtistLightboxProps> ArtistLightbox = new("Artist.Lightbox");

    /// <summary>The standalone discography page (`disco:&lt;kind&gt;:&lt;artist&gt;`). Mounted by `Artist.DiscoPageHost`. Props: <see cref="DiscographyPageProps"/>.</summary>
    public static readonly ViewKey<DiscographyPageProps> Discography = new("Pages.Discography");
}
