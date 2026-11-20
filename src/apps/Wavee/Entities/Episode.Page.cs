using FluentGpu.Controls;
using FluentGpu.Dsl;

namespace Wavee;

public readonly partial struct Episode
{
    /// <summary><see cref="Shell.RouteKind.Episode"/>: the episode page (<c>Podcasts/PodcastPages.Episode.cs</c>).</summary>
    public static Element Page(in Shell.Route route) => PodcastPages.EpisodeRoute(in route);

    internal static Episode ReaderEpisode(EntityUri uri)
    {
        _ = Entities.ScopeEpoch.Value;
        _ = Entities.Current.Episodes.Changed.Value;
        return Entities.Current.Episodes.TryGetSlot(uri.Id, out int slot) ? new Episode(slot) : default;
    }

    internal static void StartAt(Episode episode, Playback.EpisodeStartKind kind, int ms = 0)
    {
        if (!episode.IsValid || (episode.Flags & EpisodeFlags.Unplayable) != 0) return;
        Playback.PlayEpisode(episode.Id, episode.Show.IsValid ? episode.Show.Id : episode.Id, new Playback.EpisodeStart(kind, ms));
    }

    /// <summary>The episode's show-notes PROSE (the recognised chapter-seek run split off) plus whether the fetch has
    /// answered at all (the episode page's About, <c>PodcastPages.Episode.cs</c>).</summary>
    internal static (string Prose, bool Known) AboutProse(Episode e)
    {
        if (!e.IsValid) return ("", false);
        string html = e.HtmlDescription.Length > 0 ? e.HtmlDescription : DescriptionOf(e);
        bool known = e.Knows(EpisodeFields.About) || e.Knows(EpisodeFields.Detail);
        return (RichTextBlocks.Split(html).Prose, known);
    }
}
