namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>One card in the "More podcasts you might like" shelf.</summary>
public sealed class ShowRecommendationDto
{
    public string Uri { get; init; } = "";
    public string Name { get; init; } = "";
    public string? PublisherName { get; init; }
    public string? CoverArtUrl { get; init; }
}
