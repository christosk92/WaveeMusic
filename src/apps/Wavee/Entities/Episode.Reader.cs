using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Episode
{
    sealed class TranscriptReader : Component
    {
        public override Element Render()
        {
            var p = UseProps<ReaderProps>();
            return new BoxEl { Direction = 1, Grow = 1, MinHeight = 0, MinWidth = 0,
                Children = [Lyrics.TranscriptView(p.Subject.Id)] };
        }
    }

}

public static class PodcastReaderRules
{
    public static int CurrentLine(ReadOnlySpan<Spotify.Podcasts.TranscriptLine> lines, int positionMs)
    {
        if (positionMs < 0) return -1;
        int current = -1;
        for (int i = 0; i < lines.Length; i++)
            if (!lines[i].Heading && lines[i].StartMs <= positionMs && (current < 0 || lines[i].StartMs >= lines[current].StartMs)) current = i;
        return current;
    }

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

    internal sealed record SaveProps(EntityUri Subject);
    internal sealed class SaveEpisode : Component
    {
        public override Element Render()
        {
            var p = UseProps<SaveProps>(); var e = Episode.ReaderEpisode(p.Subject);
            _ = Spotify.Podcasts.SavedChanged.Value;
            uint epoch = Entities.ScopeEpoch.Value;
            var read = UseResource(ct => Platform.Args.Fake ? Task.FromResult(new Spotify.Podcasts.Mutation(false, 404)) : Spotify.Podcasts.ReadSavedAsync(ct), new Spotify.Podcasts.Mutation(false, 0), DepKey.From((int)epoch));
            bool pending = read.Loadable.State.Value == (byte)LoadState.Pending || Spotify.Podcasts.SavedBusy;
            if (!Spotify.Podcasts.SavedReady)
                return Detail.RailSatellite(Icons.Heart, Loc.Get(pending ? Strings.Podcast.Reader.Save : Strings.Podcast.Reader.SaveUnavailable),
                    pending ? null : read.Refresh);
            bool saved = e.IsValid && Spotify.Podcasts.IsSaved(e);
            return Detail.RailSatellite(saved ? Icons.HeartFill : Icons.Heart,
                Loc.Get(saved ? Strings.Podcast.Reader.RemoveSaved : Strings.Podcast.Reader.Save),
                e.IsValid && !Spotify.Podcasts.SavedBusy ? () => Spotify.Podcasts.ToggleSaved(e) : null);
        }
    }

    internal sealed record RecommendationProps(string Uri, bool Show);

    /// <summary>The shelf's placeholder while its request is in flight (four seed cards).</summary>
    internal static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation> RecommendationsSeed = new(
        Enumerable.Range(0, 4).Select(i => new Spotify.Podcasts.Recommendation("seed:" + i, "Episode title", "", "Show title")).ToArray(), "", 4, "", 200);

    /// <summary>The "More like this" request — ONE definition for the two owners that run it: the episode page's
    /// <see cref="Recommendations"/> (its own, per mount) and the show reader (<c>ReaderHost.Similar</c>, once per visit).</summary>
    internal static Task<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>> RecommendationsOf(string uri, bool show, CancellationToken ct)
        => Platform.Args.Fake
            ? Task.FromResult(new Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>([], "", 0, "", 200))
            : Spotify.Podcasts.RecommendationsAsync(uri, show, ct);

    /// <summary>"More like this" with a request of its OWN, made at mount (the episode page's).</summary>
    internal sealed class Recommendations : Component
    {
        public override Element Render()
        {
            var p = UseProps<RecommendationProps>(); uint epoch = Entities.ScopeEpoch.Value;
            var resource = UseResource(ct => RecommendationsOf(p.Uri, p.Show, ct), RecommendationsSeed, epoch + ":" + p.Uri);
            return RecommendationShelf(resource);
        }
    }

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
            OnClick: () => OpenRecommendation(item))
        {
            Menu = () => RecommendationMenu(item),
        };
        return Controls.Surface(data, Shape.Shelf(captionLines: 2), width);   // the extent above: one title line, two caption lines
    }

    /// <summary>A recommendation's menu, built at OPEN time: an episode gets the episode menu (with "Go to show"), a show
    /// the container verbs (play next · add to queue · pin) and Share. The shelf holds wire rows, not library entities, so
    /// an episode the scope has not seen is menu'd by its uri alone.</summary>
    static ContextMenuModel? RecommendationMenu(Spotify.Podcasts.Recommendation item)
    {
        var uri = EntityUri.Parse(item.Uri);
        if (!uri.IsValid) return null;
        if (uri.Kind == EntityKind.Episode)
            return Episode.Menu(Entities.Episode(uri), new Episode.MenuOptions(ShowGoToShow: true));
        if (uri.Kind != EntityKind.Show) return null;
        var ctx = new ActionContext(ActionTarget.ForShow(uri, item.Title), Actions.Services);
        var rows = new List<MenuFlyoutItem>(6);
        if (Actions.Menu.Row(ActionId.PlayContextNext, in ctx) is { } next) rows.Add(next);
        if (Actions.Menu.Row(ActionId.AddContextToQueue, in ctx) is { } queue) rows.Add(queue);
        if (Actions.Menu.Row(ActionId.PinToSidebar, in ctx) is { } pin) Actions.Menu.Group(rows, pin);
        Actions.Menu.Group(rows, Episode.ShareMenu(uri, in ctx));
        return rows.Count == 0 ? null : new ContextMenuModel(rows, Actions.Menu.Header(item.Image.Length > 0 ? item.Image : null, item.Title, item.Subtitle));
    }

    internal sealed record RatingProps(EntityUri Subject, Func<ColorF>? Tone);
    internal sealed class RatingEditor : Component
    {
        readonly Signal<bool> _busy = new(false);
        readonly Signal<string> _error = new("");
        public override Element Render()
        {
            var p = UseProps<RatingProps>(); var post = UsePost();
            uint epoch = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Shows.Changed.Value;
            var show = Entities.Current.Shows.TryGetSlot(p.Subject.Id, out int slot) ? new Show(slot) : default;
            int rating = show.IsValid ? show.MyRating : 0;
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Width = 250, Padding = Edges4.All(Spacing.M), Children =
                [
                    Heading(Strings.Podcast.Rate.Title(show.IsValid ? show.Title : "")),
                    Controls.StarRow(rating, _busy.Value || Platform.Args.Fake ? null : value => Submit(value), 24, p.Tone),
                    Quiet(_error.Value),
                ],
            };
            async void Submit(int value)
            {
                _busy.Value = true; _error.Value = "";
                Spotify.Podcasts.Mutation result;
                try { result = await Spotify.Podcasts.RateAsync(p.Subject.Text, value, CancellationToken.None).ConfigureAwait(false); }
                catch { result = new(false, 0, true); }
                post(() =>
                {
                    if (Entities.ScopeEpoch.Peek() != epoch) return;
                    _busy.Value = false;
                    if (!result.Success) { _error.Value = Loc.Get(Strings.Podcast.Reader.MutationFailed); return; }
                    if (show.IsValid)
                    {
                        Entities.Invalidate(Entities.Current.Shows, [show.Slot], (uint)ShowFields.Rating);
                        Entities.Ensure(show, ShowFields.Rating);
                    }
                    Notify.Say(Loc.Get(Strings.Podcast.Reader.RatingSaved), InfoBarSeverity.Success);
                });
            }
        }
    }
}
