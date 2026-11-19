using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
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

    /// <summary>The "More like this" request — ONE definition for every owner that runs it: the podcast pages (<c>PodcastPages.Show.cs</c>,
    /// <c>PodcastPages.Episode.cs</c>).</summary>
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
}
