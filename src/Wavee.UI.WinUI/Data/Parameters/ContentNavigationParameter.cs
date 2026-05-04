namespace Wavee.UI.WinUI.Data.Parameters;

/// <summary>
/// Navigation parameter that carries known card data to destination pages
/// so they can prefill the UI immediately while loading full details in the background.
/// </summary>
public sealed record ContentNavigationParameter
{
    public required string Uri { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
}
