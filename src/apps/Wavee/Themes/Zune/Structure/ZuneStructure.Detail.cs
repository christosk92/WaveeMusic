// ── Themes/Zune/Structure/ZuneStructure.Detail.cs — the detail frame and the album, registered (themes plan 6.3; P5 Z6a1) ──────────────────────
//
// Zune's own views for the shared detail frame (every album / playlist / Liked / show / episode page) and the album's sub-contract. They win over
// Classic's stand-in key by key (`ZuneStructure.Register`). The playlist keys (`Playlist.*`) stay the stand-in's until Z6b1.
//
// DECIDED HERE (plan "Zune specifics": each lane records its Detail.Rail and CompactRail):
//   · Detail.Rail is OWN: Zune's identity column (Z-Album's 240 column under the panorama), not Nothing: the boards draw it.
//   · Detail.CompactRail is OWN: Zune draws no grip, so it never collapses the column itself, but a collapse stored by another theme reaches it;
//     the strip (the cover and a 40 expand target) keeps that state one click from undone instead of hiding the identity.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterDetail(ViewRegistryBuilder views)
    {
        // ── the frame ──
        views.Add(ViewKeys.DetailFrame, ZuneDetailFrame.Template, "zune.detail.frame");
        views.Add(ViewKeys.DetailRail, ZuneDetailRail.RailTemplate, "zune.detail.rail");
        views.Add(ViewKeys.DetailCompactRail, ZuneDetailRail.CompactRailTemplate, "zune.detail.compact-rail");
        views.Add(ViewKeys.DetailShowHeader, ZuneDetailRail.ShowHeaderTemplate, "zune.detail.show-header");
        views.Add(ViewKeys.DetailHero, ZuneDetailHero.Template, "zune.detail.hero");
        views.AddStyle(StyleKeys.DetailHero, ZuneDetailHero.Style);
        views.Add(ViewKeys.DetailInsights, ZuneInsights.SheetTemplate, "zune.detail.insights");
        views.Add(ViewKeys.DetailInsightsToggle, ZuneInsights.ToggleTemplate, "zune.detail.insights-toggle");
        views.AddStyle(StyleKeys.DetailFacts, ZuneFacts.Style);
        views.Add(ViewKeys.DetailNotice, ZuneDetailSeam.NoticeTemplate, "zune.detail.notice");
        views.Add(ViewKeys.DetailVerticalTrailing, ZuneDetailSeam.VerticalTrailingTemplate, "zune.detail.vertical-trailing");

        // ── the album ──
        views.Add(ViewKeys.AlbumArtistLine, ZuneAlbumRelease.LineTemplate, "zune.album.artist-line");
        views.Add(ViewKeys.AlbumArtistFlyout, ZuneAlbumRelease.FlyoutTemplate, "zune.album.artist-flyout");
        views.Add(ViewKeys.AlbumCountdown, ZuneAlbumRelease.CountdownTemplate, "zune.album.countdown");
        views.Add(ViewKeys.AlbumReleasePanel, ZuneAlbumRelease.ReleasePanelTemplate, "zune.album.release-panel");
        views.Add(ViewKeys.AlbumUnresolved, ZuneAlbumRelease.UnresolvedTemplate, "zune.album.unresolved");
        views.Add(ViewKeys.AlbumTrailing, ZuneAlbumTrailing.TrailingTemplate, "zune.album.trailing");
        views.Add(ViewKeys.AlbumSections, ZuneAlbumTrailing.SectionsTemplate, "zune.album.sections");
        views.Add(ViewKeys.AlbumAside, ZuneAlbumTrailing.AsideTemplate, "zune.album.aside");
        views.Add(ViewKeys.AlbumTrailStack, ZuneAlbumTrailing.StackTemplate, "zune.album.trail-stack");
    }
}
