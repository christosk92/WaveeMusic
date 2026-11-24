// ── Entities/LocalLink.Page.cs ──────────────────────────────────────────────────────────────────────────────────────
// the core host of "Your files and Spotify" on the On this PC page: the offer, the opt-in dialog, the review's props (#240 phase 1)
//
// Role: UI (core host: built once; the theme draws `ViewKeys.LocalLinkOffer`, `LocalLinkOptIn`, `LocalLinkReview`)
// Spec: 240-spotify-linking-concept.md §5 F-A, F-B; board frames 28-30
//
// THE OFFER shows on the page's Home while matching is off, "Not now" was never pressed, and the collection has albums no decision covers;
// it counts albums and songs with no album tag (each its own group), so a library of loose songs is offered it too. "Show me" opens the opt-in dialog: the house ContentDialog with the theme's body,
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
public static partial class LocalLinkPage
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
    static (int Albums, int Songs) s_offerCount = (-1, -1);

    /// <summary>The offer now (tracked), or null. It counts albums and songs with no album tag (each its own group): a library of loose songs
    /// sees it too ("…to 1 song").</summary>
    static LocalLinkOfferProps? OfferNow()
    {
        if (LocalLink.Enabled.Value || !LocalLink.OfferOpen.Value) return null;
        _ = LocalLink.Version.Value;
        var view = LocalCollection.View.Value;
        var count = LocalLink.OfferCount(view.AlbumCount, view.LooseSongs);
        if (count.Albums + count.Songs <= 0 || view.Phase == LocalLibraryPhase.None) return null;
        if (s_offer is not null && s_offerCount == count) return s_offer;
        s_offerCount = count;
        string? before = view.Mosaic.Count > 0 ? view.Mosaic[0] : null;
        return s_offer = new LocalLinkOfferProps(LocalLinkWords.Offer(count.Albums, count.Songs), Loc.Get(W.OfferSub), before, Loc.Get(W.ShowMe), Loc.Get(W.NotNow),
            static overlay => OpenOptIn(overlay), LocalLink.DismissOffer);
    }

    /// <summary>"Find on Spotify…" on a song (its row menu, its drawer), in every theme. With matching on, the song's group is matched now and the
    /// review opens on its candidates. Off, the opt-in asks once, over the page the song is on: the same explainer (what is searched, what never
    /// leaves this PC, the files untouched) with "Turn on and find" and "Cancel"; turning on starts matching and finds this song first.</summary>
    public static void FindOnSpotify(long localId, IOverlayService? overlay)
    {
        if (LocalLink.Enabled.Peek()) { LocalLink.FindNow(localId); return; }
        OpenOptIn(overlay, forSong: localId);
    }

    /// <summary>The opt-in dialog over the page: "Show me" on the offer (board 28 ②), or "Find on Spotify…" while matching is off
    /// (<paramref name="forSong"/>: its title, body and buttons say so, and turning on finds that song at once).</summary>
    public static void OpenOptIn(IOverlayService? overlay, long forSong = 0)
    {
        if (overlay is null || Controls.IsNullOverlay(overlay)) return;
        var autoExact = new Signal<bool>(false);
        var body = new LocalLinkOptInProps(Loc.Get(forSong > 0 ? W.FindBody : W.OptInBody),
        [
            new LocalLinkOptInLine(Icons.Search, Loc.Get(W.SearchedTitle), Loc.Get(W.SearchedBody)),
            new LocalLinkOptInLine(Icons.Devices, Loc.Get(W.NeverTitle), Loc.Get(W.NeverBody)),
            new LocalLinkOptInLine(Icons.Document, Loc.Get(W.UntouchedTitle), Loc.Get(W.UntouchedBody)),
        ], Loc.Get(W.AutoExact), Loc.Get(W.AutoExactSub), on => autoExact.Value = on);
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(forSong > 0 ? W.FindTitle : W.OptInTitle);
            d.PrimaryText = Loc.Get(forSong > 0 ? W.TurnOnAndFind : W.TurnOn);
            d.CloseText = Loc.Get(forSong > 0 ? Strings.Auth.Cancel : W.NotNow);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryClick = () =>
            {
                LocalLink.TurnOn(autoExact.Peek());
                if (forSong > 0) LocalLink.FindNow(forSong);
            };
            d.Content = ViewSlots.View(ViewKeys.LocalLinkOptIn, body);
            OverlayFit.Dialog(d, Controls.DialogWidthMax);
        });
    }
}
