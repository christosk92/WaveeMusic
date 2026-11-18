// ── Podcasts/PodcastPages.cs — the show / episode routes of the podcast pages v2 ──────────────────────────────────────────────────────────────
//
// `Shell.RouteKind.Show` and `Shell.RouteKind.Episode` land here (`Show.Page` / `Episode.Page`). The show route mounts `PodcastShowScreen`, or
// `AudiobookScreen` once the show is known to be an audiobook (`Show.IsAudiobook`: the show flag, or a resident chapter); the episode route mounts
// `PodcastEpisodeScreen`. Every structure draws the three pages (`ViewKeys.PodcastShowPage`, `PodcastEpisodePage`, `AudiobookPage`).
//
// Each host is a `[CoreHost]` in its own file: `PodcastPages.Show.cs`, `PodcastPages.Episode.cs`, `PodcastPages.Audiobook.cs`.
//
// ONE SCREEN PER SUBJECT (owner, 2026-11-18). The library's detail column mounts the SAME page (`LibraryPane`): a show or a book picked in the library
// is the page its route opens, not a second design of it. The page is drawn in the tiers of the COLUMN (`PaneHost`): the content tier and the
// content-line rungs resolved from the column's own width through the active policy, the window's and the height's tiers kept.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class PodcastPages
{
    /// <summary>The show route: the podcast or audiobook page.</summary>
    public static Element ShowRoute(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new RouteProps(route.Subject, routeKey, false), static () => new RouteHost()) with { Key = "show-page:" + routeKey };
    }

    /// <summary>The library's detail column for the show or book <paramref name="show"/>: the page its route mounts, in the column's tiers. NO key: the
    /// host stays mounted across a selection change (it keeps the column's measured width, so the next page mounts in its tier at once); the page inside
    /// is keyed per subject, as a route's is.</summary>
    public static Element LibraryPane(Show show)
    {
        if (!show.IsValid) return new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f };
        EntityUri subject = show.Uri;
        return Embed.Comp(new PaneProps(subject, "library-pane:" + subject.Text), static () => new PaneHost());
    }

    /// <summary>The episode route: the episode page.</summary>
    public static Element EpisodeRoute(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new RouteProps(route.Subject, routeKey, true), static () => new RouteHost()) with { Key = "episode-page:" + routeKey };
    }

    internal sealed record RouteProps(EntityUri Subject, string RouteKey, bool Episode);

    internal sealed record PaneProps(EntityUri Subject, string RouteKey);

    /// <summary>The podcast pages' row drag (<c>RowDrag</c> on the show and episode props): the episode of <paramref name="id"/> (its uri) as the
    /// house's episode payload (<see cref="Episode.DragPayloadOf"/>), or null when it does not resolve.</summary>
    internal static readonly Func<string, object?> RowDrag = static id =>
    {
        if (string.IsNullOrEmpty(id)) return null;
        var uri = EntityUri.Parse(id);
        return uri.IsValid && Entities.Current.Episodes.TryGetSlot(uri.Id, out int slot) ? Episode.DragPayloadOf(new Episode(slot)) : null;
    };

    /// <summary>The hero cover's drag source of the show or book <paramref name="subject"/> (<c>CoverDrag</c> on the show and book props): ONE per
    /// page, its payload built when a drag promotes, as the older show page's cover (a9a799dac^ Show.Page.cs CoverPayload) plus the track resolver a
    /// show card carries (<c>Browse.Cards.cs</c> PayloadOf), so the cover drops where a show card does (a sidebar pin, a playlist, the queue).</summary>
    internal static DragSource CoverDrag(EntityUri subject) => Drag.Source(() => CoverPayload(subject));

    static DragPayload? CoverPayload(EntityUri subject)
    {
        if (!subject.IsValid) return null;
        var show = Entities.Show(subject);
        if (!show.IsValid) return null;
        string uri = subject.Text;
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (Sidebar.LibraryWrites?.ResolveTracks is { } resolve) resolver = ct => resolve(uri, ct);
        return new DragPayload(DragKind.Show, uri, uri, show.Knows(ShowFields.Title) ? show.Title : "", new EntityRef(EntityKind.Show, show.Slot),
            TrackResolver: resolver, ArtUrl: show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null);
    }

    /// <summary>An episode's access as the rows and the episode page show it (both hosts): paywalled, preview only, and the row's lock tag. The wire
    /// sets <c>PreviewOnly</c> only on an unplayable episode that offers a preview (<c>Spotify.Decode.Podcast.cs</c> EpisodeDetail).</summary>
    internal static PodcastEpisodeVm WithAccess(PodcastEpisodeVm vm, EpisodeFlags flags)
    {
        if ((flags & (EpisodeFlags.Paywalled | EpisodeFlags.PreviewOnly | EpisodeFlags.Unplayable)) == 0) return vm;
        string lockTag = (flags & EpisodeFlags.PreviewOnly) != 0 ? Loc.Get(Strings.Podcast.Pages.LockPreview)
            : (flags & EpisodeFlags.Paywalled) != 0 ? Loc.Get(Strings.Podcast.Pages.LockSubscribers)
            : Loc.Get(Strings.Podcast.Pages.LockUnavailable);
        return vm with { Paywalled = (flags & EpisodeFlags.Paywalled) != 0, PreviewOnly = (flags & EpisodeFlags.PreviewOnly) != 0, Lock = lockTag };
    }

    /// <summary>Why an episode cannot be played, for the episode page's hero ("" = it plays): the old reader's primary (a9a799dac^ Episode.Page.cs
    /// EpisodePrimary) and its About notes, one sentence.</summary>
    internal static string BlockedOf(EpisodeFlags flags)
        => (flags & EpisodeFlags.Unplayable) == 0 ? ""
            : (flags & EpisodeFlags.PreviewOnly) != 0 ? Loc.Get(Strings.Podcast.Reader.Preview)
            : (flags & EpisodeFlags.Paywalled) != 0 ? Loc.Get(Strings.Podcast.Reader.Paywall)
            : Loc.Get(Strings.Podcast.Pages.Unplayable);

    /// <summary>The props every page host reads: the subject and the route key.</summary>
    internal sealed record HostProps(EntityUri Subject, string RouteKey);

    /// <summary>Picks the page: for a show, whether it is an audiobook.</summary>
    sealed class RouteHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<RouteProps>();
            var host = new HostProps(p.Subject, p.RouteKey);
            if (p.Episode) return Embed.Comp(host, static () => new PodcastEpisodeScreen()) with { Key = "podcast-episode" };
            _ = Entities.ScopeEpoch.Value;
            var show = Entities.Show(p.Subject);
            // A single-child slot ignores its child's key: the keyed page sits in a plain column so that a show turning out to be a book swaps hosts.
            Element page = show.IsValid && show.IsAudiobook
                ? Embed.Comp(host, static () => new AudiobookScreen()) with { Key = "podcast-audiobook" }
                : Embed.Comp(host, static () => new PodcastShowScreen()) with { Key = "podcast-show" };
            return new BoxEl { Direction = 1, Grow = 1f, Children = [page] };
        }
    }

    /// <summary>The measured-width step of the library's column: a resize re-resolves the column's tiers once per step, never per pixel.</summary>
    const float PaneWidthQuantum = 8f;

    /// <summary>The library's detail column around a show or book page. It measures the column, resolves the tiers a page that wide is drawn in and
    /// provides them to the page in place of the window's (<see cref="ThemeFactsContext.Signal"/>); the page's row-2 publications are dropped (the
    /// library is the route, <see cref="PageChromeSink.None"/>). The page mounts once the column has a width: it is never drawn a frame in the
    /// window's tiers first. Like <see cref="RouteHost"/> it builds no look: two bare boxes that hold the page's place.</summary>
    sealed class PaneHost : Component
    {
        IReadSignal<ThemeFacts> _window = ThemeHost.Facts;
        IReadSignal<float>? _width;
        ThemeFacts? _drawn, _from;
        readonly Func<ThemeFacts> _factsOf;
        readonly Func<bool> _measured;

        public PaneHost()
        {
            _factsOf = FactsOf;
            _measured = () => (_width?.Value ?? 0f) > 0f;
        }

        public override Element Render()
        {
            var p = UseProps<PaneProps>();
            _window = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
            _width = UseMeasuredWidth(PaneWidthQuantum);
            var facts = UseComputed(_factsOf);
            bool measured = UseComputed(_measured).Value;
            Element[] kids = !measured
                ? []
                :
                [
                    Ctx.Provide(ThemeFactsContext.Signal, facts,
                        Ctx.Provide(PageChromeSink.Context, PageChromeSink.None, Ctx.Provide(PodcastPagePlace.Embedded, true,
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                                Children = [Embed.Comp(new RouteProps(p.Subject, p.RouteKey, false), static () => new RouteHost()) with { Key = "show-page:" + p.RouteKey }],
                            }))),
                ];
            return new BoxEl
            {
                Key = "library-show-pane", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                Children = kids,
            };
        }

        /// <summary>The facts the page is drawn in: the window's, with the content tier and the content-axis rungs of the column. The record is kept
        /// while neither the window's facts nor the column's tier or rungs move, so a resize inside a tier re-renders nothing (the widths in it are
        /// the ones published at the last crossing, as the window's are).</summary>
        ThemeFacts FactsOf()
        {
            ThemeFacts window = _window.Value;
            float width = _width?.Value ?? 0f;
            if (width <= 0f || window.Tiers.Policy is not { } policy) return window;
            TierFacts own = WindowTiers.Resolve(policy, window.Tiers.ViewportWidth, window.Tiers.ViewportHeight, width,
                _drawn is { } held ? held.Tiers : null);
            ulong rungs = window.Tiers.Rungs;
            for (int i = 0; i < policy.Rungs.Length; i++)
            {
                if (policy.Rungs[i].Axis != RungAxis.Content) continue;
                ulong bit = 1UL << i;
                rungs = (rungs & ~bit) | (own.Rungs & bit);
            }
            if (_drawn is { } d && ReferenceEquals(_from, window) && d.Tiers.Content == own.Content && d.Tiers.Rungs == rungs) return d;
            _from = window;
            return _drawn = window with { Tiers = window.Tiers with { Content = own.Content, ContentWidth = own.ContentWidth, Rungs = rungs } };
        }
    }
}
