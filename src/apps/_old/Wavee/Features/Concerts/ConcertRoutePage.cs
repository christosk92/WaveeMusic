using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee.Features.Concerts;

namespace Wavee;

/// <summary>The concert route switch: every parsed destination mounts its dedicated page, keyed by route so navigation
/// remounts cleanly (hub Phase 6, artist schedule Phase 4, detail Phase 5).</summary>
sealed class ConcertRoutePage : Component
{
    readonly Route _route;

    public ConcertRoutePage(Route route) => _route = route;

    public override Element Render()
    {
        if (!ConcertRoutes.TryParse(_route.Name, out var destination))
            return new BoxEl { Grow = 1f };

        if (destination.Kind == ConcertRouteKind.ArtistSchedule && destination.EntityId is { Length: > 0 } artistUri)
            return Embed.Comp(() => new ArtistSchedulePage(artistUri, _route.Arg))
                with { Key = "artist-schedule:" + _route.Name };
        if (destination.Kind == ConcertRouteKind.Detail && destination.EntityId is { Length: > 0 } concertUri)
            return Embed.Comp(() => new ConcertDetailPage(concertUri, _route.Arg))
                with { Key = "concert-detail:" + _route.Name };

        // Hub — plus the defensive fallback for an entity route whose identifier failed the parse guard (unreachable
        // through ConcertRoutes today).
        return Embed.Comp(() => new ConcertHubPage()) with { Key = "concert-hub" };
    }
}
