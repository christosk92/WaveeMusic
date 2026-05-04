namespace Wavee.UI.WinUI.Data.Contracts;

public sealed record StackedAvatarItem
{
    public required string DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
}
