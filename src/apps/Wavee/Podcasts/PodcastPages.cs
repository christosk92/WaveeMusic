// ── Podcasts/PodcastPages.cs — the show / episode routes of the podcast pages v2 ──────────────────────────────────────────────────────────────
//
// `Shell.RouteKind.Show` and `Shell.RouteKind.Episode` land here (`Show.Page` / `Episode.Page`). The show route mounts `PodcastShowScreen`, or
// `AudiobookScreen` once the show is known to be an audiobook (`Show.IsAudiobook`: the show flag, or a resident chapter); the episode route mounts
// `PodcastEpisodeScreen`. Every structure draws the three pages (`ViewKeys.PodcastShowPage`, `PodcastEpisodePage`, `AudiobookPage`).
//
// Each host is a `[CoreHost]` in its own file: `PodcastPages.Show.cs`, `PodcastPages.Episode.cs`, `PodcastPages.Audiobook.cs`.

using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

public static partial class PodcastPages
{
    /// <summary>The show route: the podcast or audiobook page.</summary>
    public static Element ShowRoute(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new RouteProps(route, routeKey, false), static () => new RouteHost()) with { Key = "show-page:" + routeKey };
    }

    /// <summary>The episode route: the episode page.</summary>
    public static Element EpisodeRoute(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new RouteProps(route, routeKey, true), static () => new RouteHost()) with { Key = "episode-page:" + routeKey };
    }

    internal sealed record RouteProps(Shell.Route Route, string RouteKey, bool Episode);

    /// <summary>The podcast pages' row drag (<c>RowDrag</c> on the show and episode props): the episode of <paramref name="id"/> (its uri) as the
    /// house's episode payload (<see cref="Episode.DragPayloadOf"/>), or null when it does not resolve.</summary>
    internal static readonly Func<string, object?> RowDrag = static id =>
    {
        if (string.IsNullOrEmpty(id)) return null;
        var uri = EntityUri.Parse(id);
        return uri.IsValid && Entities.Current.Episodes.TryGetSlot(uri.Id, out int slot) ? Episode.DragPayloadOf(new Episode(slot)) : null;
    };

    /// <summary>The props every page host reads: the subject and the route key.</summary>
    internal sealed record HostProps(EntityUri Subject, string RouteKey);

    /// <summary>Picks the page: for a show, whether it is an audiobook.</summary>
    sealed class RouteHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<RouteProps>();
            var route = p.Route;
            var host = new HostProps(route.Subject, p.RouteKey);
            if (p.Episode) return Embed.Comp(host, static () => new PodcastEpisodeScreen()) with { Key = "podcast-episode" };
            _ = Entities.ScopeEpoch.Value;
            var show = Entities.Show(route.Subject);
            // A single-child slot ignores its child's key: the keyed page sits in a plain column so that a show turning out to be a book swaps hosts.
            Element page = show.IsValid && show.IsAudiobook
                ? Embed.Comp(host, static () => new AudiobookScreen()) with { Key = "podcast-audiobook" }
                : Embed.Comp(host, static () => new PodcastShowScreen()) with { Key = "podcast-show" };
            return new BoxEl { Direction = 1, Grow = 1f, Children = [page] };
        }
    }
}
