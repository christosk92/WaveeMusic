namespace Wavee.UI.WinUI.Data.Parameters;

public enum PodcastLibraryNavigationTarget
{
    Show,
    Episode
}

/// <summary>
/// Deep-link into the Podcasts tab of the library page.
/// Show/episode metadata is optional and lets the library surface paint the
/// selected drill-in while full Pathfinder data loads.
/// </summary>
public sealed record PodcastLibraryNavigationParameter
{
    public PodcastLibraryNavigationTarget Target { get; init; } = PodcastLibraryNavigationTarget.Show;
    public string? ShowUri { get; init; }
    public string? ShowTitle { get; init; }
    public string? ShowImageUrl { get; init; }
    public string? EpisodeUri { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? EpisodeImageUrl { get; init; }

    public string NavigationKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(EpisodeUri))
                return EpisodeUri!;

            if (!string.IsNullOrWhiteSpace(ShowUri))
                return ShowUri!;

            return "podcasts";
        }
    }
}
