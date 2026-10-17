// ── Themes/Zune/Structure/ZuneStructure.Artist.cs — the artist family's registrar (themes plan 6.3; P5 Z6a2) ───────────────────────────────
//
// Z6a2: the artist page and its family. Zune's registrations win over Classic's stand-in key by key.
//
//   Artist page      Pages.Artist (`Views\Artist\ZuneArtistPage.cs`: the panorama hero over the blend wash, the popular band with its rail, the
//                    discography facets, the shelves, "about"; `ZuneArtistHero`, `ZuneArtistSections`, `ZuneArtistBanners`). The band's row 2 and the
//                    bleed behind it are published by the core host (`Artist.PageHost`) through the page chrome; Zune's hero draws no photograph.
//   Lightbox         Artist.Lightbox (`ZuneArtistLightbox`): the pager, the pan/zoom mode, the auto-hiding chrome, the filmstrip.
//   Discography      Pages.Discography (`ZuneDiscographyPage`: the standalone page, undesigned: built from Zune's parts) and the Discography style key
//                    (`ZuneDiscography`: the grid's cells, the album drawer).
//
// The chart (`StyleKeys.ArtistChart`) is Z3's (`ZuneArtistChart`), registered by the lists registrar.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterArtist(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ArtistPage, ZuneArtistPage.Template, "zune.artist.page");
        views.Add(ViewKeys.ArtistLightbox, ZuneArtistLightbox.Template, "zune.artist.lightbox");
        views.Add(ViewKeys.Discography, ZuneDiscographyPage.Template, "zune.discography.page");
        views.AddStyle(StyleKeys.Discography, ZuneDiscography.Style);
    }
}
