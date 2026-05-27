namespace Wavee.UI.Models;

/// <summary>One card in the "More podcasts you might like" shelf.</summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ShowRecommendationDto
{
    public string Uri { get; init; } = "";
    public string Name { get; init; } = "";
    public string? PublisherName { get; init; }
    public string? CoverArtUrl { get; init; }
}