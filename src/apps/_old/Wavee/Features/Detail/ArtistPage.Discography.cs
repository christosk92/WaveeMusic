using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Discography: the responsive album/single grids and the "Appears on" measured shelf.
sealed partial class ArtistPage : Component
{
    // ── discography (responsive grids) ───────────────────────────────────────────────────────────────────
    // The discography grid expands an album INLINE on click (iTunes-style: a full-width track drawer opens after the
    // clicked album's row) via DiscographySection → DiscoGrid → AlbumDrawerPanel, instead of navigating away. The drawer
    // header still links to the full album page. svc is threaded in so DiscoGrid can lazy-load each album's tracks.
    Element AppearsOnShelf(IReadOnlyList<Album> albums, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                albums,
                cardAt: (item, i, w) => MediaCard.Shelf(item.Cover, item.Name,
                    item.Year > 0 ? item.Year.ToString() : KindLabel(item.Kind), item.Uri,
                    () => go("album:" + item.Uri, item.Name), () => play(item.Uri), w,
                    menu: CardMenu(item.Uri, item.Name, item.Cover,
                        item.Artists.Count > 0 ? item.Artists[0].Name : null),
                    drag: CardDrag(WaveeResourceKind.Album, item.Uri, item.Name, item.Cover)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.AppearsOn)),
                keyOf: (item, i) => item.Uri),
        ],
    };
}
