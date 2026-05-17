namespace Wavee.UI.WinUI.ViewModels;

public sealed record ArtistSocialLinkVm
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required FontAwesome6.EFontAwesomeIcon Icon { get; init; }
}
