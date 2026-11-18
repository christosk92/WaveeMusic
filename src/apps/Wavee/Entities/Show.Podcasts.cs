using FluentGpu.Localization;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary>A topic's destination: Search for its words (the topics carry no page of their own). The podcast show page's topic links
    /// (<c>PodcastShowPageProps.OpenTopic</c>) and the audiobook page's genre links share it.</summary>
    internal static readonly Action<string> OpenTopic = static topic => Shell.GoTo(new Shell.Route(Shell.RouteKind.Search, default, Entities.Strings.Intern(topic)));

    /// <summary>The cadence's word ("weekly"), "" when the rule names none.</summary>
    internal static string CadenceWord(ShowCadence.Kind cadence) => cadence switch
    {
        ShowCadence.Kind.Daily => Loc.Get(Strings.Podcast.Cadence.Daily),
        ShowCadence.Kind.Weekly => Loc.Get(Strings.Podcast.Cadence.Weekly),
        ShowCadence.Kind.Fortnightly => Loc.Get(Strings.Podcast.Cadence.Fortnightly),
        ShowCadence.Kind.Monthly => Loc.Get(Strings.Podcast.Cadence.Monthly),
        _ => "",
    };
}
