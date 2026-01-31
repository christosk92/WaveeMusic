namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents detailed playlist metadata.
/// </summary>
public sealed record PlaylistDetailDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public required string OwnerName { get; init; }
    public string? OwnerId { get; init; }
    public int TrackCount { get; init; }
    public int FollowerCount { get; init; }
    public bool IsOwner { get; init; }
    public bool IsCollaborative { get; init; }
    public bool IsPublic { get; init; }
}
