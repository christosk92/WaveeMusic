// ── Entities/LocalLink.Page.cs ──────────────────────────────────────────────────────────────────────────────────────
// the core host of "Your files and Spotify" on the On this PC page: the offer, the opt-in dialog, the review's props (#240 phase 1)
//
// Role: UI (core host: built once; the theme draws `ViewKeys.LocalLinkOffer`, `LocalLinkOptIn`, `LocalLinkReview`)
// Spec: 240-spotify-linking-concept.md §5 F-A, F-B; board frames 28-30
//
// THE OFFER shows on the page's Home while matching is off, "Not now" was never pressed, and the collection has albums no decision covers;
// it counts albums, not songs (matching is album first). "Show me" opens the opt-in dialog: the house ContentDialog with the theme's body,
// "Turn on matching" (primary) and "Not now" (close: the offer stays, the user may come back to it). Turning matching on starts it at once in
// the background; the library chip says how far it is and one toast says when it is done, with Review.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;
using W = Wavee.Strings.LocalFiles.Link;

namespace Wavee;

/// <summary>The On this PC page's "Your files and Spotify" props and the opt-in dialog (see the file header).</summary>
public static class LocalLinkPage
{
    static LocalLinkPageProps? s_props;

    /// <summary>The page's props, built once (every member is live).</summary>
    public static LocalLinkPageProps Props => s_props ??= new LocalLinkPageProps
    {
        Offer = OfferNow,
        Reviewing = LocalLink.Reviewing,
        Review = new LocalLinkReviewProps
        {
            View = LocalLink.View,
            Focus = LocalLink.Reviewing,
            PageTitle = Loc.Get(Strings.LocalFiles.Page.Title),
            Close = LocalLink.CloseReview,
            Accept = LocalLink.Accept,
            Pick = LocalLink.Pick,
            AcceptAllExact = LocalLink.AcceptAllExact,
            NotThis = LocalLink.NotThis,
            NotOnSpotify = LocalLink.MarkNotOnSpotify,
            Undo = LocalLink.Undecide,
            SetSpotifyWins = LocalLink.SetSpotifyWins,
            Search = LocalLink.SearchFor,
            ShowAll = static () => LocalLink.OpenReview(""),
            RouteKey = "local:link-review",
        },
    };

    static LocalLinkOfferProps? s_offer;
    static int s_offerAlbums = -1;

    /// <summary>The offer now (tracked), or null.</summary>
    static LocalLinkOfferProps? OfferNow()
    {
        if (LocalLink.Enabled.Value || !LocalLink.OfferOpen.Value) return null;
        _ = LocalLink.Version.Value;
        var view = LocalCollection.View.Value;
        int albums = LocalLink.OfferCount(view.AlbumCount);
        if (albums <= 0 || view.Phase == LocalLibraryPhase.None) return null;
        if (s_offer is not null && s_offerAlbums == albums) return s_offer;
        s_offerAlbums = albums;
        string? before = view.Mosaic.Count > 0 ? view.Mosaic[0] : null;
        return s_offer = new LocalLinkOfferProps(LocalLinkWords.Offer(albums), Loc.Get(W.OfferSub), before, Loc.Get(W.ShowMe), Loc.Get(W.NotNow),
            OpenOptIn, LocalLink.DismissOffer);
    }

    /// <summary>"Show me": the opt-in dialog over the page (board 28 ②).</summary>
    public static void OpenOptIn(IOverlayService? overlay)
    {
        if (overlay is null || Controls.IsNullOverlay(overlay)) return;
        var autoExact = new Signal<bool>(false);
        var body = new LocalLinkOptInProps(Loc.Get(W.OptInBody),
        [
            new LocalLinkOptInLine(Icons.Search, Loc.Get(W.SearchedTitle), Loc.Get(W.SearchedBody)),
            new LocalLinkOptInLine(Icons.Devices, Loc.Get(W.NeverTitle), Loc.Get(W.NeverBody)),
            new LocalLinkOptInLine(Icons.Document, Loc.Get(W.UntouchedTitle), Loc.Get(W.UntouchedBody)),
        ], Loc.Get(W.AutoExact), Loc.Get(W.AutoExactSub), on => autoExact.Value = on);
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(W.OptInTitle);
            d.PrimaryText = Loc.Get(W.TurnOn);
            d.CloseText = Loc.Get(W.NotNow);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryClick = () => LocalLink.TurnOn(autoExact.Peek());
            d.Content = ViewSlots.View(ViewKeys.LocalLinkOptIn, body);
            OverlayFit.Dialog(d, Controls.DialogWidthMax);
        });
    }
}
