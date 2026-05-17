namespace Wavee.UI.WinUI.ViewModels;

public sealed record ArtistTopCityVm
{
    public required string City { get; init; }
    public string? Country { get; init; }
    public long NumberOfListeners { get; init; }
    public required string DisplayCount { get; init; }
    public double RelativeWidth { get; init; }
}
