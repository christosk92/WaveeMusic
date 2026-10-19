// ── Themes/WinUi/Structure/WinUiStructure.Detail.cs — the Detail family's registrar (themes plan 6.2; P6 W6a) ───────────────────────────────────────
//
// W6a: the detail frame (the hero over the table at every width, the sticky 48 bar, the Huge aside), the album sub-contract (artist line and flyout, countdown,
// release panel, the trailing band, the aside's sections, the unresolved page), the artist page and its lightbox, and the discography page and style.
// WinUI's registrations win over the stand-in key by key.
//
//   Detail.Frame / Hero / ShowHeader / Notice / VerticalTrailing / Insights / InsightsToggle   `Views\Detail\WinUiDetail*.cs`, `WinUiInsights.cs`
//   Detail.Rail / Detail.CompactRail                         NOTHING: WinUI has one composition (the hero over the table), its page-mode ladder never answers the
//                                                            two-column arm that mounts them (`Tiers.Detail.cs`)
//   Album.*                                                  `Views\Detail\WinUiAlbum*.cs`
//   Pages.Artist, Artist.Lightbox, Pages.Discography         `Views\Artist\*`, `Views\Pages\*`

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterDetail(ViewRegistryBuilder views)
    {
        // ── the frame ──
        views.Add(ViewKeys.DetailFrame, WinUiDetailFrame.Template, "winui.detail.frame");
        views.Add(ViewKeys.DetailShowHeader, WinUiDetailFrame.ShowHeaderTemplate, "winui.detail.show-header");
        views.Add(ViewKeys.DetailHero, WinUiDetailFrame.HeroTemplate, "winui.detail.hero");
        views.AddStyle(StyleKeys.DetailHero, WinUiDetailFrame.HeroStyle);
        views.Add(ViewKeys.DetailInsights, WinUiInsights.SheetTemplate, "winui.detail.insights");
        views.Add(ViewKeys.DetailInsightsToggle, WinUiInsights.ToggleTemplate, "winui.detail.insights-toggle");
        views.Add(ViewKeys.DetailNotice, WinUiDetailSeam.NoticeTemplate, "winui.detail.notice");
        views.Add(ViewKeys.DetailVerticalTrailing, WinUiDetailSeam.VerticalTrailingTemplate, "winui.detail.vertical-trailing");
        CommonNothing.Register(views, ViewKeys.DetailRail);
        CommonNothing.Register(views, ViewKeys.DetailCompactRail);

        // ── the album ──
        views.Add(ViewKeys.AlbumArtistLine, WinUiAlbumArtist.LineTemplate, "winui.album.artist-line");
        views.Add(ViewKeys.AlbumArtistFlyout, WinUiAlbumArtist.FlyoutTemplate, "winui.album.artist-flyout");
        views.Add(ViewKeys.AlbumCountdown, WinUiAlbumRelease.CountdownTemplate, "winui.album.countdown");
        views.Add(ViewKeys.AlbumReleasePanel, WinUiAlbumRelease.ReleasePanelTemplate, "winui.album.release-panel");
        views.Add(ViewKeys.AlbumUnresolved, WinUiAlbumRelease.UnresolvedTemplate, "winui.album.unresolved");
        views.Add(ViewKeys.AlbumTrailing, WinUiAlbumTrailing.TrailingTemplate, "winui.album.trailing");
        views.Add(ViewKeys.AlbumSections, WinUiAlbumTrailing.SectionsTemplate, "winui.album.sections");
        views.Add(ViewKeys.AlbumAside, WinUiAlbumTrailing.AsideTemplate, "winui.album.aside");
        views.Add(ViewKeys.AlbumTrailStack, WinUiAlbumTrailing.StackTemplate, "winui.album.trail-stack");

        // ── the artist ──
        views.Add(ViewKeys.ArtistPage, WinUiArtistPage.Template, "winui.artist.page");
        views.Add(ViewKeys.ArtistLightbox, WinUiArtistLightbox.Template, "winui.artist.lightbox");
        views.Add(ViewKeys.Discography, WinUiDiscographyPage.Template, "winui.discography.page");
        views.AddStyle(StyleKeys.Discography, WinUiDiscography.Style);
    }
}
