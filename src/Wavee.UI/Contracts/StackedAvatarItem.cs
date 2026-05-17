namespace Wavee.UI.Contracts;

public sealed record StackedAvatarItem
{
    public required string DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
}