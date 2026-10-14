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
// (`StyleKeys.ArtistChart`) and the discography facets are core hosts the view mounts as components.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The artist page. Mounted by `Artist.PageHost` (the host). Props: <see cref="ArtistPageProps"/>.</summary>
    public static readonly ViewKey<ArtistPageProps> ArtistPage = new("Pages.Artist");
}
