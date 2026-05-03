namespace Wavee.UI.WinUI.Data.Parameters;

/// <summary>
/// Navigation parameter for the podcast episode detail page. Carries the
/// episode identity plus an optional snapshot of the parent show so the
/// breadcrumb, hero, and "More from this show" rail can render before any
/// network call resolves. When the parent fields are null the episode page
/// resolves them from the episode protobuf during activation.
/// </summary>
public sealed record EpisodeNavigationParameter
{
    public required string EpisodeUri { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? EpisodeImageUrl { get; init; }
    public string? ShowUri { get; init; }
    public string? ShowTitle { get; init; }
    public string? ShowImageUrl { get; init; }
}
