// ── Podcasts/PodcastPages.cs — the show / episode routes of the podcast pages v2 and the router between them and the older reader pages ─────────────
//
// `Shell.RouteKind.Show` and `Shell.RouteKind.Episode` land here (`Show.Page` / `Episode.Page`). Under a structure that draws the podcast pages
// (`ShellCapabilities.PodcastPages`) the show route mounts `PodcastShowScreen`, or `AudiobookScreen` once the show is known to be an audiobook
// (`Show.IsAudiobook`: the show flag, or a resident chapter); the episode route mounts `PodcastEpisodeScreen`. Under any other structure the older
// pages stay (`Show.LegacyPage`, `Episode.LegacyPage`) until that theme draws the new keys: the seam the Classic and Zune skins plug into.
//
// Each host is a `[CoreHost]` in its own file: `PodcastPages.Show.cs`, `PodcastPages.Episode.cs`, `PodcastPages.Audiobook.cs`.

using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

public static partial class PodcastPages
{
    /// <summary>The show route: the podcast or audiobook page, or the older reader page.</summary>
    public static Element ShowRoute(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new RouteProps(route, routeKey, false), static () => new RouteHost()) with { Key = "show-page:" + routeKey };
    }

    /// <summary>The episode route: the episode page, or the older reader page.</summary>
    public static Element EpisodeRoute(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new RouteProps(route, routeKey, true), static () => new RouteHost()) with { Key = "episode-page:" + routeKey };
    }

    internal sealed record RouteProps(Shell.Route Route, string RouteKey, bool Episode);

    /// <summary>The props every page host reads: the subject and the route key.</summary>
    internal sealed record HostProps(EntityUri Subject, string RouteKey);

    /// <summary>Picks the page. Reads the capability (a theme switch re-picks) and, for a show, whether it is an audiobook.</summary>
    sealed class RouteHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<RouteProps>();
            var route = p.Route;
            if (!ShellCaps.Tracked.PodcastPages)
                return p.Episode ? Wavee.Episode.LegacyPage(in route) : Wavee.Show.LegacyPage(in route);
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
