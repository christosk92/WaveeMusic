using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The paged magazine shelves below the discography: on-tour banner, music videos, playlists, concerts, merch,
// gallery, and the "fans also like" / related shelves.
sealed partial class ArtistPage : Component
{
    // ── on-tour banner ───────────────────────────────────────────────────────────────────────────────────
    Element TourBannerCard(TourBanner t, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.L,
        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.L),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
        OnClick = onClick,
        Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
        Cursor = CursorId.Hand,
        Children =
        [
            new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(22f), Fill = _accent,
                Children = [ Icon(t.IsLive ? Icons.RadioTower : Icons.Calendar, 18f, Tok.TextOnAccentPrimary) ] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    // AccentDecor — an artist-shelf eyebrow is the deliberate accent identity; only the metrics moved.
                    WaveeType.Eyebrow(t.Eyebrow) with { Color = WaveeAccent.Decor },
                    new TextEl(t.Headline) { Size = 16f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(t.Subline) { Size = 13f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
            Icon(Icons.ChevronRight, 16f, Tok.TextSecondary),
        ],
    };

    // (The stand-alone pre-release BAND was deleted: the announcement is carried by the hero pill / pinned card / shy
    // pill and by ArtistPage.TopTracks' UpcomingMasthead at the head of the Releases column. See the note in
    // ArtistPage.Body where the section list is built.)

    // ── music videos (16:9 shelf) ────────────────────────────────────────────────────────────────────────
    Element MusicVideosShelf(IReadOnlyList<MusicVideo> videos, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(videos.Count, 16),
                cardAt: (i, w) => MediaCard.VideoCard(videos[i].Thumbnail, videos[i].Title, Dur(videos[i].DurationMs),
                    videos[i].TrackUri, () => play(videos[i].TrackUri), () => play(videos[i].TrackUri), w,
                    menu: CardMenu(videos[i].TrackUri, videos[i].Title, videos[i].Thumbnail, Dur(videos[i].DurationMs)),
                    // A music-video card stands for its TRACK. No Track object is in scope here (the shelf carries a
                    // MusicVideo), so the payload is uri-only: pinnable, and refused with a cue on a playlist.
                    drag: CardDrag(WaveeResourceKind.Track, videos[i].TrackUri, videos[i].Title, videos[i].Thumbnail)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.MusicVideos))),
        ],
    };

    // ── playlists and discovery ──────────────────────────────────────────────────────────────────────────
    Element PlaylistsShelf(IReadOnlyList<PlaylistRef> pls, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(pls.Count, 16),
                cardAt: (i, w) => MediaCard.Shelf(pls[i].Cover, pls[i].Name, pls[i].Subtitle, pls[i].Uri,
                    () => go("pl:" + pls[i].Uri, pls[i].Name), () => play(pls[i].Uri), w,
                    menu: CardMenu(pls[i].Uri, pls[i].Name, pls[i].Cover, pls[i].Subtitle),
                    drag: CardDrag(WaveeResourceKind.Playlist, pls[i].Uri, pls[i].Name, pls[i].Cover)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.PlaylistsDiscovery))),
        ],
    };

    // ── upcoming concerts ────────────────────────────────────────────────────────────────────────────────
    Element ConcertsRow(IReadOnlyList<Concert> concerts, Action<string, string?> go) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(concerts.Count, 12),
                cardAt: (i, w) => ConcertStub(concerts[i],
                    () => go(ConcertRoutes.Detail(concerts[i].Uri), concerts[i].Title ?? concerts[i].Venue)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.UpcomingConcerts))),
        ],
    };

    static Element ConcertStub(Concert c, Action onClick) => new BoxEl
    {
        Key = c.Uri,
        Direction = 0, Grow = 1f, Gap = Spacing.M, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
        OnClick = onClick, Role = AutomationRole.Button, Focusable = true,
        FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f), Cursor = CursorId.Hand,
        Children =
        [
            new BoxEl { Direction = 1, Width = 48f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 0f,
                Children =
                [
                    // The eyebrow month over a Title day numeral (28/36/600). The 48-DIP date column fits a two-digit
                    // 28px figure with room to spare. The month keeps the format's OWN casing ("Jan", not "JAN") and
                    // keeps its accent — a concert date caption is AccentDecor, the identity this wave preserved.
                    WaveeType.Eyebrow(c.Date.ToString("MMM", CultureInfo.InvariantCulture)) with { Color = WaveeAccent.Decor },
                    new TextEl(c.Date.Day.ToString()) { Size = 28f, LineHeight = 36f, Weight = 600, Color = Tok.TextPrimary },
                ] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    new TextEl(c.Venue) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
                        Children = [ Icon(Icons.MapPin, 12f, Tok.TextSecondary), new TextEl(c.City) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ] },
                ] },
        ],
    };

    // ── merch ────────────────────────────────────────────────────────────────────────────────────────────
    Element MerchRow(IReadOnlyList<MerchItem> merch) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(merch.Count, 12),
                cardAt: (i, w) => MerchCard(merch[i], w),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.Merch))),
        ],
    };

    static Element MerchCard(MerchItem m, float w) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Grow = 1f, ClipToBounds = true,
        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault, HoverScale = WaveeMotion.ScaleStandard.Hover,
        Children =
        [
            new BoxEl { ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
                Children = [ Surfaces.ArtworkFill(m.Image, Radii.Control) ] },
            new TextEl(m.Name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
            new TextEl(m.Price) { Size = 13f, Weight = 700, Color = Tok.AccentTextPrimary, MaxLines = 1 },
        ],
    };

    // ── gallery ──────────────────────────────────────────────────────────────────────────────────────────
    Element GalleryStrip(IReadOnlyList<Image> photos) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                Math.Min(photos.Count, 16),
                cardAt: (i, w) => new BoxEl { Width = w, Height = w, Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                    OnClick = () => OpenGallery(photos, i), Cursor = CursorId.Hand, HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
                    Children = [ Surfaces.Artwork(photos[i], i, w, w, Radii.Card, decodePx: 480) ] },
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.Gallery))),
        ],
    };

    void OpenGallery(IReadOnlyList<Image> photos, int initialIndex)
    {
        if (photos.Count == 0 || _menuOverlay is null) return;
        OverlayHandle? handle = null;
        ArtistGalleryLightbox? viewer = null;
        handle = _menuOverlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => viewer = new ArtistGalleryLightbox(photos, initialIndex, () => handle)),
            FlyoutPlacement.BottomCenter,
            // Modal, full-window: no light dismiss, focus trap, Escape-to-close for free (vetoed while zoomed below).
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        // Esc while zoomed unzooms instead of closing (returns false to veto the dismissal); tear the idle loop down on close.
        handle.ClosingAction = cause => !(viewer?.TryConsumeEscape() ?? false);
        handle.ClosedAction = () => viewer?.Cleanup();
    }

    // ── fans also like ───────────────────────────────────────────────────────────────────────────────────
    Element RelatedShelf(IReadOnlyList<RelatedArtist> related, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                related.Count,
                cardAt: (i, w) => MediaCard.Shelf(related[i].Image, related[i].Name, Loc.Get(Strings.Search.TypeArtist), related[i].Uri,
                    () => go("artist:" + related[i].Uri, related[i].Name), () => play(related[i].Uri), w, circular: true,
                    menu: CardMenu(related[i].Uri, related[i].Name, related[i].Image,
                        Loc.Get(Strings.Search.TypeArtist), circular: true),
                    drag: CardDrag(WaveeResourceKind.Artist, related[i].Uri, related[i].Name, related[i].Image)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Detail.FansAlsoLike))),
        ],
    };

    Element FansShelf(IReadOnlyList<Artist> fans, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                fans.Count,
                cardAt: (i, w) => MediaCard.Shelf(fans[i].Image, fans[i].Name, Loc.Get(Strings.Search.TypeArtist), fans[i].Uri,
                    () => go("artist:" + fans[i].Uri, fans[i].Name), () => play(fans[i].Uri), w, circular: true,
                    menu: CardMenu(fans[i].Uri, fans[i].Name, fans[i].Image,
                        Loc.Get(Strings.Search.TypeArtist), circular: true),
                    drag: CardDrag(WaveeResourceKind.Artist, fans[i].Uri, fans[i].Name, fans[i].Image)),
                measured: true, header: AccentHeader(Loc.Get(Strings.Detail.FansAlsoLike))),
        ],
    };
}
