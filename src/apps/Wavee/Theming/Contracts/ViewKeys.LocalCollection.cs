// ── Theming/Contracts/ViewKeys.LocalCollection.cs — the "On this PC" collection page's key (#240 P2, boards 18-21) ───────────

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Local Files route's page, "On this PC": the collection of the user's own music (#240 P2, boards 18-21).</summary>
    public static readonly ViewKey<LocalCollectionPageProps> LocalCollectionPage = new("Pages.LocalCollection");

    /// <summary>"Your files and Spotify": the offer on the page's Home (board 28). Props: <see cref="LocalLinkOfferProps"/>.</summary>
    public static readonly ViewKey<LocalLinkOfferProps> LocalLinkOffer = new("LocalLink.Offer");

    /// <summary>"Your files and Spotify": the opt-in dialog's body (board 28). Props: <see cref="LocalLinkOptInProps"/>.</summary>
    public static readonly ViewKey<LocalLinkOptInProps> LocalLinkOptIn = new("LocalLink.OptIn");

    /// <summary>"Your files and Spotify": the review of the matches, in place of the page's body (boards 29, 30). Props: <see cref="LocalLinkReviewProps"/>.</summary>
    public static readonly ViewKey<LocalLinkReviewProps> LocalLinkReview = new("LocalLink.Review");
}
