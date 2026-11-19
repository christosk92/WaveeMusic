using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Contracts = Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static class PodcastReaderRules
{
    public static int CurrentChapter(ReadOnlySpan<Spotify.Podcasts.Chapter> chapters, int positionMs)
    {
        if (positionMs < 0) return -1;
        int current = -1;
        for (int i = 0; i < chapters.Length; i++)
            if (chapters[i].StartMs <= positionMs && (current < 0 || chapters[i].StartMs >= chapters[current].StartMs)) current = i;
        return current >= 0 && chapters[current].EndMs > 0 && positionMs >= chapters[current].EndMs ? -1 : current;
    }

    public static string Timestamp(int milliseconds)
    {
        int seconds = Math.Max(0, milliseconds) / 1000;
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    public static bool CanComment(string eligibility)
        => eligibility == "ELIGIBILITY_STATUS_UNRESTRICTED";
}

internal static partial class PodcastReaderUI
{
    internal static Element Quiet(string text) => new TextEl(text)
        { Size = 13, LineHeight = 20, Wrap = TextWrap.Wrap, MinWidth = 0, Color = Tok.TextSecondary };
    internal static Element Heading(string text) => new TextEl(text)
        { Size = 20, LineHeight = 28, Weight = 600, Wrap = TextWrap.Wrap, MinWidth = 0, Color = Tok.TextPrimary };
    internal static Element Failed(Action retry) => new BoxEl
        { Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Start,
            Children = [Quiet(Loc.Get(Strings.Podcast.Reader.Unavailable)), Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), retry)] };

    /// <summary>The shelf's placeholder while its request is in flight (four seed cards).</summary>
    internal static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation> RecommendationsSeed = new(
        Enumerable.Range(0, 4).Select(i => new Spotify.Podcasts.Recommendation("seed:" + i, "Episode title", "", "Show title")).ToArray(), "", 4, "", 200);

    /// <summary>The "More like this" request — ONE definition for every owner that runs it: the show reader (<c>ReaderHost.Similar</c>, once
    /// per visit) and the podcast pages (<c>PodcastPages.Show.cs</c>, <c>PodcastPages.Episode.cs</c>).</summary>
    internal static Task<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>> RecommendationsOf(string uri, bool show, CancellationToken ct)
        => Platform.Args.Fake
            ? Task.FromResult(FakeRecommendations(uri, show))
            : Spotify.Podcasts.RecommendationsAsync(uri, show, ct);

    /// <summary>Under <c>--fake</c>: "More like this" for a show is the other seeded shows that are resident with a title (their cover and
    /// publisher); an episode's stays empty.</summary>
    static Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation> FakeRecommendations(string uri, bool show)
    {
        var items = new List<Spotify.Podcasts.Recommendation>(FakeSimilarMax);
        if (show)
            for (int i = 0; i < FakeShowCount && items.Count < FakeSimilarMax; i++)
            {
                string other = "spotify:show:sh" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (other == uri) continue;
                var parsed = EntityUri.Parse(other);
                if (!parsed.IsValid || !Entities.Current.Shows.TryGetSlot(parsed.Id, out int slot)) continue;
                var s = new Show(slot);
                if (!s.Knows(ShowFields.Title) || s.Title.Length == 0) continue;
                items.Add(new Spotify.Podcasts.Recommendation(other, s.Title, Controls.ArtUrl(s.ImageId) ?? "",
                    s.Knows(ShowFields.Publisher) ? Entities.Strings.Resolve(s.PublisherId) : ""));
            }
        return new Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>(items.ToArray(), "", items.Count, "", 200);
    }

    const int FakeShowCount = 9, FakeSimilarMax = 6;

    /// <summary>"More like this" over a request the CALLER owns (<paramref name="Request"/>): the show reader's foot is
    /// remounted whenever its list's items move, so the request lives above it and this only paints what it holds.</summary>
    internal sealed record SimilarProps(Resource<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>> Request);

    internal sealed class SimilarShelf : Component
    {
        public override Element Render() => RecommendationShelf(UseProps<SimilarProps>().Request);
    }

    static Element RecommendationShelf(Resource<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>> resource)
        => Skel.Region(resource.Loadable,
            page => !page.Ok ? Failed(resource.Refresh) : page.Items.Length == 0 ? new BoxEl()
                : PagedShelf.Create(page.Items, RecommendationCard, onInvoke: static (item, _) => OpenRecommendation(item), cardHeight: SurfaceGeometry.ShelfHeight,
                    title: Loc.Get(Strings.Podcast.Reader.Related), measured: false, keyOf: static (item, _) => item.Uri,
                    lift: ShelfLift.None),   // the shared card hovers fill-only: no lift halo to reserve clearance for
            onFailed: () => Failed(resource.Refresh));

    /// <summary>The recommendation card's click, shared with the shelf slot's <c>onInvoke</c>: its episode or show page.</summary>
    static void OpenRecommendation(Spotify.Podcasts.Recommendation item)
    {
        var uri = EntityUri.Parse(item.Uri);
        if (uri.IsValid) Shell.GoTo(Shell.For(uri, item.Title));
    }

    static Element RecommendationCard(Spotify.Podcasts.Recommendation item, int index, float width)
    {
        var data = new Controls.CardData(item.Uri, item.Title, Quiet(item.Subtitle), item.Image,
            OnClick: () => OpenRecommendation(item), OnPlay: RecommendationPlay(item))
        {
            Menu = () => RecommendationMenu(item),
        };
        return Controls.Surface(data, Shape.Shelf(captionLines: 2), width);   // the extent above: one title line, two caption lines
    }

    /// <summary>The card's play FAB: pause/resume when the episode or show is the playing context, else start it. Null for a
    /// uri that names neither (no dead FAB).</summary>
    static Action? RecommendationPlay(Spotify.Podcasts.Recommendation item)
    {
        var uri = EntityUri.Parse(item.Uri);
        if (!uri.IsValid || uri.Kind is not (EntityKind.Episode or EntityKind.Show)) return null;
        var id = uri.Id;
        return () => Playback.PlayOrToggleContext(id);
    }

    /// <summary>A recommendation's menu, built at OPEN time: an episode gets the episode menu (with "Go to show"), a show
    /// the shared container menu (<see cref="Menus.Container"/>). The shelf holds wire rows, not library entities, so
    /// an episode the scope has not seen is menu'd by its uri alone.</summary>
    static ContextMenuModel? RecommendationMenu(Spotify.Podcasts.Recommendation item)
    {
        var uri = EntityUri.Parse(item.Uri);
        if (!uri.IsValid) return null;
        if (uri.Kind == EntityKind.Episode)
            return Episode.Menu(Entities.Episode(uri), new Episode.MenuOptions(ShowGoToShow: true));
        if (uri.Kind != EntityKind.Show) return null;
        var show = ActionTarget.ForShow(uri, item.Title);
        return Menus.Container(in show, item.Image.Length > 0 ? item.Image : null, item.Subtitle, new ContainerExtras());
    }
}
