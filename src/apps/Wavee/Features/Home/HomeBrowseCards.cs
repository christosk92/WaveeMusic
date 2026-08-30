using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Features.Browse;

namespace Wavee;

/// <summary>The browse→home card mapping — engine-free (Wavee.Core + BCL only) so this file can be source-included
/// into Wavee.Tests without dragging in FluentGpu. Split out of <c>HomeSectionPage</c>, which is the one caller: a
/// <c>browseSection</c> read comes back as <see cref="BrowseSection"/>/<see cref="BrowseCard"/>, and the section
/// page renders <see cref="HomeSection"/>/<see cref="HomeCard"/> regardless of which endpoint fetched them.</summary>
static class HomeBrowseCards
{
    /// <summary>A browse section as a HomeSection (the section-page model). <paramref name="routeTitle"/> backs the
    /// title only when the browse response omits one — the route is the fallback of last resort, never the other way
    /// round, exactly like <c>HomeSectionPage.Identify</c> for the home-document arm.</summary>
    public static HomeSection Section(BrowseSection s, string? routeTitle)
    {
        var cards = new HomeCard[s.Cards.Count];
        for (int i = 0; i < cards.Length; i++) cards[i] = Card(s.Cards[i]);
        // totalCount under-reports (or is omitted). Never let the ledger claim fewer items than we already hold —
        // a 20/0 seed made HasMore false and froze Weekly at El Salvador.
        int total = Math.Max(s.Total, s.Cards.Count);
        return new HomeSection(s.Uri, s.Title ?? routeTitle, null, cards, total, s.Cards.Count);
    }

    /// <summary>The Charts Fold deck: one HomeSection per <see cref="ChartSections.All"/> uri, cards kept on that
    /// section (never fanned into one tile per playlist). Featured null throws; later shelves that return null or
    /// carry no cards are omitted so a market without Podcast Charts does not blank Home.</summary>
    public static async Task<IReadOnlyList<HomeSection>> LoadChartDeckAsync(IBrowseService browse, CancellationToken ct = default)
    {
        var uris = ChartSections.All;
        var tasks = new Task<BrowseSection?>[uris.Count];
        for (int i = 0; i < uris.Count; i++)
            tasks[i] = browse.GetSectionAsync(uris[i], 0, ct);
        var pages = await Task.WhenAll(tasks).ConfigureAwait(false);

        if (pages[0] is null)
            throw new InvalidOperationException("browseSection returned no Charts section for " + ChartSections.Featured + ".");

        var list = new List<HomeSection>(pages.Length);
        for (int i = 0; i < pages.Length; i++)
        {
            var s = pages[i];
            if (s is null || s.Cards.Count == 0) continue;
            // Stamp the taxonomy URI we asked for. A mapper variant on s.Uri would miss ChartSections.Contains
            // on the drill page, which skips the walk and the in-page filter.
            list.Add(Section(s, null) with { Uri = uris[i] });
        }
        return list;
    }

    /// <summary>Three blank Fold tiles — the Charts row's UseResource seed, so Skel.Region derives a Fold-shaped
    /// shimmer instead of a generic bar. Title is a single space (never shown; the region marks the tree
    /// skeletonized).</summary>
    public static readonly IReadOnlyList<HomeSection> ChartDeckSeed =
    [
        BlankFold(), BlankFold(), BlankFold(),
    ];

    static HomeSection BlankFold() => new(null, " ", null,
    [
        new HomeCard("", "", null, null, HomeCardKind.Playlist),
        new HomeCard("", "", null, null, HomeCardKind.Playlist),
        new HomeCard("", "", null, null, HomeCardKind.Playlist),
    ], TotalCount: 3, RawItemCount: 3);

    public static HomeCard Card(BrowseCard c) => new(c.Uri, c.Title, c.Subtitle, c.Image, KindOf(c.Uri),
        Meta: new HomeCardMeta(Accent: c.Accent ?? 0));

    // The card's uri names its kind through the ONE parser (hydration-facade-design.md §1.1); everything the browse
    // feed can carry that is not one of these five still reads as a Playlist card, exactly as before.
    static HomeCardKind KindOf(string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Artist => HomeCardKind.Artist,
        EntityKind.Album => HomeCardKind.Album,
        EntityKind.Show => HomeCardKind.Podcast,
        EntityKind.Episode => HomeCardKind.Episode,
        EntityKind.Track => HomeCardKind.Track,
        _ => HomeCardKind.Playlist,
    };
}
