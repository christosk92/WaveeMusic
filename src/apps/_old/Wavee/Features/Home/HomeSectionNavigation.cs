using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using Wavee.Core;

namespace Wavee;

/// <summary>Click-to-section handoff. Home already holds the returned first page, so a drill route can paint it before
/// the <c>homeSection</c> read lands. The bounded entry remains available for Back/remount: a synthetic section without
/// a server URI cannot be reconstructed once its preview is consumed.</summary>
sealed class HomeSectionPreviewStore
{
    public static readonly Context<HomeSectionPreviewStore?> Slot = new(null);
    const int Capacity = 32;
    readonly Dictionary<string, HomeSection> _map = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    public void Set(string routeKey, HomeSection section)
    {
        if (!_map.ContainsKey(routeKey)) _order.Enqueue(routeKey);
        _map[routeKey] = section;
        while (_map.Count > Capacity && _order.TryDequeue(out var old)) _map.Remove(old);
    }

    public HomeSection? Get(string routeKey) => _map.TryGetValue(routeKey, out var section) ? section : null;
}

/// <summary>Where a home CARD goes when it is opened, and how its entity id is read out of its URI. One definition for
/// every surface that renders <see cref="HomeCard"/>s (the Home feed and the Home section page both did their own copy
/// of this switch, and drifted: one routed Liked, the other did not).
/// <para><paramref name="playTrack"/> rather than the whole <c>Services</c> graph: a Track/Episode card is the one kind
/// that PLAYS instead of navigating, and that is the only service this decision needs.</para></summary>
static class HomeCardNav
{
    /// <summary>The trailing id of a <c>scheme:kind:id</c> URI (the whole string when it carries no separator) —
    /// THE <see cref="EntityUri.IdOf"/>, not a private copy of it (hydration-facade-design.md §1.1).</summary>
    public static string Id(string uri) => EntityUri.IdOf(uri);

    /// <summary>Open a card: play it (Track/Episode), or navigate to its destination — stashing the partial detail
    /// model the card already carries, so the detail page reconciles its header in place instead of flashing a
    /// full-page skeleton (see <see cref="DetailNav"/>). <paramref name="preview"/> may be null (no stash then).</summary>
    public static void Open(HomeCard card, NavPreviewStore? preview, Action<string, string?> go,
                            Action<string>? playTrack)
    {
        switch (card.Kind)
        {
            case HomeCardKind.Liked:
                go("liked", null);
                return;
            case HomeCardKind.Track:
            case HomeCardKind.Episode:
                // An episode, like a track, is a thing you PLAY rather than a destination: the feed carries a uri and
                // display metadata but no episode page of our own, and its show is one tap away from the player.
                playTrack?.Invoke(card.Uri);
                return;
            case HomeCardKind.Artist:
                go("artist:" + card.Uri, card.Title);
                return;
            case HomeCardKind.Album:
                DetailNav.OpenAlbum(preview, go,
                    new Album(Id(card.Uri), card.Uri, card.Title, card.Image, Array.Empty<ArtistRef>(), 0, 0));
                return;
            case HomeCardKind.Podcast:
            case HomeCardKind.Audiobook:
                // Both arrive under a spotify:show: uri, and the show route already renders either.
                go("show:" + card.Uri, card.Title);
                return;
            default:
                // OwnerName, not Subtitle: PlaylistSummary's third slot IS the owner, and handing it a description
                // puts the whole blurb where the detail page expects "Spotify". The daylist window rides along so the
                // detail countdown paints with the header even if playlist4 omits the format attributes.
                DetailNav.OpenPlaylist(preview, go,
                    new PlaylistSummary(card.Uri, card.Title, card.Meta?.OwnerName ?? "", 0, card.Image,
                        card.MosaicTiles,
                        DaylistExpiresAtMs: card.Meta?.ExpiresAtMs ?? 0,
                        DaylistCreatedAtMs: card.Meta?.CreatedAtMs ?? 0,
                        Accent: card.Meta?.Accent ?? 0));
                return;
        }
    }

    /// <summary>A Charts Fold tile (or a Browse shelf header) drills into <c>browse-section:</c> so paging uses
    /// <c>browseSection</c>, not <c>homeSection</c>. A section that is only one card is that card — opening it as a
    /// section page is the 1-tile "intermediate" void.</summary>
    public static void OpenBrowseSection(HomeSection s, NavPreviewStore? navPreview,
                                         HomeSectionPreviewStore? sectionPreview, Action<string, string?> go,
                                         Action<string>? playTrack, NavOrigin? origin = null,
                                         Action<string, string?, NavOrigin?>? goOrigin = null)
    {
        if (s.Cards.Count == 1)
        {
            Open(s.Cards[0], navPreview, go, playTrack);
            return;
        }
        string route = BrowseSectionRoutes.Page(s.Uri ?? "");
        sectionPreview?.Set(route, s);
        if (goOrigin is not null) goOrigin(route, s.Title, origin);
        else go(route, s.Title);
    }
}

// HomeSectionPaging — the cursor arithmetic — lives in its own engine-free file (HomeSectionPaging.cs) so
// Wavee.Tests can source-include and drive it without this file's Context/Hooks dependencies. HomeSectionRoutes and
// BrowseSectionRoutes moved out the same way, into HomeSectionRoutes.cs.
